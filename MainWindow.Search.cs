using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
    public partial class MainWindow
    {
        private CancellationTokenSource? _searchCts;
        private DispatcherTimer? _searchDebounceTimer;
        private readonly ObservableCollection<SearchResultItem> _searchResults = new();
        public ObservableCollection<SearchResultItem> SearchResults => _searchResults;
        private List<SearchResultItem> _allSearchResults = new();

        private string _selectedSortType = "Relevance";

        private long _searchGeneration = 0;

        public void InitializeSearchStudio()
        {
            if (SearchListView != null)
            {
                SearchListView.ItemsSource = _searchResults;
            }

            _searchDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _searchDebounceTimer.Tick += (s, e) =>
            {
                _searchDebounceTimer.Stop();
                ExecuteSearch(isIncremental: true);
            };

            if (SearchIncludeFoldersCheckBox != null)
            {
                SearchIncludeFoldersCheckBox.Checked += (s, e) => ExecuteSearch(isIncremental: false);
                SearchIncludeFoldersCheckBox.Unchecked += (s, e) => ExecuteSearch(isIncremental: false);
            }

            // ★ 本文検索トグル: チェック変更時に勝手に重い検索を走らせず、Enterキーまたは検索実行操作時に反映する
            // （チェックを入れた途端に検索が走り出す不快感を解消）
        }

        private void SearchRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // 現在の入力条件で検索を再実行（最新化）
            ExecuteSearch(isIncremental: false);
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            ShowToast(isJa ? "🔄 検索結果を最新化しました" : "🔄 Refreshed search results");
        }

        #region Search Input & Debounce

        private void SearchInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // インクリメンタル即時検索（タイピング中の自動デバウンス検索）
            _searchDebounceTimer?.Stop();
            _searchDebounceTimer?.Start();
        }

        private void SearchInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                _searchDebounceTimer?.Stop();
                ExecuteSearch(isIncremental: false);
            }
        }

        private void SearchExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            _searchDebounceTimer?.Stop();
            ExecuteSearch(isIncremental: false);
        }

        private void SearchCancelButton_Click(object sender, RoutedEventArgs e)
        {
            _searchCts?.Cancel();
        }

        private void SearchClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (SearchInputBox != null)
            {
                SearchInputBox.Text = string.Empty;
                SearchInputBox.Focus();
            }
            _searchResults.Clear();
            _allSearchResults.Clear();
            _selectedSortType = "Relevance";
            if (SearchSortComboBox != null) SearchSortComboBox.SelectedIndex = 0;
            UpdateSearchKpi(0, 0, TimeSpan.Zero);
        }


        private void SearchDirectBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                    ? "走査対象のフォルダーを選択"
                    : "Select Target Folder to Search"
            };
            if (dlg.ShowDialog() == true)
            {
                if (SearchDirectTargetTextBox != null)
                {
                    SearchDirectTargetTextBox.Text = dlg.FolderName;
                }
            }
        }

        private static async Task<List<FolderMorpher.Contracts.SearchResultDto>> RunLiveSearchJobAsync(
            string targetFolder,
            SearchQuery query,
            IProgress<FolderMorpher.Contracts.SearchProgressDto> progress,
            CancellationToken ct)
        {
            var started = Stopwatch.StartNew();
            var status = await FolderMorpher.HostClient.HostJobClient.RunAsync(
                new FolderMorpher.Contracts.HostJobRequestDto
                {
                    Kind = FolderMorpher.Contracts.HostJobKind.Search,
                    TargetPath = targetFolder,
                    SearchQuery = FolderMorpher.HostClient.SearchDtoMapper.ToDto(query)
                },
                job => progress.Report(new FolderMorpher.Contracts.SearchProgressDto
                {
                    CurrentPath = job.ProgressText,
                    HitCount = job.HitCount,
                    TotalHitBytes = job.TotalHitBytes,
                    Elapsed = started.Elapsed,
                    IsCompleted = job.State == FolderMorpher.Contracts.HostJobState.Completed
                }),
                ct);
            return status.SearchResults ?? throw new InvalidOperationException("検索結果がHostから返されませんでした。");
        }

        #endregion

        #region Search Execution Core (Smart Auto-Routing)

        private async void ExecuteSearch(bool isIncremental)
        {
            string rawQuery = SearchInputBox?.Text?.Trim() ?? string.Empty;
            var query = SearchQueryParser.Parse(rawQuery);

            if (SearchIncludeFoldersCheckBox?.IsChecked == true)
            {
                query.IncludeFolders = true;
            }

            if (SearchContentCheckBox?.IsChecked == true)
            {
                query.SearchContentMode = true;
            }

            // 本文検索（I/Oを伴う探索）はタイピング途中のインクリメンタル実行をスキップし、Enterまたはボタン押下で実行
            if (isIncremental && query.HasDeepFileIoRequirement)
            {
                return;
            }

            string targetFolder = SearchDirectTargetTextBox?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                // スキャン済みタブのルートをフォールバックとして取得
                var selectedTab = StorageTabs?.FirstOrDefault(t => t.IsSelected);
                if (selectedTab?.RootNode != null && !string.IsNullOrWhiteSpace(selectedTab.RootNode.FullPath))
                {
                    targetFolder = selectedTab.RootNode.FullPath;
                }
            }

            // ターゲットもスキャン済みツリーもない場合
            bool hasTarget = !string.IsNullOrWhiteSpace(targetFolder);
            var scopedRoots = GetTargetScannedRootNodes(hasTarget ? targetFolder : null);
            bool hasScannedTree = scopedRoots.Count > 0;

            if (!hasTarget && !hasScannedTree)
            {
                if (!isIncremental)
                {
                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    MessageBox.Show(
                        isJa ? "走査対象のフォルダーまたはUNCパスを入力してください。" : "Please specify a target folder or UNC path to search.",
                        isJa ? "検索エラー" : "Search Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                return;
            }

            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;
            long currentGen = Interlocked.Increment(ref _searchGeneration);

            SetSearchLoadingState(true);

            var progress = new Progress<FolderMorpher.Contracts.SearchProgressDto>(r =>
            {
                if (currentGen != Volatile.Read(ref _searchGeneration)) return;
                UpdateSearchKpi(r.HitCount, r.TotalHitBytes, r.Elapsed);
                if (SearchStatusText != null && !string.IsNullOrEmpty(r.CurrentPath))
                {
                    SearchStatusText.Text = r.CurrentPath;
                }
            });

            var lastBatchUpdate = Stopwatch.StartNew();
            var searchTotalSw = Stopwatch.StartNew();
            var batchYield = new Progress<IReadOnlyList<SearchResultItem>>(items =>
            {
                if (currentGen != Volatile.Read(ref _searchGeneration)) return;

                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                var existingMap = new Dictionary<string, SearchResultItem>(_allSearchResults.Count, StringComparer.OrdinalIgnoreCase);
                foreach (var existing in _allSearchResults)
                {
                    existingMap[existing.FullPath] = existing;
                }

                bool addedOrUpdated = false;
                foreach (var item in items)
                {
                    if (existingMap.TryGetValue(item.FullPath, out var existing))
                    {
                        if (string.IsNullOrEmpty(existing.ContentSnippet) && !string.IsNullOrEmpty(item.ContentSnippet))
                        {
                            existing.ContentSnippet = item.ContentSnippet;
                            existing.MatchedReason = item.MatchedReason;
                            addedOrUpdated = true;
                        }
                    }
                    else
                    {
                        existingMap[item.FullPath] = item;
                        _allSearchResults.Add(item);
                        addedOrUpdated = true;
                    }
                }

                if (addedOrUpdated && (lastBatchUpdate.ElapsedMilliseconds > 150 || _allSearchResults.Count <= 20))
                {
                    lastBatchUpdate.Restart();
                    ApplyFilterAndSort();
                    long totalBytes = _searchResults.Sum(h => h.SizeBytes);
                    UpdateSearchKpi(_searchResults.Count, totalBytes, searchTotalSw.Elapsed);
                    if (SearchStatusText != null && query.SearchContentMode)
                    {
                        SearchStatusText.Text = isJa
                            ? $"🔍 ヒット検出中: {_searchResults.Count:N0} 件 ―― 📄 本文を走査中..."
                            : $"🔍 Discovering matches: {_searchResults.Count:N0} hits ―― 📄 Scanning content...";
                    }
                }
            });

            try
            {
                _searchResults.Clear();
                _allSearchResults.Clear();

                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                var sw = Stopwatch.StartNew();

                // ⚡ スマートルーティング判定:
                // スキャン済みツリー（またはディスク/共有のJSONキャッシュ）が存在するか？
                if (!hasScannedTree && hasTarget)
                {
                    try
                    {
                        var cacheHost = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync(ct);
                        hasScannedTree = await cacheHost.HasCachedTreeAsync(targetFolder);
                    }
                    catch { }
                }

                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync(ct);

                if (hasScannedTree)
                {
                    // 🚀 ルート1: スキャン済みツリー対象 (0秒インメモリ検索 ＋ 本文ストリーミング)
                    // Step 1: まずツリーからファイル名・属性一致を超高速（0秒インメモリ）で先行表示！
                    var treeResults = new List<SearchResultItem>();
                    if (string.IsNullOrEmpty(query.ContentKeyword))
                    {
                        var nameOnlyQuery = query.Clone();
                        nameOnlyQuery.SearchContentMode = false;
                        nameOnlyQuery.ContentKeyword = string.Empty;

                        treeResults = (await host.SearchInMemoryAsync(targetFolder,
                            FolderMorpher.HostClient.SearchDtoMapper.ToDto(nameOnlyQuery), null, ct))
                            .Select(FolderMorpher.HostClient.SearchDtoMapper.ToViewItem).ToList();
                        if (currentGen == Volatile.Read(ref _searchGeneration))
                        {
                            _allSearchResults = treeResults.ToList();
                            ApplyFilterAndSort();
                            long totalBytes = _searchResults.Sum(h => h.SizeBytes);
                            UpdateSearchKpi(_searchResults.Count, totalBytes, sw.Elapsed);

                            if (query.SearchContentMode && SearchStatusText != null)
                            {
                                SearchStatusText.Text = isJa
                                    ? $"⚡ ツリーから即時表示: {_searchResults.Count:N0} 件 ({sw.ElapsedMilliseconds} ms) ―― 📄 本文を走査中..."
                                    : $"⚡ Instant tree matches: {_searchResults.Count:N0} hits ({sw.ElapsedMilliseconds} ms) ―― 📄 Searching content...";
                            }
                        }
                    }

                    if (query.SearchContentMode && hasTarget)
                    {
                        // Step 2: 本文検索がONの場合は、ライブ直接走査をバックグラウンド実行して本文ヒットを合流！
                        var liveHits = (await RunLiveSearchJobAsync(targetFolder, query, progress, ct))
                            .Select(FolderMorpher.HostClient.SearchDtoMapper.ToViewItem).ToList();
                        if (currentGen == Volatile.Read(ref _searchGeneration))
                        {
                            // treeResults と liveHits をマージ（同一パスならスニペットありを優先）
                            var mergedMap = new Dictionary<string, SearchResultItem>(StringComparer.OrdinalIgnoreCase);
                            foreach (var item in treeResults) mergedMap[item.FullPath] = item;
                            foreach (var item in liveHits)
                            {
                                if (mergedMap.TryGetValue(item.FullPath, out var existing))
                                {
                                    if (string.IsNullOrEmpty(existing.ContentSnippet) && !string.IsNullOrEmpty(item.ContentSnippet))
                                    {
                                        mergedMap[item.FullPath] = item;
                                    }
                                }
                                else
                                {
                                    mergedMap[item.FullPath] = item;
                                }
                            }

                            _allSearchResults = mergedMap.Values.ToList();
                            ApplyFilterAndSort();
                            long totalBytes = _searchResults.Sum(h => h.SizeBytes);
                            UpdateSearchKpi(_searchResults.Count, totalBytes, sw.Elapsed);

                            if (SearchStatusText != null)
                            {
                                SearchStatusText.Text = isJa
                                    ? $"🔍 全文走査完了: {_allSearchResults.Count:N0} 件ヒット ({sw.ElapsedMilliseconds} ms)"
                                    : $"🔍 Full content scan complete: {_allSearchResults.Count:N0} hits ({sw.ElapsedMilliseconds} ms)";
                            }
                        }
                    }
                    else if (!query.SearchContentMode && currentGen == Volatile.Read(ref _searchGeneration))
                    {
                        if (SearchStatusText != null)
                        {
                            SearchStatusText.Text = isJa
                                ? $"⚡ 0秒インメモリ検索完了: {_searchResults.Count:N0} 件ヒット ({sw.ElapsedMilliseconds} ms)"
                                : $"⚡ Instant in-memory search complete: {_searchResults.Count:N0} hits ({sw.ElapsedMilliseconds} ms)";
                        }
                    }
                }
                else
                {
                    // 🔍 ルート2: ライブ直接走査 (未スキャンUNC / 初見フォルダー / Agent Ransack 流 Channel パイプライン)
                    if (SearchStatusText != null && currentGen == Volatile.Read(ref _searchGeneration))
                    {
                        SearchStatusText.Text = isJa
                            ? (query.SearchContentMode ? "🔍 ライブ走査中 (ファイル名即時表示 ＆ 本文検索)..." : "🔍 ライブ走査を実行中...")
                            : (query.SearchContentMode ? "🔍 Live scanning (instant name hits & content search)..." : "🔍 Running live direct search...");
                    }

                    var results = (await RunLiveSearchJobAsync(targetFolder, query, progress, ct))
                        .Select(FolderMorpher.HostClient.SearchDtoMapper.ToViewItem).ToList();
                    if (currentGen == Volatile.Read(ref _searchGeneration))
                    {
                        _allSearchResults = results;
                        ApplyFilterAndSort();
                        long totalBytes = _searchResults.Sum(h => h.SizeBytes);
                        UpdateSearchKpi(_searchResults.Count, totalBytes, sw.Elapsed);

                        if (SearchStatusText != null)
                        {
                            SearchStatusText.Text = isJa
                                ? $"🔍 ライブ走査完了: {_searchResults.Count:N0} 件ヒット ({sw.ElapsedMilliseconds} ms)"
                                : $"🔍 Live direct search complete: {_searchResults.Count:N0} hits ({sw.ElapsedMilliseconds} ms)";
                        }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                if (currentGen == Volatile.Read(ref _searchGeneration))
                {
                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    if (SearchStatusText != null) SearchStatusText.Text = isJa ? "検索を中断しました。" : "Search canceled.";
                }
            }
            catch (Exception ex)
            {
                if (currentGen == Volatile.Read(ref _searchGeneration))
                {
                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    MessageBox.Show(
                        (isJa ? "検索中にエラーが発生しました: " : "Error occurred during search: ") + ex.Message,
                        isJa ? "検索エラー" : "Search Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            finally
            {
                if (currentGen == Volatile.Read(ref _searchGeneration))
                {
                    SetSearchLoadingState(false);
                }
            }
        }

        private List<FileItemNode> GetTargetScannedRootNodes(string? targetFolder)
        {
            var roots = new List<FileItemNode>();
            if (StorageTabs == null) return roots;

            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                foreach (var tab in StorageTabs)
                {
                    if (tab.RootNode != null)
                    {
                        roots.Add(tab.RootNode);
                    }
                }
                return roots;
            }

            string canonTarget = PathCanonicalizer.Normalize(targetFolder);
            foreach (var tab in StorageTabs)
            {
                if (tab.RootNode == null) continue;

                var matched = FindNodeByPathRecursive(tab.RootNode, canonTarget);
                if (matched != null)
                {
                    roots.Add(matched);
                    return roots;
                }
            }

            return roots;
        }

        private static FileItemNode? FindNodeByPathRecursive(FileItemNode current, string canonTargetPath)
        {
            string canonCurrent = PathCanonicalizer.Normalize(current.FullPath);
            if (string.Equals(canonCurrent, canonTargetPath, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            // 配下にない場合は探索を枝刈り
            if (!canonTargetPath.StartsWith(canonCurrent + "\\", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            foreach (var child in current.Children)
            {
                if (child.IsDirectory)
                {
                    var found = FindNodeByPathRecursive(child, canonTargetPath);
                    if (found != null) return found;
                }
            }

            return null;
        }

        private void SetSearchLoadingState(bool isLoading)
        {
            if (SearchProgressBar != null)
            {
                SearchProgressBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            }
            if (SearchCancelButton != null)
            {
                SearchCancelButton.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            }
            if (SearchExecuteButton != null)
            {
                SearchExecuteButton.IsEnabled = !isLoading;
            }
        }

        private void UpdateSearchKpi(int hitCount, long totalBytes, TimeSpan elapsed)
        {
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            if (SearchKpiHitCountText != null)
            {
                SearchKpiHitCountText.Text = isJa ? $"{hitCount:N0} 件" : $"{hitCount:N0} items";
            }
            if (SearchKpiTotalSizeText != null)
            {
                SearchKpiTotalSizeText.Text = FormatHelper.FormatBytes(totalBytes, 2);
            }
            if (SearchKpiElapsedText != null)
            {
                SearchKpiElapsedText.Text = $"{elapsed.TotalSeconds:F2}s";
            }
        }

        #endregion

        #region Context Menu & Hub Actions (他スタジオへの連携転送)

        private SearchResultItem? GetSelectedSearchItem()
        {
            return SearchListView?.SelectedItem as SearchResultItem;
        }

        private void SearchListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            SearchContextMenu_Open_Click(sender, e);
        }

        private void SearchItemJumpFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is SearchResultItem item && !string.IsNullOrWhiteSpace(item.FullPath))
            {
                ShellHelper.SelectInExplorer(item.FullPath);
            }
        }

        private void SearchContextMenu_Open_Click(object sender, RoutedEventArgs e)

        {
            var item = GetSelectedSearchItem();
            if (item != null)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(item.FullPath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show("ファイルを開けませんでした: " + ex.Message);
                }
            }
        }

        private void SearchContextMenu_Explore_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedSearchItem();
            if (item != null)
            {
                ShellHelper.SelectInExplorer(item.FullPath);
            }
        }

        private void SearchContextMenu_CopyPath_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedSearchItem();
            if (item != null)
            {
                Clipboard.SetText(item.FullPath);
                ShowToast("クリップボードにパスをコピーしました");
            }
        }

        private void SearchContextMenu_CopyName_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedSearchItem();
            if (item != null)
            {
                Clipboard.SetText(item.Name);
                ShowToast("クリップボードにファイル名をコピーしました");
            }
        }

        private void SearchContextMenu_LiveAcl_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedSearchItem();
            if (item == null) return;

            string targetDir = item.IsDirectory ? item.FullPath : item.DirectoryPath;
            if (string.IsNullOrEmpty(targetDir)) return;

            // Tab 2 (Live ACL) へジャンプ
            NavTabLiveAcl.IsChecked = true;

            // LiveAclStudioControl にパスを渡して開く
            if (LiveAclStudioControl != null)
            {
                LiveAclStudioControl.OpenFolder(targetDir);
            }
        }

        private async void SearchContextMenu_Simulation_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedSearchItem();
            if (item == null) return;

            string targetDir = item.IsDirectory ? item.FullPath : item.DirectoryPath;
            if (string.IsNullOrEmpty(targetDir)) return;

            // スキャン済みツリーから対応するフォルダーノードを探索
            FileItemNode? matchedNode = null;
            if (StorageTabs != null)
            {
                string norm = targetDir.TrimEnd('\\', '/');
                foreach (var tab in StorageTabs)
                {
                    if (tab.RootNode != null)
                    {
                        matchedNode = FindNodeByPathRecursive(tab.RootNode, norm);
                        if (matchedNode != null) break;
                    }
                }
            }

            SimFolderNode newNode;
            if (matchedNode != null)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    var dto = await host.ConvertStorageToMigrationAsync(
                        FolderMorpher.HostClient.StorageNodeMapper.ToTreeDto(matchedNode), CancellationToken.None);
                    newNode = FolderMorpher.HostClient.MigrationDtoMapper.ToViewNode(dto);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"移行ツリーへの追加に失敗しました: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
            }
            else
            {
                // スキャン外フォルダー: ファイル単体のサイズをフォルダー全体サイズに誤認させない安全設計
                newNode = new SimFolderNode
                {
                    Name = Path.GetFileName(targetDir),
                    EstimatedSizeBytes = item.IsDirectory ? item.SizeBytes : 0,
                    EstimatedFileCount = null,
                    Level = 0,
                    InheritAcl = true
                };
                newNode.MappedSourcePaths.Add(targetDir);
            }

            // Tab 3 (Simulation) へジャンプ
            NavTabSimulation.IsChecked = true;
            _simRootFolders.Add(newNode);
            if (SimMockTreeView != null) SimMockTreeView.ItemsSource = _simRootFolders;
            ShowToast($"移行ツリーに「{newNode.Name}」を追加しました");
        }

        private void SearchContextMenu_LinkFix_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedSearchItem();
            if (item == null) return;

            string targetDir = item.IsDirectory ? item.FullPath : item.DirectoryPath;
            if (string.IsNullOrEmpty(targetDir)) return;

            // Tab 4 (LinkFix) へジャンプ
            NavTabLinkFix.IsChecked = true;
            if (LinkSearchScopeTextBox != null)
            {
                LinkSearchScopeTextBox.Text = targetDir;
            }
            ShowToast("リンク修復対象パスを設定しました");
        }

        private void SearchContextMenu_Audit_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedSearchItem();
            if (item == null) return;

            string targetDir = item.IsDirectory ? item.FullPath : item.DirectoryPath;
            if (string.IsNullOrEmpty(targetDir)) return;

            // Tab 5 (Audit) へジャンプ
            NavTabAudit.IsChecked = true;
            if (AuditPathTextBox != null)
            {
                AuditPathTextBox.Text = targetDir;
            }
            ShowToast("ファイル整理・監査対象パスを設定しました");
        }

        #endregion

        #region Export (Excel / CSV)

        private async void SearchExportExcelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_searchResults.Count == 0)
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                MessageBox.Show(
                    isJa ? "エクスポートする検索結果がありません。" : "No search results to export.",
                    isJa ? "情報" : "Information",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = $"Search_Report_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    await host.ExportSearchResultsAsync(dlg.FileName,
                        _searchResults.Select(FolderMorpher.HostClient.SearchDtoMapper.ToDto).ToList(), CancellationToken.None);
                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    ShowToast(isJa ? "Excel台帳を出力しました" : "Exported Excel report");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Excel出力中にエラーが発生しました: " + ex.Message);
                }
            }
        }

        private async void SearchExportCsvButton_Click(object sender, RoutedEventArgs e)
        {
            if (_searchResults.Count == 0)
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                MessageBox.Show(
                    isJa ? "エクスポートする検索結果がありません。" : "No search results to export.",
                    isJa ? "情報" : "Information",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Filter = "CSV UTF-8 (*.csv)|*.csv",
                FileName = $"Search_Report_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    await host.ExportSearchResultsAsync(dlg.FileName,
                        _searchResults.Select(FolderMorpher.HostClient.SearchDtoMapper.ToDto).ToList(), CancellationToken.None);
                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    ShowToast(isJa ? "CSVを出力しました" : "Exported CSV");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("CSV出力中にエラーが発生しました: " + ex.Message);
                }
            }
        }

        #endregion

        #region Sort

        private static int CalculateRelevanceScore(SearchResultItem item, string rawQuery)
        {
            if (string.IsNullOrWhiteSpace(rawQuery)) return 0;
            int score = 0;

            // SearchQueryParser を通して構文トークン (ext:, size:, !等) を除外し、純粋なキーワードとフレーズのみを抽出
            var parsed = SearchQueryParser.Parse(rawQuery);
            var keywords = parsed.Keywords.Concat(parsed.ExactPhrases)
                                          .Where(k => !string.IsNullOrWhiteSpace(k))
                                          .Select(k => k.Trim().Trim('"', '*'))
                                          .Distinct(StringComparer.OrdinalIgnoreCase)
                                          .ToList();

            if (keywords.Count == 0) return 0;

            string name = item.Name ?? string.Empty;
            string nameWithoutExt = Path.GetFileNameWithoutExtension(name);
            string dir = item.DirectoryPath ?? string.Empty;
            string snippet = item.ContentSnippet ?? string.Empty;

            foreach (var kw in keywords)
            {
                // 1. ファイル名完全一致（拡張子除く、または拡張子含む）
                if (string.Equals(name, kw, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(nameWithoutExt, kw, StringComparison.OrdinalIgnoreCase))
                {
                    score += 100;
                }
                // 2. ファイル名前方一致
                else if (name.StartsWith(kw, StringComparison.OrdinalIgnoreCase) ||
                         nameWithoutExt.StartsWith(kw, StringComparison.OrdinalIgnoreCase))
                {
                    score += 50;
                }
                // 3. ファイル名部分一致
                else if (name.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 30;
                }

                // 4. ディレクトリパスに含まれる
                if (dir.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 10;
                }

                // 5. 本文スニペットに含まれる
                if (!string.IsNullOrEmpty(snippet) && snippet.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    score += 15;
                }
            }

            // 6. 鮮度加点（直近に更新されたファイルほど優先）
            var age = DateTime.Now - item.LastWriteTime;
            if (age.TotalDays <= 7) score += 5;
            else if (age.TotalDays <= 30) score += 3;
            else if (age.TotalDays <= 365) score += 1;

            return score;
        }

        private void SearchSortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SearchSortComboBox?.SelectedItem is ComboBoxItem item && item.Tag is string tag)
            {
                _selectedSortType = tag;
                ApplyFilterAndSort();
            }
        }

        private void ApplyFilterAndSort()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ApplyFilterAndSort);
                return;
            }

            IEnumerable<SearchResultItem> filtered = _allSearchResults;
            string currentQuery = SearchInputBox?.Text?.Trim() ?? string.Empty;

            filtered = _selectedSortType switch
            {
                "DateDesc" => filtered.OrderByDescending(x => x.LastWriteTime),
                "DateAsc" => filtered.OrderBy(x => x.LastWriteTime),
                "CreatedDesc" => filtered.OrderByDescending(x => x.CreationTime),
                "CreatedAsc" => filtered.OrderBy(x => x.CreationTime),
                "Relevance" => filtered.OrderByDescending(x => CalculateRelevanceScore(x, currentQuery)).ThenByDescending(x => x.LastWriteTime),
                _ => filtered.OrderByDescending(x => CalculateRelevanceScore(x, currentQuery)).ThenByDescending(x => x.LastWriteTime)
            };

            var list = filtered.ToList();

            _searchResults.Clear();
            foreach (var item in list)
            {
                _searchResults.Add(item);
            }

            // メトリクスバーの更新（件数・合計サイズ）
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            if (SearchKpiHitCountText != null)
            {
                SearchKpiHitCountText.Text = isJa ? $"{list.Count:N0} 件" : $"{list.Count:N0} items";
            }

            if (SearchKpiTotalSizeText != null)
            {
                long totalBytes = list.Sum(x => x.SizeBytes);
                SearchKpiTotalSizeText.Text = FormatHelper.FormatBytes(totalBytes);
            }
        }



        #endregion
    }
}

