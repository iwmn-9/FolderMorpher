using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AstraSize.Models;
using AstraSize.Services;
using FolderMorpher.Contracts;
using FolderMorpher.Models;
using FolderMorpher.Services;

namespace FolderMorpher.Host
{
    /// <summary>
    /// Core のサービス群を束ね、StreamJsonRpc 経由でクライアント (WPF GUI / CLI / Agent) へ提供する実装
    /// </summary>
    public class HostService : IFolderMorpherHostService
    {
        private readonly DiskScanService _diskScanService;
        private readonly SqliteTreeCacheService _treeCache;
        private readonly SearchEngineService _searchEngine;
        private readonly AclService _aclService;
        private readonly EffectiveAccessService _effectiveAccessService;
        private readonly ActiveDirectoryService _adService;
        private readonly SimulationProjectService _simProjectService;
        private readonly MigrationPackageService _migrationPackageService;
        private readonly LinkFixService _linkFixService;
        private readonly OfficeLinkFixService _officeLinkFixService;
        private readonly MediaOptimizerService _mediaOptimizerService;

        public HostService()
        {
            _diskScanService = new DiskScanService();
            _treeCache = new SqliteTreeCacheService();
            _searchEngine = new SearchEngineService();
            _aclService = new AclService();
            _adService = new ActiveDirectoryService();
            _effectiveAccessService = new EffectiveAccessService(_adService);
            _simProjectService = new SimulationProjectService();
            _migrationPackageService = new MigrationPackageService();
            _officeLinkFixService = new OfficeLinkFixService();
            _linkFixService = new LinkFixService();
            _mediaOptimizerService = new MediaOptimizerService();
        }

        // ==========================================
        // 1. システム健全性 & 状態
        // ==========================================
        public Task<HostStatusDto> GetStatusAsync()
        {
            return Task.FromResult(new HostStatusDto
            {
                IsRunning = true,
                ActiveJobCount = 0
            });
        }

        public Task<bool> PingAsync()
        {
            return Task.FromResult(true);
        }

