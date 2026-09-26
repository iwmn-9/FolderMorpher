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
    /// 解析済みの構造化検索クエリ
    /// </summary>
    public class SearchQuery
    {
        public string RawQuery { get; set; } = string.Empty;
        public List<string> Keywords { get; set; } = new();
        public List<List<string>> KeywordGroups { get; set; } = new();
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

        public SearchQuery Clone()
        {
            return new SearchQuery
            {
                Keywords = new List<string>(Keywords),
                KeywordGroups = KeywordGroups.Select(g => new List<string>(g)).ToList(),
                ExactPhrases = new List<string>(ExactPhrases),
                ExcludedWords = new List<string>(ExcludedWords),
                Extensions = new HashSet<string>(Extensions, StringComparer.OrdinalIgnoreCase),
                MinSizeBytes = MinSizeBytes,
                MaxSizeBytes = MaxSizeBytes,
                MinModifiedUtc = MinModifiedUtc,
                MaxModifiedUtc = MaxModifiedUtc,
                PathContains = new List<string>(PathContains),
                MinPathLength = MinPathLength,
                OnlyIllegalChars = OnlyIllegalChars,
                DormantDays = DormantDays,
                ContentKeyword = ContentKeyword,
                SearchContentMode = SearchContentMode,
                HasOfficeLinkOnly = HasOfficeLinkOnly,
                OfficeLinkKeyword = OfficeLinkKeyword,
                CompiledRegex = CompiledRegex,
                IsDirectoryOnly = IsDirectoryOnly,
                IncludeFolders = IncludeFolders
            };
        }
    }

    /// <summary>
    /// 単一の検索結果行データ（仮想化 DataGrid 向け）
    /// </summary>
    public partial class SearchResultItem : INotifyPropertyChanged
    {
        private string _name = string.Empty;
        private string _fullPath = string.Empty;
        private string _directoryPath = string.Empty;
        private long _sizeBytes;
        private DateTime _lastWriteTime;
        private DateTime _creationTime;
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
            set { _sizeBytes = value; OnPropertyChanged(); OnPropertyChanged("FormattedSize"); }
        }

        public DateTime LastWriteTime
        {
            get => _lastWriteTime;
            set { _lastWriteTime = value; OnPropertyChanged(); OnPropertyChanged("FormattedDate"); }
        }

        public DateTime CreationTime
        {
            get => _creationTime;
            set { _creationTime = value; OnPropertyChanged(); OnPropertyChanged("FormattedCreatedDate"); }
        }

        public string Extension
        {
            get => _extension;
            set { _extension = value; OnPropertyChanged(); }
        }

        public bool IsDirectory
        {
            get => _isDirectory;
            set { _isDirectory = value; OnPropertyChanged(); OnPropertyChanged("TypeIcon"); }
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

        public int PathLength => FullPath.Length;
        public bool HasSnippet => !string.IsNullOrWhiteSpace(ContentSnippet);

        public bool IsPathLengthRisk => PathLength > 240;

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
        public int AccessDeniedFolders { get; set; }
        public int UnreadFiles { get; set; }
        public long TotalHitBytes { get; set; }
        public string CurrentPath { get; set; } = string.Empty;
        public TimeSpan Elapsed { get; set; }
        public bool IsCompleted { get; set; }
    }
}
