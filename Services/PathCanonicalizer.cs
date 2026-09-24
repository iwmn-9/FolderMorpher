using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

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
        private static readonly ConcurrentDictionary<string, string?> DriveToUncCache = new(StringComparer.OrdinalIgnoreCase);

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
            return norm.StartsWith(@"\\");
        }

        /// <summary>
        /// ドライブレター（例: "Z:"）にマッピングされた UNC リモートパスを取得します。
        /// </summary>
        private static string? GetRemoteUncForDrive(string drive)
        {
            return DriveToUncCache.GetOrAdd(drive, d =>
            {
                try
                {
                    var sb = new StringBuilder(512);
                    int len = sb.Capacity;
                    int result = WNetGetConnection(d, sb, ref len);

                    if (result == NO_ERROR)
                    {
                        string unc = sb.ToString().Trim();
                        return string.IsNullOrEmpty(unc) ? null : unc;
                    }
                }
                catch
                {
                    // mpr.dll呼び出し不可や権限エラー時はnull（ローカル扱い）
                }
                return null;
            });
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
