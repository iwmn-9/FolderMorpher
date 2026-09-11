using System;
using System.Collections.Generic;
using System.Linq;
using AstraSize.Models;

namespace FolderMorpher.Services
{
    public class LinearRegressionResult
    {
        public double Slope { get; set; }           // Bytes / Day
        public double Intercept { get; set; }       // Bytes at Day 0
        public double RSquared { get; set; }        // 決定係数 (0.0 - 1.0)
        public double DaysToTarget { get; set; } = -1; // 閾値到達までの日数 (-1: 到達しない/減少傾向)
        public DateTime? TargetDate { get; set; }   // 到達予測日時
        public bool IsGrowing => Slope > 0;
        public string FormattedDailyRate => (Slope >= 0 ? "+" : "") + FileItemNode.FormatBytes((long)Slope) + "/day";
    }

    public class HoltForecastPoint
    {
        public DateTime Date { get; set; }
        public double DaysFromNow { get; set; }
        public double EstimatedBytes { get; set; }
        public string FormattedSize => FileItemNode.FormatBytes((long)Math.Max(0, EstimatedBytes));
    }

    public class HoltSmoothingResult
    {
        public double CurrentLevel { get; set; }     // 最新水準 (Bytes)
        public double CurrentTrend { get; set; }     // 最新トレンド (Bytes / Day)
        public List<HoltForecastPoint> ForecastPoints { get; set; } = new();
        public string FormattedTrend => (CurrentTrend >= 0 ? "+" : "") + FileItemNode.FormatBytes((long)CurrentTrend) + "/day";
    }

    public class SubfolderGrowthItem
    {
        public string Name { get; set; } = string.Empty;
        public long PreviousSize { get; set; }
        public long CurrentSize { get; set; }
        public long DeltaBytes { get; set; }
        public double ContributionPercent { get; set; }
        public string FormattedDelta => (DeltaBytes > 0 ? "+" : "") + FileItemNode.FormatBytes(DeltaBytes);
        public string FormattedCurrentSize => FileItemNode.FormatBytes(CurrentSize);
    }

    public class AnomalyDetectionPoint
    {
        public ScanSnapshot Snapshot { get; set; } = null!;
        public ScanSnapshot? PreviousSnapshot { get; set; }
        public double DaysElapsed { get; set; }
        public long DeltaBytes { get; set; }
        public double RateBytesPerDay { get; set; }
        public double ModifiedZScore { get; set; }
        public bool IsAnomaly { get; set; }
        public List<SubfolderGrowthItem> TopContributors { get; set; } = new();
        public string FormattedDelta => (DeltaBytes > 0 ? "+" : "") + FileItemNode.FormatBytes(DeltaBytes);
    }

    public class StorageForecastReport
    {
        public LinearRegressionResult? LinearRegression { get; set; }
        public HoltSmoothingResult? HoltSmoothing { get; set; }
        public List<AnomalyDetectionPoint> AnomalyPoints { get; set; } = new();
        public double MedianDailyRate { get; set; }
        public double EffectiveMAD { get; set; }
        public long CurrentCapacity { get; set; }
        public long TargetThresholdBytes { get; set; }
    }

    /// <summary>
    /// 容量推移の数理予測・異常検知サービス。
    /// 不均一なスキャン間隔（毎日・隔週・不定期）に対応した連続時間正規化モデル。
    /// 一次線形回帰、Holt線形トレンド二重平滑化、ロバストMAD異常検知、急増主因特定を提供。
    /// </summary>
    public class StorageForecastingService
    {
        private static readonly Lazy<StorageForecastingService> _instance =
            new(() => new StorageForecastingService());
        public static StorageForecastingService Instance => _instance.Value;

        // 異常検知のフロア定数
        public const long MinAnomalyAbsoluteDeltaBytes = 500L * 1024L * 1024L; // 500 MB
        public const double MinMadFloorBytesPerDay = 10.0 * 1024.0 * 1024.0;   // 10 MB/day
        public const double AnomalyZScoreThreshold = 3.5;

