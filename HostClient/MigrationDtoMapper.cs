using AstraSize.Models;
using FolderMorpher.Contracts;
using FolderMorpher.Models;
using System.Collections.ObjectModel;
using System.Security.AccessControl;

namespace FolderMorpher.HostClient;

internal static class MigrationDtoMapper
{
    public static MigrationWavePlan ToView(MigrationWaveDto dto) => new()
    {
        WaveNumber = dto.WaveNumber,
        WaveName = dto.WaveName,
        TotalSizeBytes = dto.TotalSizeBytes,
        TotalFileCount = dto.TotalFileCount,
        EstimatedFullCopyTime = dto.EstimatedFullCopyTime,
        EstimatedCutoverTime = dto.EstimatedCutoverTime,
        MappedSourcePaths = dto.MappedSourcePaths
    };

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

    public static SimDiffItem ToView(MigrationDiffDto dto) => new()
    {
        DiffType = dto.DiffType,
        Kind = (SimDiffKind)dto.Kind,
        SourcePath = dto.SourcePath,
        SourceDetail = dto.SourceDetail,
        TargetPath = dto.TargetPath,
        TargetDetail = dto.TargetDetail,
        AclChanges = dto.AclChanges.ToList()
    };

    public static SkeletonDeployPlan ToView(SkeletonDeployPlanDto dto) => new()
    {
        DestinationRoot = dto.DestinationRoot,
        FolderActions = dto.FolderActions.Select(action => new SkeletonFolderAction
        {
            RelativePath = action.RelativePath,
            FullTargetPath = action.FullTargetPath,
            IsExisting = action.IsExisting,
            InheritAcl = action.InheritAcl,
            AclEntries = action.AclEntries.Select(AclDtoMapper.ToView).ToList()
        }).ToList(),
        DiffReviews = dto.DiffReviews.Select(ToView).ToList()
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
        AclEntries = node.AclEntries.Select(entry => new AclEntryDto
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
        }).ToList(),
        Children = node.Children.Select(ToDto).ToList()
    };

    public static SimFolderNode ToViewNode(MigrationNodeDto dto, SimFolderNode? parent = null)
    {
        var node = new SimFolderNode
        {
            Id = dto.Id,
            Name = dto.Name,
            Level = dto.Level,
            EstimatedSizeBytes = dto.EstimatedSizeBytes,
            EstimatedFileCount = dto.EstimatedFileCount,
            InheritAcl = dto.InheritAcl,
            Parent = parent,
            MappedSourcePaths = new ObservableCollection<string>(dto.MappedSourcePaths),
            AclEntries = new ObservableCollection<SimAclEntry>(dto.AclEntries.Select(entry => new SimAclEntry
            {
                Sid = entry.Sid,
                AccountName = entry.AccountName,
                DisplayName = entry.DisplayName,
                PrincipalType = (AdPrincipalType)entry.PrincipalType,
                Rights = (FileSystemRights)entry.Rights,
                AccessType = (AccessControlType)entry.AccessType,
                IsInherited = entry.IsInherited,
                InheritanceFlags = (InheritanceFlags)entry.InheritanceFlags,
                PropagationFlags = (PropagationFlags)entry.PropagationFlags
            }))
        };
        node.Children = new ObservableCollection<SimFolderNode>(dto.Children.Select(child => ToViewNode(child, node)));
        return node;
    }

    public static MigrationPackageOptionsDto ToDto(MigrationPackageOptions options) => new()
    {
        Policy = (int)options.Policy,
        SizeBudgetBytes = options.SizeBudgetBytes,
        OutputDirectory = options.OutputDirectory,
        TargetRoot = options.TargetRoot,
        CopyAcl = options.CopyAcl,
        Threads = options.Threads,
        TransferRateMBps = options.TransferRateMBps,
        DeltaRatioPercent = options.DeltaRatioPercent,
        IncludeRunbookExcel = options.IncludeRunbookExcel,
        IncludeOldShareLock = options.IncludeOldShareLock
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
            AclEntries = action.AclEntries.Select(entry => new AclEntryDto
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
            }).ToList()
        }).ToList(),
        DiffReviews = plan.DiffReviews.Select(diff => new MigrationDiffDto
        {
            DiffType = diff.DiffType,
            Kind = (int)diff.Kind,
            SourcePath = diff.SourcePath,
            SourceDetail = diff.SourceDetail,
            TargetPath = diff.TargetPath,
            TargetDetail = diff.TargetDetail,
            AclChanges = diff.AclChanges
        }).ToList()
    };
}
