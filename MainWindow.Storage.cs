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
        #region Tab 1: Storage Explorer & Multi-Tab Management
        private bool _isInitializingTabs = false;
        private readonly Dictionary<FileItemNode, Task> _storageChildrenLoads = new();

        private async Task InitializeStorageTabsAsync()
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
                var cacheHost = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                var cachedDto = await cacheHost.LoadCachedTreeAsync(path);
                var cachedRoot = cachedDto == null ? null : FolderMorpher.HostClient.StorageNodeMapper.ToViewNode(cachedDto);
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
                TabTitle = UiText($"タブ {StorageTabs.Count + 1}", $"Tab {StorageTabs.Count + 1}"),
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
                    MessageBox.Show(UiText("最後のタブは閉じることができません。", "The last tab cannot be closed."),
                        UiText("情報", "Information"), MessageBoxButton.OK, MessageBoxImage.Information);
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
                Title = UiText("スキャン対象フォルダの選択", "Select a folder to scan"),
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
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(UiText("有効なパス (ローカルまたは UNC) を入力してください。", "Enter a valid local or UNC path."),
                    UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
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
                var cacheHost = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync(ct);
                var cachedDto = await cacheHost.LoadCachedTreeAsync(path);
                cachedRoot = cachedDto == null ? null : FolderMorpher.HostClient.StorageNodeMapper.ToViewNode(cachedDto);
                if (cachedRoot != null)
                {
                    cachedRoot.IsExpanded = true;
                    _currentTab.RootNode = cachedRoot;
                    _currentTab.FlattenTree();
                    _currentTab.AggregateExtensions();
                    FileTreeDataGrid.ItemsSource = _currentTab.VisibleFlatList;
                    UpdateDynamicInsightsForNode(cachedRoot);
                    UpdateMetricsCards(_currentTab);
                    StatusTextBlock.Text = UiText("⚡ 前回のキャッシュを表示中 (バックグラウンドで最新データを走査・差分検出中...)",
                        "⚡ Showing cached results while scanning for changes in the background...");
                }
            }
            catch
            {
                // キャッシュロード失敗は通常走査にフォールバック
            }

            var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync(ct);

            var progress = new Progress<FolderMorpher.Contracts.StorageScanProgressDto>(p =>
            {
                ScannedSizeTextBlock.Text = FileItemNode.FormatBytes(p.ScannedBytes);
                TotalFilesTextBlock.Text = UiText($"{p.ScannedFilesCount:N0} 項目走査済み", $"{p.ScannedFilesCount:N0} items scanned");
                StatusTextBlock.Text = UiText($"スキャン中: {p.CurrentDirectory}", $"Scanning: {p.CurrentDirectory}");
            });

            try
            {
                // --- バックグラウンド最新スキャン実行 (Host IPC経由) ---
                var scanResult = await host.ScanStorageAsync(new FolderMorpher.Contracts.StorageScanRequestDto { TargetPath = path }, progress, ct);
                var root = scanResult.RootNode == null ? null : FolderMorpher.HostClient.StorageNodeMapper.ToViewNode(scanResult.RootNode);
                if (root == null)
                {
                    throw new InvalidOperationException(scanResult.ErrorMessage ?? UiText("スキャン結果を取得できませんでした。", "Could not retrieve scan results."));
                }

                var summary = new ScanSummary
                {
                    TargetPath = scanResult.TargetPath,
                    TotalBytes = scanResult.TotalBytes,
                    TotalFiles = scanResult.TotalFiles,
                    TotalFolders = scanResult.TotalFolders,
                    LargestFiles = scanResult.Top10Files.Select(FolderMorpher.HostClient.StorageNodeMapper.ToViewFile).ToList(),
                    ScanMode = scanResult.ScanMode,
                    ElapsedSeconds = scanResult.Elapsed.TotalSeconds
                };
                root.CachedTopFiles = summary.LargestFiles;
                root.CachedExtensionStats = summary.ExtensionStats;

                // --- 差分自動計算＆反映 ---
                bool hadDiff = root.DiffBytes.HasValue && root.DiffBytes.Value != 0;

                root.IsExpanded = true;
                _currentTab.RootNode = root;
                _currentTab.Summary = summary;
                _currentTab.FlattenTree();
                _currentTab.AggregateExtensions();

                FileTreeDataGrid.ItemsSource = _currentTab.VisibleFlatList;
                UpdateDynamicInsightsForNode(root);
                UpdateMetricsCards(_currentTab);

                // Host owns cache and snapshot persistence after the scan.
                SaveStorageTabSession();

                string diffInfo = hadDiff && !string.IsNullOrEmpty(root.DiffFormatted)
                    ? UiText($" [差分: {root.DiffFormatted}]", $" [Change: {root.DiffFormatted}]") : "";
                StatusTextBlock.Text = summary.IsMftBoosted
                    ? UiText($"⚡ MFT高速スキャン完了 ({summary.ElapsedSeconds}秒): {root.Name} ({FileItemNode.FormatBytes(root.SizeBytes)}){diffInfo}",
                        $"⚡ MFT scan complete ({summary.ElapsedSeconds}s): {root.Name} ({FileItemNode.FormatBytes(root.SizeBytes)}){diffInfo}")
                    : UiText($"スキャン完了 ({summary.ElapsedSeconds}秒): {root.Name} ({FileItemNode.FormatBytes(root.SizeBytes)}){diffInfo}",
                        $"Scan complete ({summary.ElapsedSeconds}s): {root.Name} ({FileItemNode.FormatBytes(root.SizeBytes)}){diffInfo}");
            }
            catch (OperationCanceledException)
            {
                StatusTextBlock.Text = UiText("スキャンが中止されました。", "Scan canceled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(UiText($"スキャンエラー: {ex.Message}", $"Scan error: {ex.Message}"),
                    UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                StatusTextBlock.Text = UiText("スキャン失敗", "Scan failed");
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

        private void UpdateMetricsCards(ScanTabModel tab)
        {
            MftBoostBadge.Visibility = (tab.Summary?.IsMftBoosted == true) ? Visibility.Visible : Visibility.Collapsed;

            if (tab.RootNode != null)
            {
                ScannedSizeTextBlock.Text = FileItemNode.FormatBytes(tab.RootNode.SizeBytes);
                TotalFilesTextBlock.Text = UiText($"{tab.RootNode.FileCount:N0} ファイル / {tab.RootNode.FolderCount:N0} フォルダ",
                    $"{tab.RootNode.FileCount:N0} Files / {tab.RootNode.FolderCount:N0} Folders");

                // 前回スキャンとの差分推移
                if (tab.RootNode.DiffBytes.HasValue && tab.RootNode.DiffBytes.Value != 0)
                {
                    TrendDiffTextBlock.Text = tab.RootNode.DiffFormatted;
                    LastScanDateTextBlock.Text = UiText("前回キャッシュ比較", "Compared with previous cache");
                }
                else if (tab.RootNode.DiffBytes.HasValue && tab.RootNode.DiffBytes.Value == 0)
                {
                    TrendDiffTextBlock.Text = UiText("±0 B (変化なし)", "±0 B (no change)");
                    LastScanDateTextBlock.Text = UiText("前回キャッシュ比較", "Compared with previous cache");
                }
                else
                {
                    TrendDiffTextBlock.Text = UiText("比較データなし", "No comparison data");
                    LastScanDateTextBlock.Text = UiText("初回スキャン", "Initial scan");
                }
            }
            else
            {
                ScannedSizeTextBlock.Text = "0.00 GB";
                TotalFilesTextBlock.Text = UiText("0 ファイル / 0 フォルダ", "0 Files / 0 Folders");
                TrendDiffTextBlock.Text = UiText("比較データなし", "No comparison data");
                LastScanDateTextBlock.Text = UiText("初回スキャン", "Initial scan");
            }
        }

        private async void ExpandCollapseButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is FileItemNode node && _currentTab != null)
            {
                e.Handled = true;
                await ToggleStorageNodeAsync(node);
            }
        }

        private async void FileTreeDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject source &&
                (source is Button || FindVisualParent<Button>(source) != null)) return;
            if (FileTreeDataGrid.SelectedItem is FileItemNode item)
            {
                if (item.IsDirectory)
                {
                    await ToggleStorageNodeAsync(item);
                }
                else
                {
                    OpenInExplorer(item.FullPath);
                }
            }
        }

        internal async Task ToggleStorageNodeAsync(FileItemNode node)
        {
            var tab = _currentTab;
            if (tab == null) return;
            try
            {
                var expand = !node.IsExpanded;
                if (expand)
                    await EnsureStorageChildrenLoadedAsync(node, tab);
                node.IsExpanded = expand;
                tab.FlattenTree();
                if (ReferenceEquals(_currentTab, tab))
                    FileTreeDataGrid.ItemsSource = tab.VisibleFlatList;
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = UiText($"フォルダーを開けません: {ex.Message}", $"Could not open folder: {ex.Message}");
                Debug.WriteLine($"Storage expansion failed for {node.FullPath}: {ex}");
            }
        }

        private async Task EnsureStorageChildrenLoadedAsync(FileItemNode node, ScanTabModel tab)
        {
            if (!node.HasUnloadedChildren) return;
            if (!_storageChildrenLoads.TryGetValue(node, out var load))
            {
                load = LoadStorageChildrenAsync(node, tab);
                _storageChildrenLoads.Add(node, load);
            }
            try { await load; }
            finally { _storageChildrenLoads.Remove(node); }
        }

        private static async Task LoadStorageChildrenAsync(FileItemNode node, ScanTabModel tab)
        {
            var rootPath = tab.RootNode?.FullPath ?? throw new InvalidOperationException(UiText("スキャンルートがありません。", "Scan root is missing."));
            var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
            var children = await host.GetStorageChildrenAsync(rootPath, node.FullPath);
            if (children.Count == 0)
                throw new InvalidOperationException(UiText("キャッシュに子要素が見つかりません。", "No children found in the cache."));
            node.Children.Clear();
            foreach (var child in children)
                node.Children.Add(FolderMorpher.HostClient.StorageNodeMapper.ToViewNode(child, node));
            node.HasUnloadedChildren = false;
        }

        private void FileTreeDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (FileTreeDataGrid.SelectedItem is FileItemNode node)
            {
                UpdateDynamicInsightsForNode(node);
            }
        }

        private async void UpdateDynamicInsightsForNode(FileItemNode node)
        {
            if (InsightsTargetScopeTextBlock == null || TopFilesDataGrid == null || FolderChildSharesDataGrid == null) return;
            var selectedTab = _currentTab;
            InsightsTargetScopeTextBlock.Text = UiText($"スコープ: {node.Name}", $"Scope: {node.Name}");
            if (selectedTab is { } tab && node.HasUnloadedChildren)
            {
                try { await EnsureStorageChildrenLoadedAsync(node, tab); }
                catch (Exception ex) { Debug.WriteLine($"Storage insight child load failed for {node.FullPath}: {ex}"); }
            }
            if (!ReferenceEquals(selectedTab, _currentTab)) return;

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
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    var dto = FolderMorpher.HostClient.StorageNodeMapper.ToFlatDto(node);
                    var result = await host.GetStorageTopFilesAsync(_currentTab?.RootNode?.FullPath ?? node.FullPath, dto, CancellationToken.None);
                    var computedTop = result.Select(FolderMorpher.HostClient.StorageNodeMapper.ToViewFile).ToList();
                    node.CachedTopFiles = computedTop;
                    if (FileTreeDataGrid.SelectedItem == node || _currentTab?.RootNode == node)
                    {
                        TopFilesDataGrid.ItemsSource = computedTop;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to load storage insights for {node.FullPath}: {ex}");
                }
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
                ShowToast(UiText($"パスをコピーしました: {fileInfo.Name}", $"Path copied: {fileInfo.Name}"));
            }
        }

        private void OpenExplorerWithSelection(string filePath) { ShellHelper.SelectInExplorer(filePath); }

        private void CtxOpenLiveAcl_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeDataGrid.SelectedItem is FileItemNode item && item.IsDirectory)
            {
                NavTabLiveAcl.IsChecked = true;
                LiveAclStudioControl.OpenFolder(item.FullPath);
                ShowToast(UiText($"実環境 権限コントロールを開きました: {item.Name}", $"Opened Permission Control: {item.Name}"));
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

        private void OpenInExplorer(string fullPath) { ShellHelper.SelectInExplorer(fullPath); }

        private void CtxCopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeDataGrid.SelectedItem is FileItemNode item)
            {
                Clipboard.SetText(item.FullPath);
                ShowToast(UiText($"パスをコピーしました: {item.FullPath}", $"Path copied: {item.FullPath}"));
            }
        }

        private async void CtxSendToSimulation_Click(object sender, RoutedEventArgs e)
        {
            if (FileTreeDataGrid.SelectedItem is FileItemNode item)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    var dto = await host.ConvertStorageToMigrationAsync(
                        FolderMorpher.HostClient.StorageNodeMapper.ToTreeDto(item), CancellationToken.None);
                    _simRootFolders.Add(FolderMorpher.HostClient.MigrationDtoMapper.ToViewNode(dto));
                    NavTabSimulation.IsChecked = true;
                    SimMockTreeView.ItemsSource = _simRootFolders;
                    ShowToast(UiText($"モックツリーに配置しました: {item.Name}", $"Added to migration tree: {item.Name}"));
                }
                catch (Exception ex)
                {
                    MessageBox.Show(UiText($"移行ツリーへの追加に失敗しました: {ex.Message}", $"Could not add to migration tree: {ex.Message}"),
                        UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
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

        private async void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTab?.RootNode == null)
            {
                MessageBox.Show(UiText("エクスポートするスキャンデータがありません。", "No scan data to export."),
                    UiText("情報", "Information"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = UiText("スキャン結果を保存", "Save scan results"),
                Filter = UiText("Excelブック (*.xlsx)|*.xlsx|CSVファイル (*.csv)|*.csv", "Excel workbook (*.xlsx)|*.xlsx|CSV file (*.csv)|*.csv"),
                InitialDirectory = GetDefaultExportDirectory(),
                FileName = $"ScanResult_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
            };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    await host.ExportStorageScanAsync(dialog.FileName, _currentTab.TargetPath,
                        _currentTab.VisibleFlatList.Select(FolderMorpher.HostClient.StorageNodeMapper.ToFlatDto).ToList(), CancellationToken.None);
                    ShowToast(UiText($"📊 {Path.GetFileName(dialog.FileName)} を出力しました", $"📊 Exported {Path.GetFileName(dialog.FileName)}"));

                    ShellHelper.SelectInExplorer(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(UiText($"出力エラー: {ex.Message}", $"Export error: {ex.Message}"),
                        UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void HistoryButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(UiText("対象パスが入力されていません。", "No target path was entered."),
                    UiText("案内", "Notice"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
            var history = (await host.GetStorageHistoryAsync(path))
                .Select(FolderMorpher.HostClient.HistoryDtoMapper.ToView).ToList();
            var historyWin = new HistoryWindow(path, history) { Owner = this };
            historyWin.ShowDialog();
        }
        #endregion
    }
}
