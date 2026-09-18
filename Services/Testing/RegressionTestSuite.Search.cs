using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AstraSize.Models;
using FolderMorpher.Models;
using FolderMorpher.Services;

namespace FolderMorpher.Services.Testing
{
    public static partial class RegressionTestSuite
    {
        public static async Task TestDomain_SearchStudioAsync()
        {
            var searchEngine = new SearchEngineService();

            // -------------------------------------------------------------
            // 1. SearchQueryParser: 構文解析エンジンの検証
            // -------------------------------------------------------------
            {
                string queryStr = "project ext:xlsx,docx size:>10MB size:<1GB dormant:>3y pathlen:>240 chars:illegal content:\"社外秘\" !temp regex:^backup.*\\.zip$";
                var q = SearchQueryParser.Parse(queryStr);

                if (!q.Keywords.Contains("project"))
                    throw new Exception("SearchQueryParser failed: Keyword 'project' not detected.");

                if (q.Extensions.Count != 2 || !q.Extensions.Contains(".xlsx") || !q.Extensions.Contains(".docx"))
                    throw new Exception("SearchQueryParser failed: Extensions '.xlsx, .docx' not correctly parsed.");

                if (!q.MinSizeBytes.HasValue || q.MinSizeBytes.Value != 10L * 1024 * 1024)
                    throw new Exception($"SearchQueryParser failed: MinSizeBytes expected 10MB, got {q.MinSizeBytes}");

                if (!q.MaxSizeBytes.HasValue || q.MaxSizeBytes.Value != 1024L * 1024 * 1024)
                    throw new Exception($"SearchQueryParser failed: MaxSizeBytes expected 1GB, got {q.MaxSizeBytes}");

                if (!q.DormantYears.HasValue || q.DormantYears.Value != 3)
                    throw new Exception($"SearchQueryParser failed: DormantYears expected 3 for dormant:>3y, got {q.DormantYears}");

                if (!q.MinPathLength.HasValue || q.MinPathLength.Value != 240)
                    throw new Exception("SearchQueryParser failed: MinPathLength expected 240.");

                if (!q.OnlyIllegalChars)
                    throw new Exception("SearchQueryParser failed: OnlyIllegalChars expected true.");

                if (q.ContentKeyword != "社外秘")
                    throw new Exception($"SearchQueryParser failed: ContentKeyword expected '社外秘', got '{q.ContentKeyword}'");

                if (!q.ExcludedWords.Contains("temp"))
                    throw new Exception("SearchQueryParser failed: ExcludedWords expected 'temp'.");

                if (q.CompiledRegex == null || !q.CompiledRegex.IsMatch("backup_2024.zip"))
                    throw new Exception("SearchQueryParser failed: CompiledRegex did not match 'backup_2024.zip'.");

                // 追加検証: office-link:true と office-link:"\\OldServer"
                var qLinkTrue = SearchQueryParser.Parse("office-link:true");
                if (!qLinkTrue.HasOfficeLinkOnly || qLinkTrue.OfficeLinkKeyword != null)
                    throw new Exception("SearchQueryParser failed: office-link:true should set HasOfficeLinkOnly=true and OfficeLinkKeyword=null.");

                var qLinkNamed = SearchQueryParser.Parse("office-link:\"\\\\OldServer\"");
                if (!qLinkNamed.HasOfficeLinkOnly || qLinkNamed.OfficeLinkKeyword != "\\\\OldServer")
                    throw new Exception($"SearchQueryParser failed: office-link:\"\\\\OldServer\" expected OfficeLinkKeyword='\\\\OldServer', got '{qLinkNamed.OfficeLinkKeyword}'");

                // 追加検証: KB 倍率の検証 (1024倍であること)
                var qKb = SearchQueryParser.Parse("size:>100KB size:<200KB");
                if (!qKb.MinSizeBytes.HasValue || qKb.MinSizeBytes.Value != 100L * 1024)
                    throw new Exception($"SearchQueryParser failed: size:>100KB expected {100L * 1024}, got {qKb.MinSizeBytes}");
                if (!qKb.MaxSizeBytes.HasValue || qKb.MaxSizeBytes.Value != 200L * 1024)
                    throw new Exception($"SearchQueryParser failed: size:<200KB expected {200L * 1024}, got {qKb.MaxSizeBytes}");

                // 追加検証: dormant:>180d
                var qDormantDays = SearchQueryParser.Parse("dormant:>180d");
                if (!qDormantDays.DormantDays.HasValue || qDormantDays.DormantDays.Value != 180)
                    throw new Exception($"SearchQueryParser failed: dormant:>180d expected 180 days, got {qDormantDays.DormantDays}");
            }

            // -------------------------------------------------------------
            // 2. SearchInMemoryAsync: スキャン済みツリー対象 0秒インメモリ検索
            // -------------------------------------------------------------
            {
                // テスト用仮想ツリー構築
                var root = new FileItemNode
                {
                    Name = "Root",
                    FullPath = @"C:\MockRoot",
                    IsDirectory = true
                };

                var doc1 = new FileItemNode
                {
                    Name = "Financial_Report_2023.xlsx",
                    FullPath = @"C:\MockRoot\Financial_Report_2023.xlsx",
                    Size = 5 * 1024 * 1024, // 5MB
                    LastModified = DateTime.UtcNow.AddYears(-4), // 4年前 (休眠)
                    IsDirectory = false
                };

                var doc2 = new FileItemNode
                {
                    Name = "Financial_Draft_Temp.docx",
                    FullPath = @"C:\MockRoot\Financial_Draft_Temp.docx",
                    Size = 15 * 1024 * 1024, // 15MB
                    LastModified = DateTime.UtcNow.AddDays(-10),
                    IsDirectory = false
                };

                var subDir = new FileItemNode
                {
                    Name = "SubArchive",
                    FullPath = @"C:\MockRoot\SubArchive",
                    IsDirectory = true
                };

                var subDoc = new FileItemNode
                {
                    Name = "BigArchive.zip",
                    FullPath = @"C:\MockRoot\SubArchive\BigArchive.zip",
                    Size = 120 * 1024 * 1024, // 120MB
                    LastModified = DateTime.UtcNow.AddYears(-1),
                    IsDirectory = false
                };

                subDir.Children.Add(subDoc);
                root.Children.Add(doc1);
                root.Children.Add(doc2);
                root.Children.Add(subDir);

                var roots = new List<FileItemNode> { root };

                // テストA: 拡張子 & 休眠検索 (ext:xlsx dormant:>3y)
                var qA = SearchQueryParser.Parse("ext:xlsx dormant:>3y");
                var hitsA = await searchEngine.SearchInMemoryAsync(roots, qA, null, CancellationToken.None);
                if (hitsA.Count != 1 || hitsA[0].Name != "Financial_Report_2023.xlsx")
                    throw new Exception($"SearchInMemoryAsync Test A failed: Expected 1 hit (Financial_Report_2023.xlsx), got {hitsA.Count}");

                // テストB: キーワード & 除外検索 ("Financial" !Temp)
                var qB = SearchQueryParser.Parse("Financial !Temp");
                var hitsB = await searchEngine.SearchInMemoryAsync(roots, qB, null, CancellationToken.None);
                if (hitsB.Count != 1 || hitsB[0].Name != "Financial_Report_2023.xlsx")
                    throw new Exception($"SearchInMemoryAsync Test B failed: Expected 1 hit without 'Temp', got {hitsB.Count}");

                // テストC: 大容量サイズ検索 (size:>100MB)
                var qC = SearchQueryParser.Parse("size:>100MB");
                var hitsC = await searchEngine.SearchInMemoryAsync(roots, qC, null, CancellationToken.None);
                if (hitsC.Count != 1 || hitsC[0].Name != "BigArchive.zip")
                    throw new Exception($"SearchInMemoryAsync Test C failed: Expected 1 hit (BigArchive.zip), got {hitsC.Count}");

                // テストD: IncludeFolders (既定falseでフォルダー除外、trueでフォルダー含める)
                var qD1 = SearchQueryParser.Parse("SubArchive"); // IncludeFolders = false (既定)
                var hitsD1 = await searchEngine.SearchInMemoryAsync(roots, qD1, null, CancellationToken.None);
                // BigArchive.zip は名前に SubArchive を含まず、フォルダー SubArchive 自体は IncludeFolders=false なので 0 件
                if (hitsD1.Count != 0)
                    throw new Exception($"SearchInMemoryAsync Test D1 failed: Expected 0 hits without IncludeFolders, got {hitsD1.Count}");

                var qD2 = SearchQueryParser.Parse("SubArchive");
                qD2.IncludeFolders = true; // フォルダーも含めるON
                var hitsD2 = await searchEngine.SearchInMemoryAsync(roots, qD2, null, CancellationToken.None);
                if (hitsD2.Count != 1 || hitsD2[0].Name != "SubArchive" || !hitsD2[0].IsDirectory)
                    throw new Exception($"SearchInMemoryAsync Test D2 failed: Expected 1 folder hit (SubArchive), got {hitsD2.Count}");

                // テストE: 親フォルダー名巻き添え防止 (キーワードにパス区切りを含めるか path: 指定時のみパス全体一致)
                var qE1 = SearchQueryParser.Parse(@"SubArchive\BigArchive");
                var hitsE1 = await searchEngine.SearchInMemoryAsync(roots, qE1, null, CancellationToken.None);
                if (hitsE1.Count != 1 || hitsE1[0].Name != "BigArchive.zip")
                    throw new Exception($"SearchInMemoryAsync Test E1 failed: Expected 1 hit with path separator, got {hitsE1.Count}");
            }

            // -------------------------------------------------------------
            // 3. Content Search: 高速テキスト、PDF、Early Drop の検証
            // -------------------------------------------------------------
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_SearchTest_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);

