# FolderMorpher — AI Agent & Developer Architecture Guide

> **【AIメンテナ・自律継続規約】**  
> 本プロジェクトは「自律完遂（自ら調査・修正・検証まで行い、完成状態で返す）」を基本方針とする。  
> ⚠️ **後続のAIメンテナへ**: コードベースの構造、ファイル責務、または重要な設計制約を変更した場合は、**必ずこの `AGENTS.md` も最新の状態に同期・更新すること**。過去の設計意図（`.agents/ADR.md`）を無視した安易なコードの巻き戻しを禁止する。

---

## 0. AI実装・整線規則（依存関係と正本の施工基準）

> **【哲学：継ぎ足しは許すが、絡ませない】**  
> AIは放っておくと「要求の直近（主にMainWindow）」へ安易に変数やイベントを継ぎ足し、コードをパッチワーク化させて破綻させる癖がある。  
> だからといって美観のためだけに既存の配線を全部引き抜く（不要な大規模リファクタ）ことも禁止する。  
> **「正本は1つ。機能間の配線は最小限。UI固有状態はMainWindowにあってよい。文字列を信号線にするな。美観のために配線を全部抜くな。ただし絡まった配線は局所的に整線せよ。」**

後続のAIメンテナは、いかなる機能追加・修正においても以下の6大基準を厳守しなければならない（回帰テストの `TestArchitecturalRules` により静的検証される）。

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
  2. **`FolderMorpher.Core`**: `UseWPF=false` のヘッドレス・クラスライブラリ。走査、検索、ACL、監査、移行などの実処理を持つ。モデルの色・バッジ・サイズ表示はUI側へ分離。
  3. **`FolderMorpher.Host`**: `UseWPF=false` のライブラリ。`FolderMorpher.exe --host` で起動し、Coreサービス、SQLite、設定、ジョブを所有する。Named PipeはユーザーSIDとセッションを識別し、`PipeOptions.CurrentUserOnly` を使用。
  4. **`FolderMorpher.UI`**: WPF画面、画面用モデル、DTO変換、IPCクライアントを持ち、プロジェクト参照はContractsのみ。
     - `FolderMorpherHostClient` はHost未起動時に同じEXEを `--host` で起動し、切断時に自動再接続する。
     - 通常のウィンドウ終了ではHostのJobを継続する。環境設定の `CloseHostOnWindowClose`（初期値オフ）がオンの場合のみウィンドウ終了時にHostを正常終了する。
- **ビルド形態**: `Release win-x64` の **自己完結型（Self-Contained）単一実行可能ファイル (`FolderMorpher.exe`)**
  - 配布出力の正本はリポジトリ直下の `dist/FolderMorpher.exe`。`FolderMorpher.csproj` の `PublishDir` とCI・手元の手順を同じ場所へ統一（ADR 115）。
  - ネイティブWPFエンジンDLLおよび SQLite ネイティブDLLはEXE内部にバンドル。
  - `-p:EnableCompressionInSingleFile=true` による Deflate 圧縮を標準採用。

---

## 2. システム鳥瞰マップ（機能とソースコードの対応表）

UI層は `MainWindow.xaml` / `MainWindow.xaml.cs`（機能別に partial class 分割）と `Views/LiveAclStudio.xaml` / `LiveAclStudio.xaml.cs`。業務処理は `HostClient/FolderMorpherHostClient.cs` からDTOでHostへ委譲する。UI固有の表示処理は `FolderMorpher.UI/Models`、設定の永続化はHostが所有する。

