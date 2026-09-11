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
        public readonly DateTime CreationTimeUtc;
        public readonly DateTime LastWriteTimeUtc;
        public readonly DateTime LastAccessTimeUtc;
        public readonly bool IsDirectory;
        public readonly bool IsReparsePoint;

        public NativeFindEntry(
            string name,
            FileAttributes attributes,
            long size,
            DateTime creationTimeUtc,
            DateTime lastWriteTimeUtc,
            DateTime lastAccessTimeUtc)
        {
            Name = name;
            Attributes = attributes;
            Size = size;
            CreationTimeUtc = creationTimeUtc;
            LastWriteTimeUtc = lastWriteTimeUtc;
            LastAccessTimeUtc = lastAccessTimeUtc;
            IsDirectory = (attributes & FileAttributes.Directory) != 0;
            IsReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
        }
    }

    /// <summary>
    /// Win32 FindFirstFileExW / FindNextFileW による高効率・メモリリークゼロのディレクトリ走査エンジン。
    /// 【高速化・耐障害性アーキテクチャ】
    /// 1. FindExInfoBasic: 8.3短縮名の取得をスキップし、ファイルサーバー側の検索負荷とSMB通信量を削減。
    /// 2. FIND_FIRST_EX_LARGE_FETCH: SMB/NTFS層に巨大バッファを要求し、1往復のパケット内に最大件数のエントリを一括取得。
    /// 3. SafeFindHandle: BCL標準の SafeHandle による確実なリソース解放（メモリリーク・ハンドルリーク皆無）。
    /// 4. 厳格なエラー判定: FindNextFileW の終了時に ERROR_NO_MORE_FILES 以外の通信切断・I/Oエラーを異常終了として検出。
    /// 5. 3段自動フォールバック: (1) Basic+LargeFetch ➔ (2) Standard(フラグ無) ➔ (3) マネージド .NET DirectoryInfo。
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
        private const int ERROR_PATH_NOT_FOUND = 3;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_NO_MORE_FILES = 18;

        /// <summary>
        /// 指定フォルダー直下のエントリ（ファイルおよびサブディレクトリ）を安全に一括列挙する。
        /// 3段自動フォールバックにより、特殊なネットワーク環境や権限制約下でも最大限の取得を試みる。
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

            // 段数 1: 最速 (FindExInfoBasic + FIND_FIRST_EX_LARGE_FETCH)
            if (TryEnumerateWin32(searchPattern, FINDEX_INFO_LEVELS.FindExInfoBasic, FIND_FIRST_EX_LARGE_FETCH, subDirectories, files, out errorMessage))
            {
                return true;
            }

            // アクセス拒否 (5) やパスが存在しない (3) の場合はフォールバックしても無駄なので即時返却
            if (errorMessage != null && (errorMessage.Contains("(5)") || errorMessage.Contains("(3)")))
            {
                return false;
            }

            // 段数 2: 互換 (FindExInfoStandard, 追加フラグなし)
            subDirectories.Clear();
            files.Clear();
            if (TryEnumerateWin32(searchPattern, FINDEX_INFO_LEVELS.FindExInfoStandard, 0, subDirectories, files, out errorMessage))
            {
                return true;
            }

            // 段数 3: マネージド .NET DirectoryInfo フォールバック
            subDirectories.Clear();
            files.Clear();
            return TryEnumerateManagedFallback(folderPath, subDirectories, files, out errorMessage);
        }

        private static bool TryEnumerateWin32(
            string searchPattern,
            FINDEX_INFO_LEVELS level,
            int flags,
            List<NativeFindEntry> subDirectories,
            List<NativeFindEntry> files,
            out string? errorMessage)
        {
            errorMessage = null;

            using var handle = FindFirstFileExW(
                searchPattern,
                level,
                out var findData,
                FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                IntPtr.Zero,
                flags);

            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastWin32Error();
                if (error == ERROR_FILE_NOT_FOUND || error == ERROR_NO_MORE_FILES)
                {
                    // 空フォルダー（正常）
                    return true;
                }

                errorMessage = FormatWin32Error(error);
                return false;
            }

            do
            {
                string name = findData.cFileName;
                if (name == "." || name == "..") continue;

                long size = ((long)findData.nFileSizeHigh << 32) | (findData.nFileSizeLow & 0xFFFFFFFFL);
                DateTime creationUtc = FileTimeToUtc(findData.ftCreationTime);
                DateTime lastWriteUtc = FileTimeToUtc(findData.ftLastWriteTime);
                DateTime lastAccessUtc = FileTimeToUtc(findData.ftLastAccessTime);

                var entry = new NativeFindEntry(name, findData.dwFileAttributes, size, creationUtc, lastWriteUtc, lastAccessUtc);

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

            // ★ Sol指摘2: FindNextFileW 終了理由の厳格判定
            // 正常終了なら ERROR_NO_MORE_FILES (18)。それ以外（SMB切断やI/Oエラー）は異常中断として検出
            int finalError = Marshal.GetLastWin32Error();
            if (finalError != 0 && finalError != ERROR_NO_MORE_FILES && finalError != ERROR_FILE_NOT_FOUND)
            {
                errorMessage = $"列挙途中エラー: {FormatWin32Error(finalError)}";
                return false;
            }

            return true;
        }

        private static bool TryEnumerateManagedFallback(
            string folderPath,
            List<NativeFindEntry> subDirectories,
            List<NativeFindEntry> files,
            out string? errorMessage)
        {
            errorMessage = null;
            try
            {
                var dirInfo = new DirectoryInfo(folderPath);
                foreach (var entry in dirInfo.EnumerateFileSystemInfos())
                {
                    bool isDir = (entry.Attributes & FileAttributes.Directory) != 0;
                    long size = 0;
                    if (!isDir && entry is FileInfo fi)
                    {
                        size = fi.Length;
                    }

                    var nativeEntry = new NativeFindEntry(
                        entry.Name,
                        entry.Attributes,
                        size,
                        entry.CreationTimeUtc,
                        entry.LastWriteTimeUtc,
                        entry.LastAccessTimeUtc);

                    if (isDir)
                    {
                        subDirectories.Add(nativeEntry);
                    }
                    else
                    {
                        files.Add(nativeEntry);
                    }
                }
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                errorMessage = "アクセス拒否 (5)";
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                errorMessage = "パスが見つかりません (3)";
                return false;
            }
            catch (Exception ex)
            {
                errorMessage = $".NET列挙エラー: {ex.Message}";
                return false;
            }
        }

        private static string FormatWin32Error(int error)
        {
            return error switch
            {
                ERROR_ACCESS_DENIED => "アクセス拒否 (5)",
                ERROR_PATH_NOT_FOUND => "パスが見つかりません (3)",
                ERROR_FILE_NOT_FOUND => "ファイルが見つかりません (2)",
                59 => "ネットワーク予期せぬエラー (59)",
                64 => "ネットワーク名が利用不能 (64)",
                _ => $"Win32エラー: {error}"
            };
        }

        private static string BuildSearchPattern(string folderPath)
        {
            string clean = folderPath.TrimEnd('\\');
            string normalized = NormalizePathForWin32(clean);
            return normalized + "\\*";
        }

        public static string NormalizePathForWin32(string path)
        {
            if (path.StartsWith(@"\\?\")) return path;

            if (path.StartsWith(@"\\"))
            {
                return @"\\?\UNC\" + path.Substring(2);
            }

            if (path.Length >= 2 && path[1] == ':')
            {
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
