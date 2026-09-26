using AstraSize.Models;
using FolderMorpher.Contracts;
using FolderMorpher.Models;
using System.Collections.ObjectModel;
using System.Security.AccessControl;

namespace FolderMorpher.Host;

internal static class IdentityDtoMapper
{
    public static AdOuNodeDto ToDto(AdOuNode node) => new()
    {
        Name = node.Name,
        DistinguishedName = node.DistinguishedName,
        NodeType = (int)node.NodeType,
        Children = node.Children.Select(ToDto).ToList()
    };

    public static AdPrincipalDto ToDto(AdPrincipalItem item) => new()
    {
        AccountName = item.AccountName, DisplayName = item.DisplayName,
        PrincipalType = (int)item.PrincipalType,
        Domain = item.Domain, Description = item.Description
    };

    public static EffectiveAccessReportDto ToDto(EffectiveAccessAuditReport report) => new()
    {
        TargetAccountName = report.TargetAccountName,
        TargetDisplayName = report.TargetDisplayName,
        PrincipalType = (int)report.PrincipalType,
        RootFolderPath = report.RootFolderPath,
        ScanTimestamp = report.ScanTimestamp,
        GroupMemberships = report.GroupMemberships.Select(ToDto).ToList(),
        AccessibleFolders = report.AccessibleFolders.Select(ToDto).ToList(),
        SeveredFolders = report.SeveredFolders.Select(ToDto).ToList(),
        UnavailableFolders = report.UnavailableFolders.Select(ToDto).ToList(),
        TotalFoldersScanned = report.TotalFoldersScanned,
        FullControlCount = report.FullControlCount,
        ModifyCount = report.ModifyCount,
        ReadOnlyCount = report.ReadOnlyCount,
        EnclaveCount = report.EnclaveCount,
        ExplicitBoundaryCount = report.ExplicitBoundaryCount,
        ResolutionMode = (int)report.ResolutionMode,
        ResolutionStatusText = report.ResolutionStatusText
    };

    public static EffectiveAccessAuditReport ToCore(EffectiveAccessReportDto dto) => new()
    {
        TargetAccountName = dto.TargetAccountName,
        TargetDisplayName = dto.TargetDisplayName,
        PrincipalType = (AdPrincipalType)dto.PrincipalType,
        RootFolderPath = dto.RootFolderPath,
        ScanTimestamp = dto.ScanTimestamp,
        GroupMemberships = dto.GroupMemberships.Select(ToCore).ToList(),
        AccessibleFolders = dto.AccessibleFolders.Select(ToCore).ToList(),
        SeveredFolders = dto.SeveredFolders.Select(ToCore).ToList(),
        UnavailableFolders = dto.UnavailableFolders.Select(ToCore).ToList(),
        TotalFoldersScanned = dto.TotalFoldersScanned,
        FullControlCount = dto.FullControlCount,
        ModifyCount = dto.ModifyCount,
        ReadOnlyCount = dto.ReadOnlyCount,
        EnclaveCount = dto.EnclaveCount,
        ExplicitBoundaryCount = dto.ExplicitBoundaryCount,
        ResolutionMode = (EffectiveAccessResolutionMode)dto.ResolutionMode,
        ResolutionStatusText = dto.ResolutionStatusText
    };

    public static GroupMembershipDto ToDto(PrincipalGroupMembership membership) => new()
    {
        GroupName = membership.GroupName, DisplayName = membership.DisplayName,
        Sid = membership.Sid, IsDirect = membership.IsDirect,
        NestingDepth = membership.NestingDepth, MembershipPath = membership.MembershipPath
    };

    public static PrincipalGroupMembership ToCore(GroupMembershipDto dto) => new()
    {
        GroupName = dto.GroupName, DisplayName = dto.DisplayName,
        Sid = dto.Sid, IsDirect = dto.IsDirect,
        NestingDepth = dto.NestingDepth, MembershipPath = dto.MembershipPath
    };

    private static EffectiveFolderAccessDto ToDto(EffectiveFolderAccessItem item) => new()
    {
        FolderPath = item.FolderPath, FolderName = item.FolderName,
        PermissionLevel = (int)item.PermissionLevel,
        AllowedRights = (int)item.AllowedRights,
        DeniedRights = (int)item.DeniedRights,
        HasDeny = item.HasDeny, IsInherited = item.IsInherited,
        ChangeType = (int)item.ChangeType,
        GrantSource = item.GrantSource, GrantPathTrace = item.GrantPathTrace
    };

    private static EffectiveFolderAccessItem ToCore(EffectiveFolderAccessDto dto) => new()
    {
        FolderPath = dto.FolderPath, FolderName = dto.FolderName,
        PermissionLevel = (EffectivePermissionLevel)dto.PermissionLevel,
        AllowedRights = (FileSystemRights)dto.AllowedRights,
        DeniedRights = (FileSystemRights)dto.DeniedRights,
        HasDeny = dto.HasDeny, IsInherited = dto.IsInherited,
        ChangeType = (EffectiveAccessChangeType)dto.ChangeType,
        GrantSource = dto.GrantSource, GrantPathTrace = dto.GrantPathTrace
    };
}
