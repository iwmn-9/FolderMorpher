using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AstraSize.Models;
using AstraSize.Services;
using AstraSize.Services.Mft;
using FolderMorpher.Models;
using FolderMorpher.Services;

namespace FolderMorpher.Services.Testing
{
    public static partial class RegressionTestSuite
    {
        /// <summary>
        /// [DOMAIN 2/7] Storage Explorer & UNC Traversal 統合テスト
        /// </summary>
        public static async Task TestDomain_StorageAndUncTraversalAsync()
        {
            await TestStorageHistoryTreeCacheAndDiffAsync();
            await TestUiBindingContractAndCacheExpansionStateAsync();
            TestStorageSessionAndAuditSortingContracts();
            await TestNativeDirectoryEnumeratorAndConcurrency2Async();
        }

        private static async Task TestStorageHistoryTreeCacheAndDiffAsync()
        {
            var historyService = new StorageHistoryService();
            string testRootPath = @"C:\TestFakeShare\DepartmentShare";

            // 1. 模擬ツリーを構築
            var root = new FileItemNode(testRootPath, "DepartmentShare", 1024 * 1024 * 300, true);
            var subA = new FileItemNode(Path.Combine(testRootPath, "Sales"), "Sales", 1024 * 1024 * 100, true) { Parent = root };
            var subB = new FileItemNode(Path.Combine(testRootPath, "Dev"), "Dev", 1024 * 1024 * 200, true) { Parent = root };
            root.Children.Add(subA);
            root.Children.Add(subB);

            // 2. キャッシュ保存
            await historyService.SaveTreeCacheAsync(root);

            // 3. キャッシュ復元（0秒ロード模擬）
            var cached = await historyService.LoadTreeCacheAsync(testRootPath);
            if (cached == null)
            {
                throw new InvalidOperationException("TreeCache 復元失敗: キャッシュがロードできませんでした。");
            }
            if (cached.Children.Count != 2 || cached.Size != root.Size)
            {
                throw new InvalidOperationException($"TreeCache 整合性エラー: 復元ノードサイズが一致しません (期待: {root.Size}, 実際: {cached.Size})");
            }
            if (cached.CachedTopFiles == null || cached.CachedExtensionStats == null)
            {
                throw new InvalidOperationException("TreeCache 0秒Insightsエラー: TopFiles または ExtensionStats がキャッシュから復元されていません。");
            }

            // 4. 最新ツリー（サイズ変化＋新規フォルダ発生）
            var newRoot = new FileItemNode(testRootPath, "DepartmentShare", 1024 * 1024 * 350, true);
            var newSubA = new FileItemNode(Path.Combine(testRootPath, "Sales"), "Sales", 1024 * 1024 * 150, true) { Parent = newRoot }; // +50MB 増分
            var newSubB = new FileItemNode(Path.Combine(testRootPath, "Dev"), "Dev", 1024 * 1024 * 200, true) { Parent = newRoot };   // 変化なし
            newRoot.Children.Add(newSubA);
            newRoot.Children.Add(newSubB);

            // 5. 差分検出エンジン実行
            historyService.ApplyTreeDiff(newRoot, cached);

            // 6. 検証
            if (newSubA.DiffBytes != 1024 * 1024 * 50)
            {
                throw new InvalidOperationException($"TreeDiff 差分計算エラー: Sales の増分が不正です ({newSubA.DiffBytes})");
            }
            if (!newSubA.HasDiff || string.IsNullOrEmpty(newSubA.DiffFormatted) || !newSubA.DiffFormatted.Contains("+50 MB ▲"))
            {
                throw new InvalidOperationException($"TreeDiff バッジエラー: Sales の増分バッジ表示が不正です ({newSubA.DiffFormatted})");
            }
            if (newSubB.DiffBytes != 0 || newSubB.HasDiff)
            {
                throw new InvalidOperationException("TreeDiff 差分誤爆エラー: 変化のない Dev に差分が検出されています。");
            }

            // 7. GetInsightsForNode の Top10 インプレース保持＆省メモリ検証
            var mockParent = new FileItemNode(@"C:\Mock", "Mock", 1000, true);
            for (int i = 1; i <= 25; i++)
            {
                mockParent.Children.Add(new FileItemNode($@"C:\Mock\file{i}.dat", $"file{i}.dat", i * 100, false));
            }
            var (topFiles, extStats) = DiskScanService.GetInsightsForNode(mockParent);
            if (topFiles.Count != 10)
            {
                throw new InvalidOperationException($"GetInsightsForNode エラー: Top10 の件数が 10 件ではありません ({topFiles.Count})");
            }
            if (topFiles[0].Size != 2500 || topFiles[9].Size != 1600)
            {
                throw new InvalidOperationException($"GetInsightsForNode エラー: Top10 が正しく降順ソートされていません (Top1: {topFiles[0].Size}, Top10: {topFiles[9].Size})");
            }

            // 8. Shared + Local スナップショット履歴のマージ検証
            var allSnapshots = await historyService.LoadAllAsync();
            if (allSnapshots == null)
            {
                throw new InvalidOperationException("StorageHistory LoadAllAsync エラー: スナップショット一覧が null です。");
            }
        }


