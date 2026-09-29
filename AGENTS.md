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
- **フレームワーク**: .NET 10.0, C# 14
- **実行境界 (ADR 101・102)**: 配布は `FolderMorpher.exe` 1本。通常起動はGUI、`--host` は同じEXEの別Hostプロセス。ソース依存は `UI -> Contracts <- Host -> Core`。起動分岐はルート `Bootstrap.cs`。
  1. **`FolderMorpher.Contracts`**: Core/WPF非依存のRPC契約とDTO。循環参照する画面モデルをそのままパイプへ渡さない。
  2. **`FolderMorpher.Core`**: `UseWPF=false` のヘッドレス・クラスライブラリ。走査、検索、ACL、監査、移行などの実処理を持つ。モデルの色・バッジ・サイズ表示はUI側へ分けた。レポートに使うローカライズ済みの文言は一部Coreに残るため、変更時は出力意味論を確認する。
  3. **`FolderMorpher.Host`**: `UseWPF=false` のライブラリ。`FolderMorpher.exe --host` で起動し、Coreサービス、SQLite、設定、ジョブを所有する。Named PipeはユーザーSIDとセッションを識別し、`PipeOptions.CurrentUserOnly` を使用。
  4. **`FolderMorpher.UI`**: WPF画面、画面用モデル、DTO変換、IPCクライアントを持ち、プロジェクト参照はContractsのみ。Coreモデルの事実部分は同一ソースをUIでもコンパイルし、分離済みの表示部分はUI partial classで足す。共有ソースの検索構文解析は残存する。
     - `FolderMorpherHostClient` はHost未起動時に同じEXEを `--host` で起動し、切断時に再接続する。GUIより旧版のHostにJobがなければ正常終了を要求して新版Hostを起動し、Job実行中なら止めずに理由を表示する。GUIが旧版ならHostを巻き戻さない。
     - 通常のウィンドウ終了ではHostのJobを継続する。環境設定の `CloseHostOnWindowClose` は初期値オフで、オンの場合はウィンドウを閉じる際にHostのJobをキャンセルして `RequestShutdownIfRunningAsync` で正常終了する。閉じる操作で新しいHostを起動しない（ADR 118）。
- **ビルド形態**: `Release win-x64` の **自己完結型（Self-Contained）単一実行可能ファイル (`FolderMorpher.exe`)**
  - 配布出力の正本はリポジトリ直下の `dist/FolderMorpher.exe`。`FolderMorpher.csproj` の `PublishDir` とCI・手元の手順を同じ場所へ統一する。`bin` 内の旧 `AstraSize.*` は現在のビルド成果物ではない（ADR 115）。
  - ネイティブWPFエンジンDLLおよび SQLite ネイティブDLLはEXE内部にバンドルされる。
  - `-p:EnableCompressionInSingleFile=true` による Deflate 圧縮を標準採用。

---

## 2. システム鳥瞰マップ（機能とソースコードの対応表）

UI層は `MainWindow.xaml` / `MainWindow.xaml.cs`（機能別に partial class 分割）と `Views/LiveAclStudio.xaml` / `LiveAclStudio.xaml.cs`。業務処理は `HostClient/FolderMorpherHostClient.cs` からDTOでHostへ委譲する。UI固有の表示処理は `FolderMorpher.UI/Models`、設定の永続化はHostが所有する。

