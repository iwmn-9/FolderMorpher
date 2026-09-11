using System;
using System.Collections.Generic;
using System.Reflection;

namespace FolderMorpher.Services
{
    /// <summary>
    /// アプリケーション全体の多言語テキストを一元管理するインメモリ辞書（Single Source of Truth）。
    /// 文字列ハードコードを排し、未翻訳の発生を防止する。
    /// </summary>
    public static class Strings
    {
        private static bool IsJa => LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;

        // ==========================================
        // 共通操作 (Common)
        // ==========================================
        public static string Browse => IsJa ? "参照..." : "Browse...";
        public static string BrowseWithFolder => IsJa ? "📁 参照" : "📁 Browse...";
        public static string Cancel => IsJa ? "キャンセル" : "Cancel";
        public static string Abort => IsJa ? "中止" : "Cancel";
        public static string Close => IsJa ? "閉じる" : "Close";
        public static string Save => IsJa ? "💾 保存" : "💾 Save";
        public static string Load => IsJa ? "📂 読込" : "📂 Load";
        public static string Apply => IsJa ? "⚡ 適用" : "⚡ Apply";
        public static string Check => IsJa ? "🔍 チェック" : "🔍 Check";
        public static string Ready => IsJa ? "準備完了" : "Ready";
        public static string Waiting => IsJa ? "待機中" : "Ready";
        public static string StatusReady => IsJa ? "準備完了" : "Ready";
        public static string Filter => IsJa ? "絞り込み:" : "Filter:";
        public static string All => IsJa ? "すべて" : "All";
        public static string ShowAll => IsJa ? "すべて表示" : "Show All";
        public static string SearchPlaceholder => IsJa ? "🔍 ファイル名/パス検索..." : "🔍 Search file/path...";
        public static string CopyPath => IsJa ? "📋 パスをコピー" : "📋 Copy Path";
        public static string OpenInExplorer => IsJa ? "📂 エクスプローラーで開く" : "📂 Open in Explorer";
        public static string RevealInExplorer => IsJa ? "📂 エクスプローラーで表示" : "📂 Reveal in Explorer";
        public static string DoubleClickToOpen => IsJa ? "ダブルクリックでエクスプローラーを開く" : "Double-click to open in Explorer";
        public static string DoubleClickToDrillDown => IsJa ? "ダブルクリックで該当フォルダへドリルダウン" : "Double-click to drill down";
        public static string Delete => IsJa ? "🗑️ 削除" : "🗑️ Delete";
        public static string ExportExcel => IsJa ? "📊 Excel出力" : "📊 Export Excel";
        public static string ExportCsv => IsJa ? "📄 CSV台帳出力" : "📄 Export CSV";
        public static string ExportExcelReport => IsJa ? "📊 Excelレポート出力 (.xlsx)" : "📊 Export Excel (.xlsx)";
        public static string CloseTabToolTip => IsJa ? "タブを閉じる" : "Close tab";

        // ==========================================
        // サイドバー (Sidebar)
        // ==========================================
        public static string AppSubtitle => IsJa ? "ストレージ ＆ 移行スタジオ" : "Storage & Migration Studio";
        public static string AppSubtitleCleaner => IsJa ? "ファイルサーバー クリーンアップ Studio" : "Storage Cleanup Studio";
        public static string ToggleSidebarToolTip => IsJa ? "サイドバーの開閉 (収縮 / 展開)" : "Toggle Sidebar (Collapse / Expand)";
        public static string TabStorage => IsJa ? "容量分析 & 監視" : "Storage Explorer";
        public static string TabStorageToolTip => IsJa ? "📊 容量分析 & 監視 (Storage Explorer)" : "📊 Storage Explorer";
        public static string TabLiveAcl => IsJa ? "権限コントロール" : "Live ACL";
        public static string TabLiveAclToolTip => IsJa ? "🛡️ 実環境 権限コントロール (Live ACL)" : "🛡️ Live ACL Control";
        public static string TabSimulation => IsJa ? "移行スタジオ" : "Simulation Studio";
        public static string TabSimulationToolTip => IsJa ? "🚀 移行シミュレーション (Simulation Studio)" : "🚀 Migration Studio";
        public static string TabLinkFix => IsJa ? "リンク一括修復" : "LinkFixer";
        public static string TabLinkFixToolTip => IsJa ? "🔗 ショートカット・Office修復 (LinkFixer)" : "🔗 LinkFixer";
        public static string TabAudit => IsJa ? "断捨離・健全化" : "Audit & Hygiene";
        public static string TabAuditToolTip => IsJa ? "🧹 重複・休眠・パス長チェック (Audit & Hygiene)" : "🧹 Audit & Hygiene";
        public static string TabMedia => IsJa ? "メディア最適化" : "Media Optimizer";
        public static string TabMediaToolTip => IsJa ? "🖼️ 写真軽量化 & 巨大動画抽出 (Media Optimizer)" : "🖼️ Media Optimizer";
        public static string SettingsToolTip => IsJa ? "環境設定 / Settings" : "Settings";
        public static string LanguageToggleText => IsJa ? "🌐 EN" : "🌐 JA";
        public static string LanguageToggleToolTip => IsJa ? "英語に切り替え / Switch to English" : "日本語に切り替え / Switch to Japanese";