        public static async Task TestUiBindingContractAndCacheExpansionStateAsync()
        {
            // 1. FileItemNode UI Binding Contract の検証
            var parent = new FileItemNode(@"C:\Root", "Root", 1000, true);
            var child = new FileItemNode(@"C:\Root\Sub", "Sub", 400, true) { Parent = parent, Level = 1 };
            parent.Children.Add(child);

            // HasChildren, ExpandIcon, IconGlyph, FontWeight
            if (!parent.HasChildren) throw new InvalidOperationException("UI Binding Error: parent.HasChildren should be true");
            if (parent.ExpandIcon != "▶") throw new InvalidOperationException($"UI Binding Error: parent.ExpandIcon was '{parent.ExpandIcon}', expected '▶'");
            parent.IsExpanded = true;
            if (parent.ExpandIcon != "▼") throw new InvalidOperationException($"UI Binding Error: parent.ExpandIcon was '{parent.ExpandIcon}', expected '▼'");
            if (parent.IconGlyph != "📁") throw new InvalidOperationException($"UI Binding Error: parent.IconGlyph was '{parent.IconGlyph}', expected '📁'");
            if (parent.FontWeight != FontWeights.Bold) throw new InvalidOperationException("UI Binding Error: parent.FontWeight should be Bold");

            // SharePercentage, IsDriveRoot, ShareFormatted
            child.Percentage = 40.0;
            if (Math.Abs(child.SharePercentage - 40.0) > 0.001) throw new InvalidOperationException("UI Binding Error: child.SharePercentage should be 40.0");
            if (child.IsDriveRoot) throw new InvalidOperationException("UI Binding Error: child.IsDriveRoot should be false");
            if (child.ShareFormatted != "40.0%") throw new InvalidOperationException($"UI Binding Error: child.ShareFormatted was '{child.ShareFormatted}', expected '40.0%'");

            // 2. TreeCache の IsExpanded 永続化と復元の検証
            var historyService = new StorageHistoryService();
            string testDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_CacheExp_" + Guid.NewGuid().ToString("N"));
            try
            {
                var testRoot = new FileItemNode(testDir, "CacheExpRoot", 2000, true);
                var subFolder = new FileItemNode(Path.Combine(testDir, "ExpandedSub"), "ExpandedSub", 1000, true)
                {
                    Parent = testRoot,
                    IsExpanded = true
                };
                testRoot.Children.Add(subFolder);

                await historyService.SaveTreeCacheAsync(testRoot);
                var restored = await historyService.LoadTreeCacheAsync(testDir);

                if (restored == null) throw new InvalidOperationException("Cache expansion test failed: restored node is null");
                var restoredSub = restored.Children.FirstOrDefault(c => c.Name == "ExpandedSub");
                if (restoredSub == null) throw new InvalidOperationException("Cache expansion test failed: restoredSub is null");
                if (!restoredSub.IsExpanded) throw new InvalidOperationException("Cache expansion test failed: restoredSub.IsExpanded was not preserved as true");
            }
            finally
            {
                // clean up
            }
        }

        /// <summary>
        /// 13. 重複ファイル色分けグルーピング & AD/ローカル OU階層ツリー構築の検証
        /// </summary>