        /// <summary>
        /// スナップショット履歴を解析し、予測および異常検知レポートを生成する。
        /// </summary>
        /// <param name="history">時系列スナップショット一覧（順不同可）</param>
        /// <param name="targetThresholdBytes">予測対象の容量上限閾値（0以下の場合は現在容量の1.2倍を自動設定）</param>
        public StorageForecastReport Analyze(IReadOnlyList<ScanSnapshot> history, long targetThresholdBytes = 0)
        {
            var report = new StorageForecastReport();
            if (history == null || history.Count < 2)
            {
                if (history != null && history.Count == 1)
                {
                    report.CurrentCapacity = history[0].TotalBytes;
                }
                return report;
            }

            // タイムスタンプ順にソート
            var sorted = history.OrderBy(h => h.Timestamp).ToList();
            report.CurrentCapacity = sorted.Last().TotalBytes;

            if (targetThresholdBytes <= 0)
            {
                // 自動閾値: 最新容量の 120% (最低でも +10GB)
                targetThresholdBytes = Math.Max(report.CurrentCapacity + 10L * 1024L * 1024L * 1024L, (long)(report.CurrentCapacity * 1.2));
            }
            report.TargetThresholdBytes = targetThresholdBytes;

            DateTime t0 = sorted[0].Timestamp;
            int n = sorted.Count;

            // x: T0からの経過実日数 (TotalDays), y: TotalBytes
            double[] x = new double[n];
            double[] y = new double[n];

            for (int i = 0; i < n; i++)
            {
                x[i] = (sorted[i].Timestamp - t0).TotalDays;
                y[i] = sorted[i].TotalBytes;
            }

            // 1. 一次線形回帰 (Ordinary Least Squares)
            report.LinearRegression = CalculateLinearRegression(x, y, sorted, targetThresholdBytes);

            // 2. Holtの線形トレンド二重指数平滑法
            report.HoltSmoothing = CalculateHoltSmoothing(x, y, sorted);

            // 3. ロバストMAD異常検知 & 急増主因特定
            CalculateAnomalyDetection(sorted, report);

            return report;
        }

        private LinearRegressionResult CalculateLinearRegression(double[] x, double[] y, List<ScanSnapshot> sorted, long targetThreshold)
        {
            int n = x.Length;
            double sumX = 0, sumY = 0;
            for (int i = 0; i < n; i++)
            {
                sumX += x[i];
                sumY += y[i];
            }
            double meanX = sumX / n;
            double meanY = sumY / n;

            double covXY = 0;
            double varX = 0;
            double varY = 0;

            for (int i = 0; i < n; i++)
            {
                double dx = x[i] - meanX;
                double dy = y[i] - meanY;
                covXY += dx * dy;
                varX += dx * dx;
                varY += dy * dy;
            }

            double slope = varX > 1e-9 ? covXY / varX : 0;
            double intercept = meanY - slope * meanX;

            double rSquared = 0;
            if (varX > 1e-9 && varY > 1e-9)
            {
                rSquared = Math.Min(1.0, Math.Max(0.0, (covXY * covXY) / (varX * varY)));
            }

            var result = new LinearRegressionResult
            {
                Slope = slope,
                Intercept = intercept,
                RSquared = rSquared
            };

            double latestX = x[n - 1];
            double latestY = y[n - 1];

            // 閾値到達予測の算出
            if (slope > 0 && targetThreshold > latestY)
            {
                double targetX = (targetThreshold - intercept) / slope;
                double daysRemaining = targetX - latestX;
                if (daysRemaining >= 0)
                {
                    result.DaysToTarget = daysRemaining;
                    result.TargetDate = sorted.Last().Timestamp.AddDays(daysRemaining);
                }
            }

            return result;
        }

        private HoltSmoothingResult CalculateHoltSmoothing(double[] x, double[] y, List<ScanSnapshot> sorted)
        {
            int n = x.Length;
            double alpha = 0.3; // 水準平滑化係数
            double beta = 0.1;  // トレンド平滑化係数

            // 初期値
            double dt0 = Math.Max(x[1] - x[0], 0.001);
            double level = y[0];
            double trend = (y[1] - y[0]) / dt0;

            for (int i = 1; i < n; i++)
            {
                double dt = Math.Max(x[i] - x[i - 1], 0.001);
                double prevLevel = level;
                double prevTrend = trend;

                // 予測水準
                double forecastLevel = prevLevel + prevTrend * dt;
                level = alpha * y[i] + (1.0 - alpha) * forecastLevel;
                trend = beta * ((level - prevLevel) / dt) + (1.0 - beta) * prevTrend;
            }

            var result = new HoltSmoothingResult
            {
                CurrentLevel = level,
                CurrentTrend = trend
            };

            // 将来30日, 60日, 90日の予測点
            DateTime latestDate = sorted.Last().Timestamp;
            int[] forecastDays = { 30, 60, 90 };
            foreach (var days in forecastDays)
            {
                result.ForecastPoints.Add(new HoltForecastPoint
                {
                    Date = latestDate.AddDays(days),
                    DaysFromNow = days,
                    EstimatedBytes = Math.Max(0, level + trend * days)
                });
            }

            return result;
        }

