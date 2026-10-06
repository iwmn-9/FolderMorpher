using FolderMorpher.Models;

namespace FolderMorpher.Services;

public static class WaveDisplayFormat
{
    public static string FormatFileCount(long? count)
    {
        bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
        return count.HasValue
            ? (isJa ? $"{count.Value:N0} 件" : $"{count.Value:N0} files")
            : (isJa ? "未計測 (-)" : "Unmeasured (-)");
    }

    public static string FormatDuration(TimeSpan span)
    {
        if (span.TotalDays >= 1.0) return $"{(int)span.TotalDays}d {span.Hours}h {span.Minutes}m";
        if (span.TotalHours >= 1.0) return $"{(int)span.TotalHours}h {span.Minutes}m";
        if (span.TotalMinutes >= 1.0) return $"{(int)span.TotalMinutes}m {span.Seconds}s";
        return $"{Math.Max(1, (int)span.TotalSeconds)}s";
    }
}