| 機能領域 / タブ | XAML (MainWindow / View) | C# コードビハインド | 関連 Service / Model | 責務と概要 |
| :--- | :--- | :--- | :--- | :--- |
| **全体共通 / モード・作業スコープ・フォルダー操作** | `MainWindow.xaml` の `AdminSidebar`、`NavTab*`、`ScopePopup`、`CleanupSectionBar` | `MainWindow.Scope.cs`<br>`MainWindow.Localization.cs`<br>`MainWindow.ScanEta.cs`<br>`FolderMorpher.UI/Dialogs/AppDialog.cs` | `AppSettingsDto`<br>`HostService.BrowseChildFoldersAsync` | 情シス向け左バーはヘッダーボタンで全収納でき、既定は収納。一般向けは常に非表示。参照フォルダーの選択欄は名称に依存しない固定幅で長い名称を省略し、全パスはツールチップで見せる。追加は展開した一覧内のみ、手入力欄は置かない。展開中の最近の場所だけホバー行の×で登録解除し、原本とTreeCacheは保つ。検索・容量・整理はフォルダー下の切替。整理の完全削除だけ離す。走査の見込みは `MainWindow.ScanEta.cs` が扱い、検索は経過時間の隣、容量・整理・メディアは件数の隣、リンクは検出件数の隣、ACL逆引きは件数帯の直下に表示する。初回は十分な進捗後に安全側へ見積もり、根拠がなければ見積もり中と表示する。容量では未処理ディレクトリ数、MFT利用時は総レコード数も観測し、2回目以降は前回の所要時間や件数を補正に使う。表示済みの予定時刻は前倒しだけで更新し、超過したら明示する。見積り目的の追加走査は行わない。設定の `AdminSidebarCollapsed/ScanDurationsSeconds` はHostが永続化する（ADR 116〜122）。 |
| **Tab 1: 容量分析**<br>(Storage Explorer) | `StorageTabPanel`<br>`HistoryWindow.xaml` | `MainWindow.Storage.cs`<br>`HistoryWindow.xaml.cs` | `DiskScanService.cs`<br>`StorageAvailableSpaceService.cs`<br>`SqliteTreeCacheService.cs`<br>`StorageHistoryService.cs`<br>`ScanTabModel.cs`<br>`FileItemNode.cs` | Hostからルートと直下だけを取得し、フォルダー展開時に子の一階層を取得する。接続ユーザーの残量が10%以下なら警告。容量上位Top10と直下シェアは詳細を開いた時に表示。複数タブ、容量推移、予測を提供 |
| **Tab 2: ファイル検索**<br>(Search Studio) | `SearchTabPanel`<br>(`MainWindow.xaml`) | `MainWindow.Search.cs` | `SearchEngineService.cs`<br>`ServerSearchAccelerator.cs`<br>`WindowsSearchProvider.cs`<br>`TreeCachePruningIndex.cs`<br>`PathCanonicalizer.cs`<br>`SharedIoGovernor.cs`<br>`ContentExtractionService.cs`<br>`SearchQueryParser.cs`<br>`PdfSearchHelper.cs`<br>`SearchModels.cs` | **検索専用DBなし（現行スキャンツリーのメモリ照合・ローカルTreeCache逐次照合・未スキャン対象のLive直接走査）**、**サーバー側インデックス拝借＆候補ピンポイント原本確認（ServerSearchAccelerator: WSP / Synology 等）**、**Producer-Consumer Channel パイプライン（最大12並行）**、ripgrep流 64KBスライディングバッファ直接走査、Office/PDF 境界分割保護＆二重解析根絶、UNCルート単位 I/O ガバナー（`SharedIoGovernor` AIMD）、パス正規化エンジン（`PathCanonicalizer`: Z:\ ⇄ UNC 自動解決）、Tabler File-Type バッジ、高機能検索クエリ構文（ワイルドカード・論理演算・属性指定）、右クリック連携およびExcel/CSV出力 |
| **Tab 3: 権限コントロール & 逆引き監査**<br>(Live ACL & Effective Access) | `Views/LiveAclStudio.xaml`<br>(`LiveAclFolderView`, `LiveAclReverseView`, `LiveAclDiffModalOverlay`, `NewFolderModalOverlay`) | `Views/LiveAclStudio.xaml.cs` | `AclService.cs`<br>`EffectiveAccessService.cs`<br>`ActiveDirectoryService.cs`<br>`AclModels.cs`<br>`EffectiveAccessModels.cs` | 実環境NTFS ACL可視化・編集、**Dry-Run差分チェックモーダル（AclChangePlan貫通・継承変更警告・セマンティックVerify・SDDLロールバック）**、AD逆引き権限監査、均一幅ADアカウントカード、ADパレットUI統一、ADバックグラウンド自動同期、ツリーインライン新規フォルダー作成 |
| **Tab 4: 移行スタジオ**<br>(Simulation Studio) | `SimulationTabPanel` (L608-995) | `MainWindow.Simulation.cs` | `SimulationProjectService.cs`<br>`MigrationPackageService.cs`<br>`MigrationPackageModels.cs`<br>`SimModels.cs` | 現行ファイルサーバーから新環境への仮想ツリー設計（N:1マッピング）、ACL引き継ぎ設計、ADパレット統一、全画面・全出力完全日英両対応、ヘッダーレイアウト整線、ガワ先行作成の実機DACLセマンティックVerify、エンタープライズ移行パッケージ出力（TargetRoot必須検証・Wave分割・Runbook Excel・安全停止手順・多重コピー防止/XD・%~dp0相対ログ・exit /b 1・遅延展開排除・Dry-Run bat同梱） |
| **Tab 5: リンク修復**<br>(LinkFixer) | `LinkFixTabPanel` (L998-1094) | `MainWindow.LinkFix.cs` | `LinkFixService.cs`<br>`OfficeLinkFixService.cs` | サーバー移行後の切断ショートカット（.lnk）およびOffice内部リンク（.xlsx/.xlsm）検出・修復、**VBAマクロ非破壊保護＆通常XML混在時の部分修復（PartiallyFixed）**、全社配布用GPOログオンスクリプト（.ps1）生成 |
| **Tab 6: 整理候補発見 ＆ 健全化**<br>(Smart Hygiene & Candidates) | `AuditTabPanel` (L1097-1240) | `MainWindow.Audit.cs` | `AuditReportService.cs`<br>`DuplicateHashPipeline.cs`<br>`HygieneCandidateEngine.cs`<br>`AuditIgnoreService.cs`<br>`ExcelReportService.cs`<br>`AuditModels.cs` | **インテリジェント整理候補発見スタジオへのすり替え**、**「このファイルは捨てられる可能性が高い。理由はこれ。」説明責任ヒューリスティクス**、**新Auditの $O(N)$ ボトムアップ集約化（10万階層でも高速・メモリ最小化）**、**整理除外リストの PathCanonicalizer 適用（Z:\ と UNC 同一視）**、**直感的な単語指標（整理推奨/要確認/参考）**、**スコア内訳可視化（クリック/ホバー）**、**表示件数制御（上位100件等）**、**整理除外リスト（自己治癒性スキップ記憶）**、**3大重点候補（①世代・旧版、②展開済ZIP残骸、③墓場フォルダー化石化判定）**、完全重複（SHA256）、休眠ファイル（3年超・1年閲覧保護）、パス長危険域（240字超）・禁則文字検出、フォルダー名部分一致除外。5連スリムメトリクスバー、一括選択プリセット、ハイパーリンク付きExcel/CSVレポート出力、**原本保護＆削除直前SHA-256再照合付き安全完全削除** |
| **整理内: メディア最適化**<br>(Media Optimizer) | `MediaTabPanel` | `MainWindow.Media.cs` | `MediaOptimizerService.cs`<br>`ExcelReportService.cs`<br>`MediaOptimizerModels.cs` | 整理画面の内部切替。保護対象（_Master/RAW等）付き写真・画像軽量化（長辺2560px超縮小/85%品質/日時・Exif保持/直接上書き）、大容量動画Topランキング抽出、夜間GPU圧縮（H.265）バッチ生成 |
| **詳細権限モーダル** | `SecModalOverlay` | `MainWindow.Simulation.cs` | `AclModels.cs` | Windows標準セキュリティ詳細設定（14項目のNTFS詳細パーミッションビット）の完全再現・編集 |
| **変化点差分モーダル** | `DiffModalOverlay` | `MainWindow.Simulation.cs` | `SimModels.cs` | 移行前後（Before/After）の変化点（新規・移動・統合・ACL差分）の一覧レビューとExcel出力 |
| **移行パッケージ生成モーダル** | `MigrationPackageOverlay` | `MainWindow.Simulation.cs` | `MigrationPackageService.cs`<br>`MigrationPackageModels.cs`<br>`ExcelReportService.cs` | ベンダー標準移行工程（事前フル同期、中間差分、本番切替）の一括静的生成、波次（Wave）自動分割・容量バジェット算定、動的転送レート・差分率による所要時間算出、容量二重加算防止、実測ファイル数引き継ぎ、安全停止手順書ガイド、週末枠オーバー警告、Migration_Runbook.xlsx（WBS/進捗台帳・マッピング・除外一覧） |

