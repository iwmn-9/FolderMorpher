using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using FolderMorpher.Services;

namespace AstraSize;

public partial class MainWindow
{
    private sealed class ScanEtaSession
    {
        public required string HistoryKey { get; init; }
        public Stopwatch Elapsed { get; } = Stopwatch.StartNew();
        public double? PreviousSeconds { get; init; }
        public long? ExpectedItems { get; set; }
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
            PreviousSeconds = history != null && history.TryGetValue(key, out double seconds) &&
                double.IsFinite(seconds) && seconds > 0 ? seconds : null,
            ExpectedItems = expectedItems > 0 ? expectedItems : null,
            IsNetworkScope = scope.StartsWith(@"\\", StringComparison.Ordinal)
        };
        _scanEtaSessions.Add(session);
        _scanEtaTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _scanEtaTimer.Tick -= RefreshScanEtaOnTick;
        _scanEtaTimer.Tick += RefreshScanEtaOnTick;
        _scanEtaTimer.Start();
        RefreshScanEta();
        return session;
    }

    private void UpdateScanEtaTotal(ScanEtaSession? session, long? expectedItems)
    {
        if (session == null || !_scanEtaSessions.Contains(session) || expectedItems <= 0) return;
        session.ExpectedItems = expectedItems;
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
        if (ScanEtaText == null) return;
        if (_scanEtaSessions.Count == 0)
        {
            ScanEtaText.Visibility = Visibility.Collapsed;
            return;
        }

        var session = _scanEtaSessions[^1];
        double elapsed = session.Elapsed.Elapsed.TotalSeconds;
        double? remaining = null;
        string source = string.Empty;
        if (session.ExpectedItems is > 0 and var total &&
            session.CompletedItems >= 50 && session.CompletedItems < total &&
            elapsed >= 5 && (double)session.CompletedItems / total >= 0.05)
        {
            remaining = elapsed * (total - session.CompletedItems) / session.CompletedItems * 1.25;
            source = "measured";
        }
        else if (elapsed >= 6 && session.PreviousSeconds is double previous && previous > elapsed + 3)
        {
            remaining = (previous - elapsed) * 1.3;
            source = "history";
        }
        else if (elapsed >= 8 && session.CompletedItems >= 50)
        {
            // With no known total, this is deliberately a broad first-run forecast.
            // It uses only work already performed by the actual scan; no pre-scan is started.
            double rate = session.CompletedItems / elapsed;
            double assumedTotal = Math.Max(session.CompletedItems * 8.0, session.IsNetworkScope ? 200_000 : 50_000);
            remaining = Math.Max(session.IsNetworkScope ? 1800 : 600,
                (assumedTotal - session.CompletedItems) / rate * 1.5);
            if (session.ProcessedDirectories >= 4 && session.DiscoveredDirectories > session.ProcessedDirectories)
            {
                double pendingDirectories = session.DiscoveredDirectories - session.ProcessedDirectories;
                remaining = Math.Max(remaining.Value, elapsed / session.ProcessedDirectories * pendingDirectories * 8);
            }
            source = "first-run";
        }
        else if (elapsed >= 12)
        {
            // Some scanners currently report status text only. Give a cautious first
            // estimate once the operation has demonstrably continued past startup.
            remaining = Math.Max(session.IsNetworkScope ? 1800 : 600, elapsed * 16);
            source = "first-run-status";
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
                ScanEtaText.Text = UiText("見込み時刻を超えて走査中", "Scanning beyond estimate");
            }
            else
            {
                string duration = displaySeconds < 60
                    ? UiText($"約{Math.Ceiling(displaySeconds / 5) * 5:0}秒", $"~{Math.Ceiling(displaySeconds / 5) * 5:0}s")
                    : UiText($"約{Math.Ceiling(displaySeconds / 60):0}分", $"~{Math.Ceiling(displaySeconds / 60):0}m");
                ScanEtaText.Text = UiText($"完了目安 {deadline:HH:mm}（残り{duration}）",
                    $"ETA {deadline:HH:mm} ({duration} left)");
            }
            ScanEtaText.ToolTip = session.EstimateSource switch
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
            ScanEtaText.Text = UiText("見積もり中…", "Estimating…");
            ScanEtaText.ToolTip = UiText("走査の進み具合を観測しています。事前の全件走査は行いません。",
                "Observing scan progress without an extra pre-scan.");
        }
        ScanEtaText.Visibility = Visibility.Visible;
    }
}
