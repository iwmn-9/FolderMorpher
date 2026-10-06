using FolderMorpher.Services;

namespace AstraSize.Models;

public partial class LargestFileInfo
{
    public string FormattedSize => FileItemNode.FormatBytes(Size);
    private TablerBadgeInfo BadgeInfo => TablerBadgeHelper.GetBadge(string.IsNullOrEmpty(Extension) ? FullPath : Extension, false);
    public string BadgeText => BadgeInfo.Text;
    public string BadgeBackground => BadgeInfo.Background;
    public string BadgeBorderBrush => BadgeInfo.BorderBrush;
    public string BadgeForeground => BadgeInfo.Foreground;
}

public partial class ExtensionStat
{
    public string FormattedSize => FileItemNode.FormatBytes(TotalSize);
    public string PercentageFormatted => $"{Percentage:F1}%";
}

public partial class CleanupCandidate
{
    public string FormattedSize => FileItemNode.FormatBytes(Size);
}

public partial class ScanSummary
{
    public string FormattedCleanupTotal => FileItemNode.FormatBytes(CleanupTotalBytes);
    public string FormattedTotalSize => FileItemNode.FormatBytes(TotalBytes);
}