**検索の現行入口**: `MainWindow.Search.cs` → `HostJobClient` → `HostService.Jobs.cs` / `HostService.cs` → `SearchEngineService`。直接走査は `SafeFileEnumerator.EnumerateFileEntriesParallelAsync(..., collectResults: false)` のコールバックで逐次処理する。ファイル名・パス・本文条件の共通判定は `SearchEngineService.MatchesSearchTerms` が正本。`TreeCachePruningIndex` はフォルダー時刻だけでは検索の完全性を保証できないため、通常画面の直接走査では構築しない。

**初回性能の研究記録**: [`.agents/PERFORMANCE_RESEARCH.md`](.agents/PERFORMANCE_RESEARCH.md) にローカル・UNC双方の実験候補、採否と計測値をまとめる。採用済みの契約はADR 127〜130と実装を正本とし、Cドライブの所要時間からUNC性能を推定しない。

容量の通常走査回帰は `DiskScanService.ScanStandardPathAsync` を直接使う。管理者権限で動くCIでもMFTへ自動切替せず、今回の一括列挙・ワーカー集計を検証する。製品の入口 `ScanPathAsync` は従来どおりMFT可否を判断して同じ通常走査へ委譲する。

**容量通常走査と共通列挙（ADR 130）**: `DiskScanService` のローカル走査はCPU数に応じた最大8ワーカーで実行し、UNC用の遅延適応制御を適用しない。UNC・ネットワークドライブは従来の共有列挙枠2を維持する。一枝をワーカー内に残し、兄弟は共通キューへ渡す。容量上位・拡張子・軽量整理候補はワーカー専用に集計して完了後に合成し、進捗件数・容量は256ファイルまたはディレクトリ完了単位で反映する。非公開ツリーの並べ替えはコレクションの初期構築で行い、不要な変更通知を出さない。上位・拡張子の集計済み結果をルートに保持し、DB保存や詳細表示で再走査しない。中止は集計段階まで伝え、正常なツリーとして返さない。`NativeDirectoryEnumerator` はローカルで64KiBの `FileSystemEnumerable<NativeFindEntry>` を使い、名前・属性・サイズ・日時を一括列挙結果から得る。隠し・システム属性は除外せず、アクセス拒否を空フォルダーにしない。UNCおよび非対応環境の既存Win32フォールバックを保つ。`SafeFileEnumerator` も同じ列挙器を使い、ネットワーク判定はルートから引き継ぐ。通常権限・索引なしで比較し、外部CLIは配布へ含めない。