| 機能領域 / タブ | XAML (MainWindow / View) | C# コードビハインド | 関連 Service / Model | 責務と概要 |
| :--- | :--- | :--- | :--- | :--- |
| **全体共通 / モード・作業スコープ・フォルダー操作** | `MainWindow.xaml` の `AdminSidebar`、`NavTab*`、`ScopePopup`、`CleanupSectionBar` | `MainWindow.Scope.cs`<br>`MainWindow.Localization.cs`<br>`MainWindow.ScanEta.cs`<br>`FolderMorpher.UI/Dialogs/AppDialog.cs` | `AppSettingsDto`<br>`HostService.BrowseChildFoldersAsync` | 情シス向け左バー収納、参照フォルダー選択・履歴管理、走査所要時間見込み（Scan ETA）、日英バイリンガル切替、安全完全削除の確認ダイアログ。 |
| **Tab 1: 容量分析**<br>(Storage Explorer) | `StorageTabPanel`<br>`HistoryWindow.xaml` | `MainWindow.Storage.cs`<br>`HistoryWindow.xaml.cs` | `DiskScanService.cs`<br>`StorageAvailableSpaceService.cs`<br>`SqliteTreeCacheService.cs`<br>`StorageHistoryService.cs`<br>`ScanTabModel.cs`<br>`FileItemNode.cs` | 高速容量ツリー走査、階層別オンデマンド展開、容量上位Top10＆直下シェア分析、空き容量警告（残り10%以下）、複数タブ、容量推移グラフ・将来予測。 |
| **Tab 2: ファイル検索**<br>(Search Studio) | `SearchTabPanel`<br>(`MainWindow.xaml`) | `MainWindow.Search.cs` | `SearchEngineService.cs`<br>`ServerSearchAccelerator.cs`<br>`WindowsSearchProvider.cs`<br>`TreeCachePruningIndex.cs`<br>`PathCanonicalizer.cs`<br>`SharedIoGovernor.cs`<br>`ContentExtractionService.cs`<br>`SearchQueryParser.cs`<br>`PdfSearchHelper.cs`<br>`OcrWorkerService.cs`<br>`SearchModels.cs` | **検索専用DBなし（現行ツリーメモリ照合・TreeCache逐次照合・未スキャンUNC Live直接走査）**、サーバー側インデックス拝借（WSP/Synology等）、Producer-Consumer Channel パイプライン、ripgrep流 64KBスライディングバッファ走査、Office/PDF 境界保護、UglyToad.PdfPig主エンジン、Filunest互換 PP-OCRv6-small 2段ロケットOCR遅延探索、UNCルート単位 I/O ガバナー（AIMD）、パス正規化（Z:\ ⇄ UNC）、検索履歴ドロップダウン。 |
| **Tab 3: 権限コントロール & 逆引き監査**<br>(Live ACL & Effective Access) | `Views/LiveAclStudio.xaml`<br>(`LiveAclFolderView`, `LiveAclReverseView`, `LiveAclDiffModalOverlay`, `NewFolderModalOverlay`) | `Views/LiveAclStudio.xaml.cs` | `AclService.cs`<br>`EffectiveAccessService.cs`<br>`ActiveDirectoryService.cs`<br>`AclModels.cs`<br>`EffectiveAccessModels.cs` | 実環境NTFS ACL可視化・編集、**Dry-Run差分チェックモーダル（AclChangePlan貫通・継承変更警告・セマンティックVerify・SDDLロールバック）**、AD逆引き権限監査、均一幅ADアカウントカード、ADバックグラウンド自動同期、ツリーインライン新規フォルダー作成。 |
| **Tab 4: 移行スタジオ**<br>(Simulation Studio) | `SimulationTabPanel` | `MainWindow.Simulation.cs` | `SimulationProjectService.cs`<br>`MigrationPackageService.cs`<br>`MigrationPackageModels.cs`<br>`SimModels.cs` | 現行サーバーから新環境への仮想ツリー設計（N:1マッピング）、ACL引き継ぎ設計、実機DACLセマンティックVerify、エンタープライズ移行パッケージ出力（TargetRoot必須検証・Wave分割・Runbook Excel・安全停止手順・多重コピー防止/XD・Dry-Run bat同梱）。 |
| **Tab 5: リンク修復**<br>(LinkFixer) | `LinkFixTabPanel` | `MainWindow.LinkFix.cs` | `LinkFixService.cs`<br>`OfficeLinkFixService.cs` | サーバー移行後の切断ショートカット（.lnk）およびOffice内部リンク（.xlsx/.xlsm）検出・修復、**VBAマクロ非破壊保護＆通常XML混在時の部分修復（PartiallyFixed）**、全社配布用GPOログオンスクリプト（.ps1）生成。 |
| **Tab 6: 整理候補発見 ＆ 健全化**<br>(Smart Hygiene & Candidates) | `AuditTabPanel` | `MainWindow.Audit.cs` | `AuditReportService.cs`<br>`DuplicateHashPipeline.cs`<br>`HygieneCandidateEngine.cs`<br>`AuditIgnoreService.cs`<br>`ExcelReportService.cs`<br>`AuditModels.cs` | **理由付き整理候補発見スタジオ**（①世代・旧版、②展開済ZIP残骸、③墓場フォルダー化石化判定）、$O(N)$ ボトムアップ集約、整理除外リスト（Z:\ と UNC 同一視）、スコア内訳可視化、完全重複（SHA-256）、休眠ファイル（3年超・1年閲覧保護）、パス長危険域（240字超）、原本保護＆削除直前SHA-256再照合付き安全完全削除。 |
| **整理内: メディア最適化**<br>(Media Optimizer) | `MediaTabPanel` | `MainWindow.Media.cs` | `MediaOptimizerService.cs`<br>`ExcelReportService.cs`<br>`MediaOptimizerModels.cs` | 整理画面の内部切替。保護対象（_Master/RAW等）付き写真・画像軽量化（長辺2560px超縮小/85%品質/日時・Exif保持/アトミック置換）、大容量動画Topランキング抽出、夜間GPU圧縮（H.265）バッチ生成。 |
| **詳細権限モーダル** | `SecModalOverlay` | `MainWindow.Simulation.cs` | `AclModels.cs` | Windows標準セキュリティ詳細設定（14項目のNTFS詳細パーミッションビット）の完全再現・編集。 |
| **変化点差分モーダル** | `DiffModalOverlay` | `MainWindow.Simulation.cs` | `SimModels.cs` | 移行前後（Before/After）の変化点（新規・移動・統合・ACL差分）の一覧レビューとExcel出力。 |
| **移行パッケージ生成モーダル** | `MigrationPackageOverlay` | `MainWindow.Simulation.cs` | `MigrationPackageService.cs`<br>`MigrationPackageModels.cs`<br>`ExcelReportService.cs` | ベンダー標準移行工程（事前フル同期、中間差分、本番切替）の一括静的生成、波次（Wave）自動分割・容量バジェット算定、Migration_Runbook.xlsx 出力。 |

