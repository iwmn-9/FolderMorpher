namespace FolderMorpher.Contracts;

public sealed class AuditScoreFactorDto
{
    public string NameJa { get; set; } = string.Empty;
    public string NameEn { get; set; } = string.Empty;
    public int Points { get; set; }
}

public sealed class AuditItemDto
{
    public Guid AuditId { get; set; }
    public bool IsChecked { get; set; }
    public string FullPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public long Size { get; set; }
    public DateTime LastWriteTime { get; set; }
    public DateTime LastAccessTime { get; set; }
    public int IssueType { get; set; }
    public string Detail { get; set; } = string.Empty;
    public string? Sha256Hash { get; set; }
    public string DuplicateGroupId { get; set; } = string.Empty;
    public int DuplicateGroupIndex { get; set; }
    public int DuplicateGroupColorIndex { get; set; }
    public bool IsOriginalCandidate { get; set; }
    public bool IsIgnored { get; set; }
    public int WasteScore { get; set; }
    public string? RelatedActivePath { get; set; }
    public List<AuditScoreFactorDto> ScoreBreakdown { get; set; } = new();
}

public sealed class ScanCoverageDto
{
    public int TotalFoldersScanned { get; set; }
    public int AccessDeniedFolders { get; set; }
    public int TotalFilesFound { get; set; }
}

public sealed class AuditSummaryDto
{
    public long TotalFilesScanned { get; set; }
    public ScanCoverageDto Coverage { get; set; } = new();
    public int DuplicateCount { get; set; }
    public long DuplicateWastedBytes { get; set; }
    public int DormantCount { get; set; }
    public long DormantBytes { get; set; }
    public int VersionFamilyCount { get; set; }
    public long VersionFamilyBytes { get; set; }
    public int ExtractedArchiveCount { get; set; }
    public long ExtractedArchiveBytes { get; set; }
    public int GraveyardTreeCount { get; set; }
    public long GraveyardTreeBytes { get; set; }
    public int PathTooLongCount { get; set; }
    public int InvalidCharCount { get; set; }
}

public sealed class AuditCleanupPrepareRequestDto
{
    public Guid ReportId { get; set; }
    public List<Guid> SelectedAuditIds { get; set; } = new();
    public bool RetainForCommit { get; set; }
}

public sealed class AuditCleanupPreviewDto
{
    public Guid PlanId { get; set; }
    public int SafeFileCount { get; set; }
    public long SafeSizeBytes { get; set; }
    public List<string> ProtectedOriginalPaths { get; set; } = new();
}

public sealed class AuditCleanupResultDto
{
    public int SuccessCount { get; set; }
    public long FreedBytes { get; set; }
    public List<string> DeletedPaths { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}
