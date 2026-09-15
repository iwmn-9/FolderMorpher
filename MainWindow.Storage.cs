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
using AstraSize.Services;
using FolderMorpher.Models;
using FolderMorpher.Services;
using Microsoft.Win32;

namespace AstraSize
{
    public partial class MainWindow : Window
    {
        #region Tab 1: Storage Explorer & Multi-Tab Management
        private bool _isInitializingTabs = false;

        private async void InitializeStorageTabs()
        {
            StorageTabsItemsControl.ItemsSource = StorageTabs;
            _isInitializingTabs = true;

            try
            {
                var savedPaths = AppSettingsService.Instance.Current.StorageTabPaths;
                int savedActiveIndex = AppSettingsService.Instance.Current.ActiveStorageTabIndex;

                if (savedPaths != null && savedPaths.Count > 0)
                {
                    ScanTabModel? targetActiveTab = null;
                    for (int i = 0; i < savedPaths.Count; i++)
                    {
                        var path = savedPaths[i];
                        var tab = new ScanTabModel
                        {
                            TargetPath = path,
                            TabTitle = Path.GetFileName(path.TrimEnd('\\', '/'))
                        };
                        if (string.IsNullOrEmpty(tab.TabTitle)) tab.TabTitle = path;

                        StorageTabs.Add(tab);
                        await RestoreTabFromCacheAsync(tab, path);

                        if (i == savedActiveIndex)
                        {
                            targetActiveTab = tab;
                        }
                    }

                    if (targetActiveTab == null && StorageTabs.Count > 0)
                    {
                        targetActiveTab = StorageTabs[0];
                    }

                    if (targetActiveTab != null)
                    {
                        SelectTab(targetActiveTab);
                    }
                }
                else
                {
                    var initialTab = new ScanTabModel
                    {
                        TabTitle = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese ? "新規スキャン" : "New Scan",
                        TargetPath = string.Empty,
                        IsSelected = true
                    };
                    StorageTabs.Add(initialTab);
                    SelectTab(initialTab);
                }
            }
            finally
            {
                _isInitializingTabs = false;
            }
        }

        private async Task RestoreTabFromCacheAsync(ScanTabModel tab, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                var cachedRoot = await _historyService.LoadTreeCacheAsync(path);
                if (cachedRoot != null)
                {
                    cachedRoot.IsExpanded = true;
                    tab.RootNode = cachedRoot;
                    tab.FlattenTree();
                    tab.AggregateExtensions();
                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    tab.StatusMessage = isJa ? "⚡ 前回のキャッシュを表示中" : "⚡ Displaying previous cache";
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RestoreTabFromCacheAsync Error for {path}: {ex}");
            }
        }

        private void SaveStorageTabSession()
        {
            if (_isInitializingTabs) return;
            try
            {
                var paths = StorageTabs
                    .Select(t => t.TargetPath?.Trim() ?? string.Empty)
                    .Where(p => !string.IsNullOrEmpty(p))
                    .ToList();

                var settings = AppSettingsService.Instance.Current;
                settings.StorageTabPaths = paths;
                int activeIdx = _currentTab != null ? StorageTabs.IndexOf(_currentTab) : 0;
                settings.ActiveStorageTabIndex = Math.Max(0, activeIdx);
                AppSettingsService.Instance.Save();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveStorageTabSession Error: {ex}");
            }
        }

        private void SelectTab(ScanTabModel tab)
        {
            foreach (var t in StorageTabs) t.IsSelected = false;
            tab.IsSelected = true;
            _currentTab = tab;

            PathTextBox.Text = tab.TargetPath;
            FileTreeDataGrid.ItemsSource = tab.VisibleFlatList;
            if (tab.RootNode != null)
            {
                UpdateDynamicInsightsForNode(tab.RootNode);
            }
            else
            {
                TopFilesDataGrid.ItemsSource = null;
                FolderChildSharesDataGrid.ItemsSource = null;
            }

            UpdateMetricsCards(tab);
            StatusTextBlock.Text = string.IsNullOrEmpty(tab.StatusMessage) 
                ? (LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese ? "準備完了" : "Ready") 
                : tab.StatusMessage;

            SaveStorageTabSession();
        }

