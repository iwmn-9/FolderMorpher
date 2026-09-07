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

        /// <summary>
        /// Generates a professional Excel audit report for user/group effective folder access
        /// </summary>
        public void ExportEffectiveAccessReport(string outputPath, EffectiveAccessAuditReport report)
        {
            using var workbook = new XLWorkbook();
            var ws = workbook.Worksheets.Add("実効アクセス権台帳");
            ws.ShowGridLines = true;

            // Title
            ws.Cell("B2").Value = "FolderMorpher — NTFS実効アクセス権（逆引き監査）台帳";
            ws.Cell("B2").Style.Font.Bold = true;
            ws.Cell("B2").Style.Font.FontSize = 16;
            ws.Cell("B2").Style.Font.FontColor = XLColor.FromHtml("#0F172A");

            ws.Cell("B3").Value = $"調査対象: {report.TargetDisplayName} ({report.TargetAccountName})  |  解決状況: {report.ResolutionStatusText}  |  スキャンルート: {report.RootFolderPath}  |  出力日時: {report.ScanTimestamp:yyyy/MM/dd HH:mm:ss}";
            ws.Cell("B3").Style.Font.FontSize = 10;
            ws.Cell("B3").Style.Font.FontColor = XLColor.DimGray;

            if (report.IsUncPath)
            {
                ws.Cell("B4").Value = "⚠️ " + report.UncShareNotice;
                ws.Cell("B4").Style.Font.FontSize = 9;
                ws.Cell("B4").Style.Font.Bold = true;
                ws.Cell("B4").Style.Font.FontColor = XLColor.FromHtml("#B45309");
            }

            // KPI Cards
            DrawKpiCard(ws, "B5", "C6", "アクセス可能フォルダ数", $"{report.AccessibleFolders.Count:N0} 箇所", "#2563EB");
            DrawKpiCard(ws, "D5", "E6", "フルコントロール", $"{report.FullControlCount:N0} 箇所", "#DC2626");
            DrawKpiCard(ws, "F5", "G6", "変更 (Modify)", $"{report.ModifyCount:N0} 箇所", "#D97706");
            DrawKpiCard(ws, "H5", "I6", "読み取り専用", $"{report.ReadOnlyCount:N0} 箇所", "#059669");

            // Group Memberships Section
            int row = 8;
            ws.Cell(row, 2).Value = "【所属グループ一覧 (多重入れ子・再帰解決済み)】";
            ws.Cell(row, 2).Style.Font.Bold = true;
            ws.Cell(row, 2).Style.Font.FontSize = 11;
            ws.Cell(row, 2).Style.Font.FontColor = XLColor.FromHtml("#1E293B");
            row++;

            string[] grpHeaders = { "グループ名", "表示名", "所属形態", "入れ子深度", "SID" };
            for (int i = 0; i < grpHeaders.Length; i++)
            {
                var cell = ws.Cell(row, 2 + i);
                cell.Value = grpHeaders[i];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#475569");
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }
            row++;

            if (report.GroupMemberships.Count == 0)
            {
                ws.Cell(row, 2).Value = "(所属グループなし、または直接所属のみ)";
                ws.Cell(row, 2).Style.Font.Italic = true;
                ws.Cell(row, 2).Style.Font.FontColor = XLColor.Gray;
                row++;
            }
            else
            {
                foreach (var g in report.GroupMemberships)
                {
                    ws.Cell(row, 2).Value = g.GroupName;
                    ws.Cell(row, 3).Value = g.DisplayName;
                    ws.Cell(row, 4).Value = g.DirectStatusText;
                    ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(row, 5).Value = g.NestingDepth;
                    ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                    ws.Cell(row, 6).Value = g.Sid;
                    row++;
                }
            }

            row += 2;

            // Accessible Folders Table
            ws.Cell(row, 2).Value = "【アクセス可能フォルダー詳細一覧】";
            ws.Cell(row, 2).Style.Font.Bold = true;
            ws.Cell(row, 2).Style.Font.FontSize = 11;
            ws.Cell(row, 2).Style.Font.FontColor = XLColor.FromHtml("#1E293B");
            row++;

            int folderHeaderRow = row;
            string[] folderHeaders = { "No.", "フォルダー名", "実効アクセス権", "権限付与元 / 経由グループ", "継承状態", "完全パス (クリックで開く)" };
            for (int i = 0; i < folderHeaders.Length; i++)
            {
                var cell = ws.Cell(row, 2 + i);
                cell.Value = folderHeaders[i];
                cell.Style.Font.Bold = true;
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }
            row++;

            int folderIndex = 1;
            foreach (var item in report.AccessibleFolders)
            {
                ws.Cell(row, 2).Value = folderIndex++;
                ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                ws.Cell(row, 3).Value = item.FolderName;
                ws.Cell(row, 3).Style.Font.Bold = true;

                var permCell = ws.Cell(row, 4);
                permCell.Value = item.FormattedRights;
                permCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                permCell.Style.Font.Bold = true;
                permCell.Style.Font.FontColor = XLColor.FromHtml(item.RightsBadgeBackground);

                ws.Cell(row, 5).Value = item.GrantSource;
                ws.Cell(row, 6).Value = item.InheritanceBadgeText;
                ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                var pathCell = ws.Cell(row, 7);
                pathCell.Value = item.FolderPath;
                try
                {
                    string linkUri = "file:///" + item.FolderPath.Replace('\\', '/');
                    pathCell.SetHyperlink(new XLHyperlink(linkUri));
                    pathCell.Style.Font.FontColor = XLColor.FromHtml("#2563EB");
                    pathCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                }
                catch { }

                row++;
            }

            var tableRange = ws.Range(folderHeaderRow, 2, row - 1, 7);
            tableRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            tableRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            tableRange.SetAutoFilter();

            ws.Columns(2, 7).AdjustToContents(3, 120);

            workbook.SaveAs(outputPath);
        }
    }
}
