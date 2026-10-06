using System;
using System.Collections.Generic;
using System.Linq;
using AstraSize.Models;

namespace FolderMorpher.Services
{
    public enum ThresholdReachStatus
    {
        NotEnoughData,       // スキャン数不足 (< 3)
        AlreadyExceeded,     // 現在容量 >= 目標閾値 (既に超過)
        DecreasingOrFlat,    // 傾き <= 0 (到達見込みなし)
        Reachable            // 傾き > 0 (将来到達予測あり)
    }

    public class LinearRegressionResult
    {
        public double Slope { get; set; }               // Bytes / Day
        public double Intercept { get; set; }           // Bytes at Day 0
        public double RSquared { get; set; }            // 決定係数 (0.0 - 1.0)
        public double DaysToTarget { get; set; } = -1;  // 閾値到達までの残り日数
        public DateTime? TargetDate { get; set; }       // 到達予測日時
        public ThresholdReachStatus Status { get; set; } = ThresholdReachStatus.NotEnoughData;
        public bool IsGrowing => Slope > 0;
        public string FormattedDailyRate => (Slope >= 0 ? "+" : "") + FileItemNode.FormatBytes((long)Slope) + "/day";
    }

    public class HoltSmoothingResult
    {
        public double CurrentLevel { get; set; }     // 最新水準 (Bytes)
        public double CurrentTrend { get; set; }     // 直近重視の平滑トレンド (Bytes / Day)
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
        public bool InsufficientBaseline { get; set; } // 基準データ不足（< 5差分）
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
        public int TotalScans { get; set; }
        public bool HasSufficientDataForForecast => TotalScans >= StorageForecastingService.MinScansForForecasting;
    }

    /// <summary>
    /// 容量推移の数理予測・異常検知サービス。
    /// 不均一なスキャン間隔に対応した連続時間正規化モデル。
    /// 因果律に則った過去基準のみ（No Look-Ahead）のロバストMAD異常検知、一次線形回帰、Holtトレンド平滑化を提供。
    /// </summary>
    public class StorageForecastingService
    {
        private static readonly Lazy<StorageForecastingService> _instance =
            new(() => new StorageForecastingService());
        public static StorageForecastingService Instance => _instance.Value;

        // 統計信頼性のための最低サンプル数定数
        public const int MinScansForForecasting = 3;   // 予測（一次回帰/Holt）に必要な最低スキャン数
        public const int MinDeltasForAnomaly = 5;      // MAD異常検知に必要な最低差分履歴数

        // 異常検知のフロア定数
        public const long MinAnomalyAbsoluteDeltaBytes = 500L * 1024L * 1024L; // 500 MB (過剰アラート防止フロア)
        public const double MinMadFloorBytesPerDay = 10.0 * 1024.0 * 1024.0;   // 10 MB/day (ゼロ除算・静穏フォルダ保護)
        public const double AnomalyZScoreThreshold = 3.5;

        /// <summary>
        /// スナップショット履歴を解析し、予測および異常検知レポートを生成する。
        /// </summary>
        /// <param name="history">時系列スナップショット一覧（順不同可）</param>
        /// <param name="targetThresholdBytes">予測対象の容量上限閾値（0以下の場合は最新容量の1.2倍を自動設定）</param>
        public StorageForecastReport Analyze(IReadOnlyList<ScanSnapshot> history, long targetThresholdBytes = 0)
        {
            var report = new StorageForecastReport();
            if (history == null || history.Count == 0)
            {
                return report;
            }

            // タイムスタンプ順にソート (時系列の正本)
            var sorted = history.OrderBy(h => h.Timestamp).ToList();
            int n = sorted.Count;
            report.TotalScans = n;
            report.CurrentCapacity = sorted.Last().TotalBytes;

            // 初期閾値: 最新スナップショットの 120% (最低でも +10GB)
            if (targetThresholdBytes <= 0)
            {
                targetThresholdBytes = Math.Max(report.CurrentCapacity + 10L * 1024L * 1024L * 1024L, (long)(report.CurrentCapacity * 1.2));
            }
            report.TargetThresholdBytes = targetThresholdBytes;

            if (n < 2)
            {
                return report;
            }

            DateTime t0 = sorted[0].Timestamp;

            // x: T0からの経過実日数 (TotalDays), y: TotalBytes
            double[] x = new double[n];
            double[] y = new double[n];

            for (int i = 0; i < n; i++)
            {
                x[i] = (sorted[i].Timestamp - t0).TotalDays;
                y[i] = sorted[i].TotalBytes;
            }

            // 1. 一次線形回帰 (最低3スキャン以上で有効化)
            if (n >= MinScansForForecasting)
            {
                report.LinearRegression = CalculateLinearRegression(x, y, sorted, targetThresholdBytes);
                // 2. Holtの線形トレンド平滑法 (最低3スキャン以上)
                report.HoltSmoothing = CalculateHoltSmoothing(x, y);
            }
            else
            {
                report.LinearRegression = new LinearRegressionResult
                {
                    Status = ThresholdReachStatus.NotEnoughData
                };
            }

            // 3. 因果律的（No Look-Ahead）ロバストMAD異常検知 & 急増主因特定
            CalculateCausalAnomalyDetection(sorted, report);

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

            double latestX = x[n - 1];
            double latestY = y[n - 1];

            var result = new LinearRegressionResult
            {
                Slope = slope,
                Intercept = intercept,
                RSquared = rSquared
            };

            // 閾値到達予測の3状態判定
            if (latestY >= targetThreshold)
            {
                // 既に上限を超過している
                result.Status = ThresholdReachStatus.AlreadyExceeded;
                result.DaysToTarget = 0;
                result.TargetDate = sorted.Last().Timestamp;
            }
            else if (slope <= 0)
            {
                // 減少傾向または横ばい（到達見込みなし）
                result.Status = ThresholdReachStatus.DecreasingOrFlat;
            }
            else
            {
                // 将来到達予測あり
                double targetX = (targetThreshold - intercept) / slope;
                double daysRemaining = targetX - latestX;
                if (daysRemaining >= 0)
                {
                    result.Status = ThresholdReachStatus.Reachable;
                    result.DaysToTarget = daysRemaining;
                    result.TargetDate = sorted.Last().Timestamp.AddDays(daysRemaining);
                }
                else
                {
                    result.Status = ThresholdReachStatus.AlreadyExceeded;
                }
            }

            return result;
        }

