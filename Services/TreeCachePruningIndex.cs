using System;
using System.Collections.Generic;
using System.IO;
using AstraSize.Models;

namespace FolderMorpher.Services
{
    /// <summary>
    /// TreeCache のフォルダー更新日時（LastWriteTime）を活用し、
    /// 変更のないサブツリーのネットワークI/Oを丸ごと枝刈り（Pruning）する高速化インデックス。
    /// （ADR 93: Differential Pruning Traversal）
    /// </summary>
    public sealed class TreeCachePruningIndex
    {
        private readonly Dictionary<string, FileItemNode> _foldersByPath = new(StringComparer.OrdinalIgnoreCase);

        public int CachedFoldersCount => _foldersByPath.Count;

        public TreeCachePruningIndex(FileItemNode rootNode)
        {
            IndexRecursive(rootNode);
        }

        private void IndexRecursive(FileItemNode folder)
        {
            if (string.IsNullOrEmpty(folder.FullPath)) return;
            string key = PathCanonicalizer.Normalize(folder.FullPath);
            _foldersByPath[key] = folder;

            if (folder.Children != null)
            {
                for (int i = 0; i < folder.Children.Count; i++)
                {
                    var child = folder.Children[i];
                    if (child.IsDirectory)
                    {
                        IndexRecursive(child);
                    }
                }
            }
        }

        /// <summary>
        /// フォルダー日時に基づく枝刈りを有効化するかどうか（既定値: false）。
        /// 【安全原則】NTFSでは子ファイルの本文更新時に親フォルダーのLastWriteTimeが更新されないため、
        /// 検索漏れ（false negative）を防止すべく既定では無効化（安全Live走査）。
        /// 変更頻度が極めて低い読み取り専用アーカイブ等で明示的にオプトインされた場合のみ動作する。
        /// </summary>
        public bool EnableFolderTimestampPruning { get; set; } = false;

        /// <summary>
        /// 指定フォルダーがキャッシュに存在し、かつ更新日時が一致しているか判定。
        /// EnableFolderTimestampPruning が有効な場合のみ枝刈りを実行し配下エントリを返す。
        /// </summary>
        public bool TryGetPrunedEntries(
            string folderPath,
            DateTime currentLastWriteTimeUtc,
            bool includeDirectories,
            out List<ScannedFileEntry>? entries)
        {
            entries = null;
            if (!EnableFolderTimestampPruning)
            {
                return false;
            }

            string key = PathCanonicalizer.Normalize(folderPath);
            if (!_foldersByPath.TryGetValue(key, out var cachedFolder) || cachedFolder == null)
            {
                return false;
            }

            if (!cachedFolder.LastModified.HasValue)
            {
                return false;
            }

            // FAT/SMB等の2秒丸め誤差を許容
            var diffSeconds = Math.Abs((cachedFolder.LastModified.Value.ToUniversalTime() - currentLastWriteTimeUtc).TotalSeconds);
            if (diffSeconds > 2.0)
            {
                return false; // 更新されているためLive走査が必要
            }

            // ★ 枝刈り成功！配下の全エントリを再帰収集
            entries = new List<ScannedFileEntry>();
            CollectEntriesRecursive(cachedFolder, entries, includeDirectories);
            return true;
        }

        private static void CollectEntriesRecursive(FileItemNode folder, List<ScannedFileEntry> results, bool includeDirectories)
        {
            if (folder.Children == null) return;

            for (int i = 0; i < folder.Children.Count; i++)
            {
                var child = folder.Children[i];
                if (child.IsDirectory)
                {
                    if (includeDirectories)
                    {
                        results.Add(CreateEntry(child, folder.FullPath));
                    }
                    CollectEntriesRecursive(child, results, includeDirectories);
                }
                else
                {
                    results.Add(CreateEntry(child, folder.FullPath));
                }
            }
        }

        private static ScannedFileEntry CreateEntry(FileItemNode node, string parentPath)
        {
            var attrs = node.IsDirectory ? FileAttributes.Directory : FileAttributes.Normal;
            var time = node.LastModified ?? DateTime.Now;
            var creation = node.CreationTime ?? time;
            return new ScannedFileEntry(node.FullPath, node.Name, parentPath, node.Size, creation, time, time, attrs);
        }
    }
}
