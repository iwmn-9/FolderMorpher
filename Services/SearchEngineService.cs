using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AstraSize.Models;
using FolderMorpher.Models;

namespace FolderMorpher.Services
{
    /// <summary>
    /// Search engine service providing in-memory instant search, direct UNC traversal, and accelerated content search.
    /// </summary>
    public class SearchEngineService
    {
        private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".xlsx", ".xlsm", ".docx", ".pptx"
        };

        private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf"
        };

        private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".txt", ".log", ".csv", ".tsv", ".json", ".xml", ".html", ".htm", ".md",
            ".cs", ".sql", ".ps1", ".bat", ".cmd", ".py", ".ini", ".cfg", ".config", ".yaml", ".yml"
        };

        private static readonly char[] IllegalFileNameChars = Path.GetInvalidFileNameChars();

        /// <summary>
        /// Perform instant in-memory search on pre-scanned trees
        /// </summary>
        public async Task<List<SearchResultItem>> SearchInMemoryAsync(
            IEnumerable<FileItemNode> rootNodes,
            SearchQuery query,
            IProgress<SearchProgressReport>? progress = null,
            CancellationToken ct = default)
        {
            return await Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                var results = new List<SearchResultItem>();
                int scannedCount = 0;
                var candidates = new List<SearchResultItem>();

                void Traverse(FileItemNode node)
                {
                    ct.ThrowIfCancellationRequested();
                    scannedCount++;

                    if (IsMatchBasic(node, query, out string matchReason))
                    {
                        var item = new SearchResultItem
                        {
                            Name = node.Name,
                            FullPath = node.FullPath,
                            DirectoryPath = Path.GetDirectoryName(node.FullPath) ?? string.Empty,
                            SizeBytes = node.SizeBytes,
                            LastWriteTime = node.LastModified ?? DateTime.MinValue,
                            Extension = node.IsDirectory ? string.Empty : Path.GetExtension(node.Name).ToLowerInvariant(),
                            IsDirectory = node.IsDirectory,
                            MatchedReason = matchReason
                        };
                        candidates.Add(item);
                    }

                    foreach (var child in node.Children)
                    {
                        Traverse(child);
                    }
                }

                foreach (var root in rootNodes)
                {
                    Traverse(root);
                }

                if (query.HasDeepFileIoRequirement && candidates.Count > 0)
                {
                    results = await FilterByContentAsync(candidates, query, progress, ct);
                }
                else
                {
                    results = candidates;
                }

                sw.Stop();
                progress?.Report(new SearchProgressReport
                {
                    HitCount = results.Count,
                    ScannedCount = scannedCount,
                    TotalHitBytes = results.Sum(r => r.SizeBytes),
                    Elapsed = sw.Elapsed,
                    IsCompleted = true
                });

                return results;
            }, ct);
        }

        /// <summary>
        /// Direct streaming traversal for unscanned folders or shares
        /// </summary>
        public async Task<List<SearchResultItem>> SearchDirectFolderAsync(
            string targetFolder,
            SearchQuery query,
            IProgress<IReadOnlyList<SearchResultItem>>? batchYield = null,
            IProgress<SearchProgressReport>? progress = null,
            CancellationToken ct = default)
        {
            return await Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                var results = new List<SearchResultItem>();
                var currentBatch = new List<SearchResultItem>();
                int scannedCount = 0;
                long lastReportMs = 0;

                var entries = await SafeFileEnumerator.EnumerateFileEntriesParallelAsync(
                    targetFolder,
                    "*.*",
                    coverage: null,
                    onProgress: null,
                    ct: ct);

                var candidates = new List<SearchResultItem>();

                foreach (var entry in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    scannedCount++;

                    if (IsMatchEntry(entry, query, out string matchReason))
                    {
                        bool isDir = entry.Attributes.HasFlag(FileAttributes.Directory);
                        var item = new SearchResultItem
                        {
                            Name = entry.Name,
                            FullPath = entry.FullPath,
                            DirectoryPath = entry.DirectoryPath,
                            SizeBytes = entry.Length,
                            LastWriteTime = entry.LastWriteTime,
                            Extension = isDir ? string.Empty : Path.GetExtension(entry.Name).ToLowerInvariant(),
                            IsDirectory = isDir,
                            MatchedReason = matchReason
                        };

                        if (query.HasDeepFileIoRequirement)
                        {
                            candidates.Add(item);
                        }
                        else
                        {
                            results.Add(item);
                            currentBatch.Add(item);

                            if (currentBatch.Count >= 50)
                            {
                                batchYield?.Report(currentBatch.ToList());
                                currentBatch.Clear();
                            }
                        }
                    }

                    if (sw.ElapsedMilliseconds - lastReportMs > 150)
                    {
                        lastReportMs = sw.ElapsedMilliseconds;
                        progress?.Report(new SearchProgressReport
                        {
                            HitCount = results.Count + candidates.Count,
                            ScannedCount = scannedCount,
                            TotalHitBytes = results.Sum(r => r.SizeBytes) + candidates.Sum(r => r.SizeBytes),
                            CurrentPath = entry.FullPath,
                            Elapsed = sw.Elapsed,
                            IsCompleted = false
                        });
                    }
                }

                if (currentBatch.Count > 0 && !query.HasDeepFileIoRequirement)
                {
                    batchYield?.Report(currentBatch.ToList());
                    currentBatch.Clear();
                }

                if (query.HasDeepFileIoRequirement && candidates.Count > 0)
                {
                    results = await FilterByContentAsync(candidates, query, progress, ct);
                    batchYield?.Report(results);
                }

                sw.Stop();
                progress?.Report(new SearchProgressReport
                {
                    HitCount = results.Count,
                    ScannedCount = scannedCount,
                    TotalHitBytes = results.Sum(r => r.SizeBytes),
                    Elapsed = sw.Elapsed,
                    IsCompleted = true
                });

                return results;
            }, ct);
        }

        private static bool IsMatchBasic(FileItemNode node, SearchQuery query, out string reason)
        {
            reason = string.Empty;

            if (query.IsDirectoryOnly.HasValue)
            {
                if (query.IsDirectoryOnly.Value && !node.IsDirectory) return false;
                if (!query.IsDirectoryOnly.Value && node.IsDirectory) return false;
            }
            else if (!query.IncludeFolders && node.IsDirectory)
            {
                return false;
            }

            if (query.IsEmpty) return true;

            string name = node.Name;
            string fullPath = node.FullPath;
            string ext = node.IsDirectory ? string.Empty : Path.GetExtension(name).ToLowerInvariant();

            if (query.Extensions.Count > 0)
            {
                if (node.IsDirectory || !query.Extensions.Contains(ext)) return false;
            }

            if (query.MinSizeBytes.HasValue && node.SizeBytes < query.MinSizeBytes.Value) return false;
            if (query.MaxSizeBytes.HasValue && node.SizeBytes > query.MaxSizeBytes.Value) return false;

            if (node.LastModified.HasValue)
            {
                var utc = node.LastModified.Value.ToUniversalTime();
                if (query.MinModifiedUtc.HasValue && utc < query.MinModifiedUtc.Value) return false;
                if (query.MaxModifiedUtc.HasValue && utc > query.MaxModifiedUtc.Value) return false;
            }
            else if (query.MinModifiedUtc.HasValue || query.MaxModifiedUtc.HasValue)
            {
                return false;
            }

            if (query.MinPathLength.HasValue && fullPath.Length < query.MinPathLength.Value) return false;

            if (query.OnlyIllegalChars)
            {
                if (!HasIllegalChars(name)) return false;
                reason = "Illegal Chars";
            }

            foreach (var pc in query.PathContains)
            {
                if (fullPath.IndexOf(pc, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }

            foreach (var exc in query.ExcludedWords)
            {
                string target = (exc.IndexOf('\\') >= 0 || exc.IndexOf('/') >= 0) ? fullPath : name;
                if (target.IndexOf(exc, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }

            foreach (var phr in query.ExactPhrases)
            {
                string target = (phr.IndexOf('\\') >= 0 || phr.IndexOf('/') >= 0) ? fullPath : name;
                if (target.IndexOf(phr, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }

            // If SearchContentMode is true and ContentKeyword is empty, keywords can match either file name or content later
            if (!query.SearchContentMode || !string.IsNullOrEmpty(query.ContentKeyword))
            {
                foreach (var kw in query.Keywords)
                {
                    string target = (kw.IndexOf('\\') >= 0 || kw.IndexOf('/') >= 0) ? fullPath : name;
                    if (target.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) return false;
                }
            }

            if (query.CompiledRegex != null)
            {
                if (!query.CompiledRegex.IsMatch(name) && !query.CompiledRegex.IsMatch(fullPath)) return false;
            }

            if (string.IsNullOrEmpty(reason)) reason = "Match";
            return true;
        }

        private static bool IsMatchEntry(ScannedFileEntry entry, SearchQuery query, out string reason)
        {
            reason = string.Empty;

            bool isDir = entry.Attributes.HasFlag(FileAttributes.Directory);
            if (query.IsDirectoryOnly.HasValue)
            {
                if (query.IsDirectoryOnly.Value && !isDir) return false;
                if (!query.IsDirectoryOnly.Value && isDir) return false;
            }
            else if (!query.IncludeFolders && isDir)
            {
                return false;
            }

            if (query.IsEmpty) return true;

            string name = entry.Name;
            string fullPath = entry.FullPath;
            string ext = isDir ? string.Empty : Path.GetExtension(name).ToLowerInvariant();

            if (query.Extensions.Count > 0)
            {
                if (isDir || !query.Extensions.Contains(ext)) return false;
            }

            if (query.MinSizeBytes.HasValue || query.MaxSizeBytes.HasValue)
            {
                if (isDir) return false;
                if (query.MinSizeBytes.HasValue && entry.Length < query.MinSizeBytes.Value) return false;
                if (query.MaxSizeBytes.HasValue && entry.Length > query.MaxSizeBytes.Value) return false;
            }

            var utc = entry.LastWriteTime.ToUniversalTime();
            if (query.MinModifiedUtc.HasValue && utc < query.MinModifiedUtc.Value) return false;
            if (query.MaxModifiedUtc.HasValue && utc > query.MaxModifiedUtc.Value) return false;

            if (query.MinPathLength.HasValue && fullPath.Length < query.MinPathLength.Value) return false;

            if (query.OnlyIllegalChars)
            {
                if (!HasIllegalChars(name)) return false;
                reason = "Illegal Chars";
            }

            foreach (var pc in query.PathContains)
            {
                if (fullPath.IndexOf(pc, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }

            foreach (var exc in query.ExcludedWords)
            {
                string target = (exc.IndexOf('\\') >= 0 || exc.IndexOf('/') >= 0) ? fullPath : name;
                if (target.IndexOf(exc, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }

            foreach (var phr in query.ExactPhrases)
            {
                string target = (phr.IndexOf('\\') >= 0 || phr.IndexOf('/') >= 0) ? fullPath : name;
                if (target.IndexOf(phr, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }

            if (!query.SearchContentMode || !string.IsNullOrEmpty(query.ContentKeyword))
            {
                foreach (var kw in query.Keywords)
                {
                    string target = (kw.IndexOf('\\') >= 0 || kw.IndexOf('/') >= 0) ? fullPath : name;
                    if (target.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) return false;
                }
            }

            if (query.CompiledRegex != null)
            {
                if (!query.CompiledRegex.IsMatch(name) && !query.CompiledRegex.IsMatch(fullPath)) return false;
            }

            if (string.IsNullOrEmpty(reason)) reason = "Match";
            return true;
        }

        private static bool HasIllegalChars(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            if (name.EndsWith(' ') || name.EndsWith('.')) return true;
            foreach (var c in IllegalFileNameChars)
            {
                if (name.IndexOf(c) >= 0) return true;
            }
            return false;
        }

        private static async Task<List<SearchResultItem>> FilterByContentAsync(
            List<SearchResultItem> candidates,
            SearchQuery query,
            IProgress<SearchProgressReport>? progress,
            CancellationToken ct)
        {
            var matched = new ConcurrentBag<SearchResultItem>();

            // Determine target search keyword
            string targetContent = query.ContentKeyword ?? string.Empty;
            if (string.IsNullOrEmpty(targetContent) && query.SearchContentMode && query.Keywords.Count > 0)
            {
                targetContent = query.Keywords[0];
            }

            bool hasOfficeLinkReq = query.HasOfficeLinkOnly;
            string? officeLinkKeyword = query.OfficeLinkKeyword;

            const long MaxSearchFileSize = 50L * 1024 * 1024;
            var validFiles = candidates.Where(c => !c.IsDirectory && c.SizeBytes <= MaxSearchFileSize && File.Exists(c.FullPath)).ToList();

            int processed = 0;
            int total = validFiles.Count;

            bool containsUnc = validFiles.Any(f => f.FullPath.StartsWith(@"\\", StringComparison.Ordinal) || f.FullPath.StartsWith("//", StringComparison.Ordinal));
            int maxDegree = containsUnc
                ? 2 // UNC/ネットワーク共有はサーバー保護のためデュアルワーカー（並列度2）固定
                : Math.Clamp(Environment.ProcessorCount / 2, 2, 4); // ローカルドライブもI/O競合抑制のため安全上限4

            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxDegree,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(validFiles, po, async (item, token) =>
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    string ext = Path.GetExtension(item.FullPath).ToLowerInvariant();

                    if (!string.IsNullOrEmpty(targetContent))
                    {
                        // 1. Office (OpenXML: .xlsx, .xlsm, .docx, .pptx)
                        if (OfficeExtensions.Contains(ext))
                        {
                            if (SearchOfficeFileContent(item.FullPath, targetContent, out string snippet))
                            {
                                item.ContentSnippet = snippet;
                                item.MatchedReason = $"Content: \"{targetContent}\"";
                                matched.Add(item);
                            }
                        }
                        // 2. PDF (.pdf) with Windows IFilter and pure C# fallback
                        else if (PdfExtensions.Contains(ext))
                        {
                            if (PdfSearchHelper.SearchPdfContent(item.FullPath, targetContent, out string snippet))
                            {
                                item.ContentSnippet = snippet;
                                item.MatchedReason = $"PDF Content: \"{targetContent}\"";
                                matched.Add(item);
                            }
                        }
                        // 3. Text files (.txt, .csv, .log, .json, code files, etc.)
                        else if (TextExtensions.Contains(ext) || item.SizeBytes < 2 * 1024 * 1024)
                        {
                            if (await SearchTextFileContentAsync(item.FullPath, targetContent, token) is { } snippet)
                            {
                                item.ContentSnippet = snippet;
                                item.MatchedReason = $"Content: \"{targetContent}\"";
                                matched.Add(item);
                            }
                        }
                    }
                    else if (hasOfficeLinkReq && OfficeExtensions.Contains(ext))
                    {
                        if (SearchOfficeFileLinks(item.FullPath, officeLinkKeyword, out string snippet))
                        {
                            item.ContentSnippet = snippet;
                            item.MatchedReason = string.IsNullOrEmpty(officeLinkKeyword)
                                ? "Office External Link"
                                : $"OfficeLink: \"{officeLinkKeyword}\"";
                            matched.Add(item);
                        }
                    }
                }
                catch
                {
                }

                int c = Interlocked.Increment(ref processed);
                if (c % 20 == 0 || c == total)
                {
                    progress?.Report(new SearchProgressReport
                    {
                        HitCount = matched.Count,
                        ScannedCount = c,
                        CurrentPath = $"Deep Search ({c}/{total}): {item.Name}",
                        IsCompleted = false
                    });
                }
            });

            return matched.ToList();
        }

        private static bool SearchOfficeFileContent(string filePath, string keyword, out string snippet)
        {
            snippet = string.Empty;
            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, false);

                // アイデア 1: Excel (.xlsx / .xlsm) の場合は sharedStrings.xml を最優先・ピンポイント探索
                if (ext == ".xlsx" || ext == ".xlsm")
                {
                    ZipArchiveEntry? sharedEntry = null;
                    foreach (var e in zip.Entries)
                    {
                        if (e.FullName.EndsWith("sharedstrings.xml", StringComparison.OrdinalIgnoreCase))
                        {
                            sharedEntry = e;
                            break;
                        }
                    }

                    if (sharedEntry != null)
                    {
                        using var stream = sharedEntry.Open();
                        using var reader = new StreamReader(stream, Encoding.UTF8);
                        string text = reader.ReadToEnd();

                        int idx = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            snippet = ExtractSnippet(text, idx, keyword.Length);
                            return true; // Early exit!
                        }

                        // sharedStrings になければ、シート本文にもテキストは存在しないため即座に終了 (数ミリ秒)
                        return false;
                    }
                }

                // Word (.docx), PowerPoint (.pptx), または sharedStrings がない Excel の探索
                foreach (var entry in zip.Entries)
                {
                    string entryName = entry.FullName.ToLowerInvariant();
                    if (!entryName.EndsWith(".xml") && !entryName.EndsWith(".rels")) continue;
                    if (!entryName.Contains("sharedstrings") &&
                        !entryName.Contains("sheet") &&
                        !entryName.Contains("document") &&
                        !entryName.Contains("slide") &&
                        !entryName.Contains("_rels")) continue;

                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string text = reader.ReadToEnd();

                    // 1. 素のXML文字列で高速検索 (アイデア 3: Early Exit)
                    int idx = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        snippet = ExtractSnippet(text, idx, keyword.Length);
                        return true;
                    }

                    // 2. Word等のrun分割 (<w:t>A</w:t><w:t>B</w:t>) 対策でXMLタグ除去して探索
                    if (entryName.Contains("document") || entryName.Contains("slide") || entryName.Contains("sheet"))
                    {
                        string stripped = Regex.Replace(text, @"<[^>]+>", "");
                        int strippedIdx = stripped.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                        if (strippedIdx >= 0)
                        {
                            snippet = ExtractSnippet(stripped, strippedIdx, keyword.Length);
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static bool SearchOfficeFileLinks(string filePath, string? keyword, out string snippet)
        {
            snippet = string.Empty;
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, false);

                foreach (var entry in zip.Entries)
                {
                    string entryName = entry.FullName.ToLowerInvariant();
                    if (!entryName.Contains("externallink") &&
                        !entryName.Contains("externalreferences") &&
                        !entryName.Contains("_rels") &&
                        !entryName.EndsWith(".rels") &&
                        !entryName.Contains("worksheets")) continue;

                    using var stream = entry.Open();
                    using var reader = new StreamReader(stream, Encoding.UTF8);
                    string text = reader.ReadToEnd();

                    if (string.IsNullOrEmpty(keyword))
                    {
                        if (text.Contains("TargetMode=\"External\"", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
                            text.Contains(@"\\", StringComparison.OrdinalIgnoreCase) ||
                            entryName.Contains("externallink"))
                        {
                            var match = Regex.Match(text, @"(?:Target=""([^""]+)""|TargetMode=""External""[^>]*>|(\\\\[a-zA-Z0-9._$-]+\\[^""<\s]+))", RegexOptions.IgnoreCase);
                            snippet = match.Success ? match.Groups[1].Value : "Office External Link Detected";
                            if (string.IsNullOrWhiteSpace(snippet)) snippet = "Office External Link";
                            return true;
                        }
                    }
                    else
                    {
                        int idx = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                        if (idx >= 0)
                        {
                            snippet = ExtractSnippet(text, idx, keyword.Length);
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }

        private static async Task<string?> SearchTextFileContentAsync(string filePath, string keyword, CancellationToken ct)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);

                // アイデア 2: 先頭4KBでのバイナリ早期脱落 (Early Drop)
                byte[] headBuffer = new byte[4096];
                int readHead = await fs.ReadAsync(headBuffer, 0, headBuffer.Length, ct);
                if (readHead > 0)
                {
                    int nullCount = 0;
                    for (int i = 0; i < readHead; i++)
                    {
                        if (headBuffer[i] == 0) nullCount++;
                    }
                    // NULLバイトが混入している場合はバイナリとみなし即座に終了 (無駄なI/Oを完全カット)
                    if (nullCount >= 2) return null;

                    // ストリームを先頭に戻す
                    fs.Seek(0, SeekOrigin.Begin);
                }

                using var reader = new StreamReader(fs, Encoding.UTF8, true);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    // アイデア 3: マッチした瞬間に即座に読み込みを中断して復帰 (Early Exit)
                    int idx = line.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        return ExtractSnippet(line, idx, keyword.Length);
                    }
                }
            }
            catch { }
            return null;
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
