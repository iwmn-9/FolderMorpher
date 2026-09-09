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
- **依存パッケージ**: `System.DirectoryServices` (8.0.0, AD通信用)
- **ビルド形態**: `Release win-x64` の **自己完結型（Self-Contained）単一実行可能ファイル (`FolderMorpher.exe`)**
  - ネイティブWPFエンジンDLL（D3DCompiler, wpfgfx等）はすべてEXE内部にバンドルされる。ルートに個別DLLを展開・配置してはならない。

---

## 2. システム鳥瞰マップ（機能とソースコードの対応表）

UI層は `MainWindow.xaml` / `MainWindow.xaml.cs` および独立コンポーネント `Views/LiveAclStudio.xaml` / `LiveAclStudio.xaml.cs` で構成され、内部ロジックは `Services` と `Models` に完全に分離されている。

| 機能領域 / タブ | XAML (MainWindow / View) | C# コードビハインド | 関連 Service / Model | 責務と概要 |
| :--- | :--- | :--- | :--- | :--- |
| **全体共通 / 左サイドバー** | `SidebarBorder`, `SidebarToggleButton` (L22-75) | `SidebarToggleButton_Click`<br>`NavTab_Checked` | `Converters/ValueConverters.cs` | 収縮対応ナビゲーション（幅220px ⇄ 58px）、グローバルステータスバー、通知トースト |
| **Tab 1: 容量分析 & 監視**<br>(Storage Explorer) | `StorageTabPanel` (L82-410) | `ScanButton_Click`<br>`StorageTreeView_SelectedItemChanged`<br>`SubfolderShareGrid_MouseDoubleClick` | `DiskScanService.cs`<br>`DriveInfoService.cs`<br>`StorageHistoryService.cs`<br>`ScanTabModel.cs`<br>`FileItemNode.cs` | 複数タブスキャン、ドライブ空き容量メーター、全体占有率メーター（案A）、容量上位Top10（Explorer起動連動）、直下シェア内訳（Wクリックツリー連動） |
| **Tab 2: 権限コントロール & 逆引き監査**<br>(Live ACL & Effective Access) | `Views/LiveAclStudio.xaml`<br>(`LiveAclFolderView`, `LiveAclReverseView`, `LiveAclDiffModalOverlay`) | `Views/LiveAclStudio.xaml.cs`<br>(`LiveAclPanelApplyDeltaButton_Click`<br>`LiveAclDiffModalExecute_Click`<br>`RevStartScan_Click`) | `AclService.cs`<br>`EffectiveAccessService.cs`<br>`ActiveDirectoryService.cs`<br>`AclModels.cs`<br>`EffectiveAccessModels.cs` | 実環境NTFS ACL可視化・編集、**Dry-Run差分チェックモーダル（AclChangePlan貫通・継承変更警告・セマンティックVerify・SDDLロールバック）**、AD逆引き権限監査（ネスト全展開・ハイパーリンク付きExcel出力） |
| **Tab 3: 移行スタジオ**<br>(Simulation Studio) | `SimulationTabPanel` (L608-995) | `SimSourceLoadButton_Click`<br>`SimMockTreeView_Drop`<br>`SimDiffReviewButton_Click`<br>`SimDeploySkeletonButton_Click` | `SimulationProjectService.cs`<br>`MigrationService.cs`<br>`SimModels.cs` | 現行ファイルサーバーから新環境への仮想ツリー設計（N:1マッピング）、ACL引き継ぎ設計、Diffインスペクター、ガワ先行作成（空フォルダ+ACL一括展開）、Robocopy生成 |
| **Tab 4: リンク一括修復**<br>(LinkFixer) | `LinkFixTabPanel` (L998-1094) | `LinkScanButton_Click`<br>`LinkFixExecuteButton_Click`<br>`LinkGenerateGpoButton_Click` | `LinkFixService.cs`<br>`OfficeLinkFixService.cs` | サーバー移行後の切断ショートカット（.lnk）およびOffice内部リンク（.xlsx/.xlsm）一括検出・修復、全社配布用GPOログオンスクリプト（.ps1）生成 |
| **Tab 5: 断捨離・健全化**<br>(Audit & Hygiene) | `AuditTabPanel` (L1097-1240) | `AuditStartButton_Click`<br>`AuditExportExcelButton_Click`<br>`AuditExportCsvButton_Click`<br>`AuditGenArchiveScriptButton_Click` | `AuditReportService.cs`<br>`ExcelReportService.cs`<br>`AuditModels.cs` | GDMS完全代替。重複ファイル（SHA256）、休眠ファイル（3年超）、パス長260文字超・禁則文字検出。ハイパーリンク付きExcelレポート出力、安全退避バッチ生成 |
| **Tab 6: メディア最適化**<br>(Media Optimizer) | `MediaTabPanel` (L1243-1380) | `MediaScanButton_Click`<br>`MediaOptimizeButton_Click`<br>`MediaGenVideoBatchButton_Click`<br>`MediaExportExcelButton_Click` | `MediaOptimizerService.cs`<br>`ExcelReportService.cs`<br>`MediaOptimizerModels.cs` | 聖域保護（_Master/RAW等）付き写真・画像軽量化（長辺2560px超縮小/85%品質/日時・Exif完全保持/直接上書きで最大90%削減）、巨大動画Topランキング抽出、夜間GPU圧縮（H.265）バッチ生成 |
| **詳細権限モーダル** | `SecModalOverlay` | `SecModalApply_Click`<br>`SecModalCancel_Click` | `AclModels.cs` | Windows標準セキュリティ詳細設定（14項目のNTFS詳細パーミッションビット）の完全再現・編集 |
| **変化点差分モーダル** | `DiffModalOverlay` | `DiffModalClose_Click`<br>`DiffExportExcel_Click` | `SimModels.cs` | 移行前後（Before/After）の変化点（新規・移動・統合・ACL差分）の一覧レビューとExcel出力 |

---

## 3. 重要な設計判断の記録（Architecture Decisions / ADR）

後続のAIは、以下の仕様を「不具合」と誤認して勝手に書き換えてはならない。

