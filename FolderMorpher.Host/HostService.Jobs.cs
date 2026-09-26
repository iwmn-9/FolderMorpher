using System.Collections.Concurrent;
using FolderMorpher.Contracts;
using FolderMorpher.Models;

namespace FolderMorpher.Host;

public partial class HostService
{
    private readonly ConcurrentDictionary<Guid, HostJob> _jobs = new();
    private readonly object _jobLifecycleGate = new();

    private sealed class HostJob
    {
        public readonly object Gate = new();
        public readonly CancellationTokenSource Cancellation = new();
        public readonly Guid Id = Guid.NewGuid();
        public readonly HostJobKind Kind;
        public readonly DateTime CreatedUtc = DateTime.UtcNow;
        public HostJobState State = HostJobState.Running;
        public string ProgressText = string.Empty;
        public int HitCount;
        public long TotalHitBytes;
        public int AccessDeniedFolders;
        public int UnreadFiles;
        public string? Error;
        public List<SearchResultDto>? SearchResults;
        public readonly List<SearchResultDto> RecentSearchResults = new();
        public long SearchFirstSequence;
        public long SearchNextSequence;
        public AuditReportDto? AuditReport;
        public SkeletonDeployResultDto? SkeletonResult;
        public string? PackageDirectory;
        public EffectiveAccessReportDto? EffectiveAccessReport;

        public HostJob(HostJobKind kind) => Kind = kind;

        public void AppendSearchBatch(IReadOnlyList<SearchResultItem> batch)
        {
            var mapped = batch.Select(SearchDtoMapper.ToDto).ToList();
            lock (Gate)
            {
                foreach (var item in mapped)
                {
                    RecentSearchResults.Add(item);
                    SearchNextSequence++;
                }
                if (RecentSearchResults.Count > 2048)
                {
                    int discard = RecentSearchResults.Count - 2048;
                    RecentSearchResults.RemoveRange(0, discard);
                    SearchFirstSequence += discard;
                }
            }
        }

        public SearchJobResultsDto GetSearchResults(long afterSequence, int maxResults)
        {
            lock (Gate)
            {
                long start = Math.Clamp(afterSequence, SearchFirstSequence, SearchNextSequence);
                int offset = checked((int)(start - SearchFirstSequence));
                int count = Math.Min(Math.Clamp(maxResults, 1, 256), RecentSearchResults.Count - offset);
                return new SearchJobResultsDto
                {
                    HadGap = afterSequence < SearchFirstSequence,
                    NextSequence = start + count,
                    Results = RecentSearchResults.GetRange(offset, count)
                };
            }
        }

        public HostJobStatusDto Snapshot()
        {
            lock (Gate)
            {
                return new HostJobStatusDto
                {
                    JobId = Id,
                    Kind = Kind,
                    State = State,
                    ProgressText = ProgressText,
                    HitCount = HitCount,
                    TotalHitBytes = TotalHitBytes,
                    AccessDeniedFolders = AccessDeniedFolders,
                    UnreadFiles = UnreadFiles,
                    Error = Error,
                    SearchResults = State == HostJobState.Completed ? SearchResults : null,
                    AuditReport = State == HostJobState.Completed ? AuditReport : null,
                    SkeletonResult = State == HostJobState.Completed ? SkeletonResult : null,
                    PackageDirectory = State == HostJobState.Completed ? PackageDirectory : null,
                    EffectiveAccessReport = State == HostJobState.Completed ? EffectiveAccessReport : null
                };
            }
        }
    }

