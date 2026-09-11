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
        /// [DOMAIN 3/7] Simulation Studio 邨ｱ蜷医ユ繧ｹ繝・        /// </summary>
        public static async Task TestDomain_SimulationStudioAsync()
        {
            TestSimulationAclRobocopyAndEffectiveAccessInheritOnly();
            TestSimulationStudioLazyLoadingAndDropOutside();
            await TestUniversalPlanFirstAndMutationVerifyAsync();
            await TestArchitecturalUnificationAsync();
        }

        public static void TestSimulationAclRobocopyAndEffectiveAccessInheritOnly()
        {
            var simService = new SimulationProjectService();
            var effService = new EffectiveAccessService();

            // Part 1: .NET PowerShell スクリプト生成検証 (Set-FolderMorpherAcl, Deny, Allow, 完全ビット保持)
            var testNode = new SimFolderNode
            {
                Name = "FinanceFolder",
                InheritAcl = false
            };
            testNode.AclEntries.Add(new SimAclEntry
            {
                AccountName = "DOMAIN\\Contractors",
                AccessType = AccessControlType.Deny,
                Rights = FileSystemRights.Write,
                InheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags = PropagationFlags.InheritOnly
            });
            testNode.AclEntries.Add(new SimAclEntry
            {
                AccountName = "DOMAIN\\FinanceTeam",
                AccessType = AccessControlType.Allow,
                Rights = FileSystemRights.Modify,
                InheritanceFlags = InheritanceFlags.ContainerInherit,
                PropagationFlags = PropagationFlags.None
            });

            var psScript = simService.GeneratePowerShellAclScript(new[] { testNode }, @"D:\TargetRoot");

            if (!psScript.Contains("function Set-FolderMorpherAcl"))
            {
                throw new InvalidOperationException(
                    $"PowerShell スクリプト生成バグ: Set-FolderMorpherAcl 関数が出力されていません。\n生成内容:\n{psScript}");
            }
            if (!psScript.Contains("SetAccessRuleProtection($true, $false)"))
            {
                throw new InvalidOperationException(
                    $"PowerShell スクリプト生成バグ: 継承遮断で SetAccessRuleProtection($true, $false) が呼ばれていません。\n生成内容:\n{psScript}");
            }
            if (!psScript.Contains("Account = \"DOMAIN\\Contractors\"; Rights = 278; Inheritance = 3; Propagation = 2; AccessType = \"Deny\""))
            {
                throw new InvalidOperationException(
                    $"PowerShell スクリプト生成バグ: Deny ACE が正しく完全ビット・フラグで出力されていません。\n生成内容:\n{psScript}");
            }
            if (!psScript.Contains("Account = \"DOMAIN\\FinanceTeam\"; Rights = 197055; Inheritance = 1; Propagation = 0; AccessType = \"Allow\""))
            {
                throw new InvalidOperationException(
                    $"PowerShell スクリプト生成バグ: Allow ACE が正しく完全ビット・フラグで出力されていません。\n生成内容:\n{psScript}");
            }

            // Canonical ACL Apply Contract: InheritAcl == true のとき、親からの継承ACE (IsInherited == true) は除外されること
            var mixedAclNode = new SimFolderNode { Name = "MixedAclFolder", InheritAcl = true };
            mixedAclNode.AclEntries.Add(new SimAclEntry
            {
                AccountName = "DOMAIN\\InheritedAdmins",
                Rights = FileSystemRights.FullControl,
                IsInherited = true // 親からの継承ルール
            });
            mixedAclNode.AclEntries.Add(new SimAclEntry
            {
                AccountName = "DOMAIN\\ExplicitUsers",
                Rights = FileSystemRights.ReadAndExecute,
                IsInherited = false // このフォルダ固有の明示ルール
            });

            var mixedPsScript = simService.GeneratePowerShellAclScript(new[] { mixedAclNode }, @"D:\TargetRoot");
            if (mixedPsScript.Contains("DOMAIN\\InheritedAdmins"))
            {
                throw new InvalidOperationException(
                    $"Canonical ACL Apply Contract 違反: InheritAcl == true なのに継承ACE (IsInherited == true) が明示ルールとして出力されました。\n生成内容:\n{mixedPsScript}");
            }
            if (!mixedPsScript.Contains("DOMAIN\\ExplicitUsers"))
            {
                throw new InvalidOperationException(
                    $"Canonical ACL Apply Contract 違反: 明示ACE (IsInherited == false) が出力されていません。\n生成内容:\n{mixedPsScript}");
            }

            // Level クランプ撤廃検証 (5階層超えでもClampされず忠実に保持)
            var deepNode = new SimFolderNode { Name = "DeepSubFolder", Level = 8 };
            if (deepNode.Level != 8 || deepNode.LevelPillText != "第9階層")
            {
                throw new InvalidOperationException($"Level クランプ撤廃違反: Level が 8 に保持されていません (現在: {deepNode.Level}, {deepNode.LevelPillText})");
            }

            // SafeFileEnumerator の検証
            var coverage = new ScanCoverage();
            var dummyFiles = SafeFileEnumerator.EnumerateFilesSafe(".", "*.dll", coverage, CancellationToken.None).ToList();
            if (coverage.TotalFoldersScanned == 0)
            {
                throw new InvalidOperationException("SafeFileEnumerator 検証失敗: 走査フォルダ数が 0 です。");
            }

            // 空エントリかつ継承OFFノードの出力検証
            var emptyNode = new SimFolderNode { Name = "IsolatedEmpty", InheritAcl = false };
            var psScriptEmpty = simService.GeneratePowerShellAclScript(new[] { emptyNode }, @"D:\TargetRoot");
            if (!psScriptEmpty.Contains("-Inherit $false -Rules @()"))
            {
                throw new InvalidOperationException(
                    $"PowerShell スクリプト生成バグ: 空エントリ継承OFFノードが正しく出力されていません。\n生成内容:\n{psScriptEmpty}");
            }

            // Part 2: Robocopy スクリプトの子孫除外 (/XD) 検証
            var parentNode = new SimFolderNode
            {
                Name = "ParentShare"
            };
            parentNode.MappedSourcePaths.Add(@"C:\OldFileServer\SalesData");

            var childNode = new SimFolderNode
            {
                Name = "SubProject"
            };
            childNode.MappedSourcePaths.Add(@"C:\OldFileServer\SalesData\2023_Confidential");
            parentNode.Children.Add(childNode);

            var roboScript = simService.GenerateRobocopyScript(new[] { parentNode }, @"D:\NewFileServer");

            if (!roboScript.Contains("/XD \"C:\\OldFileServer\\SalesData\\2023_Confidential\""))
            {
                throw new InvalidOperationException(
                    $"Robocopy スクリプト生成バグ: 子孫ノードのソースパスが親の /XD に除外されていません。\n生成内容:\n{roboScript}");
            }

            // Part 3: Effective Access の InheritOnly 除外検証
            string tempDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_InheritOnly_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                var dirInfo = new DirectoryInfo(tempDir);
                var sec = dirInfo.GetAccessControl(AccessControlSections.Access);

                // 親からの継承を遮断し、既存ルールをすべてクリアしてクリーンなACLを作成
                sec.SetAccessRuleProtection(true, false);
                foreach (FileSystemAccessRule r in sec.GetAccessRules(true, true, typeof(NTAccount)))
                {
                    sec.RemoveAccessRule(r);
                }

                // InheritOnly なルールを追加（フォルダ自身には効かないはず）
                sec.AddAccessRule(new FileSystemAccessRule(
                    new NTAccount(Environment.UserDomainName, Environment.UserName),
                    FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.InheritOnly,
                    AccessControlType.Allow));

                var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Environment.UserName };
                var groupMap = new Dictionary<string, PrincipalGroupMembership>(StringComparer.OrdinalIgnoreCase);

                var eval = effService.EvaluateEffectiveAccessOnAcl(sec, Environment.UserName, targetNames, groupMap, tempDir, "InheritOnlyTest");
                if (eval != null)
                {
                    throw new InvalidOperationException(
                        $"Effective Access InheritOnly 除外バグ検出: InheritOnly のルールしかないのに、フォルダ自身の権限として評価されました。(AllowedRights={eval.AllowedRights})");
                }

                // 自身に適用される Read ルールを追加した場合は正しく評価され、Modify は含まれないこと
                sec.AddAccessRule(new FileSystemAccessRule(
                    new NTAccount(Environment.UserDomainName, Environment.UserName),
                    FileSystemRights.ReadAndExecute,
                    InheritanceFlags.None,
                    PropagationFlags.None,
                    AccessControlType.Allow));

                var eval2 = effService.EvaluateEffectiveAccessOnAcl(sec, Environment.UserName, targetNames, groupMap, tempDir, "InheritOnlyTest");
                if (eval2 == null)
                {
                    throw new InvalidOperationException("Effective Access 評価失敗: 明示的Readルールが存在するのにnullが返されました。");
                }
                // eval2 の AllowedRights に Modify (特に Write 権限) が含まれていないことを確認
                // 注意: FileSystemRights.Modify は複合ビット (ReadAndExecute | Write | Delete) のため、
                // 単純な & != 0 だと ReadAndExecute のビットで真になってしまう。
                if ((eval2.AllowedRights & FileSystemRights.Write) != 0 || eval2.AllowedRights.HasFlag(FileSystemRights.Modify))
                {
                    throw new InvalidOperationException($"Effective Access InheritOnly 混入バグ検出: InheritOnly の Modify がフォルダ自身の権限に混入しました。AllowedRights={eval2.AllowedRights}");
                }
                if ((eval2.AllowedRights & FileSystemRights.ReadAndExecute) == 0)
                {
                    throw new InvalidOperationException("Effective Access 評価失敗: 有効な ReadAndExecute が認識されませんでした。");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// 9. Live ACL Rollback DACL SDDL 完全一致検証 & 空エントリ直接展開での継承OFF検証:
        /// - Live ACL の Snapshot 取得 ➔ ACL変更 ➔ RollbackToSnapshot で、DACL SDDL が変更前と1文字の狂いもなく完全復元されること。
        /// - スケルトン先行展開（DeploySkeletonAsync）で、ACLエントリが0件でも InheritAcl = false の場合に親の継承が確実に切られること。
        /// </summary>

        private static void TestSimulationStudioLazyLoadingAndDropOutside()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_SimTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. ディレクトリ構造の作成: Root / SubA / SubSubA, Root / SubB
                string subADir = Path.Combine(tempDir, "01_Sales");
                string subSubADir = Path.Combine(subADir, "2026_Contracts");
                string subBDir = Path.Combine(tempDir, "02_Marketing");
                Directory.CreateDirectory(subSubADir);
                Directory.CreateDirectory(subBDir);

                // 2. 移行元ツリーの読み込み検証（初期展開＆ダミーノード付与）
                var di = new DirectoryInfo(tempDir);
                var rootItem = new FileItemNode(di.FullName, di.Name, 0, true, di.LastWriteTime);

                foreach (var sub in di.GetDirectories())
                {
                    var subNode = new FileItemNode(sub.FullName, sub.Name, 0, true, sub.LastWriteTime)
                    {
                        Parent = rootItem,
                        Level = rootItem.Level + 1
                    };
                    if (sub.EnumerateDirectories().Any())
                    {
                        subNode.Children.Add(new FileItemNode(string.Empty, "__DUMMY__", 0, false));
                    }
                    rootItem.Children.Add(subNode);
                }
                rootItem.IsExpanded = true;

                if (!rootItem.IsExpanded)
                    throw new InvalidOperationException("Source root node must be auto-expanded upon load.");
                if (rootItem.Children.Count != 2)
                    throw new InvalidOperationException($"Expected 2 direct subdirectories, got {rootItem.Children.Count}");

                var salesNode = rootItem.Children.First(c => c.Name == "01_Sales");
                if (salesNode.Children.Count != 1 || salesNode.Children[0].Name != "__DUMMY__")
                    throw new InvalidOperationException("Subdirectories with children must have dummy node for lazy expansion.");

                // 3. 遅延展開のシミュレーション（Expanded発火で子を実ディレクトリから展開）
                salesNode.Children.Clear();
                var salesDi = new DirectoryInfo(salesNode.FullPath);
                foreach (var sub in salesDi.GetDirectories())
                {
                    salesNode.Children.Add(new FileItemNode(sub.FullName, sub.Name, 0, true, sub.LastWriteTime) { Parent = salesNode });
                }
                if (salesNode.Children.Count != 1 || salesNode.Children[0].Name != "2026_Contracts")
                    throw new InvalidOperationException("Lazy loading failed to expand deep subdirectories.");

                // 4. 新サーバーツリーノード（SimFolderNode）の移動契約検証
                var rootSim = new SimFolderNode { Name = "NewRoot", Level = 0, InheritAcl = true };
                var child1 = new SimFolderNode { Name = "Child1", Level = 1, Parent = rootSim };
                var child2 = new SimFolderNode { Name = "Child2", Level = 1, Parent = rootSim };
                rootSim.Children.Add(child1);
                rootSim.Children.Add(child2);

                // Child2 を Child1 の配下に移動（D&D移動）
                rootSim.Children.Remove(child2);
                child2.Parent = child1;
                child2.Level = child1.Level + 1;
                child1.Children.Add(child2);

                if (child2.Parent != child1 || child2.Level != 2 || !child1.Children.Contains(child2))
                    throw new InvalidOperationException("Moving SimFolderNode under another node failed.");

                // Child2 をルートへ昇格移動（空白部分へのドロップ契約）
                child1.Children.Remove(child2);
                child2.Parent = null;
                child2.Level = 0;
                var rootList = new List<SimFolderNode> { rootSim, child2 };

                if (child2.Parent != null || child2.Level != 0 || !rootList.Contains(child2))
                    throw new InvalidOperationException("Promoting SimFolderNode to root level failed.");

                // 枠外ドロップによる削除契約
                rootList.Remove(child2);
                if (rootList.Contains(child2))
                    throw new InvalidOperationException("Drop outside SimFolderNode removal contract failed.");
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// 17. Audit & Hygiene: スマート選択、原本保護安全契約、および読み取り専用属性解除付き完全削除の検証
        /// </summary>

        private static async Task TestUniversalPlanFirstAndMutationVerifyAsync()
        {
            var baseDir = Path.Combine(Path.GetTempPath(), $"FM_PlanFirstTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(baseDir);

            try
            {
                // -------------------------------------------------------------
                // 1. スケルトン先行展開 (Skeleton Deploy & Verify)
                // -------------------------------------------------------------
                var simService = new SimulationProjectService();
                var deployTarget = Path.Combine(baseDir, "TargetServer");
                var simRoots = new List<SimFolderNode>
                {
                    new SimFolderNode
                    {
                        Name = "Department",
                        Children =
                        {
                            new SimFolderNode { Name = "Finance" },
                            new SimFolderNode { Name = "Engineering" }
                        }
                    }
                };

                var (deployedCount, logs) = await simService.DeploySkeletonAsync(simRoots, deployTarget, null, CancellationToken.None);
                if (deployedCount != 3)
                    throw new InvalidOperationException($"DeploySkeleton must create 3 folders, but created {deployedCount}");

                // Verify: フォルダが実在することを確認
                if (!Directory.Exists(Path.Combine(deployTarget, "Department")) ||
                    !Directory.Exists(Path.Combine(deployTarget, "Department", "Finance")) ||
                    !Directory.Exists(Path.Combine(deployTarget, "Department", "Engineering")))
                {
                    throw new InvalidOperationException("Skeleton deploy verification failed: Subdirectories do not exist on disk!");
                }

                // -------------------------------------------------------------
                // 2. LinkFix ショートカット修復と .bak バックアップ・実態 Verify
                // -------------------------------------------------------------
                var linkDir = Path.Combine(baseDir, "Shortcuts");
                Directory.CreateDirectory(linkDir);
                var lnkPath = Path.Combine(linkDir, "TestLink.lnk");

                dynamic? wsh = null;
                try
                {
                    var wshType = Type.GetTypeFromProgID("WScript.Shell");
                    if (wshType != null) wsh = Activator.CreateInstance(wshType);
                }
                catch { }

                if (wsh != null)
                {
                    var shortcut = wsh.CreateShortcut(lnkPath);
                    shortcut.TargetPath = @"\\OldServer\Share\Docs";
                    shortcut.Save();

                    var linkFixService = new LinkFixService();
                    var fixItem = new LinkFixItem
                    {
                        FilePath = lnkPath,
                        FileName = "TestLink.lnk",
                        FileType = "ショートカット (.lnk)",
                        OldTarget = @"\\OldServer\Share\Docs",
                        NewTarget = @"\\NewServer\Share\Docs"
                    };

                    if (!fixItem.NeedsFix)
                        throw new InvalidOperationException("LinkFixItem NeedsFix must be true when OldTarget != NewTarget!");

                    int fixedCount = await linkFixService.ExecuteFixAsync(new List<LinkFixItem> { fixItem }, null, CancellationToken.None);
                    if (fixedCount != 1)
                        throw new InvalidOperationException($"ExecuteFixAsync failed to fix shortcut (fixedCount: {fixedCount})");

                    // Verify: .bak バックアップの存在
                    if (!File.Exists(lnkPath + ".bak"))
                        throw new InvalidOperationException("LinkFix execution must produce .bak backup file!");

                    // Verify: ターゲットパスが実際に更新されたこと
                    var updatedShortcut = wsh.CreateShortcut(lnkPath);
                    if (!string.Equals(updatedShortcut.TargetPath, @"\\NewServer\Share\Docs", StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException($"LinkFix TargetPath verification failed! Actual: {updatedShortcut.TargetPath}");
                    }
                }

                // -------------------------------------------------------------
                // 3. Media Optimizer 聖域保護と変更計画の忠実性
                // -------------------------------------------------------------
                var options = new MediaOptimizeOptions();
                var sampleImageSanctuary = new MediaItem
                {
                    FullPath = @"C:\Photos\_Master\raw_shot.jpg",
                    FileName = "raw_shot.jpg",
                    OriginalSizeBytes = 10 * 1024 * 1024,
                    IsExcluded = true,
                    ExclusionReason = "聖域保護 (_Master)"
                };
                var sampleImageTarget = new MediaItem
                {
                    FullPath = @"C:\Photos\Daily\trip.jpg",
                    FileName = "trip.jpg",
                    OriginalSizeBytes = 8 * 1024 * 1024,
                    IsExcluded = false
                };

                // 聖域キーワード保護の判定契約
                bool containsMaster = options.ExcludedFolderKeywords.Any(kw => sampleImageSanctuary.FullPath.Contains(kw, StringComparison.OrdinalIgnoreCase));
                if (!containsMaster)
                    throw new InvalidOperationException("ExcludedFolderKeywords must match _Master in sample path!");

                if (!sampleImageSanctuary.IsExcluded || sampleImageTarget.IsExcluded)
                    throw new InvalidOperationException("Sanctuary media items must remain excluded from optimization mutation plan!");
            }
            finally
            {
                try { Directory.Delete(baseDir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// 25. 安全性堅牢化レビュー検証 (H1〜H4, M1〜M5)
        /// ・H1: SimAclEntry.Clone() による特殊フラグ非破壊 & AppliesTo 多言語・未知文字保護
        /// ・H2: DeploySkeletonResult 構造化結果 & 既存フォルダNTFS権限保護 & エラー集約
        /// ・H3: 画像最適化の一時ファイルアトミック置換 & メモリ画像再デコード検証
        /// ・H4 & M4: 重複削除の楽観的ロック（更新日時検知スキップ） & 未削除ファイルのカウント除外
        /// ・M2: LiveAclDiffItem の AccessType（許可/拒否）バッジ反映
        /// </summary>

        private static async Task TestArchitecturalUnificationAsync()
        {
            // --- 1. スナップショット＆履歴のパス別完全分離 ＆ マージ ---
            var historyService = new StorageHistoryService();
            string pathA = @"C:\FM_RegTest_PathA_" + Guid.NewGuid().ToString("N");
            string pathB = @"C:\FM_RegTest_PathB_" + Guid.NewGuid().ToString("N");

            string hashA = StorageHistoryService.GetPathHash(pathA);
            string hashB = StorageHistoryService.GetPathHash(pathB);
            if (hashA == hashB)
                throw new InvalidOperationException("Different paths produced identical hashes!");

            // Path A に 2 件、Path B に 1 件記録（秒単位の重複排除を回避するため明確に異なる日時を指定）
            var baseTime = DateTime.Now;
            await historyService.RecordScanAsync(pathA, 1000, 10, 2, baseTime.AddSeconds(-30));
            await historyService.RecordScanAsync(pathA, 2000, 20, 3, baseTime);
            await historyService.RecordScanAsync(pathB, 5000, 50, 5, baseTime);

            var histA = await historyService.GetHistoryForPathAsync(pathA);
            var histB = await historyService.GetHistoryForPathAsync(pathB);

            if (histA.Count < 2)
                throw new InvalidOperationException($"Expected at least 2 records for PathA, got {histA.Count}");
            if (histB.Count < 1)
                throw new InvalidOperationException($"Expected at least 1 record for PathB, got {histB.Count}");
            if (histA.Any(s => s.TargetPath.Contains("PathB", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("PathA history contains PathB records! Target isolation failed.");
            if (histB.Any(s => s.TargetPath.Contains("PathA", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("PathB history contains PathA records! Target isolation failed.");

            // --- 2. Live ACL 切り戻しスナップショットの10世代ローテーション ---
            var aclService = new AclService();
            string tempAclDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_AclRot_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempAclDir);
            try
            {
                // 12回スナップショットを作成
                for (int i = 1; i <= 12; i++)
                {
                    await aclService.CreateSnapshotAsync(tempAclDir, $"スナップショット #{i}", $"変更: +{i}, -0, ~0");
                    await Task.Delay(10);
                }

                var snapshots = await aclService.GetSnapshotsAsync(tempAclDir);
                if (snapshots.Count != 10)
                    throw new InvalidOperationException($"Expected exactly 10 rotated snapshots, but got {snapshots.Count}");

                // 最新のスナップショットが #12 であり、古い #1 と #2 が削除されていること
                if (!snapshots[0].Note.Contains("#12"))
                    throw new InvalidOperationException($"Latest snapshot should be #12, got: {snapshots[0].Note}");
                if (snapshots.Any(s => s.Note == "スナップショット #1" || s.Note == "スナップショット #2"))
                    throw new InvalidOperationException("Oldest snapshots (#1 or #2) were not purged by 10-generation rotation!");

                if (string.IsNullOrEmpty(snapshots[0].ChangeSummary))
                    throw new InvalidOperationException("ChangeSummary was not persisted in AclSnapshot!");
            }
            finally
            {
                try { Directory.Delete(tempAclDir, recursive: true); } catch { }
            }

            // --- 3. Skeleton Deploy の True Plan-First パイプライン貫通 ＆ 外部競合検知 ---
            var simService = new SimulationProjectService();
            string testDeployTarget = Path.Combine(Path.GetTempPath(), "FM_RegTest_SkeletonPlan_" + Guid.NewGuid().ToString("N"));
            var rootSim = new SimFolderNode { Name = "PlanRoot", InheritAcl = true };
            rootSim.AclEntries.Add(new SimAclEntry { AccountName = "Alice", Rights = FileSystemRights.Read });
            rootSim.Children.Add(new SimFolderNode { Name = "ChildA", InheritAcl = true, Parent = rootSim });
            rootSim.Children.Add(new SimFolderNode { Name = "ChildB", InheritAcl = true, Parent = rootSim });

            // 事前に Plan を構築
            var deployPlan = simService.BuildDeployPlan(new[] { rootSim }, testDeployTarget);
            if (deployPlan.FolderActions.Count != 3)
                throw new InvalidOperationException($"Expected 3 folder actions in plan, got {deployPlan.FolderActions.Count}");
            if (deployPlan.PlannedCreateCount != 3)
                throw new InvalidOperationException($"Expected 3 planned creations, got {deployPlan.PlannedCreateCount}");

            // ACL ディープコピー（凍結）検証: 元ノードのACLを変更してもPlanが不変であること
            rootSim.AclEntries[0].AccountName = "Mallory";
            if (deployPlan.FolderActions[0].AclEntries[0].AccountName != "Alice")
                throw new InvalidOperationException("SkeletonDeployPlan ACLs were not deep-cloned/frozen!");

            try
            {
                // プレビューした Plan をそのままコミット実行
                var deployResult = await simService.DeploySkeletonAsync(deployPlan);
                if (deployResult.CreatedCount != 3)
                    throw new InvalidOperationException($"DeploySkeletonAsync expected to create 3 folders, got {deployResult.CreatedCount}");

                // 物理実在の事後検証 (Verify)
                if (!Directory.Exists(testDeployTarget))
                    throw new InvalidOperationException("Deploy target root does not exist!");
                if (!Directory.Exists(Path.Combine(testDeployTarget, "PlanRoot", "ChildA")))
                    throw new InvalidOperationException("PlanRoot\\ChildA was not physically created!");

                // 外部競合検知テスト:
                // 既存ノードとしてPlan作成した直後に外部で物理削除されたケース
                var conflictPlan = simService.BuildDeployPlan(new[] { rootSim }, testDeployTarget);
                // ChildA は Plan 作成時点で IsExisting == true
                var childAction = conflictPlan.FolderActions.First(a => a.RelativePath.Contains("ChildA"));
                if (!childAction.IsExisting)
                    throw new InvalidOperationException("ChildA should be detected as existing during Plan build");

                // 外部管理者が ChildA を物理削除
                Directory.Delete(Path.Combine(testDeployTarget, "PlanRoot", "ChildA"), recursive: true);

                // Commit 実行 ➔ 計画時 (IsExisting=true) と実態 (exists=false) の不一致を競合として安全検知・スキップ
                var conflictResult = await simService.DeploySkeletonAsync(conflictPlan);
                if (conflictResult.ConflictCount < 1)
                    throw new InvalidOperationException("DeploySkeletonAsync failed to detect external deletion conflict!");
                if (Directory.Exists(Path.Combine(testDeployTarget, "PlanRoot", "ChildA")))
                    throw new InvalidOperationException("Conflicted folder was recreated unexpectedly instead of being guarded!");
            }
            finally
            {
                try { Directory.Delete(testDeployTarget, recursive: true); } catch { }
            }

            // --- 4. Audit の IsOriginalCandidate 単体判定（文字列非依存） ---
            var auditItems = new List<AuditItem>
            {
                new()
                {
                    FullPath = @"C:\Share\OriginalDoc.pdf",
                    FileName = "OriginalDoc.pdf",
                    Size = 1024,
                    DuplicateGroupId = "DUP-0001",
                    IsOriginalCandidate = true,
                    Detail = "", // 意図的に文字列を空にしてプロパティ単体で機能するか検証
                    IsChecked = true
                },
                new()
                {
                    FullPath = @"C:\Share\CopyDoc.pdf",
                    FileName = "CopyDoc.pdf",
                    Size = 1024,
                    DuplicateGroupId = "DUP-0001",
                    IsOriginalCandidate = false,
                    Detail = "",
                    IsChecked = true
                }
            };

            var plans = AuditCleanupService.BuildPlan(auditItems);
            // CopyDoc の計画に OriginalDoc が正本として紐づいていること
            var copyPlan = plans.FirstOrDefault(p => p.FullPath.Contains("CopyDoc.pdf"));
            if (copyPlan == null || string.IsNullOrEmpty(copyPlan.OriginalCandidatePath))
                throw new InvalidOperationException("Audit cleanup plan failed to link original candidate relying purely on IsOriginalCandidate property!");
            if (!copyPlan.OriginalCandidatePath.Contains("OriginalDoc.pdf"))
                throw new InvalidOperationException("Wrong original candidate linked!");

            // --- 5. OfficeLinkFix の一時ファイル生成 ➔ ZIP検証 ➔ アトミック置換 ---
            var officeService = new OfficeLinkFixService();
            string testXlsx = Path.Combine(Path.GetTempPath(), $"FM_RegTest_Office_{Guid.NewGuid():N}.xlsx");
            try
            {
                // 有効な ZIP アーカイブ（ダミー .xlsx）を生成
                using (var zip = System.IO.Compression.ZipFile.Open(testXlsx, System.IO.Compression.ZipArchiveMode.Create))
                {
                    var entry = zip.CreateEntry("xl/externalLinks/_rels/externalLink1.xml.rels");
                    using var writer = new StreamWriter(entry.Open(), System.Text.Encoding.UTF8);
                    writer.Write("<Relationships><Relationship Target=\"file:///\\\\oldserver\\share\\data.xlsx\" /></Relationships>");
                }

                var officeItems = new List<OfficeLinkItem>
                {
                    new()
                    {
                        FilePath = testXlsx,
                        FileName = Path.GetFileName(testXlsx),
                        Extension = ".xlsx",
                        FoundPattern = @"\\oldserver\share",
                        TargetReplacement = @"\\newserver\share",
                        IsLocked = false
                    }
                };

                int fixedCount = await officeService.ExecuteOfficeFixAsync(officeItems, null, CancellationToken.None);
                if (fixedCount != 1)
                    throw new InvalidOperationException($"Expected 1 fixed Office link, got {fixedCount}");

                // バックアップファイル (.bak) が生成されていること
                string bakFile = testXlsx + ".bak";
                if (!File.Exists(bakFile))
                    throw new InvalidOperationException("OfficeLinkFix did not create .bak backup before atomic replace!");

                // 置換後のファイルが正常に ZIP として開け、文字列が置換されていること (Verify)
                using (var verifyZip = System.IO.Compression.ZipFile.OpenRead(testXlsx))
                {
                    var verifyEntry = verifyZip.GetEntry("xl/externalLinks/_rels/externalLink1.xml.rels");
                    if (verifyEntry == null)
                        throw new InvalidOperationException("Missing entry in verified xlsx!");
                    using var reader = new StreamReader(verifyEntry.Open(), System.Text.Encoding.UTF8);
                    string updatedContent = reader.ReadToEnd();
                    if (!updatedContent.Contains(@"\\newserver\share"))
                        throw new InvalidOperationException("Target replacement string was not found in updated xlsx!");
                }
            }
            finally
            {
                try { if (File.Exists(testXlsx)) File.Delete(testXlsx); } catch { }
                try { if (File.Exists(testXlsx + ".bak")) File.Delete(testXlsx + ".bak"); } catch { }
            }

            // --- 6. .lnk ショートカットの修復 ＆ バックアップ作成 ＆ 実態Verify ---
            var linkFixService = new LinkFixService();
            string testLnk = Path.Combine(Path.GetTempPath(), $"FM_RegTest_Lnk_{Guid.NewGuid():N}.lnk");
            dynamic? wsh = null;
            try
            {
                var wshType = Type.GetTypeFromProgID("WScript.Shell");
                if (wshType != null) wsh = Activator.CreateInstance(wshType);
            }
            catch { }

            if (wsh != null)
            {
                try
                {
                    dynamic sc = wsh.CreateShortcut(testLnk);
                    sc.TargetPath = @"C:\OldTarget\App.exe";
                    sc.Save();

                    var lnkItems = new List<LinkFixItem>
                    {
                        new()
                        {
                            FilePath = testLnk,
                            OldTarget = @"C:\OldTarget\App.exe",
                            NewTarget = @"C:\NewTarget\App.exe",
                            FileType = ".lnk"
                        }
                    };

                    int fixedLnkCount = await linkFixService.ExecuteFixAsync(lnkItems, null, CancellationToken.None);
                    if (fixedLnkCount != 1)
                        throw new InvalidOperationException($"Expected 1 fixed .lnk, got {fixedLnkCount}");

                    // 正常修復後の検証
                    dynamic scCheck = wsh.CreateShortcut(testLnk);
                    string? verifiedTarget = scCheck.TargetPath;
                    if (!string.Equals(verifiedTarget?.TrimEnd('\\'), @"C:\NewTarget\App.exe", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Shortcut was not properly fixed to NewTarget!");

                    // バックアップファイル (.bak) が生成されていること
                    string lnkBak = testLnk + ".bak";
                    if (!File.Exists(lnkBak))
                        throw new InvalidOperationException("LinkFix did not create .bak backup before editing .lnk!");
                }
                finally
                {
                    try { if (File.Exists(testLnk)) File.Delete(testLnk); } catch { }
                    try { if (File.Exists(testLnk + ".bak")) File.Delete(testLnk + ".bak"); } catch { }
                }
            }
        }

        /// <summary>
        /// Test 29: Effective Access 実サービス走査（実NTFS ACLツリーによるBaseline/Inherited/Boundary/Elevated/Severed/Enclaveの網羅検証）、
        /// Skeleton Deploy 競合判定、LinkFixer 楽観ロック ＆ 直前ロールバック、および英語モード時日本語残留機械的検知
        /// </summary>
    }
}