        // ==========================================
        // 2. Tab 1: Storage Explorer
        // ==========================================
        public async Task<StorageScanResultDto> ScanStorageAsync(StorageScanRequestDto request, IProgress<StorageScanProgressDto>? progress, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = new StorageScanResultDto
            {
                TargetPath = request.TargetPath
            };

            try
            {
                var coreProgress = progress != null ? new Progress<ScanProgress>(p =>
                {
                    progress.Report(new StorageScanProgressDto
                    {
                        CurrentDirectory = p.CurrentPath,
                        ScannedFilesCount = p.FilesScanned,
                        ScannedBytes = p.BytesScanned,
                        IsCompleted = false
                    });
                }) : null;

                var (rootNode, summary) = await _diskScanService.ScanPathAsync(request.TargetPath, coreProgress, ct);
                sw.Stop();

                result.RootNode = rootNode;
                result.Elapsed = sw.Elapsed;
                result.TotalBytes = rootNode.Size;
                result.TotalFiles = rootNode.FileCount;
                result.TotalFolders = rootNode.FolderCount;
                result.Top10Files = summary.LargestFiles ?? new List<LargestFileInfo>();

                // DB キャッシュへの自動非同期永続化
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _treeCache.SaveTreeAsync(rootNode);
                    }
                    catch { }
                });
            }
            catch (Exception ex)
            {
                sw.Stop();
                result.ErrorMessage = ex.Message;
                result.Elapsed = sw.Elapsed;
            }

            return result;
        }

        public async Task<FileItemNode?> LoadCachedTreeAsync(string targetPath)
        {
            return await _treeCache.LoadTreeAsync(targetPath);
        }

        public async Task SaveTreeCacheAsync(FileItemNode rootNode)
        {
            await _treeCache.SaveTreeAsync(rootNode);
        }

        // ==========================================
        // 3. Tab 2: Search Studio
        // ==========================================
        public async Task<List<SearchResultItem>> SearchAsync(string targetFolder, SearchQuery query, IProgress<SearchProgressReport>? progress, CancellationToken ct)
        {
            return await _searchEngine.SearchDirectFolderAsync(targetFolder, query, null, progress, ct);
        }

        public async Task<List<SearchResultItem>> SearchInMemoryAsync(string targetPath, SearchQuery query, IProgress<SearchProgressReport>? progress, CancellationToken ct)
        {
            var cachedRoot = await _treeCache.LoadTreeAsync(targetPath);
            if (cachedRoot == null)
            {
                return new List<SearchResultItem>();
            }

            return await _searchEngine.SearchInMemoryAsync(new[] { cachedRoot }, query, progress, ct);
        }

        // ==========================================
        // 4. Tab 3: Live ACL & Effective Access
        // ==========================================
        public Task<List<SimAclEntry>> GetFolderAclAsync(string folderPath, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var (entries, _, _) = _aclService.GetSimAclForFolder(folderPath);
                return entries;
            }, ct);
        }

        public async Task<AclApplyResultDto> ApplyFolderAclAsync(AclApplyRequestDto request, CancellationToken ct)
        {
            var result = new AclApplyResultDto();
            try
            {
                var (currentEntries, currentInherit, _) = _aclService.GetSimAclForFolder(request.TargetFolder);
                var (hasModified, added, removed, modified, snapshot) = 
                    await _aclService.ApplyLiveAclDeltaWithRollbackAsync(
                        request.TargetFolder,
                        currentEntries,
                        request.TargetEntries,
                        request.InheritFromParent,
                        currentInherit);

                result.Success = hasModified;
                result.AddedCount = added;
                result.RemovedCount = removed;
                result.ModifiedCount = modified;
                result.OriginalSddl = snapshot?.Sddl;
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.ErrorMessage = ex.Message;
            }
            return result;
        }

        public Task<bool> RollbackFolderAclAsync(string folderPath, string sddlSnapshot, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                try
                {
                    var snapshot = new AclSnapshot
                    {
                        TargetPath = folderPath,
                        Sddl = sddlSnapshot,
                        Timestamp = DateTime.Now
                    };
                    _aclService.RollbackToSnapshot(folderPath, snapshot);
                    return true;
                }
                catch
                {
                    return false;
                }
            }, ct);
        }

        public async Task<EffectiveAccessAuditReport> RunEffectiveAccessAuditAsync(string rootPath, string targetAccount, int maxDepth, IProgress<string>? progress, CancellationToken ct)
        {
            var accProgress = progress != null 
                ? new Progress<(int scanned, int found)>(p => progress.Report($"走査中: {p.scanned} フォルダ (該当: {p.found})"))
                : null;
            return await _effectiveAccessService.ScanEffectiveAccessAsync(rootPath, targetAccount, null, maxDepth, accProgress, ct);
        }

        public async Task<List<AdPrincipalItem>> GetAdPrincipalsAsync(string filter, CancellationToken ct)
        {
            return await _adService.SearchPrincipalsAsync(filter);
        }

        // ==========================================
        // 5. Tab 4: Simulation Studio
        // ==========================================
        public async Task<string> GenerateMigrationPackageAsync(List<SimFolderNode> rootNodes, MigrationPackageOptions options, CancellationToken ct)
        {
            return await _migrationPackageService.GeneratePackageAsync(rootNodes, options, null);
        }

        public async Task<DeploySkeletonResult> DeploySkeletonTreeAsync(string targetRoot, SimFolderNode rootNode, CancellationToken ct)
        {
            return await _simProjectService.DeploySkeletonAsync(new[] { rootNode }, targetRoot, null, ct);
        }

        public async Task<DeploySkeletonResult> DeploySkeletonPlanAsync(SkeletonDeployPlan plan, CancellationToken ct)
        {
            return await _simProjectService.DeploySkeletonAsync(plan, null, ct);
        }

        public Task<AclVerificationResult> VerifyFolderDaclAsync(string targetPath, SimFolderNode planNode, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                return _aclService.VerifyFolderDacl(targetPath, planNode.InheritAcl, planNode.AclEntries);
            }, ct);
        }

        // ==========================================
        // 6. Tab 5: LinkFixer
        // ==========================================
        public async Task<LinkFixScanResultDto> ScanBrokenLinksAsync(string targetPath, string oldPrefix, string newPrefix, IProgress<string>? progress, CancellationToken ct)
        {
            var result = new LinkFixScanResultDto();
            var shortcuts = await _linkFixService.ScanShortcutsAsync(targetPath, oldPrefix, newPrefix, progress, ct);
            var officeLinks = await _officeLinkFixService.ScanOfficeLinksAsync(targetPath, oldPrefix, newPrefix, progress, ct);

            result.BrokenLinks = shortcuts;
            result.OfficeLinks = officeLinks;
            result.ScannedFilesCount = shortcuts.Count + officeLinks.Count;
            return result;
        }

        public async Task<LinkFixApplyResultDto> RepairBrokenLinksAsync(LinkFixApplyRequestDto request, IProgress<string>? progress, CancellationToken ct)
        {
            var result = new LinkFixApplyResultDto();

            if (request.TargetShortcuts.Count > 0)
            {
                var fixProgress = progress != null 
                    ? new Progress<(string Path, bool Success)>(p => progress.Report($"修復中: {p.Path}"))
                    : null;
                int repaired = await _linkFixService.ExecuteFixAsync(request.TargetShortcuts, fixProgress, ct);
                result.RepairedCount += repaired;
            }

            if (request.TargetOfficeLinks.Count > 0)
            {
                var offProgress = progress != null 
                    ? new Progress<(string File, bool Success, string Msg)>(p => progress.Report(p.Msg))
                    : null;
                int repaired = await _officeLinkFixService.ExecuteOfficeFixAsync(request.TargetOfficeLinks, offProgress, ct);
                result.RepairedCount += repaired;
            }

            return result;
        }

        // ==========================================
        // 7. Tab 6: Audit & Hygiene
        // ==========================================
        public async Task<AuditReportDto> RunAuditScanAsync(AuditScanRequestDto request, IProgress<string>? progress, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var auditService = new AuditReportService();
            var options = new AuditOptions
            {
                TargetDirectory = request.TargetPath,
                BandwidthLimit = request.BandwidthLimit,
                ExcludeFolderPatterns = request.IgnoredPaths ?? new List<string>()
            };
            var auditProgress = progress != null 
                ? new Progress<AuditProgress>(p => progress.Report(p.CurrentStatus))
                : null;

            var (summary, items) = await auditService.RunAuditAsync(options, auditProgress, ct);
            sw.Stop();

            return new AuditReportDto
            {
                TargetPath = request.TargetPath,
                Items = items,
                Summary = summary,
                Elapsed = sw.Elapsed
            };
        }

        public Task<AuditCleanupResult> ExecuteAuditCleanupAsync(List<AuditCleanupPlan> plans, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                return AuditCleanupService.ExecutePlan(plans) ?? new AuditCleanupResult();
            }, ct);
        }

        // ==========================================
        // 8. Tab 7: Media Optimizer
        // ==========================================
        public async Task<MediaScanResultDto> ScanMediaAsync(MediaOptimizeOptions options, IProgress<string>? progress, CancellationToken ct)
        {
            var (images, videos) = await _mediaOptimizerService.ScanMediaAsync(options, progress, ct);
            return new MediaScanResultDto
            {
                Images = images,
                Videos = videos
            };
        }

        public async Task<MediaOptimizeSummary> OptimizeImagesAsync(List<MediaItem> items, MediaOptimizeOptions options, IProgress<string>? progress, CancellationToken ct)
        {
            var mediaProgress = new Progress<(string File, bool Success, string Msg)>(p =>
            {
                progress?.Report(p.Msg);
            });
            return await _mediaOptimizerService.OptimizeImagesAsync(items, options, mediaProgress, ct);
        }
    }
}
