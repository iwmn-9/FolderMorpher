using System.Collections.Concurrent;
using System.Text;
using AstraSize.Models;
using AstraSize.Services;
using FolderMorpher.Contracts;

namespace FolderMorpher.Host;

public partial class HostService
{
    private sealed record PreparedFolderPlan(DateTime CreatedUtc, FolderCreatePlanDto Plan);
    private readonly ConcurrentDictionary<Guid, PreparedFolderPlan> _folderCreatePlans = new();

    public Task<FolderCreatePlanDto> PrepareFolderCreationAsync(string parentPath, string folderName, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(folderName) || folderName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                folderName is "." or ".." || folderName.EndsWith(' ') || folderName.EndsWith('.'))
                throw new ArgumentException("Invalid folder name.", nameof(folderName));
            var parent = Path.GetFullPath(parentPath);
            if (!Directory.Exists(parent)) throw new DirectoryNotFoundException(parent);
            var fullPath = Path.GetFullPath(Path.Combine(parent, folderName));
            if (!string.Equals(Path.GetDirectoryName(fullPath)?.TrimEnd('\\', '/'), parent.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Folder must be a direct child of the selected parent.", nameof(folderName));
            if (Directory.Exists(fullPath) || File.Exists(fullPath)) throw new IOException("A folder or file with this name already exists.");
            var plan = new FolderCreatePlanDto
            {
                PlanId = Guid.NewGuid(), ParentPath = parent,
                FolderName = folderName, FullPath = fullPath
            };
            foreach (var stale in _folderCreatePlans.Where(entry =>
                DateTime.UtcNow - entry.Value.CreatedUtc > TimeSpan.FromMinutes(10)))
                _folderCreatePlans.TryRemove(stale.Key, out _);
            if (_folderCreatePlans.Count >= 64)
                throw new InvalidOperationException("Too many pending folder creation plans.");
            _folderCreatePlans[plan.PlanId] = new PreparedFolderPlan(DateTime.UtcNow, plan);
            return plan;
        }, ct);
    }

    public Task<string> CommitFolderCreationAsync(Guid planId, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            if (!_folderCreatePlans.TryRemove(planId, out var prepared))
                throw new InvalidOperationException("Folder creation plan was not found or was already used.");
            if (DateTime.UtcNow - prepared.CreatedUtc > TimeSpan.FromMinutes(10))
                throw new InvalidOperationException("Folder creation plan expired. Please check again.");
            var plan = prepared.Plan;
            if (!Directory.Exists(plan.ParentPath)) throw new DirectoryNotFoundException(plan.ParentPath);
            if (Directory.Exists(plan.FullPath) || File.Exists(plan.FullPath))
                throw new IOException("The target changed after preview. Please check again.");
            Directory.CreateDirectory(plan.FullPath);
            if (!Directory.Exists(plan.FullPath)) throw new IOException("Folder creation could not be verified.");
            return plan.FullPath;
        }, ct);
    }

    public Task<StorageNodeDto> LoadAclFolderTreeAsync(string rootPath, int maxDepth, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var root = new DirectoryInfo(rootPath);
            if (!root.Exists) throw new DirectoryNotFoundException(rootPath);
            StorageNodeDto Build(DirectoryInfo folder, int depth)
            {
                ct.ThrowIfCancellationRequested();
                var dto = new StorageNodeDto
                {
                    Name = string.IsNullOrEmpty(folder.Name) ? folder.FullName : folder.Name,
                    FullPath = folder.FullName, IsDirectory = true,
                    LastModified = folder.LastWriteTime
                };
                if (depth >= Math.Clamp(maxDepth, 0, 8)) return dto;
                try
                {
                    foreach (var child in folder.EnumerateDirectories())
                    {
                        ct.ThrowIfCancellationRequested();
                        if ((child.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        dto.Children.Add(Build(child, depth + 1));
                    }
                }
                catch (UnauthorizedAccessException) { /* Keep accessible siblings. */ }
                catch (IOException) { /* A branch may disappear during enumeration. */ }
                return dto;
            }
            return Build(root, 0);
        }, ct);
    }

    private readonly ConcurrentDictionary<Guid, PreparedAclPlan> _aclPlans = new();
    private sealed record PreparedAclPlan(DateTime CreatedUtc, AclChangePlan Plan);

    public Task<AclFolderStateDto> GetAclFolderStateAsync(string folderPath, CancellationToken ct) =>
        Task.Run(() => ReadAclFolderState(folderPath), ct);

    private AclFolderStateDto ReadAclFolderState(string folderPath)
    {
        var (entries, inherits, owner) = _aclService.GetSimAclForFolder(folderPath);
        return new AclFolderStateDto
        {
            FolderPath = folderPath,
            Owner = owner,
            Sddl = _aclService.GetSddl(folderPath),
            InheritsAcl = inherits,
            Entries = entries.Select(AclDtoMapper.ToDto).ToList()
        };
    }

    public Task<AclChangePreviewDto> PrepareAclChangeAsync(AclChangeRequestDto request, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            if (string.IsNullOrWhiteSpace(request.FolderPath) || string.IsNullOrWhiteSpace(request.ExpectedOriginalSddl))
                throw new ArgumentException("ACL preparation requires a folder and its original SDDL.");
            var plan = _aclService.BuildChangePlan(
                request.FolderPath,
                request.OriginalEntries.Select(AclDtoMapper.ToCore).ToList(),
                request.CurrentEntries.Select(AclDtoMapper.ToCore).ToList(),
                request.InheritanceAfter,
                request.InheritanceBefore,
                request.ExpectedOriginalSddl);
            var planId = Guid.NewGuid();
            _aclPlans[planId] = new PreparedAclPlan(DateTime.UtcNow, plan);
            foreach (var old in _aclPlans.Where(entry => entry.Value.CreatedUtc < DateTime.UtcNow.AddMinutes(-30)))
                _aclPlans.TryRemove(old.Key, out _);
            while (_aclPlans.Count > 64)
            {
                var oldest = _aclPlans.OrderBy(entry => entry.Value.CreatedUtc).First();
                _aclPlans.TryRemove(oldest.Key, out _);
            }
            return new AclChangePreviewDto
            {
                PlanId = planId,
                InheritanceChanged = plan.InheritanceChanged,
                InheritanceAfter = plan.InheritanceAfter,
                InheritedAcesPromotedCount = plan.InheritedAcesPromotedCount,
                AddedCount = plan.Added.Count,
                RemovedCount = plan.Removed.Count,
                ModifiedCount = plan.Modified.Count,
                UntouchedCount = plan.Untouched.Count,
                HasConflict = !string.Equals(_aclService.GetSddl(request.FolderPath),
                    request.ExpectedOriginalSddl, StringComparison.OrdinalIgnoreCase),
                DiffItems = plan.DiffItems.Select(item => new AclDiffItemDto
                {
                    DiffType = (int)item.DiffType,
                    AccountName = item.AccountName,
                    DisplayName = item.DisplayName,
                    PrincipalType = (int)item.PrincipalType,
                    AccessType = (int)item.AccessType,
                    BeforeRights = item.BeforeRights,
                    AfterRights = item.AfterRights,
                    Details = item.Details,
                    AppliesTo = item.AppliesTo,
                    IsInherited = item.IsInherited
                }).ToList()
            };
        }, ct);
    }

    public async Task<AclCommitResultDto> CommitAclChangeAsync(Guid planId, bool forceIfConflict, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (planId == Guid.Empty || !_aclPlans.TryRemove(planId, out var prepared))
            throw new InvalidOperationException("ACL change plan was not found. Check the diff again.");
        if (prepared.CreatedUtc < DateTime.UtcNow.AddMinutes(-30))
            throw new InvalidOperationException("ACL change plan expired. Check the diff again.");
        var plan = prepared.Plan;
        int added, removed, modified;
        try
        {
            var applied = await _aclService.ApplyChangePlanWithRollbackAsync(plan, forceIfConflict);
            added = applied.addedCount;
            removed = applied.removedCount;
            modified = applied.modifiedCount;
        }
        catch (AclConflictException)
        {
            return new AclCommitResultDto { WasConflict = true, CurrentState = ReadAclFolderState(plan.FolderPath) };
        }
        var verification = _aclService.VerifyChangePlan(plan);
        return new AclCommitResultDto
        {
            AddedCount = added,
            RemovedCount = removed,
            ModifiedCount = modified,
            VerificationSucceeded = verification.IsSuccess,
            VerificationDiscrepancies = verification.Discrepancies,
            CurrentState = ReadAclFolderState(plan.FolderPath)
        };
    }

    public async Task<List<AclSnapshotInfoDto>> GetAclSnapshotsAsync(string folderPath, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshots = await _aclService.GetSnapshotsAsync(folderPath);
        return snapshots.Select(snapshot => new AclSnapshotInfoDto
        {
            Id = snapshot.Id,
            Timestamp = snapshot.Timestamp,
            Note = snapshot.Note
        }).ToList();
    }

    public async Task<AclFolderStateDto> RollbackAclSnapshotAsync(string folderPath, string snapshotId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var snapshots = await _aclService.GetSnapshotsAsync(folderPath);
        var snapshot = snapshots.FirstOrDefault(item => string.Equals(item.Id, snapshotId, StringComparison.Ordinal));
        if (snapshot == null) throw new InvalidOperationException("ACL snapshot was not found.");
        _aclService.RollbackToSnapshot(folderPath, snapshot);
        return ReadAclFolderState(folderPath);
    }

    public Task ExportAclMatrixAsync(string outputPath, string folderPath, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var root = _aclService.GetFolderAcl(folderPath, maxDepth: 2);
            File.WriteAllText(outputPath, _aclService.GenerateMatrixCsv(root), new UTF8Encoding(true));
        }, ct);
    }
}
