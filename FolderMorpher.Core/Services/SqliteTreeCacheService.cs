using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AstraSize.Models;
using AstraSize.Services;
using Microsoft.Data.Sqlite;

namespace FolderMorpher.Services
{
    /// <summary>
    /// キャッシュされたツリーの概要情報
    /// </summary>
    public sealed class TreeCacheSummary
    {
        public long Id { get; set; }
        public string NormalizedPath { get; set; } = string.Empty;
        public string OriginalTargetPath { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public long TotalSizeBytes { get; set; }
        public int FileCount { get; set; }
        public int FolderCount { get; set; }
    }

    /// <summary>Search facts read one row at a time without constructing a parent/child tree.</summary>
    public readonly record struct TreeCacheSearchEntry(
        string FullPath, string Name, long SizeBytes, bool IsDirectory,
        DateTime? LastModified, DateTime? CreationTime,
        long? LastModifiedBinary = null, long? CreationTimeBinary = null)
    {
        public DateTime? GetLastModified() => LastModified ??
            (LastModifiedBinary.HasValue ? DateTime.FromBinary(LastModifiedBinary.Value) : null);

        public DateTime? GetCreationTime() => CreationTime ??
            (CreationTimeBinary.HasValue ? DateTime.FromBinary(CreationTimeBinary.Value) : null);
    }

    /// <summary>
    /// SQLite ローカル専有ツリーキャッシュ サービス（ADR 98）。
    /// 
    /// 【設計方針】
    /// 1. 完全ローカル専有: %LocalAppData%\FolderMorpher\TreeCache\tree_cache.db にのみ保持。
    ///    共有フォルダー（UNC）には一切 DB ファイルを配置せず、ロック競合や遅延破損をゼロ化。
    /// 2. 親ID・名前を正本にし、パス例外のみ補助値を保存。部分木は連続ID範囲で走査する。
    /// 3. 事前集計と単一トランザクション: 直下の遅延ロードとDB保存を両立する。
    /// 4. ポータブル JSON 相互運用: 他 PC やチーム共有向けに JSON エクスポート / インポートを完全保証。
    /// </summary>
    public sealed class SqliteTreeCacheService
    {
        private static readonly Lazy<SqliteTreeCacheService> _instance = new(() => new SqliteTreeCacheService());
        public static SqliteTreeCacheService Instance => _instance.Value;

        private readonly string _dbFilePath;
        private readonly string _connectionString;
        private readonly object _dbInitLock = new();
        private bool _isInitialized = false;

        public SqliteTreeCacheService()
        {
            var localBase = AppSettingsService.Instance.GetDefaultLocalBaseDirectory();
            var dir = Path.Combine(localBase, "TreeCache");
            Directory.CreateDirectory(dir);

            var isRegression = Environment.GetCommandLineArgs().Contains("--test-regression");
            var testPath = isRegression
                ? Environment.GetEnvironmentVariable("FOLDERMORPHER_TEST_TREE_CACHE_DB")
                    ?? Path.Combine(Path.GetTempPath(), "FolderMorpher_regression_tree_cache.db")
                : null;
            _dbFilePath = testPath == null ? Path.Combine(dir, "tree_cache.db") : Path.GetFullPath(testPath);
            Directory.CreateDirectory(Path.GetDirectoryName(_dbFilePath)!);

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _dbFilePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };
            _connectionString = builder.ToString();

            EnsureInitialized();
        }

        /// <summary>
        /// テスト用等でカスタム DB パスを指定する場合のコンストラクタ
        /// </summary>
        public SqliteTreeCacheService(string customDbPath)
        {
            var dir = Path.GetDirectoryName(customDbPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            _dbFilePath = customDbPath;

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _dbFilePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };
            _connectionString = builder.ToString();

            EnsureInitialized();
        }

        public string DbFilePath => _dbFilePath;

        public Task<bool> HasRootAsync(string targetPath) => Task.Run(() =>
        {
            EnsureInitialized();
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM TreeRoots WHERE NormalizedPath = @path LIMIT 1;";
            cmd.Parameters.AddWithValue("@path", PathCanonicalizer.Normalize(targetPath));
            return cmd.ExecuteScalar() != null;
        });

        public IEnumerable<TreeCacheSearchEntry> EnumerateSearchEntries(string targetPath, System.Threading.CancellationToken ct = default)
            => EnumerateFlatEntries(targetPath, includeDates: true, ct);

        public IEnumerable<(string FullPath, long Size)> EnumeratePathSizes(string targetPath, System.Threading.CancellationToken ct = default)
        {
            foreach (var entry in EnumerateFlatEntries(targetPath, includeDates: false, ct))
                yield return (entry.FullPath, entry.SizeBytes);
        }

        private IEnumerable<TreeCacheSearchEntry> EnumerateFlatEntries(
            string targetPath, bool includeDates, System.Threading.CancellationToken ct)
        {
            EnsureInitialized();
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            var root = FindRoot(conn, targetPath);
            if (root == null) yield break;
            using var cmd = conn.CreateCommand();
            cmd.CommandText = includeDates
                ? @"SELECT Id, ParentId, Name, Size, IsDirectory, LastModified, CreationTime, PathSuffix
                    FROM TreeNodes WHERE RootId = @rootId ORDER BY Id;"
                : @"SELECT Id, ParentId, Name, Size, IsDirectory, NULL, NULL, PathSuffix
                    FROM TreeNodes WHERE RootId = @rootId ORDER BY Id;";
            cmd.Parameters.AddWithValue("@rootId", root.Value.Id);
            using var reader = cmd.ExecuteReader();
            var ancestors = new List<(long Id, string Path)>();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var id = reader.GetInt64(0);
                var parentId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                while (ancestors.Count > 0 && ancestors[^1].Id != parentId)
                    ancestors.RemoveAt(ancestors.Count - 1);
                if (parentId != null && ancestors.Count == 0)
                    throw new InvalidDataException("Tree cache rows are not in parent-before-child order.");
                var name = reader.GetString(2);
                var suffix = reader.IsDBNull(7) ? null : reader.GetString(7);
                var path = parentId == null ? root.Value.OriginalPath : ChildPath(ancestors[^1].Path, name, suffix);
                var isDirectory = reader.GetInt32(4) == 1;
                if (isDirectory) ancestors.Add((id, path));
                yield return new TreeCacheSearchEntry(path, name, reader.GetInt64(3), isDirectory,
                    null, null, reader.IsDBNull(5) ? null : reader.GetInt64(5),
                    reader.IsDBNull(6) ? null : reader.GetInt64(6));
            }
        }

