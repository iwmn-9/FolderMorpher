using FolderMorpher.Models;

namespace FolderMorpher.Services;

// Report and UI use the same palette; permission decisions depend on enum values only.
public static class EffectiveAccessPalette
{
    public static string RightsBackground(EffectivePermissionLevel level) => level switch
    {
        EffectivePermissionLevel.FullControl => "#DC2626",
        EffectivePermissionLevel.Modify => "#D97706",
        EffectivePermissionLevel.ReadAndExecute => "#0284C7",
        EffectivePermissionLevel.Read => "#059669",
        _ => "#94A3B8"
    };

    public static string ChangeBackground(EffectiveAccessChangeType change) => change switch
    {
        EffectiveAccessChangeType.Baseline => "#F1F5F9",
        EffectiveAccessChangeType.EnclaveGranted => "#FEF2F2",
        EffectiveAccessChangeType.InheritanceSevered => "#FFF1F2",
        EffectiveAccessChangeType.ScanUnavailable => "#FFFBEB",
        EffectiveAccessChangeType.ExplicitBoundary => "#F0F9FF",
        EffectiveAccessChangeType.PermissionChanged => "#FEF3C7",
        EffectiveAccessChangeType.Unknown => "#F1F5F9",
        _ => "#F8FAFC"
    };

    public static string ChangeBorder(EffectiveAccessChangeType change) => change switch
    {
        EffectiveAccessChangeType.Baseline => "#CBD5E1",
        EffectiveAccessChangeType.EnclaveGranted => "#FCA5A5",
        EffectiveAccessChangeType.InheritanceSevered => "#FDA4AF",
        EffectiveAccessChangeType.ScanUnavailable => "#FDE68A",
        EffectiveAccessChangeType.ExplicitBoundary => "#BAE6FD",
        EffectiveAccessChangeType.PermissionChanged => "#FCD34D",
        EffectiveAccessChangeType.Unknown => "#CBD5E1",
        _ => "#E2E8F0"
    };

    public static string ChangeForeground(EffectiveAccessChangeType change) => change switch
    {
        EffectiveAccessChangeType.Baseline => "#475569",
        EffectiveAccessChangeType.EnclaveGranted => "#DC2626",
        EffectiveAccessChangeType.InheritanceSevered => "#E11D48",
        EffectiveAccessChangeType.ScanUnavailable => "#D97706",
        EffectiveAccessChangeType.ExplicitBoundary => "#0284C7",
        EffectiveAccessChangeType.PermissionChanged => "#B45309",
        _ => "#64748B"
    };
}