                try
                {
                    string fileSecret = Path.Combine(tempDir, "Confidential_Doc.txt");
                    string filePublic = Path.Combine(tempDir, "Public_Notes.txt");
                    string fileBinary = Path.Combine(tempDir, "Sample_Binary.dat");
                    string filePdf = Path.Combine(tempDir, "Contract_Doc.pdf");

                    // 1. テキストファイル
                    File.WriteAllText(fileSecret, "本ドキュメントは極秘プロジェクト計画書です。\n重要キーワード: 【最高機密プロジェクトX】\n社外秘として厳重に保管してください。", Encoding.UTF8);
                    File.WriteAllText(filePublic, "これは一般公開用の議事録です。\n通常の会議内容が記載されています。", Encoding.UTF8);

                    // 2. バイナリファイル (先頭4KBにNULLバイト混入 -> Early Drop検証)
                    byte[] binaryBytes = new byte[8192];
                    binaryBytes[0] = 0x41;
                    binaryBytes[1] = 0x00; // NULLバイト
                    binaryBytes[2] = 0x00; // NULLバイト
                    byte[] secretBytes = Encoding.UTF8.GetBytes("最高機密プロジェクトX");
                    Buffer.BlockCopy(secretBytes, 0, binaryBytes, 100, secretBytes.Length);
                    File.WriteAllBytes(fileBinary, binaryBytes);

                    // 3. テキスト埋め込みPDF
                    string mockPdfText = "%PDF-1.4\n1 0 obj\n<< /Title (Confidential Agreement) >>\nendobj\n2 0 obj\n<< /Length 60 >>\nstream\nBT\n/F1 12 Tf\n(契約書キーワード: 最高機密プロジェクトX) Tj\nET\nendstream\nendobj\nxref\n0 3\ntrailer\n<< /Root 1 0 R >>\n%%EOF";
                    File.WriteAllText(filePdf, mockPdfText, Encoding.UTF8);

                    // 4. UTF-16LE テキストファイル (Sol指摘: NULLバイトが混入しても誤判定で落ちないこと)
                    string fileUtf16 = Path.Combine(tempDir, "Utf16_Doc.txt");
                    File.WriteAllText(fileUtf16, "UTF-16テキストの内容: 最高機密プロジェクトX", Encoding.Unicode);

                    // 5. サブフォルダー (Sol指摘: Direct Search でのフォルダー包含)
                    string subFolder = Path.Combine(tempDir, "SubFolder_ProjectX");
                    Directory.CreateDirectory(subFolder);

                    // 6. 名前 OR 本文（複数キーワード）テスト用ファイル
                    string filePartial = Path.Combine(tempDir, "予算_計画書.txt");
                    File.WriteAllText(filePartial, "2026年の事業方針について記載する。", Encoding.UTF8);

                    // 検索テスト (content:"最高機密プロジェクトX")
                    var qContent = SearchQueryParser.Parse("content:\"最高機密プロジェクトX\"");
                    var batchResults = new List<SearchResultItem>();
                    var batchYield = new Progress<IReadOnlyList<SearchResultItem>>(items => batchResults.AddRange(items));

                    var results = await searchEngine.SearchDirectFolderAsync(tempDir, qContent, batchYield, null, CancellationToken.None);

                    // テキストファイル、PDF、UTF-16ファイルがヒットし、バイナリファイルはEarly Dropで除外されていること
                    bool txtFound = false;
                    bool pdfFound = false;
                    bool utf16Found = false;
                    bool binFound = false;

                    foreach (var r in results)
                    {
                        if (r.FullPath.EndsWith("Confidential_Doc.txt")) txtFound = true;
                        if (r.FullPath.EndsWith("Contract_Doc.pdf")) pdfFound = true;
                        if (r.FullPath.EndsWith("Utf16_Doc.txt")) utf16Found = true;
                        if (r.FullPath.EndsWith("Sample_Binary.dat")) binFound = true;
                    }

                    if (!txtFound)
                        throw new Exception("Content Search failed: Confidential_Doc.txt was not detected.");

                    if (!pdfFound)
                        throw new Exception("Content Search failed: Contract_Doc.pdf was not detected via PDF search engine.");

                    if (!utf16Found)
                        throw new Exception("Content Search failed: Utf16_Doc.txt was dropped incorrectly as binary.");

                    if (binFound)
                        throw new Exception("Content Search failed: Sample_Binary.dat should have been dropped by Early Drop (null bytes).");

                    // 7. Direct Search での「フォルダも含める」検証
                    var qFolder = SearchQueryParser.Parse("ProjectX");
                    qFolder.IncludeFolders = true;
                    var folderResults = await searchEngine.SearchDirectFolderAsync(tempDir, qFolder, null, null, CancellationToken.None);
                    if (!folderResults.Any(r => r.IsDirectory && r.Name == "SubFolder_ProjectX"))
                        throw new Exception("Direct Search failed: SubFolder_ProjectX was not found with IncludeFolders=true.");

                    // 8. SearchContentMode ("本文も検索" トグル連動 & 複数キーワード 名前 OR 本文) 検証
                    // "予算 2026": ファイル名に「予算」、本文に「2026」があるファイルがマッチすること
                    var qMulti = SearchQueryParser.Parse("予算 2026");
                    qMulti.SearchContentMode = true; // トグルON

                    var multiResults = await searchEngine.SearchDirectFolderAsync(tempDir, qMulti, null, null, CancellationToken.None);
                    if (!multiResults.Any(r => r.FullPath.EndsWith("予算_計画書.txt")))
                        throw new Exception("Multi-keyword Name OR Content search failed: 予算_計画書.txt was not detected.");

                    // -------------------------------------------------------------
                    // 4. ContentIndexService: SQLite FTS5 インデックス・差分更新・ミリ秒検索
                    // -------------------------------------------------------------
                    string customDbPath = Path.Combine(tempDir, "TestIndex.db");
                    var indexService = new ContentIndexService(customDbPath);

                    // A. 初回インデックス作成
                    await indexService.IndexFolderAsync(tempDir, null, CancellationToken.None);

                    var stats1 = indexService.GetStats();
                    if (stats1.TotalFiles < 3)
                        throw new Exception($"ContentIndexService failed: Expected >= 3 files indexed, got {stats1.TotalFiles}");

                    // HasIndexForPath の検証
                    if (!indexService.HasIndexForPath(tempDir))
                        throw new Exception("ContentIndexService failed: HasIndexForPath returned false for indexed directory.");

                    // B. ミリ秒全文検索 (FTS5 trigram + snippet)
                    var qSecret = SearchQueryParser.Parse("最高機密");
                    qSecret.SearchContentMode = true;
                    var ftsHits = await indexService.SearchIndexedAsync(qSecret, tempDir, CancellationToken.None);
                    if (ftsHits.Count == 0)
                        throw new Exception("ContentIndexService failed: Keyword '最高機密' not found in indexed search.");

                    bool ftsDocFound = ftsHits.Any(h => h.FullPath.EndsWith("Confidential_Doc.txt") && (h.ContentSnippet?.Contains("最高機密") ?? false));
                    if (!ftsDocFound)
                        throw new Exception("ContentIndexService failed: Confidential_Doc.txt with snippet was not found in FTS results.");

                    // B1-2. 本文OFF (SearchContentMode = false) の時は、ファイル名に含まれない本文マッチが除外されること (Sol指摘: 意味論の厳格化)
                    var qSecretOff = SearchQueryParser.Parse("最高機密");
                    qSecretOff.SearchContentMode = false;
                    var offHits = await indexService.SearchIndexedAsync(qSecretOff, tempDir, CancellationToken.None);
                    if (offHits.Count > 0)
                        throw new Exception("ContentIndexService failed: Content matches should NOT appear when SearchContentMode is false.");

                    // B2. 日本語2文字検索のハイブリッド検証 (Sol指摘: 3文字未満は trigram MATCH エラーにならず LIKE fallback で正確にヒットすること)
                    var qTwoChar = SearchQueryParser.Parse("契約");
                    qTwoChar.SearchContentMode = true;
                    var twoCharHits = await indexService.SearchIndexedAsync(qTwoChar, tempDir, CancellationToken.None);
                    if (!twoCharHits.Any(h => h.FullPath.EndsWith("Contract_Doc.pdf")))
                        throw new Exception("ContentIndexService failed: 2-character Japanese keyword '契約' should hit Contract_Doc.pdf via LIKE fallback.");

                    // B3. 複数キーワード AND 検索 (Sol指摘: 単一フレーズ化せず個別トークンAND結合でヒットすること)
                    var qAndSearch = SearchQueryParser.Parse("予算 2026");
                    qAndSearch.SearchContentMode = true;
                    var andHits = await indexService.SearchIndexedAsync(qAndSearch, tempDir, CancellationToken.None);
                    if (!andHits.Any(h => h.FullPath.EndsWith("予算_計画書.txt")))
                        throw new Exception("ContentIndexService failed: Multi-keyword AND search '予算 2026' did not hit 予算_計画書.txt.");

                    // B4. クエリ構文の SQL 貫通検証 (ext:txt size:<10MB !NoSuchWord)
                    var qFilter = SearchQueryParser.Parse("ext:txt size:<10MB !xyznotfound 最高機密");
                    qFilter.SearchContentMode = true;
                    var filterHits = await indexService.SearchIndexedAsync(qFilter, tempDir, CancellationToken.None);
                    if (!filterHits.Any(h => h.FullPath.EndsWith("Confidential_Doc.txt")))
                        throw new Exception("ContentIndexService failed: Filtered indexed search with ext: and size: failed to hit Confidential_Doc.txt.");
                    if (filterHits.Any(h => h.FullPath.EndsWith(".pdf")))
                        throw new Exception("ContentIndexService failed: Filtered indexed search with ext:txt returned .pdf file.");

                    // C. 差分更新（変更なしスキップ ＆ 更新ファイルのみ再インデックス）
                    string fileDynamic = Path.Combine(tempDir, "Dynamic_Doc.txt");
                    File.WriteAllText(fileDynamic, "初期バージョン: アルファ版テストコード", Encoding.UTF8);

                    // 2回目インデックス（fileDynamicが新規追加）
                    var diffReport = await indexService.IndexFolderAsync(tempDir, null, CancellationToken.None);

                    // 既存ファイルはすべてスキップされ、新規ファイルのみインデックスされたか
                    if (diffReport.NewlyIndexedCount != 1)
                        throw new Exception($"ContentIndexService Diff Update failed: Expected newlyIndexed=1, got {diffReport.NewlyIndexedCount}");

                    if (diffReport.AlreadyIndexed < 3)
                        throw new Exception($"ContentIndexService Diff Update failed: Expected skipCount>=3, got {diffReport.AlreadyIndexed}");

                    // D. 新規ファイル検索
                    var qDyn = SearchQueryParser.Parse("アルファ版");
                    qDyn.SearchContentMode = true;
                    var dynHits = await indexService.SearchIndexedAsync(qDyn, tempDir, CancellationToken.None);
                    if (!dynHits.Any(h => h.FullPath.EndsWith("Dynamic_Doc.txt")))
                        throw new Exception("ContentIndexService failed: Newly added Dynamic_Doc.txt was not found via FTS.");

                    // E. 削除亡霊ファイルのクリーンアップ検証 (Sol指摘: 削除・リネームしたファイルが次回インデックスでDBから消去されること)
                    File.Delete(fileDynamic);
                    var cleanReport = await indexService.IndexFolderAsync(tempDir, null, CancellationToken.None);
                    if (cleanReport.DeletedCount != 1)
                        throw new Exception($"ContentIndexService Ghost Cleanup failed: Expected DeletedCount=1, got {cleanReport.DeletedCount}");

                    var ghostHits = await indexService.SearchIndexedAsync(qDyn, tempDir, CancellationToken.None);
                    if (ghostHits.Any(h => h.FullPath.EndsWith("Dynamic_Doc.txt")))
                        throw new Exception("ContentIndexService Ghost Cleanup failed: Deleted file still appeared in indexed search.");

                    // F. Sol指摘: パス境界の厳格性検証 (近接類似フォルダー tempDir + "_Backup" を巻き込まないこと)
                    string neighborDir = tempDir + "_Backup";
                    if (indexService.HasIndexForPath(neighborDir))
                        throw new Exception($"ContentIndexService Path Boundary failed: Neighbor directory '{neighborDir}' should NOT be considered indexed.");

                    var neighborHits = await indexService.SearchIndexedAsync(qSecret, neighborDir, CancellationToken.None);
                    if (neighborHits.Count > 0)
                        throw new Exception($"ContentIndexService Path Boundary failed: Scoped search for '{neighborDir}' should return 0 results.");

                    // G. 抜本案検証: 非本文ファイル (.zip, .exe) のメタデータインデックス登録 & 通常検索ミリ秒ヒット検証
                    string fileZip = Path.Combine(tempDir, "Deploy_Package.zip");
                    string fileExe = Path.Combine(tempDir, "Installer_Tool.exe");
                    File.WriteAllBytes(fileZip, new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x00 }); // PK signature
                    File.WriteAllBytes(fileExe, new byte[] { 0x4D, 0x5A, 0x90, 0x00 }); // MZ signature

