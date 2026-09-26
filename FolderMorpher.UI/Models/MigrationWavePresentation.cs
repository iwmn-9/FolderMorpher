using FolderMorpher.Services;

namespace FolderMorpher.Models;

public partial class MigrationWavePlan
{
    public string TotalSizeFormatted => FormatHelper.FormatBytes(TotalSizeBytes, 2);
    public string TotalFileCountFormatted => WaveDisplayFormat.FormatFileCount(TotalFileCount);
    public string FullCopyTimeFormatted => WaveDisplayFormat.FormatDuration(EstimatedFullCopyTime);
    public string CutoverTimeFormatted => WaveDisplayFormat.FormatDuration(EstimatedCutoverTime);
}