1. **全体占有率メーターの計算ルール（親子の二重加算防止）**:
   - 全体占有率（%）は「**スキャン対象ルートの総容量に対する割合**」である。
   - サブフォルダを潜っていった場合、直下アイテムの合計は常に「親フォルダの容量 = 100%」となるため、右ペインに新設した「**選択フォルダの内訳（直下シェア）**」と明確に分離されている。
   - ルート行（ドライブ直下等）の全体占有率は常に100%になり無意味なため、`―`（ハイフン）表示に倒すのが確定仕様である。
2. **Live ACL の安全機構（SDDLロールバック）**:
   - `AclService.ApplyLiveAclWithRollback()` は、権限を適用する直前に現行の完全なSDDL文字列をメモリに自動バックアップする。
   - 万が一管理者が誤った権限を適用してアクセス不能になりかけた場合、「↩️ バックアップ復元」ボタンで直前の状態に原子的復元できるように設計されている。
3. **AD未接続環境でのフォールバック**:
   - ワークグループPCやドメイン未参加環境では、ダミーのADアカウントを勝手に捏造表示せず、「⚠️ ローカルPC環境」の案内バナーに切り替える。
4. **WPF UI のデザイン言語（Modern Fluent Canvas）**:
   - ウィンドウ背景: `#F8FAFC`（Canvas）
   - カード背景: `#FFFFFF`、枠線: `#E2E8F0`、角丸: `CornerRadius="12"`、シャドウ: `BlurRadius="6" Opacity="0.04"`
   - 入力欄: 高さ `34px` / `32px`、角丸: `8px`
   - ボタン: 高さ `32px`、角丸: `6px`
5. **断捨離・監査の安全原則（勝手に消さない）**:
   - ツールによる自動削除は行わず、可視化・棚卸し台帳（Excel/CSV）および「安全退避（Archiveフォルダ移動）バッチ」の生成に留める。
6. **写真軽量化の聖域保護 & 完全性維持**:
   - `_Master`, `_Original`, `印刷用`, `RAW` 等のキーワードを含むフォルダやプロ用拡張子（`.psd`, `.ai`, `.raw` 等）は自動スキップ。
   - 一般写真（JPEG/PNG）は長辺2560px・85%品質で上書きするが、**撮影日時・更新日時・作成日時・Exifメタデータ・回転情報は100%引き継ぐ**。
7. **Excelレポートのハイパーリンク機能**:
   - ClosedXMLベースで出力される課題一覧・メディア一覧の「完全パス」列には、クリックするとエクスプローラーで直接親フォルダが開く `file:///` ハイパーリンクが埋め込まれている。
8. **ハイブリッドMFTスキャンの安全原則（3段自動フォールバック）**:
   - `MftScanService` はボリュームハンドルを `GENERIC_READ`（読み取り専用）かつ `FILE_SHARE_READ | FILE_SHARE_WRITE`（排他ロックなし）で開き、NTFSの内部MFTレコードを直接パースして超高速スキャンを行う。
   - UNCパス（`\\server\share`）、非管理者権限環境、またはMFT解析中の例外発生時は、一切エラー停止せず **自動で従来の安全な通常並行スキャン（`DiskScanService`）へフォールバック** させるのが確定仕様である。
9. **NTFS ACE適用範囲（InheritanceFlags / PropagationFlags）の完全保持**:
   - `SimAclEntry` はアクセス権の適用先（このフォルダーのみ、サブフォルダーおよびファイルのみ等）を決定する `InheritanceFlags` と `PropagationFlags` を完全保持する。
   - 適用時（`ApplySimAclEntries`）に一律 `ContainerInherit | ObjectInherit` かつ `None` に上書きしてはならず、ACE本来の適用範囲を忠実に維持・適用しなければならない。またUI（`SecAppliesToCombo`）との双方向マッピングは `AclInheritanceHelper` / `AclUiBindingHelper` で厳密に連動させる。
10. **AD多重入れ子逆引き権限（Effective Access）の評価規則**:
   - ユーザーの実効権限は、本人直接付与だけでなく、所属する全階層のADセキュリティグループ（`LDAP_MATCHING_RULE_IN_CHAIN` / `tokenGroups` による多重ネスト解決）を網羅して算出する。
   - **Windows Canonical DACL Ordering（評価順序）の厳密順守**:
     - DACL は「明示Deny ➔ 明示Allow ➔ 継承Deny ➔ 継承Allow」の順で順次評価する。
     - 単純な `allowed & ~denied` ではなく、**子の明示的Allowは親の継承Denyより優先される** Windows AccessCheck 正規アルゴリズムを実装。
   - **他人アカウント指定時の実行者グループ誤爆防止**:
     - 調査対象が実行中のログオンユーザー自身と一致する場合のみローカルIDをフォールバック使用する。
     - 別の他人アカウントやグループでAD解決に失敗した場合、実行者のグループを勝手に流用せず「直接付与ACEのみ判定」として明示する。
   - **NTFS実効権限スコープとUNC共有注意**:
     - スコープを「NTFS実効権限」と厳密定義し、UNC共有フォルダ経由アクセス時はファイルサーバーの「SMB共有権限（Share Permissions）」の上限も併せて適用される旨を明記・注釈する。
   - 継承無効化（`ApplySimAclEntries` の `inherit: false`）時は `SetAccessRuleProtection(true, false)` を用い、親由来の不要なWell-Knownルール（`Users`等）を意図せず複製保持させない。
11. **移行スクリプト（icacls / Robocopy）と実効権限の整合性**:
   - Robocopy 生成時は、親フォルダの移行元に含まれる子孫ノードのソースパスを `/XD` に自動連動し、新旧ツリーの多重コピーを防止する。
   - Effective Access 評価時は `InheritOnly` ACE を現在のフォルダ自身の権限から除外し、直下の誤認を防ぐ。
12. **移行ACL全経路一本化 (.NET Engine)・Robocopyモード分離・Live ACL Rollback DACL限定**:
   - スケルトン直接展開（C#）と PowerShell 生成スクリプトは、共に .NET の `FileSystemAccessRule` と `SetAccessRuleProtection(true, false)` を用いて 100% 同一の権限ビット・フラグを適用する。
   - ルール0件かつ継承OFF（`InheritAcl = false`）のフォルダでも確実に親の継承が遮断される。
   - Robocopy は「新設計ACL維持モード（`/COPY:DAT`）」と「旧環境ACL完全維持モード（`/COPYALL`）」を明示分離し、新設計ACLの上書き破壊を防止。
   - Live ACL の Snapshot および Rollback は `AccessControlSections.Access`（DACL）に限定し、SACL（監査権限）による `PrivilegeNotHeldException` を完全根絶。
