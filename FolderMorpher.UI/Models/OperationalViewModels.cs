using AstraSize.Models;
using FolderMorpher.Models;

namespace FolderMorpher.Services;

public sealed class ScanCoverage
{
    public int TotalFoldersScanned { get; set; }
    public int AccessDeniedFolders { get; set; }
    public int TotalFilesFound { get; set; }
    public bool IsCompleteCoverage => AccessDeniedFolders == 0;
    public string SummaryText => IsCompleteCoverage
        ? $"{TotalFoldersScanned:N0} フォルダ完全走査 (アクセス拒否: 0)"
        : $"{TotalFoldersScanned:N0} フォルダ走査 (⚠️ アクセス拒否スキップ: {AccessDeniedFolders:N0} 箇所)";
}

public sealed class LinkFixItem
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public string OldTarget { get; set; } = string.Empty;
    public string NewTarget { get; set; } = string.Empty;
    public bool IsFixed { get; set; }
    public string Status { get; set; } = "検出";
    public OfficeLinkItem? AssociatedOfficeItem { get; set; }
    public bool NeedsFix => !IsFixed && !string.IsNullOrEmpty(NewTarget) &&
        !string.Equals(OldTarget, NewTarget, StringComparison.OrdinalIgnoreCase);
}

[Flags]
public enum OfficeLinkCategory
{
    None = 0,
    ExternalWorkbook = 1 << 0,
    FormulaOrHyperlink = 1 << 1,
    VbaMacro = 1 << 2
}

public enum OfficeFixStatus
{
    Detected = 0, FullyFixed = 1, PartiallyFixed = 2, Skipped = 3, Failed = 4
}

public sealed class OfficeLinkItem
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string Extension { get; set; } = string.Empty;
    public string FoundPattern { get; set; } = string.Empty;
    public string TargetReplacement { get; set; } = string.Empty;
    public bool IsLocked { get; set; }
    public string LockUser { get; set; } = string.Empty;
    public OfficeLinkCategory Categories { get; set; }
    public string LinkType { get; set; } = string.Empty;
    public OfficeFixStatus FixStatus { get; set; }
    public FileVersionStamp VersionStamp { get; set; } = FileVersionStamp.Empty;
    public long ExpectedLength
    {
        get => VersionStamp.Length;
        set => VersionStamp = new FileVersionStamp(value, VersionStamp.LastWriteTimeUtc);
    }
    public DateTime ExpectedLastWriteTimeUtc
    {
        get => VersionStamp.LastWriteTimeUtc;
        set => VersionStamp = new FileVersionStamp(VersionStamp.Length, value);
    }
    public string Status { get; set; } = "検出";
    public bool IsFixed { get; set; }
}