        private static (long Id, string OriginalPath)? FindRoot(SqliteConnection conn, string targetPath)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Id, OriginalTargetPath FROM TreeRoots WHERE NormalizedPath = @path;";
            cmd.Parameters.AddWithValue("@path", PathCanonicalizer.Normalize(targetPath));
            using var reader = cmd.ExecuteReader();
            return reader.Read() ? (reader.GetInt64(0), reader.GetString(1)) : null;
        }

        private static string ChildPath(string parentPath, string name, string? suffix)
        {
            if (suffix == null) return Path.Combine(parentPath, name);
            return Path.IsPathFullyQualified(suffix) ? suffix : Path.Combine(parentPath, suffix);
        }

        private static string? GetPathSuffix(string parentPath, FileItemNode node)
        {
            var expected = Path.Combine(parentPath, node.Name);
            if (string.Equals(expected, node.FullPath, StringComparison.Ordinal)) return null;
            var prefix = parentPath.TrimEnd('\\') + "\\";
            return node.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? node.FullPath[prefix.Length..] : node.FullPath;
        }

        private void EnsureInitialized()
        {
            if (_isInitialized) return;

            lock (_dbInitLock)
            {
                if (_isInitialized) return;

                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var pragmaCmd = conn.CreateCommand();
                pragmaCmd.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;";
                pragmaCmd.ExecuteNonQuery();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE IF NOT EXISTS TreeRoots (
                        Id INTEGER PRIMARY KEY,
                        NormalizedPath TEXT UNIQUE NOT NULL,
                        OriginalTargetPath TEXT NOT NULL,
                        Timestamp TEXT NOT NULL,
                        TotalSizeBytes INTEGER NOT NULL,
                        FileCount INTEGER NOT NULL,
                        FolderCount INTEGER NOT NULL,
                        TopFilesJson TEXT,
                        ExtensionStatsJson TEXT
                    );

                    CREATE TABLE IF NOT EXISTS TreeNodes (
                        Id INTEGER PRIMARY KEY,
                        RootId INTEGER NOT NULL REFERENCES TreeRoots(Id) ON DELETE CASCADE,
                        Name TEXT NOT NULL,
                        ParentId INTEGER,
                        PathSuffix TEXT,
                        SubtreeEndId INTEGER,
                        Size INTEGER NOT NULL,
                        FileCount INTEGER NOT NULL,
                        FolderCount INTEGER NOT NULL,
                        IsDirectory INTEGER NOT NULL,
                        LastModified INTEGER,
                        CreationTime INTEGER,
                        Sha256 BLOB
                    );

                ";
                cmd.ExecuteNonQuery();

                // Legacy caches repeat the full parent path in every row and in a large index.
                // Migrate transactionally before creating the compact parent-id index.
                var migrated = false;
                using (var columns = conn.CreateCommand())
                {
                    columns.CommandText = "PRAGMA table_info(TreeNodes);";
                    using var reader = columns.ExecuteReader();
                    while (reader.Read())
                    {
                        if (reader.GetString(1) != "ParentPath") continue;
                        reader.Close();
                        MigrateParentPaths(conn);
                        migrated = true;
                        break;
                    }
                }

                using (var columns = conn.CreateCommand())
                {
                    columns.CommandText = "PRAGMA table_info(TreeNodes);";
                    using var reader = columns.ExecuteReader();
                    while (reader.Read())
                    {
                        if (reader.GetString(1) != "FullPath") continue;
                        reader.Close();
                        MigrateCompactNodes(conn);
                        migrated = true;
                        break;
                    }
                }

                using var indexes = conn.CreateCommand();
                indexes.CommandText = @"
                    DROP INDEX IF EXISTS idx_treenodes_root;
                    DROP INDEX IF EXISTS idx_treenodes_root_path;
                    CREATE INDEX IF NOT EXISTS idx_treenodes_root_parent ON TreeNodes(RootId, ParentId, Name);
                    CREATE INDEX IF NOT EXISTS idx_treenodes_root_order ON TreeNodes(RootId, Id);";
                indexes.ExecuteNonQuery();

                using var version = conn.CreateCommand();
                version.CommandText = "PRAGMA user_version;";
                var vacuumPending = migrated || Convert.ToInt64(version.ExecuteScalar()) == 106;
                if (vacuumPending)
                {
                    // The old table and indexes still occupy free pages until a one-time vacuum.
                    try
                    {
                        using var vacuum = conn.CreateCommand();
                        vacuum.CommandText = "VACUUM; PRAGMA user_version=107;";
                        vacuum.ExecuteNonQuery();
                    }
                    catch (SqliteException ex)
                    {
                        // A disk-space or concurrent-reader failure must not hide a valid migrated cache.
                        System.Diagnostics.Trace.WriteLine($"Tree cache compaction remains pending: {ex}");
                    }
                }

                _isInitialized = true;
            }
        }

        private static void MigrateParentPaths(SqliteConnection conn)
        {
            using (var lookupIndex = conn.CreateCommand())
            {
                lookupIndex.CommandText = "CREATE INDEX IF NOT EXISTS idx_treenodes_root_path ON TreeNodes(RootId, FullPath);";
                lookupIndex.ExecuteNonQuery();
            }
            using (var tx = conn.BeginTransaction())
            {
                using var migrate = conn.CreateCommand();
                migrate.Transaction = tx;
                migrate.CommandText = @"
                    CREATE TABLE TreeNodesCompact (
                        Id INTEGER PRIMARY KEY,
                        RootId INTEGER NOT NULL REFERENCES TreeRoots(Id) ON DELETE CASCADE,
                        FullPath TEXT NOT NULL,
                        Name TEXT NOT NULL,
                        ParentId INTEGER,
                        Size INTEGER NOT NULL,
                        FileCount INTEGER NOT NULL,
                        FolderCount INTEGER NOT NULL,
                        IsDirectory INTEGER NOT NULL,
                        IsExpanded INTEGER NOT NULL,
                        LastModified TEXT,
                        CreationTime TEXT,
                        Sha256 TEXT,
                        Level INTEGER NOT NULL
                    );
                    INSERT INTO TreeNodesCompact
                        (Id, RootId, FullPath, Name, ParentId, Size, FileCount, FolderCount,
                         IsDirectory, IsExpanded, LastModified, CreationTime, Sha256, Level)
                    SELECT n.Id, n.RootId, n.FullPath, n.Name,
                           (SELECT p.Id FROM TreeNodes p
                            WHERE p.RootId = n.RootId AND p.FullPath = n.ParentPath LIMIT 1),
                           n.Size, n.FileCount, n.FolderCount, n.IsDirectory, n.IsExpanded,
                           n.LastModified, n.CreationTime, n.Sha256, n.Level
                    FROM TreeNodes n;";
                migrate.ExecuteNonQuery();

                using (var validate = conn.CreateCommand())
                {
                    validate.Transaction = tx;
                    validate.CommandText = @"SELECT COUNT(*) FROM TreeNodes n
                        JOIN TreeNodesCompact c ON c.Id = n.Id
                        WHERE n.ParentPath IS NOT NULL AND c.ParentId IS NULL;";
                    if ((long)validate.ExecuteScalar()! != 0)
                        throw new InvalidDataException("Tree cache migration found nodes without a parent; original cache was preserved.");
                }

                migrate.CommandText = "DROP TABLE TreeNodes; ALTER TABLE TreeNodesCompact RENAME TO TreeNodes; PRAGMA user_version=104;";
                migrate.ExecuteNonQuery();
                tx.Commit();
            }
        }

        private static void MigrateCompactNodes(SqliteConnection conn)
        {
            // Keep the old table intact until all rows and parent links have been checked.
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = @"
                CREATE TABLE TreeNodesCompact (
                    Id INTEGER PRIMARY KEY,
                    RootId INTEGER NOT NULL REFERENCES TreeRoots(Id) ON DELETE CASCADE,
                    Name TEXT NOT NULL,
                    ParentId INTEGER,
                    PathSuffix TEXT,
                    SubtreeEndId INTEGER,
                    Size INTEGER NOT NULL,
                    FileCount INTEGER NOT NULL,
                    FolderCount INTEGER NOT NULL,
                    IsDirectory INTEGER NOT NULL,
                    LastModified INTEGER,
                    CreationTime INTEGER,
                    Sha256 BLOB
                );
                INSERT INTO TreeNodesCompact
                    (Id, RootId, Name, ParentId, PathSuffix, Size, FileCount, FolderCount,
                     IsDirectory, LastModified, CreationTime, Sha256)
                SELECT n.Id, n.RootId, n.Name, n.ParentId,
                       CASE WHEN n.ParentId IS NULL OR n.FullPath = p.FullPath || '\' || n.Name THEN NULL
                            WHEN substr(n.FullPath, 1, length(p.FullPath) + 1) = p.FullPath || '\'
                                THEN substr(n.FullPath, length(p.FullPath) + 2)
                            ELSE n.FullPath END,
                       n.Size, n.FileCount, n.FolderCount,
                       n.IsDirectory, n.LastModified, n.CreationTime, n.Sha256
                FROM TreeNodes n LEFT JOIN TreeNodes p ON p.Id = n.ParentId AND p.RootId = n.RootId;";
            cmd.ExecuteNonQuery();
            cmd.CommandText = @"SELECT
                (SELECT COUNT(*) FROM TreeNodes) - (SELECT COUNT(*) FROM TreeNodesCompact)
                + (SELECT COUNT(*) FROM TreeNodesCompact n
                   WHERE n.ParentId IS NOT NULL AND NOT EXISTS
                       (SELECT 1 FROM TreeNodesCompact p WHERE p.Id = n.ParentId AND p.RootId = n.RootId));";
            if ((long)cmd.ExecuteScalar()! != 0)
                throw new InvalidDataException("Tree cache compaction found missing nodes or parent links; original cache was preserved.");

            using (var oldDates = conn.CreateCommand())
            using (var convert = conn.CreateCommand())
            using (var finishFolder = conn.CreateCommand())
            {
                oldDates.Transaction = tx;
                oldDates.CommandText = @"SELECT Id, ParentId, IsDirectory, LastModified, CreationTime, Sha256
                    FROM TreeNodes ORDER BY Id;";
                convert.Transaction = tx;
                convert.CommandText = @"UPDATE TreeNodesCompact
                    SET LastModified = @modified, CreationTime = @created, Sha256 = @sha WHERE Id = @id;";
                var id = convert.Parameters.Add("@id", SqliteType.Integer);
                var modified = convert.Parameters.Add("@modified", SqliteType.Integer);
                var created = convert.Parameters.Add("@created", SqliteType.Integer);
                var sha = convert.Parameters.Add("@sha", SqliteType.Blob);
                finishFolder.Transaction = tx;
                finishFolder.CommandText = "UPDATE TreeNodesCompact SET SubtreeEndId = @end WHERE Id = @id;";
                var finishedId = finishFolder.Parameters.Add("@id", SqliteType.Integer);
                var endId = finishFolder.Parameters.Add("@end", SqliteType.Integer);
                var ancestors = new Stack<long>();
                long previousId = 0;
                using var reader = oldDates.ExecuteReader();
                while (reader.Read())
                {
                    var nodeId = reader.GetInt64(0);
                    var parentId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                    while (ancestors.Count > 0 && ancestors.Peek() != parentId)
                    {
                        finishedId.Value = ancestors.Pop();
                        endId.Value = previousId;
                        finishFolder.ExecuteNonQuery();
                    }
                    if (parentId != null && (ancestors.Count == 0 || ancestors.Peek() != parentId))
                        throw new InvalidDataException("Tree cache IDs are not in parent-before-child order.");
                    if (reader.GetInt32(2) == 1) ancestors.Push(nodeId);
                    if (!reader.IsDBNull(3) || !reader.IsDBNull(4) || !reader.IsDBNull(5))
                    {
                        id.Value = nodeId;
                        modified.Value = ParseStoredDate(reader, 3);
                        created.Value = ParseStoredDate(reader, 4);
                        var storedSha = reader.IsDBNull(5) ? null : ToStoredSha(reader.GetString(5));
                        sha.SqliteType = storedSha is byte[] ? SqliteType.Blob : SqliteType.Text;
                        sha.Value = storedSha ?? DBNull.Value;
                        convert.ExecuteNonQuery();
                    }
                    previousId = nodeId;
                }
                while (ancestors.Count > 0)
                {
                    finishedId.Value = ancestors.Pop();
                    endId.Value = previousId;
                    finishFolder.ExecuteNonQuery();
                }
            }
            cmd.CommandText = @"DROP TABLE TreeNodes;
                ALTER TABLE TreeNodesCompact RENAME TO TreeNodes;
                PRAGMA user_version=106;";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }

        private static object ParseStoredDate(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return DBNull.Value;
            return DateTime.TryParse(reader.GetString(ordinal), out var date) ? date.ToBinary() : DBNull.Value;
        }

        private static object? ToStoredSha(string? hash)
        {
            if (string.IsNullOrEmpty(hash)) return null;
            if (hash.Length == 64 && hash.All(Uri.IsHexDigit)) return Convert.FromHexString(hash);
            return hash;
        }

        private static string? ReadSha(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return null;
            return reader.GetFieldType(ordinal) == typeof(byte[])
                ? Convert.ToHexString(reader.GetFieldValue<byte[]>(ordinal)) : reader.GetString(ordinal);
        }

        private static DateTime? ReadDate(SqliteDataReader reader, int ordinal)
        {
            if (reader.IsDBNull(ordinal)) return null;
            return DateTime.FromBinary(reader.GetInt64(ordinal));
        }

        /// <summary>
        /// ツリーを SQLite DB に単一トランザクションで高速保存する。
        /// </summary>
        public async Task SaveTreeAsync(FileItemNode rootNode)
        {
            if (rootNode == null || string.IsNullOrWhiteSpace(rootNode.FullPath)) return;

            await Task.Run(() =>
            {
                EnsureInitialized();

                var canonicalPath = PathCanonicalizer.Normalize(rootNode.FullPath);
                var (topFiles, extStats) = (rootNode.CachedTopFiles != null && rootNode.CachedExtensionStats != null)
                    ? (rootNode.CachedTopFiles, rootNode.CachedExtensionStats)
                    : DiskScanService.GetInsightsForNode(rootNode);

                var topFilesJson = JsonSerializer.Serialize(topFiles);
                var extStatsJson = JsonSerializer.Serialize(extStats);
                var nowIso = DateTime.Now.ToString("o");

                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var tx = conn.BeginTransaction();
                try
                {
                    // 1. 既存の RootId を確認し、存在すれば削除
                    long rootId = -1;
                    using (var findCmd = conn.CreateCommand())
                    {
                        findCmd.Transaction = tx;
                        findCmd.CommandText = "SELECT Id FROM TreeRoots WHERE NormalizedPath = @path;";
                        findCmd.Parameters.AddWithValue("@path", canonicalPath);
                        var existing = findCmd.ExecuteScalar();
                        if (existing != null && existing != DBNull.Value)
                        {
                            rootId = Convert.ToInt64(existing);
                        }
                    }

                    if (rootId > 0)
                    {
                        using var delNodesCmd = conn.CreateCommand();
                        delNodesCmd.Transaction = tx;
                        delNodesCmd.CommandText = "DELETE FROM TreeNodes WHERE RootId = @rootId;";
                        delNodesCmd.Parameters.AddWithValue("@rootId", rootId);
                        delNodesCmd.ExecuteNonQuery();

                        using var delRootCmd = conn.CreateCommand();
                        delRootCmd.Transaction = tx;
                        delRootCmd.CommandText = "DELETE FROM TreeRoots WHERE Id = @rootId;";
                        delRootCmd.Parameters.AddWithValue("@rootId", rootId);
                        delRootCmd.ExecuteNonQuery();
                    }

                    // 2. TreeRoots レコード挿入
                    using (var insertRootCmd = conn.CreateCommand())
                    {
                        insertRootCmd.Transaction = tx;
                        insertRootCmd.CommandText = @"
                            INSERT INTO TreeRoots (
                                NormalizedPath, OriginalTargetPath, Timestamp, TotalSizeBytes, FileCount, FolderCount, TopFilesJson, ExtensionStatsJson
                            ) VALUES (
                                @normPath, @origPath, @ts, @size, @files, @folders, @topFiles, @extStats
                            );
                            SELECT last_insert_rowid();
                        ";
                        insertRootCmd.Parameters.AddWithValue("@normPath", canonicalPath);
                        insertRootCmd.Parameters.AddWithValue("@origPath", rootNode.FullPath);
                        insertRootCmd.Parameters.AddWithValue("@ts", nowIso);
                        insertRootCmd.Parameters.AddWithValue("@size", rootNode.Size);
                        insertRootCmd.Parameters.AddWithValue("@files", rootNode.FileCount);
                        insertRootCmd.Parameters.AddWithValue("@folders", rootNode.FolderCount);
                        insertRootCmd.Parameters.AddWithValue("@topFiles", (object?)topFilesJson ?? DBNull.Value);
                        insertRootCmd.Parameters.AddWithValue("@extStats", (object?)extStatsJson ?? DBNull.Value);

                        rootId = (long)insertRootCmd.ExecuteScalar()!;
                    }

                    // 3. TreeNodes を単一トランザクションで一括挿入
                    long nextNodeId;
                    using (var nextIdCmd = conn.CreateCommand())
                    {
                        nextIdCmd.Transaction = tx;
                        nextIdCmd.CommandText = "SELECT IFNULL(MAX(Id), 0) FROM TreeNodes;";
                        nextNodeId = (long)nextIdCmd.ExecuteScalar()!;
                    }
                    using (var insertNodeCmd = conn.CreateCommand())
                    {
                        insertNodeCmd.Transaction = tx;
                        insertNodeCmd.CommandText = @"
                            INSERT INTO TreeNodes (
                                Id, RootId, Name, ParentId, PathSuffix, SubtreeEndId, Size, FileCount, FolderCount, IsDirectory, LastModified, CreationTime, Sha256
                            ) VALUES (
                                @id, @rootId, @name, @parentId, @suffix, NULL, @size, @files, @folders, @isDir, @mod, @create, @sha
                            );
                        ";

                        var pId = insertNodeCmd.Parameters.Add("@id", SqliteType.Integer);
                        var pRootId = insertNodeCmd.Parameters.Add("@rootId", SqliteType.Integer);
                        var pName = insertNodeCmd.Parameters.Add("@name", SqliteType.Text);
                        var pParentId = insertNodeCmd.Parameters.Add("@parentId", SqliteType.Integer);
                        var pSuffix = insertNodeCmd.Parameters.Add("@suffix", SqliteType.Text);
                        var pSize = insertNodeCmd.Parameters.Add("@size", SqliteType.Integer);
                        var pFiles = insertNodeCmd.Parameters.Add("@files", SqliteType.Integer);
                        var pFolders = insertNodeCmd.Parameters.Add("@folders", SqliteType.Integer);
                        var pIsDir = insertNodeCmd.Parameters.Add("@isDir", SqliteType.Integer);
                        var pMod = insertNodeCmd.Parameters.Add("@mod", SqliteType.Integer);
                        var pCreate = insertNodeCmd.Parameters.Add("@create", SqliteType.Integer);
                        var pSha = insertNodeCmd.Parameters.Add("@sha", SqliteType.Blob);

                        pRootId.Value = rootId;

                        using var finishNodeCmd = conn.CreateCommand();
                        finishNodeCmd.Transaction = tx;
                        finishNodeCmd.CommandText = "UPDATE TreeNodes SET SubtreeEndId = @end WHERE Id = @id;";
                        var finishId = finishNodeCmd.Parameters.Add("@id", SqliteType.Integer);
                        var finishEnd = finishNodeCmd.Parameters.Add("@end", SqliteType.Integer);

                        void InsertRecursive(FileItemNode node, long? parentId, string? parentPath)
                        {
                            var nodeId = ++nextNodeId;
                            pId.Value = nodeId;
                            pName.Value = node.Name ?? string.Empty;
                            pParentId.Value = (object?)parentId ?? DBNull.Value;
                            pSuffix.Value = parentPath == null ? DBNull.Value : GetPathSuffix(parentPath, node) ?? (object)DBNull.Value;
                            pSize.Value = node.Size;
                            pFiles.Value = node.FileCount;
                            pFolders.Value = node.FolderCount;
                            pIsDir.Value = node.IsDirectory ? 1 : 0;
                            pMod.Value = node.LastModified.HasValue ? node.LastModified.Value.ToBinary() : DBNull.Value;
                            pCreate.Value = node.CreationTime.HasValue ? node.CreationTime.Value.ToBinary() : DBNull.Value;
                            var storedSha = ToStoredSha(node.Sha256);
                            pSha.SqliteType = storedSha is byte[] ? SqliteType.Blob : SqliteType.Text;
                            pSha.Value = storedSha ?? DBNull.Value;

                            insertNodeCmd.ExecuteNonQuery();

                            if (node.Children != null)
                            {
                                for (int i = 0; i < node.Children.Count; i++)
                                {
                                    InsertRecursive(node.Children[i], nodeId, node.FullPath);
                                }
                            }
                            if (node.IsDirectory)
                            {
                                finishId.Value = nodeId;
                                finishEnd.Value = nextNodeId;
                                finishNodeCmd.ExecuteNonQuery();
                            }
                        }

                        InsertRecursive(rootNode, null, null);
                    }

                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            });
        }

        /// <summary>
        /// SQLite DB からツリーを高速にメモリツリーへ復元する。
        /// </summary>
        public async Task<FileItemNode?> LoadTreeAsync(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return null;

            return await Task.Run(() =>
            {
                EnsureInitialized();

                var canonicalPath = PathCanonicalizer.Normalize(targetPath);

                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                long rootId = -1;
                string? originalPath = null;
                string? topFilesJson = null;
                string? extStatsJson = null;

                using (var findCmd = conn.CreateCommand())
                {
                    findCmd.CommandText = "SELECT Id, OriginalTargetPath, TopFilesJson, ExtensionStatsJson FROM TreeRoots WHERE NormalizedPath = @path;";
                    findCmd.Parameters.AddWithValue("@path", canonicalPath);
                    using var reader = findCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        rootId = reader.GetInt64(0);
                        originalPath = reader.GetString(1);
                        if (!reader.IsDBNull(2)) topFilesJson = reader.GetString(2);
                        if (!reader.IsDBNull(3)) extStatsJson = reader.GetString(3);
                    }
                }

                if (rootId <= 0) return null;

                // SaveTreeAsync allocates IDs in preorder, so parent IDs precede their children.
                var nodeMap = new Dictionary<long, FileItemNode>();
                FileItemNode? rootNode = null;

                using (var loadNodesCmd = conn.CreateCommand())
                {
                    loadNodesCmd.CommandText = @"
                        SELECT Name, ParentId, Size, FileCount, FolderCount, IsDirectory, LastModified, CreationTime, Sha256, Id, PathSuffix
                        FROM TreeNodes
                        WHERE RootId = @rootId
                        ORDER BY Id;
                    ";
                    loadNodesCmd.Parameters.AddWithValue("@rootId", rootId);

                    using var reader = loadNodesCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var nodeId = reader.GetInt64(9);
                        var parentId = reader.IsDBNull(1) ? (long?)null : reader.GetInt64(1);
                        if (parentId == null)
                        {
                            var node = ReadNode(reader, originalPath!, 0, false);
                            rootNode ??= node;
                            nodeMap[nodeId] = node;
                        }
                        else
                        {
                            if (!nodeMap.TryGetValue(parentId.Value, out var parentNode))
                                throw new InvalidDataException("Tree cache node has no preceding parent.");
                            var suffix = reader.IsDBNull(10) ? null : reader.GetString(10);
                            var node = ReadNode(reader, ChildPath(parentNode.FullPath, reader.GetString(0), suffix), parentNode.Level + 1, false);
                            node.Parent = parentNode;
                            parentNode.Children.Add(node);
                            nodeMap[nodeId] = node;
                        }
                    }
                }

                if (rootNode == null) return null;

                // メトリクス・インサイトの復元
                if (!string.IsNullOrEmpty(topFilesJson))
                {
                    try { rootNode.CachedTopFiles = JsonSerializer.Deserialize<List<LargestFileInfo>>(topFilesJson); } catch { }
                }
                if (!string.IsNullOrEmpty(extStatsJson))
                {
                    try { rootNode.CachedExtensionStats = JsonSerializer.Deserialize<List<ExtensionStat>>(extStatsJson); } catch { }
                }

                DiskScanService.CalculatePercentages(rootNode, rootNode.Size > 0 ? rootNode.Size : 1);
                return rootNode;
            });
        }

        private static FileItemNode ReadNode(SqliteDataReader reader, string fullPath, int level, bool markUnloaded)
        {
            var isDirectory = reader.GetInt32(5) == 1;
            var fileCount = reader.GetInt32(3);
            var folderCount = reader.GetInt32(4);
            return new FileItemNode
            {
                FullPath = fullPath,
                Name = reader.GetString(0),
                Size = reader.GetInt64(2),
                FileCount = fileCount,
                FolderCount = folderCount,
                IsDirectory = isDirectory,
                IsExpanded = false,
                LastModified = ReadDate(reader, 6),
                CreationTime = ReadDate(reader, 7),
                Sha256 = ReadSha(reader, 8),
                Level = level,
                HasUnloadedChildren = markUnloaded && isDirectory && (fileCount > 0 || folderCount > 0)
            };
        }

        private static long? FindNodeId(SqliteConnection conn, long rootId, string originalRootPath,
            string path, Dictionary<string, long>? pathIds = null, SqliteTransaction? transaction = null)
        {
            var rootPath = originalRootPath.TrimEnd('\\');
            using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = "SELECT Id FROM TreeNodes WHERE RootId = @rootId AND ParentId IS NULL LIMIT 1;";
            cmd.Parameters.AddWithValue("@rootId", rootId);
            var rootValue = cmd.ExecuteScalar();
            if (rootValue == null) return null;
            var id = Convert.ToInt64(rootValue);
            if (string.Equals(path.TrimEnd('\\'), rootPath, StringComparison.OrdinalIgnoreCase)) return id;
            if (!path.StartsWith(rootPath + "\\", StringComparison.OrdinalIgnoreCase)) return null;

            var currentPath = rootPath;
            var relative = path[(rootPath.Length + 1)..].TrimEnd('\\');
            cmd.CommandText = "SELECT Id FROM TreeNodes WHERE RootId = @rootId AND ParentId = @parentId AND Name = @name LIMIT 1;";
            var parentParam = cmd.Parameters.Add("@parentId", SqliteType.Integer);
            var nameParam = cmd.Parameters.Add("@name", SqliteType.Text);
            foreach (var segment in relative.Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                currentPath += "\\" + segment;
                if (pathIds != null && pathIds.TryGetValue(currentPath, out var cached))
                {
                    id = cached;
                    continue;
                }
                parentParam.Value = id;
                nameParam.Value = segment;
                var found = cmd.ExecuteScalar();
                if (found == null)
                {
                    // Sparse compatibility path for caches whose former FullPath skipped an intermediate node.
                    using var exceptional = conn.CreateCommand();
                    exceptional.Transaction = transaction;
                    exceptional.CommandText = @"SELECT Id FROM TreeNodes WHERE RootId = @rootId
                        AND ParentId = @parentId AND (PathSuffix = @relative OR PathSuffix = @full) LIMIT 1;";
                    exceptional.Parameters.AddWithValue("@rootId", rootId);
                    exceptional.Parameters.AddWithValue("@parentId", id);
                    exceptional.Parameters.AddWithValue("@relative", path[(currentPath.Length - segment.Length)..]);
                    exceptional.Parameters.AddWithValue("@full", path);
                    found = exceptional.ExecuteScalar();
                    return found == null ? null : Convert.ToInt64(found);
                }
                id = Convert.ToInt64(found);
                pathIds?.Add(currentPath, id);
            }
            return id;
        }

        private static string BuildPath(SqliteConnection conn, long rootId, string originalRootPath, long nodeId)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT Name, ParentId, PathSuffix FROM TreeNodes WHERE RootId = @rootId AND Id = @id;";
            cmd.Parameters.AddWithValue("@rootId", rootId);
            var idParam = cmd.Parameters.Add("@id", SqliteType.Integer);
            var segments = new Stack<string>();
            var id = nodeId;
            while (true)
            {
                idParam.Value = id;
                using var reader = cmd.ExecuteReader();
                if (!reader.Read()) throw new InvalidDataException("Tree cache path has a missing parent.");
                if (reader.IsDBNull(1)) break;
                var segment = reader.IsDBNull(2) ? reader.GetString(0) : reader.GetString(2);
                if (Path.IsPathFullyQualified(segment))
                {
                    var absolute = segment;
                    while (segments.Count > 0) absolute = Path.Combine(absolute, segments.Pop());
                    return absolute;
                }
                segments.Push(segment);
                id = reader.GetInt64(1);
            }
            var path = originalRootPath;
            while (segments.Count > 0) path = Path.Combine(path, segments.Pop());
            return path;
        }

        /// <summary>Load one folder and its direct children through the indexed ParentId lookup.</summary>
        public Task<FileItemNode?> LoadBranchAsync(string rootPath, string folderPath) => Task.Run(() =>
        {
            EnsureInitialized();
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            long rootId;
            string originalPath;
            long rootBytes;
            string? topFilesJson;
            string? extStatsJson;
            using (var rootCmd = conn.CreateCommand())
            {
                rootCmd.CommandText = "SELECT Id, OriginalTargetPath, TotalSizeBytes, TopFilesJson, ExtensionStatsJson FROM TreeRoots WHERE NormalizedPath = @path;";
                rootCmd.Parameters.AddWithValue("@path", PathCanonicalizer.Normalize(rootPath));
                using var reader = rootCmd.ExecuteReader();
                if (!reader.Read()) return null;
                rootId = reader.GetInt64(0);
                originalPath = reader.GetString(1);
                rootBytes = reader.GetInt64(2);
                topFilesJson = reader.IsDBNull(3) ? null : reader.GetString(3);
                extStatsJson = reader.IsDBNull(4) ? null : reader.GetString(4);
            }

            var branchIdValue = FindNodeId(conn, rootId, originalPath, folderPath);
            if (branchIdValue == null) return null;
            FileItemNode? branch;
            var branchId = branchIdValue.Value;
            using (var nodeCmd = conn.CreateCommand())
            {
                nodeCmd.CommandText = @"SELECT Name, ParentId, Size, FileCount, FolderCount, IsDirectory, LastModified, CreationTime, Sha256, Id, PathSuffix
                    FROM TreeNodes WHERE RootId = @rootId AND Id = @id;";
                nodeCmd.Parameters.AddWithValue("@rootId", rootId);
                nodeCmd.Parameters.AddWithValue("@id", branchId);
                using var reader = nodeCmd.ExecuteReader();
                if (!reader.Read()) return null;
                var relative = folderPath.TrimEnd('\\')[originalPath.TrimEnd('\\').Length..].TrimStart('\\');
                var level = relative.Length == 0 ? 0 : relative.Count(c => c == '\\') + 1;
                branch = ReadNode(reader, folderPath, level, false);
                branch.Percentage = rootBytes > 0 ? (double)branch.Size / rootBytes * 100 : 0;
            }

            using (var childrenCmd = conn.CreateCommand())
            {
                childrenCmd.CommandText = @"SELECT Name, ParentId, Size, FileCount, FolderCount, IsDirectory, LastModified, CreationTime, Sha256, Id, PathSuffix
                    FROM TreeNodes WHERE RootId = @rootId AND ParentId = @parentId ORDER BY IsDirectory DESC, Size DESC;";
                childrenCmd.Parameters.AddWithValue("@rootId", rootId);
                childrenCmd.Parameters.AddWithValue("@parentId", branchId);
                using var reader = childrenCmd.ExecuteReader();
                while (reader.Read())
                {
                    var suffix = reader.IsDBNull(10) ? null : reader.GetString(10);
                    var child = ReadNode(reader, ChildPath(branch.FullPath, reader.GetString(0), suffix), branch.Level + 1, true);
                    child.Parent = branch;
                    child.Percentage = rootBytes > 0 ? (double)child.Size / rootBytes * 100 : 0;
                    branch.Children.Add(child);
                }
            }

            if (string.Equals(folderPath, rootPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(topFilesJson))
                    try { branch.CachedTopFiles = JsonSerializer.Deserialize<List<LargestFileInfo>>(topFilesJson); } catch { }
                if (!string.IsNullOrEmpty(extStatsJson))
                    try { branch.CachedExtensionStats = JsonSerializer.Deserialize<List<ExtensionStat>>(extStatsJson); } catch { }
            }
            return branch;
        });

        public Task<List<LargestFileInfo>> GetTopFilesForSubtreeAsync(string rootPath, string folderPath) => Task.Run(() =>
        {
            EnsureInitialized();
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            var root = FindRoot(conn, rootPath);
            if (root == null) return new List<LargestFileInfo>();
            var folderId = FindNodeId(conn, root.Value.Id, root.Value.OriginalPath, folderPath);
            if (folderId == null) return new List<LargestFileInfo>();
            long lastId;
            using (var endCmd = conn.CreateCommand())
            {
                endCmd.CommandText = "SELECT SubtreeEndId FROM TreeNodes WHERE RootId = @rootId AND Id = @folderId;";
                endCmd.Parameters.AddWithValue("@rootId", root.Value.Id);
                endCmd.Parameters.AddWithValue("@folderId", folderId.Value);
                var end = endCmd.ExecuteScalar();
                if (end == null || end == DBNull.Value) return new List<LargestFileInfo>();
                lastId = Convert.ToInt64(end);
            }
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT Id, Name, Size FROM TreeNodes
                WHERE RootId = @rootId AND Id > @folderId AND Id <= @lastId AND IsDirectory = 0
                ORDER BY Size DESC LIMIT 10;";
            cmd.Parameters.AddWithValue("@folderId", folderId.Value);
            cmd.Parameters.AddWithValue("@rootId", root.Value.Id);
            cmd.Parameters.AddWithValue("@lastId", lastId);
            var files = new List<LargestFileInfo>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = BuildPath(conn, root.Value.Id, root.Value.OriginalPath, reader.GetInt64(0));
                var extension = Path.GetExtension(path);
                files.Add(new LargestFileInfo
                {
                    Name = reader.GetString(1), FullPath = path, Size = reader.GetInt64(2),
                    Extension = extension, Category = DiskScanService.GetCategoryForExtension(extension)
                });
            }
            return files;
        });

        /// <summary>
        /// キャッシュ内のファイル SHA-256 ハッシュを DB 上で直接一括更新する（Sol提唱 ADR 92/98）。
        /// メモリを浪費せず、指定パスのレコードだけを高速に UPDATE。
        /// </summary>
        public async Task UpdateSha256Async(string targetPath, IDictionary<string, string> hashMap)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || hashMap == null || hashMap.Count == 0) return;

            await Task.Run(() =>
            {
                EnsureInitialized();

                var canonicalPath = PathCanonicalizer.Normalize(targetPath);

                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                var root = FindRoot(conn, canonicalPath);
                if (root == null) return;

                using var tx = conn.BeginTransaction();
                try
                {
                    using var updateCmd = conn.CreateCommand();
                    updateCmd.Transaction = tx;
                    updateCmd.CommandText = @"
                        UPDATE TreeNodes
                        SET Sha256 = @sha
                        WHERE RootId = @rootId AND Id = @id;
                    ";
                    var pSha = updateCmd.Parameters.Add("@sha", SqliteType.Blob);
                    var pId = updateCmd.Parameters.Add("@id", SqliteType.Integer);
                    updateCmd.Parameters.AddWithValue("@rootId", root.Value.Id);
                    var pathIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

                    foreach (var kvp in hashMap)
                    {
                        var id = FindNodeId(conn, root.Value.Id, root.Value.OriginalPath, kvp.Key, pathIds, tx);
                        if (id == null) continue;
                        var storedSha = ToStoredSha(kvp.Value);
                        pSha.SqliteType = storedSha is byte[] ? SqliteType.Blob : SqliteType.Text;
                        pSha.Value = storedSha ?? DBNull.Value;
                        pId.Value = id.Value;
                        updateCmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            });
        }

        /// <summary>
        /// キャッシュされているすべてのルート情報を取得する。
        /// </summary>
        public async Task<List<TreeCacheSummary>> GetAllRootsAsync()
        {
            return await Task.Run(() =>
            {
                EnsureInitialized();

                var list = new List<TreeCacheSummary>();
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT Id, NormalizedPath, OriginalTargetPath, Timestamp, TotalSizeBytes, FileCount, FolderCount
                    FROM TreeRoots
                    ORDER BY Timestamp DESC;
                ";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    list.Add(new TreeCacheSummary
                    {
                        Id = reader.GetInt64(0),
                        NormalizedPath = reader.GetString(1),
                        OriginalTargetPath = reader.GetString(2),
                        Timestamp = DateTime.TryParse(reader.GetString(3), out var dt) ? dt : DateTime.MinValue,
                        TotalSizeBytes = reader.GetInt64(4),
                        FileCount = reader.GetInt32(5),
                        FolderCount = reader.GetInt32(6)
                    });
                }

                return list;
            });
        }

        /// <summary>
        /// 指定ターゲットのキャッシュを DB から削除する。
        /// </summary>
        public async Task DeleteRootAsync(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return;

            await Task.Run(() =>
            {
                EnsureInitialized();

                var canonicalPath = PathCanonicalizer.Normalize(targetPath);

                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var tx = conn.BeginTransaction();
                try
                {
                    long rootId = -1;
                    using (var findCmd = conn.CreateCommand())
                    {
                        findCmd.Transaction = tx;
                        findCmd.CommandText = "SELECT Id FROM TreeRoots WHERE NormalizedPath = @path;";
                        findCmd.Parameters.AddWithValue("@path", canonicalPath);
                        var existing = findCmd.ExecuteScalar();
                        if (existing != null && existing != DBNull.Value) rootId = Convert.ToInt64(existing);
                    }

                    if (rootId > 0)
                    {
                        using var delNodesCmd = conn.CreateCommand();
                        delNodesCmd.Transaction = tx;
                        delNodesCmd.CommandText = "DELETE FROM TreeNodes WHERE RootId = @rootId;";
                        delNodesCmd.Parameters.AddWithValue("@rootId", rootId);
                        delNodesCmd.ExecuteNonQuery();

                        using var delRootCmd = conn.CreateCommand();
                        delRootCmd.Transaction = tx;
                        delRootCmd.CommandText = "DELETE FROM TreeRoots WHERE Id = @rootId;";
                        delRootCmd.Parameters.AddWithValue("@rootId", rootId);
                        delRootCmd.ExecuteNonQuery();
                    }

                    tx.Commit();
                }
                catch
                {
                    tx.Rollback();
                    throw;
                }
            });
        }

        #region ポータブル JSON 相互運用 (Export / Import)

        /// <summary>
        /// 指定ターゲットパスのツリーを JSON ファイルへエクスポートする（共有・配布用）。
        /// 従来の TreeCacheRoot JSON と完全な互換性を維持。
        /// </summary>
        public async Task ExportToJsonFileAsync(string targetPath, string jsonFilePath)
        {
            var rootNode = await LoadTreeAsync(targetPath);
            if (rootNode == null)
            {
                throw new InvalidOperationException($"キャッシュが見つかりません: {targetPath}");
            }

            var dir = Path.GetDirectoryName(jsonFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var (topFiles, extStats) = (rootNode.CachedTopFiles != null && rootNode.CachedExtensionStats != null)
                ? (rootNode.CachedTopFiles, rootNode.CachedExtensionStats)
                : DiskScanService.GetInsightsForNode(rootNode);

            var cacheRoot = new TreeCacheRoot
            {
                TargetPath = rootNode.FullPath,
                Timestamp = DateTime.Now,
                Root = StorageHistoryService.ToCacheNode(rootNode),
                TopFiles = topFiles,
                ExtensionStats = extStats
            };

            var options = new JsonSerializerOptions { WriteIndented = false };
            var json = JsonSerializer.Serialize(cacheRoot, options);

            var tempFile = jsonFilePath + $".tmp_{Guid.NewGuid():N}";
            await File.WriteAllTextAsync(tempFile, json);
            File.Move(tempFile, jsonFilePath, overwrite: true);
        }

        /// <summary>
        /// JSON ファイルからツリーキャッシュをインポートし、ローカル SQLite DB へ取り込む。
        /// </summary>
        public async Task<bool> ImportFromJsonFileAsync(string jsonFilePath)
        {
            if (!File.Exists(jsonFilePath)) return false;

            var json = await File.ReadAllTextAsync(jsonFilePath);
            var cacheRoot = JsonSerializer.Deserialize<TreeCacheRoot>(json);
            if (cacheRoot?.Root == null) return false;

            var rootNode = StorageHistoryService.FromCacheNode(cacheRoot.Root, null, 0);
            rootNode.CachedTopFiles = cacheRoot.TopFiles;
            rootNode.CachedExtensionStats = cacheRoot.ExtensionStats;

            await SaveTreeAsync(rootNode);
            return true;
        }

        #endregion
    }
}