13. **WPF テキストレンダリングのクッキリ化 & タイポグラフィ規約（Displayモード & ClearType）**:
   - WPF デフォルトの `Ideal` モード（サブピクセル浮動小数配置）による文字の輪郭にじみを防止するため、`MainWindow.xaml` に `TextOptions.TextFormattingMode="Display"`、`TextRenderingMode="ClearType"`、`UseLayoutRounding="True"`、`SnapsToDevicePixels="True"` を常時適用する。
   - フォントファミリーは `Segoe UI Variable Text` を優先し、日本語フォールバックに `Yu Gothic UI` を配置。
   - ボタン・入力欄の標準文字サイズは `12.5px〜13px`、補足バッジや小ラベルは `11px〜11.5px` を下限とし、9px等の極小指定で掠れ・視認性低下を起こさせない。
14. **枠外ドロップ解除 & 広域受容 D&D アーキテクチャ**:
   - 従来の狭小な固定ゴミ箱バー（`LiveAclTrashZone`, `SimTrashZone`）を完全撤去。
   - 登録済みカード（NTFS権限カード）やバッジ（移行元マッピング）を「自枠外（他パネル・余白・ウィンドウ外）」へドラッグ＆ドロップすることで直感的にポイ捨て解除される仕様に統一。
   - 自枠内コンテナ（カード一覧エリアや各受容ゾーン）へのドロップ時は解除フラグ（`_droppedInSelfContainer`）により誤解除を完全防止。
   - ドロップ受付面は、従来の狭い境界線内に限定せず、アコーディオン全体（`SimAccordion_Drop`）やカード一覧エリア全体（`LiveAclCardsContainer_Drop`）を広域受容面として開放。
15. **ADプリンシパル検索結果のグループ優先ソート**:
   - 権限割り当てのベストプラクティス（AGDLP / 原則グループ付与）に基づき、AD検索結果一覧は個別ユーザーよりも先にセキュリティグループ（`AdPrincipalType.Group`）を先頭に昇格表示する（`OrderBy(p => p.PrincipalType == Group ? 0 : 1).ThenBy(p => p.DisplayName)`）。
16. **包括的バイリンガル（日英）多言語対応 (i18n)**:
   - サイドバーメニューだけでなく、全タブ（Tab 0: 容量分析、Tab 1: 権限 & 逆引き、Tab 2: 移行スタジオ、Tab 3: リンク修復、Tab 4: 健全化、Tab 5: メディア最適化）の全操作ボタン、チェックボックス、詳細モーダル（SecModal, DiffModal）、ステータスバーを完全網羅して日英動的切り替え（`ApplyLocalization`）を実装。
17. **モダンボーダレスUI規約（Excel格子線・縦線の完全撤廃、余白主導・ClearType）**:
   - 全画面の DataGrid / リストから Excel のようなダサい格子罫線（`GridLinesVisibility="Horizontal|Vertical"`）や二重の枠線（`BorderThickness="1"`）を完全禁止・撤廃。
   - `GridLinesVisibility="None"`, `BorderThickness="0"`, `Background="Transparent"` を基調とし、十分な行間（`RowHeight=28〜36px`）と淡いホバー背景（`#F8FAFC`）による余白主導のモダン・エレガントなレイアウトに統一。
18. **ファイルサーバー0秒ツリーキャッシュ ＆ バックグラウンド自動差分スキャン（案3）**:
   - スキャン開始時、キャッシュが存在すれば「0秒」で前回のツリー構造を即時全展開。
   - UIを一切ブロックせず、バックグラウンドで最新ネットワーク走査を実行。
   - スキャン完了時に新旧ツリーの差分を自動検出し、サイズが変化したフォルダやファイルに増減バッジ（`▲ +2.4GB` / `▼ -500MB`）を自動付与して最新化。
   - スキャン完了後は最新ツリーを自動でキャッシュ上書き保存。
