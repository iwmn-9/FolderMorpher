using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AstraSize.Models;
using FolderMorpher.Models;
using FolderMorpher.Services;
using Microsoft.Win32;

namespace AstraSize
{
    public partial class MainWindow : Window
    {
        #region Media Optimizer Tab
        private void MediaBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "メディア走査対象ディレクトリを選択"
            };
            if (dialog.ShowDialog() == true)
            {
                MediaPathTextBox.Text = dialog.FolderName;
            }
        }

        private async void MediaScanButton_Click(object sender, RoutedEventArgs e)
        {
            var target = MediaPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show("有効なディレクトリを入力してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _mediaCts?.Cancel();
            _mediaCts = new CancellationTokenSource();

            GlobalProgressBar.Visibility = Visibility.Visible;
            GlobalProgressBar.IsIndeterminate = true;
            MediaStatusText.Text = "メディア走査中...";

            int maxDim = int.TryParse(MediaMaxDimTextBox.Text, out var md) ? md : 2560;
            int quality = int.TryParse(MediaQualityTextBox.Text, out var q) ? q : 85;
            long minSizeMb = long.TryParse(MediaMinSizeMbTextBox.Text, out var ms) ? ms : 2;

            var options = new MediaOptimizeOptions
            {
                TargetDirectory = target,
                MaxDimension = maxDim,
                JpegQuality = quality,
                MinImageSizeBytes = minSizeMb * 1024 * 1024
            };

            var progress = new Progress<string>(msg =>
            {
                MediaStatusText.Text = msg;
                StatusTextBlock.Text = msg;
            });

            try
            {
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync(_mediaCts.Token);
                var scanRes = await host.ScanMediaAsync(FolderMorpher.HostClient.MediaDtoMapper.ToDto(options), progress, _mediaCts.Token);
                var images = scanRes.Images.Select(FolderMorpher.HostClient.MediaDtoMapper.ToViewItem).ToList();
                var videos = scanRes.Videos.Select(FolderMorpher.HostClient.MediaDtoMapper.ToViewItem).ToList();
                _lastMediaImages = images;
                _lastMediaVideos = videos;

                var allItems = images.Concat(videos).ToList();
                MediaItemsDataGrid.ItemsSource = allItems;

                MediaKpiImagesCount.Text = $"{images.Count:N0} 枚";
                MediaKpiVideosCount.Text = $"{videos.Count:N0} 本";
                MediaKpiOptimizedCount.Text = "0 枚";
                MediaKpiSavedSize.Text = "0 B";

                MediaStatusText.Text = $"走査完了: 画像 {images.Count} 枚, 動画 {videos.Count} 本";
                ShowToast($"メディア走査完了: {allItems.Count} 件検出");
            }
            catch (OperationCanceledException)
            {
                MediaStatusText.Text = "走査を中止しました。";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"メディア走査エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                MediaStatusText.Text = "エラー発生";
            }
            finally
            {
                GlobalProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private List<MediaItem> _lastMediaTargets = new();

        private void MediaOptimizeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastMediaImages == null || _lastMediaImages.Count == 0)
            {
                MessageBox.Show("軽量化対象の画像がありません。先にメディア走査を実行してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var targets = _lastMediaImages.Where(i => !i.IsExcluded && !i.IsProcessed).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show("軽量化が必要な画像はありません（すべて聖域保護または処理済みです）。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            int maxDim = int.TryParse(MediaMaxDimTextBox.Text, out var md) ? md : 2560;
            int quality = int.TryParse(MediaQualityTextBox.Text, out var q) ? q : 85;
            int excludedCount = _lastMediaImages.Count(i => i.IsExcluded);

            _lastMediaTargets = targets;
            MediaDiffDataGrid.ItemsSource = targets;
            MediaDiffSummaryText.Text = $"📊 対象: {targets.Count}枚 / 設定: 長辺 {maxDim}px超・品質 {quality}% (🛡️ 保護対象: {excludedCount}枚スキップ)";
            MediaDiffModalOverlay.Visibility = Visibility.Visible;
        }

        private void MediaDiffModalClose_Click(object sender, RoutedEventArgs e)
        {
            MediaDiffModalOverlay.Visibility = Visibility.Collapsed;
        }

        private async void MediaDiffModalApply_Click(object sender, RoutedEventArgs e)
        {
            if (_lastMediaTargets == null || _lastMediaTargets.Count == 0)
            {
                MediaDiffModalOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            GlobalProgressBar.Visibility = Visibility.Visible;
            GlobalProgressBar.IsIndeterminate = true;

            int maxDim = int.TryParse(MediaMaxDimTextBox.Text, out var md) ? md : 2560;
            int quality = int.TryParse(MediaQualityTextBox.Text, out var q) ? q : 85;

            var options = new MediaOptimizeOptions
            {
                TargetDirectory = MediaPathTextBox.Text.Trim(),
                MaxDimension = maxDim,
                JpegQuality = quality
            };

            var progress = new Progress<(string File, bool Success, string Msg)>(p =>
            {
                MediaStatusText.Text = $"{Path.GetFileName(p.File)}: {p.Msg}";
            });

            try
            {
                using var cts = new CancellationTokenSource();
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync(cts.Token);
                var hostProgress = new Progress<string>(s => MediaStatusText.Text = s);
                var result = await host.OptimizeImagesAsync(
                    _lastMediaTargets.Select(FolderMorpher.HostClient.MediaDtoMapper.ToDto).ToList(),
                    FolderMorpher.HostClient.MediaDtoMapper.ToDto(options), hostProgress, cts.Token);
                var summary = FolderMorpher.HostClient.MediaDtoMapper.ToViewSummary(result.Summary);
                var updatedByPath = result.UpdatedItems.ToDictionary(item => item.FullPath, StringComparer.OrdinalIgnoreCase);
                foreach (var target in _lastMediaTargets)
                {
                    if (!updatedByPath.TryGetValue(target.FullPath, out var updated)) continue;
                    target.OptimizedSizeBytes = updated.OptimizedSizeBytes;
                    target.Status = updated.Status;
                    target.IsProcessed = updated.IsProcessed;
                }
                _lastMediaSummary = summary;

                MediaItemsDataGrid.Items.Refresh();

                MediaKpiOptimizedCount.Text = $"{summary.OptimizedImagesCount:N0} 枚";
                MediaKpiSavedSize.Text = summary.TotalSavedSizeFormatted;

                MediaStatusText.Text = $"最適化完了: {summary.TotalSavedSizeFormatted} の空き容量を解放しました";
                MediaDiffModalOverlay.Visibility = Visibility.Collapsed;

                // Verify: 処理結果の検証
                int failed = _lastMediaTargets.Count - summary.OptimizedImagesCount;
                if (failed <= 0)
                {
                    ShowToast($"✅ 写真軽量化完了 (検証済): {summary.TotalSavedSizeFormatted} 削減 ({summary.OptimizedImagesCount} 枚)");
                }
                else
                {
                    ShowToast($"⚠️ 写真軽量化完了 (一部スキップ/エラー): {summary.TotalSavedSizeFormatted} 削減 (成功 {summary.OptimizedImagesCount}枚, 未処理 {failed}枚)");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"最適化エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                GlobalProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private async void MediaGenVideoBatchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastMediaVideos == null || _lastMediaVideos.Count == 0)
            {
                MessageBox.Show("圧縮対象の動画がありません。先にメディア走査を実行してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "大容量動画 夜間圧縮バッチの保存先",
                Filter = "バッチファイル (*.bat)|*.bat",
                FileName = "Compress-Videos-Nightly.bat"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    await host.GenerateVideoCompressBatchAsync(dialog.FileName,
                        _lastMediaVideos.Select(FolderMorpher.HostClient.MediaDtoMapper.ToDto).ToList(), CancellationToken.None);
                    ShowToast("夜間動画圧縮バッチを生成しました");
                    ShellHelper.SelectInExplorer(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"バッチ生成エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void MediaExportExcelButton_Click(object sender, RoutedEventArgs e)
        {
            var allItems = _lastMediaImages.Concat(_lastMediaVideos).ToList();
            if (allItems.Count == 0)
            {
                MessageBox.Show("出力対象のメディアデータがありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "メディア分析 Excelレポートの保存先",
                Filter = "Excel ワークブック (*.xlsx)|*.xlsx",
                InitialDirectory = GetDefaultExportDirectory(),
                FileName = $"FolderMorpher_MediaReport_{DateTime.Now:yyyyMMdd}.xlsx"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    await host.ExportExcelReportAsync(BuildReportExportRequest(
                        dialog.FileName, MediaPathTextBox.Text.Trim(), allItems), CancellationToken.None);

                    ShowToast("Excelレポートを出力しました");
                    ShellHelper.SelectInExplorer(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Excel出力エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void MediaItemsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (MediaItemsDataGrid.SelectedItem is not MediaItem item) return;

            ShellHelper.SelectInExplorer(item.FullPath);
        }
        #endregion
    }
}
