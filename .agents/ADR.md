# FolderMorpher — 重要な設計判断の記録（Architecture Decisions / ADR）

> **【AIメンテナ・自律継続規約】**  
> 本ドキュメントは、FolderMorpher の設計判断・障害対策（ADR 1〜124）の正本記録である。退役した方式は理由と継続する契約だけを記し、実装の詳細は Git 履歴に残す。
> ⚠️ **後続のAIメンテナへ**:
> 1. 以下の仕様・設計制約を「不具合」または「改善の余地」と勝手に誤認して安易に巻き戻すことを厳禁とする。仕様変更を行う場合は、該当する中核原則の背景と過去のトレードオフを熟読すること。
> 2. **後続 ADR による意図的な撤去・置換の考慮**: 過去の ADR（特に ADR 71〜86 で扱われた検索専用 FTS5 DB や FileSystemWatcher）は、後の **ADR 87（v2.2.14）において「ローカルDB肥大化・ロック競合ゼロ化」のため撤去され、インメモリ＋Live直接走査＋WSP借用へ一本化** されています。過去の古い ADR だけを読んで廃止されたコードを安易に復活させてはならない。
> 3. **現行検索の正本**: 検索経路は ADR 87・94・95・97・99・100 の後続判断を適用する。旧索引の候補だけで網羅性を判断せず、通常の直接走査では日時による枝刈りを使わない。

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

## 12. 【全方位日英両対応 & 回帰テスト品質ゲート】多言語リソース正本化・8ドメインの回帰テスト
*(旧 継続品質規約 を統合)*

- **日英バイリンガル完全同期**:
  - `Services/AppStrings.cs`（静的辞書）と `MainWindow.Localization.cs` を正本とし、全画面・全モーダル・全出力ファイル（Excel/CSV/スクリプト）の日英完全同期を保証。英語モード時のCJK文字残存をゼロ化。
- **ヘッドレス自己検証 CI ゲート（8/8 ALL PASSED 必須）**:
  - コード変更後は必ず `dotnet run --no-build -- --test-regression` を実行し、以下の8ドメインがすべて PASS することを必須ゲートとする。8/8 は網羅率ではなくドメインの通過数を示す：
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
  - 前後40文字の文脈スニペット（`...キーワード...`）を一致根拠として保持。巨大ファイルにはテキスト判定ガードと分散 Probe を使う（ADR 90・91）。カード内のスニペット表示は ADR 74 で撤去した。
- **全スタジオを束ねる統合 Hub 連携 (`MainWindow.Search.cs`)**:
  - 検索結果の右クリック／ダブルクリックから、FolderMorpher 内の各スタジオへ即座に転送・ジャンプ：
    - `Live ACL Studio`: 当該フォルダーのNTFSアクセス権を即座に可視化・編集
    - `Simulation Studio`: 移行ツリー設計へソースノードとして追加（**ファイル選択時も親フォルダーの `FileItemNode` から配下全体容量・実測ファイル数・DACLを正本継承し、ファイル単体サイズ誤設定を完全根絶**）
    - `LinkFixer`: 内部リンク切れ修復の対象としてセット
    - `Audit & Hygiene`: ファイル健全化・整理の対象としてセット
    - `Explorer で選択`: 標準エクスプローラーを起動しハイライト表示
- **洗練されたスリム UI & ClosedXML による Excel 台帳 & CSV エクスポート**:
  - 余計なプリセットチップを排したすっきりとしたコントロールバー、1行メトリクスバーと仮想化されたフル幅カード一覧。
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
  - **アイデア 1（Excel sharedStrings.xml 優先チェック）**: `.xlsx` の場合、まず `xl/sharedStrings.xml` を確認し、全条件が満たされればそこで終了する。不足する条件があればワークシートも調べる。sharedStrings の非一致だけでシートを除外しない。
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

## 16. 【退役】検索専用 FTS5 の自動ルーティング（旧 ADR 62）

- 当時はローカル FTS5 DB を自動構築し、インデックス済みなら DB、未構築なら直接走査へ切り替えた。DB の肥大化と保守負荷により ADR 87 で撤去した。現行経路は ADR 87・99・100 を正本とする。
- PDF の IFilter 失敗時のフォールバックと日本語短語の検索要件は現行 `PdfSearchHelper` / `ContentExtractionService` に残る。旧 SQL と DB 移行手順は現行仕様ではない。

---

## 17. 【一部継続】未完了インデックスの検索漏れ防止と移行パッケージ安全策（旧 ADR 63）

- `IndexedRoots` の完了判定・亡霊削除は検索専用 DB とともに退役した。元の問題は「不完全な索引を完全な答えとして扱うと検索漏れする」こと。現行の外部索引でも候補を先行処理するだけで全走査を続ける（ADR 94・97）。
- `ContentExtractionService` が本文抽出の正本である。移行パッケージの TargetRoot 必須、Dry-Run、相対ログパス、遅延展開禁止、エラー伝播は第13節の現行契約として継続する。

---

## 18. 【一部継続】二元 FTS5 インデックスの検索漏れ是正と移行バッチ停止（旧 ADR 64）

- 全ファイルのメタデータと本文 FTS を分離し、ZIP・EXE など名前一致の取りこぼしを防いだのは旧 DB の決定。DB は ADR 87 で撤去済みだが、**本文非対応ファイルも名前一致ならヒットする**意味論は現行検索にも残る。
- Shift-JIS の判定と本文抽出は `ContentExtractionService` が正本。移行マスターバッチは Wave ごとにエラーを伝播し、失敗時に後続を止める（第13節）。

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
- **本文エンコーディング判定の正本化**:
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

## 22. 【退役】検索専用 DB の背景差分同期（旧 ADR 68）

- 15分クールダウン、差分同期、自動再検索は ADR 87 で検索専用 DB とともに退役した。旧「↻」操作は ADR 99 で**現在の検索結果を更新**する操作へ変更した。

---

## 23. 【一部継続】検索結果 UI と旧インデックス権限パージ（旧 ADR 69）

- 当時のサイドバー3グループはADR 116で上部タブに変更した。仮想化された検索結果と各スタジオへの導線は継続する。クイック詳細ペインとカード内スニペット枠は後続 ADR で撤去し、現在はフル幅・均一2行の結果行が正本。
- `VerifyAndFilterPermissionsAsync` と `PurgeFilesAsync` は旧検索 DB 専用で、ADR 87・99・100 により現行検索経路から撤去した。外部サーバー索引の候補は原本確認し、通常走査も続ける（ADR 94・97）。

---

## 24. 【継続】ファイル種別表示の単一正本（旧 ADR 70）

- 色・拡張子・表示文字列は `TablerBadgeHelper.GetBadge` が決め、各モデルはその結果に委譲する。画像や外部フォントを増やさず、検索・容量分析・監査で同じ表示規則を使う。
- 当時のフォルダー `DIR` 文字バッジは ADR 73・74 でベクターのフォルダーアイコンへ置換した。現在のフォルダーバッジ文字列は空とする。

---
## 25. 【退役】MetadataFts・ScanGeneration・rowid JOIN（旧 ADR 71）

- 数百万件の旧検索 DB で、名前検索の全表走査と差分同期時の全パス保持を避けるための設計だった。ADR 87 で検索専用 DB を撤去し、現行経路には適用しない。

---

## 26. 【一部継続】Watcher と検索結果の並び替え（旧 ADR 72）

- `ContentIndexWatcherService` による常駐同期は ADR 87・100 で撤去した。MFT の高速列挙は現在 `DiskScanService` のローカル NTFS スキャンで使い、UNC は共通列挙器へ戻す。
- 結果の並び替えは継続する。旧フィルターチップと多数のソート項目は ADR 73 で撤去・整理し、現在は関連度と更新・作成日時の5項目が正本。

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
     - 作成日時は現行のインメモリ検索とライブ直接走査の結果にも保持する。旧 `IndexedFiles` カラムとそのマイグレーションは ADR 87・100 で退役した。
     - 各検索カードの右上に、ファイルサイズ・更新日時に加えて作成日時（薄いグレー表示）を並べて表示。
  3. **インテリジェント関連度スコアリング（True Relevance Scoring）**:
     - 単純なクエリ順ではなく、ファイル名完全一致（+100）、前方一致（+50）、部分一致（+30）、本文スニペット一致（+15）、ディレクトリパス一致（+10）、および直近更新鮮度加点（+1〜5）を多角的にスコア化するアルゴリズムを導入。
     - ユーザーが入力したキーワードに最も合致するファイルを瞬時に先頭にリランキング。
  4. **フォルダー行のベクターフォルダーアイコン換装（DIR文字の完全根絶）**:
     - 四角い枠内に文字「DIR」と描画されていたバッジを完全撤去。
     - Tabler Icons 準拠の上部タブ付き角丸フォルダー形状（`M 3 7 A 2 2 0 0 1 5 5 L 9 5 L 11 7 L 19 7 A 2 2 0 0 1 21 9 L 21 17 A 2 2 0 0 1 19 19 L 5 19 A 2 2 0 0 1 3 17 Z`）をベクター XAML で実装。
     - 温かみのあるアンバーゴールド（背景 `#FEF3C7`、塗り `#FDE68A`、枠線 `#D97706`）で描画し、検索結果一覧（Tab 2）のみならず容量分析（Tab 1）のファイルツリーおよび選択フォルダーの内訳にも全画面展開。
  5. **自動回帰テストによる恒久保護**:
     - 旧テスト内で LINQ の並び替え式を再現するだけのアサーションは ADR 100 で撤去した。`TablerBadgeHelper.GetBadge` のフォルダー描画契約は現行テストで検証する。

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

