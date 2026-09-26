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

## 1. プロジェクト概要 & アーキテクチャ構成

- **アプリケーション名**: `FolderMorpher` (旧 AstraSize)
- **種別**: Windows デスクトップ向け 大容量ファイルサーバー監視 & NTFSアクセス権移行・シミュレーションスタジオ
- **フレームワーク**: .NET 8.0, C# 12
- **実行境界 (ADR 101・102)**: 配布は `FolderMorpher.exe` 1本。通常起動はGUI、`--host` は同じEXEの別Hostプロセス。ソース依存は `UI -> Contracts <- Host -> Core`。起動分岐はルート `App.xaml.cs`。
  1. **`FolderMorpher.Contracts`**: Core/WPF非依存のRPC契約とDTO。循環参照する画面モデルをそのままパイプへ渡さない。
  2. **`FolderMorpher.Core`**: `UseWPF=false` のヘッドレス・クラスライブラリ。走査、検索、ACL、監査、移行などの実処理を持つ。モデルの色・バッジ・サイズ表示はUI側へ分けた。レポートに使うローカライズ済みの文言は一部Coreに残るため、変更時は出力意味論を確認する。
  3. **`FolderMorpher.Host`**: `UseWPF=false` のライブラリ。`FolderMorpher.exe --host` で起動し、Coreサービス、SQLite、設定、ジョブを所有する。Named PipeはユーザーSIDとセッションを識別し、`PipeOptions.CurrentUserOnly` を使用。
  4. **`FolderMorpher.UI`**: WPF画面、画面用モデル、DTO変換、IPCクライアントを持ち、プロジェクト参照はContractsのみ。Coreモデルの事実部分は同一ソースをUIでもコンパイルし、分離済みの表示部分はUI partial classで足す。共有ソースの検索構文解析は残存する。
     - `FolderMorpherHostClient` はHost未起動時に同じEXEを `--host` で起動し、切断時に再接続する。
     - 通常のウィンドウ終了ではHostのJobを継続する。環境設定の「アプリとHostを終了」は実行中Jobを確認してから `RequestShutdownAsync` でHostを正常終了する。
- **ビルド形態**: `Release win-x64` の **自己完結型（Self-Contained）単一実行可能ファイル (`FolderMorpher.exe`)**
  - ネイティブWPFエンジンDLLおよび SQLite ネイティブDLLはEXE内部にバンドルされる。
  - `-p:EnableCompressionInSingleFile=true` による Deflate 圧縮を標準採用。

---

## 2. システム鳥瞰マップ（機能とソースコードの対応表）

UI層は `MainWindow.xaml` / `MainWindow.xaml.cs`（機能別に partial class 分割）と `Views/LiveAclStudio.xaml` / `LiveAclStudio.xaml.cs`。業務処理は `HostClient/FolderMorpherHostClient.cs` からDTOでHostへ委譲する。UI固有の表示処理は `FolderMorpher.UI/Models`、設定の永続化はHostが所有する。

