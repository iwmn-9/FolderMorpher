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
        // Core Services
        private readonly DiskScanService _scanService = new();
        private readonly StorageHistoryService _historyService = new();
        private readonly AclService _aclService = new();
        private readonly LinkFixService _linkFixService = new();
        private readonly ActiveDirectoryService _adService = new();
        private readonly SimulationProjectService _simService = new();
        private readonly AuditReportService _auditService = new();
        private readonly OfficeLinkFixService _officeLinkService = new();
        private readonly MediaOptimizerService _mediaService = new();
        private readonly ExcelReportService _excelService = new();

        // Cancellation Tokens
        private CancellationTokenSource? _scanCts;
        private CancellationTokenSource? _linkFixCts;
        private CancellationTokenSource? _auditCts;
        private CancellationTokenSource? _mediaCts;

        // State for Audit & Media
        private AuditSummary? _lastAuditSummary;
        private List<AuditItem> _lastAuditItems = new();
        private readonly ObservableCollection<AuditItem> _auditVisibleItems = new();
        private DispatcherTimer? _auditFilterDebounceTimer;
        private string _auditSortProperty = "Default";
        private bool _auditSortDescending = false;
        private MediaOptimizeSummary? _lastMediaSummary;
        private List<MediaItem> _lastMediaImages = new();
        private List<MediaItem> _lastMediaVideos = new();

        // Multi-Tab Storage Management
        public ObservableCollection<ScanTabModel> StorageTabs { get; set; } = new();
        private ScanTabModel? _currentTab;

        // Simulation Studio State
        private readonly ObservableCollection<SimFolderNode> _simRootFolders = new();
        private readonly ObservableCollection<AdPrincipalItem> _adPrincipals = new();
        private SimFolderNode? _selectedSimNode;
        private SimAclEntry? _currentEditingAcl;

        // Live ACL & Effective Access Services
        private readonly EffectiveAccessService _effectiveAccessService = new();
        private Action? _onSecModalAppliedCallback;

        // Toast notification timer
        private DispatcherTimer? _toastTimer;

        private string GetDefaultExportDirectory()
        {
            try
            {
                var custom = AppSettingsService.Instance.Current.CacheWriteCustomPath;
                if (!string.IsNullOrWhiteSpace(custom) && Directory.Exists(custom)) return custom;
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (Directory.Exists(desktop)) return desktop;
            }
            catch { }
            return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        }

        // Drag & Drop State (枠外ドロップ解除 & 広域受容 & Escキャンセル保護)
        private bool _droppedInSelfContainer = false;
        private bool _dragCancelled = false;
        private Point _cardDragStartPoint;

        private Point _simDragStartPoint;
        private bool _isSimNodeDragging = false;
        private bool _simNodeDroppedInTree = false;
        private bool _simDragCancelled = false;

        private void OnCardQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (e.EscapePressed || e.Action == DragAction.Cancel)
            {
                _dragCancelled = true;
            }
        }

        private void OnSimQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (e.EscapePressed || e.Action == DragAction.Cancel)
            {
                _simDragCancelled = true;
            }
        }

        public MainWindow()
        {
            InitializeComponent();

            LiveAclStudioControl.InitializeServices(_aclService, _adService, _effectiveAccessService);
            LiveAclStudioControl.ToastRequested += ShowToast;
            LiveAclStudioControl.EditSecurityRequested += (acl, folderName, fullPath, onApplied) =>
            {
                OpenSecurityModal(acl, folderName, fullPath, onApplied);
            };

            AuditItemsDataGrid.ItemsSource = _auditVisibleItems;
            AuditItem.GlobalCheckedChanged += (item) => UpdateLiveSelectedReduction();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                InitializeStorageTabs();
                InitializeSimulationStudio();
                await LoadAdPrincipalsAsync();

                LocalizationService.Instance.LanguageChanged += ApplyLocalization;
                ApplyLocalization();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow_Loaded Error: {ex}");
            }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            SaveStorageTabSession();
            base.OnClosing(e);
        }

        #region Toast Notification Helper
        private void ShowToast(string message)
        {
            ToastNotificationText.Text = message;
            ToastNotificationBorder.Visibility = Visibility.Visible;
            _toastTimer?.Stop();
            _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.8) };
            _toastTimer.Tick += (s, e) =>
            {
                ToastNotificationBorder.Visibility = Visibility.Collapsed;
                _toastTimer.Stop();
            };
            _toastTimer.Start();
        }
        #endregion

        #region Navigation Tabs
        private void NavTab_Checked(object sender, RoutedEventArgs e)
        {
            if (StorageTabPanel == null || LiveAclStudioControl == null || SimulationTabPanel == null || LinkFixTabPanel == null ||
                AuditTabPanel == null || MediaTabPanel == null)
                return;

            StorageTabPanel.Visibility = Visibility.Collapsed;
            LiveAclStudioControl.Visibility = Visibility.Collapsed;
            SimulationTabPanel.Visibility = Visibility.Collapsed;
            LinkFixTabPanel.Visibility = Visibility.Collapsed;
            AuditTabPanel.Visibility = Visibility.Collapsed;
            MediaTabPanel.Visibility = Visibility.Collapsed;

            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;

            if (NavTabStorage.IsChecked == true)
            {
                StorageTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = isJa ? "モード: 容量分析 & 監視 (Storage Explorer)" : "Mode: Storage Explorer";
            }
            else if (NavTabLiveAcl.IsChecked == true)
            {
                LiveAclStudioControl.Visibility = Visibility.Visible;
                StatusTextBlock.Text = isJa ? "モード: 実環境 権限コントロール (Live ACL)" : "Mode: Live ACL Control";
                if (string.IsNullOrWhiteSpace(LiveAclStudioControl.CurrentPath) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    LiveAclStudioControl.SetDefaultPath(PathTextBox.Text);
                }
            }
            else if (NavTabSimulation.IsChecked == true)
            {
                SimulationTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = isJa ? "モード: 移行シミュレーションスタジオ (FolderMorph Studio)" : "Mode: Simulation Studio";
                if (string.IsNullOrWhiteSpace(SimSourcePathTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    SimSourcePathTextBox.Text = PathTextBox.Text;
                }
            }
            else if (NavTabLinkFix.IsChecked == true)
            {
                LinkFixTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = isJa ? "モード: ショートカット ＆ Officeリンク修復 (LinkFixer)" : "Mode: LinkFixer";
                if (string.IsNullOrWhiteSpace(LinkSearchScopeTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    LinkSearchScopeTextBox.Text = PathTextBox.Text;
                }
            }
            else if (NavTabAudit.IsChecked == true)
            {
                AuditTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = isJa ? "モード: ファイルサーバー健全化 ＆ 断捨離 (衛生監査・容量削減)" : "Mode: Audit & Hygiene";
                if (string.IsNullOrWhiteSpace(AuditPathTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    AuditPathTextBox.Text = PathTextBox.Text;
                }
            }
            else if (NavTabMedia.IsChecked == true)
            {
                MediaTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = isJa ? "モード: メディア・オプティマイザ (写真軽量化 ＆ 巨大動画攻略)" : "Mode: Media Optimizer";
                if (string.IsNullOrWhiteSpace(MediaPathTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    MediaPathTextBox.Text = PathTextBox.Text;
                }
            }
        }

        private bool _isSidebarCollapsed = false;

        private void SidebarToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isSidebarCollapsed = !_isSidebarCollapsed;
            if (_isSidebarCollapsed)
            {
                SidebarBorder.Width = 58;
                SidebarBrandPanel.Visibility = Visibility.Collapsed;
                SidebarFooterExpandedPanel.Visibility = Visibility.Collapsed;
                SidebarFooterCollapsedPanel.Visibility = Visibility.Visible;
                SidebarToggleButton.ToolTip = "サイドバーを展開";
            }
            else
            {
                SidebarBorder.Width = 220;
                SidebarBrandPanel.Visibility = Visibility.Visible;
                SidebarFooterExpandedPanel.Visibility = Visibility.Visible;
                SidebarFooterCollapsedPanel.Visibility = Visibility.Collapsed;
                SidebarToggleButton.ToolTip = "サイドバーを収縮";
            }
        }
        #endregion

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

        // Tab 2 (Live ACL & Effective Access Studio) has been extracted to Views/LiveAclStudio.xaml.cs

        #region Tab 3: Migration Simulation Studio (FolderMorph Studio)
        private void InitializeSimulationStudio()
        {
            SimMockTreeView.ItemsSource = _simRootFolders;
            AdPrincipalsItemsControl.ItemsSource = _adPrincipals;

            if (_simRootFolders.Count == 0)
            {
                var root = new SimFolderNode { Name = "新共有サーバー_Share [新設ルート]", InheritAcl = true, Level = 0 };
                var sub1 = new SimFolderNode { Name = "01_経営企画・総務統括", InheritAcl = true, Level = 1, Parent = root };
                var sub2 = new SimFolderNode { Name = "01-1_役員会議・機密資料", InheritAcl = true, Level = 2, Parent = sub1 };
                var sub3 = new SimFolderNode { Name = "2025年度_議事録アーカイブ", InheritAcl = true, Level = 3, Parent = sub2 };

                sub2.Children.Add(sub3);
                sub1.Children.Add(sub2);
                root.Children.Add(sub1);
                _simRootFolders.Add(root);

                sub1.MappedSourcePaths.Add(@"\\OldServer\Share\01_総務部");
                sub1.MappedSourcePaths.Add(@"\\OldServer\Share\01_総務部\株主総会");
                sub1.NotifyMappingChanged();
            }

            if (_adService.IsDomainJoined)
            {
                DomainStatusText.Text = $"🟢 {_adService.CurrentDomainName.ToUpperInvariant()}";
                DomainStatusBadge.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#DCFCE7")!;
            }
            else
            {
                DomainStatusText.Text = "🟡 ローカル環境 (AD未接続)";
                DomainStatusBadge.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#FEF3C7")!;
                DomainStatusText.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#B45309")!;
            }
        }

        private async Task LoadAdPrincipalsAsync(string filter = "")
        {
            var list = await _adService.SearchPrincipalsAsync(filter);
            _adPrincipals.Clear();
            foreach (var item in list) _adPrincipals.Add(item);
            LiveAclStudioControl.SetPrincipals(_adPrincipals);
        }

        private async void AdSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            await LoadAdPrincipalsAsync(AdSearchTextBox.Text.Trim());
        }

        private void SimTargetBrowse_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "移行先ルートフォルダ（新サーバー / NAS）を選択" };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                SimTargetRootTextBox.Text = dialog.FolderName;
            }
        }

        private void SimSourceBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog { Title = "現行ファイルサーバー（移行元）のフォルダーを選択" };
            var current = SimSourcePathTextBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            {
                dialog.InitialDirectory = current;
            }
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                SimSourcePathTextBox.Text = dialog.FolderName;
                LoadSourceTree(dialog.FolderName);
            }
        }

        private void SimSourceLoadButton_Click(object sender, RoutedEventArgs e)
        {
            var path = SimSourcePathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                if (_currentTab?.RootNode != null)
                {
                    _currentTab.RootNode.IsExpanded = true;
                    SimSourceTreeView.ItemsSource = new List<FileItemNode> { _currentTab.RootNode };
                    ShowToast("容量分析スキャン結果を移行元ツリーに読み込みました");
                    return;
                }
                MessageBox.Show("有効な移行元フォルダパスを入力してください。", "通知", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LoadSourceTree(path);
        }

        private void LoadSourceTree(string path)
        {
            try
            {
                var di = new DirectoryInfo(path);
                var rootItem = new FileItemNode(di.FullName, di.Name, 0, true, di.LastWriteTime);

                PopulateSubdirectoriesSafe(rootItem, di);

                // 初期状態で直下を展開して表示
                rootItem.IsExpanded = true;
                SimSourceTreeView.ItemsSource = new List<FileItemNode> { rootItem };
                ShowToast($"移行元ツリーをロードしました: {path}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"移行元読込エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static void PopulateSubdirectoriesSafe(FileItemNode parentNode, DirectoryInfo di)
        {
            try
            {
                foreach (var sub in di.GetDirectories())
                {
                    var subNode = new FileItemNode(sub.FullName, sub.Name, 0, true, sub.LastWriteTime)
                    {
                        Parent = parentNode,
                        Level = parentNode.Level + 1
                    };

                    // サブフォルダの存在確認（遅延展開用ダミー）
                    try
                    {
                        if (sub.EnumerateDirectories().Any())
                        {
                            subNode.Children.Add(new FileItemNode(string.Empty, "__DUMMY__", 0, false));
                        }
                    }
                    catch
                    {
                        // アクセス権限等で判定できない場合も展開可能にしておく
                        subNode.Children.Add(new FileItemNode(string.Empty, "__DUMMY__", 0, false));
                    }

                    parentNode.Children.Add(subNode);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // アクセス拒否は安全にスキップ
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error enumerating directories in {di.FullName}: {ex.Message}");
            }
        }

        private void SimSourceTreeViewItem_Expanded(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is TreeViewItem treeViewItem && treeViewItem.DataContext is FileItemNode folderNode)
            {
                if (folderNode.Children.Count == 1 && folderNode.Children[0].Name == "__DUMMY__")
                {
                    folderNode.Children.Clear();
                    try
                    {
                        var di = new DirectoryInfo(folderNode.FullPath);
                        PopulateSubdirectoriesSafe(folderNode, di);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to expand {folderNode.FullPath}: {ex.Message}");
                    }
                }
                e.Handled = true;
            }
        }

        private void SimSourceTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // Track selection
        }

        private void SimSourceTreeView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && SimSourceTreeView.SelectedItem is FileItemNode item)
            {
                var data = new DataObject("FolderMorpherSourceNode", item);
                DragDrop.DoDragDrop(SimSourceTreeView, data, DragDropEffects.Copy | DragDropEffects.Move);
            }
        }

        private SimFolderNode CreateSimNodeFromSourceWithAcl(FileItemNode src, SimFolderNode? parent, int level, int maxDepth = int.MaxValue)
        {
            var node = new SimFolderNode
            {
                Name = src.Name,
                EstimatedSizeBytes = src.SizeBytes,
                Level = level,
                Parent = parent,
                IsExpanded = true
            };

            if (!string.IsNullOrEmpty(src.FullPath))
            {
                node.MappedSourcePaths.Add(src.FullPath);
                try
                {
                    if (Directory.Exists(src.FullPath))
                    {
                        var (entries, isInherited, _) = _aclService.GetSimAclForFolder(src.FullPath);
                        node.InheritAcl = isInherited;
                        foreach (var entry in entries)
                        {
                            node.AclEntries.Add(entry);
                        }
                    }
                }
                catch
                {
                    // アクセス拒否等でもツリー構築は継続
                }
            }

            if (level < maxDepth)
            {
                // もし既に展開済みの子があればそれを採用（ダミーは除外）
                var validChildren = src.Children.Where(c => c.IsDirectory && c.Name != "__DUMMY__").ToList();
                if (validChildren.Count > 0)
                {
                    foreach (var childSrc in validChildren)
                    {
                        var childNode = CreateSimNodeFromSourceWithAcl(childSrc, node, level + 1, maxDepth);
                        node.Children.Add(childNode);
                    }
                }
                else if (!string.IsNullOrEmpty(src.FullPath) && Directory.Exists(src.FullPath))
                {
                    // 未展開の場合は実ファイルシステムから再帰的にサブフォルダを安全走査構築
                    try
                    {
                        var di = new DirectoryInfo(src.FullPath);
                        foreach (var subDir in di.GetDirectories())
                        {
                            var subFileItem = new FileItemNode(subDir.FullName, subDir.Name, 0, true, subDir.LastWriteTime);
                            var childNode = CreateSimNodeFromSourceWithAcl(subFileItem, node, level + 1, maxDepth);
                            node.Children.Add(childNode);
                        }
                    }
                    catch
                    {
                        // アクセス拒否等はスキップ
                    }
                }
            }

            return node;
        }

        private static void UpdateDescendantLevels(SimFolderNode node, int newLevel)
        {
            node.Level = newLevel;
            foreach (var child in node.Children)
            {
                UpdateDescendantLevels(child, newLevel + 1);
            }
        }

        private void SimCloneSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (SimSourceTreeView.SelectedItem is FileItemNode selected)
            {
                var simNode = CreateSimNodeFromSourceWithAcl(selected, null, 0);
                _simRootFolders.Add(simNode);
                ShowToast($"新環境モックツリーに配置しました (権限・階層自動引き継ぎ): {selected.Name}");
            }
            else
            {
                MessageBox.Show("移行元ツリーからフォルダを選択してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void SimAddRootFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var newFolder = new SimFolderNode
            {
                Name = $"0{_simRootFolders.Count + 1}_新設ルートフォルダ",
                InheritAcl = true,
                Level = 0,
                IsExpanded = true
            };
            _simRootFolders.Add(newFolder);
            ShowToast($"ルートフォルダを追加しました: {newFolder.Name}");
        }

        private void SimMockTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (SimMockTreeView.SelectedItem is SimFolderNode node)
            {
                _selectedSimNode = node;
                SimSelectedFolderNameText.Text = node.Name;
                SimInheritCheckBox.IsChecked = node.InheritAcl;
                SimMappedSourcesItemsControl.ItemsSource = node.MappedSourcePaths;
                SimAclCardsItemsControl.ItemsSource = node.AclEntries;
            }
            else
            {
                _selectedSimNode = null;
                SimSelectedFolderNameText.Text = "(未選択)";
                SimMappedSourcesItemsControl.ItemsSource = null;
                SimAclCardsItemsControl.ItemsSource = null;
            }
        }

        private void SimMockTreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _simDragStartPoint = e.GetPosition(null);
            _isSimNodeDragging = false;
            _simNodeDroppedInTree = false;
            _simDragCancelled = false;
        }

        private void SimMockTreeView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (_isSimNodeDragging) return;

            Point currentPoint = e.GetPosition(null);
            Vector diff = _simDragStartPoint - currentPoint;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                SimFolderNode? node = null;
                if (e.OriginalSource is DependencyObject d)
                {
                    var treeItem = FindVisualParent<TreeViewItem>(d);
                    node = treeItem?.DataContext as SimFolderNode;
                }
                node ??= SimMockTreeView.SelectedItem as SimFolderNode;

                if (node != null)
                {
                    _isSimNodeDragging = true;
                    _simNodeDroppedInTree = false;
                    _simDragCancelled = false;

                    var data = new DataObject("FolderMorpherSimNode", node);
                    DragDrop.AddQueryContinueDragHandler(SimMockTreeView, OnSimQueryContinueDrag);

                    try
                    {
                        DragDropEffects effects = DragDrop.DoDragDrop(SimMockTreeView, data, DragDropEffects.Move | DragDropEffects.Copy);

                        // 枠外ドロップ削除の判定:
                        // ツリー内の別ノードや余白・サブフォルダゾーンに正常ドロップされず（_simNodeDroppedInTree == false）
                        // かつ Escキーによるキャンセルでもない場合、枠外ポイ捨て削除として扱う
                        if (!_simNodeDroppedInTree && !_simDragCancelled)
                        {
                            Point endPoint = Mouse.GetPosition(SimMockTreeView);
                            bool isOutside = endPoint.X < 0 || endPoint.Y < 0 ||
                                             endPoint.X > SimMockTreeView.ActualWidth ||
                                             endPoint.Y > SimMockTreeView.ActualHeight;

                            if (isOutside || effects == DragDropEffects.None)
                            {
                                if (node.Parent != null)
                                {
                                    node.Parent.Children.Remove(node);
                                }
                                else
                                {
                                    _simRootFolders.Remove(node);
                                }

                                if (_selectedSimNode == node)
                                {
                                    _selectedSimNode = null;
                                    SimSelectedFolderNameText.Text = "(未選択)";
                                    SimMappedSourcesItemsControl.ItemsSource = null;
                                    SimAclCardsItemsControl.ItemsSource = null;
                                }

                                ShowToast(LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                                    ? $"🗑️ フォルダ「{node.Name}」をツリーから削除しました"
                                    : $"🗑️ Deleted folder '{node.Name}' from tree");
                            }
                        }
                    }
                    finally
                    {
                        DragDrop.RemoveQueryContinueDragHandler(SimMockTreeView, OnSimQueryContinueDrag);
                        _isSimNodeDragging = false;
                    }
                }
            }
        }

        private void SimMockTreeView_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent("FolderMorpherSourceNode") ||
                e.Data.GetDataPresent("FolderMorpherSimNode") ||
                e.Data.GetDataPresent(typeof(AdPrincipalItem)))
            {
                e.Effects = DragDropEffects.Copy | DragDropEffects.Move;
                e.Handled = true;
            }
        }

        private void SimMockTreeView_Drop(object sender, DragEventArgs e)
        {
            var target = GetSimNodeFromDragEvent(e);

            // Case 1: Drop source folder from left explorer (Create subfolder with full ACL & hierarchy)
            if (e.Data.GetData("FolderMorpherSourceNode") is FileItemNode src)
            {
                var effectiveTarget = target ?? _selectedSimNode ?? _simRootFolders.FirstOrDefault();
                if (effectiveTarget != null)
                {
                    var newSub = CreateSimNodeFromSourceWithAcl(src, effectiveTarget, effectiveTarget.Level + 1);
                    effectiveTarget.Children.Add(newSub);
                    effectiveTarget.IsExpanded = true;
                    ShowToast($"📁 「{src.Name}」を「{effectiveTarget.Name}」配下にサブフォルダ化（権限・階層継承）しました");
                }
                return;
            }

            // Case 2: Drop existing sim node inside mock tree (Move)
            if (e.Data.GetData("FolderMorpherSimNode") is SimFolderNode movingNode)
            {
                _simNodeDroppedInTree = true; // 自枠ツリー内ドロップ

                // 2-A: 特定のフォルダの上にドロップされた場合 ➔ その配下へ移動
                if (target != null)
                {
                    if (movingNode == target || IsDescendant(target, movingNode))
                    {
                        ShowToast("自身またはその配下へは移動できません");
                        return;
                    }

                    // 現在の親から切り離す
                    if (movingNode.Parent != null) movingNode.Parent.Children.Remove(movingNode);
                    else _simRootFolders.Remove(movingNode);

                    // 新しい親に接続
                    movingNode.Parent = target;
                    UpdateDescendantLevels(movingNode, target.Level + 1);
                    target.Children.Add(movingNode);
                    target.IsExpanded = true;
                    ShowToast($"📁 「{movingNode.Name}」を「{target.Name}」の配下に移動しました");
                }
                // 2-B: ツリーの余白部分（下部空白または意図的な左端ずらし）にドロップされた場合 ➔ 第1階層（ルート）へ昇格移動
                else
                {
                    Point mousePos = e.GetPosition(SimMockTreeView);
                    // 遊び幅: 明らかな左端マージン（X < 50px）またはツリー全体の最下部余白の場合のみルート昇格とみなす
                    bool isIntentionalRoot = mousePos.X < 50 || (_simRootFolders.Count > 0 && mousePos.Y > _simRootFolders.Count * 36);

                    if (isIntentionalRoot)
                    {
                        if (movingNode.Parent == null) return;

                        movingNode.Parent.Children.Remove(movingNode);
                        movingNode.Parent = null;
                        UpdateDescendantLevels(movingNode, 0);
                        _simRootFolders.Add(movingNode);
                        ShowToast($"⏮ 「{movingNode.Name}」を第1階層（ルート）へ昇格しました");
                    }
                    else
                    {
                        // 水平のブレによる不意なルート昇格を防止し、現在の親・階層を保護
                        ShowToast("⚠️ フォルダ行の上にドロップしてください（階層を保護しました）");
                    }
                }
                return;
            }

            // Case 3: Drop AD Card (Assign permissions)
            if (e.Data.GetData(typeof(AdPrincipalItem)) is AdPrincipalItem principal)
            {
                var effectiveTarget = target ?? _selectedSimNode ?? _simRootFolders.FirstOrDefault();
                if (effectiveTarget != null)
                {
                    AssignAdPrincipal(effectiveTarget, principal);
                }
                return;
            }
        }

        private void SimGenericDragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.Copy | DragDropEffects.Move;
            e.Handled = true;
        }

        private void SimSubfolderDropZone_Drop(object sender, DragEventArgs e)
        {
            if (_selectedSimNode == null) return;

            if (e.Data.GetData("FolderMorpherSourceNode") is FileItemNode src)
            {
                var newSub = CreateSimNodeFromSourceWithAcl(src, _selectedSimNode, _selectedSimNode.Level + 1);
                _selectedSimNode.Children.Add(newSub);
                _selectedSimNode.IsExpanded = true;
                ShowToast($"📥 「{src.Name}」をサブフォルダ化（権限・階層継承）しました");
            }
            else if (e.Data.GetData("FolderMorpherSimNode") is SimFolderNode movingNode)
            {
                _simNodeDroppedInTree = true;
                SimMockTreeView_Drop(sender, e);
            }
        }

        private void SimAccordion_Drop(object sender, DragEventArgs e)
        {
            if (_selectedSimNode == null)
            {
                ShowToast(LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                    ? "⚠️ 先に設計ツリーでフォルダを選択してください"
                    : "⚠️ Please select a folder in the simulation tree first");
                return;
            }

            // 自枠内ドロップ判定（枠外解除防止）
            if (e.Data.GetData(typeof(SimAclEntry)) is SimAclEntry ||
                (e.Data.GetData(typeof(string)) is string s && _selectedSimNode.MappedSourcePaths.Contains(s)))
            {
                _droppedInSelfContainer = true;
                return;
            }

            if (e.Data.GetData("FolderMorpherSimNode") is SimFolderNode movingSimNode)
            {
                _simNodeDroppedInTree = true;
                SimMockTreeView_Drop(sender, e);
                return;
            }

            // 広域ドロップ受容 1: ADプリンシパル ➔ 権限付与
            if (e.Data.GetData(typeof(AdPrincipalItem)) is AdPrincipalItem principal)
            {
                AssignAdPrincipal(_selectedSimNode, principal);
                return;
            }

            // 広域ドロップ受容 2: 現行フォルダノード（FileItemNode） ➔ 統合マッピング追加
            if (e.Data.GetData("FolderMorpherSourceNode") is FileItemNode src)
            {
                if (!_selectedSimNode.MappedSourcePaths.Contains(src.FullPath))
                {
                    _selectedSimNode.MappedSourcePaths.Add(src.FullPath);
                    _selectedSimNode.NotifyMappingChanged();
                    ShowToast(LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                        ? $"🔗 「{src.Name}」を統合マッピングに追加しました"
                        : $"🔗 Added mapping: {src.Name}");
                }
                return;
            }

            // 広域ドロップ受容 3: エクスプローラー等からのファイル/フォルダドロップ
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                {
                    foreach (var f in files)
                    {
                        if (!_selectedSimNode.MappedSourcePaths.Contains(f))
                        {
                            _selectedSimNode.MappedSourcePaths.Add(f);
                            _selectedSimNode.NotifyMappingChanged();
                            ShowToast(LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                                ? $"🔗 「{System.IO.Path.GetFileName(f)}」を統合マッピングに追加しました"
                                : $"🔗 Added mapping: {System.IO.Path.GetFileName(f)}");
                        }
                    }
                    return;
                }
            }
        }

        private void SimMappingDropZone_Drop(object sender, DragEventArgs e)
        {
            if (_selectedSimNode == null) return;

            if (e.Data.GetData(typeof(string)) is string s && _selectedSimNode.MappedSourcePaths.Contains(s))
            {
                _droppedInSelfContainer = true;
                return;
            }

            if (e.Data.GetData("FolderMorpherSourceNode") is FileItemNode src)
            {
                if (!_selectedSimNode.MappedSourcePaths.Contains(src.FullPath))
                {
                    _selectedSimNode.MappedSourcePaths.Add(src.FullPath);
                    _selectedSimNode.NotifyMappingChanged();
                    ShowToast($"🔗 「{src.Name}」を統合マッピングに追加しました");
                }
            }
        }

        private void SimMappedSource_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _cardDragStartPoint = e.GetPosition(null);
            }
        }

        private void SimMappedSource_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (_selectedSimNode == null) return;

            Point currentPoint = e.GetPosition(null);
            Vector diff = _cardDragStartPoint - currentPoint;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (sender is FrameworkElement fe && fe.DataContext is string sourcePath)
                {
                    _droppedInSelfContainer = false;
                    _dragCancelled = false;
                    try
                    {
                        DragDrop.AddQueryContinueDragHandler(fe, OnCardQueryContinueDrag);
                        DragDrop.DoDragDrop(fe, sourcePath, DragDropEffects.Move | DragDropEffects.Copy);
                    }
                    finally
                    {
                        DragDrop.RemoveQueryContinueDragHandler(fe, OnCardQueryContinueDrag);
                        // Escキャンセルされた場合は削除しない。マウスドロップで枠外に落ちた場合のみ解除
                        if (!_dragCancelled && !_droppedInSelfContainer && _selectedSimNode != null)
                        {
                            _selectedSimNode.MappedSourcePaths.Remove(sourcePath);
                            _selectedSimNode.NotifyMappingChanged();
                            ShowToast(LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                                ? $"🗑️ 移行元マッピングを枠外ドロップで解除しました: {System.IO.Path.GetFileName(sourcePath)}"
                                : $"🗑️ Removed mapping by dropping outside: {System.IO.Path.GetFileName(sourcePath)}");
                        }
                    }
                }
            }
        }

        private void SimRemoveMapping_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is string path && _selectedSimNode != null)
            {
                _selectedSimNode.MappedSourcePaths.Remove(path);
                _selectedSimNode.NotifyMappingChanged();
                ShowToast("紐づけを解除しました");
            }
        }

        private void SimAclDropZone_Drop(object sender, DragEventArgs e)
        {
            if (_selectedSimNode == null) return;

            if (e.Data.GetData(typeof(SimAclEntry)) is SimAclEntry)
            {
                _droppedInSelfContainer = true;
                return;
            }

            if (e.Data.GetData(typeof(AdPrincipalItem)) is AdPrincipalItem principal)
            {
                AssignAdPrincipal(_selectedSimNode, principal);
            }
        }

        private void AssignAdPrincipal(SimFolderNode node, AdPrincipalItem principal)
        {
            if (node.AclEntries.Any(a => a.AccountName.Equals(principal.AccountName, StringComparison.OrdinalIgnoreCase)))
            {
                ShowToast(LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                    ? $"「{principal.DisplayName}」は既に割り当てられています"
                    : $"\"{principal.DisplayName}\" is already assigned");
                return;
            }

            var entry = new SimAclEntry
            {
                AccountName = principal.AccountName,
                DisplayName = principal.DisplayName,
                PrincipalType = principal.PrincipalType,
                Rights = FileSystemRights.Modify | FileSystemRights.Synchronize
            };
            node.AclEntries.Add(entry);
            node.NotifyAclChanged();
            ShowToast(LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                ? $"🛡️ 「{principal.DisplayName}」に 変更 (Modify) 権限を付与しました"
                : $"🛡️ Granted Modify permission to \"{principal.DisplayName}\"");
        }
        #endregion

        #region Quick Level Controls (⏮ ルートへ / ◀ 昇格 / ▶ 降格)
        private void SimQuickJumpRoot_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is SimFolderNode node)
            {
                if (node.Parent != null)
                {
                    node.Parent.Children.Remove(node);
                    node.Parent = null;
                    node.Level = 0;
                    _simRootFolders.Add(node);
                    ShowToast($"🚀 「{node.Name}」を一気に第1階層（ルート）へジャンプアップしました！");
                }
            }
        }

        private void SimQuickPromote_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is SimFolderNode node)
            {
                if (node.Parent == null) return;

                var currentParent = node.Parent;
                currentParent.Children.Remove(node);

                if (currentParent.Parent != null)
                {
                    node.Parent = currentParent.Parent;
                    node.Level = currentParent.Level;
                    currentParent.Parent.Children.Add(node);
                }
                else
                {
                    node.Parent = null;
                    node.Level = 0;
                    _simRootFolders.Add(node);
                }
                ShowToast($"◀ 「{node.Name}」を1階層昇格しました");
            }
        }

        private void SimQuickDemote_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is SimFolderNode node)
            {
                // Find sibling
                IList<SimFolderNode> siblings = node.Parent != null ? node.Parent.Children : _simRootFolders;
                int idx = siblings.IndexOf(node);
                if (idx > 0)
                {
                    var newParent = siblings[idx - 1];
                    siblings.Remove(node);
                    node.Parent = newParent;
                    node.Level = newParent.Level + 1;
                    newParent.Children.Add(node);
                    newParent.IsExpanded = true;
                    ShowToast($"▶ 「{node.Name}」を「{newParent.Name}」配下に降格しました");
                }
            }
        }

        private void CtxSimJumpRoot_Click(object sender, RoutedEventArgs e) => SimQuickJumpRoot_Click(sender, new RoutedEventArgs());
        private void CtxSimPromote_Click(object sender, RoutedEventArgs e) => SimQuickPromote_Click(sender, new RoutedEventArgs());
        private void CtxSimDemote_Click(object sender, RoutedEventArgs e) => SimQuickDemote_Click(sender, new RoutedEventArgs());
        #endregion

        #region ContextMenu & Tree Operations
        private void CtxSimAddChild_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSimNode != null)
            {
                var child = new SimFolderNode
                {
                    Name = $"サブフォルダ_{_selectedSimNode.Children.Count + 1}",
                    InheritAcl = true,
                    Level = _selectedSimNode.Level + 1,
                    Parent = _selectedSimNode,
                    IsExpanded = true
                };
                _selectedSimNode.Children.Add(child);
                _selectedSimNode.IsExpanded = true;
                child.IsSelected = true;
                _selectedSimNode = child;
                SimSelectedFolderNameText.Text = child.Name;
                SimInheritCheckBox.IsChecked = child.InheritAcl;
                SimMappedSourcesItemsControl.ItemsSource = child.MappedSourcePaths;
                SimAclCardsItemsControl.ItemsSource = child.AclEntries;
                ShowToast($"サブフォルダを追加しました: {child.Name}");
            }
        }

        private void CtxSimRename_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSimNode != null)
            {
                var prompt = Microsoft.VisualBasic.Interaction.InputBox("新しいフォルダ名を入力してください:", "フォルダ名変更", _selectedSimNode.Name);
                if (!string.IsNullOrWhiteSpace(prompt))
                {
                    _selectedSimNode.Name = prompt.Trim();
                    ShowToast($"フォルダ名を変更しました: {_selectedSimNode.Name}");
                }
            }
        }

        private void CtxSimDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSimNode != null)
            {
                if (MessageBox.Show($"フォルダ '{_selectedSimNode.Name}' を削除しますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                {
                    if (_selectedSimNode.Parent != null) _selectedSimNode.Parent.Children.Remove(_selectedSimNode);
                    else _simRootFolders.Remove(_selectedSimNode);
                    _selectedSimNode = null;
                    SimSelectedFolderNameText.Text = "(未選択)";
                }
            }
        }

        private void SimInheritCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_selectedSimNode != null)
            {
                _selectedSimNode.InheritAcl = SimInheritCheckBox.IsChecked == true;
            }
        }

        private void SimDeleteAclEntryButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is SimAclEntry acl && _selectedSimNode != null)
            {
                _selectedSimNode.AclEntries.Remove(acl);
                _selectedSimNode.NotifyAclChanged();
                ShowToast("アクセス権カードを解除しました");
            }
        }

        private void AdCard_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && sender is FrameworkElement fe && fe.Tag is AdPrincipalItem item)
            {
                DragDrop.DoDragDrop(fe, item, DragDropEffects.Copy);
            }
        }

        private static bool IsDescendant(SimFolderNode candidate, SimFolderNode ancestor)
        {
            var p = candidate.Parent;
            while (p != null)
            {
                if (p == ancestor) return true;
                p = p.Parent;
            }
            return false;
        }

        private static SimFolderNode? GetSimNodeFromDragEvent(DragEventArgs e)
        {
            if (e.OriginalSource is DependencyObject d)
            {
                var treeItem = FindVisualParent<TreeViewItem>(d);
                if (treeItem?.DataContext is SimFolderNode n) return n;
            }

            // 水平オフセットの遊び: マウスが右側余白へブレてもY軸ライン上のアイテムを探索
            if (e.Source is TreeView tv)
            {
                Point pos = e.GetPosition(tv);
                Point testPoint = new Point(Math.Clamp(pos.X, 60, Math.Max(60, tv.ActualWidth - 60)), pos.Y);
                System.Windows.Media.HitTestResult result = System.Windows.Media.VisualTreeHelper.HitTest(tv, testPoint);
                if (result?.VisualHit != null)
                {
                    var treeItem = FindVisualParent<TreeViewItem>(result.VisualHit);
                    if (treeItem?.DataContext is SimFolderNode n) return n;
                }
            }
            return null;
        }

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parent = System.Windows.Media.VisualTreeHelper.GetParent(child);
            while (parent != null)
            {
                if (parent is T typed) return typed;
                parent = System.Windows.Media.VisualTreeHelper.GetParent(parent);
            }
            return null;
        }
        #endregion

        #region Security Detailed Permissions Modal (相互リアルタイム連動)
        private void SimAclCard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _cardDragStartPoint = e.GetPosition(null);
            }

            if (e.ClickCount == 2 && sender is FrameworkElement fe && fe.DataContext is SimAclEntry acl && _selectedSimNode != null)
            {
                OpenSecurityModal(acl);
            }
        }

        private void SimAclCard_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (_selectedSimNode == null) return;

            Point currentPoint = e.GetPosition(null);
            Vector diff = _cardDragStartPoint - currentPoint;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (sender is FrameworkElement fe && fe.DataContext is SimAclEntry acl)
                {
                    _droppedInSelfContainer = false;
                    _dragCancelled = false;
                    try
                    {
                        DragDrop.AddQueryContinueDragHandler(fe, OnCardQueryContinueDrag);
                        DragDrop.DoDragDrop(fe, acl, DragDropEffects.Move | DragDropEffects.Copy);
                    }
                    finally
                    {
                        DragDrop.RemoveQueryContinueDragHandler(fe, OnCardQueryContinueDrag);
                        // Escキャンセルされた場合は削除しない。マウスドロップで枠外に落ちた場合のみ解除
                        if (!_dragCancelled && !_droppedInSelfContainer && _selectedSimNode != null)
                        {
                            _selectedSimNode.AclEntries.Remove(acl);
                            _selectedSimNode.NotifyAclChanged();
                            ShowToast(LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                                ? $"🗑️ アクセス権カードを枠外ドロップで解除しました: {acl.DisplayName}"
                                : $"🗑️ Removed ACL card by dropping outside: {acl.DisplayName}");
                        }
                    }
                }
            }
        }

        private void SimOpenSecModalButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedSimNode == null) return;
            var acl = _selectedSimNode.AclEntries.FirstOrDefault() ?? new SimAclEntry
            {
                AccountName = "Domain Users",
                DisplayName = "Domain Users",
                Rights = FileSystemRights.ReadAndExecute
            };
            if (!_selectedSimNode.AclEntries.Contains(acl))
            {
                _selectedSimNode.AclEntries.Add(acl);
            }
            OpenSecurityModal(acl);
        }

        private void OpenSecurityModal(SimAclEntry acl, string? folderName = null, string? fullPath = null, Action? onApplied = null)
        {
            _currentEditingAcl = acl;
            _onSecModalAppliedCallback = onApplied;
            SecModalTargetNameText.Text = folderName ?? (_selectedSimNode?.Name ?? "フォルダ");
            SecModalFullPathText.Text = fullPath ?? $"\\\\NewServer01\\Share\\{_selectedSimNode?.RelativePath}";
            SecPrincipalInput.Text = acl.DisplayName;

            SyncCheckboxesFromAcl(acl);
            SecModalOverlay.Visibility = Visibility.Visible;
        }

        private void SyncCheckboxesFromAcl(SimAclEntry acl)
        {
            SecAccessTypeCombo.SelectedIndex = AclUiBindingHelper.AccessTypeToIndex(acl.AccessType);

            string targetApplies = acl.AppliesTo;
            int appliesIdx = 0;
            for (int i = 0; i < SecAppliesToCombo.Items.Count; i++)
            {
                if (SecAppliesToCombo.Items[i] is ComboBoxItem item &&
                    string.Equals(item.Content?.ToString(), targetApplies, StringComparison.OrdinalIgnoreCase))
                {
                    appliesIdx = i;
                    break;
                }
            }
            SecAppliesToCombo.SelectedIndex = appliesIdx;

            SecChkFullControl.IsChecked = acl.IsFullControl;
            SecChkModify.IsChecked = acl.IsModify;
            SecChkReadExecute.IsChecked = acl.IsReadExecute;
            SecChkList.IsChecked = acl.IsListFolder;
            SecChkRead.IsChecked = acl.IsRead;
            SecChkWrite.IsChecked = acl.IsWrite;

            SecAdvTraverse.IsChecked = acl.AdvTraverse;
            SecAdvList.IsChecked = acl.AdvListDirectory;
            SecAdvReadAttr.IsChecked = acl.AdvReadAttributes;
            SecAdvReadExtAttr.IsChecked = acl.AdvReadExtendedAttributes;
            SecAdvCreateFile.IsChecked = acl.AdvCreateFiles;
            SecAdvCreateFolder.IsChecked = acl.AdvCreateDirectories;
            SecAdvWriteAttr.IsChecked = acl.AdvWriteAttributes;
            SecAdvWriteExtAttr.IsChecked = acl.AdvWriteExtendedAttributes;
            SecAdvDelete.IsChecked = acl.AdvDelete;
            SecAdvDeleteSub.IsChecked = acl.AdvDeleteSubdirectoriesAndFiles;
            SecAdvReadPerm.IsChecked = acl.AdvReadPermissions;
            SecAdvChangePerm.IsChecked = acl.AdvChangePermissions;
            SecAdvTakeOwnership.IsChecked = acl.AdvTakeOwnership;
            SecAdvSync.IsChecked = acl.AdvSynchronize;
        }

        private void SecBasicPerm_Click(object sender, RoutedEventArgs e)
        {
            if (sender == SecChkFullControl)
            {
                bool isFull = SecChkFullControl.IsChecked == true;
                SecChkModify.IsChecked = isFull;
                SecChkReadExecute.IsChecked = isFull;
                SecChkList.IsChecked = isFull;
                SecChkRead.IsChecked = isFull;
                SecChkWrite.IsChecked = isFull;
                SetAllAdvCheckboxes(isFull);
            }
            else if (sender == SecChkModify)
            {
                bool isMod = SecChkModify.IsChecked == true;
                if (isMod)
                {
                    SecChkReadExecute.IsChecked = true;
                    SecChkList.IsChecked = true;
                    SecChkRead.IsChecked = true;
                    SecChkWrite.IsChecked = true;
                    SetAdvModifyFlags(true);
                }
                else
                {
                    SecChkFullControl.IsChecked = false;
                    SecAdvDelete.IsChecked = false;
                }
            }
            else if (sender == SecChkReadExecute)
            {
                bool isRx = SecChkReadExecute.IsChecked == true;
                if (isRx)
                {
                    SecChkList.IsChecked = true;
                    SecChkRead.IsChecked = true;
                    SecAdvTraverse.IsChecked = true;
                    SecAdvList.IsChecked = true;
                    SecAdvReadAttr.IsChecked = true;
                    SecAdvReadExtAttr.IsChecked = true;
                    SecAdvReadPerm.IsChecked = true;
                    SecAdvSync.IsChecked = true;
                }
                else
                {
                    SecChkFullControl.IsChecked = false;
                    SecChkModify.IsChecked = false;
                    SecAdvTraverse.IsChecked = false;
                }
            }
            RecalcBasicCheckboxesFromAdv();
        }

        private void SecAdvPerm_Click(object sender, RoutedEventArgs e)
        {
            RecalcBasicCheckboxesFromAdv();
        }

        private void SetAllAdvCheckboxes(bool val)
        {
            SecAdvTraverse.IsChecked = val;
            SecAdvList.IsChecked = val;
            SecAdvReadAttr.IsChecked = val;
            SecAdvReadExtAttr.IsChecked = val;
            SecAdvCreateFile.IsChecked = val;
            SecAdvCreateFolder.IsChecked = val;
            SecAdvWriteAttr.IsChecked = val;
            SecAdvWriteExtAttr.IsChecked = val;
            SecAdvDelete.IsChecked = val;
            SecAdvDeleteSub.IsChecked = val;
            SecAdvReadPerm.IsChecked = val;
            SecAdvChangePerm.IsChecked = val;
            SecAdvTakeOwnership.IsChecked = val;
            SecAdvSync.IsChecked = val;
        }

        private void SetAdvModifyFlags(bool val)
        {
            SecAdvTraverse.IsChecked = val;
            SecAdvList.IsChecked = val;
            SecAdvReadAttr.IsChecked = val;
            SecAdvReadExtAttr.IsChecked = val;
            SecAdvCreateFile.IsChecked = val;
            SecAdvCreateFolder.IsChecked = val;
            SecAdvWriteAttr.IsChecked = val;
            SecAdvWriteExtAttr.IsChecked = val;
            SecAdvDelete.IsChecked = val;
            SecAdvDeleteSub.IsChecked = false;
            SecAdvReadPerm.IsChecked = val;
            SecAdvChangePerm.IsChecked = false;
            SecAdvTakeOwnership.IsChecked = false;
            SecAdvSync.IsChecked = val;
        }

        private void RecalcBasicCheckboxesFromAdv()
        {
            bool isFull = SecAdvTraverse.IsChecked == true && SecAdvList.IsChecked == true &&
                          SecAdvReadAttr.IsChecked == true && SecAdvReadExtAttr.IsChecked == true &&
                          SecAdvCreateFile.IsChecked == true && SecAdvCreateFolder.IsChecked == true &&
                          SecAdvWriteAttr.IsChecked == true && SecAdvWriteExtAttr.IsChecked == true &&
                          SecAdvDelete.IsChecked == true && SecAdvDeleteSub.IsChecked == true &&
                          SecAdvReadPerm.IsChecked == true && SecAdvChangePerm.IsChecked == true &&
                          SecAdvTakeOwnership.IsChecked == true && SecAdvSync.IsChecked == true;

            bool isMod = SecAdvDelete.IsChecked == true && SecAdvCreateFile.IsChecked == true &&
                         SecAdvCreateFolder.IsChecked == true && SecAdvWriteAttr.IsChecked == true &&
                         SecAdvWriteExtAttr.IsChecked == true && SecAdvTraverse.IsChecked == true &&
                         SecAdvList.IsChecked == true && SecAdvReadAttr.IsChecked == true &&
                         SecAdvReadExtAttr.IsChecked == true && SecAdvReadPerm.IsChecked == true;

            bool isRx = SecAdvTraverse.IsChecked == true && SecAdvList.IsChecked == true &&
                        SecAdvReadAttr.IsChecked == true && SecAdvReadExtAttr.IsChecked == true &&
                        SecAdvReadPerm.IsChecked == true;

            bool isRead = SecAdvList.IsChecked == true && SecAdvReadAttr.IsChecked == true &&
                          SecAdvReadExtAttr.IsChecked == true && SecAdvReadPerm.IsChecked == true;

            bool isWrite = SecAdvCreateFile.IsChecked == true && SecAdvCreateFolder.IsChecked == true &&
                           SecAdvWriteAttr.IsChecked == true && SecAdvWriteExtAttr.IsChecked == true;

            SecChkFullControl.IsChecked = isFull;
            SecChkModify.IsChecked = isMod;
            SecChkReadExecute.IsChecked = isRx;
            SecChkRead.IsChecked = isRead;
            SecChkWrite.IsChecked = isWrite;
            SecChkList.IsChecked = SecAdvList.IsChecked == true && SecAdvReadPerm.IsChecked == true;
        }

        private void SecModalApply_Click(object sender, RoutedEventArgs e)
        {
            if (_currentEditingAcl != null)
            {
                string selectedAppliesText = (SecAppliesToCombo.SelectedItem is ComboBoxItem cbi)
                    ? (cbi.Content?.ToString() ?? AclInheritanceHelper.AppliesTo_All)
                    : AclInheritanceHelper.AppliesTo_All;

                AclUiBindingHelper.ApplyModalToEntry(
                    _currentEditingAcl,
                    SecPrincipalInput.Text.Trim(),
                    SecAccessTypeCombo.SelectedIndex,
                    selectedAppliesText
                );

                _currentEditingAcl.AdvTraverse = SecAdvTraverse.IsChecked == true;
                _currentEditingAcl.AdvListDirectory = SecAdvList.IsChecked == true;
                _currentEditingAcl.AdvReadAttributes = SecAdvReadAttr.IsChecked == true;
                _currentEditingAcl.AdvReadExtendedAttributes = SecAdvReadExtAttr.IsChecked == true;
                _currentEditingAcl.AdvCreateFiles = SecAdvCreateFile.IsChecked == true;
                _currentEditingAcl.AdvCreateDirectories = SecAdvCreateFolder.IsChecked == true;
                _currentEditingAcl.AdvWriteAttributes = SecAdvWriteAttr.IsChecked == true;
                _currentEditingAcl.AdvWriteExtendedAttributes = SecAdvWriteExtAttr.IsChecked == true;
                _currentEditingAcl.AdvDelete = SecAdvDelete.IsChecked == true;
                _currentEditingAcl.AdvDeleteSubdirectoriesAndFiles = SecAdvDeleteSub.IsChecked == true;
                _currentEditingAcl.AdvReadPermissions = SecAdvReadPerm.IsChecked == true;
                _currentEditingAcl.AdvChangePermissions = SecAdvChangePerm.IsChecked == true;
                _currentEditingAcl.AdvTakeOwnership = SecAdvTakeOwnership.IsChecked == true;
                _currentEditingAcl.AdvSynchronize = SecAdvSync.IsChecked == true;

                _selectedSimNode?.NotifyAclChanged();
                _onSecModalAppliedCallback?.Invoke();
                _onSecModalAppliedCallback = null;
                ShowToast($"🛡️ 「{_currentEditingAcl.DisplayName}」のアクセス権設定を反映しました");
            }
            SecModalOverlay.Visibility = Visibility.Collapsed;
        }

        private void SecModalCancel_Click(object sender, RoutedEventArgs e)
        {
            SecModalOverlay.Visibility = Visibility.Collapsed;
        }
        #endregion

        #region Difference Review (Diff Inspector)
        private void SimDiffReviewButton_Click(object sender, RoutedEventArgs e)
        {
            var targetRoot = SimTargetRootTextBox.Text.Trim();
            var diffs = _simService.GenerateDiffReview(_currentTab?.RootNode, _simRootFolders);
            DiffReviewDataGrid.ItemsSource = diffs;
            DiffSummaryStatsText.Text = string.IsNullOrWhiteSpace(targetRoot)
                ? $"📊 差分項目: {diffs.Count}件"
                : $"📊 差分項目: {diffs.Count}件 (展開先: {targetRoot})";
            DiffModalOverlay.Visibility = Visibility.Visible;
        }

        private void DiffModalClose_Click(object sender, RoutedEventArgs e)
        {
            DiffModalOverlay.Visibility = Visibility.Collapsed;
        }

        private async void DiffModalApply_Click(object sender, RoutedEventArgs e)
        {
            var targetRoot = SimTargetRootTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                MessageBox.Show("移行先ルートフォルダを入力してください。", "通知", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            GlobalProgressBar.Visibility = Visibility.Visible;
            GlobalProgressBar.IsIndeterminate = true;

            var progress = new Progress<(string Status, int Count)>(p =>
            {
                StatusTextBlock.Text = $"{p.Status} ({p.Count} 作成済)";
            });

            try
            {
                using var cts = new CancellationTokenSource();
                var deployResult = await _simService.DeploySkeletonAsync(_simRootFolders, targetRoot, progress, cts.Token);

                // Verify: 展開先ルートおよび新規作成された全フォルダーの実在検証 ＆ エラー件数照合
                bool allPathsExist = Directory.Exists(targetRoot) &&
                                     deployResult.DeployedFolderPaths.All(p => Directory.Exists(p));
                bool isCleanSuccess = deployResult.FailedCount == 0 && allPathsExist;
                DiffModalOverlay.Visibility = Visibility.Collapsed;

                if (isCleanSuccess)
                {
                    string skippedMsg = deployResult.SkippedExistingCount > 0 ? $" ({deployResult.SkippedExistingCount} 既存保護)" : "";
                    ShowToast($"✅ スケルトン作成完了 (全階層検証済): {deployResult.CreatedCount} フォルダ作成{skippedMsg}");
                }
                else if (allPathsExist && deployResult.FailedCount > 0)
                {
                    ShowToast($"⚠️ スケルトン作成完了 (一部権限警告 {deployResult.FailedCount}件): {deployResult.CreatedCount} フォルダ作成");
                }
                else
                {
                    ShowToast($"⚠️ スケルトン作成警告: 一部のフォルダー実在を確認できませんでした (エラー: {deployResult.FailedCount}件)");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"スケルトン作成失敗: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                GlobalProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void DiffExportExcel_Click(object sender, RoutedEventArgs e)
        {
            var diffs = DiffReviewDataGrid.ItemsSource as List<SimDiffItem>;
            if (diffs == null || diffs.Count == 0) return;

            var dialog = new SaveFileDialog
            {
                Title = "移行変化点 差分対比レポートを保存",
                Filter = "Excelブック (*.xlsx)|*.xlsx|CSVファイル (*.csv)|*.csv",
                InitialDirectory = GetDefaultExportDirectory(),
                FileName = $"FolderMorpher_DiffReport_{DateTime.Now:yyyyMMdd}.xlsx"
            };
            if (dialog.ShowDialog() == true)
            {
                try
                {
                    if (dialog.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    {
                        var excelService = new ExcelReportService();
                        excelService.ExportSimDiffReport(dialog.FileName, diffs);
                        ShowToast($"📊 {Path.GetFileName(dialog.FileName)} を出力しました");
                    }
                    else
                    {
                        var sb = new StringBuilder();
                        sb.Append('\uFEFF');
                        sb.AppendLine("変化の種別,現行サーバー (Before),Before詳細,新環境設計 (After),After詳細,権限差分詳細");
                        foreach (var d in diffs)
                        {
                            sb.AppendLine($"\"{d.DiffType}\",\"{d.SourcePath.Replace("\n", " | ")}\",\"{d.SourceDetail}\",\"{d.TargetPath}\",\"{d.TargetDetail}\",\"{d.FormattedAclChanges.Replace("\n", " | ")}\"");
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
        #endregion

        #region Simulation Project Save / Load / Deployment / Export
        private async void SimSaveProjectButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "FolderMorpher 設計プロジェクトの保存",
                Filter = "FolderMorpher 設計ファイル (*.fmorph)|*.fmorph|JSONファイル (*.json)|*.json",
                FileName = $"{SimProjectNameTextBox.Text.Trim()}_{DateTime.Now:yyyyMMdd}.fmorph"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var project = new FolderMorphProject
                    {
                        ProjectName = SimProjectNameTextBox.Text.Trim(),
                        SourceRootPath = SimSourcePathTextBox.Text.Trim(),
                        TargetRootPath = SimTargetRootTextBox.Text.Trim(),
                        RootFolders = _simRootFolders.ToList()
                    };
                    await _simService.SaveProjectAsync(project, dialog.FileName);
                    ShowToast($"プロジェクトを保存しました: {Path.GetFileName(dialog.FileName)}");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"保存エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void SimLoadProjectButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "FolderMorpher 設計プロジェクトの読込",
                Filter = "FolderMorpher 設計ファイル (*.fmorph)|*.fmorph|すべてのファイル (*.*)|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var project = await _simService.LoadProjectAsync(dialog.FileName);
                    if (project != null)
                    {
                        SimProjectNameTextBox.Text = project.ProjectName;
                        SimSourcePathTextBox.Text = project.SourceRootPath;
                        SimTargetRootTextBox.Text = project.TargetRootPath;

                        _simRootFolders.Clear();
                        foreach (var root in project.RootFolders) _simRootFolders.Add(root);

                        ShowToast($"プロジェクトを読み込みました: {Path.GetFileName(dialog.FileName)}");
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"読込エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SimDeploySkeletonButton_Click(object sender, RoutedEventArgs e)
        {
            var targetRoot = SimTargetRootTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                MessageBox.Show("移行先ルートフォルダを入力してください。", "通知", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // 差分（実行計画）を計算して「変更点」モーダルを表示
            var diffs = _simService.GenerateDiffReview(_currentTab?.RootNode, _simRootFolders);
            DiffReviewDataGrid.ItemsSource = diffs;
            DiffSummaryStatsText.Text = $"📊 差分項目: {diffs.Count}件 (展開先: {targetRoot})";
            DiffModalOverlay.Visibility = Visibility.Visible;
        }

        private void SimExportScriptsButton_Click(object sender, RoutedEventArgs e)
        {
            var targetRoot = SimTargetRootTextBox.Text.Trim();

            var modeResult = MessageBox.Show(
                "Robocopy の転送モードを選択してください：\n\n" +
                "【はい (推奨)】 新設計ACL維持モード (/COPY:DAT)\n" +
                "  FolderMorpherで設計・先行展開した新ACLを保護し、データと日時のみ高速転送します。\n\n" +
                "【いいえ】 旧環境ACL完全維持モード (/COPYALL)\n" +
                "  FolderMorpherで設計した新ACLは上書きされ、移行元の古いアクセス権をそのまま引き継ぎます。\n\n" +
                "（キャンセルで出力中止）",
                "Robocopy 転送モード選択",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (modeResult == MessageBoxResult.Cancel) return;
            bool copyAcl = (modeResult == MessageBoxResult.No);

            var roboScript = _simService.GenerateRobocopyScript(_simRootFolders, targetRoot, copyAcl: copyAcl);
            var psScript = _simService.GeneratePowerShellAclScript(_simRootFolders, targetRoot);

            var dialog = new SaveFileDialog
            {
                Title = "Robocopy 移行スクリプトを保存",
                Filter = "バッチファイル (*.bat)|*.bat",
                FileName = $"Run_Migration_Robocopy_{DateTime.Now:yyyyMMdd}.bat"
            };

            if (dialog.ShowDialog() == true)
            {
                File.WriteAllText(dialog.FileName, roboScript, Encoding.UTF8);

                var psPath = Path.ChangeExtension(dialog.FileName, ".ps1");
                File.WriteAllText(psPath, psScript, Encoding.UTF8);

                ShowToast("移行バッチ & PowerShellスクリプトを生成しました");
                MessageBox.Show($"移行実行用スクリプトを出力しました:\n\n• {dialog.FileName} (Robocopy)\n• {psPath} (PowerShell ACL)", "スクリプト生成完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void SimExportExcelButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new SaveFileDialog
            {
                Title = "移行台帳マトリクス (Excel/CSV) を保存",
                Filter = "Excelブック (*.xlsx)|*.xlsx|CSVファイル (*.csv)|*.csv",
                InitialDirectory = GetDefaultExportDirectory(),
                FileName = $"FolderMorpher_Ledger_{DateTime.Now:yyyyMMdd}.xlsx"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    if (dialog.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
                    {
                        var excelService = new ExcelReportService();
                        excelService.ExportSimulationDesignMatrix(dialog.FileName, _simRootFolders);
                        ShowToast($"📊 {Path.GetFileName(dialog.FileName)} を出力しました");
                    }
                    else
                    {
                        var csv = _simService.ExportDesignMatrixCsv(_simRootFolders);
                        File.WriteAllText(dialog.FileName, csv, Encoding.UTF8);
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
        #endregion

        #region Tab 3: Link Fixer (ショートカット一括修復)
        private async void LinkScanButton_Click(object sender, RoutedEventArgs e)
        {
            var scope = LinkSearchScopeTextBox.Text.Trim();
            var oldPattern = LinkOldPatternTextBox.Text.Trim();
            var newPattern = LinkNewPatternTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(scope) || !Directory.Exists(scope))
            {
                MessageBox.Show("有効な検索対象フォルダを入力してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _linkFixCts?.Cancel();
            _linkFixCts = new CancellationTokenSource();

            GlobalProgressBar.Visibility = Visibility.Visible;
            GlobalProgressBar.IsIndeterminate = true;

            IProgress<string> progress = new Progress<string>(msg => StatusTextBlock.Text = msg);

            try
            {
                var items = await _linkFixService.ScanShortcutsAsync(scope, oldPattern, newPattern, progress, _linkFixCts.Token);

                // M3対策: Officeファイル内部リンクも含める場合
                if (LinkIncludeOfficeCheckBox.IsChecked == true)
                {
                    progress.Report("Officeファイル内部リンクを走査中...");
                    var officeItems = await _officeLinkService.ScanOfficeLinksAsync(scope, oldPattern, newPattern, progress, _linkFixCts.Token);
                    foreach (var off in officeItems)
                    {
                        items.Add(new LinkFixItem
                        {
                            FilePath = off.FilePath,
                            FileName = off.FileName,
                            FileType = $"Office ({off.Extension}) - {off.LinkType}",
                            OldTarget = off.FoundPattern,
                            NewTarget = off.TargetReplacement,
                            Status = off.Status,
                            AssociatedOfficeItem = off
                        });
                    }
                }

                LinkItemsDataGrid.ItemsSource = items;
                ShowToast($"切断リンクスキャン完了: {items.Count} 件検出");
            }
            catch (OperationCanceledException)
            {
                StatusTextBlock.Text = "スキャンを中止しました。";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"スキャン失敗: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                GlobalProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private List<LinkFixItem> _lastLinkFixTargets = new();

        private void LinkFixExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            var items = LinkItemsDataGrid.ItemsSource as List<LinkFixItem>;
            if (items == null || items.Count == 0)
            {
                MessageBox.Show("修復対象のショートカットがありません。先に切断リンク検出スキャンを実行してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var targets = items.Where(i => i.NeedsFix).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show("修復が必要な項目はありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _lastLinkFixTargets = targets;
            LinkFixDiffDataGrid.ItemsSource = targets;
            LinkFixDiffSummaryText.Text = $"📊 修復対象: {targets.Count}件 (各ファイル .bak 自動バックアップ生成)";
            LinkFixDiffModalOverlay.Visibility = Visibility.Visible;
        }

        private void LinkFixDiffModalClose_Click(object sender, RoutedEventArgs e)
        {
            LinkFixDiffModalOverlay.Visibility = Visibility.Collapsed;
        }

        private async void LinkFixDiffModalApply_Click(object sender, RoutedEventArgs e)
        {
            if (_lastLinkFixTargets == null || _lastLinkFixTargets.Count == 0)
            {
                LinkFixDiffModalOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            GlobalProgressBar.Visibility = Visibility.Visible;
            GlobalProgressBar.IsIndeterminate = true;

            var progress = new Progress<(string Path, bool Success)>(p =>
            {
                StatusTextBlock.Text = $"修復中: {Path.GetFileName(p.Path)} ({(p.Success ? "成功" : "失敗")})";
            });

            try
            {
                using var cts = new CancellationTokenSource();
                var successCount = await _linkFixService.ExecuteFixAsync(_lastLinkFixTargets, progress, cts.Token);
                LinkItemsDataGrid.Items.Refresh();
                LinkFixDiffModalOverlay.Visibility = Visibility.Collapsed;

                // Verify: 処理結果の検証
                if (successCount == _lastLinkFixTargets.Count)
                {
                    ShowToast($"✅ ショートカット修復完了 (検証済): {successCount} / {_lastLinkFixTargets.Count} 件 全て修復");
                }
                else
                {
                    ShowToast($"⚠️ ショートカット修復完了 (一部失敗): {successCount} / {_lastLinkFixTargets.Count} 件");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"修復実行エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                GlobalProgressBar.Visibility = Visibility.Collapsed;
            }
        }
        private void LinkGenerateGpoButton_Click(object sender, RoutedEventArgs e)
        {
            var oldPattern = LinkOldPatternTextBox.Text.Trim();
            var newPattern = LinkNewPatternTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(oldPattern) || string.IsNullOrWhiteSpace(newPattern))
            {
                MessageBox.Show("置換前（旧パス）と置換後（新パス）を入力してください。", "入力確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "GPOログオンスクリプトの保存先を選択",
                Filter = "PowerShell スクリプト (*.ps1)|*.ps1|すべてのファイル (*.*)|*.*",
                FileName = "Repair-Shortcuts.ps1"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    _linkFixService.GenerateGpoLogonScript(dialog.FileName, oldPattern, newPattern);
                    ShowToast("GPOログオンスクリプトを生成しました");
                    Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"スクリプト生成失敗: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        #endregion

        #region Audit & Hygiene Tab
        private void AuditBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "監査対象ディレクトリを選択"
            };
            if (dialog.ShowDialog() == true)
            {
                AuditPathTextBox.Text = dialog.FolderName;
            }
        }

        private async void AuditStartButton_Click(object sender, RoutedEventArgs e)
        {
            var target = AuditPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
            {
                MessageBox.Show("有効な監査対象ディレクトリを入力してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _auditCts?.Cancel();
            _auditCts = new CancellationTokenSource();

            GlobalProgressBar.Visibility = Visibility.Visible;
            GlobalProgressBar.IsIndeterminate = true;
            AuditStatusText.Text = "監査スキャン中...";

            var options = new AuditOptions
            {
                TargetDirectory = target,
                CheckDuplicates = AuditCheckDuplicatesCheckBox.IsChecked == true,
                CheckDormant = AuditCheckDormantCheckBox.IsChecked == true,
                CheckPathLimits = AuditCheckPathLimitsCheckBox.IsChecked == true
            };

            var progress = new Progress<AuditProgress>(p =>
            {
                AuditStatusText.Text = $"{p.CurrentStatus} ({p.ScannedFilesCount:N0}件走査 / 課題: {p.IssueCount}件)";
                StatusTextBlock.Text = AuditStatusText.Text;
            });

            try
            {
                var (summary, items) = await _auditService.RunAuditAsync(options, progress, _auditCts.Token);
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

                // Update KPI Cards
                AuditKpiTotalFiles.Text = summary.InaccessibleDirectoriesCount > 0
                    ? $"{summary.TotalFilesScanned:N0} 件 (⚠️未走査 {summary.InaccessibleDirectoriesCount})"
                    : $"{summary.TotalFilesScanned:N0} 件";
                AuditKpiDupWasted.Text = summary.DuplicateWastedSizeFormatted;
                AuditKpiDormantSize.Text = summary.DormantSizeFormatted;
                AuditKpiPathLimits.Text = $"{summary.PathTooLongCount + summary.InvalidCharCount} 件";

                string statusMsg = summary.InaccessibleDirectoriesCount > 0
                    ? $"完了: 課題 {items.Count} 件検出 (⚠️アクセス拒否: {summary.InaccessibleDirectoriesCount} 箇所)"
                    : $"完了: 課題 {items.Count} 件検出";
                AuditStatusText.Text = statusMsg;
                ShowToast(statusMsg);
            }
            catch (OperationCanceledException)
            {
                AuditStatusText.Text = "監査を中止しました。";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"監査エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                AuditStatusText.Text = "エラー発生";
            }
            finally
            {
                GlobalProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void AuditExportExcelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastAuditItems == null || _lastAuditItems.Count == 0)
            {
                MessageBox.Show("出力対象の監査結果がありません。先にスキャンを実行してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "Excelレポートの保存先",
                Filter = "Excel ワークブック (*.xlsx)|*.xlsx",
                InitialDirectory = GetDefaultExportDirectory(),
                FileName = $"FolderMorpher_AuditReport_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    _excelService.GenerateComprehensiveReport(
                        dialog.FileName,
                        AuditPathTextBox.Text.Trim(),
                        _lastAuditSummary,
                        _lastAuditItems,
                        _lastMediaSummary,
                        _lastMediaImages.Concat(_lastMediaVideos).ToList());

                    ShowToast("Excelレポートを出力しました");
                    Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Excel出力エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void AuditExportCsvButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastAuditItems == null || _lastAuditItems.Count == 0)
            {
                MessageBox.Show("出力対象のデータがありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "CSV棚卸し台帳の保存先",
                Filter = "CSVファイル (*.csv)|*.csv",
                FileName = $"FolderMorpher_AuditList_{DateTime.Now:yyyyMMdd}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    _auditService.ExportAuditCsv(dialog.FileName, _lastAuditItems);
                    ShowToast("CSV台帳を出力しました");
                    Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"CSV出力エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void AuditGenArchiveScriptButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastAuditItems == null || _lastAuditItems.Count == 0)
            {
                MessageBox.Show("対象となる休眠・重複ファイルがありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 重複ファイル（原本候補以外）が存在するか確認し、安全オプトインを選択させる
            bool hasDuplicates = _lastAuditItems.Any(i => i.IssueType == AuditIssueType.Duplicate && !i.IsOriginalCandidate);
            bool includeDuplicates = false;

            if (hasDuplicates)
            {
                var choice = MessageBox.Show(
                    "検出された重複ファイル（原本候補以外）も退避バッチに含めますか？\n\n" +
                    "【はい】: 休眠ファイル ＋ 重複ファイル（原本候補以外）を両方退避\n" +
                    "【いいえ (推奨)】: 休眠ファイル（3年以上未更新）のみを安全に退避\n\n" +
                    "※別部署や別システムの設定ファイル等の意図しない誤退避を防ぐため、通常は【いいえ】を推奨します。",
                    "退避対象の選択（重複ファイルの安全確認）",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (choice == MessageBoxResult.Cancel) return;
                includeDuplicates = (choice == MessageBoxResult.Yes);
            }

            var dialog = new SaveFileDialog
            {
                Title = "安全退避バッチの保存先",
                Filter = "バッチファイル (*.bat)|*.bat",
                InitialDirectory = GetDefaultExportDirectory(),
                FileName = includeDuplicates ? "Archive-Dormant-And-Duplicates.bat" : "Archive-Dormant-Files.bat"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string dest = Path.Combine(Path.GetDirectoryName(dialog.FileName) ?? @"C:\", "FolderMorpher_Archive");
                    _auditService.GenerateArchiveRobocopyScript(dialog.FileName, _lastAuditItems, AuditPathTextBox.Text.Trim(), dest, includeDuplicates);
                    ShowToast("安全退避バッチを生成しました");
                    Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"バッチ生成エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private void ApplyAuditFilters()
        {
            if (AuditCategoryFilterComboBox == null || AuditSearchFilterTextBox == null || AuditItemsDataGrid == null)
                return;

            string selectedTag = (AuditCategoryFilterComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "All";
            string query = AuditSearchFilterTextBox.Text.Trim();

            var filtered = _lastAuditItems.AsEnumerable();

            if (selectedTag == "Duplicate")
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
            resultList = AuditReportService.SortAuditItems(resultList, _auditSortProperty, _auditSortDescending);

            // 一括仮想化バインド（1件ずつAddするループを撤廃し、数十万件でも一瞬で表示切替）
            AuditItemsDataGrid.ItemsSource = resultList;

            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            string baseTitle = isJa ? "検出された課題・断捨離候補一覧" : "Detected Issues & Cleanup Candidates";
            if (_lastAuditItems.Count > 0)
            {
                AuditTableTitleText.Text = $"{baseTitle} ({resultList.Count:N0} / {_lastAuditItems.Count:N0} 件)";
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

            try
            {
                if (File.Exists(item.FullPath))
                {
                    Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
                }
                else if (Directory.Exists(item.DirectoryPath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.DirectoryPath}\"") { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open explorer for audit item: {ex.Message}");
            }
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
                switch (tag)
                {
                    case "DupCopyOnly":
                        // 重複ファイルの原本候補以外を選択（原本は保護）
                        foreach (var ai in _lastAuditItems)
                        {
                            if (ai.IssueType == AuditIssueType.Duplicate)
                            {
                                ai.IsChecked = !ai.IsOriginalCandidate;
                            }
                            else
                            {
                                ai.IsChecked = false;
                            }
                        }
                        ShowToast("重複ファイルの原本以外（コピー）を一括選択しました");
                        break;

                    case "Dormant3Y":
                        // 3年以上前の休眠ファイルを選択
                        DateTime threshold3Y = DateTime.Now.AddYears(-3);
                        foreach (var ai in _lastAuditItems)
                        {
                            ai.IsChecked = (ai.IssueType == AuditIssueType.Dormant && ai.LastWriteTime < threshold3Y);
                        }
                        ShowToast("3年以上未更新の休眠ファイルを選択しました");
                        break;

                    case "Dormant5Y":
                        // 5年以上前の休眠ファイルを選択
                        DateTime threshold5Y = DateTime.Now.AddYears(-5);
                        foreach (var ai in _lastAuditItems)
                        {
                            ai.IsChecked = (ai.IssueType == AuditIssueType.Dormant && ai.LastWriteTime < threshold5Y);
                        }
                        ShowToast("5年以上未更新の休眠ファイルを選択しました");
                        break;

                    case "SelectVisible":
                        // 現在の絞り込み表示中のみすべて選択
                        foreach (var ai in visibleList)
                        {
                            ai.IsChecked = true;
                        }
                        ShowToast($"表示中の {visibleList.Count:N0} 件を選択しました");
                        break;

                    case "ClearAll":
                        // すべて選択解除
                        foreach (var ai in _lastAuditItems)
                        {
                            ai.IsChecked = false;
                        }
                        if (AuditHeaderCheckBox != null) AuditHeaderCheckBox.IsChecked = false;
                        ShowToast("選択をすべて解除しました");
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

        private void UpdateLiveSelectedReduction()
        {
            if (AuditLiveSelectedReductionText == null) return;
            if (_isAuditBatchUpdating) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(UpdateLiveSelectedReduction));
                return;
            }

            if (_lastAuditItems == null || _lastAuditItems.Count == 0)
            {
                AuditLiveSelectedReductionText.Text = "0 B (0 件)";
                return;
            }

            // Medium 1: 物理ファイル単位の削除実行計画サービス（AuditCleanupService.BuildPlan）を正本として再利用
            // 全監査行横断で原本候補パス（聖域）を特定し、同一FullPathの重複・休眠が混在していても原本候補を確実に除外
            var plans = AuditCleanupService.BuildPlan(_lastAuditItems);
            var validPlans = plans.Where(p => !p.IsOriginalCandidate).ToList();

            long totalBytes = validPlans.Sum(x => x.Size);
            int count = validPlans.Count;

            AuditLiveSelectedReductionText.Text = $"{FileItemNode.FormatBytes(totalBytes)} ({count:N0} 件)";
        }

        private async void AuditDeleteSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastAuditItems == null || _lastAuditItems.Count == 0)
            {
                MessageBox.Show("削除対象のファイルがありません。先に監査スキャンを実行してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 1. 全監査アイテムから物理ファイル（FullPath）単位の実行計画を作成（正本の整線）
            var plans = AuditCleanupService.BuildPlan(_lastAuditItems);
            if (plans.Count == 0)
            {
                MessageBox.Show("削除するファイルが選択されていません。チェックボックスでファイルを選択してから実行してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // 2. 原本候補保護の安全ガード（IssueTypeに関係なく、原本候補のFullPathが含まれていれば必ず発動）
            var originalPlans = plans.Where(p => p.IsOriginalCandidate).ToList();
            if (originalPlans.Count > 0)
            {
                var origResult = MessageBox.Show(
                    $"⚠️ 警告: 選択項目の中に、重複グループの【原本候補】が {originalPlans.Count:N0} 件含まれています！\n\n" +
                    "原本を削除すると、そのグループの全データが消失する恐れがあります。\n\n" +
                    "・[はい (Yes)] : 原本候補を除外して、重複コピーのみ削除する（推奨・安全）\n" +
                    "・[いいえ (No)] : 原本候補も含め、選択された全ファイルを削除する\n" +
                    "・[キャンセル] : 処理を中止する",
                    "原本候補の検出 - 削除安全確認",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (origResult == MessageBoxResult.Cancel) return;

                if (origResult == MessageBoxResult.Yes)
                {
                    // 原本候補の全関連行（Dormant/Duplicate問わず）のチェックを外す
                    foreach (var op in originalPlans)
                    {
                        foreach (var ai in op.AssociatedItems)
                        {
                            ai.IsChecked = false;
                        }
                    }
                    plans.RemoveAll(p => p.IsOriginalCandidate);
                    if (plans.Count == 0)
                    {
                        ShowToast("原本候補を除外した結果、削除対象が0件になりました");
                        return;
                    }
                }
            }

            // 3. 完全削除の最終確認ダイアログ（物理ファイル単位で正確な件数・容量を表示）
            long totalBytes = plans.Sum(x => x.Size);
            string sizeFormatted = FileItemNode.FormatBytes(totalBytes);
            var confirm = MessageBox.Show(
                $"選択された {plans.Count:N0} 件（合計 {sizeFormatted}）のファイルを【完全に削除】します。\n\n" +
                "⚠️ 注意:\n" +
                "・ファイルはごみ箱に入らず完全に削除され、アプリ側から復元することはできません。\n" +
                "・本当に削除を実行してもよろしいですか？",
                "ファイル完全削除の確認",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Stop);

            if (confirm != MessageBoxResult.OK) return;

            // 4. バックグラウンド削除処理（AuditCleanupService により物理1回削除＆属性復元）
            AuditDeleteSelectedButton.IsEnabled = false;
            AuditStartButton.IsEnabled = false;
            GlobalProgressBar.Visibility = Visibility.Visible;
            AuditStatusText.Text = "ファイル削除中...";

            var result = await Task.Run(() => AuditCleanupService.ExecutePlan(plans));

            // 5. データ・UIの最新化（削除されたFullPathを持つ全関連AuditItemを一括除去）
            _lastAuditItems.RemoveAll(x => result.DeletedPaths.Contains(x.FullPath));
            ApplyAuditFilters();
            UpdateAuditKpiAfterDeletion();

            GlobalProgressBar.Visibility = Visibility.Collapsed;
            AuditDeleteSelectedButton.IsEnabled = true;
            AuditStartButton.IsEnabled = true;
            AuditStatusText.Text = $"削除完了: {result.SuccessCount:N0} 件削除 ({FileItemNode.FormatBytes(result.FreedBytes)} 削減)";

            if (result.Errors.Count > 0)
            {
                MessageBox.Show(
                    $"{result.SuccessCount:N0} 件のファイルを削除しました（{FileItemNode.FormatBytes(result.FreedBytes)} 削減）。\n\n" +
                    $"以下の {result.Errors.Count:N0} 件でエラーが発生しました:\n" +
                    string.Join("\n", result.Errors.Take(5)) + (result.Errors.Count > 5 ? $"\n...他 {result.Errors.Count - 5} 件" : ""),
                    "削除完了（一部エラー）",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            else
            {
                ShowToast($"🗑️ {result.SuccessCount:N0} 件のファイルを完全削除しました（{FileItemNode.FormatBytes(result.FreedBytes)} 削減）");
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
                .Where(x => x.IssueType == AuditIssueType.Dormant)
                .Sum(x => x.Size);
            int dormantCount = _lastAuditItems.Count(x => x.IssueType == AuditIssueType.Dormant);

            int pathLimits = _lastAuditItems.Count(x => x.IssueType == AuditIssueType.PathTooLong || x.IssueType == AuditIssueType.InvalidChar);

            _lastAuditSummary.DuplicateWastedBytes = dupWasted;
            _lastAuditSummary.DuplicateCount = dupCount;
            _lastAuditSummary.DormantBytes = dormantSize;
            _lastAuditSummary.DormantCount = dormantCount;
            _lastAuditSummary.PathTooLongCount = _lastAuditItems.Count(x => x.IssueType == AuditIssueType.PathTooLong);
            _lastAuditSummary.InvalidCharCount = _lastAuditItems.Count(x => x.IssueType == AuditIssueType.InvalidChar);

            AuditKpiDupWasted.Text = _lastAuditSummary.DuplicateWastedSizeFormatted;
            AuditKpiDormantSize.Text = _lastAuditSummary.DormantSizeFormatted;
            AuditKpiPathLimits.Text = $"{pathLimits:N0} 件";
        }
        #endregion

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
            if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
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
                var (images, videos) = await _mediaService.ScanMediaAsync(options, progress, _mediaCts.Token);
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
            MediaDiffSummaryText.Text = $"📊 対象: {targets.Count}枚 / 設定: 長辺 {maxDim}px超・品質 {quality}% (🛡️ 聖域保護: {excludedCount}枚スキップ)";
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
                var summary = await _mediaService.OptimizeImagesAsync(_lastMediaTargets, options, progress, cts.Token);
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

        private void MediaGenVideoBatchButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastMediaVideos == null || _lastMediaVideos.Count == 0)
            {
                MessageBox.Show("圧縮対象の動画がありません。先にメディア走査を実行してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "巨大動画 夜間圧縮バッチの保存先",
                Filter = "バッチファイル (*.bat)|*.bat",
                FileName = "Compress-Videos-Nightly.bat"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    _mediaService.GenerateVideoCompressBatch(dialog.FileName, _lastMediaVideos);
                    ShowToast("夜間動画圧縮バッチを生成しました");
                    Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"バッチ生成エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void MediaExportExcelButton_Click(object sender, RoutedEventArgs e)
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
                    _excelService.GenerateComprehensiveReport(
                        dialog.FileName,
                        MediaPathTextBox.Text.Trim(),
                        _lastAuditSummary,
                        _lastAuditItems,
                        _lastMediaSummary,
                        allItems);

                    ShowToast("Excelレポートを出力しました");
                    Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
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

            try
            {
                if (File.Exists(item.FullPath))
                {
                    Process.Start("explorer.exe", $"/select,\"{item.FullPath}\"");
                }
                else
                {
                    var dir = Path.GetDirectoryName(item.FullPath);
                    if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    {
                        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to open explorer for media item: {ex.Message}");
            }
        }
        #endregion

        #region Localization (i18n)
        private void LanguageToggleButton_Click(object sender, RoutedEventArgs e)
        {
            LocalizationService.Instance.ToggleLanguage();
        }

        private void ApplyLocalization()
        {
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;

            // 言語切り替えボタン自体の表示（次に切り替わる言語を提示）
            LanguageToggleButton.Content = isJa ? "🌐 EN" : "🌐 JA";
            LanguageToggleButton.ToolTip = isJa ? "英語に切り替え / Switch to English" : "日本語に切り替え / Switch to Japanese";
            LanguageToggleButtonMini.ToolTip = LanguageToggleButton.ToolTip;

            // サイドバー タブ名
            NavTabStorage.Content = isJa ? "容量分析 & 監視" : "Storage Explorer";
            NavTabLiveAcl.Content = isJa ? "権限コントロール" : "Live ACL";
            NavTabSimulation.Content = isJa ? "移行スタジオ" : "Simulation Studio";
            NavTabLinkFix.Content = isJa ? "リンク一括修復" : "LinkFixer";
            NavTabAudit.Content = isJa ? "断捨離・健全化" : "Audit & Hygiene";
            NavTabMedia.Content = isJa ? "メディア最適化" : "Media Optimizer";

            // ==========================================
            // Tab 0 (Storage Explorer)
            // ==========================================
            AddStorageTabButton.Content = isJa ? "＋ 新しいタブ" : "＋ New Tab";
            StorageBrowseButton.Content = isJa ? "参照..." : "Browse...";
            ScanButton.Content = isJa ? "スキャン開始" : "Start Scan";
            CancelButton.Content = isJa ? "中止" : "Cancel";
            ExportButton.Content = isJa ? "Excel / CSV 出力" : "Export Excel/CSV";
            TabHistoryButton.Content = isJa ? "📈 容量推移グラフ" : "📈 History Graph";

            StorageKpiScannedSizeTitle.Text = isJa ? "スキャン対象 容量" : "Scanned Capacity";
            StorageKpiLargestFileTitle.Text = isJa ? "最大ファイル Top 1" : "Largest File Top 1";
            StorageKpiDiffTrendTitle.Text = isJa ? "前回差分推移" : "Historical Growth";

            if (InsightsTargetScopeTextBlock.Text == "スコープ: 全体" || InsightsTargetScopeTextBlock.Text == "Scope: Entire Scan")
            {
                InsightsTargetScopeTextBlock.Text = isJa ? "スコープ: 全体" : "Scope: Entire Scan";
            }
            if (TrendDiffTextBlock.Text == "比較データなし" || TrendDiffTextBlock.Text == "No comparison data")
            {
                TrendDiffTextBlock.Text = isJa ? "比較データなし" : "No comparison data";
            }
            if (LastScanDateTextBlock.Text == "初回スキャン" || LastScanDateTextBlock.Text == "Initial scan")
            {
                LastScanDateTextBlock.Text = isJa ? "初回スキャン" : "Initial scan";
            }
            if (TotalFilesTextBlock.Text == "0 ファイル / 0 フォルダ" || TotalFilesTextBlock.Text == "0 Files / 0 Folders")
            {
                TotalFilesTextBlock.Text = isJa ? "0 ファイル / 0 フォルダ" : "0 Files / 0 Folders";
            }

            ColTreeName.Header = isJa ? "フォルダー / ファイル名" : "Folder / File Name";
            ColTreeSize.Header = isJa ? "容量" : "Size";
            ColTreeShare.Header = isJa ? "全体占有率" : "% of Scanned";
            ColTreeCount.Header = isJa ? "配下ファイル数" : "Item Count";
            ColTreeModified.Header = isJa ? "最終更新日時" : "Last Modified";

            StorageTopFilesTitleText.Text = isJa ? "巨大ファイル Top 10 (直接起動対応)" : "Largest Files Top 10 (Double-Click to Reveal)";
            ColTopFileName.Header = isJa ? "ファイル名" : "File Name";
            ColTopFileSize.Header = isJa ? "容量" : "Size";

            StorageDirectSharesTitleText.Text = isJa ? "選択フォルダーの内訳 (直下シェア)" : "Folder Content Share Breakdown";
            StorageDirectSharesSubText.Text = isJa ? "Wクリックで下層へドリルダウン展開" : "Double-click item to drill down in tree";
            ColShareName.Header = isJa ? "直下アイテム" : "Direct Child Item";
            ColShareSize.Header = isJa ? "容量" : "Size";
            ColShareRatio.Header = isJa ? "直下比率" : "Share Ratio";

            // ==========================================
            // Tab 1 (Live ACL & Effective Access)
            // ==========================================
            LiveAclStudioControl?.ApplyLocalization(isJa);

            // ==========================================
            // Tab 2 (Simulation Studio)
            // ==========================================
            SimTargetBrowseButton.Content = isJa ? "参照..." : "Browse...";
            SimSourceLoadButton.Content = isJa ? "読込" : "Load";
            SimCloneSelectedButton.Content = isJa ? "➡️ 選択フォルダを中央へ新設配置" : "➡️ Clone Selected to Center";
            SimAddRootFolderButton.Content = isJa ? "＋ ルートフォルダ新設" : "＋ Add Root Folder";
            SimSaveProjectButton.Content = isJa ? "💾 保存" : "💾 Save";
            SimLoadProjectButton.Content = isJa ? "📂 読込" : "📂 Load";
            SimDiffReviewButton.Content = isJa ? "⚖️ 差分 (Diff)" : "⚖️ Review Diffs";
            SimDeploySkeletonButton.Content = isJa ? "🚀 スケルトン作成" : "🚀 Deploy Skeleton";
            SimExportScriptsButton.Content = isJa ? "⚙️ 移行スクリプト" : "⚙️ Export Scripts";
            SimExportExcelButton.Content = isJa ? "📊 Excel設計書" : "📊 Export Excel";
            SimInheritCheckBox.Content = isJa ? "親からの権限継承を含める" : "Inherit from parent";
            SimOpenSecModalButton.Content = isJa ? "⚙️ セキュリティ詳細設定" : "⚙️ Advanced Security";

            if (SimProjectNameTextBox.Text == "新ファイルサーバー移行設計_Ver1" || SimProjectNameTextBox.Text == "New File Server Migration Plan_Ver1")
            {
                SimProjectNameTextBox.Text = isJa ? "新ファイルサーバー移行設計_Ver1" : "New File Server Migration Plan_Ver1";
            }
            if (SimSelectedFolderNameText.Text == "(未選択 - 上のフォルダをクリック)" || SimSelectedFolderNameText.Text == "(None selected - Click a folder above)")
            {
                SimSelectedFolderNameText.Text = isJa ? "(未選択 - 上のフォルダをクリック)" : "(None selected - Click a folder above)";
            }
            if (!_adService.IsDomainJoined)
            {
                DomainStatusText.Text = isJa ? "🟡 ローカル環境 (AD未接続)" : "🟡 Local PC (No AD Domain)";
            }

            SimTargetRootLabel.Text = isJa ? "移行先 新サーバーのルートパス (UNC / ローカル)" : "Target Root Path on Destination Server (UNC / Local)";
            SimSourceTitleText.Text = isJa ? "現行ファイルサーバー (移行元)" : "Source File Server (Existing)";
            SimSourceSubText.Text = isJa ? "フォルダーを選択して中央へドラッグ＆ドロップ、または下部ボタンで新設ツリーに配置" : "Select folders and drag & drop to center, or use button below";
            SimMockTreeTitleText.Text = isJa ? "新サーバー仮想ツリー設計 (FolderMorph Studio)" : "Target Virtual Tree Architecture (FolderMorph Studio)";
            SimMockTreeSubText.Text = isJa ? "N:1 統合・階層再編成・新設計ACLを直感的にデザイン。右クリックでフォルダ追加/削除" : "Intuitive N:1 consolidation, restructuring & ACL design. Right-click to add/remove";
            SimSelectedFolderPrefixText.Text = isJa ? "選択中: " : "Target: ";
            SimSubfolderDropHintText.Text = isJa ? "➕ 左の現行サーバーまたはエクスプローラーからフォルダをドロップして追加" : "➕ Drop folders from source server or Explorer to add subfolders";
            SimMappingTitleText.Text = isJa ? "🔗 移行元マッピング (データ移行元 / N:1統合)" : "🔗 Source Mappings (Data Sources / N:1 Consolidation)";
            SimAclTitleText.Text = isJa ? "新設計 アクセス権エントリ (ACE)" : "Target Access Control Entries (ACEs)";
            SimAdHeaderTitle.Text = isJa ? "Active Directory / ローカル候補" : "Active Directory / Local Principals";
            SimAdHeaderSubText.Text = isJa ? "中央の権限エリアへドラッグ＆ドロップして付与" : "Drag & drop to center permissions area to grant";

            // ==========================================
            // Tab 3 (LinkFixer)
            // ==========================================
            LinkFixHeaderTitle.Text = isJa ? "🔗 ショートカット ＆ Officeリンク一括修復（LinkFixer）" : "🔗 Broken Link & Office Reference Repair (LinkFixer)";
            LinkFixHeaderDesc.Text = isJa ? "ファイルサーバー移行後に切断されたショートカット (.lnk) および Excel 内部リンク数式 (.xlsx / .xlsm) を高速検出し、新パスへ一括書き換えします。" : "Quickly scans and repairs broken shortcut (.lnk) targets and Excel formula references (.xlsx / .xlsm) after file server migrations.";
            LinkSearchScopeLabel.Text = isJa ? "走査対象フォルダー (クライアントPCまたはサーバー)" : "Target Scan Directory (Client PC or File Server)";
            LinkOldPatternLabel.Text = isJa ? "旧サーバーパス (置換前)" : "Old Server Path (To Replace)";
            LinkNewPatternLabel.Text = isJa ? "新サーバーパス (置換後)" : "New Server Path (Replacement)";
            LinkTableTitleText.Text = isJa ? "検出された切断リンク一覧" : "Detected Broken Links";

            LinkGenerateGpoButton.Content = isJa ? "📜 GPOログオンスクリプト生成 (.ps1)" : "📜 Generate GPO Script (.ps1)";
            LinkScanButton.Content = isJa ? "切断リンク検出スキャン" : "Scan Broken Links";
            LinkFixExecuteButton.Content = isJa ? "⚡ 一括修復を実行 (バックアップ付)" : "⚡ Execute Fix (with Backup)";
            LinkIncludeOfficeCheckBox.Content = isJa ? "Officeファイル内部リンク (.xlsx/.xlsm) も対象に含める" : "Include Office internal links (.xlsx/.xlsm)";

            ColLinkFileName.Header = isJa ? "ファイル名" : "File Name";
            ColLinkFileType.Header = isJa ? "種別" : "Type";
            ColLinkOldTarget.Header = isJa ? "置換前の旧リンク先" : "Old Target Path";
            ColLinkNewTarget.Header = isJa ? "置換後の新リンク先" : "New Target Path";
            ColLinkStatus.Header = isJa ? "状態" : "Status";

            // ==========================================
            // Tab 4 (Audit & Hygiene)
            // ==========================================
            AuditBrowseButton.Content = isJa ? "📁 参照" : "📁 Browse...";
            if (AuditStatusText.Text == "待機中" || AuditStatusText.Text == "Ready")
            {
                AuditStatusText.Text = isJa ? "待機中" : "Ready";
            }
            if (AuditKpiTotalFiles.Text == "0 件" || AuditKpiTotalFiles.Text == "0 Items")
            {
                AuditKpiTotalFiles.Text = isJa ? "0 件" : "0 Items";
            }
            if (AuditKpiPathLimits.Text == "0 件" || AuditKpiPathLimits.Text == "0 Items")
            {
                AuditKpiPathLimits.Text = isJa ? "0 件" : "0 Items";
            }
            AuditHeaderTitle.Text = isJa ? "🧹 ファイルサーバー健全化 ＆ 断捨離（衛生監査・容量削減）" : "🧹 File Server Hygiene & Cleanup";
            AuditHeaderDesc.Text = isJa ? "重複ファイル (SHA256)、休眠ファイル (3年以上未更新)、パス長260文字超、移行禁則文字を一括抽出し、安全な棚卸し台帳や退避スクリプトを生成します。" : "Batch detects duplicates (SHA256), dormant files (3+ years), paths > 260 chars, and migration-invalid characters. Generates safe audit ledgers and archive batches.";
            AuditTargetFolderLabel.Text = isJa ? "監査対象ディレクトリ (UNC / ローカル)" : "Target Audit Directory (UNC / Local)";
            AuditKpiTotalFilesTitle.Text = isJa ? "総走査ファイル数" : "Total Files Scanned";
            AuditKpiDupWastedTitle.Text = isJa ? "重複ファイルによる無駄" : "Wasted by Duplicates";
            AuditKpiDormantSizeTitle.Text = isJa ? "休眠ファイル容量 (3年超)" : "Dormant Capacity (3+ Yrs)";
            string auditBaseTitle = isJa ? "検出された課題・断捨離候補一覧" : "Detected Issues & Cleanup Candidates";
            AuditTableTitleText.Text = _lastAuditItems.Count > 0
                ? $"{auditBaseTitle} ({_auditVisibleItems.Count:N0} / {_lastAuditItems.Count:N0} 件)"
                : auditBaseTitle;
            if (AuditFilterLabel != null) AuditFilterLabel.Text = isJa ? "絞り込み:" : "Filter:";
            if (AuditCategoryFilterComboBox?.Items.Count >= 4)
            {
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[0]).Content = isJa ? "すべて表示" : "Show All";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[1]).Content = isJa ? "重複ファイルのみ" : "Duplicates Only";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[2]).Content = isJa ? "休眠ファイルのみ" : "Dormant Only";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[3]).Content = isJa ? "パス長・禁則のみ" : "Path / Invalid Only";
            }
            if (AuditSearchPlaceholder != null)
            {
                AuditSearchPlaceholder.Text = isJa ? "🔍 ファイル名/パス検索..." : "🔍 Search file/path...";
            }

            AuditStartButton.Content = isJa ? "🔍 監査スキャン開始" : "🔍 Start Audit Scan";
            AuditExportExcelButton.Content = isJa ? "📊 Excelレポート出力 (.xlsx)" : "📊 Export Excel (.xlsx)";
            AuditExportCsvButton.Content = isJa ? "📄 CSV台帳出力" : "📄 Export CSV";
            AuditGenArchiveScriptButton.Content = isJa ? "📦 安全退避バッチ生成 (.bat)" : "📦 Generate Archive Batch (.bat)";
            AuditCheckDuplicatesCheckBox.Content = isJa ? "重複ファイル (SHA256)" : "Duplicates (SHA256)";
            AuditCheckDormantCheckBox.Content = isJa ? "休眠ファイル (3年以上)" : "Dormant (3+ Years)";
            AuditCheckPathLimitsCheckBox.Content = isJa ? "パス長260字超/禁則文字" : "Path Limits / Invalid Chars";

            ColAuditIssueType.Header = isJa ? "問題種別" : "Issue Type";
            ColAuditDupGroup.Header = isJa ? "重複グループ" : "Duplicate Group";
            ColAuditFileName.Header = isJa ? "ファイル名" : "File Name";
            ColAuditSize.Header = isJa ? "容量" : "Size";
            ColAuditModified.Header = isJa ? "最終更新日時" : "Last Modified";
            ColAuditDetail.Header = isJa ? "詳細" : "Details";
            ColAuditFullPath.Header = isJa ? "完全パス" : "Full Path";

            // ==========================================
            // Tab 5 (Media Optimizer)
            // ==========================================
            MediaBrowseButton.Content = isJa ? "📁 参照" : "📁 Browse...";
            if (MediaStatusText.Text == "待機中" || MediaStatusText.Text == "Ready")
            {
                MediaStatusText.Text = isJa ? "待機中" : "Ready";
            }
            if (MediaKpiImagesCount.Text == "0 枚" || MediaKpiImagesCount.Text == "0 Items")
            {
                MediaKpiImagesCount.Text = isJa ? "0 枚" : "0 Items";
            }
            if (MediaKpiVideosCount.Text == "0 本" || MediaKpiVideosCount.Text == "0 Videos")
            {
                MediaKpiVideosCount.Text = isJa ? "0 本" : "0 Videos";
            }
            if (MediaKpiOptimizedCount.Text == "0 枚" || MediaKpiOptimizedCount.Text == "0 Items")
            {
                MediaKpiOptimizedCount.Text = isJa ? "0 枚" : "0 Items";
            }
            MediaHeaderTitle.Text = isJa ? "🖼️ メディア・オプティマイザ（写真・画像最適化 ＆ 巨大動画攻略）" : "🖼️ Media Optimizer (Photo Optimization & Video Nightly Batch)";
            MediaHeaderDesc.Text = isJa ? "聖域（_Master、印刷用、RAW等）を自動保護しながら、大容量写真（2MB超）を最適化（長辺2560px超は縮小/85%品質/Exif・日時保持）で上書き軽量化し、巨大動画のTop抽出と夜間圧縮バッチを出力します。" : "Protects sanctuary folders (_Master, Print, RAW), optimizes large photos (>2MB) in-place with high quality (resizes if >2560px, preserves Exif & timestamps), and extracts large videos for nightly GPU H.265 compression.";
            MediaTargetDirLabel.Text = isJa ? "走査対象ディレクトリ (UNC / ローカル)" : "Target Directory (UNC / Local)";
            MediaMaxDimLabel.Text = isJa ? "最大長辺 (px)" : "Max Dimension (px)";
            MediaQualityLabel.Text = isJa ? "画質 (%)" : "Quality (%)";
            MediaMinSizeLabel.Text = isJa ? "最小サイズ (MB)" : "Min Size (MB)";
            MediaKpiImagesCountTitle.Text = isJa ? "走査対象 画像数" : "Photos Found";
            MediaKpiVideosCountTitle.Text = isJa ? "巨大動画 ファイル数" : "Large Videos";
            MediaKpiOptimizedCountTitle.Text = isJa ? "軽量化 完了数" : "Photos Compressed";
            MediaKpiSavedSizeTitle.Text = isJa ? "総削減容量 (解放された空き)" : "Total Capacity Freed";
            MediaTableTitleText.Text = isJa ? "メディア一覧（画像 ＆ 巨大動画）" : "Media List (Images & Large Videos)";

            MediaScanButton.Content = isJa ? "🔍 メディア走査" : "🔍 Scan Media";
            MediaOptimizeButton.Content = isJa ? "🔍 チェック" : "🔍 Check";
            MediaGenVideoBatchButton.Content = isJa ? "🎬 巨大動画 夜間圧縮バッチ出力 (.bat)" : "🎬 Export Nightly Video Batch (.bat)";
            MediaExportExcelButton.Content = isJa ? "📊 Excelレポート出力 (.xlsx)" : "📊 Export Excel (.xlsx)";
            LinkFixExecuteButton.Content = isJa ? "🔍 チェック" : "🔍 Check";

            ColMediaType.Header = isJa ? "種別" : "Type";
            ColMediaFileName.Header = isJa ? "ファイル名" : "File Name";
            ColMediaOriginalSize.Header = isJa ? "元容量" : "Original Size";
            ColMediaOptimizedSize.Header = isJa ? "軽量化後" : "Compressed Size";
            ColMediaSavedSize.Header = isJa ? "削減容量" : "Saved Size";
            ColMediaStatus.Header = isJa ? "状態 / 聖域保護" : "Status / Sanctuary";
            ColMediaFullPath.Header = isJa ? "完全パス" : "Full Path";

            // ==========================================
            // Detailed Permission Modal (SecModal)
            // ==========================================
            SecModalTitleText.Text = isJa ? "🛡️ セキュリティの詳細設定 - " : "🛡️ Advanced Security Settings - ";
            SecModalObjectNameLabel.Text = isJa ? "オブジェクト名:" : "Object name:";
            SecModalPrincipalLabel.Text = isJa ? "プリンシパル (対象アカウント):" : "Principal:";
            SecModalTypeLabel.Text = isJa ? "種類:" : "Type:";
            SecModalAppliesToLabel.Text = isJa ? "適用先:" : "Applies to:";
            SecModalBasicPermTitle.Text = isJa ? "基本アクセス許可:" : "Basic permissions:";
            SecModalRealtimeNotice.Text = isJa ? "※高度な権限と完全リアルタイム連動" : "*Synced in real-time with advanced permissions";
            SecModalAdvPermTitle.Text = isJa ? "⚙️ 高度なアクセス許可 (Windows ACL 14項目完全網羅):" : "⚙️ Advanced permissions (All 14 Windows ACL bits):";
            SecModalAdvPermSubtitle.Text = isJa ? "Windows セキュリティ詳細設定準拠" : "Windows standard security compliant";

            SecChkFullControl.Content = isJa ? "フル コントロール" : "Full control";
            SecChkModify.Content = isJa ? "変更 (Modify)" : "Modify";
            SecChkReadExecute.Content = isJa ? "読み取りと実行" : "Read & execute";
            SecChkList.Content = isJa ? "フォルダーの内容の一覧表示" : "List folder contents";
            SecChkRead.Content = isJa ? "読み取り" : "Read";
            SecChkWrite.Content = isJa ? "書き込み" : "Write";

            SecAdvTraverse.Content = isJa ? "フォルダーのスキャン / ファイルの実行" : "Traverse folder / execute file";
            SecAdvList.Content = isJa ? "フォルダーの一覧 / データの読み取り" : "List folder / read data";
            SecAdvReadAttr.Content = isJa ? "属性の読み取り" : "Read attributes";
            SecAdvReadExtAttr.Content = isJa ? "拡張属性の読み取り" : "Read extended attributes";
            SecAdvCreateFile.Content = isJa ? "ファイルの作成 / データの書き込み" : "Create files / write data";
            SecAdvCreateFolder.Content = isJa ? "フォルダーの作成 / データの追加" : "Create folders / append data";
            SecAdvWriteAttr.Content = isJa ? "属性の書き込み" : "Write attributes";
            SecAdvWriteExtAttr.Content = isJa ? "拡張属性の書き込み" : "Write extended attributes";
            SecAdvDelete.Content = isJa ? "削除" : "Delete";
            SecAdvDeleteSub.Content = isJa ? "サブフォルダーとファイルの削除" : "Delete subfolders and files";
            SecAdvReadPerm.Content = isJa ? "アクセス許可の読み取り" : "Read permissions";
            SecAdvChangePerm.Content = isJa ? "アクセス許可の変更" : "Change permissions";
            SecAdvTakeOwnership.Content = isJa ? "所有権の取得" : "Take ownership";
            SecAdvSync.Content = isJa ? "同期 (Synchronize)" : "Synchronize";

            SecModalCancelButton.Content = isJa ? "キャンセル" : "Cancel";
            SecModalApplyButton.Content = isJa ? "変更を保存" : "Save Changes";

            // ==========================================
            // Diff Modal (変更点 - スケルトン展開)
            // ==========================================
            DiffModalTitleText.Text = isJa ? "⚖️ 変更点" : "⚖️ Changes";
            DiffModalSubTitleText.Text = isJa ? " - 移行設計差分" : " - Migration Architecture Diffs";
            ColDiffType.Header = isJa ? "変化の種別" : "Diff Type";
            ColDiffSource.Header = isJa ? "現行サーバー (Before)" : "Source Server (Before)";
            ColDiffTarget.Header = isJa ? "新環境設計 (After)" : "Target Architecture (After)";
            ColDiffAcl.Header = isJa ? "権限 (ACL) 差分詳細" : "ACL Diff Details";
            DiffModalCloseButton.Content = isJa ? "閉じる" : "Close";
            DiffExportExcelButton.Content = isJa ? "📊 Excel出力" : "📊 Export Excel";
            DiffModalApplyButton.Content = isJa ? "⚡ 適用" : "⚡ Apply";

            // ==========================================
            // LinkFix Diff Modal (変更点 - ショートカット修復)
            // ==========================================
            LinkFixDiffTitleText.Text = isJa ? "⚖️ 変更点" : "⚖️ Changes";
            LinkFixDiffSubTitleText.Text = isJa ? " - ショートカット一括修復" : " - Batch Shortcut Repair";
            LinkFixDiffModalCloseButton.Content = isJa ? "閉じる" : "Close";
            LinkFixDiffModalApplyButton.Content = isJa ? "⚡ 適用" : "⚡ Apply";

            // ==========================================
            // Media Diff Modal (変更点 - 写真軽量化)
            // ==========================================
            MediaDiffTitleText.Text = isJa ? "⚖️ 変更点" : "⚖️ Changes";
            MediaDiffSubTitleText.Text = isJa ? " - 写真・画像軽量化 (上書き縮小)" : " - Image Optimization (In-Place)";
            MediaDiffModalCloseButton.Content = isJa ? "閉じる" : "Close";
            MediaDiffModalApplyButton.Content = isJa ? "⚡ 適用" : "⚡ Apply";

            // Settings Modal
            SettingsButton.ToolTip = isJa ? "環境設定 / Settings" : "Settings";
            SettingsTitleText.Text = isJa ? "⚙️ 環境設定 (Settings)" : "⚙️ Settings";
            SettingsDescText.Text = isJa
                ? "キャッシュ、スナップショット履歴、監査レポートの参照先および保存先を構成します。"
                : "Configure read and write locations for tree caches, snapshot histories, and audit reports.";
            SettingsReadTitleText.Text = isJa ? "📂 キャッシュ・スナップショット 参照先 (読み込み)" : "📂 Cache & Snapshot Read Source";
            SettingsReadDescText.Text = isJa
                ? "共有ファイルサーバー上のマスターキャッシュ（UNCパス等）を指定すると、チーム共通の0秒ツリーや推移履歴を参照できます。"
                : "Specify a master cache folder on a shared file server (e.g. UNC path) to access team-wide 0-second trees and histories.";
            SettingsBrowseReadButton.Content = isJa ? "参照..." : "Browse...";
            SettingsFallbackCheckBox.Content = isJa
                ? "共有参照先にアクセスできない場合は自動でローカルキャッシュを参照する"
                : "Automatically fall back to local cache if shared source is unreachable";
            SettingsWriteTitleText.Text = isJa ? "💾 キャッシュ・スナップショット 保存先 (書き込み)" : "💾 Cache & Snapshot Write Destination";
            SettingsWriteDescText.Text = isJa
                ? "自身がスキャンした結果のツリーキャッシュおよび履歴データの保存場所を選択します。"
                : "Select where your local scans save tree cache and historical data.";
            SettingsWriteLocalText.Text = isJa ? "ローカルに保存" : "Save to Local";
            SettingsWriteLocalSubText.Text = isJa ? " (推奨: マスターキャッシュを上書きしない安全設定)" : " (Recommended: Safe, won't overwrite master cache)";
            SettingsWriteSameText.Text = isJa ? "参照先と同じフォルダーに保存" : "Save to same folder as read source";
            SettingsWriteSameSubText.Text = isJa ? " (管理者・マスター更新者用)" : " (For administrators / master publishers)";
            SettingsWriteCustomText.Text = isJa ? "任意のカスタムフォルダーを指定" : "Specify custom folder";
            SettingsBrowseCustomButton.Content = isJa ? "参照..." : "Browse...";
            SettingsCancelButton.Content = isJa ? "キャンセル" : "Cancel";
            SettingsSaveButton.Content = isJa ? "設定を保存" : "Save Settings";


            // 既存の空タブのタイトルとステータス
            if (StorageTabs != null)
            {
                foreach (var t in StorageTabs)
                {
                    if (t.TabTitle == "新規スキャン" || t.TabTitle == "New Scan")
                    {
                        t.TabTitle = isJa ? "新規スキャン" : "New Scan";
                    }
                    if (t.StatusMessage == "準備完了" || t.StatusMessage == "Ready")
                    {
                        t.StatusMessage = isJa ? "準備完了" : "Ready";
                    }
                }
            }

            // ステータスバー
            if (StatusTextBlock.Text == "準備完了" || StatusTextBlock.Text == "Ready")
            {
                StatusTextBlock.Text = isJa ? "準備完了" : "Ready";
            }

            // 現在のアクティブタブのステータス再反映
            NavTab_Checked(this, new RoutedEventArgs());
        }
        #endregion

        #region Settings Modal (環境設定: キャッシュ共有・保存先)

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = AppSettingsService.Instance.Current;
            SettingsReadPathTextBox.Text = settings.CacheReadPath;
            SettingsFallbackCheckBox.IsChecked = settings.FallbackToLocalOnReadError;

            switch (settings.WriteMode)
            {
                case CacheWriteMode.SameAsRead:
                    SettingsWriteModeSameRadio.IsChecked = true;
                    break;
                case CacheWriteMode.Custom:
                    SettingsWriteModeCustomRadio.IsChecked = true;
                    break;
                case CacheWriteMode.Local:
                default:
                    SettingsWriteModeLocalRadio.IsChecked = true;
                    break;
            }

            SettingsCustomPathTextBox.Text = settings.CacheWriteCustomPath;
            UpdateSettingsCustomPathEnabled();

            SettingsModalOverlay.Visibility = Visibility.Visible;
        }

        private void SettingsWriteModeRadio_Checked(object sender, RoutedEventArgs e)
        {
            UpdateSettingsCustomPathEnabled();
        }

        private void UpdateSettingsCustomPathEnabled()
        {
            if (SettingsCustomPathGrid != null && SettingsWriteModeCustomRadio != null)
            {
                SettingsCustomPathGrid.IsEnabled = SettingsWriteModeCustomRadio.IsChecked == true;
            }
        }

        private void SettingsBrowseReadButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                    ? "キャッシュ・スナップショットの参照先フォルダーを選択 (共有UNCパス可)"
                    : "Select Cache & Snapshot Read Source Folder (UNC supported)",
                InitialDirectory = SettingsReadPathTextBox.Text.Trim()
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                SettingsReadPathTextBox.Text = dialog.FolderName;
            }
        }

        private void SettingsBrowseCustomButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                    ? "キャッシュ・スナップショットの保存先フォルダーを選択"
                    : "Select Cache & Snapshot Write Destination Folder",
                InitialDirectory = SettingsCustomPathTextBox.Text.Trim()
            };
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                SettingsCustomPathTextBox.Text = dialog.FolderName;
            }
        }

        private void SettingsCancelButton_Click(object sender, RoutedEventArgs e)
        {
            SettingsModalOverlay.Visibility = Visibility.Collapsed;
        }

        private void SettingsSaveButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = AppSettingsService.Instance.Current;
            settings.CacheReadPath = SettingsReadPathTextBox.Text.Trim();
            settings.FallbackToLocalOnReadError = SettingsFallbackCheckBox.IsChecked == true;

            if (SettingsWriteModeSameRadio.IsChecked == true)
            {
                settings.WriteMode = CacheWriteMode.SameAsRead;
            }
            else if (SettingsWriteModeCustomRadio.IsChecked == true)
            {
                settings.WriteMode = CacheWriteMode.Custom;
                settings.CacheWriteCustomPath = SettingsCustomPathTextBox.Text.Trim();
            }
            else
            {
                settings.WriteMode = CacheWriteMode.Local;
            }

            AppSettingsService.Instance.Save();
            SettingsModalOverlay.Visibility = Visibility.Collapsed;
            ShowToast(LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                ? "環境設定を保存しました"
                : "Settings saved successfully");
        }

        #endregion
    }
}

