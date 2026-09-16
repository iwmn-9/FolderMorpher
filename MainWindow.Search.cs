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
using ClosedXML.Excel;
using FolderMorpher.Models;
using FolderMorpher.Services;
using Microsoft.Win32;

namespace AstraSize
{
    public partial class MainWindow
    {
        private readonly SearchEngineService _searchEngine = new();
        private CancellationTokenSource? _searchCts;
        private DispatcherTimer? _searchDebounceTimer;
        private readonly ObservableCollection<SearchResultItem> _searchResults = new();
        private List<SearchResultItem> _allSearchResults = new();

        public void InitializeSearchStudio()
        {
            if (SearchDataGrid != null)
            {
                SearchDataGrid.ItemsSource = _searchResults;
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
        }

        #region Search Input & Debounce

        private void SearchInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SearchScopeScannedRadio == null) return;

            // スキャン済みツリー対象の場合はインクリメンタル即時検索
            if (SearchScopeScannedRadio.IsChecked == true)
            {
                _searchDebounceTimer?.Stop();
                _searchDebounceTimer?.Start();
            }
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

        #region Search Execution Core

        private async void ExecuteSearch(bool isIncremental)
        {
            string rawQuery = SearchInputBox?.Text?.Trim() ?? string.Empty;
            var query = SearchQueryParser.Parse(rawQuery);

            if (SearchContentCheckBox?.IsChecked == true)
            {
                query.SearchContentMode = true;
            }

            // 本文検索（I/Oを伴う探索）はタイピング途中のインクリメンタル実行をスキップし、Enterまたはボタン押下で実行
            if (isIncremental && query.HasDeepFileIoRequirement)
            {
                return;
            }

            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var ct = _searchCts.Token;

            bool isDirectScope = (SearchScopeDirectRadio?.IsChecked == true);
            string targetFolder = SearchDirectTargetTextBox?.Text?.Trim() ?? string.Empty;

            if (isDirectScope && string.IsNullOrWhiteSpace(targetFolder))
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

            SetSearchLoadingState(true);

            var progress = new Progress<SearchProgressReport>(r =>
            {
                UpdateSearchKpi(r.HitCount, r.TotalHitBytes, r.Elapsed);
                if (SearchStatusText != null && !string.IsNullOrEmpty(r.CurrentPath))
                {
                    SearchStatusText.Text = r.CurrentPath;
                }
            });

            var batchYield = new Progress<IReadOnlyList<SearchResultItem>>(items =>
            {
                foreach (var item in items)
                {
                    _searchResults.Add(item);
                }
            });

            try
            {
                _searchResults.Clear();
                _allSearchResults.Clear();

                if (isDirectScope)
                {
                    // ライブ直接走査 (未スキャンUNC / フォルダー)
                    var results = await _searchEngine.SearchDirectFolderAsync(targetFolder, query, batchYield, progress, ct);
                    _allSearchResults = results;
                }
                else
                {
                    // スキャン済みツリー対象 (0秒インメモリ検索)
                    // 対象フォルダーの指定がある場合は、該当フォルダーの部分木のみにスコープを絞り込む
                    if (!string.IsNullOrWhiteSpace(targetFolder))
                    {
                        var scopedRoots = GetTargetScannedRootNodes(targetFolder);
                        if (scopedRoots.Count > 0)
                        {
                            var results = await _searchEngine.SearchInMemoryAsync(scopedRoots, query, progress, ct);
                            _allSearchResults = results;
                            foreach (var item in results) _searchResults.Add(item);
                        }
                        else
                        {
                            // スキャン済みツリーに対象フォルダーが含まれていない場合は直接走査へフォールバック
                            if (SearchStatusText != null)
                            {
                                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                                SearchStatusText.Text = isJa
                                    ? "※スキャン済みツリー外のため、ライブ直接走査を実行中..."
                                    : "Target folder not in cached tree. Running live direct search...";
                            }
                            var results = await _searchEngine.SearchDirectFolderAsync(targetFolder, query, batchYield, progress, ct);
                            _allSearchResults = results;
                        }
                    }
                    else
                    {
                        var roots = GetTargetScannedRootNodes(null);
                        var results = await _searchEngine.SearchInMemoryAsync(roots, query, progress, ct);
                        _allSearchResults = results;
                        foreach (var item in results) _searchResults.Add(item);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                if (SearchStatusText != null) SearchStatusText.Text = isJa ? "検索を中断しました。" : "Search canceled.";
            }
            catch (Exception ex)
            {
                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                MessageBox.Show(
                    (isJa ? "検索中にエラーが発生しました: " : "Error occurred during search: ") + ex.Message,
                    isJa ? "検索エラー" : "Search Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            finally
            {
                SetSearchLoadingState(false);
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
            return SearchDataGrid?.SelectedItem as SearchResultItem;
        }

        private void SearchDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            SearchContextMenu_Open_Click(sender, e);
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
    }
}