    public Task<Guid> StartJobAsync(HostJobRequestDto request)
    {
        ArgumentNullException.ThrowIfNull(request);
        switch (request.Kind)
        {
            case HostJobKind.Search or HostJobKind.CachedSearch when string.IsNullOrWhiteSpace(request.TargetPath) || request.SearchQuery == null:
            case HostJobKind.AuditScan when request.AuditRequest == null || string.IsNullOrWhiteSpace(request.AuditRequest.TargetPath):
            case HostJobKind.DeploySkeleton when request.SkeletonPlan == null:
            case HostJobKind.MigrationPackage when request.MigrationNodes == null || request.MigrationOptions == null:
            case HostJobKind.EffectiveAccessAudit when string.IsNullOrWhiteSpace(request.TargetPath) || string.IsNullOrWhiteSpace(request.TargetAccount):
                throw new ArgumentException("Job request is incomplete.", nameof(request));
            case HostJobKind.Search:
            case HostJobKind.CachedSearch:
            case HostJobKind.AuditScan:
            case HostJobKind.DeploySkeleton:
            case HostJobKind.MigrationPackage:
            case HostJobKind.EffectiveAccessAudit:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request.Kind));
        }

        HostJob job;
        lock (_jobLifecycleGate)
        {
            if (_shutdownRequested != 0)
                throw new InvalidOperationException("Host is shutting down.");
            foreach (var old in _jobs.Where(entry => entry.Value.State != HostJobState.Running &&
                        entry.Value.CreatedUtc < DateTime.UtcNow.AddHours(-2)))
            {
                if (_jobs.TryRemove(old.Key, out var removed)) removed.Cancellation.Dispose();
            }
            if (_jobs.Values.Count(entry => entry.State == HostJobState.Running) >= 8 || _jobs.Count >= 64)
                throw new InvalidOperationException("Host job limit reached. Wait for existing jobs to finish.");
            job = new HostJob(request.Kind);
            if (!_jobs.TryAdd(job.Id, job)) throw new InvalidOperationException("Could not register Host job.");
        }
        _ = Task.Run(() => ExecuteJobAsync(job, request));
        return Task.FromResult(job.Id);
    }

    private async Task ExecuteJobAsync(HostJob job, HostJobRequestDto request)
    {
        try
        {
            switch (job.Kind)
            {
                case HostJobKind.Search:
                case HostJobKind.CachedSearch:
                    var searchProgress = new InlineProgress<SearchProgressDto>(report =>
                    {
                        lock (job.Gate)
                        {
                            job.ProgressText = report.CurrentPath;
                            job.HitCount = report.HitCount;
                            job.TotalHitBytes = report.TotalHitBytes;
                            job.AccessDeniedFolders = report.AccessDeniedFolders;
                            job.UnreadFiles = report.UnreadFiles;
                        }
                    });
                    var searchBatches = new InlineProgress<IReadOnlyList<SearchResultItem>>(job.AppendSearchBatch);
                    var results = job.Kind == HostJobKind.CachedSearch
                        ? await SearchInMemoryWithBatchesAsync(request.TargetPath, request.SearchQuery!, searchProgress, searchBatches, job.Cancellation.Token)
                        : await SearchWithBatchesAsync(request.TargetPath, request.SearchQuery!, searchProgress, searchBatches, job.Cancellation.Token);
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    lock (job.Gate)
                    {
                        job.SearchResults = results;
                        job.HitCount = results.Count;
                        job.TotalHitBytes = results.Sum(item => item.SizeBytes);
                    }
                    break;
                case HostJobKind.AuditScan:
                    var auditProgress = new Progress<string>(message =>
                    {
                        lock (job.Gate) job.ProgressText = message;
                    });
                    var report = await RunAuditScanAsync(request.AuditRequest!, auditProgress, job.Cancellation.Token);
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    lock (job.Gate) job.AuditReport = report;
                    break;
                case HostJobKind.DeploySkeleton:
                    var deployment = await DeploySkeletonPlanAsync(request.SkeletonPlan!, job.Cancellation.Token);
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    lock (job.Gate) job.SkeletonResult = deployment;
                    break;
                case HostJobKind.MigrationPackage:
                    var packageDirectory = await GenerateMigrationPackageAsync(
                        request.MigrationNodes!, request.MigrationOptions!, job.Cancellation.Token);
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    lock (job.Gate) job.PackageDirectory = packageDirectory;
                    break;
                case HostJobKind.EffectiveAccessAudit:
                    var effectiveProgress = new Progress<string>(message =>
                    {
                        lock (job.Gate) job.ProgressText = message;
                    });
                    var effectiveReport = await RunEffectiveAccessAuditAsync(
                        request.TargetPath, request.TargetAccount, request.MaxDepth,
                        request.MembershipResolution, effectiveProgress, job.Cancellation.Token);
                    job.Cancellation.Token.ThrowIfCancellationRequested();
                    lock (job.Gate) job.EffectiveAccessReport = effectiveReport;
                    break;
            }
            lock (job.Gate) job.State = HostJobState.Completed;
        }
        catch (OperationCanceledException)
        {
            lock (job.Gate) job.State = HostJobState.Canceled;
        }
        catch (Exception ex)
        {
            lock (job.Gate)
            {
                job.Error = ex.Message;
                job.State = HostJobState.Failed;
            }
        }
    }

    public Task<HostJobStatusDto> GetJobStatusAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) throw new KeyNotFoundException("Host job not found.");
        return Task.FromResult(job.Snapshot());
    }

    public Task<SearchJobResultsDto> GetSearchJobResultsAsync(Guid jobId, long afterSequence, int maxResults)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) throw new KeyNotFoundException("Host job not found.");
        if (job.Kind is not (HostJobKind.Search or HostJobKind.CachedSearch))
            throw new InvalidOperationException("Job does not contain search results.");
        return Task.FromResult(job.GetSearchResults(afterSequence, maxResults));
    }

    public Task<bool> CancelJobAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return Task.FromResult(false);
        lock (job.Gate)
        {
            if (job.State != HostJobState.Running) return Task.FromResult(false);
            job.Cancellation.Cancel();
            return Task.FromResult(true);
        }
    }

    public Task<bool> ReleaseJobAsync(Guid jobId)
    {
        if (!_jobs.TryGetValue(jobId, out var job)) return Task.FromResult(false);
        lock (job.Gate)
        {
            if (job.State == HostJobState.Running || !_jobs.TryRemove(jobId, out var removed))
                return Task.FromResult(false);
            removed.Cancellation.Dispose();
            return Task.FromResult(true);
        }
    }

    private sealed class InlineProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }
}
