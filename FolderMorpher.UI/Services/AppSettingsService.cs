using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using FolderMorpher.Contracts;
using FolderMorpher.HostClient;

namespace FolderMorpher.Services;

public enum CacheWriteMode { Local = 0, SameAsRead = 1, Custom = 2 }

public sealed class AppSettingsService
{
    public static AppSettingsService Instance { get; } = new();
    public AppSettingsDto Current { get; private set; } = new();
    private readonly object _saveLock = new();
    private Task _pendingSave = Task.CompletedTask;

    public async Task LoadAsync()
    {
        var host = await FolderMorpherHostClient.Instance.GetServiceAsync();
        Current = await host.GetAppSettingsAsync();
    }

    public void Save()
    {
        var source = Current;
        var snapshot = new AppSettingsDto
        {
            CacheReadPath = source.CacheReadPath,
            FallbackToLocalOnReadError = source.FallbackToLocalOnReadError,
            WriteMode = source.WriteMode,
            CacheWriteCustomPath = source.CacheWriteCustomPath,
            Language = source.Language,
            StorageTabPaths = source.StorageTabPaths.ToList(),
            ActiveStorageTabIndex = source.ActiveStorageTabIndex
        };
        lock (_saveLock)
        {
            _pendingSave = SaveAfterPreviousAsync(_pendingSave, snapshot);
        }
    }

    private static async Task SaveAfterPreviousAsync(Task previous, AppSettingsDto snapshot)
    {
        try { await previous; }
        catch (Exception ex) { Debug.WriteLine($"Previous settings save failed: {ex}"); }
        var host = await FolderMorpherHostClient.Instance.GetServiceAsync();
        await host.SaveAppSettingsAsync(snapshot);
    }

    public Task FlushAsync()
    {
        lock (_saveLock) return _pendingSave;
    }
}