**初回Live走査からのTreeCache形成（ADR 125・127）**: TreeCacheにルートがないとき、検索と除外なしの整理は `SafeFileEnumerator` の同じ列挙結果を `TreeScanCapture.cs` へ流す。フォルダーを検索結果に含めない場合もキャッシュにはディレクトリを記録する。有界Channel・ローカル一時SQLiteの後、完走した完全カバレッジだけ `SqliteTreeCacheService.Capture.cs` が既存の `ParentId + Name` スキーマへ原子的に公開する。最初のアクセス拒否後は検索・監査を続けつつ一時DBへの新規投入を止める。並べ替え索引は完全走査の確認後だけ作る。中止・アクセス拒否・除外付き整理では公開しない。既存キャッシュを上書きせず、UNCを再走査しない。整理ボタンは実行中のみ「中止」になり、Host Jobのキャンセルを待って開始状態へ戻る。

**走査見込みと整理帯域（ADR 122）**: 検索のETAは結果見出しの経過時間の隣に、容量・整理・メディアは件数の隣、リンクは検出件数の隣、ACL逆引きは件数帯の直下に表示する。`MainWindow.ScanEta.cs` は件数・前回所要時間・初回の大きめの母数を用いて安全側に見積もる。進捗根拠のない初回走査はまず「見積もり中」とし、件数を返さない旧RPCだけ30秒後から広い暫定値を出す。MFTの総レコード数は確定母数として扱う。整理の帯域選択UIは撤去した。`AuditReportService` はローカルのSHA-256読み取りを制限せず、UNC・ネットワークドライブのみ実測p95遅延を見て8〜100MiB/sの範囲で調整する。並列度2は維持する。旧IPCの帯域指定値は互換のため残す。

**参照先・検索入力・容量詳細（ADR 123）**: 閉じた参照フォルダー選択欄は `ScopeSelectorButtonStyle` の単一面で輪郭・ホバーを描く。展開一覧の行は別の `ScopePickerButtonStyle` を使い、選択欄の輪郭を流用しない。下向きマークは丸い曲線のPath。検索入力の左余白は8px、案内文は9pxで、カーソルと案内文を揃える。容量分析はツリー全幅を初期表示とし、上位ファイル・選択フォルダー内訳は「詳細を表示」で右ペインを開く。右ペインを閉じている間は詳細取得RPCを起動しない。推移グラフは従来の別操作を維持する。

**低容量警告（ADR 124）**: Coreの `StorageAvailableSpaceService` は `GetDiskFreeSpaceExW` で接続ユーザー向けの利用可能量と総量を取得し、残り10%以下だけ警告とする。UIは `GetStorageAvailabilityAsync` IPCでタブ切替・対象変更・走査完了時はルート、ツリーでフォルダーを選んだ時はそのパス、ファイルなら親パスを200ms静観後に照会する。ルートの値を子に流用しない。平常時と取得できない時は表示を増やさない。FSRMの階層別クォータが容量APIで必ず見えるわけではなく、警告なしは個別クォータの安全証明ではない。

GUIは `SearchQuery.HasDeepFileIoRequirement` を正本としてLive本文・Officeリンク検索を起動する。`content:` 指定だけでも、対象ツリーがキャッシュ済みなら空結果で終わらせず原本を検索する。本文速度の比較はFolderMorpherの `SearchEngineService.SearchDirectFolderAsync` を試験プロセスから呼び、Cドライブ全体の実在する本文語で測る。この比較はGUI/Host IPCを含まない。Cドライブの結果からUNC固有のSMB効果を断定しない。

テキスト本文走査の文字バッファはプールで再利用し、返却時にクリアする。64KiB以下のファイルかつ短い検索語なら16KiB、その他は64KiB。単一語はバッファ上で直接照合し、ヒット時だけスニペット用文字列を作る。ファイルごとの先頭NUL判定は厳密な非一致証明ではないため、索引なし枝刈りとして一般化しない（ADR 109・127）。

