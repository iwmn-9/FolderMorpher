using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FolderMorpher.Services
{
    public sealed class SafeFindHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeFindHandle() : base(true) { }
        public SafeFindHandle(IntPtr handle) : base(true) { SetHandle(handle); }

        protected override bool ReleaseHandle()
        {
            return FindClose(handle);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr hFindFile);
    }

    public readonly struct NativeFindEntry
    {
        public readonly string Name;
        public readonly FileAttributes Attributes;
        public readonly long Size;
        public readonly DateTime LastWriteTimeUtc;
        public readonly bool IsDirectory;
        public readonly bool IsReparsePoint;

        public NativeFindEntry(
            string name,
            FileAttributes attributes,
            long size,
            DateTime lastWriteTimeUtc)
        {
            Name = name;
            Attributes = attributes;
            Size = size;
            LastWriteTimeUtc = lastWriteTimeUtc;
            IsDirectory = (attributes & FileAttributes.Directory) != 0;
            IsReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
        }
    }

    /// <summary>
    /// Win32 FindFirstFileExW / FindNextFileW による高効率・メモリリークゼロのディレクトリ走査エンジン。
    /// 【高速化技術】
    /// 1. FindExInfoBasic: 8.3短縮名の取得をスキップし、ファイルサーバー側の検索負荷とSMB通信量を削減。
    /// 2. FIND_FIRST_EX_LARGE_FETCH: SMB/NTFS層に巨大バッファを要求し、1往復のパケット内に最大件数のエントリを一括取得。
    /// 3. SafeFindHandle: BCL標準の SafeHandle による確実なリソース解放（メモリリーク・ハンドルリーク皆無）。
    /// </summary>
    public static class NativeDirectoryEnumerator
    {
        private const int FIND_FIRST_EX_LARGE_FETCH = 0x00000002;

        private enum FINDEX_INFO_LEVELS
        {
            FindExInfoStandard = 0,
            FindExInfoBasic = 1,
            FindExInfoMaxInfoLevel
        }

        private enum FINDEX_SEARCH_OPS
        {
            FindExSearchNameMatch = 0,
            FindExSearchLimitToDirectories = 1,
            FindExSearchLimitToDevices = 2
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATAW
        {
            public FileAttributes dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern SafeFindHandle FindFirstFileExW(
            string lpFileName,
            FINDEX_INFO_LEVELS fInfoLevelId,
            out WIN32_FIND_DATAW lpFindFileData,
            FINDEX_SEARCH_OPS fSearchOp,
            IntPtr lpSearchFilter,
            int dwAdditionalFlags);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextFileW(
            SafeFindHandle hFindFile,
            out WIN32_FIND_DATAW lpFindFileData);

        private const int ERROR_FILE_NOT_FOUND = 2;
        private const int ERROR_NO_MORE_FILES = 18;

        /// <summary>
        /// 指定フォルダー直下のエントリ（ファイルおよびサブディレクトリ）を巨大バッファかつ8.3スキップで列挙する。
        /// 1回のフォルダーオープンで両方を取得するため、SMBネットワーク往復回数を半減させる。
        /// </summary>
        public static bool TryEnumerateEntries(
            string folderPath,
            List<NativeFindEntry> subDirectories,
            List<NativeFindEntry> files,
            out string? errorMessage)
        {
            errorMessage = null;
            subDirectories.Clear();
            files.Clear();

            if (string.IsNullOrEmpty(folderPath))
            {
                errorMessage = "パスが空です";
                return false;
            }

            string searchPattern = BuildSearchPattern(folderPath);

            using var handle = FindFirstFileExW(
                searchPattern,
                FINDEX_INFO_LEVELS.FindExInfoBasic,
                out var findData,
                FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                IntPtr.Zero,
                FIND_FIRST_EX_LARGE_FETCH);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ERROR_FILE_NOT_FOUND || error == ERROR_NO_MORE_FILES)
                {
                    // 空フォルダー
                    return true;
                }

                errorMessage = error switch
                {
                    5 => "アクセス拒否 (5)",
                    3 => "パスが見つかりません (3)",
                    _ => $"Win32エラー: {error}"
                };
                return false;
            }

            do
            {
                string name = findData.cFileName;
                if (name == "." || name == "..") continue;

                long size = ((long)findData.nFileSizeHigh << 32) | (findData.nFileSizeLow & 0xFFFFFFFFL);
                DateTime lastWriteUtc = FileTimeToUtc(findData.ftLastWriteTime);

                var entry = new NativeFindEntry(name, findData.dwFileAttributes, size, lastWriteUtc);

                if (entry.IsDirectory)
                {
                    subDirectories.Add(entry);
                }
                else
                {
                    files.Add(entry);
                }
            }
            while (FindNextFileW(handle, out findData));

            return true;
        }

        private static string BuildSearchPattern(string folderPath)
        {
            string clean = folderPath.TrimEnd('\\');
            // 長大パス(>240文字)またはUNCパスの正規化接頭辞
            string normalized = NormalizePathForWin32(clean);
            return normalized + "\\*";
        }

        public static string NormalizePathForWin32(string path)
        {
            if (path.StartsWith(@"\\?\")) return path;

            if (path.StartsWith(@"\\"))
            {
                // UNCパス: \\server\share -> \\?\UNC\server\share
                return @"\\?\UNC\" + path.Substring(2);
            }

            if (path.Length >= 2 && path[1] == ':')
            {
                // ドライブレター: C:\path -> \\?\C:\path
                return @"\\?\" + path;
            }

            return path;
        }

        private static DateTime FileTimeToUtc(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        {
            long fileTime = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
            if (fileTime <= 0) return DateTime.MinValue;
            try
            {
                return DateTime.FromFileTimeUtc(fileTime);
            }
            catch
            {
                return DateTime.MinValue;
            }
        }
    }
}
