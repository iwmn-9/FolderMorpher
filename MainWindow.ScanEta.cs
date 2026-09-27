using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using FolderMorpher.Services;

namespace AstraSize;

public partial class MainWindow
{
    private sealed class ScanEtaSession
    {
        public required string HistoryKey { get; init; }
        public required string Operation { get; init; }
        public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
        public double? PreviousSeconds { get; init; }
        public long? ExpectedItems { get; set; }
        public bool HasExactTotal { get; set; }
        public long CompletedItems { get; set; }
        public int DiscoveredDirectories { get; set; }
        public int ProcessedDirectories { get; set; }
        public bool IsNetworkScope { get; init; }
        public DateTime? DisplayedDeadline { get; set; }
        public string EstimateSource { get; set; } = string.Empty;
    }

    private readonly List<ScanEtaSession> _scanEtaSessions = new();
    private ScanEtaSession? _reverseAclScanEta;
    private DispatcherTimer? _scanEtaTimer;

    private ScanEtaSession BeginScanEta(string operation, string scope, long? expectedItems = null)
    {
        var key = $"{operation}|{scope.Trim().TrimEnd('\\', '/').ToUpperInvariant()}";
        var history = AppSettingsService.Instance.Current.ScanDurationsSeconds;
        var session = new ScanEtaSession
        {
            HistoryKey = key,
            Operation = operation,
            PreviousSeconds = history != null && history.TryGetValue(key, out double seconds) &&
                double.IsFinite(seconds) && seconds > 0 ? seconds : null,
            ExpectedItems = expectedItems > 0 ? expectedItems : null,
            IsNetworkScope = PathCanonicalizer.IsNetworkPath(scope)
        };
        _scanEtaSessions.Add(session);
        _scanEtaTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _scanEtaTimer.Tick -= RefreshScanEtaOnTick;
        _scanEtaTimer.Tick += RefreshScanEtaOnTick;
        _scanEtaTimer.Start();
        RefreshScanEta();
        return session;
    }

    private void UpdateScanEtaTotal(ScanEtaSession? session, long? expectedItems, bool exact = false)
    {
        if (session == null || !_scanEtaSessions.Contains(session) || expectedItems <= 0) return;
        session.ExpectedItems = expectedItems;
        session.HasExactTotal = exact;
        RefreshScanEta();
    }

    private void ReportScanEta(ScanEtaSession? session, long completedItems,
        int processedDirectories = 0, int discoveredDirectories = 0)
    {
        if (session == null || !_scanEtaSessions.Contains(session)) return;
        session.CompletedItems = Math.Max(session.CompletedItems, completedItems);
        session.ProcessedDirectories = Math.Max(session.ProcessedDirectories, processedDirectories);
        session.DiscoveredDirectories = Math.Max(session.DiscoveredDirectories, discoveredDirectories);
        RefreshScanEta();
    }

    private void FinishScanEta(ScanEtaSession? session, bool completed)
    {
        if (session == null || !_scanEtaSessions.Remove(session)) return;
        session.Elapsed.Stop();
        if (completed && session.Elapsed.Elapsed.TotalSeconds >= 2 && session.HistoryKey.Length <= 1024)
        {
            var settings = AppSettingsService.Instance.Current;
            settings.ScanDurationsSeconds ??= new Dictionary<string, double>();
            settings.ScanDurationsSeconds.Remove(session.HistoryKey);
            settings.ScanDurationsSeconds[session.HistoryKey] = session.Elapsed.Elapsed.TotalSeconds;
            while (settings.ScanDurationsSeconds.Count > 32)
                settings.ScanDurationsSeconds.Remove(settings.ScanDurationsSeconds.Keys.First());
            AppSettingsService.Instance.Save();
        }
        if (_scanEtaSessions.Count == 0) _scanEtaTimer?.Stop();
        RefreshScanEta();
    }

    private void RefreshScanEtaOnTick(object? sender, EventArgs e) => RefreshScanEta();

    private void RefreshScanEta()
    {
        if (SearchEtaText == null) return;
        RenderScanEta(StorageEtaText, _scanEtaSessions.LastOrDefault(s => s.Operation == "storage"), true);
        RenderScanEta(AuditEtaText, _scanEtaSessions.LastOrDefault(s => s.Operation.StartsWith("audit-", StringComparison.Ordinal)), true);
        RenderScanEta(MediaEtaText, _scanEtaSessions.LastOrDefault(s => s.Operation == "media-scan"), true);
        RenderScanEta(LinkEtaText, _scanEtaSessions.LastOrDefault(s => s.Operation.StartsWith("link-", StringComparison.Ordinal)), true);
        RenderScanEta(LiveAclStudioControl.ReverseScanEtaText,
            _scanEtaSessions.LastOrDefault(s => s.Operation == "acl-reverse"), true);
        RenderScanEta(SearchEtaText, _scanEtaSessions.LastOrDefault(s => s.Operation.StartsWith("search-", StringComparison.Ordinal)), true);
        SearchEtaBadge.Visibility = SearchEtaText.Visibility;
    }

