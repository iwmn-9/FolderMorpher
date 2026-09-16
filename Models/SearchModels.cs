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
    /// 讀懃ｴ｢蟇ｾ雎｡縺ｮ繧ｹ繧ｳ繝ｼ繝・    /// </summary>
    public enum SearchScope
    {
        /// <summary>
        /// Storage Explorer (Tab 1) 縺ｧ繧ｹ繧ｭ繝｣繝ｳ貂医∩縺ｮ蜈ｨ繝・Μ繝ｼ縺九ｉ0遘偵う繝ｳ繝｡繝｢繝ｪ讀懃ｴ｢
        /// </summary>
        ScannedTrees = 0,

        /// <summary>
        /// 謖・ｮ壹＠縺溘Ο繝ｼ繧ｫ繝ｫ縺ｾ縺溘・UNC繝輔か繝ｫ繝繝ｼ繧堤峩謗･繧ｹ繝医Μ繝ｼ繝溘Φ繧ｰ襍ｰ譟ｻ
        /// </summary>
        DirectFolder = 1
    }

    /// <summary>
    /// 隗｣譫先ｸ医∩縺ｮ讒矩蛹匁､懃ｴ｢繧ｯ繧ｨ繝ｪ
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
        public int? DormantYears { get; set; }
        public bool OnlyDuplicates { get; set; }
        public string? ContentKeyword { get; set; }
        public string? OfficeLinkKeyword { get; set; }
        public Regex? CompiledRegex { get; set; }
        public bool? IsDirectoryOnly { get; set; }

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
            !DormantYears.HasValue &&
            !OnlyDuplicates &&
            string.IsNullOrEmpty(ContentKeyword) &&
            string.IsNullOrEmpty(OfficeLinkKeyword) &&
            CompiledRegex == null &&
            !IsDirectoryOnly.HasValue;

        public bool HasDeepFileIoRequirement =>
            !string.IsNullOrEmpty(ContentKeyword) || !string.IsNullOrEmpty(OfficeLinkKeyword);
    }

    /// <summary>
    /// 蜊倅ｸ縺ｮ讀懃ｴ｢邨先棡陦後ョ繝ｼ繧ｿ・井ｻｮ諠ｳ蛹・DataGrid 蜷代￠・・    /// </summary>
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
    /// 讀懃ｴ｢縺ｮ騾ｲ謐励・迥ｶ諷九Ξ繝昴・繝・    /// </summary>
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