| 機能領域 / タブ | XAML (MainWindow / View) | C# コードビハインド | 関連 Service / Model | 責務と概要 |
| :--- | :--- | :--- | :--- | :--- |
| **全体共通 / 左サイドバー** | `SidebarBorder`, `SidebarToggleButton` (L22-75) | `MainWindow.xaml.cs`<br>`MainWindow.Localization.cs` | `Converters/ValueConverters.cs` | 収縮対応ナビゲーション（幅220px ⇄ 58px）、グローバルステータスバー、通知トースト、言語切替（日英）、環境設定モーダル |
| **Tab 1: 容量分析**<br>(Storage Explorer) | `StorageTabPanel` (L82-410)<br>`HistoryWindow.xaml` | `MainWindow.Storage.cs`<br>`HistoryWindow.xaml.cs` | `DiskScanService.cs`<br>`SqliteTreeCacheService.cs`<br>`StorageHistoryService.cs`<br>`ScanTabModel.cs`<br>`FileItemNode.cs` | Hostからルートと直下だけを取得し、フォルダー展開時に子の一階層を取得する。容量上位Top10と直下シェアもHostで集計。複数タブ、容量推移、予測を提供 |
| **Tab 2: ファイル検索**<br>(Search Studio) | `SearchTabPanel`<br>(`MainWindow.xaml`) | `MainWindow.Search.cs` | `SearchEngineService.cs`<br>`ServerSearchAccelerator.cs`<br>`WindowsSearchProvider.cs`<br>`TreeCachePruningIndex.cs`<br>`PathCanonicalizer.cs`<br>`SharedIoGovernor.cs`<br>`ContentExtractionService.cs`<br>`SearchQueryParser.cs`<br>`PdfSearchHelper.cs`<br>`SearchModels.cs` | **検索専用DBなし（現行スキャンツリーのメモリ照合・ローカルTreeCache逐次照合・未スキャン対象のLive直接走査）**、**サーバー側インデックス拝借＆候補ピンポイント原本確認（ServerSearchAccelerator: WSP / Synology 等）**、**Producer-Consumer Channel パイプライン（最大12並行）**、ripgrep流 64KBスライディングバッファ直接走査、Office/PDF 境界分割保護＆二重解析根絶、UNCルート単位 I/O ガバナー（`SharedIoGovernor` AIMD）、パス正規化エンジン（`PathCanonicalizer`: Z:\ ⇄ UNC 自動解決）、Tabler File-Type バッジ、高機能検索クエリ構文（ワイルドカード・論理演算・属性指定）、右クリック連携およびExcel/CSV出力 |
| **Tab 3: 権限コントロール & 逆引き監査**<br>(Live ACL & Effective Access) | `Views/LiveAclStudio.xaml`<br>(`LiveAclFolderView`, `LiveAclReverseView`, `LiveAclDiffModalOverlay`, `NewFolderModalOverlay`) | `Views/LiveAclStudio.xaml.cs` | `AclService.cs`<br>`EffectiveAccessService.cs`<br>`ActiveDirectoryService.cs`<br>`AclModels.cs`<br>`EffectiveAccessModels.cs` | 実環境NTFS ACL可視化・編集、**Dry-Run差分チェックモーダル（AclChangePlan貫通・継承変更警告・セマンティックVerify・SDDLロールバック）**、AD逆引き権限監査、均一幅ADアカウントカード、ADパレットUI統一、ADバックグラウンド自動同期、ツリーインライン新規フォルダー作成 |
| **Tab 4: 移行スタジオ**<br>(Simulation Studio) | `SimulationTabPanel` (L608-995) | `MainWindow.Simulation.cs` | `SimulationProjectService.cs`<br>`MigrationPackageService.cs`<br>`MigrationPackageModels.cs`<br>`SimModels.cs` | 現行ファイルサーバーから新環境への仮想ツリー設計（N:1マッピング）、ACL引き継ぎ設計、ADパレット統一、全画面・全出力完全日英両対応、ヘッダーレイアウト整線、ガワ先行作成の実機DACLセマンティックVerify、エンタープライズ移行パッケージ出力（TargetRoot必須検証・Wave分割・Runbook Excel・安全停止手順・多重コピー防止/XD・%~dp0相対ログ・exit /b 1・遅延展開排除・Dry-Run bat同梱） |
| **Tab 5: リンク修復**<br>(LinkFixer) | `LinkFixTabPanel` (L998-1094) | `MainWindow.LinkFix.cs` | `LinkFixService.cs`<br>`OfficeLinkFixService.cs` | サーバー移行後の切断ショートカット（.lnk）およびOffice内部リンク（.xlsx/.xlsm）検出・修復、**VBAマクロ非破壊保護＆通常XML混在時の部分修復（PartiallyFixed）**、全社配布用GPOログオンスクリプト（.ps1）生成 |
| **Tab 6: 整理候補発見 ＆ 健全化**<br>(Smart Hygiene & Candidates) | `AuditTabPanel` (L1097-1240) | `MainWindow.Audit.cs` | `AuditReportService.cs`<br>`HygieneCandidateEngine.cs`<br>`AuditIgnoreService.cs`<br>`ExcelReportService.cs`<br>`AuditModels.cs` | **インテリジェント整理候補発見スタジオへのすり替え**、**「このファイルは捨てられる可能性が高い。理由はこれ。」説明責任ヒューリスティクス**、**新Auditの $O(N)$ ボトムアップ集約化（10万階層でも高速・メモリ最小化）**、**整理除外リストの PathCanonicalizer 適用（Z:\ と UNC 同一視）**、**直感的な単語指標（整理推奨/要確認/参考）**、**スコア内訳可視化（クリック/ホバー）**、**表示件数制御（上位100件等）**、**整理除外リスト（自己治癒性スキップ記憶）**、**3大重点候補（①世代・旧版、②展開済ZIP残骸、③墓場フォルダー化石化判定）**、完全重複（SHA256）、休眠ファイル（3年超・1年閲覧保護）、パス長危険域（240字超）・禁則文字検出、フォルダー名部分一致除外。5連スリムメトリクスバー、一括選択プリセット、ハイパーリンク付きExcel/CSVレポート出力、**原本保護＆削除直前SHA-256再照合付き安全完全削除** |
| **Tab 7: メディア最適化**<br>(Media Optimizer) | `MediaTabPanel` (L1243-1380) | `MainWindow.Media.cs` | `MediaOptimizerService.cs`<br>`ExcelReportService.cs`<br>`MediaOptimizerModels.cs` | 保護対象（_Master/RAW等）付き写真・画像軽量化（長辺2560px超縮小/85%品質/日時・Exif保持/直接上書き）、大容量動画Topランキング抽出、夜間GPU圧縮（H.265）バッチ生成 |
| **詳細権限モーダル** | `SecModalOverlay` | `MainWindow.Simulation.cs` | `AclModels.cs` | Windows標準セキュリティ詳細設定（14項目のNTFS詳細パーミッションビット）の完全再現・編集 |
| **変化点差分モーダル** | `DiffModalOverlay` | `MainWindow.Simulation.cs` | `SimModels.cs` | 移行前後（Before/After）の変化点（新規・移動・統合・ACL差分）の一覧レビューとExcel出力 |
| **移行パッケージ生成モーダル** | `MigrationPackageOverlay` | `MainWindow.Simulation.cs` | `MigrationPackageService.cs`<br>`MigrationPackageModels.cs`<br>`ExcelReportService.cs` | ベンダー標準移行工程（事前フル同期、中間差分、本番切替）の一括静的生成、波次（Wave）自動分割・容量バジェット算定、動的転送レート・差分率による所要時間算出、容量二重加算防止、実測ファイル数引き継ぎ、安全停止手順書ガイド、週末枠オーバー警告、Migration_Runbook.xlsx（WBS/進捗台帳・マッピング・除外一覧） |