        // ==========================================
        // Tab 0: 容量分析 (Storage Explorer)
        // ==========================================
        public static string NewTab => IsJa ? "＋ 新しいタブ" : "＋ New Tab";
        public static string NewScan => IsJa ? "新規スキャン" : "New Scan";
        public static string StartScan => IsJa ? "スキャン開始" : "Start Scan";
        public static string ExportExcelCsv => IsJa ? "Excel / CSV 出力" : "Export Excel/CSV";
        public static string HistoryGraph => IsJa ? "📈 容量推移グラフ" : "📈 History Graph";
        public static string KpiScannedSizeTitle => IsJa ? "スキャン対象 容量" : "Scanned Capacity";
        public static string KpiLargestFileTitle => IsJa ? "最大ファイル Top 1" : "Largest File Top 1";
        public static string KpiDiffTrendTitle => IsJa ? "前回差分推移" : "Historical Growth";
        public static string ScopeEntire => IsJa ? "スコープ: 全体" : "Scope: Entire Scan";
        public static string NoComparisonData => IsJa ? "比較データなし" : "No comparison data";
        public static string InitialScan => IsJa ? "初回スキャン" : "Initial scan";
        public static string ZeroFilesZeroFolders => IsJa ? "0 ファイル / 0 フォルダ" : "0 Files / 0 Folders";
        public static string ColTreeName => IsJa ? "フォルダー / ファイル名" : "Folder / File Name";
        public static string ColTreeSize => IsJa ? "容量" : "Size";
        public static string ColTreeShare => IsJa ? "全体占有率" : "% of Scanned";
        public static string ColTreeCount => IsJa ? "配下ファイル数" : "Item Count";
        public static string ColTreeModified => IsJa ? "最終更新日時" : "Last Modified";
        public static string ScanSubtreeAsRoot => IsJa ? "🔍 このフォルダーをルートにしてスキャン" : "🔍 Scan This Subfolder as Root";
        public static string ViewEditAcl => IsJa ? "🛡️ このフォルダーのNTFS権限を確認・編集" : "🛡️ View/Edit NTFS Permissions";
        public static string TopFilesTitle => IsJa ? "巨大ファイル Top 10 (直接起動対応)" : "Largest Files Top 10 (Double-Click to Reveal)";
        public static string ColTopFileName => IsJa ? "ファイル名" : "File Name";
        public static string ColTopFileSize => IsJa ? "容量" : "Size";
        public static string DirectSharesTitle => IsJa ? "選択フォルダーの内訳 (直下シェア)" : "Folder Content Share Breakdown";
        public static string DirectSharesSubText => IsJa ? "Wクリックで下層へドリルダウン展開" : "Double-click item to drill down in tree";
        public static string ColShareName => IsJa ? "直下アイテム" : "Direct Child Item";
        public static string ColShareSize => IsJa ? "容量" : "Size";
        public static string ColShareRatio => IsJa ? "直下比率" : "Share Ratio";

