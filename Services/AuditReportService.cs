using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FolderMorpher.Models;

namespace FolderMorpher.Services
{
    public class AuditReportService
    {
        // クラウド（SharePoint/Box）およびNTFSで問題になる禁則文字
        private static readonly char[] InvalidChars = new[] { '*', ':', '<', '>', '?', '/', '\\', '|', '"', '#', '%', '{', '}', '~', '&' };

        public async Task<(AuditSummary Summary, List<AuditItem> Items)> RunAuditAsync(
            AuditOptions options,
            IProgress<AuditProgress>? progress,
            CancellationToken ct)
        {
            var summary = new AuditSummary();
            var items = new List<AuditItem>();
            var scannedFiles = new List<FileInfo>();

            if (string.IsNullOrWhiteSpace(options.TargetDirectory) || !Directory.Exists(options.TargetDirectory))
            {
                throw new DirectoryNotFoundException($"対象ディレクトリが見つかりません: {options.TargetDirectory}");
            }

            // 1. ファイル列挙
            progress?.Report(new AuditProgress { CurrentStatus = "ファイル一覧を走査中...", ScannedFilesCount = 0, IssueCount = 0 });

            await Task.Run(() =>
            {
                EnumerateFilesSafe(new DirectoryInfo(options.TargetDirectory), scannedFiles, progress, ct);
            }, ct);

            summary.TotalFilesScanned = scannedFiles.Count;
            long processedCount = 0;

            // 2. パス長・禁則文字・休眠のチェック
            var now = DateTime.Now;
            var dormantCutoff = now.AddDays(-365.25 * options.DormantYearsThreshold);

            progress?.Report(new AuditProgress { CurrentStatus = "パス長・休眠ファイルを点検中...", ScannedFilesCount = summary.TotalFilesScanned, IssueCount = items.Count });

            foreach (var fi in scannedFiles)
            {
                ct.ThrowIfCancellationRequested();
                processedCount++;

                // パス長チェック (>= 260)
                if (options.CheckPathLimits && fi.FullName.Length >= 260)
                {
                    summary.PathTooLongCount++;
                    items.Add(new AuditItem
                    {
                        FullPath = fi.FullName,
                        FileName = fi.Name,
                        DirectoryPath = fi.DirectoryName ?? string.Empty,
                        Size = fi.Length,
                        LastWriteTime = fi.LastWriteTime,
                        LastAccessTime = fi.LastAccessTime,
                        IssueType = AuditIssueType.PathTooLong,
                        Detail = $"文字数: {fi.FullName.Length} 文字 (上限260文字)"
                    });
                }

                // 禁則文字チェック
                if (options.CheckPathLimits)
                {
                    var invalidInName = fi.Name.Where(c => InvalidChars.Contains(c)).Distinct().ToArray();
                    if (invalidInName.Length > 0)
                    {
                        summary.InvalidCharCount++;
                        items.Add(new AuditItem
                        {
                            FullPath = fi.FullName,
                            FileName = fi.Name,
                            DirectoryPath = fi.DirectoryName ?? string.Empty,
                            Size = fi.Length,
                            LastWriteTime = fi.LastWriteTime,
                            LastAccessTime = fi.LastAccessTime,
                            IssueType = AuditIssueType.InvalidChar,
                            Detail = $"移行禁則文字を含む: {string.Join(" ", invalidInName)}"
                        });
                    }
                }

                // 休眠ファイルチェック (指定年数以上前)
                if (options.CheckDormant && fi.LastWriteTime < dormantCutoff)
                {
                    var yearsOld = (now - fi.LastWriteTime).TotalDays / 365.25;
                    summary.DormantCount++;
                    summary.DormantBytes += fi.Length;
                    items.Add(new AuditItem
                    {
                        FullPath = fi.FullName,
                        FileName = fi.Name,
                        DirectoryPath = fi.DirectoryName ?? string.Empty,
                        Size = fi.Length,
                        LastWriteTime = fi.LastWriteTime,
                        LastAccessTime = fi.LastAccessTime,
                        IssueType = AuditIssueType.Dormant,
                        Detail = $"最終更新: {fi.LastWriteTime:yyyy/MM/dd} ({yearsOld:F1}年前)"
                    });
                }

                if (processedCount % 500 == 0)
                {
                    progress?.Report(new AuditProgress
                    {
                        CurrentStatus = $"監査中... ({processedCount}/{scannedFiles.Count})",
                        ScannedFilesCount = processedCount,
                        IssueCount = items.Count
                    });
                }
            }

            // 3. 重複ファイルチェック（SHA256ハッシュ判定）
            if (options.CheckDuplicates)
            {
                progress?.Report(new AuditProgress { CurrentStatus = "重複ファイルを分析中 (サイズ絞り込み)...", ScannedFilesCount = summary.TotalFilesScanned, IssueCount = items.Count });

                // サイズでグルーピング（100KB以上かつ同一サイズが2個以上あるもの）
                var candidateGroups = scannedFiles
                    .Where(f => f.Length >= options.MinFileSizeBytes)
                    .GroupBy(f => f.Length)
                    .Where(g => g.Count() > 1)
                    .ToList();

                int dupGroupIndex = 1;
                foreach (var group in candidateGroups)
                {
                    ct.ThrowIfCancellationRequested();
                    var hashToFiles = new Dictionary<string, List<FileInfo>>();

                    foreach (var file in group)
                    {
                        ct.ThrowIfCancellationRequested();
                        var hash = await ComputeSha256Async(file.FullName, ct);
                        if (string.IsNullOrEmpty(hash)) continue;

                        if (!hashToFiles.ContainsKey(hash))
                            hashToFiles[hash] = new List<FileInfo>();
                        hashToFiles[hash].Add(file);
                    }

                    // 同一ハッシュが複数あれば重複確定
                    foreach (var kvp in hashToFiles.Where(k => k.Value.Count > 1))
                    {
                        var groupId = $"DUP-{dupGroupIndex++:D4}";
                        var fileList = kvp.Value;

                        // 最初以外のファイルを「重複による無駄（Wasted）」として集計
                        for (int i = 0; i < fileList.Count; i++)
                        {
                            var fi = fileList[i];
                            var isOriginal = (i == 0);
                            if (!isOriginal)
                            {
                                summary.DuplicateCount++;
                                summary.DuplicateWastedBytes += fi.Length;
                            }

                            items.Add(new AuditItem
                            {
                                FullPath = fi.FullName,
                                FileName = fi.Name,
                                DirectoryPath = fi.DirectoryName ?? string.Empty,
                                Size = fi.Length,
                                LastWriteTime = fi.LastWriteTime,
                                LastAccessTime = fi.LastAccessTime,
                                IssueType = AuditIssueType.Duplicate,
                                Detail = isOriginal ? $"[原本候補] ハッシュ: {kvp.Key[..12]}..." : $"[重複] ハッシュ: {kvp.Key[..12]}...",
                                Sha256Hash = kvp.Key,
                                DuplicateGroupId = groupId
                            });
                        }
                    }

                    progress?.Report(new AuditProgress
                    {
                        CurrentStatus = $"重複ハッシュ計算中 (グループ {dupGroupIndex})...",
                        ScannedFilesCount = summary.TotalFilesScanned,
                        IssueCount = items.Count
                    });
                }
            }

            progress?.Report(new AuditProgress { CurrentStatus = "監査完了", ScannedFilesCount = summary.TotalFilesScanned, IssueCount = items.Count });
            return (summary, items);
        }

