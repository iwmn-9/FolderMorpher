using System.Security.AccessControl;
using AstraSize.Models;
using FolderMorpher.Contracts;

namespace FolderMorpher.HostClient;

internal static class AclDtoMapper
{
    public static AclEntryDto ToDto(SimAclEntry entry) => new()
    {
        Sid = entry.Sid,
        AccountName = entry.AccountName,
        DisplayName = entry.DisplayName,
        PrincipalType = (int)entry.PrincipalType,
        Rights = (int)entry.Rights,
        AccessType = (int)entry.AccessType,
        IsInherited = entry.IsInherited,
        InheritanceFlags = (int)entry.InheritanceFlags,
        PropagationFlags = (int)entry.PropagationFlags
    };

    public static SimAclEntry ToView(AclEntryDto dto) => new()
    {
        Sid = dto.Sid,
        AccountName = dto.AccountName,
        DisplayName = dto.DisplayName,
        PrincipalType = (AdPrincipalType)dto.PrincipalType,
        Rights = (FileSystemRights)dto.Rights,
        AccessType = (AccessControlType)dto.AccessType,
        IsInherited = dto.IsInherited,
        InheritanceFlags = (InheritanceFlags)dto.InheritanceFlags,
        PropagationFlags = (PropagationFlags)dto.PropagationFlags
    };

    public static LiveAclDiffItem ToView(AclDiffItemDto dto) => new()
    {
        DiffType = (LiveAclDiffType)dto.DiffType,
        AccountName = dto.AccountName,
        DisplayName = dto.DisplayName,
        IconGlyph = dto.IconGlyph,
        AccessType = (AccessControlType)dto.AccessType,
        BeforeRights = dto.BeforeRights,
        AfterRights = dto.AfterRights,
        Details = dto.Details,
        AppliesTo = dto.AppliesTo,
        IsInherited = dto.IsInherited
    };
}