                    await indexService.IndexFolderAsync(tempDir, null, CancellationToken.None);

                    // 通常の名前検索 (SearchContentMode = false) では .zip や .exe が即座にヒットすること
                    var qZip = SearchQueryParser.Parse("Deploy_Package");
                    var zipHits = await indexService.SearchIndexedAsync(qZip, tempDir, CancellationToken.None);
                    if (!zipHits.Any(h => h.FullPath.EndsWith("Deploy_Package.zip")))
                        throw new Exception("ContentIndexService 抜本案検証失敗: 通常検索で非本文ファイル Deploy_Package.zip がヒットしませんでした。");

                    var qExeExt = SearchQueryParser.Parse("ext:exe");
                    var exeHits = await indexService.SearchIndexedAsync(qExeExt, tempDir, CancellationToken.None);
                    if (!exeHits.Any(h => h.FullPath.EndsWith("Installer_Tool.exe")))
                        throw new Exception("ContentIndexService 抜本案検証失敗: 拡張子検索 ext:exe で Installer_Tool.exe がヒットしませんでした。");

                    // 一方で本文検索モード (SearchContentMode = true) では、非本文ファイルはFTS対象外のためヒットしないこと
                    var qZipContent = SearchQueryParser.Parse("Deploy_Package");
                    qZipContent.SearchContentMode = true;
                    var zipContentHits = await indexService.SearchIndexedAsync(qZipContent, tempDir, CancellationToken.None);
                    if (zipContentHits.Any(h => h.FullPath.EndsWith("Deploy_Package.zip")))
                        throw new Exception("ContentIndexService 抜本案検証失敗: 本文検索モードで非本文ファイルがFTS結果に混入しました。");