    private void RenderScanEta(TextBlock target, ScanEtaSession? session, bool compact)
    {
        if (session == null)
        {
            target.Visibility = Visibility.Collapsed;
            return;
        }

        double elapsed = session.Elapsed.Elapsed.TotalSeconds;
        double? remaining = null;
        string source = string.Empty;
        if (session.ExpectedItems is > 0 and var total && session.CompletedItems < total &&
            session.CompletedItems >= 200 && elapsed >= 15 &&
            (double)session.CompletedItems / total >= 0.05 &&
            (session.HasExactTotal || (double)session.CompletedItems / total <= 0.75))
        {
            // A cached count may be stale, and the first fast burst does not represent
            // the slow tail (Office/PDF extraction, large files, access latency).
            double factor = session.HasExactTotal ? 1.4 : session.Operation == "storage" ? 1.8 : 2.8;
            remaining = elapsed * (total - session.CompletedItems) / session.CompletedItems * factor;
            source = "measured";
        }
        else if (elapsed >= 12 && session.PreviousSeconds is double previous && previous > elapsed + 3)
        {
            remaining = Math.Max(previous * 2.2 - elapsed, previous - elapsed);
            source = "history";
        }
        else if (elapsed >= 20 && session.CompletedItems >= 500)
        {
            double rate = session.CompletedItems / elapsed;
            // Unknown total: keep a deliberately broad upper-side estimate.
            // Avoid locking a short deadline from a fast startup sample.
            double assumedTotal = Math.Max(session.CompletedItems * 32.0,
                session.IsNetworkScope ? 1_000_000 : 250_000);
            remaining = Math.Max(session.IsNetworkScope ? 3600 : 1200,
                (assumedTotal - session.CompletedItems) / rate * 2.5);
            if (session.ProcessedDirectories >= 4 && session.DiscoveredDirectories > session.ProcessedDirectories)
            {
                double pendingDirectories = session.DiscoveredDirectories - session.ProcessedDirectories;
                remaining = Math.Max(remaining.Value, elapsed / session.ProcessedDirectories * pendingDirectories * 16);
            }
            source = "first-run";
        }
        else if (elapsed >= 30 && session.CompletedItems == 0 &&
            (session.Operation == "media-scan" || session.Operation.StartsWith("link-", StringComparison.Ordinal) ||
             session.Operation == "acl-reverse"))
        {
            // These older RPCs report status text but no numeric work units. Until the
            // contract exposes counts, show a deliberately broad first-run estimate.
            remaining = Math.Max(session.IsNetworkScope ? 7200 : 1800, elapsed * 24);
            source = "first-run";
        }

        if (remaining is double seconds && double.IsFinite(seconds) && seconds >= 3)
        {
            var candidate = DateTime.Now.AddSeconds(Math.Min(seconds, 604800));
            if (session.DisplayedDeadline == null || candidate < session.DisplayedDeadline)
            {
                session.DisplayedDeadline = candidate;
                session.EstimateSource = source;
            }
        }

        if (session.DisplayedDeadline is DateTime deadline)
        {
            double displaySeconds = (deadline - DateTime.Now).TotalSeconds;
            if (displaySeconds <= 0)
            {
                target.Text = UiText("見込み時刻を超えて走査中", "Scanning beyond estimate");
            }
            else
            {
                string duration;
                if (displaySeconds < 60)
                {
                    double roundedSeconds = Math.Ceiling(displaySeconds / 5) * 5;
                    duration = UiText($"約{roundedSeconds:0}秒", $"~{roundedSeconds:0}s");
                }
                else if (displaySeconds < 3600)
                {
                    double minutes = Math.Ceiling(displaySeconds / 60);
                    duration = UiText($"約{minutes:0}分", $"~{minutes:0}m");
                }
                else if (displaySeconds < 86400)
                {
                    int tenMinuteBlocks = (int)Math.Ceiling(displaySeconds / 600);
                    int hours = tenMinuteBlocks / 6;
                    int minutes = tenMinuteBlocks % 6 * 10;
                    duration = minutes == 0
                        ? UiText($"約{hours}時間", $"~{hours}h")
                        : UiText($"約{hours}時間{minutes}分", $"~{hours}h {minutes}m");
                }
                else
                {
                    int days = (int)Math.Ceiling(displaySeconds / 86400);
                    duration = UiText($"約{days}日", $"~{days}d");
                }
                target.Text = compact
                    ? UiText($"残り{duration}", $"{duration} left")
                    : UiText($"完了目安 {deadline:HH:mm}（残り{duration}）", $"ETA {deadline:HH:mm} ({duration} left)");
            }
            target.ToolTip = session.EstimateSource switch
            {
                "measured" => UiText("前回の件数と今回の処理速度から算出した概算です。残り時間の表示は増やさず、早まる時だけ更新します。",
                    "Approximation from previous item count and current throughput. The displayed remaining time only moves down."),
                "history" => UiText("同じ場所で行った前回の所要時間をもとにした概算です。対象や処理内容の変化で前後します。",
                    "Approximation based on the previous run at this location. Changes in content or workload can shift it."),
                _ => UiText("初回走査の暫定的な概算です。総件数は未確定で、対象規模によって大きく外れる場合があります。見積りのための追加走査はしていません。",
                    "Provisional first-run estimate without a known total. Workload size may differ substantially. No extra pre-scan was performed.")
            };
        }
        else
        {
            target.Text = UiText("見積もり中…", "Estimating…");
            target.ToolTip = UiText("走査の進み具合を観測しています。事前の全件走査は行いません。",
                "Observing scan progress without an extra pre-scan.");
        }
        target.Visibility = Visibility.Visible;
    }
}
