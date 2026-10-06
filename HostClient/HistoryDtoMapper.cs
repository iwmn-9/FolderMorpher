using AstraSize.Models;
using FolderMorpher.Contracts;
using FolderMorpher.Services;

namespace FolderMorpher.HostClient;

internal static class HistoryDtoMapper
{
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

    public static StorageForecastReport ToView(StorageForecastDto dto) => new()
    {
        LinearRegression = dto.LinearRegression is { } regression ? new LinearRegressionResult
        {
            Slope = regression.Slope, Intercept = regression.Intercept,
            RSquared = regression.RSquared, DaysToTarget = regression.DaysToTarget,
            TargetDate = regression.TargetDate, Status = (ThresholdReachStatus)regression.Status
        } : null,
        HoltSmoothing = dto.HoltSmoothing is { } smoothing ? new HoltSmoothingResult
        {
            CurrentLevel = smoothing.CurrentLevel, CurrentTrend = smoothing.CurrentTrend
        } : null,
        AnomalyPoints = dto.AnomalyPoints.Select(point => new AnomalyDetectionPoint
        {
            Snapshot = ToView(point.Snapshot),
            PreviousSnapshot = point.PreviousSnapshot == null ? null : ToView(point.PreviousSnapshot),
            DaysElapsed = point.DaysElapsed, DeltaBytes = point.DeltaBytes,
            RateBytesPerDay = point.RateBytesPerDay,
            ModifiedZScore = point.ModifiedZScore, IsAnomaly = point.IsAnomaly,
            InsufficientBaseline = point.InsufficientBaseline,
            TopContributors = point.TopContributors.Select(item => new SubfolderGrowthItem
            {
                Name = item.Name, PreviousSize = item.PreviousSize,
                CurrentSize = item.CurrentSize, DeltaBytes = item.DeltaBytes,
                ContributionPercent = item.ContributionPercent
            }).ToList()
        }).ToList(),
        MedianDailyRate = dto.MedianDailyRate,
        EffectiveMAD = dto.EffectiveMAD,
        CurrentCapacity = dto.CurrentCapacity,
        TargetThresholdBytes = dto.TargetThresholdBytes,
        TotalScans = dto.TotalScans
    };

    public static ScanSnapshot ToView(ScanSnapshotDto dto) => new()
    {
        Id = dto.Id,
        TargetPath = dto.TargetPath,
        Timestamp = dto.Timestamp,
        TotalBytes = dto.TotalBytes,
        TotalFiles = dto.TotalFiles,
        SubFolders = dto.SubFolders.Select(folder => new FolderSnapshot { Name = folder.Name, Size = folder.Size }).ToList()
    };
}
