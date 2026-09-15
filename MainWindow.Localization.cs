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
        #region Localization (i18n)
        private void LanguageToggleButton_Click(object sender, RoutedEventArgs e)
        {
            LocalizationService.Instance.ToggleLanguage();
        }

        private void ApplyClientModeLayout()
        {
            if (!App.IsClientMode) return;

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
                var ver = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
                string verStr = ver != null ? $"v{ver.Major}.{ver.Minor}.{ver.Build}" : "v2.1.2";
                SidebarVersionText.Text = App.IsClientMode
                    ? $"FolderCleaner {verStr}"
                    : $"FolderMorpher {verStr}";
            }
            if (SidebarSubtitleText != null)
            {
                SidebarSubtitleText.Text = App.IsClientMode
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

            StorageDirectSharesTitleText.Text = isJa ? "選択フォルダーの内訳" : "Folder Content Breakdown";
            StorageDirectSharesSubText.Text = isJa ? "Wクリックで下層へドリルダウン展開" : "Double-click item to drill down in tree";
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
            SimSourceLoadButton.Content = isJa ? "読込" : "Load";
            SimCloneSelectedButton.Content = Strings.CloneSelectedToCenter;
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
            SimSubfolderDropHintText.Text = Strings.SubfolderDropHint;
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
            // Tab 3 (LinkFixer)
            // ==========================================
            LinkFixHeaderTitle.Text = isJa ? "🔗 ショートカット ＆ Officeリンク一括修復（LinkFixer）" : "🔗 Broken Link & Office Reference Repair (LinkFixer)";
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
            if (AuditKpiPathLimits.Text == "0 件" || AuditKpiPathLimits.Text == "0 Items")
            {
                AuditKpiPathLimits.Text = isJa ? "0 件" : "0 Items";
            }
            AuditHeaderTitle.Text = Strings.AuditHeaderTitle;
            AuditHeaderDesc.Text = Strings.AuditHeaderDesc;
            AuditTargetFolderLabel.Text = isJa ? "監査対象ディレクトリ (UNC / ローカル)" : "Target Audit Directory (UNC / Local)";
            if (AuditExcludeFoldersLabel != null) AuditExcludeFoldersLabel.Text = Strings.AuditExcludeFoldersLabel;
            if (AuditExcludeFoldersTextBox != null) AuditExcludeFoldersTextBox.ToolTip = Strings.AuditExcludeFoldersToolTip;
            AuditKpiTotalFilesTitle.Text = isJa ? "総走査ファイル数" : "Total Files Scanned";
            AuditKpiDupWastedTitle.Text = isJa ? "重複ファイルによる無駄" : "Wasted by Duplicates";
            AuditKpiDormantSizeTitle.Text = isJa ? "休眠ファイル容量 (3年超)" : "Dormant Capacity (3+ Yrs)";
            if (AuditKpiPathLimitsTitle != null) AuditKpiPathLimitsTitle.Text = isJa ? "パス長超過 / 禁則文字" : "Path Limit / Invalid Chars";
            string auditBaseTitle = isJa ? "検出された課題・整理候補一覧" : "Detected Issues & Cleanup Candidates";
            AuditTableTitleText.Text = _lastAuditItems.Count > 0
                ? $"{auditBaseTitle} ({_auditVisibleItems.Count:N0} / {_lastAuditItems.Count:N0} 件)"
                : auditBaseTitle;
            if (AuditLiveReductionLabel != null) AuditLiveReductionLabel.Text = isJa ? "選択中の削減見込み: " : "Est. Space Reclaimed: ";
            if (AuditSmartSelectLabel != null) AuditSmartSelectLabel.Text = isJa ? "☑️ 一括選択:" : "☑️ Smart Select:";
            if (AuditSmartItemPreset != null) AuditSmartItemPreset.Content = isJa ? "選択プリセット..." : "Select Preset...";
            if (AuditSmartItemDupCopy != null) AuditSmartItemDupCopy.Content = isJa ? "重複の原本以外を選択" : "Select Duplicate Copies";
            if (AuditSmartItemDormant3Y != null) AuditSmartItemDormant3Y.Content = isJa ? "3年以上前の休眠を選択" : "Select Dormant (>3 Years)";
            if (AuditSmartItemDormant5Y != null) AuditSmartItemDormant5Y.Content = isJa ? "5年以上前の休眠を選択" : "Select Dormant (>5 Years)";
            if (AuditSmartItemVisible != null) AuditSmartItemVisible.Content = isJa ? "表示中のみすべて選択" : "Select All Visible";
            if (AuditSmartItemClear != null) AuditSmartItemClear.Content = isJa ? "選択をすべて解除" : "Clear All Selections";
            if (AuditFilterLabel != null) AuditFilterLabel.Text = isJa ? "絞り込み:" : "Filter:";
            if (AuditCategoryFilterComboBox?.Items.Count >= 4)
            {
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[0]).Content = isJa ? "すべて表示" : "Show All";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[1]).Content = isJa ? "重複ファイルのみ" : "Duplicates Only";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[2]).Content = isJa ? "休眠ファイルのみ" : "Dormant Only";
                ((ComboBoxItem)AuditCategoryFilterComboBox.Items[3]).Content = isJa ? "パス長・禁則のみ" : "Path / Invalid Only";
            }
            if (AuditSearchPlaceholder != null)
            {
                AuditSearchPlaceholder.Text = isJa ? "🔍 ファイル名/パス検索..." : "🔍 Search file/path...";
            }

            AuditStartButton.Content = isJa ? "🔍 監査スキャン開始" : "🔍 Start Audit Scan";
            AuditExportExcelButton.Content = isJa ? "📊 Excelレポート出力 (.xlsx)" : "📊 Export Excel (.xlsx)";
            AuditExportExcelButton.ToolTip = isJa ? "上司・各部署提出用の美麗Excelレポートを生成" : "Generate executive Excel audit report (.xlsx)";
            AuditExportCsvButton.Content = isJa ? "📄 CSV台帳出力" : "📄 Export CSV";
            if (AuditDeleteSelectedButton != null)
            {
                AuditDeleteSelectedButton.Content = isJa ? "🗑️ 選択ファイルを完全削除" : "🗑️ Delete Selected Files";
                AuditDeleteSelectedButton.ToolTip = isJa ? "チェックを入れたファイルを直接完全削除します（※復元不可）" : "Permanently deletes checked files (Cannot be undone)";
            }
            if (AuditHeaderCheckBox != null) AuditHeaderCheckBox.ToolTip = isJa ? "すべて選択 / すべて解除" : "Select All / Deselect All";
            AuditCheckDuplicatesCheckBox.Content = isJa ? "重複ファイル (SHA256)" : "Duplicates (SHA256)";
            AuditCheckDormantCheckBox.Content = isJa ? "休眠ファイル (3年以上)" : "Dormant (3+ Years)";
            AuditCheckPathLimitsCheckBox.Content = isJa ? "パス長危険域(240字超)/禁則文字" : "Long Paths (>240 chars) / Invalid Chars";
            if (AuditBandwidthLabel != null) AuditBandwidthLabel.Text = Strings.AuditBandwidthLimitLabel;
            if (AuditBandwidthStandardItem != null) AuditBandwidthStandardItem.Content = Strings.AuditBandwidthStandard;
            if (AuditBandwidthUnlimitedItem != null) AuditBandwidthUnlimitedItem.Content = Strings.AuditBandwidthUnlimited;

            ColAuditIssueType.Header = isJa ? "問題種別" : "Issue Type";
            ColAuditDupGroup.Header = isJa ? "重複グループ" : "Duplicate Group";
            ColAuditFileName.Header = isJa ? "ファイル名" : "File Name";
            ColAuditSize.Header = isJa ? "容量" : "Size";
            ColAuditModified.Header = isJa ? "最終更新日時" : "Last Modified";
            ColAuditDetail.Header = isJa ? "詳細" : "Details";
            ColAuditFullPath.Header = isJa ? "完全パス" : "Full Path";

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
            SecModalRealtimeNotice.Text = isJa ? "※高度な権限と完全リアルタイム連動" : "*Synced in real-time with advanced permissions";
            SecModalAdvPermTitle.Text = isJa ? "⚙️ 高度なアクセス許可 (Windows ACL 14項目完全網羅):" : "⚙️ Advanced permissions (All 14 Windows ACL bits):";
            SecModalAdvPermSubtitle.Text = isJa ? "Windows セキュリティ詳細設定準拠" : "Windows standard security compliant";

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