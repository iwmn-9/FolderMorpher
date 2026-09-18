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

        public string DbPath => _dbPath;

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
                        DirectoryPath TEXT NOT NULL,
                        SizeBytes INTEGER NOT NULL,
                        LastWriteTimeUtcTicks INTEGER NOT NULL,
                        Status INTEGER NOT NULL, -- 0: Pending, 1: Indexed, 2: Failed
                        IndexedAtUtcTicks INTEGER,
                        ExtractorVersion INTEGER NOT NULL DEFAULT 1
                    );
                    CREATE INDEX IF NOT EXISTS idx_files_path ON IndexedFiles(FullPath);
                    CREATE INDEX IF NOT EXISTS idx_files_dir ON IndexedFiles(DirectoryPath);

                    CREATE TABLE IF NOT EXISTS IndexedRoots (
                        RootPath TEXT PRIMARY KEY COLLATE NOCASE,
                        Status TEXT NOT NULL, -- 'InProgress', 'Complete', 'Error'
                        CoverageComplete INTEGER NOT NULL DEFAULT 1,
                        LastCompletedUtcTicks INTEGER,
                        TotalFiles INTEGER NOT NULL DEFAULT 0,
                        ExtractorVersion INTEGER NOT NULL DEFAULT 1
                    );

                    CREATE VIRTUAL TABLE IF NOT EXISTS ContentFts USING fts5(
                        FileId UNINDEXED,
                        Body,
                        tokenize = 'trigram'
                    );
                ";
                cmd.ExecuteNonQuery();

                // マイグレーション: ExtractorVersion カラム追加 (既存DB対応)
                try
                {
                    using var alterCmd = conn.CreateCommand();
                    alterCmd.CommandText = "ALTER TABLE IndexedFiles ADD COLUMN ExtractorVersion INTEGER NOT NULL DEFAULT 1;";
                    alterCmd.ExecuteNonQuery();
                }
                catch
                {
                    // 既にカラムが存在する場合は無視
                }
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

                // ルート状態を InProgress に登録
                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        INSERT OR REPLACE INTO IndexedRoots (RootPath, Status, CoverageComplete, LastCompletedUtcTicks, TotalFiles, ExtractorVersion)
                        VALUES (@root, 'InProgress', 0, @ticks, 0, @extVer);";
                    cmd.Parameters.AddWithValue("@root", normTarget);
                    cmd.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
                    cmd.Parameters.AddWithValue("@extVer", CurrentExtractorVersion);
                    cmd.ExecuteNonQuery();
                }

                progress?.Report(new IndexProgressReport
                {
                    StatusMessage = "メタデータを走査中...",
                    CurrentFile = normTarget,
                    Elapsed = sw.Elapsed
                });

                var coverage = new ScanCoverage();

                try
                {
                    // 1. 並列2固定でファイル一覧を一括列挙 (I/O最小化 & カバレッジ追跡)
                    var scannedEntries = await SafeFileEnumerator.EnumerateFileEntriesParallelAsync(
                        normTarget,
                        "*.*",
                        coverage: coverage,
                        onProgress: null,
                        ct: ct);

                    // 2. ファイルを全ファイルと本文対象ファイルに分類 (抜本案: 全ファイルメタデータ登録 ＋ 本文対象のみFTS)
                    var allFiles = scannedEntries
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
                    var existingMap = new Dictionary<string, (long FileId, long SizeBytes, long LastWriteTicks, int Status, int ExtractorVer)>(StringComparer.OrdinalIgnoreCase);
                    lock (_lock)
                    {
                        using var conn = new SqliteConnection(_connectionString);
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                            SELECT FileId, FullPath, SizeBytes, LastWriteTimeUtcTicks, Status, ExtractorVersion 
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
                            existingMap[path] = (fId, size, ticks, status, extVer);
                        }
                    }

                    // 4. 亡霊ファイル（削除・リネームで消失した旧パス）のクリーンアップ (Sol指摘2 & アクセス拒否保護)
                    int deletedCount = 0;
                    var currentPaths = new HashSet<string>(allFiles.Select(e => e.FullPath), StringComparer.OrdinalIgnoreCase);
                    var ghostFileIds = existingMap
                        .Where(kvp => !currentPaths.Contains(kvp.Key))
                        .Select(kvp => kvp.Value.FileId)
                        .ToList();

                    // ★ 走査中にアクセス拒否（AccessDenied）があった場合は、ファイルが消えたのではなく読めなかっただけなので亡霊削除を抑止
                    if (coverage.AccessDeniedFolders == 0 && ghostFileIds.Count > 0)
                    {
                        lock (_lock)
                        {
                            using var conn = new SqliteConnection(_connectionString);
                            conn.Open();
                            using var trans = conn.BeginTransaction();
                            foreach (var gId in ghostFileIds)
                            {
                                using var delFts = conn.CreateCommand();
                                delFts.Transaction = trans;
                                delFts.CommandText = "DELETE FROM ContentFts WHERE FileId = @id";
                                delFts.Parameters.AddWithValue("@id", gId);
                                delFts.ExecuteNonQuery();

                                using var delFile = conn.CreateCommand();
                                delFile.Transaction = trans;
                                delFile.CommandText = "DELETE FROM IndexedFiles WHERE FileId = @id";
                                delFile.Parameters.AddWithValue("@id", gId);
                                delFile.ExecuteNonQuery();
                            }
                            trans.Commit();
                        }
                        deletedCount = ghostFileIds.Count;
                    }

                    int alreadyIndexed = 0;
                    int newlyIndexed = 0;
                    int failedCount = 0;

                    // 5-A. 非本文ファイル（zip, exe, 動画, 画像など）のメタデータを IndexedFiles に一括登録 (抜本案)
                    var metaToInsert = new List<ScannedFileEntry>();
                    foreach (var entry in metadataOnlyFiles)
                    {
                        long currentTicks = entry.LastWriteTime.ToUniversalTime().Ticks;
                        if (existingMap.TryGetValue(entry.FullPath, out var meta))
                        {
                            if (meta.SizeBytes == entry.Length && meta.LastWriteTicks == currentTicks)
                            {
                                alreadyIndexed++;
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
                                if (existingMap.TryGetValue(entry.FullPath, out var meta))
                                {
                                    using var cmdUpd = conn.CreateCommand();
                                    cmdUpd.Transaction = trans;
                                    cmdUpd.CommandText = @"
                                        UPDATE IndexedFiles 
                                        SET SizeBytes = @size, LastWriteTimeUtcTicks = @ticks, Status = 0, 
                                            IndexedAtUtcTicks = @now, ExtractorVersion = @ver
                                        WHERE FileId = @fileId";
                                    cmdUpd.Parameters.AddWithValue("@size", entry.Length);
                                    cmdUpd.Parameters.AddWithValue("@ticks", currentTicks);
                                    cmdUpd.Parameters.AddWithValue("@now", nowTicks);
                                    cmdUpd.Parameters.AddWithValue("@ver", CurrentExtractorVersion);
                                    cmdUpd.Parameters.AddWithValue("@fileId", meta.FileId);
                                    cmdUpd.ExecuteNonQuery();
                                }
                                else
                                {
                                    using var cmdIns = conn.CreateCommand();
                                    cmdIns.Transaction = trans;
                                    cmdIns.CommandText = @"
                                        INSERT INTO IndexedFiles (FullPath, DirectoryPath, SizeBytes, LastWriteTimeUtcTicks, Status, IndexedAtUtcTicks, ExtractorVersion)
                                        VALUES (@path, @dir, @size, @ticks, 0, @now, @ver);";
                                    cmdIns.Parameters.AddWithValue("@path", entry.FullPath);
                                    cmdIns.Parameters.AddWithValue("@dir", entry.DirectoryPath);
                                    cmdIns.Parameters.AddWithValue("@size", entry.Length);
                                    cmdIns.Parameters.AddWithValue("@ticks", currentTicks);
                                    cmdIns.Parameters.AddWithValue("@now", nowTicks);
                                    cmdIns.Parameters.AddWithValue("@ver", CurrentExtractorVersion);
                                    cmdIns.ExecuteNonQuery();
                                }
                            }
                            trans.Commit();
                        }
                        newlyIndexed += metaToInsert.Count;
                    }

                    // 5-B. 本文対象ファイル（Office/PDF/Text）の差分判定
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
                                continue;
                            }
                        }
                        toProcess.Add(entry);
                    }

                    // ★ Sol提唱: 小さいファイル優先（Small-File First）でソート
                    toProcess = toProcess.OrderBy(e => e.Length).ToList();

                    int totalDiscovered = allFiles.Count;
                    int processedCount = alreadyIndexed + metaToInsert.Count;

                    progress?.Report(new IndexProgressReport
                    {
                        TotalDiscovered = totalDiscovered,
                        AlreadyIndexed = alreadyIndexed,
                        ProcessedCount = processedCount,
                        NewlyIndexedCount = newlyIndexed,
                        DeletedCount = deletedCount,
                        StatusMessage = $"インデックス更新開始 (全ファイル: {totalDiscovered:N0} 件 / 本文抽出対象: {toProcess.Count:N0} 件 / 変更なしスキップ: {alreadyIndexed:N0} 件)",
                        Elapsed = sw.Elapsed
                    });

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
                                long nowTicks = DateTime.UtcNow.Ticks;

                                long fileId;
                                if (existingMap.TryGetValue(entry.FullPath, out var meta))
                                {
                                    fileId = meta.FileId;
                                    using var cmdUpd = conn.CreateCommand();
                                    cmdUpd.Transaction = trans;
                                    cmdUpd.CommandText = @"
                                        UPDATE IndexedFiles 
                                        SET SizeBytes = @size, LastWriteTimeUtcTicks = @ticks, Status = @status, 
                                            IndexedAtUtcTicks = @now, ExtractorVersion = @ver
                                        WHERE FileId = @fileId";
                                    cmdUpd.Parameters.AddWithValue("@size", entry.Length);
                                    cmdUpd.Parameters.AddWithValue("@ticks", currentTicks);
                                    cmdUpd.Parameters.AddWithValue("@status", item.Success ? 1 : 2);
                                    cmdUpd.Parameters.AddWithValue("@now", nowTicks);
                                    cmdUpd.Parameters.AddWithValue("@ver", CurrentExtractorVersion);
                                    cmdUpd.Parameters.AddWithValue("@fileId", fileId);
                                    cmdUpd.ExecuteNonQuery();

                                    // 既存の FTS エントリを削除
                                    using var cmdDelFts = conn.CreateCommand();
                                    cmdDelFts.Transaction = trans;
                                    cmdDelFts.CommandText = "DELETE FROM ContentFts WHERE FileId = @fileId";
                                    cmdDelFts.Parameters.AddWithValue("@fileId", fileId);
                                    cmdDelFts.ExecuteNonQuery();
                                }
                                else
                                {
                                    using var cmdIns = conn.CreateCommand();
                                    cmdIns.Transaction = trans;
                                    cmdIns.CommandText = @"
                                        INSERT INTO IndexedFiles (FullPath, DirectoryPath, SizeBytes, LastWriteTimeUtcTicks, Status, IndexedAtUtcTicks, ExtractorVersion)
                                        VALUES (@path, @dir, @size, @ticks, @status, @now, @ver);
                                        SELECT last_insert_rowid();";
                                    cmdIns.Parameters.AddWithValue("@path", entry.FullPath);
                                    cmdIns.Parameters.AddWithValue("@dir", entry.DirectoryPath);
                                    cmdIns.Parameters.AddWithValue("@size", entry.Length);
                                    cmdIns.Parameters.AddWithValue("@ticks", currentTicks);
                                    cmdIns.Parameters.AddWithValue("@status", item.Success ? 1 : 2);
                                    cmdIns.Parameters.AddWithValue("@now", nowTicks);
                                    cmdIns.Parameters.AddWithValue("@ver", CurrentExtractorVersion);
                                    fileId = (long)cmdIns.ExecuteScalar()!;
                                }

                                if (item.Success && !string.IsNullOrWhiteSpace(item.Text))
                                {
                                    using var cmdInsFts = conn.CreateCommand();
                                    cmdInsFts.Transaction = trans;
                                    cmdInsFts.CommandText = "INSERT INTO ContentFts (FileId, Body) VALUES (@fileId, @body)";
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
                            DeletedCount = deletedCount,
                            FailedCount = failedCount,
                            CurrentFile = batch.LastOrDefault()?.Name ?? string.Empty,
                            StatusMessage = $"インデックス中 ({processedCount:N0} / {totalDiscovered:N0})",
                            Elapsed = sw.Elapsed
                        });
                    }

                    // 完了時に IndexedRoots を Complete に更新（中断されていないことの証明）
                    lock (_lock)
                    {
                        using var conn = new SqliteConnection(_connectionString);
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = @"
                            INSERT OR REPLACE INTO IndexedRoots (RootPath, Status, CoverageComplete, LastCompletedUtcTicks, TotalFiles, ExtractorVersion)
                            VALUES (@root, 'Complete', @cov, @ticks, @total, @extVer);";
                        cmd.Parameters.AddWithValue("@root", normTarget);
                        cmd.Parameters.AddWithValue("@cov", coverage.AccessDeniedFolders == 0 ? 1 : 0);
                        cmd.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
                        cmd.Parameters.AddWithValue("@total", totalDiscovered);
                        cmd.Parameters.AddWithValue("@extVer", CurrentExtractorVersion);
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

                    string commonWhereSql = commonWhereClauses.Count > 0 ? " AND " + string.Join(" AND ", commonWhereClauses) : "";

                    string sql;
                    bool hasTrigramMatch = false;
                    var shortWords = new List<string>();

                    if (keywords.Count == 0)
                    {
                        // キーワードなし（属性検索のみ）: IndexedFiles 単体検索
                        string whereSql = commonWhereClauses.Count > 0 ? string.Join(" AND ", commonWhereClauses) : "1=1";
                        sql = $@"
                            SELECT f.FullPath, f.DirectoryPath, f.SizeBytes, f.LastWriteTimeUtcTicks, '' AS Snippet
                            FROM IndexedFiles f
                            WHERE {whereSql}
                            LIMIT 500";
                    }
                    else
                    {
                        // キーワードあり: 本文 (FTS5) と ファイル名 (LIKE) のハイブリッド検索 (Name OR Content)
                        // A. FTS5 本文検索クエリ
                        var ftsClauses = new List<string>();
                        ftsClauses.Add("f.Status = 1");

                        var trigramWords = new List<string>();
                        foreach (var kw in keywords)
                        {
                            if (string.IsNullOrWhiteSpace(kw)) continue;
                            string trimmed = kw.Trim();
                            if (trimmed.Length >= 3) trigramWords.Add(trimmed);
                            else shortWords.Add(trimmed);
                        }

                        hasTrigramMatch = trigramWords.Count > 0;
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
                            ftsClauses.Add($"(c.Body LIKE {paramName} ESCAPE '\\' OR f.FullPath LIKE {paramName} ESCAPE '\\')");
                            string escVal = "%" + shortWords[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                            cmd.Parameters.AddWithValue(paramName, escVal);
                        }

                        for (int i = 0; i < query.ExcludedWords.Count; i++)
                        {
                            string pName = $"@ftsEx_{i}";
                            ftsClauses.Add($"(f.FullPath NOT LIKE {pName} ESCAPE '\\' AND c.Body NOT LIKE {pName} ESCAPE '\\')");
                            string escEx = "%" + query.ExcludedWords[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                            cmd.Parameters.AddWithValue(pName, escEx);
                        }

                        string ftsWhereSql = string.Join(" AND ", ftsClauses) + commonWhereSql;
                        string snippetExpr = hasTrigramMatch
                            ? "snippet(ContentFts, 1, '【', '】', '...', 15) AS Snippet"
                            : "SUBSTR(c.Body, 1, 120) AS Snippet";

                        // B. ファイル名検索クエリ
                        var nameClauses = new List<string>();
                        // 本文検索明示時 (isExplicitContentSearch == true) は非本文ファイル (.zip, .exe) の混入を防止するため f.Status = 1 のみ
                        // 通常検索時 (isExplicitContentSearch == false) は .zip, .exe を含む全ファイル f.Status >= 0
                        nameClauses.Add(isExplicitContentSearch ? "f.Status = 1" : "f.Status >= 0");

                        for (int i = 0; i < keywords.Count; i++)
                        {
                            string pName = $"@nameKw_{i}";
                            nameClauses.Add($"f.FullPath LIKE {pName} ESCAPE '\\'");
                            string escKw = "%" + keywords[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                            cmd.Parameters.AddWithValue(pName, escKw);
                        }

                        for (int i = 0; i < query.ExcludedWords.Count; i++)
                        {
                            string pName = $"@nameEx_{i}";
                            nameClauses.Add($"f.FullPath NOT LIKE {pName} ESCAPE '\\'");
                            string escEx = "%" + query.ExcludedWords[i].Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                            cmd.Parameters.AddWithValue(pName, escEx);
                        }

                        string nameWhereSql = string.Join(" AND ", nameClauses) + commonWhereSql;

                        if (isExplicitContentSearch)
                        {
                            // 本文も検索ON: 本文 (FTS5) と ファイル名 (LIKE) のハイブリッド検索 (Name OR Content)
                            sql = $@"
                                SELECT f.FullPath, f.DirectoryPath, f.SizeBytes, f.LastWriteTimeUtcTicks, {snippetExpr}
                                FROM ContentFts c
                                JOIN IndexedFiles f ON c.FileId = f.FileId
                                WHERE {ftsWhereSql}
                                UNION
                                SELECT f.FullPath, f.DirectoryPath, f.SizeBytes, f.LastWriteTimeUtcTicks, '' AS Snippet
                                FROM IndexedFiles f
                                WHERE {nameWhereSql}
                                LIMIT 500";
                        }
                        else
                        {
                            // 本文も検索OFF: ファイル名・パス・属性のみの高速検索（FTS結合コストゼロ）
                            sql = $@"
                                SELECT f.FullPath, f.DirectoryPath, f.SizeBytes, f.LastWriteTimeUtcTicks, '' AS Snippet
                                FROM IndexedFiles f
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

                        var item = new SearchResultItem
                        {
                            Name = Path.GetFileName(fullPath),
                            FullPath = fullPath,
                            DirectoryPath = dirPath,
                            SizeBytes = size,
                            LastWriteTime = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime(),
                            Extension = Path.GetExtension(fullPath).ToLowerInvariant(),
                            IsDirectory = false,
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
                    DELETE FROM ContentFts;
                    DELETE FROM IndexedFiles;
                    DELETE FROM IndexedRoots;
                    VACUUM;";
                cmd.ExecuteNonQuery();
            }
        }
    }
}
