using FolderMorpher.Contracts;
using FolderMorpher.Models;

namespace FolderMorpher.HostClient;

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

    public static MediaItem ToViewItem(MediaItemDto dto) => new()
    {
        FullPath = dto.FullPath, FileName = dto.FileName,
        DirectoryPath = dto.DirectoryPath, Extension = dto.Extension,
        VersionStamp = new FileVersionStamp(dto.OriginalSizeBytes, dto.ExpectedLastWriteTimeUtc),
        OptimizedSizeBytes = dto.OptimizedSizeBytes,
        Width = dto.Width, Height = dto.Height, IsVideo = dto.IsVideo,
        IsExcluded = dto.IsExcluded, ExclusionReason = dto.ExclusionReason,
        Status = dto.Status, IsProcessed = dto.IsProcessed
    };

    public static MediaOptimizeOptionsDto ToDto(MediaOptimizeOptions options) => new()
    {
        TargetDirectory = options.TargetDirectory, MaxDimension = options.MaxDimension,
        JpegQuality = options.JpegQuality, MinImageSizeBytes = options.MinImageSizeBytes,
        ExcludedFolderKeywords = options.ExcludedFolderKeywords,
        CustomExcludedPaths = options.CustomExcludedPaths,
        BackupDirectory = options.BackupDirectory
    };

    public static MediaOptimizeSummary ToViewSummary(MediaOptimizeSummaryDto dto) => new()
    {
        TotalImagesScanned = dto.TotalImagesScanned,
        TotalVideosScanned = dto.TotalVideosScanned,
        OptimizedImagesCount = dto.OptimizedImagesCount,
        TotalOriginalBytes = dto.TotalOriginalBytes,
        TotalOptimizedBytes = dto.TotalOptimizedBytes
    };

    public static MediaOptimizeSummaryDto ToDto(MediaOptimizeSummary summary) => new()
    {
        TotalImagesScanned = summary.TotalImagesScanned,
        TotalVideosScanned = summary.TotalVideosScanned,
        OptimizedImagesCount = summary.OptimizedImagesCount,
        TotalOriginalBytes = summary.TotalOriginalBytes,
        TotalOptimizedBytes = summary.TotalOptimizedBytes
    };
}
