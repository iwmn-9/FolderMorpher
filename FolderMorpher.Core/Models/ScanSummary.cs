using System;
using System.Collections.Generic;
using FolderMorpher.Services;

namespace AstraSize.Models
{
    public partial class LargestFileInfo
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Extension { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;

    }

    public partial class ExtensionStat
    {
        public string Extension { get; set; } = string.Empty;
        public long TotalSize { get; set; }
        public int FileCount { get; set; }
        public double Percentage { get; set; }
    }

    public partial class CleanupCandidate
    {
        public string Name { get; set; } = string.Empty;
        public string FullPath { get; set; } = string.Empty;
        public long Size { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public partial class ScanSummary
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

    }
}
