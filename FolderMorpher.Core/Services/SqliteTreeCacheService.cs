using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>
    /// SQLite ローカル専有ツリーキャッシュ サービス（ADR 98）。
    /// 
    /// 【設計方針】
    /// 1. 完全ローカル専有: %LocalAppData%\FolderMorpher\TreeCache\tree_cache.db にのみ保持。
    ///    共有フォルダー（UNC）には一切 DB ファイルを配置せず、ロック競合や遅延破損をゼロ化。
    /// 2. 事前集計 Materialized: 各フォルダーノードに集計サイズ・ファイル数を保存し、深階層でも O(1) 遅延ロード可能。
    /// 3. 単一トランザクション一括コミット: 数万ノードでもミリ秒単位で超高速保存。
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

            _dbFilePath = Path.Combine(dir, "tree_cache.db");

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
                        Id INTEGER PRIMARY KEY AUTOINCREMENT,
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

                using var indexes = conn.CreateCommand();
                indexes.CommandText = @"
                    DROP INDEX IF EXISTS idx_treenodes_root;
                    CREATE INDEX IF NOT EXISTS idx_treenodes_root_parent ON TreeNodes(RootId, ParentId);
                    CREATE INDEX IF NOT EXISTS idx_treenodes_root_path ON TreeNodes(RootId, FullPath);";
                indexes.ExecuteNonQuery();

                using var version = conn.CreateCommand();
                version.CommandText = "PRAGMA user_version;";
                var vacuumPending = migrated || Convert.ToInt64(version.ExecuteScalar()) == 104;
                if (vacuumPending)
                {
                    // The old table and indexes still occupy free pages until a one-time vacuum.
                    try
                    {
                        using var vacuum = conn.CreateCommand();
                        vacuum.CommandText = "VACUUM; PRAGMA user_version=105;";
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
                                Id, RootId, FullPath, Name, ParentId, Size, FileCount, FolderCount, IsDirectory, IsExpanded, LastModified, CreationTime, Sha256, Level
                            ) VALUES (
                                @id, @rootId, @path, @name, @parentId, @size, @files, @folders, @isDir, @isExp, @mod, @create, @sha, @lvl
                            );
                        ";

                        var pId = insertNodeCmd.Parameters.Add("@id", SqliteType.Integer);
                        var pRootId = insertNodeCmd.Parameters.Add("@rootId", SqliteType.Integer);
                        var pPath = insertNodeCmd.Parameters.Add("@path", SqliteType.Text);
                        var pName = insertNodeCmd.Parameters.Add("@name", SqliteType.Text);
                        var pParentId = insertNodeCmd.Parameters.Add("@parentId", SqliteType.Integer);
                        var pSize = insertNodeCmd.Parameters.Add("@size", SqliteType.Integer);
                        var pFiles = insertNodeCmd.Parameters.Add("@files", SqliteType.Integer);
                        var pFolders = insertNodeCmd.Parameters.Add("@folders", SqliteType.Integer);
                        var pIsDir = insertNodeCmd.Parameters.Add("@isDir", SqliteType.Integer);
                        var pIsExp = insertNodeCmd.Parameters.Add("@isExp", SqliteType.Integer);
                        var pMod = insertNodeCmd.Parameters.Add("@mod", SqliteType.Text);
                        var pCreate = insertNodeCmd.Parameters.Add("@create", SqliteType.Text);
                        var pSha = insertNodeCmd.Parameters.Add("@sha", SqliteType.Text);
                        var pLvl = insertNodeCmd.Parameters.Add("@lvl", SqliteType.Integer);

                        pRootId.Value = rootId;

                        void InsertRecursive(FileItemNode node, long? parentId)
                        {
                            var nodeId = ++nextNodeId;
                            pId.Value = nodeId;
                            pPath.Value = node.FullPath ?? string.Empty;
                            pName.Value = node.Name ?? string.Empty;
                            pParentId.Value = (object?)parentId ?? DBNull.Value;
                            pSize.Value = node.Size;
                            pFiles.Value = node.FileCount;
                            pFolders.Value = node.FolderCount;
                            pIsDir.Value = node.IsDirectory ? 1 : 0;
                            pIsExp.Value = node.IsExpanded ? 1 : 0;
                            pMod.Value = node.LastModified.HasValue ? (object)node.LastModified.Value.ToString("o") : DBNull.Value;
                            pCreate.Value = node.CreationTime.HasValue ? (object)node.CreationTime.Value.ToString("o") : DBNull.Value;
                            pSha.Value = !string.IsNullOrEmpty(node.Sha256) ? (object)node.Sha256 : DBNull.Value;
                            pLvl.Value = node.Level;

                            insertNodeCmd.ExecuteNonQuery();

                            if (node.Children != null)
                            {
                                for (int i = 0; i < node.Children.Count; i++)
                                {
                                    InsertRecursive(node.Children[i], nodeId);
                                }
                            }
                        }

                        InsertRecursive(rootNode, null);
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
                string? topFilesJson = null;
                string? extStatsJson = null;

                using (var findCmd = conn.CreateCommand())
                {
                    findCmd.CommandText = "SELECT Id, TopFilesJson, ExtensionStatsJson FROM TreeRoots WHERE NormalizedPath = @path;";
                    findCmd.Parameters.AddWithValue("@path", canonicalPath);
                    using var reader = findCmd.ExecuteReader();
                    if (reader.Read())
                    {
                        rootId = reader.GetInt64(0);
                        if (!reader.IsDBNull(1)) topFilesJson = reader.GetString(1);
                        if (!reader.IsDBNull(2)) extStatsJson = reader.GetString(2);
                    }
                }

                if (rootId <= 0) return null;

                // Level ASC でソートして読み込むことで、親が必ず辞書に先に存在するかマップ化が容易
                var nodeMap = new Dictionary<long, FileItemNode>();
                FileItemNode? rootNode = null;

                using (var loadNodesCmd = conn.CreateCommand())
                {
                    loadNodesCmd.CommandText = @"
                        SELECT FullPath, Name, ParentId, Size, FileCount, FolderCount, IsDirectory, IsExpanded, LastModified, CreationTime, Sha256, Level, Id
                        FROM TreeNodes
                        WHERE RootId = @rootId
                        ORDER BY Level ASC;
                    ";
                    loadNodesCmd.Parameters.AddWithValue("@rootId", rootId);

                    using var reader = loadNodesCmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var node = ReadNode(reader, false);
                        var nodeId = reader.GetInt64(12);
                        var parentId = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);

                        if (parentId == null || !nodeMap.TryGetValue(parentId.Value, out var parentNode))
                        {
                            // ルートノード
                            if (rootNode == null)
                            {
                                rootNode = node;
                            }
                        }
                        else
                        {
                            node.Parent = parentNode;
                            parentNode.Children.Add(node);
                        }

                        nodeMap[nodeId] = node;
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

        private static FileItemNode ReadNode(SqliteDataReader reader, bool markUnloaded)
        {
            var isDirectory = reader.GetInt32(6) == 1;
            var fileCount = reader.GetInt32(4);
            var folderCount = reader.GetInt32(5);
            return new FileItemNode
            {
                FullPath = reader.GetString(0),
                Name = reader.GetString(1),
                Size = reader.GetInt64(3),
                FileCount = fileCount,
                FolderCount = folderCount,
                IsDirectory = isDirectory,
                IsExpanded = !markUnloaded && reader.GetInt32(7) == 1,
                LastModified = reader.IsDBNull(8) ? null : DateTime.TryParse(reader.GetString(8), out var modified) ? modified : null,
                CreationTime = reader.IsDBNull(9) ? null : DateTime.TryParse(reader.GetString(9), out var created) ? created : null,
                Sha256 = reader.IsDBNull(10) ? null : reader.GetString(10),
                Level = reader.GetInt32(11),
                HasUnloadedChildren = markUnloaded && isDirectory && (fileCount > 0 || folderCount > 0)
            };
        }

        /// <summary>Load one folder and its direct children through the indexed ParentId lookup.</summary>
        public Task<FileItemNode?> LoadBranchAsync(string rootPath, string folderPath) => Task.Run(() =>
        {
            EnsureInitialized();
            using var conn = new SqliteConnection(_connectionString);
            conn.Open();
            long rootId;
            long rootBytes;
            string? topFilesJson;
            string? extStatsJson;
            using (var rootCmd = conn.CreateCommand())
            {
                rootCmd.CommandText = "SELECT Id, TotalSizeBytes, TopFilesJson, ExtensionStatsJson FROM TreeRoots WHERE NormalizedPath = @path;";
                rootCmd.Parameters.AddWithValue("@path", PathCanonicalizer.Normalize(rootPath));
                using var reader = rootCmd.ExecuteReader();
                if (!reader.Read()) return null;
                rootId = reader.GetInt64(0);
                rootBytes = reader.GetInt64(1);
                topFilesJson = reader.IsDBNull(2) ? null : reader.GetString(2);
                extStatsJson = reader.IsDBNull(3) ? null : reader.GetString(3);
            }

            FileItemNode? branch;
            long branchId;
            using (var nodeCmd = conn.CreateCommand())
            {
                nodeCmd.CommandText = @"SELECT FullPath, Name, ParentId, Size, FileCount, FolderCount, IsDirectory, IsExpanded, LastModified, CreationTime, Sha256, Level, Id
                    FROM TreeNodes WHERE RootId = @rootId AND FullPath = @path;";
                nodeCmd.Parameters.AddWithValue("@rootId", rootId);
                nodeCmd.Parameters.AddWithValue("@path", folderPath);
                using var reader = nodeCmd.ExecuteReader();
                if (!reader.Read()) return null;
                branch = ReadNode(reader, false);
                branchId = reader.GetInt64(12);
                branch.Percentage = rootBytes > 0 ? (double)branch.Size / rootBytes * 100 : 0;
            }

            using (var childrenCmd = conn.CreateCommand())
            {
                childrenCmd.CommandText = @"SELECT FullPath, Name, ParentId, Size, FileCount, FolderCount, IsDirectory, IsExpanded, LastModified, CreationTime, Sha256, Level
                    FROM TreeNodes WHERE RootId = @rootId AND ParentId = @parentId ORDER BY IsDirectory DESC, Size DESC;";
                childrenCmd.Parameters.AddWithValue("@rootId", rootId);
                childrenCmd.Parameters.AddWithValue("@parentId", branchId);
                using var reader = childrenCmd.ExecuteReader();
                while (reader.Read())
                {
                    var child = ReadNode(reader, true);
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
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT Name, FullPath, Size FROM TreeNodes
                WHERE RootId = (SELECT Id FROM TreeRoots WHERE NormalizedPath = @root)
                  AND IsDirectory = 0 AND FullPath >= @lower AND FullPath < @upper
                ORDER BY Size DESC LIMIT 10;";
            cmd.Parameters.AddWithValue("@root", PathCanonicalizer.Normalize(rootPath));
            var lower = folderPath.TrimEnd('\\') + "\\";
            cmd.Parameters.AddWithValue("@lower", lower);
            cmd.Parameters.AddWithValue("@upper", lower[..^1] + "]");
            var files = new List<LargestFileInfo>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var path = reader.GetString(1);
                var extension = Path.GetExtension(path);
                files.Add(new LargestFileInfo
                {
                    Name = reader.GetString(0), FullPath = path, Size = reader.GetInt64(2),
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

                long rootId = -1;
                using (var findCmd = conn.CreateCommand())
                {
                    findCmd.CommandText = "SELECT Id FROM TreeRoots WHERE NormalizedPath = @path;";
                    findCmd.Parameters.AddWithValue("@path", canonicalPath);
                    var existing = findCmd.ExecuteScalar();
                    if (existing != null && existing != DBNull.Value)
                    {
                        rootId = Convert.ToInt64(existing);
                    }
                }

                if (rootId <= 0) return;

                using var tx = conn.BeginTransaction();
                try
                {
                    using var updateCmd = conn.CreateCommand();
                    updateCmd.Transaction = tx;
                    updateCmd.CommandText = @"
                        UPDATE TreeNodes
                        SET Sha256 = @sha
                        WHERE RootId = @rootId AND FullPath = @path;
                    ";
                    var pSha = updateCmd.Parameters.Add("@sha", SqliteType.Text);
                    var pPath = updateCmd.Parameters.Add("@path", SqliteType.Text);
                    updateCmd.Parameters.AddWithValue("@rootId", rootId);

                    foreach (var kvp in hashMap)
                    {
                        pSha.Value = (object?)kvp.Value ?? DBNull.Value;
                        pPath.Value = kvp.Key;
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
