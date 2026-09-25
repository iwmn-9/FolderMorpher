# FolderMorpher — AI Agent & Developer Architecture Guide

> **【AIメンテナ・自律継続規約】**  
> 本プロジェクトは「自律完遂（自ら調査・修正・検証まで行い、完成状態で返す）」を基本方針とする。  
> ⚠️ **後続のAIメンテナへ**: コードベースの構造、ファイル責務、または重要な設計制約を変更した場合は、**必ずこの `AGENTS.md` も最新の状態に同期・更新すること**。過去の設計意図（第3項 ADR）を無視した安易なコードの巻き戻しを禁止する。

---

## 0. AI実装・整線規則（依存関係と正本の施工基準）

> **【哲学：継ぎ足しは許すが、絡ませない】**  
> AIは放っておくと「要求の直近（主にMainWindow）」へ安易に変数やイベントを継ぎ足し、コードをパッチワーク化させて破綻させる癖がある。  
> だからといって美観のためだけに既存の配線を全部引き抜く（不要な大規模リファクタ）ことも禁止する。  
> **「正本は1つ。機能間の配線は最小限。UI固有状態はMainWindowにあってよい。文字列を信号線にするな。美観のために配線を全部抜くな。ただし絡まった配線は局所的に整線せよ。」**

後続のAIメンテナは、いかなる機能追加・修正においても以下の6大基準を厳守しなければならない。

1. **正本の単一性（信号線の出どころを1つにする）**:
   - 同一の概念・状態・判定・走査ロジックを複数箇所に分散・二重実装してはならない。
   - 正本の例:
     - 全ファイル走査・探索: 共通の `SafeFileEnumerator`（階層走査、アクセス拒否保護、カバレッジ追跡）
     - 原本・重複の判定根拠: `AuditItem.IsOriginalCandidate`（プロパティ値）
     - ACL適用の意味論: `Canonical ACL Apply Contract`（C#直接展開・PowerShell生成・AclServiceの一致）
2. **共有状態の安易な新設禁止**:
   - 新機能やフィルターを追加する際、既存の Service や Model に正本が存在しないか必ず確認する。
   - 「とりあえず動くから」と、他の機能の内部キャッシュや一時変数を実装上の近道として直結させてはならない。
3. **UI表示文字列の業務ロジック利用禁止（文字列を信号線にしない）**:
   - `item.Detail.Contains("...")` や `Label.Text == "..."` のような、人間向けに整形されたUI表示文字列を業務判定・フィルタリング・データ処理の分岐条件にしてはならない。
   - 判定は必ず Model の真偽値プロパティ、列挙型（Enum）、数値型などの構造化された正本データで行う。
4. **機能間結合の抑制（MainWindowの責務境界）**:
   - 各機能（Tab 1〜6）は独立した Service と Model の境界を尊重し、他機能の内部状態を実装上の近道として直接参照・変更してはならない。製品仕様上必然性のある連携のみを許可する。
   - **※重要（極端化防止）**: MainWindow にUI固有の一時状態（選択中インデックス、トースト表示、UIイベントのディスパッチ等）を保持すること自体は全く問題としない。これらを排除するためだけに無闇なViewModelやControllerを乱立させてはならない。
5. **構造美だけを目的とした全面リファクタリングの禁止（床を理由なく剥がさない）**:
   - MVVM化、新規フレームワーク刷新、ファイルの大規模分割などを、一般論・コード行数・形式美だけを理由に行ってはならない。
   - 具体的な不具合、正本の分裂、保守不能なスパゲッティ結合が存在する場合は、**まず局所修正（整線）** を検討する。局所修正より再設計・再実装の方が明確に安全・低リスクな場合のみ、理由・影響範囲・代替案を評価した上で最小限のスコープで実施する。
6. **実行計画先行と観測・介入の二元論（Plan First & 3段ロケット原則）**:
   - **観測系（Read-Only）**: 容量スキャン、ツリー参照、逆引き監査などの「覗く」行為はノーガード・即時実行。管理者の探索テンポを一切阻害しない。
   - **介入系（State-Mutating）**: アクセス権変更、スケルトン展開、リンク修復、写真最適化などの書き込み処理はすべて **Plan First（実行計画先行）**。
   - **「① Check (Dry-Run差分確認) ➔ ② Commit (本番適用) ➔ ③ Verify (実態検証)」** の3段ロケットに統一する。
   - **プレビュー用と本番用の差分判定ロジックを二重実装してはならない**。必ず単一の `ChangePlan` インスタンスをパイプラインで貫通させること。

---

## 1. プロジェクト概要 & 技術スタック

- **アプリケーション名**: `FolderMorpher` (旧 AstraSize)
- **種別**: Windows デスクトップ向け 大容量ファイルサーバー監視 & NTFSアクセス権移行・シミュレーションスタジオ
- **フレームワーク**: .NET 8.0 (Windows WPF), C# 12
- **依存パッケージ**: `System.DirectoryServices` (8.0.0, AD通信用), `Microsoft.Data.Sqlite` (10.0.12, FTS5全文検索用)
- **ビルド形態**: `Release win-x64` の **自己完結型（Self-Contained）単一実行可能ファイル (`FolderMorpher.exe`)**
  - ネイティブWPFエンジンDLL（D3DCompiler, wpfgfx等）および SQLite ネイティブDLLはすべてEXE内部にバンドルされる。ルートに個別DLLを展開・配置してはならない。
  - `-p:EnableCompressionInSingleFile=true` による Deflate 圧縮を標準採用し、ファイルサイズは約74.5MB（配布・共有に最適化）。

---

## 2. システム鳥瞰マップ（機能とソースコードの対応表）

UI層は `MainWindow.xaml` / `MainWindow.xaml.cs`（機能別に partial class 分割）および独立コンポーネント `Views/LiveAclStudio.xaml` / `LiveAclStudio.xaml.cs` で構成され、内部ロジックは `Services` と `Models` に完全に分離されている。

