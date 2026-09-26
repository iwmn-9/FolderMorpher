using FolderMorpher.Services;

namespace FolderMorpher.Models;

public partial class AuditItem
{
    private TablerBadgeInfo BadgeInfo => TablerBadgeHelper.GetBadge(FileName, false);
    public string BadgeText => BadgeInfo.Text;
    public string BadgeBackground => BadgeInfo.Background;
    public string BadgeBorderBrush => BadgeInfo.BorderBrush;
    public string BadgeForeground => BadgeInfo.Foreground;
    public string RowBackgroundHex => AuditReportPalette.RowBackground(this);
    public string BadgeBackgroundHex => AuditReportPalette.BadgeBackground(this);
    public string BadgeBorderHex => AuditReportPalette.BadgeBorder(this);
    public string BadgeForegroundHex => AuditReportPalette.BadgeForeground(this);

    public string ConfidenceBadgeBgHex => WasteScore switch
    {
        >= 80 => "#FEE2E2",
        >= 50 => "#FEF3C7",
        _ => "#F1F5F9"
    };

    public string ConfidenceBadgeBorderHex => WasteScore switch
    {
        >= 80 => "#FCA5A5",
        >= 50 => "#FDE68A",
        _ => "#CBD5E1"
    };

    public string ConfidenceBadgeFgHex => WasteScore switch
    {
        >= 80 => "#991B1B",
        >= 50 => "#92400E",
        _ => "#475569"
    };
}
