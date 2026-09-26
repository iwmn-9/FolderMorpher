namespace FolderMorpher.Contracts;

public sealed class SimulationProjectDto
{
    public string Version { get; set; } = "2.0";
    public string AppName { get; set; } = "FolderMorpher";
    public string ProjectName { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime LastModifiedAt { get; set; }
    public string SourceRootPath { get; set; } = string.Empty;
    public string TargetRootPath { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public List<MigrationNodeDto> RootFolders { get; set; } = new();
}
