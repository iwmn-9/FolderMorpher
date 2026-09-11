using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AstraSize.Models;
using FolderMorpher.Services;

namespace AstraSize
{
    public class HistoryRowItem
    {
        public ScanSnapshot Snapshot { get; set; } = null!;
        public string FormattedDate => Snapshot.FormattedDate;
        public string FormattedSize => Snapshot.FormattedSize;
        public string FormattedFiles => $"{Snapshot.TotalFiles:N0} 項目";
        public string FormattedDiff { get; set; } = "―";
        public Brush DiffBrush { get; set; } = Brushes.SlateGray;
        public Brush StatusDotBrush { get; set; } = new SolidColorBrush(Color.FromRgb(2, 132, 199)); // Sky-600
        public Visibility BadgeVisibility { get; set; } = Visibility.Collapsed;
        public string BadgeText { get; set; } = string.Empty;
        public Brush BadgeBackgroundBrush { get; set; } = Brushes.Transparent;
        public Brush BadgeForegroundBrush { get; set; } = Brushes.Transparent;
        public AnomalyDetectionPoint? AnomalyData { get; set; }
    }

    public partial class HistoryWindow : Window
    {
        private readonly string _targetPath;
        private readonly List<ScanSnapshot> _history;
        private long _targetThresholdBytes;
        private StorageForecastReport? _currentReport;
        private List<HistoryRowItem> _rowItems = new();

        public HistoryWindow(string targetPath, List<ScanSnapshot> history)
        {
            InitializeComponent();
            _targetPath = targetPath ?? string.Empty;
            _history = history ?? new List<ScanSnapshot>();

            TargetPathText.Text = $"対象パス: {_targetPath}";

            // 初期閾値: 最新スナップショットの 120% (最低でも +10GB)
            long latestBytes = _history.Count > 0 ? _history.Max(h => h.TotalBytes) : 10L * 1024 * 1024 * 1024;
            _targetThresholdBytes = Math.Max(latestBytes + 10L * 1024L * 1024L * 1024L, (long)(latestBytes * 1.2));
            ThresholdInputTextBox.Text = FormatBytesToInputString(_targetThresholdBytes);

            ApplyLocalization();

            if (_history.Count == 0)
            {
                EmptyHistoryText.Visibility = Visibility.Visible;
                NoChartDataText.Visibility = Visibility.Visible;
                ChartStatsTextBlock.Text = "";
                ForecastSummaryFooterText.Text = Strings.HistoryNoData;
            }
            else
            {
                RecalculateAndRender();
            }
        }

        private void ApplyLocalization()
        {
            Title = Strings.HistoryWindowTitle;
            HistoryWindowTitleText.Text = Strings.HistoryWindowTitle;
            ChartTitleTextBlock.Text = Strings.HistoryChartTitle;
            NoChartDataText.Text = Strings.HistoryRequireTwoScans;
            EmptyHistoryText.Text = Strings.HistoryNoData;
            TargetThresholdLabel.Text = Strings.HistoryTargetThreshold;
            InspectorTitleText.Text = Strings.HistoryContributorsTitle;
            CloseButton.Content = Strings.Close;
        }

        private void RecalculateAndRender()
        {
            if (_history.Count == 0) return;

            // 数理解析エンジンの実行
            _currentReport = StorageForecastingService.Instance.Analyze(_history, _targetThresholdBytes);

            // 行 ViewModel リストの生成
            BuildHistoryRows();

            // チャートの描画
            RenderTrendChart();

            // サマリーテキストの更新
            UpdateSummaryText();

            // 異常または最新スナップショットを初期選択
            SelectInitialRow();
        }

