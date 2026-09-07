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

        private string GetSnapshotReadFilePath()
        {
            var readDir = AppSettingsService.Instance.GetReadDirectory("Snapshots");
            if (!string.IsNullOrEmpty(readDir))
            {
                var path = Path.Combine(readDir, "history.json");
                if (File.Exists(path)) return path;
            }

            if (AppSettingsService.Instance.Current.FallbackToLocalOnReadError)
            {
                return _defaultLocalHistoryFilePath;
            }

            return string.Empty;
        }

        private string GetSnapshotWriteFilePath()
        {
            var writeDir = AppSettingsService.Instance.GetWriteDirectory("Snapshots");
            return Path.Combine(writeDir, "history.json");
        }

        public async Task<List<ScanSnapshot>> LoadAllAsync()
        {
            return await Task.Run(async () =>
            {
                var list = new List<ScanSnapshot>();
                var readDir = AppSettingsService.Instance.GetReadDirectory("Snapshots");
                if (string.IsNullOrEmpty(readDir) || !Directory.Exists(readDir))
                {
                    if (AppSettingsService.Instance.Current.FallbackToLocalOnReadError)
                    {
                        var localBase = AppSettingsService.Instance.GetDefaultLocalBaseDirectory();
                        readDir = Path.Combine(localBase, "Snapshots");
                    }
                    else
                    {
                        return list;
                    }
                }

                var historyPath = Path.Combine(readDir, "history.json");
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

                // 個別スナップショットファイル (snapshot_*.json) も読み込んで合算（マルチクライアント対応）
                try
                {
                    if (Directory.Exists(readDir))
                    {
                        foreach (var file in Directory.GetFiles(readDir, "snapshot_*.json"))
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
                }
                catch { }

                // 重複排除 (TargetPath + Timestamp) して日時順ソート
                return list
                    .GroupBy(s => $"{s.TargetPath}_{s.Timestamp:yyyyMMddHHmmss}")
                    .Select(g => g.First())
                    .OrderBy(s => s.Timestamp)
                    .ToList();
            });
        }

        public async Task SaveAllAsync(List<ScanSnapshot> list)
        {
            try
            {
                var writePath = GetSnapshotWriteFilePath();
                var dir = Path.GetDirectoryName(writePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(list, options);

                // アトミック書き込み（一時ファイル -> 置換）で共有破損を防止
                var tempFile = writePath + $".tmp_{Guid.NewGuid():N}";
                await File.WriteAllTextAsync(tempFile, json);
                File.Move(tempFile, writePath, overwrite: true);
            }
            catch
            {
                // Ignore file write lock errors
            }
        }

        public async Task SaveSnapshotAsync(FileItemNode rootNode)
        {
            if (rootNode == null) return;

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
                    var writeDir = AppSettingsService.Instance.GetWriteDirectory("Snapshots");
                    if (!Directory.Exists(writeDir)) Directory.CreateDirectory(writeDir);

                    // 1スキャン1ファイル形式で安全に個別保存（ファイル共有競合皆無）
                    var singleFileName = $"snapshot_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}.json";
                    var singleFilePath = Path.Combine(writeDir, singleFileName);
                    var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
                    await File.WriteAllTextAsync(singleFilePath, json);

                    // history.json もベストエフォートで統合更新
                    var all = await LoadAllAsync();
                    all.Add(snapshot);
                    if (all.Count > 100) all = all.Skip(all.Count - 100).ToList();
                    await SaveAllAsync(all);
                }
                catch { }
            });
        }

        public async Task RecordScanAsync(string targetPath, long totalBytes, int totalFiles, int totalFolders)
        {
            var snapshot = new ScanSnapshot
            {
                TargetPath = targetPath,
                Timestamp = DateTime.Now,
                TotalBytes = totalBytes,
                TotalFiles = totalFiles
            };

            var all = await LoadAllAsync();
            all.Add(snapshot);

            if (all.Count > 50)
            {
                all = all.Skip(all.Count - 50).ToList();
            }

            await SaveAllAsync(all);
        }

        public async Task<List<ScanSnapshot>> GetHistoryForPathAsync(string targetPath)
        {
            var all = await LoadAllAsync();
            return all
                .Where(s => string.Equals(s.TargetPath.TrimEnd('\\'), targetPath.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s.Timestamp)
                .ToList();
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

                    var cacheRoot = new TreeCacheRoot
                    {
                        TargetPath = rootNode.FullPath,
                        Timestamp = DateTime.Now,
                        Root = ToCacheNode(rootNode)
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

                    return FromCacheNode(cacheRoot.Root, null, 0);
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
    }
}
