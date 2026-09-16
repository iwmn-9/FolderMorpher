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
| **Tab 2: ファイル検索**<br>(Search Studio) | `SearchTabPanel`<br>(`MainWindow.xaml`) | `MainWindow.Search.cs` | `SearchEngineService.cs`<br>`ContentIndexService.cs`<br>`SearchQueryParser.cs`<br>`PdfSearchHelper.cs`<br>`SearchModels.cs` | インメモリ瞬時検索（スキャン済みツリー）・プログレッシブ直接走査（未スキャンUNC）、**SQLite FTS5 trigram による事前インデックス型ミリ秒全文検索（差分更新・クラッシュセーフレジューム・Small-File First）**、**Windows IFilter & 純C#フォールバックによるPDF全文検索**、**Office (Excel sharedStrings辞書優先チェック) / テキスト (先頭4KB Early Drop / マッチ即離脱 Early Exit) 高速化パイプライン**、**「📄 本文も検索」連動トグル**、Everything互換クエリ構文、右クリックから全スタジオ（Live ACL/Simulation/LinkFix/Audit）へ連携、Excel/CSV台帳エクスポート |
| **Tab 3: 権限コントロール & 逆引き監査**<br>(Live ACL & Effective Access) | `Views/LiveAclStudio.xaml`<br>(`LiveAclFolderView`, `LiveAclReverseView`, `LiveAclDiffModalOverlay`, `NewFolderModalOverlay`) | `Views/LiveAclStudio.xaml.cs` | `AclService.cs`<br>`EffectiveAccessService.cs`<br>`ActiveDirectoryService.cs`<br>`AclModels.cs`<br>`EffectiveAccessModels.cs` | 実環境NTFS ACL可視化・編集、**Dry-Run差分チェックモーダル（AclChangePlan貫通・継承変更警告・セマンティックVerify・SDDLロールバック）**、AD逆引き権限監査、均一幅ADアカウントカード、ADパレットUI統一、ADバックグラウンド自動同期、ツリーインライン新規フォルダー作成 |
| **Tab 4: 移行スタジオ**<br>(Simulation Studio) | `SimulationTabPanel` (L608-995) | `MainWindow.Simulation.cs` | `SimulationProjectService.cs`<br>`MigrationPackageService.cs`<br>`MigrationPackageModels.cs`<br>`SimModels.cs` | 現行ファイルサーバーから新環境への仮想ツリー設計（N:1マッピング）、ACL引き継ぎ設計、ADパレット統一、全画面・全出力完全日英両対応、ヘッダーレイアウト整線、ガワ先行作成の実機DACLセマンティックVerify、エンタープライズ移行パッケージ出力（Wave分割・Runbook Excel・安全停止手順・多重コピー防止/XD） |
| **Tab 5: リンク修復**<br>(LinkFixer) | `LinkFixTabPanel` (L998-1094) | `MainWindow.LinkFix.cs` | `LinkFixService.cs`<br>`OfficeLinkFixService.cs` | サーバー移行後の切断ショートカット（.lnk）およびOffice内部リンク（.xlsx/.xlsm）検出・修復、**VBAマクロ非破壊保護＆通常XML混在時の部分修復（PartiallyFixed）**、全社配布用GPOログオンスクリプト（.ps1）生成 |
| **Tab 6: ファイル監査**<br>(Audit & Hygiene) | `AuditTabPanel` (L1097-1240) | `MainWindow.Audit.cs` | `AuditReportService.cs`<br>`ExcelReportService.cs`<br>`AuditModels.cs` | 重複ファイル（SHA256）、休眠ファイル（3年超・1年閲覧保護）、パス長危険域（240字超）・禁則文字検出、フォルダー名部分一致除外（カンマ区切り）。ハイパーリンク付きExcel/CSVレポート出力、**原本保護＆削除直前SHA-256再照合付き完全削除** |
| **Tab 7: メディア最適化**<br>(Media Optimizer) | `MediaTabPanel` (L1243-1380) | `MainWindow.Media.cs` | `MediaOptimizerService.cs`<br>`ExcelReportService.cs`<br>`MediaOptimizerModels.cs` | 保護対象（_Master/RAW等）付き写真・画像軽量化（長辺2560px超縮小/85%品質/日時・Exif保持/直接上書き）、大容量動画Topランキング抽出、夜間GPU圧縮（H.265）バッチ生成 |
| **詳細権限モーダル** | `SecModalOverlay` | `MainWindow.Simulation.cs` | `AclModels.cs` | Windows標準セキュリティ詳細設定（14項目のNTFS詳細パーミッションビット）の完全再現・編集 |
| **変化点差分モーダル** | `DiffModalOverlay` | `MainWindow.Simulation.cs` | `SimModels.cs` | 移行前後（Before/After）の変化点（新規・移動・統合・ACL差分）の一覧レビューとExcel出力 |
| **移行パッケージ生成モーダル** | `MigrationPackageOverlay` | `MainWindow.Simulation.cs` | `MigrationPackageService.cs`<br>`MigrationPackageModels.cs`<br>`ExcelReportService.cs` | ベンダー標準移行工程（事前フル同期、中間差分、本番切替）の一括静的生成、波次（Wave）自動分割・容量バジェット算定、動的転送レート・差分率による所要時間算出、容量二重加算防止、実測ファイル数引き継ぎ、安全停止手順書ガイド、週末枠オーバー警告、Migration_Runbook.xlsx（WBS/進捗台帳・マッピング・除外一覧） |

---

## 3. 重要な設計判断の記録（Architecture Decisions / ADR）

> ⚠️ **後続のAIメンテナへ**:
> 本プロジェクトの全 60 項目に及ぶ詳細な設計判断記録（ADR 1〜60）は、トークン消費削減および可読性維持のため [`.agents/ADR.md`](.agents/ADR.md) に体系化・外部保管されている。
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