        private static void TestStorageSessionAndAuditSortingContracts()
        {
            // 1. Audit 詳細列（Detail）ソート & 重複原本優先の検証
            var items = new List<AuditItem>
            {
                new AuditItem { FullPath = "C:\\path\\b_copy.docx", FileName = "b_copy.docx", Detail = "B-Group", IsOriginalCandidate = false, DuplicateGroupIndex = 2 },
                new AuditItem { FullPath = "C:\\path\\b_master.docx", FileName = "b_master.docx", Detail = "B-Group", IsOriginalCandidate = true, DuplicateGroupIndex = 2 },
                new AuditItem { FullPath = "C:\\path\\a_file.txt", FileName = "a_file.txt", Detail = "A-Detail", IsOriginalCandidate = false },
                new AuditItem { FullPath = "C:\\path\\z_file.log", FileName = "z_file.log", Detail = "Z-Detail", IsOriginalCandidate = false }
            };

            var detailAsc = AuditReportService.SortAuditItems(items, "Detail", descending: false);
            if (detailAsc[0].Detail != "A-Detail")
                throw new InvalidOperationException($"Expected A-Detail first, got {detailAsc[0].Detail}");
            if (detailAsc[1].FileName != "b_master.docx" || !detailAsc[1].IsOriginalCandidate)
                throw new InvalidOperationException($"Expected b_master.docx as original first in B-Group, got {detailAsc[1].FileName}");
            if (detailAsc[2].FileName != "b_copy.docx")
                throw new InvalidOperationException($"Expected b_copy.docx after master, got {detailAsc[2].FileName}");
            if (detailAsc[3].Detail != "Z-Detail")
                throw new InvalidOperationException($"Expected Z-Detail last, got {detailAsc[3].Detail}");

            var detailDesc = AuditReportService.SortAuditItems(items, "Detail", descending: true);
            if (detailDesc[0].Detail != "Z-Detail")
                throw new InvalidOperationException($"Expected Z-Detail first in descending, got {detailDesc[0].Detail}");

            // 2. AppSettings の StorageTabPaths & ActiveStorageTabIndex 永続化契約
            var settings = new AppSettings
            {
                StorageTabPaths = new List<string> { @"\\server\share1", @"D:\Data", @"C:\Users\Public" },
                ActiveStorageTabIndex = 1
            };

            var json = System.Text.Json.JsonSerializer.Serialize(settings);
            var deserialized = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json);
            if (deserialized == null || deserialized.StorageTabPaths.Count != 3)
                throw new InvalidOperationException("AppSettings StorageTabPaths failed serialization roundtrip.");
            if (deserialized.StorageTabPaths[0] != @"\\server\share1" || deserialized.StorageTabPaths[1] != @"D:\Data")
                throw new InvalidOperationException("AppSettings StorageTabPaths values corrupted.");
            if (deserialized.ActiveStorageTabIndex != 1)
                throw new InvalidOperationException($"Expected ActiveStorageTabIndex 1, got {deserialized.ActiveStorageTabIndex}");

            // 3. ScanTabModel のキャッシュ復元シミュレーション
            var tab = new AstraSize.Models.ScanTabModel
            {
                TargetPath = @"D:\Data",
                TabTitle = "Data"
            };
            var root = new FileItemNode { Name = "Data", FullPath = @"D:\Data", IsDirectory = true, Size = 1024 * 1024 * 50 };
            var child = new FileItemNode { Name = "doc.txt", FullPath = @"D:\Data\doc.txt", IsDirectory = false, Size = 1024 * 1024 * 50 };
            root.Children.Add(child);
            root.IsExpanded = true;
            tab.RootNode = root;
            tab.FlattenTree();

            if (tab.FlatVisibleItems.Count != 2)
                throw new InvalidOperationException($"Expected 2 flat items after FlattenTree, got {tab.FlatVisibleItems.Count}");
        }


        private static async Task TestNativeDirectoryEnumeratorAndConcurrency2Async()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_Regression_Test31_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                // 1. テスト階層の作成 (サブフォルダ複数、ファイル複数)
                string subA = Path.Combine(testDir, "SubA");
                string subB = Path.Combine(testDir, "SubB");
                string subDeep = Path.Combine(subA, "Deep");
                Directory.CreateDirectory(subA);
                Directory.CreateDirectory(subB);
                Directory.CreateDirectory(subDeep);

                string file1 = Path.Combine(testDir, "root1.txt");
                string file2 = Path.Combine(subA, "subA_file.log");
                string file3 = Path.Combine(subDeep, "deep_file.dat");
                string file4 = Path.Combine(subB, "subB_file.bin");

                await File.WriteAllBytesAsync(file1, new byte[1000]);
                await File.WriteAllBytesAsync(file2, new byte[2000]);
                await File.WriteAllBytesAsync(file3, new byte[3000]);
                await File.WriteAllBytesAsync(file4, new byte[4000]);