        // ==========================================
        // 容量推移グラフ & 数理予測 (History & Forecasting)
        // ==========================================
        public static string HistoryWindowTitle => IsJa ? "容量推移グラフ & 数理予測" : "Storage Trend Graph & Predictive Forecasting";
        public static string HistoryChartTitle => IsJa ? "📊 容量変化タイムライン ＆ 一次線形・Holtトレンド予測" : "📊 Capacity Timeline & Linear / Holt Trend Forecast";
        public static string HistoryRequireTwoScans => IsJa ? "グラフを描画するには2回以上のスキャン履歴が必要です" : "At least 2 scans are required to plot trends";
        public static string HistoryRequireThreeScans => IsJa ? "予測には3回以上のスキャン履歴が必要です" : "At least 3 scans are required for forecasting";
        public static string HistoryNoData => IsJa ? "このパスに関する過去のスキャン履歴はありません。" : "No scan history recorded for this path.";
        public static string HistoryTargetPathPrefix => IsJa ? "対象パス: " : "Target Path: ";
        public static string HistoryRecalculate => IsJa ? "再計算" : "Recalculate";
        public static string HistoryLegendMeasured => IsJa ? "実測容量" : "Measured";
        public static string HistoryLegendRegression => IsJa ? "一次線形回帰" : "Linear Fit";
        public static string HistoryLegendTarget => IsJa ? "目標上限" : "Threshold";
        public static string HistoryLegendAnomaly => IsJa ? "⚠️ 異常急増 (MAD)" : "⚠️ Surge (MAD)";
        public static string HistoryColDate => IsJa ? "スキャン日時" : "Scan Date";
        public static string HistoryColSize => IsJa ? "総容量" : "Total Size";
        public static string HistoryColItems => IsJa ? "項目数" : "Item Count";
        public static string HistoryColGrowth => IsJa ? "前回との増減" : "Growth";
        public static string HistoryColAnomalyStatus => IsJa ? "異常判定 / 状態" : "Anomaly / Status";
        public static string HistoryRegressionForecast => IsJa ? "一次線形回帰" : "Linear Regression";
        public static string HistoryHoltTrend => IsJa ? "Holtトレンド" : "Holt Trend";
        public static string HistoryTargetThreshold => IsJa ? "目標上限閾値:" : "Target Threshold:";
        public static string HistoryTargetReachedIn => IsJa ? "閾値到達予測: " : "Est. Threshold Reach: ";
        public static string HistoryTargetAlreadyExceeded => IsJa ? "⚠️ 既に目標上限を超過しています" : "⚠️ Already exceeded target threshold";
        public static string HistoryDaysSuffix => IsJa ? " 日後" : " days";
        public static string HistoryNeverReach => IsJa ? "到達予測なし (減少/横ばい傾向)" : "No reach expected (Decreasing/Flat)";
        public static string HistoryAnomalyDetected => IsJa ? "⚠️ 異常急増検知 (MAD > 3.5)" : "⚠️ Surge Detected (MAD > 3.5)";
        public static string HistoryInsufficientBaseline => IsJa ? "判定基準蓄積中 (5回未満)" : "Warming up (< 5 scans)";
        public static string HistoryContributorsTitle => IsJa ? "💡 急増主因 内訳 (Top 5)" : "💡 Surge Contributors (Top 5)";
        public static string HistoryClickRowHint => IsJa ? "スキャン行をクリックして選択" : "Click a row to inspect breakdown";
        public static string HistoryNoContributors => IsJa ? "急増なし、または比較データがありません" : "No surge or comparison data available";
        public static string HistoryContributorDoubleClickToolTip => IsJa ? "ダブルクリックで該当フォルダへドリルダウンまたは開く" : "Double-click to open in Explorer or drill down";
        public static string HistoryColContribName => IsJa ? "フォルダー名" : "Folder Name";
        public static string HistoryColContribGrowth => IsJa ? "増加量" : "Growth";
        public static string HistoryColContribRatio => IsJa ? "寄与率" : "Contribution";
        public static string HistoryGrowthPrefix => IsJa ? "増加: " : "Growth: ";
        public static string HistoryCurrentPrefix => IsJa ? "現在: " : "Current: ";
        public static string HistoryRecordCountSingle => IsJa ? "記録 1 件: " : "1 Scan: ";
        public static string HistoryTargetLabelPrefix => IsJa ? "上限: " : "Limit: ";
        public static string HistoryPacePrefix => IsJa ? "ペース: " : "Pace: ";
        public static string HistoryRSquaredLabel => IsJa ? "決定係数" : "R²";
        public static string HistoryMadFloorLabel => IsJa ? "MADフロア: " : "MAD Floor: ";
        public static string HistoryItemsUnit => IsJa ? "項目" : "Items";


