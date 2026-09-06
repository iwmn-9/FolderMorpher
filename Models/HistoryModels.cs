using System;
using System.Collections.Generic;

namespace AstraSize.Models
{
    public class FolderSnapshot
    {
        public string Name { get; set; } = string.Empty;
        public long Size { get; set; }
    }

    public class ScanSnapshot
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string TargetPath { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public long TotalBytes { get; set; }
        public int TotalFiles { get; set; }
        public List<FolderSnapshot> SubFolders { get; set; } = new();

        public string FormattedSize => FileItemNode.FormatBytes(TotalBytes);
        public string FormattedDate => Timestamp.ToString("yyyy/MM/dd HH:mm");
    }

    public class TrendComparison
    {
        public ScanSnapshot? PreviousSnapshot { get; set; }
        public long DiffBytes { get; set; }
        public int DiffFiles { get; set; }
        public string FormattedDiff => (DiffBytes > 0 ? "+" : "") + FileItemNode.FormatBytes(DiffBytes);
        public string DiffSign => DiffBytes > 0 ? "▲ 増加" : DiffBytes < 0 ? "▼ 減少" : "±0 変化なし";
        public string DiffColor => DiffBytes > 0 ? "#E11D48" : DiffBytes < 0 ? "#10B981" : "#64748B"; // Rose for increase, Emerald for freed
    }
}
