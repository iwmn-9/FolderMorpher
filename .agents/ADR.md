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
    - `Domain 8`: Search Studio (Everything Parser, In-Memory Fast Match, Content Search)

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

## 14. 【統合ファイル検索】Search Studio（Everything構文・0秒インメモリ＆プログレッシブ直接走査・OpenXMLストリーム全文検索・スタジオ連携Hub）
*(v2.2.0 新規施工 & ADR 60)*

- **0秒インメモリ高速検索 ＆ プログレッシブ直接走査のハイブリッド・アーキテクチャ (`SearchEngineService`)**:
  - **インメモリ走査**: Storage Explorer (Tab 1) で一度スキャン済みのツリー（`FileItemNode`）に対しては、I/Oを一切発生させず完全インメモリ（0ms〜数ms）で走査。
  - **参照フォルダー指定による部分木スコープ絞り込み & 自動フォールバック**:
    - 対象フォルダーが指定された場合、全スキャン済みツリーを無差別に走査せず、指定パスに一致する `FileItemNode` を探索してその部分木のみを起点に走査。
    - 指定フォルダーがスキャン済みツリーに含まれていない場合は、シームレスに直接走査（DirectFolder）へフォールバックし、ユーザーの探索テンポを阻害しない。
  - **プログレッシブ直接走査**: 未スキャンの任意パスやUNC共有に対しては、ADR 35/49 の正本である `SafeFileEnumerator` を貫通させて非同期プログレッシブ走査を実施。進捗（走査件数・ヒット数）をUIへリアルタイム反映。
- **Everything 互換クエリ構文解析器 (`SearchQueryParser`)**:
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
  - 親フォルダーも含めて絞り込みたい場合は、`path:xxx` 構文またはキーワードに `\` を含める（例: `2024\報告書`）ことで、Everything準拠の直感的なパス絞り込みを両立。
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

