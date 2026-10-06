namespace FolderMorpher.Contracts;

public sealed class FolderSnapshotDto
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
}

public sealed class ScanSnapshotDto
{
    public string Id { get; set; } = string.Empty;
    public string TargetPath { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public long TotalBytes { get; set; }
    public int TotalFiles { get; set; }
    public List<FolderSnapshotDto> SubFolders { get; set; } = new();
}
