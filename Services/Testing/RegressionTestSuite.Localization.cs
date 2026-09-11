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
        /// [DOMAIN 7/7] Bilingual Localization & Storage Forecasting 邨ｱ蜷医ユ繧ｹ繝・        /// </summary>
        public static void TestDomain_LocalizationAndForecasting()
        {
            TestBilingualLocalizationFidelity();
            TestForecastingAndLocalizationDictionary();
        }

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
    }
}