19. **共有キャッシュ・スナップショット 参照先/保存先分離アーキテクチャ (AppSettingsService)**:
   - 社内ファイルサーバー上などの「公式共有マスターキャッシュ（UNCパス）」をチーム全体で参照しつつ、個人のスキャン結果でマスターを意図せず上書きしないよう「参照先（Read）」と「保存先（Write）」を分離構成可能。
   - 指定フォルダー配下に `TreeCaches\` (ツリー構造), `Snapshots\` (容量推移), `Reports\` (監査台帳) が体系的に集約され、推移グラフもチーム共通で共有可能。
   - 共有フォルダーアクセス不能時は自動でローカル（AppData）へフォールバックし、書き込みはアトミック置換（一時ファイル -> 置換）により同時アクセス破損を完全防止。
20. **ファイルサーバー専用UI最適化（案2採用・初期空タブ・3枚KPIカード・ClosedXML完全対応）**:
   - ローカルPCのCドライブ空き容量メーターおよび `DriveComboBox` を完全撤廃し、パス検索バーを広々と配置。初期起動時は「新規スキャン」（空パス）で待機。
   - KPIカードを「スキャン対象容量」「最大ファイル Top 1」「前回差分推移」の3枚構成に整理し、ファイルサーバー監視用途に完全特化。
   - Storage Explorer、Simulation、Diff の各画面で ClosedXML による本物の `.xlsx` 出力（ハイパーリンク・自動列幅・ヘッダースタイル付き）と CSV 出力の双方を完全サポート。
   - `LiveAclRollback` は Windows カーネルによる `AI` フラグ付与に関わらず、セマンティクス（ACEルール等価性）で厳密に整合性を検証。
21. **フォルダ別権限エディタの縦一列カードアーキテクチャ（Modern List Cards）**:
   - 横並びを廃止し、1行1カードの洗練された縦リスト形式に変更。D&D仕様（右ADからの付与、枠外ドロップによる直感的解除、Escキャンセル保護、自枠内ドロップ誤爆防止）は100%完全維持。
22. **移行スタジオの再帰的階層ドロップ＆NTFS ACL自動引き継ぎ＆親クリック不要展開**:
   - 現行ファイルサーバーやエクスプローラーから階層構造を持つフォルダをドロップした際、配下のサブフォルダ階層を再帰的に生成し、既存のNTFS ACLおよび移行元マッピングを自動で忠実に引き継ぐ。
   - `ItemContainerStyle` による `IsExpanded`/`IsSelected` 双方向バインディングにより、フォルダ追加時は親ノードを自動展開し、追加ノードを即座に選択状態にする。
23. **深層（全階層）安全走査 ＆ 完全多言語（i18n）アーキテクチャ**:
   - `MediaOptimizerService` および `AuditReportService` において、`SearchOption.AllDirectories` の `UnauthorizedAccessException` による探索即死バグを根絶し、`Stack<DirectoryInfo>` による堅牢な反復走査を採用。アクセス拒否フォルダーを安全にスキップしながら全階層を漏れなく探索。
   - ボタンだけでなく、全タブ（Tab 0〜5）の静的ラベル、見出し、KPIタイトル、リストカラムヘッダー、プレースホルダー、モーダルテキストに至るまで日英完全動的ローカライズ（`ApplyLocalization`）を網羅。
24. **キャッシュ側 Top10/拡張子内訳完全包含 ＆ インプレース Top10 抽出 ＆ Shared/Localスナップショットマージ**:
   - `TreeCacheRoot` に `TopFiles`（巨大ファイル Top 10）および `ExtensionStats`（拡張子統計）を完全包含して保存。
   - キャッシュ復元時は全ノード再帰走査を一切行わず、キャッシュ内の Top 10 および拡張子内訳を即座にUIへ反映（真の 0 秒・CPU負荷ゼロ復元）。
   - 最新スキャン時・ノード選択時の `GetInsightsForNode` は、100万ファイルあっても `LargestFileInfo` を大量アロケーションせず、サイズ10件のインプレース維持バッファと最小値閾値判定によりメモリ割り当てを 99.99% 削減。
   - `StorageHistoryService.LoadAllAsync` は、チーム共有（Shared）とローカル（Local）の両ディレクトリに存在するスナップショット（`history.json` / `snapshot_*.json`）を網羅的に集約し、`TargetPath` + `Timestamp` で一意に重複排除してマージ表示。共有マスターを参照しつつローカル最新履歴も確実にグラフへ合流。
25. **単一正準 ACL 適用契約（Canonical ACL Apply Contract）＆ 深層完全インポート ＆ SafeFileEnumerator**:
   - **ACL適用の意味論を複数箇所で独自実装してはならない**:
     - `DeploySkeletonAsync`（C#直接展開）、`GeneratePowerShellAclScript`（PowerShell生成）、`AclService.ApplySimAclEntries` の全経路で同一の正準契約を順守。
     - `InheritAcl == true`（継承有効）時は、親由来の継承ACE（`IsInherited == true`）を除外し、**そのフォルダ固有の明示ACE（`!IsInherited`）のみ**を適用・スクリプト出力。親の継承ルールを無駄に明示化して二重固定化・肥大化させない。
     - `InheritAcl == false`（継承無効）時は、親の継承を遮断（`SetAccessRuleProtection(true, false)`）した上で、全ルールを明示ACEとして確実に適用。
   - **シミュレーション深層走査 ＆ Level上限撤廃**:
     - `CreateSimNodeFromSourceWithAcl` の階層上限（4階層制限）および `SimFolderNode.Level` の 5 クランプを完全撤廃。任意の深さまで独自ACLを欠落なく取り込み、D&D移動時は配下子孫ノードの `Level` を再帰的に再計算。
   - **スキャン直後0走査 ＆ 非同期ノード集計 ＆ 耐障害SafeFileEnumerator**:
     - 通常スキャン完了直後は `summary.LargestFiles` / `summary.ExtensionStats` を即座に流用し、スキャン後の全ツリー再走査を完全ゼロ化。
     - サブフォルダ初選択時の Top10 算出は `Task.Run`（非同期）で実行し、UIスレッドのプチフリーズを完全撲滅。
     - `SafeFileEnumerator` を共通導入し、LinkFix / OfficeLinkFix を含めた全探索処理でアクセス拒否（UnauthorizedAccessException）による探索即死を根絶。
26. **Excel保存体験統一 ＆ AD/ローカル OUツリー参照ピッカー ＆ 重複ファイル色分けグルーピング**:
    - **エクスポートExcelの行方不明防止**: 全エクスポート画面で `SaveFileDialog.InitialDirectory` をユーザーのデスクトップ（または設定済みReportsフォルダ）に標準初期化。保存完了後は `Process.Start("explorer.exe", $"/select,\"{path}\"")` でエクスプローラーを自動起動・ファイル選択状態にし、保存先迷子を完全根絶。
    - **Active Directory / ローカル OU 階層参照ピッカー**: 逆引き監査に「👥 参照...」ボタンを新設。LDAP経由でOU/コンテナ階層ツリーを動的構築し、ドメイン未参加環境では自動で「ローカルPC ➔ ローカルグループ/ユーザー」にフォールバック。OU選択で配下のユーザー・グループを一覧表示し、直感的に選択可能。
    - **重複ファイルの色分けグルーピング（UI & Excel）**: 6色のソフトパステルカラー（Soft Blue, Emerald, Amber, Purple, Rose, Cyan）を隣接グループ間で絶対に被らないようサイクリックに割り当て。UI DataGridで行全体をパステルハイライト＆バッジ表示し、ClosedXMLによるExcel出力でも同一のソフト背景色で行を塗り分け。原本候補を先頭にソートし、どのファイル同士が重複ペアか一目で識別可能。
27. **原本候補のインテリジェント選定 ＆ 断捨離フィルター ＆ エクスプローラー直行連動 ＆ 逆引き深度拡張 (v1.4.5)**:
    - **原本候補のインテリジェント選定（Smart Original Scoring）**:
      - 単なる探索順ではなく、ファイル名にコピーキーワード（`コピー`, `copy`, `(1)`, `_backup` 等）が含まれないものを最優先。
      - 次にパス階層の浅さ（区切り文字 `\` が少なくルートに近いもの優先）、パス文字列長、作成日時の古さ、更新日時の古さを総合ソートし、本物のマスター（原本候補）を確実にグループ先頭（`IsOriginalCandidate = true`）に選定。
    - **断捨離・健全化のリアルタイム絞り込みフィルター**:
      - スキャン完了後でも「すべて表示」「重複ファイルのみ」「休眠ファイルのみ」「パス長・禁則のみ」のカテゴリ切り替え ComboBox、およびファイル名/パス/詳細のリアルタイムテキスト検索バーを配置。
      - `ObservableCollection<AuditItem>` によるインプレース高速フィルタリングにより、数十万件の課題でも瞬時に目的の項目を抽出。
    - **DataGrid行ダブルクリックによるエクスプローラー直行連動（断捨離 ＆ メディア）**:
      - `AuditItemsDataGrid` および `MediaItemsDataGrid` の行をダブルクリックすることで、`explorer.exe /select,"<FullPath>"` を発行し、該当ファイルを直接選択した状態でエクスプローラーを開く。
    - **逆引き権限監査の階層深度指定の拡充**:
28. **原本保護の厳格化（プロパティ判定） ＆ 重複退避オプトイン ＆ 全走査一本化 ＆ フィルター高速化 (v1.4.6)**:
    - **原本保護のUI文字列依存を完全撤廃**:
      - `Detail.Contains("[原本候補]")` のような表示テキストへの依存を廃止し、`IsOriginalCandidate` プロパティを判定の唯一の正準根拠とする（互換性のため多層防御で文字列もフォールバック救済）。
    - **重複ファイル退避の安全オプトイン化**:
      - 「重複＝不要データ」ではないため、退避バッチ生成時は「休眠ファイル（3年超）のみ安全退避」を標準とし、重複ファイル（原本以外）の退避は明示的な確認ダイアログによるオプトイン制を採用。意図して複数箇所に配置された設定ファイルの誤退避を根絶。
    - **全走査エンジンの `SafeFileEnumerator` + `ScanCoverage` 完全一本化**:
      - `AuditReportService` 独自の `EnumerateFilesSafe` を撤廃し、共通の `SafeFileEnumerator` に統一。
      - アクセス拒否フォルダ数を `ScanCoverage` で追跡し、サマリーおよび画面に「⚠️アクセス拒否: X箇所」として明示。読めなかったフォルダの存在を隠さない誠実な監査結果を提供。
    - **フィルターのデバウンス（200ms） ＆ 一括仮想化バインド**:
      - テキスト検索入力時は 200ms のデバウンスタイマーを適用し、タイピング中のUIフリーズを完全防止。
      - `_auditVisibleItems.Clear()` + `Add()` のループを撤廃し、`DataGrid.ItemsSource = resultList;` による一括代入に切り替え。数十万件の課題でも瞬時に表示が切り替わる高スケーラビリティを確保。
    - **メディア最適化の正確な仕様表記**:
      - 不可逆リサイズ（長辺2560px超の縮小）を伴う処理に対し、誤解を招く「ロスレス」表現を撤廃。「画像最適化（長辺2560px超は縮小・画質85%・Exif日時完全保持）」と正確に明記。
29. **移行スタジオ（Simulation Studio）の階層オンデマンド走査・D&D枠外削除・スムーススクロール・2段フッター規約**:
    - **現行サーバー移行元の遅延展開（On-Demand Expansion）＆ 初期直下展開**:
      - `OpenFolderDialog` によるエクスプローラー風UNC参照ボタンを新設。
      - 読込直後にルートフォルダを自動展開（`rootItem.IsExpanded = true`）し、直下の全フォルダを即座に一覧表示。
      - 子フォルダはダミーノード方式による遅延読み込み（`Expanded` イベント発火時に安全走査）を採用し、何階層でも安全に配下を深掘り可能。
    - **新サーバーツリー（SimMockTreeView）のD&D移動・昇格・枠外ドロップ削除**:
      - ドラッグ開始にしきい値（`MinimumHorizontalDragDistance` / `MinimumVerticalDragDistance`）を導入し、クリック選択の誤ドラッグを根絶。
      - 別ノードドロップ時はサブフォルダ移動、ツリー下部余白ドロップ時は「ルートフォルダへ昇格移動」、ツリー枠外ドロップ時は「ツリーから安全削除（ポイ捨て削除）」の三段直感D&Dを確立。
    - **ピクセル単位スムーススクロール（ガタガタ解消）**:
      - `SimSourceTreeView` および `SimMockTreeView` に `ScrollViewer.CanContentScroll="False"` を適用し、項目単位の飛び跳ねスクロールをピクセル単位の滑らかなスクロールへ最適化。
    - **サイドバー2段フッター＆マッピング説明文適正化**:
      - 狭小幅での文字・ボタン重なりを防止するため、サイドバーフッターを「上段：ブランド・バージョン / 下段：設定・言語ボタン」の2段構成に刷新。
      - アコーディオンの「移行元マッピング」から不要な冗長サブテキストを撤去し、1行でスッキリ視認できるように統一。
30. **断捨離・健全化のスマート一括選択 ＆ 原本保護安全ガード ＆ 物理完全削除規約 (v1.4.8)**:
    - **チェックボックス列と双方向バインディング**:
      - `AuditItem` に `INotifyPropertyChanged` を実装した `IsChecked` プロパティを追加。
      - DataGridの最左列にチェックボックス列を配置し、ヘッダーの全選択チェックボックス（`AuditHeaderCheckBox`）で現在絞り込み表示中のアイテムのみを一括ON/OFF可能。
    - **スマート一括選択（Smart Select Presets）**:
      - 「重複ファイルの原本以外（コピー）」: 各重複グループの原本候補（`IsOriginalCandidate == true`）を厳格に保護し、コピーファイルのみを選択。
      - 「休眠ファイル（3年以上未更新）」/「休眠ファイル（5年以上未更新）」: 更新日時（`LastWriteTime`）を基準に長期未更新の休眠アイテムを一括選択。
      - 「表示中アイテムを全選択」/「選択をすべて解除」をサポート。
    - **不可逆な物理完全削除と原本保護安全ガード**:
      - 「🗑️ 選択ファイルを完全削除」ボタンにより、チェックされたファイルをバックグラウンドで `File.Delete`。
      - アプリケーション側での復元（スナップショットやごみ箱）は存在しない不可逆な削除である旨を削除前の確認ダイアログで明記。
      - 読み取り専用属性が付与されているファイルでも `fi.IsReadOnly = false` で属性解除して確実に削除。
      - 原本候補（`IsOriginalCandidate == true`）がチェックされていた場合、強制的に警告ダイアログを表示し「原本を除外して続行（推奨）」「すべて削除（危険）」「キャンセル」の3択で原本誤削除を水際で防御。
    - **削除後の動的KPI再計算 ＆ 一覧即座更新**:
      - 削除成功アイテムを正本リスト（`_lastAuditItems`）から即座に除去し、表示フィルターを再適用。
      - 重複容量、休眠容量、総ファイル数の各KPIカードをリアルタイムに再集計・更新。
31. **物理削除の正本一本化（FullPath実行計画） ＆ 休眠横断原本保護 ＆ 属性復元ロールバック (v1.4.9)**:
    - **「監査行」と「物理ファイル」の境界整線 (`AuditCleanupService`)**:
      - 監査結果（`AuditItem`）は1ファイルに複数行（DormantとDuplicate等）存在し得るが、物理削除の正本は **一意の `FullPath`** である。
      - `AuditCleanupService.BuildPlan()` により、チェックされた行を FullPath 単位でグループ化し、1ファイルにつき1回のみの削除・容量集計を行う実行計画（`AuditCleanupPlan`）を生成。
    - **全監査横断の原本保護聖域（休眠選択すり抜け防止）**:
      - 原本候補のパスセット（`originalPaths`）を全監査アイテム（`_lastAuditItems`）から構築。
      - ユーザーが「休眠ファイル（3年/5年）」経由でチェックを入れた場合でも、その FullPath が原本候補であれば IssueType に関係なく確実に原本警告モーダルが発動。
      - 原本除外選択時は、そのファイルに関連付けられたすべての監査行（Dormant行もDuplicate行も）のチェックを一括OFF。
    - **削除成功時の全関連行一括除去**:
      - 削除成功した `DeletedPaths` を用いて、`_lastAuditItems.RemoveAll(x => DeletedPaths.Contains(x.FullPath))` を実行。実ファイルが消えたのに別カテゴリの行が画面に残る幽霊行バグを完全根絶。
    - **ReadOnly属性の一時解除と安全ロールバック**:
      - 削除前にファイルの元の属性を退避し、万が一ファイルロックや権限不足で削除に失敗した場合は元の属性を自動復元。
32. **断捨離・健全化の階層ソート ＆ 重複グループ一括束ね ＆ 数値ソート規約 (v1.5.0)**:
    - **重複グループの結束性維持（Cohesive Duplicate Group Sorting）**:
      - `AuditItemsDataGrid` の「容量」列ソート時、WPFの単純1行ソートを抑止し、`AuditReportService.SortAuditItems` による階層ソートを実行。
      - 同一容量に複数の重複グループや休眠ファイルが存在しても、`ThenBy(DuplicateGroupIndex)` により同一重複グループが途切れず1セットにまとまる。
      - グループ内では `ThenByDescending(IsOriginalCandidate)` により、**原本候補（`IsOriginalCandidate == true`）が必ず最優先（先頭）** に配置され、色分けパレットと原本保護の視覚的意図を100%忠実に維持。
    - **バイト単位数値ソートの保証**:
      - 容量列（`ColAuditSize`）に `SortMemberPath="Size"` を設定し、`SizeFormatted` の文字列ソート（`"100 MB"` < `"20 MB"`）による並び順破綻を根絶。
    - **フィルター連動・動的ソート維持**:
      - 絞り込みフィルター（カテゴリ切り替え・テキスト検索）適用時も現在のソート列・ソート方向（昇順/降順）を透過的に維持。
33. **容量分析マルチタブセッション完全復元 ＆ ソート内部状態トグル正本 ＆ D&D余計なボタン撤去 (v1.5.1)**:
    - **マルチタブ・セッション自動保存＆0秒ツリーキャッシュ復元（レベル2）**:
      - `AppSettings` に `StorageTabPaths`（開いていたパス一覧）および `ActiveStorageTabIndex`（選択中アクティブタブ）を永続化。
      - アプリ終了時（`OnClosing`）やタブ操作時（追加・削除・切り替え・スキャン完了）に状態を自動保存し、次回起動時に前回のタブセットを自動再構築。
      - 各タブは保存済みパスから `StorageHistoryService.LoadTreeCacheAsync` を呼び出し、前回のツリー構造・Top10・直下シェア・メーター・KPIを0秒で即時展開して直近の作業状態からシームレスに再開。
    - **Auditソートトグルの正本化（WPF ItemsSource再代入リセット対策）**:
      - WPF DataGrid は `ItemsSource` 再代入時に全カラムの `SortDirection` を内部で `null` にリセットするため、UIの揮発プロパティではなく内部状態変数 `_auditSortProperty` / `_auditSortDescending` を正本として昇降順トグルを判定。
      - 「詳細」列（`Detail`）ソートにも対応し、重複原本優先を維持。
    - **D&Dドラッグ可能アイテムの不要ボタン完全撤去**:
      - ADR 14（枠外ドロップ解除アーキテクチャ）により、登録済みカードやバッジは枠外へのドラッグ＆ドロップで直感的に解除できる仕様に統一されている。
      - 親カードのドラッグ判定と競合してクリックが反応しなくなっていた `✕` ボタン（Live ACLカード、Sim ACLカード、Sim移行元マッピングバッジ）を完全に撤去し、UIのシンプルさと操作の一貫性を向上。
34. **Live ACL マルチパネル＆差分適用 (Delta Apply) ＆ 移行スタジオ4カラム水平レイアウト ＆ 断捨離リアルタイム容量試算 ＆ 衛生監査240+現場基準化 (v1.6.0)**:
    - **Live ACL 3ペイン・マルチパネル＆差分適用（ノータッチ原則）**:
      - 1フォルダずつの切替型を完全刷新。左にフォルダ参照ツリー、中央に最大6フォルダの権限パネルが横並びスクロール、右にADパレットを配置した3ペイン構成。
      - 読み取り時からの変化点（Added/Removed/Modified）のみをピンポイントで差分適用。変更されていない既存ACEにはOS/メモリ上ともに1ビットも触れない（ノータッチ原則）。差分0件時はAPI呼び出し自体をスキップ。
      - `SimAclEntry.IsSameAccount` によりドメイン修飾の有無（`DOMAIN\User` と `User`）を安全に吸収。
      - `SimAclEntry.IsSameRights` によりWindowsカーネルが自動付加する `FileSystemRights.Synchronize` ビットを正規化し、実質的な権限変更のみを厳密判定。
      - 各パネルに「✕（閉じる）」、未保存変更バッジ、個別「⚡ 差分適用」、個別「↩️ ロールバック」を完備。
    - **移行スタジオ（Simulation Studio）の4カラム水平レイアウト化 ＆ D&D安定化**:
      - 中央の上下分割（ツリー上＋アコーディオン下）を撤廃し、「①移行元 ➔ ②新ツリー ➔ ③フォルダ詳細インスペクター ➔ ④ADパレット」の4カラム水平レイアウトに再編。
      - 新ツリーのD&Dでマウスの水平オフセットに遊び（60px〜幅-60px）を設け、深い階層からルートへ跳ね上がるドラッグ飛び移りバグを解消。
    - **断捨離（Audit）のチェックボックス連動リアルタイム削減容量試算**:
      - チェックON/OFFに連動し、原本を除外したユニークなファイルサイズをリアルタイム（ミリ秒単位）で動的再計算・表示。
      - `Dispatcher.CheckAccess()` ガードにより、ヘッドレス回帰テストやバックグラウンドスレッドからの変更でもUI例外を完全防止。
    - **衛生監査の現場基準化**:
      - パス長チェックの閾値を 260文字 から 240文字以上（危険域 / 移行先長大化リスク）に変更。
      - 日常記号（`# % & { } ~`）の過剰警告を抑止し、末尾スペース、末尾ドット、制御文字、NTFS不正文字を真の地雷として検出。

