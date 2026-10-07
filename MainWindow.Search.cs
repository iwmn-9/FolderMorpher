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
        private readonly Dictionary<string, SearchResultItem> _searchResultsByPath = new(StringComparer.OrdinalIgnoreCase);

        private string _selectedSortType = "Relevance";

        private long _searchGeneration = 0;
        private bool _isSearchRunning;

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

            // ★ 本文検索・OCRトグル: 詳細オプションパネル内に隠し、Enterキーまたは検索実行ボタン押下で実行する
            // （チェック変更時に勝手に走らせない）

            // 検索履歴ポップアップの外側クリックを検知して閉じる
            this.PreviewMouseDown += (s, e) =>
            {
                if (SearchHistoryPopup == null || !SearchHistoryPopup.IsOpen) return;
                if (e.OriginalSource is DependencyObject dep)
                {
                    if (IsDescendantOf(dep, SearchInputBox)) return;
                    if (SearchHistoryPopup.Child != null && IsDescendantOf(dep, SearchHistoryPopup.Child)) return;
                }
                SearchHistoryPopup.IsOpen = false;
            };
        }

        private void SearchAdvancedOptionsToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (SearchAdvancedOptionsPanel != null)
            {
                bool isVisible = SearchAdvancedOptionsPanel.Visibility == Visibility.Visible;
                SearchAdvancedOptionsPanel.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void SearchRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            // 更新はキャッシュの再照合ではなく、原本を再走査する。
            if (SearchQueryParser.Parse(SearchInputBox?.Text?.Trim() ?? string.Empty).IsEmpty) return;
            _searchDebounceTimer?.Stop();
            ExecuteSearch(isIncremental: false, forceLive: true);
        }

        #region Search Input & Debounce

        private void SearchInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchHistoryPopup != null && SearchHistoryPopup.IsOpen)
            {
                SearchHistoryPopup.IsOpen = false;
            }
            if (SearchInputHint != null)
                SearchInputHint.Visibility = string.IsNullOrEmpty(SearchInputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            CancelCurrentSearch();
            if (SearchQueryParser.Parse(SearchInputBox?.Text?.Trim() ?? string.Empty).IsEmpty)
            {
                ClearSearchResults();
                return;
            }
            _searchDebounceTimer?.Start();
        }

        private void SearchInputBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            OpenSearchHistoryPopupIfAvailable();
        }

        private void SearchInputBox_GotFocus(object sender, RoutedEventArgs e)
        {
            OpenSearchHistoryPopupIfAvailable();
        }

        private void OpenSearchHistoryPopupIfAvailable()
        {
            if (SearchHistoryPopup == null || SearchHistoryItemsControl == null) return;
            var history = AppSettingsService.Instance.Current.SearchHistory;
            if (history == null || history.Count == 0)
            {
                SearchHistoryPopup.IsOpen = false;
                return;
            }

            SearchHistoryItemsControl.ItemsSource = history.Select(q => new SearchHistoryEntry { QueryText = q }).ToList();
            SearchHistoryPopup.IsOpen = true;
        }

        private void SearchHistoryItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement elem && elem.DataContext is SearchHistoryEntry entry)
            {
                if (e.OriginalSource is DependencyObject dep)
                {
                    DependencyObject? current = dep;
                    while (current != null && current != elem)
                    {
                        if (current is Button) return;
                        current = System.Windows.Media.VisualTreeHelper.GetParent(current);
                    }
                }

                if (SearchHistoryPopup != null)
                {
                    SearchHistoryPopup.IsOpen = false;
                }
                if (SearchInputBox != null)
                {
                    SearchInputBox.Text = entry.QueryText;
                    SearchInputBox.CaretIndex = SearchInputBox.Text.Length;
                }
                _searchDebounceTimer?.Stop();
                ExecuteSearch(isIncremental: false, forceLive: true);
                e.Handled = true;
            }
        }

        private void SearchHistoryItemDelete_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            if (sender is Button btn && btn.Tag is string query)
            {
                var history = AppSettingsService.Instance.Current.SearchHistory;
                history.RemoveAll(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase));
                AppSettingsService.Instance.Save();

                if (history.Count == 0)
                {
                    if (SearchHistoryPopup != null)
                    {
                        SearchHistoryPopup.IsOpen = false;
                    }
                }
                else if (SearchHistoryItemsControl != null)
                {
                    SearchHistoryItemsControl.ItemsSource = history.Select(q => new SearchHistoryEntry { QueryText = q }).ToList();
                }
            }
        }

        private void SearchHistoryClearAllButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            var history = AppSettingsService.Instance.Current.SearchHistory;
            history.Clear();
            AppSettingsService.Instance.Save();
            if (SearchHistoryPopup != null)
            {
                SearchHistoryPopup.IsOpen = false;
            }
        }

        private void AddSearchHistory(string rawQuery)
        {
            if (string.IsNullOrWhiteSpace(rawQuery)) return;
            var trimmed = rawQuery.Trim();
            if (string.IsNullOrEmpty(trimmed)) return;

            var history = AppSettingsService.Instance.Current.SearchHistory;
            history.RemoveAll(q => string.Equals(q, trimmed, StringComparison.OrdinalIgnoreCase));
            history.Insert(0, trimmed);
            const int maxHistory = 15;
            if (history.Count > maxHistory)
            {
                history.RemoveRange(maxHistory, history.Count - maxHistory);
            }
            AppSettingsService.Instance.Save();
        }

        private void SearchInputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (SearchHistoryPopup != null && SearchHistoryPopup.IsOpen)
                {
                    SearchHistoryPopup.IsOpen = false;
                    e.Handled = true;
                    return;
                }
            }
            if (e.Key == Key.Enter)
            {
                if (SearchHistoryPopup != null) SearchHistoryPopup.IsOpen = false;
                _searchDebounceTimer?.Stop();
                ExecuteSearch(isIncremental: false, forceLive: true);
            }
        }

        private static bool IsDescendantOf(DependencyObject node, DependencyObject parent)
        {
            DependencyObject? current = node;
            while (current != null)
            {
                if (current == parent) return true;
                current = System.Windows.Media.VisualTreeHelper.GetParent(current);
            }
            return false;
        }

        private void SearchExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isSearchRunning)
            {
                CancelCurrentSearch();
                if (SearchStatusText != null)
                    SearchStatusText.Text = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                        ? "検索を中断しました。" : "Search canceled.";
                return;
            }
            _searchDebounceTimer?.Stop();
            ExecuteSearch(isIncremental: false, forceLive: true);
        }

        private void SearchClearButton_Click(object sender, RoutedEventArgs e)
        {
            if (SearchInputBox != null)
            {
                SearchInputBox.Text = string.Empty;
                SearchInputBox.Focus();
            }
            CancelCurrentSearch();
            ClearSearchResults();
        }

        private void CancelCurrentSearch()
        {
            _searchDebounceTimer?.Stop();
            Interlocked.Increment(ref _searchGeneration);
            var cts = _searchCts;
            _searchCts = null;
            cts?.Cancel();
            SetSearchLoadingState(false);
        }

        private void ClearSearchResults()
        {
            _searchResults.Clear();
            _allSearchResults.Clear();
            _searchResultsByPath.Clear();
            if (SearchEmptyState != null) SearchEmptyState.Visibility = Visibility.Visible;
            _selectedSortType = "Relevance";
            if (SearchSortComboBox != null) SearchSortComboBox.SelectedIndex = 0;
            UpdateSearchKpi(0, 0, TimeSpan.Zero);
            if (SearchStatusText != null)
                SearchStatusText.Text = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                    ? "待機中" : "Ready";
        }


        private void SearchDirectBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                    ? "走査対象のフォルダーを選択"
                    : "Select Target Folder to Search"
            };
            if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
            {
                var normPath = PathCanonicalizer.Normalize(dlg.FolderName);
                if (SearchDirectTargetTextBox != null)
                {
                    SearchDirectTargetTextBox.Text = normPath;
                    SetActiveFolderScope(normPath);
                }
            }
        }

        private static async Task<(List<FolderMorpher.Contracts.SearchResultDto> Results, int DeniedFolders, int UnreadFiles, int TotalHits, long TotalBytes, string OcrWarning)> RunSearchJobAsync(
            string targetFolder,
            SearchQuery query,
            IProgress<FolderMorpher.Contracts.SearchProgressDto> progress,
            Action<IReadOnlyList<FolderMorpher.Contracts.SearchResultDto>> onSearchBatch,
            bool cached,
            CancellationToken ct)
        {
            var started = Stopwatch.StartNew();
            var status = await FolderMorpher.HostClient.HostJobClient.RunAsync(
                new FolderMorpher.Contracts.HostJobRequestDto
                {
                    Kind = cached ? FolderMorpher.Contracts.HostJobKind.CachedSearch : FolderMorpher.Contracts.HostJobKind.Search,
                    TargetPath = targetFolder,
                    SearchQuery = FolderMorpher.HostClient.SearchDtoMapper.ToDto(query)
                },
                job => progress.Report(new FolderMorpher.Contracts.SearchProgressDto
                {
                    IsCached = cached,
                    CurrentPath = job.ProgressText,
                    OcrWarning = job.OcrWarning, HitCount = job.HitCount,
                    ScannedCount = job.ScannedCount,
                    ContentProcessedCount = job.ContentProcessedCount,
                    DiscoveredDirectories = job.DiscoveredDirectories,
                    ProcessedDirectories = job.ProcessedDirectories,
                    TotalHitBytes = job.TotalHitBytes,
                    AccessDeniedFolders = job.AccessDeniedFolders,
                    UnreadFiles = job.UnreadFiles,
                    Elapsed = started.Elapsed,
                    IsCompleted = job.State == FolderMorpher.Contracts.HostJobState.Completed
                }),
                ct,
                onSearchBatch);
            return (status.SearchResults ?? throw new InvalidOperationException("検索結果がHostから返されませんでした。"),
                status.AccessDeniedFolders, status.UnreadFiles, status.HitCount, status.TotalHitBytes, status.OcrWarning);
        }

        #endregion

        #region Search Execution Core (Smart Auto-Routing)

        private async void ExecuteSearch(bool isIncremental, bool forceLive = false)
        {
            string rawQuery = SearchInputBox?.Text?.Trim() ?? string.Empty;
            var query = SearchQueryParser.Parse(rawQuery);
            if (query.IsEmpty)
            {
                CancelCurrentSearch();
                ClearSearchResults();
                return;
            }

            if (!isIncremental)
            {
                AddSearchHistory(rawQuery);
            }

            if (SearchIncludeFoldersCheckBox?.IsChecked == true)
            {
                query.IncludeFolders = true;
            }

            if (SearchContentCheckBox?.IsChecked == true)
            {
                query.SearchContentMode = true;
            }

            if (SearchIncludeOcrCheckBox?.IsChecked == true)
            {
                query.IncludeOcr = true;
            }

            // 本文検索（I/Oを伴う探索）はタイピング途中のインクリメンタル実行をスキップし、Enterまたはボタン押下で実行
            if (isIncremental && query.HasDeepFileIoRequirement)
            {
                return;
            }

            string targetFolder = SearchDirectTargetTextBox?.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(targetFolder))
            {
                targetFolder = PathCanonicalizer.Normalize(targetFolder);
                if (SearchDirectTargetTextBox != null && SearchDirectTargetTextBox.Text != targetFolder)
                {
                    SearchDirectTargetTextBox.Text = targetFolder;
                }
            }
            if (string.IsNullOrWhiteSpace(targetFolder))
            {
                // スキャン済みタブのルートをフォールバックとして取得
                var selectedTab = StorageTabs?.FirstOrDefault(t => t.IsSelected);
                if (selectedTab?.RootNode != null && !string.IsNullOrWhiteSpace(selectedTab.RootNode.FullPath))
                {
                    targetFolder = PathCanonicalizer.Normalize(selectedTab.RootNode.FullPath);
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
                    AppDialog.Show(
                        isJa ? "走査対象のフォルダーまたはUNCパスを入力してください。" : "Please specify a target folder or UNC path to search.",
                        isJa ? "検索エラー" : "Search Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
                return;
            }

            CancelCurrentSearch();
            _searchCts = new CancellationTokenSource();
            var searchCts = _searchCts;
            var ct = searchCts.Token;
            long currentGen = Interlocked.Increment(ref _searchGeneration);

            SetSearchLoadingState(true);
            var searchTotalSw = Stopwatch.StartNew();
            ScanEtaSession? scanEta = null;
            bool liveSearchCompleted = false;

            var progress = new Progress<FolderMorpher.Contracts.SearchProgressDto>(r =>
            {
                if (currentGen != Volatile.Read(ref _searchGeneration)) return;
                if (!r.IsCached) ReportScanEta(scanEta,
                    query.HasDeepFileIoRequirement ? r.ContentProcessedCount : r.ScannedCount,
                    r.ProcessedDirectories, r.DiscoveredDirectories);
                // Host progress is per job; cache rows are only a provisional preview.
                if (SearchKpiElapsedText != null) SearchKpiElapsedText.Text = $"{searchTotalSw.Elapsed.TotalSeconds:F2}s";
                if (SearchStatusText != null && !string.IsNullOrEmpty(r.CurrentPath))
                {
                    SearchStatusText.Text = r.CurrentPath;
                }
            });

            var lastBatchUpdate = Stopwatch.StartNew();
            void ReceiveBatch(IReadOnlyList<FolderMorpher.Contracts.SearchResultDto> items)
            {
                if (currentGen != Volatile.Read(ref _searchGeneration)) return;

                bool addedOrUpdated = false;
                foreach (var dto in items)
                {
                    var item = FolderMorpher.HostClient.SearchDtoMapper.ToViewItem(dto);
                    if (_searchResultsByPath.TryGetValue(item.FullPath, out var existing))
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
                        _searchResultsByPath[item.FullPath] = item;
                        _allSearchResults.Add(item);
                        addedOrUpdated = true;
                    }
                }

                if (addedOrUpdated && (lastBatchUpdate.ElapsedMilliseconds > 80 || _allSearchResults.Count <= 20))
                {
                    lastBatchUpdate.Restart();
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (currentGen != Volatile.Read(ref _searchGeneration)) return;
                        ApplyFilterAndSort();
                        long totalBytes = _searchResults.Sum(h => h.SizeBytes);
                        UpdateSearchKpi(_searchResults.Count, totalBytes, searchTotalSw.Elapsed);
                        if (SearchStatusText != null && query.HasDeepFileIoRequirement)
                        {
                            SearchStatusText.Text = Strings.SearchPartialHits(_searchResults.Count);
                        }
                    }), DispatcherPriority.Background);
                }
            }

            try
            {
                _searchResults.Clear();
                _allSearchResults.Clear();
                _searchResultsByPath.Clear();

                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                var sw = Stopwatch.StartNew();

                // スキャン済みツリーまたはローカルTreeCacheから先行表示する。
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
                    // ★ 本文走査・OCR等のI/Oを伴う探索の場合:
                    // キャッシュで名前だけ検索して完了表示した直後にLive本文探索を再実行すると、
                    // ユーザーから見て「検索が2回始まってループしている」ように見えるため、
                    // 1回のジョブで最初からストリーミング表示を行う。
                    if (!query.HasDeepFileIoRequirement)
                    {
                        var treeResults = new List<SearchResultItem>();
                        if (string.IsNullOrEmpty(query.ContentKeyword) &&
                            !query.HasOfficeLinkOnly &&
                            string.IsNullOrEmpty(query.OfficeLinkKeyword))
                        {
                            var nameOnlyQuery = query.Clone();
                            nameOnlyQuery.SearchContentMode = false;
                            nameOnlyQuery.ContentKeyword = string.Empty;

                            var cachedOutcome = await RunSearchJobAsync(targetFolder, nameOnlyQuery, progress, ReceiveBatch, cached: true, ct);
                            treeResults = cachedOutcome.Results
                                .Select(FolderMorpher.HostClient.SearchDtoMapper.ToViewItem).ToList();
                            if (currentGen == Volatile.Read(ref _searchGeneration))
                            {
                                SetSearchResults(treeResults);
                                long totalBytes = _searchResults.Sum(h => h.SizeBytes);
                                UpdateSearchKpi(cachedOutcome.TotalHits, cachedOutcome.TotalBytes, sw.Elapsed);

                                if (SearchStatusText != null)
                                {
                                    SearchStatusText.Text = Strings.SearchSnapshotStatus(_searchResults.Count, forceLive);
                                }
                            }
                        }

                        if (forceLive && hasTarget)
                        {
                            ct.ThrowIfCancellationRequested();
                            scanEta = BeginScanEta("search-name", targetFolder, scopedRoots.Sum(root => (long)root.FileCount));
                            var liveOutcome = await RunSearchJobAsync(targetFolder, query, progress, ReceiveBatch, cached: false, ct);
                            liveSearchCompleted = true;
                            var liveHits = liveOutcome.Results
                                .Select(FolderMorpher.HostClient.SearchDtoMapper.ToViewItem).ToList();
                            if (currentGen == Volatile.Read(ref _searchGeneration))
                            {
                                SetSearchResults(liveHits);
                                long totalBytes = _searchResults.Sum(h => h.SizeBytes);
                                UpdateSearchKpi(liveOutcome.TotalHits, liveOutcome.TotalBytes, sw.Elapsed);

                                if (SearchStatusText != null)
                                {
                                    SearchStatusText.Text = Strings.SearchLiveComplete(liveOutcome.TotalHits,
                                        sw.ElapsedMilliseconds) + FormatSearchCoverage(liveOutcome.DeniedFolders, liveOutcome.UnreadFiles) +
                                        $" / 表示・retained {_searchResults.Count:N0} {liveOutcome.OcrWarning}";
                                }
                            }
                        }
                        else if (currentGen == Volatile.Read(ref _searchGeneration))
                        {
                            if (SearchStatusText != null)
                            {
                                SearchStatusText.Text = Strings.SearchSnapshotStatus(_searchResults.Count, false);
                            }
                        }
                    }
                    else if (hasTarget)
                    {
                        // 本文・OCR検索: 1回の一貫した走査ジョブとして実行（2重走査の防止）
                        ct.ThrowIfCancellationRequested();
                        scanEta = BeginScanEta("search-content", targetFolder, scopedRoots.Sum(root => (long)root.FileCount));
                        var liveOutcome = await RunSearchJobAsync(targetFolder, query, progress, ReceiveBatch, cached: false, ct);
                        liveSearchCompleted = true;
                        var liveHits = liveOutcome.Results
                            .Select(FolderMorpher.HostClient.SearchDtoMapper.ToViewItem).ToList();
                        if (currentGen == Volatile.Read(ref _searchGeneration))
                        {
                            SetSearchResults(liveHits);
                            long totalBytes = _searchResults.Sum(h => h.SizeBytes);
                            UpdateSearchKpi(liveOutcome.TotalHits, liveOutcome.TotalBytes, sw.Elapsed);

                            if (SearchStatusText != null)
                            {
                                SearchStatusText.Text = Strings.SearchLiveComplete(liveOutcome.TotalHits,
                                    sw.ElapsedMilliseconds) + FormatSearchCoverage(liveOutcome.DeniedFolders, liveOutcome.UnreadFiles) +
                                    $" / 表示・retained {_searchResults.Count:N0} {liveOutcome.OcrWarning}";
                            }
                        }
                    }
                }
                else
                {
                    // 🔍 ルート2: ライブ直接走査 (未スキャンUNC / 初見フォルダー / Agent Ransack 流 Channel パイプライン)
                    if (SearchStatusText != null && currentGen == Volatile.Read(ref _searchGeneration))
                    {
                        SearchStatusText.Text = isJa
                            ? (query.HasDeepFileIoRequirement ? "🔍 ライブ走査中 (ファイル名即時表示 ＆ 本文検索)..." : "🔍 ライブ走査を実行中...")
                            : (query.HasDeepFileIoRequirement ? "🔍 Live scanning (instant name hits & content search)..." : "🔍 Running live direct search...");
                    }

                    scanEta = BeginScanEta(query.HasDeepFileIoRequirement ? "search-content" : "search-name", targetFolder,
                        scopedRoots.Sum(root => (long)root.FileCount));
                    var liveOutcome = await RunSearchJobAsync(targetFolder, query, progress, ReceiveBatch, cached: false, ct);
                    liveSearchCompleted = true;
                    var results = liveOutcome.Results
                        .Select(FolderMorpher.HostClient.SearchDtoMapper.ToViewItem).ToList();
                    if (currentGen == Volatile.Read(ref _searchGeneration))
                    {
                        SetSearchResults(results);
                        long totalBytes = _searchResults.Sum(h => h.SizeBytes);
                        UpdateSearchKpi(liveOutcome.TotalHits, liveOutcome.TotalBytes, sw.Elapsed);

                        if (SearchStatusText != null)
                        {
                            SearchStatusText.Text = isJa
                                ? $"🔍 ライブ走査完了: {liveOutcome.TotalHits:N0} 件ヒット / 表示 {_searchResults.Count:N0} 件 ({sw.ElapsedMilliseconds} ms){FormatSearchCoverage(liveOutcome.DeniedFolders, liveOutcome.UnreadFiles)} {liveOutcome.OcrWarning}"
                                : $"🔍 Live direct search complete: {liveOutcome.TotalHits:N0} hits / retained {_searchResults.Count:N0} ({sw.ElapsedMilliseconds} ms){FormatSearchCoverage(liveOutcome.DeniedFolders, liveOutcome.UnreadFiles)} {liveOutcome.OcrWarning}";
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
                    if (SearchStatusText != null) SearchStatusText.Text = Strings.SearchFailed;
                    AppDialog.Show(
                        (isJa ? "検索中にエラーが発生しました: " : "Error occurred during search: ") + ex.Message,
                        isJa ? "検索エラー" : "Search Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
            finally
            {
                FinishScanEta(scanEta, liveSearchCompleted);
                if (currentGen == Volatile.Read(ref _searchGeneration))
                {
                    SetSearchLoadingState(false);
                    Interlocked.Increment(ref _searchGeneration); // Ignore queued progress after the final result is rendered.
                }
                if (ReferenceEquals(_searchCts, searchCts)) _searchCts = null;
                searchCts.Dispose();
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
            _isSearchRunning = isLoading;
            if (SearchProgressBar != null)
            {
                SearchProgressBar.Visibility = isLoading ? Visibility.Visible : Visibility.Collapsed;
            }
            UpdateSearchActionButton();
        }

        private void UpdateSearchActionButton()
        {
            if (SearchExecuteButton != null)
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                SearchExecuteButton.Content = _isSearchRunning ? Strings.SearchCancel : Strings.SearchExecute;
                SearchExecuteButton.ToolTip = _isSearchRunning
                    ? (isJa ? "実行中の検索を中止" : "Stop the running search")
                    : (isJa ? "検索を実行 (Enterキーでも実行可能)" : "Run search (or press Enter)");
                SearchExecuteButton.Style = (Style)FindResource(_isSearchRunning ? "FluentButtonDanger" : "FluentButtonPrimary");
            }
        }

        private int _totalSearchHits;
        private long _totalSearchBytes;

        private void UpdateSearchKpi(int hitCount, long totalBytes, TimeSpan elapsed)
        {
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            if (SearchKpiHitCountText != null)
            {
                _totalSearchHits = hitCount;
                _totalSearchBytes = totalBytes;
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

        private static string FormatSearchCoverage(int deniedFolders, int unreadFiles)
        {
            if (deniedFolders == 0 && unreadFiles == 0) return string.Empty;
            return Strings.SearchUnverifiedTargets(deniedFolders, unreadFiles);
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
                    AppDialog.Show(UiText("ファイルを開けませんでした: ", "Could not open the file: ") + ex.Message);
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
                ShowToast(UiText("クリップボードにパスをコピーしました", "Path copied to clipboard"));
            }
        }

        private void SearchContextMenu_CopyName_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedSearchItem();
            if (item != null)
            {
                Clipboard.SetText(item.Name);
                ShowToast(UiText("クリップボードにファイル名をコピーしました", "File name copied to clipboard"));
            }
        }

        private void SearchContextMenu_LiveAcl_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedSearchItem();
            if (item == null) return;

            string targetDir = item.IsDirectory ? item.FullPath : item.DirectoryPath;
            if (string.IsNullOrEmpty(targetDir)) return;

            // Tab 2 (Live ACL) へジャンプ
            SetActiveFolderScope(targetDir);
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
                    AppDialog.Show(UiText($"移行ツリーへの追加に失敗しました: {ex.Message}", $"Could not add to migration tree: {ex.Message}"),
                        UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
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
            ShowToast(UiText($"移行ツリーに「{newNode.Name}」を追加しました", $"Added {newNode.Name} to migration tree"));
        }

        private void SearchContextMenu_LinkFix_Click(object sender, RoutedEventArgs e)
        {
            var item = GetSelectedSearchItem();
            if (item == null) return;

            string targetDir = item.IsDirectory ? item.FullPath : item.DirectoryPath;
            if (string.IsNullOrEmpty(targetDir)) return;

            // Tab 4 (LinkFix) へジャンプ
            SetActiveFolderScope(targetDir);
            NavTabLinkFix.IsChecked = true;
            if (LinkSearchScopeTextBox != null)
            {
                LinkSearchScopeTextBox.Text = targetDir;
            }
            ShowToast(UiText("リンク修復対象パスを設定しました", "Link repair target path set"));
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
                SetActiveFolderScope(targetDir);
            }
            ShowToast(UiText("ファイル整理・監査対象パスを設定しました", "File audit target path set"));
        }

        #endregion

        #region Export (Excel / CSV)

        private async void SearchExportExcelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_searchResults.Count == 0)
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                AppDialog.Show(
                    isJa ? "エクスポートする検索結果がありません。" : "No search results to export.",
                    isJa ? "情報" : "Information",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = UiText("検索結果のExcel出力先", "Save search results as Excel"),
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
                    AppDialog.Show(UiText("Excel出力中にエラーが発生しました: ", "Excel export failed: ") + ex.Message);
                }
            }
        }

        private async void SearchExportCsvButton_Click(object sender, RoutedEventArgs e)
        {
            if (_searchResults.Count == 0)
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                AppDialog.Show(
                    isJa ? "エクスポートする検索結果がありません。" : "No search results to export.",
                    isJa ? "情報" : "Information",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = UiText("検索結果のCSV出力先", "Save search results as CSV"),
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
                    AppDialog.Show(UiText("CSV出力中にエラーが発生しました: ", "CSV export failed: ") + ex.Message);
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

        private void SetSearchResults(IEnumerable<SearchResultItem> items)
        {
            _allSearchResults = new List<SearchResultItem>();
            _searchResultsByPath.Clear();
            foreach (var item in items)
            {
                if (_searchResultsByPath.TryAdd(item.FullPath, item))
                    _allSearchResults.Add(item);
                else if (string.IsNullOrEmpty(_searchResultsByPath[item.FullPath].ContentSnippet) &&
                         !string.IsNullOrEmpty(item.ContentSnippet))
                {
                    var old = _searchResultsByPath[item.FullPath];
                    old.ContentSnippet = item.ContentSnippet;
                    old.MatchedReason = item.MatchedReason;
                }
            }
            ApplyFilterAndSort();
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
            if (SearchEmptyState != null) SearchEmptyState.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            _searchResults.Clear();
            foreach (var item in list)
            {
                _searchResults.Add(item);
            }

            // メトリクスバーの更新（件数・合計サイズ）
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            if (SearchKpiHitCountText != null)
            {
                int total = Math.Max(_totalSearchHits, list.Count);
                SearchKpiHitCountText.Text = isJa ? $"{total:N0} 件" : $"{total:N0} items";
                SearchKpiHitCountText.ToolTip = isJa ? $"一覧に保持: {list.Count:N0} 件" : $"Retained in list: {list.Count:N0}";
            }

            if (SearchKpiTotalSizeText != null)
            {
                long totalBytes = Math.Max(_totalSearchBytes, list.Sum(x => x.SizeBytes));
                SearchKpiTotalSizeText.Text = FormatHelper.FormatBytes(totalBytes);
            }
        }



        #endregion
    }

    public sealed class SearchHistoryEntry
    {
        public string QueryText { get; set; } = string.Empty;
    }
}

