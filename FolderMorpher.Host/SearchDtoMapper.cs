using System.Text.RegularExpressions;
using FolderMorpher.Contracts;
using FolderMorpher.Models;

namespace FolderMorpher.Host;

internal static class SearchDtoMapper
{
    public static SearchQuery ToCore(SearchQueryDto dto) => new()
    {
        RawQuery = dto.RawQuery,
        Keywords = dto.Keywords,
        KeywordGroups = dto.KeywordGroups,
        ExactPhrases = dto.ExactPhrases,
        ExcludedWords = dto.ExcludedWords,
        Extensions = new HashSet<string>(dto.Extensions, StringComparer.OrdinalIgnoreCase),
        MinSizeBytes = dto.MinSizeBytes,
        MaxSizeBytes = dto.MaxSizeBytes,
        MinModifiedUtc = dto.MinModifiedUtc,
        MaxModifiedUtc = dto.MaxModifiedUtc,
        PathContains = dto.PathContains,
        MinPathLength = dto.MinPathLength,
        OnlyIllegalChars = dto.OnlyIllegalChars,
        DormantDays = dto.DormantDays,
        ContentKeyword = dto.ContentKeyword,
        SearchContentMode = dto.SearchContentMode,
        HasOfficeLinkOnly = dto.HasOfficeLinkOnly,
        OfficeLinkKeyword = dto.OfficeLinkKeyword,
        CompiledRegex = dto.RegexPattern == null ? null : new Regex(dto.RegexPattern, RegexOptions.IgnoreCase | RegexOptions.Compiled),
        IsDirectoryOnly = dto.IsDirectoryOnly,
        IncludeFolders = dto.IncludeFolders
    };

    public static SearchResultDto ToDto(SearchResultItem item) => new()
    {
        Name = item.Name, FullPath = item.FullPath, DirectoryPath = item.DirectoryPath,
        SizeBytes = item.SizeBytes, LastWriteTime = item.LastWriteTime,
        CreationTime = item.CreationTime, Extension = item.Extension,
        IsDirectory = item.IsDirectory, ContentSnippet = item.ContentSnippet,
        MatchedReason = item.MatchedReason
    };

    public static SearchResultItem ToCore(SearchResultDto dto) => new()
    {
        Name = dto.Name, FullPath = dto.FullPath, DirectoryPath = dto.DirectoryPath,
        SizeBytes = dto.SizeBytes, LastWriteTime = dto.LastWriteTime,
        CreationTime = dto.CreationTime, Extension = dto.Extension,
        IsDirectory = dto.IsDirectory, ContentSnippet = dto.ContentSnippet,
        MatchedReason = dto.MatchedReason
    };

    public static SearchProgressDto ToDto(SearchProgressReport report) => new()
    {
        HitCount = report.HitCount, ScannedCount = report.ScannedCount,
        AccessDeniedFolders = report.AccessDeniedFolders, UnreadFiles = report.UnreadFiles,
        TotalHitBytes = report.TotalHitBytes, CurrentPath = report.CurrentPath,
        Elapsed = report.Elapsed, IsCompleted = report.IsCompleted
    };
}