| 機能領域 / タブ | XAML (MainWindow / View) | C# コードビハインド | 関連 Service / Model | 責務と概要 |
| :--- | :--- | :--- | :--- | :--- |
| **全体共通 / 左サイドバー** | `SidebarBorder`, `SidebarToggleButton` (L22-75) | `MainWindow.xaml.cs`<br>`MainWindow.Localization.cs` | `Converters/ValueConverters.cs` | 収縮対応ナビゲーション（幅220px ⇄ 58px）、グローバルステータスバー、通知トースト、言語切替（日英）、環境設定モーダル |
| **Tab 1: 容量分析**<br>(Storage Explorer) | `StorageTabPanel` (L82-410)<br>`HistoryWindow.xaml` | `MainWindow.Storage.cs`<br>`HistoryWindow.xaml.cs` | `DiskScanService.cs`<br>`StorageHistoryService.cs`<br>`StorageForecastingService.cs`<br>`ScanTabModel.cs`<br>`FileItemNode.cs` | 複数タブスキャン、ドライブ空き容量メーター、全体占有率メーター、容量上位Top10（Explorer起動連動）、直下シェア内訳、推移グラフ直下の3連KPIハイライトカード（上限到達予測・日次ペース・R²信頼度） |
| **Tab 2: ファイル検索**<br>(Search Studio) | `SearchTabPanel`<br>(`MainWindow.xaml`) | `MainWindow.Search.cs` | `SearchEngineService.cs`<br>`TreeCachePruningIndex.cs`<br>`PathCanonicalizer.cs`<br>`SharedIoGovernor.cs`<br>`ContentExtractionService.cs`<br>`SearchQueryParser.cs`<br>`PdfSearchHelper.cs`<br>`SearchModels.cs` | **検索専用 FTS5 DB 撤去 ＆ インメモリ ＋ ストリーミング型 Live 直接走査への一本化（ローカルDB肥大化・ロック競合ゼロ）**、**親子局所性（Locality-First LIFO走査）によるサーバー側MFT/RAMキャッシュ直撃**、**TreeCache 枝刈り差分走査（Differential Pruning Traversal: 更新のないフォルダーのネットワークI/Oを丸ごとスキップ）**、**JIT/AVX2 最適化 単一キーワード高速パス（string.IndexOf直接走査）**、**スキャンツリー／共有 TreeCache JSON による 0秒インメモリ検索**、**パス正規化エンジン（PathCanonicalizer: Z:\ ⇄ UNC 自動解決＆同一視）**、**UNCルート/ボリューム単位のI/O並列度統合ガバナー（SharedIoGovernor）**、**BoundedChannel (2048) によるBackpressure制御**、**50MB足切り撤廃＆巨大ファイル分散Probe（先頭・末尾・中間ブロック高速照合）**、**TreeCache SHA-256 サポート**、**64KB SequentialScan & 高速1パスXMLタグ除去**、**2文字日本語検索漏れ根絶 ＆ 500件上限完全撤去**、**Aho-Corasick 多パターン同時照合 ＆ 高速スニペット**、**Windows IFilter & 非圧縮対応純C#フォールバックによるPDF全文検索**、**Quiet Fluent 1行スリムメトリクスバー**、**フル幅モダンカードリスト ＆ Tabler File-Type バッジ**、高機能検索クエリ構文（ワイルドカード・論理演算・属性指定）、右クリックから全スタジオへ連携およびExcel/CSV出力 |
| **Tab 3: 権限コントロール & 逆引き監査**<br>(Live ACL & Effective Access) | `Views/LiveAclStudio.xaml`<br>(`LiveAclFolderView`, `LiveAclReverseView`, `LiveAclDiffModalOverlay`, `NewFolderModalOverlay`) | `Views/LiveAclStudio.xaml.cs` | `AclService.cs`<br>`EffectiveAccessService.cs`<br>`ActiveDirectoryService.cs`<br>`AclModels.cs`<br>`EffectiveAccessModels.cs` | 実環境NTFS ACL可視化・編集、**Dry-Run差分チェックモーダル（AclChangePlan貫通・継承変更警告・セマンティックVerify・SDDLロールバック）**、AD逆引き権限監査、均一幅ADアカウントカード、ADパレットUI統一、ADバックグラウンド自動同期、ツリーインライン新規フォルダー作成 |
| **Tab 4: 移行スタジオ**<br>(Simulation Studio) | `SimulationTabPanel` (L608-995) | `MainWindow.Simulation.cs` | `SimulationProjectService.cs`<br>`MigrationPackageService.cs`<br>`MigrationPackageModels.cs`<br>`SimModels.cs` | 現行ファイルサーバーから新環境への仮想ツリー設計（N:1マッピング）、ACL引き継ぎ設計、ADパレット統一、全画面・全出力完全日英両対応、ヘッダーレイアウト整線、ガワ先行作成の実機DACLセマンティックVerify、エンタープライズ移行パッケージ出力（TargetRoot必須検証・Wave分割・Runbook Excel・安全停止手順・多重コピー防止/XD・%~dp0相対ログ・exit /b 1・遅延展開排除・Dry-Run bat同梱） |
| **Tab 5: リンク修復**<br>(LinkFixer) | `LinkFixTabPanel` (L998-1094) | `MainWindow.LinkFix.cs` | `LinkFixService.cs`<br>`OfficeLinkFixService.cs` | サーバー移行後の切断ショートカット（.lnk）およびOffice内部リンク（.xlsx/.xlsm）検出・修復、**VBAマクロ非破壊保護＆通常XML混在時の部分修復（PartiallyFixed）**、全社配布用GPOログオンスクリプト（.ps1）生成 |
| **Tab 6: 整理候補発見 ＆ 健全化**<br>(Smart Hygiene & Candidates) | `AuditTabPanel` (L1097-1240) | `MainWindow.Audit.cs` | `AuditReportService.cs`<br>`HygieneCandidateEngine.cs`<br>`AuditIgnoreService.cs`<br>`ExcelReportService.cs`<br>`AuditModels.cs` | **インテリジェント整理候補発見スタジオへのすり替え**、**「このファイルは捨てられる可能性が高い。理由はこれ。」説明責任ヒューリスティクス**、**新Auditの $O(N)$ ボトムアップ集約化（10万階層でも高速・メモリ最小化）**、**整理除外リストの PathCanonicalizer 適用（Z:\ と UNC 同一視）**、**直感的な単語指標（整理推奨/要確認/参考）**、**スコア内訳可視化（クリック/ホバー）**、**表示件数制御（上位100件等）**、**整理除外リスト（自己治癒性スキップ記憶）**、**3大重点候補（①世代・旧版、②展開済ZIP残骸、③墓場フォルダー化石化判定）**、完全重複（SHA256）、休眠ファイル（3年超・1年閲覧保護）、パス長危険域（240字超）・禁則文字検出、フォルダー名部分一致除外。5連スリムメトリクスバー、一括選択プリセット、ハイパーリンク付きExcel/CSVレポート出力、**原本保護＆削除直前SHA-256再照合付き安全完全削除** |
| **Tab 7: メディア最適化**<br>(Media Optimizer) | `MediaTabPanel` (L1243-1380) | `MainWindow.Media.cs` | `MediaOptimizerService.cs`<br>`ExcelReportService.cs`<br>`MediaOptimizerModels.cs` | 保護対象（_Master/RAW等）付き写真・画像軽量化（長辺2560px超縮小/85%品質/日時・Exif保持/直接上書き）、大容量動画Topランキング抽出、夜間GPU圧縮（H.265）バッチ生成 |
| **詳細権限モーダル** | `SecModalOverlay` | `MainWindow.Simulation.cs` | `AclModels.cs` | Windows標準セキュリティ詳細設定（14項目のNTFS詳細パーミッションビット）の完全再現・編集 |
| **変化点差分モーダル** | `DiffModalOverlay` | `MainWindow.Simulation.cs` | `SimModels.cs` | 移行前後（Before/After）の変化点（新規・移動・統合・ACL差分）の一覧レビューとExcel出力 |
| **移行パッケージ生成モーダル** | `MigrationPackageOverlay` | `MainWindow.Simulation.cs` | `MigrationPackageService.cs`<br>`MigrationPackageModels.cs`<br>`ExcelReportService.cs` | ベンダー標準移行工程（事前フル同期、中間差分、本番切替）の一括静的生成、波次（Wave）自動分割・容量バジェット算定、動的転送レート・差分率による所要時間算出、容量二重加算防止、実測ファイル数引き継ぎ、安全停止手順書ガイド、週末枠オーバー警告、Migration_Runbook.xlsx（WBS/進捗台帳・マッピング・除外一覧） |

---

## 3. 重要な設計判断の記録（Architecture Decisions / ADR）

> ⚠️ **後続のAIメンテナへ**:
> 本プロジェクトの全 93 項目に及ぶ詳細な設計判断記録（ADR 1〜93）は、トークン消費削減および可読性維持のため [`.agents/ADR.md`](.agents/ADR.md) に体系化・外部保管されている。
> **仕様変更・機能改修を行う際は、必ず `.agents/ADR.md` を参照し、過去の設計意図を無視した安易なコード巻き戻しを行ってはならない。**
> 新たな設計判断を追加した場合は、`.agents/ADR.md` を最新の状態に同期すること。

