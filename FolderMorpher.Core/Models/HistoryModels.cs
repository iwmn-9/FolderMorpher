using System;
using System.Collections.Generic;

namespace AstraSize.Models
{
    public class FolderSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
    }

    public partial class ScanSnapshot
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string TargetPath { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public long TotalBytes { get; set; }
        public int TotalFiles { get; set; }
        public List<FolderSnapshot> SubFolders { get; set; } = new();

    }

    public partial class TrendComparison
    {
        public ScanSnapshot? PreviousSnapshot { get; set; }
        public long DiffBytes { get; set; }
        public int DiffFiles { get; set; }
    }
}
