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
- **ヘッドレス自己検証 CI ゲート（7/7 ALL PASSED 必須）**:
  - コード変更後は必ず `dotnet run --no-build -- --test-regression` を実行し、以下の7大ドメインすべてが 100% PASS することを必須ゲートとする：
    - `Domain 1`: Live ACL & Effective Access (Canonical DACL, Rollback SDDL, SID最優先一致, 余計なACE検知)
    - `Domain 2`: Storage Explorer & UNC Traversal (Win32 LargeFetch/8.3 Skip, SafeFindHandle, Concurrency 2)
    - `Domain 3`: Simulation Studio (Plan-First Skeleton, Robocopy /XD, 実機DACLセマンティックVerify)
    - `Domain 4`: Audit & Hygiene (原本絶対保護, 削除直前SHA-256再照合, 休眠1年閲覧保護)
    - `Domain 5`: Media Optimizer & LinkFixer (PNG透過保持, 聖域保護, SafeFileEnumerator, PartiallyFixed)
    - `Domain 6`: MFT & Defensive Hardening (Initial LCN 0-Base, AppSettings Cascade)
    - `Domain 7`: Bilingual Localization & Storage Forecasting (JA/EN Switch, Holt Forecasting)

---

## 13. 【エンタープライズ移行パッケージ & 意思決定支援】Wave自動分割・Runbook台帳Excel・多重コピー防止/XD・旧共有安全封鎖
*(v2.1.2 新規施工)*

- **ベンダー標準 5大フェーズ移行工程の静的生成 (`MigrationPackageService`)**:
  - `Phase 1: 01_Baseline_Sync.bat` (事前フル同期: `/R:1 /W:1 /MT:16 /COPY:DAT` による高速転送)
  - `Phase 2: 02_Delta_Sync.bat` (中間差分同期: 更新ファイルのみ追随)
  - `Phase 3: 03_Lock_OldShare_ReadOnly.bat` (旧共有安全封鎖: 業務終了時の書き込み権限遮断による先祖返り防止)
  - `Phase 4: 04_Final_Cutover_Mirror.bat` (本番最終ミラー: `/MIR` による完全一致同期、事前同期済みのため数分〜数十分で完了)
  - `Emergency: 99_ROLLBACK_RestoreOldShare.bat` (旧共有書き込み復旧: 万が一の切戻し時のワンタッチ権限復元)
  - `Orchestrator: 00_Run_All_Waves_StepByStep.bat` (全Wave対話式順次実行マスターバッチ)
- **人間の判断を強力にアシストする意思決定指標（Decision Support）**:
  - **分割ポリシー選択**: 第1階層（部署・トップフォルダごと推奨） / 容量バジェット（指定GB以下に自動集約） / 一括出力
  - **リアルタイム見積**: 1Gbps実効（約80MB/s）による初回フル同期所要時間および差分2%換算の本番カットオーバー所要時間を即座に算出。
  - **アセスメント警告**: 週末枠超過リスク（⚠️ 48時間超）およびランダムI/O過多（⚠️ 10万件超、`/MT:32` 推奨）を自動検知してバッジ提示。
- **N:1マッピングにおける子孫パス多重コピー防止 (`/XD`) の完全貫通**:
  - 親ノードの配下にある子孫マッピングパスを自動検出し、Robocopyの `/XD` 引数へ自動注入。フォルダー構成を再編成した際の二重転送・データ重複を原理的に根絶。
- **ClosedXML による 3シート構成 移行進捗台帳 (`Migration_Runbook.xlsx`)**:
  - `1_概要・Wave計画`: KPIサマリーカード（総容量、総ファイル数、推定所要時間）と波次計画テーブル。
  - `2_移行WBS・工程表`: ベンダー標準の事前準備〜フル同期〜差分〜旧共有封鎖〜本番切替〜検証〜緊急切戻しチェックリスト。
  - `3_マッピング・除外詳細`: 新旧パス対応表および `/XD` 除外パス一覧。
- **文字コード契約**:
  - バッチファイルは冒頭で `chcp 65001 > nul` を宣言し、BOMなし UTF-8 (`new UTF8Encoding(false)`) で保存。Shift-JIS (CP932) のコードページ依存エラーを根絶。