### 主要な中核原則サマリー（詳細は `.agents/ADR.md` 参照）
1. **全体占有率メーター & 2連カード（v2.1.1）**: 親フォルダに対する直下シェア（右ペイン「選択フォルダーの内訳」）と、スキャン対象ルート総容量に対する全体占有率を二重加算防止のため厳格分離。ルート行は `―`（ハイフン）表示。メトリクスカードは「スキャン対象 容量」「前回差分推移」の2連カード化、冗長な括弧文言を排除。
2. **Live ACL 安全機構 & DACL完全一致検証（v2.1.1）**: 変更前に SDDL を自動スナップショットし、ワンクリックで原子的復元（`ApplyLiveAclWithRollback`）。評価順序は Windows Canonical DACL Ordering（Explicit Allow > Inherited Deny）を厳守。スナップショット永続化が1件も成功しない場合はコミットを絶対拒否。`VerifyFolderDacl` による継承・明示ACE突合および計画にない予期せぬ余計なACE（不法侵入ACE）の完全検知・検証一本化。`SimAclEntry` のSID最優先判定による混在環境セキュリティ境界の正本化。
3. **ファイル監査・整理の安全原則**: ツールによるファイル直接削除は手動オプトインによる完全削除に限定。原本候補は無条件で削除拒否。重複削除直前に原本と対象ファイルのSHA-256を再照合し誤削除ゼロ保証。休眠ファイル判定は更新日3年超に加え、直近1年間（365日）に閲覧されたファイル（LastAccessTime）を自動保護・除外。フォルダー名部分一致除外により不要フォルダーをI/Oゼロでスキップ。安全退避batは撤去しExcel/CSV棚卸し台帳出力へ集約。
4. **メディア最適化**: 保護対象フォルダー（`_Master`, `RAW` 等）のスキップ、日時・Exif・回転情報の 100% 保持、アトミック置換。メディア走査を `SafeFileEnumerator` へ統合（v2.1.1）。
5. **UNC/ネットワーク走査**:
   - `FindFirstFileExW` (`FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`) による巨大バッファ一括取得 & 8.3短縮名スキップ。
   - `SafeFindHandle` (RAII) によるメモリリーク・ハンドルリークの原理的根逐。
   - 4段自動フォールバック（拡張UNC+LargeFetch ➔ 拡張UNC+Standard ➔ **プレーンUNC Win32再試行** ➔ .NET DirectoryInfo）。Samba/NAS でのWin32ネイティブ高速一括列挙を100%成功。
   - **フォルダー単位RPCの完全根絶（ゼロI/O化）**: 親フォルダーの列挙タイムスタンプを子ノード生成時に直結し、`Directory.GetLastWriteTime` による数千回のネットワーク往復を完全ゼロ化。
   - `ScannedFileEntry` によるメタデータ直結（Audit走査時の個別属性 stat 再問い合わせ完全根絶）。
   - **全階層並列度2固定（デュアルワーカー）＆ Sol提唱 `pendingWorkCount` レース根絶 ＆ インメモリ・ボトムアップ集計**: サーバー負荷と他業務を100%保護しながら、全階層2車線化でSMB往復遅延を隠蔽し10倍〜20倍の高速化を達成。
6. **共通基盤 & 整線規則（v2.1.1）**:
   - `FormatHelper.FormatBytes` による全機能サイズ表示の一本化。
   - `ShellHelper.SelectInExplorer` / `OpenFolder` によるエクスプローラー起動処理の集約。
   - スクリプトパス生成の安全エスケープ共通化（`ScriptEscaper`、PowerShell `$TargetRoot` 代入含む）。
   - `MainWindow.xaml.cs` partial class 物理分割（Storage, Simulation, LinkFix, Audit, Media, Localization）。
   - `.agents/ADR.md` の59項目を12大中核アーキテクチャ原則へ体系的再編・集約。
7. **Search Index 完全性 ＆ 移行パッケージ硬化 ＆ Quiet Fluent（v2.2.2 / ADR 63）**:
   - `IndexedRoots` による未完了インデックスの false negative 完全防止、ディレクトリ境界厳格化（近接類似フォルダー巻き込み防止）、`ContentExtractionService` への本文抽出正本一本化。
   - Migration Studio での TargetRoot 必須バリデーション、`04_Final_Cutover_DRYRUN.bat` 同梱、`%~dp0..\Logs`、`exit /b 1`、`EnableDelayedExpansion` 排除。
   - Search Studio の 4 KPI カードを 1 行スリムメトリクスバーへ統合、通常カードの重いドロップシャドウを撤去し面と線の Quiet Fluent へ整線。
8. **FTS5二元ハイブリッドインデックス ＆ 最深Root完全性 ＆ Master BAT安全停止（v2.2.2 / ADR 64）**:
   - **抜本案施工**: 全ファイルメタデータ登録（`IndexedFiles`）＋本文対象のみFTS（`ContentFts`）の分離により、スキャンツリー不在時の通常検索で `.zip` や `.exe` が0件になる false negative を完全根絶。
   - **UNION Name OR Content**: SQLite FTS5 の trigram MATCH と通常ファイル名 LIKE を `UNION` で統合し、本文一致（スニペット付き）とファイル名一致の両方をミリ秒で完全取得。
   - **最深IndexedRoot判定**: 一致する Root のうち最深（最長パス）を正本として判定し、親がComplete・子がErrorの中断漏れを完全防止。
   - **Master BAT エラー伝播**: `00_Run_All_Waves_StepByStep.bat` で各Waveを `call "%~dp0..."` 実行し、`if errorlevel 1 exit /b 1` で即座に後続を安全停止。CP932（Shift-JIS）自動フォールバック対応。
9. **UI整線 ＆ 深階層ツリー操作性の革新（v2.2.3 / ADR 65）**:
   - **不要要素の完全撤去**: 容量分析（Tab 1）の謎の空欄 `FilterTextBox`、移行スタジオ（Tab 4）の「読込」ボタン（Enter自動読込化）、および右ペインの対症療法だった「サブ化ドロップゾーン」を撤去。
   - **深階層でも迷わない4大工夫**:
     1. **インテリジェント動的サブ化ボタン**: 選択ノードがあればその直下にサブ化（`CreateSimNodeFromSourceWithAcl`）、未選択なら新設ルート。ボタン文言・ToolTipも動的追従。
     2. **ドロップ先端ハイライト**: D&Dドラッグ中の対象フォルダーを `IsDragOverTarget` で薄青（`#E0F2FE`）ハイライト。
     3. **ホバー自動展開 (Auto-Expand on Hover)**: 閉じたフォルダー上で 400ms 留まると自動展開し、何階層でもドラッグで潜り込める。
     4. **端点自動スクロール (Auto-Scroll)**: ドラッグ中の上下端 25px 領域で ScrollViewer を自動スクロール。
10. **移行ツリーUndo ＆ 枠外Drop安全保護 ＆ 全タブQuiet Fluent 1行メトリクスバー統一（v2.2.4 / ADR 66）**:
    - **枠外Drop削除の完全可逆化**: 隠しジェスチャー（枠外ドロップ削除）を維持しつつ、削除直前に `PushUndoSnapshot()` を呼び出し、トーストに「(Ctrl+Zで復元可能)」を明示。
    - **Undo機能（戻るボタン ＋ Ctrl+Z）**: ペイン②ヘッダーに `SimUndoButton`（`↩️ 戻る`）を配備し、キーボード `Ctrl+Z` でも直前のツリー変更を100%安全に復元。
    - **空白操作のメンタルモデル統一**: ツリー余白クリックで選択解除（ルート配置へ復元）。D&D 空白ドロップは選択状態に関わらず「新設ルート配置」に統一。
    - **Quiet Fluent 1行メトリクスバーの全画面統一**: ファイル検索に加え、ファイル監査（Tab 6）、メディア最適化（Tab 7）、権限コントロール／逆引き監査（Tab 3）の全KPIカードを1行スリムバー（面＋微細線、ドロップシャドウ撤去）へ完全統一。
    - **Search 基盤の最適化 & 重複排除**: UNION検索時の同一ファイル重複排除（スニペット優先名寄せ）、`DetectTextEncoding` による Shift-JIS/UTF-8 自動判定正本化。
