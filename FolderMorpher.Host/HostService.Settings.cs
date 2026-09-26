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
            ActiveStorageTabIndex = source.ActiveStorageTabIndex
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
        AppSettingsService.Instance.Save();
        return Task.CompletedTask;
    }

    public Task SetLanguageAsync(string language)
    {
        LocalizationService.Instance.SetLanguage(language == "en" ? AppLanguage.English : AppLanguage.Japanese);
        return Task.CompletedTask;
    }
}
