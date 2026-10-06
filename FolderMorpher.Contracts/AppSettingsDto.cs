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
    public string ActiveScopePath { get; set; } = string.Empty;
    public List<string> RecentScopePaths { get; set; } = new();
    public bool CloseHostOnWindowClose { get; set; }
    public bool AdminSidebarCollapsed { get; set; } = true;
    public Dictionary<string, double> ScanDurationsSeconds { get; set; } = new();
    public List<string> SearchHistory { get; set; } = new();
}
