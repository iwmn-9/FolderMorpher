using System;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace FolderMorpher.Services
{
    /// <summary>
    /// High-performance PDF search helper utilizing Windows native IFilter with pure C# stream fallback.
    /// </summary>
    public static class PdfSearchHelper
    {
        private static readonly Guid IFilterGuid = new("89BCB740-6119-101A-BCB7-00DD010655AF");

        #region Windows IFilter COM Interop

        [DllImport("query.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int LoadIFilter(
            string pwcsPath,
            IntPtr pUnkOuter,
            ref Guid riid,
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

            // 1. Try Windows Native IFilter (OS-level C++ Engine)
            try
            {
                var status = SearchWithIFilter(filePath, keyword, out snippet);
                if (status == PdfFilterStatus.Match)
                {
                    return true;
                }
                if (status == PdfFilterStatus.NoMatchComplete)
                {
                    // ★ 正常に最後まで走査して該当なし。重いPure C#フォールバックをスキップ（二重解析根絶）
                    return false;
                }
            }
            catch
            {
                // Fall through to pure C# fallback
            }

            // 2. Pure C# Fallback (Metadata scan + FlateDecode stream scan)
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

            // 1. Windows Native IFilter でワンパス走査
            try
            {
                var status = SearchMultipleWithIFilter(filePath, ac, patternToGroup, totalGroups, out snippet);
                if (status == PdfFilterStatus.Match)
                {
                    return true;
                }
                if (status == PdfFilterStatus.NoMatchComplete)
                {
                    // ★ 正常に最後まで走査して該当なし。重いPure C#フォールバックをスキップ（二重解析根絶）
                    return false;
                }
            }
            catch { }

            // 2. Pure C# Fallback でワンパス走査（IFilter未導入または失敗時のみ）
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
            try
            {
                Guid riid = IFilterGuid;
                int hr = LoadIFilter(filePath, IntPtr.Zero, ref riid, out pUnk);
                if (hr == 0 && pUnk != IntPtr.Zero)
                {
                    object comObj = Marshal.GetObjectForIUnknown(pUnk);
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

                            var textMatches = Regex.Matches(decompressed, @"\((?<text>[^)]*)\)\s*Tj|\[(?<text>[^\]]*)\]\s*TJ");
                            foreach (Match tm in textMatches)
                            {
                                sb.Append(tm.Groups["text"].Value).Append(' ');
                            }
                        }
                        catch { }
                    }
                }

                // 非圧縮テキストまたは平文ストリームのフォールバック抽出
                if (sb.Length < 10)
                {
                    string rawUtf8 = Encoding.UTF8.GetString(bytes);
                    var textMatches = Regex.Matches(rawUtf8, @"\((?<text>[^)]*)\)\s*Tj|\[(?<text>[^\]]*)\]\s*TJ");
                    foreach (Match tm in textMatches)
                    {
                        if (sb.Length >= maxChars) break;
                        sb.Append(tm.Groups["text"].Value).Append(' ');
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
            try
            {
                Guid riid = IFilterGuid;
                int hr = LoadIFilter(filePath, IntPtr.Zero, ref riid, out pUnk);
                if (hr != 0 || pUnk == IntPtr.Zero) return PdfFilterStatus.FailedOrUnsupported;

                object comObj = Marshal.GetObjectForIUnknown(pUnk);
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
            }
        }

        private static bool SearchWithStreamFallback(string filePath, string keyword, out string snippet)
        {
            snippet = string.Empty;
            byte[] bytes = File.ReadAllBytes(filePath);
            if (bytes.Length < 10) return false;

            // 1. Fast check: Plaintext metadata or uncompressed strings in PDF
            string rawAscii = Encoding.ASCII.GetString(bytes);
            int asciiIdx = rawAscii.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (asciiIdx >= 0)
            {
                snippet = ExtractSnippet(rawAscii, asciiIdx, keyword.Length);
                return true;
            }

            // UTF-8 metadata check
            string rawUtf8 = Encoding.UTF8.GetString(bytes);
            int utf8Idx = rawUtf8.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (utf8Idx >= 0)
            {
                snippet = ExtractSnippet(rawUtf8, utf8Idx, keyword.Length);
                return true;
            }

            // 2. Stream scan: find "stream\r\n" ... "endstream" blocks with FlateDecode
            var streamMatches = Regex.Matches(rawAscii, @"stream[\r\n]+(?<data>[\s\S]*?)endstream");
            foreach (Match match in streamMatches)
            {
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

                        // Check direct keyword
                        int idx = decompressed.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            snippet = ExtractSnippet(decompressed, idx, keyword.Length);
                            return true; // Early exit!
                        }

                        // Check PDF text operators (Tj / TJ)
                        var textMatches = Regex.Matches(decompressed, @"\((?<text>[^)]*)\)\s*Tj|\[(?<text>[^\]]*)\]\s*TJ");
                        var sb = new StringBuilder();
                        foreach (Match tm in textMatches)
                        {
                            sb.Append(tm.Groups["text"].Value);
                        }
                        string textBlock = sb.ToString();
                        int opIdx = textBlock.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                        if (opIdx >= 0)
                        {
                            snippet = ExtractSnippet(textBlock, opIdx, keyword.Length);
                            return true; // Early exit!
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
            try
            {
                Guid riid = IFilterGuid;
                int hr = LoadIFilter(filePath, IntPtr.Zero, ref riid, out pUnk);
                if (hr != 0 || pUnk == IntPtr.Zero) return PdfFilterStatus.FailedOrUnsupported;

                object comObj = Marshal.GetObjectForIUnknown(pUnk);
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

            // 1. メタデータ確認
            string rawAscii = Encoding.ASCII.GetString(bytes);
            CheckText(rawAscii);
            if (satisfiedGroups.Count == totalGroups)
            {
                snippet = localSnippet;
                return true;
            }

            string rawUtf8 = Encoding.UTF8.GetString(bytes);
            CheckText(rawUtf8);
            if (satisfiedGroups.Count == totalGroups)
            {
                snippet = localSnippet;
                return true;
            }

            // 2. Stream scan (FlateDecode)
            var streamMatches = Regex.Matches(rawAscii, @"stream[\r\n]+(?<data>[\s\S]*?)endstream");
            foreach (Match match in streamMatches)
            {
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

                        CheckText(decompressed);
                        if (satisfiedGroups.Count == totalGroups)
                        {
                            snippet = localSnippet;
                            return true;
                        }

                        var textMatches = Regex.Matches(decompressed, @"\((?<text>[^)]*)\)\s*Tj|\[(?<text>[^\]]*)\]\s*TJ");
                        var sb = new StringBuilder();
                        foreach (Match tm in textMatches)
                        {
                            sb.Append(tm.Groups["text"].Value);
                        }
                        CheckText(sb.ToString());
                        if (satisfiedGroups.Count == totalGroups)
                        {
                            snippet = localSnippet;
                            return true;
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
