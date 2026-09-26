using System;
using System.Text.Json.Serialization;

namespace AstraSize.Models;

public partial class AdPrincipalItem
{
    public string IconGlyph => PrincipalType == AdPrincipalType.User ? "👤" : "👥";
    public string BadgeBackground => PrincipalType == AdPrincipalType.User ? "#E0F2FE" : "#FEF3C7";
    public string BadgeForeground => PrincipalType == AdPrincipalType.User ? "#0369A1" : "#B45309";
}

public partial class SimAclEntry
{
    [JsonIgnore]
    public string IconGlyph => PrincipalType == AdPrincipalType.User ? "👤" : "👥";
}

public partial class SimFolderNode
{
    [JsonIgnore]
    public string FormattedSize => FolderMorpher.Services.FormatHelper.FormatBytes(EstimatedSizeBytes);

    [JsonIgnore]
    public string IndentMargin => $"{Math.Min(12, Level) * 18},0,0,0";

    [JsonIgnore]
    public string LevelPillBackground => Level switch
    {
        0 => "#065F46",
        1 => "#1E3A8A",
        2 => "#4C1D95",
        3 => "#831843",
        _ => "#713F12"
    };

    [JsonIgnore]
    public string LevelPillForeground => Level switch
    {
        0 => "#6EE7B7",
        1 => "#93C5FD",
        2 => "#C4B5FD",
        3 => "#FBCFE8",
        _ => "#FDE047"
    };
}

public partial class SimDiffItem
{
    public string DiffTypeBadgeBackground => Kind switch
    {
        SimDiffKind.Consolidation => "#D97706",
        SimDiffKind.Preserved => "#475569",
        SimDiffKind.NewFolder => "#059669",
        SimDiffKind.Relocation => "#2563EB",
        _ => "#3B82F6"
    };

    public string DiffTypeBadgeForeground => "#FFFFFF";
}
