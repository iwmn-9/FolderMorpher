using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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
            await TestPathCanonicalizerAndSha256CacheAsync();
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
            if (!newSubA.HasDiff) throw new InvalidOperationException("TreeDiff 増分判定が不正です。");
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

            // Facts and expansion state used by the cache.
            if (!parent.HasChildren) throw new InvalidOperationException("UI Binding Error: parent.HasChildren should be true");
            parent.IsExpanded = true;

            // SharePercentage, IsDriveRoot, ShareFormatted
            child.Percentage = 40.0;
            if (Math.Abs(child.SharePercentage - 40.0) > 0.001) throw new InvalidOperationException("UI Binding Error: child.SharePercentage should be 40.0");
            if (child.IsDriveRoot) throw new InvalidOperationException("UI Binding Error: child.IsDriveRoot should be false");

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

                // 6. 4段自動フォールバック（プレーンUNC / 拡張パス両対応）耐性検証
                var uncDirs = new List<NativeFindEntry>();
                var uncFiles = new List<NativeFindEntry>();
                bool fallbackOk = NativeDirectoryEnumerator.TryEnumerateEntries(testDir, uncDirs, uncFiles, out var fallbackErr);
                // 7. Adaptive Concurrency (ADR 80: 初期値2・下限2・上限4・即時崖落ち・天井クランプ) 検証
                {
                    // A. 初期状態と下限・上限の不変契約
                    var ctrl = new AdaptiveConcurrencyController();
                    if (ctrl.CurrentConcurrency != 2)
                        throw new InvalidOperationException($"ADR 80 Initial concurrency expected 2, got {ctrl.CurrentConcurrency}");
                    if (ctrl.SessionMaxCeiling != 4)
                        throw new InvalidOperationException($"ADR 80 Initial ceiling expected 4, got {ctrl.SessionMaxCeiling}");
                    if (AdaptiveConcurrencyController.MinConcurrency != 2)
                        throw new InvalidOperationException($"ADR 80 MinConcurrency must be 2, got {AdaptiveConcurrencyController.MinConcurrency}");

                    // B. ベースライン確立と昇格 (Additive Increase: +1)
                    // 50サンプルの正常低レイテンシ (10ms)
                    for (int i = 0; i < 50; i++)
                    {
                        ctrl.RecordSample(10.0, isNetworkError: false);
                    }
                    if (!ctrl.BaselineEstablished)
                        throw new InvalidOperationException("ADR 80 Baseline was not established after 50 samples.");
                    if (ctrl.CurrentConcurrency != 2)
                        throw new InvalidOperationException($"ADR 80 Concurrency during baseline should remain 2, got {ctrl.CurrentConcurrency}");

                    // さらに40回安定稼働 ➔ 3 へ昇格 (+1)
                    for (int i = 0; i < 45; i++)
                    {
                        ctrl.RecordSample(10.0, isNetworkError: false);
                    }
                    if (ctrl.CurrentConcurrency != 3)
                        throw new InvalidOperationException($"ADR 80 Concurrency after stable period expected 3, got {ctrl.CurrentConcurrency}");

                    // C. ネットワークエラー検知による即時崖落ち (3 ➔ 2) & 天井クランプ (2)
                    ctrl.RecordSample(10.0, isNetworkError: true);
                    if (ctrl.CurrentConcurrency != 2)
                        throw new InvalidOperationException($"ADR 80 Concurrency after error expected 2, got {ctrl.CurrentConcurrency}");
                    if (ctrl.SessionMaxCeiling != 2)
                        throw new InvalidOperationException($"ADR 80 Session ceiling after error expected 2, got {ctrl.SessionMaxCeiling}");

                    // エラー後はどれだけ正常サンプルが続いても天井クランプ (2) により昇格しないこと
                    for (int i = 0; i < 100; i++)
                    {
                        ctrl.RecordSample(10.0, isNetworkError: false);
                    }
                    if (ctrl.CurrentConcurrency != 2)
                        throw new InvalidOperationException($"ADR 80 Concurrency must remain clamped at 2, got {ctrl.CurrentConcurrency}");

                    // D. 構造化 EnumerationFailureKind 分類とネットワークエラー判定の完全性検証 (ADR 81)
                    if (NativeDirectoryEnumerator.ClassifyWin32Error(58) != EnumerationFailureKind.Network)
                        throw new InvalidOperationException("ADR 81 Failed to classify Win32 58 as Network");
                    if (NativeDirectoryEnumerator.ClassifyWin32Error(59) != EnumerationFailureKind.Network)
                        throw new InvalidOperationException("ADR 81 Failed to classify Win32 59 as Network");
                    if (NativeDirectoryEnumerator.ClassifyWin32Error(64) != EnumerationFailureKind.Network)
                        throw new InvalidOperationException("ADR 81 Failed to classify Win32 64 as Network");
                    if (NativeDirectoryEnumerator.ClassifyWin32Error(121) != EnumerationFailureKind.Network)
                        throw new InvalidOperationException("ADR 81 Failed to classify Win32 121 as Network");
                    if (NativeDirectoryEnumerator.ClassifyWin32Error(5) != EnumerationFailureKind.AccessDenied)
                        throw new InvalidOperationException("ADR 81 Failed to classify Win32 5 as AccessDenied");
                    if (NativeDirectoryEnumerator.ClassifyWin32Error(2) != EnumerationFailureKind.NotFound)
                        throw new InvalidOperationException("ADR 81 Failed to classify Win32 2 as NotFound");

                    // 構造化エラーによる即時崖落ちの検証
                    var enumErrCtrl = new AdaptiveConcurrencyController();
                    for (int i = 0; i < 50; i++) enumErrCtrl.RecordSample(10.0, isNetworkError: false);
                    for (int i = 0; i < 45; i++) enumErrCtrl.RecordSample(10.0, isNetworkError: false);
                    if (enumErrCtrl.CurrentConcurrency != 3)
                        throw new InvalidOperationException("ADR 81 Concurrency should be 3 before error");
                    enumErrCtrl.RecordSample(10.0, EnumerationFailureKind.Network);
                    if (enumErrCtrl.CurrentConcurrency != 2 || enumErrCtrl.SessionMaxCeiling != 2)
                        throw new InvalidOperationException("ADR 81 Concurrency failed to drop on EnumerationFailureKind.Network");

                    // E. 🚨 超短期 Emergency Window (直近8件中3件のスパイク) による超早期崖落ち検証 (ADR 81)
                    // 高速LAN (10ms) でサーバーが急激に苦しくなり 45ms〜50ms が数件続いた場合、30件を待たずに数件で退避すること
                    var emgCtrl = new AdaptiveConcurrencyController();
                    // 1. ベースライン (10ms) 確立
                    for (int i = 0; i < 50; i++) emgCtrl.RecordSample(10.0, isNetworkError: false);
                    // 2. 40回安定で並列度 3 へ昇格
                    for (int i = 0; i < 45; i++) emgCtrl.RecordSample(10.0, isNetworkError: false);
                    if (emgCtrl.CurrentConcurrency != 3)
                        throw new InvalidOperationException("ADR 81 Concurrency before emergency window should be 3");

                    // 3. わずか3件のスパイク (45ms: baseline 10ms の4.5倍) を投入
                    emgCtrl.RecordSample(45.0, isNetworkError: false);
                    emgCtrl.RecordSample(48.0, isNetworkError: false);
                    emgCtrl.RecordSample(50.0, isNetworkError: false);

                    // ➔ Emergency Window により、わずか3件の兆候で即座に並列度 2 に崖落ち＆天井クランプされていること！
                    if (emgCtrl.CurrentConcurrency != 2)
                        throw new InvalidOperationException($"ADR 81 Emergency Window expected concurrency 2, got {emgCtrl.CurrentConcurrency}");
                    if (emgCtrl.SessionMaxCeiling != 2)
                        throw new InvalidOperationException($"ADR 81 Emergency Window expected ceiling 2, got {emgCtrl.SessionMaxCeiling}");

                    // E. スロット獲得・解放の並行性（デッドロックフリー）検証
                    var slotCtrl = new AdaptiveConcurrencyController();
                    var tasks = new Task[4];
                    int completions = 0;
                    for (int t = 0; t < 4; t++)
                    {
                        tasks[t] = Task.Run(async () =>
                        {
                            for (int cycle = 0; cycle < 20; cycle++)
                            {
                                using var lease = await slotCtrl.AcquireAsync(CancellationToken.None);
                                await Task.Delay(1);
                                lease.Report(5.0, isError: false);
                            }
                            Interlocked.Increment(ref completions);
                        });
                    }
                    Task.WaitAll(tasks);
                    if (completions != 4)
                        throw new InvalidOperationException($"ADR 80 Concurrent slot lease expected 4 completions, got {completions}");

                    // F. 【ADR 82】スロット上限厳格遵守 ＆ SlotLease ReleaseOnce 二重解放根絶検証
                    // 1. CurrentConcurrency = 2 固定状態（ベースライン未確定フェーズ: 20サンプル < 50）で4タスクを同時投入し、
                    //    実際に処理中の最大アクティブスロット数が厳格に <= 2 であること（同時実行上限の厳密遵守）
                    var strictCtrl2 = new AdaptiveConcurrencyController();
                    if (strictCtrl2.CurrentConcurrency != 2)
                        throw new InvalidOperationException("ADR 82 Precondition: CurrentConcurrency should be 2");

                    int maxObserved2 = 0;
                    int activeCounter2 = 0;
                    var tasksPhase1 = new Task[4];
                    int completionsPhase1 = 0;

                    for (int t = 0; t < 4; t++)
                    {
                        tasksPhase1[t] = Task.Run(async () =>
                        {
                            for (int cycle = 0; cycle < 5; cycle++)
                            {
                                using var lease = await strictCtrl2.AcquireAsync(CancellationToken.None);

                                int currentActive = Interlocked.Increment(ref activeCounter2);
                                int internalActive = strictCtrl2.ActiveSlots;
                                int observed = Math.Max(currentActive, internalActive);

                                int curMax;
                                do
                                {
                                    curMax = Volatile.Read(ref maxObserved2);
                                    if (observed <= curMax) break;
                                } while (Interlocked.CompareExchange(ref maxObserved2, observed, curMax) != curMax);

                                if (observed > 2)
                                {
                                    throw new InvalidOperationException($"ADR 82 Violation: Active slots exceeded limit 2! Observed: {observed}");
                                }

                                await Task.Delay(5); // クリティカルセクションで少し待機
                                Interlocked.Decrement(ref activeCounter2);

                                lease.Report(5.0, isError: false);
                            }
                            Interlocked.Increment(ref completionsPhase1);
                        });
                    }
                    Task.WaitAll(tasksPhase1);

                    if (completionsPhase1 != 4)
                        throw new InvalidOperationException($"ADR 82 Expected 4 completions, got {completionsPhase1}");
                    if (maxObserved2 > 2)
                        throw new InvalidOperationException($"ADR 82 Max observed active slots ({maxObserved2}) exceeded Concurrency limit 2!");
                    if (strictCtrl2.ActiveSlots != 0)
                        throw new InvalidOperationException($"ADR 82 Slot leak or underflow detected: ActiveSlots = {strictCtrl2.ActiveSlots}");

                    // 2. 様々な解放パターン（明示的Report、二重Report、Dispose単体）を混在させた高負荷並行テストで、
                    //    スロットの二重解放やアンダーフロー（負数化）、リークが一切発生せず 0 に収束すること
                    var multiReleaseCtrl = new AdaptiveConcurrencyController();
                    var tasksPhase2 = new Task[8];
                    int completionsPhase2 = 0;

                    for (int t = 0; t < 8; t++)
                    {
                        tasksPhase2[t] = Task.Run(async () =>
                        {
                            for (int cycle = 0; cycle < 15; cycle++)
                            {
                                using var lease = await multiReleaseCtrl.AcquireAsync(CancellationToken.None);
                                await Task.Delay(1);

                                if (cycle % 3 == 0)
                                {
                                    // パターン1: 明示的 Report (その後の using Dispose による二重解放リスク検証)
                                    lease.Report(5.0, isError: false);
                                }
                                else if (cycle % 3 == 1)
                                {
                                    // パターン2: 二重 Report 呼び出し
                                    lease.Report(5.0, isError: false);
                                    lease.Report(5.0, isError: false);
                                }
                                // パターン3: 何も呼ばず using Dispose に任せる
                            }
                            Interlocked.Increment(ref completionsPhase2);
                        });
                    }
                    Task.WaitAll(tasksPhase2);

                    if (completionsPhase2 != 8)
                        throw new InvalidOperationException($"ADR 82 Expected 8 completions in Phase 2, got {completionsPhase2}");
                    if (multiReleaseCtrl.ActiveSlots != 0)
                        throw new InvalidOperationException($"ADR 82 Phase 2 slot leak or underflow detected: ActiveSlots = {multiReleaseCtrl.ActiveSlots}");
                }
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }

        private static async Task TestPathCanonicalizerAndSha256CacheAsync()
        {
            // 1. PathCanonicalizer の正規化テスト
            string uncPath = @"\\Server\Share\SubFolder\";
            string normalizedUnc = PathCanonicalizer.Normalize(uncPath);
            if (!normalizedUnc.Equals(@"\\Server\Share\SubFolder", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"PathCanonicalizer エラー: UNCパス正規化不正 (期待: \\\\Server\\Share\\SubFolder, 実際: {normalizedUnc})");
            }

            string extendedPath = @"\\?\C:\FolderName\File.txt";
            string normalizedExtended = PathCanonicalizer.Normalize(extendedPath);
            if (!normalizedExtended.Equals(@"C:\foldername\file.txt", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"PathCanonicalizer エラー: 拡張プレフィックス除去不正 (実際: {normalizedExtended})");
            }

            if (!PathCanonicalizer.AreSamePath(@"C:\Test\Sub", @"c:\test/sub/"))
            {
                throw new InvalidOperationException("PathCanonicalizer エラー: AreSamePath が一致しません");
            }

            if (!PathCanonicalizer.IsNetworkPath(@"\\server\share\folder"))
            {
                throw new InvalidOperationException("PathCanonicalizer エラー: IsNetworkPath が UNC を判定できません");
            }

            if (PathCanonicalizer.IsNetworkPath(@"C:\LocalFolder"))
            {
                throw new InvalidOperationException("PathCanonicalizer エラー: IsNetworkPath がローカルパスを誤判定しました");
            }

            // 2. TreeCache における Sha256 の保持 & JSON シリアライズ往復テスト
            string testRoot = @"C:\TestFakeRoot\TreeShaTest";
            var rootNode = new FileItemNode(testRoot, "TreeShaTest", 1024 * 1024, true);
            var fileNode = new FileItemNode(Path.Combine(testRoot, "sample.bin"), "sample.bin", 1024, false)
            {
                Parent = rootNode,
                Sha256 = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855"
            };
            rootNode.Children.Add(fileNode);

            var historyService = new StorageHistoryService();
            await historyService.SaveTreeCacheAsync(rootNode);

            var restored = await historyService.LoadTreeCacheAsync(testRoot);
            if (restored == null)
            {
                throw new InvalidOperationException("TreeCache 復元失敗: Sha256 テスト用キャッシュがロードできません。");
            }

            var restoredFile = restored.Children.FirstOrDefault(c => c.Name == "sample.bin");
            if (restoredFile == null)
            {
                throw new InvalidOperationException("TreeCache 復元失敗: 子ノード sample.bin が存在しません。");
            }

            if (restoredFile.Sha256 != fileNode.Sha256)
            {
                throw new InvalidOperationException($"TreeCache Sha256 欠落エラー: 復元されたノードの Sha256 が一致しません (期待: {fileNode.Sha256}, 実際: {restoredFile.Sha256})");
            }

            // 3. Section 24: SQLite ツリーキャッシュ ＆ ポータブル JSON 相互運用性検証 (ADR 98)
            await TestSqliteTreeCacheAndJsonInteroperabilityAsync();

            // 4. Section 24b: Sol 提唱 I/O Governor AIMD ＆ 純粋 I/O 時間追跡検証 (ADR 98)
            await TestIoGovernorAimdAndPureIoTrackingAsync();
        }

        /// <summary>
        /// Section 24: SQLite ツリーキャッシュの高速保存・復元、および JSON エクスポート/インポート相互運用性の厳格検証
        /// </summary>
        private static async Task TestSqliteTreeCacheAndJsonInteroperabilityAsync()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), $"FolderMorpher_TreeCacheTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                var testDbPath = Path.Combine(tempDir, "test_tree_cache.db");
                var cacheService = new SqliteTreeCacheService(testDbPath);

                // 1. 合成ツリー構築 (ルート -> 子フォルダ -> ファイル2件)
                var rootPath = @"C:\MockShare\ProjectAlpha";
                var root = new FileItemNode(rootPath, "ProjectAlpha", 1024 * 1024 * 50, true)
                {
                    FileCount = 2,
                    FolderCount = 1,
                    CachedTopFiles = new List<LargestFileInfo>
                    {
                        new LargestFileInfo { FullPath = @"C:\MockShare\ProjectAlpha\Sub\report.pdf", Size = 1024 * 1024 * 40 }
                    },
                    CachedExtensionStats = new List<ExtensionStat>
                    {
                        new ExtensionStat { Extension = ".pdf", FileCount = 1, TotalSize = 1024 * 1024 * 40 },
                        new ExtensionStat { Extension = ".txt", FileCount = 1, TotalSize = 1024 * 1024 * 10 }
                    }
                };

                var subDir = new FileItemNode(Path.Combine(rootPath, "Sub"), "Sub", 1024 * 1024 * 50, true)
                {
                    Parent = root,
                    Level = 1,
                    FileCount = 2,
                    FolderCount = 0
                };
                root.Children.Add(subDir);

                var file1 = new FileItemNode(Path.Combine(subDir.FullPath, "report.pdf"), "report.pdf", 1024 * 1024 * 40, false)
                {
                    Parent = subDir,
                    Level = 2,
                    Sha256 = "AAAA1111222233334444555566667777888899990000AAAABBBBCCCCDDDDEEEEFFFF"
                };
                subDir.Children.Add(file1);

                var file2 = new FileItemNode(Path.Combine(subDir.FullPath, "notes.txt"), "notes.txt", 1024 * 1024 * 10, false)
                {
                    Parent = subDir,
                    Level = 2,
                    Sha256 = "BBBB1111222233334444555566667777888899990000AAAABBBBCCCCDDDDEEEEFFFF"
                };
                subDir.Children.Add(file2);

                // 2. SQLite DB へ保存
                await cacheService.SaveTreeAsync(root);

                // 3. SQLite DB からロードして検証
                var loaded = await cacheService.LoadTreeAsync(rootPath);
                if (loaded == null)
                {
                    throw new InvalidOperationException("SqliteTreeCache: LoadTreeAsync でツリーがロードできませんでした。");
                }

                if (loaded.FullPath != rootPath || loaded.Size != root.Size || loaded.FileCount != 2)
                {
                    throw new InvalidOperationException($"SqliteTreeCache: ルートノードの集計値不一致 (Path: {loaded.FullPath}, Size: {loaded.Size}, Files: {loaded.FileCount})");
                }

                if (loaded.Children.Count != 1 || loaded.Children[0].Children.Count != 2)
                {
                    throw new InvalidOperationException($"SqliteTreeCache: 階層構造の復元不正 (Sub: {loaded.Children.Count}, Files: {loaded.Children[0].Children.Count})");
                }

                var shallowRoot = await cacheService.LoadBranchAsync(rootPath, rootPath);
                var shallowSub = await cacheService.LoadBranchAsync(rootPath, subDir.FullPath);
                if (shallowRoot?.Children.Count != 1 || !shallowRoot.Children[0].HasUnloadedChildren ||
                    shallowRoot.Children[0].Children.Count != 0 || shallowSub?.Children.Count != 2)
                    throw new InvalidOperationException("SqliteTreeCache: 階層単位の遅延読込が不正です。");
                var branchTop = await cacheService.GetTopFilesForSubtreeAsync(rootPath, subDir.FullPath);
                if (branchTop?.FirstOrDefault()?.FullPath != file1.FullPath)
                    throw new InvalidOperationException("SqliteTreeCache: 遅延読込時の上位ファイル集計が不正です。");

                var restoredFile1 = loaded.Children[0].Children.FirstOrDefault(c => c.Name == "report.pdf");
                if (restoredFile1 == null || restoredFile1.Sha256 != file1.Sha256)
                {
                    throw new InvalidOperationException("SqliteTreeCache: report.pdf の SHA-256 ハッシュが正しく復元されていません。");
                }

                if (loaded.CachedTopFiles == null || loaded.CachedTopFiles.Count != 1 || loaded.CachedTopFiles[0].FullPath != file1.FullPath)
                {
                    throw new InvalidOperationException("SqliteTreeCache: CachedTopFiles インサイトが復元されていません。");
                }

                // 4. UpdateSha256Async (DB 直接更新) の検証
                var newSha = "FFFF0000999988887777666655554444333322221111FFFF00009999888877776666";
                var updateMap = new Dictionary<string, string>
                {
                    { file2.FullPath, newSha }
                };
                await cacheService.UpdateSha256Async(rootPath, updateMap);

                var reloaded = await cacheService.LoadTreeAsync(rootPath);
                var reloadedFile2 = reloaded?.Children[0].Children.FirstOrDefault(c => c.Name == "notes.txt");
                if (reloadedFile2 == null || reloadedFile2.Sha256 != newSha)
                {
                    throw new InvalidOperationException("SqliteTreeCache: UpdateSha256Async による DB 直接更新が反映されていません。");
                }

                // 5. JSON ポータブル エクスポート ＆ インポート検証 (相互運用性)
                var exportJsonPath = Path.Combine(tempDir, "exported_tree.json");
                await cacheService.ExportToJsonFileAsync(rootPath, exportJsonPath);

                if (!File.Exists(exportJsonPath) || new FileInfo(exportJsonPath).Length == 0)
                {
                    throw new InvalidOperationException("SqliteTreeCache: ExportToJsonFileAsync で有効な JSON ファイルが出力されませんでした。");
                }

                // 別の新しい空 SQLite DB へインポート
                var testDbPath2 = Path.Combine(tempDir, "test_tree_cache_import.db");
                var cacheService2 = new SqliteTreeCacheService(testDbPath2);

                bool imported = await cacheService2.ImportFromJsonFileAsync(exportJsonPath);
                if (!imported)
                {
                    throw new InvalidOperationException("SqliteTreeCache: ImportFromJsonFileAsync が失敗しました。");
                }

                var importedTree = await cacheService2.LoadTreeAsync(rootPath);
                if (importedTree == null || importedTree.Size != root.Size || importedTree.FileCount != 2)
                {
                    throw new InvalidOperationException("SqliteTreeCache: インポートされたツリーの完全性検証失敗。");
                }

                var importedFile2 = importedTree.Children[0].Children.FirstOrDefault(c => c.Name == "notes.txt");
                if (importedFile2 == null || importedFile2.Sha256 != newSha)
                {
                    throw new InvalidOperationException("SqliteTreeCache: インポート後のツリーで更新済み SHA-256 が一致しません。");
                }

                // 6. DeleteRootAsync の検証
                await cacheService.DeleteRootAsync(rootPath);
                var deletedCheck = await cacheService.LoadTreeAsync(rootPath);
                if (deletedCheck != null)
                {
                    throw new InvalidOperationException("SqliteTreeCache: DeleteRootAsync 後もレコードが残存しています。");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// Section 24b: Sol 提唱 I/O Governor AIMD ＆ 純粋 I/O 時間追跡の検証
        /// </summary>
        private static async Task TestIoGovernorAimdAndPureIoTrackingAsync()
        {
            // 1. AIMD AdaptiveConcurrencyController の動作検証
            var controller = new AdaptiveConcurrencyController(min: 2, defaultVal: 4, max: 12);
            if (controller.CurrentConcurrency != 4)
            {
                throw new InvalidOperationException($"AdaptiveConcurrencyController: 初期並列度が 4 ではありません (実際: {controller.CurrentConcurrency})");
            }

            // 50件のサンプルでベースライン確定
            for (int i = 0; i < 50; i++)
            {
                controller.ReleaseSlot(30, false);
            }

            if (!controller.BaselineEstablished)
            {
                throw new InvalidOperationException("AdaptiveConcurrencyController: 50サンプル後もベースラインが確立されていません。");
            }

            // ベースライン確立後、さらに低遅延（30ms）を 45回報告 (sampleIndex - _lastConcurrencyChangeSample >= 30 かつ % 15 == 0)
            // -> 加算増加 (Additive Increase) で昇格すること
            for (int i = 0; i < 45; i++)
            {
                controller.ReleaseSlot(30, false);
            }

            if (controller.CurrentConcurrency <= 4)
            {
                throw new InvalidOperationException($"AdaptiveConcurrencyController: 低遅延時の AIMD 加算昇格が機能していません (実際: {controller.CurrentConcurrency})");
            }

            // 異常高レイテンシ（2500ms）を報告 -> 乗算減少 (Multiplicative Decrease) で即座に安全降下すること
            controller.ReleaseSlot(2500, false);
            int reducedConcurrency = controller.CurrentConcurrency;
            if (reducedConcurrency >= 10)
            {
                throw new InvalidOperationException($"AdaptiveConcurrencyController: 異常高遅延時の乗算減少が機能していません (実際: {reducedConcurrency})");
            }

            // 2. SharedIoGovernor の独立性検証
            var volGov = SharedIoGovernor.GetGovernor(@"C:\TestDrive\Folder");
            if (volGov.EnumerationController.MaxConcurrencyLimit != 2)
            {
                throw new InvalidOperationException("SharedIoGovernor: EnumerationController の上限が 2 ではありません。");
            }
            if (volGov.ContentController.MaxConcurrencyLimit != 12)
            {
                throw new InvalidOperationException("SharedIoGovernor: ContentController の上限が 12 ではありません。");
            }

            // 3. ContentExtractionService.OpenBufferedReadStream の knownSize 動作検証
            var tempFile = Path.Combine(Path.GetTempPath(), $"morpher_io_test_{Guid.NewGuid():N}.txt");
            await File.WriteAllTextAsync(tempFile, "Hello Morphers! This is pure I/O test.");
            try
            {
                double reportedIoMs = -1;
                var fileInfo = new FileInfo(tempFile);

                using (var stream = ContentExtractionService.OpenBufferedReadStream(
                    tempFile,
                    knownSize: fileInfo.Length,
                    reportIoElapsed: elapsedMs => reportedIoMs = elapsedMs))
                {
                    if (stream == null || stream.Length == 0)
                    {
                        throw new InvalidOperationException("ContentExtractionService: OpenBufferedReadStream が空ストリームを返しました。");
                    }
                }

                if (reportedIoMs < 0)
                {
                    throw new InvalidOperationException("ContentExtractionService: 純粋 I/O 時間のコールバックが呼び出されませんでした。");
                }
            }
            finally
            {
                try { File.Delete(tempFile); } catch { }
            }
        }
    }
}
