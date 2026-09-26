using System.Collections.Generic;

namespace FolderMorpher.Contracts;

public sealed class AppSettingsDto
{
    public string CacheReadPath { get; set; } = string.Empty;
    public bool FallbackToLocalOnReadError { get; set; } = true;
    public int WriteMode { get; set; }
    public string CacheWriteCustomPath { get; set; } = string.Empty;
    public string Language { get; set; } = "ja";
    public List<string> StorageTabPaths { get; set; } = new();
    public int ActiveStorageTabIndex { get; set; }
}
