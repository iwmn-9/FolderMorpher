using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AstraSize.Models;
using FolderMorpher.Models;
using Microsoft.Data.Sqlite;

namespace FolderMorpher.Services
{
    public class IndexProgressReport
    {
        public int TotalDiscovered { get; set; }
        public int AlreadyIndexed { get; set; }
        public int ProcessedCount { get; set; }
        public int NewlyIndexedCount { get; set; }
        public int DeletedCount { get; set; }
        public int FailedCount { get; set; }
        public string CurrentFile { get; set; } = string.Empty;
        public TimeSpan Elapsed { get; set; }
        public bool IsCompleted { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
    }

    public class IndexStats
    {
        public int TotalFiles { get; set; }
        public long TotalSizeBytes { get; set; }
        public long DbSizeBytes { get; set; }
        public DateTime? LastIndexedUtc { get; set; }
    }

    /// <summary>
    /// SQLite FTS5 (trigram) を用いた事前インデックス型 全文検索サービス。
    /// 差分更新、途中中断レジューム、Small-File First、日本語2文字LIKEフォールバック、
    /// 削除亡霊クリーンアップ、SearchQuery完全貫通に対応。
    /// </summary>
    public class ContentIndexService
    {
        public const int CurrentExtractorVersion = 1;

        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly object _lock = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _activeScanRoots = new(StringComparer.OrdinalIgnoreCase);

        public string DbPath => _dbPath;

        /// <summary>
        /// フル走査完了時に発火するイベント（Watcherの保留キューflush等に使用）
        /// </summary>
        public event Action<string>? ScanCompleted;

        /// <summary>
        /// 指定パスがインデックスDB自身（.db, .db-wal, .db-shm, .db-journal）であるかを判定。
        /// customDbPath が走査ルート配下にある場合でも自己食い（自己インデックス）を完全に防ぐ。
        /// </summary>
        public bool IsDatabaseFile(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(_dbPath)) return false;
            try
            {
                string norm = Path.GetFullPath(fullPath);
                string dbNorm = Path.GetFullPath(_dbPath);
                return string.Equals(norm, dbNorm, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(norm, dbNorm + "-wal", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(norm, dbNorm + "-shm", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(norm, dbNorm + "-journal", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 指定したパスまたはその上位/下位フォルダーが現在 Full Scan 中か判定する。
        /// </summary>
        public bool IsScanningRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || _activeScanRoots.IsEmpty) return false;
            try
            {
                string norm = Path.GetFullPath(path).TrimEnd('\\', '/');
                foreach (var root in _activeScanRoots.Keys)
                {
                    if (norm.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                        norm.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase) ||
                        root.StartsWith(norm + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// 指定ルートを Dirty 状態（再同期必須）にする。
        /// Watcherのバッファオーバーフロー等のエラー発生時に次回検索時の15分クールダウンをバイパスして強制同期させる。
        /// </summary>
        public void MarkRootDirty(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string norm = Path.GetFullPath(path).TrimEnd('\\', '/');
                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "UPDATE IndexedRoots SET LastCompletedUtcTicks = 0 WHERE RootPath = @root;";
                    cmd.Parameters.AddWithValue("@root", norm);
                    cmd.ExecuteNonQuery();
                }
            }
            catch { }
        }

        public ContentIndexService(string? customDbPath = null)
        {
            if (string.IsNullOrWhiteSpace(customDbPath))
            {
                // Sol指摘3: Roaming (%APPDATA%) ではなく LocalAppData (%LOCALAPPDATA%) を採用
                string localAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FolderMorpher");
                Directory.CreateDirectory(localAppData);
                _dbPath = Path.Combine(localAppData, "ContentIndex.db");
            }
            else
            {
                _dbPath = customDbPath;
                string? dir = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            }

            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = _dbPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared
            };
            _connectionString = builder.ToString();

            InitializeDatabase();
        }

        private void InitializeDatabase()
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    PRAGMA journal_mode = WAL;
                    PRAGMA synchronous = NORMAL;

                    CREATE TABLE IF NOT EXISTS IndexedFiles (
                        FileId INTEGER PRIMARY KEY AUTOINCREMENT,
                        FullPath TEXT UNIQUE NOT NULL,
                        Name TEXT NOT NULL DEFAULT '',
                        DirectoryPath TEXT NOT NULL,
                        SizeBytes INTEGER NOT NULL,
                        LastWriteTimeUtcTicks INTEGER NOT NULL,
                        CreationTimeUtcTicks INTEGER NOT NULL DEFAULT 0,
                        Status INTEGER NOT NULL, -- 0: Pending, 1: Indexed, 2: Failed
                        IndexedAtUtcTicks INTEGER,
                        ExtractorVersion INTEGER NOT NULL DEFAULT 1,
                        Generation INTEGER NOT NULL DEFAULT 0,
                        IsDirectory INTEGER NOT NULL DEFAULT 0
                    );
                    CREATE INDEX IF NOT EXISTS idx_files_path ON IndexedFiles(FullPath);
                    CREATE INDEX IF NOT EXISTS idx_files_dir ON IndexedFiles(DirectoryPath);
                    CREATE INDEX IF NOT EXISTS idx_files_name ON IndexedFiles(Name);
                    CREATE INDEX IF NOT EXISTS idx_files_gen ON IndexedFiles(Generation);
                    CREATE INDEX IF NOT EXISTS idx_files_is_dir ON IndexedFiles(IsDirectory);

                    CREATE TABLE IF NOT EXISTS IndexedRoots (
                        RootPath TEXT PRIMARY KEY COLLATE NOCASE,
                        Status TEXT NOT NULL, -- 'InProgress', 'Complete', 'Error'
                        CoverageComplete INTEGER NOT NULL DEFAULT 1,
                        LastCompletedUtcTicks INTEGER,
                        TotalFiles INTEGER NOT NULL DEFAULT 0,
                        ExtractorVersion INTEGER NOT NULL DEFAULT 1,
                        CurrentGeneration INTEGER NOT NULL DEFAULT 0
                    );

                    CREATE VIRTUAL TABLE IF NOT EXISTS MetadataFts USING fts5(
                        Name,
                        tokenize = 'trigram'
                    );
                ";
                cmd.ExecuteNonQuery();

                // マイグレーション: ExtractorVersion / Name / Generation / CreationTimeUtcTicks / IsDirectory カラム追加 (既存DB対応)
                try
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE IndexedFiles ADD COLUMN ExtractorVersion INTEGER NOT NULL DEFAULT 1;";
                    alterCmd.ExecuteNonQuery();
                }
                catch { }

                try
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE IndexedFiles ADD COLUMN Name TEXT NOT NULL DEFAULT '';";
                    alterCmd.ExecuteNonQuery();
                }
                catch { }

                try
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE IndexedFiles ADD COLUMN Generation INTEGER NOT NULL DEFAULT 0;";
                    alterCmd.ExecuteNonQuery();
                }
                catch { }

                try
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE IndexedFiles ADD COLUMN CreationTimeUtcTicks INTEGER NOT NULL DEFAULT 0;";
                    alterCmd.ExecuteNonQuery();
                }
                catch { }

                try
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE IndexedFiles ADD COLUMN IsDirectory INTEGER NOT NULL DEFAULT 0;";
                    alterCmd.ExecuteNonQuery();
                }
                catch { }

                try
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE IndexedRoots ADD COLUMN CurrentGeneration INTEGER NOT NULL DEFAULT 0;";
                    alterCmd.ExecuteNonQuery();
                }
                catch { }

                // マイグレーション: ContentFts の rowid=FileId 化（旧 FileId UNINDEXED スキーマからの自動昇格）
                bool needCreateContentFts = true;
                try
                {
                    using var checkCmd = conn.CreateCommand();
                    checkCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='ContentFts';";
                    var sqlObj = checkCmd.ExecuteScalar();
                    if (sqlObj != null)
                    {
                        string sqlStr = sqlObj.ToString() ?? string.Empty;
                        if (sqlStr.Contains("FileId", StringComparison.OrdinalIgnoreCase))
                        {
                            // 旧スキーマ: FileId UNINDEXED 列が存在する ➔ 一時テーブル経由で rowid 形式へ移行
                            using var migCmd = conn.CreateCommand();
                            migCmd.CommandText = @"
                                ALTER TABLE ContentFts RENAME TO ContentFts_Old;
                                CREATE VIRTUAL TABLE ContentFts USING fts5(
                                    Body,
                                    tokenize = 'trigram'
                                );
                                INSERT INTO ContentFts (rowid, Body)
                                SELECT CAST(FileId AS INTEGER), Body FROM ContentFts_Old WHERE FileId IS NOT NULL;
                                DROP TABLE ContentFts_Old;
                            ";
                            migCmd.ExecuteNonQuery();
                            needCreateContentFts = false;
                        }
                        else
                        {
                            needCreateContentFts = false;
                        }
                    }
                }
                catch { }

                if (needCreateContentFts)
                {
                    try
                    {
                        using var createCmd = conn.CreateCommand();
                        createCmd.CommandText = @"
                            CREATE VIRTUAL TABLE IF NOT EXISTS ContentFts USING fts5(
                                Body,
                                tokenize = 'trigram'
                            );";
                        createCmd.ExecuteNonQuery();
                    }
                    catch { }
                }

