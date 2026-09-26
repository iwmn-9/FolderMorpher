namespace FolderMorpher.Contracts;

public sealed class RegressionForecastDto
{
    public double Slope { get; set; }
    public double Intercept { get; set; }
    public double RSquared { get; set; }
    public double DaysToTarget { get; set; }
    public DateTime? TargetDate { get; set; }
    public int Status { get; set; }
}

public sealed class SmoothingForecastDto
{
    public double CurrentLevel { get; set; }
    public double CurrentTrend { get; set; }
}

public sealed class SubfolderGrowthDto
{
    public string Name { get; set; } = string.Empty;
    public long PreviousSize { get; set; }
    public long CurrentSize { get; set; }
    public long DeltaBytes { get; set; }
    public double ContributionPercent { get; set; }
}

public sealed class AnomalyPointDto
{
    public ScanSnapshotDto Snapshot { get; set; } = new();
    public ScanSnapshotDto? PreviousSnapshot { get; set; }
    public double DaysElapsed { get; set; }
    public long DeltaBytes { get; set; }
    public double RateBytesPerDay { get; set; }
    public double ModifiedZScore { get; set; }
    public bool IsAnomaly { get; set; }
    public bool InsufficientBaseline { get; set; }
    public List<SubfolderGrowthDto> TopContributors { get; set; } = new();
}

public sealed class StorageForecastDto
{
    public RegressionForecastDto? LinearRegression { get; set; }
    public SmoothingForecastDto? HoltSmoothing { get; set; }
    public List<AnomalyPointDto> AnomalyPoints { get; set; } = new();
    public double MedianDailyRate { get; set; }
    public double EffectiveMAD { get; set; }
    public long CurrentCapacity { get; set; }
    public long TargetThresholdBytes { get; set; }
    public int TotalScans { get; set; }
}
