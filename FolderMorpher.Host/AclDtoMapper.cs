using System.Security.AccessControl;
using AstraSize.Models;
using FolderMorpher.Contracts;

namespace FolderMorpher.Host;

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

    public static SimAclEntry ToCore(AclEntryDto dto) => new()
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
}