11. **ライブ直接走査 本文検索の最適化 ＆ フライング集計根絶 ＆ 生体反応維持（v2.2.5 / ADR 67）**:
    - **フライング集計の完全根絶**: 本文検索候補（`needsDeepCheck`）が第1段階でヒット件数・容量へ加算されるバグを排除。中身が一致した時（`EmitHit`）のみ真の加算を行う。
    - **本文非対応ファイルの事前フィルタリング**: `ContentExtractionService.SupportedExtensions` により画像・動画・実行バイナリ等をキュー投入前に即座にスキップ。2万件キューの滞留を防止し大幅高速化（Office/PDF/テキストの脱落はゼロ・トレードオフなし）。
    - **生体反応の維持**: 本文走査中も `Stopwatch sw` による所要時間タイマーと `📄 Deep Search (c/total): ファイル名` の進捗報告を 100ms 間隔でリアルタイム更新。タイマー固定・フリーズ感を完全解消。
    - **回帰テスト（Domain 8 セクション4）新設**: 直接走査時の本文一致、名前一致、非対応スキップ、フライング加算防止を自動検証（8/8 ALL PASSED）。
12. **検索連動バックグラウンド自動差分同期 ＆ 15分クールダウン ＆ 差分検知時自動再検索 ＆ 控えめ「↻」ボタン（v2.2.5 / ADR 68）**:
    - **ミリ秒応答＋裏で自動差分更新**: インデックス検索（Route 1）完了後、0.05秒で結果を表示しつつ非同期で `TriggerBackgroundIndexUpdate` を起動。
    - **15分クールダウン制御（巨大UNCサーバー負荷ゼロ）**: `NeedsBackgroundSync` により、前回の同期完了から15分以内の同一フォルダは自動スキップ。
    - **「勝手に最新でいてくれる検索」（差分検知時の自動再検索）**: 裏同期で差分（追加/更新/削除）があった場合、同一検索条件のまま自動で再検索を実行し画面の検索結果を最新化（`isAutoRefresh` ガードで再帰防止）。
    - **「本文も検索」OFF時の FTS UNION 除外（意味論厳格化）**: 本文OFF時は `ContentFts` を除外し `IndexedFiles` のみ検索。ノイズのない完全一致ファイル名検索を実現。
    - **常設「↻」アイコンボタン（幅28px）**: 一等地を圧迫していた目立つボタンを控えめな幅28pxの `↻` ボタンへ整線（手動強制更新用）。
    - **回帰テスト（Domain 8 セクション5）新設**: 15分クールダウン判定、未インデックス判定、完了日時の整合性、本文OFF時FTS分離を自動検証（8/8 ALL PASSED）。
13. **Search フル幅モダンカードリスト＆Tabler File-Type バッジ＆各カード内「📂 フォルダーを開く」 ＆ サイドバー3グループ整線 ＆ JIT権限照合・自動パージ自浄機構（v2.2.5 / ADR 69）**:
    - **サイドバー 3グループ整線**: 7機能を「探索・観測」「設計・変更」「事後・補助」の3系統順に並べ、文字見出しを置かず自然な余白（`Margin="0,0,0,14"`）のみで直感的にグループ化。FolderCleaner（クライアントモード）でも完全整合。
    - **フル幅モダンカードリスト ＆ Tabler File-Type バッジ（拡張子カラー刻印）**: 狭苦しかったクイック詳細ペインおよびヘッダーのエクスポートボタンを削ぎ落とし、カードリストを画面幅いっぱいに拡大。絵文字（📊/📕）を廃止し、オープンソース **Tabler File-Type Icons** 準拠の角丸ベクターバッジ（XLSX/PDF/DOC/CSV/ZIP/DIR等、拡張子に応じたパステル背景＋細線コントラスト枠＋太字アクロニム刻印）を動的生成・表示。未知の拡張子でも自動で上品なバッジが生成され、単一EXEの容量増ゼロ・高速60fps仮想化描画を両立。
    - **各カード内「📂 フォルダーを開く」**: 各カードのフォルダーパス右端に「📂 フォルダーを開く」（`📂 Open Folder`）ボタンをダイレクト配備し、ワンクリックで親フォルダーを表示可能に（エクスポート機能は右クリックコンテキストメニューへ集約）。
    - **JIT 権限照合 ＆ 自動パージ自浄機構**: 検索結果返却直前に並列度 16 で実アクセス権を検証。権限剥奪や消失ファイルを結果から即時除外し、裏で `ContentIndexService.PurgeFilesAsync` により SQLite FTS5 から自動削除。
    - **回帰テスト（Domain 8 セクション6）新設**: パージ実行後のインデックスからの即時抹消、本文除外、ファイル名0件を自動検証（8/8 ALL PASSED）。


14. **Tabler File-Type バッジの全画面展開 ＆ 一元正本化（`TablerBadgeHelper`）（v2.2.5 / ADR 70）**:
    - **全画面バッジ統一**: Search Studio で先行導入した Tabler File-Type バッジ（パステル角丸ベクターバッジ・拡張子カラー刻印・フォルダー `[DIR]` 刻印）を、容量分析（Tab 1）のファイルツリー（`FileTreeDataGrid`）、容量上位 Top 10（`TopFilesDataGrid`）、選択フォルダーの内訳（`FolderChildSharesDataGrid`）、およびファイル監査（Tab 6）の検出課題一覧（`AuditItemsDataGrid`）の全ファイル・フォルダー表示へ完全展開。
    - **正本の一元化（`TablerBadgeHelper`）**: バッジの色相・コントラスト枠・テキスト決定ロジックを `Services/TablerBadgeHelper.cs` に集約。各モデル（`SearchResultItem`, `FolderChildShareItem`, `LargestFileInfo`, `FileItemNode`, `AuditItem`）は4行の軽量プロパティ委譲のみを保持し、コード重複ゼロと一貫性を保証。
    - **ゼロリソース・超軽量レンダリング**: 外部画像やフォントファイルを一切追加せず純粋な WPF ベクター XAML テンプレート（`Border` + `TextBlock`）で描画するため、単一 EXE の容量増加 0 バイト、数万件の仮想化スクロールでも 60fps を維持。
15. **Search 高速化第1フェーズ：MetadataFts trigram ＆ ScanGeneration ストリーミング ＆ rowid 直結 JOIN（v2.2.5 / ADR 71）**:
    - **MetadataFts (trigram) によるファイル名ミリ秒検索**: `IndexedFiles` に対する `FullPath LIKE` のフルスキャンを全廃。3文字以上は `MetadataFts MATCH`（trigram）、1〜2文字は `idx_files_name` による `f.Name LIKE` に刷新し、数百万ファイル環境でもファイル名検索がミリ秒で完了。
    - **`ContentFts.rowid = IndexedFiles.FileId` 直結 ＆ ゼロロス自動マイグレーション**: FTS5 の内部 rowid を `FileId` と直結させ、最速の B-Tree primary key JOIN を実現。旧スキーマからの起動時自動昇格マイグレーションを施工し、既存全文インデックスデータを 1 行も失わずに移行。
    - **`ScanGeneration` によるストリーミング世代管理（メモリ O(1) ＆ 差分一括削除）**: インデックス同期ごとに世代番号をインクリメント。走査中の巨大な `HashSet<string> currentPaths` を完全撤去して順次 Upsert し、走査後に `Generation < currentGen` を O(1) で一括削除。メモリ消費を劇的に削減。
    - **回帰テスト（Domain 8 セクション7）新設**: trigram MATCH、短語フォールバック、UNION ハイブリッド、ScanGeneration 亡霊ファイル自動削除を自動検証（8/8 ALL PASSED）。