---

## 3. 重要な設計判断の記録（Architecture Decisions / ADR）

> ⚠️ **後続のAIメンテナへ**:
> 本プロジェクトの全設計判断記録（ADR 1〜144）は、トークン消費削減および可読性維持のため [`.agents/ADR.md`](.agents/ADR.md) に体系化・保管されている。
> **仕様変更・機能改修を行う際は、必ず `.agents/ADR.md` を参照し、過去の設計意図を無視した安易なコード巻き戻しを行ってはならない。**

### 8大中核アーキテクチャ原則

設計判断（ADR 1〜144）は、以下の **8大中核アーキテクチャ原則** に集約される。

1. **全体占有率メーター & 2連カード（Storage / ADR 61, 124, 130）**:
   - 親フォルダーに対する直下シェア（選択フォルダー内訳）と、ルート総容量に対する全体占有率を二重加算防止のため厳格分離。ルート行は `―`（ハイフン）表示。
   - `DiskScanService` のローカル走査はCPU数に応じた最大8ワーカーで実行し、64KiBの `FileSystemEnumerable<NativeFindEntry>` で名前・属性・サイズ・日時を一括取得（ADR 130）。
   - 接続ユーザーの空き容量が10%以下の場合に警告を表示（ADR 124）。
2. **NTFS ACL 安全機構 & DACL完全一致検証（Live ACL / ADR 2, 9, 31, 40, 52, 62, 132）**:
   - 変更前にSDDLを自動スナップショットし、`ApplyLiveAclWithRollback` で復元可能にする。スナップショット永続化が成功しない場合はコミットを拒否。
   - 実効権限評価（`EvaluateEffectiveAccessOnAcl`）は保存DACL順を保ち、先行Allowで成立したビットを後続Denyで取り消さない。
   - `VerifyFolderDacl` による継承・明示ACE突合および計画外の余計なACEの完全検知・検証。