        private void StorageTabItem_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ScanTabModel tab)
            {
                SelectTab(tab);
            }
        }

        private void AddStorageTabButton_Click(object sender, RoutedEventArgs e)
        {
            var newTab = new ScanTabModel
            {
                TabTitle = $"タブ {StorageTabs.Count + 1}",
                TargetPath = PathTextBox.Text.Trim(),
                IsSelected = true
            };
            StorageTabs.Add(newTab);
            SelectTab(newTab);
            SaveStorageTabSession();
        }

        private void CloseStorageTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is ScanTabModel tab)
            {
                if (StorageTabs.Count <= 1)
                {
                    MessageBox.Show("最後のタブは閉じることができません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                int idx = StorageTabs.IndexOf(tab);
                StorageTabs.Remove(tab);

                if (tab.IsSelected)
                {
                    int nextIdx = Math.Min(idx, StorageTabs.Count - 1);
                    SelectTab(StorageTabs[nextIdx]);
                }
                SaveStorageTabSession();
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "スキャン対象フォルダの選択",
                InitialDirectory = PathTextBox.Text.Trim()
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                PathTextBox.Text = dialog.FolderName;
                if (_currentTab != null)
                {
                    _currentTab.TargetPath = dialog.FolderName;
                    _currentTab.TabTitle = Path.GetFileName(dialog.FolderName);
                }
            }
        }

        private void PathTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                ScanButton_Click(sender, e);
            }
        }

        private async void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path) || (!Directory.Exists(path) && !File.Exists(path)))
            {
                MessageBox.Show("有効なパス (ローカルまたは UNC) を入力してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            await StartStorageScanAsync(path);
        }

        private async Task StartStorageScanAsync(string path)
        {
            if (_currentTab == null) return;

            _scanCts?.Cancel();
            _scanCts = new CancellationTokenSource();
            var ct = _scanCts.Token;

            ScanButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Visible;
            GlobalProgressBar.Visibility = Visibility.Visible;
            GlobalProgressBar.IsIndeterminate = true;

            _currentTab.TargetPath = path;
            _currentTab.TabTitle = Path.GetFileName(path.TrimEnd('\\', '/'));
            if (string.IsNullOrEmpty(_currentTab.TabTitle)) _currentTab.TabTitle = path;

            // --- ⚡ 0秒キャッシュ即時全展開 ---
            FileItemNode? cachedRoot = null;
            try
            {
                cachedRoot = await _historyService.LoadTreeCacheAsync(path);
                if (cachedRoot != null)
                {
                    cachedRoot.IsExpanded = true;
                    _currentTab.RootNode = cachedRoot;
                    _currentTab.FlattenTree();
                    _currentTab.AggregateExtensions();
                    FileTreeDataGrid.ItemsSource = _currentTab.VisibleFlatList;
                    UpdateDynamicInsightsForNode(cachedRoot);
                    UpdateMetricsCards(_currentTab);
                    StatusTextBlock.Text = $"⚡ 前回のキャッシュを表示中 (バックグラウンドで最新データを走査・差分検出中...)";
                }
            }
            catch
            {
                // キャッシュロード失敗は通常走査にフォールバック
            }

            var progress = new Progress<ScanProgress>(p =>
            {
                ScannedSizeTextBlock.Text = FileItemNode.FormatBytes(p.BytesScanned);
                TotalFilesTextBlock.Text = $"{p.FilesScanned:N0} 項目走査済み";
                StatusTextBlock.Text = $"スキャン中: {p.CurrentPath}";
            });

            try
            {
                // --- バックグラウンド最新スキャン実行 ---
                var (root, summary) = await _scanService.ScanPathAsync(path, progress, ct);
                root.CachedTopFiles = summary.LargestFiles;
                root.CachedExtensionStats = summary.ExtensionStats;

                // --- 差分自動計算＆反映 ---
                bool hadDiff = false;
                if (cachedRoot != null)
                {
                    _historyService.ApplyTreeDiff(root, cachedRoot);
                    hadDiff = root.DiffBytes.HasValue && root.DiffBytes.Value != 0;
                }

                root.IsExpanded = true;
                _currentTab.RootNode = root;
                _currentTab.Summary = summary;
                _currentTab.FlattenTree();
                _currentTab.AggregateExtensions();

                FileTreeDataGrid.ItemsSource = _currentTab.VisibleFlatList;
                UpdateDynamicInsightsForNode(root);
                UpdateMetricsCards(_currentTab);

                // キャッシュ＆スナップショットをバックグラウンド自動保存
                _ = _historyService.SaveTreeCacheAsync(root);
                _ = _historyService.SaveSnapshotAsync(root);
                SaveStorageTabSession();

                string diffInfo = hadDiff && !string.IsNullOrEmpty(root.DiffFormatted) ? $" [差分: {root.DiffFormatted}]" : "";
                StatusTextBlock.Text = summary.IsMftBoosted
                    ? $"⚡ MFT高速スキャン完了 ({summary.ElapsedSeconds}秒): {root.Name} ({FileItemNode.FormatBytes(root.SizeBytes)}){diffInfo}"
                    : $"スキャン完了 ({summary.ElapsedSeconds}秒): {root.Name} ({FileItemNode.FormatBytes(root.SizeBytes)}){diffInfo}";
            }
            catch (OperationCanceledException)
            {
                StatusTextBlock.Text = "スキャンが中止されました。";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"スキャンエラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                StatusTextBlock.Text = "スキャン失敗";
            }
            finally
            {
                ScanButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Collapsed;
                GlobalProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _scanCts?.Cancel();
        }

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_currentTab != null)
            {
                _currentTab.FilterKeyword = FilterTextBox.Text.Trim();
                _currentTab.FlattenTree();
                FileTreeDataGrid.ItemsSource = _currentTab.VisibleFlatList;
            }
        }

        private void UpdateMetricsCards(ScanTabModel tab)
        {
            MftBoostBadge.Visibility = (tab.Summary?.IsMftBoosted == true) ? Visibility.Visible : Visibility.Collapsed;

            if (tab.RootNode != null)
            {
                ScannedSizeTextBlock.Text = FileItemNode.FormatBytes(tab.RootNode.SizeBytes);
                TotalFilesTextBlock.Text = $"{tab.RootNode.FileCount:N0} ファイル / {tab.RootNode.FolderCount:N0} フォルダ";

                // 最大ファイル (Top 1)
                LargestFileInfo? largest = null;
                if (tab.RootNode.CachedTopFiles != null && tab.RootNode.CachedTopFiles.Count > 0)
                {
                    largest = tab.RootNode.CachedTopFiles.FirstOrDefault();
                }
                else
                {
                    var (topFiles, _) = DiskScanService.GetInsightsForNode(tab.RootNode);
                    tab.RootNode.CachedTopFiles = topFiles;
                    largest = topFiles.FirstOrDefault();
                }

                if (largest != null)
                {
                    LargestFileSizeTextBlock.Text = largest.FormattedSize;
                    LargestFileNameTextBlock.Text = largest.Name;
                }
                else
                {
                    LargestFileSizeTextBlock.Text = "--";
                    LargestFileNameTextBlock.Text = "--";
                }

                // 前回スキャンとの差分推移
                if (tab.RootNode.DiffBytes.HasValue && tab.RootNode.DiffBytes.Value != 0)
                {
                    TrendDiffTextBlock.Text = tab.RootNode.DiffFormatted;
                    LastScanDateTextBlock.Text = "前回キャッシュ比較";
                }
                else if (tab.RootNode.DiffBytes.HasValue && tab.RootNode.DiffBytes.Value == 0)
                {
                    TrendDiffTextBlock.Text = "±0 B (変化なし)";
                    LastScanDateTextBlock.Text = "前回キャッシュ比較";
                }
                else
                {
                    TrendDiffTextBlock.Text = "比較データなし";
                    LastScanDateTextBlock.Text = "初回スキャン";
                }
            }
            else
            {
                ScannedSizeTextBlock.Text = "0.00 GB";
                TotalFilesTextBlock.Text = "0 ファイル / 0 フォルダ";
                LargestFileSizeTextBlock.Text = "--";
                LargestFileNameTextBlock.Text = "--";
                TrendDiffTextBlock.Text = "比較データなし";
                LastScanDateTextBlock.Text = "初回スキャン";
            }
        }

        private void ExpandCollapseButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is FileItemNode node && _currentTab != null)
            {
                node.IsExpanded = !node.IsExpanded;
                _currentTab.FlattenTree();
                FileTreeDataGrid.ItemsSource = _currentTab.VisibleFlatList;
            }
        }

        private void FileTreeDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileTreeDataGrid.SelectedItem is FileItemNode item)
            {
                if (item.IsDirectory)
                {
                    item.IsExpanded = !item.IsExpanded;
                    _currentTab?.FlattenTree();
                    FileTreeDataGrid.ItemsSource = _currentTab?.VisibleFlatList;
                }
                else
                {
                    OpenInExplorer(item.FullPath);
                }
            }
        }

        private void FileTreeDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileTreeDataGrid.SelectedItem is FileItemNode node)
            {
                UpdateDynamicInsightsForNode(node);
            }
        }

        private void UpdateDynamicInsightsForNode(FileItemNode node)
        {
            if (InsightsTargetScopeTextBlock == null || TopFilesDataGrid == null || FolderChildSharesDataGrid == null) return;
            InsightsTargetScopeTextBlock.Text = $"スコープ: {node.Name}";

            // 1. Direct children breakdown (relative shares in this folder) - 即時表示
            long parentSize = node.Size > 0 ? node.Size : 1;
            var childShares = node.Children
                .OrderByDescending(c => c.Size)
                .Select(c => new FolderChildShareItem
                {
                    OriginalNode = c,
                    Name = c.Name,
                    FullPath = c.FullPath,
                    Size = c.Size,
                    IsDirectory = c.IsDirectory,
                    RelativeSharePercentage = Math.Min(100.0, (double)c.Size / parentSize * 100.0),
                    FileCount = c.FileCount,
                    FolderCount = c.FolderCount
                })
                .ToList();

            FolderChildSharesDataGrid.ItemsSource = childShares;

            // 2. Top 10 largest files in this subtree - キャッシュがあれば即時、未計算なら非同期バックグラウンドでUIブロック回避
            if (node.CachedTopFiles != null && node.CachedTopFiles.Count > 0)
            {
                TopFilesDataGrid.ItemsSource = node.CachedTopFiles;
            }
            else
            {
                // バックグラウンドで非同期計算（UIスレッドを1ミリ秒も止めない）
                _ = Task.Run(() =>
                {
                    var (computedTop, _) = DiskScanService.GetInsightsForNode(node);
                    node.CachedTopFiles = computedTop;
                    Dispatcher.InvokeAsync(() =>
                    {
                        // ユーザーが別のノードへ切り替えていないか確認して反映
                        if (FileTreeDataGrid.SelectedItem == node || _currentTab?.RootNode == node)
                        {
                            TopFilesDataGrid.ItemsSource = computedTop;
                        }
                    });
                });
            }
        }

        private void FolderChildSharesDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FolderChildSharesDataGrid.SelectedItem is FolderChildShareItem shareItem && shareItem.OriginalNode != null)
            {
                var targetNode = shareItem.OriginalNode;
                if (!targetNode.IsDirectory)
                {
                    OpenExplorerWithSelection(targetNode.FullPath);
                    return;
                }

                SelectAndFocusTreeNode(targetNode);
            }
        }

        private void SelectAndFocusTreeNode(FileItemNode targetNode)
        {
            if (_currentTab == null) return;

            // Expand all ancestors to make node visible
            var curr = targetNode.Parent;
            while (curr != null)
            {
                curr.IsExpanded = true;
                curr = curr.Parent;
            }

            // Flatten tree and re-bind
            _currentTab.FlattenTree();
            FileTreeDataGrid.ItemsSource = _currentTab.VisibleFlatList;

            // Select and scroll to node
            targetNode.IsSelected = true;
            FileTreeDataGrid.SelectedItem = targetNode;
            FileTreeDataGrid.ScrollIntoView(targetNode);

            // Update insights
            UpdateDynamicInsightsForNode(targetNode);
        }

        private void TopFilesDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (TopFilesDataGrid.SelectedItem is LargestFileInfo fileInfo && !string.IsNullOrEmpty(fileInfo.FullPath))
            {
                OpenExplorerWithSelection(fileInfo.FullPath);
            }
        }

        private void CtxTopFileOpenExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (TopFilesDataGrid.SelectedItem is LargestFileInfo fileInfo && !string.IsNullOrEmpty(fileInfo.FullPath))
            {
                OpenExplorerWithSelection(fileInfo.FullPath);
            }
        }

        private void CtxTopFileCopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (TopFilesDataGrid.SelectedItem is LargestFileInfo fileInfo && !string.IsNullOrEmpty(fileInfo.FullPath))
            {
                Clipboard.SetText(fileInfo.FullPath);
                ShowToast($"パスをコピーしました: {fileInfo.Name}");
            }
        }

        private void OpenExplorerWithSelection(string filePath)
        {
            try
            {
                if (File.Exists(filePath) || Directory.Exists(filePath))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{filePath}\"",
                        UseShellExecute = true
                    });
                }
                else
                {
                    MessageBox.Show($"対象のパスが見つかりません:\n{filePath}", "通知", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"エクスプローラー起動エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CtxOpenLiveAcl_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeDataGrid.SelectedItem is FileItemNode item && item.IsDirectory)
            {
                NavTabLiveAcl.IsChecked = true;
                LiveAclStudioControl.OpenFolder(item.FullPath);
                ShowToast($"実環境 権限コントロールを開きました: {item.Name}");
            }
        }

        private void FileTreeDataGrid_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && FileTreeDataGrid.SelectedItem is FileItemNode node)
            {
                var data = new DataObject("FolderMorpherSourceNode", node);
                DragDrop.DoDragDrop(FileTreeDataGrid, data, DragDropEffects.Copy | DragDropEffects.Move);
            }
        }

        private void CtxOpenExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeDataGrid.SelectedItem is FileItemNode item)
            {
                OpenInExplorer(item.FullPath);
            }
        }

        private void OpenInExplorer(string fullPath)
        {
            try
            {
                if (File.Exists(fullPath)) Process.Start("explorer.exe", $"/select,\"{fullPath}\"");
                else if (Directory.Exists(fullPath)) Process.Start("explorer.exe", $"\"{fullPath}\"");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"エクスプローラー起動エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void CtxCopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeDataGrid.SelectedItem is FileItemNode item)
            {
                Clipboard.SetText(item.FullPath);
                ShowToast($"パスをコピーしました: {item.FullPath}");
            }
        }

        private void CtxSendToSimulation_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeDataGrid.SelectedItem is FileItemNode item)
            {
                var simNode = _simService.ConvertToSimNode(item);
                _simRootFolders.Add(simNode);
                NavTabSimulation.IsChecked = true;
                SimMockTreeView.ItemsSource = _simRootFolders;
                ShowToast($"モックツリーに配置しました: {item.Name}");
            }
        }

        private async void CtxScanSubtree_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeDataGrid.SelectedItem is FileItemNode item && item.IsDirectory)
            {
                PathTextBox.Text = item.FullPath;
                await StartStorageScanAsync(item.FullPath);
            }
        }

        private void CtxEditAcl_Click(object sender, RoutedEventArgs e) => CtxOpenLiveAcl_Click(sender, e);

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTab?.RootNode == null)
            {
                MessageBox.Show("エクスポートするスキャンデータがありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "スキャン結果を保存",
                Filter = "Excelブック (*.xlsx)|*.xlsx|CSVファイル (*.csv)|*.csv",
                InitialDirectory = GetDefaultExportDirectory(),
                FileName = $"ScanResult_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
            };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    if (dialog.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    {
                        var excelService = new ExcelReportService();
                        excelService.ExportStorageScanResult(dialog.FileName, _currentTab.TargetPath, _currentTab.VisibleFlatList);
                        ShowToast($"📊 {Path.GetFileName(dialog.FileName)} を出力しました");
                    }
                    else
                    {
                        var sb = new StringBuilder();
                        sb.Append('\uFEFF');
                        sb.AppendLine("名前,パス,容量,全体占有率,ファイル数,フォルダ数,最終更新");
                        foreach (var item in _currentTab.VisibleFlatList)
                        {
                            sb.AppendLine($"\"{item.Name}\",\"{item.FullPath}\",\"{item.FormattedSize}\",\"{item.PercentageFormatted}\",\"{item.FileCount}\",\"{item.FolderCount}\",\"{item.LastModified:yyyy/MM/dd HH:mm}\"");
                        }
                        File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                        ShowToast($"📄 {Path.GetFileName(dialog.FileName)} を出力しました");
                    }

                    Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"出力エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show("対象パスが入力されていません。", "案内", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var history = await _historyService.GetHistoryForPathAsync(path);
            var historyWin = new HistoryWindow(path, history) { Owner = this };
            historyWin.ShowDialog();
        }
        #endregion
    }
}