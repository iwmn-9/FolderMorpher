using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AstraSize.Models;
using FolderMorpher.Models;
using FolderMorpher.Services;
using Microsoft.Win32;

namespace AstraSize.Views
{
    /// <summary>
    /// LiveAclStudio.xaml の相互作用ロジック
    /// フォルダ別権限エディタ (マルチパネル・最小差分適用) および
    /// ユーザー/グループ逆引き監査 (Effective Access & OUピッカー)
    /// </summary>
    public partial class LiveAclStudio : UserControl
    {
        // 外部連携イベント
        public event Action<string>? ToastRequested;
        public event Action<SimAclEntry, string, string, Action>? EditSecurityRequested;

        // フォルダ別権限エディタ 状態管理
        private readonly ObservableCollection<LiveAclPanelModel> _liveAclPanels = new();
        private readonly ObservableCollection<FileItemNode> _liveAclFolderTreeRoots = new();
        private readonly ObservableCollection<AdPrincipalItem> _liveAclPrincipals = new();
        private Point _liveAclFolderDragStart;
        private Point _cardDragStartPoint;
        private bool _droppedInSelfContainer = false;
        private bool _dragCancelled = false;

        // AD自動同期 ＆ 新規フォルダー作成 状態管理
        public event Action? RefreshAdRequested;
        private System.Windows.Threading.DispatcherTimer? _adSyncTimer;
        private List<AdPrincipalItem> _rawPrincipalsCache = new();
        private string _targetParentFolderForNewFolder = string.Empty;

        // 逆引き監査 (Effective Access) 状態管理
        private EffectiveAccessAuditReport? _currentEffectiveReport;
        private readonly ObservableCollection<PrincipalGroupMembership> _revGroups = new();
        private readonly ObservableCollection<EffectiveFolderAccessItem> _revFolders = new();
        private readonly List<EffectiveFolderAccessItem> _revAllFoldersCache = new();
        private CancellationTokenSource? _revCts;

        // OUピッカー 状態管理
        private readonly ObservableCollection<AdOuNode> _pickerOuRoots = new();
        private readonly ObservableCollection<AdPrincipalItem> _pickerPrincipals = new();
        private AdOuNode? _pickerSelectedOu;
        private AdPrincipalItem? _pickerSelectedPrincipal;

        // Dry-Run 差分プレビュー状態
        private LiveAclPanelModel? _pendingDiffPanel;
        private FolderMorpher.Contracts.AclChangePreviewDto? _currentChangePlan;
        private readonly ObservableCollection<LiveAclDiffItem> _diffItems = new();

        public string CurrentPath => LiveAclPathTextBox.Text;

        public LiveAclStudio()
        {
            InitializeComponent();
            DataContext = this;

            LiveAclMultiPanelsItemsControl.ItemsSource = _liveAclPanels;
            LiveAclFolderTreeView.ItemsSource = _liveAclFolderTreeRoots;
            LiveAclPrincipalsListBox.ItemsSource = _liveAclPrincipals;

            RevGroupsListBox.ItemsSource = _revGroups;
            RevFoldersDataGrid.ItemsSource = _revFolders;

            PickerOuTreeView.ItemsSource = _pickerOuRoots;
            PickerPrincipalsDataGrid.ItemsSource = _pickerPrincipals;

            LiveAclDiffDataGrid.ItemsSource = _diffItems;

            _liveAclPanels.CollectionChanged += (s, e) => UpdateLiveAclPanelsBanner();

            Loaded += (s, e) => _adSyncTimer?.Start();
            Unloaded += (s, e) => _adSyncTimer?.Stop();

            UpdateLiveAclNoticeState();
            UpdateLiveAclPanelsBanner();
        }

        public void InitializeServices()
        {
            UpdateDomainBadge();
            StartAdSyncTimer();
            _ = CheckAdChangesAsync(forceRefresh: false);
        }

        public async void UpdateDomainBadge()
        {
            if (LiveAclDomainStatusText == null || LiveAclDomainStatusBadge == null) return;
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            FolderMorpher.Contracts.DirectoryStatusDto status;
            try
            {
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                status = await host.GetDirectoryStatusAsync();
            }
            catch { status = new FolderMorpher.Contracts.DirectoryStatusDto(); }
            if (status.IsDomainJoined)
            {
                LiveAclDomainStatusText.Text = $"🟢 {status.CurrentDomainName.ToUpperInvariant()}";
                LiveAclDomainStatusBadge.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#DCFCE7")!;
                LiveAclDomainStatusText.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#15803D")!;
            }
            else
            {
                LiveAclDomainStatusText.Text = Strings.AdDomainLocal;
                LiveAclDomainStatusBadge.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#FEF3C7")!;
                LiveAclDomainStatusText.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#B45309")!;
            }
        }

        public void SetPrincipals(IEnumerable<AdPrincipalItem> principals)
        {
            _rawPrincipalsCache = principals.ToList();
            _liveAclPrincipals.Clear();
            foreach (var p in principals)
            {
                _liveAclPrincipals.Add(p);
            }
            UpdateLiveAclNoticeState();
        }

        public void SetDefaultPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            LiveAclPathTextBox.Text = path;
            LoadLiveAclFolderTree(path);
            if (_liveAclPanels.Count == 0)
            {
                AddLiveAclPanel(path);
            }
        }

        public void OpenFolder(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            LiveAclPathTextBox.Text = path;
            LoadLiveAclFolderTree(path);
            AddLiveAclPanel(path);
        }

        private void ShowToast(string message)
        {
            ToastRequested?.Invoke(message);
        }

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

        private void LiveAclGenericDragOver(object sender, DragEventArgs e)
        {
            e.Effects = DragDropEffects.Copy | DragDropEffects.Move;
            e.Handled = true;
        }

        private void OnCardQueryContinueDrag(object sender, QueryContinueDragEventArgs e)
        {
            if (e.EscapePressed || e.Action == DragAction.Cancel)
            {
                _dragCancelled = true;
            }
        }

        #region Mode Switcher
        private void LiveAclMode_Checked(object sender, RoutedEventArgs e)
        {
            if (LiveAclFolderView == null || LiveAclReverseView == null) return;

            if (LiveAclModeFolderRadio.IsChecked == true)
            {
                LiveAclFolderView.Visibility = Visibility.Visible;
                LiveAclReverseView.Visibility = Visibility.Collapsed;
            }
            else
            {
                LiveAclFolderView.Visibility = Visibility.Collapsed;
                LiveAclReverseView.Visibility = Visibility.Visible;

                if (string.IsNullOrWhiteSpace(RevRootPathTextBox.Text) && !string.IsNullOrWhiteSpace(LiveAclPathTextBox.Text))
                {
                    RevRootPathTextBox.Text = LiveAclPathTextBox.Text;
                }
            }
        }

        private void UpdateLiveAclPanelsBanner()
        {
            if (LiveAclNoPanelsBanner != null)
            {
                LiveAclNoPanelsBanner.Visibility = _liveAclPanels.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private void UpdateLiveAclNoticeState()
        {
            if (LiveAclEmptyNoticeBorder == null || LiveAclPrincipalsListBox == null) return;
            bool showNotice = _liveAclPrincipals.Count == 0;
            LiveAclEmptyNoticeBorder.Visibility = showNotice ? Visibility.Visible : Visibility.Collapsed;
            LiveAclPrincipalsListBox.Visibility = showNotice ? Visibility.Collapsed : Visibility.Visible;
        }
        #endregion

        #region Folder Browser & Multi-Panel
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
                LoadLiveAclFolderTree(dialog.FolderName);
                AddLiveAclPanel(dialog.FolderName);
            }
        }

