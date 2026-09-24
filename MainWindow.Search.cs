using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using AstraSize.Models;
using AstraSize.Services;
using ClosedXML.Excel;
using FolderMorpher.Models;
using FolderMorpher.Services;
using Microsoft.Win32;

namespace AstraSize
{
    public partial class MainWindow
    {
        private readonly SearchEngineService _searchEngine = new();
        private readonly ContentIndexService _contentIndex = new();
        private ContentIndexWatcherService? _indexWatcher;
        private CancellationTokenSource? _searchCts;
        private DispatcherTimer? _searchDebounceTimer;
        private readonly ObservableCollection<SearchResultItem> _searchResults = new();
        public ObservableCollection<SearchResultItem> SearchResults => _searchResults;
        private List<SearchResultItem> _allSearchResults = new();

        private string _selectedSortType = "Relevance";

        private long _searchGeneration = 0;

        public void InitializeSearchStudio()
        {
            _indexWatcher = new ContentIndexWatcherService(_contentIndex);
            _indexWatcher.ChangesApplied += OnWatcherChangesApplied;

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

        private int _isBackgroundIndexing = 0;

        private void TriggerBackgroundIndexUpdate(string? folderPath, bool force = false)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

            // クールダウン判定（自動同期時は前回の同期から15分経過していない場合はスキップしてサーバー負荷抑制）
            if (!force && !_contentIndex.NeedsBackgroundSync(folderPath, TimeSpan.FromMinutes(15)))
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _isBackgroundIndexing, 1, 0) != 0)
            {
                if (force)
                {
                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    ShowToast(isJa ? "⏳ 現在インデックス同期を実行中です..." : "⏳ Index synchronization is currently in progress...");
                }
                return;
            }

            if (force)
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                ShowToast(isJa ? "⚡ インデックス差分同期を開始しました..." : "⚡ Started index synchronization...");
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    var report = await _contentIndex.IndexFolderAsync(folderPath, progress: null, CancellationToken.None);
                    if (report.NewlyIndexedCount > 0 || report.DeletedCount > 0)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                            ShowToast(isJa
                                ? $"⚡ インデックスを最新化しました ({report.NewlyIndexedCount + report.DeletedCount:N0}件の変更)"
                                : $"⚡ Index updated ({report.NewlyIndexedCount + report.DeletedCount:N0} changes)");

                            // ★ 自動再検索: ユーザーが検索窓を開いたままであれば、最新インデックスから自動リフレッシュ
                            string currentFolder = SearchDirectTargetTextBox?.Text?.Trim() ?? string.Empty;
                            if (string.IsNullOrWhiteSpace(currentFolder))
                            {
                                var selectedTab = StorageTabs?.FirstOrDefault(t => t.IsSelected);
                                if (selectedTab?.RootNode != null) currentFolder = selectedTab.RootNode.FullPath;
                            }

