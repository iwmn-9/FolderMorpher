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

                if (candidates.Count > 0)
                {
                    var contentResults = await FilterByContentAsync(candidates, query, progress, ct);
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
                        }
                    }

                    long elapsed = sw.ElapsedMilliseconds;
                    if (elapsed - Volatile.Read(ref lastReportMs) > 150)
                    {
                        Volatile.Write(ref lastReportMs, elapsed);
                        progress?.Report(new SearchProgressReport
                        {
                            HitCount = results.Count + candidates.Count,
                            ScannedCount = count,
                            TotalHitBytes = results.Sum(r => r.SizeBytes) + candidates.Sum(r => r.SizeBytes),
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
                    var contentResults = await FilterByContentAsync(candidates.ToList(), query, progress, ct);
                    results.AddRange(contentResults);
                    batchYield?.Report(contentResults);
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

                    // ファイル名/パスに含まれていない不足キーワードを特定
                    List<string> requiredKeywords;
                    if (!string.IsNullOrEmpty(query.ContentKeyword))
                    {
                        requiredKeywords = new List<string> { query.ContentKeyword };
                    }
                    else if (query.SearchContentMode && query.Keywords.Count > 0)
                    {
                        requiredKeywords = query.Keywords
                            .Where(kw => (item.Name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0 &&
                                          item.FullPath.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0))
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
                        matched.Add(item);
                    }
                    else if (requiredKeywords.Count > 0)
                    {
                        // 1. Office (OpenXML: .xlsx, .xlsm, .docx, .pptx)
                        if (OfficeExtensions.Contains(ext))
                        {
                            if (SearchOfficeFileContent(item.FullPath, requiredKeywords, out string snippet))
                            {
                                item.ContentSnippet = snippet;
                                item.MatchedReason = (query.Keywords.Count > requiredKeywords.Count)
                                    ? $"Name + Content: \"{string.Join(", ", requiredKeywords)}\""
                                    : $"Content: \"{string.Join(", ", requiredKeywords)}\"";
                                matched.Add(item);
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
                                matched.Add(item);
                            }
                        }
                        // 3. Text files (.txt, .csv, .log, .json, code files, etc.)
                        else if (TextExtensions.Contains(ext) || item.SizeBytes < 2 * 1024 * 1024)
                        {
                            if (await SearchTextFileContentAsync(item.FullPath, requiredKeywords, token) is { } snippet)
                            {
                                item.ContentSnippet = snippet;
                                item.MatchedReason = (query.Keywords.Count > requiredKeywords.Count)
                                    ? $"Name + Content: \"{string.Join(", ", requiredKeywords)}\""
                                    : $"Content: \"{string.Join(", ", requiredKeywords)}\"";
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

        private static bool SearchOfficeFileContent(string filePath, IReadOnlyList<string> keywords, out string snippet)
        {
            snippet = string.Empty;
            if (keywords.Count == 0) return false;

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

                        bool allFound = true;
                        string firstSnippet = string.Empty;
                        foreach (var kw in keywords)
                        {
                            int idx = text.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                if (string.IsNullOrEmpty(firstSnippet)) firstSnippet = ExtractSnippet(text, idx, kw.Length);
                            }
                            else
                            {
                                allFound = false;
                                break;
                            }
                        }

                        if (allFound)
                        {
                            snippet = firstSnippet;
                            return true; // sharedStrings で全キーワードが揃ったので Early exit!
                        }

                        // ★ Sol指摘: sharedStrings に存在しない場合でも、inline string や数式文字列が
                        // sheet*.xml に直接存在する可能性があるため、即座に false を返さず後続の XML 探索へ進む！
                    }
                }

                // Word (.docx), PowerPoint (.pptx), または sharedStrings だけでは見つからなかった Excel の探索
                var foundKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string firstFoundSnippet = string.Empty;

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
                    foreach (var kw in keywords)
                    {
                        if (!foundKeywords.Contains(kw))
                        {
                            int idx = text.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                foundKeywords.Add(kw);
                                if (string.IsNullOrEmpty(firstFoundSnippet)) firstFoundSnippet = ExtractSnippet(text, idx, kw.Length);
                            }
                        }
                    }

                    if (foundKeywords.Count == keywords.Count)
                    {
                        snippet = firstFoundSnippet;
                        return true; // 全キーワード揃ったので Early exit!
                    }

                    // 2. Word等のrun分割 (<w:t>A</w:t><w:t>B</w:t>) 対策でXMLタグ除去して探索
                    if (entryName.Contains("document") || entryName.Contains("slide") || entryName.Contains("sheet"))
                    {
                        string stripped = Regex.Replace(text, @"<[^>]+>", "");
                        foreach (var kw in keywords)
                        {
                            if (!foundKeywords.Contains(kw))
                            {
                                int strippedIdx = stripped.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                                if (strippedIdx >= 0)
                                {
                                    foundKeywords.Add(kw);
                                    if (string.IsNullOrEmpty(firstFoundSnippet)) firstFoundSnippet = ExtractSnippet(stripped, strippedIdx, kw.Length);
                                }
                            }
                        }

                        if (foundKeywords.Count == keywords.Count)
                        {
                            snippet = firstFoundSnippet;
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

        private static async Task<string?> SearchTextFileContentAsync(string filePath, IReadOnlyList<string> keywords, CancellationToken ct)
        {
            if (keywords.Count == 0) return null;

            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);

                // アイデア 2: 先頭4KBでのバイナリ早期脱落 (Early Drop) - UTF-16 / BOM 安全保護
                byte[] headBuffer = new byte[4096];
                int readHead = await fs.ReadAsync(headBuffer, 0, headBuffer.Length, ct);
                if (readHead > 0)
                {
                    // BOM チェック
                    bool isUtf16Le = (readHead >= 2 && headBuffer[0] == 0xFF && headBuffer[1] == 0xFE);
                    bool isUtf16Be = (readHead >= 2 && headBuffer[0] == 0xFE && headBuffer[1] == 0xFF);
                    bool isUtf8Bom = (readHead >= 3 && headBuffer[0] == 0xEF && headBuffer[1] == 0xBB && headBuffer[2] == 0xBF);

                    // UTF-16 以外の一般ファイルのみ NULL バイト検査でバイナリを判定
                    if (!isUtf16Le && !isUtf16Be)
                    {
                        // BOMなし UTF-16LE ヒューリスティック判定 (奇数バイトにNULLが多発する典型的なUnicodeテキスト)
                        bool likelyUtf16 = false;
                        if (readHead >= 8)
                        {
                            int zerosOnOdds = 0;
                            int zerosOnEvens = 0;
                            int sampleCount = Math.Min(readHead, 256);
                            for (int i = 0; i < sampleCount; i++)
                            {
                                if (headBuffer[i] == 0)
                                {
                                    if (i % 2 == 1) zerosOnOdds++;
                                    else zerosOnEvens++;
                                }
                            }
                            if (zerosOnOdds > (sampleCount / 4) && zerosOnEvens == 0) likelyUtf16 = true;
                        }

                        if (!likelyUtf16)
                        {
                            int nullCount = 0;
                            for (int i = 0; i < readHead; i++)
                            {
                                if (headBuffer[i] == 0) nullCount++;
                            }
                            // 純粋なバイナリファイルは早期脱落
                            if (nullCount >= 2) return null;
                        }
                    }

                    // ストリームを先頭に戻す
                    fs.Seek(0, SeekOrigin.Begin);
                }

                using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

                var foundKeywords = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                string? firstSnippet = null;

                string? line;
                while ((line = await reader.ReadLineAsync(ct)) != null)
                {
                    foreach (var kw in keywords)
                    {
                        if (!foundKeywords.Contains(kw))
                        {
                            int idx = line.IndexOf(kw, StringComparison.OrdinalIgnoreCase);
                            if (idx >= 0)
                            {
                                foundKeywords.Add(kw);
                                if (firstSnippet == null)
                                {
                                    firstSnippet = ExtractSnippet(line, idx, kw.Length);
                                }
                            }
                        }
                    }

                    // アイデア 3: 全キーワードが揃った瞬間に即座に読み込みを中断して復帰 (Early Exit)
                    if (foundKeywords.Count == keywords.Count)
                    {
                        return firstSnippet ?? keywords[0];
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