        private void BuildHistoryRows()
        {
            var sorted = _history.OrderByDescending(h => h.Timestamp).ToList();
            _rowItems = new List<HistoryRowItem>();

            var anomalyMap = _currentReport?.AnomalyPoints
                .ToDictionary(a => a.Snapshot.Id, a => a) ?? new Dictionary<string, AnomalyDetectionPoint>();

            for (int i = 0; i < sorted.Count; i++)
            {
                var curr = sorted[i];
                var prev = i + 1 < sorted.Count ? sorted[i + 1] : null;

                var row = new HistoryRowItem
                {
                    Snapshot = curr
                };

                if (prev != null)
                {
                    long diff = curr.TotalBytes - prev.TotalBytes;
                    row.FormattedDiff = (diff > 0 ? "+" : "") + FileItemNode.FormatBytes(diff);
                    row.DiffBrush = diff > 0
                        ? new SolidColorBrush(Color.FromRgb(225, 29, 72))  // Rose-600
                        : diff < 0
                            ? new SolidColorBrush(Color.FromRgb(16, 185, 129)) // Emerald-500
                            : new SolidColorBrush(Color.FromRgb(100, 116, 139)); // Slate-500
                }
                else
                {
                    row.FormattedDiff = Strings.InitialScan;
                    row.DiffBrush = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                }

                // 異常検知判定の確認
                if (anomalyMap.TryGetValue(curr.Id, out var anomaly) && anomaly.IsAnomaly)
                {
                    row.AnomalyData = anomaly;
                    row.StatusDotBrush = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber-500
                    row.BadgeVisibility = Visibility.Visible;
                    row.BadgeText = Strings.HistoryAnomalyDetected;
                    row.BadgeBackgroundBrush = new SolidColorBrush(Color.FromArgb(30, 245, 158, 11));
                    row.BadgeForegroundBrush = new SolidColorBrush(Color.FromRgb(180, 83, 9)); // Amber-700
                }
                else
                {
                    row.BadgeVisibility = Visibility.Collapsed;
                }

                _rowItems.Add(row);
            }

            HistoryItemsControl.ItemsSource = _rowItems;
        }

        private void RenderTrendChart()
        {
            if (TrendCanvas == null) return;
            TrendCanvas.Children.Clear();

            if (_history.Count < 2 || _currentReport == null)
            {
                NoChartDataText.Visibility = Visibility.Visible;
                ChartStatsTextBlock.Text = _history.Count == 1 ? $"記録 1 件: {_history[0].FormattedSize}" : "";
                return;
            }

            NoChartDataText.Visibility = Visibility.Collapsed;

            var sorted = _history.OrderBy(h => h.Timestamp).ToList();

            double width = TrendCanvas.ActualWidth;
            double height = TrendCanvas.ActualHeight;
            if (width <= 40 || height <= 40) return;

            long minBytes = sorted.Min(h => h.TotalBytes);
            long maxBytes = sorted.Max(h => h.TotalBytes);
            long effectiveMax = Math.Max(maxBytes, _targetThresholdBytes > 0 ? _targetThresholdBytes : maxBytes);

            // マージン計算
            long range = effectiveMax - minBytes;
            if (range == 0) range = Math.Max(1024, maxBytes / 10);
            long plotMin = Math.Max(0, minBytes - (long)(range * 0.08));
            long plotMax = effectiveMax + (long)(range * 0.08);
            long plotRange = Math.Max(1, plotMax - plotMin);

            double padLeft = 20;
            double padRight = 30;
            double padTop = 20;
            double padBottom = 26;
            double chartW = width - padLeft - padRight;
            double chartH = height - padTop - padBottom;

            DateTime t0 = sorted.First().Timestamp;
            DateTime tEnd = sorted.Last().Timestamp;
            double totalDays = Math.Max(1.0, (tEnd - t0).TotalDays);

            // 1. 水平グリッド線 (4本)
            for (int i = 0; i <= 3; i++)
            {
                double y = padTop + (chartH / 3.0) * i;
                var gridLine = new Line
                {
                    X1 = padLeft,
                    X2 = width - padRight,
                    Y1 = y,
                    Y2 = y,
                    Stroke = new SolidColorBrush(Color.FromRgb(241, 245, 249)),
                    StrokeThickness = 1
                };
                TrendCanvas.Children.Add(gridLine);
            }

            // 2. 目標上限閾値ライン (赤色破線)
            if (_targetThresholdBytes > 0 && _targetThresholdBytes >= plotMin && _targetThresholdBytes <= plotMax)
            {
                double normTargetY = (double)(_targetThresholdBytes - plotMin) / plotRange;
                double targetY = height - padBottom - (normTargetY * chartH);

                var targetLine = new Line
                {
                    X1 = padLeft,
                    X2 = width - padRight,
                    Y1 = targetY,
                    Y2 = targetY,
                    Stroke = new SolidColorBrush(Color.FromArgb(180, 239, 68, 68)), // Red-500
                    StrokeThickness = 1.8,
                    StrokeDashArray = new DoubleCollection { 3, 2 }
                };
                TrendCanvas.Children.Add(targetLine);

                var targetLabel = new TextBlock
                {
                    Text = $"上限: {FileItemNode.FormatBytes(_targetThresholdBytes)}",
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromRgb(220, 38, 38))
                };
                Canvas.SetLeft(targetLabel, width - padRight - 70);
                Canvas.SetTop(targetLabel, Math.Max(2, targetY - 14));
                TrendCanvas.Children.Add(targetLabel);
            }

