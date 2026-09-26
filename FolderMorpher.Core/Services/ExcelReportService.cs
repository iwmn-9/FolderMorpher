using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ClosedXML.Excel;
using AstraSize.Models;
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
            DrawKpiCard(ws, "K5", "M6", "写真軽量化による削減見込み", media == null ? "0 B" : FormatHelper.FormatBytes(media.TotalSavedBytes, 2), "#059669");

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
            ws.Cell(row, 2).Value = "パス長危険域 (240文字以上)";
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
            ws.Cell(row, 9).Value = "優先度点数";
            ws.Cell(row, 10).Value = "点数内訳";

            var header = ws.Range(row, 2, row, 10);
            header.Style.Font.Bold = true;
            header.Style.Font.FontColor = XLColor.White;
            header.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");

            row++;
            foreach (var item in items)
            {
                ws.Cell(row, 2).Value = item.IssueTypeDisplay;
                ws.Cell(row, 3).Value = item.FileName;
                if (item.IsOriginalCandidate)
                {
                    ws.Cell(row, 3).Style.Font.Bold = true;
                }

                ws.Cell(row, 4).Value = item.SizeFormatted;
                ws.Cell(row, 5).Value = item.LastWriteTime.ToString("yyyy/MM/dd HH:mm");
                ws.Cell(row, 6).Value = item.Detail;
                ws.Cell(row, 7).Value = item.DuplicateGroupBadge;
                ws.Cell(row, 9).Value = item.WasteScore;
                ws.Cell(row, 10).Value = item.ScoreBreakdownSummary;

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

                // 重複グループごとの背景色ソフト塗り分け（隣接グループで被らない視認性カラー）
                if (item.IssueType == AuditIssueType.Duplicate && item.DuplicateGroupIndex > 0)
                {
                    var rowRange = ws.Range(row, 2, row, 10);
                    rowRange.Style.Fill.BackgroundColor = XLColor.FromHtml(AuditReportPalette.RowBackground(item));
                }

                row++;
            }

            // テーブル書式設定 ＆ オートフィルター
            var fullTable = ws.Range(2, 2, row - 1, 10);
            fullTable.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            fullTable.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            fullTable.SetAutoFilter();

            ws.Columns(2, 10).AdjustToContents(3, 100);
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
                ws.Cell(row, 4).Value = FormatHelper.FormatBytes(item.OriginalSizeBytes, 2);
                ws.Cell(row, 5).Value = item.OptimizedSizeBytes > 0 ? FormatHelper.FormatBytes(item.OptimizedSizeBytes, 2) : "―";
                ws.Cell(row, 6).Value = item.SavedBytes > 0 ? FormatHelper.FormatBytes(item.SavedBytes, 2) : "―";
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
            DrawKpiCard(ws, "B5", "C6", "総検出フォルダ数", $"{report.AccessibleFolders.Count:N0} 箇所", "#2563EB");
            DrawKpiCard(ws, "D5", "E6", "🚨 飛び地 (獲得)", $"{report.EnclaveCount:N0} 箇所", "#DC2626");
            DrawKpiCard(ws, "F5", "G6", "⛔ 遮断 (消失)", $"{report.SeveredCount:N0} 箇所", "#E11D48");
            DrawKpiCard(ws, "H5", "I6", "⚠️ 走査不能", $"{report.UnavailableCount:N0} 箇所", "#D97706");
            DrawKpiCard(ws, "J5", "K6", "フルコントロール", $"{report.FullControlCount:N0} 箇所", "#DC2626");
            DrawKpiCard(ws, "L5", "M6", "変更 (Modify)", $"{report.ModifyCount:N0} 箇所", "#D97706");
            DrawKpiCard(ws, "N5", "O6", "読み取り専用", $"{report.ReadOnlyCount:N0} 箇所", "#059669");

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
            string[] folderHeaders = { "No.", "フォルダー名", "変化点 / 状態", "実効アクセス権", "権限付与元 / 経由グループ", "詳細トレース / 理由", "継承状態", "完全パス (クリックで開く)" };
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
            foreach (var item in report.AllAuditItems)
            {
                ws.Cell(row, 2).Value = folderIndex++;
                ws.Cell(row, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                ws.Cell(row, 3).Value = item.FolderName;
                ws.Cell(row, 3).Style.Font.Bold = true;

                var changeCell = ws.Cell(row, 4);
                changeCell.Value = item.ChangeBadgeText;
                changeCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                changeCell.Style.Font.Bold = true;
                changeCell.Style.Font.FontColor = XLColor.FromHtml(EffectiveAccessPalette.ChangeForeground(item.ChangeType));

                var permCell = ws.Cell(row, 5);
                permCell.Value = item.FormattedRights;
                permCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                permCell.Style.Font.Bold = true;
                permCell.Style.Font.FontColor = XLColor.FromHtml(EffectiveAccessPalette.RightsBackground(item.PermissionLevel));

                ws.Cell(row, 6).Value = item.GrantSource;
                ws.Cell(row, 7).Value = item.GrantPathTrace;
                ws.Cell(row, 8).Value = item.InheritanceBadgeText;
                ws.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                var pathCell = ws.Cell(row, 9);
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

        public void ExportStorageScanResult(string outputPath, string targetPath, IEnumerable<AstraSize.Models.FileItemNode> items)
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("容量分析結果");
            ws.ShowGridLines = true;

            // Title
            ws.Cell("B2").Value = "FolderMorpher — 容量分析レポート";
            ws.Cell("B2").Style.Font.Bold = true;
            ws.Cell("B2").Style.Font.FontSize = 15;
            ws.Cell("B2").Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");

            ws.Cell("B3").Value = $"対象パス: {targetPath}  |  出力日時: {DateTime.Now:yyyy/MM/dd HH:mm:ss}";
            ws.Cell("B3").Style.Font.FontSize = 10;
            ws.Cell("B3").Style.Font.FontColor = XLColor.DimGray;

            int headerRow = 5;
            string[] headers = { "名前", "フルパス", "容量", "サイズ (Bytes)", "全体占有率", "ファイル数", "フォルダ数", "最終更新" };
            for (int col = 0; col < headers.Length; col++)
            {
                var cell = ws.Cell(headerRow, col + 2);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            int row = headerRow + 1;
            foreach (var item in items)
            {
                ws.Cell(row, 2).Value = item.Name;
                ws.Cell(row, 2).Style.Font.Bold = item.IsDirectory;

                var pathCell = ws.Cell(row, 3);
                pathCell.Value = item.FullPath;
                try
                {
                    string linkUri = "file:///" + item.FullPath.Replace('\\', '/');
                    pathCell.SetHyperlink(new XLHyperlink(linkUri));
                    pathCell.Style.Font.FontColor = XLColor.FromHtml("#2563EB");
                    pathCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                }
                catch { }

                ws.Cell(row, 4).Value = FormatHelper.FormatBytes(item.Size);
                ws.Cell(row, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                ws.Cell(row, 5).Value = item.Size;
                ws.Cell(row, 5).Style.NumberFormat.Format = "#,##0";
                ws.Cell(row, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                ws.Cell(row, 6).Value = item.IsRoot ? "―" : $"{item.Percentage:F1}%";
                ws.Cell(row, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                ws.Cell(row, 7).Value = item.FileCount;
                ws.Cell(row, 7).Style.NumberFormat.Format = "#,##0";
                ws.Cell(row, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                ws.Cell(row, 8).Value = item.FolderCount;
                ws.Cell(row, 8).Style.NumberFormat.Format = "#,##0";
                ws.Cell(row, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                ws.Cell(row, 9).Value = item.LastModified?.ToString("yyyy/MM/dd HH:mm") ?? "-";
                ws.Cell(row, 9).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                row++;
            }

            var tableRange = ws.Range(headerRow, 2, row - 1, 9);
            tableRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            tableRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            tableRange.SetAutoFilter();

            ws.Columns(2, 9).AdjustToContents(3, 100);
            wb.SaveAs(outputPath);
        }

        public void ExportSimDiffReport(string outputPath, IEnumerable<SimDiffItem> diffs)
        {
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add(isJa ? "移行変化点・差分対比" : "Migration Diffs");
            ws.ShowGridLines = true;

            ws.Cell("B2").Value = isJa ? "FolderMorpher — 移行変化点 差分対比レポート" : "FolderMorpher — Architecture Diff Review Report";
            ws.Cell("B2").Style.Font.Bold = true;
            ws.Cell("B2").Style.Font.FontSize = 15;
            ws.Cell("B2").Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");

            ws.Cell("B3").Value = isJa ? $"出力日時: {DateTime.Now:yyyy/MM/dd HH:mm:ss}" : $"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            ws.Cell("B3").Style.Font.FontSize = 10;
            ws.Cell("B3").Style.Font.FontColor = XLColor.DimGray;

            int headerRow = 5;
            string[] headers = isJa
                ? new[] { "変化の種別", "現行サーバー (Before)", "Before詳細", "新環境設計 (After)", "After詳細", "権限差分詳細" }
                : new[] { "Diff Type", "Source Server (Before)", "Before Details", "Target Architecture (After)", "After Details", "ACL Diff Details" };
            for (int col = 0; col < headers.Length; col++)
            {
                var cell = ws.Cell(headerRow, col + 2);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            int row = headerRow + 1;
            foreach (var d in diffs)
            {
                ws.Cell(row, 2).Value = d.DiffType;
                ws.Cell(row, 2).Style.Font.Bold = true;

                var srcCell = ws.Cell(row, 3);
                srcCell.Value = d.SourcePath;
                if (!string.IsNullOrWhiteSpace(d.SourcePath))
                {
                    try
                    {
                        var firstPath = d.SourcePath.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                        if (!string.IsNullOrEmpty(firstPath))
                        {
                            string linkUri = "file:///" + firstPath.Replace('\\', '/');
                            srcCell.SetHyperlink(new XLHyperlink(linkUri));
                            srcCell.Style.Font.FontColor = XLColor.FromHtml("#2563EB");
                            srcCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                        }
                    }
                    catch { }
                }

                ws.Cell(row, 4).Value = d.SourceDetail;
                ws.Cell(row, 5).Value = d.TargetPath;
                ws.Cell(row, 6).Value = d.TargetDetail;
                ws.Cell(row, 7).Value = d.FormattedAclChanges;

                row++;
            }

            var tableRange = ws.Range(headerRow, 2, row - 1, 7);
            tableRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            tableRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            tableRange.SetAutoFilter();

            ws.Columns(2, 7).AdjustToContents(3, 100);
            wb.SaveAs(outputPath);
        }

        public void ExportSimulationDesignMatrix(string outputPath, IEnumerable<SimFolderNode> rootNodes)
        {
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add(isJa ? "新環境設計マトリクス" : "Design Matrix");
            ws.ShowGridLines = true;

            ws.Cell("B2").Value = isJa ? "FolderMorpher — 移行設計台帳マトリクス" : "FolderMorpher — Migration Design Specification Matrix";
            ws.Cell("B2").Style.Font.Bold = true;
            ws.Cell("B2").Style.Font.FontSize = 15;
            ws.Cell("B2").Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");

            ws.Cell("B3").Value = isJa ? $"出力日時: {DateTime.Now:yyyy/MM/dd HH:mm:ss}" : $"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
            ws.Cell("B3").Style.Font.FontSize = 10;
            ws.Cell("B3").Style.Font.FontColor = XLColor.DimGray;

            int headerRow = 5;
            string[] headers = isJa
                ? new[] { "階層パス", "フォルダ名", "階層レベル", "移行元マッピング", "元容量", "継承状態", "アカウント", "権限種別", "アクセス許可" }
                : new[] { "Path", "Folder Name", "Level", "Source Mapping", "Size", "Inheritance", "Account", "Access Type", "Permissions" };
            for (int col = 0; col < headers.Length; col++)
            {
                var cell = ws.Cell(headerRow, col + 2);
                cell.Value = headers[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            int row = headerRow + 1;

            void WriteNode(SimFolderNode node)
            {
                var mappingStr = node.MappedSourcePaths.Count == 0 ? (isJa ? "(新設)" : "(New)") : string.Join(" | ", node.MappedSourcePaths);
                if (node.AclEntries.Count == 0)
                {
                    ws.Cell(row, 2).Value = node.RelativePath;
                    ws.Cell(row, 3).Value = node.Name;
                    ws.Cell(row, 4).Value = node.LevelPillText;
                    ws.Cell(row, 5).Value = mappingStr;
                    ws.Cell(row, 6).Value = FormatHelper.FormatBytes(node.EstimatedSizeBytes);
                    ws.Cell(row, 7).Value = node.InheritStatusBadge;
                    ws.Cell(row, 8).Value = isJa ? "(設定なし)" : "(None)";
                    ws.Cell(row, 9).Value = "-";
                    ws.Cell(row, 10).Value = "-";
                    row++;
                }
                else
                {
                    foreach (var acl in node.AclEntries)
                    {
                        ws.Cell(row, 2).Value = node.RelativePath;
                        ws.Cell(row, 3).Value = node.Name;
                        ws.Cell(row, 4).Value = node.LevelPillText;
                        ws.Cell(row, 5).Value = mappingStr;
                        ws.Cell(row, 6).Value = FormatHelper.FormatBytes(node.EstimatedSizeBytes);
                        ws.Cell(row, 7).Value = node.InheritStatusBadge;
                        ws.Cell(row, 8).Value = acl.DisplayName;
                        ws.Cell(row, 9).Value = acl.AccessType.ToString();
                        ws.Cell(row, 10).Value = acl.FormattedRights;
                        row++;
                    }
                }

                foreach (var c in node.Children) WriteNode(c);
            }

            foreach (var root in rootNodes)
            {
                WriteNode(root);
            }

            var tableRange = ws.Range(headerRow, 2, row - 1, 10);
            tableRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            tableRange.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            tableRange.SetAutoFilter();

            ws.Columns(2, 10).AdjustToContents(3, 100);
            wb.SaveAs(outputPath);
        }

        public void GenerateMigrationRunbook(
            string outputPath,
            List<MigrationWavePlan> wavePlans,
            string targetRoot,
            MigrationPackageOptions options)
        {
            bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
            using var wb = new XLWorkbook();

            // -------------------------------------------------------------
            // Sheet 1: 概要・Wave計画 (Overview & Wave Plan)
            // -------------------------------------------------------------
            var ws1 = wb.Worksheets.Add(isJa ? "1_概要・Wave計画" : "1_Overview_Waves");
            ws1.ShowGridLines = true;

            ws1.Cell("B2").Value = isJa ? "FolderMorpher — ファイルサーバー移行計画台帳 (Migration Runbook)" : "FolderMorpher — File Server Migration Runbook";
            ws1.Cell("B2").Style.Font.Bold = true;
            ws1.Cell("B2").Style.Font.FontSize = 15;
            ws1.Cell("B2").Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");

            ws1.Cell("B3").Value = isJa
                ? $"出力日時: {DateTime.Now:yyyy/MM/dd HH:mm:ss}  |  新環境ルート: {targetRoot}  |  転送モード: {(options.CopyAcl ? "旧ACL維持 (/COPYALL)" : "新設計ACL適用・データのみ転送 (/COPY:DAT)")}"
                : $"Exported: {DateTime.Now:yyyy-MM-dd HH:mm:ss}  |  Target Root: {targetRoot}  |  Mode: {(options.CopyAcl ? "Copy ACLs (/COPYALL)" : "Apply New ACLs / Data Only (/COPY:DAT)")}";
            ws1.Cell("B3").Style.Font.FontSize = 10;
            ws1.Cell("B3").Style.Font.FontColor = XLColor.DimGray;

            // KPI Summary Cards
            long totalBytesAll = wavePlans.Sum(w => w.TotalSizeBytes);
            long? totalFilesAll = wavePlans.Any(w => w.TotalFileCount.HasValue)
                ? wavePlans.Sum(w => w.TotalFileCount ?? 0)
                : null;
            long rateBytes = (long)Math.Max(1024.0 * 1024.0, options.TransferRateMBps * 1024.0 * 1024.0);
            double totalFullSec = (double)totalBytesAll / rateBytes;
            var totalFullTime = TimeSpan.FromSeconds(Math.Max(5, (int)totalFullSec));
            double deltaRatio = Math.Clamp(options.DeltaRatioPercent, 0.01, 100.0) / 100.0;
            double totalCutoverSec = (double)(totalBytesAll * deltaRatio) / rateBytes;
            var totalCutoverTime = TimeSpan.FromSeconds(Math.Max(5, (int)totalCutoverSec));

            void DrawKpiCard(string cellTopLeft, string title, string val, string sub)
            {
                var rng = ws1.Range(cellTopLeft + ":" + (char)(cellTopLeft[0] + 1) + (int.Parse(cellTopLeft.Substring(1)) + 1));
                rng.Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
                rng.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                rng.Style.Border.OutsideBorderColor = XLColor.FromHtml("#CBD5E1");

                var cTitle = ws1.Cell(cellTopLeft);
                cTitle.Value = title;
                cTitle.Style.Font.FontSize = 9;
                cTitle.Style.Font.FontColor = XLColor.FromHtml("#64748B");

                var cVal = ws1.Cell(int.Parse(cellTopLeft.Substring(1)) + 1, cellTopLeft[0] - 'A' + 1);
                cVal.Value = val;
                cVal.Style.Font.FontSize = 14;
                cVal.Style.Font.Bold = true;
                cVal.Style.Font.FontColor = XLColor.FromHtml("#0F172A");
            }

            DrawKpiCard("B5", isJa ? "総移行データ量" : "Total Size", FormatHelper.FormatBytes(totalBytesAll, 2), "");
            DrawKpiCard("D5", isJa ? "総ファイル件数" : "Total Files", totalFilesAll.HasValue ? $"{totalFilesAll.Value:N0} 件" : (isJa ? "未計測 (-)" : "Unmeasured (-)"), "");
            DrawKpiCard("F5", isJa ? $"全体初回フル見積 ({options.TransferRateMBps:G0}MB/s)" : $"Est. Full Sync ({options.TransferRateMBps:G0}MB/s)", $"{totalFullTime.TotalHours:F1} 時間", "");
            DrawKpiCard("H5", isJa ? $"全体本番切替見積 (差分{options.DeltaRatioPercent:G0}%)" : $"Est. Cutover ({options.DeltaRatioPercent:G0}% Delta)", $"{totalCutoverTime.TotalMinutes:F1} 分", "");

            // Wave Table
            int hRow1 = 8;
            string[] headers1 = isJa
                ? new[] { "Wave", "波次名称・移行対象", "対象フォルダ数", "想定容量", "ファイル数", $"初回フル同期想定 ({options.TransferRateMBps:G0}MB/s)", $"本番カットオーバー想定 (差分{options.DeltaRatioPercent:G0}%)", "警告・留意事項" }
                : new[] { "Wave", "Wave Name / Target", "Folders", "Size", "Files", $"Est. Full Sync ({options.TransferRateMBps:G0}MB/s)", $"Est. Cutover ({options.DeltaRatioPercent:G0}% Delta)", "Warnings & Notes" };

            for (int col = 0; col < headers1.Length; col++)
            {
                var cell = ws1.Cell(hRow1, col + 2);
                cell.Value = headers1[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            int r1 = hRow1 + 1;
            foreach (var w in wavePlans)
            {
                ws1.Cell(r1, 2).Value = $"Wave {w.WaveNumber}";
                ws1.Cell(r1, 2).Style.Font.Bold = true;
                ws1.Cell(r1, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                ws1.Cell(r1, 3).Value = w.WaveName;
                ws1.Cell(r1, 4).Value = w.TargetNodes.Count;
                ws1.Cell(r1, 4).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                ws1.Cell(r1, 5).Value = FormatHelper.FormatBytes(w.TotalSizeBytes, 2);
                ws1.Cell(r1, 5).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                if (w.TotalFileCount.HasValue)
                {
                    ws1.Cell(r1, 6).Value = w.TotalFileCount.Value;
                    ws1.Cell(r1, 6).Style.NumberFormat.Format = "#,##0";
                }
                else
                {
                    ws1.Cell(r1, 6).Value = "-";
                }
                ws1.Cell(r1, 6).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                ws1.Cell(r1, 7).Value = WaveDisplayFormat.FormatDuration(w.EstimatedFullCopyTime);
                ws1.Cell(r1, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                ws1.Cell(r1, 8).Value = WaveDisplayFormat.FormatDuration(w.EstimatedCutoverTime);
                ws1.Cell(r1, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                string warn = "";
                if (w.HasOver48hWarning) warn += isJa ? "⚠️ 週末枠超過リスク " : "⚠️ Over 48h (Weekend Risk) ";
                if (w.HasHighFileCountWarning) warn += isJa ? "⚠️ 小ファイル過多(/MT:32推奨) " : "⚠️ High File Count (/MT:32 recommended) ";
                if (string.IsNullOrEmpty(warn)) warn = "-";

                var wCell = ws1.Cell(r1, 9);
                wCell.Value = warn;
                if (warn != "-")
                {
                    wCell.Style.Font.FontColor = XLColor.FromHtml("#B45309"); // Amber
                    wCell.Style.Font.Bold = true;
                }
                wCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                r1++;
            }

            var tbl1 = ws1.Range(hRow1, 2, r1 - 1, 9);
            tbl1.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            tbl1.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            tbl1.SetAutoFilter();
            ws1.Columns(2, 9).AdjustToContents(3, 100);

            // -------------------------------------------------------------
            // Sheet 2: 移行WBS・工程表 (Cutover WBS & Tasks)
            // -------------------------------------------------------------
            var ws2 = wb.Worksheets.Add(isJa ? "2_移行WBS・工程表" : "2_Cutover_WBS");
            ws2.ShowGridLines = true;

            ws2.Cell("B2").Value = isJa ? "FolderMorpher — ベンダー標準 移行作業工程・チェックリスト (WBS)" : "FolderMorpher — Migration Cutover WBS & Checklist";
            ws2.Cell("B2").Style.Font.Bold = true;
            ws2.Cell("B2").Style.Font.FontSize = 15;
            ws2.Cell("B2").Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");

            ws2.Cell("B3").Value = isJa ? "各作業の着手前・完了時にステータスを更新し、実績ログを保管してください。" : "Track tasks and log verification results for each phase.";
            ws2.Cell("B3").Style.Font.FontSize = 10;
            ws2.Cell("B3").Style.Font.FontColor = XLColor.DimGray;

            int hRow2 = 5;
            string[] headers2 = isJa
                ? new[] { "フェーズ", "No", "作業項目名", "実行スクリプト / コマンド", "想定実施時期", "完了条件・確認内容", "進捗状況", "担当者", "実施日時", "備考・ログ結果" }
                : new[] { "Phase", "No", "Task Name", "Script / Command", "Target Window", "Success Criteria", "Status", "Assignee", "Executed At", "Notes & Log Result" };

            for (int col = 0; col < headers2.Length; col++)
            {
                var cell = ws2.Cell(hRow2, col + 2);
                cell.Value = headers2[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            var wbsRows = isJa ? new (string Phase, string No, string Task, string Script, string Window, string Criteria, string Status)[]
            {
                ("事前準備", "0-1", "移行パッケージの配置 & パス確認", "Explorer / コマンドプロンプト", "本番1ヶ月〜2週間前", "作業端末から旧サーバーおよび新環境UNCへ疎通できること", "未着手"),
                ("事前準備", "0-2", "新環境フォルダー構造（スケルトン）展開", "Simulation Studio: スケルトン展開", "本番2週間前", "新環境に空フォルダーツリーが正常作成されていること", "未着手"),
                ("事前準備", "0-3", "新環境アクセス権 (ACL) 先行適用", "Simulation Studio: ACL適用", "本番2週間前", "新環境の各部署・親フォルダーに設計通りACLが付与されていること", "未着手"),
                ("Phase 1", "1-1", "事前フル同期 (Baseline Sync) 実行", "各Wave\\01_Baseline_Sync.bat", "本番2〜3週間前の平日夜間・休日", "全体の約95%以上のファイルが新環境へ転送完了していること", "未着手"),
                ("Phase 1", "1-2", "初回転送ログ確認 & エラー精査", "Logs\\Baseline_*.log", "Phase 1完了直後", "Robocopy終了コードが 0, 1, 2, 3 のいずれかであること（Error 0件）", "未着手"),
                ("Phase 2", "2-1", "中間差分同期 (Delta Sync) 実行", "各Wave\\02_Delta_Sync.bat", "本番3日前〜前日夜間", "初回以降の更新差分が転送され、所要時間が短縮していること", "未着手"),
                ("Phase 2", "2-2", "中間転送ログ確認", "Logs\\Delta_*.log", "Phase 2完了直後", "エラーなく追いついていることを確認", "未着手"),
                ("本番切替", "3-1", "業務終了確認 & 利用者ログオフ促進", "社内アナウンス / 連絡網", "切替当日 業務終了時刻", "旧共有へのアクセスが停止していること", "未着手"),
                ("本番切替", "3-2", "旧共有の安全停止 (Freeze & Lock)", "各Wave\\03_PreCutover_Freeze_Guide.md 参照", "切替当日 業務停止直後", "共有権限またはNTFS拒否により旧フォルダーへの書き込みが停止していること", "未着手"),
                ("Phase 4", "4-1", "最終カットオーバー同期 (/MIR) 実行", "各Wave\\04_Final_Cutover_Mirror.bat", "切替当日 旧共有停止後", "完全同期完了。旧環境の最終差分・削除がミラー反映されること", "未着手"),
                ("Phase 4", "4-2", "最終転送ログ確認", "Logs\\Cutover_*.log", "Phase 4完了直後", "Robocopy終了コード正常。重大エラーがないこと", "未着手"),
                ("検証", "5-1", "新環境共有の導通・権限・書き込み検証", "クライアントPC実機テスト", "切替当日 夜間", "各部署のテストアカウントで想定通りアクセス・保存できること", "未着手"),
                ("完了", "6-1", "新環境サービスイン アナウンス", "全社通知メール / チャット", "切替翌営業日 始業前", "新共有パスでの業務開始案内", "未着手"),
                ("緊急対応", "9-1", "【切戻し時のみ】旧共有の書き込み復旧", "03_PreCutover_Freeze_Guide.md 参照", "切替中止判断時", "旧共有の書き込み権限が元の状態に復旧すること", "未着手")
            } : new (string Phase, string No, string Task, string Script, string Window, string Criteria, string Status)[]
            {
                ("Prep", "0-1", "Deploy Migration Package & Path Check", "Explorer / CMD", "2-4 weeks before cutover", "Verify network connectivity to both Old & New UNC shares", "Not Started"),
                ("Prep", "0-2", "Deploy Skeleton Folder Architecture", "Simulation Studio: Skeleton Deploy", "2 weeks before cutover", "Empty folder trees created on target share", "Not Started"),
                ("Prep", "0-3", "Pre-apply Target ACLs", "Simulation Studio: Apply ACLs", "2 weeks before cutover", "Security permissions configured on new folders as designed", "Not Started"),
                ("Phase 1", "1-1", "Execute Baseline Full Sync", "Each Wave\\01_Baseline_Sync.bat", "2-3 weeks before (night/weekend)", ">95% of data transferred to new share", "Not Started"),
                ("Phase 1", "1-2", "Review Baseline Logs", "Logs\\Baseline_*.log", "Immediately after Phase 1", "Robocopy exit code is 0-3 (No fatal errors)", "Not Started"),
                ("Phase 2", "2-1", "Execute Delta Catch-up Sync", "Each Wave\\02_Delta_Sync.bat", "1-3 days before cutover (night)", "Recent modified files updated swiftly", "Not Started"),
                ("Phase 2", "2-2", "Review Delta Logs", "Logs\\Delta_*.log", "Immediately after Phase 2", "Verify delta synchronization completed without errors", "Not Started"),
                ("Cutover", "3-1", "Confirm Business Close & User Logoff", "Internal Notification", "Cutover Day - Business End", "Ensure no active users are editing files", "Not Started"),
                ("Cutover", "3-2", "Freeze Old Share (Read-Only Lock)", "Refer to Each Wave\\03_PreCutover_Freeze_Guide.md", "Cutover Day - Business End", "Verify write operations are blocked on old shares", "Not Started"),
                ("Phase 4", "4-1", "Execute Final Cutover Mirror (/MIR)", "Each Wave\\04_Final_Cutover_Mirror.bat", "Cutover Day - After Share Lock", "Exact mirror completed within minutes/hours", "Not Started"),
                ("Phase 4", "4-2", "Review Final Cutover Logs", "Logs\\Cutover_*.log", "Immediately after Phase 4", "Robocopy exit code normal", "Not Started"),
                ("Verify", "5-1", "Verify New Share Access & Permissions", "Client PC Testing", "Cutover Night", "Test users verify read/write access per department", "Not Started"),
                ("Complete", "6-1", "Service-In Announcement", "Company-wide Email / Chat", "Next Business Day - Before Opening", "Users resume work using the new file server UNC", "Not Started"),
                ("Rollback", "9-1", "[Rollback Only] Restore Old Share Access", "Refer to 03_PreCutover_Freeze_Guide.md", "If cutover is aborted", "Old shares write permissions restored to original state", "Not Started")
            };

            int r2 = hRow2 + 1;
            foreach (var item in wbsRows)
            {
                ws2.Cell(r2, 2).Value = item.Phase;
                ws2.Cell(r2, 2).Style.Font.Bold = true;
                ws2.Cell(r2, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                ws2.Cell(r2, 3).Value = item.No;
                ws2.Cell(r2, 3).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                ws2.Cell(r2, 4).Value = item.Task;
                ws2.Cell(r2, 5).Value = item.Script;
                ws2.Cell(r2, 6).Value = item.Window;
                ws2.Cell(r2, 7).Value = item.Criteria;

                var stCell = ws2.Cell(r2, 8);
                stCell.Value = item.Status;
                stCell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                ws2.Cell(r2, 9).Value = "";  // 担当者
                ws2.Cell(r2, 10).Value = ""; // 実施日時
                ws2.Cell(r2, 11).Value = ""; // 備考・ログ結果

                r2++;
            }

            var tbl2 = ws2.Range(hRow2, 2, r2 - 1, 11);
            tbl2.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
            tbl2.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
            tbl2.SetAutoFilter();
            ws2.Columns(2, 11).AdjustToContents(3, 100);

            // -------------------------------------------------------------
            // Sheet 3: マッピング・除外詳細 (Mapping & Sync Details)
            // -------------------------------------------------------------
            var ws3 = wb.Worksheets.Add(isJa ? "3_マッピング・除外詳細" : "3_Mapping_Details");
            ws3.ShowGridLines = true;

            ws3.Cell("B2").Value = isJa ? "FolderMorpher — 移行元・先マッピングおよび子孫パス除外 (/XD) 詳細" : "FolderMorpher — Source/Target Mapping & /XD Exclusion Details";
            ws3.Cell("B2").Style.Font.Bold = true;
            ws3.Cell("B2").Style.Font.FontSize = 15;
            ws3.Cell("B2").Style.Font.FontColor = XLColor.FromHtml("#1E3A8A");

            ws3.Cell("B3").Value = isJa ? "新環境フォルダと旧環境フォルダの紐づけ、および多重コピー防止のために自動除外される配下フォルダ一覧です。" : "Detailed list of mapped source paths and automatically excluded descendant folders (/XD).";
            ws3.Cell("B3").Style.Font.FontSize = 10;
            ws3.Cell("B3").Style.Font.FontColor = XLColor.DimGray;

            int hRow3 = 5;
            string[] headers3 = isJa
                ? new[] { "Wave", "移行先 新フォルダ (Target)", "新フォルダ階層", "移行元 旧フォルダ (Source)", "多重コピー除外対象 (/XD 子孫パス)", "フォルダ容量", "想定ファイル数" }
                : new[] { "Wave", "Target Folder", "Target Relative Path", "Mapped Source Path", "Excluded Descendants (/XD)", "Folder Size", "Est. Files" };

            for (int col = 0; col < headers3.Length; col++)
            {
                var cell = ws3.Cell(hRow3, col + 2);
                cell.Value = headers3[col];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E3A8A");
                cell.Style.Font.FontColor = XLColor.White;
                cell.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            }

            int r3 = hRow3 + 1;
            foreach (var wave in wavePlans)
            {
                foreach (var node in wave.TargetNodes)
                {
                    void WriteNodeMapping(SimFolderNode n)
                    {
                        if (n.MappedSourcePaths.Count > 0)
                        {
                            foreach (var src in n.MappedSourcePaths)
                            {
                                ws3.Cell(r3, 2).Value = $"Wave {wave.WaveNumber}";
                                ws3.Cell(r3, 2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;

                                ws3.Cell(r3, 3).Value = n.Name;
                                ws3.Cell(r3, 4).Value = n.RelativePath;

                                var srcCell = ws3.Cell(r3, 5);
                                srcCell.Value = src;
                                try
                                {
                                    string linkUri = "file:///" + src.Replace('\\', '/');
                                    srcCell.SetHyperlink(new XLHyperlink(linkUri));
                                    srcCell.Style.Font.FontColor = XLColor.FromHtml("#2563EB");
                                    srcCell.Style.Font.Underline = XLFontUnderlineValues.Single;
                                }
                                catch { }

                                // Descendants mapped under this src
                                var xdList = new List<string>();
                                CollectDescendantsForExcel(n, xdList);
                                ws3.Cell(r3, 6).Value = xdList.Count > 0 ? string.Join(" ; ", xdList) : "-";

                                ws3.Cell(r3, 7).Value = FormatHelper.FormatBytes(n.EstimatedSizeBytes);
                                ws3.Cell(r3, 7).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                                ws3.Cell(r3, 8).Value = Math.Max(1, n.EstimatedSizeBytes / (10L * 1024 * 1024));
                                ws3.Cell(r3, 8).Style.NumberFormat.Format = "#,##0";
                                ws3.Cell(r3, 8).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                                r3++;
                            }
                        }
                        foreach (var c in n.Children)
                        {
                            WriteNodeMapping(c);
                        }
                    }

                    WriteNodeMapping(node);
                }
            }

            if (r3 > hRow3 + 1)
            {
                var tbl3 = ws3.Range(hRow3, 2, r3 - 1, 8);
                tbl3.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                tbl3.Style.Border.InsideBorder = XLBorderStyleValues.Thin;
                tbl3.SetAutoFilter();
                ws3.Columns(2, 8).AdjustToContents(3, 100);
            }

            wb.SaveAs(outputPath);
        }

        private static void CollectDescendantsForExcel(SimFolderNode node, List<string> list)
        {
            foreach (var child in node.Children)
            {
                foreach (var s in child.MappedSourcePaths)
                {
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
                }
                CollectDescendantsForExcel(child, list);
            }
        }
    }
}
