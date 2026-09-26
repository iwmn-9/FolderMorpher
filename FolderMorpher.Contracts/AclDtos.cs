namespace FolderMorpher.Contracts;

/// <summary>Security facts only; rights and flags use Windows enum numeric values.</summary>
public sealed class AclEntryDto
{
    public string Sid { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int PrincipalType { get; set; }
    public int Rights { get; set; }
    public int AccessType { get; set; }
    public bool IsInherited { get; set; }
    public int InheritanceFlags { get; set; }
    public int PropagationFlags { get; set; }
}

public sealed class AclFolderStateDto
{
    public string FolderPath { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Sddl { get; set; } = string.Empty;
    public bool InheritsAcl { get; set; }
    public List<AclEntryDto> Entries { get; set; } = new();
}

public sealed class AclChangeRequestDto
{
    public string FolderPath { get; set; } = string.Empty;
    public string ExpectedOriginalSddl { get; set; } = string.Empty;
    public bool InheritanceBefore { get; set; }
    public bool InheritanceAfter { get; set; }
    public List<AclEntryDto> OriginalEntries { get; set; } = new();
    public List<AclEntryDto> CurrentEntries { get; set; } = new();
}

public sealed class AclDiffItemDto
{
    public int DiffType { get; set; }
    public string AccountName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string IconGlyph { get; set; } = string.Empty;
    public int AccessType { get; set; }
    public string BeforeRights { get; set; } = string.Empty;
    public string AfterRights { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string AppliesTo { get; set; } = string.Empty;
    public bool IsInherited { get; set; }
}

public sealed class AclChangePreviewDto
{
    public Guid PlanId { get; set; }
    public bool InheritanceChanged { get; set; }
    public bool InheritanceAfter { get; set; }
    public int InheritedAcesPromotedCount { get; set; }
    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int ModifiedCount { get; set; }
    public int UntouchedCount { get; set; }
    public bool HasConflict { get; set; }
    public List<AclDiffItemDto> DiffItems { get; set; } = new();
}

public sealed class AclCommitResultDto
{
    public bool WasConflict { get; set; }
    public int AddedCount { get; set; }
    public int RemovedCount { get; set; }
    public int ModifiedCount { get; set; }
    public bool VerificationSucceeded { get; set; }
    public List<string> VerificationDiscrepancies { get; set; } = new();
    public AclFolderStateDto CurrentState { get; set; } = new();
}

public sealed class AclSnapshotInfoDto
{
    public string Id { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Note { get; set; } = string.Empty;
}