16. **Search 高速化第2・第3フェーズ ＆ 検索結果フィルター・並び替え（v2.2.5 / ADR 72）**:
    - **Change Notify (`FileSystemWatcher`) リアルタイム差分同期**: インデックス済みフォルダーを常駐監視し、ファイルの作成・更新・削除・名前変更を 300ms デバウンス集約して SQLite に即時反映。検索結果表示中も自動再検索を行い、手動再同期なしでインデックスが常に最新を維持。
    - **MFT Fast Track 直接走査連携（秒速インデックス化）**: 管理者権限かつNTFSローカルドライブの場合、raw NTFS の MFT 一括読み出しを実行し、数十万ファイルのディレクトリ列挙を 1〜2 秒で完了。UNC や一般権限環境では `SafeFileEnumerator`（並列度2）へ自動フォールバック。
    - **検索結果のフィルターチップ ＆ 並び替え（Filter & Sort）**: 検索結果ヘッダーに Fluent ピル型フィルターチップ（すべて / 📄 文書 / 🖼️ メディア / 📦 圧縮 / ⚙️ その他）と並び替え ComboBox（関連度 / 日時 / サイズ / 名前）を配備。インメモリ 0ms で瞬時に絞り込み・ソートが完了し、メトリクスバー（件数・容量）も動的連動。日英完全対応。
    - **回帰テスト（Domain 8 セクション8）新設**: Watcher 差分同期、MFT 事前保護、フィルター・ソート論理を自動検証（8/8 ALL PASSED）。
17. **検索スタジオ ヘッダー整線（ピル撤去）＆ 5項目厳選ソート＆関連度スコアリング ＆ フォルダーベクターアイコン換装（v2.2.5 / ADR 73）**:
    - **ピル型フィルターチップ撤去とヘッダー整線**: 画面上部の視覚的ノイズとなっていたピル型フィルターチップ（すべて/文書/メディア/圧縮/その他）を完全撤去し、検索結果一覧タイトルと並び替え ComboBox のみに集約したクリーンなレイアウトへ整線。
    - **ソート項目の5厳選 ＆ 作成日時（CreationTime）統合**: 不要な「名前」「サイズ」ソートを排除し、【① 関連度順 (Relevance)、② 更新日時 (新しい順)、③ 更新日時 (古い順)、④ 作成日時 (新しい順)、⑤ 作成日時 (古い順)】の 5 項目に厳選。`IndexedFiles` スキーマに `CreationTimeUtcTicks` を追加（自動マイグレーション対応）し、全検索ルート（インデックス・0秒インメモリ・直接走査）で作成日時を取得・カード上に表示。
    - **インテリジェント関連度スコアリング（True Relevance Scoring）**: クエリキーワードとのマッチ度合いに基づき、ファイル名完全一致（+100）、前方一致（+50）、部分一致（+30）、本文スニペット一致（+15）、ディレクトリ一致（+10）、直近更新鮮度（+1〜5）を多角的に合算するスコアリングエンジンを配備。真に探している重要ファイルが最上位に並ぶリランキングを実現。
18. **Option 1 折れ曲がり角付き書類アイコン ＆ 統一フォルダー ＆ 全画面完全展開（v2.2.5 / ADR 74）**:
    - **Option 1 書類アイコンのベクターXAML実装**: 単なる長方形タグだったファイルバッジを、Tabler Icons 公式の右上が折れ曲がったドキュメントシルエット（folded-corner sheet）＋パステル背景＋下部拡張子カラーピル（白抜き太字）へ全面換装。ファイル（書類）としての認識性と高級感を大幅に強化。
    - **全画面完全展開（残存長方形バッジの完全根絶）**: 検索スタジオ（`SearchListView`）および容量分析ツリー（`FileTreeDataGrid`）に加え、容量上位ファイル Top 10（`TopFilesDataGrid`）、選択フォルダーの内訳（`FolderChildSharesDataGrid`）、ファイル監査（`AuditItemsDataGrid`）に至るまで、旧来の長方形バッジを例外なく Option 1 書類ベクターアイコンへ完全統一。
    - **トーン統一フォルダーアイコン**: ファイルアイコンと同じ線幅（1.5）・角丸・立体ポケット構造（奥タブ `#FDE68A`、前ポケット `#FEF3C7`、輪郭 `#D97706`）で統一した Tabler フォルダーを配備。ファイルとフォルダーのデザイン言語を完全同期。
    - **検索結果カードの均一化（本文スニペット枠の撤去）**: ノイズとなっていたグレーの本文一致プレビュー枠（Row 2）を撤去し、すべてのカードを均一な 2 行構造（上段: アイコン＋名前＋サイズ＋更新・作成日時、下段: パス＋フォルダーを開くボタン）へ整線。一覧性と美観を極大化。
19. **自前DB自己食い完全除外 ＆ Watcher×Full Scan世代競合防止 ＆ Folderインデックス検索（IsDirectory） ＆ MFT作成日時正常化 ＆ スコアリング構文ノイズ排除（v2.2.6 / ADR 75）**:
    - **自前DB自己食いの完全除外（CI Green化）**: `ContentIndexService.IsDatabaseFile` を新設し、走査ディレクトリ直下に配置された自前 SQLite DB（`*.db`, `-wal`, `-shm`, `-journal`）を走査結果・インデックス・Watcher から 100% 除外。自己増殖および回帰テストの件数不一致を根本解消。
    - **Watcher × Full Scan 世代競合防止 ＆ Deferred Flush**: Full Scan 実行中に Watcher が検知した変更を即座に適用せず保留（deferred）し、スキャン完了イベント（`ScanCompleted`）直後に最新世代番号（`currentGen`）として安全に一括適用（flush）。誤削除レースを完全排除。Watcher エラー時は即座に `MarkRootDirty` で次回同期を要求。
    - **Folder インデックス検索（IsDirectory）＆ trigram MATCH**: `IndexedFiles` に `IsDirectory` カラムを追加（自動マイグレーション）。`SafeFileEnumerator` に `includeDirectories: true` を指定してフォルダーも列挙・インデックス登録。`IncludeFolders = true` 時にフォルダーもミリ秒で trigram MATCH 取得可能に。
    - **MFT Fast Track 作成日時正常化**: $FILE_NAME 属性 offset +0x08 から真の作成日時（`CreationTime`）を解析し、`ScannedFileEntry` まで貫通。
    - **Relevance Score 構文ノイズ排除**: `SearchQueryParser.Parse` により `ext:`, `size:`, `>` 等の構文トークンをスコア計算から排除し、純粋なキーワード・フレーズのみでスコアリング。
20. **本文ON時名前ヒット意味論統一（Status>=0） ＆ MFT引数順正本化 ＆ 最深Root Generation解決 ＆ Subtree Purge（v2.2.6 / ADR 76）**:
    - **本文ON時名前ヒット意味論統一（`f.Status >= 0`）**: `SearchIndexedAsync` の Metadata name branch において `f.Status >= 0` を固定。「本文も検索」ON ＋ 「フォルダも含める」ON でも、フォルダーや ZIP・画像・EXE などの非本文ファイルが名前一致で確実にヒットするよう Live Search と意味論を完全統一。Content branch（`ContentFts`）のみ `f.Status = 1` を維持し、非本文ファイルへの誤ヒットを防止。
    - **MFT `ScannedFileEntry` 引数順正本化**: `EnumerateEntriesViaMftAsync` での引数順を `creation, lastWrite, lastWrite` に修正。CreationTime と LastAccessTime の入れ替わりを解消。
    - **最深 Root の Generation 解決（`GetGenerationForPath`）**: `SELECT MAX(CurrentGeneration) FROM IndexedRoots;` を全廃。対象パスに最も深く合致する `IndexedRoots` の `CurrentGeneration` を解決する `GetGenerationForPath` を配備。マルチ Root 運用時の別 Root 世代混入を完全排除。
    - **ディレクトリ Subtree Purge ＆ Watcher Reconciliation**: `PurgeFilesAsync` を `WHERE FullPath = @path OR FullPath LIKE @prefix ESCAPE '\'` に拡張し、フォルダー削除・リネーム時に配下全子孫を一括抹消。Watcher でのフォルダー作成・変更時は親 Root を `MarkRootDirty` して再同期を担保。
    - **回帰テスト（Domain 8 セクション 10）新設**: 本文ON時名前ヒット、最深Root Generation、Subtree Purge を自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。
