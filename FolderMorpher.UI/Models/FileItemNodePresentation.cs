using FolderMorpher.Services;

namespace AstraSize.Models;

public partial class FileItemNode
{
    public string? DiffFormatted
    {
        get
        {
            if (!DiffBytes.HasValue || DiffBytes.Value == 0) return null;
            return DiffBytes.Value > 0
                ? $"+{FormatBytes(DiffBytes.Value)} ▲"
                : $"-{FormatBytes(Math.Abs(DiffBytes.Value))} ▼";
        }
    }
    public string PercentageDisplay => IsRoot ? "―" : $"{Percentage:F1}%";
    public string ShareFormatted => PercentageFormatted;
    public string FormattedSize { get => FormatBytes(Size); set { } }
    public string PercentageFormatted { get => IsRoot ? "―" : $"{Percentage:F1}%"; set { } }
    public string FormattedPercentage { get => IsRoot ? "―" : $"{Percentage:0.#}%"; set { } }
    public string FormattedFileCount { get => $"{FileCount:N0}"; set { } }
    public string FormattedFolderCount { get => $"{FolderCount:N0}"; set { } }
    public string FormattedLastModified { get => LastModified?.ToString("yyyy/MM/dd HH:mm") ?? "-"; set { } }
    public string DiffBadgeBackground => DiffBytes > 0 ? "#FEE2E2" : "#E0F2FE";
    public string DiffBadgeForeground => DiffBytes > 0 ? "#DC2626" : "#0284C7";
    public string ExpandGlyph
    {
        get => !CanExpand ? "" : IsExpanded ? "▼" : "▶";
        set { }
    }
    public string ExpandIcon => ExpandGlyph;
    public string IconGlyph => IsDirectory ? "📁" : "📄";
    private TablerBadgeInfo BadgeInfo => TablerBadgeHelper.GetBadge(FullPath, IsDirectory);
    public string BadgeText => BadgeInfo.Text;
    public string BadgeBackground => BadgeInfo.Background;
    public string BadgeBorderBrush => BadgeInfo.BorderBrush;
    public string BadgeForeground => BadgeInfo.Foreground;
    public string FontWeight => IsDirectory ? "Bold" : "Normal";
    public string IndentMargin
    {
        get => $"{Level * 16},0,0,0";
        set { }
    }
}

public class FolderChildShareItem
{
    public FileItemNode OriginalNode { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public string FormattedSize => FileItemNode.FormatBytes(Size);
    public bool IsDirectory { get; set; }
    public string IconGlyph => IsDirectory ? "📁" : "📄";
    private TablerBadgeInfo BadgeInfo => TablerBadgeHelper.GetBadge(FullPath, IsDirectory);
    public string BadgeText => BadgeInfo.Text;
    public string BadgeBackground => BadgeInfo.Background;
    public string BadgeBorderBrush => BadgeInfo.BorderBrush;
    public string BadgeForeground => BadgeInfo.Foreground;
    public double RelativeSharePercentage { get; set; }
    public string RelativeShareFormatted => $"{RelativeSharePercentage:F1}%";
    public int FileCount { get; set; }
    public int FolderCount { get; set; }
}
