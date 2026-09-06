using System;
using System.Collections.Generic;

namespace AstraSize.Models
{
    public class LargestFileInfo
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public string FormattedSize => FileItemNode.FormatBytes(Size);
        public string Extension { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
    }

    public class ExtensionStat
    {
        public string Extension { get; set; } = string.Empty;
        public long TotalSize { get; set; }
        public int FileCount { get; set; }
        public double Percentage { get; set; }
        public string FormattedSize => FileItemNode.FormatBytes(TotalSize);
        public string PercentageFormatted => $"{Percentage:F1}%";
    }

    public class CleanupCandidate
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public string FormattedSize => FileItemNode.FormatBytes(Size);
        public string Reason { get; set; } = string.Empty;
    }

    public class ScanSummary
    {
        public string TargetPath { get; set; } = string.Empty;
        public long TotalBytes { get; set; }
        public int TotalFiles { get; set; }
        public int TotalFolders { get; set; }
        public double ElapsedSeconds { get; set; }
        public bool IsCancelled { get; set; }
        public string ScanMode { get; set; } = "Standard";
        public bool IsMftBoosted => ScanMode.Contains("MFT", StringComparison.OrdinalIgnoreCase);

        public List<LargestFileInfo> LargestFiles { get; set; } = new();
        public List<ExtensionStat> ExtensionStats { get; set; } = new();
        public List<CleanupCandidate> CleanupCandidates { get; set; } = new();

        public long CleanupTotalBytes
        {
            get
            {
                long sum = 0;
                foreach (var c in CleanupCandidates) sum += c.Size;
                return sum;
            }
        }

        public string FormattedCleanupTotal => FileItemNode.FormatBytes(CleanupTotalBytes);
        public string FormattedTotalSize => FileItemNode.FormatBytes(TotalBytes);
    }
}
