using System.Collections.ObjectModel;
using System.Security.AccessControl;
using AstraSize.Models;
using FolderMorpher.Contracts;
using FolderMorpher.Models;

namespace FolderMorpher.HostClient;

internal static class IdentityDtoMapper
{
    public static AdPrincipalItem ToView(AdPrincipalDto dto) => new()
    {
        AccountName = dto.AccountName,
        DisplayName = dto.DisplayName,
        PrincipalType = (AdPrincipalType)dto.PrincipalType,
        Domain = dto.Domain,
        Description = dto.Description
    };

    public static AdOuNode ToView(AdOuNodeDto dto) => new()
    {
        Name = dto.Name,
        DistinguishedName = dto.DistinguishedName,
        NodeType = (AdOuNodeType)dto.NodeType,
        Children = new ObservableCollection<AdOuNode>(dto.Children.Select(ToView))
    };

    public static EffectiveAccessAuditReport ToView(EffectiveAccessReportDto dto) => new()
    {
        TargetAccountName = dto.TargetAccountName,
        TargetDisplayName = dto.TargetDisplayName,
        PrincipalType = (AdPrincipalType)dto.PrincipalType,
        RootFolderPath = dto.RootFolderPath,
        ScanTimestamp = dto.ScanTimestamp,
        GroupMemberships = dto.GroupMemberships.Select(ToView).ToList(),
        AccessibleFolders = dto.AccessibleFolders.Select(ToView).ToList(),
        SeveredFolders = dto.SeveredFolders.Select(ToView).ToList(),
        UnavailableFolders = dto.UnavailableFolders.Select(ToView).ToList(),
        TotalFoldersScanned = dto.TotalFoldersScanned,
        FullControlCount = dto.FullControlCount,
        ModifyCount = dto.ModifyCount,
        ReadOnlyCount = dto.ReadOnlyCount,
        EnclaveCount = dto.EnclaveCount,
        ExplicitBoundaryCount = dto.ExplicitBoundaryCount,
        ResolutionMode = (EffectiveAccessResolutionMode)dto.ResolutionMode,
        ResolutionStatusText = dto.ResolutionStatusText
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

    public static PrincipalGroupMembership ToView(GroupMembershipDto dto) => new()
    {
        GroupName = dto.GroupName, DisplayName = dto.DisplayName,
        Sid = dto.Sid, IsDirect = dto.IsDirect,
        NestingDepth = dto.NestingDepth, MembershipPath = dto.MembershipPath
    };

    private static GroupMembershipDto ToDto(PrincipalGroupMembership group) => new()
    {
        GroupName = group.GroupName, DisplayName = group.DisplayName,
        Sid = group.Sid, IsDirect = group.IsDirect,
        NestingDepth = group.NestingDepth, MembershipPath = group.MembershipPath
    };

    private static EffectiveFolderAccessItem ToView(EffectiveFolderAccessDto dto) => new()
    {
        FolderPath = dto.FolderPath, FolderName = dto.FolderName,
        PermissionLevel = (EffectivePermissionLevel)dto.PermissionLevel,
        AllowedRights = (FileSystemRights)dto.AllowedRights,
        DeniedRights = (FileSystemRights)dto.DeniedRights,
        HasDeny = dto.HasDeny, IsInherited = dto.IsInherited,
        ChangeType = (EffectiveAccessChangeType)dto.ChangeType,
        GrantSource = dto.GrantSource, GrantPathTrace = dto.GrantPathTrace
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
}
