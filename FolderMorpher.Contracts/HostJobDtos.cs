namespace FolderMorpher.Contracts;

public enum HostJobKind
{
    Search = 1,
    AuditScan = 2,
    DeploySkeleton = 3,
    MigrationPackage = 4,
    EffectiveAccessAudit = 5,
    CachedSearch = 6
}

public enum HostJobState
{
    Running = 1,
    Completed = 2,
    Canceled = 3,
    Failed = 4
}

public sealed class HostJobRequestDto
{
    public HostJobKind Kind { get; set; }
    public string TargetPath { get; set; } = string.Empty;
    public SearchQueryDto? SearchQuery { get; set; }
    public AuditScanRequestDto? AuditRequest { get; set; }
    public SkeletonDeployPlanDto? SkeletonPlan { get; set; }
    public List<MigrationNodeDto>? MigrationNodes { get; set; }
    public MigrationPackageOptionsDto? MigrationOptions { get; set; }
    public string TargetAccount { get; set; } = string.Empty;
    public int MaxDepth { get; set; } = int.MaxValue;
    public MembershipResolutionDto? MembershipResolution { get; set; }
}

public sealed class HostJobStatusDto
{
    public Guid JobId { get; set; }
    public HostJobKind Kind { get; set; }
    public HostJobState State { get; set; }
    public string ProgressText { get; set; } = string.Empty;
    public int HitCount { get; set; }
    public long TotalHitBytes { get; set; }
    public string? Error { get; set; }
    public List<SearchResultDto>? SearchResults { get; set; }
    public AuditReportDto? AuditReport { get; set; }
    public SkeletonDeployResultDto? SkeletonResult { get; set; }
    public string? PackageDirectory { get; set; }
    public EffectiveAccessReportDto? EffectiveAccessReport { get; set; }
}
