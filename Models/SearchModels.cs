using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AstraSize.Models;
using FolderMorpher.Services;

namespace FolderMorpher.Models
{
    /// <summary>
    /// 検索対象のスコープ
    /// </summary>
    public enum SearchScope
    {
        /// <summary>
        /// Storage Explorer (Tab 1) でスキャン済みの全ツリーから0秒インメモリ検索
        /// </summary>
        ScannedTrees = 0,

        /// <summary>
        /// 指定したローカルまたはUNCフォルダーを直接ストリーミング走査
        /// </summary>
        DirectFolder = 1
    }

    /// <summary>
    /// 解析済みの構造化検索クエリ
    /// </summary>
    public class SearchQuery
    {
        public string RawQuery { get; set; } = string.Empty;
        public List<string> Keywords { get; set; } = new();
        public List<string> ExactPhrases { get; set; } = new();
        public List<string> ExcludedWords { get; set; } = new();
        public HashSet<string> Extensions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public long? MinSizeBytes { get; set; }
        public long? MaxSizeBytes { get; set; }
        public DateTime? MinModifiedUtc { get; set; }
        public DateTime? MaxModifiedUtc { get; set; }
        public List<string> PathContains { get; set; } = new();
        public int? MinPathLength { get; set; }
        public bool OnlyIllegalChars { get; set; }
        public int? DormantDays { get; set; }
        public int? DormantYears
        {
            get => DormantDays.HasValue ? (int)Math.Round(DormantDays.Value / 365.0) : null;
            set => DormantDays = value.HasValue ? value.Value * 365 : null;
        }
        public string? ContentKeyword { get; set; }
        public bool SearchContentMode { get; set; }
        public bool HasOfficeLinkOnly { get; set; }
        public string? OfficeLinkKeyword { get; set; }
        public Regex? CompiledRegex { get; set; }
        public bool? IsDirectoryOnly { get; set; }
        public bool IncludeFolders { get; set; } = false;

        public bool IsEmpty =>
            Keywords.Count == 0 &&
            ExactPhrases.Count == 0 &&
            ExcludedWords.Count == 0 &&
            Extensions.Count == 0 &&
            !MinSizeBytes.HasValue &&
            !MaxSizeBytes.HasValue &&
            !MinModifiedUtc.HasValue &&
            !MaxModifiedUtc.HasValue &&
            PathContains.Count == 0 &&
            !MinPathLength.HasValue &&
            !OnlyIllegalChars &&
            !DormantDays.HasValue &&
            string.IsNullOrEmpty(ContentKeyword) &&
            !SearchContentMode &&
            !HasOfficeLinkOnly &&
            string.IsNullOrEmpty(OfficeLinkKeyword) &&
            CompiledRegex == null &&
            !IsDirectoryOnly.HasValue;

        public bool HasDeepFileIoRequirement =>
            !string.IsNullOrEmpty(ContentKeyword) ||
            HasOfficeLinkOnly ||
            !string.IsNullOrEmpty(OfficeLinkKeyword) ||
            (SearchContentMode && Keywords.Count > 0);
    }

