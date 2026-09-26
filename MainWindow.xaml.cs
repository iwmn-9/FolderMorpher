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
using FolderMorpher.Models;
using FolderMorpher.Services;
using Microsoft.Win32;

namespace AstraSize
{
    public partial class MainWindow : Window
    {
        private readonly TaskCompletionSource<bool> _initialLocalization = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<bool> InitialLocalization => _initialLocalization.Task;

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
        private long _auditFilterRevision;
        private MediaOptimizeSummary? _lastMediaSummary;
        private List<MediaItem> _lastMediaImages = new();
        private List<MediaItem> _lastMediaVideos = new();

        // Multi-Tab Storage Management
        public ObservableCollection<ScanTabModel> StorageTabs { get; set; } = new();
        private ScanTabModel? _currentTab;

        // Simulation Studio State
        private readonly ObservableCollection<SimFolderNode> _simRootFolders = new();
        private readonly Stack<string> _simUndoStack = new();
        private const int MaxSimUndoDepth = 30;
        private readonly ObservableCollection<AdPrincipalItem> _adPrincipals = new();
        private List<AdPrincipalItem> _rawAdPrincipalsCache = new();
        private DispatcherTimer? _adSyncTimer;
        private SimFolderNode? _selectedSimNode;
        private SimAclEntry? _currentEditingAcl;
        private SkeletonDeployPlan? _currentSkeletonPlan;

        // Live ACL modal state
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

        private FolderMorpher.Contracts.ReportExportDto BuildReportExportRequest(
            string outputPath, string scannedRoot, IEnumerable<MediaItem> mediaItems) => new()
        {
            OutputPath = outputPath,
            ScannedRoot = scannedRoot,
            AuditSummary = _lastAuditSummary == null ? null : FolderMorpher.HostClient.AuditDtoMapper.ToDto(_lastAuditSummary),
            AuditItems = _lastAuditItems.Select(FolderMorpher.HostClient.AuditDtoMapper.ToDto).ToList(),
            MediaSummary = _lastMediaSummary == null ? null : FolderMorpher.HostClient.MediaDtoMapper.ToDto(_lastMediaSummary),
            MediaItems = mediaItems.Select(FolderMorpher.HostClient.MediaDtoMapper.ToDto).ToList()
        };

        // Drag & Drop State (枠外ドロップ解除 & 広域受容 & Escキャンセル保護)
        private bool _droppedInSelfContainer = false;
        private bool _dragCancelled = false;
        private Point _cardDragStartPoint;

        private Point _simDragStartPoint;
        private bool _isSimNodeDragging = false;
        private bool _simNodeDroppedInTree = false;
        private bool _simDragCancelled = false;
        private readonly DispatcherTimer _simHoverExpandTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
        private SimFolderNode? _simHoverExpandCandidate = null;
        private SimFolderNode? _currentDragOverSimNode = null;

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

            LiveAclStudioControl.InitializeServices();
            LiveAclStudioControl.ToastRequested += ShowToast;
            LiveAclStudioControl.EditSecurityRequested += (acl, folderName, fullPath, onApplied) =>
            {
                OpenSecurityModal(acl, folderName, fullPath, onApplied);
            };

            AuditItemsDataGrid.ItemsSource = _auditVisibleItems;
            AuditItem.GlobalCheckedChanged += (item) => UpdateLiveSelectedReduction();
            UpdateIgnoredCountBadge();
            InitializeSearchStudio();

            var asmVer = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
            if (asmVer != null && SidebarVersionText != null)
            {
                SidebarVersionText.Text = $"FolderMorpher v{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}";
            }

