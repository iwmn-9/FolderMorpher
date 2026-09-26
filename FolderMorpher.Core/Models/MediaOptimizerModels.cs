using System;
using System.Collections.Generic;
using FolderMorpher.Services;

namespace FolderMorpher.Models
{
    public partial class MediaItem
    {
        public string FullPath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string DirectoryPath { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public FileVersionStamp VersionStamp { get; set; } = FileVersionStamp.Empty;
        public long OriginalSizeBytes
        {
            get => VersionStamp.Length;
            set => VersionStamp = new FileVersionStamp(value, VersionStamp.LastWriteTimeUtc);
        }
        public long OptimizedSizeBytes { get; set; }
        public long SavedBytes => OriginalSizeBytes > OptimizedSizeBytes ? OriginalSizeBytes - OptimizedSizeBytes : 0;
        public double ReductionPercent => OriginalSizeBytes > 0 && SavedBytes > 0 ? (double)SavedBytes / OriginalSizeBytes * 100.0 : 0;

        public int Width { get; set; }
        public int Height { get; set; }
        public bool IsVideo { get; set; }
        public bool IsExcluded { get; set; }
        public string ExclusionReason { get; set; } = string.Empty;
        public DateTime ExpectedLastWriteTimeUtc
        {
            get => VersionStamp.LastWriteTimeUtc;
            set => VersionStamp = new FileVersionStamp(VersionStamp.Length, value);
        }
        public string Status { get; set; } = "待機";
        public bool IsProcessed { get; set; }
    }

    public class MediaOptimizeOptions
    {
        public string TargetDirectory { get; set; } = string.Empty;
        public int MaxDimension { get; set; } = 2560; // 長辺最大ピクセル (2K相当)
        public int JpegQuality { get; set; } = 85;    // JPEG品質 (85% スイートスポット)
        public long MinImageSizeBytes { get; set; } = 2 * 1024 * 1024; // 2MB以上の写真のみ対象
        public List<string> ExcludedFolderKeywords { get; set; } = new()
        {
            "_master", "_original", "_raw", "マスター", "印刷用", "原稿", "元データ", "広報用"
        };
        public List<string> CustomExcludedPaths { get; set; } = new();
        public string? BackupDirectory { get; set; } = null; // nullなら直接上書き置換
    }

    public partial class MediaOptimizeSummary
    {
        public int TotalImagesScanned { get; set; }
        public int TotalVideosScanned { get; set; }
        public int OptimizedImagesCount { get; set; }
        public long TotalOriginalBytes { get; set; }
        public long TotalOptimizedBytes { get; set; }
        public long TotalSavedBytes => TotalOriginalBytes > TotalOptimizedBytes ? TotalOriginalBytes - TotalOptimizedBytes : 0;
    }
}