        // ==========================================
        // Tab 2: 移行スタジオ (Simulation Studio)
        // ==========================================
        public static string CloneSelectedToCenter => IsJa ? "➡️ 選択フォルダを中央へ新設配置" : "➡️ Clone Selected to Center";
        public static string AddRootFolder => IsJa ? "＋ ルートフォルダ新設" : "＋ Add Root Folder";
        public static string ReviewDiffs => IsJa ? "⚖️ 差分 (Diff)" : "⚖️ Review Diffs";
        public static string DeploySkeleton => IsJa ? "🚀 スケルトン作成" : "🚀 Deploy Skeleton";
        public static string ExportScripts => IsJa ? "⚙️ 移行スクリプト" : "⚙️ Export Scripts";
        public static string ExportSimExcel => IsJa ? "📊 Excel設計書" : "📊 Export Excel";
        public static string InheritFromParent => IsJa ? "親からの権限継承を含める" : "Inherit from parent";
        public static string AdvancedSecurity => IsJa ? "⚙️ セキュリティ詳細設定" : "⚙️ Advanced Security";
        public static string DefaultProjectName => IsJa ? "新ファイルサーバー移行設計_Ver1" : "New File Server Migration Plan_Ver1";
        public static string NoneSelectedClickFolder => IsJa ? "(未選択 - 上のフォルダをクリック)" : "(None selected - Click a folder above)";
        public static string LocalPcNoDomain => IsJa ? "🟡 ローカル環境 (AD未接続)" : "🟡 Local PC (No AD Domain)";
        public static string TargetRootLabel => IsJa ? "移行先 新サーバーのルートパス (UNC / ローカル)" : "Target Root Path on Destination Server (UNC / Local)";
        public static string SourceTitle => IsJa ? "現行ファイルサーバー (移行元)" : "Source File Server (Existing)";
        public static string SourceSubText => IsJa ? "フォルダーを選択して中央へドラッグ＆ドロップ、または下部ボタンで新設ツリーに配置" : "Select folders and drag & drop to center, or use button below";
        public static string MockTreeTitle => IsJa ? "新サーバー仮想ツリー設計 (FolderMorph Studio)" : "Target Virtual Tree Architecture (FolderMorph Studio)";
        public static string MockTreeSubText => IsJa ? "N:1 統合・階層再編成・新設計ACLを直感的にデザイン。右クリックでフォルダ追加/削除" : "Intuitive N:1 consolidation, restructuring & ACL design. Right-click to add/remove";
        public static string SelectedFolderPrefix => IsJa ? "選択中: " : "Target: ";
        public static string SubfolderDropHint => IsJa ? "➕ 左の現行サーバーまたはエクスプローラーからフォルダをドロップして追加" : "➕ Drop folders from source server or Explorer to add subfolders";
        public static string SimMappingTitle => IsJa ? "🔗 移行元マッピング (データ移行元 / N:1統合)" : "🔗 Source Mappings (Data Sources / N:1 Consolidation)";
        public static string SimAclTitle => IsJa ? "新設計 アクセス権エントリ (ACE)" : "Target Access Control Entries (ACEs)";
        public static string SimAdHeaderTitle => IsJa ? "Active Directory / ローカル候補" : "Active Directory / Local Principals";
        public static string SimAdHeaderSubText => IsJa ? "中央の権限エリアへドラッグ＆ドロップして付与" : "Drag & drop to center permissions area to grant";
        public static string SimInspectorTitle => IsJa ? "③ フォルダ詳細 ＆ 権限設定" : "③ Folder Details & Permissions";
        public static string SaveProjectToolTip => IsJa ? "プロジェクト保存 (.fmorph)" : "Save Project (.fmorph)";
        public static string LoadProjectToolTip => IsJa ? "プロジェクト読込" : "Load Project";
        public static string ReviewDiffsToolTip => IsJa ? "変化点差分インスペクター" : "Review Architecture Diffs";
        public static string DeploySkeletonToolTip => IsJa ? "空フォルダ階層と設計済みNTFSアクセス権を新環境へ先行展開" : "Deploy skeleton folders and ACLs to target server";
        public static string ExportScriptsToolTip => IsJa ? "Robocopy / FastCopy スクリプト生成" : "Generate Robocopy / FastCopy scripts";
        public static string ExportSimExcelToolTip => IsJa ? "移行設計書Excel出力" : "Export Migration Specification (.xlsx)";
        public static string BrowseSourceToolTip => IsJa ? "現行フォルダ（UNCまたはローカル）を参照選択" : "Browse source directory (UNC or local)";
        public static string CtxNewSubfolder => IsJa ? "📁 新規サブフォルダ作成" : "📁 New Subfolder";
        public static string CtxRenameFolder => IsJa ? "✏️ フォルダ名の変更" : "✏️ Rename Folder";
        public static string CtxPromoteRoot => IsJa ? "⏮ 第1階層（ルート）へ昇格" : "⏮ Promote to Root (Level 1)";
        public static string CtxPromote => IsJa ? "◀ 1階層昇格" : "◀ Promote 1 Level";
        public static string CtxDemote => IsJa ? "▶ 1階層降格 (サブ化)" : "▶ Demote 1 Level (Make Subfolder)";
        public static string QuickJumpRootToolTip => IsJa ? "第1階層(ルート)へ一気ジャンプ" : "Jump to Root (Level 1)";
        public static string QuickPromoteToolTip => IsJa ? "1階層昇格" : "Promote 1 Level";
        public static string QuickDemoteToolTip => IsJa ? "1階層降格" : "Demote 1 Level";

