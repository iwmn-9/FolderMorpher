using System;
using System.Threading.Tasks;
using FolderMorpher.Models;
using FolderMorpher.Services;

namespace FolderMorpher.Services.Testing
{
    /// <summary>
    /// FolderMorpher 自動回帰テストスイート（7大ドメイン包括検証）
    /// 過去に蓄積された全31件の再発防止セーフティネットを7大機能ドメインに統合・整線
    /// </summary>
    public static partial class RegressionTestSuite
    {
        public static async Task<bool> RunAllTestsAsync()
        {
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }
            Console.WriteLine("================================================================================");
            Console.WriteLine(" [QA] FolderMorpher Automated Regression Test Suite (Headless) Starting...");
            Console.WriteLine("================================================================================");

            int passCount = 0;
            int totalTests = 7;

            var originalLang = LocalizationService.Instance.CurrentLanguage;
            LocalizationService.Instance.SetLanguage(AppLanguage.Japanese);

            try
            {
                // Domain 1
                Console.WriteLine("\n[TEST 1/7] Domain 1: Live ACL & Effective Access (Canonical DACL, Rollback SDDL, Delta Apply, Conflict & Real Traversal)...");
                await TestDomain_LiveAclAndEffectiveAccessAsync();
                Console.WriteLine("  --> [PASS] Domain 1: Live ACL & Effective Access 100% verified.");
                passCount++;

                // Domain 2
                Console.WriteLine("\n[TEST 2/7] Domain 2: Storage Explorer & UNC Traversal (Win32 LargeFetch/8.3 Skip, SafeFindHandle, Concurrency 2 & 0s History Cache)...");
                await TestDomain_StorageAndUncTraversalAsync();
                Console.WriteLine("  --> [PASS] Domain 2: Storage Explorer & UNC Traversal 100% verified.");
                passCount++;

                // Domain 3
                Console.WriteLine("\n[TEST 3/7] Domain 3: Simulation Studio (Plan-First Skeleton, Robocopy /XD, Lazy Loading, D&D & Rollback Rotation)...");
                await TestDomain_SimulationStudioAsync();
                Console.WriteLine("  --> [PASS] Domain 3: Simulation Studio 100% verified.");
                passCount++;

                // Domain 4
                Console.WriteLine("\n[TEST 4/7] Domain 4: Audit & Hygiene (Head-Tail Hash, Bandwidth Throttler, Smart Original Protection, ScannedFileEntry & Safe Deletion)...");
                await TestDomain_AuditAndHygieneAsync();
                Console.WriteLine("  --> [PASS] Domain 4: Audit & Hygiene 100% verified.");
                passCount++;

                // Domain 5
                Console.WriteLine("\n[TEST 5/7] Domain 5: Media Optimizer & LinkFixer (PNG Signature/Alpha Preservation, Sanctum Guard, In-Place LinkFix & Scoped Rollback)...");
                await TestDomain_MediaOptimizerAndLinkFixerAsync();
                Console.WriteLine("  --> [PASS] Domain 5: Media Optimizer & LinkFixer 100% verified.");
                passCount++;

                // Domain 6
                Console.WriteLine("\n[TEST 6/7] Domain 6: MFT & Defensive Hardening (Initial LCN 0-Base, AppSettings Cascade & Mutation Invariants)...");
                await TestDomain_MftAndHardeningAsync();
                Console.WriteLine("  --> [PASS] Domain 6: MFT & Defensive Hardening 100% verified.");
                passCount++;

                // Domain 7
                Console.WriteLine("\n[TEST 7/7] Domain 7: Bilingual Localization & Storage Forecasting (JA/EN Switch, CJK-Free Verification, Holt Forecasting & Dictionary Integrity)...");
                TestDomain_LocalizationAndForecasting();
                Console.WriteLine("  --> [PASS] Domain 7: Bilingual Localization & Storage Forecasting 100% verified.");
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
    }
}