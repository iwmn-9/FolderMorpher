using AstraSize.Models;
using FolderMorpher.Contracts;
using FolderMorpher.Services;

namespace FolderMorpher.Host;

internal static class ForecastDtoMapper
{
    public static ScanSnapshot ToCore(ScanSnapshotDto dto) => new()
    {
        Id = dto.Id, TargetPath = dto.TargetPath, Timestamp = dto.Timestamp,
        TotalBytes = dto.TotalBytes, TotalFiles = dto.TotalFiles,
        SubFolders = dto.SubFolders.Select(folder => new FolderSnapshot
        {
            Name = folder.Name, Size = folder.Size
        }).ToList()
    };

    public static ScanSnapshotDto ToDto(ScanSnapshot snapshot) => new()
    {
        Id = snapshot.Id, TargetPath = snapshot.TargetPath,
        Timestamp = snapshot.Timestamp, TotalBytes = snapshot.TotalBytes,
        TotalFiles = snapshot.TotalFiles,
        SubFolders = snapshot.SubFolders.Select(folder => new FolderSnapshotDto
        {
            Name = folder.Name, Size = folder.Size
        }).ToList()
    };

    public static StorageForecastDto ToDto(StorageForecastReport report) => new()
    {
        LinearRegression = report.LinearRegression is { } regression ? new RegressionForecastDto
        {
            Slope = regression.Slope, Intercept = regression.Intercept,
            RSquared = regression.RSquared, DaysToTarget = regression.DaysToTarget,
            TargetDate = regression.TargetDate, Status = (int)regression.Status
        } : null,
        HoltSmoothing = report.HoltSmoothing is { } smoothing ? new SmoothingForecastDto
        {
            CurrentLevel = smoothing.CurrentLevel, CurrentTrend = smoothing.CurrentTrend
        } : null,
        AnomalyPoints = report.AnomalyPoints.Select(point => new AnomalyPointDto
        {
            Snapshot = ToDto(point.Snapshot),
            PreviousSnapshot = point.PreviousSnapshot == null ? null : ToDto(point.PreviousSnapshot),
            DaysElapsed = point.DaysElapsed, DeltaBytes = point.DeltaBytes,
            RateBytesPerDay = point.RateBytesPerDay,
            ModifiedZScore = point.ModifiedZScore, IsAnomaly = point.IsAnomaly,
            InsufficientBaseline = point.InsufficientBaseline,
            TopContributors = point.TopContributors.Select(item => new SubfolderGrowthDto
            {
                Name = item.Name, PreviousSize = item.PreviousSize,
                CurrentSize = item.CurrentSize, DeltaBytes = item.DeltaBytes,
                ContributionPercent = item.ContributionPercent
            }).ToList()
        }).ToList(),
        MedianDailyRate = report.MedianDailyRate,
        EffectiveMAD = report.EffectiveMAD,
        CurrentCapacity = report.CurrentCapacity,
        TargetThresholdBytes = report.TargetThresholdBytes,
        TotalScans = report.TotalScans
    };
}
