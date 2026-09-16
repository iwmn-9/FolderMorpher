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

        #region Smart Chips (Presets)

        private void SearchChipLarge_Click(object sender, RoutedEventArgs e)
        {
            SetSearchQuery("size:>1GB");
        }

        private void SearchChipDormant_Click(object sender, RoutedEventArgs e)
        {
            SetSearchQuery("dormant:3y");
        }

        private void SearchChipPathLen_Click(object sender, RoutedEventArgs e)
        {
            SetSearchQuery("pathlen:>240");
        }

        private void SearchChipIllegal_Click(object sender, RoutedEventArgs e)
        {
            SetSearchQuery("chars:illegal");
        }

        private void SearchChipOffice_Click(object sender, RoutedEventArgs e)
        {
            SetSearchQuery("ext:xlsx,docx office-link:\"\\\\\"");
        }

        private void SetSearchQuery(string q)
        {
            if (SearchInputBox != null)
            {
                SearchInputBox.Text = q;
                SearchInputBox.CaretIndex = SearchInputBox.Text.Length;
                SearchInputBox.Focus();
            }
            ExecuteSearch(isIncremental: false);
        }

        #endregion

        #region Search Execution Core

        private async void ExecuteSearch(bool isIncremental)
        {
            string rawQuery = SearchInputBox?.Text?.Trim() ?? string.Empty;
            var query = SearchQueryParser.Parse(rawQuery);

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
                    // ライブ直接走査
                    var results = await _searchEngine.SearchDirectFolderAsync(targetFolder, query, batchYield, progress, ct);
                    _allSearchResults = results;
                }
                else
                {
                    // スキャン済みツリー対象 0秒インメモリ検索
                    var roots = GetCurrentScannedRootNodes();
                    var results = await _searchEngine.SearchInMemoryAsync(roots, query, progress, ct);
                    _allSearchResults = results;
                    foreach (var item in results)
                    {
                        _searchResults.Add(item);
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

        private List<FileItemNode> GetCurrentScannedRootNodes()
        {
            var roots = new List<FileItemNode>();
            if (StorageTabs != null)
            {
                foreach (var tab in StorageTabs)
                {
                    if (tab.RootNode != null)
                    {
                        roots.Add(tab.RootNode);
                    }
                }
            }
            return roots;
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

            // Tab 3 (Simulation) へジャンプ
            NavTabSimulation.IsChecked = true;

            // 仮想ツリーに新規ルートとして追加
            var newNode = new SimFolderNode
            {
                Name = Path.GetFileName(targetDir),
                EstimatedSizeBytes = item.SizeBytes,
                Level = 0,
                InheritAcl = true
            };
            newNode.MappedSourcePaths.Add(targetDir);
            _simRootFolders.Add(newNode);
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
            if (_allSearchResults.Count == 0) return;

            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            var sfd = new SaveFileDialog
            {
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                FileName = $"Search_Results_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx",
                Title = isJa ? "検索結果をExcel台帳で保存" : "Export Search Results to Excel"
            };

            if (sfd.ShowDialog() != true) return;

            try
            {
                using var wb = new XLWorkbook();
                var ws = wb.Worksheets.Add(isJa ? "検索結果台帳" : "Search_Results");
                ws.ShowGridLines = true;

                // Header
                ws.Cell("B2").Value = isJa ? "FolderMorpher — 検索結果台帳" : "FolderMorpher — Search Results";
                ws.Cell("B2").Style.Font.Bold = true;
                ws.Cell("B2").Style.Font.FontSize = 14;

                ws.Cell("B3").Value = $"Query: {SearchInputBox?.Text} | Exported: {DateTime.Now:yyyy/MM/dd HH:mm:ss} | Count: {_allSearchResults.Count:N0}";
                ws.Cell("B3").Style.Font.FontSize = 9;
                ws.Cell("B3").Style.Font.FontColor = XLColor.DimGray;

                int hRow = 5;
                string[] headers = isJa
                    ? new[] { "ファイル名", "種別", "容量", "サイズ (Bytes)", "更新日時", "拡張子", "パス長", "フルパス", "一致理由 / 本文抜粋" }
                    : new[] { "Name", "Type", "Size", "Bytes", "Modified", "Extension", "Length", "Full Path", "Match Reason / Snippet" };

                for (int col = 0; col < headers.Length; col++)
                {
                    var c = ws.Cell(hRow, col + 2);
                    c.Value = headers[col];
                    c.Style.Font.Bold = true;
                    c.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                    c.Style.Font.FontColor = XLColor.White;
                }

                int r = hRow + 1;
                foreach (var item in _allSearchResults)
                {
                    ws.Cell(r, 2).Value = item.Name;
                    ws.Cell(r, 3).Value = item.IsDirectory ? "Folder" : "File";
                    ws.Cell(r, 4).Value = item.FormattedSize;
                    ws.Cell(r, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;
                    ws.Cell(r, 5).Value = item.SizeBytes;
                    ws.Cell(r, 5).Style.NumberFormat.Format = "#,##0";
                    ws.Cell(r, 6).Value = item.FormattedDate;
                    ws.Cell(r, 7).Value = item.Extension;
                    ws.Cell(r, 8).Value = item.PathLength;
                    ws.Cell(r, 9).Value = item.FullPath;
                    ws.Cell(r, 10).Value = item.HasSnippet ? item.ContentSnippet : item.MatchedReason;
                    r++;
                }

                var tbl = ws.Range(hRow, 2, r - 1, headers.Length + 1);
                tbl.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                tbl.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                tbl.SetAutoFilter();
                ws.Columns(2, headers.Length + 1).AdjustToContents(3, 100);

                wb.SaveAs(sfd.FileName);
                ShowToast("検索結果台帳をExcelで保存しました");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Excel保存エラー: " + ex.Message);
            }
        }

        private void SearchExportCsvButton_Click(object sender, RoutedEventArgs e)
        {
            if (_allSearchResults.Count == 0) return;

            var sfd = new SaveFileDialog
            {
                Filter = "CSV (Comma delimited) (*.csv)|*.csv",
                FileName = $"Search_Results_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (sfd.ShowDialog() != true) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Name,Type,FormattedSize,SizeBytes,LastWriteTime,Extension,PathLength,FullPath,MatchedReason");
                foreach (var item in _allSearchResults)
                {
                    sb.AppendLine($"\"{EscapeCsv(item.Name)}\",\"{(item.IsDirectory ? "Folder" : "File")}\",\"{item.FormattedSize}\",{item.SizeBytes},\"{item.FormattedDate}\",\"{item.Extension}\",{item.PathLength},\"{EscapeCsv(item.FullPath)}\",\"{EscapeCsv(item.HasSnippet ? item.ContentSnippet! : item.MatchedReason)}\"");
                }

                File.WriteAllText(sfd.FileName, sb.ToString(), new UTF8Encoding(true)); // BOM付きUTF-8
                ShowToast("CSVを出力しました");
            }
            catch (Exception ex)
            {
                MessageBox.Show("CSV保存エラー: " + ex.Message);
            }
        }

        private static string EscapeCsv(string val)
        {
            return (val ?? string.Empty).Replace("\"", "\"\"");
        }

        #endregion
    }
}
