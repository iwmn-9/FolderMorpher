using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FolderMorpher.Contracts
{
    /// <summary>
    /// FolderMorpher.Host と GUI/CLI/Agent 間を繋ぐ IPC サービス契約
    /// （ADR 101: Host/Core/GUI 完全分離境界）
    /// </summary>
    public interface IFolderMorpherHostService
    {
        // ==========================================
        // 1. システム健全性 & 状態
        // ==========================================
        Task<HostStatusDto> GetStatusAsync();
        Task<bool> PingAsync();
        Task<AppSettingsDto> GetAppSettingsAsync();
        Task SaveAppSettingsAsync(AppSettingsDto settings);
        Task SetLanguageAsync(string language);
        Task<Guid> StartJobAsync(HostJobRequestDto request);
        Task<HostJobStatusDto> GetJobStatusAsync(Guid jobId);
        Task<bool> CancelJobAsync(Guid jobId);
        Task<bool> ReleaseJobAsync(Guid jobId);

        // ==========================================
        // 2. Tab 1: Storage Explorer
        // ==========================================
        Task<StorageScanResultDto> ScanStorageAsync(StorageScanRequestDto request, IProgress<StorageScanProgressDto>? progress, CancellationToken ct);
        Task<StorageNodeDto?> LoadCachedTreeAsync(string targetPath);
        Task<List<StorageNodeDto>> GetStorageChildrenAsync(string rootPath, string folderPath);
        Task<bool> HasCachedTreeAsync(string targetPath);
        Task SaveTreeCacheAsync(StorageNodeDto rootNode);
        Task<List<ScanSnapshotDto>> GetStorageHistoryAsync(string targetPath);
        Task<List<StorageTopFileDto>> GetStorageTopFilesAsync(string rootPath, StorageNodeDto node, CancellationToken ct);
        Task<StorageForecastDto> AnalyzeStorageHistoryAsync(List<ScanSnapshotDto> history, long thresholdBytes, CancellationToken ct);

        // ==========================================
        // 3. Tab 2: Search Studio
        // ==========================================
        Task<List<SearchResultDto>> SearchAsync(string targetFolder, SearchQueryDto query, IProgress<SearchProgressDto>? progress, CancellationToken ct);
        Task<List<SearchResultDto>> SearchInMemoryAsync(string targetPath, SearchQueryDto query, IProgress<SearchProgressDto>? progress, CancellationToken ct);
        Task ExportSearchResultsAsync(string outputPath, List<SearchResultDto> results, CancellationToken ct);

        // ==========================================
        // 4. Tab 3: Live ACL & Effective Access
        // ==========================================
        Task<List<AclEntryDto>> GetFolderAclAsync(string folderPath, CancellationToken ct);
        Task<AclFolderStateDto> GetAclFolderStateAsync(string folderPath, CancellationToken ct);
        Task<StorageNodeDto> LoadAclFolderTreeAsync(string rootPath, int maxDepth, CancellationToken ct);
        Task<FolderCreatePlanDto> PrepareFolderCreationAsync(string parentPath, string folderName, CancellationToken ct);
        Task<string> CommitFolderCreationAsync(Guid planId, CancellationToken ct);
        Task<AclChangePreviewDto> PrepareAclChangeAsync(AclChangeRequestDto request, CancellationToken ct);
        Task<AclCommitResultDto> CommitAclChangeAsync(Guid planId, bool forceIfConflict, CancellationToken ct);
        Task<List<AclSnapshotInfoDto>> GetAclSnapshotsAsync(string folderPath, CancellationToken ct);
        Task<AclFolderStateDto> RollbackAclSnapshotAsync(string folderPath, string snapshotId, CancellationToken ct);
        Task ExportAclMatrixAsync(string outputPath, string folderPath, CancellationToken ct);
        Task<MembershipResolutionDto> ResolveEffectiveMembershipsAsync(string targetAccount, CancellationToken ct);
        Task<EffectiveAccessReportDto> RunEffectiveAccessAuditAsync(string rootPath, string targetAccount, int maxDepth, MembershipResolutionDto? resolution, IProgress<string>? progress, CancellationToken ct);
        Task<List<AdPrincipalDto>> GetAdPrincipalsAsync(string filter, CancellationToken ct);
        Task<DirectoryStatusDto> GetDirectoryStatusAsync();
        Task<List<AdOuNodeDto>> GetAdOuHierarchyAsync(CancellationToken ct);
        Task<List<AdPrincipalDto>> GetAdPrincipalsInOuAsync(string distinguishedName, string keyword, bool includeUsers, bool includeGroups, CancellationToken ct);
        Task ExportEffectiveAccessReportAsync(string outputPath, EffectiveAccessReportDto report, CancellationToken ct);

        // ==========================================
        // 5. Tab 4: Simulation Studio
        // ==========================================
        Task<string> GenerateMigrationPackageAsync(List<MigrationNodeDto> rootNodes, MigrationPackageOptionsDto options, CancellationToken ct);
        Task<List<MigrationWaveDto>> PlanMigrationWavesAsync(List<MigrationNodeDto> rootNodes, MigrationPackageOptionsDto options, CancellationToken ct);
        Task<StorageNodeDto> LoadMigrationSourceFolderAsync(string path, CancellationToken ct);
        Task<MigrationNodeDto> CreateMigrationNodeFromStorageAsync(StorageNodeDto source, int level, int maxDepth, CancellationToken ct);
        Task<MigrationNodeDto> ConvertStorageToMigrationAsync(StorageNodeDto source, CancellationToken ct);
        Task<List<MigrationDiffDto>> GenerateMigrationDiffAsync(StorageNodeDto? sourceRoot, List<MigrationNodeDto> targetRoots, CancellationToken ct);
        Task<SkeletonDeployPlanDto> BuildSkeletonDeployPlanAsync(StorageNodeDto? sourceRoot, List<MigrationNodeDto> targetRoots, string targetRoot, CancellationToken ct);
        Task SaveSimulationProjectAsync(SimulationProjectDto project, string filePath, CancellationToken ct);
        Task<SimulationProjectDto?> LoadSimulationProjectAsync(string filePath, CancellationToken ct);
        Task ExportSimulationMatrixAsync(string outputPath, List<MigrationNodeDto> rootNodes, CancellationToken ct);
        Task<SkeletonDeployResultDto> DeploySkeletonTreeAsync(string targetRoot, MigrationNodeDto rootNode, CancellationToken ct);
        Task<SkeletonDeployResultDto> DeploySkeletonPlanAsync(SkeletonDeployPlanDto plan, CancellationToken ct);
        Task<AclVerificationDto> VerifyFolderDaclAsync(string targetPath, MigrationNodeDto planNode, CancellationToken ct);

        // ==========================================
        // 6. Tab 5: LinkFixer
        // ==========================================
        Task<LinkFixScanResultDto> ScanBrokenLinksAsync(string targetPath, string oldPrefix, string newPrefix, IProgress<string>? progress, CancellationToken ct);
        Task<LinkFixApplyResultDto> RepairBrokenLinksAsync(LinkFixApplyRequestDto request, IProgress<string>? progress, CancellationToken ct);

        // ==========================================
        // 7. Tab 6: Audit & Hygiene
        // ==========================================
        Task<AuditReportDto> RunAuditScanAsync(AuditScanRequestDto request, IProgress<string>? progress, CancellationToken ct);
        Task<List<Guid>> SortAuditIdsAsync(Guid reportId, List<Guid> visibleIds, string sortProperty, bool descending, CancellationToken ct);
        Task AddAuditIgnoreAsync(AuditItemDto item, CancellationToken ct);
        Task<List<AuditIgnoreItemDto>> GetAuditIgnoresAsync();
        Task ClearAuditIgnoresAsync();
        Task<int> GetAuditIgnoreCountAsync();
        Task<AuditCleanupPreviewDto> PrepareAuditCleanupAsync(AuditCleanupPrepareRequestDto request, CancellationToken ct);
        Task<AuditCleanupResultDto> CommitAuditCleanupAsync(Guid planId, CancellationToken ct);

        // ==========================================
        // 8. Tab 7: Media Optimizer
        // ==========================================
        Task<MediaScanResultDto> ScanMediaAsync(MediaOptimizeOptionsDto options, IProgress<string>? progress, CancellationToken ct);
        Task<MediaOptimizeResultDto> OptimizeImagesAsync(List<MediaItemDto> items, MediaOptimizeOptionsDto options, IProgress<string>? progress, CancellationToken ct);
        Task GenerateVideoCompressBatchAsync(string outputPath, List<MediaItemDto> videos, CancellationToken ct);
        Task GenerateGpoLogonScriptAsync(string outputPath, string oldPrefix, string newPrefix, CancellationToken ct);
        Task ExportAuditCsvAsync(string outputPath, List<AuditItemDto> items, CancellationToken ct);
        Task ExportExcelReportAsync(ReportExportDto request, CancellationToken ct);
        Task ExportStorageScanAsync(string outputPath, string targetPath, List<StorageNodeDto> visibleRows, CancellationToken ct);
        Task ExportMigrationDiffAsync(string outputPath, List<MigrationDiffDto> diffs, CancellationToken ct);
    }
}
