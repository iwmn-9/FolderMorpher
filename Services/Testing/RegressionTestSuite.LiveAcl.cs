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
        /// [DOMAIN 1/7] Live ACL & Effective Access 邨ｱ蜷医ユ繧ｹ繝・        /// </summary>
        public static async Task TestDomain_LiveAclAndEffectiveAccessAsync()
        {
            TestLiveAclDenyAndInheritance();
            TestLiveAclSpecialInheritanceFlags();
            TestAclUiBindingAndHelper();
            await TestEffectiveAccessCanonicalDaclAndNestingAsync();
            await TestLiveAclRollbackAndSkeletonEmptyAclInheritanceAsync();
            await TestLiveAclDeltaApplyAndAuditHygieneThresholdsAsync();
            await TestAclMultisetDeltaAndInheritancePreservationAsync();
            await TestAclConflictAndInheritanceInitContractAsync();
            await TestAclChangePlanPipelineAndSemanticVerificationAsync();
            await TestOptimisticLockAndEffectiveAccessChangePointsAsync();
        }

        public static void TestLiveAclDenyAndInheritance()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_Acl_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string currentPrincipal = WindowsIdentity.GetCurrent().Name;
                var aclService = new AclService();

                // テスト用エントリ: Allow と Deny の両方を含み、かつ過去に継承されていたエントリも含む
                var entries = new List<SimAclEntry>
                {
                    // Allow エントリ
                    new SimAclEntry
                    {
                        AccountName = currentPrincipal,
                        DisplayName = Environment.UserName,
                        PrincipalType = AdPrincipalType.User,
                        Rights = FileSystemRights.ReadAndExecute | FileSystemRights.ListDirectory,
                        AccessType = AccessControlType.Allow,
                        IsInherited = false
                    },
                    // Deny エントリ (危険な削除権限を Deny)
                    new SimAclEntry
                    {
                        AccountName = currentPrincipal,
                        DisplayName = Environment.UserName,
                        PrincipalType = AdPrincipalType.User,
                        Rights = FileSystemRights.Delete,
                        AccessType = AccessControlType.Deny,
                        IsInherited = false
                    },
                    // 継承されていたエントリ（継承無効化時に明示的ACEとして保持されるべきルール）
                    new SimAclEntry
                    {
                        AccountName = currentPrincipal,
                        DisplayName = Environment.UserName,
                        PrincipalType = AdPrincipalType.User,
                        Rights = FileSystemRights.Write,
                        AccessType = AccessControlType.Allow,
                        IsInherited = true
                    }
                };

                // 継承無効化 (inherit: false) で適用
                aclService.ApplySimAclEntries(tempDir, entries, inherit: false);

                // 適用後の ACL を検証
                var dirInfo = new DirectoryInfo(tempDir);
                var sec = dirInfo.GetAccessControl(AccessControlSections.Access);

                // 1. 継承無効化（AreAccessRulesProtected == true）の検証
                if (!sec.AreAccessRulesProtected)
                {
                    throw new InvalidOperationException("継承無効化バグ検出: AreAccessRulesProtected が false のままです。");
                }

                // 2. ACE 消失の有無を検証
                var rawRules = sec.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(NTAccount));
                var rules = rawRules.Cast<FileSystemAccessRule>().ToList();

                if (rules.Count == 0)
                {
                    throw new InvalidOperationException("ACE消失バグ検出: 継承無効化適用後にアクセスルールが0件になり、消失しました。");
                }

                // 3. Deny ルールが保持されているか検証
                var denyRules = rules.Where(r => r.AccessControlType == AccessControlType.Deny).ToList();
                if (denyRules.Count == 0)
                {
                    throw new InvalidOperationException("Deny喪失バグ検出: 適用したはずの Deny ACE が消失しています。");
                }

                var allowRules = rules.Where(r => r.AccessControlType == AccessControlType.Allow).ToList();
                if (allowRules.Count == 0)
                {
                    throw new InvalidOperationException("Allow喪失バグ検出: 適用した Allow ACE が消失しています。");
                }

                // 4. Canonical ACL Ordering (明示的 Deny が 明示的 Allow より前にあること) の検証
                bool seenAllow = false;
                for (int i = 0; i < rules.Count; i++)
                {
                    var r = rules[i];
                    if (r.AccessControlType == AccessControlType.Allow)
                    {
                        seenAllow = true;
                    }
                    else if (r.AccessControlType == AccessControlType.Deny)
                    {
                        if (seenAllow)
                        {
                            throw new InvalidOperationException(
                                $"Canonical ACL Ordering 違反バグ検出: Allow ACE の後に Deny ACE (インデックス {i}) が配置されています。" +
                                " NTFS の標準規則では Deny ACE は Allow ACE より前に配置されなければなりません。");
                        }
                    }
                }
            }
            finally
            {
                // クリーンアップ (Deny Delete を解除してから削除)
                if (Directory.Exists(tempDir))
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(tempDir);
                        var sec = dirInfo.GetAccessControl(AccessControlSections.Access);
                        sec.SetAccessRuleProtection(false, false);
                        var existing = sec.GetAccessRules(true, true, typeof(NTAccount));
                        foreach (FileSystemAccessRule r in existing)
                        {
                            try { sec.RemoveAccessRule(r); } catch { }
                        }
                        dirInfo.SetAccessControl(sec);
                    }
                    catch { }

                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }

        /// <summary>
        /// 3. Audit の原本ごと全退避バグ:
        /// 休眠かつ重複ファイルにおいて、退避バッチに原本の move が出力されないこと、および同一 FullPath の move が重複しないこと。
        /// </summary>

        public static void TestLiveAclSpecialInheritanceFlags()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_AclFlags_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var currentUser = Environment.UserDomainName + "\\" + Environment.UserName;
                var aclService = new AclService();

                var entries = new List<SimAclEntry>
                {
                    // 1. 特殊ACE: このフォルダーのみ
                    new SimAclEntry
                    {
                        AccountName = currentUser,
                        AccessType = AccessControlType.Allow,
                        Rights = FileSystemRights.ReadAndExecute,
                        IsInherited = false,
                        InheritanceFlags = InheritanceFlags.None,
                        PropagationFlags = PropagationFlags.None,
                        AppliesTo = AclInheritanceHelper.AppliesTo_ThisFolderOnly
                    },
                    // 2. 特殊ACE: サブフォルダーおよびファイルのみ (InheritOnly)
                    new SimAclEntry
                    {
                        AccountName = "Everyone",
                        AccessType = AccessControlType.Allow,
                        Rights = FileSystemRights.ReadData,
                        IsInherited = false,
                        InheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags = PropagationFlags.InheritOnly,
                        AppliesTo = AclInheritanceHelper.AppliesTo_SubfoldersAndFilesOnly
                    }
                };

                // 適用実行
                aclService.ApplySimAclEntries(tempDir, entries, inherit: false);

                // 再読込
                var (readEntries, isInherited, _) = aclService.GetSimAclForFolder(tempDir);

                // currentUser のエントリ検証: InheritanceFlags.None, PropagationFlags.None
                var userEntry = readEntries.FirstOrDefault(e => e.AccountName.Equals(currentUser, StringComparison.OrdinalIgnoreCase));
                if (userEntry == null)
                {
                    throw new InvalidOperationException($"適用したユーザーエントリ '{currentUser}' が再読込結果に見つかりません。");
                }
                if (userEntry.InheritanceFlags != InheritanceFlags.None || userEntry.PropagationFlags != PropagationFlags.None)
                {
                    throw new InvalidOperationException(
                        $"特殊ACE適用先変質バグ検出: 'このフォルダーのみ' のフラグが保持されていません。" +
                        $" 期待値: InheritanceFlags.None, PropagationFlags.None / 実際: {userEntry.InheritanceFlags}, {userEntry.PropagationFlags}");
                }
                string expectedUserAppliesTo = AclInheritanceHelper.ToAppliesToString(InheritanceFlags.None, PropagationFlags.None);
                if (userEntry.AppliesTo != expectedUserAppliesTo && userEntry.AppliesTo != AclInheritanceHelper.AppliesTo_ThisFolderOnly)
                {
                    throw new InvalidOperationException(
                        $"AppliesTo 文字列が不正です。期待値: '{expectedUserAppliesTo}', 実際: '{userEntry.AppliesTo}'");
                }

                // Everyone のエントリ検証: ContainerInherit | ObjectInherit, InheritOnly
                var everyoneEntry = readEntries.FirstOrDefault(e => e.AccountName.Equals("Everyone", StringComparison.OrdinalIgnoreCase));
                if (everyoneEntry == null)
                {
                    throw new InvalidOperationException("適用した 'Everyone' エントリが見つかりません。");
                }
                if (!everyoneEntry.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
                {
                    throw new InvalidOperationException(
                        $"特殊ACE適用先変質バグ検出: 'サブフォルダーおよびファイルのみ' の InheritOnly フラグが保持されていません。" +
                        $" 実際: {everyoneEntry.PropagationFlags}");
                }
                string expectedEveryoneAppliesTo = AclInheritanceHelper.ToAppliesToString(InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly);
                if (everyoneEntry.AppliesTo != expectedEveryoneAppliesTo && everyoneEntry.AppliesTo != AclInheritanceHelper.AppliesTo_SubfoldersAndFilesOnly)
                {
                    throw new InvalidOperationException(
                        $"AppliesTo 文字列が不正です。期待値: '{expectedEveryoneAppliesTo}', 実際: '{everyoneEntry.AppliesTo}'");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// 6. ACL UI Binding &amp; Helper: 双方向マッピングとモーダル状態同期テスト
        /// ComboBox ⇄ AccessType, AppliesTo ⇄ InheritanceFlags/PropagationFlags の相互変換を検証。
        /// </summary>

        public static void TestAclUiBindingAndHelper()
        {
            // 検証 1: AccessType ⇄ Index 双方向
            if (AclUiBindingHelper.IndexToAccessType(0) != AccessControlType.Allow)
                throw new InvalidOperationException("Index 0 は AccessControlType.Allow でなければなりません。");
            if (AclUiBindingHelper.IndexToAccessType(1) != AccessControlType.Deny)
                throw new InvalidOperationException("Index 1 は AccessControlType.Deny でなければなりません。");
            if (AclUiBindingHelper.AccessTypeToIndex(AccessControlType.Allow) != 0)
                throw new InvalidOperationException("Allow は Index 0 でなければなりません。");
            if (AclUiBindingHelper.AccessTypeToIndex(AccessControlType.Deny) != 1)
                throw new InvalidOperationException("Deny は Index 1 でなければなりません。");

            // 検証 2: AclInheritanceHelper 7大パターンの相互可逆性
            var patterns = new[]
            {
                (AclInheritanceHelper.AppliesTo_All, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None),
                (AclInheritanceHelper.AppliesTo_ThisFolderOnly, InheritanceFlags.None, PropagationFlags.None),
                (AclInheritanceHelper.AppliesTo_ThisFolderAndSubfolders, InheritanceFlags.ContainerInherit, PropagationFlags.None),
                (AclInheritanceHelper.AppliesTo_ThisFolderAndFiles, InheritanceFlags.ObjectInherit, PropagationFlags.None),
                (AclInheritanceHelper.AppliesTo_SubfoldersAndFilesOnly, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly),
                (AclInheritanceHelper.AppliesTo_SubfoldersOnly, InheritanceFlags.ContainerInherit, PropagationFlags.InheritOnly),
                (AclInheritanceHelper.AppliesTo_FilesOnly, InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly),
            };

            foreach (var (text, inh, prop) in patterns)
            {
                var toText = AclInheritanceHelper.ToAppliesToString(inh, prop);
                if (toText != text)
                {
                    throw new InvalidOperationException($"ToAppliesToString 不正: 期待値 '{text}', 実際 '{toText}' (inh={inh}, prop={prop})");
                }
                var (parsedInh, parsedProp) = AclInheritanceHelper.FromAppliesToString(text);
                if (parsedInh != inh || parsedProp != prop)
                {
                    throw new InvalidOperationException($"FromAppliesToString 不正: 入力 '{text}', パース結果 (inh={parsedInh}, prop={parsedProp}) != 期待値 (inh={inh}, prop={prop})");
                }
            }

            // 検証 3: ApplyModalToEntry による SimAclEntry の連動
            var testEntry = new SimAclEntry();
            AclUiBindingHelper.ApplyModalToEntry(testEntry, "経理部グループ", 1, AclInheritanceHelper.AppliesTo_SubfoldersOnly);

            if (testEntry.DisplayName != "経理部グループ")
                throw new InvalidOperationException($"DisplayName 反映失敗: 実際={testEntry.DisplayName}");
            if (testEntry.AccessType != AccessControlType.Deny)
                throw new InvalidOperationException($"AccessType 反映失敗: 期待値=Deny, 実際={testEntry.AccessType}");
            if (testEntry.InheritanceFlags != InheritanceFlags.ContainerInherit)
                throw new InvalidOperationException($"InheritanceFlags 連動失敗: 実際={testEntry.InheritanceFlags}");
            if ((testEntry.PropagationFlags & PropagationFlags.InheritOnly) != PropagationFlags.InheritOnly)
                throw new InvalidOperationException($"PropagationFlags 連動失敗: 実際={testEntry.PropagationFlags}");
        }

        /// <summary>
        /// 7. Effective Access: Canonical DACL 順序評価と多重入れ子グループ（3重ネスト）の判定テスト
        /// - ユーザーが直接権限を持たず、3重に入れ子になったグループ経由のフォルダーへのアクセス権が正確に特定・トレースされること
        /// - 別人アカウント指定時に実行中ユーザーのローカルグループが混入しないこと（他人誤爆防止）
        /// - Windows Canonical DACL Ordering に従い、子の明示Allowが親の継承Denyより優先されること
        /// </summary>

        public static async Task TestEffectiveAccessCanonicalDaclAndNestingAsync()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_EffAccess_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            string subDirect = Path.Combine(tempDir, "01_Direct");
            string subNested = Path.Combine(tempDir, "02_NestedLevel3");
            string subDeny = Path.Combine(tempDir, "03_DenyPrecedence");
            string subUnrelated = Path.Combine(tempDir, "04_Unrelated");

            Directory.CreateDirectory(subDirect);
            Directory.CreateDirectory(subNested);
            Directory.CreateDirectory(subDeny);
            Directory.CreateDirectory(subUnrelated);

            try
            {
                var targetUser = Environment.UserDomainName + "\\" + Environment.UserName;

                // 3重入れ子グループの合成データ
                // targetUser -> Team_L1 (Direct) -> Section_L2 (Depth 2) -> BUILTIN\Users (Depth 3)
                var groups = new List<PrincipalGroupMembership>
                {
                    new PrincipalGroupMembership
                    {
                        GroupName = "Team_L1",
                        DisplayName = "開発第1チーム",
                        IsDirect = true,
                        NestingDepth = 1,
                        MembershipPath = "直接所属"
                    },
                    new PrincipalGroupMembership
                    {
                        GroupName = "Section_L2",
                        DisplayName = "システム開発課",
                        IsDirect = false,
                        NestingDepth = 2,
                        MembershipPath = "Team_L1 -> Section_L2"
                    },
                    new PrincipalGroupMembership
                    {
                        GroupName = "Users",
                        DisplayName = "BUILTIN\\Users",
                        IsDirect = false,
                        NestingDepth = 3,
                        MembershipPath = "Team_L1 -> Section_L2 -> BUILTIN\\Users"
                    }
                };

                var aclService = new AclService();

                // Sub 1: ユーザー直接付与 (Modify)
                aclService.ApplySimAclEntries(subDirect, new[]
                {
                    new SimAclEntry { AccountName = targetUser, AccessType = AccessControlType.Allow, Rights = FileSystemRights.Modify }
                }, inherit: false);

                // Sub 2: 3重入れ子グループにのみ付与 (BUILTIN\Users -> ReadAndExecute)
                // ターゲットユーザー本人はACLに一切記述されていない！
                aclService.ApplySimAclEntries(subNested, new[]
                {
                    new SimAclEntry { AccountName = @"BUILTIN\Users", AccessType = AccessControlType.Allow, Rights = FileSystemRights.ReadAndExecute }
                }, inherit: false);

                // Sub 3: ユーザー本人に Allow と Deny (Write) の組み合わせ
                aclService.ApplySimAclEntries(subDeny, new[]
                {
                    new SimAclEntry { AccountName = targetUser, AccessType = AccessControlType.Allow, Rights = FileSystemRights.Modify },
                    new SimAclEntry { AccountName = targetUser, AccessType = AccessControlType.Deny, Rights = FileSystemRights.Write }
                }, inherit: false);

                // Sub 4: 全く無関係なローカルサービスアカウントにのみ付与
                aclService.ApplySimAclEntries(subUnrelated, new[]
                {
                    new SimAclEntry { AccountName = @"NT AUTHORITY\LOCAL SERVICE", AccessType = AccessControlType.Allow, Rights = FileSystemRights.FullControl }
                }, inherit: false);

                // 実行
                var effService = new EffectiveAccessService();
                var report = await effService.ScanEffectiveAccessAsync(tempDir, targetUser, groups, maxDepth: 2);

                // 検証 1: 検出件数（Sub1, Sub2, Sub3 の3件が検出され、Sub4は除外されること）
                var foundPaths = report.AccessibleFolders.Select(f => f.FolderPath).ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!foundPaths.Contains(subDirect))
                {
                    throw new InvalidOperationException("Sub 1 (直接付与フォルダー) が検出されませんでした。");
                }
                if (!foundPaths.Contains(subNested))
                {
                    throw new InvalidOperationException(
                        "多重入れ子バグ検出: 3重ネストされたグループ 'BUILTIN\\Users' 経由のフォルダーが検出されませんでした。");
                }
                if (!foundPaths.Contains(subDeny))
                {
                    throw new InvalidOperationException("Sub 3 (Deny一部適用フォルダー) が検出されませんでした。");
                }
                if (foundPaths.Contains(subUnrelated))
                {
                    throw new InvalidOperationException(
                        "誤判定バグ検出: 権限を持たないはずの 'Sub 4 (無関係グループ)' がアクセス可能と判定されました。");
                }

                // 検証 2: 多重入れ子フォルダーの付与元トレース
                var nestedItem = report.AccessibleFolders.First(f => f.FolderPath.Equals(subNested, StringComparison.OrdinalIgnoreCase));
                if (!nestedItem.GrantSource.Contains("Users"))
                {
                    throw new InvalidOperationException($"付与元グループ特定失敗: 実際={nestedItem.GrantSource}");
                }
                if (!nestedItem.GrantSource.Contains("深度 3") && !nestedItem.GrantSource.Contains("depth 3"))
                {
                    throw new InvalidOperationException($"入れ子深度特定失敗: 実際={nestedItem.GrantSource}");
                }
                if (nestedItem.PermissionLevel != EffectivePermissionLevel.ReadAndExecute)
                {
                    throw new InvalidOperationException($"実効権限レベル判定失敗: 期待値=ReadAndExecute, 実際={nestedItem.PermissionLevel}");
                }

                // 検証 3: 直接付与の特定
                var directItem = report.AccessibleFolders.First(f => f.FolderPath.Equals(subDirect, StringComparison.OrdinalIgnoreCase));
                if (!directItem.GrantSource.Contains("直接付与") && !directItem.GrantSource.Contains("Direct"))
                {
                    throw new InvalidOperationException($"直接付与判定失敗: 実際={directItem.GrantSource}");
                }
                if (directItem.PermissionLevel != EffectivePermissionLevel.Modify)
                {
                    throw new InvalidOperationException($"実効権限レベル判定失敗: 期待値=Modify, 実際={directItem.PermissionLevel}");
                }

                // 検証 4: 別人指定時の実行者グループ誤爆防止
                var (unrelatedGroups, resMode, resStatus) = await effService.ResolveMembershipsAsync("CompletelyUnrelatedAuditTarget_XYZ999");
                if (unrelatedGroups.Count != 0)
                {
                    throw new InvalidOperationException(
                        $"他人アカウントへの実行者グループ誤爆バグ検出: 未知アカウントなのに {unrelatedGroups.Count} 件のグループが混入しました。");
                }
                if (resMode != EffectiveAccessResolutionMode.DirectAclOnly)
                {
                    throw new InvalidOperationException($"解決モード誤り: 期待値=DirectAclOnly, 実際={resMode}");
                }

                // 検証 5: Windows Canonical DACL 順序評価（子の明示Allowが親の継承Denyより優先されること）
                string parentFolder = Path.Combine(tempDir, "05_ParentDeny");
                string childFolder = Path.Combine(parentFolder, "ChildExplicitAllow");
                Directory.CreateDirectory(parentFolder);
                Directory.CreateDirectory(childFolder);

                // 子に明示的な Allow (Write | ReadAndExecute) を追加
                var childDirInfo = new DirectoryInfo(childFolder);
                var childSec = childDirInfo.GetAccessControl(AccessControlSections.Access);
                childSec.AddAccessRule(new FileSystemAccessRule(
                    new NTAccount(targetUser),
                    FileSystemRights.Write | FileSystemRights.ReadAndExecute,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                childDirInfo.SetAccessControl(childSec);

                // 親に Deny (Write) を設定（子へ継承）
                aclService.ApplySimAclEntries(parentFolder, new[]
                {
                    new SimAclEntry
                    {
                        AccountName = targetUser,
                        AccessType = AccessControlType.Deny,
                        Rights = FileSystemRights.Write,
                        InheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags = PropagationFlags.None
                    }
                }, inherit: false);

                // 子の最新 ACL を読み取る（親からの継承Denyと子の明示Allowの両方が入っている）
                var refreshedChildSec = childDirInfo.GetAccessControl(AccessControlSections.Access);
                var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { targetUser };
                var groupMap = new Dictionary<string, PrincipalGroupMembership>(StringComparer.OrdinalIgnoreCase);

                var evalItem = effService.EvaluateEffectiveAccessOnAcl(refreshedChildSec, targetUser, targetNames, groupMap, childFolder, "ChildExplicitAllow");
                if (evalItem == null)
                {
                    throw new InvalidOperationException("Canonical DACL 評価失敗: 明示的Allowが存在するのにnullが返されました。");
                }
                // 明示的Allow(Write)が親の継承Deny(Write)に勝つため、実効権限にWriteが含まれること！
                if ((evalItem.AllowedRights & FileSystemRights.Write) == 0)
                {
                    throw new InvalidOperationException(
                        $"Canonical DACL 順序バグ検出: 子の明示Allowが親の継承Denyに打ち消されました。(AllowedRights={evalItem.AllowedRights})");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }

        /// <summary>
        /// 8. Simulation & Script Generation:
        /// - PowerShell ACL スクリプト生成で Deny ルールが /deny で出力され、継承フラグ ((OI)(CI)(IO)等) が正確であること。
        /// - Robocopy スクリプト生成で、子孫ノードがマッピングされているソースパスが親の /XD に除外されること。
        /// - Effective Access 評価で、InheritOnly フラグ付き ACE が現在のフォルダ自身の権限から除外されること。
        /// </summary>

        public static async Task TestLiveAclRollbackAndSkeletonEmptyAclInheritanceAsync()
        {
            var aclService = new AclService();
            var simService = new SimulationProjectService();

            string tempDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_Rollback_" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(tempDir);
                var dirInfo = new DirectoryInfo(tempDir);

                // 検証 1: Live ACL Rollback の DACL SDDL 完全復元
                // 変更前のオリジナル DACL SDDL を取得してスナップショットを作成
                var snapshot = await aclService.CreateSnapshotAsync(tempDir, "Rollbackテスト");
                var origSec = dirInfo.GetAccessControl(AccessControlSections.Access);
                var originalSddl = origSec.GetSecurityDescriptorSddlForm(AccessControlSections.Access);

                if (snapshot.Sddl != originalSddl)
                {
                    throw new InvalidOperationException(
                        $"Snapshot SDDL 不一致: スナップショット作成時のSDDLが実際のDACLと異なります。\nSnapshot: {snapshot.Sddl}\nActual: {originalSddl}");
                }

                // ACL を変更（別のルールを付与）
                var modifiedSec = dirInfo.GetAccessControl(AccessControlSections.Access);
                modifiedSec.AddAccessRule(new FileSystemAccessRule(
                    new NTAccount(Environment.UserDomainName, Environment.UserName),
                    FileSystemRights.FullControl,
                    AccessControlType.Deny));
                dirInfo.SetAccessControl(modifiedSec);

                var changedSddl = dirInfo.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                if (changedSddl == originalSddl)
                {
                    throw new InvalidOperationException("ACL変更テスト失敗: ルール変更後のSDDLが変化していません。");
                }

                // ロールバックを実行
                aclService.RollbackToSnapshot(tempDir, snapshot);

                var restoredSec = dirInfo.GetAccessControl(AccessControlSections.Access);
                var restoredSddl = restoredSec.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                if (!AreAclRulesEquivalent(origSec, restoredSec, out var diffReason))
                {
                    throw new InvalidOperationException(
                        $"Rollback 失敗: ロールバック後のDACLルールが元と一致しません！({diffReason})\n期待値: {originalSddl}\n復元値: {restoredSddl}");
                }

                // 検証 2: スケルトン先行展開で、エントリ0件かつ InheritAcl = false のフォルダの継承遮断
                string skeletonTarget = Path.Combine(tempDir, "SkeletonTarget");
                Directory.CreateDirectory(skeletonTarget);

                var emptyInheritOffNode = new SimFolderNode
                {
                    Name = "ProtectedChild",
                    InheritAcl = false
                };

                var (count, logs) = await simService.DeploySkeletonAsync(new[] { emptyInheritOffNode }, skeletonTarget, null, CancellationToken.None);
                if (count != 1)
                {
                    throw new InvalidOperationException($"スケルトン展開失敗: フォルダ作成数が不正です ({count})");
                }

                string childPath = Path.Combine(skeletonTarget, "ProtectedChild");
                var childSec = new DirectoryInfo(childPath).GetAccessControl(AccessControlSections.Access);
                if (!childSec.AreAccessRulesProtected)
                {
                    throw new InvalidOperationException(
                        "スケルトン展開バグ検出: エントリが0件のフォルダで InheritAcl = false なのに親からの継承が遮断されていません！");
                }
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }


        private static async Task TestLiveAclDeltaApplyAndAuditHygieneThresholdsAsync()
        {
            // 1. Live ACL 差分適用 (Delta Apply) セマンティクス & 既存ノータッチ契約
            string testDir = Path.Combine(Path.GetTempPath(), "FM_DeltaApply_Test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);
            var aclService = new AclService();

            try
            {
                // 初期ACEの構成 (Rule A: Authenticated Users = ReadAndExecute, Rule B: Users = Read)
                var initialAcl = new List<SimAclEntry>
                {
                    new SimAclEntry
                    {
                        AccountName = "Authenticated Users",
                        DisplayName = "Authenticated Users",
                        Rights = FileSystemRights.ReadAndExecute,
                        AccessType = AccessControlType.Allow
                    },
                    new SimAclEntry
                    {
                        AccountName = "Users",
                        DisplayName = "Users",
                        Rights = FileSystemRights.Read,
                        AccessType = AccessControlType.Allow
                    }
                };

                // 初期状態を適用
                aclService.ApplySimAclEntries(testDir, initialAcl, inherit: false);
                var (currentEntries, isInherited, _) = aclService.GetSimAclForFolder(testDir);

                // 差分のシミュレーション:
                // Rule A (Authenticated Users): 変更なし (ノータッチ)
                // Rule B (Users): 変更 (Read -> Modify)
                // Rule C (Administrators): 新規追加 (FullControl)
                var updatedAcl = new List<SimAclEntry>
                {
                    new SimAclEntry
                    {
                        AccountName = "Authenticated Users",
                        DisplayName = "Authenticated Users",
                        Rights = FileSystemRights.ReadAndExecute,
                        AccessType = AccessControlType.Allow
                    },
                    new SimAclEntry
                    {
                        AccountName = "Users",
                        DisplayName = "Users",
                        Rights = FileSystemRights.Modify,
                        AccessType = AccessControlType.Allow
                    },
                    new SimAclEntry
                    {
                        AccountName = "Administrators",
                        DisplayName = "Administrators",
                        Rights = FileSystemRights.FullControl,
                        AccessType = AccessControlType.Allow
                    }
                };

                // Delta Apply 実行
                var deltaResult = await aclService.ApplyLiveAclDeltaWithRollbackAsync(
                    testDir,
                    currentEntries,
                    updatedAcl,
                    inherit: false,
                    originalInherit: isInherited);

                int deltaAppliedCount = deltaResult.addedCount + deltaResult.removedCount + deltaResult.modifiedCount;
                if (deltaAppliedCount != 2)
                    throw new InvalidOperationException($"Expected 2 delta rules applied (1 modified, 1 added), got {deltaAppliedCount} (added={deltaResult.addedCount}, removed={deltaResult.removedCount}, modified={deltaResult.modifiedCount})");

                // 適用後の状態検証
                var (afterEntries, _, _) = aclService.GetSimAclForFolder(testDir);
                var authUserAce = afterEntries.FirstOrDefault(e => SimAclEntry.IsSameAccount(e.AccountName, "Authenticated Users"));
                var usersAce = afterEntries.FirstOrDefault(e => SimAclEntry.IsSameAccount(e.AccountName, "Users"));
                var adminAce = afterEntries.FirstOrDefault(e => SimAclEntry.IsSameAccount(e.AccountName, "Administrators"));

                if (authUserAce == null || !SimAclEntry.IsSameRights(authUserAce.Rights, FileSystemRights.ReadAndExecute))
                    throw new InvalidOperationException("Untouched ACE 'Authenticated Users' was corrupted or lost.");
                if (usersAce == null || !SimAclEntry.IsSameRights(usersAce.Rights, FileSystemRights.Modify))
                    throw new InvalidOperationException($"Expected Users rights Modify, got {usersAce?.Rights}");
                if (adminAce == null || !SimAclEntry.IsSameRights(adminAce.Rights, FileSystemRights.FullControl))
                    throw new InvalidOperationException("Newly added ACE 'Administrators' was not found.");

                // 差分0件の呼び出しテスト (Skip API call)
                var noDeltaResult = await aclService.ApplyLiveAclDeltaWithRollbackAsync(
                    testDir,
                    afterEntries,
                    afterEntries,
                    inherit: false,
                    originalInherit: false);

                int noDeltaAppliedCount = noDeltaResult.addedCount + noDeltaResult.removedCount + noDeltaResult.modifiedCount;
                if (noDeltaAppliedCount != 0)
                    throw new InvalidOperationException($"Expected 0 deltas for identical entries, got {noDeltaAppliedCount}");

                // 2. パス長危険域 (240文字〜) の判定検証
                string longPath245 = "C:\\" + new string('a', 242);
                var dummyLongItem = new AuditItem
                {
                    FullPath = longPath245,
                    FileName = "test.txt",
                    DirectoryPath = "C:\\",
                    IssueType = AuditIssueType.PathTooLong
                };
                if (!dummyLongItem.IssueTypeDisplay.Contains("240"))
                    throw new InvalidOperationException($"IssueTypeDisplay should reflect 240+ danger zone, got: {dummyLongItem.IssueTypeDisplay}");

                // 3. 断捨離のリアルタイム削減計算（原本保護連動）
                var auditTestItems = new List<AuditItem>
                {
                    // 重複グループ 1: 原本 10MB + コピー 10MB (2件)
                    new AuditItem { FullPath = "C:\\Data\\Master.pdf", Size = 10 * 1024 * 1024, IssueType = AuditIssueType.Duplicate, IsOriginalCandidate = true, IsChecked = true },
                    new AuditItem { FullPath = "C:\\Data\\Copy1.pdf", Size = 10 * 1024 * 1024, IssueType = AuditIssueType.Duplicate, IsOriginalCandidate = false, IsChecked = true },
                    new AuditItem { FullPath = "C:\\Data\\Copy2.pdf", Size = 10 * 1024 * 1024, IssueType = AuditIssueType.Duplicate, IsOriginalCandidate = false, IsChecked = true },
                    // 休眠ファイル: 5MB
                    new AuditItem { FullPath = "C:\\Data\\Old.zip", Size = 5 * 1024 * 1024, IssueType = AuditIssueType.Dormant, IsOriginalCandidate = false, IsChecked = true },
                    // 未選択の休眠ファイル: 20MB
                    new AuditItem { FullPath = "C:\\Data\\Unchecked.iso", Size = 20 * 1024 * 1024, IssueType = AuditIssueType.Dormant, IsOriginalCandidate = false, IsChecked = false }
                };

                // 原本保護付き集計ロジックの検証
                var calculatedItems = auditTestItems
                    .Where(x => x.IsChecked && !x.IsOriginalCandidate)
                    .GroupBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .ToList();

                long totalReduced = calculatedItems.Sum(x => x.Size);
                if (totalReduced != 25 * 1024 * 1024)
                    throw new InvalidOperationException($"Expected 25MB reduction, got {totalReduced / (1024 * 1024)}MB");
                if (calculatedItems.Count != 3)
                    throw new InvalidOperationException($"Expected 3 reduced items, got {calculatedItems.Count}");
            }
            finally
            {
                try { Directory.Delete(testDir, recursive: true); } catch { }
            }
        }


        private static async Task TestAclMultisetDeltaAndInheritancePreservationAsync()
        {
            var aclService = new AclService();
            string testDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_Regression_Test21_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                // 1. High 2: 同一ユーザー複数ACEのマルチセット差分適用の検証
                // ACE 1: Users, ReadAndExecute, ContainerInherit | ObjectInherit, None (このフォルダー、サブフォルダーおよびファイル)
                // ACE 2: Users, Modify, ContainerInherit, InheritOnly (サブフォルダーのみ)
                var initialAcl = new List<SimAclEntry>
                {
                    new SimAclEntry
                    {
                        AccountName = Environment.UserName,
                        DisplayName = Environment.UserName,
                        Rights = FileSystemRights.FullControl,
                        AccessType = AccessControlType.Allow,
                        InheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags = PropagationFlags.None
                    },
                    new SimAclEntry
                    {
                        AccountName = "Users",
                        DisplayName = "Users",
                        Rights = FileSystemRights.ReadAndExecute,
                        AccessType = AccessControlType.Allow,
                        InheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags = PropagationFlags.None
                    },
                    new SimAclEntry
                    {
                        AccountName = "Users",
                        DisplayName = "Users",
                        Rights = FileSystemRights.Modify,
                        AccessType = AccessControlType.Allow,
                        InheritanceFlags = InheritanceFlags.ContainerInherit,
                        PropagationFlags = PropagationFlags.InheritOnly
                    }
                };

                aclService.ApplySimAclEntries(testDir, initialAcl, inherit: false);
                var (currentEntries, isInherited, _) = aclService.GetSimAclForFolder(testDir);

                if (currentEntries.Count != 3)
                    throw new InvalidOperationException($"Expected 3 initial ACEs (User + 2 Users rules), got {currentEntries.Count}");

                // ACE 2 の権限のみを Modify -> FullControl に変更
                var updatedAcl = new List<SimAclEntry>
                {
                    new SimAclEntry
                    {
                        AccountName = Environment.UserName,
                        DisplayName = Environment.UserName,
                        Rights = FileSystemRights.FullControl,
                        AccessType = AccessControlType.Allow,
                        InheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags = PropagationFlags.None
                    },
                    new SimAclEntry
                    {
                        AccountName = "Users",
                        DisplayName = "Users",
                        Rights = FileSystemRights.ReadAndExecute,
                        AccessType = AccessControlType.Allow,
                        InheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                        PropagationFlags = PropagationFlags.None
                    },
                    new SimAclEntry
                    {
                        AccountName = "Users",
                        DisplayName = "Users",
                        Rights = FileSystemRights.FullControl,
                        AccessType = AccessControlType.Allow,
                        InheritanceFlags = InheritanceFlags.ContainerInherit,
                        PropagationFlags = PropagationFlags.InheritOnly
                    }
                };

                // Delta Apply 実行
                var deltaResult = await aclService.ApplyLiveAclDeltaWithRollbackAsync(
                    testDir,
                    currentEntries,
                    updatedAcl,
                    inherit: false,
                    originalInherit: isInherited);

                // 差分は ACE 2 の変更 (modified=1) のみであるべき
                if (deltaResult.addedCount != 0 || deltaResult.removedCount != 0 || deltaResult.modifiedCount != 1)
                    throw new InvalidOperationException($"Expected exactly 1 modified rule for multiset ACE, got added={deltaResult.addedCount}, removed={deltaResult.removedCount}, modified={deltaResult.modifiedCount}");

                var (afterEntries, _, _) = aclService.GetSimAclForFolder(testDir);
                var ace1 = afterEntries.FirstOrDefault(e => e.PropagationFlags == PropagationFlags.None);
                var ace2 = afterEntries.FirstOrDefault(e => e.PropagationFlags == PropagationFlags.InheritOnly);

                if (ace1 == null || !SimAclEntry.IsSameRights(ace1.Rights, FileSystemRights.ReadAndExecute))
                    throw new InvalidOperationException("Untouched multiset ACE 1 was modified or lost.");
                if (ace2 == null || !SimAclEntry.IsSameRights(ace2.Rights, FileSystemRights.FullControl))
                    throw new InvalidOperationException("Modified multiset ACE 2 did not receive FullControl.");

                // 2. High 1: 継承無効化（OFF）時の明示ACE自動保持（ロックアウト防止）の検証
                // 親からの継承が有効なサブフォルダを作成
                string subDir = Path.Combine(testDir, "SubProtected");
                Directory.CreateDirectory(subDir);

                var (subInitialEntries, subIsInherited, _) = aclService.GetSimAclForFolder(subDir);
                if (!subIsInherited)
                    throw new InvalidOperationException("Subfolder should initially have inheritance enabled.");

                int inheritedCountBefore = subInitialEntries.Count;
                if (inheritedCountBefore == 0)
                    throw new InvalidOperationException("Subfolder should inherit at least 1 ACE from parent.");

                // 継承を無効化 (inherit: false, originalInherit: true)
                var inheritOffResult = await aclService.ApplyLiveAclDeltaWithRollbackAsync(
                    subDir,
                    subInitialEntries,
                    subInitialEntries.Select(e => { var c = e.Clone(); c.IsInherited = false; return c; }).ToList(),
                    inherit: false,
                    originalInherit: true);

                var (subAfterEntries, subAfterInherited, _) = aclService.GetSimAclForFolder(subDir);
                if (subAfterInherited)
                    throw new InvalidOperationException("Subfolder inheritance should now be disabled (protected).");
                if (subAfterEntries.Count != inheritedCountBefore)
                    throw new InvalidOperationException($"All inherited ACEs must be preserved as explicit rules! Expected {inheritedCountBefore}, got {subAfterEntries.Count}");
                if (subAfterEntries.Any(e => e.IsInherited))
                    throw new InvalidOperationException("All preserved ACEs must now be marked as explicit (IsInherited == false).");

                // 3. Medium 1: Auditリアルタイム集計での原本聖域すり抜け防止の検証
                // 同一 FullPath のファイルが休眠（Dormant）と重複原本（Duplicate）として登録されている場合
                string sharedPath = @"C:\Sanctum\CompanyMaster.xlsx";
                var mockAuditItems = new List<AuditItem>
                {
                    // 行1: 休眠ファイルとして検出 (IsOriginalCandidate = false, チェックON)
                    new AuditItem
                    {
                        FullPath = sharedPath,
                        FileName = "CompanyMaster.xlsx",
                        DirectoryPath = @"C:\Sanctum",
                        Size = 50 * 1024 * 1024, // 50MB
                        IssueType = AuditIssueType.Dormant,
                        IsOriginalCandidate = false,
                        IsChecked = true
                    },
                    // 行2: 重複グループの原本候補として検出 (IsOriginalCandidate = true, チェックOFF)
                    new AuditItem
                    {
                        FullPath = sharedPath,
                        FileName = "CompanyMaster.xlsx",
                        DirectoryPath = @"C:\Sanctum",
                        Size = 50 * 1024 * 1024,
                        IssueType = AuditIssueType.Duplicate,
                        IsOriginalCandidate = true,
                        IsChecked = false
                    }
                };

                // AuditCleanupService.BuildPlan を通した正本集計
                var plans = AuditCleanupService.BuildPlan(mockAuditItems);
                var validPlans = plans.Where(p => !p.IsOriginalCandidate).ToList();

                if (validPlans.Count != 0)
                    throw new InvalidOperationException($"Original candidate must be strictly protected across all audit types! Expected 0 valid deletion plans, got {validPlans.Count}");
            }
            finally
            {
                try { Directory.Delete(testDir, recursive: true); } catch { }
            }
        }


        private static async Task TestAclConflictAndInheritanceInitContractAsync()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_Regression_Test22_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                var aclService = new AclService();

                // 1. Medium: 外部ACL変更との競合検出 (AclConflictException) の検証
                var (initialEntries, isInherited, _) = aclService.GetSimAclForFolder(testDir);
                string originalSddl = aclService.GetSddl(testDir);

                if (string.IsNullOrEmpty(originalSddl))
                    throw new InvalidOperationException("Initial SDDL must not be empty.");

                // ケース A: 期待SDDLと一致している場合は正常終了
                var newEntries = initialEntries.Select(e => e.Clone()).ToList();
                newEntries.Add(new SimAclEntry
                {
                    AccountName = "Everyone",
                    Rights = FileSystemRights.Read,
                    AccessType = AccessControlType.Allow,
                    InheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags = PropagationFlags.None
                });

                var applyResult = await aclService.ApplyLiveAclDeltaWithRollbackAsync(
                    testDir,
                    initialEntries,
                    newEntries,
                    isInherited,
                    isInherited,
                    expectedOriginalSddl: originalSddl,
                    forceIfConflict: false);

                if (!applyResult.hasModified || applyResult.addedCount != 1)
                    throw new InvalidOperationException("Expected 1 added ACE without conflict.");

                // ケース B: 外部でSDDLが変更された（古いoriginalSddlを期待値として渡す）場合、AclConflictException がスローされること
                bool conflictDetected = false;
                try
                {
                    var dummyEntries = newEntries.Select(e => e.Clone()).ToList();
                    dummyEntries.Add(new SimAclEntry
                    {
                        AccountName = "SYSTEM",
                        Rights = FileSystemRights.FullControl,
                        AccessType = AccessControlType.Allow
                    });

                    // わざと古い originalSddl を渡す（すでにEveryoneが追加されているため実際のSDDLと異なる）
                    await aclService.ApplyLiveAclDeltaWithRollbackAsync(
                        testDir,
                        newEntries,
                        dummyEntries,
                        isInherited,
                        isInherited,
                        expectedOriginalSddl: originalSddl,
                        forceIfConflict: false);
                }
                catch (AclConflictException ex)
                {
                    conflictDetected = true;
                    if (ex.FolderPath != testDir)
                        throw new InvalidOperationException($"AclConflictException FolderPath mismatch: {ex.FolderPath}");
                }

                if (!conflictDetected)
                    throw new InvalidOperationException("AclConflictException must be thrown when external SDDL differs from expected!");

                // ケース C: forceIfConflict: true の場合は競合しても例外なく処理されること
                var forceResult = await aclService.ApplyLiveAclDeltaWithRollbackAsync(
                    testDir,
                    newEntries,
                    newEntries, // 変更なし
                    isInherited,
                    isInherited,
                    expectedOriginalSddl: originalSddl,
                    forceIfConflict: true);

                // 2. Low 1: 継承OFFフォルダの初期読み込み時に HasChanges == false であることの検証
                var nonInheritedPanel = new LiveAclPanelModel
                {
                    FolderPath = testDir,
                    FolderName = "TestDir",
                    OriginalInheritAcl = false,
                    InheritAcl = false,
                    OriginalSddl = originalSddl
                };
                nonInheritedPanel.OriginalAclEntries.Add(new SimAclEntry { AccountName = "TestUser", Rights = FileSystemRights.Read });
                nonInheritedPanel.CurrentAclEntries.Add(new SimAclEntry { AccountName = "TestUser", Rights = FileSystemRights.Read });
                nonInheritedPanel.UpdateChangeStatus();

                if (nonInheritedPanel.HasChanges)
                    throw new InvalidOperationException("LiveAclPanelModel for non-inherited folder must NOT have changes immediately upon loading!");

                // 3. Low~Medium: 同一プリンシパルの複数ACEの独立認識と変更検知
                var multiAcePanel = new LiveAclPanelModel
                {
                    FolderPath = testDir,
                    FolderName = "TestDir",
                    OriginalInheritAcl = true,
                    InheritAcl = true
                };

                // 同一アカウントで異なる2つのルール
                var ace1 = new SimAclEntry
                {
                    AccountName = "Sales_RW",
                    Rights = FileSystemRights.Read,
                    InheritanceFlags = InheritanceFlags.None,
                    PropagationFlags = PropagationFlags.None
                };
                var ace2 = new SimAclEntry
                {
                    AccountName = "Sales_RW",
                    Rights = FileSystemRights.Modify,
                    InheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags = PropagationFlags.InheritOnly
                };

                multiAcePanel.OriginalAclEntries.Add(ace1.Clone());
                multiAcePanel.OriginalAclEntries.Add(ace2.Clone());
                multiAcePanel.CurrentAclEntries.Add(ace1.Clone());
                multiAcePanel.CurrentAclEntries.Add(ace2.Clone());
                multiAcePanel.UpdateChangeStatus();

                if (multiAcePanel.HasChanges)
                    throw new InvalidOperationException("Multi-ACE panel with matching rules must not report changes!");

                // 片方だけ権限を変更すると HasChanges == true になること
                multiAcePanel.CurrentAclEntries[0].Rights = FileSystemRights.FullControl;
                multiAcePanel.UpdateChangeStatus();

                if (!multiAcePanel.HasChanges)
                    throw new InvalidOperationException("Modifying one of multiple ACEs for same principal must trigger HasChanges!");
            }
            finally
            {
                try { Directory.Delete(testDir, recursive: true); } catch { }
            }
        }


        private static async Task TestAclChangePlanPipelineAndSemanticVerificationAsync()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_ChangePlanTest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);
            var aclService = new AclService();

            try
            {
                var (initialEntries, initialInherit, _) = aclService.GetSimAclForFolder(testDir);
                string initialSddl = aclService.GetSddl(testDir);

                // 1. BuildChangePlan: 継承OFF化 + 新規ACE追加
                var currentEntries = initialEntries.Select(e => e.Clone()).ToList();
                var newAce = new SimAclEntry
                {
                    AccountName = "Everyone",
                    Rights = FileSystemRights.ReadAndExecute,
                    AccessType = AccessControlType.Allow,
                    InheritanceFlags = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags = PropagationFlags.None
                };
                currentEntries.Add(newAce);

                var plan = aclService.BuildChangePlan(
                    testDir,
                    initialEntries,
                    currentEntries,
                    inherit: false, // 継承OFF化
                    originalInherit: initialInherit,
                    originalSddl: initialSddl);

                // 検証: 継承変更検知 & 昇格件数
                if (!plan.InheritanceChanged)
                    throw new InvalidOperationException("ChangePlan must detect inheritance change from true to false!");
                if (plan.InheritedAcesPromotedCount != initialEntries.Count(e => e.IsInherited))
                    throw new InvalidOperationException($"InheritedAcesPromotedCount mismatch: expected {initialEntries.Count(e => e.IsInherited)}, got {plan.InheritedAcesPromotedCount}");
                if (plan.Added.Count != 1)
                    throw new InvalidOperationException($"Added count mismatch: expected 1, got {plan.Added.Count}");

                // 2. Commit: ApplyChangePlanWithRollbackAsync
                var (hasModified, addedCount, removedCount, modifiedCount, snapshot) =
                    await aclService.ApplyChangePlanWithRollbackAsync(plan);

                if (!hasModified || addedCount != 1)
                    throw new InvalidOperationException("ApplyChangePlanWithRollbackAsync failed to apply addition!");

                // 3. Verify: VerifyChangePlan (セマンティック突合)
                var verifyResult = aclService.VerifyChangePlan(plan);
                if (!verifyResult.IsSuccess || verifyResult.StatusText != "正常")
                    throw new InvalidOperationException($"VerifyChangePlan failed! Discrepancies: {string.Join(", ", verifyResult.Discrepancies)}");

                // 4. Commit後の OriginalSddl 更新整合性検証 (Solレビュー Point 3)
                string postCommitSddl = aclService.GetSddl(testDir);
                var panel = new LiveAclPanelModel
                {
                    FolderPath = testDir,
                    FolderName = "TestDir",
                    OriginalSddl = postCommitSddl // 正しく最新SDDLがセットされた状態
                };
                // 直後に再度現行ディスクSDDLを取得して比較した場合、外部競合が出ないこと
                string checkSddl = aclService.GetSddl(testDir);
                bool conflictAfterCommit = !string.IsNullOrEmpty(panel.OriginalSddl) &&
                                           !string.Equals(checkSddl, panel.OriginalSddl, StringComparison.OrdinalIgnoreCase);
                if (conflictAfterCommit)
                    throw new InvalidOperationException("Post-commit OriginalSddl must match current disk SDDL without false conflict!");

                // 5. 意図的不一致の検出検証: 不正な期待値を持つプランで Verify が失敗すること
                var corruptedPlan = aclService.BuildChangePlan(testDir, initialEntries, currentEntries, inherit: true, originalInherit: initialInherit);
                corruptedPlan.ExpectedAfterEntries.Add(new SimAclEntry
                {
                    AccountName = "GhostUser_ShouldNotExist",
                    Rights = FileSystemRights.FullControl
                });
                var corruptVerify = aclService.VerifyChangePlan(corruptedPlan);
                if (corruptVerify.IsSuccess || corruptVerify.StatusText != "不一致検知")
                    throw new InvalidOperationException("VerifyChangePlan must detect discrepancy when actual ACL diverges from expected!");
            }
            finally
            {
                try { Directory.Delete(testDir, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// 24. 全体共通文法 (Check ➔ 変更点 ➔ Commit ➔ Verify) の一貫性検証
        /// ・スケルトン先行展開の実態作成と存在検証
        /// ・LinkFix ショートカット一括書き換えと .bak バックアップ・更新後実態検証
        /// ・MediaOptimizer 聖域保護フィルタと変更計画の不変性検証
        /// </summary>

        private static async Task TestOptimisticLockAndEffectiveAccessChangePointsAsync()
        {
            // =========================================================================
            // Part 1: EffectiveAccessService 実ファイルシステム（NTFS ACL）による実態走査テスト
            // =========================================================================
            string testRoot = Path.Combine(Path.GetTempPath(), "Test29_EffAccessReal_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testRoot);

            string adminUser = Environment.UserName;
            string targetUser = ((NTAccount)new SecurityIdentifier(WellKnownSidType.BuiltinGuestsSid, null).Translate(typeof(NTAccount))).Value;
            var subNormal = Path.Combine(testRoot, "01_NormalInherited");
            var subExplicit = Path.Combine(testRoot, "02_ExplicitBoundary");
            var subElevated = Path.Combine(testRoot, "03_PermissionElevated");
            var subSevered = Path.Combine(testRoot, "04_Severed");
            var subRestricted = Path.Combine(testRoot, "05_RestrictedParent");
            var subEnclave = Path.Combine(subRestricted, "06_EnclaveChild");
            var subNoAccess = Path.Combine(testRoot, "07_NoAccessParent");
            var subUnknownChild = Path.Combine(subNoAccess, "08_UnknownChild");

            Directory.CreateDirectory(subNormal);
            Directory.CreateDirectory(subExplicit);
            Directory.CreateDirectory(subElevated);
            Directory.CreateDirectory(subSevered);
            Directory.CreateDirectory(subRestricted);
            Directory.CreateDirectory(subEnclave);
            Directory.CreateDirectory(subNoAccess);
            Directory.CreateDirectory(subUnknownChild);

            try
            {
                // 1. ルート: AdminUser に FullControl、TargetUser に ReadAndExecute (親なし Baseline)
                var rootSec = new DirectorySecurity();
                rootSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                rootSec.AddAccessRule(new FileSystemAccessRule(adminUser, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                rootSec.AddAccessRule(new FileSystemAccessRule(targetUser, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(testRoot).SetAccessControl(rootSec);

                // 2. subNormal: ルートから継承そのまま (InheritedSame)
                // 何も設定しない (継承有効)

                // 3. subExplicit: 継承切断だが同一権限の明示ACE (ExplicitBoundary)
                var expSec = new DirectorySecurity();
                expSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                expSec.AddAccessRule(new FileSystemAccessRule(adminUser, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                expSec.AddAccessRule(new FileSystemAccessRule(targetUser, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(subExplicit).SetAccessControl(expSec);

                // 4. subElevated: 継承切断で権限昇格 (PermissionChanged)
                var eleSec = new DirectorySecurity();
                eleSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                eleSec.AddAccessRule(new FileSystemAccessRule(adminUser, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                eleSec.AddAccessRule(new FileSystemAccessRule(targetUser, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(subElevated).SetAccessControl(eleSec);

                // 5. subSevered: 継承切断でTargetUserの権限なし (InheritanceSevered)
                var sevSec = new DirectorySecurity();
                sevSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                sevSec.AddAccessRule(new FileSystemAccessRule(adminUser, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(subSevered).SetAccessControl(sevSec);

                // 6. subRestricted: 継承切断でTargetUserの権限なし (親はアクセス不可)
                var resSec = new DirectorySecurity();
                resSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                resSec.AddAccessRule(new FileSystemAccessRule(adminUser, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(subRestricted).SetAccessControl(resSec);

                // 7. subEnclave: 親はアクセス不可だが子でTargetUserに明示Allow (EnclaveGranted)
                var encSec = new DirectorySecurity();
                encSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                encSec.AddAccessRule(new FileSystemAccessRule(adminUser, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                encSec.AddAccessRule(new FileSystemAccessRule(targetUser, FileSystemRights.Modify, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(subEnclave).SetAccessControl(encSec);

                // 8. subUnknownChild: targetUser に ReadAndExecute (親が ScanUnavailable なので判定不能 Unknown になる)
                var unkChildSec = new DirectorySecurity();
                unkChildSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                unkChildSec.AddAccessRule(new FileSystemAccessRule(adminUser, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                unkChildSec.AddAccessRule(new FileSystemAccessRule(targetUser, FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(subUnknownChild).SetAccessControl(unkChildSec);

                // 9. subNoAccess: 走査不能 (ScanUnavailable) フォルダー
                // AclReaderHook により確実に UnauthorizedAccessException をシミュレート

                // 実サービスを実行！
                var effService = new EffectiveAccessService();
                effService.AclReaderHook = d =>
                {
                    if (d.FullName.Equals(subNoAccess, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new UnauthorizedAccessException("Simulated access denied for test");
                    }
                    return null;
                };

                var report = await effService.ScanEffectiveAccessAsync(
                    testRoot,
                    targetUser,
                    new List<PrincipalGroupMembership>(),
                    maxDepth: 5,
                    ct: CancellationToken.None);

                // --- 判定結果の実態アサーション ---
                // A. ルート: Baseline
                var rootItem = report.AccessibleFolders.FirstOrDefault(f => f.FolderPath.Equals(testRoot, StringComparison.OrdinalIgnoreCase));
                if (rootItem == null || rootItem.ChangeType != EffectiveAccessChangeType.Baseline || rootItem.IsChangePoint)
                    throw new InvalidOperationException($"EffectiveAccess: Root folder should be Baseline, but was {rootItem?.ChangeType}");

                // B. NormalChild: InheritedSame
                var normalItem = report.AccessibleFolders.FirstOrDefault(f => f.FolderPath.Equals(subNormal, StringComparison.OrdinalIgnoreCase));
                if (normalItem == null || normalItem.ChangeType != EffectiveAccessChangeType.InheritedSame || normalItem.IsChangePoint)
                    throw new InvalidOperationException($"EffectiveAccess: Normal child should be InheritedSame, but was {normalItem?.ChangeType}");

                // C. ExplicitChild: ExplicitBoundary (変化点)
                var explicitItem = report.AccessibleFolders.FirstOrDefault(f => f.FolderPath.Equals(subExplicit, StringComparison.OrdinalIgnoreCase));
                if (explicitItem == null || explicitItem.ChangeType != EffectiveAccessChangeType.ExplicitBoundary || !explicitItem.IsChangePoint)
                    throw new InvalidOperationException($"EffectiveAccess: Explicit child should be ExplicitBoundary, but was {explicitItem?.ChangeType}");

                // D. ElevatedChild: PermissionChanged (変化点)
                var elevatedItem = report.AccessibleFolders.FirstOrDefault(f => f.FolderPath.Equals(subElevated, StringComparison.OrdinalIgnoreCase));
                if (elevatedItem == null || elevatedItem.ChangeType != EffectiveAccessChangeType.PermissionChanged || !elevatedItem.IsChangePoint)
                    throw new InvalidOperationException($"EffectiveAccess: Elevated child should be PermissionChanged, but was {elevatedItem?.ChangeType}");

                // E. SeveredChild: InheritanceSevered (遮断フォルダー一覧に含まれ、変化点)
                var severedItem = report.SeveredFolders.FirstOrDefault(f => f.FolderPath.Equals(subSevered, StringComparison.OrdinalIgnoreCase));
                if (severedItem == null || severedItem.ChangeType != EffectiveAccessChangeType.InheritanceSevered || !severedItem.IsChangePoint)
                    throw new InvalidOperationException($"EffectiveAccess: Severed child should be in SeveredFolders as InheritanceSevered, but was {severedItem?.ChangeType}");

                // F. EnclaveChild: EnclaveGranted (飛び地・変化点)
                var enclaveItem = report.AccessibleFolders.FirstOrDefault(f => f.FolderPath.Equals(subEnclave, StringComparison.OrdinalIgnoreCase));
                if (enclaveItem == null || enclaveItem.ChangeType != EffectiveAccessChangeType.EnclaveGranted || !enclaveItem.IsChangePoint)
                    throw new InvalidOperationException($"EffectiveAccess: Enclave child should be EnclaveGranted, but was {enclaveItem?.ChangeType}");

                // G. subNoAccess: ScanUnavailable (走査不能・変化点)
                var unavailItem = report.UnavailableFolders.FirstOrDefault(f => f.FolderPath.Equals(subNoAccess, StringComparison.OrdinalIgnoreCase));
                if (unavailItem == null || unavailItem.ChangeType != EffectiveAccessChangeType.ScanUnavailable || !unavailItem.IsChangePoint)
                    throw new InvalidOperationException($"EffectiveAccess: NoAccess folder should be in UnavailableFolders as ScanUnavailable, but was {unavailItem?.ChangeType}");

                // H. subUnknownChild: Unknown (親が走査不能のため飛び地ではなく判定不能・変化点)
                var unknownItem = report.AccessibleFolders.FirstOrDefault(f => f.FolderPath.Equals(subUnknownChild, StringComparison.OrdinalIgnoreCase));
                if (unknownItem == null || unknownItem.ChangeType != EffectiveAccessChangeType.Unknown || !unknownItem.IsChangePoint)
                    throw new InvalidOperationException($"EffectiveAccess: Child of ScanUnavailable parent should be Unknown, but was {unknownItem?.ChangeType}");

                if (report.EnclaveCount < 1)
                    throw new InvalidOperationException($"EffectiveAccess: EnclaveCount should be >= 1, but was {report.EnclaveCount}");
                if (report.SeveredCount < 1)
                    throw new InvalidOperationException($"EffectiveAccess: SeveredCount should be >= 1, but was {report.SeveredCount}");
                if (report.UnavailableFolders.Count < 1)
                    throw new InvalidOperationException($"EffectiveAccess: UnavailableFolders count should be >= 1, but was {report.UnavailableFolders.Count}");
            }
            finally
            {
                // クリーンアップ: 後片付け前に Deny ルールを全消去して削除可能にする
                try
                {
                    var clearSec = new DirectorySecurity();
                    clearSec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                    clearSec.AddAccessRule(new FileSystemAccessRule(adminUser, FileSystemRights.FullControl, AccessControlType.Allow));
                    try { new DirectoryInfo(subSevered).SetAccessControl(clearSec); } catch { }
                    try { new DirectoryInfo(subRestricted).SetAccessControl(clearSec); } catch { }
                    try { new DirectoryInfo(subEnclave).SetAccessControl(clearSec); } catch { }
                    try { new DirectoryInfo(subNoAccess).SetAccessControl(clearSec); } catch { }
                    try { new DirectoryInfo(subUnknownChild).SetAccessControl(clearSec); } catch { }
                    Directory.Delete(testRoot, recursive: true);
                }
                catch { }
            }

            // =========================================================================
            // Part 2: Skeleton Deploy の競合判定と UI 判定ロジック検証
            // =========================================================================
            var mockResultClean = new DeploySkeletonResult
            {
                CreatedCount = 5,
                ConflictCount = 0
            };
            bool cleanSuccess = mockResultClean.FailedCount == 0 && mockResultClean.ConflictCount == 0;
            if (!cleanSuccess) throw new InvalidOperationException("Skeleton Deploy cleanSuccess logic failed!");

            var mockResultConflict = new DeploySkeletonResult
            {
                CreatedCount = 3,
                ConflictCount = 2
            };
            bool conflictCleanSuccess = mockResultConflict.FailedCount == 0 && mockResultConflict.ConflictCount == 0;
            if (conflictCleanSuccess) throw new InvalidOperationException("Skeleton Deploy conflict suppression failed (should not be clean success)!");

            // =========================================================================
            // Part 3: LinkFixer 楽観ロック (Optimistic Lock) & 直前ロールバックの実動検証
            // =========================================================================
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType != null)
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "Test29_LinkFix_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                string testLnk = Path.Combine(tempDir, "OptimisticTest.lnk");

                try
                {
                    dynamic wsh = Activator.CreateInstance(shellType)!;
                    dynamic sc = wsh.CreateShortcut(testLnk);
                    sc.TargetPath = @"C:\ExternalModified\OldPath.exe";
                    sc.Save();

                    var linkService = new LinkFixService();
                    var item = new LinkFixItem
                    {
                        FilePath = testLnk,
                        OldTarget = @"C:\OriginalScan\OldPath.exe", // 実態と不一致 (外部で変更された状態)
                        NewTarget = @"C:\NewTarget\App.exe",
                        FileType = ".lnk"
                    };

                    int fixedCount = await linkService.ExecuteFixAsync(new List<LinkFixItem> { item }, null, CancellationToken.None);
                    if (fixedCount > 0 || item.IsFixed)
                        throw new InvalidOperationException("LinkFixer optimistic lock failed: Modified shortcut should be skipped!");

                    if (!item.Status.Contains("外部変更検知"))
                        throw new InvalidOperationException($"LinkFixer status should mention external modification, but was: {item.Status}");

                    // 一時ロールバックファイルが残存していないこと
                    var rollbackFiles = Directory.GetFiles(tempDir, "*.rollback_*");
                    if (rollbackFiles.Length > 0)
                        throw new InvalidOperationException("Temporary rollback snapshot was not cleaned up!");

                    // 正常修復ケース: 楽観ロックが一致し、Verify成功で一時スナップショットが削除されること
                    var normalItem = new LinkFixItem
                    {
                        FilePath = testLnk,
                        OldTarget = @"C:\ExternalModified\OldPath.exe", // 実態と一致
                        NewTarget = @"C:\NewTarget\App.exe",
                        FileType = ".lnk"
                    };
                    int fixedNormal = await linkService.ExecuteFixAsync(new List<LinkFixItem> { normalItem }, null, CancellationToken.None);
                    if (fixedNormal != 1 || !normalItem.IsFixed)
                        throw new InvalidOperationException("LinkFixer normal execution failed!");

                    var remainingRollbacks = Directory.GetFiles(tempDir, "*.rollback_*");
                    if (remainingRollbacks.Length > 0)
                        throw new InvalidOperationException("Temporary rollback snapshot should be deleted after successful verify!");
                }
                finally
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }

            // =========================================================================
            // Part 4: 英語モード時の日本語文字（CJK）残留を機械的に全件検知
            // =========================================================================
            var origLang = LocalizationService.Instance.CurrentLanguage;
            try
            {
                LocalizationService.Instance.SetLanguage(AppLanguage.English);

                var japaneseRegex = new System.Text.RegularExpressions.Regex(@"[\u3040-\u309F\u30A0-\u30FF\u4E00-\u9FAF]");

                // 1. 全 ChangeType のバッジ文字列に日本語が含まれていないこと
                foreach (EffectiveAccessChangeType ctVal in Enum.GetValues(typeof(EffectiveAccessChangeType)))
                {
                    var dummyItem = new EffectiveFolderAccessItem { ChangeType = ctVal };
                    string badge = dummyItem.ChangeBadgeText;
                    if (japaneseRegex.IsMatch(badge))
                    {
                        throw new InvalidOperationException($"English localization failed: ChangeBadgeText for {ctVal} contains Japanese characters: '{badge}'");
                    }
                }

                // 2. 権限レベル表示に日本語が含まれていないこと
                foreach (EffectivePermissionLevel plVal in Enum.GetValues(typeof(EffectivePermissionLevel)))
                {
                    var dummyItem = new EffectiveFolderAccessItem { PermissionLevel = plVal };
                    string rights = dummyItem.FormattedRights;
                    if (japaneseRegex.IsMatch(rights))
                    {
                        throw new InvalidOperationException($"English localization failed: FormattedRights for {plVal} contains Japanese characters: '{rights}'");
                    }
                }

                // 3. 継承バッジ文字列に日本語が含まれていないこと
                var inheritedItem = new EffectiveFolderAccessItem { IsInherited = true };
                var explicitItem = new EffectiveFolderAccessItem { IsInherited = false };
                if (japaneseRegex.IsMatch(inheritedItem.InheritanceBadgeText) || japaneseRegex.IsMatch(explicitItem.InheritanceBadgeText))
                {
                    throw new InvalidOperationException("English localization failed: InheritanceBadgeText contains Japanese characters!");
                }

                // 4. AppStrings 辞書の英訳漏れチェック
                var dictErrors = Strings.ValidateTranslations(AppLanguage.English);
                if (dictErrors.Count > 0)
                {
                    throw new InvalidOperationException($"English translation missing: {string.Join(", ", dictErrors)}");
                }

                // 5. 英語モードでの実走査レポート全項目（GrantSource, GrantPathTrace, Membership, Notice）の機械的 CJK ゼロ検証
                var engReport = new EffectiveAccessAuditReport
                {
                    ResolutionStatusText = string.Format(Strings.RevResAdConnected, 3),
                    GroupMemberships = new List<PrincipalGroupMembership>
                    {
                        new PrincipalGroupMembership
                        {
                            GroupName = "FinanceGroup",
                            DisplayName = "Finance",
                            IsDirect = true,
                            NestingDepth = 0,
                            MembershipPath = Strings.RevTargetGroupSelf // 対象自身がグループの場合
                        },
                        new PrincipalGroupMembership
                        {
                            GroupName = "AccountingTeam",
                            DisplayName = "Accounting",
                            IsDirect = true,
                            NestingDepth = 1,
                            MembershipPath = Strings.RevDirectMembership
                        },
                        new PrincipalGroupMembership
                        {
                            GroupName = "DomainAdmins",
                            DisplayName = "Domain Admins",
                            IsDirect = false,
                            NestingDepth = 2,
                            MembershipPath = string.Format(Strings.RevNestedMembership, 2)
                        }
                    }
                };

                // GroupMembership の文字列検証
                foreach (var gm in engReport.GroupMemberships)
                {
                    if (japaneseRegex.IsMatch(gm.DirectStatusText))
                        throw new InvalidOperationException($"English DirectStatusText contains Japanese: '{gm.DirectStatusText}'");
                    if (japaneseRegex.IsMatch(gm.MembershipPath))
                        throw new InvalidOperationException($"English MembershipPath contains Japanese: '{gm.MembershipPath}'");
                }

                if (japaneseRegex.IsMatch(engReport.ResolutionStatusText))
                    throw new InvalidOperationException($"English ResolutionStatusText contains Japanese: '{engReport.ResolutionStatusText}'");

                if (japaneseRegex.IsMatch(engReport.UncShareNotice))
                    throw new InvalidOperationException($"English UncShareNotice contains Japanese: '{engReport.UncShareNotice}'");

                // EvaluateEffectiveAccessOnAcl による実生成 GrantSource / GrantPathTrace 検証
                var testSec = new DirectorySecurity();
                testSec.AddAccessRule(new FileSystemAccessRule("BUILTIN\\Users", FileSystemRights.ReadAndExecute, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
                testSec.AddAccessRule(new FileSystemAccessRule(Environment.UserName, FileSystemRights.Modify, InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));

                var groupMap = new Dictionary<string, PrincipalGroupMembership>(StringComparer.OrdinalIgnoreCase)
                {
                    ["BUILTIN\\Users"] = engReport.GroupMemberships[0]
                };
                var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Environment.UserName };

                var effItem = new EffectiveAccessService().EvaluateEffectiveAccessOnAcl(
                    testSec,
                    Environment.UserName,
                    targetNames,
                    groupMap,
                    @"C:\TestPath",
                    "TestPath");

                if (effItem != null)
                {
                    if (japaneseRegex.IsMatch(effItem.GrantSource))
                        throw new InvalidOperationException($"English GrantSource contains Japanese: '{effItem.GrantSource}'");
                    if (japaneseRegex.IsMatch(effItem.GrantPathTrace))
                        throw new InvalidOperationException($"English GrantPathTrace contains Japanese: '{effItem.GrantPathTrace}'");
                }

                // 遮断アイテムと走査不能アイテムの文字列検証
                var sevItem = new EffectiveFolderAccessItem
                {
                    ChangeType = EffectiveAccessChangeType.InheritanceSevered,
                    GrantSource = Strings.RevGrantSeveredSource,
                    GrantPathTrace = string.Format(Strings.RevTraceSeveredFormat, "ParentFolder", "Read")
                };
                if (japaneseRegex.IsMatch(sevItem.GrantSource) || japaneseRegex.IsMatch(sevItem.GrantPathTrace))
                    throw new InvalidOperationException($"English Severed item contains Japanese: '{sevItem.GrantSource}' / '{sevItem.GrantPathTrace}'");

                var unavailTestItem = new EffectiveFolderAccessItem
                {
                    ChangeType = EffectiveAccessChangeType.ScanUnavailable,
                    GrantSource = Strings.RevChangeUnavailable,
                    GrantPathTrace = Strings.RevTraceUnavailable
                };
                if (japaneseRegex.IsMatch(unavailTestItem.GrantSource) || japaneseRegex.IsMatch(unavailTestItem.GrantPathTrace))
                    throw new InvalidOperationException($"English Unavailable item contains Japanese: '{unavailTestItem.GrantSource}' / '{unavailTestItem.GrantPathTrace}'");

                var unkTestItem = new EffectiveFolderAccessItem
                {
                    ChangeType = EffectiveAccessChangeType.Unknown,
                    GrantSource = Strings.RevChangeUnknown,
                    GrantPathTrace = Strings.RevTraceParentUnavailable
                };
                if (japaneseRegex.IsMatch(unkTestItem.GrantSource) || japaneseRegex.IsMatch(unkTestItem.GrantPathTrace))
                    throw new InvalidOperationException($"English Unknown item contains Japanese: '{unkTestItem.GrantSource}' / '{unkTestItem.GrantPathTrace}'");

                // 6. 追加された RevTargetGroupSelf, RevErrFolderNotFound, RevResPreResolved, RevGrantDefault の CJK ゼロ検証
                if (japaneseRegex.IsMatch(Strings.RevTargetGroupSelf))
                    throw new InvalidOperationException($"English RevTargetGroupSelf contains Japanese: '{Strings.RevTargetGroupSelf}'");
                if (japaneseRegex.IsMatch(Strings.RevResPreResolved))
                    throw new InvalidOperationException($"English RevResPreResolved contains Japanese: '{Strings.RevResPreResolved}'");
                if (japaneseRegex.IsMatch(Strings.RevGrantDefault))
                    throw new InvalidOperationException($"English RevGrantDefault contains Japanese: '{Strings.RevGrantDefault}'");
                var notFoundMsg = string.Format(Strings.RevErrFolderNotFound, @"C:\NonExistent");
                if (japaneseRegex.IsMatch(notFoundMsg))
                    throw new InvalidOperationException($"English RevErrFolderNotFound contains Japanese: '{notFoundMsg}'");
            }
            finally
            {
                LocalizationService.Instance.SetLanguage(origLang);
            }
        }

    }
}