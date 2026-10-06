using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace FolderMorpher.Services
{
    /// <summary>
    /// High-performance PDF search helper utilizing Windows native IFilter with pure C# stream fallback.
    /// </summary>
    public static class PdfSearchHelper
    {
        #region Windows IFilter COM Interop

        [DllImport("query.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int LoadIFilter(
            string pwcsPath,
            IntPtr pUnkOuter,
            out IntPtr ppIUnk);

        [ComImport, Guid("89BCB740-6119-101A-BCB7-00DD010655AF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFilter
        {
            [PreserveSig] int Init(uint grfFlags, uint cAttributes, nint aAttributes, out uint pdwFlags);
            [PreserveSig] int GetChunk(out STAT_CHUNK pStat);
            [PreserveSig] int GetText(ref uint pcwcBuffer, [Out] char[] awcBuffer);
            [PreserveSig] int GetValue(out nint ppPropValue);
            [PreserveSig] int BindRegion(FILTERREGION origPos, ref Guid riid, out nint ppunk);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct STAT_CHUNK
        {
            public uint idChunk;
            public uint breakType;
            public uint flags;
            public uint locale;
            public FULLPROPSPEC attribute;
            public uint idChunkSource;
            public uint cwcStartSource;
            public uint cwcLenSource;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FULLPROPSPEC
        {
            public Guid guidPropSet;
            public PROPSPEC psProperty;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PROPSPEC
        {
            public uint ulKind;
            public nint dummy;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILTERREGION
        {
            public uint idChunk;
            public uint cwcStart;
            public uint cwcExtent;
        }

        private const uint IFILTER_INIT_ALL = 0x3F;
        private const uint CHUNK_TEXT = 0x1;
        private const int FILTER_E_END_OF_CHUNKS = unchecked((int)0x80041700);
        private const int FILTER_E_NO_MORE_TEXT = unchecked((int)0x80041701);

        #endregion

        private enum PdfFilterStatus
        {
            Match,
            NoMatchComplete,
            FailedOrUnsupported
        }

        /// <summary>
        /// Search for a keyword within a PDF file using IFilter first, falling back to pure C# stream scan
        /// ONLY if IFilter failed or is unsupported (eliminating double-scanning on non-matching files).
        /// </summary>
        public static bool SearchPdfContent(string filePath, string keyword, out string snippet)
        {
            snippet = string.Empty;
            if (string.IsNullOrEmpty(keyword) || !File.Exists(filePath)) return false;

            // 1. UglyToad.PdfPig Engine (主エンジン: Pure C# 高速 ToUnicode CMap 解決 / ADR 143)
            try
            {
                using var document = PdfDocument.Open(filePath);
                foreach (var page in document.GetPages())
                {
                    string pageText = page.Text;
                    if (string.IsNullOrEmpty(pageText)) continue;

                    int idx = pageText.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        snippet = ExtractSnippet(pageText, idx, keyword.Length);
                        return true;
                    }
                }
                // 正常に全ページ走査完了して未ヒット ➔ 二重解析をスキップして即時確定 (False Negative防止と高速化の両立)
                return false;
            }
            catch
            {
                // 破損PDFや未対応構文の場合はフォールバックへ
            }

            // 2. Windows Native IFilter (OS-level C++ Engine フォールバック)
            try
            {
                var status = SearchWithIFilter(filePath, keyword, out snippet);
                if (status == PdfFilterStatus.Match) return true;
                if (status == PdfFilterStatus.NoMatchComplete) return false;
            }
            catch
            {
            }

            // 3. Pure C# Stream Fallback (Raw FlateDecode + Tj/TJ 抽出による最終救済)
            try
            {
                if (SearchWithStreamFallback(filePath, keyword, out snippet))
                {
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        /// <summary>
        /// UglyToad.PdfPig (Pure C#) を用いた単一キーワード検索 (ADR 143)
        /// 外部ネイティブDLL不要・ToUnicode CMap解決・ページ単位ストリーミング & Early Exit
        /// </summary>
        public static bool SearchWithPdfPig(string filePath, string keyword, out string snippet)
        {
            snippet = string.Empty;
            if (string.IsNullOrEmpty(keyword) || !File.Exists(filePath)) return false;

            try
            {
                using var document = PdfDocument.Open(filePath);
                foreach (var page in document.GetPages())
                {
                    string pageText = page.Text;
                    if (string.IsNullOrEmpty(pageText)) continue;

                    int idx = pageText.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        snippet = ExtractSnippet(pageText, idx, keyword.Length);
                        return true;
                    }
                }
            }
            catch
            {
                return false;
            }

            return false;
        }

        /// <summary>
        /// 【ADR 82: Aho-Corasick 多パターンワンパス PDF 照合】
        /// 複数の AND/OR キーワードグループを Aho-Corasick で単一オートマトンへ統合し、
        /// PDF を 1 回だけストリーム走査して全条件を同時判定・Early Exit する。
        /// </summary>
        public static bool SearchPdfContentMultiple(string filePath, IReadOnlyList<List<string>> requiredGroups, out string snippet)
        {
            var ac = CreateSearcher(requiredGroups, out var patternToGroup);
            if (ac == null)
            {
                snippet = string.Empty;
                return false;
            }
            return SearchPdfContentMultiple(filePath, ac, patternToGroup, requiredGroups.Count, out snippet);
        }

        /// <summary>
        /// 事前にコンパイル済みの AhoCorasickSearcher を再利用して PDF を走査（クエリ単位共有）。
        /// </summary>
        public static bool SearchPdfContentMultiple(
            string filePath,
            AhoCorasickSearcher ac,
            List<int> patternToGroup,
            int totalGroups,
            out string snippet)
        {
            snippet = string.Empty;
            if (!File.Exists(filePath)) return false;

            // 1. UglyToad.PdfPig Engine (主エンジン: Pure C# 高速 Aho-Corasick ワンパス走査 / ADR 143)
            try
            {
                if (SearchMultipleWithPdfPig(filePath, ac, patternToGroup, totalGroups, out snippet))
                {
                    return true;
                }
            }
            catch { }

            // 2. Windows Native IFilter でワンパス走査フォールバック
            try
            {
                var status = SearchMultipleWithIFilter(filePath, ac, patternToGroup, totalGroups, out snippet);
                if (status == PdfFilterStatus.Match) return true;
                if (status == PdfFilterStatus.NoMatchComplete) return false;
            }
            catch { }

            // 3. Pure C# Fallback (FlateDecode + Tj/TJ 抽出による最終救済)
            try
            {
                if (SearchMultipleWithStreamFallback(filePath, ac, patternToGroup, totalGroups, out snippet))
                {
                    return true;
                }
            }
            catch { }

            return false;
        }

        /// <summary>
        /// UglyToad.PdfPig (Pure C#) を用いた複数キーワードワンパス検索 (ADR 143)
        /// </summary>
        public static bool SearchMultipleWithPdfPig(
            string filePath,
            AhoCorasickSearcher ac,
            List<int> patternToGroup,
            int totalGroups,
            out string snippet)
        {
            snippet = string.Empty;
            if (!File.Exists(filePath)) return false;

            try
            {
                using var document = PdfDocument.Open(filePath);
                var satisfiedGroups = new HashSet<int>();
                string localSnippet = string.Empty;

                foreach (var page in document.GetPages())
                {
                    string pageText = page.Text;
                    if (string.IsNullOrEmpty(pageText)) continue;

                    var matches = ac.FindMatchedIndices(pageText);
                    foreach (var pIdx in matches)
                    {
                        int gIdx = patternToGroup[pIdx];
                        satisfiedGroups.Add(gIdx);
                        if (string.IsNullOrEmpty(localSnippet))
                        {
                            string p = ac.Patterns[pIdx];
                            int matchIdx = pageText.IndexOf(p, StringComparison.OrdinalIgnoreCase);
                            if (matchIdx >= 0)
                            {
                                localSnippet = ExtractSnippet(pageText, matchIdx, p.Length);
                            }
                        }
                    }

                    if (satisfiedGroups.Count == totalGroups)
                    {
                        snippet = localSnippet;
                        return true; // 全グループ充足で即時脱落
                    }
                }

                if (satisfiedGroups.Count == totalGroups)
                {
                    snippet = localSnippet;
                    return true;
                }
            }
            catch { }

            return false;
        }

        public static AhoCorasickSearcher? CreateSearcher(IReadOnlyList<List<string>> requiredGroups, out List<int> patternToGroup)
        {
            patternToGroup = new List<int>();
            if (requiredGroups == null || requiredGroups.Count == 0) return null;

            var patterns = new List<string>();
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
            return new AhoCorasickSearcher(patterns, ignoreCase: true);
        }
        /// </summary>
        public static string ExtractAllText(string filePath, int maxChars = 200000)
        {
            if (!File.Exists(filePath)) return string.Empty;

            // 1. Try Windows IFilter
            IntPtr pUnk = IntPtr.Zero;
            object? comObj = null;
            try
            {
                int hr = LoadIFilter(filePath, IntPtr.Zero, out pUnk);
                if (hr == 0 && pUnk != IntPtr.Zero)
                {
                    comObj = Marshal.GetObjectForIUnknown(pUnk);
                    if (comObj is IFilter filter)
                    {
                        hr = filter.Init(IFILTER_INIT_ALL, 0, 0, out _);
                        if (hr == 0)
                        {
                            char[] buffer = new char[4096];
                            var sb = new StringBuilder();
                            while (filter.GetChunk(out var stat) == 0 && sb.Length < maxChars)
                            {
                                if ((stat.flags & CHUNK_TEXT) != 0)
                                {
                                    while (sb.Length < maxChars)
                                    {
                                        uint size = (uint)buffer.Length;
                                        int textHr = filter.GetText(ref size, buffer);
                                        if (textHr == 0 || size > 0)
                                        {
                                            sb.Append(buffer, 0, (int)size);
                                        }
                                        if (textHr == FILTER_E_NO_MORE_TEXT || size == 0)
                                        {
                                            break;
                                        }
                                    }
                                }
                            }
                            if (sb.Length > 0) return sb.ToString();
                        }
                    }
                }
            }
            catch { }
            finally
            {
                if (pUnk != IntPtr.Zero)
                {
                    try { Marshal.Release(pUnk); } catch { }
                }
                GC.KeepAlive(comObj);
            }

            // 2. Pure C# Fallback
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                if (bytes.Length < 10) return string.Empty;
                var sb = new StringBuilder();

                string rawAscii = Encoding.ASCII.GetString(bytes);
                var streamMatches = Regex.Matches(rawAscii, @"stream[\r\n]+(?<data>[\s\S]*?)endstream");
                foreach (Match match in streamMatches)
                {
                    if (sb.Length >= maxChars) break;
                    // 画像ストリーム（/Subtype /Image）の事前除外
                    int dictLookback = Math.Max(0, match.Index - 500);
                    string precedingDict = rawAscii.Substring(dictLookback, match.Index - dictLookback);
                    if (Regex.IsMatch(precedingDict, @"/Subtype\s*/Image\b", RegexOptions.IgnoreCase))
                    {
                        continue;
                    }

                    int start = match.Index + (rawAscii[match.Index + 6] == '\n' ? 7 : (rawAscii[match.Index + 7] == '\n' ? 8 : 6));
                    int length = match.Length - (start - match.Index) - 9;
                    if (start + length > bytes.Length || length <= 2) continue;

                    if (bytes[start] == 0x78 && (bytes[start + 1] == 0x9C || bytes[start + 1] == 0x01 || bytes[start + 1] == 0xDA))
                    {
                        try
                        {
                            using var ms = new MemoryStream(bytes, start + 2, length - 2);
                            using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                            using var reader = new StreamReader(ds, Encoding.UTF8);
                            string decompressed = reader.ReadToEnd();

                            string text = ExtractVisibleTextFromPdfStream(decompressed);
                            if (!string.IsNullOrEmpty(text))
                            {
                                sb.Append(text).Append(' ');
                            }
                        }
                        catch { }
                    }
                }

                // 非圧縮テキストまたは平文ストリームのフォールバック抽出
                if (sb.Length < 10)
                {
                    string rawUtf8 = Encoding.UTF8.GetString(bytes);
                    string text = ExtractVisibleTextFromPdfStream(rawUtf8);
                    if (!string.IsNullOrEmpty(text))
                    {
                        sb.Append(text).Append(' ');
                    }
                }

                return sb.ToString();
            }
            catch { }

            return string.Empty;
        }

        private static PdfFilterStatus SearchWithIFilter(string filePath, string keyword, out string snippet)
        {
            snippet = string.Empty;
            IntPtr pUnk = IntPtr.Zero;
            object? comObj = null;
            try
            {
                int hr = LoadIFilter(filePath, IntPtr.Zero, out pUnk);
                if (hr != 0 || pUnk == IntPtr.Zero) return PdfFilterStatus.FailedOrUnsupported;

                comObj = Marshal.GetObjectForIUnknown(pUnk);
                if (comObj is not IFilter filter) return PdfFilterStatus.FailedOrUnsupported;

                hr = filter.Init(IFILTER_INIT_ALL, 0, 0, out _);
                if (hr != 0) return PdfFilterStatus.FailedOrUnsupported;

                char[] buffer = new char[4096];
                var sb = new StringBuilder();

                while (true)
                {
                    int chunkHr = filter.GetChunk(out var stat);
                    if (chunkHr != 0)
                    {
                        if (chunkHr == FILTER_E_END_OF_CHUNKS)
                        {
                            return PdfFilterStatus.NoMatchComplete; // 正常に最後まで走査完了
                        }
                        return PdfFilterStatus.FailedOrUnsupported;
                    }

                    if ((stat.flags & CHUNK_TEXT) != 0)
                    {
                        while (true)
                        {
                            uint size = (uint)buffer.Length;
                            int textHr = filter.GetText(ref size, buffer);
                            if (textHr == 0 || size > 0)
                            {
                                sb.Append(buffer, 0, (int)size);
                                string currentText = sb.ToString();
                                int idx = currentText.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                                if (idx >= 0)
                                {
                                    snippet = ExtractSnippet(currentText, idx, keyword.Length);
                                    return PdfFilterStatus.Match; // Early Exit!
                                }

                                if (sb.Length > 8192)
                                {
                                    sb.Remove(0, sb.Length - 1024);
                                }
                            }

                            if (textHr == FILTER_E_NO_MORE_TEXT || size == 0)
                            {
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
                return PdfFilterStatus.FailedOrUnsupported;
            }
            finally
            {
                if (pUnk != IntPtr.Zero)
                {
                    try { Marshal.Release(pUnk); } catch { }
                }
                GC.KeepAlive(comObj);
            }
        }

        private static bool SearchWithStreamFallback(string filePath, string keyword, out string snippet)
        {
            snippet = string.Empty;
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < 10) return false;

            // ADR 137: 生PDFバイナリ全体の rawAscii / rawUtf8 に対する IndexOf は完全撤廃。
            // /Producer (CUPS...), /Supplements, /Group, dup などのメタデータ辞書キーによる誤爆を根絶する。

            string rawAscii = Encoding.ASCII.GetString(bytes);
            var streamMatches = Regex.Matches(rawAscii, @"stream[\r\n]+(?<data>[\s\S]*?)endstream");
            foreach (Match match in streamMatches)
            {
                // 画像ストリーム（/Subtype /Image）の事前判定と除外
                int dictLookback = Math.Max(0, match.Index - 500);
                string precedingDict = rawAscii.Substring(dictLookback, match.Index - dictLookback);
                if (Regex.IsMatch(precedingDict, @"/Subtype\s*/Image\b", RegexOptions.IgnoreCase))
                {
                    continue; // 画像オブジェクトの生ピクセルデータはスキップ
                }

                int start = match.Index + (rawAscii[match.Index + 6] == '\n' ? 7 : (rawAscii[match.Index + 7] == '\n' ? 8 : 6));
                int length = match.Length - (start - match.Index) - 9; // approximate endstream offset
                if (start + length > bytes.Length || length <= 2) continue;

                // Check zlib header (0x78 0x9C or 0x78 0x01 or 0x78 0xDA)
                if (bytes[start] == 0x78 && (bytes[start + 1] == 0x9C || bytes[start + 1] == 0x01 || bytes[start + 1] == 0xDA))
                {
                    try
                    {
                        using var ms = new MemoryStream(bytes, start + 2, length - 2);
                        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                        using var reader = new StreamReader(ds, Encoding.UTF8);
                        string decompressed = reader.ReadToEnd();

                        // ★ ADR 137: 解凍バイト列全体への直接 IndexOf を撤廃。
                        // ピクセル配列のたまたまの一致（画像PDF）を排除し、BT〜ETテキスト描画命令からのみ抽出。
                        string textBlock = ExtractVisibleTextFromPdfStream(decompressed);
                        if (!string.IsNullOrEmpty(textBlock))
                        {
                            int opIdx = textBlock.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                            if (opIdx >= 0)
                            {
                                snippet = ExtractSnippet(textBlock, opIdx, keyword.Length);
                                return true; // Early exit!
                            }
                        }
                    }
                    catch
                    {
                    }
                }
                else
                {
                    // 非圧縮テキストストリーム（FlateDecode無しのBT〜ET描画命令）
                    try
                    {
                        string uncompressed = Encoding.UTF8.GetString(bytes, start, length);
                        string textBlock = ExtractVisibleTextFromPdfStream(uncompressed);
                        if (!string.IsNullOrEmpty(textBlock))
                        {
                            int opIdx = textBlock.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                            if (opIdx >= 0)
                            {
                                snippet = ExtractSnippet(textBlock, opIdx, keyword.Length);
                                return true; // Early exit!
                            }
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return false;
        }

        private static PdfFilterStatus SearchMultipleWithIFilter(
            string filePath,
            AhoCorasickSearcher ac,
            List<int> patternToGroup,
            int totalGroups,
            out string snippet)
        {
            snippet = string.Empty;
            IntPtr pUnk = IntPtr.Zero;
            object? comObj = null;
            try
            {
                int hr = LoadIFilter(filePath, IntPtr.Zero, out pUnk);
                if (hr != 0 || pUnk == IntPtr.Zero) return PdfFilterStatus.FailedOrUnsupported;

                comObj = Marshal.GetObjectForIUnknown(pUnk);
                if (comObj is not IFilter filter) return PdfFilterStatus.FailedOrUnsupported;

                hr = filter.Init(IFILTER_INIT_ALL, 0, 0, out _);
                if (hr != 0) return PdfFilterStatus.FailedOrUnsupported;

                char[] buffer = new char[4096];
                var sb = new StringBuilder();
                var satisfiedGroups = new HashSet<int>();

                while (true)
                {
                    int chunkHr = filter.GetChunk(out var stat);
                    if (chunkHr != 0)
                    {
                        if (chunkHr == FILTER_E_END_OF_CHUNKS)
                        {
                            return PdfFilterStatus.NoMatchComplete; // 正常に最後まで走査完了
                        }
                        return PdfFilterStatus.FailedOrUnsupported;
                    }

                    if ((stat.flags & CHUNK_TEXT) != 0)
                    {
                        while (true)
                        {
                            uint size = (uint)buffer.Length;
                            int textHr = filter.GetText(ref size, buffer);
                            if (textHr == 0 || size > 0)
                            {
                                sb.Append(buffer, 0, (int)size);
                                string currentText = sb.ToString();

                                var matchedIndices = ac.FindMatchedIndices(currentText);
                                foreach (var pIdx in matchedIndices)
                                {
                                    int gIdx = patternToGroup[pIdx];
                                    satisfiedGroups.Add(gIdx);
                                    if (string.IsNullOrEmpty(snippet))
                                    {
                                        string p = ac.Patterns[pIdx];
                                        int matchIdx = currentText.IndexOf(p, StringComparison.OrdinalIgnoreCase);
                                        if (matchIdx >= 0)
                                        {
                                            snippet = ExtractSnippet(currentText, matchIdx, p.Length);
                                        }
                                    }
                                }

                                if (satisfiedGroups.Count == totalGroups)
                                {
                                    return PdfFilterStatus.Match; // 全グループ充足で即座に Early Exit!
                                }

                                if (sb.Length > 8192)
                                {
                                    sb.Remove(0, sb.Length - 1024);
                                }
                            }

                            if (textHr == FILTER_E_NO_MORE_TEXT || size == 0)
                            {
                                break;
                            }
                        }
                    }
                }
            }
            catch
            {
                return PdfFilterStatus.FailedOrUnsupported;
            }
            finally
            {
                if (pUnk != IntPtr.Zero)
                {
                    try { Marshal.Release(pUnk); } catch { }
                }
                GC.KeepAlive(comObj);
            }
        }

        private static bool SearchMultipleWithStreamFallback(
            string filePath,
            AhoCorasickSearcher ac,
            List<int> patternToGroup,
            int totalGroups,
            out string snippet)
        {
            snippet = string.Empty;
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < 10) return false;

            var satisfiedGroups = new HashSet<int>();
            string localSnippet = string.Empty;

            void CheckText(string text)
            {
                if (string.IsNullOrEmpty(text)) return;
                var matches = ac.FindMatchedIndices(text);
                foreach (var pIdx in matches)
                {
                    int gIdx = patternToGroup[pIdx];
                    satisfiedGroups.Add(gIdx);
                    if (string.IsNullOrEmpty(localSnippet))
                    {
                        string p = ac.Patterns[pIdx];
                        int matchIdx = text.IndexOf(p, StringComparison.OrdinalIgnoreCase);
                        if (matchIdx >= 0)
                        {
                            localSnippet = ExtractSnippet(text, matchIdx, p.Length);
                        }
                    }
                }
            }

            // ADR 137: 生バイナリの素通しチェック（rawAscii / rawUtf8）を完全撤廃。
            string rawAscii = Encoding.ASCII.GetString(bytes);
            var streamMatches = Regex.Matches(rawAscii, @"stream[\r\n]+(?<data>[\s\S]*?)endstream");
            foreach (Match match in streamMatches)
            {
                // 画像ストリーム（/Subtype /Image）の事前判定と除外
                int dictLookback = Math.Max(0, match.Index - 500);
                string precedingDict = rawAscii.Substring(dictLookback, match.Index - dictLookback);
                if (Regex.IsMatch(precedingDict, @"/Subtype\s*/Image\b", RegexOptions.IgnoreCase))
                {
                    continue; // 画像オブジェクトの生ピクセルデータはスキップ
                }

                int start = match.Index + (rawAscii[match.Index + 6] == '\n' ? 7 : (rawAscii[match.Index + 7] == '\n' ? 8 : 6));
                int length = match.Length - (start - match.Index) - 9;
                if (start + length > bytes.Length || length <= 2) continue;

                if (bytes[start] == 0x78 && (bytes[start + 1] == 0x9C || bytes[start + 1] == 0x01 || bytes[start + 1] == 0xDA))
                {
                    try
                    {
                        using var ms = new MemoryStream(bytes, start + 2, length - 2);
                        using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                        using var reader = new StreamReader(ds, Encoding.UTF8);
                        string decompressed = reader.ReadToEnd();

                        // ★ ADR 137: 解凍バイト列全体への直接 IndexOf を撤廃し、テキスト描画命令からのみ抽出。
                        string textBlock = ExtractVisibleTextFromPdfStream(decompressed);
                        if (!string.IsNullOrEmpty(textBlock))
                        {
                            CheckText(textBlock);
                            if (satisfiedGroups.Count == totalGroups)
                            {
                                snippet = localSnippet;
                                return true;
                            }
                        }
                    }
                    catch { }
                }
                else
                {
                    // 非圧縮テキストストリーム（FlateDecode無しのBT〜ET描画命令）
                    try
                    {
                        string uncompressed = Encoding.UTF8.GetString(bytes, start, length);
                        string textBlock = ExtractVisibleTextFromPdfStream(uncompressed);
                        if (!string.IsNullOrEmpty(textBlock))
                        {
                            CheckText(textBlock);
                            if (satisfiedGroups.Count == totalGroups)
                            {
                                snippet = localSnippet;
                                return true;
                            }
                        }
                    }
                    catch { }
                }
            }

            if (satisfiedGroups.Count == totalGroups)
            {
                snippet = localSnippet;
                return true;
            }
            return false;
        }

        /// <summary>
        /// PDFのストリームから正規のテキスト描画命令（Tj / TJ / ' / "）の引数テキストのみを安全に抽出します（ADR 137）。
        /// 画像ピクセルの生バイナリやフォント記述・PostScript命令への誤爆を根絶します。
        /// </summary>
        private static string ExtractVisibleTextFromPdfStream(string streamContent)
        {
            if (string.IsNullOrEmpty(streamContent)) return string.Empty;
            var sb = new StringBuilder();

            // 1. (...) Tj or (...) ' or (...) "
            var tjMatches = Regex.Matches(streamContent, @"\((?<text>(?:\\.|[^)])*)\)\s*(?:Tj|'|"")");
            foreach (Match m in tjMatches)
            {
                string raw = m.Groups["text"].Value;
                sb.Append(UnescapePdfString(raw)).Append(' ');
            }

            // 2. [(...) -10 (...) ...] TJ
            var tjArrayMatches = Regex.Matches(streamContent, @"\[(?<array>[\s\S]*?)\]\s*TJ");
            foreach (Match m in tjArrayMatches)
            {
                string arrayContent = m.Groups["array"].Value;
                var subMatches = Regex.Matches(arrayContent, @"\((?<text>(?:\\.|[^)])*)\)");
                foreach (Match sm in subMatches)
                {
                    sb.Append(UnescapePdfString(sm.Groups["text"].Value)).Append(' ');
                }
                var hexMatches = Regex.Matches(arrayContent, @"<(?<hex>[0-9A-Fa-f\s]+)>");
                foreach (Match hm in hexMatches)
                {
                    sb.Append(DecodePdfHexString(hm.Groups["hex"].Value)).Append(' ');
                }
            }

            // 3. <...> Tj
            var hexTjMatches = Regex.Matches(streamContent, @"<(?<hex>[0-9A-Fa-f\s]+)>\s*(?:Tj|'|"")");
            foreach (Match m in hexTjMatches)
            {
                sb.Append(DecodePdfHexString(m.Groups["hex"].Value)).Append(' ');
            }

            return sb.ToString();
        }

        private static string UnescapePdfString(string input)
        {
            if (string.IsNullOrEmpty(input)) return string.Empty;
            return input.Replace(@"\)", ")")
                        .Replace(@"\(", "(")
                        .Replace(@"\\", @"\")
                        .Replace(@"\r", " ")
                        .Replace(@"\n", " ")
                        .Replace(@"\t", " ");
        }

        private static string DecodePdfHexString(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex)) return string.Empty;
            var cleaned = Regex.Replace(hex, @"\s+", "");
            if (cleaned.Length % 2 != 0) cleaned += "0";
            try
            {
                byte[] bytes = Convert.FromHexString(cleaned);
                if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                {
                    return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
                }
                if (bytes.Length >= 2 && bytes.Length % 2 == 0 && bytes[0] == 0x00)
                {
                    return Encoding.BigEndianUnicode.GetString(bytes);
                }
                return Encoding.UTF8.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string ExtractSnippet(string text, int matchIndex, int matchLength)
        {
            const int contextLength = 40;
            int start = Math.Max(0, matchIndex - contextLength);
            int end = Math.Min(text.Length, matchIndex + matchLength + contextLength);

            string snippet = text.Substring(start, end - start).Replace('\r', ' ').Replace('\n', ' ').Trim();
            snippet = Regex.Replace(snippet, @"<[^>]+>", " ");
            snippet = Regex.Replace(snippet, @"\s+", " ");

            return (start > 0 ? "..." : "") + snippet + (end < text.Length ? "..." : "");
        }
    }
}
