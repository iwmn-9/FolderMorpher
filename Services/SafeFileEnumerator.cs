using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FolderMorpher.Services
{
    public class ScanCoverage
    {
        public int TotalFoldersScanned { get; set; }
        public int AccessDeniedFolders { get; set; }
        public int TotalFilesFound { get; set; }
        public bool IsCompleteCoverage => AccessDeniedFolders == 0;
        public string SummaryText => IsCompleteCoverage
            ? $"{TotalFoldersScanned:N0} フォルダ完全走査 (アクセス拒否: 0)"
            : $"{TotalFoldersScanned:N0} フォルダ走査 (⚠️ アクセス拒否スキップ: {AccessDeniedFolders:N0} 箇所)";
    }

    /// <summary>
    /// アクセス拒否(UnauthorizedAccessException)やパス長制限で絶対に即死しない安全な反復ファイル列挙エンジン
    /// </summary>
    public static class SafeFileEnumerator
    {
        public static IEnumerable<FileInfo> EnumerateFilesSafe(
            string rootPath,
            string searchPattern = "*.*",
            ScanCoverage? coverage = null,
            CancellationToken ct = default)
        {
            if (!Directory.Exists(rootPath)) yield break;

            var rootDir = new DirectoryInfo(rootPath);
            var stack = new Stack<DirectoryInfo>();
            stack.Push(rootDir);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var current = stack.Pop();

                if (coverage != null) coverage.TotalFoldersScanned++;

                // サブディレクトリの安全プッシュ
                try
                {
                    var subDirs = current.GetDirectories();
                    foreach (var sub in subDirs)
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        stack.Push(sub);
                    }
                }
                catch (UnauthorizedAccessException)
                {
                    if (coverage != null) coverage.AccessDeniedFolders++;
                    continue;
                }
                catch (Exception)
                {
                    if (coverage != null) coverage.AccessDeniedFolders++;
                    continue;
                }

                // ファイルの安全列挙
                FileInfo[] files;
                try
                {
                    files = current.GetFiles(searchPattern);
                }
                catch
                {
                    continue;
                }

                foreach (var file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    if (coverage != null) coverage.TotalFilesFound++;
                    yield return file;
                }
            }
        }
    }
}