                    // H. 最深Root判定検証 (親がCompleteでも子がErrorの場合は中断漏れとしてfalseを返すこと)
                    string subErrorDir = Path.Combine(tempDir, "SubProject_Broken");
                    Directory.CreateDirectory(subErrorDir);
                    using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={customDbPath}"))
                    {
                        conn.Open();
                        using var cmd = conn.CreateCommand();
                        cmd.CommandText = "INSERT INTO IndexedRoots (RootPath, Status, CoverageComplete, LastCompletedUtcTicks, TotalFiles, ExtractorVersion) VALUES (@root, 'Error', 0, @ticks, 0, 1)";
                        cmd.Parameters.AddWithValue("@root", subErrorDir);
                        cmd.Parameters.AddWithValue("@ticks", DateTime.UtcNow.Ticks);
                        cmd.ExecuteNonQuery();
                    }

                    // 子フォルダーは Error なので false
                    if (indexService.HasCompleteIndexForPath(subErrorDir))
                        throw new Exception("ContentIndexService 最深Root判定失敗: 子フォルダーがErrorなのにHasCompleteIndexForPathがtrueを返しました。");

                    // 親フォルダー自体は Complete なので true
                    if (!indexService.HasCompleteIndexForPath(tempDir))
                        throw new Exception("ContentIndexService 最深Root判定失敗: 親フォルダーHasCompleteIndexForPathがfalseを返しました。");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }

