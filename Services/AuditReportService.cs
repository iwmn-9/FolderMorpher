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

            // 1. ファイル列挙（SafeFileEnumerator に一本化）
            var coverage = new ScanCoverage();
            summary.Coverage = coverage;
            progress?.Report(new AuditProgress { CurrentStatus = "ファイル一覧を走査中...", ScannedFilesCount = 0, IssueCount = 0 });

            await Task.Run(() =>
            {
                int scanned = 0;
                foreach (var file in SafeFileEnumerator.EnumerateFilesSafe(options.TargetDirectory, "*.*", coverage, ct))
                {
                    scannedFiles.Add(file);
                    scanned++;
                    if (scanned % 100 == 0)
                    {
                        string status = coverage.AccessDeniedFolders > 0
                            ? $"ファイル走査中 ({scanned:N0} 件 / ⚠️アクセス拒否: {coverage.AccessDeniedFolders} 箇所)..."
                            : $"ファイル走査中 ({scanned:N0} 件)...";
                        progress?.Report(new AuditProgress { CurrentStatus = status, ScannedFilesCount = scanned });
                    }
                }
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
                        var groupNum = dupGroupIndex++;
                        var groupId = $"DUP-{groupNum:D4}";
                        // 原本候補を賢く選定（スコアリングソート）
                        // 1. ファイル名に「コピー」「copy」「(1)」「_backup」等が含まれていないもの優先
                        // 2. ディレクトリ階層が浅い（パス区切り文字が少ない＝ルートに近い）もの優先
                        // 3. パス文字列長が短いもの優先
                        // 4. 作成日時が古いもの優先
                        // 5. 更新日時が古いもの優先
                        var fileList = kvp.Value
                            .OrderBy(f => HasCopyKeywords(f.Name) ? 1 : 0)
                            .ThenBy(f => f.FullName.Count(c => c == '\\' || c == '/'))
                            .ThenBy(f => f.FullName.Length)
                            .ThenBy(f => f.CreationTimeUtc)
                            .ThenBy(f => f.LastWriteTimeUtc)
                            .ToList();

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
                                DuplicateGroupId = groupId,
                                DuplicateGroupIndex = groupNum,
                                DuplicateGroupColorIndex = (groupNum - 1) % AuditItem.GroupBgPalette.Length,
                                IsOriginalCandidate = isOriginal
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

            // 重複ファイルをグループ順・原本優先でソートし、視認性と色分けの並びを完璧にする
            items = SortAuditItems(items, "Default", false);

            progress?.Report(new AuditProgress { CurrentStatus = "監査完了", ScannedFilesCount = summary.TotalFilesScanned, IssueCount = items.Count });
            return (summary, items);
        }

        /// <summary>
        /// 監査アイテム一覧を指定列で階層ソートする。
        /// 【重要】容量ソート時、重複グループは同一セットとして固まり、原本候補が必ず先頭に配置される。
        /// </summary>
        public static List<AuditItem> SortAuditItems(IEnumerable<AuditItem> source, string sortProperty, bool descending)
        {
            var list = source.ToList();
            if (list.Count <= 1) return list;

            switch (sortProperty)
            {
                case "Size":
                    // 容量ソート:
                    // 1. ファイルサイズ
                    // 2. 重複グループ（DUP-0001等）で必ず1つに束ねる
                    // 3. グループ内では原本候補が必ず最優先（先頭）
                    // 4. ファイル名順
                    return descending
                        ? list.OrderByDescending(x => x.Size)
                              .ThenBy(x => x.DuplicateGroupIndex > 0 ? x.DuplicateGroupIndex : int.MaxValue)
                              .ThenByDescending(x => x.IsOriginalCandidate)
                              .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                              .ToList()
                        : list.OrderBy(x => x.Size)
                              .ThenBy(x => x.DuplicateGroupIndex > 0 ? x.DuplicateGroupIndex : int.MaxValue)
                              .ThenByDescending(x => x.IsOriginalCandidate)
                              .ThenBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                              .ToList();

                case "LastWriteTime":
                    return descending
                        ? list.OrderByDescending(x => x.LastWriteTime)
                              .ThenBy(x => x.DuplicateGroupIndex > 0 ? x.DuplicateGroupIndex : int.MaxValue)
                              .ThenByDescending(x => x.IsOriginalCandidate)
                              .ToList()
                        : list.OrderBy(x => x.LastWriteTime)
                              .ThenBy(x => x.DuplicateGroupIndex > 0 ? x.DuplicateGroupIndex : int.MaxValue)
                              .ThenByDescending(x => x.IsOriginalCandidate)
                              .ToList();

                case "FileName":
                    return descending
                        ? list.OrderByDescending(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                              .ThenBy(x => x.DuplicateGroupIndex > 0 ? x.DuplicateGroupIndex : int.MaxValue)
                              .ThenByDescending(x => x.IsOriginalCandidate)
                              .ToList()
                        : list.OrderBy(x => x.FileName, StringComparer.OrdinalIgnoreCase)
                              .ThenBy(x => x.DuplicateGroupIndex > 0 ? x.DuplicateGroupIndex : int.MaxValue)
                              .ThenByDescending(x => x.IsOriginalCandidate)
                              .ToList();

                case "IssueType":
                    return descending
                        ? list.OrderByDescending(x => x.IssueType)
                              .ThenBy(x => x.DuplicateGroupIndex > 0 ? x.DuplicateGroupIndex : int.MaxValue)
                              .ThenByDescending(x => x.IsOriginalCandidate)
                              .ToList()
                        : list.OrderBy(x => x.IssueType)
                              .ThenBy(x => x.DuplicateGroupIndex > 0 ? x.DuplicateGroupIndex : int.MaxValue)
                              .ThenByDescending(x => x.IsOriginalCandidate)
                              .ToList();

                case "DuplicateGroupIndex":
                    return descending
                        ? list.OrderByDescending(x => x.DuplicateGroupIndex)
                              .ThenByDescending(x => x.IsOriginalCandidate)
                              .ToList()
                        : list.OrderBy(x => x.DuplicateGroupIndex > 0 ? x.DuplicateGroupIndex : int.MaxValue)
                              .ThenByDescending(x => x.IsOriginalCandidate)
                              .ToList();

                case "FullPath":
                    return descending
                        ? list.OrderByDescending(x => x.FullPath, StringComparer.OrdinalIgnoreCase).ToList()
                        : list.OrderBy(x => x.FullPath, StringComparer.OrdinalIgnoreCase).ToList();

                default:
                    // デフォルト表示（重複グループ優先、グループ順、原本先頭）
                    return list
                        .OrderBy(it => it.IssueType == AuditIssueType.Duplicate ? 0 : 1)
                        .ThenBy(it => it.DuplicateGroupIndex > 0 ? it.DuplicateGroupIndex : int.MaxValue)
                        .ThenByDescending(it => it.IsOriginalCandidate)
                        .ThenBy(it => it.FileName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
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

        public void GenerateArchiveRobocopyScript(
            string scriptPath,
            IEnumerable<AuditItem> items,
            string targetRoot,
            string archiveDestinationRoot,
            bool includeDuplicates = true)
        {
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 > nul");
            sb.AppendLine("rem ========================================================");
            sb.AppendLine("rem FolderMorpher - 休眠・重複ファイル安全退避スクリプト");
            sb.AppendLine($"rem 元パス: {targetRoot}");
            sb.AppendLine($"rem 退避先: {archiveDestinationRoot}");
            sb.AppendLine($"rem 重複ファイルの退避: {(includeDuplicates ? "有効（原本候補以外）" : "無効（休眠ファイルのみ退避）")}");
            sb.AppendLine($"rem 作成日時: {DateTime.Now:yyyy/MM/dd HH:mm:ss}");
            sb.AppendLine("rem ========================================================");
            sb.AppendLine();
            sb.AppendLine("echo [1/2] 退避ディレクトリ構造の準備中...");
            sb.AppendLine();

            // 1. 原本候補のパスを聖域として抽出（絶対に退避させない）
            // プロパティ IsOriginalCandidate を主たる判定根拠とし、互換性のため [原本候補] 文字列も多層防御でサポート
            var originalFilePaths = new HashSet<string>(
                items.Where(i => i.IsOriginalCandidate || (i.Detail != null && i.Detail.Contains("[原本候補]")))
                     .Select(i => i.FullPath),
                StringComparer.OrdinalIgnoreCase);

            // 2. 退避対象アイテムの抽出：
            //    - 原本候補（IsOriginalCandidate == true）は休眠判定されていても絶対に除外
            //    - includeDuplicates == true の場合のみ、原本候補以外の重複ファイルを退避対象に含める
            //    - 同一ファイルが休眠と重複の両方に該当しても FullPath で確実に1件に重複排除
            var archiveItems = items
                .Where(i =>
                {
                    if (i.IsOriginalCandidate || originalFilePaths.Contains(i.FullPath)) return false;
                    if (i.IssueType == AuditIssueType.Dormant) return true;
                    if (includeDuplicates && i.IssueType == AuditIssueType.Duplicate) return true;
                    return false;
                })
                .GroupBy(i => i.FullPath, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            string modeDesc = includeDuplicates
                ? "休眠ファイル ＋ 重複ファイル（原本候補は保護）"
                : "休眠ファイル（3年以上未更新）のみ";
            sb.AppendLine($"echo 退避モード: {modeDesc}");
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

        private static readonly string[] CopyKeywords = {
            "コピー", "copy", "複写", "バックアップ", "backup", "bak", "複製", "復元", "restore", "最新", "old", "new", "編集"
        };

        private static bool HasCopyKeywords(string fileName)
        {
            var name = fileName.ToLowerInvariant();
            if (CopyKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase))) return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(name, @"[\(_\- ]\d+[\)]")) return true;
            return false;
        }

        private static string EscapeCsv(string s) => s.Replace("\"", "\"\"");
    }
}