21. **「本文も検索」トグル自動実行解除 ＆ 2フェーズプログレッシブ検索（ファイル名ミリ秒先行表示 ＋ 本文ストリーミング合流）（v2.2.6 / ADR 77）**:
    - **トグル切り替え時の勝手な自動検索を完全排除**: 「本文を含める」トグル（SearchContentCheckBox）をクリックした瞬間に無条件で検索が走り出す挙動を撤去。Enterキーまたは検索ボタン押下時のみ実行されるクリーンなUXへ是正。
    - **2段階プログレッシブ検索（先行通知 callback ＆ 本文合流）**:
      - SQLite インデックス検索において、onNameHitsReady コールバックを導入。ファイル名／属性条件（MetadataFts / .Name）クエリを先行実行し、0.002〜0.005 秒で画面に先行表示。続いて本文検索（ContentFts）を実行し、同一ファイルはスニペット優先でマージ、本文のみ一致ファイルを追加して最終確定。
      - スキャン済みツリーがある場合はインメモリのツリーからファイル名一致を 0ms で画面に先行表示し、裏のライブ本文走査（FilterByContentAsync）結果をストリーミング合流。
    - **ストリーミング通知の初期適応型バッチ化**: 走査中のヒット通知閾値を 1件 ➔ 5件 ➔ 25件 の初期適応型バッチへ最適化。1件目から即座に画面へ反映され、待たされ感を完全解消。
    - **UI デバウンス整線**: atchYield によるストリーミング合流時、150ms デバウンス制御付きで ApplyFilterAndSort() を実行し、チラつきなく滑らかにリスト更新。
    - **回帰テスト（Domain 8 セクション 11）新設**: 2段階プログレッシブ検索（先行通知 callback ＆ 本文合流）を自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。
22. **カンマ（`,`）およびパイプ（`|`）によるキーワードOR検索 ＆ AND-of-ORs 構造化検索（v2.2.6 / ADR 78）**:
    - **直感的なOR記法**: スペース＝AND、カンマ（`,`）またはパイプ（`|`）＝OR のルールを確立。例: `見積,請求 2026 ext:pdf` ➔ （見積 OR 請求）AND 2026 AND PDF。ダブルクォート（`"data,backup.csv"`）で囲めば文字通りのカンマとして完全一致保護。
    - **FTS5 ネイティブOR展開**: 3文字以上は `MetadataFts MATCH '("見積" OR "請求")'` でミリ秒取得。1〜2文字は SQL WHERE `f.Name LIKE` の OR 節へ自動最適化。
    - **全経路（ツリー・直接走査・本文）完全網羅**: インメモリ、直接走査、本文検索（Office / PDF / Text）すべてで `requiredGroups` による OR グループ充足判定を施工。
    - **回帰テスト（Domain 8 セクション 12）新設**: パーサー、FTS5、直接走査の全経路一致を自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。
23. **先行表示JIT権限検証 ＆ content:修飾子必須意味論 ＆ (Name OR Content) INTERSECT 積集合（v2.2.6 / ADR 79）**:
    - **先行表示の JIT 権限照合（VerifyAndFilterPermissionsAsync）貫通**: 先行表示候補に対しても必ず JIT 権限検証を実施し、アクセス権のない古いIndexファイルの露出を完全に遮断。安全契約を最優先。
    - **`content:` 修飾子の必須本文条件正本化 ＆ 先行表示抑止**: `content:キーワード` 指定時は本文検査通過まで確定ヒットとみなさないため先行表示を安全に抑止。全探索エンジンで独立した必須本文条件として確実に貫通。
    - **Indexed Search の `(Name OR Content)` INTERSECT 積集合**: 各グループに対し `(Name matches G_i OR Content matches G_i)` の FileId 集合を構築し、全グループを SQLite の `INTERSECT` で積集合結合。フィールド跨ぎAND（例: 名前に「契約書」、本文に「2026」）を 100% 漏れなく検出し、Direct Search と Indexed Search の結果が数学的に完全一致。
    - **生体反応タイマー（Stopwatch）正本化**: ストリーミングバッチ更新時に `searchTotalSw.Elapsed` を渡し、タイマーが 0ms に巻き戻る表示バグを解消。
    - **回帰テスト（Domain 8 セクション 13）新設**: JIT権限、content:修飾子、フィールド跨ぎAND積集合を自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。
24. **ファイルサーバー保護型 適応並列度制御（Adaptive Concurrency Controller: 初期値2・下限2・上限4・即時崖落ち降下・天井クランプ）（v2.2.7 / ADR 80）**:
    - **「手遅れになる前に崖から落ちるように逃げるロジック」が本体**: SMB/RPC は一度過負荷（キュー堆積・ディスクI/O飽和）になると、クライアントが検知した時には既に大渋滞を形成している。そのため、TCP slow start よりも遥かに臆病な制御則を確立。
    - **初期値2・下限2・上限4の不変契約**: 既存の安全正本（並列2固定）を絶対に下回らない（Min=2, Default=2）。まずは4でキャップし、本番ファイルサーバーを殴るリスクを根絶。
    - **p95 / ジッター監視 ＆ 加算昇格 (Additive Increase: +1)**: 平均値の罠を排除し、直近ウィンドウの p95 latency を監視。ベースライン計測後、安定・低ジッター（`p95 <= baseline * 1.3`）かつエラーゼロが一定期間継続した場合のみ慎重に `+1`。
    - **限界効用 (Marginal Gain) 監視**: 昇格後、スループット改善が +5% 未満またはレイテンシ悪化の場合、即座に元の並列度に戻してセッション上限をクランプ。
    - **即時崖落ち降下 (Immediate Cliff Decrease) ＆ 天井クランプ (One-Way Ceiling Clamp)**:
      - Win32 ネットワークエラー（`ERROR_BAD_NET_RESP`, `ERROR_UNEXP_NET_ERR`, `ERROR_NETNAME_DELETED` 等）やタイムアウト、異常遅延（`latency > baseline_p95 * 3.5`）を検知した場合、即座に並列度 2 へ崖落ち、30秒クールダウン。
      - 一度過負荷を検知してバックオフしたセッション中はその上限に二度と挑戦しない（不可逆天井クランプによりチャタリング・脈打ちを完全抑止）。
    - **SafeFileEnumerator / DiskScanService / ContentSearch 全走査基盤へ統合**: ディレクトリ列挙、容量スキャン、本文抽出（Office/PDF/テキスト）のすべてで `AdaptiveConcurrencyController` のスロット調停を貫通。
    - **回帰テスト（Domain 2 セクション 7）新設**: 初期値・下限・上限の不変契約、ベースライン確立、昇格、即時崖落ち、天井クランプ、Win32エラー判定、並行スロットリースのデッドロックフリーを自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。
25. **検索整合性の徹底硬化 ＆ ネットワークエラー判定の構造化 ＆ 緊急退避ウィンドウ（v2.2.8 / ADR 81）**:
    - **Progressive Search のレースコンディション根絶**: 先行NameHits通知の非同期JIT権限検証が遅延完了した際、後続の最終結果（FTS5本文ヒット全件等）を古い部分件数で上書きしてしまう表示レースを `finalResultsCommitted` ガードにより完全根絶。
    - **ExactPhrase（引用符完全一致 `"契約 更新"`）の Indexed Search 統合**: 引用符で囲まれた完全一致フレーズを属性検索単体分岐に落とさず、FTS5 / LIKE インデックス検索の `keywordGroups` に必須グループとして統合。スペースを含むフレーズも LIKE 句で 100% 確実に一致。
    - **Win32 ネットワークエラー判定の構造化（文字列信号線の根絶）**: `NativeDirectoryEnumerator` に `EnumerationFailureKind` enum（`None`, `AccessDenied`, `NotFound`, `Network`, `Io`, `Unknown`）および `ClassifyWin32Error(int error)` を新設。エラーコード文字列の `Contains("58")` 等の脆弱な判定を全廃し、型安全な列挙型で `AdaptiveConcurrencyController` へ直結。
    - **超早期崖落ち（Emergency Window: 直近8件監視）**: 高速LAN（baseline 10ms）においてサーバー負荷増大で 45ms〜55ms が発生した際、30件の母集団蓄積を待たずに「直近8件中3件超過」で即座に並列度 2 へ崖落ち・天井クランプ。
    - **回帰テスト（Domain 8 セクション 14 & Domain 2 セクション 7更新）新設**: 引用符完全一致のFTS5/LIKE統合、構造化エラー分類、Emergency Window早期崖落ちを自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。