35. **Live ACL 安全保障（継承保持・マルチセット差分・継承ReadOnly化）＆ LiveAclStudio コンポーネント分離 (v1.6.1)**:
    - **Live ACL 安全ロジック改修 (Red Team レビュー対応)**:
      - **継承切断時のロックアウト防止**: 継承OFF時（inherit: false）は SetAccessRuleProtection(isProtected: true, preserveInheritance: true) を使用し、親からの既存ACEを明示的ACEに安全変換してから差分適用。管理者の意図せぬアクセス遮断・完全ロックアウトを根絶。
      - **マルチセット・ペアリング差分適用**: 同一アカウントに複数ACEが存在する場合（例: Allow Read と Allow Write）、単一 FirstOrDefault での突き合わせによるACE消失・誤削除を防ぎ、マッチ済みを追跡するマルチセット完全突き合わせアルゴリズムを導入。
      - **継承ACEの保護・ReadOnly化**: 継承ACE（IsInherited == true）は直接編集・削除不可とし、UI上に「🔒 継承」バッジを表示。親フォルダで管理すべきACEの実環境誤破壊を防止。
      - **適用後のOS実態再読込**: 差分適用後はメモリキャッシュを即時反映するだけでなく、OSのNTFS実態（GetAccessControl）から完全再読込を行い、Windowsカーネル付与フラグとの完全一致を保証。
      - **Audit原本保護 & 一括選択バッチ化**: 重複ファイルの原本候補（IsOriginalCandidate == true）は一括選択時にも安全に保護され、誤退避・誤削除を100%防止。
    - **LiveAclStudio への独立コンポーネント分離 (MainWindowの大規模整線)**:
      - フォルダ別権限エディタ、中央マルチパネル、ADパレット、D&D配線、Effective Access 逆引き監査、および OU階層ピッカーモーダルを独立した Views/LiveAclStudio.xaml / LiveAclStudio.xaml.cs (UserControl) へ完全分離。
      - MainWindow.xaml (-694行) および MainWindow.xaml.cs (-1400行) から約2100行の配線を削ぎ落とし、責務を明確化。
      - Windows 14ビット詳細権限モーダル (SecModalOverlay) は MainWindow に共通配置のまま残し、LiveAclStudio からは EditSecurityRequested イベント（コールバック付き）で連携することで、移行スタジオ（Tab 2）との二重化を完全防止。
      - 全自動回帰テスト 21/21 100% PASS を維持。

