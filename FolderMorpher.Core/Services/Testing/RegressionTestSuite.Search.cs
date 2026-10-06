using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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
                    File.WriteAllText(Path.Combine(subFolder, "Nested.txt"), "nested cache metadata", Encoding.UTF8);

                    // 6. 名前 OR 本文（複数キーワード）テスト用ファイル
                    string filePartial = Path.Combine(tempDir, "予算_計画書.txt");
                    File.WriteAllText(filePartial, "2026年の事業方針について記載する。", Encoding.UTF8);

                    // 検索テスト (content:"最高機密プロジェクトX")
                    var qContent = SearchQueryParser.Parse("content:\"最高機密プロジェクトX\"");
                    var batchResults = new List<SearchResultItem>();
                    var batchYield = new Progress<IReadOnlyList<SearchResultItem>>(items => batchResults.AddRange(items));

                    var results = await searchEngine.SearchDirectFolderAsync(tempDir, qContent, batchYield, null, CancellationToken.None);
                    var capturedRoot = await SqliteTreeCacheService.Instance.LoadBranchAsync(tempDir, tempDir);
                    if (capturedRoot == null || capturedRoot.FileCount != 7 || capturedRoot.FolderCount != 1 ||
                        !capturedRoot.Children.Any(node => node.Name == "SubFolder_ProjectX" && node.IsDirectory))
                        throw new Exception("Cold content search did not publish its complete traversal to TreeCache.");
                    var nestedBranch = await SqliteTreeCacheService.Instance.LoadBranchAsync(tempDir, subFolder);
                    if (nestedBranch == null || nestedBranch.FileCount != 1 ||
                        !nestedBranch.Children.Any(node => node.Name == "Nested.txt" && !node.IsDirectory))
                        throw new Exception("Cold search capture lost a nested file or its parent aggregate.");

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
                }
                finally
                {
                    SafeDeleteDirectory(tempDir);
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
                    SafeDeleteDirectory(tempDir);
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
            }

            // -------------------------------------------------------------
            // 【ADR 86: Agent Ransack 流高速化 ＆ 2文字日本語検索漏れ根絶 ＆ 500件上限撤去】
            // -------------------------------------------------------------
            {
                string adr86Dir = Path.Combine(Path.GetTempPath(), "ADR86_Verification_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(adr86Dir);
                try
                {
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
                    try { Directory.Delete(adr86Dir, recursive: true); } catch { }
                }
            }

            // 19. ADR 90: SharedIoGovernor & Large File Distributed Probe 検証
            {
                // A. SharedIoGovernor ＆ PathCanonicalizer.GetVolumeOrShareRoot ＆ 学習分離 (ADR 91) 検証
                SharedIoGovernor.Reset();
                var g1 = SharedIoGovernor.GetGovernor(@"C:\Users\Alpha\file1.txt");
                var g2 = SharedIoGovernor.GetGovernor(@"c:\Windows\System32");
                if (!ReferenceEquals(g1, g2))
                    throw new Exception("ADR 90/91 failed: SharedIoGovernor did not share SharedVolumeGovernor for same local volume (C:).");

                // 【ADR 91】列挙用と本文読込用のコントローラーが分離され、学習汚染が発生しないことの検証
                if (ReferenceEquals(g1.EnumerationController, g1.ContentController))
                    throw new Exception("ADR 91 failed: EnumerationController and ContentController must be separate instances.");
                if (g1.EnumerationController.MaxConcurrencyLimit != 4 || g1.ContentController.MaxConcurrencyLimit != 8)
                    throw new Exception("Shared I/O controller limits no longer match the enumeration/content budgets.");

                var uncG1 = SharedIoGovernor.GetGovernor(@"\\file-server01\ShareA\SubDir1\Doc.txt");
                var uncG2 = SharedIoGovernor.GetGovernor(@"\\FILE-SERVER01\shareA\SubDir2\Other.xlsx");
                if (!ReferenceEquals(uncG1, uncG2))
                    throw new Exception("ADR 90/91 failed: SharedIoGovernor did not share governor for same UNC share (case-insensitive).");
                if (uncG1.EnumerationController.MaxConcurrencyLimit != 2 ||
                    uncG1.ContentController.MaxConcurrencyLimit != 12)
                    throw new Exception("UNC I/O governor must retain the shared-server limits.");

                var uncG3 = SharedIoGovernor.GetGovernor(@"\\file-server01\ShareB\Data.csv");
                if (ReferenceEquals(uncG1, uncG3))
                    throw new Exception("ADR 90/91 failed: SharedIoGovernor incorrectly shared governor across different UNC shares.");

                if (!SharedIoGovernor.ActiveRoots.Contains("C:") || !SharedIoGovernor.ActiveRoots.Contains(@"\\file-server01\ShareA"))
                    throw new Exception("ADR 90/91 failed: SharedIoGovernor.ActiveRoots did not contain expected roots.");

                // B. Large File Pipeline ＆ 分散Probe（先頭・末尾・中間ブロック高速照合）検証
                string probeDir = Path.Combine(Path.GetTempPath(), "FolderMorpher_ProbeTest_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(probeDir);
                try
                {
                    string largeFilePath = Path.Combine(probeDir, "LargeSparseTest.txt");
                    // 55MB の巨大ファイルをシミュレート（末尾付近にマーカーを書き込む）
                    long targetSize = 55L * 1024 * 1024;
                    using (var fs = new FileStream(largeFilePath, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        fs.SetLength(targetSize);
                        // 先頭 4096 バイトを UTF-8 テキストで満たし、UTF-8 判別を確実にする（スパースファイルの0埋めによる誤判定防止）
                        byte[] header = Encoding.UTF8.GetBytes(new string('=', 4096) + "\r\n");
                        fs.Write(header, 0, header.Length);

                        // 50% 地点にマーカーを書き込む
                        fs.Seek(targetSize / 2, SeekOrigin.Begin);
                        byte[] marker = Encoding.UTF8.GetBytes("Special_Probe_Secret_Marker_2026");
                        fs.Write(marker, 0, marker.Length);

                        // Probe窓から外れた位置は後段の全文走査で必ず拾う。
                        fs.Seek(targetSize * 3 / 8, SeekOrigin.Begin);
                        byte[] deferredMarker = Encoding.UTF8.GetBytes("Only_Deferred_Full_Search_2026");
                        fs.Write(deferredMarker, 0, deferredMarker.Length);
                    }

                    var groupsHit = new List<List<string>> { new List<string> { "Special_Probe_Secret_Marker_2026" } };
                    string? snippetHit = await ContentExtractionService.ProbeLargeFileContentAsync(largeFilePath, targetSize, groupsHit, CancellationToken.None);
                    if (string.IsNullOrEmpty(snippetHit) || !snippetHit.Contains("Special_Probe_Secret_Marker_2026"))
                        throw new Exception("ADR 90 failed: ProbeLargeFileContentAsync failed to detect keyword at 50% probe point.");

                    var groupsMiss = new List<List<string>> { new List<string> { "NonExistentProbePattern_XYZ" } };
                    string? snippetMiss = await ContentExtractionService.ProbeLargeFileContentAsync(largeFilePath, targetSize, groupsMiss, CancellationToken.None);
                    if (!string.IsNullOrEmpty(snippetMiss))
                        throw new Exception("ADR 90 failed: ProbeLargeFileContentAsync returned snippet for non-existent keyword.");

                    var deferredQuery = SearchQueryParser.Parse("content:Only_Deferred_Full_Search_2026");
                    var directDeferred = await new SearchEngineService().SearchDirectFolderAsync(
                        probeDir, deferredQuery, null, null, CancellationToken.None);
                    if (directDeferred.Count != 1 || directDeferred[0].FullPath != largeFilePath)
                        throw new Exception("Deferred direct search missed a match outside all probe windows.");

                    var cachedEntry = new TreeCacheSearchEntry(largeFilePath, Path.GetFileName(largeFilePath),
                        targetSize, false, File.GetLastWriteTime(largeFilePath), File.GetCreationTime(largeFilePath));
                    var cachedDeferred = await new SearchEngineService().SearchCachedEntriesAsync(
                        new[] { cachedEntry }, deferredQuery, null, CancellationToken.None);
                    if (cachedDeferred.Count != 1 || cachedDeferred[0].FullPath != largeFilePath)
                        throw new Exception("Deferred cached search missed a match outside all probe windows.");
                }
                finally
                {
                    try { Directory.Delete(probeDir, recursive: true); } catch { }
                }
            }

            // 20. 【ADR 93】TreeCache 枝刈り差分走査 ＆ 親子局所性（LIFO）＆ 単一キーワード高速パス検証
            {
                // A. TreeCachePruningIndex の単体検証
                DateTime now = DateTime.UtcNow;
                var rootNode = new AstraSize.Models.FileItemNode
                {
                    FullPath = @"C:\TestShare\Root",
                    Name = "Root",
                    IsDirectory = true,
                    LastModified = now
                };

                var subNode1 = new AstraSize.Models.FileItemNode
                {
                    FullPath = @"C:\TestShare\Root\UnchangedFolder",
                    Name = "UnchangedFolder",
                    IsDirectory = true,
                    LastModified = now.AddHours(-2)
                };
                var file1 = new AstraSize.Models.FileItemNode
                {
                    FullPath = @"C:\TestShare\Root\UnchangedFolder\Doc1.txt",
                    Name = "Doc1.txt",
                    IsDirectory = false,
                    Size = 1024,
                    LastModified = now.AddHours(-2)
                };
                subNode1.Children.Add(file1);
                rootNode.Children.Add(subNode1);

                var pruningIndex = new FolderMorpher.Services.TreeCachePruningIndex(rootNode)
                {
                    EnableFolderTimestampPruning = true
                };

                // 一致するフォルダー: true が返り、配下ファイルが展開されること
                bool prunedSuccess = pruningIndex.TryGetPrunedEntries(@"C:\TestShare\Root\UnchangedFolder", now.AddHours(-2), includeDirectories: false, out var prunedEntries);
                if (!prunedSuccess || prunedEntries == null || prunedEntries.Count != 1 || prunedEntries[0].FullPath != file1.FullPath)
                    throw new Exception("ADR 93 failed: TreeCachePruningIndex failed to prune matching folder.");

                // 日時が異なるフォルダー: false が返ること（変更検知）
                bool changedPruned = pruningIndex.TryGetPrunedEntries(@"C:\TestShare\Root\UnchangedFolder", now.AddMinutes(-5), includeDirectories: false, out _);
                if (changedPruned)
                    throw new Exception("ADR 93 failed: TreeCachePruningIndex falsely pruned modified folder.");

                // B. 実ディレクトリを用いた SafeFileEnumerator 差分枝刈り走査の Parity 検証
                string testRoot = Path.Combine(Path.GetTempPath(), "FolderMorpher_PruningTest_" + Guid.NewGuid().ToString("N"));
                string unchangedDir = Path.Combine(testRoot, "UnchangedDir");
                string modifiedDir = Path.Combine(testRoot, "ModifiedDir");

                Directory.CreateDirectory(unchangedDir);
                Directory.CreateDirectory(modifiedDir);

                string f1 = Path.Combine(unchangedDir, "doc_cached.txt");
                string f2 = Path.Combine(modifiedDir, "doc_live.txt");
                File.WriteAllText(f1, "Cached file content");
                File.WriteAllText(f2, "Live file content");

                try
                {
                    // キャッシュツリー構築
                    var cacheRoot = new AstraSize.Models.FileItemNode
                    {
                        FullPath = testRoot,
                        Name = Path.GetFileName(testRoot),
                        IsDirectory = true,
                        LastModified = Directory.GetLastWriteTimeUtc(testRoot)
                    };
                    var cacheUnchanged = new AstraSize.Models.FileItemNode
                    {
                        FullPath = unchangedDir,
                        Name = "UnchangedDir",
                        IsDirectory = true,
                        LastModified = Directory.GetLastWriteTimeUtc(unchangedDir)
                    };
                    cacheUnchanged.Children.Add(new AstraSize.Models.FileItemNode
                    {
                        FullPath = f1,
                        Name = "doc_cached.txt",
                        IsDirectory = false,
                        Size = 19,
                        LastModified = File.GetLastWriteTimeUtc(f1)
                    });
                    cacheRoot.Children.Add(cacheUnchanged);

                    // modifiedDir はキャッシュには古めの日時を記録しておく（実ディスクとの差異を作る）
                    var cacheModified = new AstraSize.Models.FileItemNode
                    {
                        FullPath = modifiedDir,
                        Name = "ModifiedDir",
                        IsDirectory = true,
                        LastModified = DateTime.UtcNow.AddDays(-1)
                    };
                    cacheRoot.Children.Add(cacheModified);

                    var pIndex = new FolderMorpher.Services.TreeCachePruningIndex(cacheRoot)
                    {
                        EnableFolderTimestampPruning = true
                    };

                    // SafeFileEnumerator で走査実行
                    var entries = new List<FolderMorpher.Services.ScannedFileEntry>();
                    await FolderMorpher.Services.SafeFileEnumerator.EnumerateFileEntriesParallelAsync(
                        testRoot,
                        onEntryFound: entry => { lock (entries) { entries.Add(entry); } },
                        pruningIndex: pIndex);

                    // unchangedDir 内の doc_cached.txt と modifiedDir 内の doc_live.txt の双方が漏れなく取得できていること
                    if (!entries.Any(e => e.FullPath.Equals(f1, StringComparison.OrdinalIgnoreCase)))
                        throw new Exception("ADR 93 failed: SafeFileEnumerator missed pruned cache file.");
                    if (!entries.Any(e => e.FullPath.Equals(f2, StringComparison.OrdinalIgnoreCase)))
                        throw new Exception("ADR 93 failed: SafeFileEnumerator missed live enumerated file.");

                    // C. ContentExtractionService 単一キーワード高速パス（AVX2 / OrdinalIgnoreCase）の検証
                    string sampleDoc = "これは特許出願用の極秘技術文書です。識別コードはAlphaOmega2026です。";
                    var singleGroup = new List<List<string>> { new List<string> { "AlphaOmega2026" } };
                    string? singleSnippet = ContentExtractionService.SearchTextWithSnippet(sampleDoc, singleGroup);
                    if (string.IsNullOrEmpty(singleSnippet) || !singleSnippet.Contains("AlphaOmega2026"))
                        throw new Exception("ADR 93 failed: ContentExtractionService single-keyword fast path failed to match or generate snippet.");

                    var nonMatchGroup = new List<List<string>> { new List<string> { "NonExistentKey999" } };
                    string? nonMatchSnippet = ContentExtractionService.SearchTextWithSnippet(sampleDoc, nonMatchGroup);
                    if (!string.IsNullOrEmpty(nonMatchSnippet))
                        throw new Exception("ADR 93 failed: ContentExtractionService single-keyword fast path false positive.");
                }
                finally
                {
                    try { Directory.Delete(testRoot, true); } catch { }
                }
            }

            // 21. 【ADR 94】Server Search Accelerator ＆ Windows Search Provider (WSP/OLE DB) 検証
            {
                // A. WindowsSearchProvider.BuildSearchSql の検証（UNC & Local SQL構文生成）
                var qContent = SearchQueryParser.Parse("契約書 2026 ext:pdf");
                qContent.SearchContentMode = true;

                // 1. UNC パスの場合（FROM "server".SystemIndex & SCOPE URL）
                string uncPath = @"\\file-server01\SharedDocs\Legal";
                string? uncSql = FolderMorpher.Services.ServerSearch.WindowsSearchProvider.BuildSearchSql(uncPath, qContent);

                if (string.IsNullOrEmpty(uncSql))
                    throw new Exception("ADR 94 failed: WindowsSearchProvider returned null SQL for UNC path.");
                if (!uncSql.Contains("FROM \"file-server01\".SystemIndex"))
                    throw new Exception("ADR 94 failed: WindowsSearchProvider SQL missing remote server SystemIndex FROM clause.");
                if (!uncSql.Contains("SCOPE = 'file://file-server01/SharedDocs/Legal'"))
                    throw new Exception("ADR 94 failed: WindowsSearchProvider SQL missing correct SCOPE URL.");
                if (!uncSql.Contains("System.FileExtension = '.pdf'"))
                    throw new Exception("ADR 94 failed: WindowsSearchProvider SQL missing file extension filter.");
                if (!uncSql.Contains("CONTAINS(System.Search.Contents"))
                    throw new Exception("ADR 94 failed: WindowsSearchProvider SQL missing content CONTAINS clause.");

                // 2. ローカルパスの場合（FROM SystemIndex）
                string localPath = @"C:\Data\Reports";
                string? localSql = FolderMorpher.Services.ServerSearch.WindowsSearchProvider.BuildSearchSql(localPath, qContent);
                if (string.IsNullOrEmpty(localSql) || !localSql.Contains("FROM SystemIndex") || localSql.Contains("\"C:\".SystemIndex"))
                    throw new Exception("ADR 94 failed: WindowsSearchProvider local SQL generated incorrect FROM clause.");

                // B. CanHandleAsync の検証（CI環境/ローカル環境の双方で安全判定）
                var provider = new FolderMorpher.Services.ServerSearch.WindowsSearchProvider();
                bool isInstalled = FolderMorpher.Services.ServerSearch.WindowsSearchProvider.IsProviderInstalled();
                if (isInstalled)
                {
                    if (!await provider.CanHandleAsync(@"\\server\share\dir", CancellationToken.None))
                        throw new Exception("ADR 94 failed: CanHandleAsync should return true for UNC path when provider is installed.");
                    if (!await provider.CanHandleAsync(@"C:\Folder", CancellationToken.None))
                        throw new Exception("ADR 94 failed: CanHandleAsync should return true for local drive path when provider is installed.");
                }
                else
                {
                    if (await provider.CanHandleAsync(@"\\server\share\dir", CancellationToken.None))
                        throw new Exception("ADR 94 failed: CanHandleAsync should return false when Search.CollatorDSO is not installed.");
                }

                if (await provider.CanHandleAsync("", CancellationToken.None))
                    throw new Exception("ADR 94 failed: CanHandleAsync should return false for empty path.");

                // C. 未接続・存在しないUNCサーバーへのクエリでクラッシュせず null フォールバックすることの検証
                var qDummy = SearchQueryParser.Parse("test");
                var dummyResult = await provider.QueryCandidatesAsync(@"\\non-existent-server-9999\fake-share", qDummy, CancellationToken.None);
                if (dummyResult != null)
                    throw new Exception("ADR 94 failed: QueryCandidatesAsync should return null for non-existent server to trigger fallback.");
                // 失敗したOLE DB接続のCOM最終化でプロセスが落ちないことも確認する。
                GC.Collect();
                GC.WaitForPendingFinalizers();

                // D. ServerSearchAccelerator コーディネーター＆モックプロバイダー登録動作検証
                var accelerator = new FolderMorpher.Services.ServerSearch.ServerSearchAccelerator();
                // 初期状態で WindowsSearchProvider が登録されていること
                if (!accelerator.Providers.Any(p => p is FolderMorpher.Services.ServerSearch.WindowsSearchProvider))
                    throw new Exception("ADR 94 failed: ServerSearchAccelerator should contain WindowsSearchProvider by default.");

                // モックプロバイダー（特定パスで候補を返す）の登録と取得検証
                var mockCandidates = new List<string> { @"\\server\share\doc1.pdf", @"\\server\share\doc2.pdf" };
                var mockProvider = new MockSearchProvider(@"\\mock-server\share", mockCandidates);
                accelerator.RegisterProvider(mockProvider);

                var acceleratedCandidates = await accelerator.TryAccelerateAsync(@"\\mock-server\share\sub", qDummy, CancellationToken.None);
                if (acceleratedCandidates == null || acceleratedCandidates.Count != 2 || acceleratedCandidates[0] != mockCandidates[0])
                    throw new Exception("ADR 94 failed: ServerSearchAccelerator failed to return candidates from registered provider.");
            }

            // -------------------------------------------------------------
            // 22. ADR 95: Astra提唱 徹底的Live高速化 ＆ 書式分割保護 ＆ バッファ直接走査
            // -------------------------------------------------------------
            {
                // A. 書式分割Word/Excel対応（Format-Split Boundary Protection）
                string splitWordXml = "<w:p><w:r><w:t>秘密</w:t></w:r><w:r><w:t>保持</w:t></w:r><w:r><w:t>契約書</w:t></w:r></w:p><w:p><w:r><w:t>第1条</w:t></w:r></w:p>";
                string cleaned = ContentExtractionService.StripXmlTagsFast(splitWordXml);
                if (!cleaned.Contains("秘密保持契約書"))
                    throw new Exception($"ADR 95 failed: StripXmlTagsFast failed to preserve format-split text. Got: '{cleaned}'");
                if (!cleaned.Contains("契約書 第1条"))
                    throw new Exception($"ADR 95 failed: StripXmlTagsFast should insert space at block boundaries (<w:p>). Got: '{cleaned}'");

                // B. バッファ境界またぎ直接走査（Sliding Buffer Overlap）
                string tempDir = Path.Combine(Path.GetTempPath(), "fm_test_adr95_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    string boundaryFile = Path.Combine(tempDir, "boundary_test.txt");
                    // 65536 バイト境界の直前にキーワードがまたがるように配置 (65530 文字目に配置)
                    const string kw = "SUPER_SECRET_KEYWORD";
                    var sb = new StringBuilder();
                    sb.Append(new string('A', 65530));
                    sb.Append(kw);
                    sb.Append(new string('B', 10000));
                    File.WriteAllText(boundaryFile, sb.ToString(), Encoding.UTF8);

                    var qBoundary = SearchQueryParser.Parse(kw);
                    qBoundary.SearchContentMode = true;

                    var snippet = await ContentExtractionService.SearchTextContentWithBufferAsync(
                        boundaryFile,
                        kw,
                        null,
                        null,
                        1,
                        CancellationToken.None);

                    if (snippet == null || !snippet.Contains(kw))
                        throw new Exception("ADR 95 failed: SearchTextContentWithBufferAsync missed keyword across 64KB chunk boundary.");

                    string smallBoundaryFile = Path.Combine(tempDir, "small_boundary_test.txt");
                    File.WriteAllText(smallBoundaryFile, new string('A', 16380) + kw + new string('B', 12000));
                    var smallSnippet = await ContentExtractionService.SearchTextContentWithBufferAsync(
                        smallBoundaryFile, kw, null, null, 1, CancellationToken.None,
                        knownSize: new FileInfo(smallBoundaryFile).Length);
                    if (smallSnippet == null || !smallSnippet.Contains(kw))
                        throw new Exception("Small-file read buffer missed a keyword across the 16 KiB boundary.");

                    // Raw-byte ASCII path and decoded fallback must return the same
                    // answer when non-ASCII text appears before a later ASCII hit.
                    string mixedFile = Path.Combine(tempDir, "mixed_utf8.txt");
                    File.WriteAllText(mixedFile, "先頭の日本語\n" + new string('A', 70000) + "TaRgEt_WoRd", Encoding.UTF8);
                    var mixedSnippet = await ContentExtractionService.SearchTextContentWithBufferAsync(
                        mixedFile, "target_word", null, null, 1, CancellationToken.None);
                    if (mixedSnippet?.Contains("TaRgEt_WoRd") != true)
                        throw new Exception("UTF-8 decoded fallback missed an ASCII term after non-ASCII text.");
                    var asciiMiss = await ContentExtractionService.SearchTextContentWithBufferAsync(
                        boundaryFile, "DOES_NOT_EXIST", null, null, 1, CancellationToken.None);
                    if (asciiMiss != null)
                        throw new Exception("ASCII byte search reported a false positive.");

                    // C. 安全是正検証: TreeCachePruningIndex の安全化
                    var mockRoot = new FileItemNode
                    {
                        FullPath = @"C:\Mock",
                        LastModified = DateTime.UtcNow,
                        IsDirectory = true
                    };
                    var pruner = new TreeCachePruningIndex(mockRoot);
                    if (pruner.TryGetPrunedEntries(@"C:\Mock", DateTime.UtcNow, true, out _))
                        throw new Exception("ADR 95 failed: TreeCachePruningIndex must return false to avoid skipping modified files.");

                    // D. OpenBufferedReadStream メモリ展開の検証
                    using var memStream = ContentExtractionService.OpenBufferedReadStream(boundaryFile);
                    if (memStream is not MemoryStream)
                        throw new Exception("ADR 95 failed: OpenBufferedReadStream should buffer files under 20MB in memory.");

                    // E. 長い非一致走査後にRead幅を上げても、後方の本文ヒットを失わない。
                    string largeFile = Path.Combine(tempDir, "adaptive_read.txt");
                    byte[] fill = new byte[1024 * 1024];
                    Array.Fill(fill, (byte)'Q');
                    using (var output = File.Create(largeFile))
                    {
                        for (int i = 0; i < 18; i++) output.Write(fill);
                    }
                    var readController = new AdaptiveTextReadController(isNetworkPath: false);
                    long largeSize = new FileInfo(largeFile).Length;
                    var missing = await ContentExtractionService.SearchTextContentWithBufferAsync(
                        largeFile, "TARGET_ADAPTIVE", null, null, 1, CancellationToken.None,
                        readController: readController, knownSize: largeSize);
                    if (missing != null || readController.SelectBufferSize(largeSize) <= AdaptiveTextReadController.DefaultBufferSize)
                        throw new Exception("Adaptive text read did not promote after a complete large-file miss.");
                    File.AppendAllText(largeFile, "TARGET_ADAPTIVE", Encoding.UTF8);
                    var foundAfterPromotion = await ContentExtractionService.SearchTextContentWithBufferAsync(
                        largeFile, "TARGET_ADAPTIVE", null, null, 1, CancellationToken.None,
                        readController: readController, knownSize: new FileInfo(largeFile).Length);
                    if (foundAfterPromotion?.Contains("TARGET_ADAPTIVE") != true)
                        throw new Exception("Adaptive text read missed a keyword after buffer promotion.");
                }
                finally
                {
                    if (Directory.Exists(tempDir))
                    {
                        Directory.Delete(tempDir, true);
                    }
                }
            }

            // -------------------------------------------------------------
            // 23. ADR 97: Astra全体レビュー是正 (Search: XML文字参照・境界・BOMなしUTF-16・3GiB保護・外部Index SQL)
            // -------------------------------------------------------------
            {
                // A. XML文字参照復元（HtmlDecode）＆ Excel共有文字列/セル境界空白挿入
                string xmlWithEntities = "<w:p><w:r><w:t>R&amp;D&lt;Project&gt; &quot;2026&quot;</w:t></w:r></w:p>";
                string decoded = ContentExtractionService.StripXmlTagsFast(xmlWithEntities);
                if (!decoded.Contains("R&D<Project> \"2026\""))
                    throw new Exception($"ADR 97 failed: StripXmlTagsFast failed to decode XML entities. Got: '{decoded}'");

                string excelSharedStrings = "<si><t>Alpha</t></si><si><t>Beta</t></si><c><v>100</v></c><c><v>200</v></c>";
                string splitExcel = ContentExtractionService.StripXmlTagsFast(excelSharedStrings);
                if (splitExcel.Contains("AlphaBeta") || !splitExcel.Contains("Alpha") || !splitExcel.Contains("Beta"))
                    throw new Exception($"ADR 97 failed: StripXmlTagsFast must insert spaces at <si> / <c> boundaries to prevent merging. Got: '{splitExcel}'");

                // B. BOMなしUTF-16LE テキストのバイナリ誤脱落防止
                string tempDir = Path.Combine(Path.GetTempPath(), "fm_test_adr97_search_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDir);
                try
                {
                    string utf16NoBomFile = Path.Combine(tempDir, "utf16_nobom.txt");
                    // BOMなしUTF-16LE で "CONFIDENTIAL_REPORT" を書き込む
                    var encUtf16NoBom = new UnicodeEncoding(bigEndian: false, byteOrderMark: false);
                    File.WriteAllText(utf16NoBomFile, "CONFIDENTIAL_REPORT_DOCUMENT", encUtf16NoBom);

                    var hit = await ContentExtractionService.SearchTextContentWithBufferAsync(
                        utf16NoBomFile,
                        "CONFIDENTIAL",
                        null,
                        null,
                        1,
                        CancellationToken.None);

                    if (hit == null || !hit.Contains("CONFIDENTIAL"))
                        throw new Exception("ADR 97 failed: SearchTextContentWithBufferAsync failed to match BOM-less UTF-16 text.");

                    // Wordの本文以外にも検索可能な文字がある。部分読みの対象から落とさない。
                    string officePath = Path.Combine(tempDir, "header_footer_endnote.docx");
                    using (var archive = new ZipArchive(File.Create(officePath), ZipArchiveMode.Create))
                    {
                        foreach (var (entryName, value) in new[]
                        {
                            ("word/document.xml", "本文"),
                            ("word/header1.xml", "HEADER_ONLY_TOKEN"),
                            ("word/footer1.xml", "FOOTER_ONLY_TOKEN"),
                            ("word/endnotes.xml", "ENDNOTE_ONLY_TOKEN")
                        })
                        {
                            using var writer = new StreamWriter(archive.CreateEntry(entryName).Open(), new UTF8Encoding(false));
                            writer.Write($"<w:p><w:t>{value}</w:t></w:p>");
                        }
                    }

                    var officeGroups = new List<List<string>>
                    {
                        new() { "HEADER_ONLY_TOKEN" },
                        new() { "FOOTER_ONLY_TOKEN" },
                        new() { "ENDNOTE_ONLY_TOKEN" }
                    };
                    if (!ContentExtractionService.SearchOfficeContent(officePath, officeGroups, out _))
                        throw new Exception("Office content search missed text in a Word header, footer, or endnote.");
                    string? extractedOfficeText = ContentExtractionService.ExtractOfficeText(officePath);
                    if (extractedOfficeText == null || officeGroups.Any(group => !extractedOfficeText.Contains(group[0], StringComparison.Ordinal)))
                        throw new Exception("Office text extraction and live content search disagree about Word text parts.");

                    // C. 3GiB ファイルシミュレーション（境界計算・キャストの負数オーバーフロー防止）
                    long hugeFileSimulatedLen = 3L * 1024 * 1024 * 1024; // 3 GiB
                    int readLen = (int)Math.Min(1024L, Math.Max(0L, hugeFileSimulatedLen));
                    if (readLen != 1024)
                        throw new Exception($"ADR 97 failed: Math.Min with 3GiB should clamp to 1024, got {readLen}");

                    // D. WindowsSearchProvider BuildSearchSql での content:"..." 構文対応
                    var qContent = SearchQueryParser.Parse("content:\"secret_contract\"");
                    if (string.IsNullOrEmpty(qContent.ContentKeyword) || qContent.ContentKeyword != "secret_contract")
                        throw new Exception($"ADR 97 failed: SearchQueryParser did not extract ContentKeyword. Got: '{qContent.ContentKeyword}'");

                    var sql = FolderMorpher.Services.ServerSearch.WindowsSearchProvider.BuildSearchSql(@"\\server\share", qContent);
                    if (sql == null || !sql.Contains("CONTAINS(System.Search.Contents,") || !sql.Contains("secret_contract"))
                        throw new Exception($"ADR 97 failed: BuildSearchSql must include CONTAINS for ContentKeyword. Got: '{sql}'");

                    // E. Filunest-inspired SIMD Raw UTF-8 日本語直接照合の正確性・境界またぎ・フォールバック検証
                    string jpTestFile = Path.Combine(tempDir, "jp_search_test.txt");
                    var sbJp = new StringBuilder();
                    while (Encoding.UTF8.GetByteCount(sbJp.ToString()) < 16380)
                    {
                        sbJp.Append("これは通常の業務文書データのダミーテキストです。\n");
                    }
                    sbJp.Append("【極秘設計図面】\n");
                    for (int i = 0; i < 100; i++)
                    {
                        sbJp.Append("後続のプロジェクト業務議事録および顧客提出資料のサンプルです。\n");
                    }
                    await File.WriteAllTextAsync(jpTestFile, sbJp.ToString(), Encoding.UTF8);
                    long jpFileSize = new FileInfo(jpTestFile).Length;

                    // 1) 境界またぎキーワードの直接照合
                    string? hitSnippet = await ContentExtractionService.SearchTextContentWithBufferAsync(
                        jpTestFile, "極秘設計図面", null, null, 1, CancellationToken.None, knownSize: jpFileSize);
                    if (hitSnippet == null || !hitSnippet.Contains("極秘設計図面"))
                        throw new Exception($"Filunest Raw UTF-8 Japanese search failed: Boundary-crossing keyword '極秘設計図面' was not detected. Snippet: '{hitSnippet}'");

                    // 2) 通常の日本語キーワード
                    string? hitNormal = await ContentExtractionService.SearchTextContentWithBufferAsync(
                        jpTestFile, "業務議事録", null, null, 1, CancellationToken.None, knownSize: jpFileSize);
                    if (hitNormal == null || !hitNormal.Contains("業務議事録"))
                        throw new Exception("Filunest Raw UTF-8 Japanese search failed: Normal keyword '業務議事録' was not detected.");

                    // 3) 完全不一致（Miss）の判定
                    string? missResult = await ContentExtractionService.SearchTextContentWithBufferAsync(
                        jpTestFile, "存在しない日本語キーワード", null, null, 1, CancellationToken.None, knownSize: jpFileSize);
                    if (missResult != null)
                        throw new Exception($"Filunest Raw UTF-8 Japanese search failed: Non-existent keyword returned a hit snippet: '{missResult}'");

                    // 4) 英字混在キーワード（大小文字フォールバック経路）
                    string mixedTestFile = Path.Combine(tempDir, "mixed_test.txt");
                    await File.WriteAllTextAsync(mixedTestFile, "2026年度のProjectAlpha計画書について承認します。", Encoding.UTF8);
                    string? hitMixed = await ContentExtractionService.SearchTextContentWithBufferAsync(
                        mixedTestFile, "projectalpha計画書", null, null, 1, CancellationToken.None, knownSize: new FileInfo(mixedTestFile).Length);
                    if (hitMixed == null || !hitMixed.Contains("ProjectAlpha計画書"))
                        throw new Exception("Filunest search failed: Mixed alphanumeric-Japanese keyword did not fall back or match correctly.");

                    // 5) A/B 実測ベンチマーク（10MB 日本語テキスト未ヒット走査: 旧デコーダー方式 vs 新SIMD方式）
                    string benchFile = Path.Combine(tempDir, "jp_bench_10mb.txt");
                    byte[] chunk = Encoding.UTF8.GetBytes("これはファイルサーバーに蓄積された大容量の日本語業務報告書および設計レビュー議事録データです。\r\n");
                    using (var fsBench = new FileStream(benchFile, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                    {
                        while (fsBench.Length < 10 * 1024 * 1024)
                        {
                            fsBench.Write(chunk, 0, chunk.Length);
                        }
                    }
                    long benchSize = new FileInfo(benchFile).Length;
                    double mb = benchSize / (1024.0 * 1024.0);

                    // --- [A] 従来のデコーダー方式（StreamReader UTF-8 -> UTF-16 デコード走査） ---
                    // ウォームアップ
                    await ContentExtractionService.SearchTextContentWithBufferAsync(
                        benchFile, "非該当キーワード", null, null, 1, CancellationToken.None, knownSize: benchSize, forceDecoderRoute: true);

                    var swDecoder = System.Diagnostics.Stopwatch.StartNew();
                    await ContentExtractionService.SearchTextContentWithBufferAsync(
                        benchFile, "非該当キーワード", null, null, 1, CancellationToken.None, knownSize: benchSize, forceDecoderRoute: true);
                    swDecoder.Stop();
                    double decoderMs = swDecoder.Elapsed.TotalMilliseconds;
                    double decoderSpeed = mb / swDecoder.Elapsed.TotalSeconds;

                    // --- [B] Filunest流 Raw UTF-8 SIMD 直接照合方式 ---
                    // ウォームアップ
                    await ContentExtractionService.SearchTextContentWithBufferAsync(
                        benchFile, "非該当キーワード", null, null, 1, CancellationToken.None, knownSize: benchSize, forceDecoderRoute: false);

                    var swSimd = System.Diagnostics.Stopwatch.StartNew();
                    await ContentExtractionService.SearchTextContentWithBufferAsync(
                        benchFile, "非該当キーワード", null, null, 1, CancellationToken.None, knownSize: benchSize, forceDecoderRoute: false);
                    swSimd.Stop();
                    double simdMs = swSimd.Elapsed.TotalMilliseconds;
                    double simdSpeed = mb / swSimd.Elapsed.TotalSeconds;

                    double speedup = decoderMs / Math.Max(0.001, simdMs);
                    Console.WriteLine($"    --> [A/B BENCHMARK 10.0MB 日本語未ヒット走査]");
                    Console.WriteLine($"        - 旧方式 (StreamReader デコード走査): {decoderMs:F2}ms ({decoderSpeed:F1} MB/s)");
                    Console.WriteLine($"        - 新方式 (Filunest SIMD 生バイト照合): {simdMs:F2}ms ({simdSpeed:F1} MB/s)");
                    Console.WriteLine($"        - 速度向上: {speedup:F2}x 高速化 (差分: {decoderMs - simdMs:F2}ms 削減)");

                    // 6) Filunest流 行窓スニペット（Line-Window Excerpt）の検証
                    string multiLineText = "Line 1: System initialization sequence started.\nLine 2: Target keyword ALPHA_PAYLOAD located successfully.\nLine 3: Integrity verification complete.";
                    int kwIndex = multiLineText.IndexOf("ALPHA_PAYLOAD", StringComparison.Ordinal);
                    string lineSnippet = ContentExtractionService.ExtractSnippet(multiLineText, kwIndex, "ALPHA_PAYLOAD".Length, radius: 40);
                    if (!lineSnippet.Contains("Line 1") || !lineSnippet.Contains("ALPHA_PAYLOAD") || !lineSnippet.Contains("Line 3"))
                    {
                        throw new Exception($"Filunest line-window excerpt failed: Snippet did not preserve surrounding line contexts. Got: '{lineSnippet}'");
                    }

                    // 7) PDF UPS/画像PDF 誤ヒット防止検証 (ADR 137)
                    // (A) メタデータにCUPS/Supplementsを含むが本文にUPSがないPDF
                    string dummyPdfMeta = Path.Combine(tempDir, "meta_cups.pdf");
                    string pdfMetaContent = "%PDF-1.4\n1 0 obj\n<< /Producer (CUPS v2.4.1) /Supplements 0 /Group << /S /Transparency >> >>\nendobj\n" +
                        "2 0 obj\n<< /Length 40 >>\nstream\nBT\n/F1 12 Tf\n(Hello World Document) Tj\nET\nendstream\nendobj\nxref\n0 3\ntrailer\n<< /Root 1 0 R >>\n%%EOF";
                    File.WriteAllText(dummyPdfMeta, pdfMetaContent, Encoding.ASCII);
                    bool metaHit = PdfSearchHelper.SearchPdfContent(dummyPdfMeta, "UPS", out _);
                    if (metaHit)
                    {
                        throw new Exception("PDF search regression: Hit on metadata /Producer CUPS when UPS is not in visible text.");
                    }

                    // (B) 画像ストリームのみの画像PDF（スキャンPDF）
                    string dummyScanPdf = Path.Combine(tempDir, "scan_image.pdf");
                    byte[] upsStream = new byte[] { 0x78, 0x9C, 0x75, 0x70, 0x73, 0x00, 0x01, 0x02, 0x03, 0x04 };
                    using (var fsPdf = new FileStream(dummyScanPdf, FileMode.Create))
                    {
                        byte[] headBytes = Encoding.ASCII.GetBytes("%PDF-1.4\n1 0 obj\n<< /Type /XObject /Subtype /Image /Width 100 /Height 100 /Filter /FlateDecode /Length 10 >>\nstream\r\n");
                        fsPdf.Write(headBytes, 0, headBytes.Length);
                        fsPdf.Write(upsStream, 0, upsStream.Length);
                        byte[] tailBytes = Encoding.ASCII.GetBytes("\r\nendstream\nendobj\nxref\n0 2\ntrailer\n<< /Root 1 0 R >>\n%%EOF");
                        fsPdf.Write(tailBytes, 0, tailBytes.Length);
                    }
                    bool scanHit = PdfSearchHelper.SearchPdfContent(dummyScanPdf, "UPS", out _);
                    if (scanHit)
                    {
                        throw new Exception("PDF search regression: Hit on raw raster image stream in image-only PDF.");
                    }

                    // (C) 正当な本文 (UPS) Tj を持つPDFは確実にヒット
                    string dummyValidPdf = Path.Combine(tempDir, "valid_ups.pdf");
                    string validPdfContent = "%PDF-1.4\n1 0 obj\n<< /Length 45 >>\nstream\nBT\n/F1 12 Tf\n(Critical UPS Power Supply) Tj\nET\nendstream\nendobj\nxref\n0 2\ntrailer\n<< /Root 1 0 R >>\n%%EOF";
                    File.WriteAllText(dummyValidPdf, validPdfContent, Encoding.ASCII);
                    bool validHit = PdfSearchHelper.SearchPdfContent(dummyValidPdf, "UPS", out string validSnippet);
                    if (!validHit || !validSnippet.Contains("UPS"))
                    {
                        throw new Exception("PDF search regression: Failed to match legitimate text in (Critical UPS Power Supply) Tj.");
                    }

                    // (D) PdfPig 照合 & ベンチマーク測定 (ADR 143)
                    string dummyPigPdf = Path.Combine(tempDir, "pig_valid.pdf");
                    var pdfBuilder = new UglyToad.PdfPig.Writer.PdfDocumentBuilder();
                    var page = pdfBuilder.AddPage(595, 842);
                    var font = pdfBuilder.AddStandard14Font(UglyToad.PdfPig.Fonts.Standard14Fonts.Standard14Font.Helvetica);
                    page.AddText("Critical UPS Power Supply", 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
                    File.WriteAllBytes(dummyPigPdf, pdfBuilder.Build());

                    bool pigHit = PdfSearchHelper.SearchWithPdfPig(dummyPigPdf, "UPS", out string pigSnippet);
                    if (!pigHit || !pigSnippet.Contains("UPS"))
                    {
                        throw new Exception("PdfPig failed to extract text from structured pig_valid.pdf");
                    }

                    // 50回連続走査によるベンチマーク測定
                    var swPig = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 50; i++)
                    {
                        PdfSearchHelper.SearchWithPdfPig(dummyPigPdf, "UPS", out _);
                    }
                    swPig.Stop();

                    var swPipeline = System.Diagnostics.Stopwatch.StartNew();
                    for (int i = 0; i < 50; i++)
                    {
                        PdfSearchHelper.SearchPdfContent(dummyPigPdf, "UPS", out _);
                    }
                    swPipeline.Stop();

                    Console.WriteLine($"    --> [PDF BENCHMARK 50回走査] PdfPig直叩き: {swPig.Elapsed.TotalMilliseconds:F2}ms, パイプライン全体: {swPipeline.Elapsed.TotalMilliseconds:F2}ms");

                    // 8) Raw UTF-8 Short-Read 耐性検証 (ADR 138)
                    byte[] testJpBytes = Encoding.UTF8.GetBytes("これは重要な設計図面の承認記録です。");
                    using var shortReadStream = new ShortReadStream(testJpBytes, maxChunk: 3);
                    byte[] kwBytes = Encoding.UTF8.GetBytes("設計図面");
                    var rawShortResult = await ContentExtractionService.SearchRawBytesUtf8Async(
                        shortReadStream, kwBytes, 4096, CancellationToken.None);
                    if (rawShortResult.State.ToString() != "Hit")
                    {
                        throw new Exception("Raw UTF-8 short-read search failed: Missed keyword when stream returned small chunks.");
                    }

                    // 9) 先頭ASCII＋後半CP932フォールバック検証 (ADR 138)
                    string cp932File = Path.Combine(tempDir, "ascii_head_cp932.txt");
                    var sjis = Encoding.GetEncoding("shift_jis");
                    using (var fsCp = new FileStream(cp932File, FileMode.Create))
                    {
                        byte[] asciiHeader = Encoding.ASCII.GetBytes(new string('=', 100) + "\nLog Header: System Operation Daily Log\n" + new string('A', 5000) + "\n");
                        fsCp.Write(asciiHeader, 0, asciiHeader.Length);
                        byte[] sjisBody = sjis.GetBytes("担当部署：運用課 承認完了\n");
                        fsCp.Write(sjisBody, 0, sjisBody.Length);
                    }
                    var cpHitSnippet = await ContentExtractionService.SearchTextContentWithBufferAsync(
                        cp932File, "運用課", null, null, 1, CancellationToken.None);
                    if (cpHitSnippet == null || !cpHitSnippet.Contains("運用課"))
                    {
                        throw new Exception("Provisional UTF-8 with ASCII header failed to fallback to CP932.");
                    }

                    // 10) Filunest互換 PP-OCRv6-small 2段ロケットOCRエンジンの検証 (ADR 144)
                    if (!OcrWorkerService.IsSupportedOcrExtension(".png") ||
                        !OcrWorkerService.IsSupportedOcrExtension(".jpg") ||
                        !OcrWorkerService.IsSupportedOcrExtension(".pdf") ||
                        OcrWorkerService.IsSupportedOcrExtension(".txt") ||
                        OcrWorkerService.IsSupportedOcrExtension(".zip"))
                    {
                        throw new Exception("OcrWorkerService: Supported extension check failed.");
                    }

                    // OCR ドキュメントマッチング & 3-Line Window スニペット検証
                    var mockDoc = new OcrWorkerService.OcrDocumentResult(
                        "C:\\Mock\\scanned_invoice.png",
                        true,
                        new[]
                        {
                            new OcrWorkerService.OcrPageInfo(1, "請求書 2026年10月度\n品名：サーバー保守費用\n担当：業務システム課\n金額：150,000円",
                                new[] { "請求書 2026年10月度", "品名：サーバー保守費用", "担当：業務システム課", "金額：150,000円" })
                        },
                        null);

                    var qOcr = SearchQueryParser.Parse("content:\"サーバー保守\"");
                    qOcr.IncludeOcr = true;
                    if (!OcrWorkerService.TryMatchOcrDocument(mockDoc, qOcr, out string? ocrSnippet, out int ocrPage))
                    {
                        throw new Exception("OcrWorkerService.TryMatchOcrDocument failed to match content keyword.");
                    }
                    if (ocrPage != 1 || ocrSnippet == null || !ocrSnippet.Contains("サーバー保守費用"))
                    {
                        throw new Exception($"OcrWorkerService.TryMatchOcrDocument returned unexpected snippet: {ocrSnippet}, page: {ocrPage}");
                    }

                    // 複数条件 (AND) マッチング検証
                    var qOcrAnd = SearchQueryParser.Parse("content:\"サーバー\" content:\"業務システム課\"");
                    qOcrAnd.IncludeOcr = true;
                    if (!OcrWorkerService.TryMatchOcrDocument(mockDoc, qOcrAnd, out _, out _))
                    {
                        throw new Exception("OcrWorkerService.TryMatchOcrDocument failed on AND query.");
                    }

                    // 実ワーカー連携テスト (環境が利用可能な場合のみ実推論を安全に検証)
                    if (OcrWorkerService.IsEnvironmentAvailable())
                    {
                        await using var client = await OcrWorkerService.TryCreateClientAsync(CancellationToken.None);
                        if (client != null)
                        {
                            // 小さなダミー画像でタイムアウト/クラッシュなく即応することを確認
                            string ocrTestPng = Path.Combine(tempDir, "ocr_verify_dummy.png");
                            byte[] dummyPngBytes = new byte[] {
                                0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
                                0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
                                0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
                                0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
                                0x89, 0x00, 0x00, 0x00, 0x0A, 0x49, 0x44, 0x41,
                                0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
                                0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00,
                                0x00, 0x00, 0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE,
                                0x42, 0x60, 0x82
                            };
                            await File.WriteAllBytesAsync(ocrTestPng, dummyPngBytes);

                            var docRes = await client.ProcessFileAsync(ocrTestPng, CancellationToken.None);
                            if (!docRes.Success)
                            {
                                throw new Exception($"OcrWorkerClient real inference verification failed: {docRes.Warning}");
                            }
                        }
                    }
                }
                finally
                {
                    SafeDeleteDirectory(tempDir);
                }
            }
        }
    }

    internal sealed class MockSearchProvider : FolderMorpher.Services.ServerSearch.IServerSearchProvider
    {
        private readonly string _supportedPrefix;
        private readonly IReadOnlyList<string> _candidates;

        public string Name => "Mock Provider";

        public MockSearchProvider(string supportedPrefix, IReadOnlyList<string> candidates)
        {
            _supportedPrefix = supportedPrefix;
            _candidates = candidates;
        }

        public Task<bool> CanHandleAsync(string targetPath, CancellationToken ct)
        {
            return Task.FromResult(targetPath.StartsWith(_supportedPrefix, StringComparison.OrdinalIgnoreCase));
        }

        public Task<IReadOnlyList<string>?> QueryCandidatesAsync(string targetPath, FolderMorpher.Models.SearchQuery query, CancellationToken ct)
        {
            return Task.FromResult<IReadOnlyList<string>?>(_candidates);
        }
    }

    internal sealed class ShortReadStream : MemoryStream
    {
        private readonly int _maxChunk;
        public ShortReadStream(byte[] buffer, int maxChunk = 3) : base(buffer)
        {
            _maxChunk = maxChunk;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int allowed = Math.Min(count, _maxChunk);
            return base.Read(buffer, offset, allowed);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            int allowed = Math.Min(buffer.Length, _maxChunk);
            byte[] temp = new byte[allowed];
            int read = base.Read(temp, 0, allowed);
            temp.AsSpan(0, read).CopyTo(buffer.Span);
            return ValueTask.FromResult(read);
        }
    }
}

