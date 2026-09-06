using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using AstraSize.Models;

namespace AstraSize.Services
{
    public class DriveItem
    {
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public long TotalSize { get; set; }
        public long FreeSpace { get; set; }
        public string FileSystem { get; set; } = "NTFS";
        public bool IsReady { get; set; }

        public string FormattedTotal => FileItemNode.FormatBytes(TotalSize);
        public string FormattedFree => FileItemNode.FormatBytes(FreeSpace);
        public string FormattedUsed => FileItemNode.FormatBytes(Math.Max(0, TotalSize - FreeSpace));
        public double UsedPercentage => TotalSize > 0 ? (double)(TotalSize - FreeSpace) / TotalSize * 100.0 : 0;
    }

    public static class DriveInfoService
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailable,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        public static List<DriveItem> GetLocalDrives()
        {
            var list = new List<DriveItem>();
            try
            {
                var drives = DriveInfo.GetDrives();
                foreach (var d in drives)
                {
                    if (d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Network || d.DriveType == DriveType.Removable))
                    {
                        var name = d.Name;
                        var label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "ローカル ディスク" : d.VolumeLabel;
                        list.Add(new DriveItem
                        {
                            Name = name,
                            DisplayName = $"{label} ({name.TrimEnd('\\')})",
                            TotalSize = d.TotalSize,
                            FreeSpace = d.AvailableFreeSpace,
                            FileSystem = d.DriveFormat,
                            IsReady = true
                        });
                    }
                }
            }
            catch
            {
                // Fallback
            }
            return list;
        }

        public static DriveItem? GetVolumeInfoForPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;

            try
            {
                // Ensure trailing slash for root / share
                string dirPath = path;
                if (!dirPath.EndsWith("\\")) dirPath += "\\";

                if (GetDiskFreeSpaceEx(dirPath, out ulong freeBytes, out ulong totalBytes, out _))
                {
                    string displayName = path;
                    if (path.StartsWith(@"\\"))
                    {
                        displayName = $"共有フォルダ: {path}";
                    }
                    else
                    {
                        var root = Path.GetPathRoot(path);
                        displayName = $"ボリューム ({root?.TrimEnd('\\')})";
                    }

                    return new DriveItem
                    {
                        Name = path,
                        DisplayName = displayName,
                        TotalSize = (long)totalBytes,
                        FreeSpace = (long)freeBytes,
                        FileSystem = path.StartsWith(@"\\") ? "SMB / Network" : "NTFS",
                        IsReady = true
                    };
                }
            }
            catch
            {
                // Ignore
            }

            return null;
        }
    }
}
