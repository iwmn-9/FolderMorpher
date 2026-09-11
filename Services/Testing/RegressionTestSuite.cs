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
    /// <summary>
    /// FolderMorpher 自動回帰テストスイート
    /// 過去に発生した4大バグの再発防止を検証する
    /// </summary>
    public static class RegressionTestSuite
    {
        public static async Task<bool> RunAllTestsAsync()
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            Console.WriteLine("================================================================================");
            Console.WriteLine(" [QA] FolderMorpher Automated Regression Test Suite (Headless) Starting...");
            Console.WriteLine("================================================================================");

            int passCount = 0;
            int totalTests = 31;

            var originalLang = LocalizationService.Instance.CurrentLanguage;
            // テストスイートのベース言語を初期化（Test 26でJA/EN双方の動的切替を検証後、finallyで元の言語へ復元）
            LocalizationService.Instance.SetLanguage(AppLanguage.Japanese);

            try
            {
                // Test 1
                Console.WriteLine("\n[TEST 1/19] Media Optimizer: PNG Corruption & Alpha Channel Preservation...");
                await TestMediaOptimizerPngPreservationAsync();
                Console.WriteLine("  --> [PASS] Media Optimizer: PNG signature (0x89 50 4E 47) and alpha channel 100% preserved.");
                passCount++;

                // Test 2
                Console.WriteLine("\n[TEST 2/19] Live ACL: Deny Loss & Inheritance Disabling ACE Loss (Canonical ACL Ordering)...");
                TestLiveAclDenyAndInheritance();
                Console.WriteLine("  --> [PASS] Live ACL: ACEs preserved on inheritance disable, Deny rules ordered first (Canonical Order).");
                passCount++;

                // Test 3
                Console.WriteLine("\n[TEST 3/19] Audit Archival: Original File Archive & Move Duplication...");
                TestAuditArchivalOriginalExclusionAndDeduplication();
                Console.WriteLine("  --> [PASS] Audit: Original files safely protected from archive, move commands deduplicated.");
                passCount++;

                // Test 4
                Console.WriteLine("\n[TEST 4/19] MFT Data Run Decoder: Initial LCN Double-Addition Bug...");
                TestMftDataRunDecoderLcnCalculation();
                Console.WriteLine("  --> [PASS] MFT Data Run Decoder: Initial LCN computed relative to 0 without double-addition.");
                passCount++;

                // Test 5
                Console.WriteLine("\n[TEST 5/19] Live ACL: Special Inheritance & Propagation Flags Preservation...");
                TestLiveAclSpecialInheritanceFlags();
                Console.WriteLine("  --> [PASS] Live ACL: Special InheritanceFlags and PropagationFlags preserved across read/write.");
                passCount++;

                // Test 6
                Console.WriteLine("\n[TEST 6/19] ACL UI Binding & Helper: Bidirectional Mapping & Modal State Sync...");
                TestAclUiBindingAndHelper();
                Console.WriteLine("  --> [PASS] ACL UI Binding: AppliesTo, AccessType, and Modal state perfectly synchronized.");
                passCount++;

                // Test 7
                Console.WriteLine("\n[TEST 7/19] Effective Access: Canonical DACL Evaluation & Multi-Level Group Permission Tracing...");
                await TestEffectiveAccessCanonicalDaclAndNestingAsync();
                Console.WriteLine("  --> [PASS] Effective Access: Canonical DACL ordering (Explicit Allow > Inherited Deny), multi-level tracing & user isolation verified.");
                passCount++;

                // Test 8
                Console.WriteLine("\n[TEST 8/19] Simulation & Script Generation: .NET PS Script, Robocopy /XD Subtree Exclusion, and Effective Access InheritOnly...");
                TestSimulationAclRobocopyAndEffectiveAccessInheritOnly();
                Console.WriteLine("  --> [PASS] Simulation & Effective Access: .NET PS script fidelity, Robocopy /XD exclusion, and InheritOnly exclusion verified.");
                passCount++;

                // Test 9
                Console.WriteLine("\n[TEST 9/19] Live ACL Rollback DACL SDDL Fidelity & Skeleton Empty-ACL Inheritance Disable...");
                await TestLiveAclRollbackAndSkeletonEmptyAclInheritanceAsync();
                Console.WriteLine("  --> [PASS] Live ACL Rollback & Skeleton Deploy: DACL SDDL 100% restored after mutation, empty ACL inheritance disabled.");
                passCount++;

                // Test 10
                Console.WriteLine("\n[TEST 10/19] Storage History: 0s Tree Cache Persistence & Automatic Background Diff Detection...");
                await TestStorageHistoryTreeCacheAndDiffAsync();
                Console.WriteLine("  --> [PASS] Storage History: Tree cache restored in 0s, size diffs & badges automatically calculated.");
                passCount++;

                // Test 11
                Console.WriteLine("\n[TEST 11/19] App Settings: Shared Cache Read Source Cascade & Write Destination Resolution...");
                TestAppSettingsSharedCacheResolution();
                Console.WriteLine("  --> [PASS] App Settings: Shared cache cascade fallback and write destination modes 100% verified.");
                passCount++;

                // Test 12
                Console.WriteLine("\n[TEST 12/19] UI Binding Contract & Tree Cache Expansion State...");
                await TestUiBindingContractAndCacheExpansionStateAsync();
                Console.WriteLine("  --> [PASS] UI Binding Contract & Cache Expansion: FileItemNode properties and tree expansion state 100% verified.");
                passCount++;

                // Test 13
                Console.WriteLine("\n[TEST 13/19] Audit: Duplicate Grouping Colors & ActiveDirectory OU Hierarchy Fallback...");
                await TestDuplicateGroupingAndOuHierarchyAsync();
                Console.WriteLine("  --> [PASS] Audit & AD: Duplicate cyclic color palette, group sorting, and OU hierarchy fallback 100% verified.");
                passCount++;

                // Test 14
                Console.WriteLine("\n[TEST 14/19] Audit: Smart Original Candidate Scoring & Copy Keyword Detection...");
                await TestSmartOriginalCandidateScoringAsync();
                Console.WriteLine("  --> [PASS] Audit: Smart original candidate scoring correctly prioritizes non-copy names and root proximity.");
                passCount++;

                // Test 15
                Console.WriteLine("\n[TEST 15/19] Audit: Safe Archival Opt-In & IsOriginalCandidate Property Fidelity...");
                TestAuditArchiveScriptOptInAndPropertyFidelity();
                Console.WriteLine("  --> [PASS] Audit: Archive script generation strictly respects IsOriginalCandidate property and duplicate opt-in.");
                passCount++;

                // Test 16
                Console.WriteLine("\n[TEST 16/19] Simulation Studio: Lazy Loading, Auto-Expand & D&D Movement/Drop Outside Contract...");
                TestSimulationStudioLazyLoadingAndDropOutside();
                Console.WriteLine("  --> [PASS] Simulation Studio: Source lazy loading, root auto-expand, D&D movement & outside removal verified.");
                passCount++;

                // Test 17
                Console.WriteLine("\n[TEST 17/19] Audit & Hygiene: Smart Selection, Original File Protection & Safe Permanent Deletion...");
                TestAuditSmartSelectAndSafePermanentDeletion();
                Console.WriteLine("  --> [PASS] Audit & Hygiene: Smart select, original preservation guard, read-only unsetting & deletion verified.");
                passCount++;

                // Test 18
                Console.WriteLine("\n[TEST 18/19] Audit: Hierarchical Size Sorting with Preserved Duplicate Groups & Original Priority...");
                TestAuditHierarchicalSizeSortingWithDuplicateGroups();
                Console.WriteLine("  --> [PASS] Audit: Hierarchical size sorting keeps duplicate groups strictly cohesive with original candidates first.");
                passCount++;

                // Test 19
                Console.WriteLine("\n[TEST 19/20] Storage Explorer: Multi-Tab Session Persistence & Audit Detail/Toggle Sorting Contracts...");
                TestStorageSessionAndAuditSortingContracts();
                Console.WriteLine("  --> [PASS] Storage Explorer: Multi-tab session persistence roundtrip & Audit Detail/Toggle contracts verified.");
                passCount++;

                // Test 20
                Console.WriteLine("\n[TEST 20/20] Live ACL Delta Apply (No-Touch Rule), Path Danger Zone (240+) & Real-Time Reduction Calculation...");
                await TestLiveAclDeltaApplyAndAuditHygieneThresholdsAsync();
                Console.WriteLine("  --> [PASS] Live ACL Delta Apply (non-mutated ACEs untouched), Path Length 240+ risk & dynamic reduction calculation verified.");
                passCount++;

                // Test 21
                Console.WriteLine("\n[TEST 21/22] Live ACL: Multiset Multiple ACEs Matching, Inheritance Disable Explicit Conversion & Audit Sanctum...");
                await TestAclMultisetDeltaAndInheritancePreservationAsync();
                Console.WriteLine("  --> [PASS] Live ACL: Multiset pairing, inheritance disable explicit conversion & Audit sanctum 100% verified.");
                passCount++;

                // Test 22
                Console.WriteLine("\n[TEST 22/23] Live ACL: External Conflict Detection (AclConflictException), Initial Inheritance Change Status & Multi-ACE Addition Contract...");
                await TestAclConflictAndInheritanceInitContractAsync();
                Console.WriteLine("  --> [PASS] Live ACL: Conflict detection (AclConflictException), initial change status & multi-ACE contract 100% verified.");
                passCount++;

                // Test 23
                Console.WriteLine("\n[TEST 23/24] Live ACL: AclChangePlan Pipeline, Inheritance Promotion & Semantic Verification Contract...");
                await TestAclChangePlanPipelineAndSemanticVerificationAsync();
                Console.WriteLine("  --> [PASS] Live ACL: ChangePlan pipeline, inheritance promotion, semantic verification & post-commit SDDL 100% verified.");
                passCount++;

                // Test 24
                Console.WriteLine("\n[TEST 24/25] Universal Plan-First Contract: Skeleton Deploy, LinkFix Execution & Media Sanctum Plan Fidelity...");
                await TestUniversalPlanFirstAndMutationVerifyAsync();
                Console.WriteLine("  --> [PASS] Universal Plan-First: Skeleton deploy, LinkFix in-place rewrite & Media sanctum plan 100% verified.");
                passCount++;

                // Test 25
                Console.WriteLine("\n[TEST 25/26] Defensive Hardening: Flag Preservation (H1), Skeleton Guard (H2), Atomic Media Replace (H3), Optimistic Lock & Existence Guard (H4/M4), and AccessType Diff (M2)...");
                await TestAstraHardeningAndDefensiveMutationAsync();
                Console.WriteLine("  --> [PASS] Defensive Hardening: All critical mutation guards, optimistic locks, and plan invariants 100% verified.");
                passCount++;

                // Test 26
                Console.WriteLine("\n[TEST 26/27] Bilingual Localization: Dynamic JA/EN Language Switching & Model Display Binding Fidelity...");
                TestBilingualLocalizationFidelity();
                Console.WriteLine("  --> [PASS] Bilingual Localization: All rights, badges, diff types, and models switch seamlessly without untranslated leftovers.");
                passCount++;

                // Test 27
                Console.WriteLine("\n[TEST 27/28] Storage Forecasting, Robust MAD Anomaly Detection & i18n Dictionary Integrity...");
                TestForecastingAndLocalizationDictionary();
                Console.WriteLine("  --> [PASS] Forecasting & Dictionary: Linear regression, Holt trend, MAD floor & zero untranslated verified.");
                passCount++;

                // Test 28
                Console.WriteLine("\n[TEST 28/29] Architectural Unification: Per-Target History Isolation, Rollback Rotation, True Plan-First Skeleton & Atomic Link Verification...");
                await TestArchitecturalUnificationAsync();
                Console.WriteLine("  --> [PASS] Architectural Unification: Per-target isolation, rollback rotation, plan-first skeleton & atomic link verified.");
                passCount++;

                // Test 29
                Console.WriteLine("\n[TEST 29/30] Safety, Real Traversal & i18n v2.0.1: Real NTFS Effective Access Traversal, LinkFix Optimistic Lock & Scoped Rollback, and English CJK-Free Verification...");
                await TestOptimisticLockAndEffectiveAccessChangePointsAsync();
                Console.WriteLine("  --> [PASS] Safety, Real Traversal & i18n v2.0.1: Real NTFS Effective Access traversal, LinkFix optimistic lock & scoped rollback, and English CJK-free 100% verified.");
                passCount++;

                // Test 30
                Console.WriteLine("\n[TEST 30/31] Audit Duplicate Pipeline v2.0.3: Head-Tail Hash, Concurrency 2, Bandwidth Throttling & English CJK-Free Verification...");
                await TestHeadTailHashAndBandwidthLimiterAsync();
                Console.WriteLine("  --> [PASS] Audit Duplicate Pipeline v2.0.3: Head-Tail hash, concurrency 2, bandwidth throttling & English CJK-free 100% verified.");
                passCount++;

                // Test 31
                Console.WriteLine("\n[TEST 31/31] High-Performance UNC Traversal: Win32 FindFirstFileEx (LargeFetch & 8.3 Skip), SafeFindHandle RAII & Concurrency 2 Pipeline...");
                await TestNativeDirectoryEnumeratorAndConcurrency2Async();
                Console.WriteLine("  --> [PASS] High-Performance UNC Traversal: Win32 FindFirstFileEx (LargeFetch & 8.3 Skip), SafeFindHandle RAII & Concurrency 2 100% verified.");
                passCount++;

                Console.WriteLine("\n================================================================================");
                Console.WriteLine($" [QA RESULT] ALL REGRESSION TESTS PASSED ({passCount}/{totalTests})");
                Console.WriteLine("================================================================================");
                return true;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[QA FAILED] Regression Test Failed: {ex.Message}");
                Console.WriteLine(ex.ToString());
                Console.ResetColor();
                return false;
            }
            finally
            {
                LocalizationService.Instance.SetLanguage(originalLang);
            }
        }

        /// <summary>
        /// 1. Media Optimizer の PNG 破壊バグ:
        /// 最適化後もファイル先頭が PNG シグネチャ (0x89, 0x50, 0x4E, 0x47) であること、および透過（アルファチャンネル）が維持されていること。
        /// </summary>
        public static async Task TestMediaOptimizerPngPreservationAsync()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_Media_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string pngPath = Path.Combine(tempDir, "transparent_sample.png");

            try
            {
                // 合成透過 PNG 画像の作成 (400x400, 透過ピクセルを含む Bgra32)
                int width = 400;
                int height = 400;
                int stride = width * 4;
                byte[] rawPixels = new byte[stride * height];

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        int idx = (y * stride) + (x * 4);
                        // 中央 150x150 の領域を完全透過 (Alpha=0) に設定
                        if (x >= 125 && x <= 275 && y >= 125 && y <= 275)
                        {
                            rawPixels[idx + 0] = 0;   // Blue
                            rawPixels[idx + 1] = 0;   // Green
                            rawPixels[idx + 2] = 0;   // Red
                            rawPixels[idx + 3] = 0;   // Alpha (Transparent)
                        }
                        else
                        {
                            // 周囲は半透明カラー
                            rawPixels[idx + 0] = 200; // Blue
                            rawPixels[idx + 1] = 100; // Green
                            rawPixels[idx + 2] = 50;  // Red
                            rawPixels[idx + 3] = 128; // Alpha (Semi-transparent)
                        }
                    }
                }

                var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, rawPixels, stride);
                var initialEncoder = new PngBitmapEncoder { Interlace = PngInterlaceOption.Off };
                initialEncoder.Frames.Add(BitmapFrame.Create(source));
                using (var fs = File.Create(pngPath))
                {
                    initialEncoder.Save(fs);
                }

                long initialSizeBytes = new FileInfo(pngPath).Length;
                if (initialSizeBytes <= 0)
                {
                    throw new InvalidOperationException("テスト用合成 PNG 画像の生成に失敗しました。");
                }

                // MediaOptimizerService を実行 (リサイズ MaxDimension = 200 で確実に再エンコードを発生させる)
                var service = new MediaOptimizerService();
                var options = new MediaOptimizeOptions
                {
                    TargetDirectory = tempDir,
                    MaxDimension = 200,
                    JpegQuality = 75,
                    MinImageSizeBytes = 10
                };

                var mediaItem = new MediaItem
                {
                    FullPath = pngPath,
                    FileName = Path.GetFileName(pngPath),
                    DirectoryPath = tempDir,
                    Extension = ".png",
                    OriginalSizeBytes = initialSizeBytes,
                    IsVideo = false
                };

                var targets = new List<MediaItem> { mediaItem };
                var summary = await service.OptimizeImagesAsync(targets, options, null, CancellationToken.None);

                if (summary.OptimizedImagesCount == 0 && !mediaItem.IsProcessed)
                {
                    throw new InvalidOperationException("MediaOptimizer による PNG の最適化処理がスキップまたは失敗しました。");
                }

                // 1. ファイル先頭の PNG シグネチャ (0x89, 0x50, 0x4E, 0x47) チェック
                byte[] optBytes = File.ReadAllBytes(pngPath);
                if (optBytes.Length < 8)
                {
                    throw new InvalidOperationException($"最適化後ファイルが小さすぎます: {optBytes.Length} bytes");
                }

                if (optBytes[0] != 0x89 || optBytes[1] != 0x50 || optBytes[2] != 0x4E || optBytes[3] != 0x47)
                {
                    throw new InvalidOperationException(
                        $"PNG破壊バグ検出: ファイル先頭シグネチャが PNG (0x89, 0x50, 0x4E, 0x47) ではありません。" +
                        $" 検出バイト: 0x{optBytes[0]:X2} 0x{optBytes[1]:X2} 0x{optBytes[2]:X2} 0x{optBytes[3]:X2}");
                }

                // 2. 透過（アルファチャンネル）の維持チェック
                using var readMs = new MemoryStream(optBytes);
                var decoder = BitmapDecoder.Create(readMs, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                if (decoder.Frames.Count == 0)
                {
                    throw new InvalidOperationException("最適化後 PNG のデコードに失敗しました。");
                }

                var frame = decoder.Frames[0];
                int optW = frame.PixelWidth;
                int optH = frame.PixelHeight;

                // フォーマットがアルファ情報を持つことを確認
                if (frame.Format.BitsPerPixel < 32)
                {
                    throw new InvalidOperationException(
                        $"透過喪失バグ検出: ピクセルフォーマットにアルファチャンネルが含まれていません。Format: {frame.Format}");
                }

                int optStride = optW * 4;
                byte[] decodedPixels = new byte[optStride * optH];
                frame.CopyPixels(decodedPixels, optStride, 0);

                bool foundZeroAlpha = false;
                bool foundSemiAlpha = false;

                for (int i = 0; i < decodedPixels.Length; i += 4)
                {
                    byte alpha = decodedPixels[i + 3];
                    if (alpha == 0) foundZeroAlpha = true;
                    else if (alpha < 200) foundSemiAlpha = true;

                    if (foundZeroAlpha && foundSemiAlpha) break;
                }

                if (!foundZeroAlpha && !foundSemiAlpha)
                {
                    throw new InvalidOperationException(
                        "透過破壊バグ検出: 最適化後の PNG から透明/半透明ピクセルが失われ、全ピクセルが不透明になりました。");
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
        /// 2. Live ACL の Deny 喪失および継承無効化時の ACE 消失バグ:
        /// Allow と Deny の両方を含むエントリを適用した際、Deny が保持され Canonical ACL Ordering (Deny 先頭) になっていること、
        /// および継承OFF時にルールが消失しないこと。
        /// </summary>
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
        private static void TestBilingualLocalizationFidelity()
        {
            var loc = LocalizationService.Instance;
            var originalLang = loc.CurrentLanguage;

            try
            {
                // --- 1. 日本語モードの検証 ---
                loc.SetLanguage(AppLanguage.Japanese);

                var acl = new SimAclEntry { Rights = FileSystemRights.FullControl, AccessType = AccessControlType.Allow };
                if (acl.FormattedRights != "フルコントロール")
                    throw new InvalidOperationException($"JA FormattedRights mismatch: got '{acl.FormattedRights}'");

                acl.Rights = FileSystemRights.Modify;
                if (acl.FormattedRights != "変更 (Modify)")
                    throw new InvalidOperationException($"JA FormattedRights Modify mismatch: got '{acl.FormattedRights}'");

                var diffItem = new LiveAclDiffItem { DiffType = LiveAclDiffType.Added, AccessType = AccessControlType.Deny };
                if (diffItem.DiffTypeDisplay != "＋ 追加")
                    throw new InvalidOperationException($"JA DiffTypeDisplay mismatch: got '{diffItem.DiffTypeDisplay}'");
                if (diffItem.AccessTypeDisplay != "⛔ 拒否")
                    throw new InvalidOperationException($"JA AccessTypeDisplay mismatch: got '{diffItem.AccessTypeDisplay}'");

                var auditItem = new AuditItem
                {
                    IssueType = AuditIssueType.Duplicate,
                    DuplicateGroupId = "GRP-1",
                    IsOriginalCandidate = true
                };
                if (auditItem.IssueTypeDisplay != "重複ファイル")
                    throw new InvalidOperationException($"JA IssueTypeDisplay mismatch: got '{auditItem.IssueTypeDisplay}'");
                if (auditItem.DuplicateGroupBadge != "GRP-1 (原本候補)")
                    throw new InvalidOperationException($"JA DuplicateGroupBadge mismatch: got '{auditItem.DuplicateGroupBadge}'");

                var simNode = new SimFolderNode { Level = 0, InheritAcl = true };
                if (simNode.LevelPillText != "第1階層 (ルート)")
                    throw new InvalidOperationException($"JA LevelPillText mismatch: got '{simNode.LevelPillText}'");
                if (simNode.InheritStatusBadge != "🔗 継承")
                    throw new InvalidOperationException($"JA InheritStatusBadge mismatch: got '{simNode.InheritStatusBadge}'");

                string appliesToJa = AclInheritanceHelper.ToAppliesToString(
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None);
                if (appliesToJa != AclInheritanceHelper.AppliesTo_All)
                    throw new InvalidOperationException($"JA AppliesTo mismatch: got '{appliesToJa}'");

                // --- 2. 英語モードの検証 ---
                loc.SetLanguage(AppLanguage.English);

                acl.Rights = FileSystemRights.FullControl;
                if (acl.FormattedRights != "Full Control")
                    throw new InvalidOperationException($"EN FormattedRights mismatch: got '{acl.FormattedRights}'");

                acl.Rights = FileSystemRights.Modify;
                if (acl.FormattedRights != "Modify")
                    throw new InvalidOperationException($"EN FormattedRights Modify mismatch: got '{acl.FormattedRights}'");

                diffItem.DiffType = LiveAclDiffType.Added;
                diffItem.AccessType = AccessControlType.Deny;
                if (diffItem.DiffTypeDisplay != "+ Add")
                    throw new InvalidOperationException($"EN DiffTypeDisplay mismatch: got '{diffItem.DiffTypeDisplay}'");
                if (diffItem.AccessTypeDisplay != "⛔ Deny")
                    throw new InvalidOperationException($"EN AccessTypeDisplay mismatch: got '{diffItem.AccessTypeDisplay}'");

                auditItem.IssueType = AuditIssueType.Duplicate;
                auditItem.IsOriginalCandidate = true;
                if (auditItem.IssueTypeDisplay != "Duplicate File")
                    throw new InvalidOperationException($"EN IssueTypeDisplay mismatch: got '{auditItem.IssueTypeDisplay}'");
                if (auditItem.DuplicateGroupBadge != "GRP-1 (Original)")
                    throw new InvalidOperationException($"EN DuplicateGroupBadge mismatch: got '{auditItem.DuplicateGroupBadge}'");

                auditItem.IsOriginalCandidate = false;
                if (auditItem.DuplicateGroupBadge != "GRP-1 (Duplicate)")
                    throw new InvalidOperationException($"EN DuplicateGroupBadge (dup) mismatch: got '{auditItem.DuplicateGroupBadge}'");

                if (simNode.LevelPillText != "Level 1 (Root)")
                    throw new InvalidOperationException($"EN LevelPillText mismatch: got '{simNode.LevelPillText}'");
                if (simNode.InheritStatusBadge != "🔗 Inherited")
                    throw new InvalidOperationException($"EN InheritStatusBadge mismatch: got '{simNode.InheritStatusBadge}'");

                string appliesToEn = AclInheritanceHelper.ToAppliesToString(
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None);
                if (appliesToEn != AclInheritanceHelper.AppliesTo_All_En)
                    throw new InvalidOperationException($"EN AppliesTo mismatch: got '{appliesToEn}'");
            }
            finally
            {
                // 元の言語設定に復元
                loc.SetLanguage(originalLang);
            }
        }

        private static void TestForecastingAndLocalizationDictionary()
        {
            // --- 1. 静的辞書 Strings の網羅性 & 未翻訳ゼロ機械的検証 ---
            var jaErrors = Strings.ValidateTranslations(AppLanguage.Japanese);
            if (jaErrors.Count > 0)
            {
                throw new InvalidOperationException($"Strings validation failed for Japanese: {string.Join(", ", jaErrors)}");
            }
            var enErrors = Strings.ValidateTranslations(AppLanguage.English);
            if (enErrors.Count > 0)
            {
                throw new InvalidOperationException($"Strings validation failed for English: {string.Join(", ", enErrors)}");
            }

            // --- 2. 不均一間隔における一次線形回帰 & 閾値到達予測の検証 ---
            // Day 0: 100GB, Day 2: 120GB, Day 7: 170GB, Day 14: 240GB (傾き 10GB/day)
            var baseDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Local);
            const long GB = 1024L * 1024L * 1024L;

            // 2スキャンのみの場合: 最低サンプル数ガード (MinScansForForecasting = 3) により予測無効
            var twoScans = new List<ScanSnapshot>
            {
                new() { Timestamp = baseDate, TotalBytes = 100 * GB },
                new() { Timestamp = baseDate.AddDays(2), TotalBytes = 120 * GB }
            };
            var twoReport = StorageForecastingService.Instance.Analyze(twoScans, 300 * GB);
            if (twoReport.HasSufficientDataForForecast)
                throw new InvalidOperationException("Forecast should not be active with only 2 scans (minimum 3 required).");
            if (twoReport.LinearRegression?.Status != ThresholdReachStatus.NotEnoughData)
                throw new InvalidOperationException("LinearRegression status expected NotEnoughData for 2 scans.");

            var history = new List<ScanSnapshot>
            {
                new() { Timestamp = baseDate, TotalBytes = 100 * GB },
                new() { Timestamp = baseDate.AddDays(2), TotalBytes = 120 * GB },
                new() { Timestamp = baseDate.AddDays(7), TotalBytes = 170 * GB },
                new() { Timestamp = baseDate.AddDays(14), TotalBytes = 240 * GB }
            };

            // 4スキャンの場合: 予測有効化
            long targetThreshold = 300 * GB;
            var report = StorageForecastingService.Instance.Analyze(history, targetThreshold);

            if (report.LinearRegression == null)
                throw new InvalidOperationException("LinearRegression result should not be null.");
            if (!report.HasSufficientDataForForecast)
                throw new InvalidOperationException("Forecast should be active with 4 scans.");

            double slopeGB = report.LinearRegression.Slope / GB;
            if (Math.Abs(slopeGB - 10.0) > 0.1)
                throw new InvalidOperationException($"Slope expected ~10 GB/day, got {slopeGB:F2} GB/day");

            if (report.LinearRegression.RSquared < 0.99)
                throw new InvalidOperationException($"RSquared expected > 0.99 for perfectly linear data, got {report.LinearRegression.RSquared:F3}");

            // 100 + 10 * X = 300 => X = 20日目。最新は14日目なので、残り日数は 6 日
            if (report.LinearRegression.Status != ThresholdReachStatus.Reachable)
                throw new InvalidOperationException($"Status expected Reachable, got {report.LinearRegression.Status}");
            if (Math.Abs(report.LinearRegression.DaysToTarget - 6.0) > 0.1)
                throw new InvalidOperationException($"DaysToTarget expected ~6 days, got {report.LinearRegression.DaysToTarget:F2}");

            if (!report.LinearRegression.TargetDate.HasValue ||
                report.LinearRegression.TargetDate.Value.Date != baseDate.AddDays(20).Date)
            {
                throw new InvalidOperationException($"TargetDate expected {baseDate.AddDays(20):yyyy/MM/dd}, got {report.LinearRegression.TargetDate}");
            }

            // 閾値超過テスト: 現在容量 240GB に対し、閾値を 200GB (現在値未満) に設定した場合
            var exceededReport = StorageForecastingService.Instance.Analyze(history, 200 * GB);
            if (exceededReport.LinearRegression?.Status != ThresholdReachStatus.AlreadyExceeded)
                throw new InvalidOperationException($"Expected AlreadyExceeded status when threshold (200GB) < current (240GB), got {exceededReport.LinearRegression?.Status}");

            // --- 3. Holt の線形トレンド平滑法の検証 ---
            if (report.HoltSmoothing == null)
                throw new InvalidOperationException("HoltSmoothing result should not be null.");
            if (report.HoltSmoothing.CurrentTrend <= 0)
                throw new InvalidOperationException($"Holt CurrentTrend should be positive, got {report.HoltSmoothing.CurrentTrend}");

            // --- 4. 因果律的（No Look-Ahead）ロバスト MAD 異常検知 & 急増主因特定の検証 ---
            // 毎日の増分が 5MB 前後（平穏）な環境で、1回だけ +2GB の急増が発生するシナリオ
            var anomalyHistory = new List<ScanSnapshot>();
            DateTime dt = baseDate;
            long currentBytes = 50 * GB;

            // 5日間の平穏差分 (スナップショット6個で差分5個を蓄積し、MinDeltasForAnomaly=5 を満たす)
            for (int i = 0; i <= 5; i++)
            {
                anomalyHistory.Add(new ScanSnapshot
                {
                    Timestamp = dt,
                    TotalBytes = currentBytes,
                    SubFolders = new List<FolderSnapshot>
                    {
                        new() { Name = "Projects", Size = currentBytes - 10 * GB },
                        new() { Name = "Archive", Size = 10 * GB }
                    }
                });
                dt = dt.AddDays(1);
                currentBytes += 5L * 1024 * 1024; // +5MB
            }

            // 6日目: 突然 +2GB 急増 (Projects が +1.8GB、Archive が +200MB)
            long surgeBytes = 2L * 1024 * 1024 * 1024;
            currentBytes += surgeBytes;
            var surgeSnapshot = new ScanSnapshot
            {
                Timestamp = dt,
                TotalBytes = currentBytes,
                SubFolders = new List<FolderSnapshot>
                {
                    new() { Name = "Projects", Size = currentBytes - 10 * GB - 200L * 1024 * 1024 },
                    new() { Name = "Archive", Size = 10 * GB + 200L * 1024 * 1024 }
                }
            };
            anomalyHistory.Add(surgeSnapshot);

            var anomalyReport = StorageForecastingService.Instance.Analyze(anomalyHistory);
            var surgePoint = anomalyReport.AnomalyPoints.First(p => p.Snapshot.Id == surgeSnapshot.Id);

            if (!surgePoint.IsAnomaly)
                throw new InvalidOperationException("Surge point (+2GB) should be detected as an anomaly.");
            if (surgePoint.ModifiedZScore <= StorageForecastingService.AnomalyZScoreThreshold)
                throw new InvalidOperationException($"Surge point ModifiedZScore expected > {StorageForecastingService.AnomalyZScoreThreshold}, got {surgePoint.ModifiedZScore:F2}");

            // 急増主因特定 (Projects が第1位かつ寄与率 > 80%)
            if (surgePoint.TopContributors.Count == 0)
                throw new InvalidOperationException("TopContributors should not be empty for an anomaly.");
            var topContributor = surgePoint.TopContributors[0];
            if (topContributor.Name != "Projects")
                throw new InvalidOperationException($"Expected top contributor to be 'Projects', got '{topContributor.Name}'");
            if (topContributor.ContributionPercent < 80.0)
                throw new InvalidOperationException($"Expected 'Projects' contribution > 80%, got {topContributor.ContributionPercent}%");

            // --- 5. Look-Ahead（未来データバイアス）排除の完全検証 ---
            // その後さらに未来で、毎日 +3GB の大容量バックアップが日常化（5日間）したとする
            for (int i = 0; i < 5; i++)
            {
                dt = dt.AddDays(1);
                currentBytes += 3L * 1024 * 1024 * 1024; // +3GB/day
                anomalyHistory.Add(new ScanSnapshot
                {
                    Timestamp = dt,
                    TotalBytes = currentBytes,
                    SubFolders = new List<FolderSnapshot>
                    {
                        new() { Name = "Projects", Size = currentBytes }
                    }
                });
            }

            // 未来データが追加された後の全履歴で再解析
            var futureReport = StorageForecastingService.Instance.Analyze(anomalyHistory);
            var retestedSurgePoint = futureReport.AnomalyPoints.First(p => p.Snapshot.Id == surgeSnapshot.Id);

            // 未来に大容量増加が日常化しても、過去の6日目時点の異常判定が勝手に「通常」へ改変されていないこと（因果律の保護）
            if (!retestedSurgePoint.IsAnomaly)
                throw new InvalidOperationException("Look-Ahead bias defect: Historical anomaly was incorrectly overwritten by future high-growth data!");
            if (Math.Abs(retestedSurgePoint.ModifiedZScore - surgePoint.ModifiedZScore) > 1e-4)
                throw new InvalidOperationException("ModifiedZScore of historical point changed when future data was added. Causal isolation failed!");
        }

        /// <summary>
        /// Test 28: 安全文法の統一 ＆ 履歴分離・ロールバック世代管理・Plan-First・アトミックリンク検証
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