### ADR 75: 旧 DB の自己登録・Watcher 世代競合と、現在も残る日時・関連度の修正

- 旧 DB 自身の登録、Watcher と全走査の世代競合、フォルダー未登録は検索専用 DB の問題であり ADR 87・100 で終結した。
- MFT で取得する作成日時は `$FILE_NAME` の日時を使用する。関連度スコアは生クエリではなく解析済みキーワードと完全フレーズで計算し、`ext:` 等の構文を点数に混ぜない。

---

### ADR 76: 本文 ON 時の名前一致と MFT 日時の正本化

- **現行の検索意味論**: 本文 ON でも、フォルダーや ZIP・画像・EXE は名前が一致すればヒットする。本文抽出に非対応なら本文一致としては扱わない。
- MFT の `ScannedFileEntry` は作成・更新・アクセス日時を正しい順に渡す。旧 DB の最深 Root 世代解決と Subtree Purge は ADR 87 で退役した。

---

### ADR 77: 本文検索トグルと段階的な結果表示

- 本文検索トグルの切り替えだけで重い検索を自動起動しない。ユーザーの実行操作で開始する。
- 現行検索ではメモリ内の名前・属性一致を先に示し、必要な本文検査は逐次合流する。旧 FTS5 の `onNameHitsReady` コールバックは退役した。

---

### ADR 78: カンマ・パイプ OR と空白 AND

- `見積,請求 2026` は `(見積 OR 請求) AND 2026` と解釈する。引用符内の区切り文字は OR に分割しない。`SearchQueryParser` の `KeywordGroups` が正本。
- 名前と本文のどちらで各グループを満たしたかは `SearchEngineService` の共通判定と本文検査が扱う。旧 FTS5 SQL 変換は退役した。

---

### ADR 79: `content:` 必須条件とフィールドをまたぐ AND

- `content:社外秘` は名前だけの一致を許さず、本文確認後に確定ヒットにする。`契約書 2026` のように、名前と本文で別々のグループを満たすファイルも本文 ON ならヒットする。
- 旧索引の先行表示 JIT 権限照合・SQL `INTERSECT` は退役した。進捗の所要時間は検索全体の Stopwatch を使い、バッチ間隔計測で巻き戻さない。

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

### ADR 81: 構造化された列挙エラーと完全フレーズ

- Win32 列挙エラーは `EnumerationFailureKind` で伝達し、表示文字列やエラー番号を文字列検索して制御しない。適応並列は急なレイテンシ悪化に早期応答する。
- 引用符付き完全フレーズは名前と本文の検索条件として扱う。旧 FTS5 のフレーズ SQL と先行表示 JIT レース対策は退役した。

---

### ADR 82: SlotLease の一回解放と Live 完全フレーズ

- `SlotLease` は参照型と `ReleaseOnce` により `Report()`・`Dispose()` の重複呼び出しでもスロットを一度しか解放しない。並列上限とアンダーフロー防止を維持する。
- 本文 ON の完全フレーズは名前に無くても本文から探す。名前だけで事前に落とさない。旧 Indexed/Direct 比較のうち Indexed 側は退役した。

---

### ADR 83: 本文 ON とフォルダー包含の経路一致

- `IncludeFolders` が ON で、必須 `content:` 条件が無ければ、フォルダー名一致は本文検索 ON でもメモリ内・直接走査の両方で返す。
- 適応並列の緊急ウィンドウをリセットするときは件数と head の両方を戻す。旧 Indexed 経路は退役した。

---

### ADR 84: Aho-Corasick の採用と旧 DB 肥大化の観測

- 多パターンをファイルごとに開き直さず照合し、スニペットを得るため `AhoCorasickSearcher` を採用。現行の直接走査でも使用する。
- 旧検索 DB は実環境で数GBに膨張し、`optimize` + `VACUUM` でも 6.28GB から 6.04GB への縮小に留まった。ツリーからの DB 同期と遅延本文インデックスは ADR 87 で退役した。

---

### ADR 85: contentless FTS5 試行と撤退理由

- 旧 DB の `contentless + detail=none` は小規模実験で 11.61MB から 1.41MB へ縮小したが、trigram の候補は偽陽性を含むため原本確認が必須だった。
- Mode C の SQL、マイグレーション、Watcher は ADR 87・100 で撤去した。**外部索引の候補を確定結果とみなさない**という判断だけが現在の ADR 94・97 に続く。

---

### ADR 86: Live 逐次走査と短い日本語語句の完全性

- `SearchDirectFolderAsync` は列挙 Producer と本文検査 Consumer を bounded Channel で同時進行させ、見つけた結果を逐次通知する。本文検査の正本は `ContentExtractionService`。
- 2文字の日本語を含む短い語句も検索する。**結果を500件で暗黙に打ち切らない**。旧 FTS5 の `LIMIT 500` と SQL 候補救済は ADR 87・100 で退役した。
- テキストと OpenXML は 64KB 順次読みと `StripXmlTagsFast` を使用する。生 XML を語句だけで事前除外すると書式境界をまたぐ一致を見逃すため、ADR 95 でその事前除外を撤去した。

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

### ADR 93: 局所性走査と枝刈りの安全境界

- `SafeFileEnumerator` は LIFO 寄りの局所性走査でサーバー側キャッシュを活用する。単一キーワードは `string.IndexOf(..., OrdinalIgnoreCase)` の高速パスを使う。
- TreeCache のフォルダー日時だけでは既存ファイルの本文更新を検知できない。**通常の検索で枝刈りは無効**とし、明示的なオプトイン API に限る（ADR 95・99）。通常検索の完全性をこの時刻推定に依存させない。

---

### ADR 94: サーバー側索引は候補の先行確認に使う

- `ServerSearchAccelerator` / `WindowsSearchProvider` は WSP 対応サーバーから候補を得る。候補は現在の利用者権限で原本を開いて確認し、早く表示できるものを先行させる。
- **候補が返っても `SafeFileEnumerator` による通常走査を続ける。** WSP の件数上限・単語境界・更新遅延による検索漏れを防ぐための ADR 97 による修正である。非対応・失敗時も通常走査を使う。

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
---

### ADR 98: Sol提唱 I/O Governor 自爆崖落ち解消 ＆ 純粋I/O計測 ＆ AIMD適応並列 ＆ SQLite ローカル専有ツリーキャッシュ ＆ ポータブル JSON 相互運用アーキテクチャ
*(v2.2.21 本番施工 & ADR 98)*

- **背景 & 動機**:
  - 全文検索が UNC/NAS で 12GB/2万ファイルに対して 20分以上かかる問題（ローカル C は 120GB/40万件で 140秒）について、Sol による的確な分析レビューが行われた。
  - **課題 1（Governor の自爆崖落ち）**: `InspectContentItemAsync` 全体の所要時間（Stopwatch）を Governor に報告していたため、Office/PDF の展開や XML パース等の重い CPU 処理（100〜300ms）を「ネットワーク/SMBの遅延悪化」と誤認。即座に `SessionMaxCeiling = 2` に不可逆クランプされ、8 ワーカーを生成しても実質 2 並列で固定されていた。
  - **課題 2（I/O スロット枠の合算拘束）**: `SharedIoGovernor` で `GlobalSlotGate` が合算 4 枠に縛られ、列挙コントローラーと本文コントローラーが干渉していた。
  - **課題 3（重複 RPC オーバーヘッド）**: `OpenBufferedReadStream` や検索走査内で `File.Exists` や `new FileInfo(filePath).Length` による余計なメタデータ問い合わせ RPC が頻発していた。
  - **課題 4（ツリーキャッシュの肥大化と取り回し）**: ツリーキャッシュが単一の巨大 JSON で管理されており、階層が深くなるとメモリフットプリントが増大し、更新時の I/O が重くなっていた。SQLite 化による高速化が望まれる一方、SMB 上に SQLite を置くとロック競合や遅延破損のリスクがあるため、安全な分離設計が求められた。