                // 2. NativeDirectoryEnumerator 単体検証
                var dirs = new List<NativeFindEntry>();
                var files = new List<NativeFindEntry>();
                bool ok = NativeDirectoryEnumerator.TryEnumerateEntries(testDir, dirs, files, out var error);
                if (!ok || error != null)
                    throw new InvalidOperationException($"NativeDirectoryEnumerator failed on root: {error}");

                if (dirs.Count != 2) // SubA, SubB
                    throw new InvalidOperationException($"Expected 2 subdirectories, got {dirs.Count}");
                if (files.Count != 1) // root1.txt
                    throw new InvalidOperationException($"Expected 1 file, got {files.Count}");

                var rootFile = files[0];
                if (rootFile.Name != "root1.txt" || rootFile.Size != 1000)
                    throw new InvalidOperationException($"File metadata mismatch: Name={rootFile.Name}, Size={rootFile.Size}");

                // 3. SafeFileEnumerator.EnumerateFilesSafeParallelAsync (並列度2) 検証
                var coverage = new ScanCoverage();
                var parallelFiles = await SafeFileEnumerator.EnumerateFilesSafeParallelAsync(
                    testDir,
                    "*.*",
                    coverage,
                    null,
                    CancellationToken.None);

                if (parallelFiles.Count != 4)
                    throw new InvalidOperationException($"Parallel scan expected 4 files, got {parallelFiles.Count}");

                long totalSize = parallelFiles.Sum(f => f.Length);
                if (totalSize != 10000) // 1000 + 2000 + 3000 + 4000
                    throw new InvalidOperationException($"Parallel scan total size mismatch: expected 10000, got {totalSize}");

                if (coverage.TotalFilesFound != 4)
                    throw new InvalidOperationException($"Coverage TotalFilesFound mismatch: {coverage.TotalFilesFound}");

                // 3b. SafeFileEnumerator.EnumerateFileEntriesParallelAsync (ScannedFileEntry メタデータ直接保持・再stat問い合わせゼロ) 検証
                var entryCoverage = new ScanCoverage();
                var scannedEntries = await SafeFileEnumerator.EnumerateFileEntriesParallelAsync(
                    testDir,
                    "*.*",
                    entryCoverage,
                    null,
                    CancellationToken.None);

                if (scannedEntries.Count != 4)
                    throw new InvalidOperationException($"EnumerateFileEntriesParallelAsync expected 4 entries, got {scannedEntries.Count}");

                var rootEntry = scannedEntries.FirstOrDefault(e => e.Name == "root1.txt");
                if (rootEntry == null)
                    throw new InvalidOperationException("ScannedFileEntry for root1.txt not found.");
                if (rootEntry.Length != 1000)
                    throw new InvalidOperationException($"ScannedFileEntry Length mismatch: expected 1000, got {rootEntry.Length}");
                if (rootEntry.CreationTime == DateTime.MinValue || rootEntry.LastWriteTime == DateTime.MinValue)
                    throw new InvalidOperationException("ScannedFileEntry timestamps were not populated from Win32 find data.");

                // 4. DiskScanService (フォールバックスキャン・並列度2ツリー構築) 検証
                var diskScanner = new AstraSize.Services.DiskScanService();
                var (rootNode, summary) = await diskScanner.ScanPathAsync(testDir, null, CancellationToken.None);

                if (rootNode == null)
                    throw new InvalidOperationException("DiskScanService returned null rootNode.");

                if (rootNode.FileCount != 4)
                    throw new InvalidOperationException($"DiskScanService rootNode.FileCount expected 4, got {rootNode.FileCount}");

                if (rootNode.Size != 10000)
                    throw new InvalidOperationException($"DiskScanService rootNode.Size expected 10000, got {rootNode.Size}");

                if (rootNode.FolderCount != 3) // SubA, SubB, Deep
                    throw new InvalidOperationException($"DiskScanService rootNode.FolderCount expected 3, got {rootNode.FolderCount}");

                // 5. SafeFindHandle RAII 解放検証
                using (var safeHandle = new SafeFindHandle(IntPtr.Zero))
                {
                    if (!safeHandle.IsInvalid)
                        throw new InvalidOperationException("SafeFindHandle with IntPtr.Zero should be invalid.");
                }
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }
    }
}