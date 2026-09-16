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
        #region Tab 3: Migration Simulation Studio (FolderMorph Studio)
        private void InitializeSimulationStudio()
        {
            SimMockTreeView.ItemsSource = _simRootFolders;
            AdPrincipalsItemsControl.ItemsSource = _adPrincipals;

            // 空ツリー時のエンプティステート表示を連動
            _simRootFolders.CollectionChanged += (s, e) => UpdateSimEmptyState();
            UpdateSimEmptyState();

            // ドメイン状態バッジの初期表示
            UpdateSimulationDomainBadge();

            // Live ACL との AD 更新連動（双方向連携）
            LiveAclStudioControl.RefreshAdRequested += () => _ = CheckAndSyncAdPrincipalsAsync(forceRefresh: true);

            // 30秒周期のバックグラウンド自動同期タイマー
            _adSyncTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _adSyncTimer.Tick += async (s, e) => await CheckAndSyncAdPrincipalsAsync(forceRefresh: false);

            _simHoverExpandTimer.Tick += SimHoverExpandTimer_Tick;
            UpdateSimCloneButtonState();

            Loaded += (s, e) => _adSyncTimer?.Start();
            Closed += (s, e) =>
            {
                _adSyncTimer?.Stop();
                _simHoverExpandTimer?.Stop();
            };
        }

        private void UpdateSimulationDomainBadge()
        {
            if (DomainStatusText == null || DomainStatusBadge == null) return;
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            if (_adService.IsDomainJoined)
            {
                DomainStatusText.Text = $"🟢 {_adService.CurrentDomainName.ToUpperInvariant()}";
                DomainStatusBadge.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#DCFCE7")!;
                DomainStatusText.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#15803D")!;
            }
            else
            {
                DomainStatusText.Text = Strings.AdDomainLocal;
                DomainStatusBadge.Background = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#FEF3C7")!;
                DomainStatusText.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#B45309")!;
            }
            DomainStatusBadge.ToolTip = Strings.AdDomainJoinedTooltip;
        }

        private void UpdateSimEmptyState()
        {
            if (SimEmptyStateBorder != null)
            {
                SimEmptyStateBorder.Visibility = _simRootFolders.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        private async Task LoadAdPrincipalsAsync(string filter = "")
        {
            var list = await _adService.SearchPrincipalsAsync(filter);
            _rawAdPrincipalsCache = list;
            _adPrincipals.Clear();
            foreach (var item in list) _adPrincipals.Add(item);
            LiveAclStudioControl.SetPrincipals(_adPrincipals);
        }

        private async Task CheckAndSyncAdPrincipalsAsync(bool forceRefresh = false)
        {
            if (_adService == null) return;
            try
            {
                var currentQuery = AdSearchTextBox?.Text?.Trim() ?? "";
                var latest = await _adService.SearchPrincipalsAsync(currentQuery);
                if (forceRefresh || HasPrincipalsChanged(_rawAdPrincipalsCache, latest))
                {
                    _rawAdPrincipalsCache = latest;
                    _adPrincipals.Clear();
                    foreach (var p in latest) _adPrincipals.Add(p);
                    LiveAclStudioControl.SetPrincipals(_adPrincipals);
                    if (forceRefresh)
                    {
                        ShowToast(Strings.AdSyncUpdatedToast);
                    }
                }
            }
            catch
            {
                // バックグラウンド同期の一時的ネットワーク例外等はサイレント処理
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

        private void SimAdRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            _ = CheckAndSyncAdPrincipalsAsync(forceRefresh: true);
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

        private void SimSourcePathTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                SimSourceLoadButton_Click(sender, e);
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
                EstimatedFileCount = src.FileCount,
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

        private void UpdateSimCloneButtonState()
        {
            if (SimCloneSelectedButton == null) return;
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;

            if (_selectedSimNode != null)
            {
                SimCloneSelectedButton.Content = Strings.CloneSelectedUnder(_selectedSimNode.Name);
                SimCloneSelectedButton.ToolTip = isJa
                    ? $"選択中の「{_selectedSimNode.Name}」の直下に、左で選んだフォルダをサブフォルダとして配置します（権限・階層自動引き継ぎ）"
                    : $"Places the selected folder on the left as a direct subfolder under '{_selectedSimNode.Name}' (inherits ACL and hierarchy)";
            }
            else
            {
                SimCloneSelectedButton.Content = Strings.CloneSelectedToCenter;
                SimCloneSelectedButton.ToolTip = isJa
                    ? "新環境ツリーの第1階層（ルート）として新規配置します"
                    : "Places the selected folder on the left as a new root folder";
            }
        }

        private void SimHoverExpandTimer_Tick(object? sender, EventArgs e)
        {
            _simHoverExpandTimer.Stop();
            if (_simHoverExpandCandidate != null && !_simHoverExpandCandidate.IsExpanded)
            {
                _simHoverExpandCandidate.IsExpanded = true;
            }
        }

        private void ClearSimDragState()
        {
            _simHoverExpandTimer.Stop();
            _simHoverExpandCandidate = null;
            if (_currentDragOverSimNode != null)
            {
                _currentDragOverSimNode.IsDragOverTarget = false;
                _currentDragOverSimNode = null;
            }
        }

        #region Undo Infrastructure (Tab 3: Migration Studio)
        private void PushUndoSnapshot()
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Serialize(_simRootFolders);
                if (_simUndoStack.Count >= MaxSimUndoDepth)
                {
                    var items = _simUndoStack.ToArray();
                    _simUndoStack.Clear();
                    for (int i = items.Length - 2; i >= 0; i--)
                    {
                        _simUndoStack.Push(items[i]);
                    }
                }
                _simUndoStack.Push(json);
                UpdateUndoButtonState();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PushUndoSnapshot error: {ex}");
            }
        }

        private void UpdateUndoButtonState()
        {
            if (SimUndoButton != null)
            {
                SimUndoButton.IsEnabled = _simUndoStack.Count > 0;
            }
        }

        private void PerformUndo()
        {
            if (_simUndoStack.Count == 0) return;

            try
            {
                var json = _simUndoStack.Pop();
                var restoredRoots = System.Text.Json.JsonSerializer.Deserialize<List<SimFolderNode>>(json);
                if (restoredRoots != null)
                {
                    _simRootFolders.Clear();
                    foreach (var root in restoredRoots)
                    {
                        SimulationProjectService.LinkParentsAndLevels(root, null, 0);
                        _simRootFolders.Add(root);
                    }

                    _selectedSimNode = null;
                    SimSelectedFolderNameText.Text = "(未選択)";
                    SimMappedSourcesItemsControl.ItemsSource = null;
                    SimAclCardsItemsControl.ItemsSource = null;
                    UpdateSimCloneButtonState();
                    UpdateUndoButtonState();

                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    ShowToast(isJa ? "↩️ 直前の変更を取り消しました" : "↩️ Undo: Reverted last tree change");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PerformUndo error: {ex}");
            }
        }

        private void SimUndoButton_Click(object sender, RoutedEventArgs e)
        {
            PerformUndo();
        }
        #endregion

        private void SimCloneSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (SimSourceTreeView.SelectedItem is FileItemNode selected)
            {
                PushUndoSnapshot();
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                if (_selectedSimNode != null)
                {
                    var newSub = CreateSimNodeFromSourceWithAcl(selected, _selectedSimNode, _selectedSimNode.Level + 1);
                    _selectedSimNode.Children.Add(newSub);
                    _selectedSimNode.IsExpanded = true;
                    ShowToast(isJa
                        ? $"📁 「{selected.Name}」を「{_selectedSimNode.Name}」直下にサブ配置しました"
                        : $"📁 Placed '{selected.Name}' as a subfolder under '{_selectedSimNode.Name}'");
                }
                else
                {
                    var simNode = CreateSimNodeFromSourceWithAcl(selected, null, 0);
                    _simRootFolders.Add(simNode);
                    ShowToast(isJa
                        ? $"📁 新環境ツリーにルート配置しました: {selected.Name}"
                        : $"📁 Placed as new root folder: {selected.Name}");
                }
            }
            else
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                MessageBox.Show(
                    isJa ? "移行元ツリーからフォルダを選択してください。" : "Please select a folder from the source tree first.",
                    isJa ? "情報" : "Information",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void SimAddRootFolderButton_Click(object sender, RoutedEventArgs e)
        {
            PushUndoSnapshot();
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
            UpdateSimCloneButtonState();
        }

        private void SimMockTreeView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _simDragStartPoint = e.GetPosition(null);
            _isSimNodeDragging = false;
            _simNodeDroppedInTree = false;
            _simDragCancelled = false;

            // ツリーのアイテム以外の余白をクリックした場合は選択解除（新設ルート配置へ復元）
            var dep = e.OriginalSource as DependencyObject;
            bool hitItem = false;
            while (dep != null && dep != SimMockTreeView)
            {
                if (dep is TreeViewItem)
                {
                    hitItem = true;
                    break;
                }
                dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
            }
            if (!hitItem && _selectedSimNode != null)
            {
                _selectedSimNode.IsSelected = false;
                _selectedSimNode = null;
                SimSelectedFolderNameText.Text = "(未選択)";
                SimMappedSourcesItemsControl.ItemsSource = null;
                SimAclCardsItemsControl.ItemsSource = null;
                UpdateSimCloneButtonState();
            }
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
                        // かつ Escキーによるキャンセルでもない場合、枠外ドロップによる解除として扱う
                        if (!_simNodeDroppedInTree && !_simDragCancelled)
                        {
                            Point endPoint = Mouse.GetPosition(SimMockTreeView);
                            bool isOutside = endPoint.X < 0 || endPoint.Y < 0 ||
                                             endPoint.X > SimMockTreeView.ActualWidth ||
                                             endPoint.Y > SimMockTreeView.ActualHeight;

                            if (isOutside || effects == DragDropEffects.None)
                            {
                                PushUndoSnapshot();
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
                                    ? $"🗑️ 「{node.Name}」をツリーから削除しました (Ctrl+Zで復元可能)"
                                    : $"🗑️ Deleted '{node.Name}' from tree (Undo with Ctrl+Z)");
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
                e.Effects = DragDropEffects.Move | DragDropEffects.Copy;
                e.Handled = true;

                // Auto-Scroll during drag near edges
                var scrollViewer = FindVisualParent<ScrollViewer>(SimMockTreeView);
                if (scrollViewer != null)
                {
                    Point pos = e.GetPosition(scrollViewer);
                    const double scrollThreshold = 25.0;
                    if (pos.Y < scrollThreshold)
                    {
                        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - 16);
                    }
                    else if (pos.Y > scrollViewer.ActualHeight - scrollThreshold)
                    {
                        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset + 16);
                    }
                }

                // Auto-expand on hover
                var targetNode = GetSimNodeFromDragEvent(e);
                if (targetNode != null)
                {
                    if (_currentDragOverSimNode != targetNode)
                    {
                        if (_currentDragOverSimNode != null) _currentDragOverSimNode.IsDragOverTarget = false;
                        _currentDragOverSimNode = targetNode;
                        _currentDragOverSimNode.IsDragOverTarget = true;
                    }

                    if (!targetNode.IsExpanded && targetNode.Children.Count > 0)
                    {
                        if (_simHoverExpandCandidate != targetNode)
                        {
                            _simHoverExpandCandidate = targetNode;
                            _simHoverExpandTimer.Stop();
                            _simHoverExpandTimer.Start();
                        }
                    }
                    else
                    {
                        _simHoverExpandTimer.Stop();
                        _simHoverExpandCandidate = null;
                    }
                }
                else
                {
                    ClearSimDragState();
                }
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
        }

        private void SimMockTreeView_DragLeave(object sender, DragEventArgs e)
        {
            ClearSimDragState();
        }

        private void SimMockTreeView_Drop(object sender, DragEventArgs e)
        {
            ClearSimDragState();
            var target = GetSimNodeFromDragEvent(e);

            // Case 1: Drop source folder from left explorer (Create subfolder with full ACL & hierarchy)
            if (e.Data.GetData("FolderMorpherSourceNode") is FileItemNode src)
            {
                PushUndoSnapshot();
                if (target != null)
                {
                    var newSub = CreateSimNodeFromSourceWithAcl(src, target, target.Level + 1);
                    target.Children.Add(newSub);
                    target.IsExpanded = true;
                    ShowToast($"📁 「{src.Name}」を「{target.Name}」配下にサブフォルダ化（権限・階層継承）しました");
                }
                else
                {
                    // 空白ドロップは選択状態に関係なく第1階層（新設ルート）として配置
                    var newRoot = CreateSimNodeFromSourceWithAcl(src, null, 0);
                    _simRootFolders.Add(newRoot);
                    ShowToast($"📁 「{src.Name}」を第1階層（ルート）として配置しました");
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

                    PushUndoSnapshot();
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

                        PushUndoSnapshot();
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
                    PushUndoSnapshot();
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

            PushUndoSnapshot();
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
                    PushUndoSnapshot();
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

                PushUndoSnapshot();
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
                    PushUndoSnapshot();
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
                PushUndoSnapshot();
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
                    PushUndoSnapshot();
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
                    PushUndoSnapshot();
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
                PushUndoSnapshot();
                _selectedSimNode.InheritAcl = SimInheritCheckBox.IsChecked == true;
            }
        }

        private void SimDeleteAclEntryButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.Tag is SimAclEntry acl && _selectedSimNode != null)
            {
                PushUndoSnapshot();
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

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T typed) return typed;
                var desc = FindVisualChild<T>(child);
                if (desc != null) return desc;
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
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            DiffSummaryStatsText.Text = string.IsNullOrWhiteSpace(targetRoot)
                ? (isJa ? $"📊 差分項目: {diffs.Count}件" : $"📊 Diff Items: {diffs.Count}")
                : (isJa ? $"📊 差分項目: {diffs.Count}件 (展開先: {targetRoot})" : $"📊 Diff Items: {diffs.Count} (Target: {targetRoot})");
            DiffModalOverlay.Visibility = Visibility.Visible;
        }

        private void DiffModalClose_Click(object sender, RoutedEventArgs e)
        {
            DiffModalOverlay.Visibility = Visibility.Collapsed;
        }

        private async void DiffModalApply_Click(object sender, RoutedEventArgs e)
        {
            var targetRoot = SimTargetRootTextBox.Text.Trim();
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                MessageBox.Show(isJa ? "移行先ルートフォルダを入力してください。" : "Please enter target root folder.",
                                isJa ? "通知" : "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Plan-First 貫通: プレビュー承認された同一の SkeletonDeployPlan インスタンスを直接コミット
            var plan = _currentSkeletonPlan ?? _simService.BuildDeployPlan(_simRootFolders, targetRoot, _currentTab?.RootNode);

            GlobalProgressBar.Visibility = Visibility.Visible;
            GlobalProgressBar.IsIndeterminate = true;

            var progress = new Progress<(string Status, int Count)>(p =>
            {
                StatusTextBlock.Text = isJa ? $"{p.Status} ({p.Count} 作成済)" : $"{p.Status} ({p.Count} created)";
            });

            try
            {
                using var cts = new CancellationTokenSource();
                var deployResult = await _simService.DeploySkeletonAsync(plan, progress, cts.Token);

                // Verify: 展開先ルートおよび新規作成された全フォルダーの実在検証 ＆ エラー件数照合
                bool allPathsExist = Directory.Exists(plan.DestinationRoot) &&
                                     deployResult.DeployedFolderPaths.All(p => Directory.Exists(p));
                bool isCleanSuccess = deployResult.FailedCount == 0 && deployResult.ConflictCount == 0 && allPathsExist;
                DiffModalOverlay.Visibility = Visibility.Collapsed;

                if (isCleanSuccess)
                {
                    string skippedMsg = deployResult.SkippedExistingCount > 0
                        ? (isJa ? $" ({deployResult.SkippedExistingCount} 既存保護)" : $" ({deployResult.SkippedExistingCount} existing protected)")
                        : "";
                    ShowToast(isJa
                        ? $"✅ スケルトン作成完了 (Plan-First全階層検証済): {deployResult.CreatedCount} フォルダ作成{skippedMsg}"
                        : $"✅ Skeleton deployment complete: {deployResult.CreatedCount} folders created{skippedMsg}");
                }
                else if (deployResult.ConflictCount > 0)
                {
                    ShowToast(isJa
                        ? $"⚠️ 外部変更を検知 ({deployResult.ConflictCount}件スキップ): {deployResult.CreatedCount} フォルダ作成。ツリーを再確認してください"
                        : $"⚠️ External change detected ({deployResult.ConflictCount} skipped): {deployResult.CreatedCount} folders created. Please review tree");
                }
                else if (allPathsExist && deployResult.FailedCount > 0)
                {
                    ShowToast(isJa
                        ? $"⚠️ スケルトン作成完了 (一部権限警告 {deployResult.FailedCount}件): {deployResult.CreatedCount} フォルダ作成"
                        : $"⚠️ Skeleton deployment complete (ACL warnings: {deployResult.FailedCount}): {deployResult.CreatedCount} folders created");
                }
                else
                {
                    ShowToast(isJa
                        ? $"⚠️ スケルトン作成警告: 一部のフォルダー実在を確認できませんでした (エラー: {deployResult.FailedCount}件)"
                        : $"⚠️ Skeleton deployment warning: Some folders could not be verified (Errors: {deployResult.FailedCount})");
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(isJa ? $"スケルトン作成失敗: {ex.Message}" : $"Skeleton deployment failed: {ex.Message}",
                                isJa ? "エラー" : "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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

                    ShellHelper.SelectInExplorer(dialog.FileName);
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

                        _simUndoStack.Clear();
                        UpdateUndoButtonState();
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
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                MessageBox.Show(isJa ? "移行先ルートフォルダを入力してください。" : "Please enter target root folder.",
                                isJa ? "通知" : "Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Plan-First: 実行計画 (SkeletonDeployPlan) を事前構築し、同一インスタンスをプレビュー・コミットで貫通
            _currentSkeletonPlan = _simService.BuildDeployPlan(_simRootFolders, targetRoot, _currentTab?.RootNode);
            DiffReviewDataGrid.ItemsSource = _currentSkeletonPlan.DiffReviews;
            DiffSummaryStatsText.Text = isJa
                ? $"📊 計画項目: 作成予定 {_currentSkeletonPlan.PlannedCreateCount}件 / 既存保護 {_currentSkeletonPlan.PlannedExistingCount}件 (展開先: {targetRoot})"
                : $"📊 Planned Items: {_currentSkeletonPlan.PlannedCreateCount} to create / {_currentSkeletonPlan.PlannedExistingCount} protected (Target: {targetRoot})";
            DiffModalOverlay.Visibility = Visibility.Visible;
        }

        #region Tab 3: Migration Package & Runbook Generation
        private List<MigrationWavePlan> _currentWavePlans = new();

        private void SimExportScriptsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_simRootFolders.Count == 0)
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                MessageBox.Show(
                    isJa ? "移行設計ツリーが空です。先に移行元フォルダーを配置してください。" : "Migration tree is empty. Please add source folders first.",
                    isJa ? "移行パッケージ" : "Migration Package",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            MigrationPackageOverlay.Visibility = Visibility.Visible;
            RefreshWavePlanPreview();
        }

        private void MigPkgModalClose_Click(object sender, RoutedEventArgs e)
        {
            MigrationPackageOverlay.Visibility = Visibility.Collapsed;
        }

        private void MigPolicyRadio_Checked(object sender, RoutedEventArgs e)
        {
            if (MigrationPackageOverlay != null && MigrationPackageOverlay.Visibility == Visibility.Visible)
            {
                RefreshWavePlanPreview();
            }
        }

        private void MigSizeBudgetText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (MigrationPackageOverlay != null && MigrationPackageOverlay.Visibility == Visibility.Visible)
            {
                RefreshWavePlanPreview();
            }
        }

        private void MigParamText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (MigrationPackageOverlay != null && MigrationPackageOverlay.Visibility == Visibility.Visible)
            {
                RefreshWavePlanPreview();
            }
        }

        private void RefreshWavePlanPreview()
        {
            if (_simRootFolders.Count == 0) return;

            var options = BuildCurrentMigrationOptions();
            var service = new MigrationPackageService();
            _currentWavePlans = service.PlanWaves(_simRootFolders, options);

            MigWavePlanDataGrid.ItemsSource = null;
            MigWavePlanDataGrid.ItemsSource = _currentWavePlans;

            // KPI 計算 (WavePlans の計算結果・正本プロパティを参照)
            long totalBytes = _currentWavePlans.Sum(w => w.TotalSizeBytes);
            bool hasFiles = _currentWavePlans.Any(w => w.TotalFileCount.HasValue);
            long totalFiles = _currentWavePlans.Sum(w => w.TotalFileCount ?? 0);
            var totalFullTime = TimeSpan.FromSeconds(_currentWavePlans.Sum(w => w.EstimatedFullCopyTime.TotalSeconds));
            var totalCutoverTime = TimeSpan.FromSeconds(_currentWavePlans.Sum(w => w.EstimatedCutoverTime.TotalSeconds));

            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            MigKpiTotalSizeVal.Text = FormatHelper.FormatBytes(totalBytes, 2);
            MigKpiTotalFilesVal.Text = hasFiles
                ? (isJa ? $"{totalFiles:N0} 件" : $"{totalFiles:N0} files")
                : (isJa ? "未計測 (-)" : "Unmeasured (-)");
            MigKpiFullTimeVal.Text = FormatTimeSpanForKpi(totalFullTime);
            MigKpiCutoverTimeVal.Text = FormatTimeSpanForKpi(totalCutoverTime);

            MigKpiFullTimeLabel.Text = isJa
                ? $"初回フル同期想定 ({options.TransferRateMBps:G0}MB/s)"
                : $"Est. Full Sync ({options.TransferRateMBps:G0}MB/s)";
            MigKpiCutoverTimeLabel.Text = isJa
                ? $"本番切替想定 (差分{options.DeltaRatioPercent:G0}%)"
                : $"Est. Cutover ({options.DeltaRatioPercent:G0}% Delta)";

            string targetRoot = string.IsNullOrWhiteSpace(options.TargetRoot) ? @"\\NewServer\Share" : options.TargetRoot;
            MigFooterNoticeText.Text = isJa
                ? $"※ 新環境ルート: {targetRoot} | 子孫フォルダ (/XD) は自動除外されます"
                : $"* Target Root: {targetRoot} | Descendant folders are excluded via /XD";
        }

        private MigrationPackageOptions BuildCurrentMigrationOptions()
        {
            var policy = MigrationSplitPolicy.ByTopLevelFolder;
            if (MigPolicySizeBudgetRadio.IsChecked == true)
            {
                policy = MigrationSplitPolicy.BySizeBudget;
            }
            else if (MigPolicySingleBatchRadio.IsChecked == true)
            {
                policy = MigrationSplitPolicy.SingleBatch;
            }

            long budgetGb = 500;
            if (long.TryParse(MigSizeBudgetText.Text.Trim(), out var parsedGb) && parsedGb > 0)
            {
                budgetGb = parsedGb;
            }

            double speedMBps = 80.0;
            if (MigSpeedText != null && double.TryParse(MigSpeedText.Text.Trim(), out var parsedSpeed) && parsedSpeed > 0)
            {
                speedMBps = parsedSpeed;
            }

            double deltaPercent = 2.0;
            if (MigDeltaRatioText != null && double.TryParse(MigDeltaRatioText.Text.Trim(), out var parsedDelta) && parsedDelta >= 0)
            {
                deltaPercent = parsedDelta;
            }

            return new MigrationPackageOptions
            {
                Policy = policy,
                SizeBudgetBytes = budgetGb * 1024L * 1024 * 1024,
                TransferRateMBps = speedMBps,
                DeltaRatioPercent = deltaPercent,
                TargetRoot = SimTargetRootTextBox.Text.Trim(),
                CopyAcl = (MigModeCopyAllRadio.IsChecked == true),
                IncludeRunbookExcel = (MigIncludeExcelCheck.IsChecked == true),
                IncludeOldShareLock = (MigIncludeLockCheck.IsChecked == true),
                Threads = 16
            };
        }

        private static string FormatTimeSpanForKpi(TimeSpan ts)
        {
            if (ts.TotalDays >= 1.0)
            {
                return $"{(int)ts.TotalDays}d {ts.Hours}h {ts.Minutes}m";
            }
            if (ts.TotalHours >= 1.0)
            {
                return $"{(int)ts.TotalHours}h {ts.Minutes}m";
            }
            if (ts.TotalMinutes >= 1.0)
            {
                return $"{(int)ts.TotalMinutes}m {ts.Seconds}s";
            }
            return $"{Math.Max(1, (int)ts.TotalSeconds)}s";
        }

        private async void MigPkgExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_simRootFolders.Count == 0) return;

            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            var dialog = new OpenFolderDialog
            {
                Title = isJa ? "移行パッケージの出力先フォルダーを選択" : "Select Destination Folder for Migration Package"
            };

            if (dialog.ShowDialog() != true) return;

            var options = BuildCurrentMigrationOptions();
            options.OutputDirectory = dialog.FolderName;

            MigPkgExportButton.IsEnabled = false;
            MigPkgCancelButton.IsEnabled = false;
            var origContent = MigPkgExportButton.Content;
            MigPkgExportButton.Content = isJa ? "⏳ 生成中..." : "⏳ Generating...";

            try
            {
                var service = new MigrationPackageService();
                var progress = new Progress<string>(msg => StatusTextBlock.Text = msg);
                string packageDir = await service.GeneratePackageAsync(_simRootFolders, options, progress);

                MigrationPackageOverlay.Visibility = Visibility.Collapsed;
                ShowToast(isJa ? "移行パッケージ一式を出力しました" : "Migration package generated successfully");

                // エクスプローラーで出力先フォルダを開く
                ShellHelper.OpenFolder(packageDir);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    (isJa ? "移行パッケージ生成中にエラーが発生しました:\n\n" : "Error generating migration package:\n\n") + ex.Message,
                    isJa ? "生成エラー" : "Generation Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                MigPkgExportButton.IsEnabled = true;
                MigPkgCancelButton.IsEnabled = true;
                MigPkgExportButton.Content = origContent;
                StatusTextBlock.Text = Strings.Ready;
            }
        }
        #endregion

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

                    ShellHelper.SelectInExplorer(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"出力エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        #endregion
    }
}