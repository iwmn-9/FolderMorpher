using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AstraSize.Models;
using Microsoft.Win32.SafeHandles;

namespace AstraSize.Services.Mft
{
    public class MftScanService
    {
        private class FastNode
        {
            public ulong RecordNumber;
            public ulong ParentRecordNumber;
            public string Name = string.Empty;
            public long Size;
            public bool IsDirectory;
            public DateTime LastModified;
            public List<FastNode> Children = new();
            public int FileCount;
            public int FolderCount;
        }

        public static bool CanUseMft(string targetPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(targetPath)) return false;

                // 1. Must have a drive letter (e.g., C:\)
                var root = Path.GetPathRoot(targetPath);
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\")) return false; // UNC path not supported for direct MFT

                // 2. Must be Administrator
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                if (!principal.IsInRole(WindowsBuiltInRole.Administrator)) return false;

                // 3. Must be NTFS
                var driveInfo = new DriveInfo(root);
                if (!driveInfo.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) return false;

                return true;
            }
            catch
            {
                return false;
            }
        }

        public async Task<(FileItemNode rootNode, ScanSummary summary)> ScanPathAsync(
            string targetPath,
            IProgress<ScanProgress>? progress,
            CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var stopwatch = Stopwatch.StartNew();
                var cleanTargetPath = targetPath.TrimEnd('\\');
                if (cleanTargetPath.EndsWith(":") && cleanTargetPath.Length == 2)
                {
                    cleanTargetPath += "\\";
                }

                var rootDrive = Path.GetPathRoot(cleanTargetPath) ?? "C:\\";
                string driveLetter = rootDrive.Substring(0, 1).ToUpperInvariant();
                string volumePath = $@"\\.\{driveLetter}:";

                using var hVolume = NativeMethods.CreateFile(
                    volumePath,
                    NativeMethods.GENERIC_READ,
                    NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                    IntPtr.Zero,
                    NativeMethods.OPEN_EXISTING,
                    NativeMethods.FILE_FLAG_BACKUP_SEMANTICS | NativeMethods.FILE_FLAG_SEQUENTIAL_SCAN,
                    IntPtr.Zero);

                if (hVolume.IsInvalid)
                {
                    throw new UnauthorizedAccessException($"ボリューム {volumePath} のオープンに失敗しました (Win32エラー: {Marshal.GetLastWin32Error()})");
                }

                // 1. Get NTFS Volume Data
                int bufferSize = Marshal.SizeOf<NativeMethods.NTFS_VOLUME_DATA_BUFFER>();
                IntPtr pVolumeData = Marshal.AllocHGlobal(bufferSize);
                NativeMethods.NTFS_VOLUME_DATA_BUFFER volData;
                try
                {
                    if (!NativeMethods.DeviceIoControl(
                        hVolume,
                        NativeMethods.FSCTL_GET_NTFS_VOLUME_DATA,
                        IntPtr.Zero, 0,
                        pVolumeData, (uint)bufferSize,
                        out uint bytesReturned,
                        IntPtr.Zero))
                    {
                        throw new IOException($"FSCTL_GET_NTFS_VOLUME_DATA に失敗しました (Win32エラー: {Marshal.GetLastWin32Error()})");
                    }
                    volData = Marshal.PtrToStructure<NativeMethods.NTFS_VOLUME_DATA_BUFFER>(pVolumeData);
                }
                finally
                {
                    Marshal.FreeHGlobal(pVolumeData);
                }

                uint bytesPerCluster = volData.BytesPerCluster;
                uint bytesPerRecord = volData.BytesPerFileRecordSegment;
                long mftStartOffset = (long)volData.MftStartLcn * bytesPerCluster;

                // 2. Read Record 0 ($MFT) to get full MFT Data Runs (Extents)
                byte[] rec0Buffer = new byte[bytesPerRecord];
                if (!NativeMethods.SetFilePointerEx(hVolume, mftStartOffset, out _, 0))
                {
                    throw new IOException("MFT開始位置へのシークに失敗しました");
                }
                if (!NativeMethods.ReadFile(hVolume, rec0Buffer, bytesPerRecord, out _, IntPtr.Zero))
                {
                    throw new IOException("Record 0 ($MFT) の読み込みに失敗しました");
                }

                // Parse $DATA attribute of Record 0 to get MFT extents
                var extents = ExtractMftExtents(rec0Buffer, (int)bytesPerRecord, (long)volData.MftStartLcn);
                if (extents.Count == 0)
                {
                    // Fallback to primary extent
                    extents.Add(new MftExtent
                    {
                        StartLcn = (long)volData.MftStartLcn,
                        ClusterCount = (long)(volData.MftValidDataLength / bytesPerCluster)
                    });
                }

                // 3. Scan all records across all extents
                var allNodes = new Dictionary<ulong, FastNode>(500000);
                const int readChunkClusters = 64; // Read 64 clusters at a time (typically 256KB)
                int readChunkBytes = (int)(readChunkClusters * bytesPerCluster);
                byte[] chunkBuffer = new byte[readChunkBytes];

                ulong currentRecordNum = 0;
                long lastProgressTime = 0;

                foreach (var extent in extents)
                {
                    ct.ThrowIfCancellationRequested();

                    long extentOffset = extent.StartLcn * bytesPerCluster;
                    long totalExtentBytes = extent.ClusterCount * bytesPerCluster;
                    long bytesReadInExtent = 0;

                    if (!NativeMethods.SetFilePointerEx(hVolume, extentOffset, out _, 0))
                    {
                        continue;
                    }

                    while (bytesReadInExtent < totalExtentBytes)
                    {
                        if (ct.IsCancellationRequested) break;

                        uint toRead = (uint)Math.Min(readChunkBytes, totalExtentBytes - bytesReadInExtent);
                        if (!NativeMethods.ReadFile(hVolume, chunkBuffer, toRead, out uint actualRead, IntPtr.Zero) || actualRead == 0)
                        {
                            break;
                        }

                        bytesReadInExtent += actualRead;

                        // Parse each 1024-byte record in the buffer
                        for (int offset = 0; offset + bytesPerRecord <= actualRead; offset += (int)bytesPerRecord)
                        {
                            if (MftRecordParser.TryParseRecord(chunkBuffer, offset, (int)bytesPerRecord, currentRecordNum, out var item) && item != null)
                            {
                                allNodes[item.RecordNumber] = new FastNode
                                {
                                    RecordNumber = item.RecordNumber,
                                    ParentRecordNumber = item.ParentRecordNumber,
                                    Name = item.Name,
                                    Size = item.Size,
                                    IsDirectory = item.IsDirectory,
                                    LastModified = item.LastModified
                                };
                            }
                            currentRecordNum++;
                        }

                        var now = stopwatch.ElapsedMilliseconds;
                        if (now - lastProgressTime > 150)
                        {
                            lastProgressTime = now;
                            progress?.Report(new ScanProgress
                            {
                                CurrentPath = $"MFT解析中... ({allNodes.Count:N0} アイテム検出)",
                                FilesScanned = allNodes.Count,
                                BytesScanned = 0
                            });
                        }
                    }
                }

                // 4. Link children to parents
                const ulong ROOT_DIR_RECORD = 5; // NTFS Root Directory record is always 5
                foreach (var node in allNodes.Values)
                {
                    if (node.RecordNumber != node.ParentRecordNumber &&
                        allNodes.TryGetValue(node.ParentRecordNumber, out var parent))
                    {
                        parent.Children.Add(node);
                    }
                }

                // 5. Locate Target Root Node
                FastNode targetRootNode;
                string relativeSubPath = cleanTargetPath.Substring(rootDrive.Length).Trim('\\');

                if (string.IsNullOrEmpty(relativeSubPath))
                {
                    if (allNodes.TryGetValue(ROOT_DIR_RECORD, out var rootDir))
                    {
                        targetRootNode = rootDir;
                        targetRootNode.Name = cleanTargetPath;
                    }
                    else
                    {
                        throw new InvalidOperationException("MFTルートディレクトリ(Record 5)が見つかりませんでした");
                    }
                }
                else
                {
                    // Traverse path down from root
                    string[] parts = relativeSubPath.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                    if (!allNodes.TryGetValue(ROOT_DIR_RECORD, out var current))
                    {
                        throw new InvalidOperationException("MFTルートディレクトリ(Record 5)が見つかりませんでした");
                    }

                    foreach (var part in parts)
                    {
                        FastNode? match = null;
                        foreach (var child in current.Children)
                        {
                            if (child.IsDirectory && child.Name.Equals(part, StringComparison.OrdinalIgnoreCase))
                            {
                                match = child;
                                break;
                            }
                        }

                        if (match == null)
                        {
                            throw new DirectoryNotFoundException($"MFT内で指定フォルダ '{part}' が見つかりませんでした: {targetPath}");
                        }
                        current = match;
                    }
                    targetRootNode = current;
                }

                // 6. Calculate sizes, stats, and convert to FileItemNode
                var largestFiles = new List<LargestFileInfo>(32);
                long minLargestThreshold = 0;
                var extensionDict = new Dictionary<string, (long size, int count)>(StringComparer.OrdinalIgnoreCase);
                var cleanupList = new List<CleanupCandidate>(64);
                long totalBytesScanned = 0;
                int totalFilesScanned = 0;

                void AggregateNode(FastNode fn, string fullPath)
                {
                    if (!fn.IsDirectory)
                    {
                        totalBytesScanned += fn.Size;
                        totalFilesScanned++;

                        string ext = Path.GetExtension(fn.Name);
                        // Largest files
                        if (largestFiles.Count < 30)
                        {
                            largestFiles.Add(new LargestFileInfo
                            {
                                Name = fn.Name,
                                FullPath = fullPath,
                                Size = fn.Size,
                                Extension = ext,
                                Category = DiskScanService.GetCategoryForExtension(ext)
                            });
                            if (largestFiles.Count == 30)
                            {
                                largestFiles.Sort((a, b) => b.Size.CompareTo(a.Size));
                                minLargestThreshold = largestFiles[^1].Size;
                            }
                        }
                        else if (fn.Size > minLargestThreshold)
                        {
                            largestFiles.Add(new LargestFileInfo
                            {
                                Name = fn.Name,
                                FullPath = fullPath,
                                Size = fn.Size,
                                Extension = ext,
                                Category = DiskScanService.GetCategoryForExtension(ext)
                            });
                            largestFiles.Sort((a, b) => b.Size.CompareTo(a.Size));
                            largestFiles.RemoveAt(largestFiles.Count - 1);
                            minLargestThreshold = largestFiles[^1].Size;
                        }

                        // Extension stats
                        string extKey = string.IsNullOrEmpty(ext) ? "(なし)" : ext.ToLowerInvariant();
                        if (extensionDict.TryGetValue(extKey, out var stat))
                        {
                            extensionDict[extKey] = (stat.size + fn.Size, stat.count + 1);
                        }
                        else
                        {
                            extensionDict[extKey] = (fn.Size, 1);
                        }

                        // Cleanup candidates
                        if (ext.Equals(".dmp", StringComparison.OrdinalIgnoreCase) ||
                            ext.Equals(".tmp", StringComparison.OrdinalIgnoreCase) ||
                            (ext.Equals(".log", StringComparison.OrdinalIgnoreCase) && fn.Size > 50 * 1024 * 1024))
                        {
                            if (cleanupList.Count < 100)
                            {
                                cleanupList.Add(new CleanupCandidate
                                {
                                    Name = fn.Name,
                                    FullPath = fullPath,
                                    Size = fn.Size,
                                    Reason = ext.Equals(".dmp", StringComparison.OrdinalIgnoreCase) ? "クラッシュダンプ" : "一時/巨大ログファイル"
                                });
                            }
                        }
                    }
                    else
                    {
                        long dirSize = 0;
                        int dirFiles = 0;
                        int dirFolders = 0;

                        foreach (var child in fn.Children)
                        {
                            string childPath = fullPath.EndsWith("\\") ? fullPath + child.Name : fullPath + "\\" + child.Name;
                            AggregateNode(child, childPath);
                            dirSize += child.Size;
                            if (child.IsDirectory)
                            {
                                dirFolders += 1 + child.FolderCount;
                                dirFiles += child.FileCount;
                            }
                            else
                            {
                                dirFiles++;
                            }
                        }

                        fn.Size = dirSize;
                        fn.FileCount = dirFiles;
                        fn.FolderCount = dirFolders;
                    }
                }

                AggregateNode(targetRootNode, cleanTargetPath);

                // Convert FastNode tree to WPF FileItemNode tree
                FileItemNode ConvertToFileItemNode(FastNode fn, string fullPath, int level, FileItemNode? parent)
                {
                    var itemNode = new FileItemNode
                    {
                        Name = fn.Name,
                        FullPath = fullPath,
                        Size = fn.Size,
                        FileCount = fn.FileCount,
                        FolderCount = fn.FolderCount,
                        IsDirectory = fn.IsDirectory,
                        LastModified = fn.LastModified,
                        Level = level,
                        Parent = parent
                    };

                    if (fn.IsDirectory && fn.Children.Count > 0)
                    {
                        // Sort children descending by size
                        fn.Children.Sort((a, b) => b.Size.CompareTo(a.Size));

                        foreach (var c in fn.Children)
                        {
                            string cPath = fullPath.EndsWith("\\") ? fullPath + c.Name : fullPath + "\\" + c.Name;
                            itemNode.Children.Add(ConvertToFileItemNode(c, cPath, level + 1, itemNode));
                        }
                    }

                    return itemNode;
                }

                var rootWpfNode = ConvertToFileItemNode(targetRootNode, cleanTargetPath, 0, null);
                DiskScanService.CalculatePercentages(rootWpfNode, rootWpfNode.Size > 0 ? rootWpfNode.Size : 1);

                stopwatch.Stop();
                largestFiles.Sort((a, b) => b.Size.CompareTo(a.Size));

                var extensionStats = new List<ExtensionStat>();
                foreach (var kv in extensionDict)
                {
                    extensionStats.Add(new ExtensionStat
                    {
                        Extension = kv.Key,
                        TotalSize = kv.Value.size,
                        FileCount = kv.Value.count,
                        Percentage = Math.Min(100.0, (double)kv.Value.size / (rootWpfNode.Size > 0 ? rootWpfNode.Size : 1) * 100.0)
                    });
                }
                extensionStats.Sort((a, b) => b.TotalSize.CompareTo(a.TotalSize));

                var summary = new ScanSummary
                {
                    TargetPath = cleanTargetPath,
                    TotalBytes = rootWpfNode.Size,
                    TotalFiles = totalFilesScanned,
                    TotalFolders = rootWpfNode.FolderCount,
                    ElapsedSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
                    LargestFiles = largestFiles,
                    ExtensionStats = extensionStats,
                    CleanupCandidates = cleanupList,
                    ScanMode = "⚡ MFT Boost"
                };

                return (rootWpfNode, summary);
            }, ct);
        }

        private static List<MftExtent> ExtractMftExtents(byte[] recBuffer, int recordSize, long defaultMftStartLcn)
        {
            var extents = new List<MftExtent>();
            if (recBuffer.Length < recordSize) return extents;

            ushort firstAttrOffset = BitConverter.ToUInt16(recBuffer, 0x14);
            int attrOffset = firstAttrOffset;

            while (attrOffset + 8 <= recordSize)
            {
                uint attrType = BitConverter.ToUInt32(recBuffer, attrOffset);
                if (attrType == 0xFFFFFFFF || attrType == 0) break;

                uint attrLength = BitConverter.ToUInt32(recBuffer, attrOffset + 4);
                if (attrLength == 0 || attrOffset + attrLength > recordSize) break;

                byte nonResident = recBuffer[attrOffset + 8];

                // $DATA attribute of $MFT
                if (attrType == 0x80 && nonResident == 1)
                {
                    ushort dataRunsOffset = BitConverter.ToUInt16(recBuffer, attrOffset + 0x20);
                    int runOffset = attrOffset + dataRunsOffset;
                    if (runOffset < attrOffset + attrLength)
                    {
                        return MftDataRunDecoder.DecodeDataRuns(recBuffer, runOffset, defaultMftStartLcn);
                    }
                }

                attrOffset += (int)attrLength;
            }

            return extents;
        }
    }
}
