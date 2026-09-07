using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
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
            int totalTests = 9;

            try
            {
                // Test 1
                Console.WriteLine("\n[TEST 1/9] Media Optimizer: PNG Corruption & Alpha Channel Preservation...");
                await TestMediaOptimizerPngPreservationAsync();
                Console.WriteLine("  --> [PASS] Media Optimizer: PNG signature (0x89 50 4E 47) and alpha channel 100% preserved.");
                passCount++;

                // Test 2
                Console.WriteLine("\n[TEST 2/9] Live ACL: Deny Loss & Inheritance Disabling ACE Loss (Canonical ACL Ordering)...");
                TestLiveAclDenyAndInheritance();
                Console.WriteLine("  --> [PASS] Live ACL: ACEs preserved on inheritance disable, Deny rules ordered first (Canonical Order).");
                passCount++;

                // Test 3
                Console.WriteLine("\n[TEST 3/9] Audit Archival: Original File Archive & Move Duplication...");
                TestAuditArchivalOriginalExclusionAndDeduplication();
                Console.WriteLine("  --> [PASS] Audit: Original files safely protected from archive, move commands deduplicated.");
                passCount++;

                // Test 4
                Console.WriteLine("\n[TEST 4/9] MFT Data Run Decoder: Initial LCN Double-Addition Bug...");
                TestMftDataRunDecoderLcnCalculation();
                Console.WriteLine("  --> [PASS] MFT Data Run Decoder: Initial LCN computed relative to 0 without double-addition.");
                passCount++;

                // Test 5
                Console.WriteLine("\n[TEST 5/9] Live ACL: Special Inheritance & Propagation Flags Preservation...");
                TestLiveAclSpecialInheritanceFlags();
                Console.WriteLine("  --> [PASS] Live ACL: Special InheritanceFlags and PropagationFlags preserved across read/write.");
                passCount++;

                // Test 6
                Console.WriteLine("\n[TEST 6/9] ACL UI Binding & Helper: Bidirectional Mapping & Modal State Sync...");
                TestAclUiBindingAndHelper();
                Console.WriteLine("  --> [PASS] ACL UI Binding: AppliesTo, AccessType, and Modal state perfectly synchronized.");
                passCount++;

                // Test 7
                Console.WriteLine("\n[TEST 7/9] Effective Access: Canonical DACL Evaluation & Multi-Level Group Permission Tracing...");
                await TestEffectiveAccessCanonicalDaclAndNestingAsync();
                Console.WriteLine("  --> [PASS] Effective Access: Canonical DACL ordering (Explicit Allow > Inherited Deny), multi-level tracing & user isolation verified.");
                passCount++;

                // Test 8
                Console.WriteLine("\n[TEST 8/9] Simulation & Script Generation: .NET PS Script, Robocopy /XD Subtree Exclusion, and Effective Access InheritOnly...");
                TestSimulationAclRobocopyAndEffectiveAccessInheritOnly();
                Console.WriteLine("  --> [PASS] Simulation & Effective Access: .NET PS script fidelity, Robocopy /XD exclusion, and InheritOnly exclusion verified.");
                passCount++;

                // Test 9
                Console.WriteLine("\n[TEST 9/9] Live ACL Rollback DACL SDDL Fidelity & Skeleton Empty-ACL Inheritance Disable...");
                await TestLiveAclRollbackAndSkeletonEmptyAclInheritanceAsync();
                Console.WriteLine("  --> [PASS] Live ACL Rollback & Skeleton Deploy: DACL SDDL 100% restored after mutation, empty ACL inheritance disabled.");
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
                if (userEntry.AppliesTo != AclInheritanceHelper.AppliesTo_ThisFolderOnly)
                {
                    throw new InvalidOperationException(
                        $"AppliesTo 文字列が不正です。期待値: '{AclInheritanceHelper.AppliesTo_ThisFolderOnly}', 実際: '{userEntry.AppliesTo}'");
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
                if (everyoneEntry.AppliesTo != AclInheritanceHelper.AppliesTo_SubfoldersAndFilesOnly)
                {
                    throw new InvalidOperationException(
                        $"AppliesTo 文字列が不正です。期待値: '{AclInheritanceHelper.AppliesTo_SubfoldersAndFilesOnly}', 実際: '{everyoneEntry.AppliesTo}'");
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
                if (!nestedItem.GrantSource.Contains("深度 3"))
                {
                    throw new InvalidOperationException($"入れ子深度特定失敗: 実際={nestedItem.GrantSource}");
                }
                if (nestedItem.PermissionLevel != EffectivePermissionLevel.ReadAndExecute)
                {
                    throw new InvalidOperationException($"実効権限レベル判定失敗: 期待値=ReadAndExecute, 実際={nestedItem.PermissionLevel}");
                }

                // 検証 3: 直接付与の特定
                var directItem = report.AccessibleFolders.First(f => f.FolderPath.Equals(subDirect, StringComparison.OrdinalIgnoreCase));
                if (!directItem.GrantSource.Contains("直接付与"))
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
                var originalSddl = dirInfo.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

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

                var restoredSddl = dirInfo.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                if (restoredSddl != originalSddl)
                {
                    throw new InvalidOperationException(
                        $"Rollback 失敗: ロールバック後のDACL SDDLが元と一致しません！\n期待値: {originalSddl}\n復元値: {restoredSddl}");
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
    }
}
