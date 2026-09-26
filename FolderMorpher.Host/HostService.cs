using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
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
    public partial class HostService : IFolderMorpherHostService
    {
        private readonly DiskScanService _diskScanService;
        private readonly SqliteTreeCacheService _treeCache;
        private readonly StorageHistoryService _storageHistory;
        private readonly SearchEngineService _searchEngine;
        private readonly AclService _aclService;
        private readonly EffectiveAccessService _effectiveAccessService;
        private readonly ActiveDirectoryService _adService;
        private readonly SimulationProjectService _simProjectService;
        private readonly MigrationPackageService _migrationPackageService;
        private readonly LinkFixService _linkFixService;
        private readonly OfficeLinkFixService _officeLinkFixService;
        private readonly MediaOptimizerService _mediaOptimizerService;
        private readonly ConcurrentDictionary<Guid, PreparedAuditCleanup> _preparedAuditCleanup = new();
        private readonly ConcurrentDictionary<Guid, AuditSnapshot> _auditReports = new();
        private readonly ConcurrentDictionary<string, (DateTime SeenUtc, FileItemNode Root)> _liveScanRoots =
            new(StringComparer.OrdinalIgnoreCase);
        private int _shutdownRequested;
        internal Action? ShutdownRequested { get; set; }

        private sealed record AuditSnapshot(DateTime CreatedUtc, List<AuditItem> Items);
        private sealed record PreparedAuditCleanup(DateTime CreatedUtc, List<AuditCleanupPlan> Plans);

        public HostService()
        {
            _diskScanService = new DiskScanService();
            _treeCache = new SqliteTreeCacheService();
            _storageHistory = StorageHistoryService.Instance;
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
                ActiveJobCount = _jobs.Values.Count(job => job.State == HostJobState.Running)
            });
        }

        public Task<bool> PingAsync()
        {
            return Task.FromResult(true);
        }

        public Task<bool> RequestShutdownAsync(bool cancelActiveJobs)
        {
            if (ShutdownRequested == null) return Task.FromResult(false);
            lock (_jobLifecycleGate)
            {
                var running = _jobs.Values.Where(job => job.State == HostJobState.Running).ToList();
                if (running.Count > 0 && !cancelActiveJobs) return Task.FromResult(false);
                if (_shutdownRequested != 0) return Task.FromResult(true);
                _shutdownRequested = 1;
                foreach (var job in running)
                {
                    lock (job.Gate)
                    {
                        if (job.State == HostJobState.Running) job.Cancellation.Cancel();
                    }
                }
            }
            // Let StreamJsonRpc send the reply before closing its pipe.
            _ = Task.Run(async () =>
            {
                await Task.Delay(200);
                ShutdownRequested?.Invoke();
            });
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

                try
                {
                    var previousRoot = FindLiveScanNode(request.TargetPath);
                    if (previousRoot != null)
                    {
                        _storageHistory.ApplyTreeDiff(rootNode, previousRoot);
                    }
                    else
                    {
                        // Check shared JSON freshness without materializing the old SQLite tree.
                        var cachedBranch = await _storageHistory.LoadTreeCacheBranchAsync(request.TargetPath, request.TargetPath);
                        if (cachedBranch != null)
                        {
                            if (await _treeCache.HasRootAsync(request.TargetPath))
                                await Task.Run(() => _storageHistory.ApplyTreeDiff(rootNode,
                                    _treeCache.EnumeratePathSizes(request.TargetPath, ct)), ct);
                            else
                                _storageHistory.ApplyTreeDiff(rootNode, cachedBranch);
                        }
                    }
                }
                catch { /* History availability must not invalidate a fresh scan. */ }

                result.RootNode = StorageDtoMapper.ToLevelDto(rootNode, 1);
                RememberScanRoot(rootNode);
                result.Elapsed = sw.Elapsed;
                result.TotalBytes = rootNode.Size;
                result.TotalFiles = rootNode.FileCount;
                result.TotalFolders = rootNode.FolderCount;
                result.Top10Files = summary.LargestFiles?.Select(StorageDtoMapper.ToDto).ToList() ?? new();
                result.ScanMode = summary.ScanMode;

                // DB キャッシュへの自動非同期永続化
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _storageHistory.SaveTreeCacheAsync(rootNode);
                        await _storageHistory.SaveSnapshotAsync(rootNode);
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

        public async Task<StorageNodeDto?> LoadCachedTreeAsync(string targetPath)
        {
            var tree = FindLiveScanNode(targetPath) ??
                await _storageHistory.LoadTreeCacheBranchAsync(targetPath, targetPath);
            return tree == null ? null : StorageDtoMapper.ToLevelDto(tree, 1);
        }

        public async Task<List<StorageNodeDto>> GetStorageChildrenAsync(string rootPath, string folderPath)
        {
            var folder = FindLiveScanNode(folderPath) ??
                await _storageHistory.LoadTreeCacheBranchAsync(rootPath, folderPath);
            if (folder == null) throw new DirectoryNotFoundException($"Cached folder not found: {folderPath}");
            return folder.Children.Select(child => StorageDtoMapper.ToLevelDto(child, 0)).ToList();
        }

        public async Task<bool> HasCachedTreeAsync(string targetPath)
        {
            if (FindLiveScanNode(targetPath) != null) return true;
            return await _treeCache.HasRootAsync(targetPath);
        }

        public async Task SaveTreeCacheAsync(StorageNodeDto rootNode)
        {
            var coreRoot = StorageDtoMapper.ToCore(rootNode);
            RememberScanRoot(coreRoot);
            await _storageHistory.SaveTreeCacheAsync(coreRoot);
        }

        public async Task<List<ScanSnapshotDto>> GetStorageHistoryAsync(string targetPath)
        {
            var history = await _storageHistory.GetHistoryForPathAsync(targetPath);
            return history.Select(snapshot => new ScanSnapshotDto
            {
                Id = snapshot.Id,
                TargetPath = snapshot.TargetPath,
                Timestamp = snapshot.Timestamp,
                TotalBytes = snapshot.TotalBytes,
                TotalFiles = snapshot.TotalFiles,
                SubFolders = snapshot.SubFolders.Select(folder => new FolderSnapshotDto
                {
                    Name = folder.Name,
                    Size = folder.Size
                }).ToList()
            }).ToList();
        }

        public async Task<List<StorageTopFileDto>> GetStorageTopFilesAsync(string rootPath, StorageNodeDto node, CancellationToken ct)
        {
            var liveNode = FindLiveScanNode(node.FullPath);
            if (liveNode != null)
            {
                var (topFiles, _) = await Task.Run(() => DiskScanService.GetInsightsForNode(liveNode), ct);
                return topFiles.Select(StorageDtoMapper.ToDto).ToList();
            }
            var cachedBranch = await _storageHistory.LoadTreeCacheBranchAsync(rootPath, node.FullPath);
            if (cachedBranch != null && await _treeCache.HasRootAsync(rootPath))
            {
                var cachedFiles = await _treeCache.GetTopFilesForSubtreeAsync(rootPath, node.FullPath);
                return cachedFiles.Select(StorageDtoMapper.ToDto).ToList();
            }
            var source = cachedBranch ?? StorageDtoMapper.ToCore(node);
            var (fallbackFiles, _) = await Task.Run(() => DiskScanService.GetInsightsForNode(source), ct);
            return fallbackFiles.Select(StorageDtoMapper.ToDto).ToList();
        }

        public Task<StorageForecastDto> AnalyzeStorageHistoryAsync(
            List<ScanSnapshotDto> history, long thresholdBytes, CancellationToken ct)
        {
            return Task.Run(() => ForecastDtoMapper.ToDto(
                StorageForecastingService.Instance.Analyze(history.Select(ForecastDtoMapper.ToCore).ToList(), thresholdBytes)), ct);
        }

        private void RememberScanRoot(FileItemNode root)
        {
            var key = PathCanonicalizer.Normalize(root.FullPath);
            _liveScanRoots[key] = (DateTime.UtcNow, root);
            while (_liveScanRoots.Count > 4)
            {
                var oldest = _liveScanRoots.OrderBy(entry => entry.Value.SeenUtc).FirstOrDefault();
                if (oldest.Key == null) break;
                _liveScanRoots.TryRemove(oldest.Key, out _);
            }
        }

        private FileItemNode? FindLiveScanNode(string targetPath)
        {
            var target = PathCanonicalizer.Normalize(targetPath);
            foreach (var scan in _liveScanRoots.Values.OrderByDescending(value => value.SeenUtc))
            {
                var rootPath = PathCanonicalizer.Normalize(scan.Root.FullPath);
                if (string.Equals(rootPath, target, StringComparison.OrdinalIgnoreCase)) return scan.Root;
                if (!target.StartsWith(rootPath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                var pending = new Stack<FileItemNode>();
                pending.Push(scan.Root);
                while (pending.Count > 0)
                {
                    var node = pending.Pop();
                    var path = PathCanonicalizer.Normalize(node.FullPath);
                    if (string.Equals(path, target, StringComparison.OrdinalIgnoreCase)) return node;
                    if (!target.StartsWith(path.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var child in node.Children)
                        if (child.IsDirectory) pending.Push(child);
                }
            }
            return null;
        }

        // ==========================================
        // 3. Tab 2: Search Studio
        // ==========================================
        public Task<List<SearchResultDto>> SearchAsync(string targetFolder, SearchQueryDto query, IProgress<SearchProgressDto>? progress, CancellationToken ct)
            => SearchWithBatchesAsync(targetFolder, query, progress, null, ct);

        private async Task<List<SearchResultDto>> SearchWithBatchesAsync(string targetFolder, SearchQueryDto query,
            IProgress<SearchProgressDto>? progress, IProgress<IReadOnlyList<SearchResultItem>>? batches, CancellationToken ct)
        {
            IProgress<SearchProgressReport>? coreProgress = progress == null ? null
                : new InlineProgress<SearchProgressReport>(report => progress.Report(SearchDtoMapper.ToDto(report)));
            var results = await _searchEngine.SearchDirectFolderAsync(targetFolder, SearchDtoMapper.ToCore(query), batches, coreProgress, ct);
            return results.Select(SearchDtoMapper.ToDto).ToList();
        }

        public Task<List<SearchResultDto>> SearchInMemoryAsync(string targetPath, SearchQueryDto query, IProgress<SearchProgressDto>? progress, CancellationToken ct)
            => SearchInMemoryWithBatchesAsync(targetPath, query, progress, null, ct);

        private async Task<List<SearchResultDto>> SearchInMemoryWithBatchesAsync(string targetPath, SearchQueryDto query,
            IProgress<SearchProgressDto>? progress, IProgress<IReadOnlyList<SearchResultItem>>? batches, CancellationToken ct)
        {
            IProgress<SearchProgressReport>? coreProgress = progress == null ? null
                : new InlineProgress<SearchProgressReport>(report => progress.Report(SearchDtoMapper.ToDto(report)));
            var coreQuery = SearchDtoMapper.ToCore(query);
            var liveRoot = FindLiveScanNode(targetPath);
            List<SearchResultItem> results;
            if (liveRoot != null)
            {
                results = await _searchEngine.SearchInMemoryAsync(new[] { liveRoot }, coreQuery, coreProgress, ct, batches);
            }
            else
            {
                // This also imports a newer shared JSON cache when configured.
                var cachedRoot = await _storageHistory.LoadTreeCacheBranchAsync(targetPath, targetPath);
                if (cachedRoot == null) return new List<SearchResultDto>();
                results = await _treeCache.HasRootAsync(targetPath)
                    ? await _searchEngine.SearchCachedEntriesAsync(
                        _treeCache.EnumerateSearchEntries(targetPath, ct), coreQuery, coreProgress, ct, batches)
                    : await _searchEngine.SearchInMemoryAsync(new[] { cachedRoot }, coreQuery, coreProgress, ct, batches);
            }
            return results.Select(SearchDtoMapper.ToDto).ToList();
        }

        // ==========================================
        // 4. Tab 3: Live ACL & Effective Access
        // ==========================================
        public Task<List<AclEntryDto>> GetFolderAclAsync(string folderPath, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var (entries, _, _) = _aclService.GetSimAclForFolder(folderPath);
                return entries.Select(AclDtoMapper.ToDto).ToList();
            }, ct);
        }

        public async Task<MembershipResolutionDto> ResolveEffectiveMembershipsAsync(string targetAccount, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var (groups, mode, status) = await _effectiveAccessService.ResolveMembershipsAsync(targetAccount);
            return new MembershipResolutionDto
            {
                Groups = groups.Select(IdentityDtoMapper.ToDto).ToList(),
                ResolutionMode = (int)mode,
                StatusText = status
            };
        }

        public async Task<EffectiveAccessReportDto> RunEffectiveAccessAuditAsync(
            string rootPath, string targetAccount, int maxDepth, MembershipResolutionDto? resolution,
            IProgress<string>? progress, CancellationToken ct)
        {
            var accProgress = progress != null
                ? new Progress<(int scanned, int found)>(p => progress.Report($"走査中: {p.scanned} フォルダ (該当: {p.found})"))
                : null;
            var groups = resolution?.Groups.Select(IdentityDtoMapper.ToCore).ToList();
            var report = await _effectiveAccessService.ScanEffectiveAccessAsync(rootPath, targetAccount, groups, maxDepth, accProgress, ct);
            if (resolution != null)
            {
                report.ResolutionMode = (EffectiveAccessResolutionMode)resolution.ResolutionMode;
                report.ResolutionStatusText = resolution.StatusText;
            }
            return IdentityDtoMapper.ToDto(report);
        }

        public async Task<List<AdPrincipalDto>> GetAdPrincipalsAsync(string filter, CancellationToken ct)
        {
            var principals = await _adService.SearchPrincipalsAsync(filter);
            return principals.Select(IdentityDtoMapper.ToDto).ToList();
        }

        public Task<DirectoryStatusDto> GetDirectoryStatusAsync() => Task.FromResult(new DirectoryStatusDto
        {
            IsDomainJoined = _adService.IsDomainJoined,
            CurrentDomainName = _adService.CurrentDomainName
        });

        public async Task<List<AdOuNodeDto>> GetAdOuHierarchyAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return (await _adService.GetOuHierarchyAsync()).Select(IdentityDtoMapper.ToDto).ToList();
        }

        public async Task<List<AdPrincipalDto>> GetAdPrincipalsInOuAsync(
            string distinguishedName, string keyword, bool includeUsers, bool includeGroups, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return (await _adService.GetPrincipalsInOuAsync(distinguishedName, keyword, includeUsers, includeGroups))
                .Select(IdentityDtoMapper.ToDto).ToList();
        }

        public Task ExportEffectiveAccessReportAsync(string outputPath, EffectiveAccessReportDto report, CancellationToken ct)
        {
            return Task.Run(() => new ExcelReportService().ExportEffectiveAccessReport(
                outputPath, IdentityDtoMapper.ToCore(report)), ct);
        }

        // ==========================================
        // 5. Tab 4: Simulation Studio
        // ==========================================
        public async Task<string> GenerateMigrationPackageAsync(List<MigrationNodeDto> rootNodes, MigrationPackageOptionsDto options, CancellationToken ct)
        {
            return await _migrationPackageService.GeneratePackageAsync(
                rootNodes.Select(node => MigrationDtoMapper.ToCore(node)).ToList(),
                MigrationDtoMapper.ToCore(options), null, ct);
        }

        public Task<List<MigrationWaveDto>> PlanMigrationWavesAsync(
            List<MigrationNodeDto> rootNodes, MigrationPackageOptionsDto options, CancellationToken ct)
        {
            return Task.Run(() => _migrationPackageService.PlanWaves(
                rootNodes.Select(node => MigrationDtoMapper.ToCore(node)).ToList(), MigrationDtoMapper.ToCore(options))
                .Select(wave => new MigrationWaveDto
                {
                    WaveNumber = wave.WaveNumber,
                    WaveName = wave.WaveName,
                    TotalSizeBytes = wave.TotalSizeBytes,
                    TotalFileCount = wave.TotalFileCount,
                    EstimatedFullCopyTime = wave.EstimatedFullCopyTime,
                    EstimatedCutoverTime = wave.EstimatedCutoverTime,
                    MappedSourcePaths = wave.MappedSourcePaths
                }).ToList(), ct);
        }

        public Task<StorageNodeDto> LoadMigrationSourceFolderAsync(string path, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                var folder = new DirectoryInfo(path);
                if (!folder.Exists) throw new DirectoryNotFoundException(path);
                var root = new StorageNodeDto
                {
                    FullPath = folder.FullName, Name = folder.Name,
                    IsDirectory = true, LastModified = folder.LastWriteTime
                };
                try
                {
                    foreach (var child in folder.EnumerateDirectories())
                    {
                        ct.ThrowIfCancellationRequested();
                        var node = new StorageNodeDto
                        {
                            FullPath = child.FullName, Name = child.Name,
                            IsDirectory = true, LastModified = child.LastWriteTime
                        };
                        try
                        {
                            if (child.EnumerateDirectories().Any())
                                node.Children.Add(new StorageNodeDto { Name = "__DUMMY__" });
                        }
                        catch
                        {
                            node.Children.Add(new StorageNodeDto { Name = "__DUMMY__" });
                        }
                        root.Children.Add(node);
                    }
                }
                catch (UnauthorizedAccessException) { /* Show the accessible parent. */ }
                return root;
            }, ct);
        }

        public Task<MigrationNodeDto> CreateMigrationNodeFromStorageAsync(
            StorageNodeDto source, int level, int maxDepth, CancellationToken ct)
        {
            return Task.Run(() => MigrationDtoMapper.ToDto(
                _simProjectService.CreateSimNodeFromSourceWithAcl(
                    StorageDtoMapper.ToCore(source), level, maxDepth, ct)), ct);
        }

        public Task<MigrationNodeDto> ConvertStorageToMigrationAsync(StorageNodeDto source, CancellationToken ct)
        {
            return Task.Run(() => MigrationDtoMapper.ToDto(
                _simProjectService.ConvertToSimNode(StorageDtoMapper.ToCore(source))), ct);
        }

        public Task<List<MigrationDiffDto>> GenerateMigrationDiffAsync(
            StorageNodeDto? sourceRoot, List<MigrationNodeDto> targetRoots, CancellationToken ct)
        {
            return Task.Run(() => _simProjectService.GenerateDiffReview(
                sourceRoot == null ? null : StorageDtoMapper.ToCore(sourceRoot),
                targetRoots.Select(node => MigrationDtoMapper.ToCore(node))).Select(MigrationDtoMapper.ToDto).ToList(), ct);
        }

        public Task<SkeletonDeployPlanDto> BuildSkeletonDeployPlanAsync(
            StorageNodeDto? sourceRoot, List<MigrationNodeDto> targetRoots, string targetRoot, CancellationToken ct)
        {
            return Task.Run(() => MigrationDtoMapper.ToDto(_simProjectService.BuildDeployPlan(
                targetRoots.Select(node => MigrationDtoMapper.ToCore(node)), targetRoot,
                sourceRoot == null ? null : StorageDtoMapper.ToCore(sourceRoot))), ct);
        }

        public Task SaveSimulationProjectAsync(SimulationProjectDto dto, string filePath, CancellationToken ct)
        {
            var project = new FolderMorphProject
            {
                Version = dto.Version, AppName = dto.AppName,
                ProjectName = dto.ProjectName, CreatedAt = dto.CreatedAt,
                LastModifiedAt = dto.LastModifiedAt, SourceRootPath = dto.SourceRootPath,
                TargetRootPath = dto.TargetRootPath, Notes = dto.Notes,
                RootFolders = dto.RootFolders.Select(node => MigrationDtoMapper.ToCore(node)).ToList()
            };
            return _simProjectService.SaveProjectAsync(project, filePath);
        }

        public async Task<SimulationProjectDto?> LoadSimulationProjectAsync(string filePath, CancellationToken ct)
        {
            var project = await _simProjectService.LoadProjectAsync(filePath);
            if (project == null) return null;
            return new SimulationProjectDto
            {
                Version = project.Version, AppName = project.AppName,
                ProjectName = project.ProjectName, CreatedAt = project.CreatedAt,
                LastModifiedAt = project.LastModifiedAt, SourceRootPath = project.SourceRootPath,
                TargetRootPath = project.TargetRootPath, Notes = project.Notes,
                RootFolders = project.RootFolders.Select(MigrationDtoMapper.ToDto).ToList()
            };
        }

        public Task ExportSimulationMatrixAsync(string outputPath, List<MigrationNodeDto> rootNodes, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var roots = rootNodes.Select(node => MigrationDtoMapper.ToCore(node)).ToList();
                if (outputPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    new ExcelReportService().ExportSimulationDesignMatrix(outputPath, roots);
                else if (outputPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                    File.WriteAllText(outputPath, _simProjectService.ExportDesignMatrixCsv(roots), System.Text.Encoding.UTF8);
                else
                    throw new ArgumentException("Migration matrix must be .xlsx or .csv", nameof(outputPath));
            }, ct);
        }

        public async Task<SkeletonDeployResultDto> DeploySkeletonTreeAsync(string targetRoot, MigrationNodeDto rootNode, CancellationToken ct)
        {
            var result = await _simProjectService.DeploySkeletonAsync(new[] { MigrationDtoMapper.ToCore(rootNode) }, targetRoot, null, ct);
            return MigrationDtoMapper.ToDto(result, targetRoot);
        }

        public async Task<SkeletonDeployResultDto> DeploySkeletonPlanAsync(SkeletonDeployPlanDto plan, CancellationToken ct)
        {
            var result = await _simProjectService.DeploySkeletonAsync(MigrationDtoMapper.ToCore(plan), null, ct);
            return MigrationDtoMapper.ToDto(result, plan.DestinationRoot);
        }

        public Task<AclVerificationDto> VerifyFolderDaclAsync(string targetPath, MigrationNodeDto planNode, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var node = MigrationDtoMapper.ToCore(planNode);
                return MigrationDtoMapper.ToDto(_aclService.VerifyFolderDacl(targetPath, node.InheritAcl, node.AclEntries));
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

            result.BrokenLinks = shortcuts.Select(LinkFixDtoMapper.ToDto).ToList();
            result.OfficeLinks = officeLinks.Select(LinkFixDtoMapper.ToDto).ToList();
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
                int repaired = await _linkFixService.ExecuteFixAsync(request.TargetShortcuts.Select(LinkFixDtoMapper.ToCore).ToList(), fixProgress, ct);
                result.RepairedCount += repaired;
            }

            if (request.TargetOfficeLinks.Count > 0)
            {
                var offProgress = progress != null 
                    ? new Progress<(string File, bool Success, string Msg)>(p => progress.Report(p.Msg))
                    : null;
                int repaired = await _officeLinkFixService.ExecuteOfficeFixAsync(request.TargetOfficeLinks.Select(LinkFixDtoMapper.ToCore).ToList(), offProgress, ct);
                result.RepairedCount += repaired;
            }

            return result;
        }

        // ==========================================
        // 7. Tab 6: Audit & Hygiene
        // ==========================================
        public Task<AuditReportDto> RunAuditScanAsync(AuditScanRequestDto request, IProgress<string>? progress, CancellationToken ct) =>
            RunAuditScanCoreAsync(request, progress, null, ct);

        private async Task<AuditReportDto> RunAuditScanCoreAsync(AuditScanRequestDto request, IProgress<string>? progress,
            IProgress<IReadOnlyList<AuditItem>>? candidateProgress, CancellationToken ct)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var auditService = new AuditReportService();
            var options = new AuditOptions
            {
                TargetDirectory = request.TargetPath,
                CheckDuplicates = request.CheckDuplicates,
                CheckDormant = request.CheckDormant,
                CheckVersionFamilies = request.CheckVersionFamilies,
                CheckExtractedArchives = request.CheckExtractedArchives,
                CheckGraveyardTrees = request.CheckGraveyardTrees,
                CheckPathLimits = request.CheckPathLimits,
                DormantYearsThreshold = request.DormantYearsThreshold,
                MinFileSizeBytes = request.MinFileSizeBytes,
                BandwidthLimit = (AuditBandwidthLimit)request.BandwidthLimit,
                ExcludeFolderPatterns = request.IgnoredPaths ?? new List<string>()
            };
            var auditProgress = progress != null 
                ? new InlineProgress<AuditProgress>(p => progress.Report(p.CurrentStatus))
                : null;

            var (summary, items) = await auditService.RunAuditAsync(options, auditProgress, ct, candidateProgress);
            sw.Stop();

            _ = Task.Run(async () =>
            {
                try { await StorageHistoryService.Instance.UpdateTreeCacheSha256Async(request.TargetPath, items); }
                catch { /* Cache enrichment must not invalidate the completed audit. */ }
            });

            foreach (var item in items) item.AuditId = Guid.NewGuid();
            var reportId = Guid.NewGuid();
            _auditReports[reportId] = new AuditSnapshot(DateTime.UtcNow, items);
            foreach (var old in _auditReports.Where(entry => entry.Value.CreatedUtc < DateTime.UtcNow.AddHours(-2)))
                _auditReports.TryRemove(old.Key, out _);
            while (_auditReports.Count > 8)
            {
                var oldest = _auditReports.OrderBy(entry => entry.Value.CreatedUtc).First();
                _auditReports.TryRemove(oldest.Key, out _);
            }

            return new AuditReportDto
            {
                ReportId = reportId,
                TargetPath = request.TargetPath,
                Items = items.Select(AuditDtoMapper.ToDto).ToList(),
                Summary = AuditDtoMapper.ToDto(summary),
                Elapsed = sw.Elapsed
            };
        }

        public Task<List<Guid>> SortAuditIdsAsync(Guid reportId, List<Guid> visibleIds,
            string sortProperty, bool descending, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                if (!_auditReports.TryGetValue(reportId, out var snapshot))
                    throw new InvalidOperationException("Audit report expired. Run the audit again.");
                var selected = visibleIds.ToHashSet();
                var items = snapshot.Items.Where(item => selected.Contains(item.AuditId));
                return AuditReportService.SortAuditItems(items, sortProperty, descending)
                    .Select(item => item.AuditId).ToList();
            }, ct);
        }

        public Task AddAuditIgnoreAsync(AuditItemDto item, CancellationToken ct) =>
            Task.Run(() => AuditIgnoreService.Instance.AddIgnore(AuditDtoMapper.ToCore(item)), ct);

        public Task<List<AuditIgnoreItemDto>> GetAuditIgnoresAsync() => Task.FromResult(
            AuditIgnoreService.Instance.GetAllItems().Select(item => new AuditIgnoreItemDto
            {
                FullPath = item.FullPath,
                FileSizeBytes = item.FileSizeBytes,
                LastWriteTimeUtcTicks = item.LastWriteTimeUtcTicks,
                IgnoredAt = item.IgnoredAt
            }).ToList());

        public Task ClearAuditIgnoresAsync() => Task.Run(() => AuditIgnoreService.Instance.ClearAll());

        public Task<int> GetAuditIgnoreCountAsync() => Task.FromResult(AuditIgnoreService.Instance.GetIgnoredCount());

        public Task<AuditCleanupPreviewDto> PrepareAuditCleanupAsync(AuditCleanupPrepareRequestDto request, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (!_auditReports.TryGetValue(request.ReportId, out var snapshot))
                    throw new InvalidOperationException("監査結果がHostに残っていません。再スキャンしてください。");
                var selectedIds = request.SelectedAuditIds.ToHashSet();
                var validIds = snapshot.Items.Select(item => item.AuditId).ToHashSet();
                if (!selectedIds.IsSubsetOf(validIds))
                    throw new InvalidOperationException("選択項目が現在の監査結果と一致しません。再スキャンしてください。");
                var items = snapshot.Items.Select(item =>
                {
                    var copy = AuditDtoMapper.ToCore(AuditDtoMapper.ToDto(item));
                    copy.IsChecked = selectedIds.Contains(item.AuditId);
                    return copy;
                }).ToList();
                var allPlans = AuditCleanupService.BuildPlan(items);
                var protectedPaths = allPlans.Where(plan => plan.IsOriginalCandidate)
                    .Select(plan => plan.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var safePlans = allPlans.Where(plan => !plan.IsOriginalCandidate).ToList();
                var preview = new AuditCleanupPreviewDto
                {
                    SafeFileCount = safePlans.Count,
                    SafeSizeBytes = safePlans.Sum(plan => plan.Size),
                    ProtectedOriginalPaths = protectedPaths
                };
                if (request.RetainForCommit && safePlans.Count > 0)
                {
                    foreach (var old in _preparedAuditCleanup.Where(entry => entry.Value.CreatedUtc < DateTime.UtcNow.AddHours(-2)))
                        _preparedAuditCleanup.TryRemove(old.Key, out _);
                    while (_preparedAuditCleanup.Count >= 32)
                    {
                        var oldest = _preparedAuditCleanup.OrderBy(entry => entry.Value.CreatedUtc).First();
                        _preparedAuditCleanup.TryRemove(oldest.Key, out _);
                    }
                    preview.PlanId = Guid.NewGuid();
                    _preparedAuditCleanup[preview.PlanId] = new PreparedAuditCleanup(DateTime.UtcNow, safePlans);
                }
                return preview;
            }, ct);
        }

        public Task<AuditCleanupResultDto> CommitAuditCleanupAsync(Guid planId, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();
                if (planId == Guid.Empty || !_preparedAuditCleanup.TryRemove(planId, out var prepared))
                    throw new InvalidOperationException("削除計画が見つからないか、既に実行済みです。再確認してください。");
                if (prepared.CreatedUtc < DateTime.UtcNow.AddHours(-2))
                    throw new InvalidOperationException("削除計画の有効期限が切れました。再確認してください。");
                return AuditDtoMapper.ToDto(AuditCleanupService.ExecutePlan(prepared.Plans) ?? new AuditCleanupResult());
            }, ct);
        }

        // ==========================================
        // 8. Tab 7: Media Optimizer
        // ==========================================
        public async Task<MediaScanResultDto> ScanMediaAsync(MediaOptimizeOptionsDto options, IProgress<string>? progress, CancellationToken ct)
        {
            var (images, videos) = await _mediaOptimizerService.ScanMediaAsync(MediaDtoMapper.ToCore(options), progress, ct);
            return new MediaScanResultDto
            {
                Images = images.Select(MediaDtoMapper.ToDto).ToList(),
                Videos = videos.Select(MediaDtoMapper.ToDto).ToList()
            };
        }

        public async Task<MediaOptimizeResultDto> OptimizeImagesAsync(List<MediaItemDto> items, MediaOptimizeOptionsDto options, IProgress<string>? progress, CancellationToken ct)
        {
            var mediaProgress = new Progress<(string File, bool Success, string Msg)>(p =>
            {
                progress?.Report(p.Msg);
            });
            var coreItems = items.Select(MediaDtoMapper.ToCore).ToList();
            var summary = await _mediaOptimizerService.OptimizeImagesAsync(coreItems, MediaDtoMapper.ToCore(options), mediaProgress, ct);
            return new MediaOptimizeResultDto
            {
                Summary = MediaDtoMapper.ToDto(summary),
                UpdatedItems = coreItems.Select(MediaDtoMapper.ToDto).ToList()
            };
        }

        public Task GenerateVideoCompressBatchAsync(string outputPath, List<MediaItemDto> videos, CancellationToken ct)
        {
            return Task.Run(() =>
                _mediaOptimizerService.GenerateVideoCompressBatch(outputPath, videos.Select(MediaDtoMapper.ToCore)), ct);
        }

        public Task GenerateGpoLogonScriptAsync(string outputPath, string oldPrefix, string newPrefix, CancellationToken ct)
        {
            return Task.Run(() => _linkFixService.GenerateGpoLogonScript(outputPath, oldPrefix, newPrefix), ct);
        }

        public Task ExportAuditCsvAsync(string outputPath, List<AuditItemDto> items, CancellationToken ct)
        {
            return Task.Run(() => new AuditReportService().ExportAuditCsv(outputPath, items.Select(AuditDtoMapper.ToCore)), ct);
        }

        public Task ExportExcelReportAsync(ReportExportDto request, CancellationToken ct)
        {
            return Task.Run(() => new ExcelReportService().GenerateComprehensiveReport(
                request.OutputPath,
                request.ScannedRoot,
                request.AuditSummary == null ? null : AuditDtoMapper.ToCore(request.AuditSummary),
                request.AuditItems.Select(AuditDtoMapper.ToCore).ToList(),
                request.MediaSummary == null ? null : MediaDtoMapper.ToCore(request.MediaSummary),
                request.MediaItems.Select(MediaDtoMapper.ToCore).ToList()), ct);
        }
    }
}
