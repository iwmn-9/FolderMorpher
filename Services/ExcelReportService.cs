using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using FolderMorpher.Models;

namespace FolderMorpher.Services
{
    public class ExcelReportService
    {
        public void GenerateComprehensiveReport(
            string outputPath,
            string scannedRoot,
            AuditSummary? auditSummary,
            List<AuditItem>? auditItems,
            MediaOptimizeSummary? mediaSummary,
            List<MediaItem>? mediaItems)
        {
            using var workbook = new XLWorkbook();

            // 1. エグゼクティブ・サマリーシート
            CreateSummarySheet(workbook, scannedRoot, auditSummary, mediaSummary);

            // 2. 監査・断捨離シート（休眠・重複・パス長）
            if (auditItems != null && auditItems.Count > 0)
            {
                CreateAuditSheet(workbook, auditItems);
            }

            // 3. メディア分析シート（写真・モンスター動画）
            if (mediaItems != null && mediaItems.Count > 0)
            {
                CreateMediaSheet(workbook, mediaItems);
            }

            workbook.SaveAs(outputPath);
        }

        private static void CreateSummarySheet(
            XLWorkbook wb,
            string rootPath,
            AuditSummary? audit,
            MediaOptimizeSummary? media)
        {
            var ws = wb.Worksheets.Add("エグゼクティブ・サマリー");
            ws.ShowGridLines = true;

            // タイトルバー
            ws.Cell("B2").Value = "FolderMorpher — ファイルサーバー健全化＆容量診断レポート";
            ws.Cell("B2").Style.Font.Bold = true;
            ws.Cell("B2").Style.Font.FontSize = 16;
            ws.Cell("B2").Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");

            ws.Cell("B3").Value = $"対象パス: {rootPath}  |  出力日時: {DateTime.Now:yyyy/MM/dd HH:mm:ss}";
            ws.Cell("B3").Style.Font.FontSize = 10;
            ws.Cell("B3").Style.Font.FontColor = XLColor.DimGray;

            // KPIカード 1: 総ファイル走査数
            DrawKpiCard(ws, "B5", "D6", "総スキャンファイル数", $"{audit?.TotalFilesScanned ?? 0:N0} 件", "#2563EB");

            // KPIカード 2: 重複ファイルによる無駄
            DrawKpiCard(ws, "E5", "G6", "重複ファイル無駄容量", audit?.DuplicateWastedSizeFormatted ?? "0 B", "#DC2626");

            // KPIカード 3: 3年以上休眠容量
            DrawKpiCard(ws, "H5", "J6", "休眠ファイル容量 (3年超)", audit?.DormantSizeFormatted ?? "0 B", "#D97706");

            // KPIカード 4: 写真軽量化見込み
            DrawKpiCard(ws, "K5", "M6", "写真軽量化による削減見込み", media?.TotalSavedSizeFormatted ?? "0 B", "#059669");

            // 課題サマリーテーブル
            ws.Cell("B9").Value = "【課題別 検出件数サマリー】";
            ws.Cell("B9").Style.Font.Bold = true;
            ws.Cell("B9").Style.Font.FontSize = 12;

            int row = 11;
            ws.Cell(row, 2).Value = "課題種別";
            ws.Cell(row, 3).Value = "件数";
            ws.Cell(row, 4).Value = "影響容量";
            ws.Cell(row, 5).Value = "推奨アクション";
            var headerRange = ws.Range(row, 2, row, 5);
            headerRange.Style.Font.Bold = true;
            headerRange.Style.Fill.BackgroundColor = XLColor.FromHtml("#F1F5F9");
            headerRange.Style.Border.BottomBorder = XLBorderStyleValues.Medium;

            row++;
            ws.Cell(row, 2).Value = "重複ファイル (SHA256完全一致)";
            ws.Cell(row, 3).Value = audit?.DuplicateCount ?? 0;
            ws.Cell(row, 4).Value = audit?.DuplicateWastedSizeFormatted ?? "―";
            ws.Cell(row, 5).Value = "原本以外をアーカイブ退避または削除";

            row++;
            ws.Cell(row, 2).Value = "休眠ファイル (3年以上放置)";
            ws.Cell(row, 3).Value = audit?.DormantCount ?? 0;
            ws.Cell(row, 4).Value = audit?.DormantSizeFormatted ?? "―";
            ws.Cell(row, 5).Value = "各部署へ要否確認・別NAS/クラウドへ退避";

            row++;
            ws.Cell(row, 2).Value = "パス長超過 (260文字以上)";
            ws.Cell(row, 3).Value = audit?.PathTooLongCount ?? 0;
            ws.Cell(row, 4).Value = "―";
            ws.Cell(row, 5).Value = "フォルダ階層の浅層化・リネーム";

            row++;
            ws.Cell(row, 2).Value = "移行禁則文字を含むファイル";
            ws.Cell(row, 3).Value = audit?.InvalidCharCount ?? 0;
            ws.Cell(row, 4).Value = "―";
            ws.Cell(row, 5).Value = "クラウド移行前のリネーム";

            var tableRange = ws.Range(11, 2, row, 5);
            tableRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            tableRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;

            ws.Columns(2, 6).AdjustToContents();
        }

