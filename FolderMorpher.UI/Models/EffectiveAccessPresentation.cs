using FolderMorpher.Services;

namespace FolderMorpher.Models;

public partial class EffectiveFolderAccessItem
{
    public string RightsBadgeBackground => EffectiveAccessPalette.RightsBackground(PermissionLevel);
    public string RightsBadgeForeground => "#FFFFFF";
    public string InheritanceBadgeColor => IsInherited ? "#64748B" : "#2563EB";
    public string ChangeBadgeBackground => EffectiveAccessPalette.ChangeBackground(ChangeType);
    public string ChangeBadgeBorder => EffectiveAccessPalette.ChangeBorder(ChangeType);
    public string ChangeBadgeForeground => EffectiveAccessPalette.ChangeForeground(ChangeType);
}

public partial class PrincipalGroupMembership
{
    public string DirectBadgeColor => IsDirect ? "#2563EB" : "#7C3AED";
}
