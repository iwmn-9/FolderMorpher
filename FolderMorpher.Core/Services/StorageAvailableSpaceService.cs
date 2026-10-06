using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FolderMorpher.Services
{
    public readonly record struct StorageAvailableSpace(long AvailableBytes, long TotalBytes);

    public static class StorageAvailableSpaceService
    {
        [DllImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetDiskFreeSpaceEx(
            string directoryName,
            out ulong freeBytesAvailableToCaller,
            out ulong totalNumberOfBytes,
            out ulong totalNumberOfFreeBytes);

        public static StorageAvailableSpace? TryGet(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || !Path.IsPathFullyQualified(targetPath)) return null;

            // UNC share roots require the trailing separator; directory paths work for local and UNC targets.
            var directory = targetPath.Trim().Replace('/', '\\').TrimEnd('\\') + "\\";
            if (!GetDiskFreeSpaceEx(directory, out var available, out var total, out _)) return null;
            return new StorageAvailableSpace(
                available > long.MaxValue ? long.MaxValue : (long)available,
                total > long.MaxValue ? long.MaxValue : (long)total);
        }

        public static bool IsLowSpace(StorageAvailableSpace space) =>
            space.TotalBytes > 0 && space.AvailableBytes <= space.TotalBytes / 10;
    }
}
