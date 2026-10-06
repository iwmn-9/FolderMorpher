using FolderMorpher.Services;

namespace FolderMorpher.Models;

public partial class SearchResultItem
{
    public string FormattedSize => IsDirectory ? "-" : FormatHelper.FormatBytes(SizeBytes, 2);
    public string FormattedDate => LastWriteTime != DateTime.MinValue ? LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss") : "-";
    public string FormattedCreatedDate => CreationTime != DateTime.MinValue ? CreationTime.ToString("yyyy/MM/dd HH:mm:ss") : "-";
    public string TypeIcon => IsDirectory ? "📁" : GetFileIcon(Extension);
    public string DisplaySnippetOrReason => HasSnippet ? ContentSnippet! : MatchedReason;
    public string JumpFolderText => Strings.JumpFolder;
    public string JumpFolderToolTip => Strings.JumpFolderToolTip;
    private TablerBadgeInfo BadgeInfo => TablerBadgeHelper.GetBadge(Extension, IsDirectory);
    public string BadgeText => BadgeInfo.Text;
    public string BadgeBackground => BadgeInfo.Background;
    public string BadgeBorderBrush => BadgeInfo.BorderBrush;
    public string BadgeForeground => BadgeInfo.Foreground;

    public void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(JumpFolderText));
        OnPropertyChanged(nameof(JumpFolderToolTip));
    }

    private static string GetFileIcon(string ext) => (ext ?? string.Empty).ToLowerInvariant() switch
    {
        ".xlsx" or ".xls" or ".xlsm" or ".csv" => "📊",
        ".docx" or ".doc" => "📝",
        ".pptx" or ".ppt" => "📑",
        ".pdf" => "📕",
        ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "📦",
        ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "🖼️",
        ".mp4" or ".mov" or ".avi" or ".mkv" => "🎬",
        ".mp3" or ".wav" or ".m4a" or ".flac" => "🎵",
        ".exe" or ".msi" => "⚙️",
        ".ps1" or ".bat" or ".cmd" or ".sh" => "📜",
        _ => "📄"
    };
}