        private static void EnumerateFilesSafe(DirectoryInfo rootDir, List<FileInfo> result, IProgress<AuditProgress>? progress, CancellationToken ct)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(rootDir);

            int scanned = 0;
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var currentDir = stack.Pop();

                // 1. サブディレクトリ探索（アクセス拒否やジャンクションは安全にスキップ）
                try
                {
                    foreach (var sub in currentDir.GetDirectories())
                    {
                        try
                        {
                            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        }
                        catch { }
                        stack.Push(sub);
                    }
                }
                catch { }

                // 2. カレントディレクトリ内のファイル取得
                try
                {
                    var files = currentDir.GetFiles();
                    result.AddRange(files);
                    scanned += files.Length;
                    if (scanned % 100 == 0)
                    {
                        progress?.Report(new AuditProgress { CurrentStatus = $"ファイル走査中 ({scanned} 件)...", ScannedFilesCount = scanned });
                    }
                }
                catch { }
            }
        }

        private static async Task<string?> ComputeSha256Async(string filePath, CancellationToken ct)
        {
            try
            {
                using var sha256 = SHA256.Create();
                await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 65536, true);
                var hashBytes = await sha256.ComputeHashAsync(stream, ct);
                return Convert.ToHexString(hashBytes).ToLowerInvariant();
            }
            catch
            {
                return null; // ロック中やアクセス権なしはスキップ
            }
        }

        public void ExportAuditCsv(string filePath, IEnumerable<AuditItem> items)
        {
            var sb = new StringBuilder();
            sb.AppendLine("問題種別,ファイル名,容量,最終更新日時,最終アクセス日時,詳細,重複グループ,完全パス");

            foreach (var item in items)
            {
                sb.AppendLine($"\"{EscapeCsv(item.IssueTypeDisplay)}\"," +
                              $"\"{EscapeCsv(item.FileName)}\"," +
                              $"\"{EscapeCsv(item.SizeFormatted)}\"," +
                              $"\"{item.LastWriteTime:yyyy/MM/dd HH:mm:ss}\"," +
                              $"\"{item.LastAccessTime:yyyy/MM/dd HH:mm:ss}\"," +
                              $"\"{EscapeCsv(item.Detail)}\"," +
                              $"\"{EscapeCsv(item.DuplicateGroupId)}\"," +
                              $"\"{EscapeCsv(item.FullPath)}\"");
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        public void GenerateArchiveRobocopyScript(string scriptPath, IEnumerable<AuditItem> items, string targetRoot, string archiveDestinationRoot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 > nul");
            sb.AppendLine("rem ========================================================");
            sb.AppendLine("rem FolderMorpher - 休眠・重複ファイル安全退避スクリプト");
            sb.AppendLine($"rem 元パス: {targetRoot}");
            sb.AppendLine($"rem 退避先: {archiveDestinationRoot}");
            sb.AppendLine($"rem 作成日時: {DateTime.Now:yyyy/MM/dd HH:mm:ss}");
            sb.AppendLine("rem ========================================================");
            sb.AppendLine();
            sb.AppendLine("echo [1/2] 退避ディレクトリ構造の準備中...");
            sb.AppendLine();

            // 1. 重複グループの「原本候補」のパスを聖域として抽出（絶対に退避させない）
            var originalFilePaths = new HashSet<string>(
                items.Where(i => i.IssueType == AuditIssueType.Duplicate && i.Detail.Contains("[原本候補]"))
                     .Select(i => i.FullPath),
                StringComparer.OrdinalIgnoreCase);

            // 2. 退避対象アイテムの抽出：
            //    - 原本候補は休眠判定されていても絶対に除外
            //    - 同一ファイルが休眠と重複の両方に該当しても FullPath で確実に1件に重複排除
            var archiveItems = items
                .Where(i => (i.IssueType == AuditIssueType.Dormant || i.IssueType == AuditIssueType.Duplicate)
                            && !originalFilePaths.Contains(i.FullPath)
                            && !i.Detail.Contains("[原本候補]"))
                .GroupBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            sb.AppendLine($"echo 対象ファイル数: {archiveItems.Count} 件 (※重複原本は安全保護のため現場に残されます)");
            sb.AppendLine("pause");
            sb.AppendLine();

            foreach (var item in archiveItems)
            {
                // 相対パスを計算
                string relPath = Path.GetRelativePath(targetRoot, item.FullPath);
                string destFile = Path.Combine(archiveDestinationRoot, relPath);
                string? destDir = Path.GetDirectoryName(destFile);

                if (!string.IsNullOrEmpty(destDir))
                {
                    sb.AppendLine($"if not exist \"{destDir}\" mkdir \"{destDir}\"");
                }
                sb.AppendLine($"move /Y \"{item.FullPath}\" \"{destFile}\"");
            }

            sb.AppendLine();
            sb.AppendLine("echo [2/2] 退避完了。元のファイルサーバーから安全に移動されました。");
            sb.AppendLine("pause");

            File.WriteAllText(scriptPath, sb.ToString(), Encoding.UTF8);
        }

        private static string EscapeCsv(string s) => s.Replace("\"", "\"\"");
    }
}
