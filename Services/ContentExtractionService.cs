using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace FolderMorpher.Services
{
    /// <summary>
    /// ファイル（Office OpenXML, PDF, プレーンテキスト）からの本文テキスト抽出を行う共通サービスクラス（正本の単一性）。
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
        /// ファイルから本文テキストを非同期で抽出します。
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

            // 3. プレーンテキスト (.txt, .csv, .log, .json 等)
            return await ExtractPlainTextAsync(filePath, ct);
        }

        /// <summary>
        /// OpenXML形式のOfficeドキュメント（Excel, Word, PowerPoint）から本文テキストを抽出します。
        /// </summary>
        public static string? ExtractOfficeText(string filePath)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
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
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string xml = reader.ReadToEnd();
                    string clean = Regex.Replace(xml, @"<[^>]+>", " ");
                    clean = Regex.Replace(clean, @"\s+", " ");
                    sb.Append(clean).Append(' ');

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
        /// プレーンテキストファイルからエンコーディング自動判別（UTF-8, UTF-16LE/BE, Shift-JIS）で本文を抽出します。
        /// </summary>
        public static async Task<string?> ExtractPlainTextAsync(string filePath, CancellationToken ct = default)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length == 0) return null;

                // BOM および UTF-16 検出
                byte[] bom = new byte[Math.Min(4, (int)fs.Length)];
                int bytesRead = await fs.ReadAsync(bom.AsMemory(0, bom.Length), ct);
                fs.Position = 0;

                Encoding encoding = Encoding.UTF8;
                if (bytesRead >= 2)
                {
                    if (bom[0] == 0xFF && bom[1] == 0xFE)
                    {
                        encoding = Encoding.Unicode; // UTF-16 LE
                    }
                    else if (bom[0] == 0xFE && bom[1] == 0xFF)
                    {
                        encoding = Encoding.BigEndianUnicode; // UTF-16 BE
                    }
                    else if (bytesRead >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
                    {
                        encoding = Encoding.UTF8; // UTF-8 with BOM
                    }
                    else
                    {
                        // ゼロバイト頻度で UTF-16 LE を推定
                        byte[] sample = new byte[Math.Min(1024, (int)fs.Length)];
                        int sampleRead = await fs.ReadAsync(sample.AsMemory(0, sample.Length), ct);
                        fs.Position = 0;

                        int nullCount = 0;
                        for (int i = 1; i < sampleRead; i += 2)
                        {
                            if (sample[i] == 0x00) nullCount++;
                        }
                        if (nullCount > (sampleRead / 4))
                        {
                            encoding = Encoding.Unicode;
                        }
                    }
                }

                using var reader = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks: true);
                char[] buffer = new char[Math.Min(MaxCharsPerDocument, (int)Math.Min(fs.Length, MaxCharsPerDocument))];
                int charsRead = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), ct);

                return charsRead > 0 ? new string(buffer, 0, charsRead) : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 本文テキスト内からキーワードの一致スニペット（前後文字列）を抽出します。
        /// </summary>
        public static string ExtractSnippet(string text, string keyword, int radius = DefaultSnippetRadius)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(keyword)) return string.Empty;

            int idx = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return string.Empty;

            int start = Math.Max(0, idx - radius);
            int length = Math.Min(text.Length - start, keyword.Length + (radius * 2));
            string snippet = text.Substring(start, length).Replace("\r", " ").Replace("\n", " ");

            if (start > 0) snippet = "..." + snippet;
            if (start + length < text.Length) snippet += "...";

            return snippet.Trim();
        }
    }
}