        // ==========================================
        // Tab 3: リンク一括修復 (LinkFixer)
        // ==========================================
        public static string LinkFixTitle => IsJa ? "🔗 ショートカット ＆ Officeリンク一括修復（LinkFixer）" : "🔗 Broken Link & Office Reference Repair (LinkFixer)";
        public static string LinkFixDesc => IsJa ? "ファイルサーバー移行後に切断されたショートカット (.lnk) および Excel 内部リンク数式 (.xlsx / .xlsm) を高速検出し、新パスへ一括書き換えします。" : "Quickly scans and repairs broken shortcut (.lnk) targets and Excel formula references (.xlsx / .xlsm) after file server migrations.";
        public static string LinkSearchScopeLabel => IsJa ? "走査対象フォルダー (クライアントPCまたはサーバー)" : "Target Scan Directory (Client PC or File Server)";
        public static string LinkOldPatternLabel => IsJa ? "旧サーバーパス (置換前)" : "Old Server Path (To Replace)";
        public static string LinkNewPatternLabel => IsJa ? "新サーバーパス (置換後)" : "New Server Path (Replacement)";
        public static string LinkTableTitle => IsJa ? "検出された切断リンク一覧" : "Detected Broken Links";
        public static string LinkGenerateGpo => IsJa ? "📜 GPOログオンスクリプト生成 (.ps1)" : "📜 Generate GPO Script (.ps1)";
        public static string LinkGenerateGpoToolTip => IsJa ? "全社PCのデスクトップ/マイドキュメント等のショートカットを自動修復するスクリプトを出力" : "Generate logon script (.ps1) to repair shortcuts across client PCs";
        public static string LinkScanBroken => IsJa ? "切断リンク検出スキャン" : "Scan Broken Links";
        public static string LinkFixExecute => IsJa ? "⚡ 一括修復を実行 (バックアップ付)" : "⚡ Execute Fix (with Backup)";
        public static string LinkFixExecuteToolTip => IsJa ? "修復対象のショートカット一覧と置換差分をチェック" : "Check list of shortcuts and preview replacements";
        public static string LinkIncludeOffice => IsJa ? "Officeファイル内部リンク (.xlsx/.xlsm) も対象に含める" : "Include Office internal links (.xlsx/.xlsm)";
        public static string ColLinkFileName => IsJa ? "ファイル名" : "File Name";
        public static string ColLinkFileType => IsJa ? "種別" : "Type";
        public static string ColLinkOldTarget => IsJa ? "置換前の旧リンク先" : "Old Target Path";
        public static string ColLinkNewTarget => IsJa ? "置換後の新リンク先" : "New Target Path";
        public static string ColLinkStatus => IsJa ? "状態" : "Status";

