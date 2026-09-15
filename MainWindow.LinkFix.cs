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

            IProgress<string> progress = new Progress<string>(msg => StatusTextBlock.Text = msg);

            try
            {
                var items = await _linkFixService.ScanShortcutsAsync(scope, oldPattern, newPattern, progress, _linkFixCts.Token);

                // M3対策: Officeファイル内部リンクも含める場合
                if (LinkIncludeOfficeCheckBox.IsChecked == true)
                {
                    progress.Report("Officeファイル内部リンクを走査中...");
                    var officeItems = await _officeLinkService.ScanOfficeLinksAsync(scope, oldPattern, newPattern, progress, _linkFixCts.Token);
                    foreach (var off in officeItems)
                    {
                        items.Add(new LinkFixItem
                        {
                            FilePath = off.FilePath,
                            FileName = off.FileName,
                            FileType = $"Office ({off.Extension}) - {off.LinkType}",
                            OldTarget = off.FoundPattern,
                            NewTarget = off.TargetReplacement,
                            Status = off.Status,
                            AssociatedOfficeItem = off
                        });
                    }
                }

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

        private List<LinkFixItem> _lastLinkFixTargets = new();

        private void LinkFixExecuteButton_Click(object sender, RoutedEventArgs e)
        {
            var items = LinkItemsDataGrid.ItemsSource as List<LinkFixItem>;
            if (items == null || items.Count == 0)
            {
                MessageBox.Show("修復対象のショートカットがありません。先に切断リンク検出スキャンを実行してください。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var targets = items.Where(i => i.NeedsFix).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show("修復が必要な項目はありません。", "情報", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _lastLinkFixTargets = targets;
            LinkFixDiffDataGrid.ItemsSource = targets;
            LinkFixDiffSummaryText.Text = $"📊 修復対象: {targets.Count}件 (各ファイル .bak 自動バックアップ生成)";
            LinkFixDiffModalOverlay.Visibility = Visibility.Visible;
        }

        private void LinkFixDiffModalClose_Click(object sender, RoutedEventArgs e)
        {
            LinkFixDiffModalOverlay.Visibility = Visibility.Collapsed;
        }

        private async void LinkFixDiffModalApply_Click(object sender, RoutedEventArgs e)
        {
            if (_lastLinkFixTargets == null || _lastLinkFixTargets.Count == 0)
            {
                LinkFixDiffModalOverlay.Visibility = Visibility.Collapsed;
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
                var successCount = await _linkFixService.ExecuteFixAsync(_lastLinkFixTargets, progress, cts.Token);
                LinkItemsDataGrid.Items.Refresh();
                LinkFixDiffModalOverlay.Visibility = Visibility.Collapsed;

                // Verify: 処理結果の検証
                if (successCount == _lastLinkFixTargets.Count)
                {
                    ShowToast($"✅ ショートカット修復完了 (検証済): {successCount} / {_lastLinkFixTargets.Count} 件 全て修復");
                }
                else
                {
                    ShowToast($"⚠️ ショートカット修復完了 (一部失敗): {successCount} / {_lastLinkFixTargets.Count} 件");
                }
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
                    ShellHelper.SelectInExplorer(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"スクリプト生成失敗: {ex.Message}", "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        #endregion
    }
}