- **施工内容**:
  - **1. Governor 自爆崖落ちの根絶 ＆ AIMD 適応並列 (`Services/AdaptiveConcurrencyController.cs`)**:
    - `SessionMaxCeiling` の不可逆崖落ち（永久固定）を撤廃し、クールダウン解除後に良好なレイテンシが続けば再昇格可能な AIMD（Additive Increase / Multiplicative Decrease）を導入。
    - インスタンスごとに柔軟な上限設定が可能なコンストラクタ `(min, defaultVal, max)` を整備。本文読み込み上限を `ContentMaxConcurrency = 12` へ拡大。
  - **2. 列挙と本文の I/O ガバナー完全分離 (`Services/SharedIoGovernor.cs`)**:
    - `GlobalSlotGate` の合算 4 縛りを撤去し、ディレクトリ列挙専用コントローラー（安全な 2 固定・上限 2）と本文読み込み専用コントローラー（AIMD: 4 ➔ 最大 12）を完全分離。サーバーの MFT/RAM を 100% 保護しながら、本文パイプラインの充填を実現。
  - **3. 純粋 I/O 時間の計測 ＆ RPC ゼロ化 (`Services/ContentExtractionService.cs`, `SearchEngineService.cs`)**:
    - `OpenBufferedReadStream`: `knownSize >= 0` の場合は `FileInfo.Length` RPC を完全スキップ。
    - ストリームオープンとデータ読み込みにかかった純粋な所要時間のみを `reportIoElapsed` コールバックで測定。CPU 解析（ZIP/XML/PDF 展開、Aho-Corasick マッチング）を除外した「真の I/O レイテンシ」のみを Governor へフィードバック。
    - 事前の `File.Exists` RPC を全廃し、一発オープン＆例外ハンドリングへ統一。
  - **4. SQLite ローカル専有ツリーキャッシュ ＆ ポータブル JSON 相互運用 (`Services/SqliteTreeCacheService.cs`, `StorageHistoryService.cs`)**:
    - **完全ローカル専有**: `%LocalAppData%\FolderMorpher\TreeCache\tree_cache.db` にのみ DB を配置。WAL モード（`PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;`）により超高速・高耐障害性を確保。共有フォルダー（UNC）には一切 DB ファイルを置かず、ロック競合や遅延破損を原理的ゼロ化。
    - **事前集計 Materialized**: 各フォルダーノードにスキャン集計値（`TotalSizeBytes`, `FileCount`, `FolderCount`）をカラムとして事前保存。深階層でも再帰クエリ不要で $O(1)$ 高速遅延ロードを実現。
    - **単一トランザクション一括コミット**: 数万ノードでも単一トランザクションでバルクインサートし、0.1〜0.3秒で保存完了。
    - **DB 直接 SHA-256 更新**: `UpdateSha256Async` により、全ツリーをメモリ展開することなく DB 上で直接 UPDATE（メモリ浪費ゼロ）。
    - **ポータブル JSON 相互運用**: `ExportToJsonFileAsync` / `ImportFromJsonFileAsync` を実装。社内共有や他 PC への配布は従来の単一 JSON 形式で完全両立。既存 JSON キャッシュからの自動透過マイグレーションも完備。
- **検証と恒久保護**:
  - `Services/Testing/RegressionTestSuite.Storage.cs`: Section 24（SQLite 保存・復元、JSON エクスポート・インポート往復一致、DB直接 SHA-256 更新、AIMD 昇格・降下、純粋 I/O 時間追跡）を追加し検証。
  - 全 8 ドメイン自動回帰テスト 8/8 ALL PASS を達成。

---

### ADR 99: 検索の現行経路明確化と逐次走査の全件保持解消

- **背景**: ADR 87 で検索専用 FTS5 DB を通常画面から撤去した後も、旧サービスと履歴回帰テストが現行検索と同じ場所に残っていた。また、`SafeFileEnumerator` は逐次コールバックと同時に全エントリを `ConcurrentBag` へ保持し、検索が返却一覧を使わない場合もメモリを消費していた。
- **決定**:
  - 旧 `ContentIndexService` と `ContentIndexWatcherService` は、既存の履歴回帰テストを維持したまま `Services/LegacySearch/` と専用名前空間に隔離する。現行検索の入口は `MainWindow.Search.cs` → `SearchEngineService` とする。旧コードを現行機能の修正対象と誤認しない。
  - `SafeFileEnumerator.EnumerateFileEntriesParallelAsync` に `collectResults` を追加。既定値 `true` で既存の監査・メディア等の返却契約を維持し、検索のみ `false` を指定して逐次コールバックと全件蓄積の重複をなくす。
  - 通常検索では枝刈りが既定で無効なので、直接走査開始時の TreeCache 読込と無効な `TreeCachePruningIndex` 構築を撤去する。ツリーキャッシュを使ったインメモリ検索経路は維持する。
  - ツリー検索と直接走査で重複していた名前・パス・本文条件の判定を `SearchEngineService.MatchesSearchTerms` に集約する。元データのサイズ・日時の扱いは各経路のまま保つ。
  - 旧DBを更新しない検索画面の `↻` ボタンを「検索結果を更新」に改称し、未使用のインデックス表示文言・JITパージ用メソッド・スコープ列挙型を除去する。
- **検証**: 現行のツリー／直接走査の一致条件と、コールバックのみの列挙を検索回帰テストで確認する。ビルドと全8領域の回帰テストを実行する。

---

### ADR 100: 退役した検索専用 DB とテストの撤去・判断の正本化

- **背景**: ADR 99 で隔離した `LegacySearch` の約2,764行は通常画面から参照されず、旧 FTS5 / Watcher テストを通すためだけに残っていた。検索テストにも旧方式と現行経路が混在し、後続AIの読み取りとビルドを重くしていた。
- **決定**: 旧2サービス、旧方式の比較だけを行う `TrigramBenchmark` と CLI オプション、専用回帰テストを撤去する。`content:` 必須条件、名前と本文をまたぐ AND、完全フレーズ、フォルダー包含、2文字日本語と500件超の結果は現行 `SearchEngineService` を直接通すテストとして保つ。テスト内で計算式を再現するだけの検査は削る。検索テストは構文・現行経路・照合基盤・WSP/抽出で分割する。
- **文書の正本**: 退役した SQL・Watcher・世代管理の詳細は Git 履歴に委ね、ADR 16〜18・22〜26・75〜85 は廃止理由と継続する意味論に圧縮した。サーバー索引は候補の先行確認に使いつつ全走査を続け（ADR 94・97）、通常検索の TreeCache 時刻枝刈りは無効（ADR 93・95・99）と明記する。
- **網羅性の回帰テスト**: サーバー索引のモック候補を先行処理しても、候補に無い一致ファイルを通常走査で発見し、候補ファイルを二重表示しないことを現行検索で確認する。
- **検証の意味**: 回帰結果の `8/8` は8ドメイン通過数であり、コード網羅率や全仕様の完全性を表さない。

---

### ADR 101: GUI と業務処理のプロセス境界

- **決定の核**: GUIは表示と入力を担当し、走査・検索・ACL・監査・移行・DB操作をHost経由でCoreへ送る。CoreはWPF型に依存しない。変更系はPlan Firstを維持する。
- **更新**: 当初の別 `Host.exe` 配布案とCore内Contracts案はADR 102で撤回。過去案を現行構成として実装しない。

---

### ADR 102: 1 EXE・2プロセスモードとContracts境界

- **要求**: 配布は `FolderMorpher.exe` 1ファイル。通常起動はGUI、`--host` は同じバイナリのHostプロセス。旧別Host EXE案を廃止。
- **ソース依存**: `FolderMorpher.UI -> FolderMorpher.Contracts <- FolderMorpher.Host -> FolderMorpher.Core`。ルート実行プロジェクトがUIとHostを参照し、起動引数で実行モードを分ける。UIにCoreのProjectReferenceはない。共有事実モデルは同一ソースを両アセンブリでコンパイルし、移した表示プロパティはUIのpartial classに置く。
- **IPC**: `IFolderMorpherHostService` と機能別DTOはContractsの正本。親参照のあるツリーをそのままシリアライズしない。Named PipeはSID・セッションごとの名前と `CurrentUserOnly` で閉じる。Host未起動ならクライアントが同じEXEを `--host` で起動する。
- **処理所有権**: 検索・監査・スケルトン展開・移行パッケージ・実効権限監査はHost Job APIで開始・状態取得・中止・解放する。GUIを閉じてもHostが所有する。設定JSON、キャッシュ、エクスポートもHostが扱う。フォルダー作成とACL変更はHostで計画とコミットを分ける。
- **表示境界**: Coreモデルの色・アイコン・余白・サイズ表示はUI側へ移し、監査・実効権限レポートと画面で共通の色が必要な箇所は単一のPaletteサービスを両プロジェクトでコンパイルする。移行差分の色は文字列で判定せず `SimDiffKind` から導出する。
- **確認済みの限界**: ローカライズ済みのレポート文言・ラベルは一部Coreモデルとサービスに残る。UIはCoreモデルと検索構文解析のソースを共有コンパイルするため、論理上の全モデル依存までContractsへ移した状態ではない。次の分離ではレポートの文言正本とUI表示を整理し、現行の出力意味論を保つ。
- **検証**: Releaseビルド0警告0エラー、Core回帰8/8、publishした単一EXEの `--test-ipc` で別PIDのHostを起動し、実際のDTO往復を確認する。8/8は網羅率ではない。