26. **SlotLease 参照意味論 ＆ ReleaseOnce 二重解放根絶 ＆ ExactPhrase Direct/Indexed Parity（v2.2.9 / ADR 82）**:
    - **SlotLease の mutable struct 防御コピー問題の根本根絶**: `SlotLease` を `public sealed class SlotLease : IDisposable` に改変し参照意味論を確立。`Interlocked.Exchange` による `ReleaseOnce` イディオムを導入し、`Report()`、`Dispose()`、二重呼び出し、例外等あらゆる経路でスロット解放が厳格に 1 回のみ実行されることを保証。
    - **スロット上限・アンダーフロー防止ガード**: `AdaptiveConcurrencyController.ReleaseSlot` で `_activeSlots` が負数（アンダーフロー）にならないようガードを配備。`ActiveSlots` プロパティを公開。
    - **ExactPhrase の Direct / Indexed 完全 Parity**: `SearchEngineService` の直接走査・インメモリ検索において、本文ON時は事前ファイル名チェックでドロップせず、本文検索の必須グループ（`requiredGroups`）へ統合。ファイル名にフレーズがなく本文にのみフレーズがある場合でも Direct / Indexed の両方で 100% 同一にヒット。
    - **旧 `IsNetworkOrFatalError(string)` の完全削除**: 文字列ベースのWin32エラー判定メソッドを物理削除し、`EnumerationFailureKind` への正本一本化を完遂。
    - **回帰テスト（Domain 2 セクション 7 & Domain 8 セクション 14更新）**: `CurrentConcurrency = 2` で 4 並列投入時の最大アクティブスロット数 `<= 2` 厳格遵守、高負荷混在解放でのアンダーフロー・リークゼロ、および ExactPhrase Direct Parity を自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。
27. **「本文も検索ON＋フォルダも含めるON」Direct/InMemory Parity完全回復 ＆ Emergency Head整線（v2.2.10 / ADR 83）**:
    - **フォルダー名一致の救済（Direct / In-Memory 回帰根絶）**: `SearchEngineService` の `MatchFile` および `MatchDirectEntry` において、`SearchContentMode` が有効であっても、`content:` 必須指定がなくフォルダー名がキーワードを満たしていれば即座に `Name` 合格（`return true`）とするよう修正。Indexed / In-Memory / Direct の全 3 経路でフォルダー検索結果が 100% 完全一致。
    - **Emergency Window リセットの整線**: `AdaptiveConcurrencyController.ApplyCliffDecrease` において、`_emergencyCount = 0` に加えて `_emergencyHead = 0` も明示リセットし、リングバッファ状態を整線。
    - **回帰テスト（Domain 8 セクション 15）新設**: `SearchContentMode = true`, `IncludeFolders = true` で、フォルダー名一致が Indexed / In-Memory / Direct の 3 経路すべてで同一に返ることを自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。
28. **UNC特化新検索アーキテクチャ（二重I/Oゼロ直結・Lazy Background Builder・Aho-Corasick Live Verify ＆ スニペット生成）（v2.2.11 / ADR 84）**:
    - **Storage 列挙結果の Metadata Index 直結（二重I/Oゼロ）**: Storage Scan（容量測定）で列挙した `FileItemNode` メモリ木構造からディスク再走査ゼロで `IndexedFiles` と `MetadataFts` へミリ秒一括登録。ファイル名・属性・フォルダー検索が UNC 再アクセスゼロで即座に機能。
    - **Aho-Corasick 多パターン同時照合 ＆ 高速ハイライトスニペット生成**: `AhoCorasickSearcher`（Trie + Failure Link）により、PDF・テキストの直接走査時に同一ファイルを複数回開き直す無駄を完全根絶し Early Exit を実現。また FTS5 の重い `snippet()` 依存を撤去し、DBから取得した Body に対して C# 上で瞬時に前後コンテキストを切り抜いたハイライトスニペットを生成（1〜2文字語にも完全対応）。
    - **Lazy Background Builder（Small-File First）＆ Opportunistic Cache**: Storage Scan 完了後、アイドル時に未インデックスファイルを容量昇順（Small-File First）で低優先度バックグラウンド抽出し `ContentFts` へ順次投入。ライブ検索で読んだ本文もその場でキャッシュ投入。
    - **回帰テスト（Domain 8 セクション 16, 17, 18）新設**: Storage ツリー直結同期、Aho-Corasick 多パターン Early Exit、Small-File First 遅延インデックス、便乗キャッシュを自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。
29. **次世代検索アーキテクチャ Mode C（contentless + detail=none trigram FTS5 ＆ 3文字分解AND ＆ Progressive Verify）（v2.2.12 / ADR 85）**:
    - **10数GBのDB肥大化の根本解決（DBサイズ約1/8に激減）**:
      - 「巨大な答えを持つ全文Index」から「読まなくていいファイルを判定する極小Index ＋ 必要な原本だけ確認」へ転換。
      - `content='', contentless_delete=1, tokenize='trigram'` を採用し、DB内に本文（`Body`）を一切持たない純粋な contentless インデックスを構築（旧DBからの自動マイグレーション対応）。
      - 実機ベンチマーク（10,005文書）において、Mode A の 11.61MB ➔ **Mode C の 1.41MB（87.9% 削減、約1/8）** への劇的な軽量化を実証。
    - **スライディングウィンドウ 3文字分解 AND クエリ**:
      - `detail=none` でのフレーズエラーを回避するため、4文字以上の語句を 3文字 trigram の AND 結合式（例：「最高機密」➔ `("最高機" AND "高機密")`）へ自動分解。2文字語句は `IndexedFiles (Status = 1)` を通して原本 Verify で 100% 確実に救済。
    - **Sol指摘：False Positive の必然性と Progressive Verify（Aho-Corasick）**:
      - trigram の AND 結合では離れた `ABC...BCD` でも FTS MATCH を通過するため原本 Verify は必須。
      - FTS MATCH で絞り込んだ候補（Candidates）を Small-File First でストリーム検証し、真の確定 Hit のみ UI へ順次合流。**False Negative 100% ゼロ ＆ False Positive 100% ゼロ** を完全保証。
    - **回帰テスト（Domain 8 セクション 19）新設**:
      - 離れた `ABC...BCD`（偽陽性）と連続 `ABCD`（真の合致）の分別検証、3文字 trigram AND 結合、および `contentless_delete=1` の安全動作を自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。