            PreviewKeyDown += MainWindow_PreviewKeyDown;
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (SimulationTabPanel != null && SimulationTabPanel.Visibility == Visibility.Visible)
                {
                    PerformUndo();
                    e.Handled = true;
                }
            }
        }

        private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                await AppSettingsService.Instance.LoadAsync();
                var language = SnapshotRunner.ForcedLanguage ??
                    (AppSettingsService.Instance.Current.Language == "en" ? AppLanguage.English : AppLanguage.Japanese);
                LocalizationService.Instance.SetLanguage(language);
                LocalizationService.Instance.LanguageChanged += ApplyLocalization;
                ApplyLocalization();
                _initialLocalization.TrySetResult(true);
                var settingsHost = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                await settingsHost.SetLanguageAsync(language == AppLanguage.English ? "en" : "ja");
                InitializeStorageTabs();
                if (!ClientModeState.IsClientMode)
                {
                    InitializeSimulationStudio();
                    await LoadAdPrincipalsAsync();
                }

            }
            catch (Exception ex)
            {
                _initialLocalization.TrySetResult(false);
                Debug.WriteLine($"MainWindow_Loaded Error: {ex}");
            }
        }

        private bool _settingsFlushedOnClose;

        protected override async void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_settingsFlushedOnClose)
            {
                base.OnClosing(e);
                return;
            }
            e.Cancel = true;
            SaveStorageTabSession();
            try { await AppSettingsService.Instance.FlushAsync(); }
            catch (Exception ex) { Debug.WriteLine($"Settings flush failed: {ex}"); }
            _settingsFlushedOnClose = true;
            Close();
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
                AuditTabPanel == null || MediaTabPanel == null || SearchTabPanel == null)
                return;

            StorageTabPanel.Visibility = Visibility.Collapsed;
            LiveAclStudioControl.Visibility = Visibility.Collapsed;
            SimulationTabPanel.Visibility = Visibility.Collapsed;
            LinkFixTabPanel.Visibility = Visibility.Collapsed;
            AuditTabPanel.Visibility = Visibility.Collapsed;
            MediaTabPanel.Visibility = Visibility.Collapsed;
            SearchTabPanel.Visibility = Visibility.Collapsed;

            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;

            if (NavTabStorage.IsChecked == true)
            {
                StorageTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = isJa ? "モード: 容量分析" : "Mode: Storage Explorer";
            }
            else if (NavTabSearch.IsChecked == true)
            {
                SearchTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = isJa ? "モード: ファイル検索" : "Mode: File Search";
                if (string.IsNullOrWhiteSpace(SearchDirectTargetTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    SearchDirectTargetTextBox.Text = PathTextBox.Text;
                }
            }
            else if (NavTabLiveAcl.IsChecked == true)
            {
                LiveAclStudioControl.Visibility = Visibility.Visible;
                StatusTextBlock.Text = isJa ? "モード: 権限コントロール" : "Mode: Live ACL Control";
                if (string.IsNullOrWhiteSpace(LiveAclStudioControl.CurrentPath) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    LiveAclStudioControl.SetDefaultPath(PathTextBox.Text);
                }
            }
            else if (NavTabSimulation.IsChecked == true)
            {
                SimulationTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = isJa ? "モード: 移行スタジオ" : "Mode: Simulation Studio";
                if (string.IsNullOrWhiteSpace(SimSourcePathTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    SimSourcePathTextBox.Text = PathTextBox.Text;
                }
            }
            else if (NavTabLinkFix.IsChecked == true)
            {
                LinkFixTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = isJa ? "モード: リンク修復" : "Mode: LinkFixer";
                if (string.IsNullOrWhiteSpace(LinkSearchScopeTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    LinkSearchScopeTextBox.Text = PathTextBox.Text;
                }
            }
            else if (NavTabAudit.IsChecked == true)
            {
                AuditTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = isJa ? "モード: ファイル監査" : "Mode: Audit & Hygiene";
                if (string.IsNullOrWhiteSpace(AuditPathTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    AuditPathTextBox.Text = PathTextBox.Text;
                }
            }
            else if (NavTabMedia.IsChecked == true)
            {
                MediaTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = isJa ? "モード: メディア最適化" : "Mode: Media Optimizer";
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
            var normalStyle = (Style)FindResource("SidebarNavButton");
            var collapsedStyle = (Style)FindResource("SidebarNavButtonCollapsed");

            if (_isSidebarCollapsed)
            {
                SidebarBorder.Width = 60;
                SidebarHeaderBorder.Padding = new Thickness(0);
                SidebarToggleCol.Width = new GridLength(60);
                SidebarBrandPanel.Visibility = Visibility.Collapsed;
                SidebarNavPanel.Margin = new Thickness(8, 14, 8, 14);

                NavTabStorage.Style = collapsedStyle;
                NavTabSearch.Style = collapsedStyle;
                NavTabLiveAcl.Style = collapsedStyle;
                NavTabSimulation.Style = collapsedStyle;
                NavTabLinkFix.Style = collapsedStyle;
                NavTabAudit.Style = collapsedStyle;
                NavTabMedia.Style = collapsedStyle;

                SidebarFooterExpandedPanel.Visibility = Visibility.Collapsed;
                SidebarFooterCollapsedPanel.Visibility = Visibility.Visible;
                SidebarToggleButton.ToolTip = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese ? "サイドバーを展開" : "Expand Sidebar";
            }
            else
            {
                SidebarBorder.Width = 220;
                SidebarHeaderBorder.Padding = new Thickness(12, 0, 14, 0);
                SidebarToggleCol.Width = new GridLength(36);
                SidebarBrandPanel.Visibility = Visibility.Visible;
                SidebarNavPanel.Margin = new Thickness(10, 14, 10, 14);

                NavTabStorage.Style = normalStyle;
                NavTabSearch.Style = normalStyle;
                NavTabLiveAcl.Style = normalStyle;
                NavTabSimulation.Style = normalStyle;
                NavTabLinkFix.Style = normalStyle;
                NavTabAudit.Style = normalStyle;
                NavTabMedia.Style = normalStyle;

                SidebarFooterExpandedPanel.Visibility = Visibility.Visible;
                SidebarFooterCollapsedPanel.Visibility = Visibility.Collapsed;
                SidebarToggleButton.ToolTip = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese ? "サイドバーを収縮" : "Collapse Sidebar";
            }
        }
        #endregion


        #region Settings Modal (環境設定: キャッシュ共有・保存先)

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settings = AppSettingsService.Instance.Current;
            SettingsReadPathTextBox.Text = settings.CacheReadPath;
            SettingsFallbackCheckBox.IsChecked = settings.FallbackToLocalOnReadError;

            switch ((CacheWriteMode)settings.WriteMode)
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
                settings.WriteMode = (int)CacheWriteMode.SameAsRead;
            }
            else if (SettingsWriteModeCustomRadio.IsChecked == true)
            {
                settings.WriteMode = (int)CacheWriteMode.Custom;
                settings.CacheWriteCustomPath = SettingsCustomPathTextBox.Text.Trim();
            }
            else
            {
                settings.WriteMode = (int)CacheWriteMode.Local;
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
