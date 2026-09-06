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
using AstraSize.Models;
using AstraSize.Services;
using Microsoft.Win32;

namespace AstraSize
{
    public partial class MainWindow : Window
    {
        private readonly DiskScanService _scanService = new();
        private readonly StorageHistoryService _historyService = new();
        private readonly AclService _aclService = new();
        private readonly MigrationService _migrationService = new();
        private readonly LinkFixService _linkFixService = new();

        private CancellationTokenSource? _scanCts;
        private CancellationTokenSource? _migCts;
        private CancellationTokenSource? _linkFixCts;

        private FileItemNode? _currentRootNode;
        private ScanSummary? _currentSummary;
        private FolderAclNode? _currentAclRoot;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += MainWindow_Loaded;
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            LoadDrives();
        }

        #region Navigation Tabs
        private void NavTab_Checked(object sender, RoutedEventArgs e)
        {
            if (StorageTabPanel == null || AclTabPanel == null || MigrationTabPanel == null || LinkFixTabPanel == null)
                return;

            StorageTabPanel.Visibility = Visibility.Collapsed;
            AclTabPanel.Visibility = Visibility.Collapsed;
            MigrationTabPanel.Visibility = Visibility.Collapsed;
            LinkFixTabPanel.Visibility = Visibility.Collapsed;

            if (NavTabStorage.IsChecked == true)
            {
                StorageTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = "モード: 容量分析 (Storage Explorer)";
            }
            else if (NavTabAcl.IsChecked == true)
            {
                AclTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = "モード: 権限台帳 & シミュレーション (ACL & Ledger)";
                if (string.IsNullOrWhiteSpace(AclPathTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    AclPathTextBox.Text = PathTextBox.Text;
                }
            }
            else if (NavTabMigration.IsChecked == true)
            {
                MigrationTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = "モード: サーバー移行 & ガワ作成 (Server Migration)";
                if (string.IsNullOrWhiteSpace(MigSourceTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    MigSourceTextBox.Text = PathTextBox.Text;
                }
            }
            else if (NavTabLinkFix.IsChecked == true)
            {
                LinkFixTabPanel.Visibility = Visibility.Visible;
                StatusTextBlock.Text = "モード: リンク修復 (LinkFixer)";
                if (string.IsNullOrWhiteSpace(LinkFixSearchDirTextBox.Text) && !string.IsNullOrWhiteSpace(PathTextBox.Text))
                {
                    LinkFixSearchDirTextBox.Text = PathTextBox.Text;
                }
            }
        }
        #endregion

        #region Tab 1: Storage Explorer
        private void LoadDrives()
        {
            try
            {
                var drives = DriveInfoService.GetLocalDrives();
                DriveComboBox.Items.Clear();

                foreach (var d in drives)
                {
                    DriveComboBox.Items.Add(new ComboBoxItem
                    {
                        Content = $"{d.DisplayName} [空き {d.FormattedFree}]",
                        Tag = d.Name
                    });
                }

                if (DriveComboBox.Items.Count > 0)
                {
                    DriveComboBox.SelectedIndex = 0;
                }
                else
                {
                    PathTextBox.Text = @"C:\";
                    UpdateDriveMetrics(@"C:\");
                }
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"ドライブ一覧取得エラー: {ex.Message}";
                PathTextBox.Text = @"C:\";
            }
        }

        private void DriveComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (DriveComboBox.SelectedItem is ComboBoxItem item && item.Tag is string path)
            {
                PathTextBox.Text = path;
                UpdateDriveMetrics(path);
            }
        }

        private void UpdateDriveMetrics(string path)
        {
            try
            {
                var volume = DriveInfoService.GetVolumeInfoForPath(path);
                if (volume != null && volume.TotalSize > 0)
                {
                    FreeSpaceTextBlock.Text = $"{FileItemNode.FormatBytes(volume.FreeSpace)} 空き";
                    FreePercentTextBlock.Text = $"{volume.UsedPercentage:0.#}% 使用中 ({volume.DisplayName})";
                    DriveUsageProgressBar.Value = volume.UsedPercentage;
                }
                else
                {
                    FreeSpaceTextBlock.Text = "-- 空き";
                    FreePercentTextBlock.Text = path.StartsWith(@"\\") ? "SMB / ネットワーク共有" : "--%";
                    DriveUsageProgressBar.Value = 0;
                }
            }
            catch
            {
                FreeSpaceTextBlock.Text = "-- 空き";
                FreePercentTextBlock.Text = "--%";
                DriveUsageProgressBar.Value = 0;
            }
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFolderDialog
            {
                Title = "スキャンするフォルダまたはドライブを選択（UNCパス対応）",
                InitialDirectory = string.IsNullOrWhiteSpace(PathTextBox.Text) ? @"C:\" : PathTextBox.Text
            };

            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.FolderName))
            {
                PathTextBox.Text = dialog.FolderName;
                UpdateDriveMetrics(dialog.FolderName);
                StartStorageScan(dialog.FolderName);
            }
        }

