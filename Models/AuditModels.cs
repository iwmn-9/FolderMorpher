using System;
using System.IO;
using FolderMorpher.Services;

namespace FolderMorpher.Models
{
    public enum AuditIssueType
    {
        Duplicate,        // 重複ファイル（SHA256完全一致）
        Dormant,          // 休眠ファイル（X年以上未更新）
        PathTooLong,      // パス長危険域 (240文字以上)
        InvalidChar,      // 移行禁則文字
        VersionFamily,    // 世代・旧版ファイル (最新版が同階層に存在)
        ExtractedArchive, // 展開済みZIP残骸 (同名フォルダーが存在)
        GraveyardTree     // 墓場フォルダー (配下全ファイルが休眠・化石化)
    }

    public enum AuditBandwidthLimit
    {
        Standard50MB = 0, // 通常 50MB/s (他業務保護)
        Unlimited = 1     // 無制限 (夜間・最速)
    }

    public class AuditItem : System.ComponentModel.INotifyPropertyChanged
    {
        public static Action<AuditItem>? GlobalCheckedChanged;

        /// <summary>
        /// 整理（削除）可能かどうか。
        /// 墓場フォルダー（フォルダー全体の事故削除防止）および原本候補ファイルは安全のため削除対象外。
        /// </summary>
        public bool IsCleanable => IssueType != AuditIssueType.GraveyardTree && !IsOriginalCandidate;

