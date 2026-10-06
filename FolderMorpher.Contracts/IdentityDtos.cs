namespace FolderMorpher.Contracts;

public sealed class DirectoryStatusDto
{
    public bool IsDomainJoined { get; set; }
    public string CurrentDomainName { get; set; } = string.Empty;
}

public sealed class AdOuNodeDto
{
    public string Name { get; set; } = string.Empty;
    public string DistinguishedName { get; set; } = string.Empty;
    public int NodeType { get; set; }
    public List<AdOuNodeDto> Children { get; set; } = new();
}

public sealed class AdPrincipalDto
{
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int PrincipalType { get; set; }
    public string Domain { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class GroupMembershipDto
{
    public string GroupName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Sid { get; set; } = string.Empty;
    public bool IsDirect { get; set; }
    public int NestingDepth { get; set; }
    public string MembershipPath { get; set; } = string.Empty;
}

public sealed class MembershipResolutionDto
{
    public List<GroupMembershipDto> Groups { get; set; } = new();
    public int ResolutionMode { get; set; }
    public string StatusText { get; set; } = string.Empty;
}

public sealed class EffectiveFolderAccessDto
{
    public string FolderPath { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public int PermissionLevel { get; set; }
    public int AllowedRights { get; set; }
    public int DeniedRights { get; set; }
    public bool HasDeny { get; set; }
    public bool IsInherited { get; set; }
    public int ChangeType { get; set; }
    public string GrantSource { get; set; } = string.Empty;
    public string GrantPathTrace { get; set; } = string.Empty;
}

public sealed class EffectiveAccessReportDto
{
    public string TargetAccountName { get; set; } = string.Empty;
    public string TargetDisplayName { get; set; } = string.Empty;
    public int PrincipalType { get; set; }
    public string RootFolderPath { get; set; } = string.Empty;
    public DateTime ScanTimestamp { get; set; }
    public List<GroupMembershipDto> GroupMemberships { get; set; } = new();
    public List<EffectiveFolderAccessDto> AccessibleFolders { get; set; } = new();
    public List<EffectiveFolderAccessDto> SeveredFolders { get; set; } = new();
    public List<EffectiveFolderAccessDto> UnavailableFolders { get; set; } = new();
    public int TotalFoldersScanned { get; set; }
    public int FullControlCount { get; set; }
    public int ModifyCount { get; set; }
    public int ReadOnlyCount { get; set; }
    public int EnclaveCount { get; set; }
    public int ExplicitBoundaryCount { get; set; }
    public int ResolutionMode { get; set; }
    public string ResolutionStatusText { get; set; } = string.Empty;
}
