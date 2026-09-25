using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderMorpher.Services
{
    /// <summary>
    /// ファイル（Office OpenXML, PDF, プレーンテキスト）からの本文テキスト抽出およびストリーム高速検索を行う共通サービスクラス（正本の単一性）。
    /// </summary>
    public static class ContentExtractionService
    {
        public const int MaxCharsPerDocument = 500_000; // 1ドキュメントあたりの最大インデックス文字数 (約1MB)
        public const int DefaultSnippetRadius = 60;     // 前後スニペット文字数

        public static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".log", ".csv", ".tsv", ".json", ".xml", ".html", ".htm", ".md",
            ".cs", ".sql", ".ps1", ".bat", ".cmd", ".py", ".ini", ".cfg", ".yaml", ".yml",
            ".xlsx", ".xlsm", ".docx", ".pptx", ".pdf"
        };

        private static readonly Encoding SjisEncoding;

        static ContentExtractionService()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                SjisEncoding = Encoding.GetEncoding(932); // Shift-JIS (CP932)
            }
            catch
            {
                SjisEncoding = Encoding.UTF8;
            }
        }

        /// <summary>
        /// 指定ファイルが本文抽出に対応しているか判定
        /// </summary>
        public static bool IsSupported(string filePath)
        {
            if (string.IsNullOrEmpty(filePath)) return false;
            string ext = Path.GetExtension(filePath);
            return SupportedExtensions.Contains(ext);
        }

        /// <summary>
        /// ファイルから本文テキストを非同期で抽出します（Index登録用）。
        /// </summary>
        public static async Task<string?> ExtractTextAsync(string filePath, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return null;

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

            // 3. プレーンテキスト (.txt, .csv, .log, .json 等 - UTF-8 / UTF-16 / Shift-JIS自動判別)
            return await ExtractPlainTextAsync(filePath, ct);
        }

        /// <summary>
        /// OpenXML形式のOfficeドキュメント（Excel, Word, PowerPoint）から本文テキストを抽出します。
        /// 【Agent Ransack流高速化】64KBバッファ、SequentialScan、および正規表現フリーのゼロアロケーションタグ除去
        /// </summary>
        public static string? ExtractOfficeText(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, false);
                var sb = new StringBuilder();

                foreach (var entry in zip.Entries)
                {
                    string name = entry.FullName.ToLowerInvariant();
                    if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;

                    // 対象となるXMLパーツのみに絞り込む（Excel: sharedStrings, sheet / Word: document / PPT: slide）
                    if (!name.Contains("sharedstrings") &&
                        !name.Contains("sheet") &&
                        !name.Contains("document") &&
                        !name.Contains("slide"))
                    {
                        continue;
                    }

                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536);
                    string xml = reader.ReadToEnd();
                    string clean = StripXmlTagsFast(xml);
                    if (!string.IsNullOrWhiteSpace(clean))
                    {
                        sb.Append(clean).Append(' ');
                    }

                    if (sb.Length >= MaxCharsPerDocument)
                    {
                        sb.Length = MaxCharsPerDocument;
                        break;
                    }
                }

                return sb.Length > 0 ? sb.ToString().Trim() : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 【Agent Ransack流】XML文字列からタグ（<... >）を除去し、テキストノードの内容のみを高速抽出（正規表現ゼロ・1パス処理）
        /// </summary>
        public static string StripXmlTagsFast(string xml)
        {
            if (string.IsNullOrEmpty(xml)) return string.Empty;
            var sb = new StringBuilder(Math.Min(xml.Length, 16384));
            bool insideTag = false;
            bool lastWasSpace = false;

            for (int i = 0; i < xml.Length; i++)
            {
                char c = xml[i];
                if (c == '<')
                {
                    insideTag = true;
                    if (!lastWasSpace)
                    {
                        sb.Append(' ');
                        lastWasSpace = true;
                    }
                }
                else if (c == '>')
                {
                    insideTag = false;
                }
                else if (!insideTag)
                {
                    if (char.IsWhiteSpace(c))
                    {
                        if (!lastWasSpace)
                        {
                            sb.Append(' ');
                            lastWasSpace = true;
                        }
                    }
                    else
                    {
                        sb.Append(c);
                        lastWasSpace = false;
                    }
                }
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// ストリームの先頭データからエンコーディング（BOM付き/なし UTF-16, UTF-8, Shift-JIS/CP932）を自動判定します。
        /// </summary>
        public static Encoding DetectTextEncoding(Stream stream)
        {
            long origPos = stream.Position;
            try
            {
                byte[] head = new byte[Math.Min(4096, (int)(stream.Length - origPos))];
                int bytesRead = stream.Read(head, 0, head.Length);
                stream.Position = origPos;

                if (bytesRead >= 2)
                {
                    if (head[0] == 0xFF && head[1] == 0xFE) return Encoding.Unicode;
                    if (head[0] == 0xFE && head[1] == 0xFF) return Encoding.BigEndianUnicode;
                }
                if (bytesRead >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
                {
                    return Encoding.UTF8;
                }

                // BOMなし UTF-16LE ヒューリスティック
                int nullCount = 0;
                for (int i = 1; i < bytesRead; i += 2)
                {
                    if (head[i] == 0x00) nullCount++;
                }
                if (bytesRead >= 8 && nullCount > (bytesRead / 4))
                {
                    return Encoding.Unicode;
                }

                // UTF-8 Strict 判定（不正シーケンス検知で CP932 へフォールバック）
                try
                {
                    var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
                    strictUtf8.GetString(head, 0, bytesRead);
                    return Encoding.UTF8;
                }
                catch (DecoderFallbackException)
                {
                    return SjisEncoding;
                }
            }
            finally
            {
                stream.Position = origPos;
            }
        }

        /// <summary>
        /// プレーンテキストファイルからエンコーディング自動判別（UTF-8, UTF-16LE/BE, Shift-JIS/CP932）で本文を抽出します。
        /// </summary>
        public static async Task<string?> ExtractPlainTextAsync(string filePath, CancellationToken ct = default)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
                if (fs.Length == 0) return null;

                Encoding encoding = DetectTextEncoding(fs);
                return await ReadStreamWithEncodingAsync(fs, encoding, ct);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<string?> ReadStreamWithEncodingAsync(FileStream fs, Encoding encoding, CancellationToken ct)
        {
            using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 65536, leaveOpen: true);
            char[] buffer = new char[Math.Min(MaxCharsPerDocument, (int)Math.Min(fs.Length, MaxCharsPerDocument))];
            int charsRead = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), ct);
            return charsRead > 0 ? new string(buffer, 0, charsRead) : null;
        }

        #region Live Stream Search for FileTree Scan (Zero-Temp Diskless)

        /// <summary>
        /// テキストファイルをストリーム走査し、キーワード群の包含判定とスニペット抽出を高速実行（Early Exit対応）。
        /// Shift-JIS (CP932) および UTF-8/UTF-16 に完全対応。
        /// </summary>
        public static async Task<string?> SearchTextContentAsync(string filePath, IReadOnlyList<string> keywords, CancellationToken ct)
        {
            var groups = keywords.Select(k => new List<string> { k }).ToList();
            return await SearchTextContentAsync(filePath, groups, ct);
        }

        /// <summary>
        /// テキストファイルをストリーム走査し、ORグループ群（各グループ内のいずれかに一致、全グループを満たす）の包含判定とスニペット抽出を高速実行。
        /// </summary>
        public static async Task<string?> SearchTextContentAsync(string filePath, IReadOnlyList<List<string>> requiredGroups, CancellationToken ct)
        {
            if (requiredGroups.Count == 0) return null;

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan | FileOptions.Asynchronous);
                if (fs.Length == 0) return null;

                // 先頭バイトでバイナリ早期脱落判定
                byte[] head = new byte[Math.Min(1024, (int)fs.Length)];
                int bytesRead = await fs.ReadAsync(head.AsMemory(0, head.Length), ct);
                fs.Position = 0;

                int nulls = 0;
                for (int i = 0; i < bytesRead; i++) if (head[i] == 0) nulls++;
                bool isUtf16 = (bytesRead >= 2 && ((head[0] == 0xFF && head[1] == 0xFE) || (head[0] == 0xFE && head[1] == 0xFF)));
                if (!isUtf16 && nulls >= 2) return null; // 純粋なバイナリは即座に脱落

                Encoding encoding = DetectTextEncoding(fs);

                var patterns = new List<string>();
                var patternToGroup = new List<int>();
                for (int g = 0; g < requiredGroups.Count; g++)
                {
                    foreach (var kw in requiredGroups[g])
                    {
                        if (!string.IsNullOrWhiteSpace(kw))
                        {
                            patterns.Add(kw);
                            patternToGroup.Add(g);
                        }
                    }
                }
                if (patterns.Count == 0) return null;

                var ac = new AhoCorasickSearcher(patterns, ignoreCase: true);

                // 行単位ストリーム走査（Aho-Corasick ワンパス判定 ＆ Early Exit）
                using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true, bufferSize: 65536);
                var satisfiedGroups = new HashSet<int>();
                string? firstSnippet = null;

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    var matches = ac.FindMatchedIndices(line);
                    foreach (var pIdx in matches)
                    {
                        int gIdx = patternToGroup[pIdx];
                        satisfiedGroups.Add(gIdx);
                        if (firstSnippet == null)
                        {
                            string p = ac.Patterns[pIdx];
                            int matchIdx = line.IndexOf(p, StringComparison.OrdinalIgnoreCase);
                            if (matchIdx >= 0)
                            {
                                firstSnippet = ExtractSnippet(line, matchIdx, p.Length);
                            }
                        }
                    }

                    if (satisfiedGroups.Count == requiredGroups.Count)
                    {
                        return firstSnippet ?? (requiredGroups[0].Count > 0 ? requiredGroups[0][0] : string.Empty);
                    }
                }
            }
            catch { }
            return null;
        }

        /// <summary>
        /// 【Large File Pipeline - ADR 90】50MB超の巨大ファイル（ログ、CSV、ダンプ等）に対して、
        /// 先頭・末尾・中間（複数ポイント）をスポット検査（分散Probe）し、早期発見・早期判定を行います。
        /// 一致が確認できた場合は、巨大ファイル全体を読み切ることなく数ミリ秒で即座にスニペットを返却します。
        /// </summary>
        public static async Task<string?> ProbeLargeFileContentAsync(
            string filePath,
            long fileSize,
            IReadOnlyList<List<string>> requiredGroups,
            CancellationToken ct)
        {
            if (requiredGroups.Count == 0 || fileSize == 0) return null;

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.RandomAccess | FileOptions.Asynchronous);

                // エンコーディングの自動判別（先頭ブロックから）
                Encoding encoding = DetectTextEncoding(fs);

                var patterns = new List<string>();
                var patternToGroup = new List<int>();
                for (int g = 0; g < requiredGroups.Count; g++)
                {
                    foreach (var kw in requiredGroups[g])
                    {
                        if (!string.IsNullOrWhiteSpace(kw))
                        {
                            patterns.Add(kw);
                            patternToGroup.Add(g);
                        }
                    }
                }
                if (patterns.Count == 0) return null;

                var ac = new AhoCorasickSearcher(patterns, ignoreCase: true);

                // Probe ウィンドウ（先頭256KB、末尾256KB、1/4地点128KB、2/4地点128KB、3/4地点128KB）
                var probeOffsets = new List<(long offset, int length)>();

                const int WindowSizeHeadTail = 256 * 1024;
                const int WindowSizeMid = 128 * 1024;

                // 1. 先頭
                probeOffsets.Add((0, (int)Math.Min(fileSize, WindowSizeHeadTail)));

                // 2. 末尾
                if (fileSize > WindowSizeHeadTail * 2)
                {
                    long tailOffset = Math.Max(0, fileSize - WindowSizeHeadTail);
                    probeOffsets.Add((tailOffset, (int)Math.Min(fileSize - tailOffset, WindowSizeHeadTail)));
                }

                // 3. 中間 25%, 50%, 75%
                if (fileSize > WindowSizeHeadTail * 4)
                {
                    long p25 = fileSize / 4;
                    long p50 = fileSize / 2;
                    long p75 = (fileSize * 3) / 4;

                    probeOffsets.Add((p25, (int)Math.Min(fileSize - p25, WindowSizeMid)));
                    probeOffsets.Add((p50, (int)Math.Min(fileSize - p50, WindowSizeMid)));
                    probeOffsets.Add((p75, (int)Math.Min(fileSize - p75, WindowSizeMid)));
                }

                var satisfiedGroups = new HashSet<int>();
                string? firstSnippet = null;

                foreach (var (offset, len) in probeOffsets)
                {
                    ct.ThrowIfCancellationRequested();
                    fs.Position = offset;
                    byte[] buffer = new byte[len];
                    int bytesRead = await fs.ReadAsync(buffer.AsMemory(0, len), ct);
                    if (bytesRead <= 0) continue;

                    string chunk = encoding.GetString(buffer, 0, bytesRead);
                    var matches = ac.FindMatchedIndices(chunk);
                    foreach (var pIdx in matches)
                    {
                        int gIdx = patternToGroup[pIdx];
                        satisfiedGroups.Add(gIdx);
                        if (firstSnippet == null)
                        {
                            string p = ac.Patterns[pIdx];
                            int hitPos = chunk.IndexOf(p, StringComparison.OrdinalIgnoreCase);
                            if (hitPos >= 0)
                            {
                                int sStart = Math.Max(0, hitPos - DefaultSnippetRadius);
                                int sLen = Math.Min(chunk.Length - sStart, p.Length + DefaultSnippetRadius * 2);
                                firstSnippet = chunk.Substring(sStart, sLen).Replace('\r', ' ').Replace('\n', ' ');
                            }
                        }
                    }

                    // 全ての必須グループがProbeウィンドウ内で充足されたら即時合格！
                    if (satisfiedGroups.Count == requiredGroups.Count)
                    {
                        return firstSnippet ?? "Match in Probe Window";
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Officeファイル（Excel/Word/PowerPoint）をストリーム走査し、キーワード群の包含判定を高速実行（Early Exit対応）。
        /// </summary>
        public static bool SearchOfficeContent(string filePath, IReadOnlyList<string> keywords, out string snippet)
        {
            var groups = keywords.Select(k => new List<string> { k }).ToList();
            return SearchOfficeContent(filePath, groups, out snippet);
        }

        /// <summary>
        /// Officeファイル（Excel/Word/PowerPoint）をストリーム走査し、ORグループ群の包含判定を高速実行（Early Exit対応）。
        /// </summary>
        public static bool SearchOfficeContent(string filePath, IReadOnlyList<List<string>> requiredGroups, out string snippet)
        {
            snippet = string.Empty;
            if (requiredGroups.Count == 0) return false;

            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, FileOptions.SequentialScan);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, false);

                // Excel (.xlsx / .xlsm) の場合は sharedStrings.xml を最優先・ピンポイント探索
                if (ext == ".xlsx" || ext == ".xlsm")
                {
                    ZipArchiveEntry? sharedEntry = zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("sharedstrings.xml", StringComparison.OrdinalIgnoreCase));
                    if (sharedEntry != null)
                    {
                        using var stream = sharedEntry.Open();
                        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536);
                        string text = reader.ReadToEnd();

                        var sharedSatisfied = new HashSet<int>();
                        string firstSnippet = string.Empty;

                        for (int g = 0; g < requiredGroups.Count; g++)
                        {
                            foreach (var kw in requiredGroups[g])
                            {
                                int idx = text.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                                if (idx >= 0)
                                {
                                    sharedSatisfied.Add(g);
                                    if (string.IsNullOrEmpty(firstSnippet))
                                    {
                                        firstSnippet = ExtractSnippet(text, idx, kw.Length);
                                    }
                                    break;
                                }
                            }
                        }

                        if (sharedSatisfied.Count == requiredGroups.Count)
                        {
                            snippet = firstSnippet;
                            return true; // sharedStrings で全グループが揃ったので Early exit!
                        }
                    }
                }

                // Word (.docx), PowerPoint (.pptx), または sharedStrings だけでは見つからなかった Excel の探索
                var foundGroups = new HashSet<int>();
                string firstFoundSnippet = string.Empty;

                foreach (var entry in zip.Entries)
                {
                    string entryName = entry.FullName.ToLowerInvariant();
                    if (!entryName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !entryName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!entryName.Contains("sharedstrings") &&
                        !entryName.Contains("sheet") &&
                        !entryName.Contains("document") &&
                        !entryName.Contains("slide") &&
                        !entryName.Contains("comment") &&
                        !entryName.Contains("footnote"))
                    {
                        continue;
                    }

                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 65536);
                    string xml = reader.ReadToEnd();

                    // 【Agent Ransack流 JIT Pre-Filter】
                    // 生XMLに未充足グループのキーワードが1つも含まれていなければタグ除去すらスキップ（超高速脱落）
                    bool hasAnyCandidate = false;
                    for (int g = 0; g < requiredGroups.Count; g++)
                    {
                        if (foundGroups.Contains(g)) continue;
                        foreach (var kw in requiredGroups[g])
                        {
                            if (xml.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                hasAnyCandidate = true;
                                break;
                            }
                        }
                        if (hasAnyCandidate) break;
                    }
                    if (!hasAnyCandidate) continue;

                    string clean = StripXmlTagsFast(xml);

                    for (int g = 0; g < requiredGroups.Count; g++)
                    {
                        if (foundGroups.Contains(g)) continue;

                        foreach (var kw in requiredGroups[g])
                        {
                            int idx = clean.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                foundGroups.Add(g);
                                if (string.IsNullOrEmpty(firstFoundSnippet))
                                {
                                    firstFoundSnippet = ExtractSnippet(clean, idx, kw.Length);
                                }
                                break;
                            }
                        }
                    }

                    if (foundGroups.Count == requiredGroups.Count)
                    {
                        snippet = firstFoundSnippet;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        #endregion

        /// <summary>
        /// 本文テキスト内からキーワードの一致スニペット（前後文字列）を抽出します。
        /// </summary>
        public static string ExtractSnippet(string text, string keyword, int radius = DefaultSnippetRadius)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword)) return string.Empty;
            int idx = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return string.Empty;
            return ExtractSnippet(text, idx, keyword.Length, radius);
        }

        /// <summary>
        /// 一致位置と長さからスニペットを抽出します。
        /// </summary>
        public static string ExtractSnippet(string text, int matchIndex, int matchLength, int radius = DefaultSnippetRadius)
        {
            if (string.IsNullOrEmpty(text) || matchIndex < 0) return string.Empty;

            int start = Math.Max(0, matchIndex - radius);
            int length = Math.Min(text.Length - start, matchLength + (radius * 2));
            string snippet = text.Substring(start, length).Replace("\r", " ").Replace("\n", " ");
            snippet = Regex.Replace(snippet, @"<[^>]+>", " ");
            snippet = Regex.Replace(snippet, @"\s+", " ");

            if (start > 0) snippet = "..." + snippet;
            if (start + length < text.Length) snippet += "...";

            return snippet.Trim();
        }
    }
}
