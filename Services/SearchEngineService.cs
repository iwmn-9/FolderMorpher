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
            CancellationToken ct = default,
            IProgress<IReadOnlyList<SearchResultItem>>? batchYield = null)
        {
            return await Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                var results = new List<SearchResultItem>();
                int scannedCount = 0;
                var candidates = new List<SearchResultItem>();

                var currentBatch = new List<SearchResultItem>();

                void Traverse(FileItemNode node)
                {
                    ct.ThrowIfCancellationRequested();
                    scannedCount++;

                    if (IsMatchBasic(node, query, out string matchReason, out bool needsDeepCheck))
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

                        if (needsDeepCheck)
                        {
                            candidates.Add(item);
                        }
                        else
                        {
                            results.Add(item);
                            if (batchYield != null)
                            {
                                currentBatch.Add(item);
                                if (currentBatch.Count >= 50)
                                {
                                    batchYield.Report(currentBatch.ToList());
                                    currentBatch.Clear();
                                }
                            }
                        }
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

                if (currentBatch.Count > 0 && batchYield != null)
                {
                    batchYield.Report(currentBatch.ToList());
                    currentBatch.Clear();
                }

                if (candidates.Count > 0)
                {
                    var contentResults = await FilterByContentAsync(candidates, query, progress, ct, batchYield);
                    results.AddRange(contentResults);
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
        /// Direct progressive streaming traversal for unscanned folders or shares
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
                var batchLock = new object();
                int scannedCount = 0;
                int hitCount = 0;
                long totalHitBytes = 0;
                long lastReportMs = 0;

                var candidates = new ConcurrentBag<SearchResultItem>();

                void HandleEntry(ScannedFileEntry entry)
                {
                    if (ct.IsCancellationRequested) return;
                    int count = Interlocked.Increment(ref scannedCount);

                    if (IsMatchEntry(entry, query, out string matchReason, out bool needsDeepCheck))
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

                        if (needsDeepCheck)
                        {
                            candidates.Add(item);
                            Interlocked.Increment(ref hitCount);
                            Interlocked.Add(ref totalHitBytes, item.SizeBytes);
                        }
                        else
                        {
                            lock (batchLock)
                            {
                                results.Add(item);
                                currentBatch.Add(item);
                                if (currentBatch.Count >= 50)
                                {
                                    batchYield?.Report(currentBatch.ToList());
                                    currentBatch.Clear();
                                }
                            }
                            Interlocked.Increment(ref hitCount);
                            Interlocked.Add(ref totalHitBytes, item.SizeBytes);
                        }
                    }

                    long elapsed = sw.ElapsedMilliseconds;
                    if (elapsed - Volatile.Read(ref lastReportMs) > 150)
                    {
                        Volatile.Write(ref lastReportMs, elapsed);
                        progress?.Report(new SearchProgressReport
                        {
                            HitCount = Volatile.Read(ref hitCount),
                            ScannedCount = count,
                            TotalHitBytes = Volatile.Read(ref totalHitBytes),
                            CurrentPath = entry.FullPath,
                            Elapsed = sw.Elapsed,
                            IsCompleted = false
                        });
                    }
                }

                bool includeDirs = query.IncludeFolders || (query.IsDirectoryOnly == true);

                var entries = await SafeFileEnumerator.EnumerateFileEntriesParallelAsync(
                    targetFolder,
                    "*.*",
                    coverage: null,
                    onProgress: null,
                    ct: ct,
                    includeDirectories: includeDirs,
                    onEntryFound: HandleEntry);

                lock (batchLock)
                {
                    if (currentBatch.Count > 0)
                    {
                        batchYield?.Report(currentBatch.ToList());
                        currentBatch.Clear();
                    }
                }

                if (candidates.Count > 0)
                {
                    var contentResults = await FilterByContentAsync(candidates.ToList(), query, progress, ct, batchYield);
                    results.AddRange(contentResults);
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

        private static bool IsMatchBasic(FileItemNode node, SearchQuery query, out string reason, out bool needsDeepCheck)
        {
            reason = string.Empty;
            needsDeepCheck = false;

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

            if (query.CompiledRegex != null)
            {
                if (!query.CompiledRegex.IsMatch(name) && !query.CompiledRegex.IsMatch(fullPath)) return false;
            }

            // ★ Sol指摘: 「本文も検索」の名前 OR 本文意味論（複数キーワード対応）
            if (query.SearchContentMode && !node.IsDirectory && string.IsNullOrEmpty(query.ContentKeyword))
            {
                bool allInName = true;
                foreach (var kw in query.Keywords)
                {
                    string target = (kw.IndexOf('\\') >= 0 || kw.IndexOf('/') >= 0) ? fullPath : name;
                    if (target.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        allInName = false;
                        break;
                    }
                }

                if (allInName)
                {
                    // ファイル名/パスですべてのキーワードを満たしているなら即時合格（本文走査ゼロ）
                    needsDeepCheck = false;
                    reason = "Name";
                    return true;
                }
                else
                {
                    // 不足しているキーワードがある場合は本文検索の候補へ
                    needsDeepCheck = true;
                    reason = "Candidate for Content";
                    return true;
                }
            }
            else
            {
                // 通常検索、または明示的 content: 指定時
                foreach (var kw in query.Keywords)
                {
                    string target = (kw.IndexOf('\\') >= 0 || kw.IndexOf('/') >= 0) ? fullPath : name;
                    if (target.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) return false;
                }

                if (!node.IsDirectory && (
                    !string.IsNullOrEmpty(query.ContentKeyword) ||
                    query.HasOfficeLinkOnly ||
                    !string.IsNullOrEmpty(query.OfficeLinkKeyword)))
                {
                    needsDeepCheck = true;
                    reason = "Candidate for Deep I/O";
                }
                else
                {
                    needsDeepCheck = false;
                    if (string.IsNullOrEmpty(reason)) reason = "Match";
                }
                return true;
            }
        }

        private static bool IsMatchEntry(ScannedFileEntry entry, SearchQuery query, out string reason, out bool needsDeepCheck)
        {
            reason = string.Empty;
            needsDeepCheck = false;

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

            if (query.CompiledRegex != null)
            {
                if (!query.CompiledRegex.IsMatch(name) && !query.CompiledRegex.IsMatch(fullPath)) return false;
            }

            // ★ Sol指摘: 「本文も検索」の名前 OR 本文意味論（複数キーワード対応）
            if (query.SearchContentMode && !isDir && string.IsNullOrEmpty(query.ContentKeyword))
            {
                bool allInName = true;
                foreach (var kw in query.Keywords)
                {
                    string target = (kw.IndexOf('\\') >= 0 || kw.IndexOf('/') >= 0) ? fullPath : name;
                    if (target.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        allInName = false;
                        break;
                    }
                }

                if (allInName)
                {
                    needsDeepCheck = false;
                    reason = "Name";
                    return true;
                }
                else
                {
                    needsDeepCheck = true;
                    reason = "Candidate for Content";
                    return true;
                }
            }
            else
            {
                foreach (var kw in query.Keywords)
                {
                    string target = (kw.IndexOf('\\') >= 0 || kw.IndexOf('/') >= 0) ? fullPath : name;
                    if (target.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) return false;
                }

                if (!isDir && (
                    !string.IsNullOrEmpty(query.ContentKeyword) ||
                    query.HasOfficeLinkOnly ||
                    !string.IsNullOrEmpty(query.OfficeLinkKeyword)))
                {
                    needsDeepCheck = true;
                    reason = "Candidate for Deep I/O";
                }
                else
                {
                    needsDeepCheck = false;
                    if (string.IsNullOrEmpty(reason)) reason = "Match";
                }
                return true;
            }
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
            CancellationToken ct,
            IProgress<IReadOnlyList<SearchResultItem>>? batchYield = null)
        {
            var matched = new ConcurrentBag<SearchResultItem>();
            var progressiveBatch = new List<SearchResultItem>();
            var batchLock = new object();

            // Determine target search keyword
            string targetContent = query.ContentKeyword ?? string.Empty;
            if (string.IsNullOrEmpty(targetContent) && query.SearchContentMode && query.Keywords.Count > 0)
            {
                targetContent = query.Keywords[0];
            }

            bool hasOfficeLinkReq = query.HasOfficeLinkOnly;
            string? officeLinkKeyword = query.OfficeLinkKeyword;

            const long MaxSearchFileSize = 50L * 1024 * 1024;
            // ★ Sol提唱: 小さいファイル優先（Small-File First）
            // 100KBと40MBなら100KBから先に読み、0.1〜0.3秒で初期ヒットをUIへポンポン流す
            var validFiles = candidates
                .Where(c => !c.IsDirectory && c.SizeBytes <= MaxSearchFileSize && File.Exists(c.FullPath))
                .OrderBy(c => c.SizeBytes)
                .ToList();

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

            void EmitHit(SearchResultItem hit)
            {
                matched.Add(hit);
                if (batchYield != null)
                {
                    lock (batchLock)
                    {
                        progressiveBatch.Add(hit);
                        if (progressiveBatch.Count >= 5)
                        {
                            batchYield.Report(progressiveBatch.ToList());
                            progressiveBatch.Clear();
                        }
                    }
                }
            }

            await Parallel.ForEachAsync(validFiles, po, async (item, token) =>
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    string ext = Path.GetExtension(item.FullPath).ToLowerInvariant();

                    // ファイル名/パスに含まれていない不足キーワードを特定
                    List<string> requiredKeywords;
                    if (!string.IsNullOrEmpty(query.ContentKeyword))
                    {
                        requiredKeywords = new List<string> { query.ContentKeyword };
                    }
                    else if (query.SearchContentMode && query.Keywords.Count > 0)
                    {
                        requiredKeywords = query.Keywords
                            .Where(kw =>
                            {
                                bool hasSeparator = kw.Contains('\\') || kw.Contains('/');
                                string targetString = hasSeparator ? item.FullPath : item.Name;
                                return targetString.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0;
                            })
                            .ToList();
                    }
                    else
                    {
                        requiredKeywords = new List<string>();
                    }

                    // 不足キーワードが0件なら、名前/パスで既に完全一致しているので本文走査不要で即合格
                    if (requiredKeywords.Count == 0 && !hasOfficeLinkReq)
                    {
                        item.MatchedReason = "Name";
                        EmitHit(item);
                    }
                    else if (requiredKeywords.Count > 0)
                    {
                        // 1. Office (OpenXML: .xlsx, .xlsm, .docx, .pptx)
                        if (OfficeExtensions.Contains(ext))
                        {
                            if (ContentExtractionService.SearchOfficeContent(item.FullPath, requiredKeywords, out string snippet))
                            {
                                item.ContentSnippet = snippet;
                                item.MatchedReason = (query.Keywords.Count > requiredKeywords.Count)
                                    ? $"Name + Content: \"{string.Join(", ", requiredKeywords)}\""
                                    : $"Content: \"{string.Join(", ", requiredKeywords)}\"";
                                EmitHit(item);
                            }
                        }
                        // 2. PDF (.pdf) with Windows IFilter and pure C# fallback
                        else if (PdfExtensions.Contains(ext))
                        {
                            if (SearchPdfContentMultiple(item.FullPath, requiredKeywords, out string snippet))
                            {
                                item.ContentSnippet = snippet;
                                item.MatchedReason = (query.Keywords.Count > requiredKeywords.Count)
                                    ? $"Name + PDF Content: \"{string.Join(", ", requiredKeywords)}\""
                                    : $"PDF Content: \"{string.Join(", ", requiredKeywords)}\"";
                                EmitHit(item);
                            }
                        }
                        // 3. Text files (.txt, .csv, .log, .json, code files, etc.)
                        else if (TextExtensions.Contains(ext) || item.SizeBytes < 2 * 1024 * 1024)
                        {
                            if (await ContentExtractionService.SearchTextContentAsync(item.FullPath, requiredKeywords, token) is { } snippet)
                            {
                                item.ContentSnippet = snippet;
                                item.MatchedReason = (query.Keywords.Count > requiredKeywords.Count)
                                    ? $"Name + Content: \"{string.Join(", ", requiredKeywords)}\""
                                    : $"Content: \"{string.Join(", ", requiredKeywords)}\"";
                                EmitHit(item);
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
                            EmitHit(item);
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

            lock (batchLock)
            {
                if (progressiveBatch.Count > 0 && batchYield != null)
                {
                    batchYield.Report(progressiveBatch.ToList());
                    progressiveBatch.Clear();
                }
            }

            return matched.ToList();
        }

        private static bool SearchPdfContentMultiple(string filePath, IReadOnlyList<string> keywords, out string snippet)
        {
            snippet = string.Empty;
            string firstSnippet = string.Empty;
            foreach (var kw in keywords)
            {
                if (!PdfSearchHelper.SearchPdfContent(filePath, kw, out string s))
                {
                    return false;
                }
                if (string.IsNullOrEmpty(firstSnippet)) firstSnippet = s;
            }
            snippet = firstSnippet;
            return true;
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
                            snippet = ContentExtractionService.ExtractSnippet(text, idx, keyword.Length);
                            return true;
                        }
                    }
                }
            }
            catch { }
            return false;
        }
    }
}