**検索の現行入口**: `MainWindow.Search.cs` → `FolderMorpherHostClient` → `HostService.Search.cs` → `SearchEngineService`。直接走査は `SafeFileEnumerator.EnumerateFileEntriesParallelAsync(..., collectResults: false)` のコールバックで逐次処理する。ファイル名・パス・本文条件の共通判定は `SearchEngineService.MatchesSearchTerms` が正本。`TreeCachePruningIndex` はフォルダー時刻だけでは検索の完全性を保証できないため、通常画面の直接走査では構築しない。

スキャンツリーがHostメモリにない場合のキャッシュ検索は、TreeCacheを全ツリーへ復元せず `SqliteTreeCacheService.EnumerateSearchEntries` から逐次照合する。再スキャンの前回差分も旧ツリーを復元せずDB行を逐次読む。どちらもローカルDBだけを読むのでUNCへの追加I/Oは発生しない。

GUIの検索実行はLiveとキャッシュの双方をHost Jobとして所有し、停止・クリア・入力変更で旧Jobへキャンセルを伝える。空の検索条件では自動検索を起動しない。Host Jobのキャンセルは `HostJobClient` と `HostService.Jobs.cs` が正本で、GUIは世代番号で遅延応答を破棄する。

PDFのネイティブ `LoadIFilter` 呼び出しはWindows APIと同じ3引数を保つ（ADR 107）。P/Invoke宣言を変更する時はMicrosoftのシグネチャと照合し、検索回帰でプロセス終了時のCOM最終化も確認する。

