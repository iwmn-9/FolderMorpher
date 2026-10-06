using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderMorpher.Services
{
    /// <summary>
    /// Filunestの設計思想に基づく NTFS 物理ファイルID（VolumeSerialNumber + FileIndex）取得ヘルパー。
    /// ハードリンクやシンボリックリンクによる「同一物理実体（Same Physical Entity）」を識別し、
    /// 重複の二重カウント防止や原本の誤削除を物理レベルで防止します。
    /// </summary>
    public static class NtfsFileIdentityHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct BY_HANDLE_FILE_INFORMATION
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint dwVolumeSerialNumber;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint nNumberOfLinks;
            public uint nFileIndexHigh;
            public uint nFileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(
            SafeFileHandle hFile,
            out BY_HANDLE_FILE_INFORMATION lpFileInformation);

        /// <summary>
        /// ファイルパスから物理ファイルID（VolumeSerialNumber:FileIndexHigh:FileIndexLow）を取得します。
        /// 同一ボリューム内でハードリンクされている2つのファイルは、パスが異なっても全く同じIDを返します。
        /// 取得失敗時（非Windows、非NTFS、アクセス権限不足等）は null を返します。
        /// </summary>
        public static string? TryGetFileIdentity(string path)
        {
            if (string.IsNullOrEmpty(path) || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return null;

            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
                return TryGetFileIdentity(fs.SafeFileHandle);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 既存のファイルハンドルから物理ファイルIDを取得します。
        /// </summary>
        public static string? TryGetFileIdentity(SafeFileHandle handle)
        {
            if (handle == null || handle.IsInvalid || handle.IsClosed || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return null;

            try
            {
                if (GetFileInformationByHandle(handle, out var info))
                {
                    return $"{info.dwVolumeSerialNumber:X8}:{info.nFileIndexHigh:X8}:{info.nFileIndexLow:X8}";
                }
            }
            catch
            {
                return null;
            }
            return null;
        }

        /// <summary>
        /// 2つのファイルパスが同一の物理実体（ハードリンク）であるかを判定します。
        /// </summary>
        public static bool IsSamePhysicalFile(string path1, string path2)
        {
            if (string.Equals(path1, path2, StringComparison.OrdinalIgnoreCase)) return true;
            string? id1 = TryGetFileIdentity(path1);
            string? id2 = TryGetFileIdentity(path2);
            return id1 != null && id2 != null && string.Equals(id1, id2, StringComparison.OrdinalIgnoreCase);
        }
    }
}
