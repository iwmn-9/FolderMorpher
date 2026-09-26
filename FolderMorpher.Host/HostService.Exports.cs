using System.Text;
using ClosedXML.Excel;
using AstraSize.Models;
using FolderMorpher.Contracts;
using FolderMorpher.Services;

namespace FolderMorpher.Host;

public partial class HostService
{
    public Task ExportSearchResultsAsync(string outputPath, List<SearchResultDto> results, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var items = results.Select(SearchDtoMapper.ToCore).ToList();
            if (outputPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                using var book = new XLWorkbook();
                var sheet = book.Worksheets.Add("Search Results");
                string[] headers = { "種別", "ファイル/フォルダ名", "サイズ (Bytes)", "サイズ (表示)", "更新日時", "拡張子", "パス長", "一致理由 / スニペット", "完全パス" };
                for (var column = 0; column < headers.Length; column++)
                {
                    var cell = sheet.Cell(1, column + 1);
                    cell.Value = headers[column];
                    cell.Style.Font.Bold = true;
                    cell.Style.Fill.BackgroundColor = XLColor.FromArgb(37, 99, 235);
                    cell.Style.Font.FontColor = XLColor.White;
                }
                for (var index = 0; index < items.Count; index++)
                {
                    ct.ThrowIfCancellationRequested();
                    var item = items[index];
                    var row = index + 2;
                    sheet.Cell(row, 1).Value = item.IsDirectory ? "フォルダ" : "ファイル";
                    sheet.Cell(row, 2).Value = item.Name;
                    sheet.Cell(row, 3).Value = item.SizeBytes;
                    sheet.Cell(row, 4).Value = item.FormattedSize;
                    sheet.Cell(row, 5).Value = item.FormattedDate;
                    sheet.Cell(row, 6).Value = item.Extension;
                    sheet.Cell(row, 7).Value = item.PathLength;
                    sheet.Cell(row, 8).Value = item.DisplaySnippetOrReason;
                    sheet.Cell(row, 9).Value = item.FullPath;
                    if (item.IsPathLengthRisk)
                    {
                        sheet.Cell(row, 7).Style.Fill.BackgroundColor = XLColor.FromArgb(254, 226, 226);
                        sheet.Cell(row, 7).Style.Font.FontColor = XLColor.FromArgb(185, 28, 28);
                    }
                }
                sheet.Columns().AdjustToContents();
                ct.ThrowIfCancellationRequested();
                book.SaveAs(outputPath);
            }
            else if (outputPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var rows = new List<string> { "Type,Name,SizeBytes,FormattedSize,LastWriteTime,Extension,PathLength,ReasonOrSnippet,FullPath" };
                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();
                    rows.Add(string.Join(',', new[]
                    {
                        item.IsDirectory ? "Folder" : "File", CsvCell(item.Name), item.SizeBytes.ToString(),
                        CsvCell(item.FormattedSize), CsvCell(item.FormattedDate), CsvCell(item.Extension),
                        item.PathLength.ToString(), CsvCell(item.DisplaySnippetOrReason), CsvCell(item.FullPath)
                    }));
                }
                File.WriteAllLines(outputPath, rows, new UTF8Encoding(true));
            }
            else throw new ArgumentException("Search export must be .xlsx or .csv", nameof(outputPath));
        }, ct);
    }

    public Task ExportStorageScanAsync(string outputPath, string targetPath, List<StorageNodeDto> visibleRows, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var rows = visibleRows.Select(dto => new FileItemNode(dto.FullPath, dto.Name, dto.SizeBytes, dto.IsDirectory, dto.LastModified)
            {
                Parent = dto.IsRoot ? null : new FileItemNode(),
                Percentage = dto.PercentageOfRoot,
                FileCount = dto.FileCount,
                FolderCount = dto.FolderCount
            }).ToList();
            if (outputPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                new ExcelReportService().ExportStorageScanResult(outputPath, targetPath, rows);
            }
            else if (outputPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var lines = new List<string> { "名前,パス,容量,全体占有率,ファイル数,フォルダ数,最終更新" };
                lines.AddRange(rows.Select(row => string.Join(',', new[]
                {
                    CsvCell(row.Name), CsvCell(row.FullPath), CsvCell(row.FormattedSize),
                    CsvCell(row.PercentageFormatted), CsvCell(row.FileCount.ToString()),
                    CsvCell(row.FolderCount.ToString()), CsvCell(row.LastModified?.ToString("yyyy/MM/dd HH:mm") ?? "")
                })));
                File.WriteAllLines(outputPath, lines, new UTF8Encoding(true));
            }
            else throw new ArgumentException("Storage export must be .xlsx or .csv", nameof(outputPath));
        }, ct);
    }

    public Task ExportMigrationDiffAsync(string outputPath, List<MigrationDiffDto> diffs, CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var rows = diffs.Select(dto => new SimDiffItem
            {
                DiffType = dto.DiffType,
                SourcePath = dto.SourcePath,
                SourceDetail = dto.SourceDetail,
                TargetPath = dto.TargetPath,
                TargetDetail = dto.TargetDetail,
                AclChanges = dto.AclChanges
            }).ToList();
            if (outputPath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                new ExcelReportService().ExportSimDiffReport(outputPath, rows);
            }
            else if (outputPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            {
                var lines = new List<string> { "変化の種別,現行サーバー (Before),Before詳細,新環境設計 (After),After詳細,権限差分詳細" };
                lines.AddRange(rows.Select(row => string.Join(',', new[]
                {
                    CsvCell(row.DiffType), CsvCell(row.SourcePath.Replace("\n", " | ")),
                    CsvCell(row.SourceDetail), CsvCell(row.TargetPath), CsvCell(row.TargetDetail),
                    CsvCell(row.FormattedAclChanges.Replace("\n", " | "))
                })));
                File.WriteAllLines(outputPath, lines, new UTF8Encoding(true));
            }
            else throw new ArgumentException("Migration diff export must be .xlsx or .csv", nameof(outputPath));
        }, ct);
    }

    private static string CsvCell(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
}