    /// <summary>
    /// 単一の検索結果行データ（仮想化 DataGrid 向け）
    /// </summary>
    public class SearchResultItem : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _fullPath = string.Empty;
        private string _directoryPath = string.Empty;
        private long _sizeBytes;
        private DateTime _lastWriteTime;
        private string _extension = string.Empty;
        private bool _isDirectory;
        private string? _contentSnippet;
        private string _matchedReason = string.Empty;

        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(); }
        }

        public string FullPath
        {
            get => _fullPath;
            set { _fullPath = value; OnPropertyChanged(); OnPropertyChanged(nameof(PathLength)); }
        }

        public string DirectoryPath
        {
            get => _directoryPath;
            set { _directoryPath = value; OnPropertyChanged(); }
        }

        public long SizeBytes
        {
            get => _sizeBytes;
            set { _sizeBytes = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedSize)); }
        }

        public DateTime LastWriteTime
        {
            get => _lastWriteTime;
            set { _lastWriteTime = value; OnPropertyChanged(); OnPropertyChanged(nameof(FormattedDate)); }
        }

        public string Extension
        {
            get => _extension;
            set { _extension = value; OnPropertyChanged(); }
        }

        public bool IsDirectory
        {
            get => _isDirectory;
            set { _isDirectory = value; OnPropertyChanged(); OnPropertyChanged(nameof(TypeIcon)); }
        }

        public string? ContentSnippet
        {
            get => _contentSnippet;
            set { _contentSnippet = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasSnippet)); }
        }

        public string MatchedReason
        {
            get => _matchedReason;
            set { _matchedReason = value; OnPropertyChanged(); }
        }

        public string FormattedSize => IsDirectory ? "-" : FormatHelper.FormatBytes(SizeBytes, 2);
        public string FormattedDate => LastWriteTime != DateTime.MinValue ? LastWriteTime.ToString("yyyy/MM/dd HH:mm:ss") : "-";
        public int PathLength => FullPath.Length;
        public bool HasSnippet => !string.IsNullOrWhiteSpace(ContentSnippet);
        public string TypeIcon => IsDirectory ? "\U0001F4C1" : GetFileIcon(Extension);
        public string DisplaySnippetOrReason => HasSnippet ? ContentSnippet! : MatchedReason;

        public bool IsPathLengthRisk => PathLength > 240;
        public string JumpFolderText => Strings.JumpFolder;
        public string JumpFolderToolTip => Strings.JumpFolderToolTip;

        #region Tabler File-Type Badge Properties

        public string BadgeText
        {
            get
            {
                if (IsDirectory) return "DIR";
                var ext = (Extension ?? string.Empty).TrimStart('.').ToUpperInvariant();
                if (string.IsNullOrEmpty(ext)) return "FILE";
                if (ext.Length > 4) return ext.Substring(0, 4);
                return ext;
            }
        }

        public string BadgeBackground
        {
            get
            {
                if (IsDirectory) return "#EFF6FF"; // Blue-50
                var ext = (Extension ?? string.Empty).ToLowerInvariant();
                return ext switch
                {
                    ".xlsx" or ".xls" or ".xlsm" => "#ECFDF5",             // Green-50
                    ".csv" or ".tsv" => "#F0FDF4",                         // Emerald-50
                    ".pdf" => "#FEF2F2",                                   // Red-50
                    ".docx" or ".doc" => "#EFF6FF",                        // Blue-50
                    ".pptx" or ".ppt" => "#FFF7ED",                        // Orange-50
                    ".txt" or ".log" or ".md" => "#F8FAFC",                // Slate-50
                    ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "#FEF3C7", // Amber-50
                    ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" => "#F5F3FF", // Purple-50
                    ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" => "#ECFEFF", // Cyan-50
                    ".mp3" or ".wav" or ".m4a" or ".flac" => "#FDF2F8",    // Pink-50
                    ".exe" or ".msi" => "#F1F5F9",                         // Slate-100
                    ".ps1" or ".bat" or ".cmd" or ".sh" => "#F0FDF4",      // Emerald-50
                    ".sql" or ".db" or ".sqlite" => "#EEF2FF",             // Indigo-50
                    ".json" or ".xml" or ".yaml" or ".yml" => "#F0FDFA",   // Teal-50
                    _ => "#F8FAFC"
                };
            }
        }

        public string BadgeBorderBrush
        {
            get
            {
                if (IsDirectory) return "#60A5FA"; // Blue-400
                var ext = (Extension ?? string.Empty).ToLowerInvariant();
                return ext switch
                {
                    ".xlsx" or ".xls" or ".xlsm" => "#10B981",             // Green-500
                    ".csv" or ".tsv" => "#34D399",                         // Emerald-400
                    ".pdf" => "#EF4444",                                   // Red-500
                    ".docx" or ".doc" => "#3B82F6",                        // Blue-500
                    ".pptx" or ".ppt" => "#F97316",                        // Orange-500
                    ".txt" or ".log" or ".md" => "#94A3B8",                // Slate-400
                    ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "#F59E0B", // Amber-500
                    ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" => "#8B5CF6", // Purple-500
                    ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" => "#06B6D4", // Cyan-500
                    ".mp3" or ".wav" or ".m4a" or ".flac" => "#EC4899",    // Pink-500
                    ".exe" or ".msi" => "#64748B",                         // Slate-500
                    ".ps1" or ".bat" or ".cmd" or ".sh" => "#10B981",      // Emerald-500
                    ".sql" or ".db" or ".sqlite" => "#6366F1",              // Indigo-500
                    ".json" or ".xml" or ".yaml" or ".yml" => "#14B8A6",    // Teal-500
                    _ => "#CBD5E1"                                         // Slate-300
                };
            }
        }

        public string BadgeForeground
        {
            get
            {
                if (IsDirectory) return "#2563EB"; // Blue-600
                var ext = (Extension ?? string.Empty).ToLowerInvariant();
                return ext switch
                {
                    ".xlsx" or ".xls" or ".xlsm" => "#059669",             // Green-600
                    ".csv" or ".tsv" => "#059669",                         // Emerald-600
                    ".pdf" => "#DC2626",                                   // Red-600
                    ".docx" or ".doc" => "#2563EB",                        // Blue-600
                    ".pptx" or ".ppt" => "#EA580C",                        // Orange-600
                    ".txt" or ".log" or ".md" => "#475569",                // Slate-600
                    ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "#D97706", // Amber-600
                    ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" => "#7C3AED", // Purple-600
                    ".mp4" or ".mov" or ".avi" or ".mkv" or ".wmv" => "#0891B2", // Cyan-600
                    ".mp3" or ".wav" or ".m4a" or ".flac" => "#DB2777",    // Pink-600
                    ".exe" or ".msi" => "#334155",                         // Slate-700
                    ".ps1" or ".bat" or ".cmd" or ".sh" => "#059669",      // Emerald-600
                    ".sql" or ".db" or ".sqlite" => "#4F46E5",             // Indigo-600
                    ".json" or ".xml" or ".yaml" or ".yml" => "#0D9488",   // Teal-600
                    _ => "#475569"                                         // Slate-600
                };
            }
        }

        #endregion

        public void NotifyLanguageChanged()
        {
            OnPropertyChanged(nameof(JumpFolderText));
            OnPropertyChanged(nameof(JumpFolderToolTip));
        }

        private static string GetFileIcon(string ext)
        {
            return (ext ?? string.Empty).ToLowerInvariant() switch
            {
                ".xlsx" or ".xls" or ".xlsm" or ".csv" => "\U0001F4CA", // 📊
                ".docx" or ".doc" => "\U0001F4DD", // 📝
                ".pptx" or ".ppt" => "\U0001F4D1", // 📑
                ".pdf" => "\U0001F4D5", // 📕
                ".zip" or ".7z" or ".rar" or ".tar" or ".gz" => "\U0001F4E6", // 📦
                ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "\U0001F5BC", // 🖼️
                ".mp4" or ".mov" or ".avi" or ".mkv" => "\U0001F3AC", // 🎬
                ".mp3" or ".wav" or ".m4a" or ".flac" => "\U0001F3B5", // 🎵
                ".exe" or ".msi" => "\u2699", // ⚙️
                ".ps1" or ".bat" or ".cmd" or ".sh" => "\U0001F4DC", // 📜
                ".txt" or ".log" or ".md" => "\U0001F4C4", // 📄
                _ => "\U0001F4C4"
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    /// <summary>
    /// 検索の進捗・状態レポート
    /// </summary>
    public class SearchProgressReport
    {
        public int HitCount { get; set; }
        public int ScannedCount { get; set; }
        public long TotalHitBytes { get; set; }
        public string CurrentPath { get; set; } = string.Empty;
        public TimeSpan Elapsed { get; set; }
        public bool IsCompleted { get; set; }
    }
}