        // ==========================================
        // Tab 4: 断捨離・健全化 (Audit & Hygiene)
        // ==========================================
        public static string AuditHeaderTitle => IsJa ? "🧹 ファイルサーバー健全化 ＆ 断捨離（衛生監査・容量削減）" : "🧹 File Server Hygiene & Cleanup";
        public static string AuditHeaderDesc => IsJa ? "重複ファイル (SHA256)、休眠ファイル (3年以上未更新)、パス長260文字超、移行禁則文字を一括抽出し、安全な棚卸し台帳や退避スクリプトを生成します。" : "Batch detects duplicates (SHA256), dormant files (3+ years), paths > 260 chars, and migration-invalid characters. Generates safe audit ledgers and archive batches.";
        public static string AuditTargetFolderLabel => IsJa ? "監査対象ディレクトリ (UNC / ローカル)" : "Target Audit Directory (UNC / Local)";
        public static string AuditKpiTotalFilesTitle => IsJa ? "総走査ファイル数" : "Total Files Scanned";
        public static string AuditKpiDupWastedTitle => IsJa ? "重複ファイルによる無駄" : "Wasted by Duplicates";
        public static string AuditKpiDormantSizeTitle => IsJa ? "休眠ファイル容量 (3年超)" : "Dormant Capacity (3+ Yrs)";
        public static string AuditKpiPathLimitsTitle => IsJa ? "パス長超過 / 禁則文字" : "Path Limit / Invalid Chars";
        public static string AuditTableTitle => IsJa ? "検出された課題・断捨離候補一覧" : "Detected Issues & Cleanup Candidates";
        public static string AuditLiveReductionLabel => IsJa ? "選択中の削減見込み: " : "Est. Space Reclaimed: ";
        public static string AuditSmartSelectLabel => IsJa ? "☑️ 一括選択:" : "☑️ Smart Select:";
        public static string AuditSmartPreset => IsJa ? "選択プリセット..." : "Select Preset...";
        public static string AuditSmartDupCopy => IsJa ? "重複の原本以外を選択" : "Select Duplicate Copies";
        public static string AuditSmartDormant3Y => IsJa ? "3年以上前の休眠を選択" : "Select Dormant (>3 Years)";
        public static string AuditSmartDormant5Y => IsJa ? "5年以上前の休眠を選択" : "Select Dormant (>5 Years)";
        public static string AuditSmartVisible => IsJa ? "表示中のみすべて選択" : "Select All Visible";
        public static string AuditSmartClear => IsJa ? "選択をすべて解除" : "Clear All Selections";
        public static string AuditFilterAll => IsJa ? "すべて表示" : "Show All";
        public static string AuditFilterDup => IsJa ? "重複ファイルのみ" : "Duplicates Only";
        public static string AuditFilterDormant => IsJa ? "休眠ファイルのみ" : "Dormant Only";
        public static string AuditFilterPath => IsJa ? "パス長・禁則のみ" : "Path / Invalid Only";
        public static string AuditStartScan => IsJa ? "🔍 監査スキャン開始" : "🔍 Start Audit Scan";
        public static string AuditExportExcelToolTip => IsJa ? "上司・各部署提出用の美麗Excelレポートを生成" : "Generate executive Excel audit report (.xlsx)";
        public static string AuditGenArchiveScript => IsJa ? "📦 安全退避バッチ生成 (.bat)" : "📦 Generate Archive Batch (.bat)";
        public static string AuditGenArchiveScriptToolTip => IsJa ? "休眠・重複ファイルを安全に別フォルダへ退避するスクリプトを出力" : "Generate batch script to safely move dormant/duplicate files to archive";
        public static string AuditDeleteSelected => IsJa ? "🗑️ 選択ファイルを完全削除" : "🗑️ Delete Selected Files";
        public static string AuditDeleteSelectedToolTip => IsJa ? "チェックを入れたファイルを直接完全削除します（※復元不可）" : "Permanently deletes checked files (Cannot be undone)";
        public static string AuditHeaderCheckToolTip => IsJa ? "すべて選択 / すべて解除" : "Select All / Deselect All";
        public static string AuditCheckDuplicates => IsJa ? "重複ファイル (SHA256)" : "Duplicates (SHA256)";
        public static string AuditCheckDormant => IsJa ? "休眠ファイル (3年以上)" : "Dormant (3+ Years)";
        public static string AuditCheckPathLimits => IsJa ? "パス長260字超/禁則文字" : "Path Limits / Invalid Chars";
        public static string ColAuditIssueType => IsJa ? "問題種別" : "Issue Type";
        public static string ColAuditDupGroup => IsJa ? "重複グループ" : "Duplicate Group";
        public static string ColAuditFileName => IsJa ? "ファイル名" : "File Name";
        public static string ColAuditSize => IsJa ? "容量" : "Size";
        public static string ColAuditModified => IsJa ? "最終更新日時" : "Last Modified";
        public static string ColAuditDetail => IsJa ? "詳細" : "Details";
        public static string ColAuditFullPath => IsJa ? "完全パス" : "Full Path";

        // ==========================================
        // Tab 5: メディア最適化 (Media Optimizer)
        // ==========================================
        public static string MediaHeaderTitle => IsJa ? "🖼️ メディア・オプティマイザ（写真・画像最適化 ＆ 巨大動画攻略）" : "🖼️ Media Optimizer (Photo Optimization & Video Nightly Batch)";
        public static string MediaHeaderDesc => IsJa ? "聖域（_Master、印刷用、RAW等）を自動保護しながら、大容量写真（2MB超）を最適化（長辺2560px超は縮小/85%品質/Exif・日時保持）で上書き軽量化し、巨大動画のTop抽出と夜間圧縮バッチを出力します。" : "Protects sanctuary folders (_Master, Print, RAW), optimizes large photos (>2MB) in-place with high quality (resizes if >2560px, preserves Exif & timestamps), and extracts large videos for nightly GPU H.265 compression.";
        public static string MediaTargetDirLabel => IsJa ? "走査対象ディレクトリ (UNC / ローカル)" : "Target Directory (UNC / Local)";
        public static string MediaMaxDimLabel => IsJa ? "最大長辺 (px)" : "Max Dimension (px)";
        public static string MediaQualityLabel => IsJa ? "画質 (%)" : "Quality (%)";
        public static string MediaMinSizeLabel => IsJa ? "最小サイズ (MB)" : "Min Size (MB)";
        public static string MediaKpiImagesCountTitle => IsJa ? "走査対象 画像数" : "Photos Found";
        public static string MediaKpiVideosCountTitle => IsJa ? "巨大動画 ファイル数" : "Large Videos";
        public static string MediaKpiOptimizedCountTitle => IsJa ? "軽量化 完了数" : "Photos Compressed";
        public static string MediaKpiSavedSizeTitle => IsJa ? "総削減容量 (解放された空き)" : "Total Capacity Freed";
        public static string MediaTableTitle => IsJa ? "メディア一覧（画像 ＆ 巨大動画）" : "Media List (Images & Large Videos)";
        public static string MediaScanButton => IsJa ? "🔍 メディア走査" : "🔍 Scan Media";
        public static string MediaOptimizeButtonToolTip => IsJa ? "軽量化対象の写真一覧と設定差分をチェック" : "Check photos to optimize and preview changes";
        public static string MediaGenVideoBatch => IsJa ? "🎬 巨大動画 夜間圧縮バッチ出力 (.bat)" : "🎬 Export Nightly Video Batch (.bat)";
        public static string MediaGenVideoBatchToolTip => IsJa ? "GPUハードウェアエンコード (H.265) で動画を一括軽量化するスクリプトを出力" : "Generate GPU H.265 compression batch script for large videos";
        public static string ColMediaType => IsJa ? "種別" : "Type";
        public static string ColMediaFileName => IsJa ? "ファイル名" : "File Name";
        public static string ColMediaOriginalSize => IsJa ? "元容量" : "Original Size";
        public static string ColMediaOptimizedSize => IsJa ? "軽量化後" : "Compressed Size";
        public static string ColMediaSavedSize => IsJa ? "削減容量" : "Saved Size";
        public static string ColMediaStatus => IsJa ? "状態 / 聖域保護" : "Status / Sanctuary";
        public static string ColMediaFullPath => IsJa ? "完全パス" : "Full Path";

