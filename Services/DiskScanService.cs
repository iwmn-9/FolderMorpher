using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraSize.Models;

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

                void TrackFile(FileInfo fi, long size)
                {
                    bytesScanned += size;
                    filesScanned++;

                    // 1. High-Performance Largest Files Tracking (avoid sorting every single file)
                    if (largestFiles.Count < 30)
                    {
                        var ext = fi.Extension;
                        largestFiles.Add(new LargestFileInfo
                        {
                            Name = fi.Name,
                            FullPath = fi.FullName,
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
                        var ext = fi.Extension;
                        largestFiles.Add(new LargestFileInfo
                        {
                            Name = fi.Name,
                            FullPath = fi.FullName,
                            Size = size,
                            Extension = ext,
                            Category = GetCategoryForExtension(ext)
                        });
                        largestFiles.Sort((a, b) => b.Size.CompareTo(a.Size));
                        largestFiles.RemoveAt(largestFiles.Count - 1);
                        minLargestThreshold = largestFiles[^1].Size;
                    }

                    // 2. Extension aggregation
                    var extKey = string.IsNullOrEmpty(fi.Extension) ? "(なし)" : fi.Extension.ToLowerInvariant();
                    if (extensionDict.TryGetValue(extKey, out var stat))
                    {
                        extensionDict[extKey] = (stat.size + size, stat.count + 1);
                    }
                    else
                    {
                        extensionDict[extKey] = (size, 1);
                    }

                    // 3. Fast Cleanup Detection (check extension first to avoid heavy string allocations)
                    string extLower = fi.Extension;
                    if (extLower.Equals(".dmp", StringComparison.OrdinalIgnoreCase) ||
                        extLower.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
                        (extLower.Equals(".log", StringComparison.OrdinalIgnoreCase) && size > 50 * 1024 * 1024))
                    {
                        if (cleanupList.Count < 100)
                        {
                            cleanupList.Add(new CleanupCandidate
                            {
                                Name = fi.Name,
                                FullPath = fi.FullName,
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
                            CurrentPath = fi.FullName,
                            FilesScanned = filesScanned,
                            BytesScanned = bytesScanned
                        });
                    }
                }

                FileItemNode ScanDirectoryInternal(DirectoryInfo dir, int depth, FileItemNode? parentNode)
                {
                    ct.ThrowIfCancellationRequested();

                    var node = new FileItemNode
                    {
                        Name = string.IsNullOrEmpty(dir.Name) ? dir.FullName : dir.Name,
                        FullPath = dir.FullName,
                        IsDirectory = true,
                        Parent = parentNode,
                        Level = depth
                    };

                    try
                    {
                        node.LastModified = dir.LastWriteTime;
                    }
                    catch { }

                    long totalDirSize = 0;
                    int totalDirFiles = 0;
                    int totalDirFolders = 0;

                    // Single enumeration pass for both files and directories (drastically reduces Win32/SMB roundtrips)
                    IEnumerable<FileSystemInfo>? entries = null;
                    try
                    {
                        entries = dir.EnumerateFileSystemInfos();
                    }
                    catch (UnauthorizedAccessException)
                    {
                        node.ErrorMessage = "アクセス拒否";
                    }
                    catch (Exception ex)
                    {
                        node.ErrorMessage = ex.Message;
                    }

                    if (entries != null)
                    {
                        var subDirNodes = new List<FileItemNode>();
                        var fileNodes = new List<FileItemNode>();

                        foreach (var entry in entries)
                        {
                            if (ct.IsCancellationRequested) break;

                            if (entry is FileInfo fi)
                            {
                                try
                                {
                                    long fSize = fi.Length;
                                    totalDirSize += fSize;
                                    totalDirFiles++;
                                    TrackFile(fi, fSize);

                                    // Store files under directory
                                    fileNodes.Add(new FileItemNode
                                    {
                                        Name = fi.Name,
                                        FullPath = fi.FullName,
                                        Size = fSize,
                                        FileCount = 1,
                                        FolderCount = 0,
                                        IsDirectory = false,
                                        LastModified = fi.LastWriteTime,
                                        Parent = node,
                                        Level = depth + 1
                                    });
                                }
                                catch { }
                            }
                            else if (entry is DirectoryInfo sd)
                            {
                                // Skip junctions & reparse points to avoid infinite loops
                                if ((sd.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                                totalDirFolders++;
                                var subNode = ScanDirectoryInternal(sd, depth + 1, node);
                                totalDirSize += subNode.Size;
                                totalDirFiles += subNode.FileCount;
                                totalDirFolders += subNode.FolderCount;

                                subDirNodes.Add(subNode);
                            }
                        }

                        node.Size = totalDirSize;
                        node.FileCount = totalDirFiles;
                        node.FolderCount = totalDirFolders;

                        // Sort subdirectories & files descending by size
                        subDirNodes.Sort((a, b) => b.Size.CompareTo(a.Size));
                        fileNodes.Sort((a, b) => b.Size.CompareTo(a.Size));

                        foreach (var s in subDirNodes) node.Children.Add(s);
                        foreach (var f in fileNodes) node.Children.Add(f);
                    }

                    return node;
                }

                var rootNode = ScanDirectoryInternal(rootDir, 0, null);

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
