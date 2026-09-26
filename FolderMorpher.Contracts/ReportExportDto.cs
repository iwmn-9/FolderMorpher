namespace FolderMorpher.Contracts;

public sealed class ReportExportDto
{
    public string OutputPath { get; set; } = string.Empty;
    public string ScannedRoot { get; set; } = string.Empty;
    public AuditSummaryDto? AuditSummary { get; set; }
    public List<AuditItemDto> AuditItems { get; set; } = new();
    public MediaOptimizeSummaryDto? MediaSummary { get; set; }
    public List<MediaItemDto> MediaItems { get; set; } = new();
}
