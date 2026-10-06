using FolderMorpher.Models;

namespace FolderMorpher.Services;

// Shared palette for the audit spreadsheet and the WPF view.
public static class AuditReportPalette
{
    private static readonly string[] GroupBackground = { "#EFF6FF", "#ECFDF5", "#FEF3C7", "#F3E8FF", "#FFE4E6", "#E0F2FE" };
    private static readonly string[] GroupBorder = { "#BFDBFE", "#A7F3D0", "#FDE68A", "#DDD6FE", "#FECDD3", "#BAE6FD" };
    private static readonly string[] GroupText = { "#1E40AF", "#065F46", "#92400E", "#5B21B6", "#9F1239", "#075985" };
    public static int GroupColorCount => GroupBackground.Length;

    private static bool IsGroupedDuplicate(AuditItem item) =>
        item.IssueType == AuditIssueType.Duplicate && item.DuplicateGroupIndex > 0;

    public static string RowBackground(AuditItem item) => IsGroupedDuplicate(item)
        ? GroupBackground[item.DuplicateGroupColorIndex % GroupColorCount]
        : "Transparent";

    public static string BadgeBackground(AuditItem item)
    {
        if (IsGroupedDuplicate(item)) return GroupBackground[item.DuplicateGroupColorIndex % GroupColorCount];
        return item.IssueType switch
        {
            AuditIssueType.VersionFamily => "#EFF6FF",
            AuditIssueType.ExtractedArchive => "#FDF4FF",
            AuditIssueType.GraveyardTree => "#FEF2F2",
            _ => "Transparent"
        };
    }

    public static string BadgeBorder(AuditItem item)
    {
        if (IsGroupedDuplicate(item)) return GroupBorder[item.DuplicateGroupColorIndex % GroupColorCount];
        return item.IssueType switch
        {
            AuditIssueType.VersionFamily => "#BFDBFE",
            AuditIssueType.ExtractedArchive => "#F0ABFC",
            AuditIssueType.GraveyardTree => "#FECACA",
            _ => "#E2E8F0"
        };
    }

    public static string BadgeForeground(AuditItem item)
    {
        if (IsGroupedDuplicate(item)) return GroupText[item.DuplicateGroupColorIndex % GroupColorCount];
        return item.IssueType switch
        {
            AuditIssueType.VersionFamily => "#1D4ED8",
            AuditIssueType.ExtractedArchive => "#A21CAF",
            AuditIssueType.GraveyardTree => "#DC2626",
            _ => "#64748B"
        };
    }
}
