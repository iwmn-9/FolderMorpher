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
        #region Audit & Hygiene Tab
        private Guid _lastAuditReportId;
        private long _auditPreviewGeneration;
        private void AuditBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = UiText("監査対象ディレクトリを選択", "Select a folder to audit")
            };
            if (dialog.ShowDialog() == true)
            {
                AuditPathTextBox.Text = dialog.FolderName;
            }
        }

        private async void AuditStartButton_Click(object sender, RoutedEventArgs e)
        {
            var target = AuditPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(target))
            {
                MessageBox.Show(UiText("有効な監査対象ディレクトリを入力してください。", "Enter a valid folder to audit."), UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _auditCts?.Cancel();
            _auditCts = new CancellationTokenSource();

            GlobalProgressBar.Visibility = Visibility.Visible;
            GlobalProgressBar.IsIndeterminate = true;
            AuditStatusText.Text = UiText("監査スキャン中...", "Scanning for cleanup candidates...");

            var limit = AuditBandwidthLimit.Standard50MB;
            if (AuditBandwidthComboBox != null && AuditBandwidthComboBox.SelectedItem is ComboBoxItem cbi && cbi.Tag?.ToString() == "Unlimited")
            {
                limit = AuditBandwidthLimit.Unlimited;
            }

            var options = new AuditOptions
            {
                TargetDirectory = target,
                CheckVersionFamilies = AuditCheckVersionFamiliesCheckBox?.IsChecked == true,
                CheckExtractedArchives = AuditCheckExtractedArchivesCheckBox?.IsChecked == true,
                CheckDuplicates = AuditCheckDuplicatesCheckBox?.IsChecked == true,
                CheckDormant = AuditCheckDormantCheckBox?.IsChecked == true,
                CheckGraveyardTrees = AuditCheckDormantCheckBox?.IsChecked == true,
                CheckPathLimits = AuditCheckPathLimitsCheckBox?.IsChecked == true,
                BandwidthLimit = limit
            };

            if (AuditExcludeFoldersTextBox != null && !string.IsNullOrWhiteSpace(AuditExcludeFoldersTextBox.Text))
            {
                var patterns = AuditExcludeFoldersTextBox.Text
                    .Split(new[] { ',', ';', '、' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim())
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();
                options.ExcludeFolderPatterns.AddRange(patterns);
            }

            var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync(_auditCts.Token);
            var hostProgress = new Progress<string>(s =>
            {
                AuditStatusText.Text = s;
                StatusTextBlock.Text = s;
            });

            try
            {
                var auditReq = new FolderMorpher.Contracts.AuditScanRequestDto
                {
                    TargetPath = options.TargetDirectory,
                    CheckDuplicates = options.CheckDuplicates,
                    CheckDormant = options.CheckDormant,
                    CheckVersionFamilies = options.CheckVersionFamilies,
                    CheckExtractedArchives = options.CheckExtractedArchives,
                    CheckGraveyardTrees = options.CheckGraveyardTrees,
                    CheckPathLimits = options.CheckPathLimits,
                    DormantYearsThreshold = options.DormantYearsThreshold,
                    MinFileSizeBytes = options.MinFileSizeBytes,
                    BandwidthLimit = (int)options.BandwidthLimit,
                    IgnoredPaths = options.ExcludeFolderPatterns
                };
                var auditJob = await FolderMorpher.HostClient.HostJobClient.RunAsync(
                    new FolderMorpher.Contracts.HostJobRequestDto
                    {
                        Kind = FolderMorpher.Contracts.HostJobKind.AuditScan,
                        AuditRequest = auditReq
                    },
                    status =>
                    {
                        if (!string.IsNullOrWhiteSpace(status.ProgressText)) ((IProgress<string>)hostProgress).Report(status.ProgressText);
                    },
                    _auditCts.Token);
                var report = auditJob.AuditReport ?? throw new InvalidOperationException(UiText("監査結果がHostから返されませんでした。", "The host did not return an audit report."));
                _lastAuditReportId = report.ReportId;
                var summary = FolderMorpher.HostClient.AuditDtoMapper.ToViewSummary(report.Summary);
                var items = report.Items.Select(FolderMorpher.HostClient.AuditDtoMapper.ToViewItem).ToList();
                _lastAuditSummary = summary;
                _lastAuditItems = items;
                _auditSortProperty = "Default";
                _auditSortDescending = false;
                if (AuditItemsDataGrid != null)
                {
                    foreach (var col in AuditItemsDataGrid.Columns) col.SortDirection = null;
                }
                ApplyAuditFilters();
                UpdateLiveSelectedReduction();

                // Update KPI Bar
                if (AuditKpiTotalFiles != null)
                {
                    AuditKpiTotalFiles.Text = summary.InaccessibleDirectoriesCount > 0
                        ? UiText($"{summary.TotalFilesScanned:N0} 件 (⚠️未走査 {summary.InaccessibleDirectoriesCount})", $"{summary.TotalFilesScanned:N0} items (⚠️ {summary.InaccessibleDirectoriesCount} inaccessible folders)")
                        : UiText($"{summary.TotalFilesScanned:N0} 件", $"{summary.TotalFilesScanned:N0} items");
                }
                if (AuditKpiReadyToClean != null) AuditKpiReadyToClean.Text = summary.ReadyToCleanSizeFormatted;
                if (AuditKpiVersionFamily != null) AuditKpiVersionFamily.Text = summary.VersionFamilySizeFormatted;
                if (AuditKpiDupWasted != null) AuditKpiDupWasted.Text = summary.DuplicateWastedSizeFormatted;
                if (AuditKpiDormantSize != null) AuditKpiDormantSize.Text = summary.DormantSizeFormatted;

                string statusMsg = summary.InaccessibleDirectoriesCount > 0
                    ? UiText($"完了: 整理候補 {items.Count:N0} 件検出 (⚠️アクセス拒否: {summary.InaccessibleDirectoriesCount} 箇所)", $"Complete: {items.Count:N0} candidates (⚠️ {summary.InaccessibleDirectoriesCount} inaccessible folders)")
                    : UiText($"完了: 整理候補 {items.Count:N0} 件検出 (整理推奨: {summary.ReadyToCleanSizeFormatted})", $"Complete: {items.Count:N0} candidates (recommended: {summary.ReadyToCleanSizeFormatted})");
                AuditStatusText.Text = statusMsg;
                ShowToast(statusMsg);
            }
            catch (OperationCanceledException)
            {
                AuditStatusText.Text = UiText("監査を中止しました。", "Audit canceled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(UiText($"監査エラー: {ex.Message}", $"Audit failed: {ex.Message}"), UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                AuditStatusText.Text = UiText("エラー発生", "Error");
            }
            finally
            {
                GlobalProgressBar.Visibility = Visibility.Collapsed;
                UpdateIgnoredCountBadge();
            }
        }

        private async void AuditExportExcelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastAuditItems == null || _lastAuditItems.Count == 0)
            {
                MessageBox.Show(UiText("出力対象の監査結果がありません。先にスキャンを実行してください。", "No audit results to export. Run a scan first."), UiText("情報", "Information"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = UiText("Excelレポートの保存先", "Save Excel report"),
                Filter = UiText("Excel ワークブック (*.xlsx)|*.xlsx", "Excel workbook (*.xlsx)|*.xlsx"),
                InitialDirectory = GetDefaultExportDirectory(),
                FileName = $"FolderMorpher_AuditReport_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    await host.ExportExcelReportAsync(BuildReportExportRequest(
                        dialog.FileName, AuditPathTextBox.Text.Trim(), _lastMediaImages.Concat(_lastMediaVideos)), CancellationToken.None);

                    ShowToast(UiText("Excelレポートを出力しました", "Excel report exported"));
                    ShellHelper.SelectInExplorer(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(UiText($"Excel出力エラー: {ex.Message}", $"Excel export failed: {ex.Message}"), UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void AuditExportCsvButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastAuditItems == null || _lastAuditItems.Count == 0)
            {
                MessageBox.Show(UiText("出力対象のデータがありません。", "No data to export."), UiText("情報", "Information"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = UiText("CSV棚卸し台帳の保存先", "Save CSV inventory"),
                Filter = UiText("CSVファイル (*.csv)|*.csv", "CSV file (*.csv)|*.csv"),
                FileName = $"FolderMorpher_AuditList_{DateTime.Now:yyyyMMdd}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    await host.ExportAuditCsvAsync(dialog.FileName,
                        _lastAuditItems.Select(FolderMorpher.HostClient.AuditDtoMapper.ToDto).ToList(), CancellationToken.None);
                    ShowToast(UiText("CSV台帳を出力しました", "CSV inventory exported"));
                    ShellHelper.SelectInExplorer(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(UiText($"CSV出力エラー: {ex.Message}", $"CSV export failed: {ex.Message}"), UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void AuditFilter_Changed(object sender, RoutedEventArgs e)
        {
            // テキスト入力時は200msデバウンス（大量件数でのタイピング詰まり防止）
            if (sender == AuditSearchFilterTextBox)
            {
                _auditFilterDebounceTimer?.Stop();
                _auditFilterDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
                _auditFilterDebounceTimer.Tick += (s, ev) =>
                {
                    _auditFilterDebounceTimer.Stop();
                    ApplyAuditFilters();
                };
                _auditFilterDebounceTimer.Start();
            }
            else
            {
                _auditFilterDebounceTimer?.Stop();
                ApplyAuditFilters();
            }
        }

        private async void ApplyAuditFilters()
        {
            var revision = ++_auditFilterRevision;
            if (AuditCategoryFilterComboBox == null || AuditSearchFilterTextBox == null || AuditItemsDataGrid == null)
                return;

            string selectedTag = (AuditCategoryFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
            string maxDisplayTag = (AuditMaxDisplayComboBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "100";
            string query = AuditSearchFilterTextBox.Text.Trim();

            var filtered = _lastAuditItems.Where(x => !x.IsIgnored);

            if (selectedTag == "ReadyToClean")
            {
                filtered = filtered.Where(x => x.WasteScore >= 80 && !x.IsOriginalCandidate);
            }
            else if (selectedTag == "ReviewAdvised")
            {
                filtered = filtered.Where(x => x.WasteScore >= 50 && x.WasteScore < 80);
            }
            else if (selectedTag == "VersionFamily")
            {
                filtered = filtered.Where(x => x.IssueType == AuditIssueType.VersionFamily);
            }
            else if (selectedTag == "ExtractedArchive")
            {
                filtered = filtered.Where(x => x.IssueType == AuditIssueType.ExtractedArchive);
            }
            else if (selectedTag == "GraveyardTree")
            {
                filtered = filtered.Where(x => x.IssueType == AuditIssueType.GraveyardTree);
            }
            else if (selectedTag == "Duplicate")
            {
                filtered = filtered.Where(x => x.IssueType == AuditIssueType.Duplicate);
            }
            else if (selectedTag == "Dormant")
            {
                filtered = filtered.Where(x => x.IssueType == AuditIssueType.Dormant);
            }
            else if (selectedTag == "PathLimit")
            {
                filtered = filtered.Where(x => x.IssueType == AuditIssueType.PathTooLong || x.IssueType == AuditIssueType.InvalidChar);
            }

            if (!string.IsNullOrEmpty(query))
            {
                filtered = filtered.Where(x =>
                    (x.FileName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                    (x.FullPath?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                    (x.Detail?.Contains(query, StringComparison.OrdinalIgnoreCase) == true));
            }

            var resultList = filtered.ToList();

            // 階層ソート適用（容量ソート時は重複グループをひとかたまりに束ね、原本候補を先頭に配置）
            if (_auditSortProperty == "Default" || string.IsNullOrEmpty(_auditSortProperty))
            {
                // デフォルトは無駄度スコア降順 ➔ 容量降順（最も整理すべき重要候補が最上位に並ぶ）
                resultList = resultList
                    .OrderByDescending(x => x.WasteScore)
                    .ThenByDescending(x => x.Size)
                    .ToList();
            }
            else
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    var sortedIds = await host.SortAuditIdsAsync(_lastAuditReportId,
                        resultList.Select(item => item.AuditId).ToList(),
                        _auditSortProperty, _auditSortDescending, CancellationToken.None);
                    if (revision != _auditFilterRevision) return;
                    var byId = resultList.ToDictionary(item => item.AuditId);
                    resultList = sortedIds.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to sort audit items: {ex}");
                    return;
                }
            }

            int totalMatched = resultList.Count;

            // 表示件数制限の適用 (100 / 300 / 500 / All)
            if (maxDisplayTag != "All" && int.TryParse(maxDisplayTag, out int maxCount) && maxCount > 0)
            {
                resultList = resultList.Take(maxCount).ToList();
            }

            // 一括仮想化バインド（1件ずつAddするループを撤廃し、数十万件でも一瞬で表示切替）
            AuditItemsDataGrid.ItemsSource = resultList;

            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            string baseTitle = isJa ? "検出された整理候補一覧" : "Detected Cleanup Candidates";
            if (_lastAuditItems.Count > 0)
            {
                if (resultList.Count < totalMatched)
                {
                    AuditTableTitleText.Text = isJa
                        ? $"{baseTitle} (全 {_lastAuditItems.Count:N0} 件中 上位 {resultList.Count:N0} 件を表示)"
                        : $"{baseTitle} (Showing top {resultList.Count:N0} of {_lastAuditItems.Count:N0})";
                }
                else
                {
                    AuditTableTitleText.Text = $"{baseTitle} ({resultList.Count:N0} / {_lastAuditItems.Count:N0} {UiText("件", "items")})";
                }
            }
            else
            {
                AuditTableTitleText.Text = baseTitle;
            }
        }

        private void AuditItemsDataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true; // WPFの標準ソートを抑止し、重複グループを壊さないカスタム階層ソートを実行

            string sortProp = e.Column.SortMemberPath;
            if (string.IsNullOrEmpty(sortProp)) return;

            // ソート方向のトグル（内部状態を正本とする）
            bool newDescending;
            if (_auditSortProperty == sortProp)
            {
                // 同一列の再クリック時は昇順/降順を反転
                newDescending = !_auditSortDescending;
            }
            else
            {
                // 列切り替え時: 容量はデフォルト降順(大->小)、それ以外は昇順
                newDescending = (sortProp == "Size");
            }

            _auditSortProperty = sortProp;
            _auditSortDescending = newDescending;

            ApplyAuditFilters();

            // ItemsSource再代入でクリアされたカラムのインジケーターを再適用
            foreach (var col in AuditItemsDataGrid.Columns)
            {
                col.SortDirection = null;
            }
            e.Column.SortDirection = newDescending
                ? System.ComponentModel.ListSortDirection.Descending
                : System.ComponentModel.ListSortDirection.Ascending;
        }

        private void AuditItemsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (AuditItemsDataGrid.SelectedItem is not AuditItem item) return;

            ShellHelper.SelectInExplorer(item.FullPath);
        }

        private void AuditSmartSelectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (AuditSmartSelectComboBox.SelectedItem is not ComboBoxItem item) return;
            string tag = item.Tag?.ToString() ?? "None";
            if (tag == "None") return;

            if (_lastAuditItems == null || _lastAuditItems.Count == 0)
            {
                AuditSmartSelectComboBox.SelectedIndex = 0;
                return;
            }

            var visibleList = AuditItemsDataGrid.ItemsSource as List<AuditItem> ?? _lastAuditItems;

            _isAuditBatchUpdating = true;
            try
            {
                // ★ ADR 97: 一括選択プリセットを表示中アイテム（visibleList）に限定
                // 画面に100件しか表示されていないのに裏で数千件が選択される不一致を完全解消
                switch (tag)
                {
                    case "ReadyToClean":
                        foreach (var ai in _lastAuditItems) ai.IsChecked = false;
                        int rtcCount = 0;
                        foreach (var ai in visibleList)
                        {
                            if (ai.WasteScore >= 80 && !ai.IsOriginalCandidate)
                            {
                                ai.IsChecked = true;
                                rtcCount++;
                            }
                        }
                        ShowToast(UiText($"表示中の「すぐ整理できそう」な候補 {rtcCount:N0} 件を選択しました", $"Selected {rtcCount:N0} visible recommended candidates"));
                        break;

                    case "VersionFamilyOnly":
                        foreach (var ai in _lastAuditItems) ai.IsChecked = false;
                        int vfCount = 0;
                        foreach (var ai in visibleList)
                        {
                            if (ai.IssueType == AuditIssueType.VersionFamily)
                            {
                                ai.IsChecked = true;
                                vfCount++;
                            }
                        }
                        ShowToast(UiText($"表示中の世代・旧版の過去版 {vfCount:N0} 件を選択しました", $"Selected {vfCount:N0} visible older versions"));
                        break;

                    case "ExtractedArchiveOnly":
                        foreach (var ai in _lastAuditItems) ai.IsChecked = false;
                        int eaCount = 0;
                        foreach (var ai in visibleList)
                        {
                            if (ai.IssueType == AuditIssueType.ExtractedArchive)
                            {
                                ai.IsChecked = true;
                                eaCount++;
                            }
                        }
                        ShowToast(UiText($"表示中の展開済ZIP残骸 {eaCount:N0} 件を選択しました", $"Selected {eaCount:N0} visible extracted ZIP archives"));
                        break;

                    case "DupCopyOnly":
                        foreach (var ai in _lastAuditItems) ai.IsChecked = false;
                        int dupCount = 0;
                        foreach (var ai in visibleList)
                        {
                            if (ai.IssueType == AuditIssueType.Duplicate && !ai.IsOriginalCandidate)
                            {
                                ai.IsChecked = true;
                                dupCount++;
                            }
                        }
                        ShowToast(UiText($"表示中の重複ファイルの原本以外（コピー） {dupCount:N0} 件を選択しました", $"Selected {dupCount:N0} visible duplicate copies"));
                        break;

                    case "Dormant3Y":
                        DateTime threshold3Y = DateTime.Now.AddYears(-3);
                        foreach (var ai in _lastAuditItems) ai.IsChecked = false;
                        int d3Count = 0;
                        foreach (var ai in visibleList)
                        {
                            if (ai.IssueType == AuditIssueType.Dormant && ai.LastWriteTime < threshold3Y)
                            {
                                ai.IsChecked = true;
                                d3Count++;
                            }
                        }
                        ShowToast(UiText($"表示中の3年以上未更新ファイル {d3Count:N0} 件を選択しました", $"Selected {d3Count:N0} visible files unchanged for 3+ years"));
                        break;

                    case "Dormant5Y":
                        DateTime threshold5Y = DateTime.Now.AddYears(-5);
                        foreach (var ai in _lastAuditItems) ai.IsChecked = false;
                        int d5Count = 0;
                        foreach (var ai in visibleList)
                        {
                            if (ai.IssueType == AuditIssueType.Dormant && ai.LastWriteTime < threshold5Y)
                            {
                                ai.IsChecked = true;
                                d5Count++;
                            }
                        }
                        ShowToast(UiText($"表示中の5年以上未更新ファイル {d5Count:N0} 件を選択しました", $"Selected {d5Count:N0} visible files unchanged for 5+ years"));
                        break;

                    case "SelectVisible":
                        // 現在の絞り込み表示中のみすべて選択
                        foreach (var ai in visibleList)
                        {
                            ai.IsChecked = true;
                        }
                        ShowToast(UiText($"表示中の {visibleList.Count:N0} 件を選択しました", $"Selected {visibleList.Count:N0} visible items"));
                        break;

                    case "ClearAll":
                        // すべて選択解除
                        foreach (var ai in _lastAuditItems)
                        {
                            ai.IsChecked = false;
                        }
                        if (AuditHeaderCheckBox != null) AuditHeaderCheckBox.IsChecked = false;
                        ShowToast(UiText("選択をすべて解除しました", "Selection cleared"));
                        break;
                }
            }
            finally
            {
                _isAuditBatchUpdating = false;
            }

            UpdateLiveSelectedReduction();

            // 次回も同じプリセットを選択できるように初期インデックスへリセット
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (AuditSmartSelectComboBox != null) AuditSmartSelectComboBox.SelectedIndex = 0;
            }));
        }

        private void AuditHeaderCheckBox_Click(object sender, RoutedEventArgs e)
        {
            bool check = AuditHeaderCheckBox.IsChecked == true;
            var visibleList = AuditItemsDataGrid.ItemsSource as List<AuditItem> ?? _lastAuditItems;
            if (visibleList != null)
            {
                _isAuditBatchUpdating = true;
                try
                {
                    foreach (var item in visibleList)
                    {
                        item.IsChecked = check;
                    }
                }
                finally
                {
                    _isAuditBatchUpdating = false;
                }
            }
            UpdateLiveSelectedReduction();
        }

        private bool _isAuditBatchUpdating = false;

        private async void UpdateLiveSelectedReduction()
        {
            if (AuditLiveSelectedReductionText == null) return;
            if (_isAuditBatchUpdating) return;

            if (!Dispatcher.CheckAccess())
            {
                _ = Dispatcher.BeginInvoke(new Action(UpdateLiveSelectedReduction));
                return;
            }

            long generation = Interlocked.Increment(ref _auditPreviewGeneration);

            if (_lastAuditItems == null || _lastAuditItems.Count == 0)
            {
                AuditLiveSelectedReductionText.Text = UiText("0 B (0 件)", "0 B (0 items)");
                return;
            }

            try
            {
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                var preview = await host.PrepareAuditCleanupAsync(new FolderMorpher.Contracts.AuditCleanupPrepareRequestDto
                {
                    ReportId = _lastAuditReportId,
                    SelectedAuditIds = _lastAuditItems.Where(item => item.IsChecked).Select(item => item.AuditId).ToList(),
                    RetainForCommit = false
                }, CancellationToken.None);
                if (generation != Volatile.Read(ref _auditPreviewGeneration)) return;
                AuditLiveSelectedReductionText.Text = UiText($"{FileItemNode.FormatBytes(preview.SafeSizeBytes)} ({preview.SafeFileCount:N0} 件)", $"{FileItemNode.FormatBytes(preview.SafeSizeBytes)} ({preview.SafeFileCount:N0} items)");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Audit reduction preview failed: {ex}");
            }
        }

        private async void AuditDeleteSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastAuditItems == null || _lastAuditItems.Count == 0)
            {
                MessageBox.Show(UiText("削除対象のファイルがありません。先に監査スキャンを実行してください。", "No files to delete. Run an audit first."), UiText("案内", "Information"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            FolderMorpher.Contracts.AuditCleanupPreviewDto preview;
            FolderMorpher.Contracts.IFolderMorpherHostService host;
            try
            {
                host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                preview = await host.PrepareAuditCleanupAsync(new FolderMorpher.Contracts.AuditCleanupPrepareRequestDto
                {
                    ReportId = _lastAuditReportId,
                    SelectedAuditIds = _lastAuditItems.Where(item => item.IsChecked).Select(item => item.AuditId).ToList(),
                    RetainForCommit = true
                }, CancellationToken.None);
            }
            catch (Exception ex)
            {
                MessageBox.Show(UiText($"削除計画の確認に失敗しました: {ex.Message}", $"Could not prepare the deletion plan: {ex.Message}"), UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            if (preview.SafeFileCount == 0 && preview.ProtectedOriginalPaths.Count == 0)
            {
                MessageBox.Show(UiText("削除するファイルが選択されていません。チェックボックスでファイルを選択してから実行してください。", "No files selected. Check the files to delete, then try again."), UiText("案内", "Information"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var protectedPaths = new HashSet<string>(preview.ProtectedOriginalPaths, StringComparer.OrdinalIgnoreCase);
            if (protectedPaths.Count > 0)
            {
                foreach (var item in _lastAuditItems.Where(item => protectedPaths.Contains(item.FullPath)))
                    item.IsChecked = false;

                if (preview.SafeFileCount == 0)
                {
                    MessageBox.Show(
                        UiText($"選択された項目（{protectedPaths.Count:N0} 件）はすべて重複グループの【原本候補】です。\n\n" +
                            "原本全滅事故を防止するため、原本候補ファイルはツール上から削除できません。\n" +
                            "削除処理を中止しました。",
                            $"All {protectedPaths.Count:N0} selected items are original candidates in duplicate groups.\n\nOriginal candidates cannot be deleted in this tool. Deletion was canceled."),
                        UiText("原本候補の保護", "Original candidate protection"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                    return;
                }

                MessageBox.Show(
                    UiText($"⚠️ 選択項目の中に重複グループの【原本候補】が {protectedPaths.Count:N0} 件含まれていました。\n\n" +
                        "安全保護規則に従い、原本候補は自動的に保護・除外されました。\n" +
                        $"残りの複製・休眠ファイル（{preview.SafeFileCount:N0} 件）に対して削除確認へ進みます。",
                        $"⚠️ {protectedPaths.Count:N0} original candidates were among the selected items.\n\nThey were automatically protected and excluded. Continue to confirm deletion of the remaining {preview.SafeFileCount:N0} copies or dormant files."),
                    UiText("原本候補の保護（自動除外）", "Original candidates excluded"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
            }

            // 3. 完全削除の最終確認ダイアログ（物理ファイル単位で正確な件数・容量を表示）
            long totalBytes = preview.SafeSizeBytes;
            string sizeFormatted = FileItemNode.FormatBytes(totalBytes);
            var confirm = MessageBox.Show(
                UiText($"選択された {preview.SafeFileCount:N0} 件（合計 {sizeFormatted}）のファイルを【完全に削除】します。\n\n" +
                    "⚠️ 注意:\n" +
                    "・ファイルはごみ箱に入らず完全に削除され、アプリ側から復元することはできません。\n" +
                    "・本当に削除を実行してもよろしいですか？",
                    $"Permanently delete {preview.SafeFileCount:N0} selected files ({sizeFormatted})?\n\n⚠️ These files will bypass the Recycle Bin and cannot be restored by this app. Continue?"),
                UiText("ファイル完全削除の確認", "Confirm permanent deletion"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Stop);

            if (confirm != MessageBoxResult.OK) return;

            // 4. バックグラウンド削除処理（AuditCleanupService により物理1回削除＆属性復元）
            AuditDeleteSelectedButton.IsEnabled = false;
            AuditStartButton.IsEnabled = false;
            GlobalProgressBar.Visibility = Visibility.Visible;
            AuditStatusText.Text = UiText("ファイル削除中...", "Deleting files...");

            FolderMorpher.Contracts.AuditCleanupResultDto result;
            try
            {
                result = await host.CommitAuditCleanupAsync(preview.PlanId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                MessageBox.Show(UiText($"削除計画の実行に失敗しました: {ex.Message}", $"Deletion plan failed: {ex.Message}"), UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            finally
            {
                GlobalProgressBar.Visibility = Visibility.Collapsed;
                AuditDeleteSelectedButton.IsEnabled = true;
                AuditStartButton.IsEnabled = true;
            }

            // 5. データ・UIの最新化（削除されたFullPathを持つ全関連AuditItemを一括除去）
            var deletedPaths = result.DeletedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            _lastAuditItems.RemoveAll(x => deletedPaths.Contains(x.FullPath));
            ApplyAuditFilters();
            UpdateAuditKpiAfterDeletion();

            AuditStatusText.Text = UiText($"削除完了: {result.SuccessCount:N0} 件削除 ({FileItemNode.FormatBytes(result.FreedBytes)} 削減)", $"Deletion complete: {result.SuccessCount:N0} files ({FileItemNode.FormatBytes(result.FreedBytes)} freed)");

            if (result.Errors.Count > 0)
            {
                MessageBox.Show(
                    UiText($"{result.SuccessCount:N0} 件のファイルを削除しました（{FileItemNode.FormatBytes(result.FreedBytes)} 削減）。\n\n" +
                        $"以下の {result.Errors.Count:N0} 件でエラーが発生しました:\n",
                        $"Deleted {result.SuccessCount:N0} files ({FileItemNode.FormatBytes(result.FreedBytes)} freed).\n\n{result.Errors.Count:N0} errors occurred:\n") +
                    string.Join("\n", result.Errors.Take(5)) + (result.Errors.Count > 5 ? UiText($"\n...他 {result.Errors.Count - 5} 件", $"\n...and {result.Errors.Count - 5} more") : ""),
                    UiText("削除完了（一部エラー）", "Deletion complete with errors"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                ShowToast(UiText($"🗑️ {result.SuccessCount:N0} 件のファイルを完全削除しました（{FileItemNode.FormatBytes(result.FreedBytes)} 削減）", $"🗑️ Permanently deleted {result.SuccessCount:N0} files ({FileItemNode.FormatBytes(result.FreedBytes)} freed)"));
            }
        }

        private void UpdateAuditKpiAfterDeletion()
        {
            if (_lastAuditItems == null || _lastAuditSummary == null) return;

            long dupWasted = _lastAuditItems
                .Where(x => x.IssueType == AuditIssueType.Duplicate && !x.IsOriginalCandidate)
                .Sum(x => x.Size);
            int dupCount = _lastAuditItems.Count(x => x.IssueType == AuditIssueType.Duplicate);

            long dormantSize = _lastAuditItems
                .Where(x => x.IssueType == AuditIssueType.Dormant || x.IssueType == AuditIssueType.GraveyardTree)
                .Sum(x => x.Size);
            int dormantCount = _lastAuditItems.Count(x => x.IssueType == AuditIssueType.Dormant || x.IssueType == AuditIssueType.GraveyardTree);

            long versionFamilySize = _lastAuditItems
                .Where(x => x.IssueType == AuditIssueType.VersionFamily)
                .Sum(x => x.Size);
            int versionFamilyCount = _lastAuditItems.Count(x => x.IssueType == AuditIssueType.VersionFamily);

            long extractedArchiveSize = _lastAuditItems
                .Where(x => x.IssueType == AuditIssueType.ExtractedArchive)
                .Sum(x => x.Size);
            int extractedArchiveCount = _lastAuditItems.Count(x => x.IssueType == AuditIssueType.ExtractedArchive);

            _lastAuditSummary.DuplicateWastedBytes = dupWasted;
            _lastAuditSummary.DuplicateCount = dupCount;
            _lastAuditSummary.DormantBytes = dormantSize;
            _lastAuditSummary.DormantCount = dormantCount;
            _lastAuditSummary.VersionFamilyBytes = versionFamilySize;
            _lastAuditSummary.VersionFamilyCount = versionFamilyCount;
            _lastAuditSummary.ExtractedArchiveBytes = extractedArchiveSize;
            _lastAuditSummary.ExtractedArchiveCount = extractedArchiveCount;
            _lastAuditSummary.PathTooLongCount = _lastAuditItems.Count(x => x.IssueType == AuditIssueType.PathTooLong);
            _lastAuditSummary.InvalidCharCount = _lastAuditItems.Count(x => x.IssueType == AuditIssueType.InvalidChar);

            if (AuditKpiReadyToClean != null) AuditKpiReadyToClean.Text = _lastAuditSummary.ReadyToCleanSizeFormatted;
            if (AuditKpiVersionFamily != null) AuditKpiVersionFamily.Text = _lastAuditSummary.VersionFamilySizeFormatted;
            if (AuditKpiDupWasted != null) AuditKpiDupWasted.Text = _lastAuditSummary.DuplicateWastedSizeFormatted;
            if (AuditKpiDormantSize != null) AuditKpiDormantSize.Text = _lastAuditSummary.DormantSizeFormatted;
        }

        private void AuditScoreBreakdown_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is AuditItem item)
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                var sb = new StringBuilder();
                sb.AppendLine(isJa ? $"【整理スコア内訳: {item.FileName}】" : $"[Score Breakdown: {item.FileName}]");
                sb.AppendLine(isJa ? $"総合スコア: {item.WasteScore} 点（{item.ConfidenceDisplay}）" : $"Total Score: {item.WasteScore} pts ({item.ConfidenceDisplay})");
                sb.AppendLine(new string('─', 40));

                if (item.ScoreBreakdown != null && item.ScoreBreakdown.Count > 0)
                {
                    foreach (var factor in item.ScoreBreakdown)
                    {
                        sb.AppendLine($"・{factor.DisplayText}");
                    }
                    sb.AppendLine(new string('─', 40));
                    sb.AppendLine(isJa ? $"合計: {item.WasteScore} 点" : $"Total: {item.WasteScore} pts");
                }
                else
                {
                    sb.AppendLine(item.ScoreBreakdownSummary);
                }

                if (!string.IsNullOrEmpty(item.RelatedActivePath))
                {
                    sb.AppendLine();
                    sb.AppendLine(isJa ? $"参照先/最新版: {item.RelatedActivePath}" : $"Reference/Active: {item.RelatedActivePath}");
                }

                MessageBox.Show(sb.ToString(), isJa ? "整理スコア内訳" : "Score Breakdown", MessageBoxButton.OK, MessageBoxImage.Information);
                e.Handled = true;
            }
        }

        private async void AuditIgnoreFile_Click(object sender, RoutedEventArgs e)
        {
            if (AuditItemsDataGrid?.SelectedItem is AuditItem item)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    await host.AddAuditIgnoreAsync(FolderMorpher.HostClient.AuditDtoMapper.ToDto(item), CancellationToken.None);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(UiText($"除外登録に失敗しました: {ex.Message}", $"Could not ignore the file: {ex.Message}"), UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                _lastAuditItems.Remove(item);
                UpdateAuditKpiAfterDeletion();
                ApplyAuditFilters();
                UpdateIgnoredCountBadge();

                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                ShowToast(isJa 
                    ? $"🛡️ 「{item.FileName}」を整理候補から除外しました（変更されるまで非表示）"
                    : $"🛡️ Ignored \"{item.FileName}\" (hidden until modified)");
            }
        }

        private async void AuditIgnoredListButton_Click(object sender, RoutedEventArgs e)
        {
            List<FolderMorpher.Contracts.AuditIgnoreItemDto> ignoredItems;
            try
            {
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                ignoredItems = await host.GetAuditIgnoresAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(UiText($"除外リストを取得できませんでした: {ex.Message}", $"Could not load the ignored list: {ex.Message}"), UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;

            if (ignoredItems.Count == 0)
            {
                MessageBox.Show(
                    isJa ? "除外（保留）登録されているファイルはありません。" : "No files are currently ignored.",
                    isJa ? "除外リスト" : "Ignored List",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine(isJa ? $"現在 {ignoredItems.Count:N0} 件のファイルが整理候補から除外されています（更新されるまで非表示）:\n" : $"Currently {ignoredItems.Count:N0} files are ignored (hidden until modified):\n");

            foreach (var ig in ignoredItems.Take(15))
            {
                sb.AppendLine($"・{ig.FullPath} ({ig.IgnoredAt:yyyy/MM/dd})");
            }
            if (ignoredItems.Count > 15)
            {
                sb.AppendLine(isJa ? $"\n...他 {ignoredItems.Count - 15} 件" : $"\n...and {ignoredItems.Count - 15} more");
            }

            sb.AppendLine(isJa ? "\n除外リストをすべてリセットして再度整理候補の対象にしますか？" : "\nDo you want to reset the ignore list and re-evaluate these files?");

            var res = MessageBox.Show(sb.ToString(), isJa ? "整理除外リストの管理" : "Manage Ignored List", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    await host.ClearAuditIgnoresAsync();
                }
                catch (Exception ex)
                {
                    MessageBox.Show(UiText($"除外リストをリセットできませんでした: {ex.Message}", $"Could not reset the ignored list: {ex.Message}"), UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                UpdateIgnoredCountBadge();
                ShowToast(isJa ? "除外リストをリセットしました（次回走査時に再評価されます）" : "Ignored list cleared");
            }
        }

        private void AuditOpenExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (AuditItemsDataGrid?.SelectedItem is AuditItem item && !string.IsNullOrEmpty(item.FullPath))
            {
                ShellHelper.SelectInExplorer(item.FullPath);
            }
        }

        private void AuditCopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (AuditItemsDataGrid?.SelectedItem is AuditItem item && !string.IsNullOrEmpty(item.FullPath))
            {
                try
                {
                    Clipboard.SetText(item.FullPath);
                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    ShowToast(isJa ? "完全パスをコピーしました" : "Path copied to clipboard");
                }
                catch { }
            }
        }

        private async void UpdateIgnoredCountBadge()
        {
            if (AuditIgnoredListButton == null) return;
            int count;
            try
            {
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                count = await host.GetAuditIgnoreCountAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to count audit ignores: {ex}");
                return;
            }
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            AuditIgnoredListButton.Content = isJa ? $"🛡️ 除外リスト ({count}件)" : $"🛡️ Ignored List ({count})";
        }
        #endregion
    }
}