3. **ファイル監査・整理の安全原則 ＆ 理由付き整理候補発見（Audit & Hygiene / ADR 5, 28, 53, 88, 89, 90, 113, 114, 129, 135, 140）**:
   - 原本候補（`IsOriginalCandidate`）は無条件で削除拒否。重複削除直前に原本と対象を全文SHA-256で再照合し、不一致・読取失敗なら拒否。
   - NTFS物理File ID（`dwVolumeSerialNumber:nFileIndex`）による同一物理実体（ハードリンク）判定。解放容量（`ReclaimableBytes`）を分離し、ハードリンク重複の容量二重加算（虚偽削減表示）を排除（ADR 135, 140）。原本と同一実体の削除を物理レベルで遮断。
   - 休眠ファイル判定は更新日3年超に加え、直近1年間（365日）に閲覧されたファイル（LastAccessTime）を自動保護。
   - 重複判定（`DuplicateHashPipeline.cs`）はサイズ → 先頭/末尾4KiB → 先頭1MiB → 完全SHA-256の多段判定。部分ハッシュを重複確定や削除許可には使わない（ADR 129）。
   - 説明責任ヒューリスティクス（世代旧版・展開済ZIP残骸・墓場フォルダー）を $O(N)$ ボトムアップ集約で高速抽出。
4. **メディア最適化の聖域保護（Media Optimizer / ADR 6, 32, 54, 139）**:
   - プロ用聖域フォルダー（`_Master`, `RAW` 等）およびプロ用拡張子（`.psd`, `.ai`, `.raw` 等）の自動スキップ保護。
   - JPEG/PNG の日時・Exif・回転情報を 100% 保持し、一時ファイル経由のアトミック置換（`SafeFileReplace` 原本退避保護 / ADR 139）。
