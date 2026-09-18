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
| **Tab 2: ファイル検索**<br>(Search Studio) | `SearchTabPanel`<br>(`MainWindow.xaml`) | `MainWindow.Search.cs` | `SearchEngineService.cs`<br>`ContentIndexService.cs`<br>`ContentExtractionService.cs`<br>`SearchQueryParser.cs`<br>`PdfSearchHelper.cs`<br>`SearchModels.cs` | **全自動スマートルーティング（FTS5ミリ秒 ➔ 0秒インメモリ ➔ ライブ走査）**、**バックグラウンド自動インデックス同期**、**SQLite FTS5 trigram ＋ LIKE ハイブリッド全文検索（IndexedRoots完全性保証・日本語2文字100%ヒット・差分更新・亡霊クリーンアップ・Small-File First・ディレクトリ境界厳格化・構文完全貫通）**、**Windows IFilter & 非圧縮対応純C#フォールバックによるPDF全文検索**、**Office / テキスト本文抽出正本化 (`ContentExtractionService`)**、**「📄 本文も検索」連動トグル**、**Quiet Fluent 1行スリムメトリクスバー**、**フル幅モダンカードリスト ＆ Tabler File-Type バッジ（拡張子カラー刻印）＆ 各カード内「📂 フォルダーを開く」ボタン**、Everything互換クエリ構文、右クリックから全スタジオへ連携およびExcel/CSV出力 |
| **Tab 3: 権限コントロール & 逆引き監査**<br>(Live ACL & Effective Access) | `Views/LiveAclStudio.xaml`<br>(`LiveAclFolderView`, `LiveAclReverseView`, `LiveAclDiffModalOverlay`, `NewFolderModalOverlay`) | `Views/LiveAclStudio.xaml.cs` | `AclService.cs`<br>`EffectiveAccessService.cs`<br>`ActiveDirectoryService.cs`<br>`AclModels.cs`<br>`EffectiveAccessModels.cs` | 実環境NTFS ACL可視化・編集、**Dry-Run差分チェックモーダル（AclChangePlan貫通・継承変更警告・セマンティックVerify・SDDLロールバック）**、AD逆引き権限監査、均一幅ADアカウントカード、ADパレットUI統一、ADバックグラウンド自動同期、ツリーインライン新規フォルダー作成 |
| **Tab 4: 移行スタジオ**<br>(Simulation Studio) | `SimulationTabPanel` (L608-995) | `MainWindow.Simulation.cs` | `SimulationProjectService.cs`<br>`MigrationPackageService.cs`<br>`MigrationPackageModels.cs`<br>`SimModels.cs` | 現行ファイルサーバーから新環境への仮想ツリー設計（N:1マッピング）、ACL引き継ぎ設計、ADパレット統一、全画面・全出力完全日英両対応、ヘッダーレイアウト整線、ガワ先行作成の実機DACLセマンティックVerify、エンタープライズ移行パッケージ出力（TargetRoot必須検証・Wave分割・Runbook Excel・安全停止手順・多重コピー防止/XD・%~dp0相対ログ・exit /b 1・遅延展開排除・Dry-Run bat同梱） |
| **Tab 5: リンク修復**<br>(LinkFixer) | `LinkFixTabPanel` (L998-1094) | `MainWindow.LinkFix.cs` | `LinkFixService.cs`<br>`OfficeLinkFixService.cs` | サーバー移行後の切断ショートカット（.lnk）およびOffice内部リンク（.xlsx/.xlsm）検出・修復、**VBAマクロ非破壊保護＆通常XML混在時の部分修復（PartiallyFixed）**、全社配布用GPOログオンスクリプト（.ps1）生成 |
| **Tab 6: ファイル監査**<br>(Audit & Hygiene) | `AuditTabPanel` (L1097-1240) | `MainWindow.Audit.cs` | `AuditReportService.cs`<br>`ExcelReportService.cs`<br>`AuditModels.cs` | 重複ファイル（SHA256）、休眠ファイル（3年超・1年閲覧保護）、パス長危険域（240字超）・禁則文字検出、フォルダー名部分一致除外（カンマ区切り）。ハイパーリンク付きExcel/CSVレポート出力、**原本保護＆削除直前SHA-256再照合付き完全削除** |
| **Tab 7: メディア最適化**<br>(Media Optimizer) | `MediaTabPanel` (L1243-1380) | `MainWindow.Media.cs` | `MediaOptimizerService.cs`<br>`ExcelReportService.cs`<br>`MediaOptimizerModels.cs` | 保護対象（_Master/RAW等）付き写真・画像軽量化（長辺2560px超縮小/85%品質/日時・Exif保持/直接上書き）、大容量動画Topランキング抽出、夜間GPU圧縮（H.265）バッチ生成 |
| **詳細権限モーダル** | `SecModalOverlay` | `MainWindow.Simulation.cs` | `AclModels.cs` | Windows標準セキュリティ詳細設定（14項目のNTFS詳細パーミッションビット）の完全再現・編集 |
| **変化点差分モーダル** | `DiffModalOverlay` | `MainWindow.Simulation.cs` | `SimModels.cs` | 移行前後（Before/After）の変化点（新規・移動・統合・ACL差分）の一覧レビューとExcel出力 |
| **移行パッケージ生成モーダル** | `MigrationPackageOverlay` | `MainWindow.Simulation.cs` | `MigrationPackageService.cs`<br>`MigrationPackageModels.cs`<br>`ExcelReportService.cs` | ベンダー標準移行工程（事前フル同期、中間差分、本番切替）の一括静的生成、波次（Wave）自動分割・容量バジェット算定、動的転送レート・差分率による所要時間算出、容量二重加算防止、実測ファイル数引き継ぎ、安全停止手順書ガイド、週末枠オーバー警告、Migration_Runbook.xlsx（WBS/進捗台帳・マッピング・除外一覧） |

---

## 3. 重要な設計判断の記録（Architecture Decisions / ADR）

> ⚠️ **後続のAIメンテナへ**:
> 本プロジェクトの全 63 項目に及ぶ詳細な設計判断記録（ADR 1〜63）は、トークン消費削減および可読性維持のため [`.agents/ADR.md`](.agents/ADR.md) に体系化・外部保管されている。
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
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
Copy-Item -Path ".\bin\Release\net8.0-windows\win-x64\publish\FolderMorpher.exe" -Destination ".\FolderMorpher.exe" -Force
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