                // マイグレーション: Name が空の既存レコードを一括補完
                try
                {
                    var emptyNames = new List<(long FileId, string FullPath)>();
                    using (var readCmd = conn.CreateCommand())
                    {
                        readCmd.CommandText = "SELECT FileId, FullPath FROM IndexedFiles WHERE Name = '' OR Name IS NULL;";
                        using var reader = readCmd.ExecuteReader();
                        while (reader.Read())
                        {
                            emptyNames.Add((reader.GetInt64(0), reader.GetString(1)));
                        }
                    }

                    if (emptyNames.Count > 0)
                    {
                        using var trans = conn.BeginTransaction();
                        foreach (var (fId, path) in emptyNames)
                        {
                            string fileName = Path.GetFileName(path);
                            using var updCmd = conn.CreateCommand();
                            updCmd.Transaction = trans;
                            updCmd.CommandText = "UPDATE IndexedFiles SET Name = @name WHERE FileId = @fileId;";
                            updCmd.Parameters.AddWithValue("@name", fileName);
                            updCmd.Parameters.AddWithValue("@fileId", fId);
                            updCmd.ExecuteNonQuery();
                        }
                        trans.Commit();
                    }
                }
                catch { }

                // マイグレーション: MetadataFts が空で IndexedFiles にレコードがある場合、初回一括同期
                try
                {
                    long metaCount = 0;
                    using (var countCmd = conn.CreateCommand())
                    {
                        countCmd.CommandText = "SELECT count(*) FROM MetadataFts;";
                        var res = countCmd.ExecuteScalar();
                        if (res != null) metaCount = Convert.ToInt64(res);
                    }

                    if (metaCount == 0)
                    {
                        using var fillCmd = conn.CreateCommand();
                        fillCmd.CommandText = @"
                            INSERT INTO MetadataFts (rowid, Name)
                            SELECT FileId, Name FROM IndexedFiles WHERE Name != '';";
                        fillCmd.ExecuteNonQuery();
                    }
                }
                catch { }
            }
        }

        /// <summary>
        /// 指定パスまたはその上位フォルダーが完全にインデックス化されているか判定。
        /// 一致する最深（最長パス）の Root を正本として検証し、
        /// Status == 'Complete' かつ ExtractorVersion 一致 かつ CoverageComplete == 1 の場合のみ true を返す。
        /// 親が Complete でも子が Error の場合の中断漏れ、および未走査フォルダーの誤判定を完全に防止。
        /// </summary>
        public bool HasCompleteIndexForPath(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return false;
            string norm = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT RootPath, Status, CoverageComplete, ExtractorVersion 
                    FROM IndexedRoots";

                string? bestRootPath = null;
                string? bestStatus = null;
                int bestCoverage = 0;
                int bestExtVer = 0;

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string root = reader.GetString(0).TrimEnd('\\', '/');
                    if (norm.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                        norm.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        // 最深（最もパスが長い＝最も近い）Root を選択
                        if (bestRootPath == null || root.Length > bestRootPath.Length)
                        {
                            bestRootPath = root;
                            bestStatus = reader.GetString(1);
                            bestCoverage = reader.GetInt32(2);
                            bestExtVer = reader.GetInt32(3);
                        }
                    }
                }

                if (bestRootPath == null) return false;

                return string.Equals(bestStatus, "Complete", StringComparison.OrdinalIgnoreCase) &&
                       bestCoverage == 1 &&
                       bestExtVer == CurrentExtractorVersion;
            }
        }

        /// <summary>
        /// 指定パスのファイルが完全なインデックスに存在するか判定（後方互換用：HasCompleteIndexForPath に委譲）
        /// </summary>
        public bool HasIndexForPath(string folderPath)
        {
            return HasCompleteIndexForPath(folderPath);
        }

        /// <summary>
        /// 指定パスに最も深く合致する IndexedRoot の CurrentGeneration を取得する。
        /// 合致する Root がない場合は 1 を返す。
        /// </summary>
        public int GetGenerationForPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return 1;
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                return GetGenerationForPathInternal(conn, path);
            }
        }

        private static int GetGenerationForPathInternal(SqliteConnection conn, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return 1;
            string norm = Path.GetFullPath(path).TrimEnd('\\', '/');

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT RootPath, CurrentGeneration FROM IndexedRoots;";

            string? bestRootPath = null;
            int bestGen = 1;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string root = reader.GetString(0).TrimEnd('\\', '/');
                if (norm.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                    norm.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    if (bestRootPath == null || root.Length > bestRootPath.Length)
                    {
                        bestRootPath = root;
                        bestGen = reader.IsDBNull(1) ? 1 : reader.GetInt32(1);
                    }
                }
            }

            return Math.Max(1, bestGen);
        }

        /// <summary>
        /// 指定パスに対応する最深Rootの最終インデックス完了日時 (UTC) を取得。
        /// インデックスが存在しない、または未完了の場合は null を返す。
        /// </summary>
        public DateTime? GetLastIndexCompletedUtc(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return null;
            string norm = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT RootPath, Status, CoverageComplete, ExtractorVersion, LastCompletedUtcTicks 
                    FROM IndexedRoots";

                string? bestRootPath = null;
                long? bestTicks = null;

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string root = reader.GetString(0).TrimEnd('\\', '/');
                    if (norm.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                        norm.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase))
                    {
                        if (bestRootPath == null || root.Length > bestRootPath.Length)
                        {
                            bestRootPath = root;
                            string status = reader.GetString(1);
                            int coverage = reader.GetInt32(2);
                            int extVer = reader.GetInt32(3);
                            if (string.Equals(status, "Complete", StringComparison.OrdinalIgnoreCase) &&
                                coverage == 1 && extVer == CurrentExtractorVersion && !reader.IsDBNull(4))
                            {
                                bestTicks = reader.GetInt64(4);
                            }
                            else
                            {
                                bestTicks = null;
                            }
                        }
                    }
                }

                if (bestTicks.HasValue && bestTicks.Value > 0)
                {
                    return new DateTime(bestTicks.Value, DateTimeKind.Utc);
                }
                return null;
            }
        }

        /// <summary>
        /// 指定パスのインデックスがクールダウン期間を経過して差分同期が必要か判定。
        /// インデックス未構築・未完了の場合は常に true を返す。
        /// </summary>
        public bool NeedsBackgroundSync(string folderPath, TimeSpan cooldown)
        {
            var lastUtc = GetLastIndexCompletedUtc(folderPath);
            if (!lastUtc.HasValue) return true;
            return (DateTime.UtcNow - lastUtc.Value) > cooldown;
        }

        /// <summary>
        /// アクセス権喪失や削除により参照不能となったファイル群を、SQLite FTS5 インデックスから安全に抹消（パージ）する。
        /// </summary>
        public async Task<int> PurgeFilesAsync(IEnumerable<string> fullPaths, CancellationToken ct = default)
        {
            var pathList = fullPaths?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            if (pathList == null || pathList.Count == 0) return 0;

            return await Task.Run(() =>
            {
                int purgedCount = 0;
                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();
                    using var trans = conn.BeginTransaction();

                    foreach (var path in pathList)
                    {
                        ct.ThrowIfCancellationRequested();

                        var fileIdsToDelete = new List<long>();
                        string dirPrefix = path.TrimEnd('\\', '/') + "\\";
                        string escPrefix = dirPrefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

                        using (var cmdSelect = conn.CreateCommand())
                        {
                            cmdSelect.Transaction = trans;
                            cmdSelect.CommandText = "SELECT FileId FROM IndexedFiles WHERE FullPath = @path OR FullPath LIKE @prefix ESCAPE '\\'";
                            cmdSelect.Parameters.AddWithValue("@path", path);
                            cmdSelect.Parameters.AddWithValue("@prefix", escPrefix);
                            using var reader = cmdSelect.ExecuteReader();
                            while (reader.Read())
                            {
                                fileIdsToDelete.Add(reader.GetInt64(0));
                            }
                        }

                        foreach (var fileId in fileIdsToDelete)
                        {
                            using (var cmdDelMeta = conn.CreateCommand())
                            {
                                cmdDelMeta.Transaction = trans;
                                cmdDelMeta.CommandText = "DELETE FROM MetadataFts WHERE rowid = @fileId";
                                cmdDelMeta.Parameters.AddWithValue("@fileId", fileId);
                                cmdDelMeta.ExecuteNonQuery();
                            }

                            using (var cmdDelFts = conn.CreateCommand())
                            {
                                cmdDelFts.Transaction = trans;
                                cmdDelFts.CommandText = "DELETE FROM ContentFts WHERE rowid = @fileId";
                                cmdDelFts.Parameters.AddWithValue("@fileId", fileId);
                                cmdDelFts.ExecuteNonQuery();
                            }

                            using (var cmdDelFile = conn.CreateCommand())
                            {
                                cmdDelFile.Transaction = trans;
                                cmdDelFile.CommandText = "DELETE FROM IndexedFiles WHERE FileId = @fileId";
                                cmdDelFile.Parameters.AddWithValue("@fileId", fileId);
                                cmdDelFile.ExecuteNonQuery();
                            }

                            purgedCount++;
                        }
                    }

                    trans.Commit();
                }
                return purgedCount;
            }, ct);
        }


        /// <summary>
        /// 指定したフォルダーのファイルを走査し、差分更新およびレジューム（前回の続き）でインデックスを作成する。
        /// </summary>
        public async Task<IndexProgressReport> IndexFolderAsync(
            string folderPath,
            IProgress<IndexProgressReport>? progress = null,
            CancellationToken ct = default)
        {
            return await Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                string normTarget = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

                // ルートの CurrentGeneration を取得・インクリメントし、InProgress に登録
                int currentGen = 1;
                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();
                    using var cmdGet = conn.CreateCommand();
                    cmdGet.CommandText = "SELECT CurrentGeneration FROM IndexedRoots WHERE RootPath = @root;";
                    cmdGet.Parameters.AddWithValue("@root", normTarget);
                    var genObj = cmdGet.ExecuteScalar();
                    if (genObj != null && genObj != DBNull.Value)
                    {
                        currentGen = Convert.ToInt32(genObj) + 1;
                    }

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO IndexedRoots (RootPath, Status, CoverageComplete, LastCompletedUtcTicks, TotalFiles, ExtractorVersion, CurrentGeneration)
                        VALUES (@root, 'InProgress', 0, @ticks, 0, @extVer, @gen);";
                    cmd.Parameters.AddWithValue("@root", normTarget);
                    cmd.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
                    cmd.Parameters.AddWithValue("@extVer", CurrentExtractorVersion);
                    cmd.Parameters.AddWithValue("@gen", currentGen);
                    cmd.ExecuteNonQuery();
                }

                // アクティブスキャン中ルートとして登録（WatcherとのGeneration race防止用）
                _activeScanRoots[normTarget] = currentGen;

                progress?.Report(new IndexProgressReport
                {
                    StatusMessage = "メタデータを走査中...",
                    CurrentFile = normTarget,
                    Elapsed = sw.Elapsed
                });

                var coverage = new ScanCoverage();

                try
                {
                    // 1. ファイル一覧を一括列挙 (MFT Fast Track または 並列2 SafeFileEnumerator)
                    List<ScannedFileEntry> scannedEntries;
                    if (AstraSize.Services.Mft.MftScanService.CanUseMft(normTarget))
                    {
                        try
                        {
                            scannedEntries = await EnumerateEntriesViaMftAsync(normTarget, ct);
                        }
                        catch
                        {
                            // MFT 直接走査に失敗した場合は安全に通常走査へフォールバック
                            scannedEntries = await SafeFileEnumerator.EnumerateFileEntriesParallelAsync(
                                normTarget,
                                "*.*",
                                coverage: coverage,
                                onProgress: null,
                                ct: ct,
                                includeDirectories: true);
                        }
                    }
                    else
                    {
                        scannedEntries = await SafeFileEnumerator.EnumerateFileEntriesParallelAsync(
                            normTarget,
                            "*.*",
                            coverage: coverage,
                            onProgress: null,
                            ct: ct,
                            includeDirectories: true);
                    }

                    // 2. 自前DBファイル（TestIndex.db, -wal, -shm 等）を完全除外（customDbPath時の自己食い防止）
                    var validEntries = scannedEntries
                        .Where(e => !IsDatabaseFile(e.FullPath))
                        .ToList();

                    var directories = validEntries
                        .Where(e => e.Attributes.HasFlag(FileAttributes.Directory))
                        .ToList();

                    var allFiles = validEntries
                        .Where(e => !e.Attributes.HasFlag(FileAttributes.Directory))
                        .ToList();

                    var supportedExts = ContentExtractionService.SupportedExtensions;
                    const long MaxIndexFileSize = 50L * 1024 * 1024; // 50MB上限

                    var contentTargets = allFiles
                        .Where(e => e.Length <= MaxIndexFileSize && supportedExts.Contains(Path.GetExtension(e.Name)))
                        .ToList();
                    var metadataOnlyFiles = allFiles
                        .Where(e => e.Length > MaxIndexFileSize || !supportedExts.Contains(Path.GetExtension(e.Name)))
                        .ToList();

                    // 3. 既存DBのメタデータ状態をロード（パス境界を厳格化して近接類似フォルダーの巻き込みを防止）
                    var existingMap = new Dictionary<string, (long FileId, long SizeBytes, long LastWriteTicks, int Status, int ExtractorVer, int IsDir)>(StringComparer.OrdinalIgnoreCase);
                    lock (_lock)
                    {
                        using var conn = new SqliteConnection(_connectionString);
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                            SELECT FileId, FullPath, SizeBytes, LastWriteTimeUtcTicks, Status, ExtractorVersion, IsDirectory 
                            FROM IndexedFiles 
                            WHERE FullPath = @exact OR FullPath LIKE @prefix ESCAPE '\'";

                        string dirPrefix = normTarget + "\\";
                        string escPrefix = dirPrefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                        cmd.Parameters.AddWithValue("@exact", normTarget);
                        cmd.Parameters.AddWithValue("@prefix", escPrefix);

                        using var reader = cmd.ExecuteReader();
                        while (reader.Read())
                        {
                            long fId = reader.GetInt64(0);
                            string path = reader.GetString(1);
                            long size = reader.GetInt64(2);
                            long ticks = reader.GetInt64(3);
                            int status = reader.GetInt32(4);
                            int extVer = reader.IsDBNull(5) ? 1 : reader.GetInt32(5);
                            int isDir = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
                            existingMap[path] = (fId, size, ticks, status, extVer, isDir);
                        }
                    }

                    int alreadyIndexed = 0;
                    int newlyIndexed = 0;
                    int failedCount = 0;
                    var skippedFileIds = new List<long>();

                    // 4-0. フォルダーのメタデータを IndexedFiles ＆ MetadataFts に登録（フォルダ検索対応）
                    var dirsToInsert = new List<ScannedFileEntry>();
                    foreach (var dirEntry in directories)
                    {
                        long currentTicks = dirEntry.LastWriteTime.ToUniversalTime().Ticks;
                        if (existingMap.TryGetValue(dirEntry.FullPath, out var meta))
                        {
                            if (meta.IsDir == 1 && meta.LastWriteTicks == currentTicks)
                            {
                                alreadyIndexed++;
                                skippedFileIds.Add(meta.FileId);
                                continue;
                            }
                        }
                        dirsToInsert.Add(dirEntry);
                    }

                    if (dirsToInsert.Count > 0)
                    {
                        lock (_lock)
                        {
                            using var conn = new SqliteConnection(_connectionString);
                            conn.Open();
                            using var trans = conn.BeginTransaction();
                            long nowTicks = DateTime.UtcNow.Ticks;
                            foreach (var entry in dirsToInsert)
                            {
                                long currentTicks = entry.LastWriteTime.ToUniversalTime().Ticks;
                                long creationTicks = entry.CreationTime.ToUniversalTime().Ticks;
                                long fileId;
                                if (existingMap.TryGetValue(entry.FullPath, out var meta))
                                {
                                    fileId = meta.FileId;
                                    using var cmdUpd = conn.CreateCommand();
                                    cmdUpd.Transaction = trans;
                                    cmdUpd.CommandText = @"
                                        UPDATE IndexedFiles 
                                        SET Name = @name, SizeBytes = 0, LastWriteTimeUtcTicks = @ticks, CreationTimeUtcTicks = @cTicks, Status = 0, 
                                            IndexedAtUtcTicks = @now, ExtractorVersion = @ver, Generation = @gen, IsDirectory = 1
                                        WHERE FileId = @fileId;";
                                    cmdUpd.Parameters.AddWithValue("@name", entry.Name);
                                    cmdUpd.Parameters.AddWithValue("@ticks", currentTicks);
                                    cmdUpd.Parameters.AddWithValue("@cTicks", creationTicks);
                                    cmdUpd.Parameters.AddWithValue("@now", nowTicks);
                                    cmdUpd.Parameters.AddWithValue("@ver", CurrentExtractorVersion);
                                    cmdUpd.Parameters.AddWithValue("@gen", currentGen);
                                    cmdUpd.Parameters.AddWithValue("@fileId", fileId);
                                    cmdUpd.ExecuteNonQuery();

                                    using var cmdDelMeta = conn.CreateCommand();
                                    cmdDelMeta.Transaction = trans;
                                    cmdDelMeta.CommandText = "DELETE FROM MetadataFts WHERE rowid = @fileId;";
                                    cmdDelMeta.Parameters.AddWithValue("@fileId", fileId);
                                    cmdDelMeta.ExecuteNonQuery();
                                }
                                else
                                {
                                    using var cmdIns = conn.CreateCommand();
                                    cmdIns.Transaction = trans;
                                    cmdIns.CommandText = @"
                                        INSERT INTO IndexedFiles (FullPath, Name, DirectoryPath, SizeBytes, LastWriteTimeUtcTicks, CreationTimeUtcTicks, Status, IndexedAtUtcTicks, ExtractorVersion, Generation, IsDirectory)
                                        VALUES (@path, @name, @dir, 0, @ticks, @cTicks, 0, @now, @ver, @gen, 1);
                                        SELECT last_insert_rowid();";
                                    cmdIns.Parameters.AddWithValue("@path", entry.FullPath);
                                    cmdIns.Parameters.AddWithValue("@name", entry.Name);
                                    cmdIns.Parameters.AddWithValue("@dir", entry.DirectoryPath);
                                    cmdIns.Parameters.AddWithValue("@ticks", currentTicks);
                                    cmdIns.Parameters.AddWithValue("@cTicks", creationTicks);
                                    cmdIns.Parameters.AddWithValue("@now", nowTicks);
                                    cmdIns.Parameters.AddWithValue("@ver", CurrentExtractorVersion);
                                    cmdIns.Parameters.AddWithValue("@gen", currentGen);
                                    fileId = (long)cmdIns.ExecuteScalar()!;
                                }

                                using var cmdInsMeta = conn.CreateCommand();
                                cmdInsMeta.Transaction = trans;
                                cmdInsMeta.CommandText = "INSERT INTO MetadataFts (rowid, Name) VALUES (@fileId, @name);";
                                cmdInsMeta.Parameters.AddWithValue("@fileId", fileId);
                                cmdInsMeta.Parameters.AddWithValue("@name", entry.Name);
                                cmdInsMeta.ExecuteNonQuery();
                            }
                            trans.Commit();
                        }
                        newlyIndexed += dirsToInsert.Count;
                    }

                    // 4-A. 非本文ファイル（zip, exe, 動画, 画像など）のメタデータを IndexedFiles ＆ MetadataFts に登録
                    var metaToInsert = new List<ScannedFileEntry>();
                    foreach (var entry in metadataOnlyFiles)
                    {
                        long currentTicks = entry.LastWriteTime.ToUniversalTime().Ticks;
                        if (existingMap.TryGetValue(entry.FullPath, out var meta))
                        {
                            if (meta.IsDir == 0 && meta.SizeBytes == entry.Length && meta.LastWriteTicks == currentTicks)
                            {
                                alreadyIndexed++;
                                skippedFileIds.Add(meta.FileId);
                                continue;
                            }
                        }
                        metaToInsert.Add(entry);
                    }

                    if (metaToInsert.Count > 0)
                    {
                        lock (_lock)
                        {
                            using var conn = new SqliteConnection(_connectionString);
                            conn.Open();
                            using var trans = conn.BeginTransaction();
                            long nowTicks = DateTime.UtcNow.Ticks;
                            foreach (var entry in metaToInsert)
                            {
                                long currentTicks = entry.LastWriteTime.ToUniversalTime().Ticks;
                                long creationTicks = entry.CreationTime.ToUniversalTime().Ticks;
                                long fileId;
                                if (existingMap.TryGetValue(entry.FullPath, out var meta))
                                {
                                    fileId = meta.FileId;
                                    using var cmdUpd = conn.CreateCommand();
                                    cmdUpd.Transaction = trans;
                                    cmdUpd.CommandText = @"
                                        UPDATE IndexedFiles 
                                        SET Name = @name, SizeBytes = @size, LastWriteTimeUtcTicks = @ticks, CreationTimeUtcTicks = @cTicks, Status = 0, 
                                            IndexedAtUtcTicks = @now, ExtractorVersion = @ver, Generation = @gen, IsDirectory = 0
                                        WHERE FileId = @fileId";
                                    cmdUpd.Parameters.AddWithValue("@name", entry.Name);
                                    cmdUpd.Parameters.AddWithValue("@size", entry.Length);
                                    cmdUpd.Parameters.AddWithValue("@ticks", currentTicks);
                                    cmdUpd.Parameters.AddWithValue("@cTicks", creationTicks);
                                    cmdUpd.Parameters.AddWithValue("@now", nowTicks);
                                    cmdUpd.Parameters.AddWithValue("@ver", CurrentExtractorVersion);
                                    cmdUpd.Parameters.AddWithValue("@gen", currentGen);
                                    cmdUpd.Parameters.AddWithValue("@fileId", fileId);
                                    cmdUpd.ExecuteNonQuery();
                                }
                                else
                                {
                                    using var cmdIns = conn.CreateCommand();
                                    cmdIns.Transaction = trans;
                                    cmdIns.CommandText = @"
                                        INSERT INTO IndexedFiles (FullPath, Name, DirectoryPath, SizeBytes, LastWriteTimeUtcTicks, CreationTimeUtcTicks, Status, IndexedAtUtcTicks, ExtractorVersion, Generation, IsDirectory)
                                        VALUES (@path, @name, @dir, @size, @ticks, @cTicks, 0, @now, @ver, @gen, 0);
                                        SELECT last_insert_rowid();";
                                    cmdIns.Parameters.AddWithValue("@path", entry.FullPath);
                                    cmdIns.Parameters.AddWithValue("@name", entry.Name);
                                    cmdIns.Parameters.AddWithValue("@dir", entry.DirectoryPath);
                                    cmdIns.Parameters.AddWithValue("@size", entry.Length);
                                    cmdIns.Parameters.AddWithValue("@ticks", currentTicks);
                                    cmdIns.Parameters.AddWithValue("@cTicks", creationTicks);
                                    cmdIns.Parameters.AddWithValue("@now", nowTicks);
                                    cmdIns.Parameters.AddWithValue("@ver", CurrentExtractorVersion);
                                    cmdIns.Parameters.AddWithValue("@gen", currentGen);
                                    fileId = (long)cmdIns.ExecuteScalar()!;
                                }

                                // MetadataFts を更新
                                using (var cmdDelMeta = conn.CreateCommand())
                                {
                                    cmdDelMeta.Transaction = trans;
                                    cmdDelMeta.CommandText = "DELETE FROM MetadataFts WHERE rowid = @fileId;";
                                    cmdDelMeta.Parameters.AddWithValue("@fileId", fileId);
                                    cmdDelMeta.ExecuteNonQuery();
                                }
                                using (var cmdInsMeta = conn.CreateCommand())
                                {
                                    cmdInsMeta.Transaction = trans;
                                    cmdInsMeta.CommandText = "INSERT INTO MetadataFts (rowid, Name) VALUES (@fileId, @name);";
                                    cmdInsMeta.Parameters.AddWithValue("@fileId", fileId);
                                    cmdInsMeta.Parameters.AddWithValue("@name", entry.Name);
                                    cmdInsMeta.ExecuteNonQuery();
                                }
                            }
                            trans.Commit();
                        }
                        newlyIndexed += metaToInsert.Count;
                    }

                    // 4-B. 本文対象ファイル（Office/PDF/Text）の差分判定
                    var toProcess = new List<ScannedFileEntry>();
                    foreach (var entry in contentTargets)
                    {
                        long currentTicks = entry.LastWriteTime.ToUniversalTime().Ticks;
                        if (existingMap.TryGetValue(entry.FullPath, out var meta))
                        {
                            // 既に Indexed (Status==1) かつ サイズ・更新日時・ExtractorVersion が一致していれば完全スキップ (0 I/O)
                            if (meta.Status == 1 && meta.SizeBytes == entry.Length && meta.LastWriteTicks == currentTicks && meta.ExtractorVer == CurrentExtractorVersion)
                            {
                                alreadyIndexed++;
                                skippedFileIds.Add(meta.FileId);
                                continue;
                            }
                        }
                        toProcess.Add(entry);
                    }

                    // ★ Sol提唱: 小さいファイル優先（Small-File First）でソート
                    toProcess = toProcess.OrderBy(e => e.Length).ToList();

                    int totalDiscovered = validEntries.Count;
                    int processedCount = alreadyIndexed + metaToInsert.Count + dirsToInsert.Count;

                    progress?.Report(new IndexProgressReport
                    {
                        TotalDiscovered = totalDiscovered,
                        AlreadyIndexed = alreadyIndexed,
                        ProcessedCount = processedCount,
                        NewlyIndexedCount = newlyIndexed,
                        DeletedCount = 0,
                        StatusMessage = $"インデックス更新開始 (全ファイル: {totalDiscovered:N0} 件 / 本文抽出対象: {toProcess.Count:N0} 件 / 変更なしスキップ: {alreadyIndexed:N0} 件)",
                        Elapsed = sw.Elapsed
                    });

                    // 4-C. スキップされたファイルの Generation を一括更新（バッチ 500件）
                    if (skippedFileIds.Count > 0)
                    {
                        lock (_lock)
                        {
                            using var conn = new SqliteConnection(_connectionString);
                            conn.Open();
                            using var trans = conn.BeginTransaction();
                            const int GenBatchSize = 500;
                            for (int b = 0; b < skippedFileIds.Count; b += GenBatchSize)
                            {
                                var chunk = skippedFileIds.Skip(b).Take(GenBatchSize).ToList();
                                using var cmdGen = conn.CreateCommand();
                                cmdGen.Transaction = trans;
                                cmdGen.CommandText = $"UPDATE IndexedFiles SET Generation = @gen WHERE FileId IN ({string.Join(",", chunk)});";
                                cmdGen.Parameters.AddWithValue("@gen", currentGen);
                                cmdGen.ExecuteNonQuery();
                            }
                            trans.Commit();
                        }
                    }

                    // 5. バッチ処理（50ファイルごとにトランザクションコミットしてディスクに永続化）
                    const int BatchSize = 50;
                    for (int i = 0; i < toProcess.Count; i += BatchSize)
                    {
                        ct.ThrowIfCancellationRequested();

                        var batch = toProcess.Skip(i).Take(BatchSize).ToList();

                        // 並列度2固定でテキスト抽出
                        var extractedResults = new ConcurrentBag<(ScannedFileEntry Entry, string? Text, bool Success)>();
                        var po = new ParallelOptions
                        {
                            MaxDegreeOfParallelism = 2, // サーバー保護のためデュアルワーカー固定
                            CancellationToken = ct
                        };

                        await Parallel.ForEachAsync(batch, po, async (entry, token) =>
                        {
                            token.ThrowIfCancellationRequested();
                            try
                            {
                                string? text = await ContentExtractionService.ExtractTextAsync(entry.FullPath, token);
                                extractedResults.Add((entry, text, text != null));
                            }
                            catch
                            {
                                extractedResults.Add((entry, null, false));
                            }
                        });

                        // SQLite へバッチコミット (ACID保証・クラッシュセーフ)
                        lock (_lock)
                        {
                            using var conn = new SqliteConnection(_connectionString);
                            conn.Open();
                            using var trans = conn.BeginTransaction();

                            foreach (var item in extractedResults)
                            {
                                var entry = item.Entry;
                                long currentTicks = entry.LastWriteTime.ToUniversalTime().Ticks;
                                long creationTicks = entry.CreationTime.ToUniversalTime().Ticks;
                                long nowTicks = DateTime.UtcNow.Ticks;

                                long fileId;
                                if (existingMap.TryGetValue(entry.FullPath, out var meta))
                                {
                                    fileId = meta.FileId;
                                    using var cmdUpd = conn.CreateCommand();
                                    cmdUpd.Transaction = trans;
                                    cmdUpd.CommandText = @"
                                        UPDATE IndexedFiles 
                                        SET Name = @name, SizeBytes = @size, LastWriteTimeUtcTicks = @ticks, CreationTimeUtcTicks = @cTicks, Status = @status, 
                                            IndexedAtUtcTicks = @now, ExtractorVersion = @ver, Generation = @gen, IsDirectory = 0
                                        WHERE FileId = @fileId";
                                    cmdUpd.Parameters.AddWithValue("@name", entry.Name);
                                    cmdUpd.Parameters.AddWithValue("@size", entry.Length);
                                    cmdUpd.Parameters.AddWithValue("@ticks", currentTicks);
                                    cmdUpd.Parameters.AddWithValue("@cTicks", creationTicks);
                                    cmdUpd.Parameters.AddWithValue("@status", item.Success ? 1 : 2);
                                    cmdUpd.Parameters.AddWithValue("@now", nowTicks);
                                    cmdUpd.Parameters.AddWithValue("@ver", CurrentExtractorVersion);
                                    cmdUpd.Parameters.AddWithValue("@gen", currentGen);
                                    cmdUpd.Parameters.AddWithValue("@fileId", fileId);
                                    cmdUpd.ExecuteNonQuery();

                                    // 既存の FTS エントリを削除
                                    using var cmdDelFts = conn.CreateCommand();
                                    cmdDelFts.Transaction = trans;
                                    cmdDelFts.CommandText = "DELETE FROM ContentFts WHERE rowid = @fileId";
                                    cmdDelFts.Parameters.AddWithValue("@fileId", fileId);
                                    cmdDelFts.ExecuteNonQuery();
                                }
                                else
                                {
                                    using var cmdIns = conn.CreateCommand();
                                    cmdIns.Transaction = trans;
                                    cmdIns.CommandText = @"
                                        INSERT INTO IndexedFiles (FullPath, Name, DirectoryPath, SizeBytes, LastWriteTimeUtcTicks, CreationTimeUtcTicks, Status, IndexedAtUtcTicks, ExtractorVersion, Generation, IsDirectory)
                                        VALUES (@path, @name, @dir, @size, @ticks, @cTicks, @status, @now, @ver, @gen, 0);
                                        SELECT last_insert_rowid();";
                                    cmdIns.Parameters.AddWithValue("@path", entry.FullPath);
                                    cmdIns.Parameters.AddWithValue("@name", entry.Name);
                                    cmdIns.Parameters.AddWithValue("@dir", entry.DirectoryPath);
                                    cmdIns.Parameters.AddWithValue("@size", entry.Length);
                                    cmdIns.Parameters.AddWithValue("@ticks", currentTicks);
                                    cmdIns.Parameters.AddWithValue("@cTicks", creationTicks);
                                    cmdIns.Parameters.AddWithValue("@status", item.Success ? 1 : 2);
                                    cmdIns.Parameters.AddWithValue("@now", nowTicks);
                                    cmdIns.Parameters.AddWithValue("@ver", CurrentExtractorVersion);
                                    cmdIns.Parameters.AddWithValue("@gen", currentGen);
                                    fileId = (long)cmdIns.ExecuteScalar()!;
                                }

                                // MetadataFts を更新
                                using (var cmdDelMeta = conn.CreateCommand())
                                {
                                    cmdDelMeta.Transaction = trans;
                                    cmdDelMeta.CommandText = "DELETE FROM MetadataFts WHERE rowid = @fileId;";
                                    cmdDelMeta.Parameters.AddWithValue("@fileId", fileId);
                                    cmdDelMeta.ExecuteNonQuery();
                                }
                                using (var cmdInsMeta = conn.CreateCommand())
                                {
                                    cmdInsMeta.Transaction = trans;
                                    cmdInsMeta.CommandText = "INSERT INTO MetadataFts (rowid, Name) VALUES (@fileId, @name);";
                                    cmdInsMeta.Parameters.AddWithValue("@fileId", fileId);
                                    cmdInsMeta.Parameters.AddWithValue("@name", entry.Name);
                                    cmdInsMeta.ExecuteNonQuery();
                                }

                                if (item.Success && !string.IsNullOrWhiteSpace(item.Text))
                                {
                                    using var cmdInsFts = conn.CreateCommand();
                                    cmdInsFts.Transaction = trans;
                                    cmdInsFts.CommandText = "INSERT INTO ContentFts (rowid, Body) VALUES (@fileId, @body)";
                                    cmdInsFts.Parameters.AddWithValue("@fileId", fileId);
                                    cmdInsFts.Parameters.AddWithValue("@body", item.Text);
                                    cmdInsFts.ExecuteNonQuery();
                                    newlyIndexed++;
                                }
                                else
                                {
                                    failedCount++;
                                }

                                processedCount++;
                            }

                            trans.Commit();
                        }

                        progress?.Report(new IndexProgressReport
                        {
                            TotalDiscovered = totalDiscovered,
                            AlreadyIndexed = alreadyIndexed,
                            ProcessedCount = processedCount,
                            NewlyIndexedCount = newlyIndexed,
                            DeletedCount = 0,
                            FailedCount = failedCount,
                            CurrentFile = batch.LastOrDefault()?.Name ?? string.Empty,
                            StatusMessage = $"インデックス中 ({processedCount:N0} / {totalDiscovered:N0})",
                            Elapsed = sw.Elapsed
                        });
                    }

                    // 6. 亡霊ファイルのクリーンアップ（ScanGeneration 世代管理: 走査中に見つからなかった旧世代ファイルを一括削除）
                    int deletedCount = 0;
                    if (coverage.AccessDeniedFolders == 0)
                    {
                        lock (_lock)
                        {
                            using var conn = new SqliteConnection(_connectionString);
                            conn.Open();
                            using var trans = conn.BeginTransaction();

                            string dirPrefix = normTarget + "\\";
                            string escPrefix = dirPrefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

                            var ghostFileIds = new List<long>();
                            using (var cmdFind = conn.CreateCommand())
                            {
                                cmdFind.Transaction = trans;
                                cmdFind.CommandText = @"
                                    SELECT FileId FROM IndexedFiles 
                                    WHERE (FullPath = @exact OR FullPath LIKE @prefix ESCAPE '\') 
                                      AND Generation < @currentGen";
                                cmdFind.Parameters.AddWithValue("@exact", normTarget);
                                cmdFind.Parameters.AddWithValue("@prefix", escPrefix);
                                cmdFind.Parameters.AddWithValue("@currentGen", currentGen);
                                using var reader = cmdFind.ExecuteReader();
                                while (reader.Read())
                                {
                                    ghostFileIds.Add(reader.GetInt64(0));
                                }
                            }

                            if (ghostFileIds.Count > 0)
                            {
                                const int DelBatch = 500;
                                for (int b = 0; b < ghostFileIds.Count; b += DelBatch)
                                {
                                    var chunk = ghostFileIds.Skip(b).Take(DelBatch).ToList();
                                    string inClause = string.Join(",", chunk);

                                    using var delMeta = conn.CreateCommand();
                                    delMeta.Transaction = trans;
                                    delMeta.CommandText = $"DELETE FROM MetadataFts WHERE rowid IN ({inClause});";
                                    delMeta.ExecuteNonQuery();

                                    using var delFts = conn.CreateCommand();
                                    delFts.Transaction = trans;
                                    delFts.CommandText = $"DELETE FROM ContentFts WHERE rowid IN ({inClause});";
                                    delFts.ExecuteNonQuery();

                                    using var delFiles = conn.CreateCommand();
                                    delFiles.Transaction = trans;
                                    delFiles.CommandText = $"DELETE FROM IndexedFiles WHERE FileId IN ({inClause});";
                                    delFiles.ExecuteNonQuery();
                                }
                                deletedCount = ghostFileIds.Count;
                            }

                            trans.Commit();
                        }
                    }

                    // 7. 完了時に IndexedRoots を Complete に更新（中断されていないことの証明 ＆ 世代永続化）
                    lock (_lock)
                    {
                        using var conn = new SqliteConnection(_connectionString);
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                            INSERT OR REPLACE INTO IndexedRoots (RootPath, Status, CoverageComplete, LastCompletedUtcTicks, TotalFiles, ExtractorVersion, CurrentGeneration)
                            VALUES (@root, 'Complete', @cov, @ticks, @total, @extVer, @gen);";
                        cmd.Parameters.AddWithValue("@root", normTarget);
                        cmd.Parameters.AddWithValue("@cov", coverage.AccessDeniedFolders == 0 ? 1 : 0);
                        cmd.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
                        cmd.Parameters.AddWithValue("@total", totalDiscovered);
                        cmd.Parameters.AddWithValue("@extVer", CurrentExtractorVersion);
                        cmd.Parameters.AddWithValue("@gen", currentGen);
                        cmd.ExecuteNonQuery();
                    }

                    sw.Stop();
                    var finalReport = new IndexProgressReport
                    {
                        TotalDiscovered = totalDiscovered,
                        AlreadyIndexed = alreadyIndexed,
                        ProcessedCount = processedCount,
                        NewlyIndexedCount = newlyIndexed,
                        DeletedCount = deletedCount,
                        FailedCount = failedCount,
                        StatusMessage = $"インデックス完了 (新規/更新: {newlyIndexed:N0} 件, スキップ: {alreadyIndexed:N0} 件, 削除整理: {deletedCount:N0} 件)",
                        Elapsed = sw.Elapsed,
                        IsCompleted = true
                    };
                    progress?.Report(finalReport);
                    return finalReport;
                }
                catch (Exception)
                {
                    // 途中でエラーまたは中断が発生した場合は Status = 'Error' にマーク
                    lock (_lock)
                    {
                        try
                        {
                            using var conn = new SqliteConnection(_connectionString);
                            conn.Open();
                            using var cmd = conn.CreateCommand();
                            cmd.CommandText = @"
                                INSERT OR REPLACE INTO IndexedRoots (RootPath, Status, CoverageComplete, LastCompletedUtcTicks, TotalFiles, ExtractorVersion)
                                VALUES (@root, 'Error', 0, @ticks, 0, @extVer);";
                            cmd.Parameters.AddWithValue("@root", normTarget);
                            cmd.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
                            cmd.Parameters.AddWithValue("@extVer", CurrentExtractorVersion);
                            cmd.ExecuteNonQuery();
                        }
                        catch { }
                    }
                    throw;
                }
                finally
                {
                    _activeScanRoots.TryRemove(normTarget, out _);
                    try { ScanCompleted?.Invoke(normTarget); } catch { }
                }
            }, ct);
        }

        /// <summary>
        /// SQLite FTS5 (trigram) を使用したミリ秒全文検索（SearchQuery構文完全貫通・日本語2文字LIKE対応・複数語AND）
        /// </summary>
        public async Task<List<SearchResultItem>> SearchIndexedAsync(
            SearchQuery query,
            string? scopeFolder = null,
            CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var results = new List<SearchResultItem>();

                // 検索キーワードの収集
                var keywords = new List<string>(query.Keywords);
                if (!string.IsNullOrEmpty(query.ContentKeyword) && !keywords.Contains(query.ContentKeyword, StringComparer.OrdinalIgnoreCase))
                {
                    keywords.Add(query.ContentKeyword);
                }

                // キーワードも属性条件もない場合は空
                if (keywords.Count == 0 && query.Extensions.Count == 0 && !query.MinSizeBytes.HasValue && !query.MaxSizeBytes.HasValue && !query.DormantYears.HasValue && !query.DormantDays.HasValue && !query.MinPathLength.HasValue && query.PathContains.Count == 0)
                {
                    return results;
                }

                bool isExplicitContentSearch = query.SearchContentMode || !string.IsNullOrEmpty(query.ContentKeyword);

                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();

                    using var cmd = conn.CreateCommand();

                    // === 1. 共通属性条件 (Extensions, Min/MaxSize, Dormant, PathLen, PathContains, ScopeFolder) ===
                    var commonWhereClauses = new List<string>();

                    if (query.Extensions.Count > 0)
                    {
                        var extConditions = new List<string>();
                        int extIdx = 0;
                        foreach (var rawExt in query.Extensions)
                        {
                            string pName = $"@ext_{extIdx}";
                            extConditions.Add($"f.FullPath LIKE {pName} ESCAPE '\\'");
                            string ext = rawExt.StartsWith(".") ? rawExt : "." + rawExt;
                            cmd.Parameters.AddWithValue(pName, "%" + ext.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_"));
                            extIdx++;
                        }
                        commonWhereClauses.Add("(" + string.Join(" OR ", extConditions) + ")");
                    }

                    if (query.MinSizeBytes.HasValue)
                    {
                        commonWhereClauses.Add("f.SizeBytes >= @minSize");
                        cmd.Parameters.AddWithValue("@minSize", query.MinSizeBytes.Value);
                    }
                    if (query.MaxSizeBytes.HasValue)
                    {
                        commonWhereClauses.Add("f.SizeBytes <= @maxSize");
                        cmd.Parameters.AddWithValue("@maxSize", query.MaxSizeBytes.Value);
                    }

                    if (query.DormantYears.HasValue)
                    {
                        long maxTicks = DateTime.UtcNow.AddYears(-query.DormantYears.Value).Ticks;
                        commonWhereClauses.Add("f.LastWriteTimeUtcTicks <= @dormantTicks");
                        cmd.Parameters.AddWithValue("@dormantTicks", maxTicks);
                    }
                    else if (query.DormantDays.HasValue)
                    {
                        long maxTicks = DateTime.UtcNow.AddDays(-query.DormantDays.Value).Ticks;
                        commonWhereClauses.Add("f.LastWriteTimeUtcTicks <= @dormantTicks");
                        cmd.Parameters.AddWithValue("@dormantTicks", maxTicks);
                    }

                    if (query.MinPathLength.HasValue)
                    {
                        commonWhereClauses.Add("LENGTH(f.FullPath) >= @minPathLen");
                        cmd.Parameters.AddWithValue("@minPathLen", query.MinPathLength.Value);
                    }

                    for (int i = 0; i < query.PathContains.Count; i++)
                    {
                        string pName = $"@path_{i}";
                        commonWhereClauses.Add($"f.FullPath LIKE {pName} ESCAPE '\\'");
                        string escP = "%" + query.PathContains[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                        cmd.Parameters.AddWithValue(pName, escP);
                    }

                    if (!string.IsNullOrWhiteSpace(scopeFolder))
                    {
                        string normScope = Path.GetFullPath(scopeFolder).TrimEnd('\\', '/');
                        string dirScope = normScope + "\\";
                        string escScope = dirScope.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                        commonWhereClauses.Add("(f.FullPath = @scopeExact OR f.FullPath LIKE @scopeDir ESCAPE '\\')");
                        cmd.Parameters.AddWithValue("@scopeExact", normScope);
                        cmd.Parameters.AddWithValue("@scopeDir", escScope);
                    }

                    if (!query.IncludeFolders)
                    {
                        commonWhereClauses.Add("f.IsDirectory = 0");
                    }

                    string commonWhereSql = commonWhereClauses.Count > 0 ? " AND " + string.Join(" AND ", commonWhereClauses) : "";

                    string sql;
                    bool hasTrigramMatch = false;
                    var shortWords = new List<string>();

                    if (keywords.Count == 0)
                    {
                        // キーワードなし（属性検索のみ）: IndexedFiles 単体検索
                        string whereSql = commonWhereClauses.Count > 0 ? string.Join(" AND ", commonWhereClauses) : "1=1";
                        sql = $@"
                            SELECT f.FullPath, f.DirectoryPath, f.SizeBytes, f.LastWriteTimeUtcTicks, '' AS Snippet, f.CreationTimeUtcTicks, f.IsDirectory
                            FROM IndexedFiles f
                            WHERE {whereSql}
                            LIMIT 500";
                    }
                    else
                    {
                        // キーワードあり: 本文 (ContentFts) と ファイル名 (MetadataFts) のハイブリッド検索
                        var trigramWords = new List<string>();
                        foreach (var kw in keywords)
                        {
                            if (string.IsNullOrWhiteSpace(kw)) continue;
                            string trimmed = kw.Trim();
                            if (trimmed.Length >= 3) trigramWords.Add(trimmed);
                            else shortWords.Add(trimmed);
                        }

                        hasTrigramMatch = trigramWords.Count > 0;

                        // A. ファイル名検索クエリ (MetadataFts MATCH または f.Name LIKE)
                        var nameClauses = new List<string>();
                        nameClauses.Add("f.Status >= 0");

                        string nameFromSql;
                        if (hasTrigramMatch)
                        {
                            nameFromSql = "MetadataFts m JOIN IndexedFiles f ON m.rowid = f.FileId";
                            var matchTerms = trigramWords.Select(w => $"\"{w.Replace("\"", "\"\"")}\"");
                            string metaMatch = string.Join(" AND ", matchTerms);
                            nameClauses.Add("MetadataFts MATCH @metaQuery");
                            cmd.Parameters.AddWithValue("@metaQuery", metaMatch);

                            for (int i = 0; i < shortWords.Count; i++)
                            {
                                string pName = $"@nameShort_{i}";
                                nameClauses.Add($"f.Name LIKE {pName} ESCAPE '\\'");
                                string escShort = "%" + shortWords[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                                cmd.Parameters.AddWithValue(pName, escShort);
                            }
                        }
                        else
                        {
                            nameFromSql = "IndexedFiles f";
                            for (int i = 0; i < shortWords.Count; i++)
                            {
                                string pName = $"@nameShort_{i}";
                                nameClauses.Add($"f.Name LIKE {pName} ESCAPE '\\'");
                                string escShort = "%" + shortWords[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                                cmd.Parameters.AddWithValue(pName, escShort);
                            }
                        }

                        for (int i = 0; i < query.ExcludedWords.Count; i++)
                        {
                            string pName = $"@nameEx_{i}";
                            nameClauses.Add($"f.Name NOT LIKE {pName} ESCAPE '\\'");
                            string escEx = "%" + query.ExcludedWords[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                            cmd.Parameters.AddWithValue(pName, escEx);
                        }

                        string nameWhereSql = string.Join(" AND ", nameClauses) + commonWhereSql;

                        if (isExplicitContentSearch)
                        {
                            // B. 本文検索クエリ (ContentFts)
                            var ftsClauses = new List<string>();
                            ftsClauses.Add("f.Status = 1");

                            if (hasTrigramMatch)
                            {
                                var matchTerms = trigramWords.Select(w => $"\"{w.Replace("\"", "\"\"")}\"");
                                string ftsMatch = string.Join(" AND ", matchTerms);
                                ftsClauses.Add("ContentFts MATCH @ftsQuery");
                                cmd.Parameters.AddWithValue("@ftsQuery", ftsMatch);
                            }

                            for (int i = 0; i < shortWords.Count; i++)
                            {
                                string paramName = $"@shortWord_{i}";
                                ftsClauses.Add($"(c.Body LIKE {paramName} ESCAPE '\\' OR f.Name LIKE {paramName} ESCAPE '\\')");
                                string escVal = "%" + shortWords[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                                cmd.Parameters.AddWithValue(paramName, escVal);
                            }

                            for (int i = 0; i < query.ExcludedWords.Count; i++)
                            {
                                string pName = $"@ftsEx_{i}";
                                ftsClauses.Add($"(f.Name NOT LIKE {pName} ESCAPE '\\' AND c.Body NOT LIKE {pName} ESCAPE '\\')");
                                string escEx = "%" + query.ExcludedWords[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                                cmd.Parameters.AddWithValue(pName, escEx);
                            }

                            string ftsWhereSql = string.Join(" AND ", ftsClauses) + commonWhereSql;
                            string snippetExpr = hasTrigramMatch
                                ? "snippet(ContentFts, 0, '【', '】', '...', 15) AS Snippet"
                                : "SUBSTR(c.Body, 1, 120) AS Snippet";

                            // 本文も検索ON: 本文 (ContentFts) と ファイル名 (MetadataFts/f.Name) のハイブリッド UNION
                            sql = $@"
                                SELECT f.FullPath, f.DirectoryPath, f.SizeBytes, f.LastWriteTimeUtcTicks, {snippetExpr}, f.CreationTimeUtcTicks, f.IsDirectory
                                FROM ContentFts c
                                JOIN IndexedFiles f ON c.rowid = f.FileId
                                WHERE {ftsWhereSql}
                                UNION
                                SELECT f.FullPath, f.DirectoryPath, f.SizeBytes, f.LastWriteTimeUtcTicks, '' AS Snippet, f.CreationTimeUtcTicks, f.IsDirectory
                                FROM {nameFromSql}
                                WHERE {nameWhereSql}
                                LIMIT 500";
                        }
                        else
                        {
                            // 本文も検索OFF: ファイル名・属性のみの超高速検索（MetadataFts / f.Name）
                            sql = $@"
                                SELECT f.FullPath, f.DirectoryPath, f.SizeBytes, f.LastWriteTimeUtcTicks, '' AS Snippet, f.CreationTimeUtcTicks, f.IsDirectory
                                FROM {nameFromSql}
                                WHERE {nameWhereSql}
                                LIMIT 500";
                        }
                    }

                    cmd.CommandText = sql;

                    var deduplicated = new Dictionary<string, SearchResultItem>(StringComparer.OrdinalIgnoreCase);
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        string fullPath = reader.GetString(0);
                        string dirPath = reader.GetString(1);
                        long size = reader.GetInt64(2);
                        long ticks = reader.GetInt64(3);
                        string snippet = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);
                        long cTicks = reader.IsDBNull(5) ? 0 : reader.GetInt64(5);
                        int isDir = reader.FieldCount > 6 && !reader.IsDBNull(6) ? reader.GetInt32(6) : 0;

                        if (!string.IsNullOrEmpty(snippet) && !hasTrigramMatch && shortWords.Count > 0)
                        {
                            string firstShort = shortWords[0];
                            int idx = snippet.IndexOf(firstShort, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                int start = Math.Max(0, idx - 20);
                                int len = Math.Min(snippet.Length - start, 50);
                                snippet = (start > 0 ? "..." : "") + snippet.Substring(start, len).Replace('\r', ' ').Replace('\n', ' ').Trim() + "...";
                            }
                        }

                        if (query.CompiledRegex != null && !query.CompiledRegex.IsMatch(Path.GetFileName(fullPath)))
                        {
                            continue;
                        }

                        string reason = !string.IsNullOrEmpty(snippet)
                            ? (keywords.Count > 0 ? $"Indexed (FTS5): {string.Join(", ", keywords)}" : "Indexed: Content Match")
                            : (keywords.Count > 0 ? $"Indexed: {string.Join(", ", keywords)}" : "Indexed: Property Match");

                        string itemName = Path.GetFileName(fullPath);
                        if (string.IsNullOrEmpty(itemName)) itemName = fullPath;

                        var item = new SearchResultItem
                        {
                            Name = itemName,
                            FullPath = fullPath,
                            DirectoryPath = dirPath,
                            SizeBytes = size,
                            LastWriteTime = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime(),
                            CreationTime = cTicks > 0 ? new DateTime(cTicks, DateTimeKind.Utc).ToLocalTime() : DateTime.MinValue,
                            Extension = isDir == 1 ? string.Empty : Path.GetExtension(fullPath).ToLowerInvariant(),
                            IsDirectory = isDir == 1,
                            ContentSnippet = snippet,
                            MatchedReason = reason
                        };

                        if (deduplicated.TryGetValue(fullPath, out var existing))
                        {
                            // スニペットがある方を優先してマージ
                            if (string.IsNullOrEmpty(existing.ContentSnippet) && !string.IsNullOrEmpty(snippet))
                            {
                                deduplicated[fullPath] = item;
                            }
                        }
                        else
                        {
                            deduplicated[fullPath] = item;
                        }
                    }
                    results.AddRange(deduplicated.Values);
                }

                return results;
            }, ct);
        }

        /// <summary>
        /// 後方互換用：単一文字列キーワードによるインデックス検索
        /// </summary>
        public async Task<List<SearchResultItem>> SearchIndexedAsync(
            string keyword,
            string? scopeFolder = null,
            CancellationToken ct = default)
        {
            var query = SearchQueryParser.Parse(keyword);
            return await SearchIndexedAsync(query, scopeFolder, ct);
        }

        /// <summary>
        /// 現在のインデックス統計情報を取得
        /// </summary>
        public IndexStats GetStats()
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    SELECT COUNT(*), IFNULL(SUM(SizeBytes), 0), MAX(IndexedAtUtcTicks)
                    FROM IndexedFiles WHERE Status = 1";

                using var reader = cmd.ExecuteReader();
                if (reader.Read())
                {
                    int totalFiles = reader.GetInt32(0);
                    long totalSize = reader.GetInt64(1);
                    long maxTicks = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);

                    long dbSize = 0;
                    if (File.Exists(_dbPath))
                    {
                        try { dbSize = new FileInfo(_dbPath).Length; } catch { }
                    }

                    return new IndexStats
                    {
                        TotalFiles = totalFiles,
                        TotalSizeBytes = totalSize,
                        DbSizeBytes = dbSize,
                        LastIndexedUtc = maxTicks > 0 ? new DateTime(maxTicks, DateTimeKind.Utc) : null
                    };
                }
            }
            return new IndexStats();
        }

        /// <summary>
        /// インデックス全消去
        /// </summary>
        public void ClearIndex()
        {
            lock (_lock)
            {
                using var conn = new SqliteConnection(_connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                    DELETE FROM MetadataFts;
                    DELETE FROM ContentFts;
                    DELETE FROM IndexedFiles;
                    DELETE FROM IndexedRoots;
                    VACUUM;";
                cmd.ExecuteNonQuery();
            }
        }

        private async Task<List<ScannedFileEntry>> EnumerateEntriesViaMftAsync(string rootPath, CancellationToken ct)
        {
            var mftService = new AstraSize.Services.Mft.MftScanService();
            var (rootNode, _) = await mftService.ScanPathAsync(rootPath, progress: null, ct);

            var result = new List<ScannedFileEntry>();
            var stack = new Stack<AstraSize.Models.FileItemNode>();
            stack.Push(rootNode);

            string normRoot = Path.GetFullPath(rootPath).TrimEnd('\\', '/');

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = stack.Pop();

                if (current.Children != null)
                {
                    foreach (var child in current.Children)
                    {
                        DateTime lastWrite = child.LastModified ?? DateTime.UtcNow;
                        DateTime creation = child.CreationTime ?? lastWrite;
                        string dir = Path.GetDirectoryName(child.FullPath) ?? normRoot;

                        if (child.IsDirectory)
                        {
                            stack.Push(child);
                            result.Add(new ScannedFileEntry(
                                child.FullPath,
                                child.Name,
                                dir,
                                0,
                                creation,
                                lastWrite,
                                lastWrite,
                                FileAttributes.Directory));
                        }
                        else
                        {
                            result.Add(new ScannedFileEntry(
                                child.FullPath,
                                child.Name,
                                dir,
                                child.Size,
                                creation,
                                lastWrite,
                                lastWrite,
                                FileAttributes.Normal));
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// 単一ファイルまたはフォルダーの作成・更新をリアルタイムにインデックスDBに反映する（Watcher連携用）。
        /// </summary>
        public async Task UpsertSingleFileAsync(string fullPath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || IsDatabaseFile(fullPath)) return;

            bool isDir = Directory.Exists(fullPath);
            if (!isDir && !File.Exists(fullPath)) return;

            await Task.Run(async () =>
            {
                try
                {
                    string name;
                    string dir;
                    long size = 0;
                    long ticks;
                    long cTicks;
                    long nowTicks = DateTime.UtcNow.Ticks;

                    var supportedExts = ContentExtractionService.SupportedExtensions;
                    const long MaxIndexFileSize = 50L * 1024 * 1024; // 50MB
                    bool isContentTarget = false;

                    if (isDir)
                    {
                        var di = new DirectoryInfo(fullPath);
                        name = di.Name;
                        dir = di.Parent?.FullName ?? string.Empty;
                        ticks = di.LastWriteTimeUtc.Ticks;
                        cTicks = di.CreationTimeUtc.Ticks;
                    }
                    else
                    {
                        var fi = new FileInfo(fullPath);
                        name = fi.Name;
                        dir = fi.DirectoryName ?? string.Empty;
                        size = fi.Length;
                        ticks = fi.LastWriteTimeUtc.Ticks;
                        cTicks = fi.CreationTimeUtc.Ticks;
                        isContentTarget = size <= MaxIndexFileSize && supportedExts.Contains(fi.Extension);
                    }

                    string? extractedText = null;
                    bool success = true;

                    if (isContentTarget)
                    {
                        try
                        {
                            extractedText = await ContentExtractionService.ExtractTextAsync(fullPath, ct);
                        }
                        catch
                        {
                            success = false;
                        }
                    }

                    // Generation race 防止: 現在アクティブなスキャンがあればそのGeneration、なければ最新Generationを取得
                    int targetGen = 1;
                    string normPath = Path.GetFullPath(fullPath);
                    foreach (var kvp in _activeScanRoots)
                    {
                        if (normPath.StartsWith(kvp.Key, StringComparison.OrdinalIgnoreCase))
                        {
                            targetGen = (int)kvp.Value;
                            break;
                        }
                    }

                    lock (_lock)
                    {
                        using var conn = new SqliteConnection(_connectionString);
                        conn.Open();

                        if (targetGen == 1)
                        {
                            targetGen = GetGenerationForPathInternal(conn, fullPath);
                        }

                        using var trans = conn.BeginTransaction();

                        long? fileId = null;
                        using (var cmdSel = conn.CreateCommand())
                        {
                            cmdSel.Transaction = trans;
                            cmdSel.CommandText = "SELECT FileId FROM IndexedFiles WHERE FullPath = @path";
                            cmdSel.Parameters.AddWithValue("@path", fullPath);
                            var res = cmdSel.ExecuteScalar();
                            if (res != null && res != DBNull.Value) fileId = Convert.ToInt64(res);
                        }

                        if (fileId.HasValue)
                        {
                            using var cmdUpd = conn.CreateCommand();
                            cmdUpd.Transaction = trans;
                            cmdUpd.CommandText = @"
                                UPDATE IndexedFiles 
                                SET Name = @name, DirectoryPath = @dir, SizeBytes = @size, LastWriteTimeUtcTicks = @ticks, CreationTimeUtcTicks = @cTicks,
                                    Status = @status, IndexedAtUtcTicks = @now, ExtractorVersion = @ver, Generation = @gen, IsDirectory = @isDir
                                WHERE FileId = @fileId;";
                            cmdUpd.Parameters.AddWithValue("@name", name);
                            cmdUpd.Parameters.AddWithValue("@dir", dir);
                            cmdUpd.Parameters.AddWithValue("@size", size);
                            cmdUpd.Parameters.AddWithValue("@ticks", ticks);
                            cmdUpd.Parameters.AddWithValue("@cTicks", cTicks);
                            cmdUpd.Parameters.AddWithValue("@status", success ? 1 : 2);
                            cmdUpd.Parameters.AddWithValue("@now", nowTicks);
                            cmdUpd.Parameters.AddWithValue("@ver", CurrentExtractorVersion);
                            cmdUpd.Parameters.AddWithValue("@gen", targetGen);
                            cmdUpd.Parameters.AddWithValue("@isDir", isDir ? 1 : 0);
                            cmdUpd.Parameters.AddWithValue("@fileId", fileId.Value);
                            cmdUpd.ExecuteNonQuery();

                            using var cmdDelFts = conn.CreateCommand();
                            cmdDelFts.Transaction = trans;
                            cmdDelFts.CommandText = "DELETE FROM ContentFts WHERE rowid = @fileId";
                            cmdDelFts.Parameters.AddWithValue("@fileId", fileId.Value);
                            cmdDelFts.ExecuteNonQuery();

                            using var cmdDelMeta = conn.CreateCommand();
                            cmdDelMeta.Transaction = trans;
                            cmdDelMeta.CommandText = "DELETE FROM MetadataFts WHERE rowid = @fileId";
                            cmdDelMeta.Parameters.AddWithValue("@fileId", fileId.Value);
                            cmdDelMeta.ExecuteNonQuery();
                        }
                        else
                        {
                            using var cmdIns = conn.CreateCommand();
                            cmdIns.Transaction = trans;
                            cmdIns.CommandText = @"
                                INSERT INTO IndexedFiles (FullPath, Name, DirectoryPath, SizeBytes, LastWriteTimeUtcTicks, CreationTimeUtcTicks, Status, IndexedAtUtcTicks, ExtractorVersion, Generation, IsDirectory)
                                VALUES (@path, @name, @dir, @size, @ticks, @cTicks, @status, @now, @ver, @gen, @isDir);
                                SELECT last_insert_rowid();";
                            cmdIns.Parameters.AddWithValue("@path", fullPath);
                            cmdIns.Parameters.AddWithValue("@name", name);
                            cmdIns.Parameters.AddWithValue("@dir", dir);
                            cmdIns.Parameters.AddWithValue("@size", size);
                            cmdIns.Parameters.AddWithValue("@ticks", ticks);
                            cmdIns.Parameters.AddWithValue("@cTicks", cTicks);
                            cmdIns.Parameters.AddWithValue("@status", success ? 1 : 2);
                            cmdIns.Parameters.AddWithValue("@now", nowTicks);
                            cmdIns.Parameters.AddWithValue("@ver", CurrentExtractorVersion);
                            cmdIns.Parameters.AddWithValue("@gen", targetGen);
                            cmdIns.Parameters.AddWithValue("@isDir", isDir ? 1 : 0);
                            fileId = (long)cmdIns.ExecuteScalar()!;
                        }

                        using (var cmdInsMeta = conn.CreateCommand())
                        {
                            cmdInsMeta.Transaction = trans;
                            cmdInsMeta.CommandText = "INSERT INTO MetadataFts (rowid, Name) VALUES (@fileId, @name);";
                            cmdInsMeta.Parameters.AddWithValue("@fileId", fileId.Value);
                            cmdInsMeta.Parameters.AddWithValue("@name", name);
                            cmdInsMeta.ExecuteNonQuery();
                        }

                        if (isContentTarget && !string.IsNullOrWhiteSpace(extractedText))
                        {
                            using var cmdInsFts = conn.CreateCommand();
                            cmdInsFts.Transaction = trans;
                            cmdInsFts.CommandText = "INSERT INTO ContentFts (rowid, Body) VALUES (@fileId, @body);";
                            cmdInsFts.Parameters.AddWithValue("@fileId", fileId.Value);
                            cmdInsFts.Parameters.AddWithValue("@body", extractedText);
                            cmdInsFts.ExecuteNonQuery();
                        }

                        trans.Commit();
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ContentIndexService] UpsertSingleFileAsync error: {ex.Message}");
                }
            }, ct);
        }
    }
}