---

### ADR 103: 大規模ツリーの階層単位ロード

- **背景**: `C:\` の実キャッシュは約153万ノードに達した。従来の起動時 `LoadTreeAsync` は全行をメモリツリーへ復元し、さらに全件をRPC DTOへ変換してGUIへ送っていた。ADR 98の事前集計は成立していたが、GUIの読込経路が全件処理のため、フォルダー展開前に待たされた。
- **決定**: 容量分析GUIの初期表示はルートと直下だけ、展開時は親ノードの索引で対象フォルダーの直下だけ読む。スキャン直後の返却DTOも同じ一階層に制限し、全ツリーはHostが保持・保存する。`HasUnloadedChildren` は展開可能性を表し、GUIは子取得後に解除する。検索・JSONエクスポート等、全ツリーが必要な経路は維持する。
- **整合性**: 子のサイズ・ファイル数・フォルダー数・全体占有率はDBの事前集計を使う。選択フォルダーの容量上位ファイルはHostの全ツリーまたはDB範囲検索で求め、GUIに見える一階層だけから誤集計しない。共有JSONが新しい場合のインポート判定も階層ロードに適用する。

### ADR 104: ツリーキャッシュの親ID化と旧DB移行

- **背景**: 実C:\キャッシュ約153万ノードのDBは約991MB。`TreeNodes` の `ParentPath` が約1.47億文字、`(RootId, ParentPath)` 索引が約202MBを占め、`FullPath` と親パスを重複保持していた。空きページはほぼゼロで、VACUUMだけでは減らない。
- **決定**: `ParentPath` を `ParentId` に替え、直下の取得を `(RootId, ParentId)` 索引で行う。`FullPath` とその索引は階層の特定、SHA-256更新、部分木の上位ファイル取得に必要なので維持する。全ツリー復元は親IDから接続する。冗長な単独 `RootId` 索引は複合索引で代用する。
- **後続変更**: `FullPath` とその索引の維持判断はADR 105の実DB計測と代替経路の実装により撤回した。
- **移行**: 旧スキーマはトランザクション内で新表にコピーし、親が解決できない行があれば元表を残して失敗させる。成功後に旧表・索引を落とし、VACUUMで空きページを回収する。既存Hostが旧スキーマを操作しないよう、GUIはHostの実行バージョンを確認し、異なる場合は接続を拒んで再ログインを案内する。
- **検証**: 旧形式の合成DBからの移行、階層とSHA-256の保持、回帰8/8を確認。実DBは約991MB→587MB（約41%削減）で、C:\ルートの1,527,447ノード、合計容量2,809,919,586,946バイト、フォルダー件数を維持し、`C:\Users` の直下取得は約0.1msだった。
- **検証**: 小ツリーのDB分岐・IPC往復を回帰試験に追加。約153万ノードのローカルキャッシュで最上位表示と`Users`の展開を撮影・確認した。これらはDB保存時間や全機能の性能保証を意味しない。

### ADR 105: パス重複を除いたツリーキャッシュと逐次検索

- **背景**: ADR 104後の実DB約165万ノード・651MBでは、`FullPath` 本体と `(RootId, FullPath)` 索引が容量の大半を占めた。Host再起動後のキャッシュ検索と再スキャン差分は旧ツリー全件をメモリ復元し、容量分析の応答を遅くしていた。
- **決定**: ノードは `RootId, ParentId, Name` からパスを復元し、`FullPath` とパス索引を削除。旧データに親子パスが一致しない行だけ `PathSuffix` を保存して元パスを保持する。`IsExpanded` はGUIの一時状態、`Level` は親から再計算する。日時は `DateTime.ToBinary()` のINTEGER、正規64桁SHA-256は32バイトBLOBとし、互換用の非標準ハッシュ文字列はそのまま保持する。
- **速度**: `(RootId, ParentId, Name)` で直下とパスを引く。DFS挿入順からフォルダーの `SubtreeEndId` を記録し、部分木Top10は連続ID範囲を走査する。`(RootId, Id)` は全ノード逐次走査用。キャッシュ検索と再スキャン差分はDB行をストリームで読み、旧全ツリーの復元を避ける。検索条件の正本は引き続き `SearchEngineService`、UNCのI/O制御は既存ガバナーとする。
- **移行**: 旧表から新表へ単一トランザクションでコピーし、件数・親リンク・DFS順を検証してから切り替え、VACUUMで旧ページを回収する。実行中の旧Hostが所有するDBには触れない。初回起動時に移行が必要なため、既存GUI/Hostの終了後に新バージョンを起動する。
- **検証範囲**: 実DBコピーの約165万行でSQLite整合性、親リンク、共通行の旧パスとの全件比較（不一致0）、部分木Top10一致を確認。約651MB→約199MB。`C:\Users`のTop10 SQLは旧約0.86秒から新約0.20秒、C#の直下取得は約0.02秒、C:\ルート約153万件の不一致キャッシュ検索は約2.7秒（同じ端末のローカルコピーでの測定）。合成DBと8領域の回帰試験も通す。数値は他の端末やUNC性能の保証ではない。

### ADR 106: 検索Jobの停止とHostの明示終了

- **背景**: クリア時の入力変更イベントがデバウンスを再起動し、空条件で全件検索が走った。停止ボタンはキャンセル要求後も画面を即時に戻さず、キャッシュ検索は直接RPCでHost側の実行停止が確実ではなかった。常駐Hostの終了はタスクマネージャーに頼っていた。
- **決定**: GUIのLive検索とキャッシュ検索をともにHost Jobへ統一する。入力変更・停止・クリアで旧CancellationTokenをキャンセルし、世代番号を進めて旧応答を無視し、停止表示を即時反映する。空条件では検索を開始しない。キャッシュ照合の正本は従来の `SearchInMemoryAsync` / `SearchEngineService` のまま使う。
- **終了操作（当時）**: 環境設定に「アプリとHostを終了」を設け、稼働Jobがあれば確認の上キャンセルする。Hostは新Job受付を止め、RPC応答を返してからNamed Pipeを閉じて正常終了する。通常のウィンドウ終了は従来どおりHost Jobを継続する。単一EXE・二プロセス境界は維持する。現在の操作はADR 118で更新。
- **検証**: IPC試験にGUIのClear/Stop、キャッシュJob、試験が起動したHostの正常終了を追加。試験は専用Named Pipe・Mutex・一時SQLiteを使い、普段のHostとDBから隔離する。Releaseビルド0警告0エラー、8領域回帰、DLLとpublish後の単一EXE双方のIPC往復を確認。

### ADR 107: PDF IFilter のネイティブ呼び出し定義

- **事実**: `query.dll` の `LoadIFilter` は `path, outer, out interface` の3引数。旧P/Invokeは存在しない `riid` 引数を入れた4引数で、COM最終化時に間欠的なAccessViolationが出た。Microsoftの[定義](https://learn.microsoft.com/en-us/windows/win32/api/ntquery/nf-ntquery-loadifilter)へ合わせる。
- **決定**: `LoadIFilter` の全3呼び出しを正しい宣言に統一する。既存のテキスト抽出・検索のフォールバックとCOM解放は維持する。
- **検証**: 修正後のReleaseビルドと検索を含む8領域回帰を3回実行し、AccessViolationの再発がないことを確認。間欠的な不具合の絶対不存在を保証するものではない。
- **後続是正**: 検索回帰8/8表示後の終了時に、COMラッパーのFinalizerで間欠的な `NullReferenceException` と終了コード1を観測した。RCWの `ReleaseComObject` 明示呼び出しを撤去し、`LoadIFilter` が返した元ポインターは `Marshal.Release`、RCWは `GC.KeepAlive` 後にランタイム管理へ委ねる。修正後の8領域回帰を連続3回、すべて終了コード0で確認した。

### ADR 108: 通常検索の非管理者権限要件

- **要求**: 基本検索は一般ユーザー権限で成立させる。管理者権限が必要な手段は検索用途の必須経路に採用しない。
- **適用**: USN変更ジャーナルを、通常検索の差分検出・索引鮮度判定・検索結果の完全性を支える前提から外す。ローカルとUNCの両方で、非管理者権限の検索経路を維持する。
- **未決定**: 除外用索引の更新方法は実装・計測前であり、メタデータが同一というだけで内容の不変を証明した扱いにはしない。鮮度を確認できないファイルを古い索引だけで除外しない。

### ADR 109: 索引なしの非一致証明を先行調査

- **優先順位**: 永続的な除外用CプランIndexは保留する。先に、現行検索の判定・抽出と同じ意味で「確実に不一致」と言える軽量判定を調べ、効果を測る。想定除外率を実測値として扱わない。
- **安全条件**: 判定は `DefinitelyNo` または `Unknown` とし、失敗・未対応・古いTreeCacheメタデータは `Unknown` に倒す。ファイルの一部を読んで一致しなかっただけでは除外しない。名前・パスで充足した条件と本文必須条件を分け、本文に必要な語だけに判定を適用する。通常のUNC走査は同時更新に対する原子的スナップショットではないため、「検索開始から終了まで絶対不変」とは表現しない。
- **現状**: 拡張子・名前・属性の絞り込みと、Office ZIP内の対象XMLだけを展開する処理は既にある。ただし20MB以下のOfficeファイルは先に全バイトをメモリへ読むため、ZIP目次だけの判定では現状のUNC転送量を減らせない。プレーンテキストの先頭NULによる早期脱落は厳密な非一致証明ではない。
- **先行整線**: Live検索開始時に、既定で無効な枝刈りIndexのためだけにTreeCache全体を読む残存経路を撤去した。Officeの検索対象XML判定を抽出とLive検索で共通化し、Wordのヘッダー・フッター・文末脚注を追加した。これだけでOffice全形式の可視テキスト網羅を証明したとは扱わない。
- **計測方針**: 追加判定の有無で、候補数だけでなくファイルオープン数・実読込バイト数・SMB往復を含む時間・誤除外件数を比較する。UNCの既存I/Oガバナーを守り、Officeの部分読みは全体先読みとの実測比較で採否を決める。PDFの構造的除外は現在のIFilter/フォールバック双方と等価性を示せるまで導入しない。
- **I/O実験の順序**: まずベンチ専用計測で `Open` と後続 `Read` の回数・要求/返却バイト・レイテンシ分布を取り、形式・サイズ階級別に集計する。ラッパーで数えられるのはアプリのRead呼び出しであり、SMBプロトコル上の要求数と同一視しない。現行の適応制御は1ファイルにつき1サンプル・処理件数/秒を見ているため、ファイル全体のRead合計時間をそのままレイテンシとして投入しない。64KB〜大きなバッファの比較、Officeの全体先読み対選択読込、I/O/解析ワーカー分離は、実測で支配要因を確かめた後に個別に判断する。OS・サーバー側のSMB設定は変更しない。
- **比較対象**: 改善幅はFolderMorpherのHost検索を使い、まずCドライブ全体に対する実在する本文語（例: `content:SubtreeEndId`）で測る。ファイル名検索を本文I/Oの代理にしない。検索結果・走査範囲・アクセス不能件数・経過時間・初回ヒット時間・Open/Read数とバイト数を併記し、実行順を交互にしてキャッシュの偏りを抑える。UNC実測ができない環境では、ローカルの結果をSMB性能の実証と呼ばない。
- **GUI経路の修正**: キャッシュ済み対象に `content:` のみを指定した場合、従来は `SearchContentMode` が偽なのでLive検索を起動せず空結果で終わった。`HasDeepFileIoRequirement` を正本として本文・Officeリンクの原本検索を起動し、Officeリンク条件が残るクエリを名前だけのキャッシュ先行表示へ流さない。
- **小さな実装改善**: テキスト本文検索の64KB文字バッファをプールで再利用し、返却時にクリアする。単一語はバッファ上の `ReadOnlySpan<char>.IndexOf(..., OrdinalIgnoreCase)` で直接照合し、ヒット時にだけスニペット用文字列を作る。従来のファイルごとの大きな配列と非一致チャンクごとの文字列割当を避ける。速度改善率はCドライブ全体の同一本文クエリで実測するまで未確定とする。
- **Readサイズ調整候補**: 現行のテキスト経路はFileStreamとStreamReaderのバッファを64KB固定にしている。`reportIoElapsed` は本文Read全体のp95を示さないので、その値だけを使って自動昇格しない。まずアプリ層の実Read回数・返却バイト・所要時間・初回ヒット時間を同じ本文クエリで計測する。64KB/256KB/512KB/1MBをファイルサイズ階級ごとに比較し、十分大きい非一致ファイルでのみ昇格を検討する。UNCでは既存のルート単位の同時実行制御と未処理バイト上限を守り、SMB要求の実サイズとアプリのReadサイズを同一視しない。優位性が確認できるまで64KBを既定とする。

### ADR 110: 初回本文検索のRead幅と確実な早期終了

- **採用**: 永続Indexや検索履歴を使わず、`AdaptiveTextReadController` を検索セッションごとに生成する。8MiB未満のテキストは従来の64KiB固定。大きなファイルの完全な非一致走査から、アプリ層の読込文字数・`ReadBlockAsync` 所要時間・直近Readのp95を集め、16Mi文字ごとに64→256→512→1024KiBの`FileStream`バッファを試す。5%以上の実効速度増加と読込遅延条件を満たさなければ昇格を止め、10%以上の低下または遅延悪化では前段へ戻す。UNC/ネットワークドライブは最大256KiBとし、既存の同時実行ガバナーを維持する。これはアプリの先読み幅であり、SMB READ要求そのもののサイズを保証しない。
- **初見で効く整線**: Officeの既存検索対象XMLは主要本文・スライド・シートを先に検査し、残りも必ず検査する。非一致ファイルの検索理由文字列は作らず、ヒット時だけ生成する。テキスト先頭判定用の1KiB配列はプールで再利用する。単一語のspan照合、複数語のAho-Corasick一走査、`SequentialScan`、条件充足時の早期終了は既存の実装を使う。
- **不採用**: UTF-8 byte列だけの事前除外はUTF-16/CP932等と大文字小文字照合の意味が揃わず、非一致証明にならない。AND希少語順は履歴または別パスを要し、現行の複数語一走査を崩す根拠がない。無バッファI/Oはアラインメント制約とキャッシュ喪失のリスクがあり、現段階では使わない。
- **検証**: `content:SubtreeEndId` を `SearchEngineService.SearchDirectFolderAsync("C:\\", ...)` でCドライブ全体に実行し、変更前後の時間・累計割当・ヒット集合ハッシュを比較する。これは実際の本文検索エンジンだが、GUI/Host IPCの時間は含まない。UNC性能はこの測定から断定しない。
- **Cドライブ全体の比較**: v2.2.29初回763.946秒、Read調整＋Office順序だけの中間版760.038秒、最終版1回目716.722秒、v2.2.29再測488.179秒、最終版再測758.307秒。全回8件・同一結果集合SHA-256 `14D0E5EF...38BDDF`。後半の旧版・最終版のCPU時間は327.250/376.625秒、累計割当は87,387,046,488/87,314,852,416バイト。実行順による振れ幅が変更差より大きく、全Cにおける速度向上も回帰もこの測定だけでは確定できない。大きなテキストのRead幅選択が昇格後も末尾ヒットを失わないことは回帰テストで確認済み。UNC性能は未計測。

### ADR 111: 到達不能UNCでWindows Search OLE DBを開かない

- **再現**: `WindowsSearchProvider.QueryCandidatesAsync` に存在しないUNCサーバーを渡すと、接続失敗後のOLE DB COM最終化で `AccessViolationException` が起きた。単独プローブで1回目に再現し、全領域回帰でも検索領域中に間欠的にプロセスが終了した。PDF IFilterにも同じ最終化スタックが見え得るため、スタックだけで原因を決めない。
- **決定**: `Directory.Exists` で対象の到達性を確認し、不在なら候補加速を行わず通常の直接検索へ戻す。UNCへのメタデータ確認は検索開始時に1回。検索結果の完全性は従来どおり直接走査側が担う。
- **検証**: 存在しないUNCへのクエリと強制GCを回帰テストへ追加する。単独プローブでは同じクエリとGCを10回繰り返して異常終了がないことを確認した。

### ADR 112: 検索途中結果・累計件数・共有I/O予算と主体の同一性

- **検索の表示契約**: Coreの既存 `batchYield` をHost Jobへ通し、`GetSearchJobResultsAsync(jobId, sequence, limit)` で途中ヒットをGUIへ渡す。送信用履歴は2048件を上限とし、カーソルが遅れた場合も完了時の全結果で必ず照合する。GUIはパスで重複除去したキャッシュ先行結果とLive結果の件数を表示し、個別Jobの進捗件数で累計を上書きしない。停止・クリア後の遅延通知は世代番号で無視する。
- **不完全な探索の表示**: 列挙不能フォルダーと本文処理で外へ出た例外の件数をHostから返し、Live検索の完了表示に添える。Officeとテキスト検索は内部例外を非一致として飲み込まない。PDF等の抽出器内部で完結する失敗は依然として全件検出できず、0件でも完全網羅の証明とはしない。
- **I/OとACL**: 大きなファイルの再検査で同一ContentControllerを二度取得する経路を撤去する。共通列挙器のContentController取得も外し、ネットワーク容量スキャンと検索列挙は同じ共有先のEnumerationControllerを使う。未使用の`GlobalSlotGate`と`AcquireSlotAsync`を削除し、実際の列挙2・本文12の上限をテストする。別ドメインの同名ユーザーを現在ユーザー扱いしない。修飾名のAD検索結果は要求SIDと一致する場合だけ採用し、LDAP検索条件へ入れる名前・DNはエスケープする。
- **検証**: Releaseビルド0警告0エラー、8領域回帰、実Named Pipeの検索途中バッチ・連番・キャッシュ名一致とLive検索のGUI件数・不正Officeファイルの未読報告・停止を確認する。UNCの実サーバー性能は未測定。

### ADR 113: 整理候補スコアの内訳を一覧から直接確認する

- **表示**: 監査一覧の「整理の目安」に判定ラベル、点数、「内訳」ボタンを並べる。従来の小さな情報アイコンとバッジのMouseDownだけでは操作が見つけにくかった。理由欄は判定理由に限定し、点数の根拠は内訳ボタンから開く。
- **意味**: `AuditItem.ScoreBreakdown` を根拠の正本に保つ。完全重複の理由は原本以外にSHA-256一致で95点、原本候補に0点を与え、両方に内訳を付ける。点数は整理候補の優先順位で、削除してよい確率ではない。削除可否は従来どおり `IsCleanable` と実行計画・再照合で判断する。複数理由の合算はADR 114。
- **言語と検証**: 内訳の要約と行操作を日英で表示し、言語変更時は監査行の表示プロパティを通知する。重複検出の実行結果でスコアと内訳の一致を回帰検証する。

### ADR 114: ファイル単位の加算優先度と容量順の監査途中表示

- **点数と候補の単位**: `AuditCandidateComposer` は同一物理パスの監査理由を1行へまとめる。`IssueTypes` と `ScoreBreakdown` に全理由を保ち、優先度点数は理由別点数の合計とする。100点では切らない。重複コピー95点＋3年休眠70点は165点。点数は削除可能性の確率ではなく優先順位で、原本候補の削除拒否は他理由と合算しても解除しない。従来の単一 `IssueType` は色・ソートの主分類として残す。
- **集計と出力**: 分類別KPIは各理由を数えるが、削減見込み容量は物理パスを一度だけ加算する。GUI分類フィルターと一括選択は `HasIssue` を使う。最終一覧とCSV/Excelは削除可能候補を先、合算点数と容量を降順にし、原本候補は後ろへ置く。CSV/Excelに合算点数と内訳を出力する。
- **途中表示**: 世代・休眠などの安価な判定を先に済ませ、サイズ別・クイックハッシュ後のフルSHA候補を容量降順に処理する。ハッシュ検証を終えたグループの候補と、ハッシュ対象外で判定済みの容量上位300候補をHost Jobの連番・上限2048件リングからGUIへ送る。初期上位300件の選択には固定長ヒープを使い、全候補の追加ソートを避ける。GUIは物理パスで上書きし、走査中は容量降順で暫定表示する。最終報告で全件を照合して優先度降順に確定する。途中表示中は閲覧・絞り込み・選択を許可し、削除計画と出力は最終報告IDができるまで無効化する。
- **検証**: 2サイズの重複グループで大きい方の先行配信、165点の内訳、同一パス1行、原本保護、容量の重複加算防止を回帰試験する。Named Pipe経由の途中バッチと最終DTO、GUI暫定容量順を統合試験する。UNC実機の時間改善率は未測定。

### ADR 115: 単一EXEの配布出力を dist に統一

- **背景**: 手元のpublish先が `publish`、`publish-single`、版番号付きディレクトリに分かれ、CIだけ `dist` を参照していた。古い成果物を誤配布しやすい。
- **決定**: リポジトリ直下の `dist/FolderMorpher.exe` を唯一の配布出力とする。`FolderMorpher.csproj` の既定 `PublishDir`、手元のpublish・Google Drive同期・IPC試験、CIをこの場所に揃える。過去の配布ディレクトリと `bin` 内の旧 `AstraSize.*` 成果物はゴミ箱へ移す。`AstraSize` 名前空間と `StartupObject` は現行ソースの型名であり、この整理では変更しない。
- **検証**: Release単一EXEを `dist` に生成し、直下が `FolderMorpher.exe` の1件だけであること、既定 `PublishDir` が `dist` を指すこと、配布EXEの `--test-ipc` が終了コード0で完走することを確認した。

### ADR 116: 参照フォルダー起点の上部タブと共通ダイアログ

- **画面構成**: 常設左サイドバーを廃止し、検索・容量・整理・メディアを先頭に、権限・移行・リンクを続ける上部タブへ変更する。各作業を同時に詰め込まず、タブで切り替える。参照フォルダーの切替はその下の独立した操作とし、最近の場所、登録済みフォルダーの階層、UNCパス直接入力、OSフォルダーピッカーを用意する。容量分析の旧内部タブは非表示とし、既存の複数キャッシュは上部スコープから再選択できる。
- **正本**: `MainWindow.Scope.cs` の選択パスを画面上の作業スコープとし、検索・容量・整理および関連する入力欄へ同期する。選択の変更で旧スコープの検索結果を消し、検索語は保持して勝手に再検索しない。`AppSettingsDto.ActiveScopePath/RecentScopePaths` をHostの設定へ保存する。GUIは直接列挙せず、Hostの `BrowseChildFoldersAsync` が要求した階層だけ最大256フォルダーを列挙する。UNCの深い先読みや再帰走査はしない。
- **見た目と確認**: WPFアプリ内の確認・警告・エラーは `AppDialog` に統一し、既存の `MessageBoxResult` を維持する。共通のTextBoxとScrollBarを角丸・細線・低彩度で整える。`OpenFolderDialog` とファイル入出力のコモンダイアログはOS管理の選択UIとしてそのまま使用する。既存の変更計画・確認・検証の安全フローは変えない。
- **検証**: 0警告0エラービルド、8領域回帰、実Named Pipeでのフォルダー一階層往復と共通ダイアログの結果、検索・容量・整理・スコープポップアップのヘッドレス画面撮影を行う。IPC試験時は設定ファイルもGUID付き一時パスへ隔離し、実ユーザーの参照フォルダー履歴を書き換えない。

### ADR 117: フォルダー操作と管理の二層ナビゲーション

- **変更（当時）**: ADR 116 の上部タブ配置を更新する。上段は「一般用途」と管理機能（権限・移行・リンク）、中段は参照フォルダー、下段は選択フォルダーに対する検索・容量・整理の横並び操作とする。管理画面では下段を折り畳み、整理内で「整理候補」と「メディア最適化」を切り替える。切替で作業状態を破棄しない。検索エンジンの対象は現時点で単一フォルダーであり、複数フォルダー横断を見せかけるUIは置かない。管理メニューの現行配置はADR 119で左バーへ更新。
- **表示部品**: 言語選択を設定パネルへ移す。全ComboBoxに角丸の本体、候補行、Popupを共通適用し、選択値と既存イベントを維持する。OS管理のフォルダー・ファイル選択ダイアログはADR 116の扱いを維持する。
- **検証**: 一般用途・整理候補・メディア・管理・設定・候補Popupを実レンダリングで確認し、0警告0エラーのビルドと既存回帰試験を通す。
- **実行中Hostとの整合**: HostがGUIより旧版で実行中Jobが0件の場合だけHostへ正常終了を依頼し、新版を自己起動する。Jobがある場合やGUIが旧版の場合は中断・版の巻き戻しをせず、起動失敗の理由を画面に出す。画面撮影は試験専用Pipe・SQLite・設定で行い、通常のHostとユーザー設定を変更しない。

### ADR 118: 参照フォルダーの直接操作と任意のHost同時終了

- **背景**: 参照先と検索範囲の繰り返し表示、離れた追加ボタン、独立カード状の操作タブが検索画面の視線を散らしていた。Host終了の赤い専用ボタンも、普段のウィンドウ終了と別の操作を強いていた。
- **画面（当時）**: 参照フォルダーを目立つ選択ボタンにし、その隣に追加ボタン、同じ列に最近使った場所を置く。選択ボタンやチップをクリックすると作業先を切り替え、ホバー中の×は登録済み参照先と対応する容量タブから外す。実フォルダーとTreeCacheは消さない。検索画面の「このフォルダーで」と範囲パンくずを撤去し、検索・容量・整理は一体のセグメント切替へ整える。検索TextBoxの本文領域を縦中央揃えにする。検索実行ボタンは実行中に同じ位置の「中止」へ変わり、中止または完了時に「検索」へ戻す。別の中断ボタンは置かない。最近の場所チップと×の位置はADR 119で更新。
- **終了**: 赤い「アプリとHostを終了」を撤去し、設定へ `CloseHostOnWindowClose` を追加する。初期値オフで、通常の×ではHostとJobを継続する。オンの場合は設定保存後、既存のHostだけへJobキャンセル付き終了を要求する。Host未起動時に新しく起動しない。設定値はUI→Contracts→Host→Core設定JSONで往復する。
- **検証**: 0警告ビルド、8領域回帰、IPCで設定値の往復・検索ボタンの検索→中止→検索・既存Hostの正常終了、隔離されたクライアント画面撮影で参照先の登録解除・再選択と設定画面の表示を確認する。

### ADR 119: 一般向けの広さと情シス向け左ナビの分離

- **画面モード**: FolderCleanerの一般向け画面は上部の「一般用途」と左ナビを表示せず、参照フォルダーと検索・容量・整理を広く使う。情シス向けFolderMorpherは左バーに「一般用途／フォルダー操作」と「管理者用／権限・移行・リンク」を置く。既存の `NavTabGeneral/LiveAcl/Simulation/LinkFix` を左へ移し、作業種別の `NavTabSearch/Storage/Audit` は一般用途の下段に保つ。モード切替の正本は既存の `ClientModeState`。一般向けの初期表示で管理バーが一瞬出ないよう、Window生成時点で折り畳む。
- **参照先の解除**: ADR 118で導入した右側の最近の場所チップと、閉じた選択ボタン上の×を撤去する。誤操作を避け、展開中の最近の場所の行にホバーした時だけ×を表示する。解除は登録・対応する容量タブだけを対象とし、実フォルダーとTreeCacheは保持する。
- **操作配置**: 検索入力は全幅のまま、検索／中止・クリア・更新を次の行の左へ置く。容量スキャン、整理候補の発見、メディア走査・チェックも左から開始する。整理の完全削除は走査・出力群から離して右端に置く。動作、Job、出力の意味論は変更しない。
- **検証**: 0警告ビルド、8領域回帰、実IPC往復、一般向け・情シス向けの画面撮影、展開一覧での×の表示と登録解除を確認する。

### ADR 120: 一覧領域を優先する情報配置と参照先の追加導線

- **参照先**: 「追加」は閉じた参照フォルダー選択ボタンの隣から外し、展開した選択一覧のフッターへ移す。既存のOSフォルダーピッカー、UNCパス直接入力、最近の場所のホバー削除はそのまま使う。
- **検索・容量**: 検索のヒット件数・合計容量・経過時間・走査状況を結果カードの見出しにまとめ、独立したKPI帯をなくす。長い走査状況は表示幅で省略し、全文をツールチップで確認できるようにする。容量の2枚の大きなカードと重複したページ見出しはやめ、スキャン操作列の下へ容量・件数・前回差分・MFT状態を短く並べる。数値の更新元は従来のTextBlockを移すだけで変えない。
- **整理**: 検出条件と除外フォルダー名は開閉可能とし、初期状態は閉じて候補一覧の高さを優先する。選択値は開閉しても保持する。結果一覧の表示件数・一括選択・絞り込みは狭い幅で折り返し、画面外へはみ出さないようにする。完全削除は引き続き開始・出力群から離れた右端に置く。
- **検証**: 0警告ビルド、8領域回帰、実IPC、一般画面・展開した参照一覧・整理の閉じた状態／開いた状態・狭い整理画面のレンダリングで確認する。

### ADR 121: 情シスナビの収納、固定幅の参照先、走査の完了目安

- **画面**: 情シス向け左バーはヘッダーのボタンで幅0まで畳める。初期値は収納で、状態は `AdminSidebarCollapsed` としてHost設定へ保存する。一般向けにはボタンもバーも出さない。参照フォルダーの選択欄を280px固定にし、長い名称は省略して全パスをツールチップに残す。展開一覧の下にあったパス手入力欄と「開く」は廃止し、「フォルダーを追加」のOSピッカーを追加導線にする。ADR 116・120 の手入力導線に関する記述はこの決定で更新する。
- **完了目安**: `MainWindow.ScanEta.cs` で容量・ライブ検索・整理候補走査・メディア走査・リンク走査・ACL逆引きの実行セッションを扱う。開始数秒は「見積もり中」。初回でも本番走査中の件数・速度、容量では発見済み／処理済みディレクトリから保守的な暫定見積りを出す。ローカルMFT走査では処理済み／総MFTレコード数を利用する。進捗件数を渡さない処理は、継続実行時間から広めの暫定値を出す。全文検索の速度観測には列挙件数でなく本文処理済み件数を用いる。操作種別と対象パスごとに直近の成功所要時間だけを最大32件記録し、次回の基準にする。TreeCacheの件数があれば今回の処理速度で補正する。表示済み完了予定時刻は前倒しにだけ更新し、超過した場合は超過中と明示する。見積りのための事前全件走査はしない。中断・失敗は履歴に残さない。初回の総量は保証できず、仮定が外れれば見込みも外れる。UNCへのI/O負荷と検索の網羅性は変えない。

### ADR 122: 見込みの過小評価を抑え、整理帯域は自動調整する

- **ETA（ADR 121の補正）**: 件数のない初回走査に経過時間だけから適当な総量を与える方式は撤回する。既知件数による推定は15秒・200件以上を観測してから行い、前回件数は変動し得るため進捗率75%を超える地点で新たな短縮をしない。MFTが返す総レコード数のみ確定母数として扱う。前回所要時間には2.2倍、前回件数からの速度外挿には内容走査2.8倍・容量走査1.8倍の余裕を持たせる。初回の未知件数は20秒・500件以上の本番進捗後に限り、ローカル25万件／ネットワーク100万件および観測件数32倍を下限とする広い見込みを出す。数値を作れない間は「見積もり中」。これらは上限保証ではなく暫定的なヒューリスティクスであり、実測で再調整する。確定期限を前倒しする表示規則と追加走査なしは維持する。
- **位置**: 検索の見込みは検索結果見出しの経過時間の隣に表示し、経過時間と一緒に読めるようにする。その他の走査は共通フッターの見込み表示を使う。停止・完了時に該当表示を隠す。
- **整理の帯域**: 利用者の手動二択は撤去し、GUIは常にAutoを送る。ローカルのSHA-256本読み取りには帯域上限を掛けない。UNCとネットワークドライブの本読み取りは50MiB/sから開始し、並列度2を維持したまま、実Readの2秒窓p95・実効速度から慎重に昇格（最大100MiB/s）し、異常遅延で直ちに降下（最小8MiB/s）する。一度降下した速度への再挑戦を制限し、30秒は昇格しない。サーバーの設定・管理者権限・追加の試験Readは使わない。旧RPC列挙値0/1は既存クライアント向けに残す。これでサーバーの健全性を完全には判定できないため、UNC実機での速度と負荷の改善率は未検証。

### ADR 123: 参照先の表示を整え、容量詳細を必要時だけ開く

- **選択欄と入力欄**: 閉じた参照フォルダー選択欄の外枠・内側ボタンの二重構造をやめ、280pxの `ScopeSelectorButtonStyle` 一枚で枠とホバーを描く。展開一覧の行は従来の枠なし `ScopePickerButtonStyle` を継続する。下向き記号は角を丸めた曲線Pathにする。検索入力の左余白を15pxから8pxへ、案内文を16pxから9pxへ寄せて、カーソルと文頭を揃える。
- **容量の主画面**: 初期状態ではツリーを全幅にする。「詳細を表示」で従来の上位ファイルTop 10と選択フォルダーの内訳を右側へ開き、「詳細を閉じる」で全幅へ戻す。情報そのものは削除しない。閉じた状態では詳細用の子階層取得とTop 10 RPCを行わず、開いた時に選択中のノードから取得する。推移グラフは従来の別操作を維持する。
- **クォータの表示境界**: `GetDiskFreeSpaceEx` がUNCで返す容量は呼び出しユーザーに見えるボリューム／共有の容量であり、FSRMのフォルダー単位の上限と同一視しない。FSRMの上限はサーバー側の管理情報であるため、通常のUNC走査結果を「フォルダークォータ」と表示しない。権限がある環境で正本取得できる経路を設計するまでは未実装とする。

### ADR 124: 接続ユーザーから見た利用可能量で低容量を警告する

- **判断**: 利用者の目的は、選択先で自分が使える残量が危険域に入ったと気付くこと。通常時に容量の数値を常時表示せず、`GetDiskFreeSpaceExW` の `freeBytesAvailableToCaller` が同じ呼び出しの `totalNumberOfBytes` の10%以下になった時だけ警告バッジを出す。バッジには残量を、ツールチップには分母と意味を示す。フォルダー単位のFSRMクォータ上限や走査済み容量と同一視しない。取得できない場合は推測で警告しない。
- **経路**: GUIはHostの `GetStorageAvailabilityAsync` だけを呼び、Coreの `StorageAvailableSpaceService` がローカルまたはUNCの対象パスを問い合わせて低容量を判定する。ルートの値を子へ流用せず、ツリーでフォルダーを選択した時はそのフォルダー、ファイル選択時は親フォルダーのパスを使う。選択を200ms静観し、通過した行を逐一問い合わせない。未選択時・タブ切替・対象変更・走査完了時はルートで更新し、以前の遅い応答で新しい表示を上書きしない。サーバー設定の変更や追加の全件走査はしない。
- **限界**: 直下や深い階層に独立したFSRMクォータがあっても、通常の容量APIがそれを返すとは限らない。したがって「警告なし」を「そのフォルダーのクォータに余裕あり」の証明として使わない。すべての子を一括照会してUNCへ負荷を増やすこともしない。

### ADR 125: 走査の進捗を作業画面に置き、初回検索・整理の列挙をTreeCacheへ再利用する

- **見込みの位置（ADR 122の更新）**: 検索は結果見出しの経過時間の隣を維持し、容量・整理候補・メディアは走査件数の隣、リンクは検出件数の隣、ACL逆引きは件数帯の直下へ見込みを出す。共通フッターの見込み欄は使わない。数値を出せない間は「見積もり中」とし、進捗件数を返さない旧RPC（メディア・リンク・ACL逆引き）だけは30秒以降に広い初回暫定値を示す。前倒しだけの予定時刻と超過表示は維持する。
- **整理の中止**: 整理候補の開始ボタンを実行中は同位置の「中止」に切り替える。中止はGUIのトークンから `HostJobClient.CancelJobAsync` を経てHost Jobへ伝わる。キャンセル応答を待つ間は再開始を防ぎ、途中結果を最終報告として削除・出力に使わない。完了・中止・失敗時は開始表示へ戻す。
- **初回列挙の再利用**: TreeCacheがない対象でLive検索または除外なしの整理走査を開始したら、`SafeFileEnumerator` が既に取得したファイル・フォルダーのメタデータを `TreeScanCapture` の有界Channelでローカル一時SQLiteへ流す。検索結果にフォルダーを含めるかとは独立に、キャッシュ用には全ディレクトリを捕捉する。対象共有へ追加の列挙・statは出さない。成功した完全走査だけ `SqliteTreeCacheService.ImportCaptureAsync` が `ParentId + Name` の正本スキーマへ単一トランザクションで公開し、一時DBは削除する。中止・アクセス拒否・除外付き整理では不完全なルートを公開しない。既存ルートは上書きせず、通常の容量スキャンが更新を所有する。検索専用DBや本文Indexは作らない。
- **検証**: 冷えた本文検索と整理からTreeCacheのルート・親子・集計が読めること、除外付き整理ではルートを作らないことを回帰試験する。ビルド、8領域回帰、単一EXEの実IPC往復を確認する。大規模UNCでの一時DB書き込み量と時間増分は未計測なので、実測で調整する。

### ADR 126: 容量再スキャンで SHA の履歴を保ち、重複確定は原本を読む

- **保持**: `SaveTreeAsync` のルート入替トランザクション内で、旧ツリーの SHA 付きファイルをパスとメタデータで照合する。パス・サイズ・更新日時・作成日時がすべて一致する場合だけ、新ツリーへ SHA を引き継ぐ。SHA 付き行がなければ旧ツリーの全行読みを省く。SHA の部分索引を設け、通常の空キャッシュ更新に余計な全行走査を加えない。
- **古い結果の書き戻し**: Audit完了後の非同期 `UpdateSha256Async` はパスに加えてサイズ・更新日時の一致を条件にする。容量再スキャンが先に終わった場合でも、違う内容の行へ古いSHAを書かない。共有JSON側も同じ条件で更新する。
- **完全性**: メタデータ一致は本文不変を保証しない。既存のSHAは履歴・将来の候補整理用とし、次回Auditの完全重複判定でフルSHAの代わりにはしない。重複削除直前には従来どおり双方のSHAを再計算する。管理者権限を要する変更ジャーナル照会はこの用途に採用しない。大規模ツリーでSHA付き行が多い場合の再保存コストは未測定。
- **検証**: メタデータ一致時の継承、不一致時の破棄、古いAudit更新の拒否をStorage回帰で確認する。0警告ビルドと8領域回帰を通す。

### ADR 127: 初回走査の不要な読込・割当・一時DB仕事を削る

- **本文検索**: 50MiB超テキストの分散Probeが外れて後段の全文検索へ進む際、Probe済みであることを明示して同じ範囲を二度読まない。後段は小ファイルの処理完了後に最大2件ずつ検査する。大きいテキストでは `ReadBlockAsync` のp95をガバナーへ報告する。この値にはデコード時間も含むため純粋なI/O時間ではなく、遅延を過小申告しない保守的な上限として扱う。64KiB以下かつ検索語が短いファイルは16KiBの文字バッファと8KiBのStreamReader内部バッファを使い、16KiB以下はFileStreamバッファも16KiBとする。長い検索語と大きいファイルでは従来のバッファ・適応Read幅を維持する。
- **一時TreeCache**: 最初のアクセス拒否後は完全なルートを公開できないため、検索・監査を止めずにローカル一時DBへの追加投入だけ止める。並べ替え索引は完全な列挙を確認した時だけ作る。UNC対象への追加列挙や管理者権限は必要ない。
- **重複整理**: 既存のサイズ→Head/Tailの後、ローカルかつ32MiB以上で残った群のみ先頭1MiBのSHA-256で分割する。単独になったファイルは全文を読まない。部分読取失敗時は群全体を従来の完全SHAへ戻す。UNCではこの追加段階を使わない。完全SHAと削除直前の双方再照合は維持する。巨大な同一サイズ群でもファイル数ぶんのTaskを作らず、2並列の有界ワーカーで入力順に結果を格納する。ローカル8MiB以上の完全SHAはプールした256KiBバッファとSequentialScanを使用し、UNCは従来の64KiBと適応帯域制御を維持する。
- **容量**: `DiskScanService` が既に取得した作成日時をファイル・フォルダーのノードへ入れ、ADR 126のSHA継承条件を通常の容量再走査でも成立させる。追加のstatは行わない。
- **採否と限界**: Cドライブ全体の同じ本文語で3回走査し、結果集合と読取失敗件数は一致した。壁時計には大きな揺れがあり、全Cの短縮は確定できない。一方、最終版のCPU時間と割り当ては初回より低く、ローカル小ファイル・重複群の交互比較でも短縮を確認した。数値と実験条件は `PERFORMANCE_RESEARCH.md` を正本とする。UNCの実測はなく、ローカルの改善率をUNCへ適用しない。形式別ストリーム抽出、容量ノード構造の変更、サーバー索引との重ね合わせは別実験として残す。

### ADR 128: .NET 10移行とrga/fclones比較から採用する初回経路

- **実行環境**: ルート・UI・Contracts・Host・Coreを `net10.0-windows` に揃え、Windows用のDirectoryServices/Drawingパッケージも10.0.12へ更新する。CIと配布CIはSDK 10を使い、自己完結型・単一EXE・同一EXEの `--host` 境界を維持する。UIの不要なDirectoryServices直接参照はSDKの警告に従って除く。アプリ版は2.4.0とし、旧Hostとの版判定を働かせる。
- **検索**: ripgrep-allのAGPL-3.0コードや変換器を複製・同梱しない。既存の形式別抽出と単一語照合を生かし、UTF-8判定・単一ASCII語の生テキストだけ同一FileStreamのbyte列で直接探す。ASCII-onlyの全件非一致は確定できる。非ASCII byteを含む非一致、長い語、複合語、他の文字コードは従来のデコーダーへ戻す。ヒット時だけスニペットを作り、16KiB境界と日本語混在を回帰確認する。ローカルではUNC用の遅延AIMDが並列2へ沈むため、列挙4・本文8の固定上限へ分ける。UNCとネットワークドライブは列挙2・本文AIMD 2〜12を維持し、サーバーへ負荷を集中させない。
- **重複**: fclones 0.35.0を試験用に隔離してSHA-256・キャッシュなしで実データ比較した。既存のサイズ→先頭/末尾4KiB→大容量群の先頭1MiB→完全SHA-256は39群・105重複件数と全グループのパスが一致した。fclones式に先頭と末尾を別々に開く案は、旧方式と交互に測って有意な短縮がなく、追加Openと分岐が増すので採用しない。既存の部分ハッシュは不一致候補を外すだけとし、重複確定と削除前再照合の完全SHA-256を維持する。外部試験EXEは配布に含めない。
- **計測と限界**: Cドライブの19種類の生テキスト拡張子に対して、実在する本文語 `SubtreeEndId` を索引なしで探した。新経路のFolderMorpher 209.057秒、rga 0.9.6 `--rga-no-cache` 223.158秒で、双方の25パスは一致した。旧ローカルガバナーのFolderMorpher 527.067秒とも比較したが、実行順によるOSキャッシュの影響を除けず、恒常的な速度比は断定しない。試験用TreeCacheは毎回空にした。GUI/Host IPC、Office/PDF抽出、UNC、HDDはこの数値の対象外。詳細は `PERFORMANCE_RESEARCH.md` を正本とする。