        private static void CreateAuditSheet(XLWorkbook wb, List<AuditItem> items)
        {
            var ws = wb.Worksheets.Add("断捨離・課題一覧");
            ws.ShowGridLines = true;

            int row = 2;
            ws.Cell(row, 2).Value = "問題種別";
            ws.Cell(row, 3).Value = "ファイル名";
            ws.Cell(row, 4).Value = "容量";
            ws.Cell(row, 5).Value = "最終更新日時";
            ws.Cell(row, 6).Value = "詳細";
            ws.Cell(row, 7).Value = "重複グループ";
            ws.Cell(row, 8).Value = "完全パス (クリックで開く)";

            var header = ws.Range(row, 2, row, 8);
            header.Style.Font.Bold = true;
            header.Style.Font.FontColor = XLColor.White;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");

            row++;
            foreach (var item in items)
            {
                ws.Cell(row, 2).Value = item.IssueTypeDisplay;
                ws.Cell(row, 3).Value = item.FileName;
                ws.Cell(row, 4).Value = item.SizeFormatted;
                ws.Cell(row, 5).Value = item.LastWriteTime.ToString("yyyy/MM/dd HH:mm");
                ws.Cell(row, 6).Value = item.Detail;
                ws.Cell(row, 7).Value = item.DuplicateGroupId;

                // フルパスセル（ハイパーリンク化）
                var pathCell = ws.Cell(row, 8);
                pathCell.Value = item.FullPath;
                try
                {
                    // 親フォルダを開くハイパーリンク
                    string linkUri = "file:///" + item.DirectoryPath.Replace('\\', '/');
                    pathCell.SetHyperlink(new XLHyperlink(linkUri));
                    pathCell.Style.Font.FontColor = XLColor.FromHtml("#2563EB");
                    pathCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                }
                catch
                {
                    // URI変換失敗時はテキストのまま
                }

                row++;
            }

            // テーブル書式設定 ＆ オートフィルター
            var fullTable = ws.Range(2, 2, row - 1, 8);
            fullTable.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            fullTable.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            fullTable.SetAutoFilter();

            ws.Columns(2, 8).AdjustToContents(3, 100);
        }

        private static void CreateMediaSheet(XLWorkbook wb, List<MediaItem> items)
        {
            var ws = wb.Worksheets.Add("メディア最適化・動画Top");
            ws.ShowGridLines = true;

            int row = 2;
            ws.Cell(row, 2).Value = "種別";
            ws.Cell(row, 3).Value = "ファイル名";
            ws.Cell(row, 4).Value = "元容量";
            ws.Cell(row, 5).Value = "最適化後容量";
            ws.Cell(row, 6).Value = "削減容量";
            ws.Cell(row, 7).Value = "聖域保護 / ステータス";
            ws.Cell(row, 8).Value = "完全パス (クリックで開く)";

            var header = ws.Range(row, 2, row, 8);
            header.Style.Font.Bold = true;
            header.Style.Font.FontColor = XLColor.White;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#047857"); // 濃いエメラルドグリーン

            row++;
            foreach (var item in items)
            {
                ws.Cell(row, 2).Value = item.IsVideo ? "動画" : "画像";
                ws.Cell(row, 3).Value = item.FileName;
                ws.Cell(row, 4).Value = item.OriginalSizeFormatted;
                ws.Cell(row, 5).Value = item.OptimizedSizeFormatted;
                ws.Cell(row, 6).Value = item.SavedSizeFormatted;
                ws.Cell(row, 7).Value = item.Status;

                var pathCell = ws.Cell(row, 8);
                pathCell.Value = item.FullPath;
                try
                {
                    string linkUri = "file:///" + item.DirectoryPath.Replace('\\', '/');
                    pathCell.SetHyperlink(new XLHyperlink(linkUri));
                    pathCell.Style.Font.FontColor = XLColor.FromHtml("#2563EB");
                    pathCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                }
                catch { }

                row++;
            }

            var fullTable = ws.Range(2, 2, row - 1, 8);
            fullTable.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            fullTable.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            fullTable.SetAutoFilter();

            ws.Columns(2, 8).AdjustToContents(3, 100);
        }

        private static void DrawKpiCard(IXLWorksheet ws, string topLeft, string bottomRight, string label, string value, string accentColorHex)
        {
            var range = ws.Range(topLeft, bottomRight);
            range.Merge();
            range.Value = $"{label}\n{value}";
            range.Style.Alignment.WrapText = true;
            range.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            range.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;
            range.Style.Font.Bold = true;
            range.Style.Font.FontSize = 13;
            range.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
            range.Style.Border.OutsideBorder = XLBorderStyleValues.Medium;
            range.Style.Border.OutsideBorderColor = XLColor.FromHtml(accentColorHex);
        }
    }
}