            // -------------------------------------------------------------
            // 4. SearchDirectFolderAsync: ライブ直接走査における本文検索のフライング加算防止 & 非対応拡張子事前スキップ検証
            // -------------------------------------------------------------
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_DirectContentTest_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    // 1. テストファイル群の準備
                    // A: ファイル名一致（画像だが名前一致で即時合格）
                    string fileImgHit = Path.Combine(tempDir, "秘密_Scan.jpg");
                    File.WriteAllBytes(fileImgHit, new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x01, 0x02 }); // 6 bytes

                    // B: 本文一致（テキストで中身にキーワードあり）
                    string fileTextHit = Path.Combine(tempDir, "Audit_Report.txt");
                    File.WriteAllText(fileTextHit, "社内限定: 本文に秘密の暗号キーを含む", Encoding.UTF8);

                    // C: 本文非対応＆名前不一致（画像、中身検索できずスキップされるべき）
                    string fileImgMiss = Path.Combine(tempDir, "Unrelated_Photo.png");
                    File.WriteAllBytes(fileImgMiss, new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A });

                    // D: バイナリ非対応＆名前不一致（exe、中身検索できずスキップされるべき）
                    string fileExeMiss = Path.Combine(tempDir, "Tool.exe");
                    File.WriteAllBytes(fileExeMiss, new byte[] { 0x4D, 0x5A, 0x90, 0x00 });

                    var qDirect = SearchQueryParser.Parse("秘密");
                    qDirect.SearchContentMode = true;

                    int maxProgressHitCount = 0;
                    var progress = new Progress<SearchProgressReport>(r =>
                    {
                        if (r.HitCount > maxProgressHitCount)
                        {
                            maxProgressHitCount = r.HitCount;
                        }
                    });

                    var hits = await searchEngine.SearchDirectFolderAsync(tempDir, qDirect, null, progress, CancellationToken.None);

                    // ヒット数はちょうど2件（fileImgHit と fileTextHit）であること
                    if (hits.Count != 2)
                        throw new Exception($"SearchDirectFolderAsync Content Search failed: Expected 2 hits, got {hits.Count}");

                    if (!hits.Any(h => h.FullPath == fileImgHit))
                        throw new Exception("SearchDirectFolderAsync Content Search failed: '秘密_Scan.jpg' was not matched by name.");

                    if (!hits.Any(h => h.FullPath == fileTextHit))
                        throw new Exception("SearchDirectFolderAsync Content Search failed: 'Audit_Report.txt' was not matched by content.");

                    if (hits.Any(h => h.FullPath == fileImgMiss || h.FullPath == fileExeMiss))
                        throw new Exception("SearchDirectFolderAsync Content Search failed: Non-supported binary files (png/exe) without matching name were mistakenly matched.");

                    // フライング加算防止検証: 途中進捗の HitCount が全ファイル数 (4件) に跳ね上がっていないこと
                    if (maxProgressHitCount > 2)
                        throw new Exception($"SearchDirectFolderAsync Content Search failed: Premature hit counting detected! Max reported progress hit count was {maxProgressHitCount}, expected <= 2.");

                    long expectedBytes = new FileInfo(fileImgHit).Length + new FileInfo(fileTextHit).Length;
                    long actualBytes = hits.Sum(h => h.SizeBytes);
                    if (actualBytes != expectedBytes)
                        throw new Exception($"SearchDirectFolderAsync Content Search failed: Total bytes mismatch. Expected {expectedBytes}, got {actualBytes}");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }

            // -------------------------------------------------------------
            // 5. ContentIndexService: インデックス完了日時 & 差分同期クールダウン (NeedsBackgroundSync) の検証
            // -------------------------------------------------------------
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_CooldownTest_" + Guid.NewGuid().ToString("N"));
                string customDb = Path.Combine(tempDir, "TestIndex.db");
                Directory.CreateDirectory(tempDir);
                try
                {
                    var indexService = new ContentIndexService(customDb);

                    // 1. 未インデックス状態では NeedsBackgroundSync は true
                    if (!indexService.NeedsBackgroundSync(tempDir, TimeSpan.FromMinutes(5)))
                        throw new Exception("NeedsBackgroundSync failed: Unindexed folder should require sync (expected true).");

                    if (indexService.GetLastIndexCompletedUtc(tempDir) != null)
                        throw new Exception("GetLastIndexCompletedUtc failed: Unindexed folder should return null.");

                    // 2. ファイルを1つ作ってインデックス作成
                    string testFile = Path.Combine(tempDir, "Doc.txt");
                    File.WriteAllText(testFile, "Hello World", Encoding.UTF8);
                    await indexService.IndexFolderAsync(tempDir, null, CancellationToken.None);

                    // 3. インデックス完了直後: 5分クールダウン内なので NeedsBackgroundSync は false (同期不要)
                    if (indexService.NeedsBackgroundSync(tempDir, TimeSpan.FromMinutes(5)))
                        throw new Exception("NeedsBackgroundSync failed: Freshly indexed folder should be in cooldown (expected false).");

                    // 4. クールダウン0秒指定なら true (即時再同期可能)
                    if (!indexService.NeedsBackgroundSync(tempDir, TimeSpan.Zero))
                        throw new Exception("NeedsBackgroundSync failed: Zero cooldown should require sync (expected true).");

                    // 5. GetLastIndexCompletedUtc が有効な直近のUTC日時を返すこと
                    var completedUtc = indexService.GetLastIndexCompletedUtc(tempDir);
                    if (!completedUtc.HasValue)
                        throw new Exception("GetLastIndexCompletedUtc failed: Completed index should return non-null DateTime.");

                    var diff = DateTime.UtcNow - completedUtc.Value;
                    if (diff.TotalSeconds < 0 || diff.TotalSeconds > 60)
                        throw new Exception($"GetLastIndexCompletedUtc failed: Returned timestamp {completedUtc.Value} is not within 60s of UTC now.");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }

            // -------------------------------------------------------------
            // 6. ContentIndexService: アクセス権喪失・削除ファイルの自動パージ (PurgeFilesAsync) の検証
            // -------------------------------------------------------------
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_PurgeTest_" + Guid.NewGuid().ToString("N"));
                string customDb = Path.Combine(tempDir, "TestPurge.db");
                Directory.CreateDirectory(tempDir);
                try
                {
                    var indexService = new ContentIndexService(customDb);

                    // 1. ファイルを2つ作成してインデックス化
                    string file1 = Path.Combine(tempDir, "Confidential_Doc1.txt");
                    string file2 = Path.Combine(tempDir, "Confidential_Doc2.txt");
                    File.WriteAllText(file1, "機密データ内容: プロジェクトAlphaの設計書", Encoding.UTF8);
                    File.WriteAllText(file2, "機密データ内容: プロジェクトBetaの予算書", Encoding.UTF8);

                    await indexService.IndexFolderAsync(tempDir, null, CancellationToken.None);

                    // 2. 本文検索で2件ヒットすることを確認
                    var qAll = SearchQueryParser.Parse("機密データ");
                    qAll.SearchContentMode = true;
                    var hitsAll = await indexService.SearchIndexedAsync(qAll, tempDir, CancellationToken.None);
                    if (hitsAll.Count != 2)
                        throw new Exception($"PurgeFilesAsync test failed: Expected 2 initial hits, got {hitsAll.Count}.");

                    // 3. file1 をパージ (アクセス拒否や削除をシミュレート)
                    int purged = await indexService.PurgeFilesAsync(new[] { file1 }, CancellationToken.None);
                    if (purged != 1)
                        throw new Exception($"PurgeFilesAsync test failed: Expected 1 purged file, got {purged}.");

                    // 4. 再度検索: file1 は完全に除外され、file2 のみ1件ヒットすること
                    var hitsAfter = await indexService.SearchIndexedAsync(qAll, tempDir, CancellationToken.None);
                    if (hitsAfter.Count != 1)
                        throw new Exception($"PurgeFilesAsync test failed: Expected 1 hit after purge, got {hitsAfter.Count}.");

                    if (!string.Equals(hitsAfter[0].FullPath, file2, StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"PurgeFilesAsync test failed: Remaining hit should be file2, got {hitsAfter[0].FullPath}.");

                    // 5. 本文OFF (ファイル名検索) でも file1 が IndexedFiles から完全抹消されていること
                    var qName = SearchQueryParser.Parse("Confidential_Doc1");
                    qName.SearchContentMode = false;
                    var hitsName = await indexService.SearchIndexedAsync(qName, tempDir, CancellationToken.None);
                    if (hitsName.Count != 0)
                        throw new Exception("PurgeFilesAsync test failed: Purged file was still present in IndexedFiles table.");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }
}

