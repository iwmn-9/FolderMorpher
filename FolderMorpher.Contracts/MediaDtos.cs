namespace FolderMorpher.Contracts;

public sealed class MediaItemDto
{
    public string FullPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public long OriginalSizeBytes { get; set; }
    public DateTime ExpectedLastWriteTimeUtc { get; set; }
    public long OptimizedSizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public bool IsVideo { get; set; }
    public bool IsExcluded { get; set; }
    public string ExclusionReason { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool IsProcessed { get; set; }
}

public sealed class MediaOptimizeOptionsDto
{
    public string TargetDirectory { get; set; } = string.Empty;
    public int MaxDimension { get; set; } = 2560;
    public int JpegQuality { get; set; } = 85;
    public long MinImageSizeBytes { get; set; } = 2 * 1024 * 1024;
    public List<string> ExcludedFolderKeywords { get; set; } = new();
    public List<string> CustomExcludedPaths { get; set; } = new();
    public string? BackupDirectory { get; set; }
}

public sealed class MediaOptimizeSummaryDto
{
    public int TotalImagesScanned { get; set; }
    public int TotalVideosScanned { get; set; }
    public int OptimizedImagesCount { get; set; }
    public long TotalOriginalBytes { get; set; }
    public long TotalOptimizedBytes { get; set; }
}

public sealed class MediaOptimizeResultDto
{
    public MediaOptimizeSummaryDto Summary { get; set; } = new();
    public List<MediaItemDto> UpdatedItems { get; set; } = new();
}
