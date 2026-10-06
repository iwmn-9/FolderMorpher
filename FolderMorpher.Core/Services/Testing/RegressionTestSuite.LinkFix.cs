using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FolderMorpher.Services;

namespace FolderMorpher.Services.Testing
{
    public static partial class RegressionTestSuite
    {
        /// <summary>
        /// Office LinkFixer 回帰テスト:
        /// 1. 通常リンク＋VBAマクロ混在時に「部分成功（XML修復済 / VBA未修復）」ステータスになること
        /// 2. VBAマクロ単体の場合は安全にスキップされること
        /// </summary>
        public static async Task TestOfficeLinkFixMixedXmlAndVbaPartialSuccessAsync()
        {
            var officeService = new OfficeLinkFixService();
            string tempDir = Path.Combine(Path.GetTempPath(), "FM_RegTest_LinkFix_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string xlsmPath = Path.Combine(tempDir, "BudgetModel.xlsm");
                string oldServer = @"\\OldServer\FinanceShare";
                string newServer = @"\\NewServer\FinanceShare";

                // 合成 .xlsm (ZIP) の作成: externalLink (XML) と vbaProject.bin (バイナリ) の両方に旧パスを含める
                using (var zip = ZipFile.Open(xlsmPath, ZipArchiveMode.Create))
                {
                    // 1. 通常の外部参照XML
                    var xmlEntry = zip.CreateEntry("xl/externalLinks/_rels/externalLink1.xml.rels");
                    using (var writer = new StreamWriter(xmlEntry.Open(), Encoding.UTF8))
                    {
                        writer.Write($"<Relationships><Relationship Target=\"file:///{oldServer}\\data.xlsx\" /></Relationships>");
                    }

                    // 2. VBAマクロバイナリ (vbaProject.bin)
                    var vbaEntry = zip.CreateEntry("xl/vbaProject.bin");
                    using (var vbaStream = vbaEntry.Open())
                    {
                        byte[] vbaBytes = Encoding.ASCII.GetBytes($"DUMMY_VBA_CODE_CONTAINING_{oldServer}_MACRO_REF");
                        vbaStream.Write(vbaBytes, 0, vbaBytes.Length);
                    }
                }

                // A. スキャン検証: Categories に ExternalWorkbook と VbaMacro の両方が検出されること
                var scanResults = await officeService.ScanOfficeLinksAsync(tempDir, oldServer, newServer, null, CancellationToken.None);
                if (scanResults.Count != 1)
                {
                    throw new InvalidOperationException($"Expected 1 scanned item, got {scanResults.Count}");
                }

                var item = scanResults[0];
                if (!item.Categories.HasFlag(OfficeLinkCategory.ExternalWorkbook))
                {
                    throw new InvalidOperationException("ExternalWorkbook flag was not detected!");
                }
                if (!item.Categories.HasFlag(OfficeLinkCategory.VbaMacro))
                {
                    throw new InvalidOperationException("VbaMacro flag was not detected in mixed xlsm!");
                }

                // B. 修復実行検証: 通常リンク＋VBA混在時に「部分成功」ステータスになること
                int fixedCount = await officeService.ExecuteOfficeFixAsync(new List<OfficeLinkItem> { item }, null, CancellationToken.None);
                if (fixedCount != 1)
                {
                    throw new InvalidOperationException($"Expected 1 fixed count, got {fixedCount}");
                }

                if (!item.IsFixed)
                {
                    throw new InvalidOperationException("Item.IsFixed must be true for partially fixed item");
                }
                if (item.FixStatus != OfficeFixStatus.PartiallyFixed)
                {
                    throw new InvalidOperationException($"Expected FixStatus == PartiallyFixed, but got {item.FixStatus}");
                }
                if (!item.Status.Contains("一部修復") || !item.Status.Contains("VBAマクロは未修復"))
                {
                    throw new InvalidOperationException($"Unexpected item.Status text: {item.Status}");
                }

                // C. 実態検証: XMLは新パスに置換され、VBAは旧パスのまま破壊されずに保護されていること
                using (var verifyZip = ZipFile.OpenRead(xlsmPath))
                {
                    var xmlEntry = verifyZip.GetEntry("xl/externalLinks/_rels/externalLink1.xml.rels");
                    using (var reader = new StreamReader(xmlEntry!.Open(), Encoding.UTF8))
                    {
                        string content = reader.ReadToEnd();
                        if (!content.Contains(newServer))
                        {
                            throw new InvalidOperationException("XML link was not updated to new server path!");
                        }
                    }

                    var vbaEntry = verifyZip.GetEntry("xl/vbaProject.bin");
                    using (var ms = new MemoryStream())
                    {
                        vbaEntry!.Open().CopyTo(ms);
                        string vbaText = Encoding.ASCII.GetString(ms.ToArray());
                        if (!vbaText.Contains(oldServer))
                        {
                            throw new InvalidOperationException("VBA binary was corrupted or modified unexpectedly!");
                        }
                    }
                }
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, true); } catch { }
                }
            }
        }
    }
}