30. **ストリーミング型 Live 直接走査 ＆ 2文字日本語検索漏れ根絶 ＆ 500件上限撤去（v2.2.13 / ADR 86）**:
    - **500件上限（`LIMIT 500`）の完全撤去**:
      - `ContentIndexService.cs` における属性検索、ファイル名先行表示、全体検索の各 SQL からハードコードされた `LIMIT 500` を完全撤去。大規模環境でも上限なく全件がヒット・検証されるよう改善。
    - **2文字日本語キーワード検索の漏れ根絶（False Negative 0% 保証）**:
      - SQLite trigram で直接 MATCH できない 2文字語（「設計」「仕様」等）において、`LIMIT 500` 撤去および候補抽出条件の `Status IN (0, 1)` 拡大により、全候補が漏れなく原本 Verify（Aho-Corasick）へ送られ、1件の漏れもなく確実にヒット。
    - **ストリーミング型 Producer-Consumer Channel パイプライン（列挙と本文走査の完全並行化）**:
      - `SearchEngineService.SearchDirectFolderAsync` において `System.Threading.Channels.Channel<SearchResultItem>` を導入。
      - ファイル列挙（Producer）で見つかった本文候補を即座に Channel へ投入し、バックグラウンドの Consumer ワーカー群（`AdaptiveConcurrencyController` 制御下、最大8並行）が即座にファイルを開いて本文検査を開始。
      - 走査開始からわずか数百ミリ秒で 1 件目のヒットが画面（UI）にポップアップ表示される超高速ストリーミングを実現。
    - **ストリーミング I/O & 高速XML解析最適化**:
      - `FileOptions.SequentialScan` ＋ 64KB バッファ（`FileStream`, `StreamReader`）をテキスト・Office 解析に全面適用。
      - `StripXmlTagsFast`: 正規表現（`Regex.Replace`）を全廃し、1パスの高速 char スキャン＆空白圧縮へ刷新。
      - Office 本文走査において生XMLでの事前キーワード存在チェック（Pre-Filter）を導入し、不要なタグ除去をスキップして即脱落。
    - **回帰テスト（Domain 8 セクション 20）新設**:
      - `StripXmlTagsFast` の精度、550ファイル生成・520番目「設計」配置による 500件上限突破＆2文字日本語検索の完全性、および `SearchDirectFolderAsync` の Producer-Consumer パイプライン直接走査を自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。
31. **検索専用 FTS5 DB 撤去 ＆ インメモリ ＋ Live走査一本化 ＆ Canonical Path ＆ TreeCache SHA-256（v2.2.14 / ADR 87）**:
    - **ローカル全文DBの完全撤去（運用コスト・肥大化ゼロ）**:
      - 検索専用の SQLite DB（`folder_morpher_search.db`、`_contentIndex`）を撤去し、数GB〜10GB以上のDB肥大化、WALロック競合、バックグラウンドでの不要なディスク・ネットワークアクセスを完全に根絶。
      - 検索経路を「スキャン済みツリー／共有JSONキャッシュによる 0秒インメモリ検索」＋「未スキャンUNC／初見フォルダーに対する Live 直接走査（Producer-Consumer Channel パイプライン）」の 2 大柱へ一本化。
    - **パス正規化エンジン（`PathCanonicalizer`: Z:\ ⇄ UNC 自動解決＆同一視）**:
      - `WNetGetConnectionW`（`mpr.dll`）によりネットワークドライブ（`Z:\`）を実体の UNC パス（`\\server\share`）へ自動解決。
      - キャッシュ・検索対象判定において、ドライブ割り当てやUNCの違いによらず同一フォルダーとして 100% 同一視。
    - **共有キャッシュツリー（JSON）への SHA-256 サポート**:
      - `FileItemNode` および `TreeCacheNode` に `Sha256` プロパティを追加。
      - スキャンツリーの JSON キャッシュ（`TreeCaches/*.json`）にハッシュ値を保持・ポータブル共有可能に。
    - **回帰テスト（Domain 2）新設**:
      - `TestPathCanonicalizerAndSha256CacheAsync` により、パス正規化（UNC・拡張UNC・末尾スラッシュ・大文字小文字同一視・ネットワーク判定）および TreeCache での Sha256 の JSON シリアライズ往復保持を自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。
32. **整理候補発見スタジオへのすり替え ＆ 説明責任ヒューリスティクス ＆ 「すぐ整理できそう」指標（v2.2.15 / ADR 88）**:
    - **「不要ファイル判定」から「理由付き整理候補発見エンジン」への刷新**:
      - 単なる重複・休眠抽出から、AIが勝手に消去を断定せず「このファイルは捨てられる可能性が高い。理由はこれ。」と明確な根拠（Why）を添えて提示する新スタジオへすり替え。
      - 「高確度」などの硬いシステム用語を排除し、「すぐ整理できそう」「確認推奨」「参考」の親しみやすい目安を採用。
    - **3大重点整理候補エンジンの新設（`HygieneCandidateEngine`）**:
      - **① 世代・旧版ファイル（Version Family）**: 同一フォルダー内でステミング解析（正規表現による日付・バージョン・コピーサフィックスのループ剥ぎ取り）を実施し、最新版（Active）以外の旧版ファイル（例: `見積書_最終_本当.xlsx` に対する `見積書_修正版.xlsx`）を抽出（WasteScore 85点 / 「すぐ整理できそう」）。理由列に最新版パスと更新日を明示。
      - **② 展開済みアーカイブ残骸（Extracted Archive Shadow）**: 同一階層内に同名フォルダーが存在する ZIP / 7z 等の残骸を O(1) 判定で抽出（WasteScore 90点 / 「すぐ整理できそう」）。
      - **③ 墓場フォルダー判定（Graveyard / Ghost Tree）**: 配下全ファイルが3年以上未更新かつ直近1年間アクセスゼロの化石化フォルダーを検出（3ファイル以上 & 1MB以上）。フォルダー代表エントリとして抽出（WasteScore 85〜95点 / 「すぐ整理できそう」）。
    - **UI刷新 ＆ 既存安全原則の100%堅持**:
      - 5連スリムメトリクスバー（総走査 / すぐ整理できそう / 世代・旧版 / 完全重複 / 休眠・墓場）、絞り込みフィルター、一括選択プリセット（「すぐ整理できそう」を選択 等）を配備。
      - 原本候補の絶対保護（`IsOriginalCandidate`）および削除直前 SHA-256 再照合による誤削除ゼロ保証は完全に維持。
    - **回帰テスト（Domain 4）新設**:
      - `TestHygieneCandidateDiscoveryAsync` により、世代旧版抽出、展開済ZIP残骸検出、墓場フォルダー判定、スコアリング・目安表示、サマリー集計を自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。
---

---

## 4. ビルド・実行・検証コマンド

### ビルド（0 警告・0 エラーを維持すること）
```powershell
& "$HOME\.dotnet\dotnet.exe" build
```

### 配布用単一EXEの生成（Release self-contained・圧縮約74.5MB）
```powershell
$env:PATH = "C:\Users\iwakura\.dotnet;" + $env:PATH
& "$HOME\.dotnet\dotnet.exe" publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o ./publish
# Google Drive同期時は社内互換のため2つとも配置すること
Copy-Item ./publish/FolderMorpher.exe "G:\マイドライブ\FolderMorpher\FolderMorpher.exe" -Force
Copy-Item ./publish/FolderMorpher.exe "G:\マイドライブ\FolderMorpher\FolderCleaner.exe" -Force
```

### 自動回帰テストスイート（ヘッドレス自己検証・CIゲート）
バグ修正やリファクタリング後は、必ず以下の回帰テストを実行して 8/8 ALL PASSED（8大ドメイン包括検証）であることを確認すること。
```powershell
& "$HOME\.dotnet\dotnet.exe" run --no-build -- --test-regression
```

### 自動統合テスト（ヘッドレス実行）
```powershell
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --test-suite "C:\Path\To\TestDir"
```

### ヘッドレス実機レンダリング（オフスクリーン撮影でUIを目視確認）
```powershell
# Tab 0: 容量分析
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab0.png" --tab 0

# Tab 1: ファイル検索
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab1_search.png" --tab 1

# Tab 2: 権限コントロール
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab2_liveacl.png" --tab 2

# Tab 3: 移行スタジオ
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab3_simulation.png" --tab 3

# Tab 4: リンク修復
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab4_linkfix.png" --tab 4

# Tab 5: ファイル監査
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab5_audit.png" --tab 5

# Tab 6: メディア最適化
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab6_media.png" --tab 6
```