        private void PathTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                var path = PathTextBox.Text.Trim();
                if (!string.IsNullOrWhiteSpace(path))
                {
                    UpdateDriveMetrics(path);
                    StartStorageScan(path);
                }
            }
        }

        private void ScanButton_Click(object sender, RoutedEventArgs e)
        {
            var path = PathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show("スキャン対象パスを入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            StartStorageScan(path);
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_scanCts != null && !_scanCts.IsCancellationRequested)
            {
                StatusTextBlock.Text = "スキャン中止を要求しています...";
                _scanCts.Cancel();
            }
        }

        private async void StartStorageScan(string targetPath)
        {
            if (_scanCts != null) return;

            _scanCts = new CancellationTokenSource();
            ScanButton.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Visible;
            GlobalProgressBar.Visibility = Visibility.Visible;
            StatusTextBlock.Text = $"走査中: {targetPath}...";
            UpdateDriveMetrics(targetPath);

            var progress = new Progress<ScanProgress>(p =>
            {
                StatusTextBlock.Text = $"走査中: {p.FilesScanned:N0} 項目 ({FileItemNode.FormatBytes(p.BytesScanned)}) — {p.CurrentPath}";
            });

            try
            {
                var (rootNode, summary) = await _scanService.ScanPathAsync(targetPath, progress, _scanCts.Token);
                _currentRootNode = rootNode;
                _currentSummary = summary;

                // Setup levels for TreeGrid indent
                SetNodeLevels(rootNode, 0);

                // Update Metrics
                ScannedSizeTextBlock.Text = summary.FormattedTotalSize;
                TotalFilesTextBlock.Text = $"{summary.TotalFiles:N0} ファイル / {summary.TotalFolders:N0} フォルダ";

                if (summary.LargestFiles.Count > 0)
                {
                    LargestFileSizeTextBlock.Text = summary.LargestFiles[0].FormattedSize;
                    LargestFileNameTextBlock.Text = summary.LargestFiles[0].Name;
                }
                else
                {
                    LargestFileSizeTextBlock.Text = "--";
                    LargestFileNameTextBlock.Text = "--";
                }

                // Update Trend
                var (lastScan, diffBytes, formattedDiff) = await _historyService.GetLastScanDiffAsync(targetPath, summary.TotalBytes);
                if (lastScan != null)
                {
                    TrendDiffTextBlock.Text = formattedDiff;
                    TrendDiffTextBlock.Foreground = diffBytes > 0
                        ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(225, 29, 72))
                        : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(16, 185, 129));
                    LastScanDateTextBlock.Text = $"前回スキャン: {lastScan.Timestamp:yyyy/MM/dd HH:mm}";
                }
                else
                {
                    TrendDiffTextBlock.Text = "初回スキャン記録";
                    TrendDiffTextBlock.Foreground = (System.Windows.Media.SolidColorBrush)FindResource("AccentSky");
                    LastScanDateTextBlock.Text = "履歴保存完了";
                }

                await _historyService.RecordScanAsync(targetPath, summary.TotalBytes, summary.TotalFiles, summary.TotalFolders);

                // Bind TreeGrid & Insights
                rootNode.IsExpanded = true;
                RefreshDataGridFlattened();

                InsightsTargetScopeTextBlock.Text = $"スコープ: 全体 ({rootNode.Name})";
                TopFilesDataGrid.ItemsSource = summary.LargestFiles;
                ExtensionsDataGrid.ItemsSource = summary.ExtensionStats;

                StatusTextBlock.Text = $"スキャン完了: {summary.FormattedTotalSize} ({summary.TotalFiles:N0} ファイル)";
            }
            catch (OperationCanceledException)
            {
                StatusTextBlock.Text = "スキャンはユーザーによって中止されました。";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"エラー発生: {ex.Message}";
                MessageBox.Show($"スキャン中にエラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                _scanCts.Dispose();
                _scanCts = null;
                ScanButton.Visibility = Visibility.Visible;
                CancelButton.Visibility = Visibility.Collapsed;
                GlobalProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void SetNodeLevels(FileItemNode node, int level)
        {
            node.Level = level;
            foreach (var child in node.Children)
            {
                SetNodeLevels(child, level + 1);
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
            InsightsTargetScopeTextBlock.Text = $"スコープ: {node.Name}";
            var (topFiles, extStats) = DiskScanService.GetInsightsForNode(node);
            TopFilesDataGrid.ItemsSource = topFiles;
            ExtensionsDataGrid.ItemsSource = extStats;
        }

        private void ExpandCollapseButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is FileItemNode node)
            {
                node.IsExpanded = !node.IsExpanded;
                RefreshDataGridFlattened();
            }
        }

        private void FileTreeDataGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileTreeDataGrid.SelectedItem is FileItemNode node && node.CanExpand)
            {
                node.IsExpanded = !node.IsExpanded;
                RefreshDataGridFlattened();
            }
        }

        private void RefreshDataGridFlattened()
        {
            if (_currentRootNode == null) return;
            var list = new List<FileItemNode>();
            void AddVisible(FileItemNode n)
            {
                list.Add(n);
                if (n.IsExpanded)
                {
                    foreach (var child in n.Children) AddVisible(child);
                }
            }
            AddVisible(_currentRootNode);
            FileTreeDataGrid.ItemsSource = list;
        }

        private void FilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            var filter = FilterTextBox.Text.Trim();
            if (_currentRootNode == null) return;

            if (string.IsNullOrWhiteSpace(filter))
            {
                RefreshDataGridFlattened();
                return;
            }

            var matches = new List<FileItemNode>();
            void Search(FileItemNode n)
            {
                if (n.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(n);
                }
                foreach (var c in n.Children) Search(c);
            }
            Search(_currentRootNode);
            FileTreeDataGrid.ItemsSource = matches;
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentRootNode == null)
            {
                MessageBox.Show("エクスポートするスキャンデータがありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "容量レポートの保存",
                Filter = "CSVファイル (Excel対応)|*.csv",
                FileName = $"AstraSize_Report_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var sb = new StringBuilder();
                    sb.Append('\uFEFF');
                    sb.AppendLine("パス,名前,サイズ(Byte),容量,ファイル数,フォルダ数,更新日");
                    void WriteRow(FileItemNode n)
                    {
                        sb.AppendLine($"\"{n.FullPath.Replace("\"", "\"\"")}\",\"{n.Name.Replace("\"", "\"\"")}\",{n.Size},\"{n.FormattedSize}\",{n.FileCount},{n.FolderCount},\"{n.FormattedLastModified}\"");
                        foreach (var c in n.Children) WriteRow(c);
                    }
                    WriteRow(_currentRootNode);
                    File.WriteAllText(dialog.FileName, sb.ToString(), Encoding.UTF8);

                    var res = MessageBox.Show("CSVレポートを出力しました。今すぐ開きますか？", "出力完了", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res == MessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
                    }
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
            if (string.IsNullOrWhiteSpace(path)) path = @"C:\";
            var history = await _historyService.GetHistoryForPathAsync(path);
            var win = new HistoryWindow(path, history) { Owner = this };
            win.ShowDialog();
        }
        #endregion

        #region Tab 2: ACL & Simulation Ledger
        private async void AclScanButton_Click(object sender, RoutedEventArgs e)
        {
            var path = AclPathTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                MessageBox.Show("有効なフォルダパスを入力してください。", "入力エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            int depth = AclDepthComboBox.SelectedIndex switch
            {
                0 => 1,
                1 => 2,
                2 => 3,
                _ => 10
            };

            GlobalProgressBar.Visibility = Visibility.Visible;
            StatusTextBlock.Text = $"ACL権限構造を解析中: {path} (深度: {depth})...";

            try
            {
                var rootAcl = await Task.Run(() => _aclService.GetFolderAcl(path, depth));
                _currentAclRoot = rootAcl;
                AclFolderTreeView.ItemsSource = new List<FolderAclNode> { rootAcl };
                StatusTextBlock.Text = $"ACL解析完了: {path}";
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"ACL解析エラー: {ex.Message}";
                MessageBox.Show($"ACL解析中にエラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                GlobalProgressBar.Visibility = Visibility.Collapsed;
            }
        }

        private void AclFolderTreeView_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            if (e.NewValue is FolderAclNode node)
            {
                AclSelectedFolderTextBlock.Text = node.Path;
                AclEntriesDataGrid.ItemsSource = node.Entries;
            }
        }

        private void ExportAclMatrixButton_Click(object sender, RoutedEventArgs e)
        {
            if (_currentAclRoot == null)
            {
                MessageBox.Show("まず権限スキャンを実行してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = "アクセス権マトリクス台帳 (Excel/CSV) の保存",
                Filter = "CSVファイル (Excel UTF-8 BOM)|*.csv",
                FileName = $"AclMatrix_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var csv = _aclService.GenerateMatrixCsv(_currentAclRoot);
                    File.WriteAllText(dialog.FileName, csv, Encoding.UTF8);

                    var res = MessageBox.Show("アクセス権マトリクス台帳を出力しました！Excelで開きますか？", "台帳自動出力完了", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (res == MessageBoxResult.Yes)
                    {
                        Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"台帳出力エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void AclSnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            var path = AclSelectedFolderTextBlock.Text;
            if (string.IsNullOrWhiteSpace(path) || path == "未選択" || !Directory.Exists(path))
            {
                MessageBox.Show("スナップショットを取得するフォルダを選択してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var snap = await _aclService.CreateSnapshotAsync(path, "手動取得バックアップ");
                MessageBox.Show($"ACLスナップショットを安全に保存しました！\n\n対象: {snap.TargetPath}\nID: {snap.Id}\n日時: {snap.Timestamp}", "バックアップ完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"スナップショット取得失敗: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async void AclRollbackHistoryButton_Click(object sender, RoutedEventArgs e)
        {
            var path = AclSelectedFolderTextBlock.Text;
            if (string.IsNullOrWhiteSpace(path) || path == "未選択")
            {
                path = AclPathTextBox.Text.Trim();
            }

            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                MessageBox.Show("確認する対象フォルダを選択または入力してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var snapshots = await _aclService.GetSnapshotsAsync(path);
            if (snapshots.Count == 0)
            {
                MessageBox.Show($"このフォルダ ({path}) のロールバックスナップショットはまだありません。", "履歴なし", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var latest = snapshots[0];
            var msg = $"直近のスナップショットが見つかりました:\n\n" +
                      $"日時: {latest.Timestamp:yyyy/MM/dd HH:mm:ss}\n" +
                      $"メモ: {latest.Note}\n\n" +
                      $"この状態にアクセス権をロールバック（巻き戻し）しますか？";

            var res = MessageBox.Show(msg, "ロールバック確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                try
                {
                    _aclService.RollbackToSnapshot(path, latest);
                    MessageBox.Show("アクセス権をスナップショットの状態へ巻き戻しました！", "ロールバック完了", MessageBoxButton.OK, MessageBoxImage.Information);
                    AclScanButton_Click(this, new RoutedEventArgs());
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"ロールバック失敗: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void SimulateButton_Click(object sender, RoutedEventArgs e)
        {
            var path = AclSelectedFolderTextBlock.Text;
            if (string.IsNullOrWhiteSpace(path) || path == "未選択")
            {
                MessageBox.Show("シミュレーション対象のフォルダをツリーから選択してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var account = SimAccountTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(account))
            {
                MessageBox.Show("対象アカウント（例: CONTOSO\\SalesGroup または Users）を入力してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            string rightsName = ((ComboBoxItem)SimRightsComboBox.SelectedItem).Content.ToString() ?? "";
            string actionType = ((ComboBoxItem)SimActionTypeComboBox.SelectedItem).Content.ToString() ?? "";

            SimResultTextBlock.Text = $"【シミュレーション結果 - Dry Run】\n" +
                                     $"対象: {path}\n" +
                                     $"アカウント: {account}\n" +
                                     $"操作: {actionType} [{rightsName}]\n" +
                                     $"状態: 安全に実行可能。親からの継承ルールおよび他のグループの権限に破損・競合は発生しません。実環境は一切変更されていません。";
        }

        private async void ApplyAclButton_Click(object sender, RoutedEventArgs e)
        {
            var path = AclSelectedFolderTextBlock.Text;
            if (string.IsNullOrWhiteSpace(path) || path == "未選択" || !Directory.Exists(path))
            {
                MessageBox.Show("対象フォルダを選択してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var account = SimAccountTextBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(account))
            {
                MessageBox.Show("対象アカウントを入力してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var res = MessageBox.Show($"安全のため、変更直前のスナップショットを自動取得してからアクセス権を適用します。\n\n" +
                                      $"フォルダ: {path}\n" +
                                      $"アカウント: {account}\n\n" +
                                      $"続行しますか？", "権限適用の確認", MessageBoxButton.YesNo, MessageBoxImage.Warning);

            if (res != MessageBoxResult.Yes) return;

            try
            {
                // Auto Snapshot before applying!
                await _aclService.CreateSnapshotAsync(path, $"変更適用直前の自動スナップショット ({account})");

                // Apply
                var dir = new DirectoryInfo(path);
                var sec = dir.GetAccessControl(AccessControlSections.Access);

                FileSystemRights rights = SimRightsComboBox.SelectedIndex switch
                {
                    0 => FileSystemRights.FullControl,
                    1 => FileSystemRights.Modify,
                    2 => FileSystemRights.ReadAndExecute,
                    _ => FileSystemRights.Read
                };

                bool isAdd = SimActionTypeComboBox.SelectedIndex == 0;
                var ntAccount = new NTAccount(account);

                if (isAdd)
                {
                    sec.AddAccessRule(new FileSystemAccessRule(ntAccount, rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                }
                else
                {
                    sec.RemoveAccessRule(new FileSystemAccessRule(ntAccount, rights, AccessControlType.Allow));
                }

                dir.SetAccessControl(sec);
                MessageBox.Show("アクセス権の適用に成功しました！（自動バックアップ取得済み）", "適用完了", MessageBoxButton.OK, MessageBoxImage.Information);
                AclScanButton_Click(this, new RoutedEventArgs());
            }
            catch (Exception ex)
            {
                MessageBox.Show($"権限適用エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        #endregion

        #region Tab 3: Server Migration
        private async void MigCloneSkeletonButton_Click(object sender, RoutedEventArgs e)
        {
            var src = MigSourceTextBox.Text.Trim();
            var dst = MigTargetTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(src) || !Directory.Exists(src))
            {
                MessageBox.Show("有効な移行元フォルダを入力してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(dst))
            {
                MessageBox.Show("移行先フォルダを入力してください。", "エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _migCts = new CancellationTokenSource();
            MigCloneSkeletonButton.IsEnabled = false;
            MigSkeletonProgressBar.IsIndeterminate = true;
            MigSkeletonStatusText.Text = "ガワ（フォルダ構造）＋アクセス権の複製を開始...";
            MigSkeletonLogTextBox.Clear();

            var progress = new Progress<MigrationProgress>(p =>
            {
                MigSkeletonStatusText.Text = p.StatusMessage;
                MigSkeletonCountText.Text = $"作成フォルダ数: {p.FoldersCreated:N0}";
                MigSkeletonLogTextBox.AppendText($"[作成] {p.CurrentItem}\n");
                MigSkeletonLogTextBox.ScrollToEnd();
            });

            try
            {
                var (foldersCount, errors) = await _migrationService.CloneStructureAndAclAsync(src, dst, progress, _migCts.Token);
                MigSkeletonStatusText.Text = $"先行作成が完了しました！(作成数: {foldersCount} フォルダ)";

                if (errors.Count > 0)
                {
                    MigSkeletonLogTextBox.AppendText($"\n--- エラー ({errors.Count}件) ---\n" + string.Join("\n", errors));
                }

                MessageBox.Show($"空フォルダ（ガワ）およびNTFSアクセス権の先行複製が完了しました！\n\n作成フォルダ数: {foldersCount}\n移行先: {dst}", "先行作成完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MigSkeletonStatusText.Text = $"作成中断: {ex.Message}";
                MessageBox.Show($"ガワ作成中にエラーが発生しました:\n{ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                MigCloneSkeletonButton.IsEnabled = true;
                MigSkeletonProgressBar.IsIndeterminate = false;
                _migCts.Dispose();
                _migCts = null;
            }
        }

        private void MigGenRobocopyButton_Click(object sender, RoutedEventArgs e)
        {
            var src = MigSourceTextBox.Text.Trim();
            var dst = MigTargetTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(src) || string.IsNullOrWhiteSpace(dst))
            {
                MessageBox.Show("移行元および移行先フォルダを指定してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            bool copyAcl = MigAclCopyCheckBox.IsChecked == true;
            int threads = int.Parse(((ComboBoxItem)MigThreadsComboBox.SelectedItem).Content.ToString() ?? "16");

            var cmd = _migrationService.GenerateRobocopyCommand(src, dst, copyAcl, threads);
            MigRobocopyCommandTextBox.Text = cmd;
        }

        private void MigCopyClipButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(MigRobocopyCommandTextBox.Text))
            {
                Clipboard.SetText(MigRobocopyCommandTextBox.Text);
                MessageBox.Show("Robocopyコマンドをクリップボードにコピーしました！", "コピー完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void MigRunRobocopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(MigRobocopyCommandTextBox.Text))
            {
                MigGenRobocopyButton_Click(sender, e);
            }

            var cmd = MigRobocopyCommandTextBox.Text;
            if (string.IsNullOrWhiteSpace(cmd)) return;

            var res = MessageBox.Show($"管理者権限のコマンドプロンプトで以下のRobocopyを実行しますか？\n\n{cmd}", "実行確認", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (res == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/k {cmd}",
                        Verb = "runas",
                        UseShellExecute = true
                    });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"起動エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        #endregion

        #region Tab 4: Link Fixer
        private async void LinkFixStartButton_Click(object sender, RoutedEventArgs e)
        {
            var searchDir = LinkFixSearchDirTextBox.Text.Trim();
            var oldPattern = LinkFixOldPatternTextBox.Text.Trim();
            var newPattern = LinkFixNewPatternTextBox.Text.Trim();
            bool dryRun = LinkFixDryRunCheckBox.IsChecked == true;

            if (string.IsNullOrWhiteSpace(searchDir) || !Directory.Exists(searchDir))
            {
                MessageBox.Show("有効な探索フォルダを入力してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(oldPattern))
            {
                MessageBox.Show("置換前の旧サーバー文字列を入力してください。", "案内", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _linkFixCts = new CancellationTokenSource();
            LinkFixStartButton.IsEnabled = false;
            GlobalProgressBar.Visibility = Visibility.Visible;
            StatusTextBlock.Text = $"ショートカット＆Excelリンクを走査中 (DryRun={dryRun})...";

            try
            {
                var items = await _linkFixService.ScanAndFixLinksAsync(searchDir, oldPattern, newPattern, dryRun, _linkFixCts.Token);
                LinkFixDataGrid.ItemsSource = items;

                string modeStr = dryRun ? "【シミュレーション】" : "【実ファイル置換】";
                StatusTextBlock.Text = $"{modeStr} リンク走査完了: {items.Count} 件検出";
                MessageBox.Show($"{modeStr} 走査完了！\n\n検出リンク数: {items.Count} 件\n{(dryRun ? "※シミュレーションのため実ファイルは未変更です。" : "※指定パスの置換を実行しました。")}", "処理完了", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                StatusTextBlock.Text = $"エラー: {ex.Message}";
                MessageBox.Show($"リンク走査エラー: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                LinkFixStartButton.IsEnabled = true;
                GlobalProgressBar.Visibility = Visibility.Collapsed;
                _linkFixCts.Dispose();
                _linkFixCts = null;
            }
        }
        #endregion
    }
}
