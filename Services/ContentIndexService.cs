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
    /// 差分更新、途中中断レジューム、Small-File First、Contentless/Snippetsに対応。
    /// </summary>
    public class ContentIndexService
    {
        private readonly string _dbPath;
        private readonly string _connectionString;
        private readonly object _lock = new();

        public string DbPath => _dbPath;

        public ContentIndexService(string? customDbPath = null)
        {
            if (string.IsNullOrWhiteSpace(customDbPath))
            {
                string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FolderMorpher");
                Directory.CreateDirectory(appData);
                _dbPath = Path.Combine(appData, "ContentIndex.db");
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
                        IndexedAtUtcTicks INTEGER
                    );
                    CREATE INDEX IF NOT EXISTS idx_files_path ON IndexedFiles(FullPath);
                    CREATE INDEX IF NOT EXISTS idx_files_dir ON IndexedFiles(DirectoryPath);

                    CREATE VIRTUAL TABLE IF NOT EXISTS ContentFts USING fts5(
                        FileId UNINDEXED,
                        Body,
                        tokenize = 'trigram'
                    );
                ";
                cmd.ExecuteNonQuery();
            }
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

                progress?.Report(new IndexProgressReport
                {
                    StatusMessage = "メタデータを走査中...",
                    CurrentFile = normTarget,
                    Elapsed = sw.Elapsed
                });

                // 1. 並列2固定でファイル一覧を一括列挙 (I/O最小化)
                var scannedEntries = await SafeFileEnumerator.EnumerateFileEntriesParallelAsync(
                    normTarget,
                    "*.*",
                    coverage: null,
                    onProgress: null,
                    ct: ct);

                // 検索対象の拡張子（Office, PDF, Text）
                var supportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    ".txt", ".log", ".csv", ".tsv", ".json", ".xml", ".html", ".htm", ".md",
                    ".cs", ".sql", ".ps1", ".bat", ".cmd", ".py", ".ini", ".cfg", ".yaml", ".yml",
                    ".xlsx", ".xlsm", ".docx", ".pptx", ".pdf"
                };

                const long MaxIndexFileSize = 50L * 1024 * 1024; // 50MB上限
                var targetEntries = scannedEntries
                    .Where(e => !e.Attributes.HasFlag(FileAttributes.Directory) &&
                                e.Length <= MaxIndexFileSize &&
                                supportedExts.Contains(Path.GetExtension(e.Name)))
                    .ToList();

                // 2. 既存DBのメタデータ状態をロード
                var existingMap = new Dictionary<string, (long FileId, long SizeBytes, long LastWriteTicks, int Status)>(StringComparer.OrdinalIgnoreCase);
                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT FileId, FullPath, SizeBytes, LastWriteTimeUtcTicks, Status FROM IndexedFiles WHERE FullPath LIKE @prefix ESCAPE '\\'";
                    string esc = normTarget.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                    cmd.Parameters.AddWithValue("@prefix", esc);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        long fId = reader.GetInt64(0);
                        string path = reader.GetString(1);
                        long size = reader.GetInt64(2);
                        long ticks = reader.GetInt64(3);
                        int status = reader.GetInt32(4);
                        existingMap[path] = (fId, size, ticks, status);
                    }
                }

                // 3. 差分判定（未変更スキップ、新規・更新・中断分を抽出）
                var toProcess = new List<ScannedFileEntry>();
                int alreadyIndexed = 0;

                foreach (var entry in targetEntries)
                {
                    long currentTicks = entry.LastWriteTime.ToUniversalTime().Ticks;
                    if (existingMap.TryGetValue(entry.FullPath, out var meta))
                    {
                        // 既に Indexed (Status==1) かつ サイズと更新日時が一致していれば完全スキップ (0 I/O)
                        if (meta.Status == 1 && meta.SizeBytes == entry.Length && meta.LastWriteTicks == currentTicks)
                        {
                            alreadyIndexed++;
                            continue;
                        }
                    }
                    toProcess.Add(entry);
                }

                // ★ Sol提唱: 小さいファイル優先（Small-File First）でソート
                toProcess = toProcess.OrderBy(e => e.Length).ToList();

                int totalDiscovered = targetEntries.Count;
                int processedCount = alreadyIndexed;
                int newlyIndexed = 0;
                int failedCount = 0;

                progress?.Report(new IndexProgressReport
                {
                    TotalDiscovered = totalDiscovered,
                    AlreadyIndexed = alreadyIndexed,
                    ProcessedCount = processedCount,
                    StatusMessage = $"インデックス更新開始 (対象: {toProcess.Count:N0} 件 / 変更なしスキップ: {alreadyIndexed:N0} 件)",
                    Elapsed = sw.Elapsed
                });

                // 4. バッチ処理（50ファイルごとにトランザクションコミットしてディスクに永続化）
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
                            string? text = await ExtractTextContentAsync(entry.FullPath, token);
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
                                    SET SizeBytes = @size, LastWriteTimeUtcTicks = @ticks, Status = @status, IndexedAtUtcTicks = @now
                                    WHERE FileId = @fileId";
                                cmdUpd.Parameters.AddWithValue("@size", entry.Length);
                                cmdUpd.Parameters.AddWithValue("@ticks", currentTicks);
                                cmdUpd.Parameters.AddWithValue("@status", item.Success ? 1 : 2);
                                cmdUpd.Parameters.AddWithValue("@now", nowTicks);
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
                                    INSERT INTO IndexedFiles (FullPath, DirectoryPath, SizeBytes, LastWriteTimeUtcTicks, Status, IndexedAtUtcTicks)
                                    VALUES (@path, @dir, @size, @ticks, @status, @now);
                                    SELECT last_insert_rowid();";
                                cmdIns.Parameters.AddWithValue("@path", entry.FullPath);
                                cmdIns.Parameters.AddWithValue("@dir", entry.DirectoryPath);
                                cmdIns.Parameters.AddWithValue("@size", entry.Length);
                                cmdIns.Parameters.AddWithValue("@ticks", currentTicks);
                                cmdIns.Parameters.AddWithValue("@status", item.Success ? 1 : 2);
                                cmdIns.Parameters.AddWithValue("@now", nowTicks);
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
                        FailedCount = failedCount,
                        CurrentFile = batch.LastOrDefault()?.Name ?? string.Empty,
                        StatusMessage = $"インデックス中 ({processedCount:N0} / {totalDiscovered:N0})",
                        Elapsed = sw.Elapsed
                    });
                }

                sw.Stop();
                var finalReport = new IndexProgressReport
                {
                    TotalDiscovered = totalDiscovered,
                    AlreadyIndexed = alreadyIndexed,
                    ProcessedCount = processedCount,
                    NewlyIndexedCount = newlyIndexed,
                    FailedCount = failedCount,
                    StatusMessage = $"インデックス完了 (新規/更新: {newlyIndexed:N0} 件, スキップ: {alreadyIndexed:N0} 件)",
                    Elapsed = sw.Elapsed,
                    IsCompleted = true
                };
                progress?.Report(finalReport);
                return finalReport;
            }, ct);
        }

        /// <summary>
        /// SQLite FTS5 (trigram) を使用したミリ秒全文検索
        /// </summary>
        public async Task<List<SearchResultItem>> SearchIndexedAsync(
            string keyword,
            string? scopeFolder = null,
            CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var results = new List<SearchResultItem>();
                if (string.IsNullOrWhiteSpace(keyword)) return results;

                string sanitized = keyword.Trim().Replace("\"", "\"\"");

                lock (_lock)
                {
                    using var conn = new SqliteConnection(_connectionString);
                    conn.Open();

                    using var cmd = conn.CreateCommand();
                    string sql = @"
                        SELECT f.FullPath, f.DirectoryPath, f.SizeBytes, f.LastWriteTimeUtcTicks,
                               snippet(ContentFts, 1, '【', '】', '...', 15) AS Snippet
                        FROM ContentFts c
                        JOIN IndexedFiles f ON c.FileId = f.FileId
                        WHERE ContentFts MATCH @query";

                    if (!string.IsNullOrWhiteSpace(scopeFolder))
                    {
                        sql += " AND f.FullPath LIKE @scope ESCAPE '\\'";
                        string escScope = scopeFolder.TrimEnd('\\', '/').Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
                        cmd.Parameters.AddWithValue("@scope", escScope);
                    }

                    sql += " LIMIT 500";
                    cmd.CommandText = sql;
                    cmd.Parameters.AddWithValue("@query", $"\"{sanitized}\"");

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        ct.ThrowIfCancellationRequested();
                        string fullPath = reader.GetString(0);
                        string dirPath = reader.GetString(1);
                        long size = reader.GetInt64(2);
                        long ticks = reader.GetInt64(3);
                        string snippet = reader.IsDBNull(4) ? string.Empty : reader.GetString(4);

                        results.Add(new SearchResultItem
                        {
                            Name = Path.GetFileName(fullPath),
                            FullPath = fullPath,
                            DirectoryPath = dirPath,
                            SizeBytes = size,
                            LastWriteTime = new DateTime(ticks, DateTimeKind.Utc).ToLocalTime(),
                            Extension = Path.GetExtension(fullPath).ToLowerInvariant(),
                            IsDirectory = false,
                            ContentSnippet = snippet,
                            MatchedReason = $"Indexed: \"{keyword}\""
                        });
                    }
                }

                return results;
            }, ct);
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
                    VACUUM;";
                cmd.ExecuteNonQuery();
            }
        }

        private static async Task<string?> ExtractTextContentAsync(string filePath, CancellationToken ct)
        {
            string ext = Path.GetExtension(filePath).ToLowerInvariant();

            // 1. Office (OpenXML: .xlsx, .xlsm, .docx, .pptx)
            if (ext == ".xlsx" || ext == ".xlsm" || ext == ".docx" || ext == ".pptx")
            {
                return ExtractOfficeText(filePath);
            }

            // 2. PDF (.pdf)
            if (ext == ".pdf")
            {
                return PdfSearchHelper.ExtractAllText(filePath);
            }

            // 3. Text (.txt, .csv, .log, .json, etc.) - UTF-16対応
            return await ExtractPlainTextAsync(filePath, ct);
        }

        private static string? ExtractOfficeText(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Read, false);
                var sb = new StringBuilder();

                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName.ToLowerInvariant();
                    if (!name.EndsWith(".xml")) continue;
                    if (!name.Contains("sharedstrings") &&
                        !name.Contains("sheet") &&
                        !name.Contains("document") &&
                        !name.Contains("slide")) continue;

                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string xml = reader.ReadToEnd();
                    string clean = Regex.Replace(xml, @"<[^>]+>", " ");
                    clean = Regex.Replace(clean, @"\s+", " ");
                    sb.Append(clean).Append(' ');

                    if (sb.Length > 500_000) break; // 巨大テキストの過大インデックス防止上限
                }
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string?> ExtractPlainTextAsync(string filePath, CancellationToken ct)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);

                // BOM チェック & UTF-16判定
                byte[] head = new byte[4096];
                int read = await fs.ReadAsync(head, 0, head.Length, ct);
                if (read > 0)
                {
                    bool isUtf16Le = (read >= 2 && head[0] == 0xFF && head[1] == 0xFE);
                    bool isUtf16Be = (read >= 2 && head[0] == 0xFE && head[1] == 0xFF);

                    if (!isUtf16Le && !isUtf16Be)
                    {
                        int nulls = 0;
                        for (int i = 0; i < read; i++) if (head[i] == 0) nulls++;
                        if (nulls >= 2) return null; // バイナリ早期脱落
                    }
                    fs.Seek(0, SeekOrigin.Begin);
                }

                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                char[] buffer = new char[64 * 1024];
                var sb = new StringBuilder();
                int charsRead;
                while ((charsRead = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    sb.Append(buffer, 0, charsRead);
                    if (sb.Length > 500_000) break;
                }
                return sb.ToString();
            }
            catch
            {
                return null;
            }
        }
    }
}
