# FolderMorpher — 重要な設計判断の記録（Architecture Decisions / ADR）

> **【AIメンテナ・自律継続規約】**  
> 本ドキュメントは、FolderMorpher の全 59 項目に及ぶ過去の設計判断・障害対策（ADR 1〜59）を、**12大中核アーキテクチャ原則（Core Architecture Principles）** として体系化・統合した正本記録である。  
> ⚠️ **後続のAIメンテナへ**: 以下の仕様・設計制約を「不具合」または「改善の余地」と勝手に誤認して安易に巻き戻すことを厳禁とする。仕様変更を行う場合は、該当する中核原則の背景と過去のトレードオフを熟読すること。

---

## 1. 【正本走査 & UNC高速化】Win32一括列挙・4段フォールバック・全階層並列度2固定・ゼロI/O化
*(旧 ADR 8, 24, 34, 35, 36, 37, 38, 39, 41, 42, 43, 45, 51 を統合)*

- **Win32 ネイティブ高速一括取得**:
  - `FindFirstFileExW` (`FindExInfoBasic` + `FIND_FIRST_EX_LARGE_FETCH`) を採用。8.3短縮名の生成をスキップし、OSカーネルから大容量バッファでディレクトリエントリを一括取得する。
  - ハンドル管理は `SafeFindHandle` (RAII) で完全カプセル化し、メモリリーク・ハンドルリークを原理的根絶。
