using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace FolderMorpher.Services.Testing
{
    /// <summary>
    /// 【次世代検索アーキテクチャ検証ベンチマーク】
    /// 方式A: 現行 trigram (detail=full)
    /// 方式B: 軽量 trigram (detail=none + 3文字分解クエリ + Aho-Corasick Verify)
    /// 方式C: 極小 contentless trigram (content='', detail=none + 3文字分解クエリ + Aho-Corasick Verify)
    /// </summary>
    public static class TrigramBenchmark
    {
        public static async Task RunAsync()
        {
            await Task.Yield();
            Console.WriteLine("================================================================================");
            Console.WriteLine(" [BENCHMARK] Next-Gen Search Architecture: Trigram Modes Comparison");
            Console.WriteLine("================================================================================");

            // SQLite バージョン & contentless_delete=1 診断
            using (var testConn = new SqliteConnection("Data Source=:memory:;"))
            {
                testConn.Open();
                using var verCmd = testConn.CreateCommand();
                verCmd.CommandText = "SELECT sqlite_version();";
                string ver = Convert.ToString(verCmd.ExecuteScalar()) ?? "unknown";
                Console.WriteLine($"[0/4] Bundled SQLite Version: {ver}");

                // contentless_delete=1 & 2-char token behavior check
                try
                {
                    using var createCmd = testConn.CreateCommand();
                    createCmd.CommandText = "CREATE VIRTUAL TABLE TestContentless USING fts5(Body, tokenize = 'trigram', content = '', contentless_delete = 1, detail = 'none');";
                    createCmd.ExecuteNonQuery();

                    using var insCmd = testConn.CreateCommand();
                    insCmd.CommandText = "INSERT INTO TestContentless (rowid, Body) VALUES (1, '契約書の文章です。');";
                    insCmd.ExecuteNonQuery();

                    // 2文字 MATCH テスト
                    try
                    {
                        using var match2Cmd = testConn.CreateCommand();
                        match2Cmd.CommandText = "SELECT rowid FROM TestContentless WHERE TestContentless MATCH '\"契約\"';";
                        using var reader = match2Cmd.ExecuteReader();
                        bool hasHit = reader.Read();
                        Console.WriteLine($"  --> MATCH 2-char ('契約'): {(hasHit ? "HIT!" : "No hit")}");
                    }
                    catch (Exception exMatch2)
                    {
                        Console.WriteLine($"  --> MATCH 2-char ('契約') ERROR: {exMatch2.Message}");
                    }

                    // 1文字 MATCH テスト
                    try
                    {
                        using var match1Cmd = testConn.CreateCommand();
                        match1Cmd.CommandText = "SELECT rowid FROM TestContentless WHERE TestContentless MATCH '\"契\"';";
                        using var reader = match1Cmd.ExecuteReader();
                        bool hasHit = reader.Read();
                        Console.WriteLine($"  --> MATCH 1-char ('契'): {(hasHit ? "HIT!" : "No hit")}");
                    }
                    catch (Exception exMatch1)
                    {
                        Console.WriteLine($"  --> MATCH 1-char ('契') ERROR: {exMatch1.Message}");
                    }

                    using var delCmd = testConn.CreateCommand();
                    delCmd.CommandText = "DELETE FROM TestContentless WHERE rowid = 1;";
                    delCmd.ExecuteNonQuery();

                    Console.WriteLine("  --> contentless_delete=1: SUPPORTED & FUNCTIONAL!");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  --> Benchmark init error: {ex.Message}");
                }
            }

            string userDbPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FolderMorpher",
                "ContentIndex.db");

            var sampleTexts = new List<(long Id, string Body)>();

            if (File.Exists(userDbPath))
            {
                Console.WriteLine($"\n[1/4] Reading sample texts from real DB: {userDbPath}");
                try
                {
                    using var conn = new SqliteConnection($"Data Source={userDbPath};Mode=ReadOnly;");
                    conn.Open();

                    using (var diagCmd = conn.CreateCommand())
                    {
                        diagCmd.CommandText = @"
                            SELECT 
                                (SELECT count(*) FROM sqlite_master WHERE type='table' AND name='IndexedFiles') AS HasIndexedFiles,
                                (SELECT count(*) FROM sqlite_master WHERE type='table' AND name='ContentFts') AS HasContentFts;";
                        using var reader = diagCmd.ExecuteReader();
                        if (reader.Read())
                        {
                            Console.WriteLine($"  --> DB Tables: IndexedFiles={reader.GetInt32(0)}, ContentFts={reader.GetInt32(1)}");
                        }
                    }

                    long countFts = 0;
                    try
                    {
                        using var countCmd = conn.CreateCommand();
                        countCmd.CommandText = "SELECT count(*) FROM ContentFts;";
                        countFts = Convert.ToInt64(countCmd.ExecuteScalar());
                        Console.WriteLine($"  --> ContentFts total row count: {countFts:N0}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  --> Error reading ContentFts count: {ex.Message}");
                    }

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT rowid, Body FROM ContentFts LIMIT 10000;";
                    using var reader2 = cmd.ExecuteReader();
                    while (reader2.Read())
                    {
                        long id = reader2.GetInt64(0);
                        string body = reader2.IsDBNull(1) ? string.Empty : reader2.GetString(1);
                        if (!string.IsNullOrWhiteSpace(body) && body.Length > 20)
                        {
                            sampleTexts.Add((id, body));
                        }
                    }
                    Console.WriteLine($"  --> Successfully loaded {sampleTexts.Count:N0} real sample documents.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  --> Could not read real DB: {ex.Message}. Falling back to synthetic texts.");
                }
            }

            if (sampleTexts.Count < 1000)
            {
                Console.WriteLine("\n[1/4] Generating synthetic business documents...");
                var random = new Random(42);
                string[] nouns = { "契約書", "秘密保持", "仕様書", "プロジェクト", "予算案", "議事録", "更新手続", "売上計画", "システム障害", "移行設計" };
                string[] prefixes = { "株式会社", "有限会社", "合同会社", "東京支社", "大阪営業所", "開発本部" };
                string[] codes = { "XQZ-9281", "REV-2026", "PRJ-PHOENIX", "SEC-ALPHA", "DOC-99812", "NAS-STORAGE" };

                for (int i = 0; i < 10000; i++)
                {
                    var sb = new StringBuilder();
                    sb.Append($"{prefixes[random.Next(prefixes.Length)]}における{nouns[random.Next(nouns.Length)]}に関する報告書。");
                    sb.Append($"管理コード: {codes[random.Next(codes.Length)]}。");
                    sb.Append($"2026年度の進捗状況および{nouns[random.Next(nouns.Length)]}について合意した。");
                    for (int j = 0; j < 5; j++)
                    {
                        string fillerNoun = nouns[random.Next(nouns.Length)];
                        sb.Append($"本条項に基づき第{j + 1}条の{fillerNoun}の取り決めを遵守すること。");
                    }
                    if (i % 7 == 0) sb.Append(" 添付書類: 秘密保持誓約書原本および印鑑証明書。");
                    if (i % 11 == 0) sb.Append(" 担当者連絡先: dev-team@internal.example.co.jp 内線: 9912。");
                    if (i % 13 == 0) sb.Append(" 留意事項: システム障害発生時は直ちにネットワークを遮断しバックアップを確認すること。");
                    sampleTexts.Add((i + 1, sb.ToString()));
                }

                // Sol指摘の False Positive 検証用ドキュメント:
                // 1. ABC と BCD が離れて存在（ABCD という連続語はない）
                sampleTexts.Add((9999901, "この文書には ABC と途中に長いダミー文字列があって BCD が存在します。"));
                // 2. ABCD が実際に連続して存在
                sampleTexts.Add((9999902, "この文書には本物の ABCD が正確に含まれています。"));

                Console.WriteLine($"  --> Generated {sampleTexts.Count:N0} synthetic documents.");
            }

            string workDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_Benchmark_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workDir);

            try
            {
                string dbPathA = Path.Combine(workDir, "ModeA_Full.db");
                string dbPathB = Path.Combine(workDir, "ModeB_DetailNone.db");
                string dbPathC = Path.Combine(workDir, "ModeC_Contentless.db");

                Console.WriteLine("\n[2/4] Building Indexes for Mode A, Mode B, Mode C...");

                // Mode A: detail=full (現行)
                var swA = Stopwatch.StartNew();
                BuildDatabase(dbPathA, "CREATE VIRTUAL TABLE ContentFts USING fts5(Body, tokenize = 'trigram');", sampleTexts);
                swA.Stop();
                long sizeA = GetTotalDbSize(dbPathA);
                Console.WriteLine($"  * Mode A (detail=full) built in {swA.ElapsedMilliseconds:N0} ms, Total Size: {sizeA / 1024.0 / 1024.0:F2} MB");

                // Mode B: detail=none (軽量転置)
                var swB = Stopwatch.StartNew();
                BuildDatabase(dbPathB, "CREATE VIRTUAL TABLE ContentFts USING fts5(Body, tokenize = 'trigram', detail = 'none');", sampleTexts);
                swB.Stop();
                long sizeB = GetTotalDbSize(dbPathB);
                Console.WriteLine($"  * Mode B (detail=none) built in {swB.ElapsedMilliseconds:N0} ms, Total Size: {sizeB / 1024.0 / 1024.0:F2} MB (削減率: {(1.0 - (double)sizeB / sizeA) * 100:F1}%)");

                // Mode C: content='', detail=none (極小contentless)
                var swC = Stopwatch.StartNew();
                BuildDatabase(dbPathC, "CREATE VIRTUAL TABLE ContentFts USING fts5(Body, tokenize = 'trigram', content = '', detail = 'none');", sampleTexts);
                swC.Stop();
                long sizeC = GetTotalDbSize(dbPathC);
                Console.WriteLine($"  * Mode C (contentless + detail=none) built in {swC.ElapsedMilliseconds:N0} ms, Total Size: {sizeC / 1024.0 / 1024.0:F2} MB (削減率: {(1.0 - (double)sizeC / sizeA) * 100:F1}%)");

                // テーブル詳細サイズ計測
                Console.WriteLine("\n[3/4] Detailed Table Row Breakdown:");
                PrintTableSizes("Mode A (detail=full)", dbPathA);
                PrintTableSizes("Mode B (detail=none)", dbPathB);
                PrintTableSizes("Mode C (contentless)", dbPathC);

                // 検索パフォーマンステスト
                Console.WriteLine("\n[4/4] Search & Candidate Reduction Performance Benchmark:");

                var testQueries = new[]
                {
                    ("3文字単語", "契約書"),
                    ("4文字単語", "秘密保持"),
                    ("5文字複合語", "プロジェクト"),
                    ("英数字・型番", "XQZ-9281"),
                    ("希小フレーズ", "留意事項: システム障害"),
                    ("FP除外検証 (ABCD)", "ABCD"),
                    ("0件（存在しない語）", "絶対存在しない謎の単語99999")
                };

                Console.WriteLine($"{"Query Type",-20} | {"Query",-24} | {"Mode A (Hit/Time)",-20} | {"Mode B (Cand/Hit/Time)",-24} | {"Mode C (Cand/Hit/Time)",-24}");
                Console.WriteLine(new string('-', 125));

                foreach (var (label, q) in testQueries)
                {
                    // Mode A: 現行 MATCH
                    var (hitsA, timeA) = QueryModeA(dbPathA, q);

                    // Mode B: 3文字分解 MATCH + Aho-Corasick Verify
                    var (candsB, hitsB, timeB) = QueryModeB(dbPathB, q, isContentless: false, sampleTexts);

                    // Mode C: 3文字分解 MATCH + 原本(sampleTexts) Aho-Corasick Verify
                    var (candsC, hitsC, timeC) = QueryModeB(dbPathC, q, isContentless: true, sampleTexts);

                    string resA = $"{hitsA:N0} hits / {timeA:F2}ms";
                    string resB = $"{candsB:N0} -> {hitsB:N0} / {timeB:F2}ms";
                    string resC = $"{candsC:N0} -> {hitsC:N0} / {timeC:F2}ms";
                    Console.WriteLine($"{label,-20} | {q,-24} | {resA,-20} | {resB,-24} | {resC,-24}");
                }

                Console.WriteLine("\n================================================================================");
                Console.WriteLine(" [BENCHMARK COMPLETED]");
                Console.WriteLine("================================================================================");
            }
            finally
            {
                SqliteConnection.ClearAllPools();
                try { Directory.Delete(workDir, recursive: true); } catch { }
            }
        }

        private static long GetTotalDbSize(string dbPath)
        {
            long total = 0;
            if (File.Exists(dbPath)) total += new FileInfo(dbPath).Length;
            if (File.Exists(dbPath + "-wal")) total += new FileInfo(dbPath + "-wal").Length;
            if (File.Exists(dbPath + "-shm")) total += new FileInfo(dbPath + "-shm").Length;
            return total;
        }

        private static void BuildDatabase(string dbPath, string createTableSql, List<(long Id, string Body)> data)
        {
            using (var conn = new SqliteConnection($"Data Source={dbPath};"))
            {
                conn.Open();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
                    cmd.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = createTableSql;
                    cmd.ExecuteNonQuery();
                }

                using (var trans = conn.BeginTransaction())
                {
                    using var insCmd = conn.CreateCommand();
                    insCmd.Transaction = trans;
                    insCmd.CommandText = "INSERT INTO ContentFts (rowid, Body) VALUES (@id, @body);";
                    var pId = insCmd.Parameters.Add("@id", SqliteType.Integer);
                    var pBody = insCmd.Parameters.Add("@body", SqliteType.Text);

                    foreach (var (id, body) in data)
                    {
                        pId.Value = id;
                        pBody.Value = body;
                        insCmd.ExecuteNonQuery();
                    }
                    trans.Commit();
                }

                using (var chkCmd = conn.CreateCommand())
                {
                    chkCmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    chkCmd.ExecuteNonQuery();
                }
                conn.Close();
            }
            SqliteConnection.ClearAllPools();
        }

        private static void PrintTableSizes(string label, string dbPath)
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
            conn.Open();
            Console.WriteLine($"  --- {label} ---");
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                SELECT name FROM sqlite_master WHERE type='table' AND name LIKE 'ContentFts%';";
            var tableNames = new List<string>();
            using (var reader = cmd.ExecuteReader())
            {
                while (reader.Read()) tableNames.Add(reader.GetString(0));
            }

            foreach (var tbl in tableNames)
            {
                using var cntCmd = conn.CreateCommand();
                cntCmd.CommandText = $"SELECT count(*) FROM \"{tbl}\";";
                long count = 0;
                try { count = Convert.ToInt64(cntCmd.ExecuteScalar()); } catch { }
                Console.WriteLine($"      - {tbl,-25}: {count:N0} rows");
            }
        }

        private static (int Hits, double TimeMs) QueryModeA(string dbPath, string query)
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT rowid FROM ContentFts WHERE ContentFts MATCH @match;";
            cmd.Parameters.AddWithValue("@match", $"\"{query.Replace("\"", "\"\"")}\"");

            var sw = Stopwatch.StartNew();
            int count = 0;
            try
            {
                using var reader = cmd.ExecuteReader();
                while (reader.Read()) count++;
            }
            catch (Exception ex)
            {
                sw.Stop();
                Console.WriteLine($"      [Mode A Error: {ex.Message}]");
                return (-1, sw.Elapsed.TotalMilliseconds);
            }
            sw.Stop();
            return (count, sw.Elapsed.TotalMilliseconds);
        }

        private static (int Cands, int Hits, double TimeMs) QueryModeB(
            string dbPath,
            string query,
            bool isContentless,
            List<(long Id, string Body)> sampleTexts)
        {
            using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly;");
            conn.Open();

            var sw = Stopwatch.StartNew();

            // 1. 3文字分解クエリの構築
            // 例: "秘密保持" -> "秘密保" AND "密保持"
            string matchExpr = BuildTrigramAndQuery(query);

            var candidateIds = new List<long>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT rowid FROM ContentFts WHERE ContentFts MATCH @match;";
                cmd.Parameters.AddWithValue("@match", matchExpr);

                try
                {
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read()) candidateIds.Add(reader.GetInt64(0));
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    Console.WriteLine($"      [Mode B/C Match Error: {ex.Message}]");
                    return (-1, -1, sw.Elapsed.TotalMilliseconds);
                }
            }

            int candCount = candidateIds.Count;
            if (candCount == 0)
            {
                sw.Stop();
                return (0, 0, sw.Elapsed.TotalMilliseconds);
            }

            // 2. Aho-Corasick Verify
            var aho = new AhoCorasickSearcher(new[] { query });
            int verifiedHits = 0;

            if (!isContentless)
            {
                // Mode B: DBのBody列から取得してVerify
                string idList = string.Join(",", candidateIds);
                using var cmdBody = conn.CreateCommand();
                cmdBody.CommandText = $"SELECT rowid, Body FROM ContentFts WHERE rowid IN ({idList});";
                using var reader = cmdBody.ExecuteReader();
                while (reader.Read())
                {
                    string body = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                    if (aho.ContainsAny(body))
                    {
                        verifiedHits++;
                    }
                }
            }
            else
            {
                // Mode C: 原本(sampleTexts)から直接Verify (UNC想定)
                var sampleDict = sampleTexts.ToDictionary(s => s.Id, s => s.Body);
                foreach (var id in candidateIds)
                {
                    if (sampleDict.TryGetValue(id, out var body) && aho.ContainsAny(body))
                    {
                        verifiedHits++;
                    }
                }
            }

            sw.Stop();
            return (candCount, verifiedHits, sw.Elapsed.TotalMilliseconds);
        }

        /// <summary>
        /// 検索語を 3文字 trigram の AND 式へ分解する。
        /// 3文字未満の場合は単一引用符、3文字以上の場合は全連続 trigram の AND。
        /// </summary>
        private static string BuildTrigramAndQuery(string query)
        {
            if (string.IsNullOrEmpty(query)) return "\"\"";
            if (query.Length <= 3)
            {
                return $"\"{query.Replace("\"", "\"\"")}\"";
            }

            var trigrams = new List<string>();
            for (int i = 0; i <= query.Length - 3; i++)
            {
                string tri = query.Substring(i, 3);
                trigrams.Add($"\"{tri.Replace("\"", "\"\"")}\"");
            }

            return string.Join(" AND ", trigrams);
        }
    }
}
