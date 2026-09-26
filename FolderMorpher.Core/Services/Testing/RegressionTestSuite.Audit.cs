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
        /// [DOMAIN 4/7] Audit & Hygiene 統合テスト
        /// </summary>
        /// <summary>
        /// [DOMAIN 4/7] Audit & Hygiene 統合テスト
        /// </summary>
        public static async Task TestDomain_AuditAndHygieneAsync()
        {
            TestAuditOriginalProtectionAndHashVerification();
            await TestDuplicateGroupingAndOuHierarchyAsync();
            await TestSmartOriginalCandidateScoringAsync();
            TestAuditSmartSelectAndSafePermanentDeletion();
            TestAuditHierarchicalSizeSortingWithDuplicateGroups();
            await TestHeadTailHashAndBandwidthLimiterAsync();
            await TestDormantExclusionWithRecentAccessAsync();
            await TestFolderExclusionInAuditAsync();
            await TestHygieneCandidateDiscoveryAsync();
        }

        /// <summary>
        /// Audit: 原本候補の絶対保護（削除拒否）＆ 削除直前SHA-256再照合（誤削除ゼロ保証）の検証
        /// </summary>
        public static void TestAuditOriginalProtectionAndHashVerification()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_AuditSafe_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                byte[] contentA = new byte[8192];
                new Random(42).NextBytes(contentA);

                string origPath = Path.Combine(tempDir, "original.dat");
                string dupPath1 = Path.Combine(tempDir, "copy1.dat");
                string dupPath2 = Path.Combine(tempDir, "copy2.dat");

                File.WriteAllBytes(origPath, contentA);
                File.WriteAllBytes(dupPath1, contentA);
                File.WriteAllBytes(dupPath2, contentA);

                // 1. 原本候補の直接投入 ➔ ExecutePlan で確実に拒否され、原本ファイルが保護されること
                var origPlan = new AuditCleanupPlan
                {
                    FullPath = origPath,
                    Size = contentA.Length,
                    IsOriginalCandidate = true,
                    OriginalCandidatePath = origPath
                };
                var origResult = AuditCleanupService.ExecutePlan(new[] { origPlan });
                if (origResult.SuccessCount != 0 || origResult.Errors.Count != 1)
                {
                    throw new InvalidOperationException($"原本候補の削除拒否に失敗しました。Success: {origResult.SuccessCount}, Errors: {origResult.Errors.Count}");
                }
                if (!File.Exists(origPath))
                {
                    throw new InvalidOperationException("重大欠陥: 原本候補ファイルが削除されてしまいました！");
                }

                // 2. 正常な重複ファイル削除 ➔ 直前SHA-256照合が一致し、正常に削除されること
                var dup1Plan = new AuditCleanupPlan
                {
                    FullPath = dupPath1,
                    Size = contentA.Length,
                    IsOriginalCandidate = false,
                    OriginalCandidatePath = origPath
                };
                var dup1Result = AuditCleanupService.ExecutePlan(new[] { dup1Plan });
                if (dup1Result.SuccessCount != 1 || dup1Result.Errors.Count != 0)
                {
                    throw new InvalidOperationException($"正常な重複ファイルの削除に失敗しました。Errors: {string.Join(", ", dup1Result.Errors)}");
                }
                if (File.Exists(dupPath1))
                {
                    throw new InvalidOperationException("重複ファイル copy1.dat が削除されていません。");
                }
                if (!File.Exists(origPath))
                {
                    throw new InvalidOperationException("原本ファイル original.dat が巻き添えで削除されてしまいました！");
                }

                // 3. 走査後のファイル改ざん/内容変更 ➔ 直前SHA-256不一致を検知して削除中止（誤削除ゼロ保証）
                byte[] modifiedContent = new byte[8192];
                new Random(99).NextBytes(modifiedContent);
                File.WriteAllBytes(dupPath2, modifiedContent); // copy2の中身が走査後に更新されたと仮定

                var dup2Plan = new AuditCleanupPlan
                {
                    FullPath = dupPath2,
                    Size = contentA.Length,
                    IsOriginalCandidate = false,
                    OriginalCandidatePath = origPath
                };
                var dup2Result = AuditCleanupService.ExecutePlan(new[] { dup2Plan });
                if (dup2Result.SuccessCount != 0 || dup2Result.Errors.Count != 1)
                {
                    throw new InvalidOperationException($"内容不一致の重複ファイル削除が阻止されませんでした。Success: {dup2Result.SuccessCount}");
                }
                if (!File.Exists(dupPath2))
                {
                    throw new InvalidOperationException("重大欠陥: 内容が不一致になったファイルが誤削除されました！");
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        /// <summary>
        /// 4. MFT Data Run Decoder の LCN 二重加算バグ:
        /// 合成 Runlist で最初の LCN が 0 基準で正しく計算されること。
        /// </summary>

        public static async Task TestDuplicateGroupingAndOuHierarchyAsync()
        {
            // 1. 重複ファイル色分け・グルーピングの検証
            var auditService = new AuditReportService();
            string tempDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_DupGroup_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // グループ1 (サイズ 120KB) 2ファイル
                byte[] contentA = new byte[120 * 1024];
                new Random(42).NextBytes(contentA);
                File.WriteAllBytes(Path.Combine(tempDir, "original_A.dat"), contentA);
                File.WriteAllBytes(Path.Combine(tempDir, "copy_A.dat"), contentA);

                // グループ2 (サイズ 150KB) 2ファイル
                byte[] contentB = new byte[150 * 1024];
                new Random(84).NextBytes(contentB);
                File.WriteAllBytes(Path.Combine(tempDir, "original_B.dat"), contentB);
                File.WriteAllBytes(Path.Combine(tempDir, "copy_B.dat"), contentB);

                var options = new AuditOptions
                {
                    TargetDirectory = tempDir,
                    CheckDuplicates = true,
                    CheckDormant = false,
                    CheckPathLimits = false,
                    MinFileSizeBytes = 50 * 1024
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var (summary, items) = await auditService.RunAuditAsync(options, null, cts.Token);

                if (summary.DuplicateCount != 2)
                    throw new InvalidOperationException($"DuplicateCount expected 2, got {summary.DuplicateCount}");

                var dupItems = items.Where(i => i.IssueType == AuditIssueType.Duplicate).ToList();
                if (dupItems.Count != 4)
                    throw new InvalidOperationException($"Duplicate items count expected 4, got {dupItems.Count}");

                var group1 = dupItems.Where(i => i.DuplicateGroupIndex == 1).ToList();
                var group2 = dupItems.Where(i => i.DuplicateGroupIndex == 2).ToList();
                if (group1.Count != 2 || group2.Count != 2)
                    throw new InvalidOperationException("Duplicate grouping index assignment failed");

                // 隣接グループで色インデックスが異なり、各行に有効なHEX背景色が設定されているか
                if (group1[0].DuplicateGroupColorIndex == group2[0].DuplicateGroupColorIndex)
                    throw new InvalidOperationException("Adjacent duplicate groups must have distinct cyclic color indexes");

                if (string.IsNullOrEmpty(AuditReportPalette.RowBackground(group1[0])) || AuditReportPalette.RowBackground(group1[0]) == "Transparent")
                    throw new InvalidOperationException("Duplicate item RowBackgroundHex should be non-transparent pastel color");

                // 原本候補が先頭に並んでいるか
                if (!group1[0].IsOriginalCandidate || group1[1].IsOriginalCandidate)
                    throw new InvalidOperationException("Original candidate should be sorted first in each duplicate group");

                // Excel出力の検証
                string excelOut = Path.Combine(tempDir, "test_dup_report.xlsx");
                var excelService = new ExcelReportService();
                excelService.GenerateComprehensiveReport(excelOut, tempDir, summary, items, null, null);
                if (!File.Exists(excelOut))
                    throw new InvalidOperationException("Excel report generation with duplicate group colors failed");

                // 2. Active Directory / ローカル OU 階層ツリー構築の検証
                var adService = new ActiveDirectoryService();
                var ouRoots = await adService.GetOuHierarchyAsync();
                if (ouRoots == null || ouRoots.Count == 0)
                    throw new InvalidOperationException("OU hierarchy tree returned empty list");

                var root = ouRoots[0];
                if (string.IsNullOrEmpty(root.Name) || root.Children.Count == 0)
                    throw new InvalidOperationException("OU root node should have name and children");

                // ローカルまたはADコンテナのプリンシパル取得検証
                var firstChild = root.Children[0];
                var principals = await adService.GetPrincipalsInOuAsync(firstChild.DistinguishedName);
                if (principals == null)
                    throw new InvalidOperationException("GetPrincipalsInOuAsync returned null");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// 14. Audit: インテリジェント原本選定スコアリング（コピーキーワード除外・浅い階層優先）の検証
        /// </summary>

        public static async Task TestSmartOriginalCandidateScoringAsync()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "fm_test_smart_orig_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string subDir = Path.Combine(tempDir, "sub");
                Directory.CreateDirectory(subDir);

                byte[] dummyContent = new byte[120 * 1024];
                new Random(42).NextBytes(dummyContent);

                // 4つの同一ファイルを意図的に異なる名前・階層で作成
                // 1. root / report - コピー.txt (コピーキーワードあり)
                // 2. sub / report.txt (コピーキーワードなしだが階層が深い)
                // 3. root / report (1).txt (コピーキーワードあり)
                // 4. root / report.txt (原本の本命: キーワードなし & ルート直下で最浅)
                File.WriteAllBytes(Path.Combine(tempDir, "report - コピー.txt"), dummyContent);
                File.WriteAllBytes(Path.Combine(subDir, "report.txt"), dummyContent);
                File.WriteAllBytes(Path.Combine(tempDir, "report (1).txt"), dummyContent);
                File.WriteAllBytes(Path.Combine(tempDir, "report.txt"), dummyContent);

                var auditService = new AuditReportService();
                var options = new AuditOptions
                {
                    TargetDirectory = tempDir,
                    CheckDuplicates = true,
                    CheckDormant = false,
                    CheckPathLimits = false,
                    MinFileSizeBytes = 10 * 1024
                };

                var (summary, items) = await auditService.RunAuditAsync(options, null, CancellationToken.None);
                var dupItems = items.Where(i => i.IssueType == AuditIssueType.Duplicate).ToList();

                if (dupItems.Count != 4)
                    throw new InvalidOperationException($"Expected 4 duplicate items, but found {dupItems.Count}");

                var original = dupItems.FirstOrDefault(i => i.IsOriginalCandidate);
                if (original == null)
                    throw new InvalidOperationException("No original candidate was selected in duplicate group");

                // 原本は root/report.txt であるべき
                string expectedOriginal = Path.Combine(tempDir, "report.txt");
                if (!string.Equals(original.FullPath, expectedOriginal, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Original candidate mismatch. Expected: {expectedOriginal}, Actual: {original.FullPath}");
                }

                // ほかの3件は IsOriginalCandidate == false であるべき
                int originalCount = dupItems.Count(i => i.IsOriginalCandidate);
                if (originalCount != 1)
                {
                    throw new InvalidOperationException($"Expected exactly 1 original candidate, but found {originalCount}");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// 16. Simulation Studio: 移行元ツリーの遅延展開・初期直下展開 & 新サーバーツリーのD&D移動・枠外削除契約
        /// </summary>

        public static void TestAuditSmartSelectAndSafePermanentDeletion()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_AuditDel_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. INotifyPropertyChanged 発火検証
                var testItem = new AuditItem
                {
                    FullPath = Path.Combine(tempDir, "prop_test.txt"),
                    IssueType = AuditIssueType.Duplicate,
                    IsOriginalCandidate = false
                };
                bool propChanged = false;
                testItem.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(AuditItem.IsChecked))
                        propChanged = true;
                };
                testItem.IsChecked = true;
                if (!propChanged)
                    throw new InvalidOperationException("AuditItem.IsChecked must raise PropertyChanged event for UI data binding.");

                // 2. スマート選択ロジック契約検証
                DateTime now = DateTime.Now;
                var items = new List<AuditItem>
                {
                    // 重複グループ（原本 + コピー2件）
                    new AuditItem { FullPath = Path.Combine(tempDir, "orig.pdf"), IssueType = AuditIssueType.Duplicate, IsOriginalCandidate = true, Size = 1000 },
                    new AuditItem { FullPath = Path.Combine(tempDir, "copy1.pdf"), IssueType = AuditIssueType.Duplicate, IsOriginalCandidate = false, Size = 1000 },
                    new AuditItem { FullPath = Path.Combine(tempDir, "copy2.pdf"), IssueType = AuditIssueType.Duplicate, IsOriginalCandidate = false, Size = 1000 },
                    // 休眠ファイル
                    new AuditItem { FullPath = Path.Combine(tempDir, "dormant_4y.xlsx"), IssueType = AuditIssueType.Dormant, LastWriteTime = now.AddYears(-4), Size = 2000 },
                    new AuditItem { FullPath = Path.Combine(tempDir, "dormant_6y.xlsx"), IssueType = AuditIssueType.Dormant, LastWriteTime = now.AddYears(-6), Size = 3000 },
                    new AuditItem { FullPath = Path.Combine(tempDir, "recent.xlsx"), IssueType = AuditIssueType.Dormant, LastWriteTime = now.AddYears(-1), Size = 500 },
                    // 長大パス
                    new AuditItem { FullPath = Path.Combine(tempDir, "long_path.txt"), IssueType = AuditIssueType.PathTooLong, Size = 100 }
                };

                // A) 重複の原本以外（DupCopyOnly）
                foreach (var ai in items)
                {
                    ai.IsChecked = (ai.IssueType == AuditIssueType.Duplicate && !ai.IsOriginalCandidate);
                }
                if (items[0].IsChecked)
                    throw new InvalidOperationException("DupCopyOnly must NOT check original candidate (orig.pdf).");
                if (!items[1].IsChecked || !items[2].IsChecked)
                    throw new InvalidOperationException("DupCopyOnly must check non-original duplicates (copy1, copy2).");
                if (items[3].IsChecked || items[4].IsChecked || items[5].IsChecked || items[6].IsChecked)
                    throw new InvalidOperationException("DupCopyOnly must not check dormant or long path items.");

                // B) 3年超休眠（Dormant3Y）
                DateTime threshold3Y = now.AddYears(-3);
                foreach (var ai in items)
                {
                    ai.IsChecked = (ai.IssueType == AuditIssueType.Dormant && ai.LastWriteTime <= threshold3Y);
                }
                if (!items[3].IsChecked || !items[4].IsChecked)
                    throw new InvalidOperationException("Dormant3Y must check 4y and 6y dormant items.");
                if (items[5].IsChecked)
                    throw new InvalidOperationException("Dormant3Y must NOT check 1y dormant item.");
                if (items[0].IsChecked || items[1].IsChecked)
                    throw new InvalidOperationException("Dormant3Y must NOT check duplicate items.");

                // C) 5年超休眠（Dormant5Y）
                DateTime threshold5Y = now.AddYears(-5);
                foreach (var ai in items)
                {
                    ai.IsChecked = (ai.IssueType == AuditIssueType.Dormant && ai.LastWriteTime <= threshold5Y);
                }
                if (!items[4].IsChecked)
                    throw new InvalidOperationException("Dormant5Y must check 6y dormant item.");
                if (items[3].IsChecked || items[5].IsChecked)
                    throw new InvalidOperationException("Dormant5Y must NOT check 4y or 1y dormant items.");

                // D) 全解除（ClearAll）
                foreach (var ai in items) ai.IsChecked = false;
                if (items.Any(i => i.IsChecked))
                    throw new InvalidOperationException("ClearAll must uncheck all items.");

                // 3. 🔴 High検証: 原本候補が「休眠ファイル経由」で選択された場合の原本保護契約
                // 同一ファイル "dormant_and_orig.xlsx" が Dormant 行と Duplicate(原本) 行の2行を持つ
                string multiRolePath = Path.Combine(tempDir, "dormant_and_orig.xlsx");
                var multiRoleDormant = new AuditItem
                {
                    FullPath = multiRolePath,
                    FileName = "dormant_and_orig.xlsx",
                    IssueType = AuditIssueType.Dormant,
                    LastWriteTime = now.AddYears(-6),
                    IsOriginalCandidate = false, // Dormant側はfalse
                    Size = 5000
                };
                var multiRoleDuplicate = new AuditItem
                {
                    FullPath = multiRolePath,
                    FileName = "dormant_and_orig.xlsx",
                    IssueType = AuditIssueType.Duplicate,
                    LastWriteTime = now.AddYears(-6),
                    IsOriginalCandidate = true, // Duplicate側で原本候補
                    Size = 5000
                };
                var auditItems = new List<AuditItem> { multiRoleDormant, multiRoleDuplicate };

                // ユーザーが「5年超休眠を一括選択」を実施（Dormant行のみがチェックされる）
                multiRoleDormant.IsChecked = true;
                multiRoleDuplicate.IsChecked = false;

                // AuditCleanupService.BuildPlan による実行計画立案
                var plans = AuditCleanupService.BuildPlan(auditItems);
                if (plans.Count != 1)
                    throw new InvalidOperationException($"Expected 1 unique plan for same FullPath, got {plans.Count}");

                // 休眠行経由の選択であっても、全監査正本から原本候補（IsOriginalCandidate == true）と判定されなければならない！
                if (!plans[0].IsOriginalCandidate)
                    throw new InvalidOperationException("HIGH BUG REPRODUCED: Dormant item selection bypassed IsOriginalCandidate protection!");

                // 原本除外アクションシミュレーション
                if (plans[0].IsOriginalCandidate)
                {
                    foreach (var ai in plans[0].AssociatedItems)
                    {
                        ai.IsChecked = false;
                    }
                    plans.RemoveAll(p => p.IsOriginalCandidate);
                }
                if (plans.Count != 0 || multiRoleDormant.IsChecked || multiRoleDuplicate.IsChecked)
                    throw new InvalidOperationException("Excluding original candidate must uncheck associated dormant item and clear plans.");

                // 4. 🟠 Medium-High検証: 同一 FullPath の複数行選択時の多重加算防止 & 全関連行の一括除去契約
                string multiRowCopyPath = Path.Combine(tempDir, "multi_row_copy.dat");
                File.WriteAllBytes(multiRowCopyPath, new byte[4096]);
                var copyDormant = new AuditItem { FullPath = multiRowCopyPath, FileName = "multi_row_copy.dat", IssueType = AuditIssueType.Dormant, Size = 4096, IsOriginalCandidate = false, IsChecked = true };
                var copyDuplicate = new AuditItem { FullPath = multiRowCopyPath, FileName = "multi_row_copy.dat", IssueType = AuditIssueType.Duplicate, Size = 4096, IsOriginalCandidate = false, IsChecked = true };
                var copyPathLimit = new AuditItem { FullPath = multiRowCopyPath, FileName = "multi_row_copy.dat", IssueType = AuditIssueType.PathTooLong, Size = 4096, IsOriginalCandidate = false, IsChecked = false };

                var multiRowList = new List<AuditItem> { copyDormant, copyDuplicate, copyPathLimit };
                var multiPlans = AuditCleanupService.BuildPlan(multiRowList);

                if (multiPlans.Count != 1)
                    throw new InvalidOperationException($"Multiple checked items with same FullPath must be grouped into 1 plan, got {multiPlans.Count}");
                if (multiPlans[0].Size != 4096 || multiPlans.Sum(p => p.Size) != 4096)
                    throw new InvalidOperationException("Duplicate rows must not double-count freed size in execution plan.");

                var execResult = AuditCleanupService.ExecutePlan(multiPlans);
                if (execResult.SuccessCount != 1 || execResult.FreedBytes != 4096)
                    throw new InvalidOperationException($"Execution plan must execute 1 delete and report 4096 bytes freed. Got {execResult.SuccessCount} deletes, {execResult.FreedBytes} bytes.");
                if (File.Exists(multiRowCopyPath))
                    throw new InvalidOperationException("File must be deleted after ExecutePlan.");

                // 全関連行（Dormant, Duplicate, 未チェックのPathTooLongすべて）が FullPath 一致で除去されること
                multiRowList.RemoveAll(x => execResult.DeletedPaths.Contains(x.FullPath));
                if (multiRowList.Count != 0)
                    throw new InvalidOperationException($"All associated AuditItems for deleted FullPath must be removed from list. Remaining: {multiRowList.Count}");

                // 5. 🟡 Medium検証: ReadOnly属性の解除 & 削除失敗時の属性復元ロールバック契約
                string lockedReadOnlyFile = Path.Combine(tempDir, "locked_readonly.dat");
                File.WriteAllBytes(lockedReadOnlyFile, new byte[1024]);
                File.SetAttributes(lockedReadOnlyFile, File.GetAttributes(lockedReadOnlyFile) | FileAttributes.ReadOnly);

                // 排他ロックをかけて削除を強制失敗させる
                using (var fs = new FileStream(lockedReadOnlyFile, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    var lockedItem = new AuditItem { FullPath = lockedReadOnlyFile, FileName = "locked_readonly.dat", Size = 1024, IsChecked = true };
                    var failPlans = AuditCleanupService.BuildPlan(new[] { lockedItem });
                    var failResult = AuditCleanupService.ExecutePlan(failPlans);

                    if (failResult.Errors.Count != 1)
                        throw new InvalidOperationException("Expected 1 failure error on locked file.");
                }

                // ロック解放後、削除失敗したファイルの ReadOnly 属性が安全に復元されていることを検証
                var attrsAfterFail = File.GetAttributes(lockedReadOnlyFile);
                if ((attrsAfterFail & FileAttributes.ReadOnly) == 0)
                    throw new InvalidOperationException("MEDIUM BUG: FileAttributes.ReadOnly was not restored after deletion failure!");

                // ロック解放後に通常削除できることを検証
                var finalPlans = AuditCleanupService.BuildPlan(new[] { new AuditItem { FullPath = lockedReadOnlyFile, FileName = "locked_readonly.dat", Size = 1024, IsChecked = true } });
                var finalResult = AuditCleanupService.ExecutePlan(finalPlans);
                if (finalResult.SuccessCount != 1 || File.Exists(lockedReadOnlyFile))
                    throw new InvalidOperationException("Final cleanup of unlocked file failed.");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// 18. Audit & Hygiene: 容量ソート時の重複グループ一括束ね ＆ 原本最優先整列の検証
        /// </summary>

        public static void TestAuditHierarchicalSizeSortingWithDuplicateGroups()
        {
            var dorm10G = new AuditItem { FileName = "dormant_10g.iso", Size = 10_000_000_000L, IssueType = AuditIssueType.Dormant, DuplicateGroupIndex = 0, IsOriginalCandidate = false };
            var dup1Copy1 = new AuditItem { FileName = "dup1_copy1.zip", Size = 5_000_000_000L, IssueType = AuditIssueType.Duplicate, DuplicateGroupIndex = 1, IsOriginalCandidate = false };
            var dup1Orig = new AuditItem { FileName = "dup1_master.zip", Size = 5_000_000_000L, IssueType = AuditIssueType.Duplicate, DuplicateGroupIndex = 1, IsOriginalCandidate = true };
            var dup1Copy2 = new AuditItem { FileName = "dup1_copy2.zip", Size = 5_000_000_000L, IssueType = AuditIssueType.Duplicate, DuplicateGroupIndex = 1, IsOriginalCandidate = false };
            var dorm3G = new AuditItem { FileName = "dormant_3g.vhdx", Size = 3_000_000_000L, IssueType = AuditIssueType.Dormant, DuplicateGroupIndex = 0, IsOriginalCandidate = false };
            var dup2Copy1 = new AuditItem { FileName = "dup2_copy.pdf", Size = 1_000_000_000L, IssueType = AuditIssueType.Duplicate, DuplicateGroupIndex = 2, IsOriginalCandidate = false };
            var dup2Orig = new AuditItem { FileName = "dup2_master.pdf", Size = 1_000_000_000L, IssueType = AuditIssueType.Duplicate, DuplicateGroupIndex = 2, IsOriginalCandidate = true };
            var dorm500M = new AuditItem { FileName = "dormant_500m.dat", Size = 500_000_000L, IssueType = AuditIssueType.Dormant, DuplicateGroupIndex = 0, IsOriginalCandidate = false };

            // わざとバラバラ順序で投入
            var rawList = new List<AuditItem>
            {
                dup1Copy2, dorm3G, dup2Copy1, dorm10G, dup1Copy1, dorm500M, dup2Orig, dup1Orig
            };

            // 1. 降順ソート検証 (Descending)
            var descSorted = AuditReportService.SortAuditItems(rawList, "Size", descending: true);
            if (descSorted.Count != 8)
                throw new InvalidOperationException($"Expected 8 items, got {descSorted.Count}");

            if (descSorted[0].FileName != "dormant_10g.iso")
                throw new InvalidOperationException($"[0] Expected dormant_10g.iso, got {descSorted[0].FileName}");

            // 重複グループ1 (5GB): 原本が先頭で、コピー2件が同一セットで連続すること
            if (descSorted[1].FileName != "dup1_master.zip" || !descSorted[1].IsOriginalCandidate || descSorted[1].DuplicateGroupIndex != 1)
                throw new InvalidOperationException($"[1] Expected dup1_master.zip as original at top of group 1, got {descSorted[1].FileName}");
            if (descSorted[2].DuplicateGroupIndex != 1 || descSorted[3].DuplicateGroupIndex != 1)
                throw new InvalidOperationException("Duplicate group 1 items must stay strictly together!");

            if (descSorted[4].FileName != "dormant_3g.vhdx")
                throw new InvalidOperationException($"[4] Expected dormant_3g.vhdx, got {descSorted[4].FileName}");

            // 重複グループ2 (1GB): 原本が先頭で、コピーが連続すること
            if (descSorted[5].FileName != "dup2_master.pdf" || !descSorted[5].IsOriginalCandidate || descSorted[5].DuplicateGroupIndex != 2)
                throw new InvalidOperationException($"[5] Expected dup2_master.pdf as original at top of group 2, got {descSorted[5].FileName}");
            if (descSorted[6].DuplicateGroupIndex != 2)
                throw new InvalidOperationException("Duplicate group 2 copy must follow immediately after original!");

            if (descSorted[7].FileName != "dormant_500m.dat")
                throw new InvalidOperationException($"[7] Expected dormant_500m.dat, got {descSorted[7].FileName}");

            // 2. 昇順ソート検証 (Ascending)
            var ascSorted = AuditReportService.SortAuditItems(rawList, "Size", descending: false);
            if (ascSorted[0].FileName != "dormant_500m.dat")
                throw new InvalidOperationException($"[Asc 0] Expected dormant_500m.dat, got {ascSorted[0].FileName}");

            // 重複グループ2 (1GB): 原本が先頭
            if (ascSorted[1].FileName != "dup2_master.pdf" || !ascSorted[1].IsOriginalCandidate || ascSorted[1].DuplicateGroupIndex != 2)
                throw new InvalidOperationException($"[Asc 1] Expected dup2_master.pdf as original, got {ascSorted[1].FileName}");
            if (ascSorted[2].DuplicateGroupIndex != 2)
                throw new InvalidOperationException("[Asc 2] Duplicate group 2 items must stay together!");

            if (ascSorted[3].FileName != "dormant_3g.vhdx")
                throw new InvalidOperationException($"[Asc 3] Expected dormant_3g.vhdx, got {ascSorted[3].FileName}");

            // 重複グループ1 (5GB): 原本が先頭
            if (ascSorted[4].FileName != "dup1_master.zip" || !ascSorted[4].IsOriginalCandidate || ascSorted[4].DuplicateGroupIndex != 1)
                throw new InvalidOperationException($"[Asc 4] Expected dup1_master.zip as original, got {ascSorted[4].FileName}");
            if (ascSorted[5].DuplicateGroupIndex != 1 || ascSorted[6].DuplicateGroupIndex != 1)
                throw new InvalidOperationException("[Asc 5,6] Duplicate group 1 items must stay together!");

            if (ascSorted[7].FileName != "dormant_10g.iso")
                throw new InvalidOperationException($"[Asc 7] Expected dormant_10g.iso, got {ascSorted[7].FileName}");
        }


        private static async Task TestHeadTailHashAndBandwidthLimiterAsync()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_Regression_Test30_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                // 1. Head-Tail ハッシュの判定検証 (1MB超ファイル)
                // サイズ: 1.5MB (1,572,864 bytes)
                const int fileSize = 1572864;
                byte[] dataA = new byte[fileSize];
                byte[] dataB = new byte[fileSize]; // 先頭違い
                byte[] dataC = new byte[fileSize]; // 末尾違い
                byte[] dataD = new byte[fileSize]; // Aと同一

                // 先頭4KBにシグネチャ
                System.Text.Encoding.ASCII.GetBytes("HEAD_A").CopyTo(dataA, 0);
                System.Text.Encoding.ASCII.GetBytes("HEAD_B").CopyTo(dataB, 0);
                System.Text.Encoding.ASCII.GetBytes("HEAD_A").CopyTo(dataC, 0);
                System.Text.Encoding.ASCII.GetBytes("HEAD_A").CopyTo(dataD, 0);

                // 末尾4KBにシグネチャ
                int tailPos = fileSize - 100;
                System.Text.Encoding.ASCII.GetBytes("TAIL_COMMON").CopyTo(dataA, tailPos);
                System.Text.Encoding.ASCII.GetBytes("TAIL_COMMON").CopyTo(dataB, tailPos);
                System.Text.Encoding.ASCII.GetBytes("TAIL_DIFFERENT").CopyTo(dataC, tailPos);
                System.Text.Encoding.ASCII.GetBytes("TAIL_COMMON").CopyTo(dataD, tailPos);

                string fileA = Path.Combine(testDir, "fileA.dat");
                string fileB = Path.Combine(testDir, "fileB.dat");
                string fileC = Path.Combine(testDir, "fileC.dat");
                string fileD = Path.Combine(testDir, "fileD.dat");

                await File.WriteAllBytesAsync(fileA, dataA);
                await File.WriteAllBytesAsync(fileB, dataB);
                await File.WriteAllBytesAsync(fileC, dataC);
                await File.WriteAllBytesAsync(fileD, dataD);

                var ct = CancellationToken.None;
                var hashA = await AuditReportService.ComputeHeadTailHashAsync(fileA, fileSize, ct);
                var hashB = await AuditReportService.ComputeHeadTailHashAsync(fileB, fileSize, ct);
                var hashC = await AuditReportService.ComputeHeadTailHashAsync(fileC, fileSize, ct);
                var hashD = await AuditReportService.ComputeHeadTailHashAsync(fileD, fileSize, ct);

                if (string.IsNullOrEmpty(hashA) || string.IsNullOrEmpty(hashB) || string.IsNullOrEmpty(hashC) || string.IsNullOrEmpty(hashD))
                    throw new InvalidOperationException("HeadTailHash returned null for test files.");

                if (hashA != hashD)
                    throw new InvalidOperationException($"HeadTailHash mismatch for identical head-tail files: {hashA} != {hashD}");
                if (hashA == hashB)
                    throw new InvalidOperationException("HeadTailHash failed to distinguish different head content.");
                if (hashA == hashC)
                    throw new InvalidOperationException("HeadTailHash failed to distinguish different tail content.");

                // 2. 1MB未満ファイルの直接照合ファイルも作成
                byte[] smallData1 = new byte[200 * 1024];
                byte[] smallData2 = new byte[200 * 1024];
                new Random(42).NextBytes(smallData1);
                Array.Copy(smallData1, smallData2, smallData1.Length);

                string smallFile1 = Path.Combine(testDir, "small1.bin");
                string smallFile2 = Path.Combine(testDir, "small2.bin");
                await File.WriteAllBytesAsync(smallFile1, smallData1);
                await File.WriteAllBytesAsync(smallFile2, smallData2);

                // 3. AuditReportService.RunAuditAsync 実行検証 (Standard50MB)
                var auditService = new AuditReportService();
                var options = new AuditOptions
                {
                    TargetDirectory = testDir,
                    CheckDuplicates = true,
                    CheckDormant = false,
                    CheckPathLimits = false,
                    MinFileSizeBytes = 100 * 1024,
                    BandwidthLimit = AuditBandwidthLimit.Standard50MB
                };

                var (summary, items) = await auditService.RunAuditAsync(options, null, ct);

                // 重複グループ数は2 (fileA+fileD と small1+small2)
                var dupItems = items.Where(i => i.IssueType == AuditIssueType.Duplicate).ToList();
                var dupGroups = dupItems.GroupBy(i => i.DuplicateGroupId).ToList();
                if (dupGroups.Count != 2)
                    throw new InvalidOperationException($"Expected exactly 2 duplicate groups, got {dupGroups.Count}");

                var largeGroup = dupGroups.FirstOrDefault(g => g.Any(i => i.FileName == "fileA.dat"));
                if (largeGroup == null || largeGroup.Count() != 2)
                    throw new InvalidOperationException("Large duplicate group (fileA & fileD) was not detected correctly.");

                // fileB と fileC は重複に含まれていないこと
                if (dupItems.Any(i => i.FileName == "fileB.dat" || i.FileName == "fileC.dat"))
                    throw new InvalidOperationException("Non-duplicate files (fileB or fileC) were erroneously marked as duplicates.");

                // 4. BandwidthThrottler の単体挙動検証
                var throttler = new BandwidthThrottler(10 * 1024 * 1024); // 10MB/s
                if (throttler.BytesPerSecond != 10 * 1024 * 1024)
                    throw new InvalidOperationException("BandwidthThrottler BytesPerSecond mismatch.");
                await throttler.ThrottleAsync(1024, ct); // 微小バイトは遅延なしで通過

                // 5. 英語モード時の CJK ゼロ検証
                var origLang = LocalizationService.Instance.CurrentLanguage;
                try
                {
                    LocalizationService.Instance.SetLanguage(AppLanguage.English);
                    var japaneseRegex = new System.Text.RegularExpressions.Regex(@"[\p{IsCJKUnifiedIdeographs}\p{IsHiragana}\p{IsKatakana}]");

                    if (japaneseRegex.IsMatch(Strings.AuditBandwidthLimitLabel))
                        throw new InvalidOperationException($"English AuditBandwidthLimitLabel contains Japanese: '{Strings.AuditBandwidthLimitLabel}'");
                    if (japaneseRegex.IsMatch(Strings.AuditBandwidthStandard))
                        throw new InvalidOperationException($"English AuditBandwidthStandard contains Japanese: '{Strings.AuditBandwidthStandard}'");
                    if (japaneseRegex.IsMatch(Strings.AuditBandwidthUnlimited))
                        throw new InvalidOperationException($"English AuditBandwidthUnlimited contains Japanese: '{Strings.AuditBandwidthUnlimited}'");
                    if (japaneseRegex.IsMatch(Strings.AuditProgressQuickHash))
                        throw new InvalidOperationException($"English AuditProgressQuickHash contains Japanese: '{Strings.AuditProgressQuickHash}'");
                    if (japaneseRegex.IsMatch(Strings.AuditProgressFullHash))
                        throw new InvalidOperationException($"English AuditProgressFullHash contains Japanese: '{Strings.AuditProgressFullHash}'");
                }
                finally
                {
                    LocalizationService.Instance.SetLanguage(origLang);
                }
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }

        private static async Task TestDormantExclusionWithRecentAccessAsync()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_Dormant_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                var auditService = new AuditReportService();
                var now = DateTime.Now;

                // 1. 完全休眠ファイル (更新4年前、アクセス4年前)
                string dormantFile = Path.Combine(testDir, "dormant_4y.pdf");
                await File.WriteAllBytesAsync(dormantFile, new byte[1024]);
                File.SetLastWriteTime(dormantFile, now.AddYears(-4));
                File.SetLastAccessTime(dormantFile, now.AddYears(-4));

                // 2. 参照中ファイル (更新4年前だが、アクセス半年前 = 過去1年以内)
                string referencedFile = Path.Combine(testDir, "referenced_manual_4y.pdf");
                await File.WriteAllBytesAsync(referencedFile, new byte[2048]);
                File.SetLastWriteTime(referencedFile, now.AddYears(-4));
                File.SetLastAccessTime(referencedFile, now.AddMonths(-6));

                // 3. 通常の新規ファイル (更新半年前)
                string activeFile = Path.Combine(testDir, "recent.pdf");
                await File.WriteAllBytesAsync(activeFile, new byte[512]);
                File.SetLastWriteTime(activeFile, now.AddMonths(-6));
                File.SetLastAccessTime(activeFile, now.AddMonths(-6));

                var options = new AuditOptions
                {
                    TargetDirectory = testDir,
                    CheckDuplicates = false,
                    CheckDormant = true,
                    CheckPathLimits = false,
                    DormantYearsThreshold = 3.0
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var (summary, items) = await auditService.RunAuditAsync(options, null, cts.Token);

                // 完全休眠ファイルのみが抽出され、過去1年以内に閲覧されたファイルは除外されていること
                var dormantItems = items.Where(i => i.IssueType == AuditIssueType.Dormant).ToList();
                if (dormantItems.Count != 1)
                {
                    throw new InvalidOperationException($"Expected 1 dormant item, got {dormantItems.Count}");
                }

                if (dormantItems[0].FileName != "dormant_4y.pdf")
                {
                    throw new InvalidOperationException($"Expected dormant_4y.pdf to be detected, got {dormantItems[0].FileName}");
                }

                if (items.Any(i => i.FileName == "referenced_manual_4y.pdf"))
                {
                    throw new InvalidOperationException("referenced_manual_4y.pdf was accessed within 1 year and MUST NOT be flagged as dormant.");
                }
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }

        public static async Task TestFolderExclusionInAuditAsync()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_FolderExclusion_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                var auditService = new AuditReportService();
                var now = DateTime.Now;

                // 1. 通常フォルダー (走査対象)
                string normalDir = Path.Combine(testDir, "NormalDocs");
                Directory.CreateDirectory(normalDir);
                string normalFile = Path.Combine(normalDir, "doc1.pdf");
                await File.WriteAllBytesAsync(normalFile, new byte[1024]);
                File.SetLastWriteTime(normalFile, now.AddYears(-4));
                File.SetLastAccessTime(normalFile, now.AddYears(-4));

                // 2. 除外対象フォルダー (node_modules: 完全一致パターン)
                string nodeDir = Path.Combine(testDir, "node_modules");
                Directory.CreateDirectory(nodeDir);
                string nodeFile = Path.Combine(nodeDir, "package_dormant.json");
                await File.WriteAllBytesAsync(nodeFile, new byte[1024]);
                File.SetLastWriteTime(nodeFile, now.AddYears(-4));
                File.SetLastAccessTime(nodeFile, now.AddYears(-4));

                // 3. 除外対象フォルダー (Project_Backup_2020: "backup" 部分一致)
                string backupDir = Path.Combine(testDir, "Project_Backup_2020");
                Directory.CreateDirectory(backupDir);
                string backupFile = Path.Combine(backupDir, "backup_file.zip");
                await File.WriteAllBytesAsync(backupFile, new byte[2048]);
                File.SetLastWriteTime(backupFile, now.AddYears(-4));
                File.SetLastAccessTime(backupFile, now.AddYears(-4));

                // 4. 深い階層の除外対象フォルダー (Sub\temp_cache: "temp" 部分一致)
                string deepDir = Path.Combine(testDir, "Sub", "temp_cache");
                Directory.CreateDirectory(deepDir);
                string deepFile = Path.Combine(deepDir, "cache.dat");
                await File.WriteAllBytesAsync(deepFile, new byte[512]);
                File.SetLastWriteTime(deepFile, now.AddYears(-4));
                File.SetLastAccessTime(deepFile, now.AddYears(-4));

                var options = new AuditOptions
                {
                    TargetDirectory = testDir,
                    CheckDuplicates = false,
                    CheckDormant = true,
                    CheckPathLimits = false,
                    DormantYearsThreshold = 3.0,
                    ExcludeFolderPatterns = new List<string> { "node_modules", "backup", "temp" }
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var (summary, items) = await auditService.RunAuditAsync(options, null, cts.Token);

                // 除外フォルダー配下のファイルはスキャンすらされず、NormalDocs\doc1.pdf のみ走査・検出されること
                if (summary.TotalFilesScanned != 1)
                {
                    throw new InvalidOperationException($"Expected exactly 1 scanned file, got {summary.TotalFilesScanned}");
                }

                if (items.Count != 1)
                {
                    throw new InvalidOperationException($"Expected 1 audit item, got {items.Count}");
                }

                if (items[0].FileName != "doc1.pdf")
                {
                    throw new InvalidOperationException($"Expected doc1.pdf, got {items[0].FileName}");
                }

                // 除外対象のファイルが一切混入していないこと
                if (items.Any(i => i.FileName == "package_dormant.json" || i.FileName == "backup_file.zip" || i.FileName == "cache.dat"))
                {
                    throw new InvalidOperationException("Excluded folder files were erroneously included in audit results.");
                }
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }

        public static async Task TestHygieneCandidateDiscoveryAsync()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_Hygiene_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                var now = DateTime.Now;

                // 1. 世代・旧版 (VersionFamily) のテストデータ
                // 同一フォルダ内に最新版と過去版
                string vDir = Path.Combine(testDir, "Versions");
                Directory.CreateDirectory(vDir);

                string activeFile = Path.Combine(vDir, "見積書_最終_本当.xlsx");
                string oldFile = Path.Combine(vDir, "見積書_修正版.xlsx");
                await File.WriteAllBytesAsync(activeFile, new byte[2048]);
                await File.WriteAllBytesAsync(oldFile, new byte[2048]);
                File.SetLastWriteTime(activeFile, now.AddDays(-2));
                File.SetLastWriteTime(oldFile, now.AddDays(-30));

                // 2. 展開済みアーカイブ残骸 (ExtractedArchive) のテストデータ
                // 同一フォルダ内に 同名.zip と 同名/ フォルダ
                string arcDir = Path.Combine(testDir, "Archives");
                Directory.CreateDirectory(arcDir);

                string zipFile = Path.Combine(arcDir, "ProjectLogs_2025.zip");
                string extractedDir = Path.Combine(arcDir, "ProjectLogs_2025");
                Directory.CreateDirectory(extractedDir);
                await File.WriteAllBytesAsync(zipFile, new byte[5000]);
                await File.WriteAllBytesAsync(Path.Combine(extractedDir, "log1.txt"), new byte[100]);

                // 3. 墓場フォルダー (GraveyardTree) のテストデータ
                // サブフォルダー配下の全ファイルが3年以上未更新かつ直近1年未アクセス (3件以上 & 1MB以上)
                string graveDir = Path.Combine(testDir, "GraveFolder");
                Directory.CreateDirectory(graveDir);
                string graveFile1 = Path.Combine(graveDir, "old_doc1.pdf");
                string graveFile2 = Path.Combine(graveDir, "old_doc2.pdf");
                string graveFile3 = Path.Combine(graveDir, "old_doc3.pdf");
                await File.WriteAllBytesAsync(graveFile1, new byte[500 * 1024]);
                await File.WriteAllBytesAsync(graveFile2, new byte[500 * 1024]);
                await File.WriteAllBytesAsync(graveFile3, new byte[200 * 1024]);
                File.SetLastWriteTime(graveFile1, now.AddYears(-4));
                File.SetLastAccessTime(graveFile1, now.AddYears(-4));
                File.SetLastWriteTime(graveFile2, now.AddYears(-5));
                File.SetLastAccessTime(graveFile2, now.AddYears(-5));
                File.SetLastWriteTime(graveFile3, now.AddYears(-4));
                File.SetLastAccessTime(graveFile3, now.AddYears(-4));

                // 4. 監査エンジンの実行
                var auditService = new AuditReportService();
                var options = new AuditOptions
                {
                    TargetDirectory = testDir,
                    CheckVersionFamilies = true,
                    CheckExtractedArchives = true,
                    CheckGraveyardTrees = true,
                    CheckDuplicates = false,
                    CheckDormant = false,
                    CheckPathLimits = false,
                    DormantYearsThreshold = 3.0
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var (summary, items) = await auditService.RunAuditAsync(options, null, cts.Token);

                // 検証 1: 世代・旧版
                var vItems = items.Where(i => i.IssueType == AuditIssueType.VersionFamily).ToList();
                if (vItems.Count != 1)
                {
                    throw new InvalidOperationException($"Expected 1 VersionFamily item, got {vItems.Count}");
                }
                if (vItems[0].FileName != "見積書_修正版.xlsx")
                {
                    throw new InvalidOperationException($"Expected 見積書_修正版.xlsx as older version, got {vItems[0].FileName}");
                }
                if (vItems[0].WasteScore < 80 || vItems[0].ConfidenceDisplay != AuditItem.ConfidenceRecommendedJa)
                {
                    throw new InvalidOperationException($"VersionFamily confidence should be '{AuditItem.ConfidenceRecommendedJa}', got '{vItems[0].ConfidenceDisplay}' (score: {vItems[0].WasteScore})");
                }
                if (string.IsNullOrEmpty(vItems[0].RelatedActivePath) || !(vItems[0].RelatedActivePath?.Contains("見積書_最終_本当.xlsx") ?? false))
                {
                    throw new InvalidOperationException($"VersionFamily RelatedActivePath should point to active version, got '{vItems[0].RelatedActivePath}'");
                }

                // 検証 2: 展開済みアーカイブ残骸
                var arcItems = items.Where(i => i.IssueType == AuditIssueType.ExtractedArchive).ToList();
                if (arcItems.Count != 1)
                {
                    throw new InvalidOperationException($"Expected 1 ExtractedArchive item, got {arcItems.Count}");
                }
                if (arcItems[0].FileName != "ProjectLogs_2025.zip")
                {
                    throw new InvalidOperationException($"Expected ProjectLogs_2025.zip as extracted archive, got {arcItems[0].FileName}");
                }
                if (arcItems[0].WasteScore < 80 || arcItems[0].ConfidenceDisplay != AuditItem.ConfidenceRecommendedJa)
                {
                    throw new InvalidOperationException($"ExtractedArchive confidence should be '{AuditItem.ConfidenceRecommendedJa}', got '{arcItems[0].ConfidenceDisplay}' (score: {arcItems[0].WasteScore})");
                }

                // 検証 3: 墓場フォルダー
                var graveItems = items.Where(i => i.IssueType == AuditIssueType.GraveyardTree).ToList();
                if (graveItems.Count != 1)
                {
                    throw new InvalidOperationException($"Expected 1 GraveyardTree item, got {graveItems.Count}");
                }
                if (!graveItems[0].FileName.Contains("GraveFolder"))
                {
                    throw new InvalidOperationException($"Expected GraveFolder as graveyard item, got {graveItems[0].FileName}");
                }
                if (graveItems[0].WasteScore < 80 || graveItems[0].ConfidenceDisplay != AuditItem.ConfidenceRecommendedJa)
                {
                    throw new InvalidOperationException($"GraveyardTree confidence should be '{AuditItem.ConfidenceRecommendedJa}', got '{graveItems[0].ConfidenceDisplay}' (score: {graveItems[0].WasteScore})");
                }
                long expectedGraveSize = (500 + 500 + 200) * 1024;
                if (graveItems[0].Size != expectedGraveSize)
                {
                    throw new InvalidOperationException($"Expected GraveyardTree size to be sum of files ({expectedGraveSize}), got {graveItems[0].Size}");
                }

                // 検証 4: サマリー集計
                if (summary.VersionFamilyCount != 1)
                    throw new InvalidOperationException($"Expected VersionFamilyCount 1, got {summary.VersionFamilyCount}");
                if (summary.ExtractedArchiveCount != 1)
                    throw new InvalidOperationException($"Expected ExtractedArchiveCount 1, got {summary.ExtractedArchiveCount}");
                if (summary.GraveyardTreeCount != 1)
                    throw new InvalidOperationException($"Expected GraveyardTreeCount 1, got {summary.GraveyardTreeCount}");

                // 検証 5: スコア内訳 (ScoreBreakdown) の整合性
                if (vItems[0].ScoreBreakdown.Count == 0 || string.IsNullOrEmpty(vItems[0].ScoreBreakdownSummary))
                {
                    throw new InvalidOperationException("ScoreBreakdown should contain factors and summary should not be empty.");
                }

                // 検証 6: 整理除外（保持マーク）リスト / スキップ記憶機能の検証
                string testFilePath = vItems[0].FullPath;
                long testFileSize = vItems[0].Size;
                DateTime testLastWrite = File.GetLastWriteTimeUtc(testFilePath);

                AuditIgnoreService.Instance.ClearAll();
                if (AuditIgnoreService.Instance.IsIgnored(testFilePath, testFileSize, testLastWrite))
                    throw new InvalidOperationException("File should NOT be ignored initially");

                AuditIgnoreService.Instance.AddIgnore(vItems[0]);
                if (!AuditIgnoreService.Instance.IsIgnored(testFilePath, testFileSize, testLastWrite))
                    throw new InvalidOperationException("File should BE ignored after AddIgnore");

                // Canonical 化による大文字小文字・スラッシュ揺れ吸収の検証
                string variantPath = testFilePath.ToLowerInvariant().Replace('\\', '/');
                if (!AuditIgnoreService.Instance.IsIgnored(variantPath, testFileSize, testLastWrite))
                    throw new InvalidOperationException("File should BE ignored even with lowercase/forward-slash variant path (PathCanonicalizer)");

                // ファイルサイズ変更で自動復帰
                if (AuditIgnoreService.Instance.IsIgnored(testFilePath, testFileSize + 1, testLastWrite))
                    throw new InvalidOperationException("File should NOT be ignored when size changes");

                // 更新日時変更で自動復帰
                if (AuditIgnoreService.Instance.IsIgnored(testFilePath, testFileSize, testLastWrite.AddMinutes(5)))
                    throw new InvalidOperationException("File should NOT be ignored when LastWriteTime changes");

                AuditIgnoreService.Instance.RemoveIgnore(testFilePath);
                if (AuditIgnoreService.Instance.IsIgnored(testFilePath, testFileSize, testLastWrite))
                    throw new InvalidOperationException("File should NOT be ignored after RemoveIgnore");

                // 検証 7: ADR 91 TargetRoot 境界ガードの検証
                // 対象フォルダー配下のみが墓場候補になり、親フォルダーやターゲットルート自体が墓場候補に含まれていないこと
                foreach (var gItem in graveItems)
                {
                    if (!gItem.FullPath.StartsWith(testDir, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Graveyard candidate leaked outside target directory: {gItem.FullPath}");
                    }
                    if (string.Equals(gItem.FullPath.TrimEnd('\\', '/'), testDir.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"Target root itself must never be reported as graveyard candidate: {gItem.FullPath}");
                    }
                }

                // 検証 8: ADR 92 墓場フォルダーの削除事故防止
                // 墓場フォルダーは IsCleanable が false であり、IsChecked に true を代入しても false のまま維持されること
                if (graveItems.Count > 0)
                {
                    var g = graveItems[0];
                    if (g.IsCleanable)
                        throw new InvalidOperationException("GraveyardTree item must have IsCleanable == false.");
                    g.IsChecked = true;
                    if (g.IsChecked)
                        throw new InvalidOperationException("Setting IsChecked to true on GraveyardTree item must be rejected.");

                    // BuildPlan にも含まれないこと
                    var planList = AuditCleanupService.BuildPlan(new[] { g });
                    if (planList.Count > 0)
                        throw new InvalidOperationException("BuildPlan must not include GraveyardTree items.");
                }

                // 検証 9: ADR 92 UpdateTreeCacheSha256Async によるハッシュ書き戻し検証
                var rootNode = new AstraSize.Models.FileItemNode { FullPath = testDir, Name = Path.GetFileName(testDir), IsDirectory = true };
                var childFile = new AstraSize.Models.FileItemNode { FullPath = testFilePath, Name = Path.GetFileName(testFilePath), IsDirectory = false };
                rootNode.Children.Add(childFile);
                var historyService = AstraSize.Services.StorageHistoryService.Instance;
                await historyService.SaveTreeCacheAsync(rootNode);

                var dummyAuditItem = new AuditItem { FullPath = testFilePath, FileName = Path.GetFileName(testFilePath), Sha256Hash = "ABCDEF1234567890" };
                await historyService.UpdateTreeCacheSha256Async(testDir, new[] { dummyAuditItem });

                var reloadedNode = await historyService.LoadTreeCacheAsync(testDir);
                if (reloadedNode == null)
                    throw new InvalidOperationException("TreeCache reloaded node is null.");
                var reloadedChild = reloadedNode.Children.FirstOrDefault(c => string.Equals(c.FullPath, testFilePath, StringComparison.OrdinalIgnoreCase));
                if (reloadedChild == null || reloadedChild.Sha256 != "ABCDEF1234567890")
                    throw new InvalidOperationException($"TreeCache Sha256 was not updated: expected ABCDEF1234567890, got {reloadedChild?.Sha256}");

                if (summary.ReadyToCleanBytes <= 0)
                    throw new InvalidOperationException($"ReadyToCleanBytes should be > 0, got {summary.ReadyToCleanBytes}");

                // 検証 10: ADR 97 Astraレビュー是正（削除計画の重複安全情報全行集約 ＆ IsIgnored 動作）
                // 休眠行と重複行の両方に存在する同一物理ファイルについて、休眠行のみを BuildPlan に渡しても
                // 重複グループの原本参照（OriginalCandidatePath, OriginalExpectedSize 等）が集約されること
                string dupOriginalPath = Path.Combine(testDir, "OriginalFile.txt");
                string dupCopyPath = Path.Combine(testDir, "CopyFile.txt");
                File.WriteAllText(dupOriginalPath, "DUPLICATE_CONTENT_DATA_12345");
                File.WriteAllText(dupCopyPath, "DUPLICATE_CONTENT_DATA_12345");
                long dupSize = new FileInfo(dupOriginalPath).Length;
                DateTime dupWriteUtc = File.GetLastWriteTimeUtc(dupOriginalPath);

                var originalItem = new AuditItem
                {
                    FullPath = dupOriginalPath,
                    FileName = "OriginalFile.txt",
                    Size = dupSize,
                    LastWriteTime = dupWriteUtc,
                    IssueType = AuditIssueType.Duplicate,
                    DuplicateGroupId = "GRP_TEST_001",
                    IsOriginalCandidate = true,
                    IsChecked = false
                };

                var duplicateItem = new AuditItem
                {
                    FullPath = dupCopyPath,
                    FileName = "CopyFile.txt",
                    Size = dupSize,
                    LastWriteTime = dupWriteUtc,
                    IssueType = AuditIssueType.Duplicate,
                    DuplicateGroupId = "GRP_TEST_001",
                    IsOriginalCandidate = false,
                    IsChecked = false
                };

                var dormantItem = new AuditItem
                {
                    FullPath = dupCopyPath, // 同一物理ファイル
                    FileName = "CopyFile.txt",
                    Size = dupSize,
                    LastWriteTime = dupWriteUtc,
                    IssueType = AuditIssueType.Dormant,
                    DuplicateGroupId = string.Empty,
                    IsOriginalCandidate = false,
                    IsChecked = true // 休眠行のみ選択
                };

                // 全アイテムリストとして3つを渡し、選択は休眠行のみ
                var allItems = new List<AuditItem> { originalItem, duplicateItem, dormantItem };
                var plan = AuditCleanupService.BuildPlan(allItems);

                if (plan.Count != 1)
                    throw new InvalidOperationException($"ADR 97 failed: Expected 1 plan item for CopyFile.txt, got {plan.Count}");

                var planItem = plan[0];
                if (planItem.OriginalCandidatePath != dupOriginalPath)
                    throw new InvalidOperationException($"ADR 97 failed: BuildPlan failed to aggregate OriginalCandidatePath from duplicate sibling item. Got: '{planItem.OriginalCandidatePath}'");
                if (planItem.OriginalExpectedSize != dupSize)
                    throw new InvalidOperationException($"ADR 97 failed: BuildPlan failed to aggregate OriginalExpectedSize. Got: {planItem.OriginalExpectedSize}");
                if (!planItem.OriginalExpectedLastWriteTimeUtc.HasValue || planItem.OriginalExpectedLastWriteTimeUtc.Value != dupWriteUtc.ToUniversalTime())
                    throw new InvalidOperationException($"ADR 97 failed: BuildPlan failed to aggregate OriginalExpectedLastWriteTimeUtc.");
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }
    }
}
