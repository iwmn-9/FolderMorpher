using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FolderMorpher.Services
{
    /// <summary>
    /// パス正規化エンジン（Canonical Path Engine）
    /// ネットワークドライブ（Z:\）とUNC（\\server\share）を同一視し、キャッシュや走査の二重化を根絶する。
    /// </summary>
    public static class PathCanonicalizer
    {
        [DllImport("mpr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int WNetGetConnection(string? lpLocalName, StringBuilder lpRemoteName, ref int lpnLength);

        private const int NO_ERROR = 0;
        private sealed record CachedDriveMapping(string? RemoteUnc, long TimestampTicks);
        private static readonly ConcurrentDictionary<string, CachedDriveMapping> DriveToUncCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        /// <summary>
        /// パスを正本（Canonical Path）に正規化します。
        /// ネットワークドライブ（Z:\Docs）は実体のUNCパス（\\server\share\Docs）へ自動解決されます。
        /// </summary>
        public static string Normalize(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;

            string p = path.Trim().Replace('/', '\\');

            // 拡張UNCプレフィックス (\\?\) の除去
            if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                p = @"\\" + p.Substring(8);
            }
            else if (p.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase))
            {
                p = p.Substring(4);
            }

            // ドライブレター形式 (例: Z:\...) の判定
            if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':')
            {
                string drive = p.Substring(0, 2).ToUpperInvariant(); // "Z:"
                string? remoteUnc = GetRemoteUncForDrive(drive);

                if (!string.IsNullOrEmpty(remoteUnc))
                {
                    string remainder = p.Length > 2 ? p.Substring(2) : string.Empty;
                    if (remainder.StartsWith('\\'))
                    {
                        p = remoteUnc.TrimEnd('\\') + remainder;
                    }
                    else if (!string.IsNullOrEmpty(remainder))
                    {
                        p = remoteUnc.TrimEnd('\\') + "\\" + remainder;
                    }
                    else
                    {
                        p = remoteUnc;
                    }
                }
            }

            // ルートパス以外の末尾バックスラッシュをトリム
            if (p.Length > 3 && p.EndsWith('\\'))
            {
                p = p.TrimEnd('\\');
            }

            return p;
        }

        /// <summary>
        /// 2つのパスが実体として同一であるかを判定します（Z:\ と \\server\share の同一視対応）。
        /// </summary>
        public static bool AreSamePath(string? pathA, string? pathB)
        {
            if (pathA == null && pathB == null) return true;
            if (pathA == null || pathB == null) return false;

            string normA = Normalize(pathA).ToLowerInvariant();
            string normB = Normalize(pathB).ToLowerInvariant();

            return string.Equals(normA, normB, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// パスがUNCまたはネットワークドライブであるかを判定します。
        /// </summary>
        public static bool IsNetworkPath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            string norm = Normalize(path);
            if (norm.StartsWith(@"\\")) return true;

            // WNetGetConnection が失敗しても、ドライブ種別が分かればUNC用のI/O上限を守る。
            try
            {
                string? root = Path.GetPathRoot(path);
                return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Network;
            }
            catch
            {
                return false;
            }
        }

        private const int ERROR_MORE_DATA = 234;

        /// <summary>
        /// ドライブレター（例: "Z:"）にマッピングされた UNC リモートパスを取得します。
        /// Win32 WNetGetConnection および HKCU\Network レジストリフォールバックにより
        /// 管理者昇格セッション（Linked Connections）下でも確実に直接UNCパスを解決します。
        /// </summary>
        private static string? GetRemoteUncForDrive(string drive)
        {
            if (DriveToUncCache.TryGetValue(drive, out var cached))
            {
                var elapsed = Stopwatch.GetElapsedTime(cached.TimestampTicks);
                if (elapsed < CacheTtl)
                {
                    return cached.RemoteUnc;
                }
            }

            string? resolved = ResolveRemoteUncDirect(drive);
            DriveToUncCache[drive] = new CachedDriveMapping(resolved, Stopwatch.GetTimestamp());
            return resolved;
        }

        private static string? ResolveRemoteUncDirect(string d)
        {
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;

            try
            {
                // 1. Win32 WNetGetConnection
                var sb = new StringBuilder(512);
                int len = sb.Capacity;
                int result = WNetGetConnection(d, sb, ref len);

                if (result == ERROR_MORE_DATA && len > sb.Capacity)
                {
                    sb.Capacity = len;
                    result = WNetGetConnection(d, sb, ref len);
                }

                if (result == NO_ERROR)
                {
                    string unc = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(unc)) return unc;
                }
            }
            catch
            {
                // mpr.dll呼び出し不可時はレジストリフォールバックへ
            }

            // 2. HKCU\Network\<ドライブレター>\RemotePath レジストリフォールバック
            try
            {
                string driveLetterOnly = d.TrimEnd(':');
                using var key = Registry.CurrentUser.OpenSubKey($@"Network\{driveLetterOnly}");
                if (key != null)
                {
                    var remotePath = key.GetValue("RemotePath") as string;
                    if (!string.IsNullOrWhiteSpace(remotePath))
                    {
                        return remotePath.Trim();
                    }
                }
            }
            catch { }

            return null;
        }

        /// <summary>
        /// パスからボリュームルート（"C:"）またはUNC共有ルート（"\\server\share"）を取得します。
        /// ネットワークドライブ（Z:\...）は正規化されて \\server\share が返されます。
        /// </summary>
        public static string GetVolumeOrShareRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "DEFAULT";
            string norm = Normalize(path);

            if (norm.StartsWith(@"\\"))
            {
                // \\server\share\path... から \\server\share を抽出
                int thirdSlash = norm.IndexOf('\\', 2);
                if (thirdSlash < 0) return norm;
                int fourthSlash = norm.IndexOf('\\', thirdSlash + 1);
                if (fourthSlash < 0) return norm;
                return norm.Substring(0, fourthSlash);
            }

            if (norm.Length >= 2 && norm[1] == ':')
            {
                return norm.Substring(0, 2).ToUpperInvariant();
            }

            return "DEFAULT";
        }

        /// <summary>
        /// キャッシュされたドライブマッピングをクリアします（ドライブ割り当て変更時用）。
        /// </summary>
        public static void ClearDriveCache()
        {
            DriveToUncCache.Clear();
        }
    }
}
