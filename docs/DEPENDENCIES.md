# 依存ライブラリと配布表記

2026-09-29に、2.4.3のNuGetパッケージ・自己完結型ランタイム・参照素材を確認した。ライセンス全文、著作権、上流NOTICE、版と出典の正本はルートの [`THIRD-PARTY-NOTICES.txt`](../THIRD-PARTY-NOTICES.txt)。EXEにも同じファイルを埋め込み、**設定 → ライセンス**で表示・コピーできる。別ファイルを配布しなくても表記を保持する。

FolderMorpher自身のソースは [`LICENSE`](../LICENSE) のMIT。依存コンポーネントは各自のライセンスを保持する。自分のコードをMITで公開していても、依存側の表示条件がなくなるわけではない。

## 現在の配布物

実際の `FolderMorpher.deps.json` から24のNuGetパッケージと2つのランタイムパックを確認した。ビルド専用の `Microsoft.NET.ILLink.Tasks` は配布対象に数えない。

| コンポーネント | 使用目的 | 確認したライセンス・収録内容 |
| --- | --- | --- |
| ClosedXML、Parser、Open XML SDK、ExcelNumberFormat、RBush | Excel出力 | MIT、各著作権表示 |
| SixLabors.Fonts **1.0.0** | ClosedXMLの間接依存 | この版はApache 2.0。ライセンス全文とパッケージの著作権を収録。現在の最新版の条件とは区別する |
| Microsoft.Data.Sqlite、SQLitePCLRaw | ローカルTreeCache | Microsoft側はMIT、SQLitePCLRaw **2.1.12**はApache 2.0とNOTICE。内包するSQLite本体はパブリックドメイン |
| StreamJsonRpc、Nerdbank、MessagePack、PolyType、Newtonsoft.Json、Visual Studio関連、StringTools | GUI/Host間のRPCとその依存 | MIT、付属NOTICE・THIRD-PARTY-NOTICESも収録 |
| System.Data.OleDb、.NET / Windows Desktop **10.0.12** | Windows連携と自己完結型実行 | MIT。ランタイムの第三者表記は省略せず収録。DirectoryServices・Drawing等のフレームワークアセンブリもランタイムに含まれる |
| Tabler Iconsの書類・フォルダー図形のアレンジ | ファイル種別表示 | MIT。図形の参照元として表記。フォントファイルは同梱しない |

MITは著作権と許諾文の保持、Apache 2.0はライセンス全文と適用されるNOTICE等の保持を要する。今回の一覧に本体をAGPLへ変更する依存はない。上流NOTICEにはそのプロジェクトのビルド・試験用ツールの説明も含まれ得るため、NOTICEの登場名をFolderMorpherの同梱一覧と解釈しない。同梱一覧は各 `Package:` 行と配布マニフェストで確認する。

## 外部ツールとの区別

- ripgrep-all、Czkawka、fclones、dua、gdu、diskus、fd等は性能比較や設計調査に使用した。コード・実行ファイルをFolderMorpherへ組み込んでいない。採用した処理は独立実装。比較の条件と採否は [性能研究記録](../.agents/PERFORMANCE_RESEARCH.md) を正本とする。
- 動画変換の生成バッチは、利用者が別途用意したFFmpegを呼ぶ。EXEには含めない。FFmpegを将来配布へ含める場合は、そのビルドのLGPL/GPL条件・有効なコーデック・ソース提供等を別途確認する。
- PDFのIFilterやWindows標準フォントは端末にある機能を利用する。第三者製フィルターやフォントファイルを再配布しない。

## 依存を変更するとき

1. ビルド／publish後の `.deps.json` と、NuGetの実際のパッケージ内容を確認する。直接参照だけでなく間接依存、ネイティブDLL、ランタイムの版も対象にする。
2. パッケージに付属するライセンス・著作権・NOTICEを読む。ライセンス表明だけで全文がない場合は、NuSpecのRepository commitに対応する上流ファイルを確認する。上流の最新版だけを見て使用中の版の条件を決めない。
3. 正本の `THIRD-PARTY-NOTICES.txt` に版、出典、必要な全文・著作権・NOTICEを反映する。上流ファイルの改変を避け、ファイルの外に説明を加える。依存のソース自体を改変する場合は、そのライセンスが要求する変更表示等も確認する。
4. 下の検証を実行する。これは**依存の版に対する表記漏れ**を検出するゲートであり、個々のライセンス条件の確認は上の手順で行う。
5. 配布EXEのIPC試験と「設定 → ライセンス」の表示を確認する。EXE内の全文とリポジトリの正本を別々に編集しない。

```powershell
# 通常のReleaseビルド（CIでも実行）
./tools/Test-LicenseNotices.ps1

# win-x64自己完結型publish（配布CIでも実行）
./tools/Test-LicenseNotices.ps1 -DepsPath ./bin/Release/net10.0-windows/win-x64/FolderMorpher.deps.json
```

## 一次資料

- [MIT](https://opensource.org/license/mit)、[Apache 2.0 §4](https://www.apache.org/licenses/LICENSE-2.0)
- [SixLabors.Fonts 1.0.0に対応するライセンス](https://github.com/SixLabors/Fonts/blob/32bef42997adb10268369ca149777f00e4241ce9/LICENSE)
- [SQLitePCLRaw 2.1.12](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/LICENSE.TXT)、[NOTICE](https://github.com/ericsink/SQLitePCL.raw/blob/v2.1.12/NOTICE.TXT)、[SQLite著作権方針](https://www.sqlite.org/copyright.html)
- [FFmpegの法的条件](https://ffmpeg.org/legal.html)

各MIT依存の使用版に対応する出典URLと全文は `THIRD-PARTY-NOTICES.txt` に収録している。