**旧方式**: 検索専用 FTS5 と Watcher は ADR 87 で通常画面から退役し、ADR 100 で旧サービスと専用テストも撤去した。`Microsoft.Data.Sqlite` は現行の `SqliteTreeCacheService` で引き続き使用する。

`USER_REQUIREMENTS.md` はユーザーの意図で Git 管理から除外されている。無断で追跡・公開しない。ローカルにある場合は要求の正本として参照する。後続 ADR と記述が異なる箇所は時系列とユーザーの最新指示を確認する。GitHub 上で見えないことを理由に要求が存在しないと推定しない。

---

## 3. 重要な設計判断の記録（Architecture Decisions / ADR）

> ⚠️ **後続のAIメンテナへ**:
> 本プロジェクトの設計判断記録（ADR 1〜105）は、トークン消費削減および可読性維持のため [`.agents/ADR.md`](.agents/ADR.md) に体系化・外部保管されている。
> **仕様変更・機能改修を行う際は、必ず `.agents/ADR.md` を参照し、過去の設計意図を無視した安易なコード巻き戻しを行ってはならない。**
> 新たな設計判断を追加した場合は、`.agents/ADR.md` を最新の状態に同期すること。

#### 主要な中核原則サマリー（詳細は `.agents/ADR.md` 参照）

設計判断（ADR 1〜105）は、以下の **8大中核アーキテクチャ原則** に集約される。後続のメンテナは、これらの仕様・制約を安易に巻き戻してはならない。

1. **全体占有率メーター & 2連カード（Storage / ADR 61）**:
   - 親フォルダーに対する直下シェア（右ペイン「選択フォルダーの内訳」）と、スキャン対象ルート総容量に対する全体占有率を二重加算防止のため厳格分離。ルート行は `―`（ハイフン）表示。メトリクスカードは「スキャン対象 容量」「前回差分推移」の2連カード化。
2. **NTFS ACL 安全機構 & DACL完全一致検証（Live ACL / ADR 2, 9, 31, 40, 52, 62）**:
   - 変更前に SDDL を自動スナップショットし、ワンクリックで原子的復元（`ApplyLiveAclWithRollback`）。評価順序は Windows Canonical DACL Ordering（Explicit Allow > Inherited Deny）を厳守。スナップショット永続化が1件も成功しない場合はコミットを絶対拒否。
   - `VerifyFolderDacl` による継承・明示ACE突合および計画にない予期せぬ余計なACE（不法侵入ACE）の完全検知・検証一本化。`SimAclEntry` のSID最優先判定による混在環境セキュリティ境界の正本化。
3. **ファイル監査・整理の安全原則 ＆ 理由付き整理候補発見（Audit & Hygiene / ADR 5, 28, 53, 88, 89, 90, 97）**:
   - ツールによるファイル直接削除は手動オプトインによる完全削除に限定。原本候補（`IsOriginalCandidate`）は無条件で削除拒否。重複削除直前に原本と対象ファイルのSHA-256を再照合し誤削除ゼロ保証。
   - 休眠ファイル判定は更新日3年超に加え、直近1年間（365日）に閲覧されたファイル（LastAccessTime）を自動保護・除外。フォルダー名部分一致除外により不要フォルダーをI/Oゼロでスキップ。
   - 「このファイルは捨てられる可能性が高い。理由はこれ。」説明責任ヒューリスティクス（世代旧版・展開済ZIP残骸・墓場フォルダー化石化判定）を $O(N)$ ボトムアップ集約で高速抽出。
