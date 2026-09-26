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
        #region Localization (i18n)
        private async void LanguageToggleButton_Click(object sender, RoutedEventArgs e)
        {
            LocalizationService.Instance.ToggleLanguage();
            var language = LocalizationService.Instance.CurrentLanguage == AppLanguage.English ? "en" : "ja";
            AppSettingsService.Instance.Current.Language = language;
            AppSettingsService.Instance.Save();
            try
            {
                var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                await host.SetLanguageAsync(language);
            }
            catch (Exception ex) { Debug.WriteLine($"Host language update failed: {ex}"); }
        }

        private void ApplyClientModeLayout()
        {
            if (!ClientModeState.IsClientMode) return;

            // クライアントモード（FolderCleaner）では管理者専用タブを非表示
            if (NavTabLiveAcl != null) NavTabLiveAcl.Visibility = Visibility.Collapsed;
            if (NavTabSimulation != null) NavTabSimulation.Visibility = Visibility.Collapsed;
            if (NavTabLinkFix != null) NavTabLinkFix.Visibility = Visibility.Collapsed;

            Title = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese
                ? "FolderCleaner - 容量分析 & ファイル監査・写真軽量化 クライアント"
                : "FolderCleaner - Storage Analyzer & Cleanup Client";
        }

        private void ApplyLocalization()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(ApplyLocalization);
                return;
            }

            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;

            // バージョン表示動的反映
            if (SidebarVersionText != null)
            {
                var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
                string verStr = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v2.1.2";
                SidebarVersionText.Text = ClientModeState.IsClientMode
                    ? $"FolderCleaner {verStr}"
                    : $"FolderMorpher {verStr}";
            }
            if (SidebarSubtitleText != null)
            {
                SidebarSubtitleText.Text = ClientModeState.IsClientMode
                    ? Strings.AppSubtitleCleaner
                    : Strings.AppSubtitle;
            }

            ApplyClientModeLayout();

            // 言語切り替えボタン自体の表示（次に切り替わる言語を提示）
            LanguageToggleButton.Content = Strings.LanguageToggleText;
            LanguageToggleButton.ToolTip = Strings.LanguageToggleToolTip;
            LanguageToggleButtonMini.ToolTip = LanguageToggleButton.ToolTip;

            // サイドバー 開閉ボタン & タブ名 & ToolTip
            if (SidebarToggleButton != null) SidebarToggleButton.ToolTip = Strings.ToggleSidebarToolTip;
            NavTabStorage.Content = Strings.TabStorage;
            NavTabStorage.ToolTip = Strings.TabStorageToolTip;
            NavTabLiveAcl.Content = Strings.TabLiveAcl;
            NavTabLiveAcl.ToolTip = Strings.TabLiveAclToolTip;
            NavTabSimulation.Content = Strings.TabSimulation;
            NavTabSimulation.ToolTip = Strings.TabSimulationToolTip;
            NavTabLinkFix.Content = Strings.TabLinkFix;
            NavTabLinkFix.ToolTip = Strings.TabLinkFixToolTip;
            NavTabAudit.Content = Strings.TabAudit;
            NavTabAudit.ToolTip = Strings.TabAuditToolTip;
            NavTabMedia.Content = Strings.TabMedia;
            NavTabMedia.ToolTip = Strings.TabMediaToolTip;
            if (NavTabSearch != null)
            {
                NavTabSearch.Content = Strings.TabSearch;
                NavTabSearch.ToolTip = Strings.TabSearchToolTip;
            }

            // ==========================================
            // Tab 0 (Storage Explorer)
            // ==========================================
            AddStorageTabButton.Content = isJa ? "＋ 新しいタブ" : "＋ New Tab";
            StorageBrowseButton.Content = isJa ? "参照..." : "Browse...";
            ScanButton.Content = isJa ? "スキャン開始" : "Start Scan";
            CancelButton.Content = isJa ? "中止" : "Cancel";
            ExportButton.Content = isJa ? "Excel / CSV 出力" : "Export Excel/CSV";
            TabHistoryButton.Content = isJa ? "📈 容量推移グラフ" : "📈 History Graph";

            if (CtxTreeOpenExplorer != null) CtxTreeOpenExplorer.Header = isJa ? "📂 エクスプローラーで開く" : "📂 Open in Explorer";
            if (CtxTreeCopyPath != null) CtxTreeCopyPath.Header = isJa ? "📋 パスをコピー" : "📋 Copy Path";
            if (CtxTreeScanSubtree != null) CtxTreeScanSubtree.Header = isJa ? "🔍 このフォルダーをルートにしてスキャン" : "🔍 Scan This Subfolder as Root";
            if (CtxTreeEditAcl != null) CtxTreeEditAcl.Header = isJa ? "🛡️ このフォルダーのNTFS権限を確認・編集" : "🛡️ View/Edit NTFS Permissions";

            if (CtxTopOpenExplorer != null) CtxTopOpenExplorer.Header = isJa ? "📂 エクスプローラーで表示" : "📂 Reveal in Explorer";
            if (CtxTopCopyPath != null) CtxTopCopyPath.Header = isJa ? "📋 パスをコピー" : "📋 Copy Path";

            if (TopFilesDataGrid != null) TopFilesDataGrid.ToolTip = isJa ? "ダブルクリックでエクスプローラーを開く" : "Double-click to open in Explorer";
            if (FolderChildSharesDataGrid != null) FolderChildSharesDataGrid.ToolTip = isJa ? "ダブルクリックで該当フォルダへドリルダウン" : "Double-click to drill down";

            StorageKpiScannedSizeTitle.Text = isJa ? "スキャン対象 容量" : "Scanned Capacity";
            StorageKpiDiffTrendTitle.Text = isJa ? "前回差分推移" : "Historical Growth";

            if (InsightsTargetScopeTextBlock.Text == "スコープ: 全体" || InsightsTargetScopeTextBlock.Text == "Scope: Entire Scan")
            {
                InsightsTargetScopeTextBlock.Text = isJa ? "スコープ: 全体" : "Scope: Entire Scan";
            }
            if (TrendDiffTextBlock.Text == "比較データなし" || TrendDiffTextBlock.Text == "No comparison data")
            {
                TrendDiffTextBlock.Text = isJa ? "比較データなし" : "No comparison data";
            }
            if (LastScanDateTextBlock.Text == "初回スキャン" || LastScanDateTextBlock.Text == "Initial scan")
            {
                LastScanDateTextBlock.Text = isJa ? "初回スキャン" : "Initial scan";
            }
            if (TotalFilesTextBlock.Text == "0 ファイル / 0 フォルダ" || TotalFilesTextBlock.Text == "0 Files / 0 Folders")
            {
                TotalFilesTextBlock.Text = isJa ? "0 ファイル / 0 フォルダ" : "0 Files / 0 Folders";
            }

            ColTreeName.Header = isJa ? "フォルダー / ファイル名" : "Folder / File Name";
            ColTreeSize.Header = isJa ? "容量" : "Size";
            ColTreeShare.Header = isJa ? "全体占有率" : "% of Scanned";
            ColTreeCount.Header = isJa ? "配下ファイル数" : "Item Count";
            ColTreeModified.Header = isJa ? "最終更新日時" : "Last Modified";

            StorageTopFilesTitleText.Text = isJa ? "容量上位ファイル Top 10" : "Top 10 Largest Files";
            ColTopFileName.Header = isJa ? "ファイル名" : "File Name";
            ColTopFileSize.Header = isJa ? "容量" : "Size";

            StorageDirectSharesTitleText.Text = isJa ? "📁 選択フォルダーの内訳" : "📁 Direct Children Breakdown";
            StorageDirectSharesSubText.Text = string.Empty;
            StorageDirectSharesSubText.Visibility = Visibility.Collapsed;
            ColShareName.Header = isJa ? "直下アイテム" : "Direct Child Item";
            ColShareSize.Header = isJa ? "容量" : "Size";
            ColShareRatio.Header = isJa ? "直下比率" : "Subfolder Share";

            // ==========================================
            // Tab 1 (Live ACL & Effective Access)
            // ==========================================
            LiveAclStudioControl?.ApplyLocalization(isJa);

            // ==========================================
            // Tab 2 (Simulation Studio)
            // ==========================================
            SimTargetBrowseButton.Content = isJa ? "参照..." : "Browse...";
            UpdateSimCloneButtonState();
            SimUndoButton.Content = isJa ? "↩️ 戻る" : "↩️ Undo";
            SimUndoButton.ToolTip = isJa ? "直前のツリー変更を取り消す (Ctrl+Z)" : "Undo last tree change (Ctrl+Z)";
            SimAddRootFolderButton.Content = Strings.AddRootFolder;
            SimSaveProjectButton.Content = Strings.Save;
            SimSaveProjectButton.ToolTip = Strings.SaveProjectToolTip;
            SimLoadProjectButton.Content = Strings.Load;
            SimLoadProjectButton.ToolTip = Strings.LoadProjectToolTip;
            SimDiffReviewButton.Content = Strings.ReviewDiffs;
            SimDiffReviewButton.ToolTip = Strings.ReviewDiffsToolTip;
            SimDeploySkeletonButton.Content = Strings.DeploySkeleton;
            SimDeploySkeletonButton.ToolTip = Strings.DeploySkeletonToolTip;
            SimExportScriptsButton.Content = Strings.ExportScripts;
            SimExportScriptsButton.ToolTip = Strings.ExportScriptsToolTip;
            SimExportExcelButton.Content = Strings.ExportSimExcel;
            SimExportExcelButton.ToolTip = Strings.ExportSimExcelToolTip;
            SimInheritCheckBox.Content = Strings.InheritFromParent;
            SimOpenSecModalButton.Content = Strings.AdvancedSecurity;

            if (SimInspectorTitleText != null) SimInspectorTitleText.Text = Strings.SimInspectorTitle;
            if (SimSourceBrowseButton != null)
            {
                SimSourceBrowseButton.Content = isJa ? "参照..." : "Browse...";
                SimSourceBrowseButton.ToolTip = Strings.BrowseSourceToolTip;
            }

            if (SimEmptyStateTitleText != null) SimEmptyStateTitleText.Text = Strings.SimEmptyStateTitle;
            if (SimEmptyStateDescText != null) SimEmptyStateDescText.Text = Strings.SimEmptyStateDesc;

            if (SimAdSyncBadgeText != null) SimAdSyncBadgeText.Text = Strings.AdSyncBadge;
            if (SimAdSyncBadgeBorder != null) SimAdSyncBadgeBorder.ToolTip = Strings.AdSyncToolTip;
            if (SimAdRefreshButton != null) SimAdRefreshButton.ToolTip = Strings.AdRefreshToolTip;
            UpdateSimulationDomainBadge();

            if (SimProjectNameTextBox.Text == "新ファイルサーバー移行設計_Ver1" || SimProjectNameTextBox.Text == "New File Server Migration Plan_Ver1")
            {
                SimProjectNameTextBox.Text = Strings.DefaultProjectName;
            }
            if (SimSelectedFolderNameText.Text == "(未選択 - 左のフォルダをクリック)" ||
                SimSelectedFolderNameText.Text == "(未選択 - 上のフォルダをクリック)" ||
                SimSelectedFolderNameText.Text == "(None selected - Click a folder on left)" ||
                SimSelectedFolderNameText.Text == "(None selected - Click a folder above)")
            {
                SimSelectedFolderNameText.Text = Strings.NoneSelectedClickFolder;
            }

            SimTargetRootLabel.Text = Strings.TargetRootLabel;
            SimSourceTitleText.Text = Strings.SourceTitle;
            SimSourceSubText.Text = Strings.SourceSubText;
            SimMockTreeTitleText.Text = Strings.MockTreeTitle;
            SimMockTreeSubText.Text = Strings.MockTreeSubText;
            SimSelectedFolderPrefixText.Text = Strings.SelectedFolderPrefix;
            SimMappingTitleText.Text = Strings.SimMappingTitle;
            SimAclTitleText.Text = Strings.SimAclTitle;
            SimAdHeaderTitle.Text = Strings.SimAdHeaderTitle;
            SimAdHeaderSubText.Text = Strings.SimAdHeaderSubText;

            // Simulation Tree ContextMenu
            SimCtxNewSubfolder.Header = Strings.CtxNewSubfolder;
            SimCtxRename.Header = Strings.CtxRenameFolder;
            SimCtxPromoteRoot.Header = Strings.CtxPromoteRoot;
            SimCtxPromote.Header = Strings.CtxPromote;
            SimCtxDemote.Header = Strings.CtxDemote;
            SimCtxDelete.Header = Strings.Delete;

            // 仮想ツリーの各ノードの表示言語更新
            foreach (var root in _simRootFolders)
            {
                root.NotifyLanguageChanged();
            }

            // ==========================================
            // Tab 4 (LinkFixer)
            // ==========================================
            LinkFixHeaderTitle.Text = isJa ? "🔗 ショートカット ＆ Officeリンク修復" : "🔗 Shortcut & Office Link Repair";
            LinkFixHeaderDesc.Text = isJa ? "ファイルサーバー移行後に切断されたショートカット (.lnk) および Excel 内部リンク数式 (.xlsx / .xlsm) を高速検出し、新パスへ一括書き換えします。" : "Quickly scans and repairs broken shortcut (.lnk) targets and Excel formula references (.xlsx / .xlsm) after file server migrations.";
            LinkSearchScopeLabel.Text = isJa ? "走査対象フォルダー (クライアントPCまたはサーバー)" : "Target Scan Directory (Client PC or File Server)";
            LinkOldPatternLabel.Text = isJa ? "旧サーバーパス (置換前)" : "Old Server Path (To Replace)";
            LinkNewPatternLabel.Text = isJa ? "新サーバーパス (置換後)" : "New Server Path (Replacement)";
            LinkTableTitleText.Text = isJa ? "検出された切断リンク一覧" : "Detected Broken Links";

            LinkGenerateGpoButton.Content = isJa ? "📜 GPOログオンスクリプト生成 (.ps1)" : "📜 Generate GPO Script (.ps1)";
            LinkGenerateGpoButton.ToolTip = isJa ? "全社PCのデスクトップ/マイドキュメント等のショートカットを自動修復するスクリプトを出力" : "Generate logon script (.ps1) to repair shortcuts across client PCs";
            LinkScanButton.Content = isJa ? "切断リンク検出スキャン" : "Scan Broken Links";
            LinkFixExecuteButton.Content = isJa ? "⚡ 一括修復を実行 (バックアップ付)" : "⚡ Execute Fix (with Backup)";
            LinkFixExecuteButton.ToolTip = isJa ? "修復対象のショートカット一覧と置換差分をチェック" : "Check list of shortcuts and preview replacements";
            LinkIncludeOfficeCheckBox.Content = isJa ? "Officeファイル内部リンク (.xlsx/.xlsm) も対象に含める" : "Include Office internal links (.xlsx/.xlsm)";

            ColLinkFileName.Header = isJa ? "ファイル名" : "File Name";
            ColLinkFileType.Header = isJa ? "種別" : "Type";
            ColLinkOldTarget.Header = isJa ? "置換前の旧リンク先" : "Old Target Path";
            ColLinkNewTarget.Header = isJa ? "置換後の新リンク先" : "New Target Path";
            ColLinkStatus.Header = isJa ? "状態" : "Status";

            // ==========================================
            // Tab 4 (Audit & Hygiene)
            // ==========================================
            AuditBrowseButton.Content = isJa ? "📁 参照" : "📁 Browse...";
            if (AuditStatusText.Text == "待機中" || AuditStatusText.Text == "Ready")
            {
                AuditStatusText.Text = isJa ? "待機中" : "Ready";
            }
            if (AuditKpiTotalFiles.Text == "0 件" || AuditKpiTotalFiles.Text == "0 Items")
            {
                AuditKpiTotalFiles.Text = isJa ? "0 件" : "0 Items";
            }
            AuditHeaderTitle.Text = isJa ? "🧹 ファイルサーバー健全化 ＆ 整理候補発見スタジオ" : "🧹 Storage Hygiene & Candidate Discovery Studio";
            AuditHeaderDesc.Text = isJa ? "世代・旧版、展開済ZIP残骸、墓場フォルダー、完全重複、休眠ファイルを分析し、理由付きで整理候補を提示します。" : "Discovers older versions, extracted archive shadows, graveyard folders, duplicates, and dormant files with explainable reasons.";
            AuditTargetFolderLabel.Text = isJa ? "監査対象ディレクトリ (UNC / ローカル)" : "Target Audit Directory (UNC / Local)";
            if (AuditExcludeFoldersLabel != null) AuditExcludeFoldersLabel.Text = Strings.AuditExcludeFoldersLabel;
            if (AuditExcludeFoldersTextBox != null) AuditExcludeFoldersTextBox.ToolTip = Strings.AuditExcludeFoldersToolTip;
            AuditKpiTotalFilesTitle.Text = isJa ? "総走査ファイル数:" : "Total Files Scanned:";
            if (AuditKpiReadyToCleanTitle != null) AuditKpiReadyToCleanTitle.Text = isJa ? "整理推奨:" : "Recommended:";
            if (AuditKpiVersionFamilyTitle != null) AuditKpiVersionFamilyTitle.Text = isJa ? "世代・旧版:" : "Older Versions:";
            AuditKpiDupWastedTitle.Text = isJa ? "完全重複:" : "Duplicates:";
            AuditKpiDormantSizeTitle.Text = isJa ? "休眠・墓場:" : "Dormant / Abandoned:";

            string auditBaseTitle = isJa ? "検出された整理候補一覧" : "Detected Cleanup Candidates";
            AuditTableTitleText.Text = _lastAuditItems.Count > 0
                ? $"{auditBaseTitle} ({AuditItemsDataGrid?.Items.Count ?? _lastAuditItems.Count:N0} / {_lastAuditItems.Count:N0} 件)"
                : auditBaseTitle;
            if (AuditLiveReductionLabel != null) AuditLiveReductionLabel.Text = isJa ? "選択中の削減見込み: " : "Est. Space Reclaimed: ";
            if (AuditMaxDisplayLabel != null) AuditMaxDisplayLabel.Text = isJa ? "表示件数:" : "Display Limit:";
            if (AuditMaxDisplayComboBox?.Items.Count >= 4)
            {
                ((ComboBoxItem)AuditMaxDisplayComboBox.Items[0]).Content = isJa ? "上位100件 (推奨)" : "Top 100 (Recommended)";
                ((ComboBoxItem)AuditMaxDisplayComboBox.Items[1]).Content = isJa ? "上位300件" : "Top 300";
                ((ComboBoxItem)AuditMaxDisplayComboBox.Items[2]).Content = isJa ? "上位500件" : "Top 500";
                ((ComboBoxItem)AuditMaxDisplayComboBox.Items[3]).Content = isJa ? "全件表示" : "Show All";
            }
            if (AuditSmartSelectLabel != null) AuditSmartSelectLabel.Text = isJa ? "☑️ 一括選択:" : "☑️ Smart Select:";
            if (AuditSmartSelectComboBox?.Items.Count >= 8)
            {
                if (AuditSmartItemPreset != null) AuditSmartItemPreset.Content = isJa ? "選択プリセット..." : "Select Preset...";
                ((ComboBoxItem)AuditSmartSelectComboBox.Items[1]).Content = isJa ? "「整理推奨」を一括選択" : "Select Recommended";
                ((ComboBoxItem)AuditSmartSelectComboBox.Items[2]).Content = isJa ? "世代・旧版の過去版を選択" : "Select Older Versions";
                ((ComboBoxItem)AuditSmartSelectComboBox.Items[3]).Content = isJa ? "展開済ZIP残骸を選択" : "Select Extracted ZIPs";
                if (AuditSmartItemDupCopy != null) AuditSmartItemDupCopy.Content = isJa ? "重複の原本以外を選択" : "Select Duplicate Copies";
                if (AuditSmartItemDormant3Y != null) AuditSmartItemDormant3Y.Content = isJa ? "3年以上前の休眠を選択" : "Select Dormant (>3 Years)";
                if (AuditSmartItemVisible != null) AuditSmartItemVisible.Content = isJa ? "表示中のみすべて選択" : "Select All Visible";
                if (AuditSmartItemClear != null) AuditSmartItemClear.Content = isJa ? "選択をすべて解除" : "Clear All Selections";
            }
            if (AuditFilterLabel != null) AuditFilterLabel.Text = isJa ? "絞り込み:" : "Filter:";
            if (AuditCategoryFilterComboBox?.Items.Count >= 9)
            {
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[0]).Content = isJa ? "すべて表示" : "Show All";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[1]).Content = isJa ? "整理推奨 のみ" : "Recommended Only";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[2]).Content = isJa ? "要確認 のみ" : "Review Needed Only";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[3]).Content = isJa ? "世代・旧版 のみ" : "Older Versions Only";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[4]).Content = isJa ? "展開済ZIP のみ" : "Extracted ZIP Only";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[5]).Content = isJa ? "墓場フォルダー のみ" : "Graveyard Folders Only";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[6]).Content = isJa ? "完全重複 のみ" : "Duplicates Only";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[7]).Content = isJa ? "長期休眠 のみ" : "Dormant Only";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[8]).Content = isJa ? "パス長・禁則 のみ" : "Path / Invalid Only";
            }
            if (AuditSearchPlaceholder != null)
            {
                AuditSearchPlaceholder.Text = isJa ? "🔍 ファイル名/パス検索..." : "🔍 Search file/path...";
            }

            AuditStartButton.Content = isJa ? "🔍 整理候補を発見" : "🔍 Discover Candidates";
            AuditExportExcelButton.Content = isJa ? "📊 Excel台帳出力 (.xlsx)" : "📊 Export Excel (.xlsx)";
            AuditExportExcelButton.ToolTip = isJa ? "整理理由・スコア・最新版パス付きの美麗Excel台帳を出力" : "Generate executive Excel audit report (.xlsx)";
            AuditExportCsvButton.Content = isJa ? "📄 CSV台帳出力" : "📄 Export CSV";
            if (AuditDeleteSelectedButton != null)
            {
                AuditDeleteSelectedButton.Content = isJa ? "🗑️ 選択ファイルを完全削除" : "🗑️ Delete Selected Files";
                AuditDeleteSelectedButton.ToolTip = isJa ? "チェックを入れたファイルを直接完全削除します（※復元不可）" : "Permanently deletes checked files (Cannot be undone)";
            }
            if (AuditHeaderCheckBox != null) AuditHeaderCheckBox.ToolTip = isJa ? "すべて選択 / すべて解除" : "Select All / Deselect All";
            if (AuditCheckVersionFamiliesCheckBox != null) AuditCheckVersionFamiliesCheckBox.Content = isJa ? "世代・旧版" : "Older Versions";
            if (AuditCheckExtractedArchivesCheckBox != null) AuditCheckExtractedArchivesCheckBox.Content = isJa ? "展開済ZIP" : "Extracted ZIPs";
            AuditCheckDuplicatesCheckBox.Content = isJa ? "完全重複" : "Duplicates";
            AuditCheckDormantCheckBox.Content = isJa ? "休眠・墓場 (3年超)" : "Dormant (3+ Yrs)";
            AuditCheckPathLimitsCheckBox.Content = isJa ? "パス長/禁則" : "Path / Invalid";
            if (AuditBandwidthLabel != null) AuditBandwidthLabel.Text = isJa ? "帯域:" : "Bandwidth:";
            if (AuditBandwidthStandardItem != null) AuditBandwidthStandardItem.Content = Strings.AuditBandwidthStandard;
            if (AuditBandwidthUnlimitedItem != null) AuditBandwidthUnlimitedItem.Content = Strings.AuditBandwidthUnlimited;

            if (ColAuditConfidence != null) ColAuditConfidence.Header = isJa ? "整理の目安" : "Clean Readiness";
            ColAuditIssueType.Header = isJa ? "問題種別" : "Issue Type";
            ColAuditDupGroup.Header = isJa ? "整理グループ / 状況" : "Group / Status";
            ColAuditFileName.Header = isJa ? "ファイル名" : "File Name";
            ColAuditSize.Header = isJa ? "容量" : "Size";
            ColAuditModified.Header = isJa ? "最終更新日時" : "Last Modified";
            ColAuditDetail.Header = isJa ? "判定理由 / 内訳" : "Reason / Breakdown";
            ColAuditFullPath.Header = isJa ? "完全パス" : "Full Path";

            if (AuditMenuIgnoreFile != null) AuditMenuIgnoreFile.Header = isJa ? "🛡️ このファイルを整理候補から除外 (次回から非表示)" : "🛡️ Ignore this file from candidate list";
            if (AuditMenuOpenExplorer != null) AuditMenuOpenExplorer.Header = isJa ? "📂 エクスプローラーで表示" : "📂 Show in Explorer";
            if (AuditMenuCopyPath != null) AuditMenuCopyPath.Header = isJa ? "📋 完全パスをコピー" : "📋 Copy Full Path";

            UpdateIgnoredCountBadge();

            // ==========================================
            // Tab 5 (Media Optimizer)
            // ==========================================
            MediaBrowseButton.Content = isJa ? "📁 参照" : "📁 Browse...";
            if (MediaStatusText.Text == "待機中" || MediaStatusText.Text == "Ready")
            {
                MediaStatusText.Text = isJa ? "待機中" : "Ready";
            }
            if (MediaKpiImagesCount.Text == "0 枚" || MediaKpiImagesCount.Text == "0 Items")
            {
                MediaKpiImagesCount.Text = isJa ? "0 枚" : "0 Items";
            }
            if (MediaKpiVideosCount.Text == "0 本" || MediaKpiVideosCount.Text == "0 Videos")
            {
                MediaKpiVideosCount.Text = isJa ? "0 本" : "0 Videos";
            }
            if (MediaKpiOptimizedCount.Text == "0 枚" || MediaKpiOptimizedCount.Text == "0 Items")
            {
                MediaKpiOptimizedCount.Text = isJa ? "0 枚" : "0 Items";
            }
            MediaHeaderTitle.Text = Strings.MediaHeaderTitle;
            MediaHeaderDesc.Text = Strings.MediaHeaderDesc;
            MediaTargetDirLabel.Text = isJa ? "走査対象ディレクトリ (UNC / ローカル)" : "Target Directory (UNC / Local)";
            MediaMaxDimLabel.Text = isJa ? "最大長辺 (px)" : "Max Dimension (px)";
            MediaQualityLabel.Text = isJa ? "画質 (%)" : "Quality (%)";
            MediaMinSizeLabel.Text = isJa ? "最小サイズ (MB)" : "Min Size (MB)";
            MediaKpiImagesCountTitle.Text = isJa ? "走査対象 画像数" : "Photos Found";
            MediaKpiVideosCountTitle.Text = isJa ? "大容量動画 ファイル数" : "Large Videos";
            MediaKpiOptimizedCountTitle.Text = isJa ? "軽量化 完了数" : "Photos Compressed";
            MediaKpiSavedSizeTitle.Text = isJa ? "総削減容量 (解放された空き)" : "Total Capacity Freed";
            MediaTableTitleText.Text = isJa ? "メディア一覧（画像 ＆ 大容量動画）" : "Media List (Images & Large Videos)";

            MediaScanButton.Content = isJa ? "🔍 メディア走査" : "🔍 Scan Media";
            MediaOptimizeButton.Content = isJa ? "🔍 チェック" : "🔍 Check";
            MediaOptimizeButton.ToolTip = isJa ? "軽量化対象の写真一覧と設定差分をチェック" : "Check photos to optimize and preview changes";
            MediaGenVideoBatchButton.Content = isJa ? "🎬 大容量動画 夜間圧縮バッチ出力 (.bat)" : "🎬 Export Nightly Video Batch (.bat)";
            MediaGenVideoBatchButton.ToolTip = isJa ? "GPUハードウェアエンコード (H.265) で動画を一括軽量化するスクリプトを出力" : "Generate GPU H.265 compression batch script for large videos";
            MediaExportExcelButton.Content = isJa ? "📊 Excelレポート出力 (.xlsx)" : "📊 Export Excel (.xlsx)";

            ColMediaType.Header = isJa ? "種別" : "Type";
            ColMediaFileName.Header = isJa ? "ファイル名" : "File Name";
            ColMediaOriginalSize.Header = isJa ? "元容量" : "Original Size";
            ColMediaOptimizedSize.Header = isJa ? "軽量化後" : "Compressed Size";
            ColMediaSavedSize.Header = isJa ? "削減容量" : "Saved Size";
            ColMediaStatus.Header = isJa ? "状態 / 保護" : "Status / Protection";
            ColMediaFullPath.Header = isJa ? "完全パス" : "Full Path";

            // ==========================================
            // Detailed Permission Modal (SecModal)
            // ==========================================
            SecModalTitleText.Text = isJa ? "🛡️ セキュリティの詳細設定 - " : "🛡️ Advanced Security Settings - ";
            SecModalObjectNameLabel.Text = isJa ? "オブジェクト名:" : "Object name:";
            SecModalPrincipalLabel.Text = isJa ? "プリンシパル (対象アカウント):" : "Principal:";
            SecModalTypeLabel.Text = isJa ? "種類:" : "Type:";
            SecModalAppliesToLabel.Text = isJa ? "適用先:" : "Applies to:";
            SecModalBasicPermTitle.Text = isJa ? "基本アクセス許可:" : "Basic permissions:";
            SecModalRealtimeNotice.Text = string.Empty;
            SecModalRealtimeNotice.Visibility = Visibility.Collapsed;
            SecModalAdvPermTitle.Text = isJa ? "⚙️ 高度なアクセス許可:" : "⚙️ Advanced permissions:";
            SecModalAdvPermSubtitle.Text = string.Empty;
            SecModalAdvPermSubtitle.Visibility = Visibility.Collapsed;

            SecChkFullControl.Content = isJa ? "フル コントロール" : "Full control";
            SecChkModify.Content = isJa ? "変更 (Modify)" : "Modify";
            SecChkReadExecute.Content = isJa ? "読み取りと実行" : "Read & execute";
            SecChkList.Content = isJa ? "フォルダーの内容の一覧表示" : "List folder contents";
            SecChkRead.Content = isJa ? "読み取り" : "Read";
            SecChkWrite.Content = isJa ? "書き込み" : "Write";

            SecAdvTraverse.Content = isJa ? "フォルダーのスキャン / ファイルの実行" : "Traverse folder / execute file";
            SecAdvList.Content = isJa ? "フォルダーの一覧 / データの読み取り" : "List folder / read data";
            SecAdvReadAttr.Content = isJa ? "属性の読み取り" : "Read attributes";
            SecAdvReadExtAttr.Content = isJa ? "拡張属性の読み取り" : "Read extended attributes";
            SecAdvCreateFile.Content = isJa ? "ファイルの作成 / データの書き込み" : "Create files / write data";
            SecAdvCreateFolder.Content = isJa ? "フォルダーの作成 / データの追加" : "Create folders / append data";
            SecAdvWriteAttr.Content = isJa ? "属性の書き込み" : "Write attributes";
            SecAdvWriteExtAttr.Content = isJa ? "拡張属性の書き込み" : "Write extended attributes";
            SecAdvDelete.Content = isJa ? "削除" : "Delete";
            SecAdvDeleteSub.Content = isJa ? "サブフォルダーとファイルの削除" : "Delete subfolders and files";
            SecAdvReadPerm.Content = isJa ? "アクセス許可の読み取り" : "Read permissions";
            SecAdvChangePerm.Content = isJa ? "アクセス許可の変更" : "Change permissions";
            SecAdvTakeOwnership.Content = isJa ? "所有権の取得" : "Take ownership";
            SecAdvSync.Content = isJa ? "同期 (Synchronize)" : "Synchronize";

            SecComboItemAllow.Content = isJa ? "許可" : "Allow";
            SecComboItemDeny.Content = isJa ? "拒否" : "Deny";

            SecComboAppliesToAll.Content = isJa ? "このフォルダー、サブフォルダーおよびファイル" : "This folder, subfolders and files";
            SecComboAppliesToFolderOnly.Content = isJa ? "このフォルダーのみ" : "This folder only";
            SecComboAppliesToFolderAndSub.Content = isJa ? "このフォルダーおよびサブフォルダー" : "This folder and subfolders";
            SecComboAppliesToFolderAndFiles.Content = isJa ? "このフォルダーおよびファイル" : "This folder and files";
            SecComboAppliesToSubAndFiles.Content = isJa ? "サブフォルダーおよびファイルのみ" : "Subfolders and files only";
            SecComboAppliesToSubOnly.Content = isJa ? "サブフォルダーのみ" : "Subfolders only";
            SecComboAppliesToFilesOnly.Content = isJa ? "ファイルのみ" : "Files only";

            SecModalCancelButton.Content = isJa ? "キャンセル" : "Cancel";
            SecModalApplyButton.Content = isJa ? "変更を保存" : "Save Changes";

            // ==========================================
            // Diff Modal (変更点 - スケルトン展開)
            // ==========================================
            DiffModalTitleText.Text = isJa ? "⚖️ 変更点" : "⚖️ Changes";
            DiffModalSubTitleText.Text = isJa ? " - 移行設計差分" : " - Migration Architecture Diffs";
            ColDiffType.Header = isJa ? "変化の種別" : "Diff Type";
            ColDiffSource.Header = isJa ? "現行サーバー (Before)" : "Source Server (Before)";
            ColDiffTarget.Header = isJa ? "新環境設計 (After)" : "Target Architecture (After)";
            ColDiffAcl.Header = isJa ? "権限 (ACL) 差分詳細" : "ACL Diff Details";
            DiffModalCloseButton.Content = isJa ? "閉じる" : "Close";
            DiffExportExcelButton.Content = isJa ? "📊 Excel出力" : "📊 Export Excel";
            DiffModalApplyButton.Content = isJa ? "⚡ 適用" : "⚡ Apply";

            // ==========================================
            // LinkFix Diff Modal (変更点 - ショートカット修復)
            // ==========================================
            LinkFixDiffTitleText.Text = isJa ? "⚖️ 変更点" : "⚖️ Changes";
            LinkFixDiffSubTitleText.Text = isJa ? " - ショートカット一括修復" : " - Batch Shortcut Repair";
            ColLinkDiffFileName.Header = isJa ? "ファイル名" : "File Name";
            ColLinkDiffOldTarget.Header = isJa ? "置換前ターゲット (Before)" : "Old Target (Before)";
            ColLinkDiffNewTarget.Header = isJa ? "置換後ターゲット (After)" : "New Target (After)";
            LinkFixDiffModalCloseButton.Content = isJa ? "閉じる" : "Close";
            LinkFixDiffModalApplyButton.Content = isJa ? "⚡ 適用" : "⚡ Apply";

            // ==========================================
            // Media Diff Modal (変更点 - 写真軽量化)
            // ==========================================
            MediaDiffTitleText.Text = isJa ? "⚖️ 変更点" : "⚖️ Changes";
            MediaDiffSubTitleText.Text = isJa ? " - 写真・画像軽量化 (上書き縮小)" : " - Image Optimization (In-Place)";
            ColMediaDiffFileName.Header = isJa ? "ファイル名" : "File Name";
            ColMediaDiffOriginalSize.Header = isJa ? "元容量" : "Original Size";
            ColMediaDiffDimensions.Header = isJa ? "現在の解像度" : "Current Resolution";
            ColMediaDiffFullPath.Header = isJa ? "完全パス" : "Full Path";
            MediaDiffModalCloseButton.Content = isJa ? "閉じる" : "Close";
            MediaDiffModalApplyButton.Content = isJa ? "⚡ 適用" : "⚡ Apply";

            // ==========================================
            // Migration Package Modal (移行パッケージ生成)
            // ==========================================
            if (MigPkgTitleText != null) MigPkgTitleText.Text = Strings.MigPkgTitle;
            if (MigPkgSubtitleText != null) MigPkgSubtitleText.Text = Strings.MigPkgSubtitle;
            if (MigPolicyHeaderLabel != null) MigPolicyHeaderLabel.Text = Strings.MigPolicyHeader;
            if (MigPolicyTopLevelText != null) MigPolicyTopLevelText.Text = Strings.MigPolicyTopLevel;
            if (MigPolicySizeBudgetText != null) MigPolicySizeBudgetText.Text = Strings.MigPolicySizeBudget;
            if (MigPolicySingleBatchText != null) MigPolicySingleBatchText.Text = Strings.MigPolicySingleBatch;
            if (MigOptionsHeaderLabel != null) MigOptionsHeaderLabel.Text = Strings.MigOptionsHeader;
            if (MigModeCopyDatText != null) MigModeCopyDatText.Text = Strings.MigModeCopyDat;
            if (MigModeCopyAllText != null) MigModeCopyAllText.Text = Strings.MigModeCopyAll;
            if (MigIncludeExcelCheck != null) MigIncludeExcelCheck.Content = Strings.MigIncludeExcel;
            if (MigIncludeLockCheck != null) MigIncludeLockCheck.Content = Strings.MigIncludeLock;
            if (MigSpeedLabel != null) MigSpeedLabel.Text = Strings.MigSpeedLabel;
            if (MigDeltaRatioLabel != null) MigDeltaRatioLabel.Text = Strings.MigDeltaRatioLabel;
            if (MigKpiTotalSizeLabel != null) MigKpiTotalSizeLabel.Text = Strings.MigKpiTotalSize;
            if (MigKpiTotalFilesLabel != null) MigKpiTotalFilesLabel.Text = Strings.MigKpiTotalFiles;
            if (MigKpiFullTimeLabel != null) MigKpiFullTimeLabel.Text = Strings.MigKpiFullTime;
            if (MigKpiCutoverTimeLabel != null) MigKpiCutoverTimeLabel.Text = Strings.MigKpiCutoverTime;
            if (MigPkgCancelButton != null) MigPkgCancelButton.Content = Strings.Cancel;
            if (MigPkgExportButton != null) MigPkgExportButton.Content = Strings.MigExportButton;

            // ==========================================
            // Tab 6: Search Studio (統合ファイル検索)
            // ==========================================
            if (SearchExecuteButton != null) SearchExecuteButton.Content = Strings.SearchExecute;
            if (SearchCancelButton != null) SearchCancelButton.Content = Strings.SearchCancel;
            if (SearchClearButton != null) SearchClearButton.Content = Strings.SearchClear;
            if (SearchIncludeFoldersCheckBox != null) SearchIncludeFoldersCheckBox.Content = Strings.SearchIncludeFoldersCheck;
            if (SearchContentCheckBox != null) SearchContentCheckBox.Content = Strings.SearchContentCheck;
            if (SearchDirectBrowseButton != null) SearchDirectBrowseButton.Content = Strings.BrowseWithFolder;
            if (SearchRefreshButton != null)
            {
                SearchRefreshButton.Content = "↻";
                SearchRefreshButton.ToolTip = isJa ? "検索結果を更新" : "Refresh search results";
            }
            if (SearchKpiHitCountTitle != null) SearchKpiHitCountTitle.Text = Strings.SearchKpiHitCount;
            if (SearchKpiTotalSizeTitle != null) SearchKpiTotalSizeTitle.Text = Strings.SearchKpiTotalSize;
            if (SearchKpiElapsedTitle != null) SearchKpiElapsedTitle.Text = Strings.SearchKpiElapsed;
            if (SearchStatusTitle != null) SearchStatusTitle.Text = Strings.SearchStatusLabel;
            if (SearchTableTitleText != null) SearchTableTitleText.Text = Strings.SearchTableTitle;
            if (SearchTargetFolderLabel != null) SearchTargetFolderLabel.Text = Strings.SearchTargetFolder;
            if (SearchTableHintText != null)
            {
                SearchTableHintText.Text = string.Empty;
                SearchTableHintText.Visibility = Visibility.Collapsed;
            }
            if (SearchStatusText != null && (SearchStatusText.Text == "待機中" || SearchStatusText.Text == "Ready"))
            {
                SearchStatusText.Text = Strings.Waiting;
            }
            if (SearchKpiHitCountText != null && (SearchKpiHitCountText.Text == "0 件" || SearchKpiHitCountText.Text == "0 items"))
            {
                SearchKpiHitCountText.Text = isJa ? "0 件" : "0 items";
            }

            // Search Filter Chips & Sort
            if (SearchSortLabel != null) SearchSortLabel.Text = isJa ? "並び替え:" : "Sort by:";
            if (SearchSortComboBox != null)
            {
                var items = SearchSortComboBox.Items;
                if (items.Count >= 5)
                {
                    if (items[0] is ComboBoxItem c0) c0.Content = isJa ? "🎯 関連度順" : "🎯 Relevance";
                    if (items[1] is ComboBoxItem c1) c1.Content = isJa ? "🕒 更新日時 (新しい順)" : "🕒 Modified (Newest)";
                    if (items[2] is ComboBoxItem c2) c2.Content = isJa ? "⏳ 更新日時 (古い順)" : "⏳ Modified (Oldest)";
                    if (items[3] is ComboBoxItem c3) c3.Content = isJa ? "✨ 作成日時 (新しい順)" : "✨ Created (Newest)";
                    if (items[4] is ComboBoxItem c4) c4.Content = isJa ? "📅 作成日時 (古い順)" : "📅 Created (Oldest)";
                }
            }
            if (_searchResults != null)
            {
                foreach (var item in _searchResults)
                {
                    item.NotifyLanguageChanged();
                }
            }


            // Settings Modal
            SettingsButton.ToolTip = isJa ? "環境設定 / Settings" : "Settings";
            SettingsTitleText.Text = isJa ? "⚙️ 環境設定 (Settings)" : "⚙️ Settings";
            SettingsDescText.Text = isJa
                ? "キャッシュ、スナップショット履歴、監査レポートの参照先および保存先を構成します。"
                : "Configure read and write locations for tree caches, snapshot histories, and audit reports.";
            SettingsReadTitleText.Text = isJa ? "📂 キャッシュ・スナップショット 参照先 (読み込み)" : "📂 Cache & Snapshot Read Source";
            SettingsReadDescText.Text = isJa
                ? "共有ファイルサーバー上のマスターキャッシュ（UNCパス等）を指定すると、チーム共通の0秒ツリーや推移履歴を参照できます。"
                : "Specify a master cache folder on a shared file server (e.g. UNC path) to access team-wide 0-second trees and histories.";
            SettingsBrowseReadButton.Content = isJa ? "参照..." : "Browse...";
            SettingsFallbackCheckBox.Content = isJa
                ? "共有参照先にアクセスできない場合は自動でローカルキャッシュを参照する"
                : "Automatically fall back to local cache if shared source is unreachable";
            SettingsWriteTitleText.Text = isJa ? "💾 キャッシュ・スナップショット 保存先 (書き込み)" : "💾 Cache & Snapshot Write Destination";
            SettingsWriteDescText.Text = isJa
                ? "自身がスキャンした結果のツリーキャッシュおよび履歴データの保存場所を選択します。"
                : "Select where your local scans save tree cache and historical data.";
            SettingsWriteLocalText.Text = isJa ? "ローカルに保存" : "Save to Local";
            SettingsWriteLocalSubText.Text = isJa ? " (推奨: マスターキャッシュを上書きしない安全設定)" : " (Recommended: Safe, won't overwrite master cache)";
            SettingsWriteSameText.Text = isJa ? "参照先と同じフォルダーに保存" : "Save to same folder as read source";
            SettingsWriteSameSubText.Text = isJa ? " (管理者・マスター更新者用)" : " (For administrators / master publishers)";
            SettingsWriteCustomText.Text = isJa ? "任意のカスタムフォルダーを指定" : "Specify custom folder";
            SettingsBrowseCustomButton.Content = isJa ? "参照..." : "Browse...";
            SettingsCancelButton.Content = isJa ? "キャンセル" : "Cancel";
            SettingsSaveButton.Content = isJa ? "設定を保存" : "Save Settings";
            if (SettingsReadPathTextBox != null)
            {
                SettingsReadPathTextBox.ToolTip = isJa ? "空の場合はローカル既定値 (%LocalAppData%\\FolderMorpher) を参照します" : "Defaults to local directory (%LocalAppData%\\FolderMorpher) if blank";
            }

            // 既存の空タブのタイトルとステータス
            if (StorageTabs != null)
            {
                foreach (var t in StorageTabs)
                {
                    t.NotifyLanguageChanged();
                    if (t.TabTitle == "新規スキャン" || t.TabTitle == "New Scan")
                    {
                        t.TabTitle = isJa ? "新規スキャン" : "New Scan";
                    }
                    if (t.StatusMessage == "準備完了" || t.StatusMessage == "Ready")
                    {
                        t.StatusMessage = isJa ? "準備完了" : "Ready";
                    }
                }
            }

            // ステータスバー
            if (StatusTextBlock.Text == "準備完了" || StatusTextBlock.Text == "Ready")
            {
                StatusTextBlock.Text = isJa ? "準備完了" : "Ready";
            }

            // 現在のアクティブタブのステータス再反映
            NavTab_Checked(this, new RoutedEventArgs());
        }
        #endregion
    }
}
