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