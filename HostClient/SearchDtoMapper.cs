using FolderMorpher.Contracts;
using FolderMorpher.Models;

namespace FolderMorpher.HostClient;

public static class SearchDtoMapper
{
    public static SearchResultDto ToDto(SearchResultItem item) => new()
    {
        Name = item.Name, FullPath = item.FullPath, DirectoryPath = item.DirectoryPath,
        SizeBytes = item.SizeBytes, LastWriteTime = item.LastWriteTime,
        CreationTime = item.CreationTime, Extension = item.Extension,
        IsDirectory = item.IsDirectory, ContentSnippet = item.ContentSnippet,
        MatchedReason = item.MatchedReason
    };

    public static SearchQueryDto ToDto(SearchQuery query) => new()
    {
        RawQuery = query.RawQuery,
        Keywords = query.Keywords,
        KeywordGroups = query.KeywordGroups,
        ExactPhrases = query.ExactPhrases,
        ExcludedWords = query.ExcludedWords,
        Extensions = query.Extensions.ToList(),
        MinSizeBytes = query.MinSizeBytes,
        MaxSizeBytes = query.MaxSizeBytes,
        MinModifiedUtc = query.MinModifiedUtc,
        MaxModifiedUtc = query.MaxModifiedUtc,
        PathContains = query.PathContains,
        MinPathLength = query.MinPathLength,
        OnlyIllegalChars = query.OnlyIllegalChars,
        DormantDays = query.DormantDays,
        ContentKeyword = query.ContentKeyword,
        SearchContentMode = query.SearchContentMode,
        HasOfficeLinkOnly = query.HasOfficeLinkOnly,
        OfficeLinkKeyword = query.OfficeLinkKeyword,
        RegexPattern = query.CompiledRegex?.ToString(),
        IsDirectoryOnly = query.IsDirectoryOnly,
        IncludeFolders = query.IncludeFolders
    };

    public static SearchResultItem ToViewItem(SearchResultDto dto) => new()
    {
        Name = dto.Name, FullPath = dto.FullPath, DirectoryPath = dto.DirectoryPath,
        SizeBytes = dto.SizeBytes, LastWriteTime = dto.LastWriteTime,
        CreationTime = dto.CreationTime, Extension = dto.Extension,
        IsDirectory = dto.IsDirectory, ContentSnippet = dto.ContentSnippet,
        MatchedReason = dto.MatchedReason
    };
}