5. **UNC/ネットワーク走査 ＆ 適応並列度ガバナー ＆ 直接パス正本一元化（Win32 Native & SharedIoGovernor / ADR 8, 36, 45, 80, 90, 91, 98, 128, 136, 141）**:
   - UNCは `FindFirstFileExW` (`FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`) と `SafeFindHandle` を使用。サイズ・日時取得のために各ファイルを再度開かない（ゼロI/O化）。
   - `SharedIoGovernor`: UNCは列挙2並列、本文AIMD 2〜12並列。ローカルの検索列挙は4並列、本文は8並列。純粋I/O時間計測に基づく再昇格可能なAIMD制御。
   - **直接パス（UNC）正本一元化**: ネットワークドライブレター（`Z:\` 等）の入力・指定時も、Win32 `WNetGetConnection` およびレジストリフォールバックにより実体の直接パス（UNC: `\\server\share\...`）へ即時正規化（TTL30秒キャッシュ & 破壊操作前クリア / ADR 136, 141）。
6. **検索スタジオのアーキテクチャ（Search Studio / ADR 86, 87, 90, 93〜97, 99, 108〜112, 127, 128, 132, 134, 135, 137, 138, 142, 143, 144）**:
   - **非管理者権限が通常経路の前提**: 一般ユーザー権限による検索を維持。
   - **検索専用ローカルDBの完全撤去**: 現行ツリーはメモリ照合、永続TreeCacheは行単位逐次照合、未スキャンUNCはストリーミングLive直接走査。
   - **サーバー側インデックス拝借（ServerSearchAccelerator）**: Windows Server WSP / Synology 等の既存インデックスから候補を秒速取得しつつ、網羅走査を併用して False Negative を防止。
   - **多段PDF検索 ＆ 2段ロケットOCR**: `UglyToad.PdfPig` (Pure C#) を主エンジン化し、ToUnicode CMap解決・ページ早期脱落で高速化（ADR 143）。「🖼️ OCRを含む」チェック時は、Filunest互換 PP-OCRv6-small による2段ロケット遅延走査（画像・スキャンPDF）を実行し、通常ヒット済みの重複排除・3行窓スニペット生成・`IsOcrEstimated` 推定バッジ表示（ADR 144）。
   - **ストリーミング走査の最適化**: 64KBスライディングバッファ直接走査、Office書式境界分割保護、単一ASCII語およびFilunest由来の安定日本語Raw UTF-8 SIMD直接照合（short-read耐性・CP932後半フォールバック / ADR 134, 138）、Filunest流 3行ウィンドウ（3-Line Window Excerpt）スニペット生成（ADR 135）、PDFテキスト描画命令（Tj/TJ）抽出による誤ヒット根絶（ADR 137）、パス正規化（`PathCanonicalizer`: Z:\ ⇄ UNC 自動解決）。
7. **SQLite ローカル専有ツリーキャッシュ ＆ ポータブル JSON（ADR 98, 103〜105, 125, 127）**:
   - **完全ローカル専有**: `%LocalAppData%\FolderMorpher\TreeCache\tree_cache.db`（WALモード）にのみ配置。共有フォルダー（UNC）には一切 DB を置かない。
   - **親ID・名前による省容量化**: `RootId, ParentId, Name` を正本とし、階層単位のオンデマンド取得によりGUIへの全件復元・全件IPC送信を回避。
   - **未スキャン初回Live走査からのTreeCache自動形成**: 完走した完全カバレッジのみ原子的に公開（ADR 125・127）。
   - **ポータブル JSON 相互運用**: `ExportToJsonFileAsync` / `ImportFromJsonFileAsync` により社内配布・共有用には単一 JSON を出力。
8. **UI共通整線 ＆ 深階層ツリー操作性（ADR 65, 66, 69, 120, 122, 123）**:
   - Quiet Fluentの低彩度表示。移行ツリーの可逆Undo機能（戻るボタン ＋ Ctrl+Z）。
   - 深階層ツリー操作性: 動的サブ化、ドロップ先端ハイライト、ホバー自動展開（400ms）、端点自動スクロール（25px）。
   - 走査見込み時間（Scan ETA）の安全側見積もり表示（`MainWindow.ScanEta.cs`）。

---

## 4. ビルド・実行・検証コマンド

### ビルド（0 警告・0 エラーを維持すること）
```powershell
& "$HOME\.dotnet\dotnet.exe" build FolderMorpher.sln
./tools/Test-LicenseNotices.ps1 -DepsPath ./bin/Debug/net10.0-windows/FolderMorpher.deps.json
```

### 配布用単一EXEの生成（Release self-contained・単一EXE）
```powershell
$env:PATH = "$HOME\.dotnet;" + $env:PATH
& "$HOME\.dotnet\dotnet.exe" publish ./FolderMorpher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o ./dist
./tools/Test-LicenseNotices.ps1 -DepsPath ./bin/Release/net10.0-windows/win-x64/FolderMorpher.deps.json

# Google Drive同期時は社内互換のため2つとも配置すること
Copy-Item ./dist/FolderMorpher.exe "G:\マイドライブ\FolderMorpher\FolderMorpher.exe" -Force
Copy-Item ./dist/FolderMorpher.exe "G:\マイドライブ\FolderMorpher\FolderCleaner.exe" -Force
```

### 自動回帰テストスイート（ヘッドレス自己検証・CIゲート）
バグ修正やリファクタリング後は、必ず以下の回帰テストを実行して **ALL REGRESSION TESTS PASSED (8/8)** であることを確認すること（静的アーキテクチャ検査 `TestArchitecturalRules` を含む）。
```powershell
& "$HOME\.dotnet\dotnet.exe" run --project FolderMorpher.csproj --no-build -- --test-regression
```

### 単一EXEのIPC統合試験
配布物を生成後、`dist/FolderMorpher.exe --test-ipc` を実行する。試験専用Named Pipe・Mutex・一時SQLite・一時設定で別PIDの `--host` を起動し、普段のHost/DB/設定へ触れず全ドメインのDTO、参照フォルダー、検索Clear/Stop、Host正常終了を確認する。