            // 3. 実測データの座標計算
            var points = new PointCollection();
            var polyPoints = new PointCollection();
            polyPoints.Add(new Point(padLeft, height - padBottom));

            for (int i = 0; i < sorted.Count; i++)
            {
                double dayOffset = (sorted[i].Timestamp - t0).TotalDays;
                double normX = totalDays > 0 ? (dayOffset / totalDays) : ((double)i / (sorted.Count - 1));
                double x = padLeft + normX * chartW;

                double normY = (double)(sorted[i].TotalBytes - plotMin) / plotRange;
                double y = height - padBottom - (normY * chartH);

                var pt = new Point(x, y);
                points.Add(pt);
                polyPoints.Add(pt);
            }

            polyPoints.Add(new Point(padLeft + chartW, height - padBottom));

            // 4. 実測面塗りつぶし (Gradient Polygon)
            var areaBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            areaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(70, 2, 132, 199), 0.0));
            areaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(8, 2, 132, 199), 1.0));

            var polygon = new Polygon
            {
                Points = polyPoints,
                Fill = areaBrush
            };
            TrendCanvas.Children.Add(polygon);

            // 5. 実測折れ線 (Polyline)
            var polyline = new Polyline
            {
                Points = points,
                Stroke = new SolidColorBrush(Color.FromRgb(2, 132, 199)),
                StrokeThickness = 2.5
            };
            TrendCanvas.Children.Add(polyline);

            // 6. 一次線形回帰トレンド線 (紫の破線)
            var reg = _currentReport.LinearRegression;
            if (reg != null)
            {
                double y0Estimate = reg.Intercept;
                double yEndEstimate = reg.Intercept + reg.Slope * totalDays;

                double normRegY0 = (y0Estimate - plotMin) / plotRange;
                double normRegYEnd = (yEndEstimate - plotMin) / plotRange;

                double regY1 = height - padBottom - (normRegY0 * chartH);
                double regY2 = height - padBottom - (normRegYEnd * chartH);

                var regLine = new Line
                {
                    X1 = padLeft,
                    Y1 = regY1,
                    X2 = padLeft + chartW,
                    Y2 = regY2,
                    Stroke = new SolidColorBrush(Color.FromArgb(200, 139, 92, 246)), // Purple-500
                    StrokeThickness = 2.0,
                    StrokeDashArray = new DoubleCollection { 4, 3 }
                };
                TrendCanvas.Children.Add(regLine);
            }

            // 7. 実測プロット点 & MAD異常検知ピン
            var anomalyMap = _currentReport.AnomalyPoints
                .ToDictionary(a => a.Snapshot.Id, a => a);

            for (int i = 0; i < sorted.Count; i++)
            {
                var pt = points[i];
                var snap = sorted[i];
                bool isAnomaly = anomalyMap.TryGetValue(snap.Id, out var ad) && ad.IsAnomaly;

                if (isAnomaly)
                {
                    // ⚠️ MAD異常急増ピン (オレンジ色の警告ハイライト)
                    var outerHalo = new Ellipse
                    {
                        Width = 18,
                        Height = 18,
                        Fill = new SolidColorBrush(Color.FromArgb(60, 245, 158, 11)),
                        Stroke = new SolidColorBrush(Color.FromArgb(160, 245, 158, 11)),
                        StrokeThickness = 1.5,
                        Cursor = Cursors.Hand
                    };
                    Canvas.SetLeft(outerHalo, pt.X - 9);
                    Canvas.SetTop(outerHalo, pt.Y - 9);
                    outerHalo.Tag = snap;
                    outerHalo.MouseLeftButtonUp += AnomalyPin_MouseLeftButtonUp;
                    TrendCanvas.Children.Add(outerHalo);

                    var dot = new Ellipse
                    {
                        Width = 9,
                        Height = 9,
                        Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11)), // Amber-500
                        Stroke = Brushes.White,
                        StrokeThickness = 2,
                        Cursor = Cursors.Hand
                    };
                    Canvas.SetLeft(dot, pt.X - 4.5);
                    Canvas.SetTop(dot, pt.Y - 4.5);
                    dot.Tag = snap;
                    dot.MouseLeftButtonUp += AnomalyPin_MouseLeftButtonUp;
                    TrendCanvas.Children.Add(dot);

                    var warnLabel = new TextBlock
                    {
                        Text = "⚠️",
                        FontSize = 11,
                        Cursor = Cursors.Hand
                    };
                    Canvas.SetLeft(warnLabel, pt.X - 6);
                    Canvas.SetTop(warnLabel, pt.Y - 22);
                    warnLabel.Tag = snap;
                    warnLabel.MouseLeftButtonUp += AnomalyPin_MouseLeftButtonUp;
                    TrendCanvas.Children.Add(warnLabel);
                }
                else
                {
                    // 通常プロット点
                    var dot = new Ellipse
                    {
                        Width = 7,
                        Height = 7,
                        Fill = Brushes.White,
                        Stroke = new SolidColorBrush(Color.FromRgb(2, 132, 199)),
                        StrokeThickness = 2,
                        Cursor = Cursors.Hand
                    };
                    Canvas.SetLeft(dot, pt.X - 3.5);
                    Canvas.SetTop(dot, pt.Y - 3.5);
                    dot.Tag = snap;
                    dot.MouseLeftButtonUp += NormalDot_MouseLeftButtonUp;
                    TrendCanvas.Children.Add(dot);
                }

                // 最初、最後、または少数計測時の容量ラベル
                if (i == 0 || i == sorted.Count - 1 || sorted.Count <= 4)
                {
                    var label = new TextBlock
                    {
                        Text = snap.FormattedSize,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42))
                    };
                    Canvas.SetLeft(label, Math.Max(padLeft, Math.Min(pt.X - 22, width - padRight - 55)));
                    Canvas.SetTop(label, isAnomaly ? pt.Y + 10 : pt.Y - 18);
                    TrendCanvas.Children.Add(label);
                }
            }

            // チャート右上サマリー
            var regSlope = reg != null ? reg.FormattedDailyRate : "+0 B/day";
            ChartStatsTextBlock.Text = $"ペース: {regSlope} | 決定係数 R²={reg?.RSquared:F2}";
        }

        private void UpdateSummaryText()
        {
            if (_currentReport == null || _history.Count < 2) return;

            var reg = _currentReport.LinearRegression;
            string targetText;
            if (reg != null && reg.DaysToTarget > 0 && reg.TargetDate.HasValue)
            {
                targetText = $"{Strings.HistoryTargetReachedIn}約 {(int)Math.Ceiling(reg.DaysToTarget)}{Strings.HistoryDaysSuffix} ({reg.TargetDate.Value:yyyy/MM/dd})";
            }
            else
            {
                targetText = Strings.HistoryNeverReach;
            }

            string holtTrend = _currentReport.HoltSmoothing != null
                ? $" | {Strings.HistoryHoltTrend}: {_currentReport.HoltSmoothing.FormattedTrend}"
                : "";

            ForecastSummaryFooterText.Text = $"📊 {targetText}{holtTrend} (MADフロア: {FileItemNode.FormatBytes((long)_currentReport.EffectiveMAD)}/day)";
        }

        private void SelectInitialRow()
        {
            // 異常急増のある最初の行、または最新行
            var anomalyRow = _rowItems.FirstOrDefault(r => r.AnomalyData != null && r.AnomalyData.IsAnomaly);
            if (anomalyRow != null)
            {
                ShowContributors(anomalyRow.Snapshot, anomalyRow.AnomalyData);
            }
            else if (_rowItems.Count > 0)
            {
                ShowContributors(_rowItems[0].Snapshot, null);
            }
        }

        private void ShowContributors(ScanSnapshot snapshot, AnomalyDetectionPoint? anomaly)
        {
            InspectorDateText.Text = $"{snapshot.FormattedDate} ({snapshot.FormattedSize})";

            List<SubfolderGrowthItem> contributors;
            if (anomaly != null && anomaly.TopContributors.Count > 0)
            {
                contributors = anomaly.TopContributors;
            }
            else
            {
                // 直前スナップショットとの比較から算出
                var sorted = _history.OrderBy(h => h.Timestamp).ToList();
                int idx = sorted.FindIndex(h => h.Id == snapshot.Id);
                var prev = idx > 0 ? sorted[idx - 1] : null;

                contributors = CalculateSubfolderDiffs(prev, snapshot);
            }

            if (contributors.Count > 0)
            {
                NoContributorsText.Visibility = Visibility.Collapsed;
                ContributorsItemsControl.ItemsSource = contributors;
            }
            else
            {
                NoContributorsText.Visibility = Visibility.Visible;
                ContributorsItemsControl.ItemsSource = null;
            }
        }

        private List<SubfolderGrowthItem> CalculateSubfolderDiffs(ScanSnapshot? prev, ScanSnapshot curr)
        {
            var list = new List<SubfolderGrowthItem>();
            if (curr.SubFolders == null || curr.SubFolders.Count == 0) return list;

            var prevMap = prev?.SubFolders != null
                ? prev.SubFolders.ToDictionary(f => f.Name, f => f.Size, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

            long totalPositiveGrowth = 0;
            foreach (var cf in curr.SubFolders)
            {
                long pSize = prevMap.TryGetValue(cf.Name, out long s) ? s : 0;
                long delta = cf.Size - pSize;
                if (delta > 0)
                {
                    totalPositiveGrowth += delta;
                    list.Add(new SubfolderGrowthItem
                    {
                        Name = cf.Name,
                        PreviousSize = pSize,
                        CurrentSize = cf.Size,
                        DeltaBytes = delta
                    });
                }
            }

            if (totalPositiveGrowth > 0)
            {
                foreach (var item in list)
                {
                    item.ContributionPercent = Math.Round((double)item.DeltaBytes / totalPositiveGrowth * 100.0, 1);
                }
            }

            return list.OrderByDescending(x => x.DeltaBytes).Take(5).ToList();
        }

        private void HistoryRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is HistoryRowItem item)
            {
                ShowContributors(item.Snapshot, item.AnomalyData);
            }
        }

        private void AnomalyPin_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.Tag is ScanSnapshot snap)
            {
                var row = _rowItems.FirstOrDefault(r => r.Snapshot.Id == snap.Id);
                ShowContributors(snap, row?.AnomalyData);
            }
        }

        private void NormalDot_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.Tag is ScanSnapshot snap)
            {
                var row = _rowItems.FirstOrDefault(r => r.Snapshot.Id == snap.Id);
                ShowContributors(snap, row?.AnomalyData);
            }
        }

        private void ContributorItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2) return;
            if (sender is FrameworkElement elem && elem.DataContext is SubfolderGrowthItem item)
            {
                if (string.IsNullOrEmpty(_targetPath) || string.IsNullOrEmpty(item.Name)) return;

                string fullPath = System.IO.Path.Combine(_targetPath, item.Name);
                if (Directory.Exists(fullPath) || File.Exists(fullPath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = "explorer.exe",
                            Arguments = Directory.Exists(fullPath) ? $"\"{fullPath}\"" : $"/select,\"{fullPath}\"",
                            UseShellExecute = true
                        });
                    }
                    catch { }
                }
            }
        }

        private void ApplyThresholdButton_Click(object sender, RoutedEventArgs e)
        {
            CommitThresholdInput();
        }

        private void ThresholdInputTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                CommitThresholdInput();
            }
        }

        private void ThresholdInputTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            CommitThresholdInput();
        }

        private void CommitThresholdInput()
        {
            string text = ThresholdInputTextBox.Text.Trim();
            if (TryParseBytes(text, out long bytes) && bytes > 0)
            {
                _targetThresholdBytes = bytes;
                ThresholdInputTextBox.Text = FormatBytesToInputString(bytes);
                RecalculateAndRender();
            }
        }

        private static string FormatBytesToInputString(long bytes)
        {
            const double TB = 1024.0 * 1024.0 * 1024.0 * 1024.0;
            const double GB = 1024.0 * 1024.0 * 1024.0;
            const double MB = 1024.0 * 1024.0;

            if (bytes >= TB)
            {
                return $"{(bytes / TB):F1} TB";
            }
            if (bytes >= GB)
            {
                return $"{(bytes / GB):F1} GB";
            }
            return $"{(bytes / MB):F0} MB";
        }

        private static bool TryParseBytes(string input, out long bytes)
        {
            bytes = 0;
            if (string.IsNullOrWhiteSpace(input)) return false;

            input = input.Trim().ToUpperInvariant();
            double multiplier = 1;

            if (input.EndsWith("TB"))
            {
                multiplier = 1024.0 * 1024.0 * 1024.0 * 1024.0;
                input = input.Substring(0, input.Length - 2).Trim();
            }
            else if (input.EndsWith("GB"))
            {
                multiplier = 1024.0 * 1024.0 * 1024.0;
                input = input.Substring(0, input.Length - 2).Trim();
            }
            else if (input.EndsWith("MB"))
            {
                multiplier = 1024.0 * 1024.0;
                input = input.Substring(0, input.Length - 2).Trim();
            }
            else if (input.EndsWith("B"))
            {
                input = input.Substring(0, input.Length - 1).Trim();
            }

            if (double.TryParse(input, out double val) && val > 0)
            {
                bytes = (long)(val * multiplier);
                return true;
            }

            return false;
        }

        private void TrendCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RenderTrendChart();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}

