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

            if (string.IsNullOrWhiteSpace(scope))
            {
                MessageBox.Show(UiText("有効な検索対象フォルダを入力してください。", "Enter a valid folder to search."), UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _linkFixCts?.Cancel();
            _linkFixCts = new CancellationTokenSource();

            GlobalProgressBar.Visibility = Visibility.Visible;
            GlobalProgressBar.IsIndeterminate = true;

            IProgress<string> progress = new Progress<string>(msg => StatusTextBlock.Text = msg);

            try
            {
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync(_linkFixCts.Token);
                var scanRes = await host.ScanBrokenLinksAsync(scope, oldPattern, newPattern, progress, _linkFixCts.Token);
                var items = scanRes.BrokenLinks.Select(FolderMorpher.HostClient.LinkFixDtoMapper.ToViewItem).ToList();

                // M3対策: Officeファイル内部リンクも含める場合
                if (LinkIncludeOfficeCheckBox.IsChecked == true)
                {
                    var officeItems = scanRes.OfficeLinks.Select(FolderMorpher.HostClient.LinkFixDtoMapper.ToViewItem);
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
                ShowToast(UiText($"切断リンクスキャン完了: {items.Count} 件検出", $"Broken link scan complete: {items.Count} found"));
            }
            catch (OperationCanceledException)
            {
                StatusTextBlock.Text = UiText("スキャンを中止しました。", "Scan canceled.");
            }
            catch (Exception ex)
            {
                MessageBox.Show(UiText($"スキャン失敗: {ex.Message}", $"Scan failed: {ex.Message}"), UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                MessageBox.Show(UiText("修復対象のショートカットがありません。先に切断リンク検出スキャンを実行してください。", "No shortcuts to repair. Run a broken link scan first."), UiText("情報", "Information"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var targets = items.Where(i => i.NeedsFix).ToList();
            if (targets.Count == 0)
            {
                MessageBox.Show(UiText("修復が必要な項目はありません。", "No items need repair."), UiText("情報", "Information"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _lastLinkFixTargets = targets;
            LinkFixDiffDataGrid.ItemsSource = targets;
            LinkFixDiffSummaryText.Text = UiText($"📊 修復対象: {targets.Count}件 (各ファイル .bak 自動バックアップ生成)", $"📊 To repair: {targets.Count} (automatic .bak backup per file)");
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
                StatusTextBlock.Text = UiText($"修復中: {Path.GetFileName(p.Path)} ({(p.Success ? "成功" : "失敗")})", $"Repairing: {Path.GetFileName(p.Path)} ({(p.Success ? "success" : "failed")})");
            });

            try
            {
                using var cts = new CancellationTokenSource();
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync(cts.Token);
                var hostProgress = new Progress<string>(s => StatusTextBlock.Text = s);
                var applyReq = new FolderMorpher.Contracts.LinkFixApplyRequestDto
                {
                    TargetShortcuts = _lastLinkFixTargets.Select(FolderMorpher.HostClient.LinkFixDtoMapper.ToDto).ToList()
                };
                var applyRes = await host.RepairBrokenLinksAsync(applyReq, hostProgress, cts.Token);
                var successCount = applyRes.RepairedCount;
                LinkItemsDataGrid.Items.Refresh();
                LinkFixDiffModalOverlay.Visibility = Visibility.Collapsed;

                // Verify: 処理結果の検証
                if (successCount == _lastLinkFixTargets.Count)
                {
                    ShowToast(UiText($"✅ ショートカット修復完了 (検証済): {successCount} / {_lastLinkFixTargets.Count} 件 全て修復", $"✅ Shortcut repair verified: {successCount} / {_lastLinkFixTargets.Count} repaired"));
                }
                else
                {
                    ShowToast(UiText($"⚠️ ショートカット修復完了 (一部失敗): {successCount} / {_lastLinkFixTargets.Count} 件", $"⚠️ Shortcut repair completed with failures: {successCount} / {_lastLinkFixTargets.Count}"));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(UiText($"修復実行エラー: {ex.Message}", $"Repair failed: {ex.Message}"), UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                GlobalProgressBar.Visibility = Visibility.Collapsed;
            }
        }
        private async void LinkGenerateGpoButton_Click(object sender, RoutedEventArgs e)
        {
            var oldPattern = LinkOldPatternTextBox.Text.Trim();
            var newPattern = LinkNewPatternTextBox.Text.Trim();

            if (string.IsNullOrWhiteSpace(oldPattern) || string.IsNullOrWhiteSpace(newPattern))
            {
                MessageBox.Show(UiText("置換前（旧パス）と置換後（新パス）を入力してください。", "Enter the old and new paths."), UiText("入力確認", "Check input"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var dialog = new SaveFileDialog
            {
                Title = UiText("GPOログオンスクリプトの保存先を選択", "Save GPO logon script"),
                Filter = UiText("PowerShell スクリプト (*.ps1)|*.ps1|すべてのファイル (*.*)|*.*", "PowerShell script (*.ps1)|*.ps1|All files (*.*)|*.*"),
                FileName = "Repair-Shortcuts.ps1"
            };

            if (dialog.ShowDialog() == true)
            {
                try
                {
                    var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                    await host.GenerateGpoLogonScriptAsync(dialog.FileName, oldPattern, newPattern, CancellationToken.None);
                    ShowToast(UiText("GPOログオンスクリプトを生成しました", "GPO logon script generated"));
                    ShellHelper.SelectInExplorer(dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(UiText($"スクリプト生成失敗: {ex.Message}", $"Script generation failed: {ex.Message}"), UiText("エラー", "Error"), MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
        #endregion
    }
}
