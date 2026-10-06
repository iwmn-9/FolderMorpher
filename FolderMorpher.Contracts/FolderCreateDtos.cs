namespace FolderMorpher.Contracts;

public sealed class FolderCreatePlanDto
{
    public Guid PlanId { get; set; }
    public string ParentPath { get; set; } = string.Empty;
    public string FolderName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
}
