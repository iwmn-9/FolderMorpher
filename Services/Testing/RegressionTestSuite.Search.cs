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

                    // 一方で本文検索モード (SearchContentMode = true) でも、ファイル名が合致する場合は非本文ファイル（ZIP）がヒットすること (ADR 76)
                    var qZipName = SearchQueryParser.Parse("Deploy_Package");
                    qZipName.SearchContentMode = true;
                    var zipNameHits = await indexService.SearchIndexedAsync(qZipName, tempDir, CancellationToken.None);
                    if (!zipNameHits.Any(h => h.FullPath.EndsWith("Deploy_Package.zip")))
                        throw new Exception("ContentIndexService ADR 76検証失敗: ファイル名が合致する非本文ファイル Deploy_Package.zip が本文検索モードで除外されました。");

                    // ファイル名に含まれない本文キーワードで検索した場合は、非本文ファイルはFTS対象外のためヒットしないこと
                    var qZipContent = SearchQueryParser.Parse("最高機密");
                    qZipContent.SearchContentMode = true;
                    var zipContentHits = await indexService.SearchIndexedAsync(qZipContent, tempDir, CancellationToken.None);
                    if (zipContentHits.Any(h => h.FullPath.EndsWith("Deploy_Package.zip")))
                        throw new Exception("ContentIndexService 抜本案検証失敗: 本文検索モードで名前不一致の非本文ファイルがFTS結果に混入しました。");

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

            // -------------------------------------------------------------
            // 7. ContentIndexService: MetadataFts (trigram), 短語LIKE, ScanGeneration 世代削除の検証
            // -------------------------------------------------------------
            {
                string tempDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_MetaFtsTest_" + Guid.NewGuid().ToString("N"));
                string dbDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_MetaFtsDb_" + Guid.NewGuid().ToString("N"));
                string customDb = Path.Combine(dbDir, "TestMetaFts.db");
                Directory.CreateDirectory(tempDir);
                Directory.CreateDirectory(dbDir);
                try
                {
                    var indexService = new ContentIndexService(customDb);

                    // 1. ファイルを作成
                    // - file1: 3文字以上キーワード "業務委託契約書_2026.xlsx" (本文なし)
                    // - file2: 2文字キーワード "契約_覚書.txt" (本文に "最高機密プロジェクト")
                    // - file3: 削除テスト用 "臨時ファイル_Temp.pdf" (本文なし)
                    string file1 = Path.Combine(tempDir, "業務委託契約書_2026.xlsx");
                    string file2 = Path.Combine(tempDir, "契約_覚書.txt");
                    string file3 = Path.Combine(tempDir, "臨時ファイル_Temp.pdf");

                    File.WriteAllBytes(file1, new byte[1024]);
                    File.WriteAllText(file2, "本覚書の内容は最高機密プロジェクトに関する規定である。", Encoding.UTF8);
                    File.WriteAllBytes(file3, new byte[2048]);

                    var rep1 = await indexService.IndexFolderAsync(tempDir, null, CancellationToken.None);
                    if (rep1.TotalDiscovered < 3)
                        throw new Exception($"MetadataFts Test failed: Expected at least 3 discovered files, got {rep1.TotalDiscovered}.");

                    // 2. MetadataFts trigram MATCH 検証 (3文字以上 "業務委託" または "契約書")
                    var qTri = SearchQueryParser.Parse("業務委託");
                    qTri.SearchContentMode = false; // ファイル名のみ
                    var hitsTri = await indexService.SearchIndexedAsync(qTri, tempDir, CancellationToken.None);
                    if (hitsTri.Count != 1 || !hitsTri[0].FullPath.EndsWith("業務委託契約書_2026.xlsx"))
                        throw new Exception($"MetadataFts trigram MATCH failed: Expected file1 hit, got {hitsTri.Count} hits.");

                    // 3. 短語 (1〜2文字 "契約") の f.Name LIKE フォールバック検証
                    var qShort = SearchQueryParser.Parse("契約");
                    qShort.SearchContentMode = false; // ファイル名のみ
                    var hitsShort = await indexService.SearchIndexedAsync(qShort, tempDir, CancellationToken.None);
                    // "業務委託契約書_2026.xlsx" と "契約_覚書.txt" の2件がヒットすること
                    if (hitsShort.Count != 2)
                        throw new Exception($"Metadata short-word LIKE fallback failed: Expected 2 hits for '契約', got {hitsShort.Count}.");

                    // 4. ContentFts (rowid=FileId) と MetadataFts の UNION ハイブリッド検証
                    // "最高機密" (file2の本文のみ) ＋ "業務委託" (file1のファイル名のみ)
                    var qUnion1 = SearchQueryParser.Parse("最高機密");
                    qUnion1.SearchContentMode = true; // 本文ON
                    var hitsUnion1 = await indexService.SearchIndexedAsync(qUnion1, tempDir, CancellationToken.None);
                    if (hitsUnion1.Count != 1 || !hitsUnion1[0].FullPath.EndsWith("契約_覚書.txt") || string.IsNullOrEmpty(hitsUnion1[0].ContentSnippet))
                        throw new Exception($"ContentFts rowid MATCH failed: Expected file2 with snippet, got {hitsUnion1.Count} hits.");

                    // 5. ScanGeneration による亡霊ファイル自動削除の検証
                    // file3 をディスクから物理削除し、再インデックス
                    File.Delete(file3);
                    var rep2 = await indexService.IndexFolderAsync(tempDir, null, CancellationToken.None);
                    if (rep2.DeletedCount != 1)
                        throw new Exception($"ScanGeneration purge failed: Expected 1 deleted file, got {rep2.DeletedCount}.");

                    // file3 ("臨時ファイル") を検索して 0 件であることを確認
                    var qGhost = SearchQueryParser.Parse("臨時ファイル");
                    var hitsGhost = await indexService.SearchIndexedAsync(qGhost, tempDir, CancellationToken.None);
                    if (hitsGhost.Count != 0)
                        throw new Exception("ScanGeneration purge failed: Ghost file was still present in index after re-scan.");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                    try { Directory.Delete(dbDir, true); } catch { }
                }
            }

            // -------------------------------------------------------------
            // 8. Change Notify Watcher, MFT Preconditions & Filter/Sort Logic
            // -------------------------------------------------------------
            {
                // 1. MftScanService の事前条件・保護検証
                if (AstraSize.Services.Mft.MftScanService.CanUseMft(@"\\fileserver\share\dept"))
                    throw new Exception("MftScanService.CanUseMft failed: UNC path must NOT allow direct MFT scan.");

                if (AstraSize.Services.Mft.MftScanService.CanUseMft(string.Empty))
                    throw new Exception("MftScanService.CanUseMft failed: Empty path must return false.");

                // 2. ContentIndexWatcherService のリアルタイム差分同期検証
                string watchTempDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_WatchTest_" + Guid.NewGuid().ToString("N"));
                string watchDbDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_WatchDb_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(watchTempDir);
                Directory.CreateDirectory(watchDbDir);

                try
                {
                    string customDb = Path.Combine(watchDbDir, "WatchIndex.db");
                    var indexService = new ContentIndexService(customDb);
                    using var watcher = new ContentIndexWatcherService(indexService);

                    bool started = watcher.StartWatching(watchTempDir);
                    if (!started)
                        throw new Exception("ContentIndexWatcherService failed to start watching temporary directory.");

                    // 新規ファイルを作成してバッチ同期を実行
                    string watchFile1 = Path.Combine(watchTempDir, "リアルタイム速報_2026.txt");
                    File.WriteAllText(watchFile1, "即時インデックス反映テスト本文です。");

                    // OS イベント通知待ち (最大500ms)
                    for (int i = 0; i < 10 && !watcher.HasPendingChanges; i++)
                    {
                        await Task.Delay(50);
                    }
                    if (!watcher.HasPendingChanges)
                    {
                        watcher.EnqueueChange(watchFile1, FileChangeType.Upsert);
                    }
                    await watcher.ProcessPendingChangesAsync();

                    var qWatch = SearchQueryParser.Parse("リアルタイム速報");
                    var watchHits = await indexService.SearchIndexedAsync(qWatch, watchTempDir, CancellationToken.None);
                    if (watchHits.Count != 1 || !watchHits[0].FullPath.Equals(watchFile1, StringComparison.OrdinalIgnoreCase))
                        throw new Exception($"Watcher upsert failed: Expected 1 hit for new file, got {watchHits.Count}.");

                    // ファイルを削除してバッチ同期を実行
                    File.Delete(watchFile1);
                    for (int i = 0; i < 10 && !watcher.HasPendingChanges; i++)
                    {
                        await Task.Delay(50);
                    }
                    if (!watcher.HasPendingChanges)
                    {
                        watcher.EnqueueChange(watchFile1, FileChangeType.Delete);
                    }
                    await watcher.ProcessPendingChangesAsync();

                    var watchHitsAfterDelete = await indexService.SearchIndexedAsync(qWatch, watchTempDir, CancellationToken.None);
                    if (watchHitsAfterDelete.Count != 0)
                        throw new Exception("Watcher delete failed: File remained in index after deletion.");
                }
                finally
                {
                    try { Directory.Delete(watchTempDir, true); } catch { }
                    try { Directory.Delete(watchDbDir, true); } catch { }
                }

                // 3. 検索結果ソート（更新日時新旧、作成日時新旧、関連度）およびフォルダーバッジ検証
                var mockItems = new List<SearchResultItem>
                {
                    new() { Name = "報告書.xlsx", FullPath = @"C:\Docs\報告書.xlsx", Extension = ".xlsx", SizeBytes = 5000, LastWriteTime = new DateTime(2026, 1, 1), CreationTime = new DateTime(2025, 1, 1), IsDirectory = false },
                    new() { Name = "仕様書.pdf", FullPath = @"C:\Docs\仕様書.pdf", Extension = ".pdf", SizeBytes = 20000, LastWriteTime = new DateTime(2026, 2, 1), CreationTime = new DateTime(2025, 6, 1), IsDirectory = false },
                    new() { Name = "写真.jpg", FullPath = @"C:\Photos\写真.jpg", Extension = ".jpg", SizeBytes = 100000, LastWriteTime = new DateTime(2026, 3, 1), CreationTime = new DateTime(2025, 12, 1), IsDirectory = false },
                    new() { Name = "SubFolder", FullPath = @"C:\SubFolder", Extension = "", SizeBytes = 0, LastWriteTime = new DateTime(2026, 6, 1), CreationTime = new DateTime(2026, 1, 1), IsDirectory = true }
                };

                // 作成日時降順ソート検証
                var sortedByCreatedDesc = mockItems.OrderByDescending(x => x.CreationTime).ToList();
                if (sortedByCreatedDesc[0].Name != "SubFolder" || sortedByCreatedDesc[1].Name != "写真.jpg")
                    throw new Exception("Sort logic failed: CreationTime descending order incorrect.");

                // 作成日時昇順ソート検証
                var sortedByCreatedAsc = mockItems.OrderBy(x => x.CreationTime).ToList();
                if (sortedByCreatedAsc[0].Name != "報告書.xlsx" || sortedByCreatedAsc[1].Name != "仕様書.pdf")
                    throw new Exception("Sort logic failed: CreationTime ascending order incorrect.");

                // 更新日時降順ソート検証
                var sortedByDateDesc = mockItems.OrderByDescending(x => x.LastWriteTime).ToList();
                if (sortedByDateDesc[0].Name != "SubFolder" || sortedByDateDesc[1].Name != "写真.jpg")
                    throw new Exception("Sort logic failed: Date descending order incorrect.");

                // フォルダーバッジが "DIR" ではなく空文字（ベクター描画対応）であることを検証
                var folderBadge = TablerBadgeHelper.GetBadge(@"C:\SubFolder", isDirectory: true);
                if (!string.IsNullOrEmpty(folderBadge.Text))
                    throw new Exception($"TablerBadgeHelper failed: Folder badge text must be empty (vector rendering), got '{folderBadge.Text}'.");
                if (folderBadge.Background != "#FEF3C7" || folderBadge.BorderBrush != "#D97706")
                    throw new Exception("TablerBadgeHelper failed: Folder badge color must be Amber palette.");

                // -------------------------------------------------------------
                // 4. セクション 9: Folder インデックス検索（IsDirectory）＆ 自前DB除外 ＆ 世代競合防止
                // -------------------------------------------------------------
                string dirTestRoot = Path.Combine(Path.GetTempPath(), "FM_DirTest_" + Guid.NewGuid().ToString("N"));
                string dirTestDb = Path.Combine(dirTestRoot, "SelfContainedIndex.db"); // 自前DBを走査ルート内に配置！
                Directory.CreateDirectory(dirTestRoot);

                try
                {
                    string subDir = Path.Combine(dirTestRoot, "2026_プロジェクト資料");
                    Directory.CreateDirectory(subDir);
                    string subFile = Path.Combine(subDir, "プロジェクト計画.txt");
                    File.WriteAllText(subFile, "計画本文", Encoding.UTF8);

                    var selfService = new ContentIndexService(dirTestDb);

                    // A. 自前DB除外の検証
                    if (!selfService.IsDatabaseFile(dirTestDb) ||
                        !selfService.IsDatabaseFile(dirTestDb + "-wal") ||
                        !selfService.IsDatabaseFile(dirTestDb + "-shm"))
                    {
                        throw new Exception("IsDatabaseFile failed: Database path or its WAL/SHM was not detected as database file.");
                    }
                    if (selfService.IsDatabaseFile(subFile))
                    {
                        throw new Exception("IsDatabaseFile failed: Normal text file was falsely identified as database file.");
                    }

                    // B. インデックス作成（自前DBが同居していても自己食いしないこと）
                    var dirReport = await selfService.IndexFolderAsync(dirTestRoot, null, CancellationToken.None);
                    // MFT走査: subFile(1) + subDir(1) = 2
                    // 非MFT走査: subFile(1) + subDir(1) + dirTestRoot(1) = 3 (SelfContainedIndex.dbは除外!)
                    if (dirReport.NewlyIndexedCount != 2 && dirReport.NewlyIndexedCount != 3)
                    {
                        throw new Exception($"Self-contained DB exclusion failed: Expected newlyIndexed=2 or 3, got {dirReport.NewlyIndexedCount}");
                    }

                    // C. フォルダを含める=OFF（通常検索）: ファイルのみヒット
                    var qNoDir = SearchQueryParser.Parse("プロジェクト");
                    qNoDir.IncludeFolders = false;
                    var hitsNoDir = await selfService.SearchIndexedAsync(qNoDir, dirTestRoot, CancellationToken.None);
                    if (hitsNoDir.Any(h => h.IsDirectory))
                    {
                        throw new Exception("IncludeFolders=false failed: Directory appeared in search results.");
                    }
                    if (!hitsNoDir.Any(h => h.FullPath.EndsWith("プロジェクト計画.txt")))
                    {
                        throw new Exception("IncludeFolders=false failed: Text file did not hit.");
                    }

                    // D. フォルダを含める=ON: フォルダーも trigram MATCH でヒットすること
                    var qWithDir = SearchQueryParser.Parse("プロジェクト");
                    qWithDir.IncludeFolders = true;
                    var hitsWithDir = await selfService.SearchIndexedAsync(qWithDir, dirTestRoot, CancellationToken.None);
                    if (!hitsWithDir.Any(h => h.IsDirectory && h.Name.Contains("プロジェクト資料")))
                    {
                        throw new Exception("IncludeFolders=true failed: Directory '2026_プロジェクト資料' was not found via FTS.");
                    }

                    // E. 世代競合防止: スキャン中判定が正しく機能すること
                    if (selfService.IsScanningRoot(dirTestRoot))
                    {
                        throw new Exception("IsScanningRoot failed: Scan already completed but still marked as active.");
                    }
                }
                finally
                {
                    try { Directory.Delete(dirTestRoot, true); } catch { }
                }

                // -------------------------------------------------------------
                // 5. セクション 10: 本文ON時名前ヒット（Status>=0）＆ 最深Root Generation ＆ Subtree Purge (ADR 76)
                // -------------------------------------------------------------
                string s10Root = Path.Combine(Path.GetTempPath(), "FM_S10Test_" + Guid.NewGuid().ToString("N"));
                string s10Db = Path.Combine(s10Root, "S10Index.db");
                Directory.CreateDirectory(s10Root);

                try
                {
                    // A. 本文ON時でも非本文ファイル（ZIP）やフォルダーが名前一致でヒットすること
                    string s10SubDir = Path.Combine(s10Root, "契約関連フォルダー");
                    Directory.CreateDirectory(s10SubDir);
                    string s10Zip = Path.Combine(s10Root, "契約書_アーカイブ.zip");
                    File.WriteAllBytes(s10Zip, new byte[] { 0x50, 0x4B, 0x03, 0x04 }); // PK
                    string s10Txt = Path.Combine(s10SubDir, "契約約款.txt");
                    File.WriteAllText(s10Txt, "通常テキストの本文です", Encoding.UTF8);

                    var s10Service = new ContentIndexService(s10Db);
                    await s10Service.IndexFolderAsync(s10Root, null, CancellationToken.None);

                    // 「本文も検索」ON ＋ 「フォルダも含める」ON で「契約」を検索
                    var qContentOn = SearchQueryParser.Parse("契約");
                    qContentOn.SearchContentMode = true; // 本文も検索 ON！
                    qContentOn.IncludeFolders = true;    // フォルダも含める ON！

                    var s10Hits = await s10Service.SearchIndexedAsync(qContentOn, s10Root, CancellationToken.None);

                    bool hitZip = s10Hits.Any(h => h.FullPath.EndsWith("契約書_アーカイブ.zip"));
                    bool hitDir = s10Hits.Any(h => h.IsDirectory && h.Name == "契約関連フォルダー");
                    bool hitTxt = s10Hits.Any(h => h.FullPath.EndsWith("契約約款.txt"));

                    if (!hitZip)
                        throw new Exception("ADR 76 Verification Failed: Non-content file (ZIP) did not hit when SearchContentMode=true.");
                    if (!hitDir)
                        throw new Exception("ADR 76 Verification Failed: Directory did not hit when SearchContentMode=true & IncludeFolders=true.");
                    if (!hitTxt)
                        throw new Exception("ADR 76 Verification Failed: Text file did not hit when SearchContentMode=true.");

                    // B. 最深Root Generation の検証 (親 Root Gen 50, 子 Root Gen 2)
                    string parentRoot = Path.Combine(s10Root, "ParentRoot");
                    string childRoot = Path.Combine(parentRoot, "ChildDept");
                    Directory.CreateDirectory(childRoot);

                    using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={s10Db}"))
                    {
                        conn.Open();
                        using var cmdIns = conn.CreateCommand();
                        cmdIns.CommandText = @"
                            INSERT INTO IndexedRoots (RootPath, Status, CoverageComplete, LastCompletedUtcTicks, TotalFiles, ExtractorVersion, CurrentGeneration)
                            VALUES (@parent, 'Complete', 1, 1000, 10, 1, 50);
                            INSERT INTO IndexedRoots (RootPath, Status, CoverageComplete, LastCompletedUtcTicks, TotalFiles, ExtractorVersion, CurrentGeneration)
                            VALUES (@child, 'Complete', 1, 1000, 5, 1, 2);";
                        cmdIns.Parameters.AddWithValue("@parent", parentRoot);
                        cmdIns.Parameters.AddWithValue("@child", childRoot);
                        cmdIns.ExecuteNonQuery();
                    }

                    string fileInChild = Path.Combine(childRoot, "child_file.txt");
                    string fileInParent = Path.Combine(parentRoot, "parent_file.txt");

                    int genChild = s10Service.GetGenerationForPath(fileInChild);
                    int genParent = s10Service.GetGenerationForPath(fileInParent);

                    if (genChild != 2)
                        throw new Exception($"ADR 76 Verification Failed: Expected child file Gen=2 (deepest root), got {genChild}");
                    if (genParent != 50)
                        throw new Exception($"ADR 76 Verification Failed: Expected parent file Gen=50, got {genParent}");

                    // C. Subtree Purge の検証 (ディレクトリを指定してPurgeすると配下全ファイル・フォルダが消去されること)
                    int purgedCount = await s10Service.PurgeFilesAsync(new[] { s10SubDir });
                    if (purgedCount < 2) // 契約関連フォルダー (1) + 契約約款.txt (1) = 最低2件
                        throw new Exception($"ADR 76 Subtree Purge Failed: Expected >=2 items purged for subtree, got {purgedCount}");

                    var hitsAfterPurge = await s10Service.SearchIndexedAsync(qContentOn, s10Root, CancellationToken.None);
                    if (hitsAfterPurge.Any(h => h.FullPath.Contains("契約関連フォルダー") || h.FullPath.Contains("契約約款.txt")))
                        throw new Exception("ADR 76 Subtree Purge Failed: Subtree items still exist in indexed results after purge.");
                }
                finally
                {
                    try { Directory.Delete(s10Root, true); } catch { }
                }
            }

            // 11. 【ADR 77】2段階プログレッシブ検索（ファイル名先行通知 & 本文合流）の検証
            {
                string s11Root = Path.Combine(Path.GetTempPath(), "FM_Reg_S11_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(s11Root);
                string s11Db = Path.Combine(s11Root, "ContentIndex.db");

                try
                {
                    // ファイル名一致用（本文なし）
                    string nameMatchFile = Path.Combine(s11Root, "財務諸表_2026.csv");
                    File.WriteAllText(nameMatchFile, "col1,col2,col3\n1,2,3", Encoding.UTF8);

                    // 本文一致用（ファイル名にキーワードなし）
                    string contentMatchFile = Path.Combine(s11Root, "internal_memo.txt");
                    File.WriteAllText(contentMatchFile, "今年の財務諸表に関する打ち合わせ議事録です。", Encoding.UTF8);

                    var s11Service = new ContentIndexService(s11Db);
                    await s11Service.IndexFolderAsync(s11Root, null, CancellationToken.None);

                    var qProgressive = SearchQueryParser.Parse("財務諸表");
                    qProgressive.SearchContentMode = true;

                    var earlyNameHits = new List<SearchResultItem>();
                    var finalHits = await s11Service.SearchIndexedAsync(
                        qProgressive,
                        s11Root,
                        ct: CancellationToken.None,
                        onNameHitsReady: items => earlyNameHits.AddRange(items));

                    // 1. ファイル名一致が先行通知 callback で届いていること
                    if (!earlyNameHits.Any(h => h.FullPath.EndsWith("財務諸表_2026.csv")))
                        throw new Exception("ADR 77 Verification Failed: Name-match file was not reported via early onNameHitsReady callback.");

                    // 2. 本文のみ一致ファイルは先行通知には含まれていないこと（本文フェーズ前のため）
                    if (earlyNameHits.Any(h => h.FullPath.EndsWith("internal_memo.txt")))
                        throw new Exception("ADR 77 Verification Failed: Content-only match should not appear in early name-hits callback.");

                    // 3. 最終結果にはファイル名一致と本文一致の両方が含まれていること
                    if (!finalHits.Any(h => h.FullPath.EndsWith("財務諸表_2026.csv")))
                        throw new Exception("ADR 77 Verification Failed: Name-match file missing from final hits.");
                    var memoHit = finalHits.FirstOrDefault(h => h.FullPath.EndsWith("internal_memo.txt"));
                    if (memoHit == null)
                        throw new Exception("ADR 77 Verification Failed: Content-match file missing from final hits.");
                    if (string.IsNullOrEmpty(memoHit.ContentSnippet))
                        throw new Exception("ADR 77 Verification Failed: Content-match file has empty snippet in final hits.");
                }
                finally
                {
                    try { Directory.Delete(s11Root, true); } catch { }
                }
            }

            // 12. 【ADR 78】カンマおよびパイプによるキーワードOR検索（AND of ORs）の検証
            {
                // A. パーサーの検証
                var qOrComma = SearchQueryParser.Parse("見積,請求 2026");
                if (qOrComma.KeywordGroups.Count != 2)
                    throw new Exception($"ADR 78 Parser Failed: Expected 2 keyword groups, got {qOrComma.KeywordGroups.Count}");
                if (qOrComma.KeywordGroups[0].Count != 2 || qOrComma.KeywordGroups[0][0] != "見積" || qOrComma.KeywordGroups[0][1] != "請求")
                    throw new Exception("ADR 78 Parser Failed: Group 0 did not match [見積, 請求]");
                if (qOrComma.KeywordGroups[1].Count != 1 || qOrComma.KeywordGroups[1][0] != "2026")
                    throw new Exception("ADR 78 Parser Failed: Group 1 did not match [2026]");

                var qOrPipe = SearchQueryParser.Parse("契約|約款");
                if (qOrPipe.KeywordGroups.Count != 1 || qOrPipe.KeywordGroups[0].Count != 2)
                    throw new Exception("ADR 78 Parser Failed: Pipe OR group count mismatch");

                var qQuoted = SearchQueryParser.Parse("\"見積,請求.csv\"");
                if (!qQuoted.ExactPhrases.Contains("見積,請求.csv"))
                    throw new Exception("ADR 78 Parser Failed: Quoted comma keyword should be ExactPhrase");

                // B. 実検索（SQLite FTS5 ＆ Direct Search）の検証
                string s12Root = Path.Combine(Path.GetTempPath(), "FM_Reg_S12_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(s12Root);
                string s12Db = Path.Combine(s12Root, "ContentIndex.db");

                try
                {
                    string f1 = Path.Combine(s12Root, "見積書_2026.pdf");
                    string f2 = Path.Combine(s12Root, "請求書_2026.pdf");
                    string f3 = Path.Combine(s12Root, "納品書_2026.pdf");
                    string f4 = Path.Combine(s12Root, "見積書_2025.pdf");

                    File.WriteAllText(f1, "dummy", Encoding.UTF8);
                    File.WriteAllText(f2, "dummy", Encoding.UTF8);
                    File.WriteAllText(f3, "dummy", Encoding.UTF8);
                    File.WriteAllText(f4, "dummy", Encoding.UTF8);

                    var s12Service = new ContentIndexService(s12Db);
                    await s12Service.IndexFolderAsync(s12Root, null, CancellationToken.None);

                    // 1. FTS5 インデックス検索: "見積,請求 2026"
                    var indexedHits = await s12Service.SearchIndexedAsync(qOrComma, s12Root, CancellationToken.None);
                    if (indexedHits.Count != 2)
                        throw new Exception($"ADR 78 Indexed Search Failed: Expected 2 hits, got {indexedHits.Count}");
                    if (!indexedHits.Any(h => h.FullPath.EndsWith("見積書_2026.pdf")) || !indexedHits.Any(h => h.FullPath.EndsWith("請求書_2026.pdf")))
                        throw new Exception("ADR 78 Indexed Search Failed: Did not match expected files (見積書_2026 and 請求書_2026)");

                    // 2. DirectFolder 直接走査: "見積,請求 2026"
                    var engine = new SearchEngineService();
                    var directHits = await engine.SearchDirectFolderAsync(s12Root, qOrComma, null, null, CancellationToken.None);
                    if (directHits.Count != 2)
                        throw new Exception($"ADR 78 Direct Search Failed: Expected 2 hits, got {directHits.Count}");
                    if (!directHits.Any(h => h.FullPath.EndsWith("見積書_2026.pdf")) || !directHits.Any(h => h.FullPath.EndsWith("請求書_2026.pdf")))
                        throw new Exception("ADR 78 Direct Search Failed: Did not match expected files (見積書_2026 and 請求書_2026)");
                }
                finally
                {
                    try { Directory.Delete(s12Root, true); } catch { }
                }
            }

            // 13. 【ADR 79】安全契約（JIT権限）、content:修飾子の必須意味論、およびフィールド跨ぎAND積集合の検証
            {
                string s13Root = Path.Combine(Path.GetTempPath(), "FM_Reg_S13_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(s13Root);
                string s13Db = Path.Combine(s13Root, "ContentIndex.db");

                try
                {
                    // テストファイル準備
                    // f1: ファイル名に「契約書」、本文に「2026年度プロジェクト」
                    string f1 = Path.Combine(s13Root, "契約書_計画.txt");
                    File.WriteAllText(f1, "2026年度プロジェクトの重要計画書です。", Encoding.UTF8);

                    // f2: ファイル名「社外秘.txt」、本文「一般公開データ」
                    string f2 = Path.Combine(s13Root, "社外秘.txt");
                    File.WriteAllText(f2, "誰でも閲覧可能な一般公開データです。", Encoding.UTF8);

                    // f3: ファイル名「議事録.txt」、本文「社外秘の取扱について」
                    string f3 = Path.Combine(s13Root, "議事録.txt");
                    File.WriteAllText(f3, "社外秘の取扱について厳格に管理する。", Encoding.UTF8);

                    var s13Service = new ContentIndexService(s13Db);
                    await s13Service.IndexFolderAsync(s13Root, null, CancellationToken.None);

                    var engine = new SearchEngineService();

                    // --- 検証 A: content:修飾子の必須意味論 ---
                    // クエリ: content:社外秘
                    var qContentOnly = SearchQueryParser.Parse("content:社外秘");
                    
                    // 1. 先行表示コールバックが抑止されること
                    bool progressiveCalled = false;
                    var hitsContentIndexed = await s13Service.SearchIndexedAsync(
                        qContentOnly,
                        s13Root,
                        CancellationToken.None,
                        onNameHitsReady: hits => { progressiveCalled = true; });

                    if (progressiveCalled)
                        throw new Exception("ADR 79 Verification Failed: Progressive name hits should be suppressed when content: modifier is present.");

                    // 2. 本文に「社外秘」がある f3 のみがヒットし、ファイル名だけの f2 は除外されること
                    if (hitsContentIndexed.Count != 1 || !hitsContentIndexed[0].FullPath.EndsWith("議事録.txt"))
                        throw new Exception($"ADR 79 Indexed Content Modifier Failed: Expected 1 hit (議事録.txt), got {hitsContentIndexed.Count}");

                    var hitsContentDirect = await engine.SearchDirectFolderAsync(s13Root, qContentOnly, null, null, CancellationToken.None);
                    if (hitsContentDirect.Count != 1 || !hitsContentDirect[0].FullPath.EndsWith("議事録.txt"))
                        throw new Exception($"ADR 79 Direct Content Modifier Failed: Expected 1 hit (議事録.txt), got {hitsContentDirect.Count}");

                    // --- 検証 B: 複合クエリ「契約 content:社外秘」---
                    var qCombined = SearchQueryParser.Parse("契約 content:社外秘");
                    // f1（契約書だが社外秘なし）は除外、f3（社外秘だが契約なし）は除外 -> 0件
                    var hitsCombined = await s13Service.SearchIndexedAsync(qCombined, s13Root, CancellationToken.None);
                    if (hitsCombined.Count != 0)
                        throw new Exception($"ADR 79 Combined Query Failed: Expected 0 hits for '契約 content:社外秘', got {hitsCombined.Count}");

                    // --- 検証 C: フィールド跨ぎAND（ファイル名に契約書 ＋ 本文に2026）---
                    // クエリ: 契約書 2026 （本文も検索 ON）
                    var qCrossField = SearchQueryParser.Parse("契約書 2026");
                    qCrossField.SearchContentMode = true;

                    // 1. Indexed Search (FTS5 INTERSECT 積集合)
                    var hitsCrossIndexed = await s13Service.SearchIndexedAsync(qCrossField, s13Root, CancellationToken.None);
                    if (hitsCrossIndexed.Count != 1 || !hitsCrossIndexed[0].FullPath.EndsWith("契約書_計画.txt"))
                        throw new Exception($"ADR 79 Cross-Field Indexed Search Failed: Expected 1 hit (契約書_計画.txt), got {hitsCrossIndexed.Count}");
                    if (string.IsNullOrEmpty(hitsCrossIndexed[0].ContentSnippet) || hitsCrossIndexed[0].ContentSnippet?.Contains("2026") != true)
                        throw new Exception("ADR 79 Cross-Field Indexed Search Failed: Snippet for 2026 was not generated.");

                    // 2. Direct Search
                    var hitsCrossDirect = await engine.SearchDirectFolderAsync(s13Root, qCrossField, null, null, CancellationToken.None);
                    if (hitsCrossDirect.Count != 1 || !hitsCrossDirect[0].FullPath.EndsWith("契約書_計画.txt"))
                        throw new Exception($"ADR 79 Cross-Field Direct Search Failed: Expected 1 hit (契約書_計画.txt), got {hitsCrossDirect.Count}");

                    // 3. フィールド跨ぎANDで Direct Search と Indexed Search の結果が完全一致すること
                    if (hitsCrossIndexed[0].FullPath != hitsCrossDirect[0].FullPath)
                        throw new Exception("ADR 79 Cross-Field Parity Failed: Indexed and Direct search results do not match.");
                }
                finally
                {
                    try { Directory.Delete(s13Root, true); } catch { }
                }

                // =========================================================================================
                // セクション 14: ExactPhrase 引用符完全フレーズ検索の Indexed Search 統合検証 (ADR 81)
                // =========================================================================================
                string s14Root = Path.Combine(Path.GetTempPath(), "AstraSearch_Section14_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(s14Root);
                try
                {
                    // ファイル作成
                    // File 1: 名前に「契約 更新」を含む
                    string file1 = Path.Combine(s14Root, "2026_契約 更新_一覧.txt");
                    File.WriteAllText(file1, "通常のドキュメントです。");

                    // File 2: 順序が逆「更新 契約」
                    string file2 = Path.Combine(s14Root, "2026_更新 契約_一覧.txt");
                    File.WriteAllText(file2, "通常のドキュメントです。");

                    // File 3: 本文に「機密 保持」を含む
                    string file3 = Path.Combine(s14Root, "NDA_Document.txt");
                    File.WriteAllText(file3, "この文書は 機密 保持 の誓約を含みます。");

                    var s14Service = new ContentIndexService();
                    await s14Service.IndexFolderAsync(s14Root, null, CancellationToken.None);

                    // 検証 A: 引用符完全一致「"契約 更新"」で File 1 のみヒットし、File 2（順序逆）はヒットしないこと
                    var qExact1 = SearchQueryParser.Parse("\"契約 更新\"");
                    if (qExact1.ExactPhrases.Count != 1 || qExact1.ExactPhrases[0] != "契約 更新")
                        throw new Exception("ADR 81 ExactPhrase Parser Failed: '契約 更新' not parsed as ExactPhrase.");

                    var hitsExact1 = await s14Service.SearchIndexedAsync(qExact1, s14Root, CancellationToken.None);
                    if (hitsExact1.Count != 1 || !hitsExact1[0].FullPath.EndsWith("2026_契約 更新_一覧.txt"))
                        throw new Exception($"ADR 81 ExactPhrase Search Failed: Expected 1 hit (2026_契約 更新_一覧.txt), got {hitsExact1.Count}");

                    // 検証 B: 本文も検索ONで本文内の引用符完全一致「"機密 保持"」が正しくヒットすること
                    var qExact2 = SearchQueryParser.Parse("\"機密 保持\"");
                    qExact2.SearchContentMode = true;
                    var hitsExact2 = await s14Service.SearchIndexedAsync(qExact2, s14Root, CancellationToken.None);
                    if (hitsExact2.Count != 1 || !hitsExact2[0].FullPath.EndsWith("NDA_Document.txt"))
                        throw new Exception($"ADR 81 ExactPhrase Content Search Failed: Expected 1 hit (NDA_Document.txt), got {hitsExact2.Count}");

                    // 検証 C: 【ADR 82】Direct / ライブ走査における ExactPhrase Parity 検証
                    // Indexed だけでなく、SearchEngineService の直接走査でも本文のみに含まれる「"機密 保持"」が正しくヒットすること
                    var engineService = new SearchEngineService();
                    var hitsDirect = await engineService.SearchDirectFolderAsync(s14Root, qExact2, null, null, CancellationToken.None);
                    if (hitsDirect.Count != 1 || !hitsDirect[0].FullPath.EndsWith("NDA_Document.txt"))
                        throw new Exception($"ADR 82 ExactPhrase Direct Parity Failed: Expected 1 hit (NDA_Document.txt), got {hitsDirect.Count}");
                }
                finally
                {
                    try { Directory.Delete(s14Root, true); } catch { }
                }
            }

            // 15. 【ADR 83】「本文も検索ON ＋ フォルダも含めるON」における Indexed / In-Memory / Direct 3経路完全Parity検証
            {
                string s15Root = Path.Combine(Path.GetTempPath(), "FM_Reg_S15_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(s15Root);
                string s15Db = Path.Combine(s15Root, "ContentIndex.db");

                try
                {
                    // 1. テスト環境構築
                    // 📁 契約関連フォルダー (フォルダー名に「契約」を含む)
                    string folderPath = Path.Combine(s15Root, "契約関連フォルダー");
                    Directory.CreateDirectory(folderPath);

                    // 📁 契約関連フォルダー\通常メモ.txt (本文に「契約」を含まない)
                    string memoFile = Path.Combine(folderPath, "通常メモ.txt");
                    File.WriteAllText(memoFile, "これはプロジェクトの議事録メモです。");

                    // 請求書_2026.txt (本文に「契約」を含む)
                    string invoiceFile = Path.Combine(s15Root, "請求書_2026.txt");
                    File.WriteAllText(invoiceFile, "本請求書は契約合意に基づくものです。");

                    // 2. インデックス構築
                    var s15IndexService = new ContentIndexService(s15Db);
                    await s15IndexService.IndexFolderAsync(s15Root, null, CancellationToken.None);

                    // 3. インメモリツリー構築
                    var rootNode = new FileItemNode { Name = Path.GetFileName(s15Root), FullPath = s15Root, IsDirectory = true };
                    var folderNode = new FileItemNode { Name = "契約関連フォルダー", FullPath = folderPath, IsDirectory = true };
                    var memoNode = new FileItemNode { Name = "通常メモ.txt", FullPath = memoFile, IsDirectory = false, Size = 50 };
                    var invoiceNode = new FileItemNode { Name = "請求書_2026.txt", FullPath = invoiceFile, IsDirectory = false, Size = 60 };
                    folderNode.Children.Add(memoNode);
                    rootNode.Children.Add(folderNode);
                    rootNode.Children.Add(invoiceNode);
                    var memRoots = new List<FileItemNode> { rootNode };

                    // 4. クエリ: 「契約」, 本文も検索 ON, フォルダも含める ON
                    var qFolder = SearchQueryParser.Parse("契約");
                    qFolder.SearchContentMode = true;
                    qFolder.IncludeFolders = true;

                    // A. Route 1: Indexed Search
                    var hitsIndexed = await s15IndexService.SearchIndexedAsync(qFolder, s15Root, CancellationToken.None);
                    var indexedFolderHit = hitsIndexed.FirstOrDefault(h => h.IsDirectory);
                    if (indexedFolderHit == null || !indexedFolderHit.FullPath.EndsWith("契約関連フォルダー"))
                        throw new Exception($"ADR 83 Indexed Search failed to return folder match (Total: {hitsIndexed.Count})");

                    // B. Route 2: In-Memory Search
                    var engineService = new SearchEngineService();
                    var hitsMemory = await engineService.SearchInMemoryAsync(memRoots, qFolder, null, CancellationToken.None);
                    var memoryFolderHit = hitsMemory.FirstOrDefault(h => h.IsDirectory);
                    if (memoryFolderHit == null || !memoryFolderHit.FullPath.EndsWith("契約関連フォルダー"))
                        throw new Exception($"ADR 83 In-Memory Search failed to return folder match (Total: {hitsMemory.Count})");

                    // C. Route 3: Direct Search
                    var hitsDirect = await engineService.SearchDirectFolderAsync(s15Root, qFolder, null, null, CancellationToken.None);
                    var directFolderHit = hitsDirect.FirstOrDefault(h => h.IsDirectory);
                    if (directFolderHit == null || !directFolderHit.FullPath.EndsWith("契約関連フォルダー"))
                        throw new Exception($"ADR 83 Direct Search failed to return folder match (Total: {hitsDirect.Count})");

                    // D. 3経路の整合性確認（フォルダーHITがすべて一致すること）
                    if (indexedFolderHit.FullPath != memoryFolderHit.FullPath || indexedFolderHit.FullPath != directFolderHit.FullPath)
                        throw new Exception("ADR 83 Parity mismatch: Folder hit paths differ across Indexed, Memory, and Direct routes.");
                }
                finally
                {
                    try { Directory.Delete(s15Root, true); } catch { }
                }
            }

            // 16. ADR 81: Storage スキャンツリー直結同期（二重I/Oゼロ＆メタデータ即時完了）検証
            {
                string s16Root = Path.Combine(Path.GetTempPath(), "AstraSearch_Section16_" + Guid.NewGuid().ToString("N"));
                string s16Db = Path.Combine(s16Root, "Astra_Section16.db");
                Directory.CreateDirectory(s16Root);

                try
                {
                    // 1. テストファイル群を生成
                    string subDir = Path.Combine(s16Root, "財務諸表");
                    Directory.CreateDirectory(subDir);
                    string file1 = Path.Combine(subDir, "2026年度_予算計画書.xlsx");
                    string file2 = Path.Combine(subDir, "監査ログ_Q1.log");
                    string file3 = Path.Combine(s16Root, "全体方針.txt");
                    File.WriteAllText(file1, "dummy binary excel");
                    File.WriteAllText(file2, "log text");
                    File.WriteAllText(file3, "policy text");

                    // 2. DiskScanService でツリーをスキャン（Storage Scan シミュレーション）
                    var scanner = new AstraSize.Services.DiskScanService();
                    var (rootNode, _) = await scanner.ScanPathAsync(s16Root, null, CancellationToken.None);

                    if (rootNode == null)
                        throw new Exception("ADR 81 failed: DiskScanService returned null rootNode.");

                    // 3. SyncFromStorageScanTreeAsync を実行（ディスク再走査ゼロ・インメモリ同期）
                    var indexService = new ContentIndexService(s16Db);
                    var report = await indexService.SyncFromStorageScanTreeAsync(rootNode, indexContent: false);

                    if (!report.IsCompleted)
                        throw new Exception("ADR 81 failed: SyncFromStorageScanTreeAsync did not complete successfully.");

                    // 4. HasCompleteIndexForPath が true を返すこと（即時検索可能）
                    if (!indexService.HasCompleteIndexForPath(s16Root))
                        throw new Exception("ADR 81 failed: HasCompleteIndexForPath returned false after storage sync.");

                    // 5. ファイル名検索（日本語・部分一致）で即時ヒットすること
                    var qBudget = SearchQueryParser.Parse("予算計画書");
                    var hitsBudget = await indexService.SearchIndexedAsync(qBudget, s16Root, CancellationToken.None);
                    if (hitsBudget.Count == 0 || !hitsBudget.Any(h => h.Name.Contains("2026年度_予算計画書")))
                        throw new Exception($"ADR 81 failed: Budget plan not hit in indexed search (Hits: {hitsBudget.Count}).");

                    // 6. フォルダも含めた検索でサブフォルダがヒットすること
                    var qFolder = SearchQueryParser.Parse("財務諸表");
                    qFolder.IncludeFolders = true;
                    var hitsFolder = await indexService.SearchIndexedAsync(qFolder, s16Root, CancellationToken.None);
                    if (!hitsFolder.Any(h => h.IsDirectory && h.Name == "財務諸表"))
                        throw new Exception("ADR 81 failed: Subfolder '財務諸表' not hit in indexed search.");

                    // 7. 2回目呼び出し（差分なし）でスキップされること（ゼロI/O・冪等性）
                    var diffReport = await indexService.SyncFromStorageScanTreeAsync(rootNode, indexContent: false);
                    if (diffReport.NewlyIndexedCount != 0 || diffReport.AlreadyIndexed == 0)
                        throw new Exception($"ADR 81 failed: Idempotent resync expected NewlyIndexed=0, got {diffReport.NewlyIndexedCount} (AlreadyIndexed={diffReport.AlreadyIndexed})");
                }
                finally
                {
                    try { Directory.Delete(s16Root, true); } catch { }
                }
            }

            // 17. ADR 82: Aho-Corasick 多パターン同時照合＆Early Exit検証
            {
                var patterns = new[] { "契約書", "2026年度", "予算", "SecretCode_X" };
                var ac = new AhoCorasickSearcher(patterns, ignoreCase: true);

                if (ac.PatternCount != 4)
                    throw new Exception($"ADR 82 failed: PatternCount expected 4, got {ac.PatternCount}");

                // テスト本文（大文字小文字混在、日本語混在）
                string docText = "これは2026年度の事業計画です。秘密の識別子はsecretcode_xであり、予算に関する詳細が記載されています。";

                var matched = ac.FindMatchedIndices(docText);
                // "2026年度"(1), "SecretCode_X"(3), "予算"(2) がマッチするはず（"契約書"(0)はマッチしない）
                if (!matched.Contains(1) || !matched.Contains(2) || !matched.Contains(3))
                    throw new Exception("ADR 82 failed: Expected matched patterns 1, 2, 3 not found.");
                if (matched.Contains(0))
                    throw new Exception("ADR 82 failed: False positive on unmatched pattern '契約書'.");

                // ContainsAny の即時判定
                if (!ac.ContainsAny("単なる予算のメモ"))
                    throw new Exception("ADR 82 failed: ContainsAny returned false for matching text.");
                if (ac.ContainsAny("全く関係のない文章です"))
                    throw new Exception("ADR 82 failed: ContainsAny returned true for non-matching text.");

                // 全パターン一致時の早期終了
                string fullDoc = "2026年度の契約書と予算およびSecretCode_Xの全てが含まれています。";
                var fullMatched = ac.FindMatchedIndices(fullDoc);
                if (fullMatched.Count != 4)
                    throw new Exception($"ADR 82 failed: Full doc expected 4 matches, got {fullMatched.Count}");
            }

            // 18. ADR 83: Aho-Corasick スニペット抽出 ＆ Lazy Background Indexer（Small-File First）検証
            {
                // A. Aho-Corasick スニペット抽出テスト
                var ac = new AhoCorasickSearcher(new[] { "秘密計画", "2026" });
                string longText = "前文前文前文前文前文前文\r\nここに2026の秘密計画が記載されています。\n後文後文後文後文後文";
                string snippet = ac.ExtractSnippet(longText, snippetLength: 40);

                if (!snippet.Contains("【") || !snippet.Contains("】"))
                    throw new Exception("ADR 83 failed: Snippet does not contain highlight delimiters.");
                if (snippet.Contains("\r") || snippet.Contains("\n"))
                    throw new Exception("ADR 83 failed: Snippet contains unnormalized newline characters.");

                // B. Lazy Background Indexer & Opportunistic Cache テスト
                string testRoot = Path.Combine(Path.GetTempPath(), "FolderMorpher_LazyIndexTest_" + Guid.NewGuid().ToString("N"));
                string dbPath = Path.Combine(testRoot, "TestLazy.db");
                Directory.CreateDirectory(testRoot);

                try
                {
                    // テスト用ファイル作成（大・小・非本文）
                    string smallFile = Path.Combine(testRoot, "small.txt");
                    string largeFile = Path.Combine(testRoot, "large.txt");
                    string binFile = Path.Combine(testRoot, "binary.dat");

                    File.WriteAllText(smallFile, "これはSmallファイルの中身です。キーワードAlphaが含まれています。");
                    File.WriteAllText(largeFile, new string('A', 5000) + " キーワードBetaが含まれています。");
                    File.WriteAllBytes(binFile, new byte[] { 0x00, 0x01, 0x02 });

                    var service = new ContentIndexService(dbPath);

                    // 1. スキャンツリー（メタデータのみ）直結同期
                    var rootNode = new AstraSize.Models.FileItemNode
                    {
                        FullPath = testRoot,
                        Name = Path.GetFileName(testRoot),
                        IsDirectory = true
                    };
                    rootNode.Children.Add(new AstraSize.Models.FileItemNode
                    {
                        FullPath = smallFile,
                        Name = "small.txt",
                        Size = 100,
                        LastModified = DateTime.UtcNow,
                        Parent = rootNode
                    });
                    rootNode.Children.Add(new AstraSize.Models.FileItemNode
                    {
                        FullPath = largeFile,
                        Name = "large.txt",
                        Size = 6000,
                        LastModified = DateTime.UtcNow,
                        Parent = rootNode
                    });
                    rootNode.Children.Add(new AstraSize.Models.FileItemNode
                    {
                        FullPath = binFile,
                        Name = "binary.dat",
                        Size = 3,
                        LastModified = DateTime.UtcNow,
                        Parent = rootNode
                    });

                    await service.SyncFromStorageScanTreeAsync(rootNode, indexContent: false);

                    // 同期直後: メタデータのみ（Status = 0）なので本文検索ではまだヒットしない
                    var qPre = SearchQueryParser.Parse("content:Alpha");
                    var preHits = await service.SearchIndexedAsync(qPre, testRoot);
                    if (preHits.Count != 0)
                        throw new Exception("ADR 83 failed: Expected 0 content hits before lazy processing.");

                    // 2. ProcessPendingContentIndexAsync（Small-File First）で1件だけ処理
                    int processed1 = await service.ProcessPendingContentIndexAsync(testRoot, maxCount: 1);
                    if (processed1 != 1)
                        throw new Exception($"ADR 83 failed: Expected 1 processed file, got {processed1}");

                    // SmallFile（small.txt）が先にインデックス化され、Alphaがヒットするはず！
                    var postHits1 = await service.SearchIndexedAsync(qPre, testRoot);
                    if (postHits1.Count != 1 || !postHits1[0].FullPath.EndsWith("small.txt", StringComparison.OrdinalIgnoreCase))
                        throw new Exception("ADR 83 failed: small.txt was not indexed first by Small-File First ordering.");

                    // largeFile（Beta）はまだ処理されていないので0件のはず
                    var qBeta = SearchQueryParser.Parse("content:Beta");
                    var betaHitsPre = await service.SearchIndexedAsync(qBeta, testRoot);
                    if (betaHitsPre.Count != 0)
                        throw new Exception("ADR 83 failed: large.txt should not be indexed yet.");

                    // 3. 残りの未インデックスを処理
                    int processed2 = await service.ProcessPendingContentIndexAsync(testRoot, maxCount: 10);
                    if (processed2 < 1)
                        throw new Exception($"ADR 83 failed: Expected remaining file to be processed, got {processed2}");

                    // 今度は largeFile（Beta）もヒットする
                    var betaHitsPost = await service.SearchIndexedAsync(qBeta, testRoot);
                    if (betaHitsPost.Count != 1)
                        throw new Exception("ADR 83 failed: large.txt was not indexed in second pass.");

                    // 4. UpsertFileContentDirectlyAsync（便乗キャッシュ）のテスト
                    string cachedFile = Path.Combine(testRoot, "cached.txt");
                    string cachedContent = "ライブ検索で読んだ本文: SecretGamma_999";
                    File.WriteAllText(cachedFile, cachedContent, Encoding.UTF8);
                    // メタデータを手動追加して便乗投入をテスト
                    rootNode.Children.Add(new AstraSize.Models.FileItemNode
                    {
                        FullPath = cachedFile,
                        Name = "cached.txt",
                        Size = (long)Encoding.UTF8.GetByteCount(cachedContent),
                        LastModified = DateTime.UtcNow,
                        Parent = rootNode
                    });
                    await service.SyncFromStorageScanTreeAsync(rootNode, indexContent: false);

                    await service.UpsertFileContentDirectlyAsync(cachedFile, cachedContent);
                    var qGamma = SearchQueryParser.Parse("content:SecretGamma_999");
                    var gammaHits = await service.SearchIndexedAsync(qGamma, testRoot);
                    if (gammaHits.Count != 1 || string.IsNullOrEmpty(gammaHits[0].ContentSnippet))
                        throw new Exception("ADR 83 failed: Opportunistic cache did not index content with snippet.");

                    // 5. 【ADR 85: Mode C (contentless FTS5 ＆ Progressive Verify ＆ False Positive 排除)】
                    string adr85Dir = Path.Combine(testRoot, "ADR85_Verification");
                    Directory.CreateDirectory(adr85Dir);

                    // A. Sol指摘の False Positive 排除テスト:
                    // ABCD 検索に対し、ABC...BCD が離れている偽陽性ファイルと、連続 ABCD の真のファイルを生成
                    string falseCandFile = Path.Combine(adr85Dir, "FalseCandidate.txt");
                    File.WriteAllText(falseCandFile, "ABC 前半の文章です。ここには何の関係もない文章が挟まります。後半に BCD が現れます。", Encoding.UTF8);

                    string trueMatchFile = Path.Combine(adr85Dir, "TrueMatch.txt");
                    File.WriteAllText(trueMatchFile, "ここには連続した ABCD というキーワードが確実に含まれています。", Encoding.UTF8);

                    string multiWordFile = Path.Combine(adr85Dir, "MultiTrigramDoc.txt");
                    File.WriteAllText(multiWordFile, "極秘監査計画の全貌がここに記載されています。", Encoding.UTF8);

                    // インデックス同期（本文付き）
                    await service.IndexFolderAsync(adr85Dir, null, CancellationToken.None);

                    // 検証1: "ABCD" で検索（3文字分解 AND: "ABC" AND "BCD"）
                    // FTS5 MATCH 段階では両方が候補になるが、Progressive Verify により TrueMatch のみ 1 件確定すること
                    var qAbcd = SearchQueryParser.Parse("ABCD");
                    qAbcd.SearchContentMode = true;
                    var abcdHits = await service.SearchIndexedAsync(qAbcd, adr85Dir, CancellationToken.None);

                    if (abcdHits.Count != 1 || !abcdHits[0].FullPath.EndsWith("TrueMatch.txt"))
                        throw new Exception($"ADR 85 failed: Expected 1 hit (TrueMatch.txt), but got {abcdHits.Count} hits (False positive was not eliminated).");
                    if (string.IsNullOrEmpty(abcdHits[0].ContentSnippet) || !abcdHits[0].ContentSnippet!.Contains("ABCD"))
                        throw new Exception("ADR 85 failed: Snippet for TrueMatch.txt did not contain 'ABCD'.");

                    // 検証2: 4文字以上の日本語 3文字 trigram AND 結合（"極秘監査計画" -> "極秘監" AND "秘監査" AND "監査計" AND "査計画"）
                    var qMulti = SearchQueryParser.Parse("content:極秘監査計画");
                    var multiHits = await service.SearchIndexedAsync(qMulti, adr85Dir, CancellationToken.None);
                    if (multiHits.Count != 1 || !multiHits[0].FullPath.EndsWith("MultiTrigramDoc.txt"))
                        throw new Exception($"ADR 85 failed: Sliding-window trigram AND search failed for '極秘監査計画', got {multiHits.Count} hits.");

                    // 検証3: Mode C contentless テーブルでの DELETE 動作（contentless_delete=1 の正常性）
                    File.Delete(falseCandFile);
                    await service.IndexFolderAsync(adr85Dir, null, CancellationToken.None);
                    // 削除後もエラーなく検索が動作することを確認
                    var postDeleteHits = await service.SearchIndexedAsync(qAbcd, adr85Dir, CancellationToken.None);
                    if (postDeleteHits.Count != 1 || !postDeleteHits[0].FullPath.EndsWith("TrueMatch.txt"))
                        throw new Exception("ADR 85 failed: Contentless delete caused unexpected search corruption.");

                    // 6. 【ADR 86: Agent Ransack 流高速化 ＆ 2文字日本語検索漏れ根絶 ＆ 500件上限撤去】
                    string adr86Dir = Path.Combine(testRoot, "ADR86_Verification");
                    Directory.CreateDirectory(adr86Dir);

                    // A. StripXmlTagsFast（正規表現全廃・高速XMLタグ除去）の検証
                    string testXml = "<w:document><w:body><w:p><w:r><w:t>機密プロジェクトの仕様書</w:t></w:r></w:p></w:body></w:document>";
                    string strippedText = ContentExtractionService.StripXmlTagsFast(testXml);
                    if (strippedText != "機密プロジェクトの仕様書")
                        throw new Exception($"ADR 86 failed: StripXmlTagsFast failed, got '{strippedText}'");

                    // B. 500件上限撤去 ＆ 2文字日本語検索漏れの検証:
                    // 550ファイルのテキストを生成し、520番目のファイルにのみ2文字キーワード「設計」を含める
                    for (int i = 1; i <= 550; i++)
                    {
                        string fPath = Path.Combine(adr86Dir, $"doc_{i:D4}.txt");
                        if (i == 520)
                        {
                            File.WriteAllText(fPath, "これは第520文書であり、アーキテクチャの設計方針が詳細に記載されています。", Encoding.UTF8);
                        }
                        else
                        {
                            File.WriteAllText(fPath, $"これは第{i}文書の本文です。一般的な業務連絡の内容です。", Encoding.UTF8);
                        }
                    }

                    // インデックス化
                    await service.IndexFolderAsync(adr86Dir, null, CancellationToken.None);

                    // 2文字の日本語「設計」（3文字未満の trigram なので全件走査フォールバックへ）で検索
                    // LIMIT 500 が残っていると 520番目のファイルが漏れて 0 件になるが、上限撤去により 1 件確実にヒットする
                    var q2Char = SearchQueryParser.Parse("content:設計");
                    var hits2Char = await service.SearchIndexedAsync(q2Char, adr86Dir, CancellationToken.None);

                    if (hits2Char.Count != 1 || !hits2Char[0].FullPath.EndsWith("doc_0520.txt"))
                        throw new Exception($"ADR 86 failed: 2-character Japanese search over 500 files failed. Expected 1 hit (doc_0520.txt), got {hits2Char.Count} hits.");
                    if (string.IsNullOrEmpty(hits2Char[0].ContentSnippet) || !hits2Char[0].ContentSnippet!.Contains("設計"))
                        throw new Exception("ADR 86 failed: Content snippet for 2-character hit did not contain '設計'.");

                    // C. Agent Ransack 流 Producer-Consumer Channel パイプライン（SearchDirectFolderAsync）の直接走査検証
                    var engineService = new SearchEngineService();
                    var directQuery = SearchQueryParser.Parse("content:設計");
                    directQuery.SearchContentMode = true;
                    var directHits = await engineService.SearchDirectFolderAsync(adr86Dir, directQuery, null, null, CancellationToken.None);

                    if (directHits.Count != 1 || !directHits[0].FullPath.EndsWith("doc_0520.txt"))
                        throw new Exception($"ADR 86 failed: Direct Producer-Consumer search failed. Expected 1 hit (doc_0520.txt), got {directHits.Count} hits.");
                    if (string.IsNullOrEmpty(directHits[0].ContentSnippet) || !directHits[0].ContentSnippet!.Contains("設計"))
                        throw new Exception("ADR 86 failed: Direct Producer-Consumer snippet did not contain '設計'.");
                }
                finally
                {
                    try { Directory.Delete(testRoot, recursive: true); } catch { }
                }
            }
        }
    }
}

