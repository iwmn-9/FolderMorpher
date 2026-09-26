namespace AstraSize.Models;

public partial class ScanSnapshot
{
    public string FormattedSize => FileItemNode.FormatBytes(TotalBytes);
    public string FormattedDate => Timestamp.ToString("yyyy/MM/dd HH:mm");
}

public partial class TrendComparison
{
    public string FormattedDiff => (DiffBytes > 0 ? "+" : "") + FileItemNode.FormatBytes(DiffBytes);
    public string DiffSign => DiffBytes > 0 ? "▲ 増加" : DiffBytes < 0 ? "▼ 減少" : "±0 変化なし";
    public string DiffColor => DiffBytes > 0 ? "#E11D48" : DiffBytes < 0 ? "#10B981" : "#64748B";
}
