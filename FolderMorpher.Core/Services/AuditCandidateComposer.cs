using FolderMorpher.Models;

namespace FolderMorpher.Services;

/// <summary>One physical path is one cleanup candidate. Reason scores are additive ranking points.</summary>
public static class AuditCandidateComposer
{
    public static List<AuditItem> CombineByPath(IEnumerable<AuditItem> reasons) => reasons
        .GroupBy(item => item.FullPath, StringComparer.OrdinalIgnoreCase)
        .Select(group => Combine(group))
        .ToList();

    private static AuditItem Combine(IGrouping<string, AuditItem> group)
    {
        var ordered = group.OrderBy(item => item.IssueType == AuditIssueType.Duplicate ? 0 : 1)
            .ThenByDescending(item => item.WasteScore)
            .ToList();
        var primary = ordered[0];
        var duplicate = ordered.FirstOrDefault(item => item.IssueType == AuditIssueType.Duplicate);
        return new AuditItem
        {
            FullPath = primary.FullPath,
            FileName = primary.FileName,
            DirectoryPath = primary.DirectoryPath,
            Size = primary.Size,
            LastWriteTime = primary.LastWriteTime,
            LastAccessTime = primary.LastAccessTime,
            IssueType = primary.IssueType,
            IssueTypes = ordered.Select(item => item.IssueType).Distinct().ToList(),
            Detail = string.Join(" / ", ordered.Select(item => item.Detail).Where(text => !string.IsNullOrWhiteSpace(text)).Distinct()),
            Sha256Hash = duplicate?.Sha256Hash,
            DuplicateGroupId = duplicate?.DuplicateGroupId ?? string.Empty,
            DuplicateGroupIndex = duplicate?.DuplicateGroupIndex ?? 0,
            DuplicateGroupColorIndex = duplicate?.DuplicateGroupColorIndex ?? 0,
            IsOriginalCandidate = ordered.Any(item => item.IsOriginalCandidate),
            IsIgnored = ordered.Any(item => item.IsIgnored),
            WasteScore = ordered.Sum(item => item.WasteScore),
            ScoreBreakdown = ordered.SelectMany(item => item.ScoreBreakdown).ToList(),
            RelatedActivePath = ordered.Select(item => item.RelatedActivePath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path))
        };
    }
}