4. **メディア最適化の聖域保護（Media Optimizer / ADR 6, 32, 54）**:
   - プロ用聖域フォルダー（`_Master`, `RAW` 等）およびプロ用拡張子（`.psd`, `.ai`, `.raw` 等）の自動スキップ保護。JPEG/PNG の日時・Exif・回転情報の 100% 保持、アトミック置換。メディア走査を `SafeFileEnumerator` へ統合。
5. **UNC/ネットワーク走査 ＆ 適応並列度ガバナー（Win32 Native & SharedIoGovernor / ADR 8, 36, 45, 80, 90, 91, 98）**:
   - `FindFirstFileExW` (`FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`) による巨大バッファ一括取得 & 8.3短縮名スキップ。`SafeFindHandle` (RAII) によるリーク根絶。4段自動フォールバック。
   - フォルダー単位RPCの完全根絶（ゼロI/O化）: 列挙タイムスタンプを子ノード生成時に直結。
   - `SharedIoGovernor`: ディレクトリ列挙専用コントローラー（安全な2並列固定・上限2）と本文読み込み専用コントローラー（AIMD: 4 ➔ 最大12並列）を完全分離。
   - 純粋I/O時間計測（CPU展開・パース時間を除外した真のネットワーク遅延）と、再昇格可能な AIMD（不可逆崖落ち永久固定の撤廃）により、サーバーを保護しつつ SMB スループットを最大化。重複 RPC（事前の `File.Exists`、既知サイズの `FileInfo.Length`）を全廃。
6. **検索スタジオのアーキテクチャ（Search Studio / ADR 87, 90, 93, 94, 95, 96, 97, 99, 108, 109）**:
   - **非管理者権限が通常経路の前提**: 管理者権限が必要なUSN変更ジャーナル等に基本検索や検索結果の完全性を依存させない。ローカル・UNCの双方で一般ユーザー権限による検索を維持する（ADR 108）。
   - **索引なしの非一致証明を先行調査**: 永続CプランIndexは保留。追加I/Oも含めて効果を測り、確実に不一致と言えない場合は通常の本文検査へ進める（ADR 109）。
   - **検索専用ローカルDBの完全撤去**: Host内の現行スキャンツリーはメモリ照合、永続TreeCacheは行単位で逐次照合、未スキャンUNCはストリーミングLive直接走査とする。条件判定は `SearchEngineService` に集約する。
   - **サーバー側インデックス拝借（ServerSearchAccelerator）**: Windows Server WSP / Synology 等の既存インデックスから候補を秒速取得しつつ、網羅走査を併用して False Negative を完全防止。
   - **ストリーミング走査の最適化**: ripgrep流 64KBスライディングバッファ直接走査、Office書式境界分割保護（`<w:t>` 連続結合・HTMLデコード・セル境界空白保護）、PDF正常非一致の早期脱落、親子局所性（Locality-First LIFO走査）、パス正規化エンジン（`PathCanonicalizer`: Z:\ ⇄ UNC 自動解決＆同一視）。
   - **列挙結果の保持を選択**: 共通列挙器は既定で一覧を返す。検索の逐次コールバック利用時は `collectResults: false` とし、全件を別途メモリへ蓄積しない。名前・パス・本文条件の共通判定は `MatchesSearchTerms` に集約。
   - **UI・デザイン言語**: フル幅モダンカードリスト、Tabler File-Type バッジ（Option 1 折れ曲がり角付き書類ベクターアイコン ＆ 統一フォルダー）。
