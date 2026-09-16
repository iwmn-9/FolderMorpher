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
            [MarshalAs(UnmanagedType.IUnknown)] object? pUnkOuter,
            ref Guid riid,
            [MarshalAs(UnmanagedType.IUnknown)] out object? ppIUnk);

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

        /// <summary>
        /// Search for a keyword within a PDF file using IFilter first, falling back to pure C# stream scan.
        /// </summary>
        public static bool SearchPdfContent(string filePath, string keyword, out string snippet)
        {
            snippet = string.Empty;
            if (string.IsNullOrEmpty(keyword) || !File.Exists(filePath)) return false;

            // 1. Try Windows Native IFilter (OS-level C++ Engine)
            try
            {
                if (SearchWithIFilter(filePath, keyword, out snippet))
                {
                    return true;
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

        private static bool SearchWithIFilter(string filePath, string keyword, out string snippet)
        {
            snippet = string.Empty;
            Guid riid = IFilterGuid;
            int hr = LoadIFilter(filePath, null, ref riid, out object? obj);
            if (hr != 0 || obj is not IFilter filter) return false;

            try
            {
                hr = filter.Init(IFILTER_INIT_ALL, 0, 0, out _);
                if (hr != 0) return false;

                char[] buffer = new char[4096];
                var sb = new StringBuilder();

                while (filter.GetChunk(out var stat) == 0)
                {
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
                                    return true; // Early Exit!
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
            finally
            {
                Marshal.ReleaseComObject(filter);
            }

            return false;
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