単一ASCII語かつUTF-8判定のテキストは、同じFileStreamからbyte列を直接照合する。ASCII-onlyを最後まで読んだ非一致だけ確定し、非ASCII byteを含む非一致は既存のデコーダーへ巻き戻してUnicodeの大小文字照合を保つ。ヒット時だけスニペットを生成する。これはripgrep-allの「形式別抽出＋byte検索」の発想を独立実装した部分であり、AGPLのコードは取り込まない（ADR 128）。

大容量テキストの`FileStream`先読み幅は`AdaptiveTextReadController.cs`が検索セッション内で64/256/512/1024KiBから選ぶ。8MiB未満は64KiBを基本とし、16KiB以下の小ファイルだけ16KiBを使う。UNCとネットワークドライブの大容量読み幅は最大256KiB。64KiB以下では`StreamReader`の内部バッファを8KiBに抑える。Officeの主要本文Entryを先に読み、残りの対象Entryも検査する。検索理由はヒット時だけ作り、先頭判定用byte配列も再利用する（ADR 110・127）。

50MiB超のテキストは分散Probeの後で全文照合が必要な時だけ後回しにし、後段で同じProbeを繰り返さない。後段の並列数は最大2。大きい本文のReadBlock待ちを含むp95をガバナーへ保守的に報告する。`ReadBlockAsync`にはデコード時間も含むため、純粋なSMB READ所要時間とは呼ばない（ADR 127）。

`WindowsSearchProvider.QueryCandidatesAsync`は到達不能な対象にはOLE DBを開かず直接検索へ戻す。存在しないUNCへの接続はCOM最終化時にプロセスを落とす環境があったため、`Directory.Exists`の事前確認と強制GCを含む回帰検証を維持する（ADR 111）。

スキャンツリーがHostメモリにない場合のキャッシュ検索は、TreeCacheを全ツリーへ復元せず `SqliteTreeCacheService.EnumerateSearchEntries` から逐次照合する。再スキャンの前回差分も旧ツリーを復元せずDB行を逐次読む。どちらもローカルDBだけを読むのでUNCへの追加I/Oは発生しない。

GUIの検索実行はLiveとキャッシュの双方をHost Jobとして所有し、停止・クリア・入力変更で旧Jobへキャンセルを伝える。検索ボタンは実行中だけ同位置の「中止」に変わり、停止・完了で「検索」に戻る。空の検索条件では自動検索を起動しない。Host Jobのキャンセルは `HostJobClient` と `HostService.Jobs.cs` が正本で、GUIは世代番号で遅延応答を破棄する。検索ヒットは `GetSearchJobResultsAsync` の連番カーソルで途中表示し、Hostの途中送信用リングは最大2048件、完了時の `SearchResults` が最終正本。入力中のキャッシュ結果は前回の走査情報と明示する。検索ボタン・Enter・更新は原本をLive走査し、完了時はLiveヒットだけで最終一覧と件数を置き換える。途中表示ではパス重複を除き、Jobごとの進捗件数でヒット数を上書きしない。列挙不能フォルダーと読取失敗は別の未確認件数として表示し、古いキャッシュ行を確定ヒットへ混ぜない（すべての抽出器内部の失敗を検出できるわけではない）。更新開始時に成功トーストを出さない（ADR 132）。

`EffectiveAccessService.EvaluateEffectiveAccessOnAcl` は一致するACEを保存DACL順に処理する。継承の全Denyを全Allowより先へ移す四群集計は使わない。三世代の実フォルダーACLをWindowsの `AccessCheck` と突き合わせる回帰を維持する。これはメンバーシップ解決とNTFS DACLの監査であり、共有ACL・特権などを含むAuthz完全再現ではない。

ショートカットの変更失敗時は `LinkFixService.RestoreShortcutSnapshot` が復元未実施・復元検証成功・復元失敗を構造化して返す。直前スナップショットからコピー後に全文SHAを照合し、失敗時は復旧用ファイルを保持してパスを結果へ出す。スナップショット削除だけの失敗は復元失敗としない。処理対象は実拡張子で判定し、表示用FileTypeを信号線にしない（ADR 132）。

UNC/ネットワークドライブの容量スキャンは検索列挙と `SharedIoGovernor.EnumerationController` を共有し、同一共有先の列挙枠を合計2にする。本文はUNCだけAIMD 2〜12並列。ローカルは列挙4・本文8の固定上限を用い、SMBの遅延閾値でローカルReadが2並列へ落ちないようにする（ADR 128）。`SafeFileEnumerator` の列挙と巨大ファイル再検査は各コントローラーのリースを1回だけ取得する。権限逆引きの現在ユーザー判定は修飾名の短縮名一致を禁じ、SID一致または完全修飾名一致に限定する（ADR 112）。

