using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraSize.Models;
using FolderMorpher.Services;

namespace AstraSize.Services
{
    public class ScanProgress
    {
        public string CurrentPath { get; set; } = string.Empty;
        public int FilesScanned { get; set; }
        public long BytesScanned { get; set; }
        public int DiscoveredDirectories { get; set; }
        public int ProcessedDirectories { get; set; }
        public long ProcessedWorkUnits { get; set; }
        public long TotalWorkUnits { get; set; }
    }

    public class DiskScanService
    {
        public async Task<(FileItemNode rootNode, ScanSummary summary)> ScanPathAsync(
            string targetPath,
            IProgress<ScanProgress>? progress,
            CancellationToken ct)
        {
            // ⚡ Hybrid Check: Try Ultra-Fast MFT Engine if supported (Local NTFS + Administrator)
            if (Mft.MftScanService.CanUseMft(targetPath))
            {
                try
                {
                    progress?.Report(new ScanProgress
                    {
                        CurrentPath = "⚡ MFT高速スキャンエンジン起動中...",
                        FilesScanned = 0,
                        BytesScanned = 0
                    });

                    var mftScanner = new Mft.MftScanService();
                    var mftResult = await mftScanner.ScanPathAsync(targetPath, progress, ct);
                    if (mftResult.rootNode != null && mftResult.rootNode.Size > 0)
                    {
                        return mftResult;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch
                {
                    // Fallback to standard recursive scan seamlessly
                }
            }

            return await ScanStandardPathAsync(targetPath, progress, ct).ConfigureAwait(false);
        }

        // Regression tests must exercise this path even on an elevated NTFS CI runner.
        internal Task<(FileItemNode rootNode, ScanSummary summary)> ScanStandardPathAsync(
            string targetPath,
            IProgress<ScanProgress>? progress,
            CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                var cleanTargetPath = targetPath.TrimEnd('\\');
                if (cleanTargetPath.EndsWith(":") && cleanTargetPath.Length == 2)
                {
                    cleanTargetPath += "\\";
                }

                var rootDir = new DirectoryInfo(cleanTargetPath);
                if (!rootDir.Exists)
                {
                    throw new DirectoryNotFoundException($"指定されたパスが見つかりません: {targetPath}");
                }

                int filesScanned = 0;
                long bytesScanned = 0;
                long lastProgressTime = 0;
                int pendingWorkCount = 0;
                int discoveredDirectories = 1;
                int processedDirectories = 0;

                var statsLock = new object();
                void ReportProgress(string path)
                {
                    if (progress == null || stopwatch.ElapsedMilliseconds - Volatile.Read(ref lastProgressTime) <= 150)
                        return;
                    lock (statsLock)
                    {
                        var now = stopwatch.ElapsedMilliseconds;
                        if (now - lastProgressTime > 150)
                        {
                            lastProgressTime = now;
                            progress?.Report(new ScanProgress
                            {
                                CurrentPath = path,
                                FilesScanned = Volatile.Read(ref filesScanned),
                                BytesScanned = Volatile.Read(ref bytesScanned),
                                DiscoveredDirectories = Volatile.Read(ref discoveredDirectories),
                                ProcessedDirectories = Volatile.Read(ref processedDirectories)
                            });
                        }
                    }
                }

                // Network scans share the same per-share enumeration budget as search.
                bool isNetworkPath = PathCanonicalizer.IsNetworkPath(targetPath);
                var controller = isNetworkPath
                    ? SharedIoGovernor.GetGovernor(targetPath).EnumerationController
                    : null;
                int maxWorkers = isNetworkPath
                    ? AdaptiveConcurrencyController.MaxConcurrency
                    : Math.Clamp(Environment.ProcessorCount, 1, 8);

                DateTime rootLastModified = DateTime.MinValue;
                DateTime rootCreationTime = DateTime.MinValue;
                try { rootLastModified = rootDir.LastWriteTime; } catch { }
                try { rootCreationTime = rootDir.CreationTime; } catch { }

                var rootNode = new FileItemNode
                {
                    Name = string.IsNullOrEmpty(rootDir.Name) ? cleanTargetPath : rootDir.Name,
                    FullPath = cleanTargetPath,
                    IsDirectory = true,
                    Parent = null,
                    Level = 0,
                    LastModified = rootLastModified,
                    CreationTime = rootCreationTime
                };

                // Sol提唱: 未処理＋処理中ワークアイテム数を Interlocked で厳密追跡（Worker race 完全根絶）
                var folderQueue = new ConcurrentQueue<(FileItemNode node, string currentPath, int depth)>();
                folderQueue.Enqueue((rootNode, cleanTargetPath, 0));
                Interlocked.Increment(ref pendingWorkCount);

                var workerTasks = new Task[maxWorkers];
                var workerStats = new ScanWorkerStats[maxWorkers];

                for (int w = 0; w < maxWorkers; w++)
                {
                    var stats = new ScanWorkerStats();
                    workerStats[w] = stats;
                    workerTasks[w] = Task.Run(async () =>
                    {
                        var subDirs = new List<NativeFindEntry>(32);
                        var files = new List<NativeFindEntry>(64);
                        var localStack = new Stack<(FileItemNode node, string currentPath, int depth)>();

                        while (!ct.IsCancellationRequested)
                        {
                            if (!localStack.TryPop(out var item) && !folderQueue.TryDequeue(out item))
                            {
                                // pendingWorkCount が 0 なら全フォルダーの探索が完全に終了
                                if (Volatile.Read(ref pendingWorkCount) == 0)
                                {
                                    break;
                                }
                                await Task.Delay(2, ct).ConfigureAwait(false);
                                continue;
                            }

                            using var lease = controller == null ? null : await controller.AcquireAsync(ct).ConfigureAwait(false);
                            try
                            {
                                var (node, currentPath, depth) = item;

                                var dirSw = Stopwatch.StartNew();
                                bool ok = NativeDirectoryEnumerator.TryEnumerateEntries(currentPath, subDirs, files, out var error, out var failureKind, ct, isNetworkPath);
                                dirSw.Stop();

                                lease?.Report(dirSw.Elapsed.TotalMilliseconds, failureKind);

                                if (!ok)
                                {
                                    node.ErrorMessage = error ?? "アクセス拒否";
                                    continue;
                                }

                                // 1. 直下ファイルのノード生成 ＆ 統計追跡
                                long batchBytes = 0;
                                int batchFiles = 0;
                                for (int i = 0; i < files.Count; i++)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    var f = files[i];
                                    long fSize = f.Size;
                                    string fPath = Path.Combine(currentPath, f.Name);
                                    string ext = Path.GetExtension(f.Name);
                                    stats.TrackFile(f.Name, fPath, fSize, ext);
                                    batchBytes += fSize;
                                    batchFiles++;

                                    var fileNode = new FileItemNode
                                    {
                                        Name = f.Name,
                                        FullPath = fPath,
                                        Size = fSize,
                                        FileCount = 1,
                                        FolderCount = 0,
                                        IsDirectory = false,
                                        LastModified = f.LastWriteTimeUtc.ToLocalTime(),
                                        CreationTime = f.CreationTimeUtc.ToLocalTime(),
                                        Parent = node,
                                        Level = depth + 1
                                    };
                                    node.Children.Add(fileNode);
                                    if (batchFiles == 256)
                                    {
                                        Interlocked.Add(ref bytesScanned, batchBytes);
                                        Interlocked.Add(ref filesScanned, batchFiles);
                                        batchBytes = 0;
                                        batchFiles = 0;
                                        ReportProgress(fPath);
                                    }
                                }
                                Interlocked.Add(ref bytesScanned, batchBytes);
                                Interlocked.Add(ref filesScanned, batchFiles);

                                // 2. サブディレクトリのノード生成 ＆ キュー投入 (Junction / ReparsePoint 除外)
                                for (int i = 0; i < subDirs.Count; i++)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    var sd = subDirs[i];
                                    if (sd.IsReparsePoint) continue;

                                    string subPath = Path.Combine(currentPath, sd.Name);
                                    var subNode = new FileItemNode
                                    {
                                        Name = sd.Name,
                                        FullPath = subPath,
                                        IsDirectory = true,
                                        Parent = node,
                                        Level = depth + 1,
                                        LastModified = sd.LastWriteTimeUtc.ToLocalTime(), // ★ 親の列挙から直結（RPCゼロ）
                                        CreationTime = sd.CreationTimeUtc.ToLocalTime()
                                    };
                                    node.Children.Add(subNode);

                                    Interlocked.Increment(ref pendingWorkCount);
                                    Interlocked.Increment(ref discoveredDirectories);
                                    // Keep one branch on this worker; siblings remain available to peers.
                                    if (localStack.Count == 0) localStack.Push((subNode, subPath, depth + 1));
                                    else folderQueue.Enqueue((subNode, subPath, depth + 1));
                                }
                            }
                            finally
                            {
                                Interlocked.Decrement(ref pendingWorkCount);
                                Interlocked.Increment(ref processedDirectories);
                                ReportProgress(item.currentPath);
                            }
                        }
                    }, ct);
                }

                Task.WhenAll(workerTasks).GetAwaiter().GetResult();
                ct.ThrowIfCancellationRequested();

                // 3. インメモリ・ボトムアップ高速集計（Phase 2: サイズ・ファイル数・フォルダー数の合算と降順ソート）
                void AggregateNode(FileItemNode node)
                {
                    ct.ThrowIfCancellationRequested();
                    long totalSize = 0;
                    int totalFiles = 0;
                    int totalFolders = 0;

                    for (int i = 0; i < node.Children.Count; i++)
                    {
                        var child = node.Children[i];
                        if (child.IsDirectory)
                        {
                            AggregateNode(child);
                            totalSize += child.Size;
                            totalFiles += child.FileCount;
                            totalFolders += child.FolderCount + 1; // 直下のサブフォルダ自身 + その配下のサブフォルダ数
                        }
                        else
                        {
                            totalSize += child.Size;
                            totalFiles++;
                        }
                    }

                    node.Size = totalSize;
                    node.FileCount = totalFiles;
                    node.FolderCount = totalFolders;

                    // 既存規約: ディレクトリ（サイズ降順）➔ ファイル（サイズ降順）
                    var orderedChildren = node.Children.ToList();
                    orderedChildren.Sort(static (a, b) =>
                    {
                        int typeOrder = b.IsDirectory.CompareTo(a.IsDirectory);
                        return typeOrder != 0 ? typeOrder : b.Size.CompareTo(a.Size);
                    });
                    // The tree is still private to this scan: no collection notifications are needed.
                    node.Children = new ObservableCollection<FileItemNode>(orderedChildren);
                }

                AggregateNode(rootNode);

                // Calculate percentages relative to root
                CalculatePercentages(rootNode, rootNode.Size > 0 ? rootNode.Size : 1, ct);

                stopwatch.Stop();

                var largestFiles = workerStats.SelectMany(stats => stats.LargestFiles)
                    .OrderByDescending(file => file.Size).Take(10).ToList();
                var extensionDict = new Dictionary<string, (long size, int count)>(StringComparer.OrdinalIgnoreCase);
                foreach (var stats in workerStats)
                    foreach (var entry in stats.ExtensionStats)
                    {
                        extensionDict.TryGetValue(entry.Key, out var existing);
                        extensionDict[entry.Key] = (existing.size + entry.Value.size, existing.count + entry.Value.count);
                    }

                var extensionStats = extensionDict
                    .Select(kv => new ExtensionStat
                    {
                        Extension = kv.Key.ToLowerInvariant(),
                        TotalSize = kv.Value.size,
                        FileCount = kv.Value.count,
                        Percentage = rootNode.Size > 0 ? (double)kv.Value.size / rootNode.Size * 100.0 : 0
                    })
                    .OrderByDescending(e => e.TotalSize)
                    .Take(10)
                    .ToList();

                var summary = new ScanSummary
                {
                    TargetPath = cleanTargetPath,
                    TotalBytes = rootNode.Size,
                    TotalFiles = rootNode.FileCount,
                    TotalFolders = rootNode.FolderCount,
                    ElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 1),
                    IsCancelled = ct.IsCancellationRequested,
                    LargestFiles = largestFiles.Take(10).ToList(),
                    ExtensionStats = extensionStats,
                    CleanupCandidates = workerStats.SelectMany(stats => stats.CleanupCandidates).Take(10).ToList()
                };

