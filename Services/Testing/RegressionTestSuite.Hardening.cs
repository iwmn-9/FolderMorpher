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
        /// [DOMAIN 6/7] MFT & Defensive Hardening 邨ｱ蜷医ユ繧ｹ繝・        /// </summary>
        public static async Task TestDomain_MftAndHardeningAsync()
        {
            TestMftDataRunDecoderLcnCalculation();
            TestAppSettingsSharedCacheResolution();
            await TestAstraHardeningAndDefensiveMutationAsync();
        }

        public static void TestMftDataRunDecoderLcnCalculation()
        {
            // 合成 NTFS Data Runlist のバイナリ作成:
            // 各ランのヘッダーバイト: (offsetBytes << 4) | lengthBytes
            //
            // ラン 1: 長さ 128 (0x80 -> 1 byte), オフセット +4096 (0x1000 -> 2 bytes [0x00, 0x10])
            //   header = 0x21
            //   期待 StartLcn = 0 + 4096 = 4096, ClusterCount = 128
            //
            // ラン 2: 長さ 256 (0x0100 -> 2 bytes [0x00, 0x01]), オフセット +1024 (0x0400 -> 2 bytes [0x00, 0x04])
            //   header = 0x22
            //   期待 StartLcn = 4096 + 1024 = 5120, ClusterCount = 256
            //
            // ラン 3: 長さ 64 (0x40 -> 1 byte), オフセット -512 (0xFE00 -> 2 bytes [0x00, 0xFE], 負のデルタ符号拡張テスト)
            //   header = 0x21
            //   期待 StartLcn = 5120 - 512 = 4608, ClusterCount = 64
            //
            // 終端バイト: 0x00
            byte[] syntheticRunlist = new byte[]
            {
                // Run 1: header 0x21, length 0x80, offset 0x00, 0x10
                0x21, 0x80, 0x00, 0x10,
                // Run 2: header 0x22, length 0x00, 0x01, offset 0x00, 0x04
                0x22, 0x00, 0x01, 0x00, 0x04,
                // Run 3: header 0x21, length 0x40, offset 0x00, 0xFE
                0x21, 0x40, 0x00, 0xFE,
                // End marker
                0x00
            };

            // 重要: fallbackStartLcn に非ゼロ（例: 8888888）を渡す。
            // 過去の二重加算バグでは currentLcn が fallbackStartLcn で初期化されていたため、
            // 最初の StartLcn が 8888888 + 4096 = 8892984 になってしまっていた！
            long dummyFallback = 8888888;
            var extents = MftDataRunDecoder.DecodeDataRuns(syntheticRunlist, 0, dummyFallback);

            if (extents.Count != 3)
            {
                throw new InvalidOperationException($"デコードされた Extent 数が不正です。期待値: 3, 実際: {extents.Count}");
            }

            // 検証 1: 最初の LCN が 0 基準の 4096 であること (fallback が加算されていないこと)
            if (extents[0].StartLcn != 4096)
            {
                throw new InvalidOperationException(
                    $"LCN二重加算バグ検出: 最初のランの StartLcn が 0 基準で計算されていません。" +
                    $" 期待値: 4096, 実際: {extents[0].StartLcn} (fallback加算の可能性: {dummyFallback})");
            }
            if (extents[0].ClusterCount != 128)
            {
                throw new InvalidOperationException($"ラン 1 の ClusterCount が不正です。期待値: 128, 実際: {extents[0].ClusterCount}");
            }

            // 検証 2: 2番目のランが正しく累積加算されていること (4096 + 1024 = 5120)
            if (extents[1].StartLcn != 5120)
            {
                throw new InvalidOperationException(
                    $"ラン 2 の StartLcn が不正です。期待値: 5120, 実際: {extents[1].StartLcn}");
            }
            if (extents[1].ClusterCount != 256)
            {
                throw new InvalidOperationException($"ラン 2 の ClusterCount が不正です。期待値: 256, 実際: {extents[1].ClusterCount}");
            }

            // 検証 3: 3番目のランの負の差分 (符号拡張) が正しく計算されていること (5120 - 512 = 4608)
            if (extents[2].StartLcn != 4608)
            {
                throw new InvalidOperationException(
                    $"ラン 3 の 負のデルタ StartLcn が不正です。期待値: 4608, 実際: {extents[2].StartLcn}");
            }
            if (extents[2].ClusterCount != 64)
            {
                throw new InvalidOperationException($"ラン 3 の ClusterCount が不正です。期待値: 64, 実際: {extents[2].ClusterCount}");
            }
        }

        /// <summary>
        /// 5. Live ACL: 特殊適用先フラグ（InheritanceFlags &amp; PropagationFlags）の保持・適用テスト
        /// 「このフォルダーのみ (None, None)」や「サブフォルダーおよびファイルのみ (Container|Object, InheritOnly)」などの
        /// 特殊ACEが、一律 ContainerInherit|ObjectInherit, None に変質しないことを検証。
        /// </summary>

        private static void TestAppSettingsSharedCacheResolution()
        {
            var service = new AppSettingsService();
            var settings = service.Current;
            var localDefault = service.GetDefaultLocalBaseDirectory();

            // 1. 空の参照先 -> ローカル既定値
            settings.CacheReadPath = "";
            var readBase = service.GetEffectiveReadBaseDirectory();
            if (readBase != localDefault)
            {
                throw new InvalidOperationException($"AppSettings エラー: 空の参照先でローカル既定値が返っていません ({readBase})");
            }

            // 2. 存在しない共有UNCパス + Fallback有効 -> ローカル既定値にフォールバック
            settings.CacheReadPath = @"\\NonExistentFakeServer\FakeShare\Cache";
            settings.FallbackToLocalOnReadError = true;
            readBase = service.GetEffectiveReadBaseDirectory();
            if (readBase != localDefault)
            {
                throw new InvalidOperationException($"AppSettings エラー: 存在しない共有パスでローカルフォールバックが機能していません ({readBase})");
            }

            // 3. 書き込みモード: Local -> ローカル既定値
            settings.WriteMode = CacheWriteMode.Local;
            var writeBase = service.GetEffectiveWriteBaseDirectory();
            if (writeBase != localDefault)
            {
                throw new InvalidOperationException($"AppSettings エラー: Local書き込みモードでローカル既定値が返っていません ({writeBase})");
            }

            // 4. 書き込みモード: Custom -> 指定パス
            string tempDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_SettingsTest_" + Guid.NewGuid().ToString("N"));
            try
            {
                settings.WriteMode = CacheWriteMode.Custom;
                settings.CacheWriteCustomPath = tempDir;
                writeBase = service.GetEffectiveWriteBaseDirectory();
                if (writeBase != tempDir)
                {
                    throw new InvalidOperationException($"AppSettings エラー: Custom書き込みモードで指定パスが返っていません ({writeBase})");
                }
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
                // 元に戻す
                settings.CacheReadPath = "";
                settings.WriteMode = CacheWriteMode.Local;
                settings.CacheWriteCustomPath = "";
            }
        }

        private static bool AreAclRulesEquivalent(DirectorySecurity original, DirectorySecurity restored, out string reason)
        {
            reason = "";
            if (original.AreAccessRulesProtected != restored.AreAccessRulesProtected)
            {
                reason = $"AreAccessRulesProtected mismatch (orig: {original.AreAccessRulesProtected}, rest: {restored.AreAccessRulesProtected})";
                return false;
            }

            var origRules = original.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .OrderBy(r => r.IdentityReference.Value)
                .ThenBy(r => r.AccessControlType)
                .ThenBy(r => r.FileSystemRights)
                .ToList();

            var restRules = restored.GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .OrderBy(r => r.IdentityReference.Value)
                .ThenBy(r => r.AccessControlType)
                .ThenBy(r => r.FileSystemRights)
                .ToList();

            if (origRules.Count != restRules.Count)
            {
                reason = $"Rule count mismatch (orig: {origRules.Count}, rest: {restRules.Count})";
                return false;
            }

            for (int i = 0; i < origRules.Count; i++)
            {
                var o = origRules[i];
                var r = restRules[i];
                if (o.IdentityReference.Value != r.IdentityReference.Value)
                {
                    reason = $"Identity mismatch at [{i}]: {o.IdentityReference.Value} vs {r.IdentityReference.Value}";
                    return false;
                }
                if (o.AccessControlType != r.AccessControlType)
                {
                    reason = $"AccessControlType mismatch at [{i}]: {o.AccessControlType} vs {r.AccessControlType}";
                    return false;
                }
                if (o.FileSystemRights != r.FileSystemRights)
                {
                    reason = $"FileSystemRights mismatch at [{i}]: {o.FileSystemRights} vs {r.FileSystemRights}";
                    return false;
                }
                if (o.InheritanceFlags != r.InheritanceFlags)
                {
                    reason = $"InheritanceFlags mismatch at [{i}]: {o.InheritanceFlags} vs {r.InheritanceFlags}";
                    return false;
                }
                if (o.PropagationFlags != r.PropagationFlags)
                {
                    reason = $"PropagationFlags mismatch at [{i}]: {o.PropagationFlags} vs {r.PropagationFlags}";
                    return false;
                }
                if (o.IsInherited != r.IsInherited)
                {
                    reason = $"IsInherited mismatch at [{i}]: {o.IsInherited} vs {r.IsInherited}";
                    return false;
                }
            }

            return true;
        }


        private static async Task TestAstraHardeningAndDefensiveMutationAsync()
        {
            // 1. H1: SimAclEntry.Clone() のフラグ保持
            var specialEntry = new SimAclEntry
            {
                AccountName = @"CORP\SpecialAdmin",
                InheritanceFlags = InheritanceFlags.ContainerInherit,
                PropagationFlags = PropagationFlags.NoPropagateInherit,
                AccessType = AccessControlType.Deny
            };
            var cloned = specialEntry.Clone();
            if (cloned.InheritanceFlags != InheritanceFlags.ContainerInherit ||
                cloned.PropagationFlags != PropagationFlags.NoPropagateInherit)
            {
                throw new InvalidOperationException($"H1 Fail: SimAclEntry.Clone corrupted PropagationFlags! Expected NoPropagateInherit, got {cloned.PropagationFlags}");
            }

            // AppliesTo に未知文字列を渡してもフラグが破壊されないこと
            cloned.AppliesTo = "カスタム適用範囲（未知）";
            if (cloned.InheritanceFlags != InheritanceFlags.ContainerInherit ||
                cloned.PropagationFlags != PropagationFlags.NoPropagateInherit)
            {
                throw new InvalidOperationException("H1 Fail: AppliesTo setter corrupted flags on unknown display text!");
            }

            // 2. H2: スケルトン展開の既存フォルダ保護 & DeploySkeletonResult
            var testTemp = Path.Combine(Path.GetTempPath(), $"FM_HardeningTest_{Guid.NewGuid():N}");
            Directory.CreateDirectory(testTemp);

            try
            {
                var existingFolder = Path.Combine(testTemp, "ExistingSub");
                Directory.CreateDirectory(existingFolder);

                var simService = new SimulationProjectService();
                var simTree = new List<SimFolderNode>
                {
                    new SimFolderNode
                    {
                        Name = "ExistingSub",
                        InheritAcl = false, // 既存フォルダに対して勝手に継承遮断をかけないかのテスト
                        AclEntries = { new SimAclEntry { AccountName = "DummyUser", Rights = FileSystemRights.FullControl } },
                        Children = { new SimFolderNode { Name = "NewChild" } }
                    }
                };

                var deployRes = await simService.DeploySkeletonAsync(simTree, testTemp, null, CancellationToken.None);
                if (deployRes.SkippedExistingCount != 1) // ExistingSub が既存スキップ
                {
                    throw new InvalidOperationException($"H2 Fail: SkippedExistingCount expected 1, got {deployRes.SkippedExistingCount}");
                }
                if (deployRes.CreatedCount != 1) // NewChild のみ新規作成
                {
                    throw new InvalidOperationException($"H2 Fail: CreatedCount expected 1, got {deployRes.CreatedCount}");
                }
                if (!Directory.Exists(Path.Combine(existingFolder, "NewChild")))
                {
                    throw new InvalidOperationException("H2 Fail: NewChild folder was not created!");
                }

                // 3. H4 & M4: 重複削除の楽観的ロック & 存在しないファイルのカウント除外
                var dummyFile = Path.Combine(testTemp, "target.txt");
                File.WriteAllText(dummyFile, "initial content");
                var fi = new FileInfo(dummyFile);
                var plan = new AuditCleanupPlan
                {
                    FullPath = dummyFile,
                    FileName = "target.txt",
                    Size = fi.Length,
                    ExpectedLastWriteTimeUtc = fi.LastWriteTimeUtc.AddMinutes(-10) // 意図的に不一致（スキャン後に更新された想定）
                };

                var auditRes = AuditCleanupService.ExecutePlan(new[] { plan });
                if (auditRes.SuccessCount != 0 || auditRes.FreedBytes != 0)
                {
                    throw new InvalidOperationException("H4 Fail: Modified file must NOT be deleted and SuccessCount must be 0!");
                }
                if (!File.Exists(dummyFile))
                {
                    throw new InvalidOperationException("H4 Fail: Modified file was deleted despite timestamp discrepancy!");
                }

                // M4: 存在しないファイルの削除要求
                var missingPlan = new AuditCleanupPlan
                {
                    FullPath = Path.Combine(testTemp, "ghost.txt"),
                    FileName = "ghost.txt",
                    Size = 1000
                };
                var missingRes = AuditCleanupService.ExecutePlan(new[] { missingPlan });
                if (missingRes.SuccessCount != 0 || missingRes.FreedBytes != 0 || missingRes.DeletedPaths.Count != 0)
                {
                    throw new InvalidOperationException("M4 Fail: Non-existent file must NOT increment SuccessCount or FreedBytes!");
                }

                // 4. H2: 重複原本が消失または変更されている場合の重複削除防止テスト
                var origSample = Path.Combine(testTemp, "original_candidate.txt");
                var dupSample = Path.Combine(testTemp, "duplicate_target.txt");
                File.WriteAllText(origSample, "original content");
                File.WriteAllText(dupSample, "duplicate content");

                var dupPlan = new AuditCleanupPlan
                {
                    FullPath = dupSample,
                    FileName = "duplicate_target.txt",
                    Size = new FileInfo(dupSample).Length,
                    OriginalCandidatePath = origSample,
                    OriginalExpectedSize = 99999 // 意図的に不一致（原本サイズ異常）
                };
                var dupRes = AuditCleanupService.ExecutePlan(new[] { dupPlan });
                if (dupRes.SuccessCount != 0 || !File.Exists(dupSample))
                {
                    throw new InvalidOperationException("H2 Fail: Duplicate file must NOT be deleted when original size mismatch!");
                }

                // 原本が存在しない場合
                dupPlan.OriginalExpectedSize = new FileInfo(origSample).Length;
                dupPlan.OriginalCandidatePath = Path.Combine(testTemp, "missing_original.txt");
                var missingOrigRes = AuditCleanupService.ExecutePlan(new[] { dupPlan });
                if (missingOrigRes.SuccessCount != 0 || !File.Exists(dupSample))
                {
                    throw new InvalidOperationException("H2 Fail: Duplicate file must NOT be deleted when original does not exist!");
                }

                // 5. M2: LiveAclDiffItem の AccessType & 詳細権限ビット差分 (Details)
                var aclService = new AclService();
                var origList = new List<SimAclEntry>
                {
                    new SimAclEntry { AccountName = "Alice", Rights = FileSystemRights.Read, AccessType = AccessControlType.Allow }
                };
                var curList = new List<SimAclEntry>
                {
                    new SimAclEntry { AccountName = "Alice", Rights = FileSystemRights.FullControl, AccessType = AccessControlType.Allow },
                    new SimAclEntry { AccountName = "Bob", Rights = FileSystemRights.Write, AccessType = AccessControlType.Deny }
                };
                var changePlan = aclService.BuildChangePlan(testTemp, origList, curList, inherit: true, originalInherit: true);
                var bobDiff = changePlan.DiffItems.FirstOrDefault(d => d.AccountName == "Bob");
                if (bobDiff == null || bobDiff.AccessType != AccessControlType.Deny || 
                    (bobDiff.AccessTypeDisplay != "⛔ 拒否" && bobDiff.AccessTypeDisplay != "⛔ Deny"))
                {
                    throw new InvalidOperationException("M2 Fail: Deny entry must have AccessType = Deny and display ⛔ 拒否/Deny!");
                }

                var aliceDiff = changePlan.DiffItems.FirstOrDefault(d => d.AccountName == "Alice");
                if (aliceDiff == null || string.IsNullOrEmpty(aliceDiff.Details) || 
                    (!aliceDiff.Details.Contains("+フルコントロール") && !aliceDiff.Details.Contains("+Full Control")))
                {
                    throw new InvalidOperationException($"M2 Fail: Alice diff Details must contain detailed bit differences! Got: '{aliceDiff?.Details}'");
                }

                // 6. M1: Effective Access 異ドメイン同名グループの誤マッチ抑止検証
                var effService = new EffectiveAccessService();
                var mockDirSec = new DirectorySecurity();
                // ACLには別ドメインの OTHERDOMAIN\Sales (または同名グループ) のACEが存在する想定
                var targetAcc = "CORP\\Taro";
                var tNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { targetAcc };

                // 所属グループ: CORP\Sales (SID: S-1-5-21-1111-9999)
                var grpMap = new Dictionary<string, PrincipalGroupMembership>(StringComparer.OrdinalIgnoreCase);
                var corpGroup = new PrincipalGroupMembership
                {
                    GroupName = "CORP\\Sales",
                    DisplayName = "Sales (CORP)",
                    Sid = "S-1-5-21-1111-9999",
                    IsDirect = true
                };
                grpMap[corpGroup.GroupName] = corpGroup;
                grpMap[corpGroup.Sid] = corpGroup;

                // 異ドメインの OTHERDOMAIN\Sales (SID: S-1-5-21-2222-9999) がACLに設定されている場合
                var otherSid = new SecurityIdentifier("S-1-5-21-2222-9999");
                var otherGroupRule = new FileSystemAccessRule(
                    otherSid,
                    FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow);

                // ドメイン修飾または異なるSIDを持つACEは CORP\Sales に誤爆一致してはならない
                mockDirSec.AddAccessRule(otherGroupRule);
                var crossDomainEval = effService.EvaluateEffectiveAccessOnAcl(mockDirSec, targetAcc, tNames, grpMap, testTemp, "TestFolder");
                if (crossDomainEval != null)
                {
                    throw new InvalidOperationException("M1 Fail: Cross-domain group (OTHERDOMAIN\\Sales / other SID) must NOT match CORP\\Sales!");
                }
            }
            finally
            {
                try { Directory.Delete(testTemp, recursive: true); } catch { }
            }
        }

        /// <summary>
        /// 26. Bilingual Localization (JA <-> EN) Display & Model Binding Fidelity:
        /// 言語切り替え時に、SimAclEntry, LiveAclDiffItem, AuditItem, SimFolderNode, AclInheritanceHelper が
        /// 日本語・英語の両方で正確に切り替わること（日本語モードで既存アサーション維持、英語モードで日本語が混じらないこと）を検証。
        /// </summary>
    }
}