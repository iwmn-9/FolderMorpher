using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AstraSize.Models;
using FolderMorpher.Models;
using FolderMorpher.Services;

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

        // ==========================================
        // 2. Tab 1: Storage Explorer
        // ==========================================
        Task<StorageScanResultDto> ScanStorageAsync(StorageScanRequestDto request, IProgress<StorageScanProgressDto>? progress, CancellationToken ct);
        Task<FileItemNode?> LoadCachedTreeAsync(string targetPath);
        Task SaveTreeCacheAsync(FileItemNode rootNode);

        // ==========================================
        // 3. Tab 2: Search Studio
        // ==========================================
        Task<List<SearchResultItem>> SearchAsync(string targetFolder, SearchQuery query, IProgress<SearchProgressReport>? progress, CancellationToken ct);
        Task<List<SearchResultItem>> SearchInMemoryAsync(string targetPath, SearchQuery query, IProgress<SearchProgressReport>? progress, CancellationToken ct);

        // ==========================================
        // 4. Tab 3: Live ACL & Effective Access
        // ==========================================
        Task<List<SimAclEntry>> GetFolderAclAsync(string folderPath, CancellationToken ct);
        Task<AclApplyResultDto> ApplyFolderAclAsync(AclApplyRequestDto request, CancellationToken ct);
        Task<bool> RollbackFolderAclAsync(string folderPath, string sddlSnapshot, CancellationToken ct);
        Task<EffectiveAccessAuditReport> RunEffectiveAccessAuditAsync(string rootPath, string targetAccount, int maxDepth, IProgress<string>? progress, CancellationToken ct);
        Task<List<AdPrincipalItem>> GetAdPrincipalsAsync(string filter, CancellationToken ct);

        // ==========================================
        // 5. Tab 4: Simulation Studio
        // ==========================================
        Task<string> GenerateMigrationPackageAsync(List<SimFolderNode> rootNodes, MigrationPackageOptions options, CancellationToken ct);
        Task<DeploySkeletonResult> DeploySkeletonTreeAsync(string targetRoot, SimFolderNode rootNode, CancellationToken ct);
        Task<AclVerificationResult> VerifyFolderDaclAsync(string targetPath, SimFolderNode planNode, CancellationToken ct);

        // ==========================================
        // 6. Tab 5: LinkFixer
        // ==========================================
        Task<LinkFixScanResultDto> ScanBrokenLinksAsync(string targetPath, string oldPrefix, string newPrefix, IProgress<string>? progress, CancellationToken ct);
        Task<LinkFixApplyResultDto> RepairBrokenLinksAsync(LinkFixApplyRequestDto request, IProgress<string>? progress, CancellationToken ct);

        // ==========================================
        // 7. Tab 6: Audit & Hygiene
        // ==========================================
        Task<AuditReportDto> RunAuditScanAsync(AuditScanRequestDto request, IProgress<string>? progress, CancellationToken ct);
        Task<AuditCleanupResult> ExecuteAuditCleanupAsync(List<AuditCleanupPlan> plans, CancellationToken ct);

        // ==========================================
        // 8. Tab 7: Media Optimizer
        // ==========================================
        Task<MediaScanResultDto> ScanMediaAsync(MediaOptimizeOptions options, IProgress<string>? progress, CancellationToken ct);
        Task<MediaOptimizeSummary> OptimizeImagesAsync(List<MediaItem> items, MediaOptimizeOptions options, IProgress<string>? progress, CancellationToken ct);
    }
}
