using AstraSize.Models;

namespace FolderMorpher.Services;

public enum ThresholdReachStatus
{
    NotEnoughData, AlreadyExceeded, DecreasingOrFlat, Reachable
}

public sealed class LinearRegressionResult
{
    public double Slope { get; set; }
    public double Intercept { get; set; }
    public double RSquared { get; set; }
    public double DaysToTarget { get; set; } = -1;
    public DateTime? TargetDate { get; set; }
    public ThresholdReachStatus Status { get; set; }
    public bool IsGrowing => Slope > 0;
    public string FormattedDailyRate => (Slope >= 0 ? "+" : "") + FileItemNode.FormatBytes((long)Slope) + "/day";
}

public sealed class HoltSmoothingResult
{
    public double CurrentLevel { get; set; }
    public double CurrentTrend { get; set; }
    public string FormattedTrend => (CurrentTrend >= 0 ? "+" : "") + FileItemNode.FormatBytes((long)CurrentTrend) + "/day";
}

public sealed class SubfolderGrowthItem
{
    public string Name { get; set; } = string.Empty;
    public long PreviousSize { get; set; }
    public long CurrentSize { get; set; }
    public long DeltaBytes { get; set; }
    public double ContributionPercent { get; set; }
    public string FormattedDelta => (DeltaBytes > 0 ? "+" : "") + FileItemNode.FormatBytes(DeltaBytes);
    public string FormattedCurrentSize => FileItemNode.FormatBytes(CurrentSize);
}

public sealed class AnomalyDetectionPoint
{
    public ScanSnapshot Snapshot { get; set; } = null!;
    public ScanSnapshot? PreviousSnapshot { get; set; }
    public double DaysElapsed { get; set; }
    public long DeltaBytes { get; set; }
    public double RateBytesPerDay { get; set; }
    public double ModifiedZScore { get; set; }
    public bool IsAnomaly { get; set; }
    public bool InsufficientBaseline { get; set; }
    public List<SubfolderGrowthItem> TopContributors { get; set; } = new();
    public string FormattedDelta => (DeltaBytes > 0 ? "+" : "") + FileItemNode.FormatBytes(DeltaBytes);
}

public sealed class StorageForecastReport
{
    public LinearRegressionResult? LinearRegression { get; set; }
    public HoltSmoothingResult? HoltSmoothing { get; set; }
    public List<AnomalyDetectionPoint> AnomalyPoints { get; set; } = new();
    public double MedianDailyRate { get; set; }
    public double EffectiveMAD { get; set; }
    public long CurrentCapacity { get; set; }
    public long TargetThresholdBytes { get; set; }
    public int TotalScans { get; set; }
    public bool HasSufficientDataForForecast => TotalScans >= 3;
}