7. **SQLite ローカル専有ツリーキャッシュ ＆ ポータブル JSON 相互運用（ADR 98・103〜105）**:
   - **完全ローカル専有**: `%LocalAppData%\FolderMorpher\TreeCache\tree_cache.db`（WALモード）にのみ DB を配置。共有フォルダー（UNC）には一切 DB を置かず、ロック競合・遅延破損をゼロ化。
   - **事前集計と階層単位の取得**: 集計値を各ノードに保存し、GUIへの全件復元・全件IPC送信を避ける。`LoadTreeCacheBranchAsync` と `GetStorageChildrenAsync` はルートまたはクリックされたフォルダーの直下だけ読む。検索・エクスポート用の全ツリー読込は別経路に残る。
   - **親ID・名前による省容量化**: `FullPath/ParentPath` とその索引を廃止し、`RootId, ParentId, Name` を正本にする。例外行だけ `PathSuffix` を保存。日時は整数、正規SHA-256は32バイトBLOB、部分木の連続ID範囲は `SubtreeEndId` に保持する。`IsExpanded/Level` はDBに保存しない。旧DBはトランザクション移行、件数・親リンク検証、VACUUMを行う。実DBコピー約165万ノードで約651MB→198MBを確認。旧HostとのIPCはバージョン不一致を明示して接続を拒む。
   - **単一トランザクション一括コミット**: DB保存は一括トランザクション。数百万ノードの保存所要時間は別途計測し、以前の「0.1〜0.3秒」を保証値と扱わない。
   - **DB直接 SHA-256 更新**: `UpdateSha256Async` によりメモリ展開ゼロで高速 UPDATE。
   - **ポータブル JSON 相互運用**: `ExportToJsonFileAsync` / `ImportFromJsonFileAsync` により社内配布・共有用には単一 JSON を出力。既存 JSON キャッシュからの自動透過マイグレーション完備。
8. **UI共通整線 ＆ 深階層ツリー操作性（ADR 65, 66, 69）**:
   - Quiet Fluent 1行スリムメトリクスバーの全画面統一（面＋微細線、ドロップシャドウ撤去）。
   - 移行ツリー Undo 機能（戻るボタン ＋ Ctrl+Z）による枠外ドロップ削除の完全可逆化。
   - 深階層でも迷わない 4 大工夫: インテリジェント動的サブ化、ドロップ先端ハイライト、ホバー自動展開（400ms）、端点自動スクロール（25px）。


---

## 4. ビルド・実行・検証コマンド

### ビルド（0 警告・0 エラーを維持すること）
```powershell
& "$HOME\.dotnet\dotnet.exe" build
```

### 配布用単一EXEの生成（Release self-contained・圧縮約75.5MB）
```powershell
$env:PATH = "C:\Users\iwakura\.dotnet;" + $env:PATH
& "$HOME\.dotnet\dotnet.exe" publish ./FolderMorpher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o ./publish-single
# Google Drive同期時は社内互換のため2つとも配置すること
Copy-Item ./publish-single/FolderMorpher.exe "G:\マイドライブ\FolderMorpher\FolderMorpher.exe" -Force
Copy-Item ./publish-single/FolderMorpher.exe "G:\マイドライブ\FolderMorpher\FolderCleaner.exe" -Force
```

### 自動回帰テストスイート（ヘッドレス自己検証・CIゲート）
バグ修正やリファクタリング後は、必ず以下の回帰テストを実行して 8/8 ALL PASSED（8ドメイン通過）であることを確認すること。8/8 は網羅率を意味しない。検索テストは `RegressionTestSuite.Search.cs`（構文・現行経路）、`.Search.CurrentRoutes.cs`（名前・本文の意味論）、`.Search.Infrastructure.cs`（照合・列挙）、`.Search.ServerContent.cs`（WSP・抽出）に分ける。
```powershell
& "$HOME\.dotnet\dotnet.exe" run --no-build -- --test-regression
```

### 単一EXEのIPC統合試験
配布物を生成後、`publish-single/FolderMorpher.exe --test-ipc` を実行する。試験専用Named Pipe・Mutex・一時SQLiteで別PIDの `--host` を起動し、普段のHost/DBへ触れずStorage/Search/ACL/Audit/移行/設定のDTO、検索Clear/Stop、Host正常終了を確認する。

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

