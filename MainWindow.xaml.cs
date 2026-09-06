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

        // Live ACL Management State
        private readonly ObservableCollection<SimAclEntry> _liveAclEntries = new();
        private readonly ObservableCollection<AdPrincipalItem> _liveAclPrincipals = new();
        private bool _isLiveAclEditing = false;

        // Toast notification timer
        private DispatcherTimer? _toastTimer;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LocalizationService.Instance.LanguageChanged += ApplyLocalization;
                ApplyLocalization();

                LoadDrives();
                InitializeStorageTabs();
                InitializeSimulationStudio();
                InitializeLiveAcl();
                await LoadAdPrincipalsAsync();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"MainWindow_Loaded Error: {ex}");
            }
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
            if (StorageTabPanel == null || LiveAclTabPanel == null || SimulationTabPanel == null || LinkFixTabPanel == null ||
                AuditTabPanel == null || MediaTabPanel == null)
                return;

            StorageTabPanel.Visibility = Visibility.Collapsed;
            LiveAclTabPanel.Visibility = Visibility.Collapsed;
            SimulationTabPanel.Visibility = Visibility.Collapsed;
            LinkFixTabPanel.Visibility = Visibility.Collapsed;
            AuditTabPanel.Visibility = Visibility.Collapsed;
            MediaTabPanel.Visibility = Visibility.Collapsed;

            if (NavTabStorage.IsChecked == true)
            {
                StorageTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = "モード: 容量分析 & 監視 (Storage Explorer)";
            }
            else if (NavTabLiveAcl.IsChecked == true)
            {
                LiveAclTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = "モード: 実環境 権限コントロール (Live ACL)";
                if (string.IsNullOrWhiteSpace(LiveAclPathTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    LiveAclPathTextBox.Text = PathTextBox.Text;
                    LoadLiveAclForPath(PathTextBox.Text);
                }
            }
            else if (NavTabSimulation.IsChecked == true)
            {
                SimulationTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = "モード: 移行シミュレーションスタジオ (FolderMorph Studio)";
                if (string.IsNullOrWhiteSpace(SimSourcePathTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    SimSourcePathTextBox.Text = PathTextBox.Text;
                }
            }
            else if (NavTabLinkFix.IsChecked == true)
            {
                LinkFixTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = "モード: ショートカット ＆ Officeリンク修復 (LinkFixer)";
                if (string.IsNullOrWhiteSpace(LinkSearchScopeTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    LinkSearchScopeTextBox.Text = PathTextBox.Text;
                }
            }
            else if (NavTabAudit.IsChecked == true)
            {
                AuditTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = "モード: ファイルサーバー健全化 ＆ 断捨離 (GDMS代替・衛生監査)";
                if (string.IsNullOrWhiteSpace(AuditPathTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    AuditPathTextBox.Text = PathTextBox.Text;
                }
            }
            else if (NavTabMedia.IsChecked == true)
            {
                MediaTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = "モード: メディア・オプティマイザ (写真軽量化 ＆ 巨大動画攻略)";
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
                SidebarFooterPanel.Visibility = Visibility.Collapsed;
                SidebarToggleButton.ToolTip = "サイドバーを展開";
            }
            else
            {
                SidebarBorder.Width = 220;
                SidebarBrandPanel.Visibility = Visibility.Visible;
                SidebarFooterPanel.Visibility = Visibility.Visible;
                SidebarToggleButton.ToolTip = "サイドバーを収縮";
            }
        }
        #endregion

        #region Tab 1: Storage Explorer & Multi-Tab Management
        private void InitializeStorageTabs()
        {
            StorageTabsItemsControl.ItemsSource = StorageTabs;

            var initialTab = new ScanTabModel
            {
                TabTitle = "C: ドライブ",
                TargetPath = @"C:\",
                IsSelected = true
            };
            StorageTabs.Add(initialTab);
            SelectTab(initialTab);
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
            StatusTextBlock.Text = string.IsNullOrEmpty(tab.StatusMessage) ? "準備完了" : tab.StatusMessage;
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
            }
        }

        private void LoadDrives()
        {
            DriveComboBox.Items.Clear();
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                DriveComboBox.Items.Add($"{drive.Name} ({drive.DriveType}) - {FileItemNode.FormatBytes(drive.AvailableFreeSpace)} 空き");
            }
            if (DriveComboBox.Items.Count > 0)
            {
                DriveComboBox.SelectedIndex = 0;
            }
        }

        private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DriveComboBox.SelectedItem is string sel)
            {
                var driveRoot = sel.Split(' ')[0];
                PathTextBox.Text = driveRoot;
                if (_currentTab != null)
                {
                    _currentTab.TargetPath = driveRoot;
                    _currentTab.TabTitle = driveRoot;
                }
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

            var progress = new Progress<ScanProgress>(p =>
            {
                ScannedSizeTextBlock.Text = FileItemNode.FormatBytes(p.BytesScanned);
                TotalFilesTextBlock.Text = $"{p.FilesScanned:N0} 項目走査済み";
                StatusTextBlock.Text = $"スキャン中: {p.CurrentPath}";
            });

            try
            {
                var (root, summary) = await _scanService.ScanPathAsync(path, progress, ct);
                _currentTab.RootNode = root;
                _currentTab.Summary = summary;
                _currentTab.FlattenTree();
                _currentTab.AggregateExtensions();

                FileTreeDataGrid.ItemsSource = _currentTab.VisibleFlatList;
                UpdateDynamicInsightsForNode(root);

                UpdateMetricsCards(_currentTab);
                StatusTextBlock.Text = summary.IsMftBoosted
                    ? $"⚡ MFT高速スキャン完了 ({summary.ElapsedSeconds}秒): {root.Name} ({FileItemNode.FormatBytes(root.SizeBytes)})"
                    : $"スキャン完了 ({summary.ElapsedSeconds}秒): {root.Name} ({FileItemNode.FormatBytes(root.SizeBytes)})";
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
                FileTreeDataGrid.ItemsSource = _currentTab.VisibleFlatList;
            }
        }

        private void UpdateMetricsCards(ScanTabModel tab)
        {
            MftBoostBadge.Visibility = (tab.Summary?.IsMftBoosted == true) ? Visibility.Visible : Visibility.Collapsed;

            if (tab.RootNode != null)
            {
                ScannedSizeTextBlock.Text = FileItemNode.FormatBytes(tab.RootNode.SizeBytes);
                TotalFilesTextBlock.Text = $"{tab.RootNode.FileCount:N0} ファイル";
            }

            try
            {
                var root = Path.GetPathRoot(tab.TargetPath);
                if (!string.IsNullOrEmpty(root) && Directory.Exists(root))
                {
                    var drive = new DriveInfo(root);
                    if (drive.IsReady)
                    {
                        var total = drive.TotalSize;
                        var free = drive.AvailableFreeSpace;
                        var percent = (double)(total - free) / total * 100;

                        FreeSpaceTextBlock.Text = $"{FileItemNode.FormatBytes(free)} 空き";
                        FreePercentTextBlock.Text = $"{percent:F1}% 使用中";
                        DriveUsageProgressBar.Value = percent;
                    }
                }
            }
            catch { }
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

            // 1. Top 10 largest files in this subtree
            var (topFiles, _) = DiskScanService.GetInsightsForNode(node);
            TopFilesDataGrid.ItemsSource = topFiles;

            // 2. Direct children breakdown (relative shares in this folder)
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
                LiveAclPathTextBox.Text = item.FullPath;
                NavTabLiveAcl.IsChecked = true;
                LoadLiveAclForPath(item.FullPath);
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

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentTab?.RootNode == null)
            {
                MessageBox.Show("エクスポートするスキャンデータがありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "スキャン結果を CSV で保存",
                Filter = "CSVファイル (*.csv)|*.csv",
                FileName = $"ScanResult_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };
            if (dialog.ShowDialog() == true)
            {
                var sb = new StringBuilder();
                sb.Append('\uFEFF');
                sb.AppendLine("名前,パス,容量,割合,ファイル数,最終更新");
                foreach (var item in _currentTab.VisibleFlatList)
                {
                    sb.AppendLine($"\"{item.Name}\",\"{item.FullPath}\",\"{item.FormattedSize}\",\"{item.FormattedPercentage}\",\"{item.FileCount}\",\"{item.LastModified:yyyy/MM/dd HH:mm}\"");
                }
                File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                ShowToast("CSVレポートを出力しました");
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

        #region Tab 2: Live ACL Control (実環境 権限マネージャー)
        private void InitializeLiveAcl()
        {
            LiveAclCardsItemsControl.ItemsSource = _liveAclEntries;
            LiveAclPrincipalsListBox.ItemsSource = _liveAclPrincipals;

            _adPrincipals.CollectionChanged += (s, e) =>
            {
                _liveAclPrincipals.Clear();
                foreach (var p in _adPrincipals) _liveAclPrincipals.Add(p);
                UpdateLiveAclNoticeState();
            };

            UpdateLiveAclNoticeState();
        }

        private void UpdateLiveAclNoticeState()
        {
            if (LiveAclEmptyNoticeBorder == null || LiveAclPrincipalsListBox == null) return;
            bool showNotice = _liveAclPrincipals.Count == 0;
            LiveAclEmptyNoticeBorder.Visibility = showNotice ? Visibility.Visible : Visibility.Collapsed;
            LiveAclPrincipalsListBox.Visibility = showNotice ? Visibility.Collapsed : Visibility.Visible;
        }

        private void LiveAclBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "アクセス権を管理するフォルダを選択（UNC対応）",
                InitialDirectory = string.IsNullOrWhiteSpace(LiveAclPathTextBox.Text) ? @"C:\" : LiveAclPathTextBox.Text
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                LiveAclPathTextBox.Text = dialog.FolderName;
                LoadLiveAclForPath(dialog.FolderName);
            }
        }

        private void LiveAclReloadButton_Click(object sender, RoutedEventArgs e)
        {
            var path = LiveAclPathTextBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(path))
            {
                LoadLiveAclForPath(path);
            }
        }

        private void LiveAclPathTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var path = LiveAclPathTextBox.Text.Trim();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    LoadLiveAclForPath(path);
                }
            }
        }

        private void LoadLiveAclForPath(string path)
        {
            if (!Directory.Exists(path))
            {
                MessageBox.Show($"指定フォルダが存在しません:\n{path}", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var (entries, isInherited, owner) = _aclService.GetSimAclForFolder(path);
                _liveAclEntries.Clear();
                foreach (var entry in entries)
                {
                    _liveAclEntries.Add(entry);
                }

                var folderName = Path.GetFileName(path.TrimEnd('\\', '/'));
                if (string.IsNullOrEmpty(folderName)) folderName = path;
                LiveAclFolderNameText.Text = folderName;
                LiveAclOwnerText.Text = $"所有者: {owner}";
                LiveAclInheritCheckBox.IsChecked = isInherited;

                ShowToast($"実環境の権限を読み込みました: {folderName} ({entries.Count} 件)");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"権限読み込みエラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LiveAclInheritCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            // 継承変更
        }

        private async void LiveAclApplyButton_Click(object sender, RoutedEventArgs e)
        {
            var path = LiveAclPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                MessageBox.Show("有効なフォルダパスを指定してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var confirm = MessageBox.Show(
                $"【注意: 実環境のアクセス権変更】\n\n対象: {path}\n付与ルール数: {_liveAclEntries.Count} 件\n継承設定: {(LiveAclInheritCheckBox.IsChecked == true ? "親から継承" : "固有設定 (継承無効)")}\n\n※実行直前にSDDLバックアップが自動保存され、いつでも復元できます。\n\n実ファイルサーバーへ直ちに適用しますか？",
                "実環境アクセス権の即時適用確認",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirm != MessageBoxResult.Yes) return;

            try
            {
                await _aclService.CreateSnapshotAsync(path, "LiveACL 即時適用前の自動バックアップ");
                _aclService.ApplySimAclEntries(path, _liveAclEntries, LiveAclInheritCheckBox.IsChecked == true);

                ShowToast($"⚡ 実環境へNTFSアクセス権を即時適用しました: {Path.GetFileName(path)}");
                MessageBox.Show("実サーバーへのアクセス権適用が完了しました！\n（必要に応じて直前のバックアップから復元可能です）", "適用完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"権限適用エラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void LiveAclRollbackButton_Click(object sender, RoutedEventArgs e)
        {
            var path = LiveAclPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path)) return;

            try
            {
                var snapshots = await _aclService.GetSnapshotsAsync(path);
                if (snapshots.Count == 0)
                {
                    MessageBox.Show("このフォルダの保存済みバックアップはありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var latest = snapshots[0];
                var confirm = MessageBox.Show(
                    $"最新のバックアップ（{latest.Timestamp:yyyy/MM/dd HH:mm:ss} 保存）へ復元しますか？\n\n対象: {path}",
                    "バックアップ復元確認",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm == MessageBoxResult.Yes)
                {
                    _aclService.RollbackToSnapshot(path, latest);
                    LoadLiveAclForPath(path);
                    ShowToast("↩️ 直前のバックアップから権限を復元しました");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"復元エラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LiveAclExportMatrixButton_Click(object sender, RoutedEventArgs e)
        {
            var path = LiveAclPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                MessageBox.Show("有効なフォルダパスを指定してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "実環境アクセス権マトリクス台帳 (CSV) を保存",
                Filter = "CSVファイル (*.csv)|*.csv",
                FileName = $"LiveAcl_Matrix_{Path.GetFileName(path.TrimEnd('\\', '/'))}_{DateTime.Now:yyyyMMdd}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var rootNode = _aclService.GetFolderAcl(path, maxDepth: 2);
                    var csv = _aclService.GenerateMatrixCsv(rootNode);
                    File.WriteAllText(dialog.FileName, csv, Encoding.UTF8);
                    ShowToast("権限台帳CSVを出力しました");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"CSV出力エラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void LiveAclDropZone_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(AdPrincipalItem)) is AdPrincipalItem p)
            {
                if (_liveAclEntries.Any(a => a.AccountName.Equals(p.AccountName, StringComparison.OrdinalIgnoreCase)))
                {
                    ShowToast($"⚠️ すでに割り当て済みです: {p.DisplayName}");
                    return;
                }

                var entry = new SimAclEntry
                {
                    AccountName = p.AccountName,
                    DisplayName = p.DisplayName,
                    PrincipalType = p.PrincipalType,
                    Rights = FileSystemRights.ReadAndExecute
                };
                _liveAclEntries.Add(entry);
                ShowToast($"🛡️ アクセス権カードを追加: {p.DisplayName}");
            }
        }

        private void LiveAclTrashZone_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(SimAclEntry)) is SimAclEntry acl)
            {
                _liveAclEntries.Remove(acl);
                ShowToast("🗑️ アクセス権カードをポイ捨て削除しました");
            }
        }

        private void LiveAclCard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && sender is FrameworkElement fe && fe.DataContext is SimAclEntry acl)
            {
                _isLiveAclEditing = true;
                _currentEditingAcl = acl;
                SecModalTargetNameText.Text = LiveAclFolderNameText.Text;
                SecModalFullPathText.Text = LiveAclPathTextBox.Text;
                SecPrincipalInput.Text = acl.DisplayName;
                SyncCheckboxesFromAcl(acl);
                SecModalOverlay.Visibility = Visibility.Visible;
            }
        }

        private void LiveAclOpenSecModalButton_Click(object sender, RoutedEventArgs e)
        {
            var acl = _liveAclEntries.FirstOrDefault();
            if (acl == null)
            {
                if (string.IsNullOrWhiteSpace(LiveAclPathTextBox.Text)) return;
                acl = new SimAclEntry
                {
                    AccountName = "Authenticated Users",
                    DisplayName = "Authenticated Users",
                    PrincipalType = AdPrincipalType.Group,
                    Rights = FileSystemRights.ReadAndExecute
                };
                _liveAclEntries.Add(acl);
            }

            _isLiveAclEditing = true;
            _currentEditingAcl = acl;
            SecModalTargetNameText.Text = LiveAclFolderNameText.Text;
            SecModalFullPathText.Text = LiveAclPathTextBox.Text;
            SecPrincipalInput.Text = acl.DisplayName;
            SyncCheckboxesFromAcl(acl);
            SecModalOverlay.Visibility = Visibility.Visible;
        }

        private void LiveAclDeleteEntryButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is SimAclEntry acl)
            {
                _liveAclEntries.Remove(acl);
                ShowToast($"アクセス権カードを削除しました: {acl.DisplayName}");
            }
        }

        private void LiveAclPrincipalSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var q = LiveAclPrincipalSearchTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(q))
            {
                LiveAclPrincipalsListBox.ItemsSource = _liveAclPrincipals;
            }
            else
            {
                LiveAclPrincipalsListBox.ItemsSource = _liveAclPrincipals
                    .Where(p => p.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                p.AccountName.Contains(q, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        private void LiveAclPrincipalsListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && LiveAclPrincipalsListBox.SelectedItem is AdPrincipalItem item)
            {
                DragDrop.DoDragDrop(LiveAclPrincipalsListBox, item, DragDropEffects.Copy);
            }
        }
        #endregion

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
            UpdateLiveAclNoticeState();
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

        private void SimSourceLoadButton_Click(object sender, RoutedEventArgs e)
        {
            var path = SimSourcePathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                if (_currentTab?.RootNode != null)
                {
                    SimSourceTreeView.ItemsSource = new List<FileItemNode> { _currentTab.RootNode };
                    ShowToast("容量分析スキャン結果を移行元ツリーに読み込みました");
                    return;
                }
                MessageBox.Show("有効な移行元フォルダパスを入力してください。", "通知", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var di = new DirectoryInfo(path);
                var rootItem = new FileItemNode(di.FullName, di.Name, 0, true, di.LastWriteTime);
                foreach (var sub in di.GetDirectories())
                {
                    rootItem.Children.Add(new FileItemNode(sub.FullName, sub.Name, 0, true, sub.LastWriteTime) { Parent = rootItem });
                }
                SimSourceTreeView.ItemsSource = new List<FileItemNode> { rootItem };
                ShowToast($"移行元ツリーをロードしました: {path}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"移行元読込エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
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

        private void SimCloneSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (SimSourceTreeView.SelectedItem is FileItemNode selected)
            {
                var simNode = _simService.ConvertToSimNode(selected);
                _simRootFolders.Add(simNode);
                ShowToast($"新環境モックツリーに配置しました: {selected.Name}");
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
                Level = 0
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

        private void SimMockTreeView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && SimMockTreeView.SelectedItem is SimFolderNode node)
            {
                var data = new DataObject("FolderMorpherSimNode", node);
                DragDrop.DoDragDrop(SimMockTreeView, data, DragDropEffects.Move);
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
            var target = GetSimNodeFromDragEvent(e) ?? _selectedSimNode ?? _simRootFolders.FirstOrDefault();
            if (target == null) return;

            // Case 1: Drop source folder from left explorer (Create subfolder)
            if (e.Data.GetData("FolderMorpherSourceNode") is FileItemNode src)
            {
                var newSub = new SimFolderNode
                {
                    Name = src.Name,
                    EstimatedSizeBytes = src.SizeBytes,
                    InheritAcl = true,
                    Level = target.Level + 1,
                    Parent = target
                };
                newSub.MappedSourcePaths.Add(src.FullPath);
                target.Children.Add(newSub);
                target.IsExpanded = true;
                ShowToast($"📁 「{src.Name}」を「{target.Name}」配下にサブフォルダ化しました");
            }
            // Case 2: Drop existing sim node inside mock tree (Move & demote)
            else if (e.Data.GetData("FolderMorpherSimNode") is SimFolderNode movingNode)
            {
                if (movingNode == target || IsDescendant(target, movingNode))
                {
                    ShowToast("自身またはその配下へは移動できません");
                    return;
                }

                // Detach from current parent
                if (movingNode.Parent != null) movingNode.Parent.Children.Remove(movingNode);
                else _simRootFolders.Remove(movingNode);

                // Attach to target
                movingNode.Parent = target;
                movingNode.Level = target.Level + 1;
                target.Children.Add(movingNode);
                target.IsExpanded = true;
                ShowToast($"📁 「{movingNode.Name}」を「{target.Name}」の配下に移動しました");
            }
            // Case 3: Drop AD Card (Assign permissions)
            else if (e.Data.GetData(typeof(AdPrincipalItem)) is AdPrincipalItem principal)
            {
                AssignAdPrincipal(target, principal);
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
                var newSub = new SimFolderNode
                {
                    Name = src.Name,
                    EstimatedSizeBytes = src.SizeBytes,
                    InheritAcl = true,
                    Level = _selectedSimNode.Level + 1,
                    Parent = _selectedSimNode
                };
                newSub.MappedSourcePaths.Add(src.FullPath);
                _selectedSimNode.Children.Add(newSub);
                _selectedSimNode.IsExpanded = true;
                ShowToast($"📥 「{src.Name}」をサブフォルダ化しました");
            }
            else if (e.Data.GetData("FolderMorpherSimNode") is SimFolderNode movingNode)
            {
                SimMockTreeView_Drop(sender, e);
            }
        }

        private void SimMappingDropZone_Drop(object sender, DragEventArgs e)
        {
            if (_selectedSimNode == null) return;

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
            if (e.Data.GetData(typeof(AdPrincipalItem)) is AdPrincipalItem principal)
            {
                AssignAdPrincipal(_selectedSimNode, principal);
            }
        }

        private void AssignAdPrincipal(SimFolderNode node, AdPrincipalItem principal)
        {
            if (node.AclEntries.Any(a => a.AccountName.Equals(principal.AccountName, StringComparison.OrdinalIgnoreCase)))
            {
                ShowToast($"「{principal.DisplayName}」は既に割り当てられています");
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
            ShowToast($"🛡️ 「{principal.DisplayName}」に 変更 (Modify) 権限を付与しました");
        }

        private void SimTrashZone_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData(typeof(SimAclEntry)) is SimAclEntry acl && _selectedSimNode != null)
            {
                _selectedSimNode.AclEntries.Remove(acl);
                _selectedSimNode.NotifyAclChanged();
                ShowToast("🗑️ アクセス権カードをポイ捨て解除しました");
            }
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
                    Parent = _selectedSimNode
                };
                _selectedSimNode.Children.Add(child);
                _selectedSimNode.IsExpanded = true;
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
            if (e.ClickCount == 2 && sender is FrameworkElement fe && fe.DataContext is SimAclEntry acl && _selectedSimNode != null)
            {
                OpenSecurityModal(acl);
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

        private void OpenSecurityModal(SimAclEntry acl)
        {
            _currentEditingAcl = acl;
            SecModalTargetNameText.Text = _selectedSimNode?.Name ?? "フォルダ";
            SecModalFullPathText.Text = $"\\\\NewServer01\\Share\\{_selectedSimNode?.RelativePath}";
            SecPrincipalInput.Text = acl.DisplayName;

            SyncCheckboxesFromAcl(acl);
            SecModalOverlay.Visibility = Visibility.Visible;
        }

        private void SyncCheckboxesFromAcl(SimAclEntry acl)
        {
            SecAccessTypeCombo.SelectedIndex = (acl.AccessType == AccessControlType.Deny) ? 1 : 0;

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
                _currentEditingAcl.DisplayName = SecPrincipalInput.Text.Trim();
                _currentEditingAcl.AccessType = (SecAccessTypeCombo.SelectedIndex == 1) ? System.Security.AccessControl.AccessControlType.Deny : System.Security.AccessControl.AccessControlType.Allow;
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
                if (_isLiveAclEditing)
                {
                    LiveAclCardsItemsControl.Items.Refresh();
                    _isLiveAclEditing = false;
                }
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
            var diffs = _simService.GenerateDiffReview(_currentTab?.RootNode, _simRootFolders);
            DiffReviewDataGrid.ItemsSource = diffs;
            DiffModalOverlay.Visibility = Visibility.Visible;
        }

        private void DiffModalClose_Click(object sender, RoutedEventArgs e)
        {
            DiffModalOverlay.Visibility = Visibility.Collapsed;
        }

        private void DiffExportExcel_Click(object sender, RoutedEventArgs e)
        {
            var diffs = DiffReviewDataGrid.ItemsSource as List<SimDiffItem>;
            if (diffs == null || diffs.Count == 0) return;

            var dialog = new SaveFileDialog
            {
                Title = "移行変化点 差分対比レポートを保存",
                Filter = "CSVファイル (*.csv)|*.csv",
                FileName = $"FolderMorpher_DiffReport_{DateTime.Now:yyyyMMdd}.csv"
            };
            if (dialog.ShowDialog() == true)
            {
                var sb = new StringBuilder();
                sb.Append('\uFEFF');
                sb.AppendLine("変化の種別,現行サーバー (Before),Before詳細,新環境設計 (After),After詳細,権限差分詳細");
                foreach (var d in diffs)
                {
                    sb.AppendLine($"\"{d.DiffType}\",\"{d.SourcePath.Replace("\n", " | ")}\",\"{d.SourceDetail}\",\"{d.TargetPath}\",\"{d.TargetDetail}\",\"{d.FormattedAclChanges.Replace("\n", " | ")}\"");
                }
                File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);
                ShowToast("差分対比レポートを出力しました");
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

        private async void SimDeploySkeletonButton_Click(object sender, RoutedEventArgs e)
        {
            var targetRoot = SimTargetRootTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                MessageBox.Show("移行先ルートフォルダを入力してください。", "通知", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show($"以下の場所に仮想モックのガワ（ディレクトリ階層と権限）を作成します:\n\n{targetRoot}\n\n続行しますか？",
                "ガワ先行作成の確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
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
                var (count, logs) = await _simService.DeploySkeletonAsync(_simRootFolders, targetRoot, progress, cts.Token);
                ShowToast($"ガワ先行作成完了: {count} 個のフォルダを作成しました");
                MessageBox.Show($"ガワ先行作成が完了しました。\n作成フォルダ数: {count}\n\n対象: {targetRoot}", "完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ガワ作成失敗: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                GlobalProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void SimExportScriptsButton_Click(object sender, RoutedEventArgs e)
        {
            var targetRoot = SimTargetRootTextBox.Text.Trim();
            var roboScript = _simService.GenerateRobocopyScript(_simRootFolders, targetRoot);
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
                Filter = "CSVファイル (*.csv)|*.csv",
                FileName = $"FolderMorpher_Ledger_{DateTime.Now:yyyyMMdd}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                var csv = _simService.ExportDesignMatrixCsv(_simRootFolders);
                File.WriteAllText(dialog.FileName, csv, Encoding.UTF8);
                ShowToast("移行台帳を出力しました");
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

            var progress = new Progress<string>(msg => StatusTextBlock.Text = msg);

            try
            {
                var items = await _linkFixService.ScanShortcutsAsync(scope, oldPattern, newPattern, progress, _linkFixCts.Token);
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

        private async void LinkFixExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            var items = LinkItemsDataGrid.ItemsSource as List<LinkFixItem>;
            if (items == null || items.Count == 0)
            {
                MessageBox.Show("修復対象のショートカットがありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var targets = items.Where(i => i.NeedsFix).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show("修復が必要な項目はありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (MessageBox.Show($"{targets.Count} 件のショートカットを書き換えます（.bak バックアップ自動生成）。\n実行しますか？", "確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
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
                var successCount = await _linkFixService.ExecuteFixAsync(targets, progress, cts.Token);
                LinkItemsDataGrid.Items.Refresh();
                ShowToast($"ショートカット修復完了: {successCount} / {targets.Count} 件");
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

                AuditItemsDataGrid.ItemsSource = items;

                // Update KPI Cards
                AuditKpiTotalFiles.Text = $"{summary.TotalFilesScanned:N0} 件";
                AuditKpiDupWasted.Text = summary.DuplicateWastedSizeFormatted;
                AuditKpiDormantSize.Text = summary.DormantSizeFormatted;
                AuditKpiPathLimits.Text = $"{summary.PathTooLongCount + summary.InvalidCharCount} 件";

                AuditStatusText.Text = $"完了: 課題 {items.Count} 件検出";
                ShowToast($"監査完了: 課題 {items.Count} 件");
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

            var dialog = new SaveFileDialog
            {
                Title = "安全退避バッチの保存先",
                Filter = "バッチファイル (*.bat)|*.bat",
                FileName = "Archive-Dormant-Files.bat"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    string dest = Path.Combine(Path.GetDirectoryName(dialog.FileName) ?? @"C:\", "FolderMorpher_Archive");
                    _auditService.GenerateArchiveRobocopyScript(dialog.FileName, _lastAuditItems, AuditPathTextBox.Text.Trim(), dest);
                    ShowToast("安全退避バッチを生成しました");
                    Process.Start("explorer.exe", $"/select,\"{dialog.FileName}\"");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"バッチ生成エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
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

        private async void MediaOptimizeButton_Click(object sender, RoutedEventArgs e)
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

            if (MessageBox.Show($"{targets.Count} 枚の写真を視覚的ロスレス（長辺2560px/85%品質/日時保持）で上書き軽量化します。\n聖域保護されたフォルダやRAWデータは保護されます。\n\n実行しますか？",
                                "写真の最適化確認", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
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
                var summary = await _mediaService.OptimizeImagesAsync(targets, options, progress, cts.Token);
                _lastMediaSummary = summary;

                MediaItemsDataGrid.Items.Refresh();

                MediaKpiOptimizedCount.Text = $"{summary.OptimizedImagesCount:N0} 枚";
                MediaKpiSavedSize.Text = summary.TotalSavedSizeFormatted;

                MediaStatusText.Text = $"最適化完了: {summary.TotalSavedSizeFormatted} の空き容量を解放しました";
                ShowToast($"写真軽量化完了: {summary.TotalSavedSizeFormatted} 削減");
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

            // サイドバー タブ名
            NavTabStorage.Content = isJa ? "容量分析 & 監視" : "Storage Explorer";
            NavTabLiveAcl.Content = isJa ? "権限コントロール" : "Live ACL";
            NavTabSimulation.Content = isJa ? "移行スタジオ" : "Simulation Studio";
            NavTabLinkFix.Content = isJa ? "リンク一括修復" : "LinkFixer";
            NavTabAudit.Content = isJa ? "断捨離・健全化" : "Audit & Hygiene";
            NavTabMedia.Content = isJa ? "メディア最適化" : "Media Optimizer";

            // Tab 4 (LinkFixer)
            LinkGenerateGpoButton.Content = isJa ? "📜 GPOログオンスクリプト生成 (.ps1)" : "📜 Generate GPO Script (.ps1)";
            LinkScanButton.Content = isJa ? "切断リンク検出スキャン" : "Scan Broken Links";
            LinkFixExecuteButton.Content = isJa ? "⚡ 一括修復を実行 (バックアップ付)" : "⚡ Execute Fix (with Backup)";
            LinkIncludeOfficeCheckBox.Content = isJa ? "Officeファイル内部リンク (.xlsx/.xlsm) も対象に含める" : "Include Office internal links (.xlsx/.xlsm)";

            // Tab 5 (Audit & Hygiene)
            AuditStartButton.Content = isJa ? "🔍 監査スキャン開始" : "🔍 Start Audit Scan";
            AuditExportExcelButton.Content = isJa ? "📊 Excelレポート出力 (.xlsx)" : "📊 Export Excel (.xlsx)";
            AuditExportCsvButton.Content = isJa ? "📄 CSV台帳出力" : "📄 Export CSV";
            AuditGenArchiveScriptButton.Content = isJa ? "📦 安全退避バッチ生成 (.bat)" : "📦 Generate Archive Batch (.bat)";
            AuditCheckDuplicatesCheckBox.Content = isJa ? "重複ファイル (SHA256)" : "Duplicates (SHA256)";
            AuditCheckDormantCheckBox.Content = isJa ? "休眠ファイル (3年以上)" : "Dormant (3+ Years)";
            AuditCheckPathLimitsCheckBox.Content = isJa ? "パス長260字超/禁則文字" : "Path Limits / Invalid Chars";

            // Tab 6 (Media Optimizer)
            MediaScanButton.Content = isJa ? "🔍 メディア走査" : "🔍 Scan Media";
            MediaOptimizeButton.Content = isJa ? "⚡ 写真を軽量化 (直接上書き/日時維持)" : "⚡ Slim Photos (Lossless/In-Place)";
            MediaGenVideoBatchButton.Content = isJa ? "🎬 巨大動画 夜間圧縮バッチ出力 (.bat)" : "🎬 Export Nightly Video Batch (.bat)";
            MediaExportExcelButton.Content = isJa ? "📊 Excelレポート出力 (.xlsx)" : "📊 Export Excel (.xlsx)";

            // 現在のアクティブタブのステータス再反映
            NavTab_Checked(this, new RoutedEventArgs());
        }
        #endregion
    }
}