        private void CalculateAnomalyDetection(List<ScanSnapshot> sorted, StorageForecastReport report)
        {
            int n = sorted.Count;
            if (n < 2) return;

            var rates = new List<double>();
            var points = new List<AnomalyDetectionPoint>();

            for (int i = 1; i < n; i++)
            {
                var prev = sorted[i - 1];
                var curr = sorted[i];

                double daysElapsed = Math.Max((curr.Timestamp - prev.Timestamp).TotalDays, 1.0 / 86400.0);
                long deltaBytes = curr.TotalBytes - prev.TotalBytes;
                double rate = deltaBytes / daysElapsed; // Bytes / Day

                rates.Add(rate);

                points.Add(new AnomalyDetectionPoint
                {
                    Snapshot = curr,
                    PreviousSnapshot = prev,
                    DaysElapsed = daysElapsed,
                    DeltaBytes = deltaBytes,
                    RateBytesPerDay = rate
                });
            }

            // レートの中央値
            double medianRate = CalculateMedian(rates);
            report.MedianDailyRate = medianRate;

            // MAD (Median Absolute Deviation)
            var absDeviations = rates.Select(r => Math.Abs(r - medianRate)).ToList();
            double rawMad = CalculateMedian(absDeviations);

            // ゼロ除算・静穏フォルダ誤爆防止フロア (最低10MB/day)
            double effectiveMad = Math.Max(rawMad, MinMadFloorBytesPerDay);
            report.EffectiveMAD = effectiveMad;

            // Modified Z-score 計算と判定
            foreach (var pt in points)
            {
                // Boris Iglewicz and David Hoaglin (1993) 標準推定量定数 0.6745
                pt.ModifiedZScore = 0.6745 * Math.Abs(pt.RateBytesPerDay - medianRate) / effectiveMad;

                // 増加傾向であり、統計的に突出 (Z > 3.5) かつ 実増分がフロア閾値(500MB)以上
                if (pt.DeltaBytes >= MinAnomalyAbsoluteDeltaBytes &&
                    pt.RateBytesPerDay > medianRate &&
                    pt.ModifiedZScore > AnomalyZScoreThreshold)
                {
                    pt.IsAnomaly = true;
                    // 急増主因の内訳特定
                    pt.TopContributors = IdentifyContributors(pt.PreviousSnapshot, pt.Snapshot);
                }

                report.AnomalyPoints.Add(pt);
            }
        }

        private List<SubfolderGrowthItem> IdentifyContributors(ScanSnapshot? prev, ScanSnapshot curr)
        {
            var list = new List<SubfolderGrowthItem>();
            if (curr.SubFolders == null || curr.SubFolders.Count == 0) return list;

            var prevMap = prev?.SubFolders != null
                ? prev.SubFolders.ToDictionary(f => f.Name, f => f.Size, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            long totalPositiveGrowth = 0;
            foreach (var cf in curr.SubFolders)
            {
                long pSize = prevMap.TryGetValue(cf.Name, out long s) ? s : 0;
                long delta = cf.Size - pSize;
                if (delta > 0)
                {
                    totalPositiveGrowth += delta;
                    list.Add(new SubfolderGrowthItem
                    {
                        Name = cf.Name,
                        PreviousSize = pSize,
                        CurrentSize = cf.Size,
                        DeltaBytes = delta
                    });
                }
            }

            if (totalPositiveGrowth > 0)
            {
                foreach (var item in list)
                {
                    item.ContributionPercent = Math.Round((double)item.DeltaBytes / totalPositiveGrowth * 100.0, 1);
                }
            }

            return list.OrderByDescending(x => x.DeltaBytes).Take(5).ToList();
        }

        private static double CalculateMedian(List<double> values)
        {
            if (values == null || values.Count == 0) return 0;
            var sorted = values.OrderBy(v => v).ToList();
            int count = sorted.Count;
            if (count % 2 == 1)
            {
                return sorted[count / 2];
            }
            return (sorted[count / 2 - 1] + sorted[count / 2]) / 2.0;
        }
    }
}
