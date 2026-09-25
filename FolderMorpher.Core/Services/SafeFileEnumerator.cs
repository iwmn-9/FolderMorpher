using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// ディスク/SMB探索で一括取得されたメタデータをそのまま保持する軽量ファイルモデル。
    /// FileInfo と異なり、追加の stat (属性問い合わせ) I/O を一切発生させない。
    /// </summary>
    public sealed class ScannedFileEntry
    {
        public string FullPath { get; }
        public string Name { get; }
        public string DirectoryPath { get; }
        public long Length { get; }
        public DateTime CreationTime { get; }
        public DateTime LastWriteTime { get; }
        public DateTime LastAccessTime { get; }
        public FileAttributes Attributes { get; }

        public ScannedFileEntry(
            string fullPath,
            string name,
            string directoryPath,
            long length,
            DateTime creationTime,
            DateTime lastWriteTime,
            DateTime lastAccessTime,
            FileAttributes attributes)
        {
            FullPath = fullPath;
            Name = name;
            DirectoryPath = directoryPath;
            Length = length;
            CreationTime = creationTime;
            LastWriteTime = lastWriteTime;
            LastAccessTime = lastAccessTime;
            Attributes = attributes;
        }

        public FileInfo ToFileInfo() => new FileInfo(FullPath);
    }

    /// <summary>
    /// アクセス拒否(UnauthorizedAccessException)やパス長制限で絶対に即死しない安全な反復ファイル列挙エンジン。
    /// Win32 FindFirstFileExW (FindExInfoBasic + LargeFetch) による巨大バッファ・8.3スキップ・並列度2対応。
    /// </summary>
    public static class SafeFileEnumerator
    {
        private static bool ShouldExcludeDirectory(string dirName, IReadOnlyList<string>? excludePatterns)
        {
            if (excludePatterns == null || excludePatterns.Count == 0) return false;
            for (int i = 0; i < excludePatterns.Count; i++)
            {
                var pattern = excludePatterns[i];
                if (!string.IsNullOrWhiteSpace(pattern) && dirName.Contains(pattern.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public static IEnumerable<FileInfo> EnumerateFilesSafe(
            string rootPath,
            string searchPattern = "*.*",
            ScanCoverage? coverage = null,
            CancellationToken ct = default,
            IReadOnlyList<string>? excludeFolderPatterns = null)
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

                // サブディレクトリの安全プッシュ (Junction / ReparsePoint 除外 & フォルダ名パターン除外)
                for (int i = 0; i < subDirs.Count; i++)
                {
                    var sd = subDirs[i];
                    if (sd.IsReparsePoint) continue;
                    if (ShouldExcludeDirectory(sd.Name, excludeFolderPatterns)) continue;
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
        /// 適応型並列制御（Adaptive Concurrency: 初期値2・下限2・上限4・即時崖落ち降下 - ADR 80）による高効率な並行ディレクトリスキャン（FileInfo 返却・互換用）。
        /// </summary>
        public static async Task<List<FileInfo>> EnumerateFilesSafeParallelAsync(
            string rootPath,
            string searchPattern = "*.*",
            ScanCoverage? coverage = null,
            Action<int>? onProgress = null,
            CancellationToken ct = default,
            IReadOnlyList<string>? excludeFolderPatterns = null)
        {
            var entries = await EnumerateFileEntriesParallelAsync(rootPath, searchPattern, coverage, onProgress, ct, excludeFolderPatterns: excludeFolderPatterns);
            return entries.Select(e => e.ToFileInfo()).ToList();
        }

        /// <summary>
        /// 適応型並列制御（Adaptive Concurrency: 初期値2・下限2・上限4・即時崖落ち降下 - ADR 80）による高効率な並行ディレクトリスキャン（ScannedFileEntry 返却・高速用）。
        /// 一括取得したファイルサイズ・日時属性をそのまま保持し、後続の個別属性再問い合わせ（再stat）を完全根絶する。
        /// onEntryFound で逐次処理する呼び出し元は collectResults: false を指定できる。
        /// その場合、結果一覧を保持せず空のリストを返す。コールバックは複数ワーカーから呼ばれる。
        /// </summary>
        public static async Task<List<ScannedFileEntry>> EnumerateFileEntriesParallelAsync(
            string rootPath,
            string searchPattern = "*.*",
            ScanCoverage? coverage = null,
            Action<int>? onProgress = null,
            CancellationToken ct = default,
            IReadOnlyList<string>? excludeFolderPatterns = null,
            bool includeDirectories = false,
            Action<ScannedFileEntry>? onEntryFound = null,
            TreeCachePruningIndex? pruningIndex = null,
            bool collectResults = true)
        {
            var resultFiles = collectResults ? new System.Collections.Concurrent.ConcurrentBag<ScannedFileEntry>() : null;
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) return new List<ScannedFileEntry>();

            var folderQueue = new System.Collections.Concurrent.ConcurrentQueue<string>();
            bool matchAll = searchPattern == "*.*" || searchPattern == "*";
            int scannedFilesCount = 0;
            var governor = SharedIoGovernor.GetGovernor(rootPath);
            var controller = governor.EnumerationController;
            int maxWorkers = AdaptiveConcurrencyController.MaxConcurrency; // 4ワーカーまで待機可能

            // ★ Sol指摘: Worker起動レースの解消
            // root フォルダーを先に一度同期列挙し、サブフォルダーをキューへ投入してからワーカーを起動する。
            var rootSubDirs = new List<NativeFindEntry>(64);
            var rootFiles = new List<NativeFindEntry>(128);

            if (coverage != null)
            {
                lock (coverage) coverage.TotalFoldersScanned++;
            }

            bool rootOk;
            EnumerationFailureKind rootFailureKind = EnumerationFailureKind.None;
            using (var rootSlot = await governor.AcquireSlotAsync(ct))
            using (var rootLease = await controller.AcquireAsync(ct))
            {
                var rootSw = Stopwatch.StartNew();
                rootOk = NativeDirectoryEnumerator.TryEnumerateEntries(rootPath, rootSubDirs, rootFiles, out var rootError, out rootFailureKind);
                rootSw.Stop();
                rootLease.Report(rootSw.Elapsed.TotalMilliseconds, rootFailureKind);
            }

            if (rootOk)
            {
                for (int i = 0; i < rootFiles.Count; i++)
                {
                    var f = rootFiles[i];
                    if (!matchAll && !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(searchPattern, f.Name, ignoreCase: true))
                        continue;

                    string fullPath = Path.Combine(rootPath, f.Name);
                    var entry = new ScannedFileEntry(
                        fullPath,
                        f.Name,
                        rootPath,
                        f.Size,
                        f.CreationTimeUtc.ToLocalTime(),
                        f.LastWriteTimeUtc.ToLocalTime(),
                        f.LastAccessTimeUtc.ToLocalTime(),
                        f.Attributes);
                    resultFiles?.Add(entry);
                    onEntryFound?.Invoke(entry);

                    int count = Interlocked.Increment(ref scannedFilesCount);
                    if (coverage != null)
                    {
                        lock (coverage) coverage.TotalFilesFound++;
                    }
                }

                for (int i = 0; i < rootSubDirs.Count; i++)
                {
                    var sd = rootSubDirs[i];
                    if (!sd.IsReparsePoint && !ShouldExcludeDirectory(sd.Name, excludeFolderPatterns))
                    {
                        string dirPath = Path.Combine(rootPath, sd.Name);

                        // ★ ADR 93: TreeCache Pruning（ルート直下の枝刈り判定）
                        if (pruningIndex != null && pruningIndex.TryGetPrunedEntries(dirPath, sd.LastWriteTimeUtc, includeDirectories, out var prunedEntries))
                        {
                            if (prunedEntries != null)
                            {
                                for (int p = 0; p < prunedEntries.Count; p++)
                                {
                                    var pEntry = prunedEntries[p];
                                    if (!pEntry.Attributes.HasFlag(FileAttributes.Directory) &&
                                        !matchAll && !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(searchPattern, pEntry.Name, ignoreCase: true))
                                    {
                                        continue;
                                    }
                                    resultFiles?.Add(pEntry);
                                    onEntryFound?.Invoke(pEntry);
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
                            continue; // 枝刈り成功時はキューに投入せず配下I/Oスキップ
                        }

                        folderQueue.Enqueue(dirPath);
                        if (includeDirectories)
                        {
                            var dirEntry = new ScannedFileEntry(
                                dirPath,
                                sd.Name,
                                rootPath,
                                0,
                                sd.CreationTimeUtc.ToLocalTime(),
                                sd.LastWriteTimeUtc.ToLocalTime(),
                                sd.LastAccessTimeUtc.ToLocalTime(),
                                sd.Attributes | FileAttributes.Directory);
                            resultFiles?.Add(dirEntry);
                            onEntryFound?.Invoke(dirEntry);
                        }
                    }
                }
            }
            else
            {
                if (coverage != null)
                {
                    lock (coverage) coverage.AccessDeniedFolders++;
                }
                return resultFiles?.ToList() ?? new List<ScannedFileEntry>();
            }

            // サブフォルダーが存在しない場合は即時返却
            if (folderQueue.IsEmpty)
            {
                onProgress?.Invoke(resultFiles?.Count ?? scannedFilesCount);
                return resultFiles?.ToList() ?? new List<ScannedFileEntry>();
            }

            int activeWorkers = 0;
            var tasks = new Task[maxWorkers];

            for (int w = 0; w < maxWorkers; w++)
            {
                tasks[w] = Task.Run(async () =>
                {
                    var localSubDirs = new List<NativeFindEntry>(64);
                    var localFiles = new List<NativeFindEntry>(128);
                    var localStack = new Stack<string>(32); // ★ 親子局所性 LIFO スタック

                    while (!ct.IsCancellationRequested)
                    {
                        string? currentPath;
                        if (localStack.Count > 0)
                        {
                            currentPath = localStack.Pop(); // ★ 親子局所性: 直下のサブフォルダーを優先深掘りしてMFTキャッシュを直撃
                        }
                        else if (!folderQueue.TryDequeue(out currentPath))
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

                            bool ok;
                            EnumerationFailureKind failureKind = EnumerationFailureKind.None;
                            using (var slot = await governor.AcquireSlotAsync(ct))
                            using (var lease = await controller.AcquireAsync(ct))
                            {
                                var sw = Stopwatch.StartNew();
                                ok = NativeDirectoryEnumerator.TryEnumerateEntries(currentPath, localSubDirs, localFiles, out var error, out failureKind);
                                sw.Stop();
                                lease.Report(sw.Elapsed.TotalMilliseconds, failureKind);
                            }

                            if (!ok)
                            {
                                if (coverage != null)
                                {
                                    lock (coverage) coverage.AccessDeniedFolders++;
                                }
                                continue;
                            }

                            // サブフォルダーの処理（枝刈り ＆ 親子局所性キューイング）
                            bool firstSubDir = true;
                            for (int i = 0; i < localSubDirs.Count; i++)
                            {
                                var sd = localSubDirs[i];
                                if (sd.IsReparsePoint) continue;
                                if (ShouldExcludeDirectory(sd.Name, excludeFolderPatterns)) continue;
                                string dirPath = Path.Combine(currentPath, sd.Name);

                                // ★ ADR 93: TreeCache Pruning（ワーカーループ内の枝刈り判定）
                                if (pruningIndex != null && pruningIndex.TryGetPrunedEntries(dirPath, sd.LastWriteTimeUtc, includeDirectories, out var prunedEntries))
                                {
                                    if (prunedEntries != null)
                                    {
                                        for (int p = 0; p < prunedEntries.Count; p++)
                                        {
                                            var pEntry = prunedEntries[p];
                                            if (!pEntry.Attributes.HasFlag(FileAttributes.Directory) &&
                                                !matchAll && !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(searchPattern, pEntry.Name, ignoreCase: true))
                                            {
                                                continue;
                                            }
                                            resultFiles?.Add(pEntry);
                                            onEntryFound?.Invoke(pEntry);
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
                                    continue; // 枝刈り成功時はキューに投入せず配下I/Oスキップ
                                }

                                if (firstSubDir)
                                {
                                    localStack.Push(dirPath); // 最初のサブフォルダーは自分のスタックへ積んで直下を掘る
                                    firstSubDir = false;
                                }
                                else
                                {
                                    folderQueue.Enqueue(dirPath); // 2つ目以降はグローバルキューへ積んで他ワーカーへ分配
                                }

                                if (includeDirectories)
                                {
                                    var dirEntry = new ScannedFileEntry(
                                        dirPath,
                                        sd.Name,
                                        currentPath,
                                        0,
                                        sd.CreationTimeUtc.ToLocalTime(),
                                        sd.LastWriteTimeUtc.ToLocalTime(),
                                        sd.LastAccessTimeUtc.ToLocalTime(),
                                        sd.Attributes | FileAttributes.Directory);
                                    resultFiles?.Add(dirEntry);
                                    onEntryFound?.Invoke(dirEntry);
                                }
                            }

                            // ファイルを結果コレクションへ追加（stat 再問い合わせゼロ）
                            for (int i = 0; i < localFiles.Count; i++)
                            {
                                var f = localFiles[i];
                                if (!matchAll && !System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(searchPattern, f.Name, ignoreCase: true))
                                    continue;

                                string fullPath = Path.Combine(currentPath, f.Name);
                                var entry = new ScannedFileEntry(
                                    fullPath,
                                    f.Name,
                                    currentPath,
                                    f.Size,
                                    f.CreationTimeUtc.ToLocalTime(),
                                    f.LastWriteTimeUtc.ToLocalTime(),
                                    f.LastAccessTimeUtc.ToLocalTime(),
                                    f.Attributes);
                                resultFiles?.Add(entry);
                                onEntryFound?.Invoke(entry);

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
            onProgress?.Invoke(resultFiles?.Count ?? scannedFilesCount);
            return resultFiles?.ToList() ?? new List<ScannedFileEntry>();
        }
    }
}
