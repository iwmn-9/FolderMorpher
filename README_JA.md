# FolderMorpher

**Windowsのファイル検索・容量分析・整理と、ファイルサーバー管理を行うポータブルツール。**

参照フォルダーを選び、**検索・容量・整理**を切り替えて使います。開閉可能な管理サイドバーからはNTFS権限操作、移行設計、リンク修復も利用できます。ローカルフォルダーとUNC共有に対応しています。

![Platform](https://img.shields.io/badge/Platform-Windows%20x64-blue.svg)
![Framework](https://img.shields.io/badge/.NET-10%20(Self--Contained)-purple.svg)
![License](https://img.shields.io/badge/Source%20License-MIT-green.svg)

[English](README.md) · [ダウンロード](https://github.com/iwmn-9/FolderMorpher/releases) · [不具合・要望](https://github.com/iwmn-9/FolderMorpher/issues)

## 起動方法

1. Releasesから `FolderMorpher.exe` をダウンロードして実行します。インストールや別途.NETを用意する必要はありません。
2. 参照フォルダーを選びます。展開した選択欄の中から追加し、対象を切り替えられます。
3. 検索・容量・整理を選んで使います。言語などの環境設定は **設定** にまとまっています。

設定・履歴・メタデータキャッシュは実行端末に保存します。通常はウィンドウを閉じてもHostの処理を継続します。設定の **「×で閉じたらHostも終了する」** をオンにすると、実行中の処理も中断してHostを終了します。

### 動作要件

- デスクトップのあるWindows x64：サポート対象のWindows 10/11、またはDesktop ExperienceのあるWindows Server 2016以降。エディション・保守条件はMicrosoftの [.NET 10対応OS一覧](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) を参照してください。
- 検索・通常の容量走査に管理者権限は不要です。ローカルNTFSのMFT走査は、実行プロセスに必要な権限が既にある場合だけ利用します。共有先は実行ユーザーのアクセス権で参照します。
- Open XML形式（`.docx`、`.xlsx`、`.pptx`）の処理にOfficeは不要です。PDFは端末のWindows IFilterと内蔵フォールバックを利用し、形式や保護状態によって本文を抽出できない場合があります。
- 動画変換バッチには別途FFmpegが必要です。EXEには同梱していません。

## 検索

- 通常はファイル名から探します。**本文も検索** または `content:` で対応するテキスト・Office・PDFの本文を検索できます。必要ならフォルダーも検索対象にできます。
- `ext:xlsx,docx`、`size:>100MB`、`dormant:>3y`、`pathlen:>240`、`chars:illegal` などの条件、論理演算、ワイルドカードを利用できます。
- 入力中はメモリや端末内のTreeCacheから前回の走査情報を先行表示します。検索ボタン・Enter・更新では原本を走査し、最終一覧は今回確認したヒットに置き換えます。**永続的な全文検索DBは作りません。**
- 未走査の対象は列挙結果を逐次処理し、全件一覧を二重に保持しません。本文ではバッファー再利用、適用可能なASCII/UTF-8直接照合、読取サイズの自動調整を使います。Officeは画像等を展開せず、本文に関係するEntryを読みます。
- 実行中は検索ボタンが同じ位置の **中止** に変わります。クリアは処理を中断し、空の条件で検索が再開しません。途中のキャッシュ表示は暫定情報です。完了時の件数には原本で確認したファイル名・本文ヒットを重複なく含めます。
- 経過時間・残り時間の目安は結果件数の付近に表示します。開始直後は「見積もり中」とし、予定超過は明示します。完了時には列挙不能フォルダーや検出できた本文読取失敗も表示します。

## 容量

- 容量ツリーで各項目のスキャン対象全体に占める割合を確認できます。フォルダー展開時は子の一階層だけ取得し、全ツリーをGUIへ送りません。
- 通常権限のローカル走査は一括列挙と最大8ワーカーを使います。UNCは共有先の列挙枠を2に保ちます。属性・サイズ・日時は列挙結果から取り、ファイルごとに別途照会しません。
- 前回の情報を読み込み、最新走査後に増減を示します。キャッシュは前回の観測結果であり、原本が変わっていないことの保証ではありません。
- 容量上位ファイル・選択フォルダーの内訳は **詳細を表示** で開きます。通常はツリーを広く使い、推移グラフは別操作で開きます。
- 選択した場所で接続ユーザーの利用可能量が10%以下になると警告します。Windowsの空き容量APIで個別フォルダーのサーバークォータが必ず見えるわけではありません。
- SQLiteのTreeCacheは `%LocalAppData%\FolderMorpher\TreeCache\tree_cache.db` に保存します。親ID・名前でパスの重複保存を避けます。共有・任意の保存先にはJSONツリーと履歴を置き、SQLite DBを共有先へ置きません。
- キャッシュがない対象でも、検索や整理の完全な初回Live走査からTreeCacheを形成できます。中断・アクセス拒否・不完全な走査・除外付き走査では、その新規キャッシュを公開しません。

## 整理

- 重複・休眠・旧版・展開済ZIP・パス問題などの理由を物理ファイルごとに統合します。**点数は100点を上限にせず加算**し、内訳を確認できます。削除の安全確率ではなく、確認する優先度です。
- 重複はサイズ・部分Fingerprintで候補を絞り、生き残った群を **全文SHA-256** で確定します。保存したSHAで次回の全文照合を省略しません。
- 大容量候補のグループから確認し、途中結果を暫定表示します。中止も可能です。削除・出力は最終報告後に有効になります。
- 原本候補を保護します。完全削除は離れた操作として設け、重複ファイルは削除直前にもSHA-256を再照合します。
- 読取並列度やUNCのSHA帯域はツール側で自動調整します。
- **メディア最適化** は整理に内包します。保護フォルダー、画像の縮小・圧縮、大容量動画の抽出、FFmpegバッチ生成に対応します。画像圧縮は非可逆の場合があるため、上書き前に計画を確認します。

## 情シス向け機能

| ツール | 用途 |
| --- | --- |
| 権限コントロール・逆引き監査 | NTFSルール、ADグループ経路、実効アクセスを確認。変更プレビュー、SDDL保存、適用、検証 |
| 移行スタジオ | 仮想ツリーとN:1マッピング設計、権限差分、スケルトン展開、波次・手順書・Robocopyパッケージ出力 |
| リンク修復 | `.lnk` とExcel外部リンクの検出・バックアップ付き修復。VBAバイナリを保護し、部分修復も報告 |

観測はそのまま開始し、変更は共通計画の **確認 → 適用 → 検証** で行います。アクセス権や保護判定は維持します。本番の変更にはバックアップを用意してください。

## 性能と実装

現在の構成は **C# 14 / .NET 10 / WPF** です。配布は `FolderMorpher.exe` 1本。通常起動のGUIと、同じEXEを `--host` で起動するHostを別プロセスに分けます。ソース依存は `UI → Contracts ← Host → Core`、Named Pipeは同一ユーザー・セッションに限定します。

通常権限・MFTなし・TreeCache再利用なしのCドライブ容量試験では、通常走査の実時間中央値が **33.070秒 → 13.518秒** になりました。DB保存は別計測で、Windowsのファイルキャッシュは消していません。この端末・条件の結果であり、UNC性能の保証には使いません。検索・重複・容量の比較条件、採用技術、限界は [性能研究記録](.agents/PERFORMANCE_RESEARCH.md) にまとめています。

比較ツールは製品に同梱していません。ripgrep-all、Czkawka、容量ツールから採用した手法は独立実装であり、それらのコードをFolderMorpherへコピーしていません。

### ビルド・検証

Windows上で.NET 10 SDKを使用します。

```powershell
dotnet build -c Release
./tools/Test-LicenseNotices.ps1
dotnet run -c Release --no-build -- --test-regression
dotnet publish ./FolderMorpher.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None
./tools/Test-LicenseNotices.ps1 -DepsPath ./bin/Release/net10.0-windows/win-x64/FolderMorpher.deps.json
./dist/FolderMorpher.exe --test-ipc
```

配布出力の正本は `dist/FolderMorpher.exe` です。回帰試験は8ドメインを扱いますが、網羅率を意味しません。配布EXEのIPC試験は専用データで別Hostを起動します。構造・保守手順は [AGENTS.md](AGENTS.md)、決定理由は [ADR.md](.agents/ADR.md) に記載しています。

## ライセンス

FolderMorpherのソースは **[MIT](LICENSE)**、著作権は © 2026 iwmn-9です。同梱ライブラリと.NETランタイムはそれぞれのライセンスを保持します。全文・著作権・上流NOTICEは **[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)** にまとめ、EXEの **設定 → ライセンス** にも収録しています。

[依存ライセンスの確認記録](docs/DEPENDENCIES.md) に、MIT/Apache 2.0の依存、使用版での確認手順、外部ツールとの区別、CIの表記漏れ検証を記載しています。ソフトウェアは各ライセンスの条件に従って現状有姿で提供します。
