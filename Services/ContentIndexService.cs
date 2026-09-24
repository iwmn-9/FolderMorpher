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

                // ADR 85: ContentFts を Mode C（content='', contentless_delete=1, detail='none', tokenize='trigram'）へ自動昇格
                // 本文コピーを保持せず、position情報も持たない（detail=none）極小インデックス
                bool needCreateContentFts = true;
                try
                {
                    using var checkCmd = conn.CreateCommand();
                    checkCmd.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='ContentFts';";
                    var sqlObj = checkCmd.ExecuteScalar();
                    if (sqlObj != null)
                    {
                        string sqlStr = sqlObj.ToString() ?? string.Empty;
                        bool hasFileIdCol = sqlStr.Contains("FileId", StringComparison.OrdinalIgnoreCase);
                        bool isTrueModeC = sqlStr.Contains("contentless_delete", StringComparison.OrdinalIgnoreCase) &&
                                           sqlStr.Contains("content", StringComparison.OrdinalIgnoreCase) &&
                                           sqlStr.Contains("detail", StringComparison.OrdinalIgnoreCase) &&
                                           sqlStr.Contains("none", StringComparison.OrdinalIgnoreCase);

                        if (hasFileIdCol || !isTrueModeC)
                        {
                            // 旧スキーマ（FileId列がある、Mode A通常テーブル、または detail=none 未設定の半端なMode C）
                            bool isOldContentless = sqlStr.Contains("contentless", StringComparison.OrdinalIgnoreCase) ||
                                                    sqlStr.Contains("content = ''", StringComparison.OrdinalIgnoreCase) ||
                                                    sqlStr.Contains("content=''", StringComparison.OrdinalIgnoreCase);

                            if (isOldContentless)
                            {
                                // 既に contentless だった場合（Body が保存されていないため SELECT 移行不可）
                                // ➔ テーブルを detail='none' で再作成し、IndexedFiles の本文ステータスを未インデックスへリセット
                                using var resetCmd = conn.CreateCommand();
                                resetCmd.CommandText = @"
                                    DROP TABLE IF EXISTS ContentFts;
                                    CREATE VIRTUAL TABLE ContentFts USING fts5(
                                        Body,
                                        tokenize = 'trigram',
                                        content = '',
                                        contentless_delete = 1,
                                        detail = 'none'
                                    );
                                    UPDATE IndexedFiles SET Status = 0 WHERE Status = 1;
                                    UPDATE IndexedRoots SET LastCompletedUtcTicks = 0;
                                ";
                                resetCmd.ExecuteNonQuery();
                            }
                            else
                            {
                                // 旧 Mode A 通常テーブル（Body が保持されている）
                                // ➔ 一時テーブル経由で新 Mode C へ安全データ引き継ぎ
                                using var migCmd = conn.CreateCommand();
                                string selectCols = hasFileIdCol
                                    ? "SELECT CAST(FileId AS INTEGER), Body FROM ContentFts_Old WHERE FileId IS NOT NULL;"
                                    : "SELECT rowid, Body FROM ContentFts_Old WHERE rowid IS NOT NULL;";

                                migCmd.CommandText = $@"
                                    ALTER TABLE ContentFts RENAME TO ContentFts_Old;
                                    CREATE VIRTUAL TABLE ContentFts USING fts5(
                                        Body,
                                        tokenize = 'trigram',
                                        content = '',
                                        contentless_delete = 1,
                                        detail = 'none'
                                    );
                                    INSERT INTO ContentFts (rowid, Body)
                                    {selectCols}
                                    DROP TABLE ContentFts_Old;
                                ";
                                migCmd.ExecuteNonQuery();
                            }
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
                                tokenize = 'trigram',
                                content = '',
                                contentless_delete = 1,
                                detail = 'none'
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
        /// 【ADR 81: Storage列挙結果のSearch Metadata Index直結（二重I/Oゼロ）】
        /// DiskScanService で取得済みの FileItemNode ツリーから、ディスク・UNCの再走査なしで
        /// インメモリに ScannedFileEntry を抽出し、IndexedFiles および MetadataFts へ一括高速登録する。
        /// </summary>
        public async Task<IndexProgressReport> SyncFromStorageScanTreeAsync(
            AstraSize.Models.FileItemNode rootNode,
            bool indexContent = false,
            IProgress<IndexProgressReport>? progress = null,
            CancellationToken ct = default)
        {
            if (rootNode == null || string.IsNullOrWhiteSpace(rootNode.FullPath))
            {
                return new IndexProgressReport { StatusMessage = "対象ツリーが無効です。", IsCompleted = true };
            }

            return await Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                string normTarget = Path.GetFullPath(rootNode.FullPath).TrimEnd('\\', '/');

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

                _activeScanRoots[normTarget] = currentGen;

                progress?.Report(new IndexProgressReport
                {
                    StatusMessage = "Storageスキャンツリーからインデックス同期中...",
                    CurrentFile = normTarget,
                    Elapsed = sw.Elapsed
                });

                var scannedEntries = ExtractScannedEntriesFromTree(rootNode, ct);
                var coverage = new ScanCoverage();

                return await ProcessScannedEntriesCoreAsync(
                    normTarget,
                    currentGen,
                    scannedEntries,
                    indexContent,
                    coverage,
                    sw,
                    progress,
                    ct);
            }, ct);
        }

        /// <summary>
        /// 【ADR 83: Lazy Background Builder】
        /// Storage Scan 完了後やアイドル時に、メタデータのみ登録済み（Status = 0）の本文対応ファイルを
        /// Small-File First（容量昇順）で低優先度・バッチ抽出して ContentFts へ投入する。
        /// 他の操作やスキャン開始時は CancellationToken により即座に中断できる。
        /// </summary>
        public async Task<int> ProcessPendingContentIndexAsync(
            string folderPath,
            int maxCount = 100,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return 0;
            string normTarget = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

            return await Task.Run(async () =>
            {
                var supportedExts = ContentExtractionService.SupportedExtensions;
                const long MaxIndexFileSize = 50L * 1024 * 1024; // 50MB上限

                var pendingFiles = new List<(long FileId, string FullPath, long SizeBytes)>();
                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT FileId, FullPath, SizeBytes
                        FROM IndexedFiles
                        WHERE (FullPath = @exact OR FullPath LIKE @prefix ESCAPE '\')
                          AND Status = 0
                          AND IsDirectory = 0
                        ORDER BY SizeBytes ASC
                        LIMIT @limit;";

                    string dirPrefix = normTarget + "\\";
                    string escPrefix = dirPrefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                    cmd.Parameters.AddWithValue("@exact", normTarget);
                    cmd.Parameters.AddWithValue("@prefix", escPrefix);
                    cmd.Parameters.AddWithValue("@limit", maxCount * 2);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        long fId = reader.GetInt64(0);
                        string path = reader.GetString(1);
                        long size = reader.GetInt64(2);
                        if (size <= MaxIndexFileSize && supportedExts.Contains(Path.GetExtension(path)))
                        {
                            pendingFiles.Add((fId, path, size));
                            if (pendingFiles.Count >= maxCount) break;
                        }
                    }
                }

                if (pendingFiles.Count == 0) return 0;

                int processedCount = 0;
                var extractedItems = new List<(long FileId, string Body, bool Success)>();

                foreach (var (fId, path, size) in pendingFiles)
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        if (File.Exists(path))
                        {
                            string? text = await ContentExtractionService.ExtractTextAsync(path, ct);
                            extractedItems.Add((fId, text ?? string.Empty, text != null));
                        }
                        else
                        {
                            extractedItems.Add((fId, string.Empty, false));
                        }
                    }
                    catch
                    {
                        extractedItems.Add((fId, string.Empty, false));
                    }

                    processedCount++;
                }

                if (extractedItems.Count > 0)
                {
                    lock (_lock)
                    {
                        using var conn = new SqliteConnection(_connectionString);
                        conn.Open();
                        using var trans = conn.BeginTransaction();
                        long nowTicks = DateTime.UtcNow.Ticks;

                        foreach (var (fId, body, success) in extractedItems)
                        {
                            int status = success ? 1 : 2; // 1: Success, 2: Failed

                            using var cmdUpd = conn.CreateCommand();
                            cmdUpd.Transaction = trans;
                            cmdUpd.CommandText = "UPDATE IndexedFiles SET Status = @st, IndexedAtUtcTicks = @now WHERE FileId = @fileId;";
                            cmdUpd.Parameters.AddWithValue("@st", status);
                            cmdUpd.Parameters.AddWithValue("@now", nowTicks);
                            cmdUpd.Parameters.AddWithValue("@fileId", fId);
                            cmdUpd.ExecuteNonQuery();

                            if (success && !string.IsNullOrWhiteSpace(body))
                            {
                                using var cmdDel = conn.CreateCommand();
                                cmdDel.Transaction = trans;
                                cmdDel.CommandText = "DELETE FROM ContentFts WHERE rowid = @fileId;";
                                cmdDel.Parameters.AddWithValue("@fileId", fId);
                                cmdDel.ExecuteNonQuery();

                                using var cmdIns = conn.CreateCommand();
                                cmdIns.Transaction = trans;
                                cmdIns.CommandText = "INSERT INTO ContentFts (rowid, Body) VALUES (@fileId, @body);";
                                cmdIns.Parameters.AddWithValue("@fileId", fId);
                                cmdIns.Parameters.AddWithValue("@body", body);
                                cmdIns.ExecuteNonQuery();
                            }
                        }

                        trans.Commit();
                    }
                }

                return processedCount;
            }, ct);
        }

        /// <summary>
        /// 【ADR 83: Opportunistic Indexing（検索時便乗キャッシュ）】
        /// ライブ検索などで本文を読み取ったファイルの内容をその場でインデックスへ投入する。
        /// 次回以降の同一検索がミリ秒インデックス検索へ自動昇格する。
        /// </summary>
        public async Task UpsertFileContentDirectlyAsync(
            string fullPath,
            string contentText,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || string.IsNullOrWhiteSpace(contentText)) return;

            await Task.Run(() =>
            {
                lock (_lock)
                {
                    try
                    {
                        using var conn = new SqliteConnection(_connectionString);
                        conn.Open();

                        long fileId = 0;
                        using (var cmdFind = conn.CreateCommand())
                        {
                            cmdFind.CommandText = "SELECT FileId FROM IndexedFiles WHERE FullPath = @path;";
                            cmdFind.Parameters.AddWithValue("@path", fullPath);
                            var obj = cmdFind.ExecuteScalar();
                            if (obj != null && obj != DBNull.Value)
                            {
                                fileId = Convert.ToInt64(obj);
                            }
                        }

                        if (fileId <= 0) return; // メタデータが存在しない場合はスキップ

                        using var trans = conn.BeginTransaction();
                        long nowTicks = DateTime.UtcNow.Ticks;

                        using var cmdUpd = conn.CreateCommand();
                        cmdUpd.Transaction = trans;
                        cmdUpd.CommandText = "UPDATE IndexedFiles SET Status = 1, IndexedAtUtcTicks = @now WHERE FileId = @fileId;";
                        cmdUpd.Parameters.AddWithValue("@now", nowTicks);
                        cmdUpd.Parameters.AddWithValue("@fileId", fileId);
                        cmdUpd.ExecuteNonQuery();

                        using var cmdDel = conn.CreateCommand();
                        cmdDel.Transaction = trans;
                        cmdDel.CommandText = "DELETE FROM ContentFts WHERE rowid = @fileId;";
                        cmdDel.Parameters.AddWithValue("@fileId", fileId);
                        cmdDel.ExecuteNonQuery();

                        using var cmdIns = conn.CreateCommand();
                        cmdIns.Transaction = trans;
                        cmdIns.CommandText = "INSERT INTO ContentFts (rowid, Body) VALUES (@fileId, @body);";
                        cmdIns.Parameters.AddWithValue("@fileId", fileId);
                        cmdIns.Parameters.AddWithValue("@body", contentText);
                        cmdIns.ExecuteNonQuery();

                        trans.Commit();
                    }
                    catch
                    {
                        // 便乗キャッシュ失敗は安全に無視
                    }
                }
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

                return await ProcessScannedEntriesCoreAsync(
                    normTarget,
                    currentGen,
                    scannedEntries,
                    indexContent: true,
                    coverage,
                    sw,
                    progress,
                    ct);
            }, ct);
        }

        private async Task<IndexProgressReport> ProcessScannedEntriesCoreAsync(
            string normTarget,
            int currentGen,
            List<ScannedFileEntry> scannedEntries,
            bool indexContent,
            ScanCoverage coverage,
            Stopwatch sw,
            IProgress<IndexProgressReport>? progress,
            CancellationToken ct)
        {
            try
            {
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

                List<ScannedFileEntry> contentTargets;
                List<ScannedFileEntry> metadataOnlyFiles;

                if (indexContent)
                {
                    contentTargets = allFiles
                        .Where(e => e.Length <= MaxIndexFileSize && supportedExts.Contains(Path.GetExtension(e.Name)))
                        .ToList();
                    metadataOnlyFiles = allFiles
                        .Where(e => e.Length > MaxIndexFileSize || !supportedExts.Contains(Path.GetExtension(e.Name)))
                        .ToList();
                }
                else
                {
                    // メタデータ専用モード（Storage同期時等）: 全ファイルをメタデータ登録へ回す（本文抽出は後回し）
                    contentTargets = new List<ScannedFileEntry>();
                    metadataOnlyFiles = allFiles;
                }

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
        }

        /// <summary>
        /// trigram FTS5 (detail=none / content='') 用にキーワードを 3文字 term の AND 結合式に変換する。
        /// 例: "秘密保持" ➔ "\"秘密保\" AND \"密保持\""
        /// 3文字未満の場合は null を返す（trigram FTS5 では検索不可）。
        /// </summary>
        public static string? BuildTrigramMatchExpression(string keyword)
        {
            if (string.IsNullOrWhiteSpace(keyword)) return null;

            string clean = keyword.Trim().Trim('"', '\'');
            if (clean.Length < 3) return null;

            if (clean.Length == 3)
            {
                return $"\"{clean.Replace("\"", "\"\"")}\"";
            }

            var terms = new List<string>();
            for (int i = 0; i <= clean.Length - 3; i++)
            {
                string trigram = clean.Substring(i, 3);
                terms.Add($"\"{trigram.Replace("\"", "\"\"")}\"");
            }

            var distinctTerms = terms.Distinct().ToList();
            return string.Join(" AND ", distinctTerms);
        }

        /// <summary>
        /// SQLite FTS5 (Mode C: trigram contentless + detail=none) を使用したミリ秒全文検索
        /// （SearchQuery構文完全貫通・3文字分解AND・Progressive Verify・先行通知対応）
        /// </summary>
        public async Task<List<SearchResultItem>> SearchIndexedAsync(
            SearchQuery query,
            string? scopeFolder = null,
            CancellationToken ct = default,
            Action<IReadOnlyList<SearchResultItem>>? onNameHitsReady = null,
            IProgress<SearchProgressReport>? progress = null,
            IProgress<IReadOnlyList<SearchResultItem>>? batchYield = null)
        {
            return await Task.Run(async () =>
            {
                var results = new List<SearchResultItem>();

                // 検索キーワードの整理
                var keywords = new List<string>(query.Keywords);
                bool hasContentKeyword = !string.IsNullOrEmpty(query.ContentKeyword);

                // キーワードも属性条件もない場合は空
                if (keywords.Count == 0 && !hasContentKeyword && query.ExactPhrases.Count == 0 && query.Extensions.Count == 0 && !query.MinSizeBytes.HasValue && !query.MaxSizeBytes.HasValue && !query.DormantYears.HasValue && !query.DormantDays.HasValue && !query.MinPathLength.HasValue && query.PathContains.Count == 0)
                {
                    return results;
                }

                bool isExplicitContentSearch = query.SearchContentMode || hasContentKeyword;
                var nameHits = new List<SearchResultItem>();
                var fullHits = new List<SearchResultItem>();
                var groupSubqueries = new List<string>();

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

                    bool hasTrigramMatch = false;
                    var shortWords = new List<string>();

                    SearchResultItem ParseReaderItem(SqliteDataReader reader, string snippetOverride = "")
                    {
                        string fullPath = reader.GetString(0);
                        string dirPath = reader.GetString(1);
                        long size = reader.GetInt64(2);
                        long ticks = reader.GetInt64(3);
                        string snippet = string.IsNullOrEmpty(snippetOverride)
                            ? (reader.IsDBNull(4) ? string.Empty : reader.GetString(4))
                            : snippetOverride;
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

                        string reason = !string.IsNullOrEmpty(snippet)
                            ? (keywords.Count > 0 ? $"Indexed (FTS5): {string.Join(", ", keywords)}" : "Indexed: Content Match")
                            : (keywords.Count > 0 ? $"Indexed: {string.Join(", ", keywords)}" : "Indexed: Property Match");

                        string itemName = Path.GetFileName(fullPath);
                        if (string.IsNullOrEmpty(itemName)) itemName = fullPath;

                        return new SearchResultItem
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
                    }

                    if (keywords.Count == 0 && !hasContentKeyword && query.ExactPhrases.Count == 0)
                    {
                        // キーワードなし（属性検索のみ）: IndexedFiles 単体検索
                        string whereSql = commonWhereClauses.Count > 0 ? string.Join(" AND ", commonWhereClauses) : "1=1";
                        cmd.CommandText = $@"
                            SELECT f.FullPath, f.DirectoryPath, f.SizeBytes, f.LastWriteTimeUtcTicks, '' AS Snippet, f.CreationTimeUtcTicks, f.IsDirectory
                            FROM IndexedFiles f
                            WHERE {whereSql}";

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                ct.ThrowIfCancellationRequested();
                                var item = ParseReaderItem(reader);
                                if (query.CompiledRegex != null && !query.CompiledRegex.IsMatch(item.Name)) continue;
                                results.Add(item);
                            }
                        }

                        if (onNameHitsReady != null && results.Count > 0)
                        {
                            try { onNameHitsReady(results); } catch { }
                        }

                        return results;
                    }

                    // キーワードあり: 本文 (ContentFts) と ファイル名 (MetadataFts) のハイブリッド検索（ORグループ対応 ＆ INTERSECT 積集合 ＆ ExactPhrases統合）
                    var baseGroups = query.KeywordGroups.Count > 0 ? query.KeywordGroups : keywords.Select(k => new List<string> { k }).ToList();
                    var keywordGroups = baseGroups
                        .Where(g => g.Count > 0 && g.Any(w => !string.IsNullOrWhiteSpace(w)))
                        .Select(g => g.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
                        .ToList();

                    // ExactPhrases（完全一致引用符フレーズ）を必須グループとして統合
                    foreach (var phrase in query.ExactPhrases)
                    {
                        if (!string.IsNullOrWhiteSpace(phrase))
                        {
                            keywordGroups.Add(new List<string> { phrase.Trim() });
                        }
                    }

                    // 除外ワードの SQL パラメータと WHERE 句
                    for (int i = 0; i < query.ExcludedWords.Count; i++)
                    {
                        string pName = $"@nameEx_{i}";
                        commonWhereClauses.Add($"f.Name NOT LIKE {pName} ESCAPE '\\'");
                        string escEx = "%" + query.ExcludedWords[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                        cmd.Parameters.AddWithValue(pName, escEx);
                    }

                    // 各キーワードグループのサブクエリ構築
                    groupSubqueries.Clear();
                    var nameOnlySubqueries = new List<string>();
                    int termCounter = 0;

                    for (int gIdx = 0; gIdx < keywordGroups.Count; gIdx++)
                    {
                        var grp = keywordGroups[gIdx];
                        if (grp.Count == 0) continue;

                        // スペースを含む完全フレーズは LIKE 句で 100% 確実に一致させる
                        bool grpAllFts = grp.All(w => w.Length >= 3 && !w.Contains(' '));

                        // 1. ファイル名側の条件
                        string nameSql;
                        if (grpAllFts)
                        {
                            string pName = $"@meta_{gIdx}";
                            var terms = grp.Select(w => $"\"{w.Replace("\"", "\"\"")}\"");
                            string metaExpr = grp.Count > 1 ? "(" + string.Join(" OR ", terms) + ")" : terms.First();
                            cmd.Parameters.AddWithValue(pName, metaExpr);
                            nameSql = $"SELECT rowid AS FileId FROM MetadataFts WHERE MetadataFts MATCH {pName}";
                        }
                        else
                        {
                            var orClauses = new List<string>();
                            foreach (var w in grp)
                            {
                                string pName = $"@nameLike_{termCounter++}";
                                orClauses.Add($"Name LIKE {pName} ESCAPE '\\'");
                                string esc = "%" + w.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                                cmd.Parameters.AddWithValue(pName, esc);
                            }
                            nameSql = $"SELECT FileId FROM IndexedFiles WHERE ({string.Join(" OR ", orClauses)})";
                        }

                        nameOnlySubqueries.Add(nameSql);

                        // 2. 本文検索が有効な場合、3文字分解 trigram MATCH 式を生成して UNION
                        if (query.SearchContentMode)
                        {
                            var trigramExprs = new List<string>();
                            foreach (var w in grp)
                            {
                                string? expr = BuildTrigramMatchExpression(w);
                                if (!string.IsNullOrEmpty(expr))
                                {
                                    trigramExprs.Add(expr.Contains(" AND ") ? $"({expr})" : expr);
                                }
                            }

                            if (trigramExprs.Count > 0)
                            {
                                string pName = $"@body_{gIdx}";
                                string bodyMatch = trigramExprs.Count > 1
                                    ? string.Join(" OR ", trigramExprs)
                                    : trigramExprs[0];
                                cmd.Parameters.AddWithValue(pName, bodyMatch);
                                string bodySql = $"SELECT rowid AS FileId FROM ContentFts WHERE ContentFts MATCH {pName}";
                                groupSubqueries.Add($"SELECT FileId FROM (\n  {nameSql}\n  UNION\n  {bodySql}\n)");
                            }
                            else
                            {
                                // 3文字以上の語がない（1〜2文字語のみ）場合:
                                // ファイル名一致 (nameSql) に加え、本文検索ONなら本文対象ファイル (Status IN (0, 1)) を候補としてUNIONし、
                                // 後段の Progressive Verify (Aho-Corasick) で原本を直接照合する（False Negative 100% ゼロ保証）
                                string bodySql = "SELECT FileId FROM IndexedFiles WHERE Status IN (0, 1) AND IsDirectory = 0";
                                groupSubqueries.Add($"SELECT FileId FROM (\n  {nameSql}\n  UNION\n  {bodySql}\n)");
                            }
                        }
                        else
                        {
                            groupSubqueries.Add(nameSql);
                        }
                    }

                    // 3. content: 修飾子がある場合、本文必須条件として追加
                    if (hasContentKeyword)
                    {
                        string kw = query.ContentKeyword!.Trim();
                        string? expr = BuildTrigramMatchExpression(kw);
                        if (!string.IsNullOrEmpty(expr))
                        {
                            string pName = "@contentKw";
                            cmd.Parameters.AddWithValue(pName, expr);
                            string contentSql = $"SELECT rowid AS FileId FROM ContentFts WHERE ContentFts MATCH {pName}";
                            groupSubqueries.Add(contentSql);
                        }
                        else
                        {
                            // 3文字未満の語の場合、本文対象ファイルを候補として通し、後段 Verify で確定
                            string contentSql = "SELECT FileId FROM IndexedFiles WHERE Status IN (0, 1) AND IsDirectory = 0";
                            groupSubqueries.Add(contentSql);
                        }
                    }

                    commonWhereSql = commonWhereClauses.Count > 0 ? " AND " + string.Join(" AND ", commonWhereClauses) : "";

                    var deduplicated = new Dictionary<string, SearchResultItem>(StringComparer.OrdinalIgnoreCase);
                    nameHits.Clear();

                    // ★ フェーズ1: ファイル名一致（先行表示用、ミリ秒応答）
                    bool canDoProgressiveNameHits = onNameHitsReady != null &&
                                                    query.SearchContentMode &&
                                                    !hasContentKeyword &&
                                                    nameOnlySubqueries.Count > 0;

                    if (nameOnlySubqueries.Count > 0)
                    {
                        string nameIntersectSql = string.Join("\nINTERSECT\n", nameOnlySubqueries);
                        string nameSql = $@"
                            SELECT f.FullPath, f.DirectoryPath, f.SizeBytes, f.LastWriteTimeUtcTicks, '' AS Snippet, f.CreationTimeUtcTicks, f.IsDirectory, f.FileId
                            FROM IndexedFiles f
                            WHERE f.FileId IN (
                                {nameIntersectSql}
                            )
                            {commonWhereSql}";

                        cmd.CommandText = nameSql;
                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                ct.ThrowIfCancellationRequested();
                                var item = ParseReaderItem(reader);
                                if (query.CompiledRegex != null && !query.CompiledRegex.IsMatch(item.Name)) continue;
                                item.MatchedReason = "Indexed: Name Match";
                                nameHits.Add(item);
                                deduplicated[item.FullPath] = item;
                            }
                        }

                        if (nameHits.Count > 0 && canDoProgressiveNameHits)
                        {
                            try { onNameHitsReady!(nameHits); } catch { }
                        }
                    }

                    // ★ フェーズ2: 全体検索（(Name OR Content) の積集合）
                    fullHits.Clear();
                    if (groupSubqueries.Count > 0)
                    {
                        string intersectSql = string.Join("\nINTERSECT\n", groupSubqueries);
                        string fullSearchSql = $@"
                            SELECT f.FullPath, f.DirectoryPath, f.SizeBytes, f.LastWriteTimeUtcTicks, '' AS Snippet, f.CreationTimeUtcTicks, f.IsDirectory, f.FileId
                            FROM IndexedFiles f
                            WHERE f.FileId IN (
                                {intersectSql}
                            )
                            {commonWhereSql}";

                        cmd.CommandText = fullSearchSql;

                        using (var reader = cmd.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                ct.ThrowIfCancellationRequested();
                                var item = ParseReaderItem(reader);
                                if (query.CompiledRegex != null && !query.CompiledRegex.IsMatch(item.Name)) continue;

                                if (deduplicated.TryGetValue(item.FullPath, out var existing))
                                {
                                    fullHits.Add(existing);
                                }
                                else
                                {
                                    item.MatchedReason = "Indexed Match";
                                    deduplicated[item.FullPath] = item;
                                    fullHits.Add(item);
                                }
                            }
                        }
                    }
                    else
                    {
                        fullHits.AddRange(deduplicated.Values);
                    }
                } // lock (_lock) 終了：DB接続を即座に解放

                // ★ ADR 85: Progressive Verify パイプライン（Aho-Corasick ＆ AdaptiveConcurrency）
                // Mode C (contentless) では DB に本文を持たないため、本文候補（Candidates）に対して
                // 原本ファイルを Small-File First でストリーム Verify し、真の Hit のみ UI へ順次合流
                if (isExplicitContentSearch && groupSubqueries.Count > 0)
                {
                    var allSearchWords = new List<string>();
                    foreach (var w in query.Keywords)
                    {
                        if (!string.IsNullOrWhiteSpace(w) && !allSearchWords.Contains(w, StringComparer.OrdinalIgnoreCase))
                            allSearchWords.Add(w);
                    }
                    foreach (var grp in query.KeywordGroups)
                    {
                        foreach (var w in grp)
                        {
                            if (!string.IsNullOrWhiteSpace(w) && !allSearchWords.Contains(w, StringComparer.OrdinalIgnoreCase))
                                allSearchWords.Add(w);
                        }
                    }
                    foreach (var p in query.ExactPhrases)
                    {
                        if (!string.IsNullOrWhiteSpace(p) && !allSearchWords.Contains(p, StringComparer.OrdinalIgnoreCase))
                            allSearchWords.Add(p);
                    }
                    if (!string.IsNullOrWhiteSpace(query.ContentKeyword) && !allSearchWords.Contains(query.ContentKeyword, StringComparer.OrdinalIgnoreCase))
                    {
                        allSearchWords.Add(query.ContentKeyword);
                    }

                    // fullHits は SQL レベルですべての条件を満たした正本集合。
                    // そのうち「content:修飾子がなく、かつファイル名だけで全キーワードを満たすもの」は本文検査不要の確定 Hit。
                    // それ以外（本文照合を要求されるもの、またはファイル名だけでは満たしていないもの）は本文候補（Candidates）。
                    var namePathSet = new HashSet<string>(nameHits.Select(n => n.FullPath), StringComparer.OrdinalIgnoreCase);

                    var confirmedHits = fullHits
                        .Where(h => !hasContentKeyword && namePathSet.Contains(h.FullPath))
                        .ToList();

                    var contentCandidates = fullHits
                        .Where(h => hasContentKeyword || !namePathSet.Contains(h.FullPath))
                        .ToList();

                    // 1. 確定 Hit（ファイル名一致で本文検査不要なもの）を結果に追加
                    results.AddRange(confirmedHits);

                        // 2. 本文候補がある場合、原本ファイルを Small-File First で Progressive Verify
                        if (contentCandidates.Count > 0 && allSearchWords.Count > 0)
                        {
                            var ahoCorasick = new AhoCorasickSearcher(allSearchWords);
                            var validCandidates = contentCandidates
                                .Where(c => !c.IsDirectory && File.Exists(c.FullPath))
                                .OrderBy(c => c.SizeBytes)
                                .ToList();

                            var verifiedHits = new System.Collections.Concurrent.ConcurrentBag<SearchResultItem>();
                            var streamingBatch = new List<SearchResultItem>();
                            var batchLock = new object();
                            int verifiedCount = 0;
                            var controller = new AdaptiveConcurrencyController();
                            var po = new ParallelOptions
                            {
                                MaxDegreeOfParallelism = AdaptiveConcurrencyController.MaxConcurrency,
                                CancellationToken = ct
                            };

                            await Parallel.ForEachAsync(validCandidates, po, async (cand, token) =>
                            {
                                token.ThrowIfCancellationRequested();
                                using var lease = await controller.AcquireAsync(token);
                                var swCand = Stopwatch.StartNew();

                                string? body = null;
                                try
                                {
                                    body = await ContentExtractionService.ExtractTextAsync(cand.FullPath, token);
                                    swCand.Stop();
                                    lease.Report(swCand.Elapsed.TotalMilliseconds, isError: false);
                                }
                                catch
                                {
                                    swCand.Stop();
                                    lease.Report(swCand.Elapsed.TotalMilliseconds, isError: true);
                                }

                                int currVerified = Interlocked.Increment(ref verifiedCount);

                                if (!string.IsNullOrEmpty(body))
                                {
                                    string snip = ahoCorasick.ExtractSnippet(body);
                                    if (!string.IsNullOrEmpty(snip))
                                    {
                                        cand.ContentSnippet = snip;
                                        cand.MatchedReason = $"Indexed (FTS5): {string.Join(", ", allSearchWords)}";
                                        verifiedHits.Add(cand);

                                        if (batchYield != null)
                                        {
                                            lock (batchLock)
                                            {
                                                streamingBatch.Add(cand);
                                                int threshold = verifiedHits.Count <= 3 ? 1 : 5;
                                                if (streamingBatch.Count >= threshold)
                                                {
                                                    batchYield.Report(streamingBatch.ToList());
                                                    streamingBatch.Clear();
                                                }
                                            }
                                        }
                                    }
                                }

                                if (progress != null && currVerified % 10 == 0)
                                {
                                    progress.Report(new SearchProgressReport
                                    {
                                        HitCount = results.Count + verifiedHits.Count,
                                        ScannedCount = currVerified,
                                        CurrentPath = cand.FullPath,
                                        IsCompleted = false
                                    });
                                }
                            });

                            lock (batchLock)
                            {
                                if (streamingBatch.Count > 0 && batchYield != null)
                                {
                                    batchYield.Report(streamingBatch.ToList());
                                    streamingBatch.Clear();
                                }
                            }

                            results.AddRange(verifiedHits);
                        }
                    }
                    else
                    {
                        results.AddRange(fullHits.Count > 0 ? fullHits : nameHits);
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

        /// <summary>
        /// 【ADR 81: Storageツリーからのエントリ抽出（再走査ゼロ）】
        /// メモリ上の FileItemNode ツリーを木構造走査し、再stat・ネットワークI/Oゼロで ScannedFileEntry の一覧へ展開する。
        /// </summary>
        public static List<ScannedFileEntry> ExtractScannedEntriesFromTree(AstraSize.Models.FileItemNode rootNode, CancellationToken ct = default)
        {
            var result = new List<ScannedFileEntry>();
            if (rootNode == null) return result;

            var stack = new Stack<AstraSize.Models.FileItemNode>();
            stack.Push(rootNode);

            string normRoot = Path.GetFullPath(rootNode.FullPath).TrimEnd('\\', '/');

            // ルート自身もディレクトリとして登録（ドライブ直下等の場合）
            DateTime rootLastWrite = rootNode.LastModified ?? DateTime.UtcNow;
            DateTime rootCreation = rootNode.CreationTime ?? rootLastWrite;
            string? rootParentDir = Path.GetDirectoryName(rootNode.FullPath);
            if (!string.IsNullOrEmpty(rootParentDir))
            {
                result.Add(new ScannedFileEntry(
                    rootNode.FullPath,
                    rootNode.Name,
                    rootParentDir,
                    0,
                    rootCreation,
                    rootLastWrite,
                    rootLastWrite,
                    FileAttributes.Directory));
            }

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

        private async Task<List<ScannedFileEntry>> EnumerateEntriesViaMftAsync(string rootPath, CancellationToken ct)
        {
            var mftService = new AstraSize.Services.Mft.MftScanService();
            var (rootNode, _) = await mftService.ScanPathAsync(rootPath, progress: null, ct);
            return ExtractScannedEntriesFromTree(rootNode, ct);
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