        private HoltSmoothingResult CalculateHoltSmoothing(double[] x, double[] y)
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

                // 予測水準と更新
                double forecastLevel = prevLevel + prevTrend * dt;
                level = alpha * y[i] + (1.0 - alpha) * forecastLevel;
                trend = beta * ((level - prevLevel) / dt) + (1.0 - beta) * prevTrend;
            }

            return new HoltSmoothingResult
            {
                CurrentLevel = level,
                CurrentTrend = trend
            };
        }

        /// <summary>
        /// 因果律に則った（No Look-Ahead）MAD異常検知。
        /// 各スナップショット時点の異常判定は、未来のデータを一切使わず、
        /// 「その時点までに蓄積されていた過去の差分履歴（Expanding Window）」のみを母集団として計算する。
        /// これにより、後からデータが増えても過去の異常判定が勝手に書き換わらない。
        /// </summary>
        private void CalculateCausalAnomalyDetection(List<ScanSnapshot> sorted, StorageForecastReport report)
        {
            int n = sorted.Count;
            if (n < 2) return;

            var historicalRates = new List<double>();

            for (int i = 1; i < n; i++)
            {
                var prev = sorted[i - 1];
                var curr = sorted[i];

                double daysElapsed = Math.Max((curr.Timestamp - prev.Timestamp).TotalDays, 1.0 / 86400.0);
                long deltaBytes = curr.TotalBytes - prev.TotalBytes;
                double rate = deltaBytes / daysElapsed; // Bytes / Day

                var pt = new AnomalyDetectionPoint
                {
                    Snapshot = curr,
                    PreviousSnapshot = prev,
                    DaysElapsed = daysElapsed,
                    DeltaBytes = deltaBytes,
                    RateBytesPerDay = rate
                };

                // 過去の差分履歴が MinDeltasForAnomaly (5件) 以上ある場合のみ判定を行う
                // ※ この時点までの過去履歴（historicalRates）のみを母集団とする（未来データバイアス排除）
                if (historicalRates.Count >= MinDeltasForAnomaly)
                {
                    double baselineMedian = CalculateMedian(historicalRates);
                    var absDevs = historicalRates.Select(r => Math.Abs(r - baselineMedian)).ToList();
                    double baselineMad = CalculateMedian(absDevs);
                    double effectiveMad = Math.Max(baselineMad, MinMadFloorBytesPerDay);

                    pt.ModifiedZScore = 0.6745 * Math.Abs(rate - baselineMedian) / effectiveMad;

                    if (pt.DeltaBytes >= MinAnomalyAbsoluteDeltaBytes &&
                        pt.RateBytesPerDay > baselineMedian &&
                        pt.ModifiedZScore > AnomalyZScoreThreshold)
                    {
                        pt.IsAnomaly = true;
                        pt.TopContributors = IdentifyContributors(prev, curr);
                    }
                }
                else
                {
                    // 基準データ不足（初期ウォームアップ期間）
                    pt.InsufficientBaseline = true;
                }

                // 今回のレートを過去履歴に追加（次の時点のための母集団へ）
                historicalRates.Add(rate);

                report.AnomalyPoints.Add(pt);
            }

            // 最新スナップショット時点での全体の基準値をレポートに記録
            if (historicalRates.Count > 0)
            {
                report.MedianDailyRate = CalculateMedian(historicalRates);
                var allAbsDevs = historicalRates.Select(r => Math.Abs(r - report.MedianDailyRate)).ToList();
                report.EffectiveMAD = Math.Max(CalculateMedian(allAbsDevs), MinMadFloorBytesPerDay);
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
