using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using AstraSize.Models;

namespace AstraSize.Services
{
    public class StorageHistoryService
    {
        private readonly string _historyFilePath;

        public StorageHistoryService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var dir = Path.Combine(appData, "FolderMorpher");
            Directory.CreateDirectory(dir);
            _historyFilePath = Path.Combine(dir, "history.json");

            // 旧 AstraSize からのデータ移行（存在すればコピー）
            var legacyPath = Path.Combine(appData, "AstraSize", "history.json");
            if (!File.Exists(_historyFilePath) && File.Exists(legacyPath))
            {
                try { File.Copy(legacyPath, _historyFilePath); } catch { }
            }
        }

        public async Task<List<ScanSnapshot>> LoadAllAsync()
        {
            if (!File.Exists(_historyFilePath)) return new List<ScanSnapshot>();
            try
            {
                var json = await File.ReadAllTextAsync(_historyFilePath);
                var list = JsonSerializer.Deserialize<List<ScanSnapshot>>(json);
                return list ?? new List<ScanSnapshot>();
            }
            catch
            {
                return new List<ScanSnapshot>();
            }
        }

        public async Task SaveAllAsync(List<ScanSnapshot> list)
        {
            try
            {
                var options = new JsonSerializerOptions { WriteIndented = true };
                var json = JsonSerializer.Serialize(list, options);
                await File.WriteAllTextAsync(_historyFilePath, json);
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

            var all = await LoadAllAsync();
            all.Add(snapshot);

            // Keep max 50 snapshots
            if (all.Count > 50)
            {
                all = all.Skip(all.Count - 50).ToList();
            }

            await SaveAllAsync(all);
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
    }
}