        // ==========================================
        // 詳細権限モーダル (SecModal)
        // ==========================================
        public static string SecModalTitle => IsJa ? "🛡️ セキュリティの詳細設定 - " : "🛡️ Advanced Security Settings - ";
        public static string SecModalObjectName => IsJa ? "オブジェクト名:" : "Object name:";
        public static string SecModalPrincipal => IsJa ? "プリンシパル (対象アカウント):" : "Principal:";
        public static string SecModalType => IsJa ? "種類:" : "Type:";
        public static string SecModalAppliesTo => IsJa ? "適用先:" : "Applies to:";
        public static string SecModalBasicPermTitle => IsJa ? "基本アクセス許可:" : "Basic permissions:";
        public static string SecModalRealtimeNotice => IsJa ? "※高度な権限と完全リアルタイム連動" : "*Synced in real-time with advanced permissions";
        public static string SecModalAdvPermTitle => IsJa ? "⚙️ 高度なアクセス許可 (Windows ACL 14項目完全網羅):" : "⚙️ Advanced permissions (All 14 Windows ACL bits):";
        public static string SecModalAdvPermSubtitle => IsJa ? "Windows セキュリティ詳細設定準拠" : "Windows standard security compliant";
        public static string SecFullControl => IsJa ? "フル コントロール" : "Full control";
        public static string SecModify => IsJa ? "変更 (Modify)" : "Modify";
        public static string SecReadExecute => IsJa ? "読み取りと実行" : "Read & execute";
        public static string SecListFolder => IsJa ? "フォルダーの内容の一覧表示" : "List folder contents";
        public static string SecRead => IsJa ? "読み取り" : "Read";
        public static string SecWrite => IsJa ? "書き込み" : "Write";
        public static string SecAllow => IsJa ? "許可" : "Allow";
        public static string SecDeny => IsJa ? "拒否" : "Deny";
        public static string SecAppliesToAll => IsJa ? "このフォルダー、サブフォルダーおよびファイル" : "This folder, subfolders and files";
        public static string SecAppliesToFolderOnly => IsJa ? "このフォルダーのみ" : "This folder only";
        public static string SecAppliesToFolderAndSub => IsJa ? "このフォルダーおよびサブフォルダー" : "This folder and subfolders";
        public static string SecAppliesToFolderAndFiles => IsJa ? "このフォルダーおよびファイル" : "This folder and files";
        public static string SecAppliesToSubAndFiles => IsJa ? "サブフォルダーおよびファイルのみ" : "Subfolders and files only";
        public static string SecAppliesToSubOnly => IsJa ? "サブフォルダーのみ" : "Subfolders only";
        public static string SecAppliesToFilesOnly => IsJa ? "ファイルのみ" : "Files only";
        public static string SaveChanges => IsJa ? "変更を保存" : "Save Changes";

        // ==========================================
        // 変更点モーダル群 (Diff Modals)
        // ==========================================
        public static string DiffChanges => IsJa ? "⚖️ 変更点" : "⚖️ Changes";
        public static string DiffSimSubtitle => IsJa ? " - 移行設計差分" : " - Migration Architecture Diffs";
        public static string DiffLinkFixSubtitle => IsJa ? " - ショートカット一括修復" : " - Batch Shortcut Repair";
        public static string DiffMediaSubtitle => IsJa ? " - 写真・画像軽量化 (上書き縮小)" : " - Image Optimization (In-Place)";