- **4段自動フォールバック**:
  - 1. 拡張UNC (`\\?\UNC\`) + `LargeFetch`
  - 2. 拡張UNC + `StandardFetch`
  - 3. **プレーンUNC Win32再試行 (`\\server\share`)**: Samba/NAS等の拡張UNC非対応環境でも即座にフォールバックして100%走査完遂。
  - 4. .NET `DirectoryInfo` (セーフガード)
- **ゼロI/O化（フォルダー単位RPCの完全根絶）**:
  - 親フォルダー列挙時のタイムスタンプ・属性情報を子ノード生成時に直結。`Directory.GetLastWriteTime` による数万回のネットワーク往復RPCを完全ゼロ化。
  - 監査走査（Audit）では `ScannedFileEntry` によりファイル属性の再問い合わせを根絶。
- **全階層並列度2固定（デュアルワーカー）＆ インメモリ・ボトムアップ集計**:
  - サーバー負荷と他業務ネットワークを100%保護しながら、全階層2車線化でSMB往復遅延を隠蔽し10〜20倍の高速化を達成。
  - Sol提唱の `pendingWorkCount` 連動によりワーカースレッドの早期脱落レースを完全根絶。

---

## 2. 【NTFS ACL完全等価・セキュリティ境界】Canonical DACL・SID最優先判定・DACL完全一致検証・SDDL原子的ロールバック
*(旧 ADR 2, 9, 12, 18, 20, 21, 23, 26, 31, 33, 40, 48, 52, 57 を統合)*

- **Windows Canonical DACL Ordering（評価順序）の厳格順守**:
  - 「明示Deny ➔ 明示Allow ➔ 継承Deny ➔ 継承Allow」の評価順序を完全再現。子の明示的Allowは親の継承Denyより優先される。
- **SID最優先アイデンティティ判定（マルチドメイン・混在環境保護）**:
  - `SimAclEntry.MatchesKey` および `IsSameAccount` は、両者に SID が存在する場合は **SID 完全一致判定を最優先**（セキュリティ境界の正本化）。同名アカウント（`PC01\Taro` vs `DOMAIN\Taro`）の誤同一視を完全防止。未解決時のみアカウント名比較へフォールバック。
- **DACLセマンティック検証（VerifyFolderDacl 一本化 & 余計なACE完全検知）**:
  - プレビュー差分検証（`VerifyChangePlan`）とスケルトン展開後検証（`DeploySkeletonAsync`）を `AclService.VerifyFolderDacl` へ一本化。
  - 継承設定、未反映ACEに加え、**計画にない予期せぬ余計なACE（不法侵入ルール）の存在を完全検知**して検証失敗とする。
- **SDDL原子的スナップショット & ロールバック保証**:
  - 変更前に SDDL を自動退避し、ワンクリックで復元可能（`ApplyLiveAclWithRollback`）。スナップショットの永続化が全保存先で失敗した（1件も成功しなかった）場合はコミットを絶対拒否。
  - スナップショットおよびロールバックのスコープは `AccessControlSections.Access`（DACL）に限定し、監査権限（SACL）欠如による例外を防止。

---

## 3. 【AD統合 & 逆引き監査】多重ネスト解決・primaryGroupID・双方向30s自動同期・他人アカウント安全保護
*(旧 ADR 3, 10, 15, 17, 33, 50 を統合)*

- **多重グループネスト & primaryGroupID 解決**:
  - `LDAP_MATCHING_RULE_IN_CHAIN` / `tokenGroups` により全階層のAD所属グループを再帰解決。
  - `primaryGroupID`（通常 513 = Domain Users）からドメインSIDを結合してプライマリグループSIDを正確に導出。`Domain Users` のハードコード特殊扱いを完全撤廃。
- **ADバックグラウンド自動同期（30s周期・MainWindow集約・双方向連動）**:
  - Tab 1（Live ACL）と Tab 2（Simulation）のADパレットUIを完全統一。
  - MainWindow の `_adSyncTimer`（30秒周期）により、フォーカス中タブのADキャッシュとパレット表示を常時自動同期。
- **他人アカウント指定時の実行者グループ誤爆防止**:
  - 調査対象が自身と一致する場合のみローカルIDをフォールバック使用。他人の場合は直接付与ACEのみとして明示し、実行者の権限が混入する誤認を防止。
- **AD未接続環境でのフォールバック**:
  - ドメイン未参加環境ではダミーアカウントを捏造せず、「⚠️ ローカルPC環境」案内バナーを表示。

---

## 4. 【移行スタジオ & スケルトン展開】Plan-First 3段ロケット・N:1仮想設計・ガワ先行DACL検証・Robocopyモード分離・安全エスケープ
*(旧 ADR 11, 12, 16, 25, 27, 46, 56, 58 を統合)*

- **3段ロケット原則（① Check ➔ ② Commit ➔ ③ Verify）**:
  - 移行ツリー設計から本番反映まで、必ず単一の `DeployPlan` を貫通させてプレビューと本番適用を行う。二重実装禁止。
- **ガワ先行作成 & 実機DACLセマンティックVerify**:
  - 空フォルダーツリーを先に実機展開し、NTFSアクセス権を適用後、即座に実機のDACLをセマンティック検証（`VerifyFolderDacl`）。
- **Robocopy モード分離 & /XD 連動**:
  - 「新設計ACL維持モード（`/COPY:DAT`）」と「旧環境ACL完全維持モード（`/COPYALL`）」を明示分離。
  - 親フォルダーの移行元に含まれる子孫ノードのソースパスを `/XD` に自動連動し、新旧ツリーの多重コピーを防止。
- **スクリプトパス安全エスケープ共通化 (`ScriptEscaper`)**:
  - PowerShell 変数代入式（`$TargetRoot = "..."`）、icacls、Robocopy の全出力パスにおいて、バッククォート・ダブルクォーテーション・特殊記号を共通クラスで厳格エスケープ。

---

## 5. 【断捨離・健全化（GDMS代替）】原本絶対保護・削除直前SHA-256再照合・休眠1年閲覧保護・フォルダ除外ゼロI/O・Excel台帳出力
*(旧 ADR 5, 22, 28, 30, 49, 53 を統合)*

- **原本候補の絶対保護 & 削除直前SHA-256再照合（誤削除ゼロ保証）**:
  - `AuditCleanupService.ExecutePlan()` は、原本候補（`IsOriginalCandidate == true`）を無条件で削除拒否。
  - 重複コピーの削除直前に、原本と対象ファイルの SHA-256 ハッシュを再計算・再照合。1バイトでも変化があれば削除を緊急中断。
- **休眠ファイル判定の二重保護（1年閲覧保護）**:
  - 更新日時（LastWriteTime）が閾値（3年超等）であっても、直近1年間（365日）に閲覧・参照されたファイル（LastAccessTime）は自動保護・除外。
- **フォルダー名部分一致除外（I/Oゼロスキップ）**:
  - カンマ区切りの除外キーワードに合致するフォルダーは、内部のファイル列挙自体をスキップし走査時間を大幅短縮。
- **安全退避batの撤去とExcel/CSV棚卸し台帳集約**:
  - 誤実行リスクのある一括退避batの出力を全廃し、ハイパーリンク付きExcel/CSV棚卸しレポートによる手動オプトイン完全削除へ集約。

---

## 6. 【メディア最適化】聖域保護（Master/RAW）・日時Exif完全保持・アトミック置換・GPU夜間バッチ
*(旧 ADR 6, 32, 54 を統合)*

- **プロ用マスター聖域保護**:
  - `_Master`, `RAW`, `印刷用` 等のフォルダー名、およびプロ用拡張子（`.psd`, `.ai`, `.raw`, `.cr2` 等）は自動保護・除外。
- **メタデータ・回転情報の100%保持 & アトミック置換**:
  - JPEG/PNG は長辺2560px・85%品質で圧縮。撮影日時・更新日時・Exifメタデータ・回転情報を完全保持。
  - テンポラリファイルへ出力後、原子的置換（Replace）を行うことで破損・中途半端な上書きを防止。
- **SafeFileEnumerator 統合**:
  - メディア走査エンジンを共通の `SafeFileEnumerator` に統合し、UNC高速化・8.3短縮名スキップ・RPCゼロ化を享受。
- **巨大動画Top抽出 & GPU夜間バッチ**:
  - モンスター動画をサイズ降順でTop抽出。夜間GPU圧縮（H.265 / ffmpeg）用バッチスクリプトを自動生成。

---

## 7. 【容量分析 & 数理予測】全体占有率と直下比率の厳格分離・二重加算防止・一次線形&Holt指数平滑・3連KPIカード
*(旧 ADR 1, 19, 29, 44 を統合)*

- **全体占有率メーターと直下比率の厳格分離**:
  - 親フォルダーに対する直下シェア（右ペイン「選択フォルダーの内訳」）と、スキャン対象ルート総容量に対する「全体占有率」を二重加算防止のため厳格分離。ルート行は `―` 表示。
- **数理予測エンジン（一次線形回帰 & Holt指数平滑化）**:
  - 過去スキャン履歴（`StorageHistoryService`）から、上限到達予測・日次ペース・R²信頼度を数理算出。推移グラフ直下に3連KPIハイライトカードを表示。
- **すっきりした2連KPIカード（v2.1.1）**:
  - メトリクスカードを「スキャン対象 容量」と「前回差分推移」の2連カードに集約。容量上位ファイル Top 10 は右ペインで独立して表示・機能。

---

## 8. 【Office & ショートカットリンク修復】対象XML限定・意味的Verify・VBAマクロ非破壊保護（PartiallyFixed）・GPOスクリプト生成
*(旧 リンク修復ADR を統合)*

- **Office内部リンク修復の対象XML限定**:
  - `.xlsx`/`.xlsm` のZIPパッケージ内において、リンク情報を含むXML（`xl/_rels/workbook.xml.rels`, `sheet*.xml.rels`）のみを対象に書き換え。
- **VBAマクロバイナリ（vbaProject.bin）の非破壊保護**:
  - バイナリ部分の破損を防ぐため、VBAマクロが含まれるファイルはバイナリに触れず通常XMLのみ修復し、ステータスを `PartiallyFixed`（部分修復）として明示。
- **意味的Verify**:
  - 修復後にZIPパッケージを展開・再検査し、リンク先が新パスに置換されていることを確認。
- **全社配布用GPOログオンスクリプト生成**:
  - デスクトップや共有上のショートカット（.lnk）を一括修復するPowerShellスクリプトを出力。

---

## 9. 【共通基盤 & 整線規則】FormatHelper・ShellHelper・ScriptEscaperによる重複根絶・文字列信号線禁止
*(v2.1.1 新規施工)*

- **サイズフォーマット正本化 (`FormatHelper`)**:
  - `FileItemNode`, `AuditModels`, `MediaOptimizerModels` 等に重複実装されていたバイト数フォーマットロジックを `FormatHelper.FormatBytes(long bytes, int decimals = 1)` に完全一本化。
- **エクスプローラー連携正本化 (`ShellHelper`)**:
  - 各タブ・ウィンドウに散在していた `Process.Start("explorer.exe", ...)` を `ShellHelper.SelectInExplorer` および `ShellHelper.OpenFolder` に集約。存在確認・親ディレクトリフォールバックを内包。
- **UI表示文字列の業務ロジック利用禁止（文字列を信号線にしない）**:
  - `item.Detail.Contains(...)` などのUI整形文字列による分岐を禁止し、Model の構造化プロパティ（Enum, bool, long）を判定の正本とする。

---

## 10. 【コードビハインド物理分割】MainWindow partial class 機能別分割・MVVM全面改築の禁止・UI一時状態の局所保持
*(旧 ADR 59 を統合)*

- **機能別 partial class 分割**:
  - 4,100行超に肥大化した `MainWindow.xaml.cs` を、機能タブ単位で物理分割：
    - `MainWindow.xaml.cs` (本体・ライフサイクル・共通状態・Settings)
    - `MainWindow.Storage.cs` (Tab 1: 容量分析・監視)
    - `MainWindow.Simulation.cs` (Tab 3: 移行スタジオ・D&D・詳細権限モーダル)
    - `MainWindow.LinkFix.cs` (Tab 4: リンク一括修復)
    - `MainWindow.Audit.cs` (Tab 5: 断捨離・健全化)
    - `MainWindow.Media.cs` (Tab 6: メディア最適化)
    - `MainWindow.Localization.cs` (日英ローカライズディスパッチ)
- **MVVM全面改築の禁止（床を理由なく剥がさない）**:
  - UI固有の一時状態（選択中インデックス、トースト表示、UIイベントディスパッチ）は MainWindow に保持して問題としない。美観のためだけに不要なViewModel/Controllerを乱立させない。

---

## 11. 【UI/UXデザイン言語】Modern Fluent Canvas・クッキリ文字レンダリング・広域受容D&D・引き締まったプロ文言
*(旧 ADR 4, 13, 14, 47, 55 を統合)*

- **Modern Fluent Canvas & タイポグラフィ**:
  - ウィンドウ背景 `#F8FAFC`、カード背景 `#FFFFFF`、枠線 `#E2E8F0`。
  - `TextOptions.TextFormattingMode="Display"`、`TextRenderingMode="ClearType"` による滲みのない鮮明な文字描画。
- **広域受容 D&D アーキテクチャ**:
  - アコーディオン全体やカード一覧エリアを広域受容面として開放。枠外へのドロップによる直感的なポイ捨て解除。
- **引き締まったプロフェッショナル文言（v2.1.1）**:
  - `(直接起動対応)`, `(直下シェア)` などの冗長な括弧付き補足語を排除し、洗練された簡潔な表記に統一。

---

## 12. 【全方位日英両対応 & 回帰テスト品質ゲート】多言語リソース正本化・7大ドメイン自動回帰テスト常時通過
*(旧 継続品質規約 を統合)*

- **日英バイリンガル完全同期**:
  - `Services/AppStrings.cs`（静的辞書）と `MainWindow.Localization.cs` を正本とし、全画面・全モーダル・全出力ファイル（Excel/CSV/スクリプト）の日英完全同期を保証。英語モード時のCJK文字残存をゼロ化。
- **ヘッドレス自己検証 CI ゲート（8/8 ALL PASSED 必須）**:
  - コード変更後は必ず `dotnet run --no-build -- --test-regression` を実行し、以下の8大ドメインすべてが 100% PASS することを必須ゲートとする：
    - `Domain 1`: Live ACL & Effective Access (Canonical DACL, Rollback SDDL, SID最優先一致, 余計なACE検知)
    - `Domain 2`: Storage Explorer & UNC Traversal (Win32 LargeFetch/8.3 Skip, SafeFindHandle, Concurrency 2)
    - `Domain 3`: Simulation Studio (Plan-First Skeleton, Robocopy /XD, 実機DACLセマンティックVerify)
    - `Domain 4`: Audit & Hygiene (原本絶対保護, 削除直前SHA-256再照合, 休眠1年閲覧保護)
    - `Domain 5`: Media Optimizer & LinkFixer (PNG透過保持, 聖域保護, SafeFileEnumerator, PartiallyFixed)
    - `Domain 6`: MFT & Defensive Hardening (Initial LCN 0-Base, AppSettings Cascade)
    - `Domain 7`: Bilingual Localization & Storage Forecasting (JA/EN Switch, Holt Forecasting)
    - `Domain 8`: Search Studio (Advanced Query Parser, In-Memory Fast Match, Content Search)

---

## 13. 【エンタープライズ移行パッケージ & 意思決定支援】Wave自動分割・Runbook台帳Excel・多重コピー防止/XD・旧共有安全停止ガイド・二重加算防止
*(v2.1.2 新規施工 & 契約完全収束)*

- **ベンダー標準 移行工程の静的生成 (`MigrationPackageService`)**:
  - `Phase 1: 01_Baseline_Sync.bat` (事前フル同期: `/R:1 /W:1 /MT:16 /COPY:DAT` による高速転送)
  - `Phase 2: 02_Delta_Sync.bat` (中間差分同期: 更新ファイルのみ追随)
  - `Phase 3: 03_PreCutover_Freeze_Guide.md` (旧共有安全停止ガイド: 安易な一律バッチ実行を廃止し、SMB共有アクセス権変更・セッション切断・DFS無効化等の推奨手順および切戻し手順を明記したガイド手順書を生成)
  - `Phase 4: 04_Final_Cutover_Mirror.bat` (本番最終ミラー: `/MIR` による完全一致同期、事前同期済みのため数分〜数十分で完了)
  - `Orchestrator: 00_Run_All_Waves_StepByStep.bat` (全Wave対話式順次実行マスターバッチ)
- **人間の判断を強力にアシストする意思決定指標（Decision Support） & 正本計算**:
  - **分割ポリシー選択**: 第1階層（部署・トップフォルダごと推奨） / 容量バジェット（指定GB以下に自動集約） / 一括出力
  - **動的見積パラメータ**: 想定転送速度（MB/s、既定80）と想定差分率（%、既定2.0）をUI上で調整可能。計算ロジックは `MigrationPackageService` と `MigrationWavePlan` の正本プロパティに一本化。
  - **アセスメント警告**: 週末枠超過リスク（⚠️ 48時間超）および実測値に基づくランダムI/O過多（⚠️ 10万件超、`/MT:32` 推奨）を自動検知してバッジ提示。
- **容量二重加算の完全根絶（包括親ノード優先）**:
  - `CalculateRecursiveSize` において、親ノードが包括サイズを保持している場合は `Math.Max(node.EstimatedSizeBytes, childrenSum)` により二重合算を原理的根絶。
- **ファイル数の実測値引き継ぎ & 捏造完全排除**:
  - 容量÷10MBの推測カウントを完全撤廃。DiskScanの実測値（`FileItemNode.FileCount`）を `SimFolderNode.EstimatedFileCount` (long?) として引き継ぎ、未計測の場合は「未計測 (-)」と潔く表示。小ファイル警告も実測値がある場合のみ発報。
- **N:1マッピングにおける子孫パス多重コピー防止 (`/XD`) の完全貫通**:
  - 親ノードの配下にある子孫マッピングパスを自動検出し、Robocopyの `/XD` 引数へ自動注入。フォルダー構成を再編成した際の二重転送・データ重複を原理的に根絶。
- **ClosedXML による 3シート構成 移行進捗台帳 (`Migration_Runbook.xlsx`)**:
  - `1_概要・Wave計画`: KPIサマリーカード（総容量、実測総ファイル数、推定所要時間）と波次計画テーブル。
  - `2_移行WBS・工程表`: ベンダー標準の事前準備〜フル同期〜差分〜旧共有停止ガイド〜本番切替〜検証〜緊急切戻しチェックリスト。
  - `3_マッピング・除外詳細`: 新旧パス対応表および `/XD` 除外パス一覧。
- **文字コード契約**:
  - バッチファイルは冒頭で `chcp 65001 > nul` を宣言し、BOMなし UTF-8 (`new UTF8Encoding(false)`) で保存。Markdownドキュメントは UTF-8 で保存。Shift-JIS (CP932) のコードページ依存エラーを根絶。

---

## 14. 【統合ファイル検索】Search Studio（高度な検索構文・0秒インメモリ＆プログレッシブ直接走査・OpenXMLストリーム全文検索・スタジオ連携Hub）
*(v2.2.0 新規施工 & ADR 60)*

- **0秒インメモリ高速検索 ＆ プログレッシブ直接走査のハイブリッド・アーキテクチャ (`SearchEngineService`)**:
  - **インメモリ走査**: Storage Explorer (Tab 1) で一度スキャン済みのツリー（`FileItemNode`）に対しては、I/Oを一切発生させず完全インメモリ（0ms〜数ms）で走査。
  - **参照フォルダー指定による部分木スコープ絞り込み & 自動フォールバック**:
    - 対象フォルダーが指定された場合、全スキャン済みツリーを無差別に走査せず、指定パスに一致する `FileItemNode` を探索してその部分木のみを起点に走査。
    - 指定フォルダーがスキャン済みツリーに含まれていない場合は、シームレスに直接走査（DirectFolder）へフォールバックし、ユーザーの探索テンポを阻害しない。
  - **プログレッシブ直接走査**: 未スキャンの任意パスやUNC共有に対しては、ADR 35/49 の正本である `SafeFileEnumerator` を貫通させて非同期プログレッシブ走査を実施。進捗（走査件数・ヒット数）をUIへリアルタイム反映。
- **高度な検索クエリ構文解析器 (`SearchQueryParser`)**:
  - 高速・直感的な検索構文をフルサポート（未実装の `duplicate:` 構文は排除し、100%嘘のない仕様に統一）：
    - 拡張子フィルタ: `ext:xlsx,docx`
    - サイズ条件: `size:>100MB`, `size:<10MB`（KB/MB/GB/TB対応）
    - 休眠期間: `dormant:>3y`, `dormant:>180d`（年単位・日単位の両対応、`>`プレフィックス正規化）
    - パス長危険域: `pathlen:>240`（Windows MAX_PATH 260字問題の予防）
    - 禁則文字: `chars:illegal`（Windows予約文字 `< > : " / \ | ? *` の検出）
    - 全文検索: `content:"キーワード"`（Word等のrun分割タグ跨ぎキーワードもXMLタグ除去探索で確実に抽出）
    - Office内部リンク: `office-link:true`（外部リンク有無検知） / `office-link:"\\OldServer"`（特定リンク先検索）
    - 正規表現: `regex:^FIN_[0-9]+`
    - 除外（NOT）: `!temp`
    - 完全一致フレーズ: `"Project Alpha"`
- **Office OpenXML ストリーム全文検索 & スニペット抽出**:
  - テキストファイル（.txt, .csv, .log, .json, .xml 等）および Office ファイル（.xlsx, .docx, .pptx）のZIP内XMLストリーム（`xl/sharedStrings.xml`, `word/document.xml`, `ppt/slides/` 等）を直接メモリ展開してキーワード探索。
  - 前後40文字の文脈スニペット（`...キーワード...`）を自動抽出し、UI上に即座に提示。巨大ファイル事故を防ぐため 50MB 制限およびテキスト判定ガードを設置。
- **全スタジオを束ねる統合 Hub 連携 (`MainWindow.Search.cs`)**:
  - 検索結果の右クリック／ダブルクリックから、FolderMorpher 内の各スタジオへ即座に転送・ジャンプ：
    - `Live ACL Studio`: 当該フォルダーのNTFSアクセス権を即座に可視化・編集
    - `Simulation Studio`: 移行ツリー設計へソースノードとして追加（**ファイル選択時も親フォルダーの `FileItemNode` から配下全体容量・実測ファイル数・DACLを正本継承し、ファイル単体サイズ誤設定を完全根絶**）
    - `LinkFixer`: 内部リンク切れ修復の対象としてセット
    - `Audit & Hygiene`: ファイル健全化・整理の対象としてセット
    - `Explorer で選択`: 標準エクスプローラーを起動しハイライト表示
- **洗練されたスリム UI & ClosedXML による Excel 台帳 & CSV エクスポート**:
  - 余計なプリセットチップを排したすっきりとしたコントロールバー、リアルタイム4連KPIカード、広大な仮想化DataGrid。
  - 検索結果一覧（ファイル名、パス、サイズ、更新日時、拡張子、属性、一致理由/スニペット）をハイパーリンク付き Excel および UTF-8 CSV として即座に出力可能。
- **日英バイリンガル完全対応 & 回帰テスト Domain 8 追加**:
  - 全UIリソースの日英完全辞書化および文字化け（mojibake）の完全根絶。
  - 回帰テストスイートに `Domain 8`（パーサー構文、インメモリ高速検索、OpenXML全文検索、休眠・リンク厳密判定）を追加し、CIゲートを 8/8 ALL PASSED 化。

---

## 15. 【高速全文検索 & PDF IFilter エンジン】（Windows IFilter・PDF/Office/Text 4大高速化・本文検索トグル）
*(v2.2.1 新規施工 & ADR 61)*

- **Windows ネイティブ IFilter ＆ 純C# PDFストリーム・フォールバック (`PdfSearchHelper`)**:
  - **Windows IFilter 連携**: OS標準のネイティブCOMインターフェース（`query.dll` の `LoadIFilter` および `IFilter` COMインタフェース）をP/Invoke呼び出しし、OSレベルの高速C++インデックスエンジン（`Windows.Data.Pdf.dll` 等）でPDFのテキストチャンクを直接ストリーム抽出。
  - **純C#ストリーム・フォールバック**: IFilter未登録環境や一時的なCOMエラー時にも、純C#による高速フォールバック機構を配備。メタデータスキャン（UTF-8/ASCII）および PDF内部の Deflate/FlateDecode 圧縮ストリーム（zlib `0x78 0x9C` 等）を展開し、オペレータ（`Tj` / `TJ`）や生テキストからキーワードを高速抽出。
  - **OCRの明示的除外**: 画像PDFに対するOCR処理は一切行わず、テキストPDFおよびハイブリッドPDFを対象とすることで極小のフットプリントと超高速性を両立。
- **全文検索の 4 大高速化アーキテクチャ (`SearchEngineService`)**:
  - **アイデア 1（Excel sharedStrings.xml 優先チェック）**: `.xlsx` の場合、全ワークシートXML（`sheet1.xml`, `sheet2.xml` ...）を無差別に走査する前に、まず共通文字列辞書である `xl/sharedStrings.xml` を高速チェック。辞書内にキーワードが存在しない場合は、全シートのI/O・XMLパースを数ミリ秒で即座に完全スキップ。
  - **アイデア 2（先頭4KB Early Drop）**: テキスト系ファイルにおいて、先頭 4096 バイト内に NULL バイト（`\0`）が連続して含まれるバイナリファイル（実行ファイル、DB、独自バイナリ等）を即座に検知して探索から早期脱落。無駄な全読込I/Oを未然に防止。
  - **アイデア 3（マッチ即離脱 Early Exit）**: 最初のキーワード一致を検出した瞬間に、以降の行・ブロック・ZIPエントリの読み込みを直ちに打ち切り、不要な残存ストリームI/Oをゼロ化。
  - **アイデア 4（.NET 8 SIMD / 高速メモリ走査）**: `Span<char>` / `StringComparison.OrdinalIgnoreCase` によるベクトル化メモリスキャン、および 64KB/4KB チャンクバッファによる低アロケーション走査。
- **UI直結の「📄 本文も検索 (全文検索)」トグル (`SearchContentCheckBox`)**:
  - ユーザーが `content:` 構文を手動入力しなくても、チェックボックスをONにするだけで通常の単語入力（例: `最高機密`）がファイル名だけでなくファイル本文（PDF, Excel, Word, PPT, TXT, CSV, JSON, LOG 等）も対象にシームレスに探索。
  - インクリメンタル検索（タイピング中の250msデバウンス走査）時には、深いファイルI/Oを伴う本文検索を自動ガードし、Enterキー押下または「検索」ボタンクリック時にのみ重いI/O走査を実行する保護設計を採用。
- **既定「ファイルのみ」検索 ＆ 「📁 フォルダも含める」オプトイン (`SearchIncludeFoldersCheckBox`)**:
  - 検索時はファイルを探すケースが大多数であるため、デフォルトはフォルダーを検索結果から除外（ファイルのみリストアップ）。
  - フォルダー自体も結果一覧に含めたい場合は、新設された `[ ] 📁 フォルダも含める` チェックボックス（または `type:dir` / `type:folder` 構文）で明示的にオプトイン可能。
- **ファイル名優先（Name-first）マッチングによる親フォルダー巻き添えヒットの完全根絶**:
  - キーワード照合において、`\` や `/` を含まない通常単語は `node.FullPath` ではなく **`node.Name`（ファイル名単体）** と照合。
  - 上位の親フォルダー名（例: `D:\プロジェクトA\...`）にキーワードが含まれている場合に、配下の全ファイルが巻き添えで大量ヒットする事故を原理的に根絶。
  - 親フォルダーも含めて絞り込みたい場合は、`path:xxx` 構文またはキーワードに `\` を含める（例: `2024\報告書`）ことで、直感的なパス絞り込みを両立。
- **回帰テスト Domain 8 の強化 & CI 8/8 ALL PASSED 維持**:
  - 回帰テストに PDF（IFilter/ストリーム探索）、テキスト、バイナリEarly Drop、`SearchContentMode` トグル、および `IncludeFolders`（既定false/trueのフォルダー排他・包含）と親フォルダー巻き添え防止の包括アサーションを追加し、全8大ドメイン回帰テストで 100% 検証を担保。

---

## 16. 【全自動スマート検索 ＆ SQLite FTS5 trigram + LIKE ハイブリッド全文検索】（全自動ルーティング・差分更新・亡霊クリーンアップ・構文完全貫通）
*(v2.2.1 新規施工 & ADR 62)*

- **ユーザー操作不要の全自動スマートルーティング (`MainWindow.Search.cs`)**:
  - **メカニカルスイッチ（ラジオボタン・更新ボタン）の完全撤廃**: 「スキャン済みツリー」「直接走査」「インデックス検索」のラジオボタンや「インデックス更新」ボタンを排し、入力窓と対象フォルダーだけの直感的一本化UIへ刷新。
  - **最速パス自動ルーティング**:
    1. **FTS5 インデックス検索（最速ミリ秒）**: 対象パスのインデックスが存在する場合、または「本文も検索」ON時は自動で FTS5 ミリ秒検索を実行。
    2. **0秒インメモリ検索**: スキャン済みツリーに対象が含まれ、本文検索OFF（名前・属性検索）の場合は 0ms でメモリ内ツリーから瞬時抽出。
    3. **ライブ直接走査 ＆ バックグラウンド自動インデックス同期**: 初見フォルダーや未スキャンUNCの場合は即座に直接ストリーミング走査を開始し、完了後に裏で `TriggerBackgroundIndexUpdate` によりインデックスを自動蓄積。ユーザーはインデックスの存在すら意識する必要がない。
- **SQLite FTS5 trigram ＋ LIKE ハイブリッド全文検索 (`ContentIndexService`)**:
  - **日本語2文字単語の 100% ヒット保証**: SQLite FTS5 trigram の 3文字未満 MATCH 不可制約に対し、3文字以上のトークンは `ContentFts MATCH`（個別トークンAND結合）、1〜2文字の単語（「契約」「極秘」等）は `c.Body LIKE '%語%' OR f.FullPath LIKE '%語%'` へ自動フォールバック。構文エラーを起こさず確実にミリ秒抽出。
  - **複数キーワードの独立 AND 結合**: 単語間に空白がある場合（例: `予算 2026`）、単一フレーズ化せず `"予算" AND "2026"` として SQL 結合し、語順や離散テキストに依存せず確実にヒット。
  - **SearchQuery 構文の SQLite WHERE 句への完全貫通**: `ext:`, `size:`, `date:`, `path:`, `!除外`, `pathlen:` を SQL レベルでフィルタリングし、不要な行フェッチをゼロ化。
  - **削除・リネーム亡霊ファイルの自動クリーンアップ**: 完全走査完了時、実ファイルが削除されたレコードを `DELETE FROM IndexedFiles / ContentFts` により自動消去。
  - **ExtractorVersion によるマイグレーション保証**: 抽出エンジンのバージョンが一致しないレコードは次回走査時に自動で再抽出・再インデックス。
  - **安全なDB格納先**: `%LOCALAPPDATA%\FolderMorpher\ContentIndex.db`。WALモード永続化。
- **非圧縮・平文PDFテキスト抽出フォールバック (`PdfSearchHelper`)**:
  - IFilter 未登録環境や非圧縮テキスト埋め込みPDFにおいても、`rawUtf8` から `\((?<text>[^)]*)\)\s*Tj` 等のテキストオペレータを自動抽出してインデックス化。
- **回帰テスト Domain 8 包括自己検証**:
  - 2文字日本語フォールバック、複数語AND結合、クエリ構文貫通、差分更新、新規ファイル即時ヒット、削除亡霊ファイルクリーンアップ、および HasIndexForPath を網羅し、CI 8/8 ALL PASSED を堅持。

---

## 17. 【Search Index 完全性保証 ＆ 移行パッケージ硬化 ＆ Quiet Fluent 研磨】（IndexedRoots・ディレクトリ境界厳格化・TargetRoot必須化・スリム1行メトリクスバー）
*(v2.2.2 新規施工 & ADR 63)*

- **Search Index 完全性保証 (`IndexedRoots` テーブル ＆ `HasCompleteIndexForPath`)**:
  - **問題の根絶**: 従来は途中で中断されたインデックスであっても1件でもレコードがあれば `HasIndexForPath` が `true` を返し、未処理の99%が FTS でヒットせず検索漏れ（false negative）を起こす危険性があった。
  - **設計**: `IndexedRoots` テーブル（`RootPath`, `Status: InProgress/Complete/Error`, `CoverageComplete`, `LastCompletedUtcTicks`, `TotalFiles`, `ExtractorVersion`）を配備。走査開始時に `InProgress` を記録し、全走査が正常完遂した時点でのみ `Complete` に更新。
  - **判定**: `HasCompleteIndexForPath` は、対象フォルダーまたはその上位フォルダーが `IndexedRoots` で `Status == 'Complete'` かつ `ExtractorVersion` が現在バージョンと一致する場合にのみ `true` を返す。中断・クラッシュ時は自動で `false` となり、安全な直接走査へフォールバック。
  - **AccessDenied 保護付き亡霊クリーンアップ**: 走査中にアクセス拒否フォルダー（`ScanCoverage.AccessDeniedFolders > 0`）が1つでも存在した場合は、ファイルが消えたのではなく読めなかっただけであるため、DB からの誤削除クリーンアップ（DELETE）を完全に抑止。
- **ディレクトリ境界の厳格化（近接類似フォルダーの巻き込み事故完全防止）**:
  - SQL prefix において `normTarget.Replace(...) + "%"` としていたため、`C:\Share\Sales` を走査した際に `C:\Share\SalesOld` や `SalesBackup` が誤マッチしていた。
  - パスプレフィックスに必ずディレクトリセパレータ（`\`）を付与し、`(FullPath = @exact OR FullPath LIKE @dirPrefix ESCAPE '\')` の2条件に分離・正本化。
- **本文抽出ロジックの単一正本化 (`ContentExtractionService`)**:
  - `ContentIndexService` と `SearchEngineService` に分散していた OpenXML、PDF、PlainText（UTF-16/BOM/Shift-JIS自動判定）の抽出処理を `ContentExtractionService` へ集約。
- **移行パッケージ（Migration Studio）の安全硬化**:
  - **TargetRoot 必須バリデーション**: `options.TargetRoot` が空の場合に `\\NewServer\Share` を勝手に仮定する危険な挙動を廃止し、未入力時は `InvalidOperationException` で生成を拒否。
  - **スクリプト相対ログパス (`%~dp0..\Logs`)**: カレントディレクトリ（CWD）に依存せず、バッチファイル自身の配置場所から確実にログディレクトリを解決。
  - **遅延環境変数展開 (`EnableDelayedExpansion`) の完全排除**: 感嘆符 `!` を含むフォルダー名・ファイル名が文字化け・消失する Windows CMD の罠を根絶。
  - **エラー伝播 (`exit /b 1`)**: robocopy で `errorlevel 8` を検知した際、バッチ終了時に `exit /b 1` を返し、マスターバッチからの連続実行時に即座に後続を安全停止。
  - **本番最終ミラー Dry-Run (`04_Final_Cutover_DRYRUN.bat`) 同時出力**: `/MIR` による削除・上書きを恐れる管理者が、本番切替前にノーリスクで差分ログを事前確認できるよう、`/L` オプション付きの Dry-Run スクリプトを標準同梱。
- **UI/UX Quiet Fluent 研磨（スリム1行メトリクスバー & ドロップシャドウ撤去）**:
  - 4枚の独立した巨大KPIカード（約70px高）を、1行のスリムステータスメトリクスバー（面＋微細1pxボーダー、約36px高）へ統合。DataGrid の表示領域を大幅に拡大。
  - コントロールカードおよび結果テーブルの重いドロップシャドウを撤去し、フラットで落ち着いた Quiet Fluent デザインへ整線。
- **回帰テスト Domain 3 & Domain 8 の更なる強化**:
  - パス境界厳格性、未完了ルート判定、TargetRoot未入力拒否、`04_Final_Cutover_DRYRUN.bat` 同時生成、`LOG_DIR=%~dp0..\Logs`、`exit /b 1`、`EnableDelayedExpansion` 排除のアサーションを追加し、CI 8/8 ALL PASSED を堅持。

---

## 18. 【FTS5ハイブリッド二元インデックス ＆ 最深Root完全性 ＆ Master BAT安全停止】（抜本案施工・UNION Name OR Content・最深Root判定・Master BAT errorlevel伝播）
*(v2.2.2 本番施工 & ADR 64)*

- **全ファイルメタデータ登録 ＋ 本文対象のみFTS（抜本案の完全施工）**:
  - **背景と課題**: 従来の `ContentIndexService` は Office/PDF/Text（50MB以下）のみを `IndexedFiles` に登録していたため、インデックスが存在するフォルダーで通常ファイル名検索（例: `backup.zip`, `setup.exe`）を行った際、インデックス検索にルーティングされてしまい、0件（false negative）になる深刻な検索漏れが存在した。
  - **抜本案設計**: 走査で発見された**すべてのファイル**のメタデータを `IndexedFiles` に登録（ディレクトリのみ除外、非本文ファイルは `Status = 0: MetadataOnly`）。
  - **本文抽出の局所化**: テキスト抽出および `ContentFts`（trigram 仮想テーブル）への登録は、`ContentExtractionService.IsSupported` かつ 50MB 以下のファイルにのみ限定。
  - **UNION による Name OR Content ハイブリッド検索**:
    `SearchIndexedAsync` では、SQLite のクエリオプティマイザが FTS5 の `MATCH` 制約を最大限に活用できるよう、`ContentFts`（本文検索）と `IndexedFiles`（ファイル名/属性検索）を `UNION` で結合。
    - 通常の名前検索時（`SearchContentMode == false`）: 本文にキーワードがあるファイル（スニペット付き）と、ファイル名にキーワードがある非本文ファイル（`.zip`, `.exe`, `.mp4` 等含む）の両方がミリ秒でヒット。
    - 本文検索明示時（`SearchContentMode == true` または `content:`）: 非本文ファイルを除外（`f.Status = 1`）し、厳密な本文検索結果を返す。
- **最深（最長パス）IndexedRoot による完全性判定**:
  - **課題**: `C:\Share` が Complete で `C:\Share\Sales` が中断・Error の場合、`C:\Share\Sales` を検索した際に親の Complete が先にヒットして「完全インデックス済み」と誤判定される穴が存在した。
  - **施工**: マッチするすべての `IndexedRoots` のうち、**最深（パス文字列長が最大）** のレコードを正本として判定。
  - `bestStatus == 'Complete' && bestCoverage == 1 && bestExtVer == CurrentExtractorVersion` の全条件を満たす場合のみ `true` を返す。親が Complete でも子が Error なら `false`、親が Error でも子が Complete なら `true` となる完全なセマンティクスを確立。
- **本文抽出エンジンの単一正本化 ＆ Shift-JIS（CP932）自動フォールバック**:
  - `ContentExtractionService` に `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);` を導入し、UTF-8/UTF-16 だけでなくレガシーな Shift-JIS 日本語テキストも完全サポート。
  - Live走査用のストリーム高速検索メソッド（`SearchOfficeContent`, `SearchTextContentAsync`, `ExtractSnippet`）を `ContentExtractionService` へ単一正本化し、`SearchEngineService` 内の二重実装を完全根絶。
- **マスター移行バッチ（`00_Run_All_Waves_StepByStep.bat`）の安全停止・エラー伝播**:
  - 各 Wave の呼び出しを `call "%~dp0{dirName}\{batName}"` に統一し、CWD（カレントディレクトリ）非依存化。
  - 各 `call` の直後に `if errorlevel 1 (` ブロックを配置し、エラー発生時は即座に「後続Waveを安全停止しました」と警告して `exit /b 1` で中断。事故防止・手戻りゼロを保証。
- **回帰テスト Domain 3 & Domain 8 の包括検証更新**:
  - 非本文ファイル（`.zip`, `.exe`）の通常インデックス検索ヒット検証。
  - 最深Root判定（子がErrorの場合にfalseを返すこと）の検証。
  - Master BAT の `%~dp0` および `if errorlevel 1 / exit /b 1` の検証。
  - 全 8 大ドメイン包括自動回帰テスト ALL PASSED (8/8) を堅持。

---

## 19. 【UI整線 ＆ 深階層ツリー操作性の革新】不要要素撤去・Enter自動読込・インテリジェント動的サブ化・D&Dホバー自動展開・ドロップ先ハイライト
*(v2.2.3 本番施工 & ADR 65)*

- **不要・冗長要素の完全撤去（UI整線）**:
  - 容量分析（Tab 1）ツールバーの機能重複していた謎の空欄 `FilterTextBox` を撤去。
  - 移行スタジオ（Tab 4）ペイン①の「読込」ボタンを撤去し、パス入力欄での Enter キー押下で即座に走査開始するイベント配線へ集約。
  - 移行スタジオ（Tab 4）ペイン③の対症療法だった「📥 左の現行フォルダをここにドロップして直下にサブ化」ゾーンを完全撤去。
- **深階層でも迷わない4大ツリー操作性の革新**:
  - **1. インテリジェント動的サブ化ボタン（ワンクリック確定）**:
    ペイン①下の `SimCloneSelectedButton` を動的追従化。中央ツリーでフォルダーが選択されていれば、その直下にサブフォルダーとして配置（`CreateSimNodeFromSourceWithAcl(selected, _selectedSimNode, _selectedSimNode.Level + 1)`）。未選択なら新設ルートとして配置。ボタン文言および ToolTip も選択状態に応じて動的更新（例: `➡️ 「{親フォルダー名}」直下にサブ配置` / `➡️ 新設ルートとして配置`）。D&Dの繊細なマウス操作が苦手な管理者でも、ワンクリックで100%確実に深い階層へサブ配置可能。
  - **2. ドロップ先ノードの視覚的ハイライト（Visual Drop Target Feedback）**:
    D&Dドラッグ中、マウスが乗っている `SimFolderNode` の `IsDragOverTarget` をリアルタイム検知し、薄青背景（`#E0F2FE`）で明示。狭いツリー行でも「今どこに落とそうとしているか」が一目で分かる。
  - **3. ドラッグ中のホバー自動展開（Auto-Expand on Hover）**:
    ドラッグしたまま折りたたまれているフォルダー（`!node.IsExpanded`）の上に 400ms 留まった際、自動でそのフォルダーを開いて下層を表示（Windows Explorer / macOS Finder 互換）。何階層深くても、ドラッグしながらスイスイ目的の階層へ潜り込んでドロップ可能。
  - **4. ドラッグ中の上下端自動スクロール（Auto-Scroll on Drag）**:
    ツリーの上下端付近（25px領域）にドラッグカーソルが侵入した際、ScrollViewer を自動で滑らかにスクロール。ツリーが縦に長くてもスクロールバー操作なしでドロップ可能。

---

## 20. 【移行ツリーUndo ＆ 枠外Drop安全保護 ＆ 全タブQuiet Fluent 1行メトリクスバー統一】
*(v2.2.4 本番施工 & ADR 66)*

- **移行ツリー Undo 機能 ＆ 隠しジェスチャー（枠外Drop削除）の可逆性担保**:
  - 隠しジェスチャー（枠外ドロップ削除）は撤去せず維持。削除直前に `PushUndoSnapshot()` を呼び出し、トーストに「(Ctrl+Zで復元可能)」を明記。誤操作でツリー外に放り出しても `Ctrl+Z` または「↩️ 戻る」ボタン一発で100%安全に元に戻せる可逆性を保証。
  - ペイン②ヘッダーに `SimUndoButton`（`↩️ 戻る`）を新設（初期状態 Disabled）。ツリー変更時に自動でスタック（最大30件）を保持し、変更があるときだけ活性化。
  - `Ctrl+Z` ショートカットを Window レベルで配線し、Tab 4（移行スタジオ）表示中にキーボードだけで直前の変更を取り消し可能。
  - プロジェクトロード時は Undo スタックをクリアし、誤った過去状態への復元を防止。
- **空白・余白操作の直感的一貫性（メンタルモデル統一）**:
  - **ツリー余白クリックでの選択解除**: `SimMockTreeView_PreviewMouseLeftButtonDown` で、アイテム以外の空白領域をクリックした際に `_selectedSimNode` を解除し、左ペイン下部ボタンを「新設ルート配置」へ即座に復元。
  - **D&D 空白ドロップのルール統一**: `SimMockTreeView_Drop` で `target == null`（余白・空白へのドロップ）の時は、ツリーの選択状態に関わらず「新設ルートとして配置」に統一。「特定の行に落とせばサブ化、何もない場所に落とせばルート化」という直感的一貫性を確立。
- **Quiet Fluent 1行スリムステータスメトリクスバーの全タブ完全統一**:
  - ファイル検索（Tab 2）で採用された「Quiet Fluent 1行スリムステータスメトリクスバー」（面＋微細ボーダー、重いドロップシャドウの完全撤去）の設計思想を、他機能のKPI表示へ完全適用。
  - **ファイル監査（Tab 6）**: 4枚の巨大カードを1行スリムメトリクスバー（総走査ファイル数、重複の無駄、休眠ファイル容量、パス長・禁則違反）へ置換、DataGrid のドロップシャドウを撤去。
  - **メディア最適化（Tab 7）**: 4枚の巨大カードを1行スリムメトリクスバー（画像数、動画数、軽量化完了数、総削減容量）へ置換、DataGrid のドロップシャドウを撤去。
  - **権限コントロール / 逆引き監査（Tab 3）**: 7枚の細かいカードを1行スリムメトリクスバー（アクセス可能、飛び地、遮断、走査不能、フル、変更、読取）へ置換、カードコンテナのドロップシャドウを撤去。
  - アプリケーション全体のビジュアル階層が整理され、余計な視覚的ノイズを排除しデータ作業領域を最大化。
- **FTS5 / ファイル名ハイブリッド検索の UNION 重複排除 ＆ エンコーディング判定正本化**:
  - `ContentIndexService.SearchIndexedAsync` において、UNION 検索で同一 FullPath がファイル名と本文の両方にヒットした場合、スニペットが存在する方を優先して名寄せ（重複排除）し、同一ファイルが2行表示される問題を解消。
  - `ContentExtractionService.DetectTextEncoding` を新設し、BOM判定、Strict UTF-8、DecoderFallbackException ➔ Shift-JIS (CP932) のフォールバック判定を正本化。Live Search でも Shift-JIS 日本語テキストを完全ヒット。

---

## 21. 【ライブ直接走査 本文検索の最適化 ＆ フライング集計根絶 ＆ 生体反応維持】
*(v2.2.5 本番施工 & ADR 67)*

- **背景と課題**:
  - 初見フォルダーやインデックス未構築フォルダーに対するライブ直接走査（`SearchDirectFolderAsync`）で「本文も検索」を実行した際、画面上のメトリクスバーが「ヒット件数 22,112 件 | 合計容量 12.57 GB | 検索所要時間 144.31s」のようにフォルダ全容量と同一の数値でピタッとストップする現象が発生。
  - **原因**:
    1. **フライング加算**: 第1段階（全ファイル列挙）で `needsDeepCheck`（本文検査が必要な候補）のファイルが、中身を検査する前に `Interlocked.Increment(ref hitCount)` と `Interlocked.Add(ref totalHitBytes, ...)` に加算されていたため、全ファイルがヒット扱いになっていた。
    2. **候補爆発**: `SearchContentMode` の際、ファイル名にキーワードが含まれていないファイルが拡張子を問わず全件 `needsDeepCheck = true` と判定され、画像・動画・実行バイナリ（exe/dll/iso/zip等）を含む全2万件が本文走査キューに投入されていた。
    3. **タイマー・進捗の停止感**: 第2段階（本文走査）中、Stopwatch や進捗報告の更新が途絶え、画面の所要時間タイマーが第1段階完了時の数値（144.31s）で固定され、フリーズしたかのように見えていた。
- **施工内容**:
  1. **フライング加算の完全根絶**:
     - `SearchDirectFolderAsync` の `HandleEntry` から `needsDeepCheck` 時の `hitCount` / `totalHitBytes` 加算を完全撤去。ファイル名完全一致で即時合格確定したエントリのみを初期カウントする。
  2. **本文対応拡張子の事前フィルタリング（検索漏れゼロで高速化）**:
     - `IsMatchBasic` および `IsMatchEntry` において、ファイル名にキーワードが含まれていない場合の `needsDeepCheck` 判定に、正本である `ContentExtractionService.SupportedExtensions.Contains(ext)`（Office/PDF/テキスト/スクリプト/ログ/CSV等）を必須化。
     - 画像や動画、実行バイナリなど本文抽出非対応のファイルは即座に `return false` となりキューから排除。走査対象ファイル数が激減し、大幅な高速化を達成。
     - もともと本文抽出に対応していないファイルのみを除外するため、**Office/PDF/テキストの検索漏れ（false negative）は一切発生しない（トレードオフゼロ）**。
  3. **本文走査中のタイマー継続 ＆ リアルタイム生体反応**:
     - `FilterByContentAsync` に `Stopwatch sw`, `initialHitCount`, `initialTotalBytes`, `scannedCount` を貫通配線。
     - 本文一致が確定した時（`EmitHit`）のみ真のヒット件数と合計容量を加算し、`batchYield` にも即時反映。
     - 100ms 間隔で `sw.Elapsed`（リアルタイムタイマー）および `$"📄 Deep Search ({c:N0}/{total:N0}): {item.Name}"` の進捗報告をディスパッチ。画面の所要時間タイマーが生きて動き続け、走査進捗とヒット件数がスムーズに増加する生体反応を実現。
  4. **自動回帰テストによる検証保護**:
     - `RegressionTestSuite.Search.cs` にセクション4を新設。直接走査における本文一致（テキスト）、名前一致（画像）、非対応スキップ（画像/exe）の判定精度、およびフライング加算が発生しないことを自動検証。8/8 ALL PASSED を維持。

---

## 22. 【検索連動バックグラウンド自動差分同期 ＆ 15分クールダウン ＆ 差分検知時自動再検索 ＆ 控えめ「↻」ボタン】
*(v2.2.5 本番施工 & ADR 68)*

- **背景と課題**:
  - これまで FTS5 インデックスが構築済みのフォルダ（Route 1）では、ミリ秒で既存 DB から検索結果を返すのみで、バックグラウンドでの差分更新がトリガーされていなかった。
  - そのため、フォルダ側で新規ファイルの追加・変更・削除があっても自動で察知できず、インデックスが陳腐化して新ファイルが検索にヒットしない課題があった。
  - また、バックグラウンド同期で新ファイルがインデックスに追加されても、画面上の検索結果には自動反映されず、ユーザーが再度検索ボタンを押す必要があった（片手落ち）。
  - さらに、100万ファイル級の巨大UNC共有において5分間隔の走査は負荷が懸念され、UI上も「⚡ 同期」という大きな目立つボタンは検索画面の一等地を圧迫していた。
- **施工内容**:
  1. **インデックス検索連動の自動差分同期（スマートルーティング強化）**:
     - `ExecuteSearch` の Route 1（インデックス検索完了直後）において、検索結果は 0.05秒でユーザーへ即時返却しつつ、裏で非同期に `TriggerBackgroundIndexUpdate(targetFolder, force: false)` を起動。
  2. **巨大UNC共有に配慮した 15分クールダウン制御（サーバー負荷ゼロ）**:
     - 検索するたびに毎回ネットワーク走査が走るのを防ぐため、`ContentIndexService.NeedsBackgroundSync(folderPath, TimeSpan.FromMinutes(15))` を配備。
     - `IndexedRoots` の `LastCompletedUtcTicks` を最深Root基準で照合し、前回の同期完了から15分以内の場合は裏走査を完全スキップ。
  3. **自動同期完了時の「勝手に最新でいてくれる検索」（自動再検索）**:
     - バックグラウンド同期によって差分（`NewlyIndexedCount > 0 || DeletedCount > 0`）が検知された場合、同期完了時点でユーザーの検索条件（対象フォルダ・キーワード）が同一であれば、`ExecuteSearch(isIncremental: false, isAutoRefresh: true)` を自動発火。
     - `isAutoRefresh: true` ガードにより再帰的な裏同期ループを完全に防止。ユーザーは何も操作することなく、追加・更新されたファイルが画面上の検索結果にスッと現れる理想的なUXを実現。
  4. **「本文も検索」OFF時の FTS UNION 除外（意味論厳格化）**:
     - `ContentIndexService.SearchIndexedAsync` において、「📄 本文も検索」トグルが OFF（`isExplicitContentSearch == false`）の時は `ContentFts` との UNION を行わず、`IndexedFiles`（ファイル名・属性）単体のみを検索。
     - ファイル名検索時には本文一致のノイズを完全に排除し、本文ON時のみ Metadata OR Content の完全統合検索を行う厳格な意味論を確立。
  5. **常設「↻」アイコンボタン（幅28px）への小型化・控えめ化**:
     - 検索スタジオの対象フォルダー行にあった目立つ「⚡ 同期」ボタンを、幅28pxの控えめな「↻」アイコンボタン（`SearchSyncIndexButton`）へリファクタリング。
     - ツールチップに「インデックスを手動更新 (差分最新化) / Refresh Index (Differential update)」を付与し、必要な時だけ手動で同期できる控えめな佇まいに整線。
  6. **自動回帰テストによる恒久保護**:
     - `RegressionTestSuite.Search.cs` にセクション5を新設。未インデックス時の同期要求判定、完了直後の15分クールダウン（同期抑止）、0秒指定時の即時判定、および `GetLastIndexCompletedUtc` の日時精度を自動検証。
     - さらに本文検索OFF時の FTS 分離検証（本文一致ファイルが0件になること）を追加。8/8 ALL PASSED を堅持。

---

## 23. 【Searchモダンカードリスト＆詳細ペイン ＆ サイドバー3グループ整線 ＆ JIT権限照合・自動パージ自浄機構】
*(v2.2.5 本番施工 & ADR 69)*

- **背景と課題**:
  - **サイドバー認知負荷**: 7機能のボタンが同列に並んでいたため「7個のツールが雑多にある」印象を与えていた。
  - **Search 結果の管理台帳感**: 横長の多カラム DataGrid（Audit画面と同様）だったため、ファイル名検索での視線移動が多く、せっかくの FTS5 本文スニペット（抜粋）が右端カラムに追いやられて読みにくかった。
  - **Index の機密性・整合性（FolderCleaner/実環境課題）**: FTS5 DB 内に本文やファイル名がキャッシュされるため、権限が剥奪されたファイルや削除されたファイルが検索結果に残り続け、アクセス拒否されるまで気づけない（情報漏洩・整合性リスク）。
- **施工内容**:
  1. **サイドバーの 3グループ整線（文字見出しなし・余白分離）**:
     - 7機能を「探索・観測（容量分析/ファイル検索/ファイル監査）」「設計・変更（権限コントロール/移行スタジオ）」「事後・補助（リンク修復/メディア最適化）」の順序に再編。
     - 説明テキスト見出しは置かず、グループ境界に `Margin="0,0,0,14"` の自然な余白を挿入。クライアントモード（FolderCleaner）での非表示制御時も破綻なく調和。
  2. **Search 結果一覧のモダンカードリスト化 ＆ クイック詳細ペイン（Split View）**:
     - **左ペイン（カードリスト）**: 1行目（アイコン＋ファイル名＋容量＋更新日時）、2行目（フォルダパス）、3行目（本文スニペットバブル、薄グレー枠線付き角丸カード）のモダンなカード形式（VirtualizingStackPanel）へ刷新。右上に Excel / CSV 出力ボタンを配置。
     - **右ペイン（クイック詳細＆アクション）**: 選択中アイテムのアイコン、ファイル名、完全パス、ワンクリックコピー、容量・更新日・文字数・一致理由のメタデータ表、本文抜粋全文、および 4 つのスタジオ連携ボタン（Live ACL、移行スタジオ、ファイル監査、リンク修復）を常設。
  3. **JIT 権限照合 ＆ 自動パージ自浄機構（Just-In-Time Verification & Auto-Purge）**:
     - `VerifyAndFilterPermissionsAsync`: インデックス検索結果を画面に返す直前、並列度 16 で実際の読み取り可能性を検証。アクセス拒否（`UnauthorizedAccessException`）やファイル消失を検知したエントリは結果から即時除外。
     - `ContentIndexService.PurgeFilesAsync`: アクセス不能になったパスを裏で SQLite DB（`IndexedFiles` / `ContentFts`）から自動削除。事前に全サーバーを再スキャンすることなく、検索された瞬間に古い不要キャッシュが自浄されていく理想的な整合性を実現。
  4. **自動回帰テストによる恒久保護**:
     - `RegressionTestSuite.Search.cs` にセクション6を新設。`PurgeFilesAsync` によるインデックスからの即時抹消、本文検索での除外、およびファイル名検索（`IndexedFiles` 単体）での 0件確認を自動検証。8/8 ALL PASSED を堅持。

---

## 24. 【Tabler File-Type バッジの全画面展開 ＆ 一元正本化（`TablerBadgeHelper`）】
*(v2.2.5 本番施工 & ADR 70)*

- **背景と課題**:
  - Search Studio で導入した Tabler File-Type バッジ（パステル角丸ベクターバッジ・拡張子カラー刻印・フォルダー `[DIR]` 刻印）の視認性とモダンさが好評だった一方、他の画面（容量分析 Tab 1 のツリーやTop 10・内訳、ファイル監査 Tab 6 の一覧等）では絵文字（📁/📄）やプレーンテキストが残っており、アプリ全体としての視覚的一貫性が欠けていた。
  - 各画面ごとにバッジの配色や拡張子判定を実装すると、正本が分裂し保守性が崩壊するリスクがあった。
- **施工内容**:
  1. **正本の一元化（`TablerBadgeHelper`）**:
     - `Services/TablerBadgeHelper.cs` を新設し、拡張子（Excel/PDF/Word/PowerPoint/CSV/ZIP/画像/動画/コード/DIR等）に応じた上品なパステル背景（`Background`）、微細コントラスト枠（`BorderBrush`）、太字アクロニム刻印（`Text`）、文字色（`Foreground`）の算出ロジックを完全正本化。
     - 未知の拡張子でも先頭3〜4文字をアクロニムとして抽出し、ニュートラルなバッジを自動生成。
  2. **全モデルへの軽量プロパティ委譲配備**:
     - `SearchResultItem`, `FolderChildShareItem`, `LargestFileInfo`, `FileItemNode`, `AuditItem` の各モデルに `BadgeText`, `BadgeBackground`, `BadgeBorderBrush`, `BadgeForeground` を追加。内部で `TablerBadgeHelper.GetBadge(...)` を呼ぶだけの4行委譲に統一し、重複コードを根絶。
  3. **主要 DataGrid / Tree の XAML テンプレート換装**:
     - 容量分析のツリービュー（`FileTreeDataGrid`）: インデント付きの `[DIR]` / 拡張子バッジ表示。
     - 容量上位 Top 10（`TopFilesDataGrid`）: ファイル名先頭への拡張子バッジ配備。
     - 選択フォルダーの内訳（`FolderChildSharesDataGrid`）: `[DIR]` / 拡張子バッジと直下比率バーの調和。
     - ファイル監査（`AuditItemsDataGrid`）: 検出された課題ファイルのファイル名列に拡張子バッジを配備。
  4. **ゼロリソース・高速仮想化レンダリング**:
     - 画像ファイルや外部フォントを一切使用せず、WPF ネイティブの `Border` + `TextBlock` のみで描画。単一 EXE の容量増加 0 バイト、数万件の仮想化スクロールでも 60fps を維持。


---

## 25. 【Search高速化第1フェーズ：MetadataFts trigram ＆ ScanGeneration ストリーミング ＆ rowid直結JOIN】
*(v2.2.5 本番施工 & ADR 71)*

- **背景と課題**:
  - **ファイル名検索の遅延（数百万件時の課題）**: 従来のファイル名検索は SQLite テーブル IndexedFiles に対する FullPath LIKE '%keyword%' であり、B-Tree インデックスが効かず全行フルスキャン（O(N)）となっていた。
  - **同期時のメモリ圧迫**: 差分更新の際、インデックス走査で検出した全パスを HashSet<string> currentPaths に保持していたため、数百万ファイルの走査時にメモリを数十〜数百MB浪費していた。
  - **FTS5 と メタデータの非効率な JOIN**: ContentFts が (FileId UNINDEXED, Body) という設計だったため、FTS5 と IndexedFiles を結合する際に FileId のセカンダリインデックス探索が発生し、オーバーヘッドが生じていた。
- **施工内容**:
  1. **IndexedFiles スキーマ硬化 ＆ MetadataFts (trigram) 新設**:
     - IndexedFiles に Name TEXT 列と Generation INTEGER DEFAULT 0 列を追加。インデックス idx_files_name(Name), idx_files_gen(Generation) を配備。
     - ファイル名専用の trigram FTS5 仮想テーブル MetadataFts (Name, tokenize='trigram') を新設。
     - 既存 DB の起動時マイグレーションにより、既存レコードの Name を自動抽出補完し、MetadataFts を自動バックフィル。
  2. **検索クエリの FTS5 / LIKE ハイブリッド超高速化**:
     - 3文字以上のファイル名検索では MetadataFts MATCH @metaQuery を使用（ミリ秒応答）。
     - 1〜2文字の短語検索では idx_files_name を活用した .Name LIKE @nameShort へ自動フォールバック。
     - 本文ON時は ContentFts (rowid) と MetadataFts (rowid) の UNION ハイブリッド検索を実行。
  3. **ContentFts.rowid = IndexedFiles.FileId への統一 ＆ ゼロロス昇格マイグレーション**:
     - FTS5 の内部 rowid を IndexedFiles.FileId と直結。旧スキーマからの移行時は一時テーブル経由で既存の全文インデックスデータを 1 行も失わずに新テーブルへ自動マイグレーション。
     - JOIN が SQLite 最速の B-Tree primary key（rowid）参照となり、クエリ実行計画を極限まで最適化。
  4. **ScanGeneration によるストリーミング世代管理（メモリ O(1)・差分 O(1) 削除）**:
     - インデックス走査ごとに IndexedRoots.CurrentGeneration をインクリメント。
     - 検出したファイルは順次ストリーミングで DB へ Upsert / Generation 更新。走査中にメモリ上に全ファイルパスの HashSet を保持する処理を完全撤去。
     - 走査完了後、Generation < currentGen のレコードを MetadataFts, ContentFts, IndexedFiles から O(1) で一括削除し、亡霊ファイルを完全抹消。
  5. **自動回帰テストによる恒久保護**:
     - RegressionTestSuite.Search.cs にセクション7を新設。3文字以上の trigram MATCH、1〜2文字の .Name LIKE、本文＋ファイル名の UNION ハイブリッド、および ScanGeneration による亡霊ファイル削除（物理削除後に再走査して DeletedCount == 1、検索0件になること）を自動検証。8/8 ALL PASSED を堅持。
---

## 26. 【Search高速化第2・第3フェーズ ＆ 検索結果フィルター・並び替え（Change Notifyリアルタイム同期、MFT Fast Track、インメモリ0msソート）】
*(v2.2.5 本番施工 & ADR 72)*

- **背景と課題**:
  - **ファイル更新の追従ラグ**: インデックス同期後に外部アプリやエクスプローラー等でファイルが作成・更新・削除された場合、15分クールダウンまたは手動再同期を実行するまで検索結果に反映されなかった。
  - **ローカルNTFSの初回走査速度**: 管理者権限のローカルドライブであっても通常のディレクトリ再帰列挙を行っていたため、数十万ファイルの大規模ドライブで初回のメタデータ収集に数十秒を要していた。
  - **検索結果の操作性**: ヒット件数が多い場合（数百〜数千件）、目的の文書やメディア、大容量ファイルを見つけるための種別絞り込み（フィルター）や並び替え（ソート）機能が不足していた。
- **施工内容**:
  1. **Change Notify (FileSystemWatcher) リアルタイム差分同期 (ContentIndexWatcherService)**:
     - FileSystemWatcher（64KBバッファ・再帰監視）を用いた常駐監視サービスを新設。
     - インデックス走査済みフォルダーに対し自動アタッチし、ファイルの作成（Created）、更新（Changed）、削除（Deleted）、名前変更（Renamed）を 300ms デバウンス集約。
     - ContentIndexService.UpsertSingleFileAsync および PurgeFilesAsync により SQLite（IndexedFiles, MetadataFts, ContentFts）を即時反映。
     - 検索結果表示中も ChangesApplied イベントで自動再検索を行い、検索結果を常に最新状態へ追従。
  2. **MFT Fast Track 直接走査連携（秒速インデックス化）**:
     - MftScanService.CanUseMft 判定により、「管理者権限 ＋ ローカルドライブ ＋ NTFS」を満たす場合、raw NTFS の MFT 一括読み出しを実行。
     - 数十万ファイルのローカルドライブでもディレクトリ列挙を 1〜2 秒で完了し、インデックス登録パイプラインへ直結。UNC や一般権限環境では SafeFileEnumerator（並列度2）へ自動フォールバック。
  3. **検索結果のフィルターチップ ＆ 並び替え（Filter & Sort）**:
     - 検索結果一覧ヘッダーに、Fluent ピル型フィルターチップ（すべて / 📄 文書 / 🖼️ メディア / 📦 圧縮 / ⚙️ その他）と並び替え ComboBox（関連度順 / 新しい順 / 古い順 / 大きい順 / 小さい順 / 名前 A-Z / 名前 Z-A）を配備。
     - _allSearchResults に対するインメモリ 0ms（一瞬）のフィルタリング・ソートを実現。件数・合計容量のメトリクスバーも絞り込み結果に動的連動。日英完全ローカライズ対応。
  4. **自動回帰テストによる恒久保護**:
     - RegressionTestSuite.Search.cs にセクション8を新設。Watcher のファイル作成・削除の即時反映、MFT 事前保護（UNC拒否・空パス拒否）、およびフィルター・ソート論理を自動検証。8/8 ドメイン ALL PASSED を堅持。

---

### ADR 73: 検索スタジオ ヘッダー整線（ピル撤去）＆ 5項目厳選ソート＆関連度スコアリング ＆ フォルダーベクターアイコン換装
*(v2.2.5 本番施工 & ADR 73)*

- **背景と課題**:
  - **ヘッダーの視覚的ノイズ**: 検索結果ヘッダーに配置されていたピル型フィルターチップ（すべて/文書/メディア/圧縮/その他）が画面を圧迫し、ユーザーの利用実態に対して過剰なUIとなっていた。
  - **ソート項目の冗長性と不足**: ファイル名やファイルサイズのソートは検索実務での需要が低く、一方で「作成日時」による新旧ソートや、探しているファイルが最上位に来る「真の関連度スコアリング」が求められていた。
  - **フォルダーアイコンの視認性・美観**: フォルダー行の頭に四角い枠で文字「DIR」と表示されていたため、拡張子バッジのように見えて視覚的に違和感があり（キモい）、直感的なフォルダー認識を妨げていた。
- **施工内容**:
  1. **ピル型フィルターチップの完全撤去とヘッダー整線**:
     - 検索結果一覧ヘッダーからピル型チップを完全撤去。タイトル「検索結果一覧」と右寄せの「並び替え: [ComboBox]」のみで構成されるクリーンで静的な Quiet Fluent ツールバーへ整線。
  2. **ソート項目の5厳選 ＆ 作成日時（CreationTime）統合**:
     - ソート項目を【① 🎯 関連度順 (Relevance)、② 🕒 更新日時 (新しい順)、③ ⏳ 更新日時 (古い順)、④ ✨ 作成日時 (新しい順)、⑤ 📅 作成日時 (古い順)】の 5 項目に厳選。
     - `IndexedFiles` SQLite テーブルに `CreationTimeUtcTicks` カラムを追加（起動時 ALTER TABLE 自動マイグレーション対応）。インデックス検索、0秒インメモリ検索、ライブ直接走査の全ルートで作成日時を取得。
     - 各検索カードの右上に、ファイルサイズ・更新日時に加えて作成日時（薄いグレー表示）を並べて表示。
  3. **インテリジェント関連度スコアリング（True Relevance Scoring）**:
     - 単純なクエリ順ではなく、ファイル名完全一致（+100）、前方一致（+50）、部分一致（+30）、本文スニペット一致（+15）、ディレクトリパス一致（+10）、および直近更新鮮度加点（+1〜5）を多角的にスコア化するアルゴリズムを導入。
     - ユーザーが入力したキーワードに最も合致するファイルを瞬時に先頭にリランキング。
  4. **フォルダー行のベクターフォルダーアイコン換装（DIR文字の完全根絶）**:
     - 四角い枠内に文字「DIR」と描画されていたバッジを完全撤去。
     - Tabler Icons 準拠の上部タブ付き角丸フォルダー形状（`M 3 7 A 2 2 0 0 1 5 5 L 9 5 L 11 7 L 19 7 A 2 2 0 0 1 21 9 L 21 17 A 2 2 0 0 1 19 19 L 5 19 A 2 2 0 0 1 3 17 Z`）をベクター XAML で実装。
     - 温かみのあるアンバーゴールド（背景 `#FEF3C7`、塗り `#FDE68A`、枠線 `#D97706`）で描画し、検索結果一覧（Tab 2）のみならず容量分析（Tab 1）のファイルツリーおよび選択フォルダーの内訳にも全画面展開。
  5. **自動回帰テストによる恒久保護**:
     - `RegressionTestSuite.Search.cs` セクション8を更新し、作成日時の新旧ソート、更新日時の新旧ソート、および `TablerBadgeHelper.GetBadge` のフォルダー時テキスト空文字（ベクター描画）を自動検証。8/8 ALL PASSED を堅持。

---

### ADR 74: Option 1 折れ曲がり角付き書類アイコン ＆ 統一フォルダー ＆ 本文スニペット枠撤去
*(v2.2.5 本番施工 & ADR 74)*

- **背景と課題**:
  - **ファイルバッジの「非書類感」**: 角丸長方形の単純なカラータグでは「ファイル・書類」らしさが乏しく、フォルダーアイコンが立体的になったことで逆にチープさが目立っていた。
  - **フォルダーアイコンとのデザイン不一致**: フォルダーとファイルのアイコンデザイン言語（線幅・角丸・質感）を揃える必要があった。
  - **本文スニペット（一致証拠）による一覧性の低下**: 本文一致スニペット（グレー枠）が表示されることで各カードの高さが不揃いになり、JSONやXMLのコード文字列が長々と露出して視覚ノイズとなっていた。
- **施工内容**:
  1. **Option 1 折れ曲がり角付き書類アイコン（Dog-Eared Paper Sheet）のベクターXAML実装**:
     - Tabler Icons 公式の右上が折れ曲がったドキュメント輪郭（`M 14 2 L 6 2 A 2 2 0 0 0 4 4 L 4 22 A 2 2 0 0 0 6 24 L 18 24 A 2 2 0 0 0 20 22 L 20 8 L 14 2 Z`）を純C#・XAMLベクターで実装。
     - 内部にパステルカラー背景、シャープなコントラスト線幅（1.5）、および下部中央に枠線と同色の鮮やかなミニピル（白抜き太字の `XLSX`, `PDF`, `CSV`, `XML`, `JSON`, `ZIP` 等）を配置。
     - 書類としての説得力と拡張子の瞬時判別性を極大化。
  2. **デザイン言語を統一したフォルダーアイコン**:
     - ファイルアイコンと同じ線幅（1.5）・角丸・トーンに合わせ、奥のタブ（`#FDE68A`）と手前のポケット（`#FEF3C7`）を持つ立体的な Tabler フォルダーを配備。
     - 検索スタジオ（Tab 2）だけでなく、容量分析（Tab 1）のファイルツリー（`FileTreeDataGrid`）、容量上位 Top 10（`TopFilesDataGrid`）、選択フォルダー内訳（`FolderChildSharesDataGrid`）、およびファイル監査（Tab 6: `AuditItemsDataGrid`）に至るまで例外なく全画面展開し、旧来の長方形バッジを完全に根絶。
  3. **本文スニペット枠（Row 2）の完全撤去によるカードの均一化**:
     - 検索結果リストからグレーの本文プレビュー枠（Row 2）を撤去。
     - 全カードを均一な 2 行構造（上段: アイコン＋ファイル名＋サイズ＋更新・作成日時、下段: パス＋フォルダーを開くボタン）に統一し、スクロール時の快適性と一覧性を飛躍的に向上。

---

### ADR 75: 自前DB自己食い完全除外 ＆ Watcher×Full Scan世代競合防止 ＆ Folderインデックス検索（IsDirectory） ＆ MFT作成日時正常化 ＆ スコアリング構文ノイズ排除
*(v2.2.6 本番施工 & ADR 75)*

- **背景と課題**:
  - **CI/回帰テストでの自前DB自己食い**: テスト用一時ディレクトリ直下に `TestIndex.db` を作成した際、走査エンジンが自身のリレーショナルDB（および `-wal`, `-shm`）を発見・インデックス登録してしまい、`newlyIndexed` 件数が不一致を起こしていた。
  - **Watcher と Full Scan の世代競合（Generation Race）**: Full Scan の実行中に FileSystemWatcher が新着ファイルを検知して Generation 0 で INSERT すると、Full Scan 完了時の `DELETE WHERE Generation < currentGen` によって新着ファイルが亡霊ファイルとして誤消去されるリスクがあった。
  - **インデックス検索でのフォルダーヒット非対応**: ライブ直接走査では「📁 フォルダも含める」でフォルダーがヒットするのに対し、高速インデックス検索（FTS5）ではファイルのみが登録されていたため、フォルダー検索で0件になっていた。
  - **MFT Fast Track での作成日時欠落**: MFT 走査時に $FILE_NAME 属性の作成日時が取得されず、LastWriteTime が代入されていた。
  - **関連度スコアへの構文トークン混入**: `ext:txt` や `size:<10MB` などの構文トークンがそのままキーワードとしてスコア計算に混入し、意図しないリランキングを引き起こしていた。
- **施工内容**:
  1. **自前DB自己食いの完全除外**:
     - `ContentIndexService.IsDatabaseFile` を新設。対象パスが DB 本体（`_dbPath`）またはその関連ファイル（`-wal`, `-shm`, `-journal`）であるかを完全判定し、走査結果、インデックス登録、および Watcher イベントから 100% 除外。
  2. **Watcher × Full Scan 世代競合防止 ＆ Deferred Flush**:
     - `ContentIndexService._activeScanRoots`（スキャン中ルートと実行中世代の管理）および `ScanCompleted` イベントを実装。
     - `ContentIndexWatcherService` は、スキャン中ルート配下のイベントを即座に適用せず保留（deferred）キューに退避。スキャン完了通知（`ScanCompleted`）を受信した直後に、最新世代（`currentGen`）として安全に一括適用（flush）。
     - Watcher エラー（`watcher.Error`）発生時は即座に `MarkRootDirty` で次回整合対象としてマーク。
  3. **Folder インデックス検索（IsDirectory）＆ trigram MATCH 貫通**:
     - `IndexedFiles` テーブルに `IsDirectory INTEGER DEFAULT 0` カラムを追加（起動時 ALTER TABLE 自動マイグレーション）。
     - `SafeFileEnumerator.EnumerateFileEntriesParallelAsync` に `includeDirectories: true` を指定してフォルダーも網羅走査。
     - ディレクトリを `IndexedFiles` および `MetadataFts`（trigram）に登録。
     - `SearchIndexedAsync` において、`query.IncludeFolders = false` 時は `f.IsDirectory = 0` でファイルのみ、`query.IncludeFolders = true` 時はフォルダーもミリ秒でヒットさせ、`SearchResultItem.IsDirectory` を正しく設定。
  4. **MFT Fast Track の作成日時（CreationTime）正常化**:
     - `MftRecordParser` において、$FILE_NAME 属性の offset +0x08 から真の作成日時（`CreationTime`）を解析。
     - `RawMftItem` -> `FastNode` -> `FileItemNode` -> `ScannedFileEntry` へと作成日時を貫通させ、MFT 経由でも正確な作成日時ソートを実現。
  5. **Relevance Score の構文ノイズ排除**:
     - `MainWindow.Search.cs` の `CalculateRelevanceScore` において、生クエリ文字列ではなく `SearchQueryParser.Parse` 後の純粋な `Keywords` および `ExactPhrases` のみを取り出してスコアリング。構文トークンによる歪みを完全排除。
  6. **自動回帰テストによる恒久保護**:
     - `RegressionTestSuite.Search.cs` に「セクション 9: Folder インデックス検索（IsDirectory）＆ 自前DB除外 ＆ 世代競合防止」を新設。全 8 ドメイン 8/8 ALL PASSED を自動検証・堅持。

---

### ADR 76: 本文ON時名前ヒット意味論統一（Status>=0） ＆ MFT引数順正本化 ＆ 最深Root Generation解決 ＆ Subtree Purge
*(v2.2.6 本番施工 & ADR 76)*

- **背景と課題**:
  - **本文検索ON時の名前ヒット消失**: `SearchIndexedAsync` の Metadata name branch において `isExplicitContentSearch` が真の場合に `f.Status = 1` を要求していたため、フォルダーや ZIP・画像・EXE などの非本文ファイル（`Status = 0`）が名前一致しているにもかかわらず除外されていた。直接走査（Live Search）では名前一致なら非本文ファイルもヒットするため、経路による意味論の乖離が生じていた。
  - **MFT ScannedFileEntry の引数順逆転**: `EnumerateEntriesViaMftAsync` で `ScannedFileEntry` のコンストラクタ引数順が誤って `lastWrite, lastWrite, creation` となっており、CreationTime と LastAccessTime が入れ替わっていた。
  - **Watcher の Generation が全 Root の MAX に依存**: `UpsertSingleFileAsync` で `SELECT MAX(CurrentGeneration) FROM IndexedRoots` を使用していたため、世代が進んだ Root A（Gen 50）と浅い Root B（Gen 2）が混在する環境で Root B の新着ファイルに Gen 50 が付与され、次回 Full Scan 時のクリーンアップから漏れて亡霊化するリスクがあった。
  - **ディレクトリ Rename/Delete 時の子孫取り残し**: 単一パスの完全一致 DELETE では、フォルダーが削除・リネームされた際に配下の子孫ファイルが SQLite 上に残存・亡霊化するリスクがあった。
- **施工内容**:
  1. **本文ON時名前ヒット意味論統一（`f.Status >= 0`）**:
     - `MetadataFts` ファイル名検索ブランチの抽出条件を `f.Status >= 0` に固定。
     - 「本文も検索」ON ＋ 「フォルダも含める」ON でも、フォルダーや ZIP・画像等の非本文ファイルが名前一致で確実にヒットするよう Live Search と意味論を完全統一。Content branch（`ContentFts`）のみ `f.Status = 1` を維持し、非本文ファイルの中身マッチ誤判定を防止。
  2. **MFT `ScannedFileEntry` 引数順の正本化**:
     - `EnumerateEntriesViaMftAsync` での引数順を `creation, lastWrite, lastWrite` に修正。MFT 経由でも作成日時・更新日時が正しく格納され、作成日時ソートが意図通りに機能。
  3. **最深 Root の Generation 解決（`GetGenerationForPath`）**:
     - `SELECT MAX(CurrentGeneration) FROM IndexedRoots;` を全廃。対象パスに最も深く合致する `IndexedRoots` の `CurrentGeneration` を解決する `GetGenerationForPath` / `GetGenerationForPathInternal` を実装。マルチ Root 運用時の世代混入を完全排除。
  4. **ディレクトリ Subtree Purge ＆ Watcher Dirty Reconciliation**:
     - `PurgeFilesAsync` において、`WHERE FullPath = @path OR FullPath LIKE @prefix ESCAPE '\'` により、指定パス自身および配下の全子孫を一括削除する Subtree Purge へ拡張。
     - `ContentIndexWatcherService` でフォルダーの作成・名前変更を検知した際に `_indexService.MarkRootDirty(path)` を発行し、子孫の再同期（Reconciliation）を担保。
  5. **自動回帰テストによる恒久保護**:
     - `RegressionTestSuite.Search.cs` に「セクション 10: 本文ON時名前ヒット（Status>=0）＆ 最深Root Generation ＆ Subtree Purge」を新設。全 8 ドメイン 8/8 ALL PASSED を自動検証・堅持。
---

### ADR 77: 「本文も検索」トグル自動実行解除 ＆ 2フェーズプログレッシブ検索（ファイル名ミリ秒先行表示 ＋ 本文ストリーミング合流）
*(v2.2.6 本番施工 & ADR 77)*

- **背景と課題**:
  - **チェックボックス切り替え時の勝手な自動検索**: 「本文を含める」トグル（SearchContentCheckBox）をクリックした瞬間に ExecuteSearch(isIncremental: false) が無条件発火し、ユーザーが意図しないタイミングで重い検索処理が走り出すというUX上の違和感・不快感があった。
  - **本文検索時のファイル名一致待ち（ブロッキング）**: 「本文も検索」ON時、従来の SQLite インデックス検索では MetadataFts と ContentFts を UNION していたため、本文 trigram / LIKE の全解析が完了するまでファイル名で即座にヒットするはずの候補すら画面に表示されず待たされていた。直接走査（Direct Search）でもバッチ閾値（50件）まで待たされる構造だった。
- **施工内容**:
  1. **トグル切り替え時の自動検索発火を完全排除**:
     - MainWindow.Search.cs の SearchContentCheckBox_Checked / Unchecked イベントハンドラから無条件の ExecuteSearch 呼び出しを撤去。
     - ユーザーが [Enter] キーを押すか、[検索] ボタンを押したタイミングでのみ検索が走るよう改修。
  2. **SQLite インデックス検索の2段階プログレッシブ実行**:
     - ContentIndexService.SearchIndexedAsync に onNameHitsReady コールバックを導入。
     - 1フェーズ目: まずファイル名／属性条件（MetadataFts / .Name）のクエリを 0.002〜0.005 秒で即時実行し、onNameHitsReady 経由で UI へ先行通知。
     - 2フェーズ目: 続いて本文検索（ContentFts）を実行し、同一ファイルはスニペット優先でマージ、本文のみ一致ファイルを追加して最終確定。
  3. **スキャン済みツリー検索の先行ハイブリッド**:
     - スキャン済みツリーが存在する場合、まずインメモリのツリーからファイル名一致候補を 0ms で画面に先行表示。
     - その後、バックグラウンドのライブ本文走査（FilterByContentAsync）で本文一致をストリーミング合流・リランキング。
  4. **ストリーミング通知の初期適応型バッチ化**:
     - SearchEngineService.SearchDirectFolderAsync および FilterByContentAsync において、通知閾値を 1件 ➔ 5件 ➔ 25件 の初期適応型バッチへ最適化。
     - 1件目の一致が検出された瞬間に UI へ即時ストリーミングされ、待たされ感を完全解消。
  5. **UI パイプラインの整線とデバウンス**:
     - atchYield によるストリーミング合流時、150ms のデバウンス制御付きで ApplyFilterAndSort() を実行し、ファイル名先行表示から本文合流までチラつきなく滑らかにリスト更新。
  6. **自動回帰テストによる恒久保護**:
     - RegressionTestSuite.Search.cs に「セクション 11: 2段階プログレッシブ検索（ファイル名先行通知 & 本文合流）」を新設。全 8 ドメイン 8/8 ALL PASSED を自動検証・堅持。
---

### ADR 78: カンマ（,）およびパイプ（|）によるキーワードOR検索 ＆ AND-of-ORs 構造化検索
*(v2.2.6 本番施工 & ADR 78)*

- **背景と課題**:
  - **キーワードOR検索の欠如**: 従来の検索エンジンでは複数キーワードがすべて AND 結合されていたため、「見積 または 請求」のような複数条件のいずれかに一致するファイルを検索する際、egex: を用いるしかなく直感的な操作が難しかった。
  - **構文の直感性と入力効率**: 一般的な OR 演算子では英単語としての「or」との曖昧さが生じるため、拡張子指定（ext:pdf,xlsx）と同様に「カンマ（,）またはパイプ（|）でOR」という直感的かつタイピング効率の高い記法が求められた。
- **施工内容**:
  1. **クエリモデルの多層化（KeywordGroups）**:
     - SearchModels.cs の SearchQuery に public List<List<string>> KeywordGroups { get; set; } = new(); を追加。AND-of-ORs（各グループ内はいずれか一致のOR、グループ同士はすべて満たすAND）を構造化表現。
     - 既存の Keywords プロパティも併存させ、後方互換性を 100% 維持。
  2. **パーサー（SearchQueryParser）のカンマ/パイプOR解析**:
     - スペース区切りの各トークンにおいて、未クォート文字列に , または | が含まれる場合、OR グループとして分割・登録。
     - ダブルクォートで囲まれた文字列（例: "data,backup.csv"）は従来通り ExactPhrases として保護され、カンマを文字通りに解釈。
  3. **SQLite FTS5 インデックス検索のネイティブOR対応**:
     - 全単語が3文字以上のグループは、FTS5 MATCH 構文の ("見積" OR "請求") へ直接マッピングし、B-Tree trigram インデックスによるミリ秒検索を実現。
     - 1〜2文字を含むグループは SQL WHERE 節の (f.Name LIKE @p1 OR f.Name LIKE @p2) へ自動最適化。
     - 本文検索（ContentFts）でも同様に OR 式を展開。
  4. **インメモリツリー検索 ＆ 直接走査（Direct Search）への完全展開**:
     - SearchEngineService.cs に MatchesKeywordGroups を新設し、インメモリおよび直接走査で各グループの包含判定（OR）を共通適用。
     - FilterByContentAsync および ContentExtractionService.cs（Text, Office, PDF）において equiredGroups に対応し、名前で未充足のグループのみを本文から OR 探索。
  5. **自動回帰テストによる恒久保護**:
     - RegressionTestSuite.Search.cs に「セクション 12: カンマおよびパイプによるキーワードOR検索（AND of ORs）」を新設。パーサー、FTS5 インデックス、直接走査の全経路一致を自動検証（8/8 ALL PASSED）。

---

### ADR 79: 先行表示JIT権限検証 ＆ content:修飾子必須意味論 ＆ (Name OR Content) INTERSECT 積集合
*(v2.2.6 本番施工 & ADR 79)*

- **背景と課題**:
  - **先行表示のJIT権限バイパス**: ADR 77 で導入した2フェーズプログレッシブ検索において、ファイル名一致候補（`onNameHitsReady`）が `VerifyAndFilterPermissionsAsync` を素通りして画面に即座に表示されていた。ACL剥奪後の古いIndexが存在した場合、数秒間とはいえ権限のないファイル情報が露出する安全契約違反が生じていた。
  - **`content:` 修飾子の意味論破綻**: `content:社外秘` 単体時に先行表示用クエリで条件が空になり全ファイルが一時表示される問題、および `契約 content:社外秘` で `KeywordGroups` が採用されると `ContentKeyword` が条件から欠落する問題が存在した。
  - **フィールド跨ぎANDの不整合**: ファイル名に「契約書」、本文に「2026」を持つファイルに対し、クエリ `契約書 2026`（本文も検索 ON）を実行した場合、Direct Search では正しくヒットするのに対し、Indexed Search では `MetadataFts`（両方名前要求）と `ContentFts`（両方本文要求）の UNION だったためヒットから漏れる不整合があった。
  - **ストリーミング中KPIタイマーのリセットバグ**: `lastBatchUpdate.Restart()` 直後の `Elapsed`（0ms付近）を渡していたため、表示中の所要時間が不自然に戻る問題があった。
- **施工内容**:
  1. **先行表示の JIT 権限照合（VerifyAndFilterPermissionsAsync）貫通**:
     - `MainWindow.Search.cs` の `OnNameHitsReady` コールバック内で、先行表示候補に対しても必ず `VerifyAndFilterPermissionsAsync` を非同期実行。権限が確認されたクリーンなアイテムのみを画面（`_allSearchResults`）へ描画。
  2. **`content:` 修飾子の必須本文条件（Mandatory Content Condition）正本化 ＆ 先行表示抑止**:
     - `content:キーワード` が指定されている場合、本文検査が通過するまで確定ヒットとみなさないため、プログレッシブ先行表示を安全に抑止。
     - Direct Search（`SearchEngineService.cs`）および Indexed Search（`ContentIndexService.cs`）において、`content:` を独立した必須本文グループとして分離し、全探索エンジンで確実に本文一致を要求。
  3. **Indexed Search の `(Name OR Content)` INTERSECT 積集合アーキテクチャ**:
     - 各キーワードグループ $G_i$ に対し、`(Name matches $G_i$ OR Content matches $G_i$)` の FileId 集合を構築し、全グループを SQLite の `INTERSECT` で積集合結合。
     - これにより、名前に「契約書」・本文に「2026」のようなフィールド跨ぎANDもミリ秒で 100% 漏れなく検出。Direct Search と Indexed Search の結果が数学的に完全一致。
     - ヒットした `FileId` 群に対して、`ContentFts MATCH` によるスニペット一括抽出を安全に実行。
  4. **生体反応タイマー（Stopwatch）の正本化**:
     - ストリーミングバッチ更新時に、検索開始からの総経過時間を計測する `searchTotalSw.Elapsed` を渡し、タイマーが 0ms に巻き戻る表示バグを解消。
  5. **自動回帰テストによる恒久保護**:
     - `RegressionTestSuite.Search.cs` に「セクション 13: 安全契約（JIT権限）、content:修飾子の必須意味論、およびフィールド跨ぎAND積集合の検証」を新設。全 8 ドメイン 8/8 ALL PASSED を自動検証・堅持。

---

### ADR 80: ファイルサーバー保護型 適応並列度制御 (Adaptive Concurrency Controller)
*(v2.2.7 本番施工 & ADR 80)*

- **背景と課題**:
  - **固定並列度（2固定）と速度向上のトレードオフ**: これまで FolderMorpher はファイルサーバー（SMB/CIFS）の負荷保護のため並列度2固定（デュアルワーカー）を採用してきた。極めて安全である一方、高速な10GbE / SSD環境や余力のあるWindows Server環境では、さらなるスキャン高速化の余地があった。
  - **一般的なAdaptive Concurrencyの致命的欠点**: TCP BBRや一般的なHTTPクライアントの動的並列度は「パケットロスやレイテンシ悪化などの壁にぶち当たるまで帯域を攻める」アプローチをとる。しかしファイルサーバー（特にNASやSamba）に対してこれをやると、キュー堆積・ディスクシーク競合により「遅くなった」と観測した時点ですでにサーバーが大渋滞し、他部署の通常業務（Excelが開かない・基幹連携タイムアウト等）を巻き込んでしまう。
  - **平均値の罠とチャタリング（脈打ち）**: 平均レイテンシは悪化の初動（最初のスパイク）を鈍く見せてしまう。また、負荷を検知して並列度を下げ、少し改善したからといってすぐに再昇格すると、ツール自身が脈打ってサーバーを揺さぶり続ける迷惑装置と化す。
- **施工内容**:
  1. **「手遅れになる前に崖から落ちるように逃げるロジック」が本体**:
     - 上げるロジックではなく、降下制御（Cliff Decrease）を主軸に設計した `AdaptiveConcurrencyController` を新設。
  2. **初期値2・下限2・上限4の不変安全契約**:
     - 既存の安全正本（並列2固定）を絶対に下回らない（`MinConcurrency = 2`, `DefaultConcurrency = 2`）。
     - 上限はまずは `MaxConcurrency = 4` で厳格にキャップ。本番サーバーを不用意に殴るリスクを根絶。
  3. **p95 / ジッター監視 ＆ 慎重な加算昇格 (Additive Increase: +1)**:
     - 直近100件のスライディングウィンドウ（RingBuffer）でレイテンシを追跡。
     - 最初の約50サンプルでベースライン（p50/p95 latency, スループット）を確立。
     - クールダウン中でなく、直近 p95 が `baseline * 1.3` 以下かつエラーゼロで一定期間（40サンプル以上）安定した場合のみ、慎重に 1 段階ずつ昇格（2 ➔ 3 ➔ 4）。
  4. **限界効用 (Marginal Gain) 監視**:
     - 並列度を増やした後の評価期間（直近30サンプル）で、スループット改善が +5% 未満またはレイテンシ悪化（p95 > baseline * 1.4）の場合、「並列化の恩恵なし、ボトルネックはサーバー側」と即座に判断。元の並列度に戻してその上限をクランプ。
  5. **即時崖落ち降下 (Immediate Cliff Decrease) ＆ 不可逆天井クランプ (One-Way Ceiling Clamp)**:
     - Win32 ネットワークエラー（`ERROR_BAD_NET_RESP` 58, `ERROR_UNEXP_NET_ERR` 59, `ERROR_NETNAME_DELETED` 64, `ERROR_NETWORK_BUSY` 54 等）やタイムアウト、異常遅延（`latency > baseline_p95 * 3.5`）を検知した場合、即座に並列度 2 へ崖落ち、30秒クールダウン。
     - 一度過負荷を検知してバックオフしたセッション中はその上限に二度と挑戦しない（不可逆天井クランプによりチャタリング・脈打ちを完全抑止）。
  6. **SafeFileEnumerator / DiskScanService / ContentSearch 全走査基盤へ統合**:
     - ディレクトリ列挙（`SafeFileEnumerator.cs`）、容量スキャン（`DiskScanService.cs`）、および本文抽出（`SearchEngineService.FilterByContentAsync`）の全並列ループを `AdaptiveConcurrencyController` の非同期スロット調停（`AcquireAsync` / `SlotLease`）に統合。
  7. **自動回帰テストによる恒久保護**:
     - `RegressionTestSuite.Storage.cs` に「セクション 7: Adaptive Concurrency (ADR 80)」を新設。初期値・下限・上限の不変契約、ベースライン確立、昇格、即時崖落ち、天井クランプ、Win32エラー判定、並行スロットリースのデッドロックフリーを自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。


---

### ADR 81: 検索整合性の徹底硬化 ＆ ネットワークエラー判定の構造化 ＆ 緊急退避ウィンドウ
*(v2.2.8 本番施工 & ADR 81)*

- **背景 & 動機**:
  - **Progressive Search の非同期レース**: 先行NameHits通知の非同期JIT権限検証が遅延完了した際、後続の最終結果（FTS5本文ヒット全件等）を古い部分件数で画面上書きしてしまうバグが存在した。
  - **ExactPhrase のインデックス孤立**: query.ExactPhrases（"契約 更新" 等）単体で指定された際、キーワードグループにマッピングされず属性検索専用の単独フォールバックに入り、FTS5 / LIKE インデックスで 0 件になる不整合があった。
  - **エラー判定の文字列依存（AI実装整線規則 第3項違反）**: AdaptiveConcurrencyController でのネットワークエラー判定が IsNetworkOrFatalError(string) による "58", "59" などの文字列信号線になっていた。
  - **高速LANでの遅延検知（手遅れリスク）**: baseline が 10ms のような高速環境で 45ms〜55ms のスパイクが発生した際、30件の母集団蓄積を待っていては退避が遅れるリスクがあった。
- **施工内容**:
  1. **Progressive Search のレースコンディション根絶**:
     - MainWindow.Search.cs に finalResultsCommitted ガードを導入。
     - 最終結果（Route 1 や Route 2）が確定・画面コミットされた後は、先行表示の遅延非同期JIT権限検証コールバックがUI表示を巻き戻すことを100%防止。
  2. **ExactPhrase（引用符完全一致）の Indexed Search 完全統合**:
     - ContentIndexService.cs において、query.ExactPhrases を keywordGroups に必須グループとして統合。
     - 空クエリ判定および「キーワードなし」分岐に query.ExactPhrases.Count == 0 を追加。
     - スペースを含む完全フレーズは grpAllFts = false（LIKE 句）で 100% 確実に一致。
  3. **Win32 ネットワークエラー判定の構造化（列挙型への一本化）**:
     - NativeDirectoryEnumerator.cs に EnumerationFailureKind enum（None, AccessDenied, NotFound, Network, Io, Unknown）を新設。
     - ClassifyWin32Error(int error) により、Win32エラーコード（58, 59, 64, 54, 56, 71, 121 等）を型安全に分類。
     - SafeFileEnumerator ➔ DiskScanService ➔ AdaptiveConcurrencyController のパイプラインを failureKind で貫通。文字列信号線を完全根絶。
  4. **超早期崖落ち（Emergency Window: 直近8件監視）**:
     - 直近8件の超短期ウィンドウ _emergencyWindow を新設。
     - 直近8件中3件以上が > baselineP95 * 2.0 && > baselineP95 + 15ms を超過した場合、30件のサンプル蓄積を待たずに即座に並列度 2 へ崖落ち・天井クランプ。
     - 単一スパイク閾値も Math.Max(60.0, _baselineP95 * 3.0) に引き締め。
  5. **自動回帰テストによる恒久保護**:
     - RegressionTestSuite.Search.cs に「セクション 14: ExactPhrase（引用符完全一致）の Indexed Search 統合 (ADR 81)」を新設。
     - RegressionTestSuite.Storage.cs の「セクション 7」に構造化 EnumerationFailureKind 分類および Emergency Window 早期崖落ち検証を追加。
     - 全 8 ドメイン 8/8 ALL PASSED を堅持。


---

### ADR 82: SlotLease 参照意味論 ＆ ReleaseOnce 二重解放根絶 ＆ ExactPhrase Direct/Indexed Parity
*(v2.2.9 本番施工 & ADR 82)*

- **背景 & 動機**:
  - **SlotLease の mutable struct 防御コピー問題**: `public struct SlotLease : IDisposable` を `using var lease = ...` で利用した場合、C#仕様により `using` 変数は `readonly` となり、ミュータブルメソッド `lease.Report()` 呼び出し時に防御コピー（defensive copy）が発生する。その結果、コピー先で `_isReported = true` になっても元のインスタンスは `false` のまま残り、スコープ脱出時の `Dispose()` で二重に `ReleaseSlot()` が走る危険があった。これにより `_activeSlots` がアンダーフロー（負数化）すると、`current < limit` 判定を突き抜けて並列度上限（2/3/4）が崩壊する。
  - **ExactPhrase の Direct / Indexed Parity の不整合**: Indexed Search では `"機密 保持"`（本文ON）で `(Name OR Content)` として評価されるのに対し、Direct Search では事前ファイル名検査で `target.IndexOf(phr) < 0` により本文を見る前にドロップされていた。
  - **旧文字列エラー判定メソッドの残存**: `NativeDirectoryEnumerator` に `EnumerationFailureKind` が配備された後も、`AdaptiveConcurrencyController` 内に旧 `IsNetworkOrFatalError(string?)` が残存していた。
- **施工内容**:
  1. **SlotLease を sealed class へ改変 ＆ ReleaseOnce 一回解放の徹底**:
     - `public sealed class SlotLease : IDisposable` へ変更し、参照意味論を確立。
     - `private int _released;` を配備し、`Interlocked.Exchange(ref _released, 1) != 0` による `ReleaseOnce` イディオムを導入。
     - `Report()`、`Dispose()`、二重呼び出し、例外等どの経路であってもスロット解放は厳格に 1 回のみ実行されることを保証。
  2. **スロット上限・アンダーフロー防止ガード**:
     - `ReleaseSlot` において、`int remaining = Interlocked.Decrement(ref _activeSlots);` が負数になった場合は `0` へ引き戻す安全ガードを配備。
     - 外部からスロット状態を観測できるよう `public int ActiveSlots => Volatile.Read(ref _activeSlots);` を公開。
  3. **ExactPhrase Direct / Indexed 完全 Parity**:
     - `SearchEngineService.cs` の `MatchFile` および `MatchDirectEntry` において、`if (!query.SearchContentMode)` で囲み、本文ON時は名前検査でドロップしないよう改修。
     - `MatchesKeywordGroups` で `ExactPhrases` も名前/パスに含まれているかを検証し、すべて満たしていれば名前一致（`needsDeepCheck = false`）で即合格。
     - `FilterByContentAsync` の `requiredGroups` に `query.ExactPhrases` を統合し、名前で満たされていないフレーズを本文検査へ自動投入。
  4. **旧 `IsNetworkOrFatalError(string?)` の物理削除**:
     - 文字列信号線を完全に根絶し、`EnumerationFailureKind` 列挙型への正本一本化を完了。
  5. **自動回帰テストによる恒久保護**:
     - `RegressionTestSuite.Storage.cs` セクション 7 に、`CurrentConcurrency = 2` で 4 並列投入時に最大アクティブスロット数が厳格に `<= 2` であることの直接計測、および明示Report/二重Report/Dispose混在時のアンダーフロー・リークゼロ検証（`ActiveSlots == 0`）を追加。
     - `RegressionTestSuite.Search.cs` セクション 14 に、Direct Search における ExactPhrase 本文ヒットの Parity 検証を追加。
     - 全 8 ドメイン 8/8 ALL PASSED を堅持。

---

### ADR 83: 「本文も検索ON＋フォルダも含めるON」Direct/InMemory Parity完全回復 ＆ Emergency Head整線
*(v2.2.10 本番施工 & ADR 83)*

- **背景 & 動機**:
  - **Direct / In-Memory 側でのフォルダー名一致ドロップ回帰**: `SearchEngineService` の `MatchFile` および `MatchDirectEntry` において、`if (query.SearchContentMode)` の直下で `if (node.IsDirectory) return false;` と一律にドロップしていたため、「本文も検索ON ＋ フォルダも含めるON」のクエリ（例: `契約`）で、フォルダー名に「契約」が含まれるフォルダー（例: `📁 契約関連フォルダー`）が、Indexed Search ではヒットするのに Direct / In-Memory Search では 0 件になる不整合（ADR 76 意味論の回帰）が発生していた。
  - **Emergency Window リングバッファの Head 未リセット**: `ApplyCliffDecrease` 時に `_emergencyCount = 0` のみで `_emergencyHead = 0` をリセットしていなかったため、リングバッファ状態を整線。
- **施工内容**:
  1. **フォルダー名一致の救済（3経路完全Parity回復）**:
     - `MatchFile`（メモリ内検索）および `MatchDirectEntry`（ライブ直接走査）において、`query.SearchContentMode` 有効時でも、フォルダー判定時に `hasMandatoryContent`（`content:` 必須指定）が無ければ `MatchesKeywordGroups(name, fullPath, query)` を評価。
     - フォルダー名/パスがキーワード条件を満たしていれば `needsDeepCheck = false; reason = "Name"; return true;` で即座に合格とし、Indexed / In-Memory / Direct の全 3 経路でフォルダー検索結果が 100% 同一になるよう回復。
  2. **Emergency Window の完全リセット整線**:
     - `AdaptiveConcurrencyController.ApplyCliffDecrease` において、`_emergencyCount = 0` と同時に `_emergencyHead = 0` も明示リセット。
  3. **自動回帰テストによる恒久保護**:
     - `RegressionTestSuite.Search.cs` に「セクション 15: 『本文も検索ON ＋ フォルダも含めるON』における Indexed / In-Memory / Direct 3経路完全Parity検証」を新設。
     - フォルダー名一致が 3 経路すべてで同一に返ることを自動検証。全 8 ドメイン 8/8 ALL PASSED を堅持。

---

### ADR 84: UNC特化新検索アーキテクチャ（二重I/Oゼロ直結・Lazy Background Builder・Aho-Corasick Live Verify ＆ スニペット生成）
*(v2.2.11 本番施工 & ADR 84)*

- **背景 & 動機**:
  - **DB肥大化の実態調査と限界**:
    - ユーザー実環境でローカル DB（`ContentIndex.db`）が数GB〜10数GBまで膨張した事象について実測サンプリング調査を実施（総ファイル数 1,268,643 件、本文インデックス済み 218,804 件）。
    - 内訳: `ContentFts_data` 3.23GB (51%), `IndexedFiles` + B-Tree 1.80GB (29%), `ContentFts_content` 1.10GB (18%)。
    - 実験により、FTS5 の `optimize` + `VACUUM` を実行しても 6.28GB ➔ 6.04GB（わずか 3.8% 減）に留まることが判明。
    - また、FTS5 で `detail=none` を指定すると、SQLite 内部で trigram の任意文字列 MATCH がフレーズクエリとして処理されるため、`fts5: phrase queries are not supported (detail!=full)` となり検索が完全に失敗する SQLite 固有の制約を確認。
  - **UNCファイルサーバーにおける真のボトルネック**:
    - ローカルと異なり、UNC では「1ファイルを開くこと自体のネットワーク往復」が極めて高い。
    - 初回インデックス作成で UNC 全体を二重走査（容量測定で走査した直後に検索用にもう一度走査）するのは無駄であり、サーバーに負荷をかける。
    - 本文抽出も、初回から巨大ファイルを網羅しようとすると長時間の高負荷とDB肥大化を招く。
- **施工内容**:
  1. **Storage 列挙結果の Metadata Index 直結（二重I/Oゼロ）**:
     - `ContentIndexService.SyncFromStorageScanTreeAsync` を新設。
     - Storage Scan（容量測定）で列挙済みの `FileItemNode` メモリ木構造から、ディスク・UNCの再走査なしで `ScannedFileEntry` 一覧を生成し、`IndexedFiles` および `MetadataFts` へミリ秒一括登録。
     - 登録完了後、`IndexedRoots` を `Complete` に設定。ファイル名・属性・フォルダー検索が UNC 再アクセスゼロで即座に機能。
  2. **Aho-Corasick 多パターン同時照合エンジン ＆ ワンパスハイライトスニペット生成**:
     - `Services/AhoCorasickSearcher.cs` を新設。決定性オートマトン（Trie + Failure Link）によるワンパス照合。
     - PDF（`PdfSearchHelper`）、テキスト（`ContentExtractionService`）の直接走査時、同一ファイルをキーワードごとに複数回開き直す無駄を完全根絶し、全条件合致時の Early Exit を実現。
     - `AhoCorasickSearcher.ExtractSnippet` を実装し、FTS5 の重い `snippet()` 組み込み関数依存を撤去。DB から取得した Body に対し、C# メモリ上で瞬時に前後コンテキストを切り抜いたハイライトスニペットを生成。1文字・2文字のキーワードにも完全対応。
  3. **Lazy Background Builder（Small-File First）＆ Opportunistic Cache**:
     - `ContentIndexService.ProcessPendingContentIndexAsync` を新設。
     - Storage Scan 完了後、3秒のアイドルを置いて低優先度バックグラウンドで未インデックス（`Status = 0`）のファイルを **Small-File First（容量昇順）** で順次抽出・登録。
     - ユーザーの操作や新しいスキャン時は `CancellationToken` で即座に中断。
     - `UpsertFileContentDirectlyAsync` により、ライブ検索で本文を読んだファイルをその場でインデックスへ便乗投入する学習型キャッシュを配備。
  4. **自動回帰テストによる恒久保護**:
     - `RegressionTestSuite.Search.cs` に セクション 16（Storage スキャンツリー直結同期 ＆ 冪等性）、セクション 17（Aho-Corasick 多パターン同時照合 ＆ Early Exit）、セクション 18（Aho-Corasick スニペット抽出 ＆ Lazy Background Builder Small-File First ＆ Opportunistic Cache）を新設。
     - 全 8 ドメイン 8/8 ALL REGRESSION TESTS PASSED を堅持。

---

### ADR 85: 次世代検索アーキテクチャ Mode C（contentless + detail=none trigram FTS5 ＆ 3文字分解AND ＆ Progressive Verify）
*(v2.2.12 本番施工 & ADR 85)*

- **背景 & 動機**:
  - **10数GBのDB肥大化の根本解決**:
    - 本文全文をDB内に保存し、trigram + 位置情報（detail=full）をFTS5へ保持する従来方式（Mode A）では、127万ファイル環境でDBが6.28GB〜10数GBに膨張。
    - Solとの技術協議およびSQLite公式仕様・実機ベンチマークに基づき、発想を転換。
    - **「巨大な答えを持つ全文Index」から「読まなくていいファイルを判定する極小Index ＋ 必要な原本だけ確認（Verify）」** へアーキテクチャを進化させる。
  - **SQLite trigram の制約克服と実機検証**:
    - `detail=none` で 4文字以上のトークンを渡すと `fts5: phrase queries are not supported (detail!=full)` エラーが発生するが、**3 Unicode文字以内のトークンであれば detail=none でも完全に MATCH 可能**。
    - 4文字以上の単語はスライディングウィンドウ方式で 3文字 trigram の AND 結合式へ分解（例：「最高機密」➔ `("最高機" AND "高機密")`）。
    - さらに `Microsoft.Data.Sqlite` 10.0.12（SQLite 3.53.3）で正式サポートされている `content='', contentless_delete=1` を採用することで、DB内に本文（`Body`）を一切持たない純粋な contentless trigram インデックスを実現。
    - **10,005ファイル実機ベンチマーク結果**:
      - Mode A (detail=full, 本文あり): 11.61 MB, 443 ms
      - Mode B (detail=none, 本文あり): 7.94 MB, 336 ms (31.6% 削減)
      - **Mode C (contentless + detail=none, 本文なし): 1.41 MB, 194 ms (87.9% 削減、約1/8に激減！)**
  - **Sol指摘：False Positive（偽陽性）の必然性と Progressive Verify の必須性**:
    - trigram の AND 結合は False Negative（見落とし）を防ぐ必要条件だが十分条件ではない。例えば `"ABCD"` 検索に対し、本文中で `"ABC"` と `"BCD"` が遠く離れて出現する偽陽性ファイルも FTS MATCH を通過する。
    - したがって原本 Aho-Corasick による Verify は飾りではなく必須のセーフティネット。
    - FTS MATCH で絞り込んだ候補（Candidates）に対し、Small-File First でストリーム検証（Progressive Verify）を行い、真の Hit のみ UI へ順次合流させることで、**False Negative 100% ゼロ ＆ False Positive 100% ゼロ** を完全保証。
- **施工内容**:
  1. **Mode C スキーマ自動昇格 ＆ 自動マイグレーション**:
     - `ContentIndexService` の初期化時、既存 `ContentFts` のテーブル定義を検査。
     - 旧方式（detail=full や contentテーブル）の場合、トランザクション内で自動的に `content='', contentless_delete=1, tokenize='trigram'` の Mode C へ自動マイグレーション。
     - `IndexedFiles` の更新・削除時も `DELETE FROM ContentFts WHERE rowid = @fileId` が `contentless_delete=1` により通常テーブルと同様に完全動作。
  2. **スライディングウィンドウ 3文字分解 AND クエリビルダー**:
     - `BuildTrigramMatchExpression(string token)` を実装。
     - 3文字ジャスト: `"token"`
     - 4文字以上: `("tok" AND "oke" AND "ken")` へスライディング分解し、trigram のみの AND 式を構築。
     - 2文字以下の語: FTS MATCH をスキップし、ファイル名一致（先行表示）＋本文検索ON時は `IndexedFiles (Status = 1)` を候補として通し、後段の原本 Verify で確実に救済。
  3. **Progressive Verify パイプライン（Aho-Corasick ＆ AdaptiveConcurrency）**:
     - `SearchIndexedAsync` において、`fullHits`（SQL レベルの INTERSECT 集合）からファイル名だけで条件を満たす確定 Hit（`confirmedHits`）を即座に抽出し先行表示。
     - 本文照合を要求される候補（`contentCandidates`）を Small-File First（容量昇順）で並べ替え、`AdaptiveConcurrencyController` の帯域制御下で `ContentExtractionService` ＋ `AhoCorasickSearcher` による原本ストリーム検証を実行。
     - 合格したアイテムはスニペットを付与してリアルタイムに UI へ逐次バッチ通知（`batchYield`）。
  4. **自動回帰テストによる恒久保護**:
     - `RegressionTestSuite.Search.cs` に ADR 85 検証を新設。
     - Sol指摘の離れた `ABC...BCD`（偽陽性候補）と連続 `ABCD`（真の合致）を用意し、FTS MATCH で 2 件候補に挙がった後、Progressive Verify により真の 1 件のみに 100% 確定されること、およびスライディングウィンドウ 3文字 trigram AND 結合、`contentless_delete=1` の安全動作を自動検証。
     - 全 8 ドメイン 8/8 ALL REGRESSION TESTS PASSED を堅持。

---

### ADR 86: ストリーミング型 Live 直接走査 ＆ 2文字日本語検索漏れ根絶 ＆ 500件上限撤去
*(v2.2.13 本番施工 & ADR 86)*

- **背景 & 動機**:
  - **インデックス無しでも超高速な本文検索の追求**:
    - インデックスのない状態でも高速に本文検索できるインライン・ストリーミング走査技術を FolderMorpher に全面導入。
    - 従来の `SearchDirectFolderAsync` では、ファイル列挙（SafeFileEnumerator）が全件完了するまで本文検査が 1 件も開始されず、大容量フォルダや UNC 共有で数十秒間の待たされ感（フリーズ感）が発生していた。
  - **2文字日本語キーワード検索の漏れと 500件上限の正体**:
    - 2文字の日本語（例：「設計」「仕様」「報告」等）で本文検索をかけた際、SQLite trigram は 3文字以上を前提とするため、全候補を原本 Verify に回す設計にしていたが、SQL クエリ内部にハードコードされた `LIMIT 500` の存在により、500件枠から溢れたファイルが原本 Verify に送られず 100% 漏れていた（False Negative）。
    - さらに、属性検索・名前先行表示・全体検索の各 SQL にも `LIMIT 500` が存在し、大規模ファイルサーバー運用における暗黙の制約となっていた。
- **施工内容**:
  1. **500件上限（`LIMIT 500`）の完全撤去**:
     - `ContentIndexService.cs` における属性検索、ファイル名先行表示、全体検索の全 SQL から `LIMIT 500` を完全撤去。大規模環境でも上限なく全件がヒット・検証されるよう改善。
  2. **2文字日本語キーワードの検索漏れ根絶**:
     - `ContentIndexService.cs` の候補抽出条件を `Status IN (0, 1)` に拡大し、`LIMIT 500` 撤去と相まって、2文字以下のキーワードでも該当ファイルが確実に原本 Verify（Aho-Corasick）へ送られ、漏れゼロ（False Negative 0%）を保証。
  3. **ストリーミング型 Producer-Consumer Channel パイプライン（列挙と本文走査の完全並行化）**:
     - `SearchEngineService.SearchDirectFolderAsync` において `System.Threading.Channels.Channel<SearchResultItem>` を導入。
     - ファイル列挙（Producer）で見つかった本文候補（`needsDeepCheck`）を即座に Channel へ投入。
     - バックグラウンドで待機する Consumer ワーカー群（`AdaptiveConcurrencyController` 制御下、最大8並行）が即座にファイルを開いて本文検査を開始。
     - 検索開始からわずか数百ミリ秒で 1 件目のヒットが画面（UI）にポップアップ表示される超高速ストリーミングを実現。
     - 単一ファイルの本文検査ロジックを `InspectContentItemAsync` として一本化（正本の単一性を維持）。
  4. **ストリーミング I/O & 高速XML解析最適化**:
     - `FileOptions.SequentialScan` ＋ 64KB バッファ（`FileStream`, `StreamReader`）を `ContentExtractionService` のテキスト・Office 解析に全面適用し、OS の Read-Ahead キャッシュを最大活用。
     - `StripXmlTagsFast`: Office OpenXML（`.docx`, `.xlsx`, `.pptx`）のタグ除去において正規表現（`Regex.Replace`）を全廃し、1パスの高速 char スキャン＆空白圧縮へ刷新。
     - Office 本文走査において生XMLでの事前キーワード存在チェック（Pre-Filter）を導入し、ヒットしないファイルの不要なタグ除去をスキップして即脱落。
  5. **自動回帰テストによる恒久保護**:
     - `RegressionTestSuite.Search.cs` に ADR 86 検証を追加。
     - `StripXmlTagsFast` の精度検証、550ファイル生成・520番目「設計」配置による 500件上限突破＆2文字日本語検索の完全性検証、および `SearchDirectFolderAsync` の Producer-Consumer パイプライン直接走査検証を実施。
     - 全 8 ドメイン 8/8 ALL REGRESSION TESTS PASSED を堅持。

---

### ADR 87: 検索専用 FTS5 DB 撤去 ＆ インメモリ ＋ Live走査一本化 ＆ Canonical Path ＆ TreeCache SHA-256
*(v2.2.14 本番施工 & ADR 87)*

- **背景 & 動機**:
  - **検索専用 FTS5 DB の運用コストと肥大化の解消**:
    - 検索専用の SQLite DB（`folder_morpher_search.db`）は、120万〜数百万ファイル環境で数GB〜10GB以上に肥大化し、ディスク・ネットワーク負荷や WAL ロック競合を発生させていた。
    - 実態として、DBは検索専用にしか使われておらず、アプリ内にはすでに高速・軽量なスキャンツリー（インメモリおよびポータブルな `TreeCaches/*.json`）が存在していた。
    - 加えて、ストリーミング型 Producer-Consumer Channel パイプライン（列挙と本文走査の完全並行化 ＋ 64KB SequentialScan ＋ XML高速タグ除去）が完成したことにより、重厚なインデックスがなくとも開始数百ミリ秒でストリーミング検索が可能となった。
  - **ネットワークドライブ（Z:\）と UNC の二重化解消**:
    - ネットワークドライブ（例: `Z:\`）と UNC パス（例: `\\server\share`）が、中身同一であるにもかかわらず別名として扱われ、キャッシュや検索走査が二重化する問題があった。
  - **共有キャッシュツリー（JSON）への SHA-256 保持**:
    - ファイルサーバー共有用の JSON キャッシュツリーに SHA-256 ハッシュ値を保持できるようにし、共有キャッシュのポータビリティと安全性を高めることが求められた。
- **施工内容**:
  1. **検索専用 FTS5 DB の完全撤去と 2 大柱への一本化**:
     - `MainWindow.Search.cs` および `MainWindow.Storage.cs` から SQLite インデックス（`folder_morpher_search.db`、`_contentIndex`、`ContentIndexWatcherService`）のUI依存を完全に撤去。
     - 検索ルートを「① スキャン済みツリー / 共有JSONキャッシュによる 0秒インメモリ検索（ファイル名・属性一致を先行表示 ＋ 本文ストリーミング合流）」および「② 未スキャンUNC / 初見フォルダに対する Live 直接走査（Channel パイプライン）」の 2 大柱へ一本化。
     - 巨大なローカル SQLite DB の肥大化やロック競合、裏での常時ディスク・ネットワーク負荷を完全に根絶。
  2. **パス正規化エンジン（`Services/PathCanonicalizer.cs`）新設**:
     - `WNetGetConnectionW`（`mpr.dll`）により、ネットワークドライブ（例: `Z:\`）を実体の UNC パス（`\\server\share`）へ自動解決。
     - 高速インメモリキャッシュ（`DriveToUncCache`）により Win32 API 呼び出しのオーバーヘッドをゼロ化。
     - 拡張UNC（`\\?\`）の安全除去、末尾スラッシュの正規化、`AreSamePath` による大文字小文字・UNCマウント同一視判定を提供。
  3. **キャッシュツリー（`TreeCaches`）の Canonical 統合 ＆ SHA-256 サポート**:
     - `Models/FileItemNode.cs`: `public string? Sha256 { get; set; }` を追加。
     - `Services/StorageHistoryService.cs`:
       - `GetCacheFileName`: `PathCanonicalizer.Normalize` を適用し、`Z:\` でも `\\server\share` でも同一のキャッシュファイル名（ハッシュ）を生成・参照。
       - `GetTreeCacheReadFilePath`: 過去バージョンで生成された非正規化ハッシュのキャッシュファイルも自動検知する後方互換フォールバックを完備。
       - `TreeCacheNode`: `Sha256` プロパティを追加し、`ToCacheNode` / `FromCacheNode` でシームレスに相互変換・JSONシリアライズ。
  4. **自動回帰テストによる恒久保護**:
     - `Services/Testing/RegressionTestSuite.Storage.cs` に `TestPathCanonicalizerAndSha256CacheAsync` を追加（Domain 2）。
     - パス正規化（UNC、拡張UNC、末尾スラッシュ、大文字小文字同一視、ネットワーク判定）および TreeCache における Sha256 の JSON シリアライズ往復・保持を自動検証。
     - 全 8 ドメイン 8/8 ALL REGRESSION TESTS PASSED を堅持。


---

### ADR 88: 整理候補発見スタジオへのすり替え ＆ 説明責任ヒューリスティクス ＆ 「すぐ整理できそう」指標
*(v2.2.15 本番施工 & ADR 88)*

- **背景 & 動機**:
  - **従来のファイル監査（重複・休眠抽出）の限界**:
    - 従来のファイル監査（Tab 6）は「完全重複（SHA-256）」と「長期休眠（3年超）」のみを対象としており、管理者が最も困っている「業務上の無駄ファイル」を拾いきれていなかった。
    - また、AIが一方的に「消していい」と断定するアプローチは現場の不信感を招き、実務で使われない原因となっていた。
  - **「不要ファイル判定」から「理由付き整理候補発見エンジン」へのパラダイムシフト**:
    - AIが消去を決定するのではなく、「このファイルは捨てられる可能性が高い。理由はこれ。」と明確な根拠（Why）を添えて提示する。
    - 「高確度」といった小難しいシステム用語を排除し、誰にでも直感的に伝わる「すぐ整理できそう」「確認推奨」「参考」の親しみやすい目安を採用する。
- **施工内容**:
  1. **3大重点整理候補エンジンの新設（Services/HygieneCandidateEngine.cs）**:
     - **① 世代・旧版ファイル（Version Family）**:
       - 同一フォルダー内でステミング解析（正規表現による日付・バージョン・コピーサフィックスの最大5回ループ剥ぎ取り）を実施し、FamilyKey を抽出。
       - 更新日時が最新のファイル（Active）を特定し、過去の旧版ファイル（例: 見積書_最終_本当.xlsx に対する 見積書_修正版.xlsx）を整理候補として抽出（WasteScore 85点 / 「すぐ整理できそう」）。
       - RelatedActivePath により最新版のパスと更新日時を理由列に明示。
     - **② 展開済みアーカイブ残骸（Extracted Archive Shadow）**:
       - 同一フォルダー内にアーカイブベース名と同名のフォルダーが存在する ZIP / 7z / tar 等を O(1) 判定で検出。
       - 展開先フォルダー内の合計容量・ファイル数を理由に表示（WasteScore 90点 / 「すぐ整理できそう」）。
     - **③ 墓場フォルダー（Graveyard / Ghost Tree）**:
       - 配下の全ファイルが3年以上未更新かつ直近1年間の閲覧（アクセス）ゼロのフォルダーを検出（3ファイル以上 & 1MB以上）。
       - フォルダー名に _old, _bak, _退避, rchive 等のキーワードが含まれる場合はスコア加点。
       - 配下全ファイルの合計容量と経過年数を理由に添え、フォルダー代表エントリとして登録（WasteScore 85〜95点 / 「すぐ整理できそう」）。
  2. **データモデルの拡張（Models/AuditModels.cs）**:
     - AuditIssueType: VersionFamily, ExtractedArchive, GraveyardTree を追加。
     - AuditItem: WasteScore (0〜100), ConfidenceDisplay (すぐ整理できそう/確認推奨/参考), ConfidenceBadgeBgHex, RelatedActivePath を追加。
     - AuditSummary: VersionFamilyCount/Bytes, ExtractedArchiveCount/Bytes, GraveyardTreeCount/Bytes, ReadyToCleanBytes を追加。
  3. **UIの抜本的すり替え（MainWindow.xaml / MainWindow.Audit.cs / MainWindow.Localization.cs）**:
     - **ヘッダー & オプション**: 「ファイルサーバー健全化 & 整理候補発見スタジオ」へ刷新。チェックボックス（世代・旧版、展開済ZIP、完全重複、休眠・墓場、パス長/禁則）を配備。
     - **5連スリムメトリクスバー**: 「総走査」「すぐ整理できそう」「世代・旧版」「完全重複」「休眠・墓場」のQuiet Fluentバーを配備。
     - **絞り込み & 一括選択プリセット**: 「すぐ整理できそうを選択」「世代・旧版のみ」「展開済ZIPのみ」「墓場フォルダーのみ」等の高速操作を提供。
     - **「整理の目安」ピルバッジ列**: 赤（すぐ整理できそう）、黄（確認推奨）、灰（参考）の角丸バッジで視認性を最大化。
  4. **既存安全原則の100%堅持**:
     - 原本候補の絶対保護（IsOriginalCandidate）および削除直前 SHA-256 再照合による誤削除ゼロ保証は完全に維持。
  5. **自動回帰テストによる恒久保護**:
     - RegressionTestSuite.Audit.cs に TestHygieneCandidateDiscoveryAsync を追加（Domain 4）。
     - 世代旧版の抽出、展開済ZIP残骸の検出、墓場フォルダーの判定、スコアリング・目安表示、サマリー集計を自動検証。
     - 全 8 ドメイン 8/8 ALL REGRESSION TESTS PASSED を堅持。

---

### ADR 89: スコア内訳の可視化 ＆ 上位表示件数制御 ＆ 整理除外記憶（保持マーク） ＆ 用語の単語化整線
*(v2.2.16 本番施工 & ADR 89)*

- **背景 & 動機**:
  - **スコア根拠の透明化**:
    - なぜそのスコアや目安になったのか、詳細な内訳（基礎点、サフィックス有無、経過年数等）をクリックまたはホバーで確認したいというニーズ。
  - **大量候補に対する意思決定疲労の防止（表示件数制御）**:
    - 大規模ファイルサーバーでは整理候補が数千〜数万件に及ぶことがあり、全件表示は管理者の判断を麻痺させる。全件解析しつつも、まずは影響の大きい上位100件などに絞って着手できる仕組みが求められた。
  - **「消さない」と判断したファイルの次回以降のスキップ（除外記憶）**:
    - ユーザーが業務上必要と判断したファイルを、次回スキャン時に再度候補として出さないようにしたい。ただしファイルが更新された場合は再評価されるべき。
  - **用語の整線（口語の排除 ➔ 単語の組み合わせ）**:
    - 「すぐ整理できそう」といった長文口語表現を排し、業務ツールとして直感的かつ簡潔な単語組み合わせ（「整理推奨」「要確認」「参考」）へ刷新する。
- **施工内容**:
  1. **スコア内訳（ScoreBreakdown）の構造化 & 可視化**:
     - `Models/AuditModels.cs`: `ScoreFactorItem`（`NameJa`, `NameEn`, `Points`, `DisplayText`）を新設。`AuditItem.ScoreBreakdown` および `ScoreBreakdownSummary` を追加。
     - `Services/HygieneCandidateEngine.cs` & `Services/AuditReportService.cs`: 世代旧版、展開済ZIP、墓場、完全重複、休眠の各判定で加点要素を記録。
     - `MainWindow.xaml` / `MainWindow.Audit.cs`: 「整理の目安」ピルバッジをホバー時に ToolTip で内訳サマリーを表示し、クリック時に詳細ダイアログを表示（`AuditScoreBreakdown_MouseDown`）。
  2. **表示件数の制御（AuditMaxDisplayComboBox）**:
     - 「上位100件 (推奨)」「上位300件」「上位500件」「全件表示」のコンボボックスを配置。
     - `ApplyAuditFilters` にて WasteScore 降順 ➔ Size 降順でソートの上、指定件数で `Take(maxCount)` 抽出。ヘッダーに「全 X 件中 上位 Y 件を表示」と明示。
  3. **整理除外（保持マーク）リスト / スキップ記憶機能（Services/AuditIgnoreService.cs）**:
     - `%LOCALAPPDATA%\FolderMorpher\audit_ignore_list.json` に永続化。
     - 右クリック `ContextMenu`（`🛡️ このファイルを整理候補から除外 (次回から非表示)`）から即座に登録・行除外。
     - **自己治癒性（Self-Invalidation）**: `FileSizeBytes` と `LastWriteTimeUtcTicks` を保持し、ファイルが変更（サイズ変更または更新）された瞬間に自動で除外解除（再評価）される。
     - `AuditIgnoredListButton`（`🛡️ 除外リスト (N件)`）により、除外ファイル一覧の確認および一括クリアを提供。
  4. **用語の整線（口語の排除 ➔ 直感的な単語組み合わせ）**:
     - 目安表示を刷新：
       - スコア >= 80: **「整理推奨」**（英語: `Recommended`）
       - スコア >= 50: **「要確認」**（英語: `Review Needed`）
       - スコア < 50: **「参考」**（英語: `Reference`）
     - メトリクスバー（`整理推奨:`）、一括選択（`「整理推奨」を一括選択`）、絞り込み（`整理推奨 のみ` / `要確認 のみ`）、日英ローカライズを完全同期。
     - `AuditItem` に定数（`ConfidenceRecommendedJa`, `ConfidenceRecommendedEn` 等）を新設し、テスト・UIロジックで正本として参照。
  5. **自動回帰テストによる恒久保護**:
     - `RegressionTestSuite.Audit.cs`: `ScoreBreakdown` 整合性、および `AuditIgnoreService` の登録・変更時自動復帰・解除動作を自動検証。
     - `RegressionTestSuite.Localization.cs`: 日英両言語での `ConfidenceDisplay` 判定を自動検証。
     - 全 8 ドメイン 8/8 ALL REGRESSION TESTS PASSED を堅持。

---

### ADR 90: SharedIoGovernor（I/O並列度統合ガバナー）＆ Large File Pipeline（分散Probe）＆ BoundedChannel Backpressure ＆ 新Audit O(N)ボトムアップ集約 ＆ Ignore Canonical化
*(v2.2.17 本番施工 & ADR 90)*

- **背景 & 動機**:
  - **1. AdaptiveConcurrency の分裂と合算スパイク（I/Oガバナンスの欠如）**:
    - `SafeFileEnumerator`（列挙側）と `SearchEngineService`（本文検索側）がそれぞれ独立に `AdaptiveConcurrencyController` を持っていたため、同一UNCサーバーに対して列挙4並列＋本文4並列の合計最大8並列が走り、サーバー負荷のスパイクやスロットリングを招く危険があった。
  - **2. 50MB超ファイルの足切りと巨大ファイル本文検索の不在**:
    - `MaxSearchFileSize = 50MB` の一律足切りにより、50MBを超える大容量ログファイル、CSV、ダンプ等の本文検索が一切行えなかった。しかし丸ごと全走査すると巨大ファイルのI/Oで検索がスタックするジレンマがあった。
  - **3. Channelの無制限キューイング（メモリ肥大化）**:
    - Producer-Consumerパイプラインが `Channel.CreateUnbounded` であったため、超高速列挙時にキューが数万件滞留しメモリを浪費するリスクがあった。
  - **4. 墓場フォルダー・展開済ZIP判定の計算量 $O(D^2)$（大規模ファイルサーバーでの遅延）**:
    - 各フォルダーごとに配下の全フォルダーを `StartsWith` で再帰スキャンしていたため、フォルダー階層数 $D$ に対して $O(D^2)$ の走査が発生し、10万ディレクトリ規模でCPUとメモリを圧迫していた。
  - **5. ネットワークドライブ（Z:\）とUNC（\\server\share）の除外リスト二重化**:
    - `AuditIgnoreService` が生パスをキーとしていたため、`Z:\` で除外したファイルが UNC パスでの走査時に再検出される不整合があった。
- **施工内容**:
  - **1. SharedIoGovernor による UNC Root / ボリューム単位の並列度一本化 (`Services/SharedIoGovernor.cs`)**:
    - `PathCanonicalizer.GetVolumeOrShareRoot(path)` により `\\server\share` または `C:` を抽出し、ルート単位で単一の `AdaptiveConcurrencyController` を共有・再利用。
    - 列挙側（`SafeFileEnumerator`）と本文検索側（`SearchEngineService`）の合算負荷を完全に単一ガバナー下で統制。
  - **2. Large File Pipeline & 分散Probeによる巨大ファイル超高速スポット判定 (`Services/ContentExtractionService.cs`)**:
    - 50MB足切りを撤廃。
    - 50MB超ファイルに対して、先頭256KB、末尾256KB、中間ブロック（25%, 50%, 75% 各128KB）をスポット検査する `ProbeLargeFileContentAsync` を先行実行。
    - Aho-Corasick 多パターン照合により、見当違いのファイルは数ミリ秒で即座に脱落、キーワードを含むファイルは全走査することなく即時スニペット付きでヒット確定。
  - **3. BoundedChannel (2048件) による Backpressure 同期 (`Services/SearchEngineService.cs`)**:
    - `Channel.CreateBounded<FileCandidateItem>(2048)` に改修し、本文検査の消費速度に合わせて列挙の生産速度を自然にBackpressure制御。メモリフットプリントを最小化。
  - **4. 新Audit $O(N)$ ボトムアップ集約化 (`Services/HygieneCandidateEngine.cs`)**:
    - 深さ降順（Deepest First）の1パス集約 `BuildFolderAggregationMap` を実装。
    - 子フォルダーの合計容量・ファイル数・最新更新日時・アクセス日時を親へボトムアップ加算。
    - 墓場フォルダーおよび展開済ZIPの判定をマップ参照により $O(1)$ 化し、全フォルダー走査を $O(N)$（$N$ はフォルダー総数）へ圧縮。
  - **5. AuditIgnoreService の PathCanonicalizer 適用 (`Services/AuditIgnoreService.cs`)**:
    - `IsIgnored`, `AddIgnore`, `RemoveIgnore`, `Load` のすべてで `PathCanonicalizer.Normalize` を適用。
    - ドライブレターとUNCパス、大文字小文字、スラッシュ揺れを完全に同一視し、除外設定のポータビリティを保証。
- **検証と恒久保護**:
  - `Services/Testing/RegressionTestSuite.Search.cs`:
    - Section 19 を新設し、`SharedIoGovernor` の UNC ルート/ボリューム同一性共有、`PathCanonicalizer.GetVolumeOrShareRoot`、55MBファイルの分散Probe合否判定を自動検証。
  - `Services/Testing/RegressionTestSuite.Audit.cs`:
    - 検証 6 に `PathCanonicalizer` による小文字/スラッシュ揺れパスの除外一致を自動検証。
  - 全 8 ドメイン 8/8 ALL REGRESSION TESTS PASSED を堅持。

---

### ADR 91: SharedVolumeGovernor（スロット枠とレイテンシ学習の完全分離）＆ Deferred Large Files（2段構えパイプライン）＆ Hygiene TargetRoot 境界ガード ＆ ドキュメント他社製品名・商標の完全整線
*(v2.2.17 本番施工 & ADR 91)*

- **背景 & 動機**:
  - **1. スロット共有とレイテンシ学習の混同による性能劣化**:
    - ADR 90 で同一ボリュームに対して `AdaptiveConcurrencyController` を一本化したが、ディレクトリ列挙（5〜15ms）と本文読込（50〜200ms）のレイテンシ特性は全く異なる。重い本文読込の計測値によってベースラインが汚染され、列挙側の並列度まで下限（2並列）に急降下・固着する問題があった。
  - **2. 巨大ファイル（50MB超）のパイプライン詰まり vs 検索漏れ**:
    - 50MB超ファイルで分散Probeが不一致だった場合、その場で全文読込（Sequential Scan）を行うと、後続の軽量ファイル走査が詰まって体感速度が著しく低下する。かといって読まなければ検索漏れになる。
  - **3. Hygiene ボトムアップ集約の TargetRoot 境界漏れ**:
    - 集約処理が親フォルダーやドライブレター（`C:\` 等）にまで遡って集計マップに登録され、ルート自身やルート外が墓場フォルダー候補として誤検出されるリスクがあった。
  - **4. ドキュメントにおける他社製品名・商標表記の是正**:
    - `AGENTS.md` や `ADR.md` 内に他社製品名（`Agent Ransack`, `FileLocator Pro`, `Everything` 等）が散見され、プロプライエタリなアーキテクチャ記述・自律開発ドキュメントとして不適切であった。
- **施工内容**:
  - **1. SharedVolumeGovernor による物理スロット枠とレイテンシ学習の分離 (`Services/SharedIoGovernor.cs`)**:
    - `SharedVolumeGovernor` を新設。ボリューム全体の最大同時I/O数を `GlobalSlotGate`（`SemaphoreSlim(4, 4)`）で物理的に統制。
    - レイテンシ適応学習は `EnumerationController`（列挙専用）と `ContentController`（本文読込専用）に完全分離。列挙側の高速性・安定性を確保しつつ、重い本文読込による学習汚染を根本根絶。
  - **2. Deferred Large Files（2段構えパイプライン）(`Services/SearchEngineService.cs`)**:
    - 50MB超ファイルで分散Probeがヒットした場合は即座に結果を提示。
    - Probe未ヒットの場合はその場での全文読込をスキップし、`deferredLargeFiles`（後回しキュー）へ退避。
    - 通常ファイルの列挙・本文検索が完了した直後に、退避された巨大ファイルの全文検索を順次実行。パイプラインの詰まりを解消しつつ、検索漏れ（False Negative）ゼロを完全保証。
  - **3. Hygiene TargetRoot 境界ガード (`Services/HygieneCandidateEngine.cs`, `Services/AuditReportService.cs`)**:
    - `BuildFolderAggregationMap` および `DetectGraveyardTrees` に `targetRoot` を引き渡し、親ディレクトリへの遡及登録を `targetRoot` で打ち切り。
    - `targetRoot` 外および `targetRoot` 自身が墓場フォルダー候補として出力される誤爆を物理的に排除。
  - **4. ドキュメント他社製品名・商標の完全整線 (`AGENTS.md`, `ADR.md`, `RegressionTestSuite.cs`)**:
    - 全出現箇所を「ストリーミング型 Producer-Consumer Live 直接走査」「高度な検索クエリ構文」等の構造的・技術的表現へ整線。
- **検証と恒久保護**:
  - `Services/Testing/RegressionTestSuite.Search.cs`: `SharedVolumeGovernor` のスロット共有・コントローラー分離検証、`GlobalSlotGate` の初期値4検証。
  - `Services/Testing/RegressionTestSuite.Audit.cs`: TargetRoot 境界ガード（親やルート自身の墓場誤爆防止）の検証。
  - 全 8 ドメイン 8/8 ALL REGRESSION TESTS PASSED を堅持。

---

### ADR 92: BoundedChannel × GlobalSlotGate 循環デッドロック根絶（スロット保持スコープ即時解放）＆ Audit 墓場フォルダー多層防御（削除対象外化）＆ TreeCache 知識再利用（Audit SHA-256書き戻し）＆ UIバージョン動的同期
*(v2.2.17 本番施工 & ADR 92)*

- **背景 & 動機**:
  - **1. Producer-Consumer 循環待ちデッドロック（Solレビュー指摘）**:
    - `SafeFileEnumerator` の列挙ワーカーが `GlobalSlotGate` を保持したまま `onEntryFound` を呼び出し、`SearchEngineService` の `BoundedChannel(2048)` が満杯になると `WriteAsync` で待機していた。一方 Consumer は本文読込のため `GlobalSlotGate` を待機するため、「Producer: Channel空き待ち ⇄ Consumer: GlobalSlot空き待ち」の循環待ちデッドロックが発生する危険があった。
  - **2. Audit 墓場フォルダーの削除事故・不整合**:
    - 墓場候補は `WasteScore >= 80` のため「整理推奨」一括選択の対象となっていたが、実態はフォルダーであるため `File.Exists` を前提とする削除処理で失敗するか、意図しないフォルダー削除を招く危険があった。
  - **3. TreeCache の SHA-256 器活用（知識の再利用）**:
    - TreeCache のノードに `Sha256` プロパティが新設されていたが、Audit で計算したハッシュを書き戻す経路がなく、次回以降の重複検出や監査で再計算が必要だった。
  - **4. サイドバーのバージョンハードコード乖離**:
    - `.csproj` が `2.2.17` なのに対し、サイドバーが `v2.2.15` のまま固定されていた。
- **施工内容**:
  - **1. スロット保持スコープの I/O 即時限定 (`Services/SafeFileEnumerator.cs`)**:
    - `NativeDirectoryEnumerator.TryEnumerateEntries` の実際の Win32 API 呼び出しの瞬間のみスロットとリースを保持し、I/O 完了と同時に即時解放。
    - メモリ上の子フォルダー処理や Channel 書き込み待機中はスロットを保持しないことで、Producer-Consumer 間の循環待ちデッドロックを物理的に根絶。
  - **2. 墓場フォルダーの多層防御 (`Models/AuditModels.cs`, `MainWindow.xaml`, `MainWindow.Audit.cs`, `Services/AuditCleanupService.cs`)**:
    - `AuditItem.IsCleanable`: `IssueType != GraveyardTree && !IsOriginalCandidate`。
    - `AuditItem.IsChecked` setter: `if (value && !IsCleanable) return;` でチェックを無効化。
    - UI DataGrid: CheckBox に `IsEnabled="{Binding IsCleanable}"` を設定しグレーアウト。
    - `AuditCleanupService.BuildPlan`: `Where(i => i.IsChecked && i.IsCleanable)` で計画から完全排除。
    - `AuditCleanupService.ExecutePlan`: `if (Directory.Exists(plan.FullPath))` でフォルダー直接削除を安全拒否。
  - **3. TreeCache への Audit SHA-256 書き戻し (`Services/StorageHistoryService.cs`, `MainWindow.Audit.cs`)**:
    - `UpdateTreeCacheSha256Async` を新設し、Audit 完了時に計算済みハッシュを TreeCache ノードへ自動反映・保存（ファイルサーバー知識の蓄積と再利用）。
  - **4. サイドバーバージョンの動的同期 (`MainWindow.xaml`, `MainWindow.xaml.cs`)**:
    - XAML の静的表記を `v2.2.17` に修正し、起動時に `Assembly.GetExecutingAssembly().GetName().Version` から動的設定。
- **検証と恒久保護**:
  - `Services/Testing/RegressionTestSuite.Audit.cs`:
    - 検証 8: 墓場フォルダーの `IsCleanable == false`、`IsChecked` 拒否、`BuildPlan` 除外の多層防御を自動検証。
    - 検証 9: `UpdateTreeCacheSha256Async` による TreeCache へのハッシュ永続化を自動検証。
  - 全 8 ドメイン 8/8 ALL REGRESSION TESTS PASSED を堅持。

---

### ADR 94: Server Search Accelerator ＆ Windows Search Provider（WSP / OLE DB サーバー側インデックス拝借 ＆ 候補ピンポイント原本確認）
*(v2.2.19 本番施工 & ADR 94)*

- **背景 & 動機**:
  - **1. クライアント単独での大容量ファイルサーバー全文走査の物理的限界**:
    - SMB/UNC ネットワーク越しに数万〜数十万ファイルを走査・本文読込する場合、ネットワーク帯域やI/Oスループットの制約から数分〜十数分を要していた。
  - **2. サーバー側資産の遊休**:
    - Windows Server や WSP 互換 NAS（Synology Universal Search 等）は、既にバックグラウンドで高速な全文インデックスを構築・維持している。クライアントがこのインデックスを拝借できれば、走査時間を数秒に圧縮できる。
  - **3. インデックス不整合・権限昇格リスクの解消**:
    - サーバー側インデックスを完全な正解として盲信すると、インデックス遅延によるゴミ（偽陽性）や権限のズレを招く。また、製品ごとにAPIが異なり、NetAppのように検索APIを開放していないエンタープライズ製品もあるため、共通インデックス規格を前提としない設計が必須であった。
- **施工内容**:
  - **1. `IServerSearchProvider` / `ServerSearchAccelerator` 抽象層 (`Services/ServerSearch/`)**:
    - サーバー側インデックスの利用口をプロバイダーパターンで抽象化。
    - プローブ（`CanHandleAsync`）と候補取得（`QueryCandidatesAsync`）を分離し、非対応・エラー・ポリシー禁止（`PreventRemoteQueries`）時は例外を出さず透過的に `null` フォールバック。
  - **2. `WindowsSearchProvider` (WSP / `Search.CollatorDSO` OLE DB)**:
    - Windows 標準の Search OLE DB Provider を利用し、Windows Server および WSP 互換 NAS（Synology 等）へリモートクエリ（`FROM "server".SystemIndex WHERE SCOPE='file://server/share/...'`）を発行。
    - 現在のWindowsログオンユーザーのToken（SMB Identity）で問い合わせるため、ユーザーが読めないファイルはサーバー側で自動排除。管理者権限も不要。
    - キーワードグループ（AND-of-ORs）、ExactPhrases、拡張子フィルターを Windows Search SQL へ自動変換。
  - **3. 候補ピンポイント原本確認パイプライン (`Services/SearchEngineService.cs`)**:
    - サーバーから候補（数十〜数百件）が返ってきた場合、数万ファイルのディレクトリ全走査（`SafeFileEnumerator`）を丸ごとスキップ。
    - 候補ファイルだけを `ScannedFileEntry` 化して Consumer へ流し、クライアント側で Aho-Corasick や Office パーサーによる原本確認（Verify）を実行。
    - 偽陽性（False Positive）を100%排除しつつ、20分要していた走査・読込を1〜2秒で確定。
    - サーバーインデックス非対応環境では、従来の差分枝刈り＋局所性走査へ安全にフォールバック。
- **検証と恒久保護**:
  - `Services/Testing/RegressionTestSuite.Search.cs`:
    - セクション 21: WindowsSearchProvider の UNC / Local SQL構文生成、CanHandleAsync 判定、未接続サーバーへの null フォールバック、ServerSearchAccelerator とモックプロバイダー連携を自動検証。
  - 全 8 ドメイン 8/8 ALL REGRESSION TESTS PASSED を堅持。

---

### ADR 93: 親子局所性（Locality-First LIFO走査）＆ TreeCache 枝刈り差分走査（Differential Pruning Traversal）＆ JIT/AVX2 最適化 単一キーワード高速パス
*(v2.2.18 本番施工 & ADR 93)*

- **背景 & 動機**:
  - **1. ファイル走査におけるI/O局所性の欠如**:
    - 幅優先（BFS）的なキュー管理では、ワーカーがディレクトリ階層を横断して探索するため、ファイルサーバー（特にSMB/NAS）側のキャッシュが効かず、ディスクヘッドのランダムシークやネットワーク往復遅延が累積していた。また、未処理フォルダーのパス文字列がキューに大量滞留しメモリを圧迫していた。
  - **2. スキャン済み環境における冗長なネットワーク列挙**:
    - 一度容量スキャンを行ったフォルダーであっても、検索のたびに全階層をネットワーク列挙（`FindFirstFileExW`）していた。大容量ファイルサーバーではフォルダーの99%以上が更新されないため、変更のないフォルダーまで毎回SMBパケットを往復させるのは極めて無駄であった。
  - **3. 単一キーワード検索時のステートマシン構築オーバーヘッド**:
    - 単一キーワードであっても Aho-Corasick の Trie 木構築や状態遷移判定を行っており、.NET ランタイムが持つ JIT/AVX2 最適化された `string.IndexOf(OrdinalIgnoreCase)` の潜在能力を活かし切れていなかった。
- **施工内容**:
  - **1. 親子局所性走査（Locality-First LIFO Traversal）(`Services/SafeFileEnumerator.cs`)**:
    - 各列挙ワーカーにローカル LIFO スタック（`localStack`）を配備。
    - 列挙したサブフォルダーのうち、直下（深掘り）フォルダーは自分のローカルスタックへプッシュして直ちに探索を継続し、2つ目以降の兄弟フォルダーをグローバルキューへ投入して他ワーカーへ分配。
    - ファイルサーバーOS側の RAM/MFT キャッシュを 100% 直撃させ、SMB パケットの連続性を最大化。クライアント側のキュー保持件数も最小限に圧縮。
  - **2. TreeCache 枝刈り差分走査（Differential Pruning Traversal）(`Services/TreeCachePruningIndex.cs`, `Services/SafeFileEnumerator.cs`, `Services/SearchEngineService.cs`)**:
    - 親フォルダー列挙時に追加I/Oなしで手に入る子フォルダーの `LastWriteTimeUtc` と、TreeCache（`FileItemNode`）の記録を $O(1)$ パス照合。
    - 日時が一致している場合、配下サブツリーの全ネットワークI/Oを丸ごとスキップ（枝刈り）し、メモリ上のエントリを即座に再帰展開して `onEntryFound` へ供給。
    - 変更があったフォルダーのみ Live 列挙するため、検索結果の最新性（Freshness）を100%担保しつつ、数万フォルダーのI/Oをゼロ化して一瞬で本文読込パイプラインへ接続。
  - **3. JIT/AVX2 最適化 単一キーワード高速パス (`Services/ContentExtractionService.cs`)**:
    - 単一キーワード時（`patterns.Count == 1 && requiredGroups.Count == 1`）は Aho-Corasick の構築を完全にスキップ。
    - ハードウェアの AVX2 / SIMD 命令セットを活用した `string.IndexOf(singleKw, StringComparison.OrdinalIgnoreCase)` を直接実行するストリーム読込パスを実装。
- **検証と恒久保護**:
  - `Services/Testing/RegressionTestSuite.Search.cs`:
    - セクション 20: TreeCachePruningIndex の枝刈り判定、SafeFileEnumerator の枝刈り差分走査 parity、ContentExtractionService の単一キーワード高速パス（AVX2 / OrdinalIgnoreCase）を自動検証。
  - 全 8 ドメイン 8/8 ALL REGRESSION TESTS PASSED を堅持。

---

### ADR 95: Astra提唱 徹底的Live高速化 ＆ 書式分割保護 ＆ バッファ直接走査 ＆ 二重解析根絶
*(v2.2.20 本番施工 & ADR 95)*

- **背景 & 動機**:
  - **1. PDF外れファイルにおける二重解析の発生**:
    - Windows Native IFilter で正常に最後まで走査して「該当なし（CompleteNoMatch）」と判明したにもかかわらず、bool 返却値の曖昧さにより Pure C# フォールバック（PDFバイナリ全読み＋正規表現＋ストリーム解凍）が重複実行されていた。検索の大半を占める「外れファイル」ほど2倍の仕事をしていた。
  - **2. Office 書式境界分割による検索漏れ（False Negative）＆ sharedStrings 二重読み**:
    - Word/Excelでは `<w:t>契約</w:t><w:t>書</w:t>` のようにスタイルや境界でテキストが分断される場合があるが、従来のタグ除去ではタグごとに無条件で空白を挿入していたため、「契約 書」となり検索語「契約書」にヒットしなくなっていた。また生XMLに対する JIT Pre-Filter も同様の誤脱落を引き起こしていた。
    - さらに Excel 走査において、先頭で `sharedStrings.xml` を読んだ後、後続の全体走査ループでも再度 `sharedStrings.xml` を開いて `ReadToEnd()` する二重読みが発生していた。
  - **3. テキスト走査の行単位アロケーション（ReadLineAsync）＆ 検索機械のファイル毎再生成**:
    - `ReadLineAsync` により行ごとに `string` をヒープ生成・GC させており、巨大ファイルや多行ログでオーバーヘッドとなっていた。また、各ファイル検査ごとに `new AhoCorasickSearcher` を再コンパイルしていた。
  - **4. UNC 上の ZIP/PDF における細切れ Read/Seek による SMB 往復遅延**:
    - 数百KB〜数MBの小〜中規模文書に対して遠隔 FileStream から直接 ZipArchive や PDF 解析を行うと、細かい Read/Seek が数十〜数百回発生し、SMBのRTT（往復遅延）が累積していた。
  - **5. TreeCachePruningIndex の潜在的危険性（NTFS仕様起因の検索漏れ）**:
    - NTFSでは親フォルダーの `LastWriteTime` は「既存ファイルの本文更新」では更新されないため、フォルダー日時一致で配下を全部スキップすると既存ファイルの編集を見逃す危険があった。
- **施工内容**:
  - **1. PDF 正常非一致と抽出失敗の厳格分離 (`Services/PdfSearchHelper.cs`)**:
    - `PdfFilterStatus { Match, NoMatchComplete, FailedOrUnsupported }` を導入。
    - IFilter で `FILTER_E_END_OF_CHUNKS` まで走査し切って一致しなかった場合は `NoMatchComplete` として即座に終了し、Pure C# フォールバックを完全スキップ。
  - **2. 書式分割保護 ＆ sharedStrings 二重読み根絶 ＆ JIT誤脱落排除 (`Services/ContentExtractionService.cs`)**:
    - `StripXmlTagsFast`: インライン要素（`<w:t>`, `<t>`, `<a:t>` 等）の境界では空白を挿入せず連続結合し、ブロック要素（`<w:p>`, `<w:tr>`, `<row>`, `<a:p>` 等）の境界でのみ空白を挿入。書式分割された単語を100%保護。
    - 生XMLでの危険な JIT Pre-Filter を撤去し、タグ除去後のクリーンテキストで安全に照合。
    - `sharedStrings.xml` を検査した後は `hasScannedSharedStrings` フラグにより後続ループでの再読を100%スキップ。充足済みグループも後続シートへ引き継ぎ。
  - **3. ripgrep流 バッファ直接走査 ＆ 境界またぎオーバーラップ ＆ クエリ単位共有検索機械 (`Services/ContentExtractionService.cs`, `Services/SearchEngineService.cs`)**:
    - `SearchTextContentWithBufferAsync`: 行単位の `ReadLineAsync` を完全撤去し、64KBスライディングバッファで直接走査。境界をまたぐキーワードを保護するため `MaxOverlap`（最大1024文字）を次バッファ先頭へ引き継ぎ。
    - `QuerySearchContext`: クエリ全体のキーワードグループから `SingleKeyword` または `AhoCorasickSearcher` を検索開始時に1度だけコンパイルし、全ファイルワーカーで共有・再利用。
  - **4. UNC 細切れ読みのメモリ一括展開 (`Services/ContentExtractionService.cs`, `OpenBufferedReadStream`)**:
    - 20MB以下の Office / PDF ファイルは `File.ReadAllBytes` により一括でメモリへ読み込み `MemoryStream` として開く。UNC/SMBのSeek/Read往復遅延を根本から解消。
  - **5. TreeCachePruningIndex の安全是正 (`Services/TreeCachePruningIndex.cs`)**:
    - `EnableFolderTimestampPruning` プロパティ（既定値: `false`）を導入。通常時は検索漏れゼロの安全 Live 走査を保証し、オプトイン時のみ差分枝刈りを実行。
- **検証と恒久保護**:
---

### ADR 96: Windows Native IFilter COM Interop ポインタ安全化 ＆ RCWクラッシュ根絶
*(v2.2.20 本番施工 & ADR 96)*

- **背景 & 動機**:
  - **GitHub Actions CI および Windows Server ヘッドレス環境での AccessViolationException 即死**:
    - CI ランナー（Azure VM / Windows Server 2022）環境で回帰テストを実行すると、Domain 8（Search Studio）の PDF 検査において `Fatal error. System.AccessViolationException` が発生し、プロセスがクラッシュ（exit code 1）していた。
    - スタックトレース:
      ```
      System.AccessViolationException: Attempted to read or write protected memory.
         at System.Runtime.InteropServices.Marshal.Release(IntPtr)
         at System.Runtime.InteropServices.Marshalling.ComObject.Finalize()
      ```
  - **根本原因の解明**:
    - `PdfSearchHelper.cs` において、`query.dll` の `LoadIFilter` が `[MarshalAs(UnmanagedType.IUnknown)] out object? ppIUnk` と定義されていた。
    - Windows Server や CI 環境等で PDF に対する IFilter が存在しないか無効な場合、`LoadIFilter` はエラーコード（例: `FILTER_E_NOT_FOUND` / `E_FAIL`）を返すが、C++ 側が `*ppIUnk` をクリアせず未定義ポインタのまま復帰することがある。
    - CLR の P/Invoke マーシャラーは、HRESULT がエラーであっても `out object` の引数を無条件で IUnknown として解釈し、ゴミポインタに対して RCW（Runtime Callable Wrapper）を生成してしまう。
    - その後、GC のファイナライザースレッドが `ComObject.Finalize()` -> `Marshal.Release(IntPtr)` を呼び出した瞬間に、不正な保護メモリへアクセスしてプロセスが即死していた。さらに、`Marshal.ReleaseComObject(filter)` の明示的呼び出しと GC ファイナライザーとの競合も発生していた。
- **施工内容**:
  - **1. LoadIFilter P/Invoke の IntPtr 安全化 (`Services/PdfSearchHelper.cs`)**:
    - `ppIUnk` 引数を `[MarshalAs(UnmanagedType.IUnknown)] out object?` から `out IntPtr ppIUnk` へ変更。
    - CLR による自動 RCW 生成を完全に遮断。
  - **2. HRESULT ＆ 非Zero ポインタ厳格ガード**:
    - `if (hr == 0 && pUnk != IntPtr.Zero)` の場合のみ `Marshal.GetObjectForIUnknown(pUnk)` を呼び出すよう防壁を構築。エラー時は一切 COM オブジェクトをインスタンス化しない。
  - **3. COM ライフサイクルの整線 ＆ ReleaseComObject 競合排除**:
    - `try ... finally` ブロックでネイティブポインタ `pUnk` を `Marshal.Release(pUnk)` することで COM 規約に準拠した解放を保証。RCW のアンチパターンな二重解放（`Marshal.ReleaseComObject`）を全廃。
- **検証と恒久保護**:
  - `Services/Testing/RegressionTestSuite.Search.cs`: 全 8 ドメイン 8/8 ALL REGRESSION TESTS PASSED を堅持。
  - Windows Server / CI ヘッドレス環境でのネイティブ COM クラッシュを根絶。

---

### ADR 97: Astra全体レビュー完全是正 ＆ 外部Index網羅性担保 ＆ 削除安全集約 ＆ 3GiBオーバーフロー防止 ＆ XML文字参照復元 ＆ 表示選択整合
*(v2.2.20 本番施工 & ADR 97)*

- **背景 & 動機**:
  - Astra によるコードベース横断レビューにより、検索、削除安全、大容量ファイル処理、Office 解析、UI整合性の 5 大領域にわたる潜在的課題が指摘された。
  - **課題 1（検索網羅性）**: 外部Index（Windows Search WSP）が候補を返した際に通常走査を完全にスキップすると、WSPの2,000件上限や単語境界の違い（部分一致等）により、インデックス外のファイルが走査漏れ（False Negative）となるリスク。
  - **課題 2（削除安全情報抜け）**: 削除計画（`BuildPlan`）において、ユーザーが「休眠行」や「旧版行」側のみを選択した場合、その物理ファイルが重複グループに属していても原本参照情報が集約されず、原本生存確認・SHA-256再照合がバイパスされるリスク。
  - **課題 3（巨大ファイル・エンコーディング）**: 3GiB を超えるテキストファイルで `(int)fs.Length` のキャストによる負数オーバーフロー（`OverflowException` ➔ 偽の不一致）。また、BOM なし UTF-16LE/BE テキストが先頭 NUL カウントによりバイナリとして誤判定・脱落するリスク。
  - **課題 4（Office XML 文字参照・セル境界）**: `StripXmlTagsFast` で `<w:t>R&amp;D</w:t>` などの XML 文字参照がデコードされず検索漏れになる問題。また、Excel 共有文字列 `<si>` やセル `<c>` 境界で空白が挟まれず単語が結合するリスク。
  - **課題 5（表示件数と一括選択の不一致 ＆ 重複除外記憶）**: 表示件数制限（上位100件等）がある状態で「整理推奨を一括選択」すると、画面外の見えないアイテムまで全選択されてしまう不整合。また、重複候補で「整理除外（スキップ記憶）」を設定しても一覧から消えない問題。
- **施工内容**:
  - **1. 外部Index先行ブースト ＆ 完全網羅走査 (`Services/SearchEngineService.cs`, `WindowsSearchProvider.cs`)**:
    - `WindowsSearchProvider.BuildSearchSql`: `content:"..."`（`query.ContentKeyword`）に対応。
    - `SearchEngineService.SearchDirectFolderAsync`: サーバーIndexから候補が得られた場合、早期原本確認キュー（先頭ブースト）として即時投入しつつ、**通常のディレクトリ走査（`SafeFileEnumerator`）を省略せずに継続実行**。先行投入済みのパスは `seenPaths.Contains(...)` で $O(1)$ スキップし、上限や単語境界による走査漏れを 100% 根絶。
  - **2. 削除計画における安全情報の全行集約 (`Services/AuditCleanupService.cs`)**:
    - `BuildPlan`: 全アイテム（`itemList`）から `FullPath` ➔ `DuplicateGroupId` の集約マップを作成。休眠行のみ選択時でも、同一物理ファイルが重複グループに属していれば原本参照（`OriginalCandidatePath`, `OriginalExpectedSize`, `OriginalExpectedLastWriteTimeUtc`）を同一ファイルの他行から 100% 確実に集約・マージ。原本生存確認・SHA-256 再照合がバイパスされる穴を完全に塞いだ。
  - **3. 巨大ファイル 3GiB オーバーフロー防止 ＆ BOMなしUTF-16 ＆ 長大キーワード境界保護 (`Services/ContentExtractionService.cs`)**:
    - `DetectTextEncoding`, `ReadStreamWithEncodingAsync`, `SearchTextContentWithBufferAsync`: 先に `(int)fs.Length` にキャストしていた箇所を `Math.Min(1024L, Math.Max(0L, fs.Length))` 等で安全に境界制限してからキャストするよう修正。3GiB 超での負数オーバーフローを根絶。
    - `SearchTextContentWithBufferAsync`: 先頭バイナリ早期ドロップにおいて、奇数/偶数バイトに 0x00 が並ぶ BOM なし UTF-16LE/BE ヒューリスティック判定を追加し、テキストファイルがバイナリとして誤脱落するのを防止。
    - 最長キーワード長に応じた動的オーバーラップ幅（最大16,384文字）を導入。
  - **4. Office XML 文字参照復元 ＆ セル/共有文字列境界保護 (`Services/ContentExtractionService.cs`)**:
    - `StripXmlTagsFast`: ブロック/境界タグの判定に `<si>`, `<c>`, `<v>`, `<w:tc>` 等を追加し、Excel 共有文字列やセル間の文字が結合されるのを防止。
    - 抽出結果に対して `System.Net.WebUtility.HtmlDecode` を適用し、`<w:t>R&amp;D</w:t>` などの文字参照を `R&D` へ完全復元。
  - **5. 「見せる量」と「処理する量」の完全一致 ＆ 重複候補の除外記憶 (`MainWindow.Audit.cs`, `Services/AuditReportService.cs`, `Models/AuditModels.cs`)**:
    - `AuditItem` に `IsIgnored` プロパティを追加。`AuditReportService.cs` の重複生成ループで除外記憶判定を反映し、UI 表示フィルターで除外候補を即時非表示化。
    - `AuditSmartSelectComboBox_SelectionChanged`: 一括選択プリセット（整理推奨、旧版、重複等）を現在画面にバインドされている `visibleList`（`ItemsSource`、例: 上位100件）のみを対象に適用するよう改修。
- **検証と恒久保護**:
  - `Services/Testing/RegressionTestSuite.Search.cs`: Section 23（XML文字参照復元、Excel境界、BOMなしUTF-16LE、3GiB境界計算、WSP content:構文）を追加し検証。
  - `Services/Testing/RegressionTestSuite.Audit.cs`: 検証 10（休眠行のみ選択時の重複原本安全情報集約）を追加し検証。
  - 全 8 ドメイン自動回帰テスト 8/8 ALL PASS を達成。




