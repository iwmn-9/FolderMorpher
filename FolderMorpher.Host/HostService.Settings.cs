using System;
using System.Linq;
using System.Threading.Tasks;
using FolderMorpher.Contracts;
using FolderMorpher.Services;

namespace FolderMorpher.Host;

public partial class HostService
{
    public Task<AppSettingsDto> GetAppSettingsAsync()
    {
        var source = AppSettingsService.Instance.Current;
        return Task.FromResult(new AppSettingsDto
        {
            CacheReadPath = source.CacheReadPath,
            FallbackToLocalOnReadError = source.FallbackToLocalOnReadError,
            WriteMode = (int)source.WriteMode,
            CacheWriteCustomPath = source.CacheWriteCustomPath,
            Language = source.Language,
            StorageTabPaths = source.StorageTabPaths.ToList(),
            ActiveStorageTabIndex = source.ActiveStorageTabIndex,
            ActiveScopePath = source.ActiveScopePath,
            RecentScopePaths = source.RecentScopePaths.ToList(),
            CloseHostOnWindowClose = source.CloseHostOnWindowClose,
            AdminSidebarCollapsed = source.AdminSidebarCollapsed,
            ScanDurationsSeconds = new(source.ScanDurationsSeconds ?? new()),
            SearchHistory = source.SearchHistory?.ToList() ?? new()
        });
    }

    public Task SaveAppSettingsAsync(AppSettingsDto settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(typeof(CacheWriteMode), settings.WriteMode))
            throw new ArgumentOutOfRangeException(nameof(settings.WriteMode));
        var target = AppSettingsService.Instance.Current;
        target.CacheReadPath = settings.CacheReadPath ?? string.Empty;
        target.FallbackToLocalOnReadError = settings.FallbackToLocalOnReadError;
        target.WriteMode = (CacheWriteMode)settings.WriteMode;
        target.CacheWriteCustomPath = settings.CacheWriteCustomPath ?? string.Empty;
        target.Language = settings.Language == "en" ? "en" : "ja";
        target.StorageTabPaths = settings.StorageTabPaths?.Where(path => !string.IsNullOrWhiteSpace(path)).ToList() ?? new();
        target.ActiveStorageTabIndex = Math.Max(0, settings.ActiveStorageTabIndex);
        target.ActiveScopePath = settings.ActiveScopePath ?? string.Empty;
        target.RecentScopePaths = settings.RecentScopePaths?.Where(path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList() ?? new();
        target.CloseHostOnWindowClose = settings.CloseHostOnWindowClose;
        target.AdminSidebarCollapsed = settings.AdminSidebarCollapsed;
        target.ScanDurationsSeconds = (settings.ScanDurationsSeconds ?? new())
            .Where(entry => entry.Key.Length <= 1024 && double.IsFinite(entry.Value) && entry.Value > 0 && entry.Value <= 604800)
            .Take(32)
            .ToDictionary(entry => entry.Key, entry => entry.Value);
        target.SearchHistory = settings.SearchHistory?
            .Where(query => !string.IsNullOrWhiteSpace(query))
            .Select(query => query.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList() ?? new();
        AppSettingsService.Instance.Save();
        return Task.CompletedTask;
    }

    public Task SetLanguageAsync(string language)
    {
        LocalizationService.Instance.SetLanguage(language == "en" ? AppLanguage.English : AppLanguage.Japanese);
        return Task.CompletedTask;
    }
}
