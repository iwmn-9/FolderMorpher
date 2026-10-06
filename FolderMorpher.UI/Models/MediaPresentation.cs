using FolderMorpher.Services;

namespace FolderMorpher.Models;

public partial class MediaItem
{
    public string OriginalSizeFormatted => FormatHelper.FormatBytes(OriginalSizeBytes, 2);
    public string OptimizedSizeFormatted => OptimizedSizeBytes > 0 ? FormatHelper.FormatBytes(OptimizedSizeBytes, 2) : "―";
    public string SavedSizeFormatted => SavedBytes > 0 ? FormatHelper.FormatBytes(SavedBytes, 2) : "―";
}

public partial class MediaOptimizeSummary
{
    public string TotalSavedSizeFormatted => FormatHelper.FormatBytes(TotalSavedBytes, 2);
}
