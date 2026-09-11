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
        /// [DOMAIN 4/7] Audit & Hygiene 統合テスト
        /// </summary>
        public static async Task TestDomain_AuditAndHygieneAsync()
        {
            TestAuditArchivalOriginalExclusionAndDeduplication();
            await TestDuplicateGroupingAndOuHierarchyAsync();
            await TestSmartOriginalCandidateScoringAsync();
            TestAuditArchiveScriptOptInAndPropertyFidelity();
            TestAuditSmartSelectAndSafePermanentDeletion();
            TestAuditHierarchicalSizeSortingWithDuplicateGroups();
            await TestHeadTailHashAndBandwidthLimiterAsync();
        }

        public static void TestAuditArchivalOriginalExclusionAndDeduplication()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_Audit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string scriptPath = Path.Combine(tempDir, "archive_test.bat");

            try
            {
                string origPath = @"C:\MockRoot\Department\Contract_Original.pdf";
                string dupPath1 = @"C:\MockRoot\Backup\Contract_Copy1.pdf";
                string dupPath2 = @"C:\MockRoot\Archive\Contract_Copy2.pdf";
                string dormantOnlyPath = @"C:\MockRoot\OldReports\Report2016.xlsx";

                // 合成データ: 原本および複製が休眠かつ重複の両方に合致するシナリオ
                var items = new List<AuditItem>
                {
                    // 1. 原本候補 (重複レコード)
                    new AuditItem
                    {
                        FullPath = origPath,
                        FileName = "Contract_Original.pdf",
                        DirectoryPath = @"C:\MockRoot\Department",
                        IssueType = AuditIssueType.Duplicate,
                        IsOriginalCandidate = true,
                        Detail = "[原本候補] ハッシュ: e3b0c44298fc...",
                        DuplicateGroupId = "DUP-0001",
                        Size = 5000000
                    },
                    // 2. 原本候補 (休眠レコード) - 原本が古い休眠ファイルでもある場合
                    new AuditItem
                    {
                        FullPath = origPath,
                        FileName = "Contract_Original.pdf",
                        DirectoryPath = @"C:\MockRoot\Department",
                        IssueType = AuditIssueType.Dormant,
                        Detail = "最終更新: 2017/04/10 (9.4年前)",
                        Size = 5000000
                    },
                    // 3. 重複ファイル1 (重複レコード)
                    new AuditItem
                    {
                        FullPath = dupPath1,
                        FileName = "Contract_Copy1.pdf",
                        DirectoryPath = @"C:\MockRoot\Backup",
                        IssueType = AuditIssueType.Duplicate,
                        Detail = "[重複] ハッシュ: e3b0c44298fc...",
                        DuplicateGroupId = "DUP-0001",
                        Size = 5000000
                    },
                    // 4. 重複ファイル1 (休眠レコード) - 同一ファイルが重複と休眠の双方で検出されたケース
                    new AuditItem
                    {
                        FullPath = dupPath1,
                        FileName = "Contract_Copy1.pdf",
                        DirectoryPath = @"C:\MockRoot\Backup",
                        IssueType = AuditIssueType.Dormant,
                        Detail = "最終更新: 2017/04/10 (9.4年前)",
                        Size = 5000000
                    },
                    // 5. 重複ファイル2 (重複レコードのみ)
                    new AuditItem
                    {
                        FullPath = dupPath2,
                        FileName = "Contract_Copy2.pdf",
                        DirectoryPath = @"C:\MockRoot\Archive",
                        IssueType = AuditIssueType.Duplicate,
                        Detail = "[重複] ハッシュ: e3b0c44298fc...",
                        DuplicateGroupId = "DUP-0001",
                        Size = 5000000
                    },
                    // 6. 単なる休眠ファイル (重複ではない)
                    new AuditItem
                    {
                        FullPath = dormantOnlyPath,
                        FileName = "Report2016.xlsx",
                        DirectoryPath = @"C:\MockRoot\OldReports",
                        IssueType = AuditIssueType.Dormant,
                        Detail = "最終更新: 2016/11/20 (9.8年前)",
                        Size = 2500000
                    }
                };

                var auditService = new AuditReportService();
                auditService.GenerateArchiveRobocopyScript(scriptPath, items, @"C:\MockRoot", @"E:\SafetyArchive");

                if (!File.Exists(scriptPath))
                {
                    throw new InvalidOperationException("退避バッチスクリプトが出力されませんでした。");
                }

                var scriptLines = File.ReadAllLines(scriptPath);

                // move コマンド行を抽出
                var moveLines = scriptLines
                    .Where(l => l.TrimStart().StartsWith("move ", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // 1. 原本ごと全退避バグの検証: 原本候補 (Contract_Original.pdf) に対する move が一切含まれていないこと
                bool originalMoved = moveLines.Any(l => l.Contains("Contract_Original.pdf", StringComparison.OrdinalIgnoreCase));
                if (originalMoved)
                {
                    throw new InvalidOperationException(
                        "原本ごと全退避バグ検出: 重複の [原本候補] である Contract_Original.pdf に対する move コマンドが出力されています。" +
                        " 休眠状態であっても原本は聖域として保護され、現場に残されなければなりません。");
                }

                // 2. move の重複出力排除の検証: dupPath1 に対する move はちょうど 1 行であること
                int dup1MoveCount = moveLines.Count(l => l.Contains("Contract_Copy1.pdf", StringComparison.OrdinalIgnoreCase));
                if (dup1MoveCount == 0)
                {
                    throw new InvalidOperationException("退避漏れバグ: 重複ファイル Contract_Copy1.pdf の move が出力されていません。");
                }
                if (dup1MoveCount > 1)
                {
                    throw new InvalidOperationException(
                        $"重複出力バグ検出: 同一ファイル Contract_Copy1.pdf に対する move コマンドが {dup1MoveCount} 回重複して出力されています。");
                }

                // 3. 他の対象も正しく1回ずつ出力されていること
                int dup2MoveCount = moveLines.Count(l => l.Contains("Contract_Copy2.pdf", StringComparison.OrdinalIgnoreCase));
                if (dup2MoveCount != 1)
                {
                    throw new InvalidOperationException($"Contract_Copy2.pdf の move 出力回数が不正です: {dup2MoveCount}");
                }

                int dormantMoveCount = moveLines.Count(l => l.Contains("Report2016.xlsx", StringComparison.OrdinalIgnoreCase));
                if (dormantMoveCount != 1)
                {
                    throw new InvalidOperationException($"Report2016.xlsx の move 出力回数が不正です: {dormantMoveCount}");
                }

                if (moveLines.Count != 3)
                {
                    throw new InvalidOperationException(
                        $"予期しない move コマンド行数です。期待値: 3, 実際: {moveLines.Count}");
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

                if (string.IsNullOrEmpty(group1[0].RowBackgroundHex) || group1[0].RowBackgroundHex == "Transparent")
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
        /// 15. Audit: 安全退避スクリプト生成（原本保護の IsOriginalCandidate プロパティ判定 & 重複オプトイン）の検証
        /// </summary>

        public static void TestAuditArchiveScriptOptInAndPropertyFidelity()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "fm_test_archive_optin_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string targetRoot = Path.Combine(tempDir, "Source");
                string archiveRoot = Path.Combine(tempDir, "Archive");
                Directory.CreateDirectory(targetRoot);

                string fileOriginal = Path.Combine(targetRoot, "important_original.xlsx");
                string fileCopy = Path.Combine(targetRoot, "important_copy.xlsx");
                string fileDormant = Path.Combine(targetRoot, "old_dormant_2020.pdf");

                // 意図的に Detail から "[原本候補]" という文字を排除し、IsOriginalCandidate プロパティのみで判定されるかを検証
                var items = new List<AuditItem>
                {
                    new()
                    {
                        FullPath = fileOriginal,
                        FileName = "important_original.xlsx",
                        IssueType = AuditIssueType.Duplicate,
                        IsOriginalCandidate = true,
                        Detail = "推奨マスターファイル (カスタム文言)"
                    },
                    new()
                    {
                        FullPath = fileCopy,
                        FileName = "important_copy.xlsx",
                        IssueType = AuditIssueType.Duplicate,
                        IsOriginalCandidate = false,
                        Detail = "重複ファイル (カスタム文言)"
                    },
                    new()
                    {
                        FullPath = fileDormant,
                        FileName = "old_dormant_2020.pdf",
                        IssueType = AuditIssueType.Dormant,
                        IsOriginalCandidate = false,
                        Detail = "4.2年間更新なし"
                    }
                };

                var auditService = new AuditReportService();

                // 1. includeDuplicates = false (デフォルト/安全推奨: 休眠のみ退避)
                string scriptDormantOnly = Path.Combine(tempDir, "test_dormant_only.bat");
                auditService.GenerateArchiveRobocopyScript(scriptDormantOnly, items, targetRoot, archiveRoot, includeDuplicates: false);
                string contentDormantOnly = File.ReadAllText(scriptDormantOnly);

                // 休眠ファイルは退避対象
                if (!contentDormantOnly.Contains("old_dormant_2020.pdf"))
                    throw new InvalidOperationException("Dormant file should be included in archive script");

                // 重複コピーおよび原本は絶対に退避対象外
                if (contentDormantOnly.Contains("important_copy.xlsx"))
                    throw new InvalidOperationException("Duplicate copy should NOT be included when includeDuplicates is false");
                if (contentDormantOnly.Contains("important_original.xlsx"))
                    throw new InvalidOperationException("Original file should NEVER be included in archive script");

                // 2. includeDuplicates = true (オプトイン: 休眠 + 原本以外の重複を退避)
                string scriptWithDuplicates = Path.Combine(tempDir, "test_with_duplicates.bat");
                auditService.GenerateArchiveRobocopyScript(scriptWithDuplicates, items, targetRoot, archiveRoot, includeDuplicates: true);
                string contentWithDuplicates = File.ReadAllText(scriptWithDuplicates);

                // 休眠ファイルと重複コピーは両方含まれる
                if (!contentWithDuplicates.Contains("old_dormant_2020.pdf"))
                    throw new InvalidOperationException("Dormant file should be included when includeDuplicates is true");
                if (!contentWithDuplicates.Contains("important_copy.xlsx"))
                    throw new InvalidOperationException("Duplicate copy should be included when includeDuplicates is true");

                // 原本（IsOriginalCandidate == true）は文字列に [原本候補] がなくても絶対に除外されること
                if (contentWithDuplicates.Contains("important_original.xlsx"))
                    throw new InvalidOperationException("Original file must NEVER be included in archive script even when includeDuplicates is true");
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

    }
}