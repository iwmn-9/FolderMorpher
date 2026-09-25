using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FolderMorpher.Services
{
    public enum FileChangeType
    {
        Upsert,
        Delete
    }

    /// <summary>
    /// FileSystemWatcher を用いてインデックス対象フォルダーのファイル変更をリアルタイム検知し、
    /// デバウンス集約して ContentIndexService を即時最新化するサービス。
    /// </summary>
    public class ContentIndexWatcherService : IDisposable
    {
        private readonly ContentIndexService _indexService;
        private readonly ConcurrentDictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, (FileChangeType ChangeType, DateTime Timestamp)> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
        private readonly Timer _debounceTimer;
        private readonly SemaphoreSlim _processLock = new(1, 1);
        private bool _disposed;

        /// <summary>
        /// 差分更新完了時に発火するイベント（UIの自動リフレッシュ等に利用可能）
        /// </summary>
        public event EventHandler<IReadOnlyList<string>>? ChangesApplied;

        public ContentIndexWatcherService(ContentIndexService indexService)
        {
            _indexService = indexService ?? throw new ArgumentNullException(nameof(indexService));
            _indexService.ScanCompleted += OnScanCompleted;
            // 300ms 間隔でキュー内の変更を集約バッチ処理
            _debounceTimer = new Timer(OnDebounceTick, null, Timeout.Infinite, Timeout.Infinite);
        }

        private void OnScanCompleted(string rootPath)
        {
            // フル走査完了時: 保留されていた変更を即座に Flush して最新状態を確定
            if (!_disposed && !_pendingChanges.IsEmpty)
            {
                _debounceTimer.Change(50, Timeout.Infinite);
            }
        }

        /// <summary>
        /// 指定フォルダーのリアルタイム監視を開始（既に監視中ならスキップ）
        /// </summary>
        public bool StartWatching(string folderPath)
        {
            if (_disposed || string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
            {
                return false;
            }

            string normPath = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

            return _watchers.GetOrAdd(normPath, path =>
            {
                try
                {
                    var watcher = new FileSystemWatcher(path)
                    {
                        IncludeSubdirectories = true,
                        NotifyFilter = NotifyFilters.FileName |
                                       NotifyFilters.DirectoryName |
                                       NotifyFilters.LastWrite |
                                       NotifyFilters.Size,
                        InternalBufferSize = 64 * 1024 // 64KB
                    };

                    watcher.Created += (s, e) => EnqueueChange(e.FullPath, FileChangeType.Upsert);
                    watcher.Changed += (s, e) => EnqueueChange(e.FullPath, FileChangeType.Upsert);
                    watcher.Deleted += (s, e) => EnqueueChange(e.FullPath, FileChangeType.Delete);
                    watcher.Renamed += (s, e) =>
                    {
                        EnqueueChange(e.OldFullPath, FileChangeType.Delete);
                        EnqueueChange(e.FullPath, FileChangeType.Upsert);
                    };
                    watcher.Error += (s, e) =>
                    {
                        System.Diagnostics.Debug.WriteLine($"[Watcher] Error on {path}: {e.GetException()?.Message}");
                        // バッファOverflow等のエラー時はDirtyマークを付け、次回検索時に15分クールダウンをバイパスして強制フル走査させる
                        _indexService.MarkRootDirty(path);
                    };

                    watcher.EnableRaisingEvents = true;
                    return watcher;
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Watcher] Failed to start watching {path}: {ex.Message}");
                    return null!;
                }
            }) != null;
        }

        /// <summary>
        /// 指定フォルダーの監視を停止
        /// </summary>
        public void StopWatching(string folderPath)
        {
            if (string.IsNullOrWhiteSpace(folderPath)) return;
            string normPath = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

            if (_watchers.TryRemove(normPath, out var watcher))
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                catch { }
            }
        }

        /// <summary>
        /// 全ての監視を停止
        /// </summary>
        public void StopAll()
        {
            foreach (var kvp in _watchers)
            {
                try
                {
                    kvp.Value.EnableRaisingEvents = false;
                    kvp.Value.Dispose();
                }
                catch { }
            }
            _watchers.Clear();
        }

        public bool HasPendingChanges => !_pendingChanges.IsEmpty;

        public void EnqueueChange(string fullPath, FileChangeType changeType)
        {
            if (_disposed || string.IsNullOrWhiteSpace(fullPath)) return;

            // 自前DB関連ファイルはWatcherからも完全除外
            if (_indexService.IsDatabaseFile(fullPath)) return;

            string ext = Path.GetExtension(fullPath);
            if (string.Equals(ext, ".tmp", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".db", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".db-wal", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(ext, ".db-shm", StringComparison.OrdinalIgnoreCase) ||
                fullPath.Contains("~$"))
            {
                return;
            }

            _pendingChanges[fullPath] = (changeType, DateTime.UtcNow);
            _debounceTimer.Change(300, Timeout.Infinite);
        }

        private void OnDebounceTick(object? state)
        {
            _ = ProcessPendingChangesAsync();
        }

        /// <summary>
        /// キュー内の変更をバッチで ContentIndexService に反映
        /// </summary>
        public async Task ProcessPendingChangesAsync()
        {
            if (_disposed) return;

            await _processLock.WaitAsync();
            try
            {
                if (_pendingChanges.IsEmpty) return;

                var snapshot = new Dictionary<string, FileChangeType>(StringComparer.OrdinalIgnoreCase);
                foreach (var key in _pendingChanges.Keys)
                {
                    if (_pendingChanges.TryRemove(key, out var val))
                    {
                        snapshot[key] = val.ChangeType;
                    }
                }

                if (snapshot.Count == 0) return;

                var toDelete = new List<string>();
                var toUpsert = new List<string>();
                var deferred = new List<KeyValuePair<string, FileChangeType>>();

                foreach (var kvp in snapshot)
                {
                    // フル走査中のルート下の変更は保留（スキャン完了時のScanCompletedでflushされ最新Generationで安全適用）
                    if (_indexService.IsScanningRoot(kvp.Key))
                    {
                        deferred.Add(kvp);
                        continue;
                    }

                    bool exists = File.Exists(kvp.Key) || Directory.Exists(kvp.Key);
                    if (kvp.Value == FileChangeType.Delete || !exists)
                    {
                        toDelete.Add(kvp.Key);
                    }
                    else
                    {
                        toUpsert.Add(kvp.Key);
                    }
                }

                // 走査中のため保留したアイテムをキューに復帰
                foreach (var def in deferred)
                {
                    _pendingChanges[def.Key] = (def.Value, DateTime.UtcNow);
                }

                if (toDelete.Count > 0)
                {
                    await _indexService.PurgeFilesAsync(toDelete);
                }

                if (toUpsert.Count > 0)
                {
                    foreach (var path in toUpsert)
                    {
                        await _indexService.UpsertSingleFileAsync(path);
                        if (Directory.Exists(path))
                        {
                            _indexService.MarkRootDirty(path);
                        }
                    }
                }

                var appliedPaths = new List<string>(toDelete.Concat(toUpsert));
                if (appliedPaths.Count > 0)
                {
                    ChangesApplied?.Invoke(this, appliedPaths);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Watcher] Batch process error: {ex.Message}");
            }
            finally
            {
                _processLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _debounceTimer.Dispose();
            StopAll();
            _processLock.Dispose();
        }
    }
}