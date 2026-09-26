namespace FolderMorpher.Contracts;

public sealed class SearchQueryDto
{
    public string RawQuery { get; set; } = string.Empty;
    public List<string> Keywords { get; set; } = new();
    public List<List<string>> KeywordGroups { get; set; } = new();
    public List<string> ExactPhrases { get; set; } = new();
    public List<string> ExcludedWords { get; set; } = new();
    public List<string> Extensions { get; set; } = new();
    public long? MinSizeBytes { get; set; }
    public long? MaxSizeBytes { get; set; }
    public DateTime? MinModifiedUtc { get; set; }
    public DateTime? MaxModifiedUtc { get; set; }
    public List<string> PathContains { get; set; } = new();
    public int? MinPathLength { get; set; }
    public bool OnlyIllegalChars { get; set; }
    public int? DormantDays { get; set; }
    public string? ContentKeyword { get; set; }
    public bool SearchContentMode { get; set; }
    public bool HasOfficeLinkOnly { get; set; }
    public string? OfficeLinkKeyword { get; set; }
    public string? RegexPattern { get; set; }
    public bool? IsDirectoryOnly { get; set; }
    public bool IncludeFolders { get; set; }
}

public sealed class SearchResultDto
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public string DirectoryPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public DateTime LastWriteTime { get; set; }
    public DateTime CreationTime { get; set; }
    public string Extension { get; set; } = string.Empty;
    public bool IsDirectory { get; set; }
    public string? ContentSnippet { get; set; }
    public string MatchedReason { get; set; } = string.Empty;
}

public sealed class SearchProgressDto
{
    public int HitCount { get; set; }
    public int ScannedCount { get; set; }
    public int AccessDeniedFolders { get; set; }
    public int UnreadFiles { get; set; }
    public long TotalHitBytes { get; set; }
    public string CurrentPath { get; set; } = string.Empty;
    public TimeSpan Elapsed { get; set; }
    public bool IsCompleted { get; set; }
}