36. **外部ACL変更との競合検出（AclConflictException）＆ 継承初期状態整合性 ＆ 同一アカウント複数ACE追加 (v1.6.2)**:
    - **外部ACL変更の競合検出 (Medium)**:
      - 画面で読み込んだ時点の OriginalSddl と、適用直前の現行ディスク上SDDLを比較。外部（別管理者・システム・他ツール）による権限変更を事前検知。
      - 競合検知時は警告ダイアログ（再読込 / 強制適用 / キャンセル）を表示し、誤った旧差分による上書き破壊を防止。
      - AclService.ApplyLiveAclDeltaWithRollbackAsync でも expectedOriginalSddl を検証し、不一致時は AclConflictException をスローする2重防護アーキテクチャ。
    - **元から継承OFFフォルダの初期変更フラグ誤検知修正 (Low 1)**:
      - LiveAclPanelModel 初期化時に OriginalInheritAcl = isInherited を InheritAcl = isInherited より先にセットし、初期ロード完了時に明示的に panel.UpdateChangeStatus() を呼び出し。元から継承OFFのフォルダを開いた直後に「未適用の変更あり」と誤判定される不具合を根絶。
    - **同一アカウントに対する複数ACE作成の自由化 (Low〜Medium)**:
      - 完全同一のデフォルトACEの重複追加は防ぎつつ、設定（権限・適用先）の異なる複数ACEの作成・保持・編集をUIから自由に実行可能に拡張。
    - **アプリバージョン整合性の統一**:
      - FolderMorpher.csproj（1.6.2）、MainWindow.xaml（v1.6.2）、アセンブリメタデータ、および GitHub Release / タグ（v1.6.2）の表記揺れを完全統一。

