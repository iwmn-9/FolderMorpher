# FolderMorpher — AI Agent & Developer Architecture Guide

> **【AIメンテナ・自律継続規約】**  
> 本プロジェクトは「自律完遂（自ら調査・修正・検証まで行い、完成状態で返す）」を基本方針とする。  
> ⚠️ **後続のAIメンテナへ**: コードベースの構造、ファイル責務、または重要な設計制約を変更した場合は、**必ずこの `AGENTS.md` も最新の状態に同期・更新すること**。過去の設計意図（第3項 ADR）を無視した安易なコードの巻き戻しを禁止する。

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

UI層は `MainWindow.xaml` / `MainWindow.xaml.cs` に集約され、内部ロジックは `Services` と `Models` に完全に分離されている。

| 機能領域 / タブ | XAML (MainWindow.xaml) | C# コードビハインド (MainWindow.xaml.cs) | 関連 Service / Model | 責務と概要 |
| :--- | :--- | :--- | :--- | :--- |
| **全体共通 / 左サイドバー** | `SidebarBorder`, `SidebarToggleButton` (L22-68) | `SidebarToggleButton_Click` (L140-157)<br>`NavTab_Checked` (L93-136) | `Converters/ValueConverters.cs` | 収縮対応ナビゲーション（幅220px ⇄ 58px）、グローバルステータスバー、通知トースト |
| **Tab 1: 容量分析 & 監視**<br>(Storage Explorer) | `StorageTabPanel` (L82-410) | `ScanButton_Click`<br>`StorageTreeView_SelectedItemChanged`<br>`SubfolderShareGrid_MouseDoubleClick` | `DiskScanService.cs`<br>`DriveInfoService.cs`<br>`StorageHistoryService.cs`<br>`ScanTabModel.cs`<br>`FileItemNode.cs` | 複数タブスキャン、ドライブ空き容量メーター、全体占有率メーター（案A）、容量上位Top10（Explorer起動連動）、直下シェア内訳（Wクリックツリー連動） |
| **Tab 2: 権限コントロール**<br>(Live ACL) | `LiveAclTabPanel` (L413-605) | `LiveAclReloadButton_Click`<br>`LiveAclApplyButton_Click`<br>`LiveAclRollbackButton_Click`<br>`LiveAclDropZone_Drop` | `AclService.cs`<br>`ActiveDirectoryService.cs`<br>`AclModels.cs` | 実環境NTFS ACLの可視化・直接編集、ADドラッグ＆ドロップ付与、ポイ捨て削除、SDDL直前スナップショット復元（ロールバック）、台帳CSV |
| **Tab 3: 移行スタジオ**<br>(Simulation Studio) | `SimulationTabPanel` (L608-995) | `SimSourceLoadButton_Click`<br>`SimMockTreeView_Drop`<br>`SimDiffReviewButton_Click`<br>`SimDeploySkeletonButton_Click` | `SimulationProjectService.cs`<br>`MigrationService.cs`<br>`SimModels.cs` | 現行ファイルサーバーから新環境への仮想ツリー設計（N:1マッピング）、ACL引き継ぎ設計、Diffインスペクター、ガワ先行作成（空フォルダ+ACL一括展開）、Robocopy生成 |
| **Tab 4: リンク一括修復**<br>(LinkFixer) | `LinkFixTabPanel` (L998-1065) | `LinkScanButton_Click`<br>`LinkFixExecuteButton_Click` | `LinkFixService.cs` | サーバー移行後の切断ショートカット（.lnk）一括検出、新UNCパスへの安全な一括書き換え（バックアップ付き） |
| **詳細権限モーダル** | `SecModalOverlay` (L1063-1175) | `SecModalApply_Click`<br>`SecModalCancel_Click` | `AclModels.cs` | Windows標準セキュリティ詳細設定（14項目のNTFS詳細パーミッションビット）の完全再現・編集 |
| **変化点差分モーダル** | `DiffModalOverlay` (L1180-1257) | `DiffModalClose_Click`<br>`DiffExportExcel_Click` | `SimModels.cs` | 移行前後（Before/After）の変化点（新規・移動・統合・ACL差分）の一覧レビューとExcel出力 |

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

---

## 4. ビルド・実行・検証コマンド

コンテキストを持たないAIが修正を行った後は、必ず以下のコマンドで検証すること。

### ビルド（0 警告・0 エラーを維持すること）
```powershell
& "$HOME\.dotnet\dotnet.exe" build
```

### 配布用単一EXEの生成（Release self-contained）
```powershell
& "$HOME\.dotnet\dotnet.exe" publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
Copy-Item -Path ".\bin\Release\net8.0-windows\win-x64\publish\FolderMorpher.exe" -Destination ".\FolderMorpher.exe" -Force
```

### ヘッドレス実機レンダリング（オフスクリーン撮影でUIを目視確認）
AI自身がUIの描画崩れを確認するための内蔵スナップショット機能：
```powershell
# Tab 0 (容量分析) を撮影
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab0.png" --tab 0

# Tab 0 のサイドバー収縮状態を撮影
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab0_collapsed.png" --tab 0 --collapse

# Tab 1 (権限コントロール) を撮影
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab1.png" --tab 1

# Tab 2 (移行スタジオ) を撮影
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab2.png" --tab 2

# Tab 3 (リンク一括修復) を撮影
& "$HOME\.dotnet\dotnet.exe" ".\bin\Debug\net8.0-windows\FolderMorpher.dll" --snapshot ".\tab3.png" --tab 3
```