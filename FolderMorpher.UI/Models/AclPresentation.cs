using System.Security.AccessControl;

namespace AstraSize.Models;

public partial class LiveAclDiffItem
{
    public string BadgeBackground => DiffType switch
    {
        LiveAclDiffType.Added => "#DCFCE7",
        LiveAclDiffType.Removed => "#FEE2E2",
        LiveAclDiffType.Modified => "#DBEAFE",
        _ => "#F1F5F9"
    };

    public string BadgeForeground => DiffType switch
    {
        LiveAclDiffType.Added => "#15803D",
        LiveAclDiffType.Removed => "#B91C1C",
        LiveAclDiffType.Modified => "#1D4ED8",
        _ => "#64748B"
    };

    public string IconGlyph => PrincipalType == AdPrincipalType.User ? "👤" : "👥";
    public string AccessTypeBadgeBg => AccessType == AccessControlType.Deny ? "#FEE2E2" : "#F0FDF4";
    public string AccessTypeBadgeFg => AccessType == AccessControlType.Deny ? "#B91C1C" : "#15803D";
}
