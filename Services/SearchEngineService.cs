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
            if (query.IsEmpty) return true;

            if (query.IsDirectoryOnly.HasValue)
            {
                if (query.IsDirectoryOnly.Value && !node.IsDirectory) return false;
                if (!query.IsDirectoryOnly.Value && node.IsDirectory) return false;
            }

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
                if (fullPath.IndexOf(exc, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }

            foreach (var phr in query.ExactPhrases)
            {
                if (fullPath.IndexOf(phr, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }

            foreach (var kw in query.Keywords)
            {
                if (fullPath.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) return false;
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
            if (query.IsEmpty) return true;

            bool isDir = entry.Attributes.HasFlag(FileAttributes.Directory);
            if (query.IsDirectoryOnly.HasValue)
            {
                if (query.IsDirectoryOnly.Value && !isDir) return false;
                if (!query.IsDirectoryOnly.Value && isDir) return false;
            }

            string name = entry.Name;
            string fullPath = entry.FullPath;
            string ext = isDir ? string.Empty : Path.GetExtension(name).ToLowerInvariant();

            if (query.Extensions.Count > 0)
            {
                if (isDir || !query.Extensions.Contains(ext)) return false;
            }

            if (isDir)
            {
                if (query.MinSizeBytes.HasValue || query.MaxSizeBytes.HasValue) return false;
            }
            else
            {
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
                if (fullPath.IndexOf(exc, StringComparison.OrdinalIgnoreCase) >= 0) return false;
            }

            foreach (var phr in query.ExactPhrases)
            {
                if (fullPath.IndexOf(phr, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }

            foreach (var kw in query.Keywords)
            {
                if (fullPath.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) return false;
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
            string targetContent = query.ContentKeyword ?? string.Empty;
            string targetLink = query.OfficeLinkKeyword ?? string.Empty;

            const long MaxSearchFileSize = 50L * 1024 * 1024;
            var validFiles = candidates.Where(c => !c.IsDirectory && c.SizeBytes <= MaxSearchFileSize && File.Exists(c.FullPath)).ToList();

            int processed = 0;
            int total = validFiles.Count;

            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount / 2),
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
                        if (OfficeExtensions.Contains(ext))
                        {
                            if (SearchOfficeFileContent(item.FullPath, targetContent, out string snippet))
                            {
                                item.ContentSnippet = snippet;
                                item.MatchedReason = $"Content: \"{targetContent}\"";
                                matched.Add(item);
                            }
                        }
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
                    else if (!string.IsNullOrEmpty(targetLink) && OfficeExtensions.Contains(ext))
                    {
                        if (SearchOfficeFileContent(item.FullPath, targetLink, out string snippet))
                        {
                            item.ContentSnippet = snippet;
                            item.MatchedReason = $"OfficeLink: \"{targetLink}\"";
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
                        CurrentPath = $"Content Search ({c}/{total}): {item.Name}",
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
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, false);

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

                    int idx = text.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                    if (idx >= 0)
                    {
                        snippet = ExtractSnippet(text, idx, keyword.Length);
                        return true;
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
                using var reader = new StreamReader(fs, Encoding.UTF8, true);

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
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