整理候補の一覧は各行に点数と「内訳」操作を表示する。`AuditItem.ScoreBreakdown` が内訳の正本で、重複理由は原本以外に95点、原本候補に0点を与える。点数は整理の優先度であり、削除安全性の確率ではない。言語変更時は監査行の表示プロパティも通知する（ADR 113）。

監査候補は `AuditCandidateComposer` で物理パスごとに1行へ統合し、重複・休眠・世代など独立した理由の点を加算する（100点上限なし、重複95＋3年休眠70なら165点）。原本候補も他理由の点は表示するが、`IsOriginalCandidate` を引き継いで削除を拒否する。`AuditItem.IssueTypes` が複数理由の正本で、分類フィルター・一括選択・出力は `HasIssue` を使う。削減見込み容量は同一パスを1回だけ数える。SHA候補グループは容量降順で検証し、`GetAuditJobResultsAsync` の途中結果をGUIへ渡す。途中一覧は容量降順の暫定表示で閲覧・選択でき、削除と出力は最終報告ができてから有効にする（ADR 114）。

重複の初回判定は `DuplicateHashPipeline.cs` がサイズ→先頭/末尾4KiB→（ローカル32MiB以上の生き残り群だけ）先頭1MiB→完全SHA-256を所有する。部分判定の開始はローカル64KiB・UNC 1MiB。単独候補の残りは読まず、部分読取失敗時は群全体を完全SHAへ戻す。部分ハッシュを重複確定や削除許可には使わない。ローカルはサイズ群を最大4群先行し、全群合計4リーダーで256KiB同期逐次読込・SHA計算器とArrayPoolバッファーを再利用する。UNCは1群ずつ・全体2リーダー・64KiB非同期読込と既存帯域制御を維持し、列挙済みサイズを再照会しない。全文の実読取バイト数と既知サイズが違えば確定しない。結果は入力の容量降順で返し、集計・原本選択・途中表示は従来どおり逐次行う。中止時は先行ワーカーの終了を待つ。公開ハッシュAPIと削除前再照合も同じ `DuplicateHashReader` を使う（ADR 129）。

PDFのネイティブ `LoadIFilter` 呼び出しはWindows APIと同じ3引数を保つ（ADR 107）。P/Invoke宣言を変更する時はMicrosoftのシグネチャと照合し、検索回帰でプロセス終了時のCOM最終化も確認する。

**旧方式**: 検索専用 FTS5 と Watcher は ADR 87 で通常画面から退役し、ADR 100 で旧サービスと専用テストも撤去した。`Microsoft.Data.Sqlite` は現行の `SqliteTreeCacheService` で引き続き使用する。

`USER_REQUIREMENTS.md` はユーザーの意図で Git 管理から除外されている。無断で追跡・公開しない。ローカルにある場合は要求の正本として参照する。後続 ADR と記述が異なる箇所は時系列とユーザーの最新指示を確認する。GitHub 上で見えないことを理由に要求が存在しないと推定しない。

---

## 3. 重要な設計判断の記録（Architecture Decisions / ADR）

**配布ライセンスと説明の正本（ADR 131）**: 本体はMIT、依存の使用版に対応する全文・著作権・NOTICEはルート `THIRD-PARTY-NOTICES.txt` が正本。UIに埋め込み、`FolderMorpher.UI/Dialogs/LicenseNotices.cs` が設定から表示・コピーする。配布は引き続き1 EXE。確認対象・更新手順は `docs/DEPENDENCIES.md`。依存を更新したら直接・間接パッケージ、ネイティブDLL、自己完結ランタイムの実配布版を確認する。`tools/Test-LicenseNotices.ps1` は `.deps.json` に対する表記漏れをCI・配布CIで拒否するが、ライセンス条件の審査は代替しない。README日英は現在のフォルダー中心の操作と実装を説明し、旧画面・全文索引・0秒保証の説明へ戻さない。性能条件の詳細は既存の研究記録を参照する。設定の共有データ説明は `Strings.Settings*Desc` を正本にする。

> ⚠️ **後続のAIメンテナへ**:
> 本プロジェクトの設計判断記録（ADR 1〜133）は、トークン消費削減および可読性維持のため [`.agents/ADR.md`](.agents/ADR.md) に体系化・外部保管されている。
> **仕様変更・機能改修を行う際は、必ず `.agents/ADR.md` を参照し、過去の設計意図を無視した安易なコード巻き戻しを行ってはならない。**
> 新たな設計判断を追加した場合は、`.agents/ADR.md` を最新の状態に同期すること。

#### 主要な中核原則サマリー（詳細は `.agents/ADR.md` 参照）

設計判断（ADR 1〜133）は、以下の **8大中核アーキテクチャ原則** に集約される。後続のメンテナは、これらの仕様・制約を安易に巻き戻してはならない。

