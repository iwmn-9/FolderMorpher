using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

            return await Task.Run(() =>
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

                var largestFiles = new List<LargestFileInfo>(32);
                long minLargestThreshold = 0;
                var extensionDict = new Dictionary<string, (long size, int count)>(StringComparer.OrdinalIgnoreCase);
                var cleanupList = new List<CleanupCandidate>(64);

                var statsLock = new object();
                void TrackFile(string name, string fullPath, long size, string ext)
                {
                    lock (statsLock)
                    {
                        bytesScanned += size;
                        filesScanned++;

                        // 1. High-Performance Largest Files Tracking (avoid sorting every single file)
                        if (largestFiles.Count < 30)
                        {
                            largestFiles.Add(new LargestFileInfo
                            {
                                Name = name,
                                FullPath = fullPath,
                                Size = size,
                                Extension = ext,
                                Category = GetCategoryForExtension(ext)
                            });
                            if (largestFiles.Count == 30)
                            {
                                largestFiles.Sort((a, b) => b.Size.CompareTo(a.Size));
                                minLargestThreshold = largestFiles[^1].Size;
                            }
                        }
                        else if (size > minLargestThreshold)
                        {
                            largestFiles.Add(new LargestFileInfo
                            {
                                Name = name,
                                FullPath = fullPath,
                                Size = size,
                                Extension = ext,
                                Category = GetCategoryForExtension(ext)
                            });
                            largestFiles.Sort((a, b) => b.Size.CompareTo(a.Size));
                            largestFiles.RemoveAt(largestFiles.Count - 1);
                            minLargestThreshold = largestFiles[^1].Size;
                        }

                        // 2. Extension aggregation
                        var extKey = string.IsNullOrEmpty(ext) ? "(なし)" : ext.ToLowerInvariant();
                        if (extensionDict.TryGetValue(extKey, out var stat))
                        {
                            extensionDict[extKey] = (stat.size + size, stat.count + 1);
                        }
                        else
                        {
                            extensionDict[extKey] = (size, 1);
                        }

                        // 3. Fast Cleanup Detection (check extension first to avoid heavy string allocations)
                        string extLower = ext;
                        if (extLower.Equals(".dmp", StringComparison.OrdinalIgnoreCase) ||
                            extLower.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
                            (extLower.Equals(".log", StringComparison.OrdinalIgnoreCase) && size > 50 * 1024 * 1024))
                        {
                            if (cleanupList.Count < 100)
                            {
                                cleanupList.Add(new CleanupCandidate
                                {
                                    Name = name,
                                    FullPath = fullPath,
                                    Size = size,
                                    Reason = extLower.Equals(".dmp", StringComparison.OrdinalIgnoreCase) ? "クラッシュダンプ" : "一時/巨大ログファイル"
                                });
                            }
                        }

                        // 4. Progress throttle (150ms)
                        var now = stopwatch.ElapsedMilliseconds;
                        if (now - lastProgressTime > 150)
                        {
                            lastProgressTime = now;
                            progress?.Report(new ScanProgress
                            {
                                CurrentPath = fullPath,
                                FilesScanned = filesScanned,
                                BytesScanned = bytesScanned
                            });
                        }
                    }
                }

                var controller = new AdaptiveConcurrencyController();
                int maxWorkers = AdaptiveConcurrencyController.MaxConcurrency;

                DateTime rootLastModified = DateTime.MinValue;
                try { rootLastModified = rootDir.LastWriteTime; } catch { }

                var rootNode = new FileItemNode
                {
                    Name = string.IsNullOrEmpty(rootDir.Name) ? cleanTargetPath : rootDir.Name,
                    FullPath = cleanTargetPath,
                    IsDirectory = true,
                    Parent = null,
                    Level = 0,
                    LastModified = rootLastModified
                };

                // Sol提唱: 未処理＋処理中ワークアイテム数を Interlocked で厳密追跡（Worker race 完全根絶）
                int pendingWorkCount = 0;
                var folderQueue = new ConcurrentQueue<(FileItemNode node, string currentPath, int depth)>();
                folderQueue.Enqueue((rootNode, cleanTargetPath, 0));
                Interlocked.Increment(ref pendingWorkCount);

                var workerTasks = new Task[maxWorkers];

                for (int w = 0; w < maxWorkers; w++)
                {
                    workerTasks[w] = Task.Run(async () =>
                    {
                        var subDirs = new List<NativeFindEntry>(32);
                        var files = new List<NativeFindEntry>(64);

                        while (!ct.IsCancellationRequested)
                        {
                            if (!folderQueue.TryDequeue(out var item))
                            {
                                // pendingWorkCount が 0 なら全フォルダーの探索が完全に終了
                                if (Volatile.Read(ref pendingWorkCount) == 0)
                                {
                                    break;
                                }
                                await Task.Delay(2, ct).ConfigureAwait(false);
                                continue;
                            }

                            using var lease = await controller.AcquireAsync(ct).ConfigureAwait(false);
                            try
                            {
                                var (node, currentPath, depth) = item;

                                var dirSw = Stopwatch.StartNew();
                                bool ok = NativeDirectoryEnumerator.TryEnumerateEntries(currentPath, subDirs, files, out var error, out var failureKind);
                                dirSw.Stop();

                                lease.Report(dirSw.Elapsed.TotalMilliseconds, failureKind);

                                if (!ok)
                                {
                                    node.ErrorMessage = error ?? "アクセス拒否";
                                    continue;
                                }

                                // 1. 直下ファイルのノード生成 ＆ 統計追跡
                                for (int i = 0; i < files.Count; i++)
                                {
                                    if (ct.IsCancellationRequested) break;
                                    var f = files[i];
                                    long fSize = f.Size;
                                    string fPath = Path.Combine(currentPath, f.Name);
                                    string ext = Path.GetExtension(f.Name);
                                    TrackFile(f.Name, fPath, fSize, ext);

                                    var fileNode = new FileItemNode
                                    {
                                        Name = f.Name,
                                        FullPath = fPath,
                                        Size = fSize,
                                        FileCount = 1,
                                        FolderCount = 0,
                                        IsDirectory = false,
                                        LastModified = f.LastWriteTimeUtc.ToLocalTime(),
                                        Parent = node,
                                        Level = depth + 1
                                    };
                                    node.Children.Add(fileNode);
                                }

                                // 2. サブディレクトリのノード生成 ＆ キュー投入 (Junction / ReparsePoint 除外)
                                for (int i = 0; i < subDirs.Count; i++)
                                {
                                    if (ct.IsCancellationRequested) break;
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
                                        LastModified = sd.LastWriteTimeUtc.ToLocalTime() // ★ 親の列挙から直結（RPCゼロ）
                                    };
                                    node.Children.Add(subNode);

                                    Interlocked.Increment(ref pendingWorkCount);
                                    folderQueue.Enqueue((subNode, subPath, depth + 1));
                                }
                            }
                            finally
                            {
                                Interlocked.Decrement(ref pendingWorkCount);
                            }
                        }
                    }, ct);
                }

                Task.WhenAll(workerTasks).GetAwaiter().GetResult();

                // 3. インメモリ・ボトムアップ高速集計（Phase 2: サイズ・ファイル数・フォルダー数の合算と降順ソート）
                void AggregateNode(FileItemNode node)
                {
                    long totalSize = 0;
                    int totalFiles = 0;
                    int totalFolders = 0;

                    var dirChildren = new List<FileItemNode>();
                    var fileChildren = new List<FileItemNode>();

                    for (int i = 0; i < node.Children.Count; i++)
                    {
                        var child = node.Children[i];
                        if (child.IsDirectory)
                        {
                            AggregateNode(child);
                            totalSize += child.Size;
                            totalFiles += child.FileCount;
                            totalFolders += child.FolderCount + 1; // 直下のサブフォルダ自身 + その配下のサブフォルダ数
                            dirChildren.Add(child);
                        }
                        else
                        {
                            totalSize += child.Size;
                            totalFiles++;
                            fileChildren.Add(child);
                        }
                    }

                    node.Size = totalSize;
                    node.FileCount = totalFiles;
                    node.FolderCount = totalFolders;

                    // 既存規約: ディレクトリ（サイズ降順）➔ ファイル（サイズ降順）
                    dirChildren.Sort((a, b) => b.Size.CompareTo(a.Size));
                    fileChildren.Sort((a, b) => b.Size.CompareTo(a.Size));

                    node.Children.Clear();
                    foreach (var d in dirChildren) node.Children.Add(d);
                    foreach (var f in fileChildren) node.Children.Add(f);
                }

                AggregateNode(rootNode);

                // Calculate percentages relative to root
                CalculatePercentages(rootNode, rootNode.Size > 0 ? rootNode.Size : 1);

                stopwatch.Stop();

                // Final sort for largest files
                largestFiles.Sort((a, b) => b.Size.CompareTo(a.Size));

                var extensionStats = extensionDict
                    .Select(kv => new ExtensionStat
                    {
                        Extension = kv.Key,
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
                    CleanupCandidates = cleanupList.Take(10).ToList()
                };

                return (rootNode, summary);
            }, ct);
        }

        internal static void CalculatePercentages(FileItemNode node, long rootSize)
        {
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
                CalculatePercentages(child, rootSize);
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
