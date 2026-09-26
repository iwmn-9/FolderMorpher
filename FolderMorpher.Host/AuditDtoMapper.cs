using FolderMorpher.Contracts;
using FolderMorpher.Models;
using FolderMorpher.Services;

namespace FolderMorpher.Host;

internal static class AuditDtoMapper
{
    public static AuditItemDto ToDto(AuditItem item) => new()
    {
        AuditId = item.AuditId,
        IsChecked = item.IsChecked,
        FullPath = item.FullPath, FileName = item.FileName,
        DirectoryPath = item.DirectoryPath, Size = item.Size,
        LastWriteTime = item.LastWriteTime, LastAccessTime = item.LastAccessTime,
        IssueType = (int)item.IssueType, Detail = item.Detail,
        Sha256Hash = item.Sha256Hash, DuplicateGroupId = item.DuplicateGroupId,
        DuplicateGroupIndex = item.DuplicateGroupIndex,
        DuplicateGroupColorIndex = item.DuplicateGroupColorIndex,
        IsOriginalCandidate = item.IsOriginalCandidate, IsIgnored = item.IsIgnored,
        WasteScore = item.WasteScore, RelatedActivePath = item.RelatedActivePath,
        ScoreBreakdown = item.ScoreBreakdown.Select(factor => new AuditScoreFactorDto
        {
            NameJa = factor.NameJa, NameEn = factor.NameEn, Points = factor.Points
        }).ToList()
    };

    public static AuditItem ToCore(AuditItemDto dto)
    {
        var item = new AuditItem
        {
            AuditId = dto.AuditId,
            FullPath = dto.FullPath, FileName = dto.FileName,
            DirectoryPath = dto.DirectoryPath, Size = dto.Size,
            LastWriteTime = dto.LastWriteTime, LastAccessTime = dto.LastAccessTime,
            IssueType = (AuditIssueType)dto.IssueType, Detail = dto.Detail,
            Sha256Hash = dto.Sha256Hash, DuplicateGroupId = dto.DuplicateGroupId,
            DuplicateGroupIndex = dto.DuplicateGroupIndex,
            DuplicateGroupColorIndex = dto.DuplicateGroupColorIndex,
            IsOriginalCandidate = dto.IsOriginalCandidate, IsIgnored = dto.IsIgnored,
            WasteScore = dto.WasteScore, RelatedActivePath = dto.RelatedActivePath,
            ScoreBreakdown = dto.ScoreBreakdown.Select(factor => new ScoreFactorItem
            {
                NameJa = factor.NameJa, NameEn = factor.NameEn, Points = factor.Points
            }).ToList()
        };
        item.IsChecked = dto.IsChecked;
        return item;
    }

    public static AuditSummaryDto ToDto(AuditSummary summary) => new()
    {
        TotalFilesScanned = summary.TotalFilesScanned,
        Coverage = new ScanCoverageDto
        {
            TotalFoldersScanned = summary.Coverage.TotalFoldersScanned,
            AccessDeniedFolders = summary.Coverage.AccessDeniedFolders,
            TotalFilesFound = summary.Coverage.TotalFilesFound
        },
        DuplicateCount = summary.DuplicateCount,
        DuplicateWastedBytes = summary.DuplicateWastedBytes,
        DormantCount = summary.DormantCount,
        DormantBytes = summary.DormantBytes,
        VersionFamilyCount = summary.VersionFamilyCount,
        VersionFamilyBytes = summary.VersionFamilyBytes,
        ExtractedArchiveCount = summary.ExtractedArchiveCount,
        ExtractedArchiveBytes = summary.ExtractedArchiveBytes,
        GraveyardTreeCount = summary.GraveyardTreeCount,
        GraveyardTreeBytes = summary.GraveyardTreeBytes,
        PathTooLongCount = summary.PathTooLongCount,
        InvalidCharCount = summary.InvalidCharCount
    };

    public static AuditSummary ToCore(AuditSummaryDto dto) => new()
    {
        TotalFilesScanned = dto.TotalFilesScanned,
        Coverage = new ScanCoverage
        {
            TotalFoldersScanned = dto.Coverage.TotalFoldersScanned,
            AccessDeniedFolders = dto.Coverage.AccessDeniedFolders,
            TotalFilesFound = dto.Coverage.TotalFilesFound
        },
        DuplicateCount = dto.DuplicateCount,
        DuplicateWastedBytes = dto.DuplicateWastedBytes,
        DormantCount = dto.DormantCount,
        DormantBytes = dto.DormantBytes,
        VersionFamilyCount = dto.VersionFamilyCount,
        VersionFamilyBytes = dto.VersionFamilyBytes,
        ExtractedArchiveCount = dto.ExtractedArchiveCount,
        ExtractedArchiveBytes = dto.ExtractedArchiveBytes,
        GraveyardTreeCount = dto.GraveyardTreeCount,
        GraveyardTreeBytes = dto.GraveyardTreeBytes,
        PathTooLongCount = dto.PathTooLongCount,
        InvalidCharCount = dto.InvalidCharCount
    };

    public static AuditCleanupResultDto ToDto(AuditCleanupResult result) => new()
    {
        SuccessCount = result.SuccessCount,
        FreedBytes = result.FreedBytes,
        DeletedPaths = result.DeletedPaths.ToList(),
        Errors = result.Errors
    };
}