37. **プロダクト思想の統一（観測即実行 / 介入3段ロケット）＆ Live ACL Dry-Run 差分モーダル ＆ カード2行レイアウト ＆ UI文言クリーンアップ (v1.6.3)**:
    - **プロダクト思想の統一（観測と介入の明確な二元論）**:
      - **観測系（Read-Only: スキャン/参照/逆引き等）**: Dry-Run もスナップショットも挟まず、ボタン押下で即時実行。管理者の探索テンポを阻害しない。
      - **介入系（State-Mutating: 権限変更/スケルトン作成/リンク修復/写真最適化等）**: **「① Check (Dry-Run差分確認) ➔ ② Commit (本番適用) ➔ ③ Verify (実態検証)」** の3段ロケット原則をプロダクト全体で統一。
    - **Live ACL 差分チェック (Dry-Run) モーダルの新設**:
      - 移行スタジオの `DiffModal` と同様のモーダル形式を採用。
      - 本番適用前に「変更予定（追加・削除・変更・維持件数）」、外部競合有無（安全/警告バッジ）、および「変更前（Before）➔ 変更後（After）」の対比テーブルを事前確認。
      - 右上「✕」および「キャンセル」で安全に中断可能。「⚡ 差分を本番適用 (Commit)」で atomic apply を実行後、OS実態再読込（Verify）を行い、SDDL自動バックアップも完備。
    - **カードレイアウトの構造的2行化（名前文字切れの完全解消）**:
      - ACEカード（`CurrentAclEntries`、`SimAclEntry`）および ADパレットカード（`AdPrincipalItem`）を意図された上下2行レイアウトに統一。
      - 1行目: アイコン ＋ 表示名（DisplayName）に横幅を100%開放し、長い名前も途切れず表示（ToolTipも完備）。
      - 2行目: アカウントID（Identity / AccountName）＋ 各種バッジ群（🔒継承、権限、適用先、種別）をすっきり整列。レスポンシブによる不自然な文章途中改行を根絶。
    - **UI文言・表記の洗練と重複削除**:
      - ステータスバー右下のバージョン重複表示（左サイドバーと重複）を完全削除。
      - 旧コード名・他社製品名（`GDMS` 等）を排除し、「衛生監査・容量削減」へと品位向上。
      - 「ガワ作成」「ガワ先行作成」を「スケルトン作成」「スケルトン先行展開」へ全面統一。長すぎる補足説明文もスリム化。

