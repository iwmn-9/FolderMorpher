namespace FolderMorpher.Contracts;

public sealed class ShortcutLinkDto
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string OldTarget { get; set; } = string.Empty;
    public string NewTarget { get; set; } = string.Empty;
    public bool IsFixed { get; set; }
    public string Status { get; set; } = string.Empty;
    public OfficeLinkDto? AssociatedOfficeItem { get; set; }
}

public sealed class OfficeLinkDto
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string FoundPattern { get; set; } = string.Empty;
    public string TargetReplacement { get; set; } = string.Empty;
    public bool IsLocked { get; set; }
    public string LockUser { get; set; } = string.Empty;
    public int Categories { get; set; }
    public string LinkType { get; set; } = string.Empty;
    public int FixStatus { get; set; }
    public long ExpectedLength { get; set; }
    public DateTime ExpectedLastWriteTimeUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsFixed { get; set; }
}
