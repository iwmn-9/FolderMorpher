using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using AstraSize.Models;

namespace AstraSize
{
    public partial class HistoryWindow : Window
    {
        private readonly List<ScanSnapshot> _history;

        public HistoryWindow(string targetPath, List<ScanSnapshot> history)
        {
            InitializeComponent();
            _history = history ?? new List<ScanSnapshot>();
            TargetPathText.Text = $"対象パス: {targetPath}";
            HistoryItemsControl.ItemsSource = _history;

            if (_history.Count == 0)
            {
                EmptyHistoryText.Visibility = Visibility.Visible;
                NoChartDataText.Visibility = Visibility.Visible;
            }
            else
            {
                RenderTrendChart();
            }
        }

        private void TrendCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            RenderTrendChart();
        }

        private void RenderTrendChart()
        {
            if (TrendCanvas == null) return;
            TrendCanvas.Children.Clear();

            if (_history.Count < 2)
            {
                NoChartDataText.Visibility = Visibility.Visible;
                ChartStatsTextBlock.Text = _history.Count == 1 ? $"記録 1 件: {_history[0].FormattedSize}" : "";
                return;
            }

            NoChartDataText.Visibility = Visibility.Collapsed;

            // Sort chronologically (oldest to newest)
            var sorted = _history.OrderBy(h => h.Timestamp).ToList();

            double width = TrendCanvas.ActualWidth;
            double height = TrendCanvas.ActualHeight;
            if (width <= 40 || height <= 40) return;

            long minBytes = sorted.Min(h => h.TotalBytes);
            long maxBytes = sorted.Max(h => h.TotalBytes);

            // Add margin to min and max so chart doesn't clip
            long range = maxBytes - minBytes;
            if (range == 0) range = Math.Max(1024, maxBytes / 10);
            long plotMin = Math.Max(0, minBytes - (long)(range * 0.1));
            long plotMax = maxBytes + (long)(range * 0.1);
            long plotRange = plotMax - plotMin;
            if (plotRange == 0) plotRange = 1;

            double padLeft = 16;
            double padRight = 16;
            double padTop = 16;
            double padBottom = 24;
            double chartW = width - padLeft - padRight;
            double chartH = height - padTop - padBottom;

            // Draw horizontal grid lines
            for (int i = 0; i <= 3; i++)
            {
                double y = padTop + (chartH / 3) * i;
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

            // Calculate points
            var points = new PointCollection();
            var polyPoints = new PointCollection();

            polyPoints.Add(new Point(padLeft, height - padBottom));

            for (int i = 0; i < sorted.Count; i++)
            {
                double x = padLeft + (chartW / (sorted.Count - 1)) * i;
                double normalizedY = (double)(sorted[i].TotalBytes - plotMin) / plotRange;
                double y = height - padBottom - (normalizedY * chartH);

                var pt = new Point(x, y);
                points.Add(pt);
                polyPoints.Add(pt);
            }

            polyPoints.Add(new Point(width - padRight, height - padBottom));

            // Area fill (Polygon with gradient)
            var areaBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 1)
            };
            areaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(90, 2, 132, 199), 0.0));
            areaBrush.GradientStops.Add(new GradientStop(Color.FromArgb(10, 2, 132, 199), 1.0));

            var polygon = new Polygon
            {
                Points = polyPoints,
                Fill = areaBrush
            };
            TrendCanvas.Children.Add(polygon);

            // Trend line (Polyline)
            var polyline = new Polyline
            {
                Points = points,
                Stroke = new SolidColorBrush(Color.FromRgb(2, 132, 199)),
                StrokeThickness = 2.5
            };
            TrendCanvas.Children.Add(polyline);

            // Data point dots & labels
            for (int i = 0; i < sorted.Count; i++)
            {
                var pt = points[i];
                var dot = new Ellipse
                {
                    Width = 8,
                    Height = 8,
                    Fill = Brushes.White,
                    Stroke = new SolidColorBrush(Color.FromRgb(2, 132, 199)),
                    StrokeThickness = 2
                };
                Canvas.SetLeft(dot, pt.X - 4);
                Canvas.SetTop(dot, pt.Y - 4);
                TrendCanvas.Children.Add(dot);

                // For first and last, or if small count, show date/size label
                if (i == 0 || i == sorted.Count - 1 || sorted.Count <= 5)
                {
                    var label = new TextBlock
                    {
                        Text = sorted[i].FormattedSize,
                        FontSize = 10,
                        FontWeight = FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42))
                    };
                    Canvas.SetLeft(label, Math.Max(padLeft, Math.Min(pt.X - 20, width - padRight - 50)));
                    Canvas.SetTop(label, pt.Y - 18);
                    TrendCanvas.Children.Add(label);
                }
            }

            // Stats text
            var firstSize = sorted.First().TotalBytes;
            var lastSize = sorted.Last().TotalBytes;
            var totalDiff = lastSize - firstSize;
            string diffSign = totalDiff > 0 ? "+" : "";
            ChartStatsTextBlock.Text = $"期間差分: {diffSign}{FileItemNode.FormatBytes(totalDiff)} (全{sorted.Count}回計測)";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
