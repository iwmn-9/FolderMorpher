using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FolderMorpher.Models;

namespace FolderMorpher.Services
{
    /// <summary>
    /// 整理候補から「消さない」と判断されたファイルの除外（保留）記憶サービス。
    /// ファイルに更新・変更がない限り、次回以降の整理候補から自動除外します。
    /// </summary>
    public class AuditIgnoreService
    {
        private static readonly Lazy<AuditIgnoreService> _lazy = new(() => new AuditIgnoreService());
        public static AuditIgnoreService Instance => _lazy.Value;

        private readonly string _ignoreFilePath;
        private readonly ConcurrentDictionary<string, AuditIgnoreItem> _ignoreMap = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _lock = new();

        public AuditIgnoreService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var folder = Path.Combine(appData, "FolderMorpher");
            Directory.CreateDirectory(folder);
            _ignoreFilePath = Path.Combine(folder, "audit_ignore_list.json");

            Load();
        }

        /// <summary>
        /// 指定されたファイルが除外リストに登録されており、かつ変更されていない（サイズ・更新日時が一致）か判定します。
        /// </summary>
        public bool IsIgnored(string fullPath, long fileSizeBytes, DateTime lastWriteTime)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;

            if (_ignoreMap.TryGetValue(fullPath, out var item))
            {
                // ファイルサイズと更新日時のUtcTicksが一致していれば「変更なし」と判定し除外
                long ticks = lastWriteTime.ToUniversalTime().Ticks;
                if (item.FileSizeBytes == fileSizeBytes && item.LastWriteTimeUtcTicks == ticks)
                {
                    return true;
                }

                // 変更があった場合は除外リストから自動解除
                _ignoreMap.TryRemove(fullPath, out _);
                Save();
            }

            return false;
        }

        /// <summary>
        /// ファイルを整理除外（保持）として登録します。
        /// </summary>
        public void AddIgnore(AuditItem item)
        {
            if (item == null || string.IsNullOrEmpty(item.FullPath)) return;

            var entry = new AuditIgnoreItem
            {
                FullPath = item.FullPath,
                FileSizeBytes = item.Size,
                LastWriteTimeUtcTicks = item.LastWriteTime.ToUniversalTime().Ticks,
                IgnoredAt = DateTime.Now
            };

            _ignoreMap[item.FullPath] = entry;
            Save();
        }

        /// <summary>
        /// 除外登録を解除します。
        /// </summary>
        public bool RemoveIgnore(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath)) return false;
            bool removed = _ignoreMap.TryRemove(fullPath, out _);
            if (removed) Save();
            return removed;
        }

        /// <summary>
        /// 登録されている除外アイテムの総数を取得します。
        /// </summary>
        public int GetIgnoredCount() => _ignoreMap.Count;

        /// <summary>
        /// すべての除外登録をクリアします。
        /// </summary>
        public void ClearAll()
        {
            _ignoreMap.Clear();
            Save();
        }

        public IReadOnlyList<AuditIgnoreItem> GetAllItems()
        {
            return _ignoreMap.Values.ToList();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_ignoreFilePath)) return;
                var json = File.ReadAllText(_ignoreFilePath);
                var items = JsonSerializer.Deserialize<List<AuditIgnoreItem>>(json);
                if (items != null)
                {
                    _ignoreMap.Clear();
                    foreach (var item in items)
                    {
                        if (!string.IsNullOrEmpty(item.FullPath))
                        {
                            _ignoreMap[item.FullPath] = item;
                        }
                    }
                }
            }
            catch { }
        }

        private void Save()
        {
            lock (_lock)
            {
                try
                {
                    var items = _ignoreMap.Values.ToList();
                    var json = JsonSerializer.Serialize(items, new JsonSerializerOptions { WriteIndented = true });
                    File.WriteAllText(_ignoreFilePath, json);
                }
                catch { }
            }
        }
    }
}