38. **AclChangePlan 実行計画一本化 ＆ 継承変更警告バナー ＆ セマンティック正常性検証 (Verify) ＆ UI文言徹底簡潔化 (v1.6.4)**:
    - **AclChangePlan による実行計画の単一化（PreviewとCommitの二重計算完全排除）**:
      - `AclService.BuildChangePlan()` により、読み取り時からの差分（Added/Removed/Modified/Untouched）、継承設定の推移、および適用後に期待される完全なACEセット（`ExpectedAfterEntries`）を1つの構造体に構築。
      - Dry-Runプレビュー表示（`_diffItems`）と本番適用（`ApplyChangePlanWithRollbackAsync`）で同一インスタンスを貫通させ、ロジックの乖離余地を物理的に排除。
    - **継承変更の可視化バナー（High寄り課題の解消）**:
      - 継承設定が変更された場合、モーダル最上部に専用警告バナー（`LiveAclInheritanceChangeBanner`）を自動展開。
      - 継承OFF時（有効➔無効）は、親から明示ACEへ昇格・保持されるACE件数を明示し、重大なアクセス権波及の切断事故を事前警告。
    - **セマンティック正常性検証 (True Semantic Verify)**:
      - 本番適用後、OSから再取得した最新ACLと `ExpectedAfterEntries` をセマンティック突合（`VerifyChangePlan`）。
      - 単なる再読込ではなく、期待されるACEと実際のACEが100%合致しているかを厳密検証（`✅ 正常`）。意図しないACEの混入や脱落があれば即座に不一致を警告。
    - **OriginalSddl 更新バグの完全修正（直後自己操作の競合誤爆根絶）**:
      - Commit成功後は、直前の旧バックアップSDDLではなく、ディスクに書き込まれた最新の実態SDDL（`_aclService.GetSddl(panel.FolderPath)`）を `panel.OriginalSddl` に再代入。
      - 続けて次の変更を行う際に、自分自身の直前変更を「⚠️ 外部競合」と誤判定するバグを根絶。
    - **UI文言の徹底簡潔化（プロ用ツールの品位向上）**:
      - パネルボタン: `⚡ 差分適用` ➔ `🔍 チェック`
      - モーダルタイトル: `⚖️ NTFS アクセス権 差分チェック (Dry-Run)` ➔ `⚖️ 差分チェック`
      - モーダル確定ボタン: `⚡ 差分を本番適用 (Commit)` ➔ `⚡ 適用`
      - バッジ・ステータス: `● 未適用の変更あり` ➔ `● 未適用`、`✅ 外部競合なし (安全)` ➔ `✅ 正常`、`⚠️ 外部変更を検知 (警告)` ➔ `⚠️ 外部競合`
      - ツールバー/逆引き: `✕ パネルを全解除` ➔ `✕ 全て閉じる`、`📋 台帳CSV出力` ➔ `📋 CSV出力`、`🔍 逆引き調査開始` ➔ `🔍 調査開始`、`📋 監査台帳 Excel` ➔ `📋 Excel出力`

---

## 4. ビルド・実行・検証コマンド

### ビルド（0 警告・0 エラーを維持すること）
```powershell
& "$HOME\.dotnet\dotnet.exe" build
```

### 配布用単一EXEの生成（Release self-contained）
```powershell
& "$HOME\.dotnet\dotnet.exe" publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
Copy-Item -Path ".\bin\Release\net8.0-windows\win-x64\publish\FolderMorpher.exe" -Destination ".\FolderMorpher.exe" -Force
```

### 自動回帰テストスイート（ヘッドレス自己検証・CIゲート）
バグ修正やリファクタリング後は、必ず以下の回帰テストを実行して 23/23 ALL PASSED であることを確認すること。
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

# Tab 3: リンク一括修復
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab3.png" --tab 3

# Tab 4: 断捨離・健全化
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab4.png" --tab 4

# Tab 5: メディア最適化
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab5.png" --tab 5
```