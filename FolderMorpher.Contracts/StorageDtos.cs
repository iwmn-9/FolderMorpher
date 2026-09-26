namespace FolderMorpher.Contracts;

/// <summary>Tree data transferred over IPC. No parent link or presentation state.</summary>
public sealed class StorageNodeDto
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public int FileCount { get; set; }
    public int FolderCount { get; set; }
    public bool IsDirectory { get; set; }
    public bool IsRoot { get; set; }
    public double PercentageOfRoot { get; set; }
    public DateTime? LastModified { get; set; }
    public DateTime? CreationTime { get; set; }
    public string? Sha256 { get; set; }
    public string? ErrorMessage { get; set; }
    public long? DiffBytes { get; set; }
    public bool HasUnloadedChildren { get; set; }
    public List<StorageTopFileDto> CachedTopFiles { get; set; } = new();
    public List<StorageExtensionStatDto> CachedExtensionStats { get; set; } = new();
    public List<StorageNodeDto> Children { get; set; } = new();
}

public sealed class StorageTopFileDto
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long SizeBytes { get; set; }
    public string Extension { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
}

public sealed class StorageExtensionStatDto
{
    public string Extension { get; set; } = string.Empty;
    public long TotalSizeBytes { get; set; }
    public int FileCount { get; set; }
    public double Percentage { get; set; }
}