        private bool _isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (value && !IsCleanable) return;
                if (_isChecked != value)
                {
                    _isChecked = value;
                    OnPropertyChanged(nameof(IsChecked));
                    GlobalCheckedChanged?.Invoke(this);
                }
            }
        }

        public string FullPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string DirectoryPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public string SizeFormatted => FormatHelper.FormatBytes(Size, 2);

        private TablerBadgeInfo BadgeInfo => TablerBadgeHelper.GetBadge(FileName, false);
        public string BadgeText => BadgeInfo.Text;
        public string BadgeBackground => BadgeInfo.Background;
        public string BadgeBorderBrush => BadgeInfo.BorderBrush;
        public string BadgeForeground => BadgeInfo.Foreground;
        public DateTime LastWriteTime { get; set; }
        public DateTime LastAccessTime { get; set; }
        public AuditIssueType IssueType { get; set; }
        public string IssueTypeDisplay
        {
            get
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                return IssueType switch
                {
                    AuditIssueType.Duplicate => isJa ? "完全重複" : "Duplicate File",
                    AuditIssueType.Dormant => isJa ? "長期休眠" : "Dormant File",
                    AuditIssueType.VersionFamily => isJa ? "世代・旧版" : "Older Version",
                    AuditIssueType.ExtractedArchive => isJa ? "展開済ZIP残骸" : "Extracted Archive",
                    AuditIssueType.GraveyardTree => isJa ? "墓場フォルダー" : "Graveyard Folder",
                    AuditIssueType.PathTooLong => isJa ? "パス長危険域 (240字超)" : "Long Path (>240 chars)",
                    AuditIssueType.InvalidChar => isJa ? "地雷文字" : "Invalid Characters",
                    _ => isJa ? "その他" : "Other"
                };
            }
        }
        public string Detail { get; set; } = string.Empty;
        public string? Sha256Hash { get; set; }
        public string DuplicateGroupId { get; set; } = string.Empty;
        public int DuplicateGroupIndex { get; set; }
        public int DuplicateGroupColorIndex { get; set; }
        public bool IsOriginalCandidate { get; set; }

        // 整理候補スコア & 親しみやすい目安
        public int WasteScore { get; set; } = 50;
        public string? RelatedActivePath { get; set; }
        public List<ScoreFactorItem> ScoreBreakdown { get; set; } = new();

        public string ScoreBreakdownSummary
        {
            get
            {
                if (ScoreBreakdown == null || ScoreBreakdown.Count == 0)
                {
                    return LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                        ? $"スコア: {WasteScore} 点"
                        : $"Score: {WasteScore} pts";
                }
                var parts = ScoreBreakdown.Select(f => f.DisplayText);
                return string.Join(" / ", parts) + $" ➔ 合計: {WasteScore}点";
            }
        }

        public const string ConfidenceRecommendedJa = "整理推奨";
        public const string ConfidenceRecommendedEn = "Recommended";
        public const string ConfidenceReviewNeededJa = "要確認";
        public const string ConfidenceReviewNeededEn = "Review Needed";
        public const string ConfidenceReferenceJa = "参考";
        public const string ConfidenceReferenceEn = "Reference";

        public string ConfidenceDisplay
        {
            get
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                if (WasteScore >= 80) return isJa ? ConfidenceRecommendedJa : ConfidenceRecommendedEn;
                if (WasteScore >= 50) return isJa ? ConfidenceReviewNeededJa : ConfidenceReviewNeededEn;
                return isJa ? ConfidenceReferenceJa : ConfidenceReferenceEn;
            }
        }

        public string ConfidenceBadgeBgHex => WasteScore switch
        {
            >= 80 => "#FEE2E2", // 薄赤
            >= 50 => "#FEF3C7", // 薄黄
            _ => "#F1F5F9"      // 薄灰
        };

        public string ConfidenceBadgeBorderHex => WasteScore switch
        {
            >= 80 => "#FCA5A5",
            >= 50 => "#FDE68A",
            _ => "#CBD5E1"
        };

        public string ConfidenceBadgeFgHex => WasteScore switch
        {
            >= 80 => "#991B1B",
            >= 50 => "#92400E",
            _ => "#475569"
        };

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string prop) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(prop));

        public string DuplicateGroupBadge
        {
            get
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                if (IssueType == AuditIssueType.Duplicate && !string.IsNullOrEmpty(DuplicateGroupId))
                {
                    return IsOriginalCandidate 
                        ? $"{DuplicateGroupId} {(isJa ? "(原本候補)" : "(Original)")}" 
                        : $"{DuplicateGroupId} {(isJa ? "(重複)" : "(Duplicate)")}";
                }
                if (IssueType == AuditIssueType.VersionFamily)
                {
                    return isJa ? "👥 最新版あり" : "👥 Has Newer Version";
                }
                if (IssueType == AuditIssueType.ExtractedArchive)
                {
                    return isJa ? "📦 展開済フォルダあり" : "📦 Extracted Folder Exists";
                }
                if (IssueType == AuditIssueType.GraveyardTree)
                {
                    return isJa ? "🪦 化石化" : "🪦 Abandoned";
                }
                if (IssueType == AuditIssueType.Dormant)
                {
                    return isJa ? "⏳ 休眠" : "⏳ Dormant";
                }
                return "-";
            }
        }

        // 6色のソフトパステルパレット（隣接グループで重複しない視認性カラー）
        public static readonly string[] GroupBgPalette = { "#EFF6FF", "#ECFDF5", "#FEF3C7", "#F3E8FF", "#FFE4E6", "#E0F2FE" };
        public static readonly string[] GroupBorderPalette = { "#BFDBFE", "#A7F3D0", "#FDE68A", "#DDD6FE", "#FECDD3", "#BAE6FD" };
        public static readonly string[] GroupTextPalette = { "#1E40AF", "#065F46", "#92400E", "#5B21B6", "#9F1239", "#075985" };

        public string RowBackgroundHex => IssueType == AuditIssueType.Duplicate && DuplicateGroupIndex > 0
            ? GroupBgPalette[DuplicateGroupColorIndex % GroupBgPalette.Length]
            : "Transparent";

        public string BadgeBackgroundHex
        {
            get
            {
                if (IssueType == AuditIssueType.Duplicate && DuplicateGroupIndex > 0)
                    return GroupBgPalette[DuplicateGroupColorIndex % GroupBgPalette.Length];
                if (IssueType == AuditIssueType.VersionFamily) return "#EFF6FF"; // 薄青
                if (IssueType == AuditIssueType.ExtractedArchive) return "#FDF4FF"; // 薄紫
                if (IssueType == AuditIssueType.GraveyardTree) return "#FEF2F2"; // 薄赤
                return "Transparent";
            }
        }

        public string BadgeBorderHex
        {
            get
            {
                if (IssueType == AuditIssueType.Duplicate && DuplicateGroupIndex > 0)
                    return GroupBorderPalette[DuplicateGroupColorIndex % GroupBorderPalette.Length];
                if (IssueType == AuditIssueType.VersionFamily) return "#BFDBFE";
                if (IssueType == AuditIssueType.ExtractedArchive) return "#F0ABFC";
                if (IssueType == AuditIssueType.GraveyardTree) return "#FECACA";
                return "#E2E8F0";
            }
        }

        public string BadgeForegroundHex
        {
            get
            {
                if (IssueType == AuditIssueType.Duplicate && DuplicateGroupIndex > 0)
                    return GroupTextPalette[DuplicateGroupColorIndex % GroupTextPalette.Length];
                if (IssueType == AuditIssueType.VersionFamily) return "#1D4ED8";
                if (IssueType == AuditIssueType.ExtractedArchive) return "#A21CAF";
                if (IssueType == AuditIssueType.GraveyardTree) return "#DC2626";
                return "#64748B";
            }
        }
    }

    public class AuditSummary
    {
        public long TotalFilesScanned { get; set; }
        public ScanCoverage Coverage { get; set; } = new();
        public int InaccessibleDirectoriesCount => Coverage.AccessDeniedFolders;
        public int DuplicateCount { get; set; }
        public long DuplicateWastedBytes { get; set; }
        public int DormantCount { get; set; }
        public long DormantBytes { get; set; }
        public int VersionFamilyCount { get; set; }
        public long VersionFamilyBytes { get; set; }
        public int ExtractedArchiveCount { get; set; }
        public long ExtractedArchiveBytes { get; set; }
        public int GraveyardTreeCount { get; set; }
        public long GraveyardTreeBytes { get; set; }
        public int PathTooLongCount { get; set; }
        public int InvalidCharCount { get; set; }

        // すぐ整理できそうな容量（重複 + 世代旧版 + 展開済ZIP + 墓場）
        public long ReadyToCleanBytes => DuplicateWastedBytes + VersionFamilyBytes + ExtractedArchiveBytes + GraveyardTreeBytes;
        public string ReadyToCleanSizeFormatted => FormatHelper.FormatBytes(ReadyToCleanBytes, 2);

        public string DuplicateWastedSizeFormatted => FormatHelper.FormatBytes(DuplicateWastedBytes, 2);
        public string DormantSizeFormatted => FormatHelper.FormatBytes(DormantBytes, 2);
        public string VersionFamilySizeFormatted => FormatHelper.FormatBytes(VersionFamilyBytes, 2);
        public string ExtractedArchiveSizeFormatted => FormatHelper.FormatBytes(ExtractedArchiveBytes, 2);
        public string GraveyardTreeSizeFormatted => FormatHelper.FormatBytes(GraveyardTreeBytes, 2);
    }

    public class AuditOptions
    {
        public string TargetDirectory { get; set; } = string.Empty;
        public bool CheckDuplicates { get; set; } = true;
        public bool CheckDormant { get; set; } = true;
        public bool CheckVersionFamilies { get; set; } = true;
        public bool CheckExtractedArchives { get; set; } = true;
        public bool CheckGraveyardTrees { get; set; } = true;
        public double DormantYearsThreshold { get; set; } = 3.0;
        public bool CheckPathLimits { get; set; } = true;
        public long MinFileSizeBytes { get; set; } = 100 * 1024; // デフォルト100KB以上を重複チェック対象
        public AuditBandwidthLimit BandwidthLimit { get; set; } = AuditBandwidthLimit.Standard50MB;
        public System.Collections.Generic.List<string> ExcludeFolderPatterns { get; set; } = new();
    }

    public class AuditProgress
    {
        public string CurrentStatus { get; set; } = string.Empty;
        public long ScannedFilesCount { get; set; }
        public int IssueCount { get; set; }
    }

    /// <summary>
    /// スコア算出根拠の要素アイテム
    /// </summary>
    public class ScoreFactorItem
    {
        public string NameJa { get; set; } = string.Empty;
        public string NameEn { get; set; } = string.Empty;
        public int Points { get; set; }

        public string Name => LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese ? NameJa : NameEn;
        public string DisplayText => $"{Name} ({(Points >= 0 ? "+" : "")}{Points} pt)";
    }

    /// <summary>
    /// 整理除外（保持マーク）されたファイルのエントリ
    /// </summary>
    public class AuditIgnoreItem
    {
        public string FullPath { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public long LastWriteTimeUtcTicks { get; set; }
        public DateTime IgnoredAt { get; set; } = DateTime.Now;
    }
}