        // ==========================================
        // 環境設定モーダル (Settings Modal)
        // ==========================================
        public static string SettingsTitle => IsJa ? "⚙️ 環境設定 (Settings)" : "⚙️ Settings";
        public static string SettingsDesc => IsJa ? "キャッシュ、スナップショット履歴、監査レポートの参照先および保存先を構成します。" : "Configure read and write locations for tree caches, snapshot histories, and audit reports.";
        public static string SettingsReadTitle => IsJa ? "📂 キャッシュ・スナップショット 参照先 (読み込み)" : "📂 Cache & Snapshot Read Source";
        public static string SettingsReadDesc => IsJa ? "共有ファイルサーバー上のマスターキャッシュ（UNCパス等）を指定すると、チーム共通の0秒ツリーや推移履歴を参照できます。" : "Specify a master cache folder on a shared file server (e.g. UNC path) to access team-wide 0-second trees and histories.";
        public static string SettingsFallbackText => IsJa ? "共有参照先にアクセスできない場合は自動でローカルキャッシュを参照する" : "Automatically fall back to local cache if shared source is unreachable";
        public static string SettingsWriteTitle => IsJa ? "💾 キャッシュ・スナップショット 保存先 (書き込み)" : "💾 Cache & Snapshot Write Destination";
        public static string SettingsWriteDesc => IsJa ? "自身がスキャンした結果のツリーキャッシュおよび履歴データの保存場所を選択します。" : "Select where your local scans save tree cache and historical data.";
        public static string SettingsWriteLocal => IsJa ? "ローカルに保存" : "Save to Local";
        public static string SettingsWriteLocalSub => IsJa ? " (推奨: マスターキャッシュを上書きしない安全設定)" : " (Recommended: Safe, won't overwrite master cache)";
        public static string SettingsWriteSame => IsJa ? "参照先と同じフォルダーに保存" : "Save to same folder as read source";
        public static string SettingsWriteSameSub => IsJa ? " (管理者・マスター更新者用)" : " (For administrators / master publishers)";
        public static string SettingsWriteCustom => IsJa ? "任意のカスタムフォルダーを指定" : "Specify custom folder";
        public static string SettingsSaveButton => IsJa ? "設定を保存" : "Save Settings";
        public static string SettingsReadPathToolTip => IsJa ? "空の場合はローカル既定値 (%LocalAppData%\\FolderMorpher) を参照します" : "Defaults to local directory (%LocalAppData%\\FolderMorpher) if blank";

        // ==========================================
        // 新機能: 容量予測 & 異常検知 (Forecasting & Anomaly)
        // ==========================================
        public static string ForecastThresholdTitle => IsJa ? "目標/枯渇 閾値:" : "Target Threshold:";
        public static string ForecastDaysRemaining => IsJa ? "到達予測:" : "Est. Arrival:";
        public static string ForecastGrowthRate => IsJa ? "平均増減ペース:" : "Average Pace:";
        public static string ForecastRecentTrend => IsJa ? "直近トレンド:" : "Recent Trend:";
        public static string ForecastConfidence => IsJa ? "決定係数 (R²):" : "Confidence (R²):";
        public static string ForecastDecreasing => IsJa ? "減少傾向（閾値到達リスクなし）" : "Decreasing (No Threshold Risk)";
        public static string ForecastInsufficientData => IsJa ? "蓄積中（最低3回のスキャンが必要）" : "Accumulating (Min 3 scans required)";
        public static string AnomalyDetectedSpike => IsJa ? "⚠️ 異常急増を検知" : "⚠️ Abnormal Surge Detected";
        public static string AnomalySpikeBreakdownTitle => IsJa ? "🔍 選択スキャンの急増主因 Top" : "🔍 Top Growth Contributors";
        public static string AnomalyDoubleclickHint => IsJa ? "ダブルクリックで該当フォルダへ直行" : "Double-click to reveal in tree/Explorer";

        /// <summary>
        /// 全プロパティが言語ごとに非空文字列を返すかを自己検証（自動テスト用）
        /// </summary>
        public static List<string> ValidateTranslations(AppLanguage lang)
        {
            var errors = new List<string>();
            var originalLang = LocalizationService.Instance.CurrentLanguage;
            try
            {
                LocalizationService.Instance.SetLanguage(lang);
                var props = typeof(Strings).GetProperties(BindingFlags.Public | BindingFlags.Static);
                foreach (var p in props)
                {
                    if (p.PropertyType == typeof(string))
                    {
                        var val = p.GetValue(null) as string;
                        if (string.IsNullOrWhiteSpace(val))
                        {
                            errors.Add($"Property '{p.Name}' is empty for language '{lang}'");
                        }
                    }
                }
            }
            finally
            {
                LocalizationService.Instance.SetLanguage(originalLang);
            }
            return errors;
        }
    }
}
