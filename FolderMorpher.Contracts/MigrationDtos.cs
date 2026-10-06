namespace FolderMorpher.Contracts;

/// <summary>Migration design tree with child-only links and security facts.</summary>
public sealed class MigrationNodeDto
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int Level { get; set; }
    public long EstimatedSizeBytes { get; set; }
    public long? EstimatedFileCount { get; set; }
    public bool InheritAcl { get; set; }
    public List<string> MappedSourcePaths { get; set; } = new();
    public List<AclEntryDto> AclEntries { get; set; } = new();
    public List<MigrationNodeDto> Children { get; set; } = new();
}

public sealed class MigrationPackageOptionsDto
{
    public int Policy { get; set; }
    public long SizeBudgetBytes { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;
    public string TargetRoot { get; set; } = string.Empty;
    public bool CopyAcl { get; set; }
    public int Threads { get; set; }
    public double TransferRateMBps { get; set; }
    public double DeltaRatioPercent { get; set; }
    public bool IncludeRunbookExcel { get; set; }
    public bool IncludeOldShareLock { get; set; }
}

public sealed class MigrationWaveDto
{
    public int WaveNumber { get; set; }
    public string WaveName { get; set; } = string.Empty;
    public long TotalSizeBytes { get; set; }
    public long? TotalFileCount { get; set; }
    public TimeSpan EstimatedFullCopyTime { get; set; }
    public TimeSpan EstimatedCutoverTime { get; set; }
    public List<string> MappedSourcePaths { get; set; } = new();
}

public sealed class SkeletonFolderActionDto
{
    public string RelativePath { get; set; } = string.Empty;
    public string FullTargetPath { get; set; } = string.Empty;
    public bool IsExisting { get; set; }
    public bool InheritAcl { get; set; }
    public List<AclEntryDto> AclEntries { get; set; } = new();
}

public sealed class MigrationDiffDto
{
    public string DiffType { get; set; } = string.Empty;
    public int Kind { get; set; }
    public string SourcePath { get; set; } = string.Empty;
    public string SourceDetail { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public string TargetDetail { get; set; } = string.Empty;
    public List<string> AclChanges { get; set; } = new();
}

public sealed class SkeletonDeployPlanDto
{
    public string DestinationRoot { get; set; } = string.Empty;
    public List<SkeletonFolderActionDto> FolderActions { get; set; } = new();
    public List<MigrationDiffDto> DiffReviews { get; set; } = new();
}

public sealed class SkeletonDeployResultDto
{
    public int CreatedCount { get; set; }
    public int SkippedExistingCount { get; set; }
    public int ConflictCount { get; set; }
    public int AclAppliedCount { get; set; }
    public int VerifiedCount { get; set; }
    public int VerificationFailedCount { get; set; }
    public List<string> Errors { get; set; } = new();
    public List<string> VerificationErrors { get; set; } = new();
    public List<string> Logs { get; set; } = new();
    public List<string> DeployedFolderPaths { get; set; } = new();
    public bool AllPathsExist { get; set; }
    public int FailedCount => Errors.Count + VerificationFailedCount;
}

public sealed class AclVerificationDto
{
    public bool IsSuccess { get; set; }
    public string StatusText { get; set; } = string.Empty;
    public List<string> Discrepancies { get; set; } = new();
}
