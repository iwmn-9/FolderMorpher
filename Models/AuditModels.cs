using System;
using System.IO;

namespace FolderMorpher.Models
{
    public enum AuditIssueType
    {
        Duplicate,     // 重複ファイル（SHA256完全一致）
        Dormant,       // 休眠ファイル（X年以上未更新）
        PathTooLong,   // パス長260文字超
        InvalidChar    // 移行禁則文字
    }

    public class AuditItem
    {
        public string FullPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string DirectoryPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public string SizeFormatted => FormatSize(Size);
        public DateTime LastWriteTime { get; set; }
        public DateTime LastAccessTime { get; set; }
        public AuditIssueType IssueType { get; set; }
        public string IssueTypeDisplay => IssueType switch
        {
            AuditIssueType.Duplicate => "重複ファイル",
            AuditIssueType.Dormant => "休眠ファイル",
            AuditIssueType.PathTooLong => "パス長超過 (260字超)",
            AuditIssueType.InvalidChar => "禁則文字",
            _ => "その他"
        };
        public string Detail { get; set; } = string.Empty;
        public string? Sha256Hash { get; set; }
        public string DuplicateGroupId { get; set; } = string.Empty;
        public int DuplicateGroupIndex { get; set; }
        public int DuplicateGroupColorIndex { get; set; }
        public bool IsOriginalCandidate { get; set; }

        public string DuplicateGroupBadge
        {
            get
            {
                if (IssueType != AuditIssueType.Duplicate || string.IsNullOrEmpty(DuplicateGroupId))
                    return "-";
                return IsOriginalCandidate ? $"{DuplicateGroupId} (原本候補)" : $"{DuplicateGroupId} (重複)";
            }
        }

        // 6色のソフトパステルパレット（隣接グループで重複しない視認性カラー）
        public static readonly string[] GroupBgPalette = { "#EFF6FF", "#ECFDF5", "#FEF3C7", "#F3E8FF", "#FFE4E6", "#E0F2FE" };
        public static readonly string[] GroupBorderPalette = { "#BFDBFE", "#A7F3D0", "#FDE68A", "#DDD6FE", "#FECDD3", "#BAE6FD" };
        public static readonly string[] GroupTextPalette = { "#1E40AF", "#065F46", "#92400E", "#5B21B6", "#9F1239", "#075985" };

        public string RowBackgroundHex => IssueType == AuditIssueType.Duplicate && DuplicateGroupIndex > 0
            ? GroupBgPalette[DuplicateGroupColorIndex % GroupBgPalette.Length]
            : "Transparent";

        public string BadgeBackgroundHex => IssueType == AuditIssueType.Duplicate && DuplicateGroupIndex > 0
            ? GroupBgPalette[DuplicateGroupColorIndex % GroupBgPalette.Length]
            : "Transparent";

        public string BadgeBorderHex => IssueType == AuditIssueType.Duplicate && DuplicateGroupIndex > 0
            ? GroupBorderPalette[DuplicateGroupColorIndex % GroupBorderPalette.Length]
            : "#E2E8F0";

        public string BadgeForegroundHex => IssueType == AuditIssueType.Duplicate && DuplicateGroupIndex > 0
            ? GroupTextPalette[DuplicateGroupColorIndex % GroupTextPalette.Length]
            : "#64748B";

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
                return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
            if (bytes >= 1024L * 1024L)
                return $"{bytes / (1024.0 * 1024.0):F2} MB";
            if (bytes >= 1024L)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }
    }

    public class AuditSummary
    {
        public long TotalFilesScanned { get; set; }
        public int DuplicateCount { get; set; }
        public long DuplicateWastedBytes { get; set; }
        public int DormantCount { get; set; }
        public long DormantBytes { get; set; }
        public int PathTooLongCount { get; set; }
        public int InvalidCharCount { get; set; }

        public string DuplicateWastedSizeFormatted => FormatSize(DuplicateWastedBytes);
        public string DormantSizeFormatted => FormatSize(DormantBytes);

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1024L * 1024L * 1024L)
                return $"{bytes / (1024.0 * 1024.0 * 1024.0):F2} GB";
            if (bytes >= 1024L * 1024L)
                return $"{bytes / (1024.0 * 1024.0):F2} MB";
            if (bytes >= 1024L)
                return $"{bytes / 1024.0:F1} KB";
            return $"{bytes} B";
        }
    }

    public class AuditOptions
    {
        public string TargetDirectory { get; set; } = string.Empty;
        public bool CheckDuplicates { get; set; } = true;
        public bool CheckDormant { get; set; } = true;
        public double DormantYearsThreshold { get; set; } = 3.0;
        public bool CheckPathLimits { get; set; } = true;
        public long MinFileSizeBytes { get; set; } = 100 * 1024; // デフォルト100KB以上を重複チェック対象
    }

    public class AuditProgress
    {
        public string CurrentStatus { get; set; } = string.Empty;
        public long ScannedFilesCount { get; set; }
        public int IssueCount { get; set; }
    }
}
