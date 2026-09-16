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

                    // 検索テスト (content:"最高機密プロジェクトX")
                    var qContent = SearchQueryParser.Parse("content:\"最高機密プロジェクトX\"");
                    var batchResults = new List<SearchResultItem>();
                    var batchYield = new Progress<IReadOnlyList<SearchResultItem>>(items => batchResults.AddRange(items));

                    var results = await searchEngine.SearchDirectFolderAsync(tempDir, qContent, batchYield, null, CancellationToken.None);

                    // テキストファイルとPDFファイルがヒットし、バイナリファイルはEarly Dropで除外されていること
                    bool txtFound = false;
                    bool pdfFound = false;
                    bool binFound = false;

                    foreach (var r in results)
                    {
                        if (r.FullPath.EndsWith("Confidential_Doc.txt")) txtFound = true;
                        if (r.FullPath.EndsWith("Contract_Doc.pdf")) pdfFound = true;
                        if (r.FullPath.EndsWith("Sample_Binary.dat")) binFound = true;
                    }

                    if (!txtFound)
                        throw new Exception("Content Search failed: Confidential_Doc.txt was not detected.");

                    if (!pdfFound)
                        throw new Exception("Content Search failed: Contract_Doc.pdf was not detected via PDF search engine.");

                    if (binFound)
                        throw new Exception("Content Search failed: Sample_Binary.dat should have been dropped by Early Drop (null bytes).");

                    // 4. SearchContentMode ("本文も検索" トグル連動) 検証
                    var qToggle = SearchQueryParser.Parse("最高機密プロジェクトX");
                    qToggle.SearchContentMode = true; // トグルON

                    var toggleResults = await searchEngine.SearchDirectFolderAsync(tempDir, qToggle, null, null, CancellationToken.None);
                    if (toggleResults.Count < 2)
                        throw new Exception($"SearchContentMode failed: Expected at least 2 hits for TXT & PDF, got {toggleResults.Count}");
                }
                finally
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }
}
