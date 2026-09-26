using FolderMorpher.Contracts;
using FolderMorpher.Models;

namespace FolderMorpher.Host;

internal static class MediaDtoMapper
{
    public static MediaItemDto ToDto(MediaItem item) => new()
    {
        FullPath = item.FullPath, FileName = item.FileName,
        DirectoryPath = item.DirectoryPath, Extension = item.Extension,
        OriginalSizeBytes = item.OriginalSizeBytes,
        ExpectedLastWriteTimeUtc = item.ExpectedLastWriteTimeUtc,
        OptimizedSizeBytes = item.OptimizedSizeBytes,
        Width = item.Width, Height = item.Height, IsVideo = item.IsVideo,
        IsExcluded = item.IsExcluded, ExclusionReason = item.ExclusionReason,
        Status = item.Status, IsProcessed = item.IsProcessed
    };

    public static MediaItem ToCore(MediaItemDto dto) => new()
    {
        FullPath = dto.FullPath, FileName = dto.FileName,
        DirectoryPath = dto.DirectoryPath, Extension = dto.Extension,
        VersionStamp = new FileVersionStamp(dto.OriginalSizeBytes, dto.ExpectedLastWriteTimeUtc),
        OptimizedSizeBytes = dto.OptimizedSizeBytes,
        Width = dto.Width, Height = dto.Height, IsVideo = dto.IsVideo,
        IsExcluded = dto.IsExcluded, ExclusionReason = dto.ExclusionReason,
        Status = dto.Status, IsProcessed = dto.IsProcessed
    };

    public static MediaOptimizeOptions ToCore(MediaOptimizeOptionsDto dto) => new()
    {
        TargetDirectory = dto.TargetDirectory, MaxDimension = dto.MaxDimension,
        JpegQuality = dto.JpegQuality, MinImageSizeBytes = dto.MinImageSizeBytes,
        ExcludedFolderKeywords = dto.ExcludedFolderKeywords,
        CustomExcludedPaths = dto.CustomExcludedPaths, BackupDirectory = dto.BackupDirectory
    };

    public static MediaOptimizeSummaryDto ToDto(MediaOptimizeSummary summary) => new()
    {
        TotalImagesScanned = summary.TotalImagesScanned,
        TotalVideosScanned = summary.TotalVideosScanned,
        OptimizedImagesCount = summary.OptimizedImagesCount,
        TotalOriginalBytes = summary.TotalOriginalBytes,
        TotalOptimizedBytes = summary.TotalOptimizedBytes
    };

    public static MediaOptimizeSummary ToCore(MediaOptimizeSummaryDto dto) => new()
    {
        TotalImagesScanned = dto.TotalImagesScanned,
        TotalVideosScanned = dto.TotalVideosScanned,
        OptimizedImagesCount = dto.OptimizedImagesCount,
        TotalOriginalBytes = dto.TotalOriginalBytes,
        TotalOptimizedBytes = dto.TotalOptimizedBytes
    };
}