1. **全体占有率メーター & 2連カード（Storage / ADR 61）**:
   - 親フォルダーに対する直下シェア（右ペイン「選択フォルダーの内訳」）と、スキャン対象ルート総容量に対する全体占有率を二重加算防止のため厳格分離。ルート行は `―`（ハイフン）表示。メトリクスカードは「スキャン対象 容量」「前回差分推移」の2連カード化。
2. **NTFS ACL 安全機構 & DACL完全一致検証（Live ACL / ADR 2, 9, 31, 40, 52, 62）**:
   - 変更前にSDDLを自動スナップショットし、`ApplyLiveAclWithRollback`で復元する。実効権限は保存DACLの順序を保ち、親・祖父母の継承世代を混ぜてDenyを先頭へ集めない。先行Allowで成立したビットは後続Denyで取り消さない（ADR 132）。スナップショット永続化が1件も成功しない場合はコミットを拒否する。
   - `VerifyFolderDacl` による継承・明示ACE突合および計画にない予期せぬ余計なACE（不法侵入ACE）の完全検知・検証一本化。`SimAclEntry` のSID最優先判定による混在環境セキュリティ境界の正本化。
3. **ファイル監査・整理の安全原則 ＆ 理由付き整理候補発見（Audit & Hygiene / ADR 5, 28, 53, 88, 89, 90, 97）**:
   - ツールによるファイル直接削除は手動オプトインによる完全削除に限定。原本候補（`IsOriginalCandidate`）は無条件で削除拒否。重複削除直前に原本と対象を全文SHA-256で再照合し、不一致・読取失敗なら拒否する。照合後の外部変更まで排除するものではなく、誤削除ゼロとは保証しない。
   - 休眠ファイル判定は更新日3年超に加え、直近1年間（365日）に閲覧されたファイル（LastAccessTime）を自動保護・除外。フォルダー名部分一致除外により不要フォルダーをI/Oゼロでスキップ。
   - 「このファイルは捨てられる可能性が高い。理由はこれ。」説明責任ヒューリスティクス（世代旧版・展開済ZIP残骸・墓場フォルダー化石化判定）を $O(N)$ ボトムアップ集約で高速抽出。
4. **メディア最適化の聖域保護（Media Optimizer / ADR 6, 32, 54）**:
   - プロ用聖域フォルダー（`_Master`, `RAW` 等）およびプロ用拡張子（`.psd`, `.ai`, `.raw` 等）の自動スキップ保護。JPEG/PNG の日時・Exif・回転情報の 100% 保持、アトミック置換。メディア走査を `SafeFileEnumerator` へ統合。
5. **UNC/ネットワーク走査 ＆ 適応並列度ガバナー（Win32 Native & SharedIoGovernor / ADR 8, 36, 45, 80, 90, 91, 98）**:
   - ローカルはBCLの64KiB一括列挙を使う（ADR 130）。UNCは `FindFirstFileExW` (`FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`) と `SafeFindHandle` を使い、既存の4段フォールバックを保つ。サイズ・日時取得のために各ファイルを再度開かない。
   - フォルダー単位RPCの完全根絶（ゼロI/O化）: 列挙タイムスタンプを子ノード生成時に直結。
   - `SharedIoGovernor`: UNCは列挙2並列、本文AIMD 2〜12並列。ローカルの検索列挙は4並列、本文は8並列。容量のローカル走査はCPU数に応じた最大8並列の専用ワーカーを使う（ADR 128・130）。
   - 純粋I/O時間計測（CPU展開・パース時間を除外した真のネットワーク遅延）と、再昇格可能な AIMD（不可逆崖落ち永久固定の撤廃）により、サーバーを保護しつつ SMB スループットを最大化。重複 RPC（事前の `File.Exists`、既知サイズの `FileInfo.Length`）を全廃。
6. **検索スタジオのアーキテクチャ（Search Studio / ADR 87, 90, 93, 94, 95, 96, 97, 99, 108〜112）**:
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
   - **SHA-256 の保存**: 監査で確認された重複グループの SHA-256 は `UpdateSha256Async` でサイズ・更新日時が一致する行にだけ書き戻す。容量再スキャンでツリーを入れ替える際はパス・サイズ・更新日時・作成日時が一致する旧 SHA を保持する。通常の `DiskScanService` も列挙時の作成日時をノードへ入れるため、この条件に届く。ただしメタデータ一致は本文不変の証明ではないため、次回監査の重複確定には毎回フル SHA-256 を計算する。削除直前の再照合も維持する（ADR 126・127）。
   - **ポータブル JSON 相互運用**: `ExportToJsonFileAsync` / `ImportFromJsonFileAsync` により社内配布・共有用には単一 JSON を出力。既存 JSON キャッシュからの自動透過マイグレーション完備。
