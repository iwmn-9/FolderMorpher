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
using System.Threading.Channels;
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
        /// 画面・メモリ保持する最大ヒット件数（ADR 141）。
        /// Filunest流 有界保持設計により、数十万件ヒット時でもメモリ逼迫を防ぎ、総ヒット数のみ正確に集計。
        /// </summary>
        public const int MaxRetainedResults = 10_000;

        /// <summary>
        /// Perform instant in-memory search on pre-scanned trees
        /// </summary>
        public Task<List<SearchResultItem>> SearchInMemoryAsync(
            IEnumerable<FileItemNode> rootNodes,
            SearchQuery query,
            IProgress<SearchProgressReport>? progress = null,
            CancellationToken ct = default,
            IProgress<IReadOnlyList<SearchResultItem>>? batchYield = null)
        {
            return SearchEntriesAsync(EnumerateTreeEntries(rootNodes), query, progress, ct, batchYield);
        }

        public Task<List<SearchResultItem>> SearchCachedEntriesAsync(
            IEnumerable<TreeCacheSearchEntry> entries,
            SearchQuery query,
            IProgress<SearchProgressReport>? progress = null,
            CancellationToken ct = default,
            IProgress<IReadOnlyList<SearchResultItem>>? batchYield = null)
        {
            return SearchEntriesAsync(entries, query, progress, ct, batchYield);
        }

        private static IEnumerable<TreeCacheSearchEntry> EnumerateTreeEntries(IEnumerable<FileItemNode> roots)
        {
            var stack = new Stack<FileItemNode>();
            foreach (var root in roots.Reverse()) stack.Push(root);
            while (stack.Count > 0)
            {
                var node = stack.Pop();
                yield return new TreeCacheSearchEntry(
                    node.FullPath, node.Name, node.SizeBytes, node.IsDirectory,
                    node.LastModified, node.CreationTime);
                for (int i = node.Children.Count - 1; i >= 0; i--) stack.Push(node.Children[i]);
            }
        }

        private static async Task<List<SearchResultItem>> SearchEntriesAsync(
            IEnumerable<TreeCacheSearchEntry> entries,
            SearchQuery query,
            IProgress<SearchProgressReport>? progress,
            CancellationToken ct,
            IProgress<IReadOnlyList<SearchResultItem>>? batchYield)
        {
            return await Task.Run(async () =>
            {
                var sw = Stopwatch.StartNew();
                string ocrWarning = string.Empty;
                var results = new List<SearchResultItem>();
                int scannedCount = 0;
                int totalNameHits = 0, contentHitCount = 0, ocrHitCount = 0;
                long totalMatchedBytes = 0;
                var allHitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var candidates = new List<SearchResultItem>();
                var ocrCandidates = new List<SearchResultItem>();

                var currentBatch = new List<SearchResultItem>();

                foreach (var node in entries)
                {
                    ct.ThrowIfCancellationRequested();
                    scannedCount++;

                    string nodeExt = node.IsDirectory ? string.Empty : Path.GetExtension(node.Name).ToLowerInvariant();
                    if (query.IncludeOcr && !node.IsDirectory && OcrWorkerService.IsSupportedOcrExtension(nodeExt) && IsMatchBasic(node, query, out _, out bool ocrNeedsDeepCheck) && ocrNeedsDeepCheck)
                    {
                        ocrCandidates.Add(new SearchResultItem
                        {
                            Name = node.Name,
                            FullPath = node.FullPath,
                            DirectoryPath = Path.GetDirectoryName(node.FullPath) ?? string.Empty,
                            SizeBytes = node.SizeBytes,
                            LastWriteTime = node.GetLastModified() ?? DateTime.MinValue,
                            CreationTime = node.GetCreationTime() ?? node.GetLastModified() ?? DateTime.MinValue,
                            Extension = nodeExt,
                            IsDirectory = false
                        });
                    }

                    if (IsMatchBasic(node, query, out string matchReason, out bool needsDeepCheck))
                    {
                        var item = new SearchResultItem
                        {
                            Name = node.Name,
                            FullPath = node.FullPath,
                            DirectoryPath = Path.GetDirectoryName(node.FullPath) ?? string.Empty,
                            SizeBytes = node.SizeBytes,
                            LastWriteTime = node.GetLastModified() ?? DateTime.MinValue,
                            CreationTime = node.GetCreationTime() ?? node.GetLastModified() ?? DateTime.MinValue,
                            Extension = nodeExt,
                            IsDirectory = node.IsDirectory,
                            MatchedReason = matchReason
                        };

                        if (needsDeepCheck)
                        {
                            candidates.Add(item);
                        }
                        else
                        {
                            totalNameHits++;
                            totalMatchedBytes += item.SizeBytes;

                            if (results.Count < MaxRetainedResults)
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
                    }

                }

                if (currentBatch.Count > 0 && batchYield != null)
                {
                    batchYield.Report(currentBatch.ToList());
                    currentBatch.Clear();
                }

                if (candidates.Count > 0)
                {
                    var contentResults = await FilterByContentAsync(
                        candidates,
                        query,
                        progress,
                        ct,
                        batchYield,
                        sw,
                        initialHitCount: totalNameHits + contentHitCount,
                        initialTotalBytes: totalMatchedBytes,
                        scannedCount: scannedCount);
                    contentHitCount = contentResults.Count;
                    totalMatchedBytes += contentResults.Sum(i => i.SizeBytes);
                    if (query.IncludeOcr) allHitPaths.UnionWith(contentResults.Where(i => OcrWorkerService.IsSupportedOcrExtension(i.Extension)).Select(i => i.FullPath));
                    results.AddRange(contentResults.Take(Math.Max(0, MaxRetainedResults - results.Count)));
                }

                // ★ ADR 144: 第2ロケット（遅延OCR走査）
                // 通常検索（第1ロケット）を先行返却後、未ヒットの画像・PDFに対してOCR遅延走査を実行
                if (query.IncludeOcr && ocrCandidates.Count > 0)
                {
                    var existingHitPaths = allHitPaths;
                    var ocrHits = await ExecuteOcrStageAsync(
                        ocrCandidates,
                        existingHitPaths,
                        query,
                        batchYield,
                        progress,
                        ct,
                        sw,
                        initialScannedCount: scannedCount,
                        initialHitCount: totalNameHits + contentHitCount,
                        initialTotalBytes: totalMatchedBytes, recordWarning: w => ocrWarning = w);
                    ocrHitCount = ocrHits.Count;
                    totalMatchedBytes += ocrHits.Sum(i => i.SizeBytes);
                    results.AddRange(ocrHits.Take(Math.Max(0, MaxRetainedResults - results.Count)));
                }

                sw.Stop();
                progress?.Report(new SearchProgressReport
                {
                    OcrWarning = ocrWarning,
                    HitCount = totalNameHits + contentHitCount + ocrHitCount,
                    ScannedCount = scannedCount,
                    TotalHitBytes = totalMatchedBytes,
                    Elapsed = sw.Elapsed,
                    IsCompleted = true
                });

                return results;
            }, ct);
        }

        /// <summary>
        /// Direct progressive streaming traversal for unscanned folders or shares
        /// 【Agent Ransack流】Producer-Consumer Channel パイプラインにより、列挙と本文走査を完全並行化。
        /// 走査開始から数百ミリ秒で1件目の本文ヒットをUIへ即座にストリーミング。
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
                string ocrWarning = string.Empty;
                var results = new List<SearchResultItem>();
                var currentBatch = new List<SearchResultItem>();
                var batchLock = new object();
                int scannedCount = 0;
                int hitCount = 0;
                int unreadFiles = 0;
                long totalHitBytes = 0;
                long lastReportMs = 0;
                var coverage = new ScanCoverage();
                await using var treeCapture = await TreeScanCapture.TryStartAsync(targetFolder, ct);

                (int discovered, int completed) DirectoryProgress()
                {
                    lock (coverage) return (coverage.DiscoveredFolders, coverage.CompletedFolders);
                }

                // BoundedChannel & Backpressure (最大2048件バッファでメモリ浪費防止)
                var contentChannel = Channel.CreateBounded<SearchResultItem>(
                    new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.Wait, SingleWriter = false, SingleReader = false });

                var ocrCandidates = new ConcurrentBag<SearchResultItem>();
                var completedOcrPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                bool hasOfficeLinkReq = query.HasOfficeLinkOnly;
                string? officeLinkKeyword = query.OfficeLinkKeyword;

                void EmitHit(SearchResultItem hit)
                {
                    lock (batchLock)
                    {
                        if (query.IncludeOcr && OcrWorkerService.IsSupportedOcrExtension(hit.Extension)) completedOcrPaths.Add(hit.FullPath);
                        if (results.Count < MaxRetainedResults)
                        {
                            results.Add(hit);
                            currentBatch.Add(hit);
                            int threshold = results.Count <= 3 ? 1 : (results.Count <= 30 ? 5 : 25);
                            if (currentBatch.Count >= threshold)
                            {
                                batchYield?.Report(currentBatch.ToList());
                                currentBatch.Clear();
                            }
                        }
                    }
                    Interlocked.Increment(ref hitCount);
                    Interlocked.Add(ref totalHitBytes, hit.SizeBytes);
                }

                // Consumer ワーカー群の起動（Shared I/O Governor による UNC Root 単位の適応並列制御）
                int workerCount = Math.Max(4, Math.Min(Environment.ProcessorCount * 2, AdaptiveConcurrencyController.ContentMaxConcurrency));
                var governor = SharedIoGovernor.GetGovernor(targetFolder);
                var controller = governor.ContentController;
                int deepProcessed = 0;
                var deferredLargeFiles = new System.Collections.Concurrent.ConcurrentBag<SearchResultItem>();

                var sharedContext = CreateSearchContext(query, targetFolder);

                var consumerTasks = Enumerable.Range(0, workerCount).Select(async _ =>
                {
                    while (await contentChannel.Reader.WaitToReadAsync(ct))
                    {
                        while (contentChannel.Reader.TryRead(out var item))
                        {
                            if (ct.IsCancellationRequested) return;

                            using var lease = await controller.AcquireAsync(ct);
                            double measuredIoMs = 0;
                            bool isError = false;

                            try
                            {
                                var (isHit, isDeferred) = await InspectContentItemAsync(
                                    item,
                                    query,
                                    hasOfficeLinkReq,
                                    officeLinkKeyword,
                                    ct,
                                    allowDeferred: true,
                                    sharedContext: sharedContext,
                                    reportIoElapsed: ms => measuredIoMs = ms);

                                if (isHit)
                                {
                                    EmitHit(item);
                                }
                                else if (isDeferred)
                                {
                                    deferredLargeFiles.Add(item);
                                }
                            }
                            catch (Exception ex)
                            {
                                Interlocked.Increment(ref unreadFiles);
                                isError = ex is System.Net.Sockets.SocketException ||
                                          (ex is IOException ioEx && (ioEx.HResult == unchecked((int)0x8007003B) || ioEx.HResult == unchecked((int)0x80070040) || ioEx.HResult == unchecked((int)0x80070036)));
                            }
                            finally
                            {
                                // ★ ADR 98: CPU解析時間を除外した「純粋なI/O時間」のみをGovernorへ報告（誤った崖落ち防止）
                                lease.Report(measuredIoMs > 0 ? measuredIoMs : 15.0, isError);
                            }

                            int dp = Interlocked.Increment(ref deepProcessed);
                            long elapsed = sw.ElapsedMilliseconds;
                            if (elapsed - Volatile.Read(ref lastReportMs) > 100)
                            {
                                Volatile.Write(ref lastReportMs, elapsed);
                                var (discovered, completed) = DirectoryProgress();
                                progress?.Report(new SearchProgressReport
                                {
                                    HitCount = Volatile.Read(ref hitCount),
                                    ScannedCount = Volatile.Read(ref scannedCount),
                                    ContentProcessedCount = dp,
                                    DiscoveredDirectories = discovered,
                                    ProcessedDirectories = completed,
                                    TotalHitBytes = Volatile.Read(ref totalHitBytes),
                                    CurrentPath = $"📄 Deep Search: {item.Name}",
                                    Elapsed = sw.Elapsed,
                                    IsCompleted = false
                                });
                            }
                        }
                    }
                }).ToArray();

                void HandleEntry(ScannedFileEntry entry)
                {
                    if (ct.IsCancellationRequested) return;
                    int count = Interlocked.Increment(ref scannedCount);

                    bool isEntryDir = entry.Attributes.HasFlag(FileAttributes.Directory);
                    if (query.IncludeOcr && !isEntryDir)
                    {
                        string entryExt = Path.GetExtension(entry.Name).ToLowerInvariant();
                        if (OcrWorkerService.IsSupportedOcrExtension(entryExt) && IsMatchEntry(entry, query, out _, out _))
                        {
                            ocrCandidates.Add(new SearchResultItem
                            {
                                Name = entry.Name,
                                FullPath = entry.FullPath,
                                DirectoryPath = entry.DirectoryPath,
                                SizeBytes = entry.Length,
                                LastWriteTime = entry.LastWriteTime,
                                CreationTime = entry.CreationTime,
                                Extension = entryExt,
                                IsDirectory = false
                            });
                        }
                    }

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
                            CreationTime = entry.CreationTime,
                            Extension = isDir ? string.Empty : Path.GetExtension(entry.Name).ToLowerInvariant(),
                            IsDirectory = isDir,
                            MatchedReason = matchReason
                        };

                        if (needsDeepCheck)
                        {
                            if (!item.IsDirectory)
                            {
                                if (!contentChannel.Writer.TryWrite(item))
                                {
                                    try
                                    {
                                        contentChannel.Writer.WriteAsync(item, ct).AsTask().GetAwaiter().GetResult();
                                    }
                                    catch (OperationCanceledException) { }
                                }
                            }
                        }
                        else
                        {
                            EmitHit(item);
                        }
                    }

                    long elapsed = sw.ElapsedMilliseconds;
                    if (elapsed - Volatile.Read(ref lastReportMs) > 150)
                    {
                        Volatile.Write(ref lastReportMs, elapsed);
                        var (discovered, completed) = DirectoryProgress();
                        progress?.Report(new SearchProgressReport
                        {
                            HitCount = Volatile.Read(ref hitCount),
                            ScannedCount = count,
                            ContentProcessedCount = Volatile.Read(ref deepProcessed),
                            DiscoveredDirectories = discovered,
                            ProcessedDirectories = completed,
                            TotalHitBytes = Volatile.Read(ref totalHitBytes),
                            CurrentPath = entry.FullPath,
                            Elapsed = sw.Elapsed,
                            IsCompleted = false
                        });
                    }
                }

                bool includeDirs = query.IncludeFolders || (query.IsDirectoryOnly == true);

                // ★ ADR 94: Server Search Accelerator（サーバー側インデックス拝借 ＆ 候補ピンポイント原本確認）
                // Windows Server (WSP) または WSP 互換 NAS (Synology等) がインデックスを公開していれば、
                // 数万〜数十万ファイルのディレクトリ全走査をスキップし、返された候補（例: 83件）のみを即座に原本確認する。
                var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // ★ ADR 94 & ADR 97: Server Search Accelerator（先行ブースト ＆ 完全網羅走査）
                // サーバー側インデックス（WSP等）から候補が得られた場合、まずその候補群を「先行原本確認」として
                // 最優先でパイプラインへ投入し、初期結果を秒速でユーザーへ提示する。
                // ただし外部インデックスの件数上限（2000件）や語境界による取りこぼしを防ぐため、
                // 通常のディレクトリ走査も省略せずに継続実行し、先行投入済みのパスは重複スキップ（seenPaths）する。
                IReadOnlyList<string>? serverCandidates = null;
                try
                {
                    serverCandidates = await FolderMorpher.Services.ServerSearch.ServerSearchAccelerator.Instance.TryAccelerateAsync(targetFolder, query, ct);
                }
                catch { }

                if (serverCandidates != null && serverCandidates.Count > 0)
                {
                    for (int i = 0; i < serverCandidates.Count; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        string candPath = serverCandidates[i];
                        if (string.IsNullOrEmpty(candPath) || !seenPaths.Add(candPath)) continue;
                        if (!File.Exists(candPath)) continue;

                        try
                        {
                            var fi = new FileInfo(candPath);
                            var entry = new ScannedFileEntry(
                                fi.FullName,
                                fi.Name,
                                Path.GetDirectoryName(fi.FullName) ?? string.Empty,
                                fi.Length,
                                fi.CreationTimeUtc.ToLocalTime(),
                                fi.LastWriteTimeUtc.ToLocalTime(),
                                fi.LastAccessTimeUtc.ToLocalTime(),
                                fi.Attributes);

                            HandleEntry(entry);
                        }
                        catch { }
                    }
                }

                try
                {
                    await SafeFileEnumerator.EnumerateFileEntriesParallelAsync(
                        targetFolder,
                        "*.*",
                        coverage: coverage,
                        onProgress: null,
                        ct: ct,
                        includeDirectories: includeDirs,
                        onEntryFound: entry =>
                        {
                            // サーバー側候補として先行投入済みのパスは二重走査をスキップ
                            if (seenPaths.Count > 0 && seenPaths.Contains(entry.FullPath))
                            {
                                return;
                            }
                            HandleEntry(entry);
                        },
                        collectResults: false,
                        onDiscoveredEntry: treeCapture == null ? null : entry =>
                        {
                            // 欠けたツリーは公開できない。最初のアクセス拒否後は
                            // 原本の検索を続けつつ一時DBへの投入だけ止める。
                            if (coverage.IsCompleteCoverage) treeCapture.Add(entry);
                        });
                }
                finally
                {
                    contentChannel.Writer.Complete();
                }

                // Consumer ワーカーの完了を待機
                await Task.WhenAll(consumerTasks);

                // 小ファイルを先に返してから巨大ファイルを少数並列で全文検査する。
                if (!deferredLargeFiles.IsEmpty)
                {
                    await Parallel.ForEachAsync(deferredLargeFiles, new ParallelOptions
                    {
                        MaxDegreeOfParallelism = 2,
                        CancellationToken = ct
                    }, async (dItem, token) =>
                    {
                        using var lease = await controller.AcquireAsync(token);
                        double measuredIoMs = 0;
                        bool isError = false;
                        try
                        {
                            var (isHit, _) = await InspectContentItemAsync(dItem, query, hasOfficeLinkReq, officeLinkKeyword,
                                token, allowDeferred: false, sharedContext: sharedContext,
                                reportIoElapsed: ms => measuredIoMs = ms, skipCompletedProbe: true);
                            if (isHit)
                            {
                                EmitHit(dItem);
                            }
                        }
                        catch (Exception ex)
                        {
                            Interlocked.Increment(ref unreadFiles);
                            isError = ex is System.Net.Sockets.SocketException ||
                                      (ex is IOException ioEx && (ioEx.HResult == unchecked((int)0x8007003B) || ioEx.HResult == unchecked((int)0x80070040) || ioEx.HResult == unchecked((int)0x80070036)));
                        }
                        finally
                        {
                            lease.Report(measuredIoMs > 0 ? measuredIoMs : 15.0, isError);
                        }
                    });
                }

                lock (batchLock)
                {
                    if (currentBatch.Count > 0)
                    {
                        batchYield?.Report(currentBatch.ToList());
                        currentBatch.Clear();
                    }
                }

                // ★ ADR 144: 第2ロケット（遅延OCR走査）
                if (query.IncludeOcr && !ocrCandidates.IsEmpty)
                {
                    var existingHitPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    lock (batchLock)
                    {
                        existingHitPaths.UnionWith(completedOcrPaths);
                    }

                    var ocrHits = await ExecuteOcrStageAsync(
                        ocrCandidates.ToList(),
                        existingHitPaths,
                        query,
                        batchYield,
                        progress,
                        ct,
                        sw,
                        initialScannedCount: scannedCount,
                        initialHitCount: Volatile.Read(ref hitCount),
                        initialTotalBytes: Interlocked.Read(ref totalHitBytes), recordWarning: w => ocrWarning = w);

                    lock (batchLock)
                    {
                        results.AddRange(ocrHits.Take(Math.Max(0, MaxRetainedResults - results.Count)));
                    }
                    Interlocked.Add(ref hitCount, ocrHits.Count);
                    Interlocked.Add(ref totalHitBytes, ocrHits.Sum(h => h.SizeBytes));
                }

                if (treeCapture != null)
                {
                    try { await treeCapture.PublishIfCompleteAsync(coverage, ct); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { Trace.WriteLine($"Search tree cache capture failed: {ex}"); }
                }
                ct.ThrowIfCancellationRequested();
                sw.Stop();
                var (finalDiscovered, finalCompleted) = DirectoryProgress();
                progress?.Report(new SearchProgressReport
                {
                    OcrWarning = ocrWarning,
                    HitCount = Volatile.Read(ref hitCount),
                    ScannedCount = scannedCount,
                    ContentProcessedCount = Volatile.Read(ref deepProcessed),
                    DiscoveredDirectories = finalDiscovered,
                    ProcessedDirectories = finalCompleted,
                    AccessDeniedFolders = coverage.AccessDeniedFolders,
                    UnreadFiles = Volatile.Read(ref unreadFiles),
                    TotalHitBytes = Interlocked.Read(ref totalHitBytes),
                    Elapsed = sw.Elapsed,
                    IsCompleted = true
                });

                return results;
            }, ct);
        }

        private static bool IsMatchBasic(TreeCacheSearchEntry node, SearchQuery query, out string reason, out bool needsDeepCheck)
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
            bool needsExtension = query.Extensions.Count > 0 || query.SearchContentMode ||
                query.HasOfficeLinkOnly || !string.IsNullOrEmpty(query.OfficeLinkKeyword) ||
                !string.IsNullOrEmpty(query.ContentKeyword);
            string ext = node.IsDirectory || !needsExtension ? string.Empty : Path.GetExtension(name).ToLowerInvariant();

            if (query.Extensions.Count > 0)
            {
                if (node.IsDirectory || !query.Extensions.Contains(ext)) return false;
            }

            if (query.MinSizeBytes.HasValue && node.SizeBytes < query.MinSizeBytes.Value) return false;
            if (query.MaxSizeBytes.HasValue && node.SizeBytes > query.MaxSizeBytes.Value) return false;

            if (query.MinModifiedUtc.HasValue || query.MaxModifiedUtc.HasValue)
            {
                var modified = node.GetLastModified();
                if (!modified.HasValue) return false;
                var utc = modified.Value.ToUniversalTime();
                if (query.MinModifiedUtc.HasValue && utc < query.MinModifiedUtc.Value) return false;
                if (query.MaxModifiedUtc.HasValue && utc > query.MaxModifiedUtc.Value) return false;
            }

            return MatchesSearchTerms(name, fullPath, ext, node.IsDirectory, query, out reason, out needsDeepCheck);
        }

        private static bool MatchesKeywordGroups(string name, string fullPath, SearchQuery query)
        {
            foreach (var phr in query.ExactPhrases)
            {
                string target = (phr.IndexOf('\\') >= 0 || phr.IndexOf('/') >= 0) ? fullPath : name;
                if (target.IndexOf(phr, StringComparison.OrdinalIgnoreCase) < 0) return false;
            }

            if (query.KeywordGroups.Count > 0)
            {
                foreach (var group in query.KeywordGroups)
                {
                    if (group.Count == 0) continue;
                    bool groupMatch = false;
                    foreach (var kw in group)
                    {
                        string target = (kw.IndexOf('\\') >= 0 || kw.IndexOf('/') >= 0) ? fullPath : name;
                        if (target.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            groupMatch = true;
                            break;
                        }
                    }
                    if (!groupMatch) return false;
                }
                return true;
            }
            else
            {
                foreach (var kw in query.Keywords)
                {
                    string target = (kw.IndexOf('\\') >= 0 || kw.IndexOf('/') >= 0) ? fullPath : name;
                    if (target.IndexOf(kw, StringComparison.OrdinalIgnoreCase) < 0) return false;
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
            bool needsExtension = query.Extensions.Count > 0 || query.SearchContentMode ||
                query.HasOfficeLinkOnly || !string.IsNullOrEmpty(query.OfficeLinkKeyword) ||
                !string.IsNullOrEmpty(query.ContentKeyword);
            string ext = isDir || !needsExtension ? string.Empty : Path.GetExtension(name).ToLowerInvariant();

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

            if (query.MinModifiedUtc.HasValue || query.MaxModifiedUtc.HasValue)
            {
                var utc = entry.LastWriteTime.ToUniversalTime();
                if (query.MinModifiedUtc.HasValue && utc < query.MinModifiedUtc.Value) return false;
                if (query.MaxModifiedUtc.HasValue && utc > query.MaxModifiedUtc.Value) return false;
            }

            return MatchesSearchTerms(name, fullPath, ext, isDir, query, out reason, out needsDeepCheck);
        }

        private static bool MatchesSearchTerms(
            string name, string fullPath, string ext, bool isDir, SearchQuery query,
            out string reason, out bool needsDeepCheck)
        {
            reason = string.Empty;
            needsDeepCheck = false;
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

            if (!query.SearchContentMode)
            {
                foreach (var phr in query.ExactPhrases)
                {
                    string target = (phr.IndexOf('\\') >= 0 || phr.IndexOf('/') >= 0) ? fullPath : name;
                    if (target.IndexOf(phr, StringComparison.OrdinalIgnoreCase) < 0) return false;
                }
            }

            if (query.CompiledRegex != null)
            {
                if (!query.CompiledRegex.IsMatch(name) && !query.CompiledRegex.IsMatch(fullPath)) return false;
            }

            // ★ 本文検査が必要なケースの判定（SearchContentMode または content: 指定時）
            bool hasMandatoryContent = !string.IsNullOrEmpty(query.ContentKeyword);

            if (query.SearchContentMode)
            {
                if (isDir)
                {
                    if (hasMandatoryContent) return false;
                    if (!MatchesKeywordGroups(name, fullPath, query)) return false;
                    needsDeepCheck = false;
                    reason = "Name";
                    return true;
                }

                bool allInName = MatchesKeywordGroups(name, fullPath, query);
                if (allInName && !hasMandatoryContent)
                {
                    // ファイル名で通常キーワードを満たしており、必須content条件もなければ即時合格（本文走査ゼロ）
                    needsDeepCheck = false;
                    reason = "Name";
                    return true;
                }
                else
                {
                    // 不足キーワードがある、またはcontent:必須条件がある場合は本文抽出対応拡張子のみ候補へ
                    if (!ContentExtractionService.SupportedExtensions.Contains(ext) && !(query.IncludeOcr && OcrWorkerService.IsSupportedOcrExtension(ext))) return false;

                    needsDeepCheck = true;
                    reason = "Candidate for Content";
                    return true;
                }
            }
            else if (hasMandatoryContent || query.HasOfficeLinkOnly || !string.IsNullOrEmpty(query.OfficeLinkKeyword))
            {
                // 通常検索（名前一致が必須）＋ 本文条件（content: または office-link）
                if (!MatchesKeywordGroups(name, fullPath, query)) return false;
                if (isDir) return false;

                bool isDeepTarget = false;
                if (query.HasOfficeLinkOnly || !string.IsNullOrEmpty(query.OfficeLinkKeyword))
                {
                    isDeepTarget = OfficeExtensions.Contains(ext);
                }
                else if (hasMandatoryContent)
                {
                    isDeepTarget = ContentExtractionService.SupportedExtensions.Contains(ext) || (query.IncludeOcr && OcrWorkerService.IsSupportedOcrExtension(ext));
                }

                if (!isDeepTarget) return false;

                needsDeepCheck = true;
                reason = "Candidate for Deep I/O";
                return true;
            }
            else
            {
                // 純粋な名前・属性検索
                if (!MatchesKeywordGroups(name, fullPath, query)) return false;
                needsDeepCheck = false;
                if (string.IsNullOrEmpty(reason)) reason = "Match";
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
            IProgress<IReadOnlyList<SearchResultItem>>? batchYield = null,
            Stopwatch? sw = null,
            int initialHitCount = 0,
            long initialTotalBytes = 0,
            int scannedCount = 0)
        {
            var matched = new ConcurrentBag<SearchResultItem>();
            var progressiveBatch = new List<SearchResultItem>();
            var batchLock = new object();

            int currentHitCount = initialHitCount;
            long currentTotalBytes = initialTotalBytes;
            long lastReportMs = 0;

            // Determine target search keyword
            string targetContent = query.ContentKeyword ?? string.Empty;
            if (string.IsNullOrEmpty(targetContent) && query.SearchContentMode && query.Keywords.Count > 0)
            {
                targetContent = query.Keywords[0];
            }

            bool hasOfficeLinkReq = query.HasOfficeLinkOnly;
            string? officeLinkKeyword = query.OfficeLinkKeyword;

            // ★ Sol提唱: 小さいファイル優先（Small-File First）
            // 100KBと100MBなら100KBから先に読み、0.1〜0.3秒で初期ヒットをUIへポンポン流す
            var validFiles = candidates
                .Where(c => !c.IsDirectory)
                .OrderBy(c => c.SizeBytes)
                .ToList();

            int processed = 0;
            int total = validFiles.Count;

            string firstPath = validFiles.FirstOrDefault()?.FullPath ?? string.Empty;
            var governor = SharedIoGovernor.GetGovernor(firstPath);
            var controller = governor.ContentController;
            var deferredLargeFiles = new System.Collections.Concurrent.ConcurrentBag<SearchResultItem>();

            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = AdaptiveConcurrencyController.ContentMaxConcurrency,
                CancellationToken = ct
            };

            void EmitHit(SearchResultItem hit)
            {
                matched.Add(hit);
                Interlocked.Increment(ref currentHitCount);
                Interlocked.Add(ref currentTotalBytes, hit.SizeBytes);

                if (batchYield != null)
                {
                    lock (batchLock)
                    {
                        progressiveBatch.Add(hit);
                        int threshold = matched.Count <= 3 ? 1 : 5;
                        if (progressiveBatch.Count >= threshold)
                        {
                            batchYield.Report(progressiveBatch.ToList());
                            progressiveBatch.Clear();
                        }
                    }
                }
            }

            var sharedContext = CreateSearchContext(query, firstPath);

            await Parallel.ForEachAsync(validFiles, po, async (item, token) =>
            {
                token.ThrowIfCancellationRequested();

                using var lease = await controller.AcquireAsync(token);
                double measuredIoMs = 0;
                bool isError = false;

                try
                {
                    var (isHit, isDeferred) = await InspectContentItemAsync(
                        item,
                        query,
                        hasOfficeLinkReq,
                        officeLinkKeyword,
                        token,
                        allowDeferred: true,
                        sharedContext: sharedContext,
                        reportIoElapsed: ms => measuredIoMs = ms);

                    if (isHit)
                    {
                        EmitHit(item);
                    }
                    else if (isDeferred)
                    {
                        deferredLargeFiles.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    isError = ex is System.Net.Sockets.SocketException ||
                              (ex is IOException ioEx && (ioEx.HResult == unchecked((int)0x8007003B) || ioEx.HResult == unchecked((int)0x80070040) || ioEx.HResult == unchecked((int)0x80070036)));
                }
                finally
                {
                    // ★ ADR 98: CPU解析時間を除外した「純粋なI/O時間」のみをGovernorへ報告
                    lease.Report(measuredIoMs > 0 ? measuredIoMs : 15.0, isError);
                }

                int c = Interlocked.Increment(ref processed);
                long elapsedMs = sw?.ElapsedMilliseconds ?? 0;
                if (elapsedMs - Volatile.Read(ref lastReportMs) > 100 || c == total)
                {
                    Volatile.Write(ref lastReportMs, elapsedMs);
                    progress?.Report(new SearchProgressReport
                    {
                        HitCount = Volatile.Read(ref currentHitCount),
                        ScannedCount = scannedCount > 0 ? scannedCount : c,
                        TotalHitBytes = Volatile.Read(ref currentTotalBytes),
                        CurrentPath = $"📄 Deep Search ({c:N0}/{total:N0}): {item.Name}",
                        Elapsed = sw?.Elapsed ?? TimeSpan.Zero,
                        IsCompleted = false
                    });
                }
            });

            // キャッシュ候補も同じ少数並列の巨大ファイル処理へ渡す。
            if (!deferredLargeFiles.IsEmpty)
            {
                await Parallel.ForEachAsync(deferredLargeFiles, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 2,
                    CancellationToken = ct
                }, async (dItem, token) =>
                {
                    using var lease = await controller.AcquireAsync(token);
                    double measuredIoMs = 0;
                    bool isError = false;
                    try
                    {
                        var (isHit, _) = await InspectContentItemAsync(
                            dItem,
                            query,
                            hasOfficeLinkReq,
                            officeLinkKeyword,
                            token,
                            allowDeferred: false,
                            sharedContext: sharedContext,
                            reportIoElapsed: ms => measuredIoMs = ms,
                            skipCompletedProbe: true);

                        if (isHit)
                        {
                            EmitHit(dItem);
                        }
                    }
                    catch (Exception ex)
                    {
                        isError = ex is System.Net.Sockets.SocketException ||
                                  (ex is IOException ioEx && (ioEx.HResult == unchecked((int)0x8007003B) || ioEx.HResult == unchecked((int)0x80070040) || ioEx.HResult == unchecked((int)0x80070036)));
                    }
                    finally
                    {
                        lease.Report(measuredIoMs > 0 ? measuredIoMs : 15.0, isError);
                    }
                });
            }

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

        private sealed class QuerySearchContext
        {
            public IReadOnlyList<List<string>> Groups { get; }
            public string? SingleKeyword { get; }
            public AhoCorasickSearcher? Ac { get; }
            public List<int>? PatternToGroup { get; }
            public AdaptiveTextReadController ReadController { get; }

            public QuerySearchContext(IReadOnlyList<List<string>> groups, bool isNetworkPath)
            {
                Groups = groups;
                ReadController = new AdaptiveTextReadController(isNetworkPath);
                if (groups.Count == 1 && groups[0].Count == 1)
                {
                    SingleKeyword = groups[0][0];
                }
                else if (groups.Count > 0)
                {
                    Ac = PdfSearchHelper.CreateSearcher(groups, out var p2g);
                    PatternToGroup = p2g;
                }
            }
        }

        private static QuerySearchContext CreateSearchContext(SearchQuery query, string? targetPath)
        {
            var groups = new List<List<string>>();
            if (!string.IsNullOrEmpty(query.ContentKeyword))
            {
                groups.Add(new List<string> { query.ContentKeyword });
            }
            if (query.SearchContentMode)
            {
                var baseGroups = query.KeywordGroups.Count > 0
                    ? query.KeywordGroups.ToList()
                    : query.Keywords.Select(k => new List<string> { k }).ToList();
                groups.AddRange(baseGroups);
                foreach (var phr in query.ExactPhrases)
                {
                    if (!string.IsNullOrWhiteSpace(phr))
                    {
                        groups.Add(new List<string> { phr });
                    }
                }
            }
            return new QuerySearchContext(groups, PathCanonicalizer.IsNetworkPath(targetPath));
        }

        /// <summary>
        /// 単一ファイルの本文/リンク検査を行い、条件に合致するか判定（Early Exit・高速スニペット付き）
        /// 【ADR 91】巨大ファイルでProbe未ヒット時は allowDeferred=true なら (false, true) を返し、後回しにする。
        /// 【ADR 95】クエリ単位で共有された QuerySearchContext を使用し、検索機械のファイル毎再生成を根絶。
        /// </summary>
        internal static List<List<string>> GetRequiredContentGroups(SearchResultItem item, SearchQuery query)
        {

            List<List<string>> requiredGroups = new();

            // 1. content: キーワードは本文検査に絶対必須
            if (!string.IsNullOrEmpty(query.ContentKeyword))
            {
                requiredGroups.Add(new List<string> { query.ContentKeyword });
            }

            // 2. 通常キーワードグループ + ExactPhrases の処理（本文も検索ONなら、名前で満たしていないグループを本文で要求）
            var groups = query.KeywordGroups.Count > 0
                ? query.KeywordGroups.ToList()
                : query.Keywords.Select(k => new List<string> { k }).ToList();

            foreach (var phr in query.ExactPhrases)
            {
                if (!string.IsNullOrWhiteSpace(phr))
                {
                    groups.Add(new List<string> { phr });
                }
            }

            if (query.SearchContentMode)
            {
                foreach (var grp in groups)
                {
                    bool matchedInName = false;
                    foreach (var kw in grp)
                    {
                        bool hasSeparator = kw.Contains('\\') || kw.Contains('/');
                        string targetString = hasSeparator ? item.FullPath : item.Name;
                        if (targetString.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            matchedInName = true;
                            break;
                        }
                    }
                    if (!matchedInName && grp.Count > 0)
                    {
                        requiredGroups.Add(grp);
                    }
                }
            }

            return requiredGroups;
        }

        private static async Task<(bool isHit, bool isDeferred)> InspectContentItemAsync(
            SearchResultItem item,
            SearchQuery query,
            bool hasOfficeLinkReq,
            string? officeLinkKeyword,
            CancellationToken token,
            bool allowDeferred = true,
            QuerySearchContext? sharedContext = null,
            Action<double>? reportIoElapsed = null,
            bool skipCompletedProbe = false)
        {
            string ext = Path.GetExtension(item.FullPath).ToLowerInvariant();

            var requiredGroups = GetRequiredContentGroups(item, query);

            // 不足グループが0件なら、名前/パスで既に完全一致しているので本文走査不要で即合格
            if (requiredGroups.Count == 0 && !hasOfficeLinkReq)
            {
                item.MatchedReason = "Name";
                return (true, false);
            }
            else if (requiredGroups.Count > 0)
            {
                // 1. Office (OpenXML: .xlsx, .xlsm, .docx, .pptx)
                if (OfficeExtensions.Contains(ext))
                {
                    if (ContentExtractionService.SearchOfficeContent(item.FullPath, requiredGroups, out string snippet, item.SizeBytes, reportIoElapsed))
                    {
                        item.ContentSnippet = snippet;
                        item.MatchedReason = (query.KeywordGroups.Count > requiredGroups.Count || query.Keywords.Count > requiredGroups.Count)
                            ? $"Name + Content: \"{FormatRequiredGroups(requiredGroups)}\""
                            : $"Content: \"{FormatRequiredGroups(requiredGroups)}\"";
                        return (true, false);
                    }
                }
                // 2. PDF (.pdf) with Windows IFilter and pure C# fallback
                else if (PdfExtensions.Contains(ext))
                {
                    var pdfIoSw = Stopwatch.StartNew();
                    bool isFullMatch = sharedContext != null && requiredGroups.Count == sharedContext.Groups.Count;
                    bool pdfHit;
                    string snippet;
                    if (isFullMatch && sharedContext!.Ac != null)
                    {
                        pdfHit = PdfSearchHelper.SearchPdfContentMultiple(item.FullPath, sharedContext.Ac, sharedContext.PatternToGroup!, sharedContext.Groups.Count, out snippet);
                    }
                    else
                    {
                        pdfHit = SearchPdfContentMultiple(item.FullPath, requiredGroups, out snippet);
                    }
                    pdfIoSw.Stop();
                    reportIoElapsed?.Invoke(pdfIoSw.Elapsed.TotalMilliseconds);

                    if (pdfHit)
                    {
                        item.ContentSnippet = snippet;
                        item.MatchedReason = (query.KeywordGroups.Count > requiredGroups.Count || query.Keywords.Count > requiredGroups.Count)
                            ? $"Name + PDF Content: \"{FormatRequiredGroups(requiredGroups)}\""
                            : $"PDF Content: \"{FormatRequiredGroups(requiredGroups)}\"";
                        return (true, false);
                    }
                }
                // 3. Text files (.txt, .csv, .log, .json, code files, etc.)
                else if (TextExtensions.Contains(ext) || ContentExtractionService.SupportedExtensions.Contains(ext))
                {
                    // 【Large File Pipeline - ADR 90/91】50MB超の巨大ファイルは分散Probeを先行実施
                    const long LargeFileProbeThreshold = 50L * 1024 * 1024;
                    if (item.SizeBytes > LargeFileProbeThreshold && !skipCompletedProbe)
                    {
                        var probeSnippet = await ContentExtractionService.ProbeLargeFileContentAsync(item.FullPath, item.SizeBytes, requiredGroups, token);
                        if (probeSnippet != null)
                        {
                            item.ContentSnippet = probeSnippet;
                            item.MatchedReason = (query.KeywordGroups.Count > requiredGroups.Count || query.Keywords.Count > requiredGroups.Count)
                                ? $"Name + Content (Probe): \"{FormatRequiredGroups(requiredGroups)}\""
                                : $"Content (Probe): \"{FormatRequiredGroups(requiredGroups)}\"";
                            return (true, false);
                        }

                        // 【ADR 91】Probe で外れた巨大ファイルは、通常ファイル検索の完了後まで後回し（Deferred）
                        if (allowDeferred)
                        {
                            return (false, true);
                        }
                    }

                    bool isFullMatch = sharedContext != null && requiredGroups.Count == sharedContext.Groups.Count;
                    string? snippet;
                    if (isFullMatch)
                    {
                        snippet = await ContentExtractionService.SearchTextContentWithBufferAsync(
                            item.FullPath,
                            sharedContext!.SingleKeyword,
                            sharedContext.Ac,
                            sharedContext.PatternToGroup,
                            sharedContext.Groups.Count,
                            token,
                            reportIoElapsed,
                            sharedContext.ReadController,
                            item.SizeBytes);
                    }
                    else
                    {
                        var textIoSw = Stopwatch.StartNew();
                        snippet = await ContentExtractionService.SearchTextContentAsync(
                            item.FullPath, requiredGroups, token, sharedContext?.ReadController, item.SizeBytes);
                        textIoSw.Stop();
                        reportIoElapsed?.Invoke(textIoSw.Elapsed.TotalMilliseconds);
                    }

                    if (snippet is not null)
                    {
                        item.ContentSnippet = snippet;
                        item.MatchedReason = (query.KeywordGroups.Count > requiredGroups.Count || query.Keywords.Count > requiredGroups.Count)
                            ? $"Name + Content: \"{FormatRequiredGroups(requiredGroups)}\""
                            : $"Content: \"{FormatRequiredGroups(requiredGroups)}\"";
                        return (true, false);
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
                    return (true, false);
                }
            }

            return (false, false);
        }

        private static string FormatRequiredGroups(IReadOnlyList<List<string>> requiredGroups) =>
            string.Join(" AND ", requiredGroups.Select(g =>
                g.Count > 1 ? "(" + string.Join(" OR ", g) + ")" : (g.Count > 0 ? g[0] : "")));

        private static bool SearchPdfContentMultiple(string filePath, IReadOnlyList<List<string>> requiredGroups, out string snippet)
        {
            return PdfSearchHelper.SearchPdfContentMultiple(filePath, requiredGroups, out snippet);
        }


        private static bool SearchOfficeFileLinks(string filePath, string? keyword, out string snippet)
        {
            snippet = string.Empty;
            try
            {
                using var fs = ContentExtractionService.OpenBufferedReadStream(filePath);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read, false);

                foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
                {
                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                    foreach (string value in OfficeLinkDocument.Values(OfficeLinkDocument.Parse(reader.ReadToEnd())))
                    {
                        if (!string.IsNullOrEmpty(keyword) && !value.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;
                        snippet = value;
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// ★ ADR 144: 第2ロケット（遅延OCR走査）
        /// 通常検索を先行完了させた後、未ヒットの画像・画像PDFに対してworker.pyを呼び出し遅延走査を実行する。
        /// </summary>
        private static async Task<List<SearchResultItem>> ExecuteOcrStageAsync(
            IReadOnlyList<SearchResultItem> ocrCandidates,
            HashSet<string> existingHitPaths,
            SearchQuery query,
            IProgress<IReadOnlyList<SearchResultItem>>? batchYield,
            IProgress<SearchProgressReport>? progress,
            CancellationToken ct,
            Stopwatch sw,
            int initialScannedCount,
            int initialHitCount,
            long initialTotalBytes, Action<string>? recordWarning = null)
        {
            var ocrHits = new List<SearchResultItem>();
            if (query.HasOfficeLinkOnly || !string.IsNullOrWhiteSpace(query.OfficeLinkKeyword)) return ocrHits;
            var remainingCandidates = ocrCandidates
                .Where(c => !existingHitPaths.Contains(c.FullPath))
                .ToList();

            if (remainingCandidates.Count == 0) return ocrHits;
            if (!OcrWorkerService.IsEnvironmentAvailable()) { recordWarning?.Invoke("OCR未実行 / OCR unavailable"); return ocrHits; }

            await using var ocrClient = await OcrWorkerService.TryCreateClientAsync(ct);
            if (ocrClient == null) { recordWarning?.Invoke("OCR起動失敗 / OCR startup failed"); return ocrHits; }

            var currentBatch = new List<SearchResultItem>();
            int hitCount = initialHitCount;
            long totalHitBytes = initialTotalBytes;
            int processedOcr = 0;

            foreach (var candidate in remainingCandidates)
            {
                if (ct.IsCancellationRequested) break;
                processedOcr++;

                try
                {
                    var docResult = await ocrClient.ProcessFileAsync(candidate.FullPath, ct);
                    if (!docResult.Success || !string.IsNullOrEmpty(docResult.Warning))
                        recordWarning?.Invoke("OCR一部未確認 / OCR incomplete: " + docResult.Warning);
                    if (OcrWorkerService.TryMatchOcrDocument(docResult, query, candidate, out string? snippet, out int matchedPage))
                    {
                        var hitItem = new SearchResultItem
                        {
                            Name = candidate.Name,
                            FullPath = candidate.FullPath,
                            DirectoryPath = candidate.DirectoryPath,
                            SizeBytes = candidate.SizeBytes,
                            LastWriteTime = candidate.LastWriteTime,
                            CreationTime = candidate.CreationTime,
                            Extension = candidate.Extension,
                            IsDirectory = false,
                            IsOcrEstimated = true,
                            ContentSnippet = $"[OCR推定 p.{matchedPage}] {snippet}",
                            MatchedReason = "OCR推定本文一致"
                        };

                        ocrHits.Add(hitItem);
                        currentBatch.Add(hitItem);
                        hitCount++;
                        totalHitBytes += hitItem.SizeBytes;

                        if (currentBatch.Count >= 5)
                        {
                            batchYield?.Report(currentBatch.ToList());
                            currentBatch.Clear();
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // 個別ファイルのOCRエラーはスキップ
                }

                progress?.Report(new SearchProgressReport
                {
                    HitCount = hitCount,
                    ScannedCount = initialScannedCount,
                    ContentProcessedCount = processedOcr,
                    TotalHitBytes = totalHitBytes,
                    CurrentPath = candidate.FullPath,
                    Elapsed = sw.Elapsed,
                    IsCompleted = false
                });
            }

            if (currentBatch.Count > 0)
            {
                batchYield?.Report(currentBatch.ToList());
                currentBatch.Clear();
            }

            return ocrHits;
        }
    }
}