                rootNode.CachedTopFiles = summary.LargestFiles;
                rootNode.CachedExtensionStats = summary.ExtensionStats;
                ct.ThrowIfCancellationRequested();
                return (rootNode, summary);
            }, ct);
        }

        /// <summary>Owned by one worker; only the completed summaries are merged.</summary>
        private sealed class ScanWorkerStats
        {
            public List<LargestFileInfo> LargestFiles { get; } = new(31);
            public Dictionary<string, (long size, int count)> ExtensionStats { get; } = new(StringComparer.OrdinalIgnoreCase);
            public List<CleanupCandidate> CleanupCandidates { get; } = new(10);

            public void TrackFile(string name, string path, long size, string extension)
            {
                if (LargestFiles.Count < 30 || size > LargestFiles[^1].Size)
                {
                    LargestFiles.Add(new LargestFileInfo
                    {
                        Name = name, FullPath = path, Size = size, Extension = extension,
                        Category = GetCategoryForExtension(extension)
                    });
                    LargestFiles.Sort(static (a, b) => b.Size.CompareTo(a.Size));
                    if (LargestFiles.Count > 30) LargestFiles.RemoveAt(30);
                }
                string key = string.IsNullOrEmpty(extension) ? "(なし)" : extension;
                ExtensionStats.TryGetValue(key, out var stat);
                ExtensionStats[key] = (stat.size + size, stat.count + 1);

                bool isDump = extension.Equals(".dmp", StringComparison.OrdinalIgnoreCase);
                if (CleanupCandidates.Count < 10 && (isDump || extension.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    (extension.Equals(".log", StringComparison.OrdinalIgnoreCase) && size > 50 * 1024 * 1024)))
                    CleanupCandidates.Add(new CleanupCandidate
                    {
                        Name = name, FullPath = path, Size = size,
                        Reason = isDump ? "クラッシュダンプ" : "一時/巨大ログファイル"
                    });
            }
        }

        internal static void CalculatePercentages(FileItemNode node, long rootSize, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (rootSize > 0)
            {
                node.Percentage = Math.Min(100.0, (double)node.Size / rootSize * 100.0);
            }
            else
            {
                node.Percentage = 0.0;
            }

            foreach (var child in node.Children)
            {
                child.Parent = node;
                CalculatePercentages(child, rootSize, ct);
            }
        }

        public static (List<LargestFileInfo> largestFiles, List<ExtensionStat> extensionStats) GetInsightsForNode(FileItemNode node)
        {
            const int MaxTop = 10;
            var topList = new List<LargestFileInfo>(MaxTop + 1);
            long minSizeInTop = -1;
            var extDict = new Dictionary<string, (long size, int count)>(StringComparer.OrdinalIgnoreCase);

            void Traverse(FileItemNode current)
            {
                if (!current.IsDirectory)
                {
                    long size = current.Size;

                    // Top 10の保持：上位10件に満たないか、現在の10位よりも大きい場合のみオブジェクト生成＆挿入
                    if (topList.Count < MaxTop || size > minSizeInTop)
                    {
                        string ext = Path.GetExtension(current.Name);
                        var fileInfo = new LargestFileInfo
                        {
                            Name = current.Name,
                            FullPath = current.FullPath,
                            Size = size,
                            Extension = ext,
                            Category = GetCategoryForExtension(ext)
                        };

                        int idx = topList.FindIndex(f => f.Size < size);
                        if (idx < 0)
                        {
                            topList.Add(fileInfo);
                        }
                        else
                        {
                            topList.Insert(idx, fileInfo);
                        }

                        if (topList.Count > MaxTop)
                        {
                            topList.RemoveAt(MaxTop);
                        }

                        if (topList.Count == MaxTop)
                        {
                            minSizeInTop = topList[MaxTop - 1].Size;
                        }
                    }

                    string rawExt = Path.GetExtension(current.Name);
                    string extKey = string.IsNullOrEmpty(rawExt) ? "(なし)" : rawExt.ToLowerInvariant();
                    if (extDict.TryGetValue(extKey, out var val))
                    {
                        extDict[extKey] = (val.size + size, val.count + 1);
                    }
                    else
                    {
                        extDict[extKey] = (size, 1);
                    }
                }
                else
                {
                    foreach (var c in current.Children)
                    {
                        Traverse(c);
                    }
                }
            }

            Traverse(node);

            long totalScopeSize = node.Size > 0 ? node.Size : 1;

            var topExts = extDict
                .Select(kv => new ExtensionStat
                {
                    Extension = kv.Key,
                    TotalSize = kv.Value.size,
                    FileCount = kv.Value.count,
                    Percentage = Math.Min(100.0, (double)kv.Value.size / totalScopeSize * 100.0)
                })
                .OrderByDescending(e => e.TotalSize)
                .Take(10)
                .ToList();

            return (topList, topExts);
        }

        internal static string GetCategoryForExtension(string ext)
        {
            return ext.ToLowerInvariant() switch
            {
                ".vhdx" or ".vhd" or ".iso" or ".vmdk" => "仮想ディスク / ISO",
                ".exe" or ".dll" or ".msi" or ".sys" => "実行可能ファイル / システム",
                ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "圧縮アーカイブ",
                ".mp4" or ".mkv" or ".avi" or ".mov" => "動画メディア",
                ".jpg" or ".png" or ".gif" or ".webp" or ".psd" => "画像 / グラフィック",
                ".log" or ".dmp" or ".tmp" => "ログ / システムダンプ",
                ".docx" or ".xlsx" or ".pptx" or ".pdf" => "オフィス文書",
                _ => "その他"
            };
        }
    }
}
