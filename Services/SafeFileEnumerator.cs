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
    /// アクセス拒否(UnauthorizedAccessException)やパス長制限で絶対に即死しない安全な反復ファイル列挙エンジン。
    /// Win32 FindFirstFileExW (FindExInfoBasic + LargeFetch) による巨大バッファ・8.3スキップ・並列度2対応。
    /// </summary>
    public static class SafeFileEnumerator
    {
        public static IEnumerable<FileInfo> EnumerateFilesSafe(
            string rootPath,
            string searchPattern = "*.*",
            ScanCoverage? coverage = null,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) yield break;

            var stack = new Stack<string>();
            stack.Push(rootPath);

            var subDirs = new List<NativeFindEntry>(64);
            var files = new List<NativeFindEntry>(128);

            bool matchAll = searchPattern == "*.*" || searchPattern == "*";

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var currentPath = stack.Pop();

                if (coverage != null) coverage.TotalFoldersScanned++;

                if (!NativeDirectoryEnumerator.TryEnumerateEntries(currentPath, subDirs, files, out var error))
                {
                    if (coverage != null) coverage.AccessDeniedFolders++;
                    continue;
                }

                // サブディレクトリの安全プッシュ (Junction / ReparsePoint 除外)
                for (int i = 0; i < subDirs.Count; i++)
                {
                    var sd = subDirs[i];
                    if (sd.IsReparsePoint) continue;
                    stack.Push(Path.Combine(currentPath, sd.Name));
                }

                // ファイルの返却
                for (int i = 0; i < files.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var f = files[i];
                    if (!matchAll && !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(searchPattern, f.Name, ignoreCase: true))
                        continue;

                    if (coverage != null) coverage.TotalFilesFound++;
                    yield return new FileInfo(Path.Combine(currentPath, f.Name));
                }
            }
        }

        /// <summary>
        /// 並列度2（デュアルワーカー）による高効率な並行ディレクトリスキャン。
        /// 1ワーカーがSMBネットワーク応答待ちの間に別ワーカーが次のフォルダーを要求し、他業務ディスクを荒らさずに通信レイテンシを相殺。
        /// </summary>
        public static async Task<List<FileInfo>> EnumerateFilesSafeParallelAsync(
            string rootPath,
            string searchPattern = "*.*",
            ScanCoverage? coverage = null,
            Action<int>? onProgress = null,
            CancellationToken ct = default)
        {
            var resultFiles = new System.Collections.Concurrent.ConcurrentBag<FileInfo>();
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return new List<FileInfo>();

            var folderQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
            folderQueue.Enqueue(rootPath);

            bool matchAll = searchPattern == "*.*" || searchPattern == "*";
            int activeWorkers = 0;
            int scannedFilesCount = 0;
            const int concurrency = 2; // 並列度2

            var tasks = new Task[concurrency];
            for (int w = 0; w < concurrency; w++)
            {
                tasks[w] = Task.Run(async () =>
                {
                    var localSubDirs = new List<NativeFindEntry>(64);
                    var localFiles = new List<NativeFindEntry>(128);

                    while (!ct.IsCancellationRequested)
                    {
                        if (!folderQueue.TryDequeue(out var currentPath))
                        {
                            // キューが空でも、別ワーカーが探索中ならサブフォルダが追加される可能性がある
                            if (Volatile.Read(ref activeWorkers) == 0 && folderQueue.IsEmpty)
                            {
                                break;
                            }
                            await Task.Delay(2, ct);
                            continue;
                        }

                        Interlocked.Increment(ref activeWorkers);
                        try
                        {
                            if (coverage != null)
                            {
                                lock (coverage) coverage.TotalFoldersScanned++;
                            }

                            if (!NativeDirectoryEnumerator.TryEnumerateEntries(currentPath, localSubDirs, localFiles, out var error))
                            {
                                if (coverage != null)
                                {
                                    lock (coverage) coverage.AccessDeniedFolders++;
                                }
                                continue;
                            }

                            // サブフォルダーをキューへ追加
                            for (int i = 0; i < localSubDirs.Count; i++)
                            {
                                var sd = localSubDirs[i];
                                if (sd.IsReparsePoint) continue;
                                folderQueue.Enqueue(Path.Combine(currentPath, sd.Name));
                            }

                            // ファイルを結果コレクションへ追加
                            for (int i = 0; i < localFiles.Count; i++)
                            {
                                var f = localFiles[i];
                                if (!matchAll && !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(searchPattern, f.Name, ignoreCase: true))
                                    continue;

                                resultFiles.Add(new FileInfo(Path.Combine(currentPath, f.Name)));
                                int count = Interlocked.Increment(ref scannedFilesCount);
                                if (coverage != null)
                                {
                                    lock (coverage) coverage.TotalFilesFound++;
                                }

                                if (count % 100 == 0)
                                {
                                    onProgress?.Invoke(count);
                                }
                            }
                        }
                        finally
                        {
                            Interlocked.Decrement(ref activeWorkers);
                        }
                    }
                }, ct);
            }

            await Task.WhenAll(tasks);
            onProgress?.Invoke(resultFiles.Count);
            return resultFiles.ToList();
        }
    }
}