                            if (string.Equals(currentFolder, folderPath, StringComparison.OrdinalIgnoreCase))
                            {
                                if (!string.IsNullOrWhiteSpace(SearchInputBox?.Text))
                                {
                                    ExecuteSearch(isIncremental: false, isAutoRefresh: true);
                                }
                            }
                        });
                    }
                    else if (force)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                            ShowToast(isJa ? "インデックスは既に最新です (差分なし)" : "Index is already up-to-date (no changes)");
                        });
                    }
                }
                catch
                {
                    // バックグラウンドインデックス失敗はサイレントに処理
                }
                finally
                {
                    Interlocked.Exchange(ref _isBackgroundIndexing, 0);
                }
            });
        }

        private void SearchSyncIndexButton_Click(object sender, RoutedEventArgs e)
        {
            string targetFolder = SearchDirectTargetTextBox?.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                var selectedTab = StorageTabs?.FirstOrDefault(t => t.IsSelected);
                if (selectedTab?.RootNode != null && !string.IsNullOrWhiteSpace(selectedTab.RootNode.FullPath))
                {
                    targetFolder = selectedTab.RootNode.FullPath;
                }
            }

            if (string.IsNullOrWhiteSpace(targetFolder) || !Directory.Exists(targetFolder))
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                MessageBox.Show(
                    isJa ? "同期対象のフォルダーまたはUNCパスを指定してください。" : "Please specify a valid folder or UNC path to synchronize.",
                    isJa ? "インデックス同期" : "Index Synchronization",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            TriggerBackgroundIndexUpdate(targetFolder, force: true);
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

        #endregion

        #region Search Execution Core (Smart Auto-Routing)

        private async void ExecuteSearch(bool isIncremental, bool isAutoRefresh = false)
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

            var progress = new Progress<SearchProgressReport>(r =>
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

                // ⚡ スマートルーティング判定:
                // 1. FTS5インデックスが存在するか？
                bool hasIndex = hasTarget && _contentIndex.HasIndexForPath(targetFolder);

                if (hasIndex && (query.SearchContentMode || !hasScannedTree))
                {
                    // 📑 ルート1: SQLite FTS5 事前インデックス全文検索 (ミリ秒応答 ＆ 先行通知)
                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    var sw = Stopwatch.StartNew();

                    async void OnNameHitsReady(IReadOnlyList<SearchResultItem> nameHits)
                    {
                        if (currentGen != Volatile.Read(ref _searchGeneration)) return;

                        try
                        {
                            // 🛡️ JIT 権限照合＆自動自浄: 先行表示であってもアクセス権のないファイルは絶対に画面に出さない
                            var verified = await VerifyAndFilterPermissionsAsync(nameHits.ToList(), ct);
                            if (currentGen != Volatile.Read(ref _searchGeneration)) return;

                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (currentGen != Volatile.Read(ref _searchGeneration)) return;
                                _allSearchResults = verified;
                                ApplyFilterAndSort();
                                long totalBytes = _searchResults.Sum(h => h.SizeBytes);
                                UpdateSearchKpi(_searchResults.Count, totalBytes, sw.Elapsed);
                                if (SearchStatusText != null)
                                {
                                    SearchStatusText.Text = isJa
                                        ? $"⚡ ファイル名一致: {_searchResults.Count:N0} 件 ({sw.ElapsedMilliseconds} ms) ―― 📄 本文を検索中..."
                                        : $"⚡ Name matches: {_searchResults.Count:N0} hits ({sw.ElapsedMilliseconds} ms) ―― 📄 Searching content...";
                                }
                            });
                        }
                        catch (OperationCanceledException) { }
                        catch { }
                    }

                    var rawHits = await _contentIndex.SearchIndexedAsync(
                        query,
                        targetFolder,
                        ct,
                        onNameHitsReady: (query.SearchContentMode && string.IsNullOrEmpty(query.ContentKeyword)) ? OnNameHitsReady : null);

                    // 🛡️ JIT 権限照合＆自動自浄: アクセス権のないファイルや消失したファイルを即時除外し、裏でインデックスからパージ
                    var hits = await VerifyAndFilterPermissionsAsync(rawHits, ct);
                    sw.Stop();

                    if (currentGen == Volatile.Read(ref _searchGeneration))
                    {
                        _allSearchResults = hits;
                        ApplyFilterAndSort();
                        long totalBytes = _searchResults.Sum(h => h.SizeBytes);
                        UpdateSearchKpi(_searchResults.Count, totalBytes, sw.Elapsed);
                        if (SearchStatusText != null)
                        {
                            SearchStatusText.Text = isJa
                                ? $"⚡ インデックス高速検索完了: {hits.Count:N0} 件ヒット ({sw.ElapsedMilliseconds} ms)"
                                : $"⚡ Indexed search complete: {hits.Count:N0} hits ({sw.ElapsedMilliseconds} ms)";
                        }
                    }

                    // ⚡ インデックス検索完了後、裏で差分更新を自動トリガー（15分クールダウン制御でサーバー負荷抑制）
                    if (hasTarget && !isAutoRefresh)
                    {
                        TriggerBackgroundIndexUpdate(targetFolder, force: false);
                    }
                    if (hasTarget)
                    {
                        _indexWatcher?.StartWatching(targetFolder);
                    }
                }
                else if (hasScannedTree)
                {
                    // 🚀 ルート2: スキャン済みツリー対象 (0秒インメモリ検索 ＋ 本文ストリーミング)
                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    var sw = Stopwatch.StartNew();

                    // Step 2-1: まずツリーからファイル名・属性一致を超高速（0秒インメモリ）で先行表示！
                    // ※ content: 指定がある場合は本文検査が必須のため先行表示は行わない
                    var treeResults = new List<SearchResultItem>();
                    if (string.IsNullOrEmpty(query.ContentKeyword))
                    {
                        var nameOnlyQuery = query.Clone();
                        nameOnlyQuery.SearchContentMode = false;
                        nameOnlyQuery.ContentKeyword = string.Empty;

                        treeResults = await _searchEngine.SearchInMemoryAsync(scopedRoots, nameOnlyQuery, null, ct, null);
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

                    if (query.SearchContentMode)
                    {
                        // Step 2-2: 本文検索がONの場合は、ライブ直接走査をバックグラウンド実行して本文ヒットを合流！
                        var liveHits = await _searchEngine.SearchDirectFolderAsync(targetFolder, query, batchYield, progress, ct);
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

                    // インデックス同期を裏で自動トリガー（15分クールダウン制御付き）
                    if (hasTarget && !isAutoRefresh)
                    {
                        TriggerBackgroundIndexUpdate(targetFolder, force: false);
                    }
                    if (hasTarget)
                    {
                        _indexWatcher?.StartWatching(targetFolder);
                    }
                }
                else
                {
                    // 🔍 ルート3: ライブ直接走査 (未スキャンUNC / 初見フォルダー / インデックス未構築時の本文検索)
                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    var sw = Stopwatch.StartNew();

                    if (SearchStatusText != null && currentGen == Volatile.Read(ref _searchGeneration))
                    {
                        SearchStatusText.Text = isJa
                            ? (query.SearchContentMode ? "🔍 ライブ走査中 (ファイル名即時表示 ＆ 本文検索)..." : "🔍 ライブ走査を実行中...")
                            : (query.SearchContentMode ? "🔍 Live scanning (instant name hits & content search)..." : "🔍 Running live direct search...");
                    }

                    var results = await _searchEngine.SearchDirectFolderAsync(targetFolder, query, batchYield, progress, ct);
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

                    // 走査完了後、裏でインデックスを自動蓄積
                    if (hasTarget && !isAutoRefresh)
                    {
                        TriggerBackgroundIndexUpdate(targetFolder, force: false);
                    }
                    if (hasTarget)
                    {
                        _indexWatcher?.StartWatching(targetFolder);
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

            string normTarget = targetFolder.Trim().TrimEnd('\\', '/');
            foreach (var tab in StorageTabs)
            {
                if (tab.RootNode == null) continue;

                var matched = FindNodeByPathRecursive(tab.RootNode, normTarget);
                if (matched != null)
                {
                    roots.Add(matched);
                    return roots;
                }
            }

            return roots;
        }

        private static FileItemNode? FindNodeByPathRecursive(FileItemNode current, string targetPath)
        {
            string currentPath = current.FullPath.TrimEnd('\\', '/');
            if (string.Equals(currentPath, targetPath, StringComparison.OrdinalIgnoreCase))
            {
                return current;
            }

            // 配下にない場合は探索を枝刈り
            if (!targetPath.StartsWith(currentPath + "\\", StringComparison.OrdinalIgnoreCase) &&
                !targetPath.StartsWith(currentPath + "/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            foreach (var child in current.Children)
            {
                if (child.IsDirectory)
                {
                    var found = FindNodeByPathRecursive(child, targetPath);
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
            if (item != null && (File.Exists(item.FullPath) || Directory.Exists(item.FullPath)))
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

        private void SearchContextMenu_Simulation_Click(object sender, RoutedEventArgs e)
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
            if (matchedNode != null && _simService != null)
            {
                // 正本: スキャン済みノードから正確な配下容量・実測ファイル数・ACLを一括継承
                newNode = _simService.ConvertToSimNode(matchedNode);
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

        private void SearchExportExcelButton_Click(object sender, RoutedEventArgs e)
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
                    ExportSearchResultsToExcel(dlg.FileName, _searchResults.ToList());
                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    ShowToast(isJa ? "Excel台帳を出力しました" : "Exported Excel report");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Excel出力中にエラーが発生しました: " + ex.Message);
                }
            }
        }

        private void SearchExportCsvButton_Click(object sender, RoutedEventArgs e)
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
                    ExportSearchResultsToCsv(dlg.FileName, _searchResults.ToList());
                    bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                    ShowToast(isJa ? "CSVを出力しました" : "Exported CSV");
                }
                catch (Exception ex)
                {
                    MessageBox.Show("CSV出力中にエラーが発生しました: " + ex.Message);
                }
            }
        }

        private static void ExportSearchResultsToExcel(string filePath, List<SearchResultItem> items)
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Search Results");

            // Header styling
            string[] headers = { "種別", "ファイル/フォルダ名", "サイズ (Bytes)", "サイズ (表示)", "更新日時", "拡張子", "パス長", "一致理由 / スニペット", "完全パス" };
            for (int c = 0; c < headers.Length; c++)
            {
                ws.Cell(1, c + 1).Value = headers[c];
                ws.Cell(1, c + 1).Style.Font.Bold = true;
                ws.Cell(1, c + 1).Style.Fill.BackgroundColor = XLColor.FromArgb(37, 99, 235);
                ws.Cell(1, c + 1).Style.Font.FontColor = XLColor.White;
            }

            int row = 2;
            foreach (var item in items)
            {
                ws.Cell(row, 1).Value = item.IsDirectory ? "フォルダ" : "ファイル";
                ws.Cell(row, 2).Value = item.Name;
                ws.Cell(row, 3).Value = item.SizeBytes;
                ws.Cell(row, 4).Value = item.FormattedSize;
                ws.Cell(row, 5).Value = item.FormattedDate;
                ws.Cell(row, 6).Value = item.Extension;
                ws.Cell(row, 7).Value = item.PathLength;
                ws.Cell(row, 8).Value = item.DisplaySnippetOrReason;
                ws.Cell(row, 9).Value = item.FullPath;

                if (item.IsPathLengthRisk)
                {
                    ws.Cell(row, 7).Style.Fill.BackgroundColor = XLColor.FromArgb(254, 226, 226);
                    ws.Cell(row, 7).Style.Font.FontColor = XLColor.FromArgb(185, 28, 28);
                }

                row++;
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(filePath);
        }

        private static void ExportSearchResultsToCsv(string filePath, List<SearchResultItem> items)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Type,Name,SizeBytes,FormattedSize,LastWriteTime,Extension,PathLength,ReasonOrSnippet,FullPath");

            foreach (var item in items)
            {
                string type = item.IsDirectory ? "Folder" : "File";
                string name = EscapeCsv(item.Name);
                string reason = EscapeCsv(item.DisplaySnippetOrReason);
                string path = EscapeCsv(item.FullPath);

                sb.AppendLine($"{type},{name},{item.SizeBytes},{item.FormattedSize},{item.FormattedDate},{item.Extension},{item.PathLength},{reason},{path}");
            }

            // BOM付き UTF-8
            File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(true));
        }

        private static string EscapeCsv(string text)
        {
            if (string.IsNullOrEmpty(text)) return "\"\"";
            if (text.Contains(',') || text.Contains('"') || text.Contains('\n') || text.Contains('\r'))
            {
                return "\"" + text.Replace("\"", "\"\"") + "\"";
            }
            return "\"" + text + "\"";
        }

        #endregion

        #region JIT Permission Verification & Auto-Purge

        /// <summary>
        /// 検索ヒットしたファイル群の読み取りアクセス権を高速並列検証し、
        /// アクセス拒否（権限剥奪）やファイル消失を検知した場合は結果から除外して裏でDBから自動パージする。
        /// </summary>
        private async Task<List<SearchResultItem>> VerifyAndFilterPermissionsAsync(List<SearchResultItem> items, CancellationToken ct)
        {
            if (items == null || items.Count == 0) return items ?? new List<SearchResultItem>();

            var accessibleItems = new ConcurrentBag<SearchResultItem>();
            var inaccessiblePaths = new ConcurrentBag<string>();

            var po = new ParallelOptions
            {
                MaxDegreeOfParallelism = 16,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(items, po, (item, token) =>
            {
                token.ThrowIfCancellationRequested();
                if (CanAccessItem(item))
                {
                    accessibleItems.Add(item);
                }
                else
                {
                    inaccessiblePaths.Add(item.FullPath);
                }
                return ValueTask.CompletedTask;
            });

            // アクセス不能（権限剥奪または削除）なファイルを検知した場合、裏でインデックスから安全にパージ
            if (!inaccessiblePaths.IsEmpty)
            {
                var pathsToPurge = inaccessiblePaths.ToList();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await _contentIndex.PurgeFilesAsync(pathsToPurge, CancellationToken.None);
                    }
                    catch
                    {
                        // バックグラウンドパージの例外はサイレントに処理
                    }
                });
            }

            // 元の順序を維持して返却
            var accessibleSet = new HashSet<string>(accessibleItems.Select(x => x.FullPath), StringComparer.OrdinalIgnoreCase);
            return items.Where(x => accessibleSet.Contains(x.FullPath)).ToList();
        }

        private static bool CanAccessItem(SearchResultItem item)
        {
            try
            {
                if (item.IsDirectory)
                {
                    return Directory.Exists(item.FullPath);
                }
                else
                {
                    if (!File.Exists(item.FullPath)) return false;
                    // 実際に読み取りオープン可能か検証 (共有モードを広く取ってロック中の誤検知を回避)
                    using var fs = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    return true;
                }
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch (SecurityException)
            {
                return false;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
            catch (PathTooLongException)
            {
                return false;
            }
            catch (IOException)
            {
                // 共有違反（他プロセスが排他ロック中等）はファイル自体は存在し権限もあるためアクセス可能とみなす
                return true;
            }
            catch
            {
                return false;
            }
        }

        #region Sort & Watcher

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

        private void OnWatcherChangesApplied(object? sender, IReadOnlyList<string> changedPaths)
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (!string.IsNullOrWhiteSpace(SearchDirectTargetTextBox?.Text) && _allSearchResults.Count > 0)
                {
                    ExecuteSearch(isIncremental: false, isAutoRefresh: true);
                }
            });
        }

        #endregion

        #endregion
    }
}

