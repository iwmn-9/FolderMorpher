using System.Collections.ObjectModel;
using AstraSize.Models;
using FolderMorpher.Contracts;
using FolderMorpher.Models;

namespace FolderMorpher.Host;

internal static class MigrationDtoMapper
{
    public static MigrationDiffDto ToDto(SimDiffItem item) => new()
    {
        DiffType = item.DiffType,
        Kind = (int)item.Kind,
        SourcePath = item.SourcePath,
        SourceDetail = item.SourceDetail,
        TargetPath = item.TargetPath,
        TargetDetail = item.TargetDetail,
        AclChanges = item.AclChanges.ToList()
    };

    public static SkeletonDeployPlanDto ToDto(SkeletonDeployPlan plan) => new()
    {
        DestinationRoot = plan.DestinationRoot,
        FolderActions = plan.FolderActions.Select(action => new SkeletonFolderActionDto
        {
            RelativePath = action.RelativePath,
            FullTargetPath = action.FullTargetPath,
            IsExisting = action.IsExisting,
            InheritAcl = action.InheritAcl,
            AclEntries = action.AclEntries.Select(AclDtoMapper.ToDto).ToList()
        }).ToList(),
        DiffReviews = plan.DiffReviews.Select(ToDto).ToList()
    };

    public static MigrationNodeDto ToDto(SimFolderNode node) => new()
    {
        Id = node.Id,
        Name = node.Name,
        Level = node.Level,
        EstimatedSizeBytes = node.EstimatedSizeBytes,
        EstimatedFileCount = node.EstimatedFileCount,
        InheritAcl = node.InheritAcl,
        MappedSourcePaths = node.MappedSourcePaths.ToList(),
        AclEntries = node.AclEntries.Select(AclDtoMapper.ToDto).ToList(),
        Children = node.Children.Select(ToDto).ToList()
    };

    public static SimFolderNode ToCore(MigrationNodeDto dto, SimFolderNode? parent = null)
    {
        var node = new SimFolderNode
        {
            Id = dto.Id, Name = dto.Name, Level = dto.Level,
            EstimatedSizeBytes = dto.EstimatedSizeBytes,
            EstimatedFileCount = dto.EstimatedFileCount,
            InheritAcl = dto.InheritAcl, Parent = parent,
            MappedSourcePaths = new ObservableCollection<string>(dto.MappedSourcePaths),
            AclEntries = new ObservableCollection<SimAclEntry>(dto.AclEntries.Select(AclDtoMapper.ToCore))
        };
        node.Children = new ObservableCollection<SimFolderNode>(dto.Children.Select(child => ToCore(child, node)));
        return node;
    }

    public static MigrationPackageOptions ToCore(MigrationPackageOptionsDto dto) => new()
    {
        Policy = (MigrationSplitPolicy)dto.Policy,
        SizeBudgetBytes = dto.SizeBudgetBytes,
        OutputDirectory = dto.OutputDirectory,
        TargetRoot = dto.TargetRoot,
        CopyAcl = dto.CopyAcl,
        Threads = dto.Threads,
        TransferRateMBps = dto.TransferRateMBps,
        DeltaRatioPercent = dto.DeltaRatioPercent,
        IncludeRunbookExcel = dto.IncludeRunbookExcel,
        IncludeOldShareLock = dto.IncludeOldShareLock
    };

    public static SkeletonDeployPlan ToCore(SkeletonDeployPlanDto dto) => new()
    {
        DestinationRoot = dto.DestinationRoot,
        FolderActions = dto.FolderActions.Select(action => new SkeletonFolderAction
        {
            RelativePath = action.RelativePath,
            FullTargetPath = action.FullTargetPath,
            IsExisting = action.IsExisting,
            InheritAcl = action.InheritAcl,
            AclEntries = action.AclEntries.Select(AclDtoMapper.ToCore).ToList()
        }).ToList(),
        DiffReviews = dto.DiffReviews.Select(diff => new SimDiffItem
        {
            DiffType = diff.DiffType,
            Kind = (SimDiffKind)diff.Kind,
            SourcePath = diff.SourcePath,
            SourceDetail = diff.SourceDetail,
            TargetPath = diff.TargetPath,
            TargetDetail = diff.TargetDetail,
            AclChanges = diff.AclChanges
        }).ToList()
    };

    public static SkeletonDeployResultDto ToDto(DeploySkeletonResult result, string destinationRoot) => new()
    {
        CreatedCount = result.CreatedCount,
        SkippedExistingCount = result.SkippedExistingCount,
        ConflictCount = result.ConflictCount,
        AclAppliedCount = result.AclAppliedCount,
        VerifiedCount = result.VerifiedCount,
        VerificationFailedCount = result.VerificationFailedCount,
        Errors = result.Errors,
        VerificationErrors = result.VerificationErrors,
        Logs = result.Logs,
        DeployedFolderPaths = result.DeployedFolderPaths,
        AllPathsExist = Directory.Exists(destinationRoot) && result.DeployedFolderPaths.All(Directory.Exists)
    };

    public static AclVerificationDto ToDto(AclVerificationResult result) => new()
    {
        IsSuccess = result.IsSuccess,
        StatusText = result.StatusText,
        Discrepancies = result.Discrepancies
    };
}