8. **UI共通整線 ＆ 深階層ツリー操作性（ADR 65, 66, 69）**:
   - Quiet Fluentの低彩度表示を維持しつつ、検索の件数・容量・時間・状況は結果見出しへ、容量の集計と前回差分は操作列下の短い要約へ置く。整理の詳細な検出条件は開閉可能にして一覧の初期表示を広げる（ADR 120）。
   - 移行ツリー Undo 機能（戻るボタン ＋ Ctrl+Z）による枠外ドロップ削除の完全可逆化。
   - 深階層でも迷わない 4 大工夫: インテリジェント動的サブ化、ドロップ先端ハイライト、ホバー自動展開（400ms）、端点自動スクロール（25px）。


---

## 4. ビルド・実行・検証コマンド

### ビルド（0 警告・0 エラーを維持すること）
```powershell
& "$HOME\.dotnet\dotnet.exe" build
./tools/Test-LicenseNotices.ps1 -DepsPath ./bin/Debug/net10.0-windows/FolderMorpher.deps.json
```

### 配布用単一EXEの生成（Release self-contained・単一EXE）
```powershell
$env:PATH = "C:\Users\iwakura\.dotnet;" + $env:PATH
& "$HOME\.dotnet\dotnet.exe" publish ./FolderMorpher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o ./dist
./tools/Test-LicenseNotices.ps1 -DepsPath ./bin/Release/net10.0-windows/win-x64/FolderMorpher.deps.json
# Google Drive同期時は社内互換のため2つとも配置すること
Copy-Item ./dist/FolderMorpher.exe "G:\マイドライブ\FolderMorpher\FolderMorpher.exe" -Force
Copy-Item ./dist/FolderMorpher.exe "G:\マイドライブ\FolderMorpher\FolderCleaner.exe" -Force
```

### 自動回帰テストスイート（ヘッドレス自己検証・CIゲート）
バグ修正やリファクタリング後は、必ず以下の回帰テストを実行して 8/8 ALL PASSED（8ドメイン通過）であることを確認すること。8/8 は網羅率を意味しない。検索テストの現行ソースは `FolderMorpher.Core/Services/Testing/RegressionTestSuite.Search.cs`。
ACL試験の後片付けは `AclTestDirectoryCleanup.cs` を正本とする。現ユーザーのTemp直下にある既知の試験名＋GUIDだけを扱い、子孫のDACLを通常権限で戻してから片付ける。再解析ポイントは拒否し、失敗は回帰ゲートへ伝える。検索対象のアクセス権修正や古い残骸の自動削除に転用しない。既存の残骸を回収する場合は対象計画を作り、ゴミ箱へ移す（ADR 133）。
```powershell
& "$HOME\.dotnet\dotnet.exe" run --no-build -- --test-regression
```

### 単一EXEのIPC統合試験
配布物を生成後、`dist/FolderMorpher.exe --test-ipc` を実行する。試験専用Named Pipe・Mutex・一時SQLite・一時設定で別PIDの `--host` を起動し、普段のHost/DB/設定へ触れずStorage/Search/ACL/Audit/移行/設定のDTO、参照フォルダー一階層、共通ダイアログ、検索Clear/Stop、Host正常終了を確認する。`--snapshot` も専用Host・一時DB・一時設定を使い、撮影後に終了と一時ファイルの片付けを行う。

### 自動統合テスト（ヘッドレス実行）
```powershell
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --test-suite "C:\Path\To\TestDir"
```

### ヘッドレス実機レンダリング（オフスクリーン撮影でUIを目視確認）
```powershell
# Tab 0: 容量分析
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --snapshot ".\tab0.png" --tab 0

# Tab 1: ファイル検索
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --snapshot ".\tab1_search.png" --tab 1

# Tab 2: 権限コントロール
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --snapshot ".\tab2_liveacl.png" --tab 2

# Tab 3: 移行スタジオ
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --snapshot ".\tab3_simulation.png" --tab 3

# Tab 4: リンク修復
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --snapshot ".\tab4_linkfix.png" --tab 4

# Tab 5: ファイル監査
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --snapshot ".\tab5_audit.png" --tab 5

# Tab 6: メディア最適化
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --snapshot ".\tab6_media.png" --tab 6

# 参照フォルダー切替: 閉じた階層 / 一階層展開
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --snapshot ".\scope.png" --tab 12
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --snapshot ".\scope_expanded.png" --tab 13

# 一般向け: 参照先の展開・ホバー×と登録解除／情シス向け最小幅
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --snapshot ".\client_scope.png" --tab 16 --client
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --snapshot ".\admin_narrow.png" --tab 18

# 設定 / EXEに埋め込んだライセンス全文（--lang enで英語）
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --snapshot ".\settings.png" --tab 99 --lang ja
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net10.0-windows\FolderMorpher.dll" --snapshot ".\licenses.png" --tab 106 --lang ja
```