        private void LiveAclReloadButton_Click(object sender, RoutedEventArgs e)
        {
            var path = LiveAclPathTextBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(path))
            {
                LoadLiveAclFolderTree(path);
                if (_liveAclPanels.Count == 0)
                {
                    AddLiveAclPanel(path);
                }
            }
        }

        private void LiveAclPathTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var path = LiveAclPathTextBox.Text.Trim();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    LoadLiveAclFolderTree(path);
                    if (_liveAclPanels.Count == 0)
                    {
                        AddLiveAclPanel(path);
                    }
                }
            }
        }

        private async void LoadLiveAclFolderTree(string rootPath)
        {
            try
            {
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                var dto = await host.LoadAclFolderTreeAsync(rootPath, 2, CancellationToken.None);
                _liveAclFolderTreeRoots.Clear();
                var rootNode = FolderMorpher.HostClient.StorageNodeMapper.ToViewNode(dto);
                rootNode.IsExpanded = true;
                _liveAclFolderTreeRoots.Add(rootNode);
                ShowToast($"📁 フォルダツリーを展開しました: {rootNode.Name}");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ツリー読み込みエラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LiveAclFolderTreeView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LiveAclFolderTreeView.SelectedItem is FileItemNode node && node.IsDirectory)
            {
                AddLiveAclPanel(node.FullPath);
            }
        }

        private void LiveAclFolderTreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _liveAclFolderDragStart = e.GetPosition(null);
        }

        private void LiveAclFolderTreeView_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep)
            {
                var tvi = FindVisualParent<TreeViewItem>(dep);
                if (tvi != null)
                {
                    tvi.Focus();
                    tvi.IsSelected = true;
                }
            }
        }

        private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
        {
            var parentObject = VisualTreeHelper.GetParent(child);
            if (parentObject == null) return null;
            if (parentObject is T parent) return parent;
            return FindVisualParent<T>(parentObject);
        }

        private void LiveAclCtxNewFolder_Click(object sender, RoutedEventArgs e)
        {
            PromptNewFolderCreation();
        }

        private void LiveAclNewFolderButton_Click(object sender, RoutedEventArgs e)
        {
            PromptNewFolderCreation();
        }

        private void LiveAclCtxOpenPanel_Click(object sender, RoutedEventArgs e)
        {
            if (LiveAclFolderTreeView.SelectedItem is FileItemNode node && node.IsDirectory)
            {
                AddLiveAclPanel(node.FullPath);
            }
        }

        private void LiveAclCtxOpenExplorer_Click(object sender, RoutedEventArgs e)
        {
            if (LiveAclFolderTreeView.SelectedItem is FileItemNode node)
            {
                ShellHelper.SelectInExplorer(node.FullPath);
            }
        }

        private void LiveAclCtxCopyPath_Click(object sender, RoutedEventArgs e)
        {
            if (LiveAclFolderTreeView.SelectedItem is FileItemNode node)
            {
                try
                {
                    Clipboard.SetText(node.FullPath);
                    ShowToast($"📋 パスをコピーしました: {node.FullPath}");
                }
                catch { }
            }
        }

        private void PromptNewFolderCreation()
        {
            string parentPath = string.Empty;
            if (LiveAclFolderTreeView.SelectedItem is FileItemNode selectedNode && selectedNode.IsDirectory)
            {
                parentPath = selectedNode.FullPath;
            }
            else if (!string.IsNullOrWhiteSpace(LiveAclPathTextBox.Text))
            {
                parentPath = LiveAclPathTextBox.Text;
            }

            if (string.IsNullOrWhiteSpace(parentPath))
            {
                ShowToast("⚠️ 新規フォルダーの作成先（親フォルダー）が存在しません。先にフォルダーを参照または展開してください。");
                return;
            }

            _targetParentFolderForNewFolder = parentPath;
            NewFolderModalParentPath.Text = $"作成先: {_targetParentFolderForNewFolder}";
            NewFolderNameTextBox.Text = "新しいフォルダー";
            NewFolderModalErrorText.Visibility = Visibility.Collapsed;
            NewFolderModalOverlay.Visibility = Visibility.Visible;
            NewFolderNameTextBox.Focus();
            NewFolderNameTextBox.SelectAll();
        }

        private void NewFolderModalCancel_Click(object sender, RoutedEventArgs e)
        {
            NewFolderModalOverlay.Visibility = Visibility.Collapsed;
        }

        private void NewFolderNameTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                NewFolderModalCreate_Click(sender, e);
            }
            else if (e.Key == Key.Escape)
            {
                NewFolderModalCancel_Click(sender, e);
            }
        }

        private async void NewFolderModalCreate_Click(object sender, RoutedEventArgs e)
        {
            string folderName = NewFolderNameTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(folderName))
            {
                NewFolderModalErrorText.Text = "フォルダー名を入力してください。";
                NewFolderModalErrorText.Visibility = Visibility.Visible;
                return;
            }

            char[] invalidChars = Path.GetInvalidFileNameChars();
            if (folderName.IndexOfAny(invalidChars) >= 0)
            {
                NewFolderModalErrorText.Text = "フォルダー名に使用できない文字 (\\ / : * ? \" < > | 等) が含まれています。";
                NewFolderModalErrorText.Visibility = Visibility.Visible;
                return;
            }

            try
            {
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                var plan = await host.PrepareFolderCreationAsync(_targetParentFolderForNewFolder, folderName, CancellationToken.None);
                if (MessageBox.Show($"次のフォルダーを作成しますか？\n{plan.FullPath}", "作成内容の確認",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;
                var newPath = await host.CommitFolderCreationAsync(plan.PlanId, CancellationToken.None);
                NewFolderModalOverlay.Visibility = Visibility.Collapsed;

                // 親ノードの子リストに新しいノードを追加
                InsertNewNodeIntoTree(_targetParentFolderForNewFolder, newPath, folderName);

                // 即座に権限編集パネルを開く
                AddLiveAclPanel(newPath);

                ShowToast($"📁 新規フォルダーを作成し権限パネルを開きました: {folderName}");
            }
            catch (Exception ex)
            {
                NewFolderModalErrorText.Text = $"作成に失敗しました: {ex.Message}";
                NewFolderModalErrorText.Visibility = Visibility.Visible;
            }
        }

        private void InsertNewNodeIntoTree(string parentPath, string newPath, string folderName)
        {
            FileItemNode? parentNode = null;
            if (LiveAclFolderTreeView.SelectedItem is FileItemNode selectedNode &&
                string.Equals(selectedNode.FullPath, parentPath, StringComparison.OrdinalIgnoreCase))
            {
                parentNode = selectedNode;
            }
            else
            {
                parentNode = FindNodeByPath(_liveAclFolderTreeRoots, parentPath);
            }

            var newNode = new FileItemNode
            {
                Name = folderName,
                FullPath = newPath,
                IsDirectory = true,
                IsSelected = true
            };

            if (parentNode != null)
            {
                parentNode.IsExpanded = true;
                parentNode.Children.Add(newNode);
            }
            else if (_liveAclFolderTreeRoots.Count > 0)
            {
                _liveAclFolderTreeRoots[0].Children.Add(newNode);
            }
            else
            {
                _liveAclFolderTreeRoots.Add(newNode);
            }
        }

        private static FileItemNode? FindNodeByPath(IEnumerable<FileItemNode> nodes, string path)
        {
            foreach (var node in nodes)
            {
                if (string.Equals(node.FullPath, path, StringComparison.OrdinalIgnoreCase))
                    return node;
                var found = FindNodeByPath(node.Children, path);
                if (found != null) return found;
            }
            return null;
        }

        private void LiveAclFolderTreeView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;
            if (LiveAclFolderTreeView.SelectedItem is not FileItemNode node) return;

            Point currentPoint = e.GetPosition(null);
            Vector diff = _liveAclFolderDragStart - currentPoint;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                DragDrop.DoDragDrop(LiveAclFolderTreeView, new DataObject("FolderMorpherLiveAclFolder", node.FullPath), DragDropEffects.Copy);
            }
        }

        private void LiveAclMultiPanelArea_Drop(object sender, DragEventArgs e)
        {
            if (e.Data.GetData("FolderMorpherLiveAclFolder") is string path)
            {
                AddLiveAclPanel(path);
            }
        }

        private async void AddLiveAclPanel(string path)
        {
            var existing = _liveAclPanels.FirstOrDefault(p => p.FolderPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                ShowToast($"⚠️ すでにパネルが開かれています: {existing.FolderName}");
                return;
            }

            if (_liveAclPanels.Count >= 6)
            {
                MessageBox.Show("同時に開けるパネルは最大6つまでです。\n不要なパネルを閉じてから追加してください。", "パネル上限", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            try
            {
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                var state = await host.GetAclFolderStateAsync(path, CancellationToken.None);

                var panel = new LiveAclPanelModel
                {
                    FolderPath = path,
                    FolderName = Path.GetFileName(path.TrimEnd('\\', '/')),
                    OriginalInheritAcl = state.InheritsAcl,
                    InheritAcl = state.InheritsAcl,
                    OriginalSddl = state.Sddl,
                    StatusMessage = $"所有者: {state.Owner}"
                };

                if (string.IsNullOrEmpty(panel.FolderName)) panel.FolderName = path;

                foreach (var dto in state.Entries)
                {
                    var entry = FolderMorpher.HostClient.AclDtoMapper.ToView(dto);
                    panel.OriginalAclEntries.Add(entry.Clone());
                    panel.CurrentAclEntries.Add(entry.Clone());
                }

                panel.CurrentAclEntries.CollectionChanged += (s, e) => panel.UpdateChangeStatus();
                panel.PropertyChanged += (s, e) =>
                {
                    if (e.PropertyName == nameof(LiveAclPanelModel.InheritAcl))
                        panel.UpdateChangeStatus();
                };

                // Low 1: 初期ロード完了時の変更フラグを確実にクリア（元から継承OFFのフォルダ等の誤検知防止）
                panel.UpdateChangeStatus();

                _liveAclPanels.Add(panel);
                UpdateLiveAclPanelsBanner();
                ShowToast($"🛡️ 権限パネルを追加しました: {panel.FolderName} (計 {_liveAclPanels.Count}/6)");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"権限読み込みエラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LiveAclPanelCloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is LiveAclPanelModel panel)
            {
                if (panel.HasChanges)
                {
                    var res = MessageBox.Show($"「{panel.FolderName}」には未適用の変更があります。閉じてもよろしいですか？", "未適用変更の確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res != MessageBoxResult.Yes) return;
                }
                _liveAclPanels.Remove(panel);
                UpdateLiveAclPanelsBanner();
                ShowToast($"パネルを閉じました: {panel.FolderName}");
            }
        }

        private void LiveAclCloseAllPanelsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_liveAclPanels.Count == 0) return;
            bool hasAnyChanges = _liveAclPanels.Any(p => p.HasChanges);
            if (hasAnyChanges)
            {
                var res = MessageBox.Show("未適用の変更があるパネルが含まれています。すべて閉じてもよろしいですか？", "全パネル閉じる確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (res != MessageBoxResult.Yes) return;
            }
            _liveAclPanels.Clear();
            UpdateLiveAclPanelsBanner();
            ShowToast("全パネルを閉じました");
        }

        private static void ApplyFolderState(LiveAclPanelModel panel, FolderMorpher.Contracts.AclFolderStateDto state)
        {
            panel.OriginalAclEntries.Clear();
            panel.CurrentAclEntries.Clear();
            foreach (var dto in state.Entries)
            {
                var item = FolderMorpher.HostClient.AclDtoMapper.ToView(dto);
                panel.OriginalAclEntries.Add(item.Clone());
                panel.CurrentAclEntries.Add(item.Clone());
            }
            panel.OriginalInheritAcl = state.InheritsAcl;
            panel.InheritAcl = state.InheritsAcl;
            panel.OriginalSddl = state.Sddl;
            panel.UpdateChangeStatus();
        }

        private async void LiveAclPanelApplyDeltaButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not LiveAclPanelModel panel) return;

            if (!panel.HasChanges)
            {
                ShowToast($"未適用の変更はありません: {panel.FolderName}");
                return;
            }

            try { await ShowDiffModalAsync(panel); }
            catch (Exception ex) { MessageBox.Show($"差分確認エラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private async Task ShowDiffModalAsync(LiveAclPanelModel panel)
        {
            _pendingDiffPanel = panel;
            _diffItems.Clear();

            // 1. Change Plan の単一構築 (BuildChangePlan)
            var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
            _currentChangePlan = await host.PrepareAclChangeAsync(new FolderMorpher.Contracts.AclChangeRequestDto
            {
                FolderPath = panel.FolderPath,
                ExpectedOriginalSddl = panel.OriginalSddl,
                InheritanceBefore = panel.OriginalInheritAcl,
                InheritanceAfter = panel.InheritAcl,
                OriginalEntries = panel.OriginalAclEntries.Select(FolderMorpher.HostClient.AclDtoMapper.ToDto).ToList(),
                CurrentEntries = panel.CurrentAclEntries.Select(FolderMorpher.HostClient.AclDtoMapper.ToDto).ToList()
            }, CancellationToken.None);

            LiveAclDiffTargetText.Text = $" - 対象: {panel.FolderName} ({panel.FolderPath})";

            // 2. プレビューリストへのバインド
            foreach (var item in _currentChangePlan.DiffItems)
            {
                _diffItems.Add(FolderMorpher.HostClient.AclDtoMapper.ToView(item));
            }

            // 3. 継承変更バナーの表示/非表示と文言
            if (_currentChangePlan.InheritanceChanged)
            {
                LiveAclInheritanceChangeBanner.Visibility = Visibility.Visible;
                if (!_currentChangePlan.InheritanceAfter)
                {
                    LiveAclInheritanceChangeText.Text = $"🛡️ 継承: 有効 ➔ 無効 (親からの継承ACE {_currentChangePlan.InheritedAcesPromotedCount}件を明示ACEへ昇格・保持)";
                }
                else
                {
                    LiveAclInheritanceChangeText.Text = "🛡️ 継承: 無効 ➔ 有効 (親フォルダーからの権限を再継承)";
                }
            }
            else
            {
                LiveAclInheritanceChangeBanner.Visibility = Visibility.Collapsed;
            }

            // 4. サマリーテキスト更新
            LiveAclDiffSummaryText.Text = $"📊 予定: +{_currentChangePlan.AddedCount}件, -{_currentChangePlan.RemovedCount}件, ~{_currentChangePlan.ModifiedCount}件";
            LiveAclDiffUntouchedText.Text = $" (🛡️ 維持: {_currentChangePlan.UntouchedCount}件)";

            // 5. 外部競合チェック (✅ 正常 / ⚠️ 外部競合)
            bool hasConflict = _currentChangePlan.HasConflict;

            if (hasConflict)
            {
                LiveAclConflictBadge.Background = new SolidColorBrush(Color.FromRgb(0xFE, 0xE2, 0xE2));
                LiveAclConflictBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFE, 0xCA, 0xCA));
                LiveAclConflictBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0xB9, 0x1C, 0x1C));
                LiveAclConflictBadgeText.Text = "⚠️ 外部競合";
            }
            else
            {
                LiveAclConflictBadge.Background = new SolidColorBrush(Color.FromRgb(0xDC, 0xFC, 0xE7));
                LiveAclConflictBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(0xBB, 0xF7, 0xD0));
                LiveAclConflictBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0x15, 0x80, 0x3D));
                LiveAclConflictBadgeText.Text = "✅ 正常";
            }

            LiveAclDiffModalOverlay.Visibility = Visibility.Visible;
        }

        private void LiveAclDiffModalClose_Click(object sender, RoutedEventArgs e)
        {
            LiveAclDiffModalOverlay.Visibility = Visibility.Collapsed;
            _pendingDiffPanel = null;
            _currentChangePlan = null;
        }

        private async void LiveAclDiffModalExecute_Click(object sender, RoutedEventArgs e)
        {
            if (_pendingDiffPanel == null || _currentChangePlan == null) return;
            var panel = _pendingDiffPanel;
            var plan = _currentChangePlan;

            // 競合がある場合の最終確認
            FolderMorpher.Contracts.IFolderMorpherHostService host;
            string currentSddl;
            try
            {
                host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                currentSddl = (await host.GetAclFolderStateAsync(panel.FolderPath, CancellationToken.None)).Sddl;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ACLの再確認に失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            bool hasConflict = !string.IsNullOrEmpty(panel.OriginalSddl) &&
                               !string.IsNullOrEmpty(currentSddl) &&
                               !string.Equals(currentSddl, panel.OriginalSddl, StringComparison.OrdinalIgnoreCase);

            bool forceApply = false;
            if (hasConflict)
            {
                var conflictRes = MessageBox.Show(
                    $"⚠️ 外部ACL変更の競合が検知されています。\n\n外部の変更を上書きして適用を強制続行しますか？",
                    "外部ACL競合",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (conflictRes != MessageBoxResult.Yes) return;
                forceApply = true;
            }

            LiveAclDiffModalOverlay.Visibility = Visibility.Collapsed;
            panel.IsApplying = true;
            panel.StatusMessage = "適用中...";

            try
            {
                // 1. Commit (Change Plan をそのまま適用)
                var result = await host.CommitAclChangeAsync(plan.PlanId, forceApply, CancellationToken.None);
                if (result.WasConflict)
                {
                    panel.StatusMessage = "外部競合";
                    var reload = MessageBox.Show(
                        "適用直前に外部変更（競合）が検出されました。最新のACLを再読込しますか？",
                        "外部ACL競合", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (reload == MessageBoxResult.Yes) ApplyFolderState(panel, result.CurrentState);
                    return;
                }
                int appliedDeltaCount = result.AddedCount + result.RemovedCount + result.ModifiedCount;
                ApplyFolderState(panel, result.CurrentState);

                if (result.VerificationSucceeded)
                {
                    panel.StatusMessage = $"正常 ({DateTime.Now:HH:mm:ss})";
                    ShowToast($"✅ 適用完了 (正常): {panel.FolderName} ({appliedDeltaCount}件反映)");
                }
                else
                {
                    panel.StatusMessage = "検証不一致";
                    ShowToast($"⚠️ 適用結果に不一致を検知: {panel.FolderName}");
                }
            }
            catch (Exception ex)
            {
                panel.StatusMessage = "適用失敗";
                MessageBox.Show($"適用エラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                panel.IsApplying = false;
                _pendingDiffPanel = null;
                _currentChangePlan = null;
            }
        }

        private async void LiveAclPanelRollbackButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not LiveAclPanelModel panel) return;

            try
            {
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                var snapshots = await host.GetAclSnapshotsAsync(panel.FolderPath, CancellationToken.None);
                if (snapshots.Count == 0)
                {
                    MessageBox.Show("このフォルダの保存済みバックアップはありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var latest = snapshots[0];
                var confirm = MessageBox.Show(
                    $"最新のバックアップ（{latest.Timestamp:yyyy/MM/dd HH:mm:ss} 保存）へ復元しますか？\n\n対象: {panel.FolderPath}",
                    "バックアップ復元確認",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (confirm == MessageBoxResult.Yes)
                {
                    var state = await host.RollbackAclSnapshotAsync(panel.FolderPath, latest.Id, CancellationToken.None);
                    ApplyFolderState(panel, state);
                    panel.StatusMessage = $"復元完了 ({latest.Timestamp:HH:mm:ss})";

                    ShowToast($"↩️ バックアップから復元しました: {panel.FolderName}");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"復元エラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void LiveAclExportMatrixButton_Click(object sender, RoutedEventArgs e)
        {
            var path = LiveAclPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
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
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    await host.ExportAclMatrixAsync(dialog.FileName, path, CancellationToken.None);
                    ShowToast("権限台帳CSVを出力しました");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"CSV出力エラー:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void LiveAclPanelDropZone_Drop(object sender, DragEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not LiveAclPanelModel panel) return;

            if (e.Data.GetData(typeof(SimAclEntry)) is SimAclEntry)
            {
                _droppedInSelfContainer = true;
                return;
            }

            if (e.Data.GetData(typeof(AdPrincipalItem)) is AdPrincipalItem p)
            {
                AddPrincipalToPanel(panel, p);
            }
        }

        private void LiveAclCardsContainer_Drop(object sender, DragEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not LiveAclPanelModel panel) return;

            if (e.Data.GetData(typeof(SimAclEntry)) is SimAclEntry)
            {
                _droppedInSelfContainer = true;
                return;
            }

            if (e.Data.GetData(typeof(AdPrincipalItem)) is AdPrincipalItem p)
            {
                AddPrincipalToPanel(panel, p);
            }
        }

        private void AddPrincipalToPanel(LiveAclPanelModel panel, AdPrincipalItem p)
        {
            // Low~Medium: 完全に同一のアクセス権ルール（アカウント・権限・適用先）が既に存在する場合は重複防止
            if (panel.CurrentAclEntries.Any(a =>
                a.AccountName.Equals(p.AccountName, StringComparison.OrdinalIgnoreCase) &&
                a.Rights == FileSystemRights.ReadAndExecute &&
                a.AccessType == AccessControlType.Allow &&
                a.InheritanceFlags == (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit) &&
                a.PropagationFlags == PropagationFlags.None))
            {
                ShowToast($"⚠️ すでに同一のアクセス権ルールが存在します: {p.DisplayName}");
                return;
            }

            bool isAdditional = panel.CurrentAclEntries.Any(a => a.AccountName.Equals(p.AccountName, StringComparison.OrdinalIgnoreCase));

            var entry = new SimAclEntry
            {
                AccountName = p.AccountName,
                DisplayName = p.DisplayName,
                PrincipalType = p.PrincipalType,
                Rights = FileSystemRights.ReadAndExecute
            };
            panel.CurrentAclEntries.Add(entry);
            panel.UpdateChangeStatus();

            if (isAdditional)
            {
                ShowToast($"👥 「{panel.FolderName}」に同一アカウントの追加ルールを作成しました: {p.DisplayName} (Wクリックで詳細設定)");
            }
            else
            {
                ShowToast($"🛡️ 「{panel.FolderName}」に権限カードを追加: {p.DisplayName}");
            }
        }

        private void LiveAclCard_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                _cardDragStartPoint = e.GetPosition(null);
            }

            if (e.ClickCount == 2 && sender is FrameworkElement fe && fe.DataContext is SimAclEntry acl)
            {
                // High 3: 継承ACEは親から引き継がれているため直接編集を抑止
                if (acl.IsInherited)
                {
                    ShowToast("🔒 親フォルダーから継承されているため直接編集できません。\n親フォルダーで変更するか、「親フォルダからの権限継承」を外してください。");
                    return;
                }

                var parentPanel = _liveAclPanels.FirstOrDefault(p => p.CurrentAclEntries.Contains(acl));
                string folderName = parentPanel?.FolderName ?? string.Empty;
                string folderPath = parentPanel?.FolderPath ?? LiveAclPathTextBox.Text;

                EditSecurityRequested?.Invoke(acl, folderName, folderPath, () =>
                {
                    parentPanel?.UpdateChangeStatus();
                });
            }
        }

        private void LiveAclCard_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed) return;

            Point currentPoint = e.GetPosition(null);
            Vector diff = _cardDragStartPoint - currentPoint;

            if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
                Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
            {
                if (sender is FrameworkElement fe && fe.DataContext is SimAclEntry acl)
                {
                    // High 3: 継承ACEは削除不可
                    if (acl.IsInherited)
                    {
                        return;
                    }

                    _droppedInSelfContainer = false;
                    _dragCancelled = false;
                    var parentPanel = _liveAclPanels.FirstOrDefault(p => p.CurrentAclEntries.Contains(acl));
                    try
                    {
                        DragDrop.AddQueryContinueDragHandler(fe, OnCardQueryContinueDrag);
                        DragDrop.DoDragDrop(fe, acl, DragDropEffects.Move | DragDropEffects.Copy);
                    }
                    finally
                    {
                        DragDrop.RemoveQueryContinueDragHandler(fe, OnCardQueryContinueDrag);
                        // Escキャンセルされた場合は削除しない。マウスドロップで枠外に落ちた場合のみ解除
                        if (!_dragCancelled && !_droppedInSelfContainer && parentPanel != null)
                        {
                            parentPanel.CurrentAclEntries.Remove(acl);
                            parentPanel.UpdateChangeStatus();
                            ShowToast($"🗑️ 枠外ドロップで権限カードを解除しました: {acl.DisplayName}");
                        }
                    }
                }
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
                                p.AccountName.Contains(q, StringComparison.OrdinalIgnoreCase));
            }
        }

        private void LiveAclPrincipalsListBox_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed && LiveAclPrincipalsListBox.SelectedItem is AdPrincipalItem item)
            {
                DragDrop.DoDragDrop(LiveAclPrincipalsListBox, item, DragDropEffects.Copy);
            }
        }

        private void AdRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (RefreshAdRequested != null)
            {
                RefreshAdRequested.Invoke();
            }
            else
            {
                _ = CheckAdChangesAsync(forceRefresh: true);
            }
        }

        private void StartAdSyncTimer()
        {
            if (_adSyncTimer != null) return;
            _adSyncTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _adSyncTimer.Tick += async (s, e) => await CheckAdChangesAsync();
            _adSyncTimer.Start();
        }

        private async Task CheckAdChangesAsync(bool forceRefresh = false)
        {
            try
            {
                var currentQuery = LiveAclPrincipalSearchTextBox?.Text?.Trim() ?? "";
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                var latestPrincipals = (await host.GetAdPrincipalsAsync(currentQuery, CancellationToken.None))
                    .Select(FolderMorpher.HostClient.IdentityDtoMapper.ToView).ToList();

                if (forceRefresh || HasPrincipalsChanged(_rawPrincipalsCache, latestPrincipals))
                {
                    _rawPrincipalsCache = latestPrincipals;
                    _liveAclPrincipals.Clear();
                    foreach (var p in latestPrincipals)
                    {
                        _liveAclPrincipals.Add(p);
                    }
                    UpdateLiveAclNoticeState();
                    if (forceRefresh)
                    {
                        ShowToast(Strings.AdSyncUpdatedToast);
                    }
                }
            }
            catch
            {
                // バックグラウンド同期での一時的エラーはサイレントに処理
            }
        }

        private static bool HasPrincipalsChanged(List<AdPrincipalItem> oldList, List<AdPrincipalItem> newList)
        {
            if (oldList.Count != newList.Count) return true;
            for (int i = 0; i < oldList.Count; i++)
            {
                if (!string.Equals(oldList[i].AccountName, newList[i].AccountName, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(oldList[i].DisplayName, newList[i].DisplayName, StringComparison.OrdinalIgnoreCase) ||
                    oldList[i].PrincipalType != newList[i].PrincipalType)
                {
                    return true;
                }
            }
            return false;
        }
        #endregion

        #region Effective Access (ユーザー/グループ 逆引き監査) Handlers
        private void RevRootPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (RevUncNoticeBanner == null) return;
            var text = RevRootPathTextBox.Text.Trim();
            RevUncNoticeBanner.Visibility = text.StartsWith(@"\\") ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RevBrowseRoot_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "逆引き走査を行うルートフォルダーを選択（UNC対応）",
                InitialDirectory = string.IsNullOrWhiteSpace(RevRootPathTextBox.Text) ? @"C:\" : RevRootPathTextBox.Text
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                RevRootPathTextBox.Text = dialog.FolderName;
            }
        }

        private async void RevStartScan_Click(object sender, RoutedEventArgs e)
        {
            var targetAccount = RevUserAccountTextBox.Text.Trim();
            var rootPath = RevRootPathTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(targetAccount))
            {
                MessageBox.Show("調査対象のアカウント名（ユーザーまたはグループ）を入力してください。", "入力確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                RevUserAccountTextBox.Focus();
                return;
            }

            if (string.IsNullOrWhiteSpace(rootPath))
            {
                MessageBox.Show("有効な走査ルートフォルダーを指定してください。", "入力確認", MessageBoxButton.OK, MessageBoxImage.Warning);
                RevRootPathTextBox.Focus();
                return;
            }

            // Parse selected search depth
            int maxDepth = int.MaxValue;
            if (RevDepthComboBox.SelectedItem is ComboBoxItem item && item.Tag is string tagStr)
            {
                if (int.TryParse(tagStr, out int parsed)) maxDepth = parsed;
            }

            RevStartScanButton.Visibility = Visibility.Collapsed;
            RevCancelScanButton.Visibility = Visibility.Visible;
            RevProgressBar.Visibility = Visibility.Visible;
            RevExportExcelButton.IsEnabled = false;

            RevTargetAccountHeader.Text = $"👤 調査対象: {targetAccount}";
            RevTargetAccountSub.Text = "所属グループを解決中...";
            RevStatusText.Text = "Active Directory / ローカルグループを解決中...";

            _revGroups.Clear();
            _revFolders.Clear();
            _revAllFoldersCache.Clear();
            RevKpiTotal.Text = "0";
            RevKpiEnclave.Text = "0";
            RevKpiSevered.Text = "0";
            RevKpiUnavailable.Text = "0";
            RevKpiFull.Text = "0";
            RevKpiMod.Text = "0";
            RevKpiRead.Text = "0";

            _revCts = new CancellationTokenSource();
            var ct = _revCts.Token;

            try
            {
                // 1. グループ解決（直接所属＋多重入れ子AD Chain）
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync(ct);
                var resolution = await host.ResolveEffectiveMembershipsAsync(targetAccount, ct);
                foreach (var group in resolution.Groups)
                    _revGroups.Add(FolderMorpher.HostClient.IdentityDtoMapper.ToView(group));
                RevGroupCountText.Text = $"{_revGroups.Count} 件";
                RevTargetAccountSub.Text = resolution.StatusText;

                // 2. フォルダツリーの実効アクセス権スキャン
                RevStatusText.Text = "フォルダーツリーの実効アクセス権（Effective Access）を監査中...";

                var effectiveJob = await FolderMorpher.HostClient.HostJobClient.RunAsync(
                    new FolderMorpher.Contracts.HostJobRequestDto
                    {
                        Kind = FolderMorpher.Contracts.HostJobKind.EffectiveAccessAudit,
                        TargetPath = rootPath,
                        TargetAccount = targetAccount,
                        MaxDepth = maxDepth,
                        MembershipResolution = resolution
                    }, status =>
                    {
                        if (!string.IsNullOrWhiteSpace(status.ProgressText)) RevStatusText.Text = status.ProgressText;
                    }, ct);
                var reportDto = effectiveJob.EffectiveAccessReport ?? throw new InvalidOperationException("逆引き監査結果がHostから返されませんでした。");
                var report = FolderMorpher.HostClient.IdentityDtoMapper.ToView(reportDto);

                _currentEffectiveReport = report;
                _revAllFoldersCache.AddRange(report.AllAuditItems);

                // フィルター（変化点・テキスト）を適用して一覧に反映
                ApplyRevFilter();

                RevKpiTotal.Text = $"{report.AccessibleFolders.Count:N0}";
                RevKpiEnclave.Text = $"{report.EnclaveCount:N0}";
                RevKpiSevered.Text = $"{report.SeveredCount:N0}";
                RevKpiUnavailable.Text = $"{report.UnavailableCount:N0}";
                RevKpiFull.Text = $"{report.FullControlCount:N0}";
                RevKpiMod.Text = $"{report.ModifyCount:N0}";
                RevKpiRead.Text = $"{report.ReadOnlyCount:N0}";

                RevStatusText.Text = $"監査完了: 総走査 {report.TotalFoldersScanned:N0} フォルダ中、{report.AccessibleFolders.Count:N0} 件のフォルダーを検出 (飛び地: {report.EnclaveCount}件, 遮断: {report.SeveredCount}件, 走査不能: {report.UnavailableCount}件)";
                RevExportExcelButton.IsEnabled = report.AccessibleFolders.Count > 0;
                ShowToast($"🔍 「{targetAccount}」の逆引き監査が完了しました ({report.AccessibleFolders.Count:N0} 件)");
            }
            catch (OperationCanceledException)
            {
                RevStatusText.Text = "⚠️ ユーザーによって調査が中止されました。";
                ShowToast("⏹️ 逆引き調査を中止しました");
            }
            catch (Exception ex)
            {
                RevStatusText.Text = $"エラー: {ex.Message}";
                MessageBox.Show($"逆引き調査中にエラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                RevStartScanButton.Visibility = Visibility.Visible;
                RevCancelScanButton.Visibility = Visibility.Collapsed;
                RevProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void RevCancelScan_Click(object sender, RoutedEventArgs e)
        {
            _revCts?.Cancel();
        }

        private async void RevExportExcel_Click(object sender, RoutedEventArgs e)
        {
            if (_currentEffectiveReport == null || _currentEffectiveReport.AccessibleFolders.Count == 0) return;

            var safeName = _currentEffectiveReport.TargetAccountName.Replace('\\', '_').Replace('/', '_');
            var sfd = new SaveFileDialog
            {
                Title = "実効アクセス権（逆引き監査）台帳の保存先を指定",
                Filter = "Excel ワークブック (*.xlsx)|*.xlsx",
                InitialDirectory = GetDefaultExportDirectory(),
                FileName = $"EffectiveAccessAudit_{safeName}_{DateTime.Now:yyyyMMdd_HHmm}.xlsx"
            };

            if (sfd.ShowDialog() == true)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    await host.ExportEffectiveAccessReportAsync(sfd.FileName,
                        FolderMorpher.HostClient.IdentityDtoMapper.ToDto(_currentEffectiveReport), CancellationToken.None);
                    ShowToast($"📋 {Path.GetFileName(sfd.FileName)} を出力しました");
                    ShellHelper.SelectInExplorer(sfd.FileName);
                    Process.Start(new ProcessStartInfo(sfd.FileName) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Excel出力に失敗しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void RevFoldersDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (RevFoldersDataGrid.SelectedItem is EffectiveFolderAccessItem item && !string.IsNullOrWhiteSpace(item.FolderPath))
            {
                ShellHelper.OpenFolder(item.FolderPath);
            }
        }

        private void RevFilterChangesOnlyCheckBox_Click(object sender, RoutedEventArgs e)
        {
            ApplyRevFilter();
        }

        private void RevFolderFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyRevFilter();
        }

        private void ApplyRevFilter()
        {
            var filter = RevFolderFilterTextBox?.Text?.Trim() ?? string.Empty;
            bool changesOnly = RevFilterChangesOnlyCheckBox?.IsChecked == true;

            _revFolders.Clear();

            var source = _revAllFoldersCache.AsEnumerable();

            if (changesOnly)
            {
                source = source.Where(f => f.IsChangePoint);
            }

            if (!string.IsNullOrWhiteSpace(filter))
            {
                source = source.Where(f =>
                    f.FolderName.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    f.FolderPath.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    f.GrantSource.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    f.FormattedRights.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                    f.ChangeBadgeText.Contains(filter, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var item in source)
            {
                _revFolders.Add(item);
            }
        }
        #endregion

        #region Principal Picker Modal (OU Hierarchy & Account Selection)
        private async void RevBrowseUser_Click(object sender, RoutedEventArgs e)
        {
            PrincipalPickerModalOverlay.Visibility = Visibility.Visible;
            PickerSelectedAccountText.Text = string.IsNullOrWhiteSpace(RevUserAccountTextBox.Text) ? "(未選択)" : RevUserAccountTextBox.Text.Trim();
            PickerApplyButton.IsEnabled = !string.IsNullOrWhiteSpace(RevUserAccountTextBox.Text);

            if (_pickerOuRoots.Count == 0)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    var ous = (await host.GetAdOuHierarchyAsync(CancellationToken.None))
                        .Select(FolderMorpher.HostClient.IdentityDtoMapper.ToView).ToList();
                    _pickerOuRoots.Clear();
                    foreach (var ou in ous)
                    {
                        _pickerOuRoots.Add(ou);
                    }

                    if (_pickerOuRoots.Count > 0)
                    {
                        _pickerSelectedOu = _pickerOuRoots[0];
                        await LoadPrincipalsForSelectedOuAsync();
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Failed to load OU hierarchy: {ex}");
                }
            }
            else if (_pickerSelectedOu != null)
            {
                await LoadPrincipalsForSelectedOuAsync();
            }
        }

        private async void PickerOuTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is AdOuNode node)
            {
                _pickerSelectedOu = node;
                await LoadPrincipalsForSelectedOuAsync();
            }
        }

        private async Task LoadPrincipalsForSelectedOuAsync()
        {
            if (_pickerSelectedOu == null) return;

            var keyword = PickerSearchTextBox.Text.Trim();
            bool incUsers = PickerIncludeUsersCheck.IsChecked == true;
            bool incGroups = PickerIncludeGroupsCheck.IsChecked == true;

            try
            {
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                var list = (await host.GetAdPrincipalsInOuAsync(
                    _pickerSelectedOu.DistinguishedName, keyword, incUsers, incGroups, CancellationToken.None))
                    .Select(FolderMorpher.HostClient.IdentityDtoMapper.ToView).ToList();
                _pickerPrincipals.Clear();
                foreach (var p in list)
                {
                    _pickerPrincipals.Add(p);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error querying principals in OU: {ex}");
            }
        }

        private async void PickerFilter_Changed(object sender, RoutedEventArgs e)
        {
            if (PrincipalPickerModalOverlay.Visibility == Visibility.Visible)
            {
                await LoadPrincipalsForSelectedOuAsync();
            }
        }

        private async void PickerSearchTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (PrincipalPickerModalOverlay.Visibility == Visibility.Visible)
            {
                await LoadPrincipalsForSelectedOuAsync();
            }
        }

        private void PickerPrincipalsDataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (PickerPrincipalsDataGrid.SelectedItem is AdPrincipalItem item)
            {
                _pickerSelectedPrincipal = item;
                PickerSelectedAccountText.Text = item.AccountName;
                PickerApplyButton.IsEnabled = true;
            }
        }

        private void PickerPrincipalsDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (PickerPrincipalsDataGrid.SelectedItem is AdPrincipalItem item)
            {
                _pickerSelectedPrincipal = item;
                ApplySelectedPrincipal();
            }
        }

        private void PickerApplyButton_Click(object sender, RoutedEventArgs e)
        {
            ApplySelectedPrincipal();
        }

        private void ApplySelectedPrincipal()
        {
            if (_pickerSelectedPrincipal != null)
            {
                RevUserAccountTextBox.Text = _pickerSelectedPrincipal.AccountName;
                PrincipalPickerModalOverlay.Visibility = Visibility.Collapsed;
                ShowToast($"👤 調査対象を「{_pickerSelectedPrincipal.AccountName}」に設定しました");
            }
            else if (!string.IsNullOrWhiteSpace(PickerSelectedAccountText.Text) && PickerSelectedAccountText.Text != "(未選択)")
            {
                RevUserAccountTextBox.Text = PickerSelectedAccountText.Text;
                PrincipalPickerModalOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private void PickerCancelButton_Click(object sender, RoutedEventArgs e)
        {
            PrincipalPickerModalOverlay.Visibility = Visibility.Collapsed;
        }
        #endregion

        #region Localization (i18n)
        public void ApplyLocalization(bool isJa)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ApplyLocalization(isJa));
                return;
            }

            // Mode Header
            LiveAclHeaderTitle.Text = isJa ? "🛡️ 権限コントロール" : "🛡️ Permission Control";
            LiveAclModeFolderRadio.Content = isJa ? "📁 フォルダ別 権限エディタ" : "📁 Folder ACL Editor";
            LiveAclModeReverseRadio.Content = isJa ? "🔍 ユーザー/グループ 逆引き監査 (Effective Access)" : "🔍 Effective Access (Reverse Lookup)";
            LiveAclBrowseButton.Content = isJa ? "参照" : "Browse";
            LiveAclExportMatrixButton.Content = isJa ? "📋 CSV出力" : "📋 Export CSV";

            LiveAclTargetFolderLabel.Text = isJa ? "マルチフォルダ 権限エディタ" : "Multi-Folder ACL Editor";
            LiveAclEditorSubtitleText.Text = isJa ? "💡 ツリーからフォルダーを開いて権限を編集（最大6つ同時表示）" : "💡 Open folders from tree to edit permissions (up to 6 simultaneously)";
            LiveAclCloseAllPanelsButton.Content = isJa ? "✕ 全て閉じる" : "✕ Close All";
            LiveAclCloseAllPanelsButton.ToolTip = isJa ? "開いているパネルをすべて閉じます" : "Close all open panels";
            LiveAclBrowseTitleText.Text = isJa ? "📁 フォルダ参照" : "📁 Folder Explorer";
            LiveAclBrowseSubText.Text = isJa ? "Wクリック または D&Dでパネル追加" : "Double-click or D&D to open panel";
            LiveAclReloadButton.ToolTip = isJa ? "フォルダ階層を展開" : "Expand folder hierarchy";

            LiveAclPrincipalsHeaderTitle.Text = isJa ? "👥 Active Directory / ローカル" : "👥 Active Directory / Local";
            AdSyncBadgeText.Text = Strings.AdSyncBadge;
            AdSyncBadgeBorder.ToolTip = Strings.AdSyncToolTip;
            AdRefreshButton.ToolTip = Strings.AdRefreshToolTip;
            LiveAclDomainStatusBadge.ToolTip = Strings.AdDomainJoinedTooltip;
            UpdateDomainBadge();

            LiveAclLocalPcTitle.Text = isJa ? "⚠️ ローカルPC環境" : "⚠️ Local PC Environment";
            LiveAclLocalPcDesc.Text = isJa ? "※ローカルPC環境のためADプリンシパルは未接続です\n（上部の入力欄から直接アカウント名を入力して追加可能）" : "※Not connected to Active Directory in local PC environment.\n(Direct account name input is available)";

            // Reverse Lookup (Effective Access)
            RevBrowseRootButton.Content = isJa ? "参照..." : "Browse...";
            RevStartScanButton.Content = isJa ? "🔍 調査開始" : "🔍 Start Scan";
            RevCancelScanButton.Content = isJa ? "⏹️ 中止" : "⏹️ Cancel";
            RevExportExcelButton.Content = isJa ? "📋 Excel出力" : "📋 Export Excel";

            RevTargetAccountLabel.Text = isJa ? "調査対象 ユーザー / グループ (sAMAccountName または 表示名)" : "Target User / Group (sAMAccountName or Display Name)";
            RevRootPathLabel.Text = isJa ? "調査ルートディレクトリ (UNC / ローカル)" : "Root Directory (UNC / Local)";
            RevDepthLabel.Text = isJa ? "探索階層深度" : "Folder Depth";

            RevDepthItemUnlimited.Content = isJa ? "無制限 (推奨)" : "Unlimited (Recommended)";
            RevDepthItem1.Content = isJa ? "第1階層 (直下のみ)" : "Level 1 (Direct Only)";
            RevDepthItem2.Content = isJa ? "第2階層まで" : "Up to Level 2";
            RevDepthItem3.Content = isJa ? "第3階層まで" : "Up to Level 3";
            RevDepthItem5.Content = isJa ? "第5階層まで" : "Up to Level 5";
            RevDepthItem8.Content = isJa ? "第8階層まで" : "Up to Level 8";
            RevDepthItem10.Content = isJa ? "第10階層まで" : "Up to Level 10";

            RevUncNoticeText.Text = isJa ? "※UNC共有経由アクセス時は、ファイルサーバーのSMB共有権限（Share Permissions）の上限も併せて適用されます。" : "*When accessing via UNC shares, SMB share permissions also apply as an upper limit.";
            RevTargetAccountHeader.Text = isJa ? "👤 調査対象: (未選択)" : "👤 Target: (None selected)";
            RevTargetAccountSub.Text = isJa ? "所属グループと実効権限を自動解決します" : "Automatically resolves groups and effective access";
            RevGroupsHeaderTitle.Text = isJa ? "👥 所属グループ (多重入れ子・再帰解決)" : "👥 Group Memberships (Recursive Chain)";
            RevGroupsLegendText.Text = isJa ? "💡 青バッジ＝直接所属 / 紫バッジ＝多重入れ子所属 (AD Chainにより自動解決)" : "💡 Blue: Direct / Purple: Nested / Green: Built-in";
            RevKpiTotalTitle.Text = isJa ? "アクセス可能" : "Accessible";
            RevKpiEnclaveTitle.Text = Strings.RevKpiEnclaveTitle;
            RevKpiSeveredTitle.Text = Strings.RevKpiSeveredTitle;
            RevKpiUnavailableTitle.Text = Strings.RevKpiUnavailableTitle;
            RevKpiFullTitle.Text = isJa ? "フルコントロール" : "Full Control";
            RevKpiModTitle.Text = isJa ? "変更 (Modify)" : "Modify";
            RevKpiReadTitle.Text = isJa ? "読み取り専用" : "Read-Only";
            RevFoldersTableTitle.Text = isJa ? "📂 監査フォルダー一覧" : "📂 Audit Folders";
            RevFilterChangesOnlyCheckBox.Content = Strings.RevFilterChangesOnly;
            RevFilterChangesOnlyCheckBox.ToolTip = Strings.RevFilterChangesOnlyToolTip;
            RevFolderFilterLabel.Text = isJa ? "絞り込み:" : "Filter:";

            ColRevFolderName.Header = isJa ? "フォルダ名" : "Folder Name";
            ColRevChangeType.Header = Strings.ColRevChangeType;
            ColRevRights.Header = isJa ? "実効権限レベル" : "Effective Rights";
            ColRevGrantSource.Header = isJa ? "権限付与元 (直接付与 / 経由グループ)" : "Grant Source (Direct / Group)";
            ColRevGrantTrace.Header = Strings.ColRevGrantTrace;
            ColRevFullPath.Header = isJa ? "フォルダー完全パス" : "Full Folder Path";

            if (_revFolders.Count > 0)
            {
                RevFoldersDataGrid.Items.Refresh();
            }

            // OU Picker Modal
            PickerTitleText.Text = isJa ? "👥 調査対象アカウントの参照・選択" : "👥 Select Target Account";
            PickerDescText.Text = isJa
                ? "組織単位 (OU) 階層からユーザーまたはセキュリティグループを選択します。"
                : "Select user or security group from Organizational Unit (OU) hierarchy.";
            PickerListTitleText.Text = isJa ? "選択OU内の所属アカウント一覧" : "Accounts in Selected OU";
            PickerIncludeUsersCheck.Content = isJa ? "👤 ユーザー" : "👤 Users";
            PickerIncludeGroupsCheck.Content = isJa ? "👥 グループ" : "👥 Groups";
            ColPickerType.Header = isJa ? "種別" : "Type";
            ColPickerName.Header = isJa ? "表示名 (アカウント名)" : "Name (sAMAccountName)";
            ColPickerDesc.Header = isJa ? "説明" : "Description";
            PickerSelectedLabel.Text = isJa ? "選択中:" : "Selected:";
            PickerCancelButton.Content = isJa ? "キャンセル" : "Cancel";
            PickerApplyButton.Content = isJa ? "決定" : "Select";
            RevBrowseUserButton.Content = isJa ? "👥 参照..." : "👥 Browse...";

            // Empty Banner
            LiveAclNoPanelsTitle.Text = isJa ? "権限操作パネルが開かれていません" : "No Permission Panels Open";
            LiveAclNoPanelsDesc.Text = isJa
                ? "左のフォルダツリーからフォルダをダブルクリック、またはここにドラッグ＆ドロップしてください。"
                : "Double-click a folder from the tree on the left, or drag & drop here.";
            LiveAclNoPanelsNote.Text = isJa
                ? "※最大6つのフォルダを横並びで同時に比較・編集できます。"
                : "*Compare and edit up to 6 folders side by side simultaneously.";

            // Opened Panels
            foreach (var panel in _liveAclPanels)
            {
                panel.NotifyLanguageChanged();
            }

            // Live ACL Diff Modal (Dry-Run)
            LiveAclDiffTitleText.Text = isJa ? "⚖️ 変更点" : "⚖️ Changes";
            LiveAclDiffTargetText.Text = isJa ? " - 対象フォルダー" : " - Target Folder";
            ColLiveAclDiffType.Header = isJa ? "種別" : "Type";
            ColLiveAclDiffAccessType.Header = isJa ? "設定" : "Access";
            ColLiveAclDiffAccount.Header = isJa ? "アカウント / プリンシパル" : "Account / Principal";
            ColLiveAclDiffBefore.Header = isJa ? "変更前の権限 (Before)" : "Before Rights";
            ColLiveAclDiffAfter.Header = isJa ? "変更後の権限 (After)" : "After Rights";
            ColLiveAclDiffDetails.Header = isJa ? "差分詳細" : "Details";
            ColLiveAclDiffAppliesTo.Header = isJa ? "適用先 (AppliesTo)" : "Applies To";
            LiveAclDiffFooterNotice.Text = isJa
                ? "🛡️ 適用直前の状態は自動保存され、いつでもロールバック可能です"
                : "🛡️ State before apply is automatically saved and can be rolled back anytime";
            LiveAclDiffModalCancelButton.Content = isJa ? "キャンセル" : "Cancel";
            LiveAclDiffModalExecuteButton.Content = isJa ? "⚡ 適用" : "⚡ Apply";
        }
        #endregion
    }
}
