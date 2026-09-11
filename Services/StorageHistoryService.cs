using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AstraSize.Models;
using FolderMorpher.Services;

namespace AstraSize.Services
{
    public class StorageHistoryService
    {
        private readonly string _defaultLocalHistoryFilePath;

        public StorageHistoryService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "FolderMorpher");
            Directory.CreateDirectory(dir);
            _defaultLocalHistoryFilePath = Path.Combine(dir, "history.json");

            // 旧 AstraSize からのデータ移行（存在すればコピー）
            var legacyPath = Path.Combine(appData, "AstraSize", "history.json");
            if (!File.Exists(_defaultLocalHistoryFilePath) && File.Exists(legacyPath))
            {
                try { File.Copy(legacyPath, _defaultLocalHistoryFilePath); } catch { }
            }
        }

        public static string GetPathHash(string targetPath)
        {
            var normalized = targetPath.TrimEnd('\\', '/').ToLowerInvariant();
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
            var hash = sha.ComputeHash(bytes);
            return Convert.ToHexString(hash);
        }

        private List<string> GetSnapshotDirectories(string? subHash = null)
        {
            var dirs = new List<string>();
            var configuredReadDir = AppSettingsService.Instance.GetReadDirectory("Snapshots");
            if (!string.IsNullOrEmpty(configuredReadDir) && Directory.Exists(configuredReadDir))
            {
                dirs.Add(string.IsNullOrEmpty(subHash) ? configuredReadDir : Path.Combine(configuredReadDir, subHash));
            }

            var localBase = AppSettingsService.Instance.GetDefaultLocalBaseDirectory();
            var localDir = Path.Combine(localBase, "Snapshots");
            if (Directory.Exists(localDir))
            {
                var targetLocal = string.IsNullOrEmpty(subHash) ? localDir : Path.Combine(localDir, subHash);
                if (!dirs.Contains(targetLocal, StringComparer.OrdinalIgnoreCase))
                {
                    dirs.Add(targetLocal);
                }
            }
            return dirs;
        }

        public async Task<List<ScanSnapshot>> LoadAllAsync()
        {
            return await Task.Run(async () =>
            {
                var list = new List<ScanSnapshot>();
                var baseDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                var configuredReadDir = AppSettingsService.Instance.GetReadDirectory("Snapshots");
                if (!string.IsNullOrEmpty(configuredReadDir) && Directory.Exists(configuredReadDir))
                {
                    baseDirs.Add(configuredReadDir);
                }

                var localBase = AppSettingsService.Instance.GetDefaultLocalBaseDirectory();
                var localDir = Path.Combine(localBase, "Snapshots");
                if (Directory.Exists(localDir))
                {
                    baseDirs.Add(localDir);
                }

                if (baseDirs.Count == 0) return list;

                foreach (var baseDir in baseDirs)
                {
                    // 1. ルート直下の旧形式 history.json / snapshot_*.json
                    await LoadSnapshotsFromDirectoryAsync(baseDir, list);

                    // 2. パス別ハッシュサブディレクトリ (Snapshots/{hash}/)
                    try
                    {
                        foreach (var subDir in Directory.GetDirectories(baseDir))
                        {
                            await LoadSnapshotsFromDirectoryAsync(subDir, list);
                        }
                    }
                    catch { }
                }

                // 重複排除 (TargetPath + Timestamp) して日時順ソート
                return list
                    .GroupBy(s => $"{s.TargetPath.TrimEnd('\\', '/').ToLowerInvariant()}_{s.Timestamp:yyyyMMddHHmmss}")
                    .Select(g => g.First())
                    .OrderBy(s => s.Timestamp)
                    .ToList();
            });
        }

        private static async Task LoadSnapshotsFromDirectoryAsync(string dir, List<ScanSnapshot> list)
        {
            if (!Directory.Exists(dir)) return;

            var historyPath = Path.Combine(dir, "history.json");
            if (File.Exists(historyPath))
            {
                try
                {
                    var json = await File.ReadAllTextAsync(historyPath);
                    var baseList = JsonSerializer.Deserialize<List<ScanSnapshot>>(json);
                    if (baseList != null) list.AddRange(baseList);
                }
                catch { }
            }

            try
            {
                foreach (var file in Directory.GetFiles(dir, "snapshot_*.json"))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file);
                        var single = JsonSerializer.Deserialize<ScanSnapshot>(json);
                        if (single != null) list.Add(single);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public async Task SaveAllAsync(List<ScanSnapshot> list)
        {
            try
            {
                var writeDir = AppSettingsService.Instance.GetWriteDirectory("Snapshots");
                var writePath = Path.Combine(writeDir, "history.json");
                var dir = Path.GetDirectoryName(writePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(list, options);

                // アトミック書き込み（一時ファイル -> 置換）で共有破損を防止
                var tempFile = writePath + $".tmp_{Guid.NewGuid():N}";
                await File.WriteAllTextAsync(tempFile, json);
                File.Move(tempFile, writePath, overwrite: true);
            }
            catch { }
        }

        public async Task SaveSnapshotAsync(FileItemNode rootNode)
        {
            if (rootNode == null || string.IsNullOrWhiteSpace(rootNode.FullPath)) return;

            var snapshot = new ScanSnapshot
            {
                TargetPath = rootNode.FullPath,
                Timestamp = DateTime.Now,
                TotalBytes = rootNode.Size,
                TotalFiles = rootNode.FileCount,
                SubFolders = rootNode.Children
                    .Where(c => c.IsDirectory)
                    .Select(c => new FolderSnapshot { Name = c.Name, Size = c.Size })
                    .ToList()
            };

            await Task.Run(async () =>
            {
                try
                {
                    var hash = GetPathHash(rootNode.FullPath);

                    // 1. 保存先ディレクトリの決定 (指定の WriteDirectory とローカル両方への書き込み同期)
                    var targetDirs = new List<string>();
                    var configuredWriteDir = AppSettingsService.Instance.GetWriteDirectory("Snapshots");
                    if (!string.IsNullOrEmpty(configuredWriteDir))
                    {
                        targetDirs.Add(Path.Combine(configuredWriteDir, hash));
                    }

                    var localBase = AppSettingsService.Instance.GetDefaultLocalBaseDirectory();
                    var localTargetDir = Path.Combine(localBase, "Snapshots", hash);
                    if (!targetDirs.Contains(localTargetDir, StringComparer.OrdinalIgnoreCase))
                    {
                        targetDirs.Add(localTargetDir);
                    }

                    // 2. 既存の該当パス履歴をすべてマージロード
                    var pathHistory = await GetHistoryForPathAsync(rootNode.FullPath);
                    pathHistory.Add(snapshot);

                    // 同一タイムスタンプ重複排除 ＆ 日時順ソート
                    pathHistory = pathHistory
                        .GroupBy(s => $"{s.TargetPath.TrimEnd('\\', '/').ToLowerInvariant()}_{s.Timestamp:yyyyMMddHHmmss}")
                        .Select(g => g.First())
                        .OrderBy(s => s.Timestamp)
                        .ToList();

                    // フォルダ単位で最大500件まで長期保持（別フォルダによる押し出し完全根絶）
                    if (pathHistory.Count > 500)
                    {
                        pathHistory = pathHistory.Skip(pathHistory.Count - 500).ToList();
                    }

                    var options = new JsonSerializerOptions { WriteIndented = true };
                    var historyJson = JsonSerializer.Serialize(pathHistory, options);

                    var singleFileName = $"snapshot_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.json";
                    var singleJson = JsonSerializer.Serialize(snapshot, options);

                    // 3. 各保存先（共有・ローカル）へアトミック書き込み ＆ 古い個別スナップショットのお掃除
                    foreach (var dir in targetDirs)
                    {
                        try
                        {
                            Directory.CreateDirectory(dir);

                            // 個別ファイル書き出し
                            var singleFilePath = Path.Combine(dir, singleFileName);
                            await File.WriteAllTextAsync(singleFilePath, singleJson);

                            // history.json アトミック更新
                            var historyFile = Path.Combine(dir, "history.json");
                            var tempFile = historyFile + $".tmp_{Guid.NewGuid():N}";
                            await File.WriteAllTextAsync(tempFile, historyJson);
                            File.Move(tempFile, historyFile, overwrite: true);

                            // 個別ファイルの自動ローテーション（最新20件を残して古いものを安全削除）
                            CleanOldSnapshotFiles(dir, keepCount: 20);
                        }
                        catch { }
                    }
                }
                catch { }
            });
        }

        private static void CleanOldSnapshotFiles(string dir, int keepCount)
        {
            try
            {
                var files = Directory.GetFiles(dir, "snapshot_*.json")
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(fi => fi.CreationTimeUtc)
                    .ToList();

                if (files.Count > keepCount)
                {
                    foreach (var oldFile in files.Skip(keepCount))
                    {
                        try { oldFile.Delete(); } catch { }
                    }
                }
            }
            catch { }
        }

        public async Task RecordScanAsync(string targetPath, long totalBytes, int totalFiles, int totalFolders, DateTime? timestamp = null)
        {
            var snapshot = new ScanSnapshot
            {
                TargetPath = targetPath,
                Timestamp = timestamp ?? DateTime.Now,
                TotalBytes = totalBytes,
                TotalFiles = totalFiles
            };

            await Task.Run(async () =>
            {
                try
                {
                    var pathHistory = await GetHistoryForPathAsync(targetPath);
                    pathHistory.Add(snapshot);
                    pathHistory = pathHistory
                        .GroupBy(s => $"{s.TargetPath.TrimEnd('\\', '/').ToLowerInvariant()}_{s.Timestamp:yyyyMMddHHmmss}")
                        .Select(g => g.First())
                        .OrderBy(s => s.Timestamp)
                        .ToList();

                    if (pathHistory.Count > 500) pathHistory = pathHistory.Skip(pathHistory.Count - 500).ToList();

                    var hash = GetPathHash(targetPath);
                    var writeDir = AppSettingsService.Instance.GetWriteDirectory("Snapshots");
                    var targetDir = Path.Combine(writeDir, hash);
                    Directory.CreateDirectory(targetDir);

                    var historyFile = Path.Combine(targetDir, "history.json");
                    var tempFile = historyFile + $".tmp_{Guid.NewGuid():N}";
                    var json = JsonSerializer.Serialize(pathHistory, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(tempFile, json);
                    File.Move(tempFile, historyFile, overwrite: true);
                }
                catch { }
            });
        }

        public async Task<List<ScanSnapshot>> GetHistoryForPathAsync(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return new List<ScanSnapshot>();

            return await Task.Run(async () =>
            {
                var list = new List<ScanSnapshot>();
                var hash = GetPathHash(targetPath);
                var normalizedTarget = targetPath.TrimEnd('\\', '/').ToLowerInvariant();

                // 1. パス別ハッシュディレクトリ (Snapshots/{hash}/) からのピンポイント高速ロード
                var targetDirs = GetSnapshotDirectories(hash);
                foreach (var dir in targetDirs)
                {
                    await LoadSnapshotsFromDirectoryAsync(dir, list);
                }

                // 2. 旧形式（ルート直下の history.json / snapshot_*.json）にも該当データがあればマージ（旧データ救済）
                var rootDirs = GetSnapshotDirectories(null);
                foreach (var rDir in rootDirs)
                {
                    var rootHistory = Path.Combine(rDir, "history.json");
                    if (File.Exists(rootHistory))
                    {
                        try
                        {
                            var json = await File.ReadAllTextAsync(rootHistory);
                            var rootList = JsonSerializer.Deserialize<List<ScanSnapshot>>(json);
                            if (rootList != null)
                            {
                                list.AddRange(rootList.Where(s => string.Equals(s.TargetPath.TrimEnd('\\', '/').ToLowerInvariant(), normalizedTarget, StringComparison.OrdinalIgnoreCase)));
                            }
                        }
                        catch { }
                    }
                }

                // 3. 重複排除 (TargetPath + Timestamp) して降順ソート
                var merged = list
                    .Where(s => string.Equals(s.TargetPath.TrimEnd('\\', '/').ToLowerInvariant(), normalizedTarget, StringComparison.OrdinalIgnoreCase))
                    .GroupBy(s => $"{s.TargetPath.TrimEnd('\\', '/').ToLowerInvariant()}_{s.Timestamp:yyyyMMddHHmmss}")
                    .Select(g => g.First())
                    .OrderByDescending(s => s.Timestamp)
                    .ToList();

                return merged;
            });
        }

        public async Task<(ScanSnapshot? lastScan, long diffBytes, string formattedDiff)> GetLastScanDiffAsync(string targetPath, long currentBytes)
        {
            var history = await GetHistoryForPathAsync(targetPath);
            var prev = history.FirstOrDefault(h => (DateTime.Now - h.Timestamp).TotalSeconds > 3);
            if (prev == null)
            {
                return (null, 0, "初回スキャン");
            }

            long diff = currentBytes - prev.TotalBytes;
            string sign = diff > 0 ? "+" : "";
            string formatted = $"{sign}{FileItemNode.FormatBytes(diff)} {(diff > 0 ? "▲ 増加" : diff < 0 ? "▼ 減少" : "±0")}";

            return (prev, diff, formatted);
        }

        #region Tree Cache & Diff Engine (ファイルサーバー高速0秒キャッシュ ＆ 自動差分検出)

        private static string GetCacheFileName(string targetPath)
        {
            var normalized = targetPath.TrimEnd('\\', '/').ToLowerInvariant();
            using var sha = System.Security.Cryptography.SHA256.Create();
            var bytes = System.Text.Encoding.UTF8.GetBytes(normalized);
            var hash = sha.ComputeHash(bytes);
            return $"{Convert.ToHexString(hash)}.json";
        }

        private string? GetTreeCacheReadFilePath(string targetPath)
        {
            var fileName = GetCacheFileName(targetPath);
            var readDir = AppSettingsService.Instance.GetReadDirectory("TreeCaches");
            string? primaryPath = null;
            if (!string.IsNullOrEmpty(readDir))
            {
                var p = Path.Combine(readDir, fileName);
                if (File.Exists(p)) primaryPath = p;
            }

            var localBase = AppSettingsService.Instance.GetDefaultLocalBaseDirectory();
            var localPath = Path.Combine(localBase, "TreeCaches", fileName);
            bool localExists = File.Exists(localPath);

            // フォールバック抑制チェック
            if (!AppSettingsService.Instance.Current.FallbackToLocalOnReadError && string.IsNullOrEmpty(primaryPath))
            {
                return null;
            }

            // 共有とローカルの両方が存在する場合、新しいタイムスタンプの方を優先ロード
            if (primaryPath != null && localExists)
            {
                try
                {
                    var primaryTime = File.GetLastWriteTimeUtc(primaryPath);
                    var localTime = File.GetLastWriteTimeUtc(localPath);
                    return (localTime > primaryTime) ? localPath : primaryPath;
                }
                catch
                {
                    return primaryPath;
                }
            }

            if (primaryPath != null) return primaryPath;
            if (localExists && AppSettingsService.Instance.Current.FallbackToLocalOnReadError) return localPath;

            return null;
        }

        private string GetTreeCacheWriteFilePath(string targetPath)
        {
            var fileName = GetCacheFileName(targetPath);
            var writeDir = AppSettingsService.Instance.GetWriteDirectory("TreeCaches");
            return Path.Combine(writeDir, fileName);
        }

        public async Task SaveTreeCacheAsync(FileItemNode rootNode)
        {
            if (rootNode == null || string.IsNullOrWhiteSpace(rootNode.FullPath)) return;

            await Task.Run(async () =>
            {
                try
                {
                    var filePath = GetTreeCacheWriteFilePath(rootNode.FullPath);
                    var dir = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                    var (topFiles, extStats) = (rootNode.CachedTopFiles != null && rootNode.CachedExtensionStats != null)
                        ? (rootNode.CachedTopFiles, rootNode.CachedExtensionStats)
                        : DiskScanService.GetInsightsForNode(rootNode);

                    var cacheRoot = new TreeCacheRoot
                    {
                        TargetPath = rootNode.FullPath,
                        Timestamp = DateTime.Now,
                        Root = ToCacheNode(rootNode),
                        TopFiles = topFiles,
                        ExtensionStats = extStats
                    };

                    var options = new JsonSerializerOptions { WriteIndented = false };
                    var json = JsonSerializer.Serialize(cacheRoot, options);

                    // アトミック書き込みで共有破損を防止
                    var tempFile = filePath + $".tmp_{Guid.NewGuid():N}";
                    await File.WriteAllTextAsync(tempFile, json);
                    File.Move(tempFile, filePath, overwrite: true);
                }
                catch
                {
                    // バックグラウンドキャッシュ保存エラーはUIを阻害しないよう安全に無視
                }
            });
        }

        public async Task<FileItemNode?> LoadTreeCacheAsync(string targetPath)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return null;

            return await Task.Run(async () =>
            {
                try
                {
                    var filePath = GetTreeCacheReadFilePath(targetPath);
                    if (filePath == null || !File.Exists(filePath)) return null;

                    var json = await File.ReadAllTextAsync(filePath);
                    var cacheRoot = JsonSerializer.Deserialize<TreeCacheRoot>(json);
                    if (cacheRoot?.Root == null) return null;

                    var node = FromCacheNode(cacheRoot.Root, null, 0);
                    node.CachedTopFiles = cacheRoot.TopFiles;
                    node.CachedExtensionStats = cacheRoot.ExtensionStats;
                    DiskScanService.CalculatePercentages(node, node.Size > 0 ? node.Size : 1);
                    return node;
                }
                catch
                {
                    return null;
                }
            });
        }

        public void ApplyTreeDiff(FileItemNode current, FileItemNode cached)
        {
            if (current == null || cached == null) return;

            // キャッシュノードのパスとサイズをマップ化
            var cacheMap = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            BuildCachePathMap(cached, cacheMap);

            // 現在のツリーに差分を再帰適用
            ApplyDiffRecursive(current, cacheMap);
        }

        private void BuildCachePathMap(FileItemNode node, Dictionary<string, long> map)
        {
            if (!string.IsNullOrEmpty(node.FullPath))
            {
                map[node.FullPath] = node.Size;
            }
            foreach (var child in node.Children)
            {
                BuildCachePathMap(child, map);
            }
        }

        private void ApplyDiffRecursive(FileItemNode node, Dictionary<string, long> cacheMap)
        {
            if (cacheMap.TryGetValue(node.FullPath, out var prevSize))
            {
                node.DiffBytes = node.Size - prevSize;
            }
            else
            {
                // 前回存在しなかった新規ファイル/フォルダ
                node.DiffBytes = node.Size;
            }

            foreach (var child in node.Children)
            {
                ApplyDiffRecursive(child, cacheMap);
            }
        }

        private static TreeCacheNode ToCacheNode(FileItemNode node)
        {
            var c = new TreeCacheNode
            {
                Name = node.Name,
                FullPath = node.FullPath,
                Size = node.Size,
                FileCount = node.FileCount,
                FolderCount = node.FolderCount,
                IsDirectory = node.IsDirectory,
                IsExpanded = node.IsExpanded,
                LastModified = node.LastModified
            };

            foreach (var child in node.Children)
            {
                c.Children.Add(ToCacheNode(child));
            }
            return c;
        }

        private static FileItemNode FromCacheNode(TreeCacheNode c, FileItemNode? parent, int level)
        {
            var node = new FileItemNode
            {
                Name = c.Name,
                FullPath = c.FullPath,
                Size = c.Size,
                FileCount = c.FileCount,
                FolderCount = c.FolderCount,
                IsDirectory = c.IsDirectory,
                IsExpanded = c.IsExpanded,
                LastModified = c.LastModified,
                Level = level,
                Parent = parent
            };

            foreach (var childC in c.Children)
            {
                var childNode = FromCacheNode(childC, node, level + 1);
                node.Children.Add(childNode);
            }

            return node;
        }

        #endregion
    }

    public class TreeCacheNode
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public int FileCount { get; set; }
        public int FolderCount { get; set; }
        public bool IsDirectory { get; set; }
        public bool IsExpanded { get; set; }
        public DateTime? LastModified { get; set; }
        public List<TreeCacheNode> Children { get; set; } = new();
    }

    public class TreeCacheRoot
    {
        public string TargetPath { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public TreeCacheNode Root { get; set; } = new();
        public List<LargestFileInfo> TopFiles { get; set; } = new();
        public List<ExtensionStat> ExtensionStats { get; set; } = new();
    }
}
