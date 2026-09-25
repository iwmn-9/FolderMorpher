using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FolderMorpher.Models;

namespace FolderMorpher.Services
{
    /// <summary>
    /// インテリジェント整理候補発見エンジン (Smart Hygiene Discovery Engine)
    /// 「不要と決めつけず、理由を添えて整理候補を発見する」
    /// 1. 世代・旧版ファイル検出 (Version / Family)
    /// 2. 展開済みアーカイブ検出 (Extracted Archive Shadow)
    /// 3. 墓場フォルダー判定 (Graveyard / Ghost Tree)
    /// </summary>
    public static class HygieneCandidateEngine
    {
        private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".zip", ".7z", ".tar.gz", ".tgz", ".tar", ".rar", ".lzh", ".bz2", ".xz"
        };

        // 世代サフィックス除去用正規表現パターン
        private static readonly Regex VersionTokenRegex = new(
            @"([_\s\-\.](v\d+(\.\d+)*|ver\d*|rev\d*|修正版?|\d+|最新版?|本当(の最終)?|最終版?|確定版?|提出用?|案\d*|fix|draft|old|bak|copy|コピー|\(\d+\)))+$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex DateStampRegex = new(
            @"([_\s\-\.]\d{4}[._\-]?\d{2}[._\-]?\d{2}|[_\s\-\.]\d{6,8})$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly Regex CopySuffixRegex = new(
            @"(\s*-\s*コピー(\s*\(\d+\))?)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private static readonly string[] GraveyardKeywords = new[]
        {
            "_old", "_bak", "_backup", "_退避", "_過去", "_旧", "archive", "temp", "-old", "-bak", "old_"
        };

        /// <summary>
        /// 走査済みファイル一覧から、3大整理候補（世代・旧版、展開済ZIP、墓場フォルダー）を検出します。
        /// </summary>
        public static List<AuditItem> DiscoverCandidates(
            IReadOnlyList<ScannedFileEntry> scannedFiles,
            DateTime now,
            double dormantYearsThreshold = 3.0,
            bool checkVersionFamilies = true,
            bool checkExtractedArchives = true,
            bool checkGraveyardTrees = true,
            string? targetRoot = null)
        {
            var results = new List<AuditItem>();
            if (scannedFiles == null || scannedFiles.Count == 0) return results;

            var dormantCutoff = now.AddDays(-365.25 * dormantYearsThreshold);
            var recentAccessCutoff = now.AddDays(-365.25);

            // ディレクトリ別にファイルをグループ化 (O(N))
            var dirMap = new Dictionary<string, List<ScannedFileEntry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in scannedFiles)
            {
                if (!dirMap.TryGetValue(f.DirectoryPath, out var list))
                {
                    list = new List<ScannedFileEntry>();
                    dirMap[f.DirectoryPath] = list;
                }
                list.Add(f);
            }

            // 1. 世代・旧版ファイル検出 (Version Family)
            if (checkVersionFamilies)
            {
                DetectVersionFamilies(dirMap, results, now);
            }

            // 展開済アーカイブ・墓場フォルダー判定用の Bottom-Up 集計ツリーを O(N) で1度だけ構築
            Dictionary<string, FolderAggInfo>? aggMap = null;
            if (checkExtractedArchives || checkGraveyardTrees)
            {
                aggMap = BuildFolderAggregationMap(dirMap, recentAccessCutoff, now, targetRoot);
            }

            // 2. 展開済みアーカイブ残骸検出 (Extracted Archive Shadow) - O(N)
            if (checkExtractedArchives && aggMap != null)
            {
                DetectExtractedArchives(dirMap, aggMap, results);
            }

            // 3. 墓場フォルダー判定 (Graveyard Trees) - O(N)
            if (checkGraveyardTrees && aggMap != null)
            {
                DetectGraveyardTrees(aggMap, results, dormantCutoff, now, targetRoot);
            }

            return results;
        }

        #region 1. 世代・旧版ファイル検出

        private static void DetectVersionFamilies(
            Dictionary<string, List<ScannedFileEntry>> dirMap,
            List<AuditItem> results,
            DateTime now)
        {
            foreach (var (dirPath, files) in dirMap)
            {
                if (files.Count < 2) continue;

                // 同一ディレクトリ内で FamilyKey ごとにグループ化
                var familyMap = new Dictionary<string, List<(ScannedFileEntry File, string Suffix)>>(StringComparer.OrdinalIgnoreCase);
                foreach (var f in files)
                {
                    var (familyKey, suffix) = ExtractFamilyKey(f.Name);
                    if (!familyMap.TryGetValue(familyKey, out var list))
                    {
                        list = new List<(ScannedFileEntry File, string Suffix)>();
                        familyMap[familyKey] = list;
                    }
                    list.Add((f, suffix));
                }

                foreach (var (familyKey, familyMembers) in familyMap)
                {
                    // 2件以上あり、かつ少なくとも1件に世代サフィックスが付与されている場合
                    if (familyMembers.Count < 2) continue;
                    bool hasSuffixes = familyMembers.Any(m => !string.IsNullOrEmpty(m.Suffix));
                    if (!hasSuffixes) continue;

                    // 最新版 (Active) を決定: LastWriteTime が最も新しいもの
                    var sorted = familyMembers.OrderByDescending(m => m.File.LastWriteTime).ToList();
                    var active = sorted.First().File;

                    // それ以外の過去版を整理候補として抽出
                    foreach (var (cand, suffix) in sorted.Skip(1))
                    {
                        // 最新版と全く同じ更新日時の場合は重複の可能性があるのでスキップ (重複チェックに任せる)
                        if (cand.LastWriteTime == active.LastWriteTime && cand.Length == active.Length)
                            continue;

                        int score = 75; // 基礎点
                        var breakdown = new List<ScoreFactorItem>
                        {
                            new ScoreFactorItem { NameJa = "過去バージョン判定 (基礎点)", NameEn = "Older Version Baseline", Points = 75 }
                        };

                        if (!string.IsNullOrEmpty(suffix))
                        {
                            score += 10;
                            breakdown.Add(new ScoreFactorItem { NameJa = $"世代サフィックス検出 ({suffix})", NameEn = $"Version suffix detected ({suffix})", Points = 10 });
                        }
                        if ((now - cand.LastWriteTime).TotalDays > 365)
                        {
                            score += 10;
                            breakdown.Add(new ScoreFactorItem { NameJa = "1年以上未更新", NameEn = "Unmodified for >1 year", Points = 10 });
                        }
                        if (score > 95) score = 95;

                        bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                        string suffixText = string.IsNullOrEmpty(suffix) ? "" : $" / サフィックス: {suffix}";
                        string detail = isJa
                            ? $"最新版あり: {active.Name} ({active.LastWriteTime:yyyy/MM/dd}){suffixText}"
                            : $"Newer version exists: {active.Name} ({active.LastWriteTime:yyyy/MM/dd}){suffixText}";

                        results.Add(new AuditItem
                        {
                            FullPath = cand.FullPath,
                            FileName = cand.Name,
                            DirectoryPath = cand.DirectoryPath,
                            Size = cand.Length,
                            LastWriteTime = cand.LastWriteTime,
                            LastAccessTime = cand.LastAccessTime,
                            IssueType = AuditIssueType.VersionFamily,
                            Detail = detail,
                            WasteScore = score,
                            ScoreBreakdown = breakdown,
                            RelatedActivePath = active.FullPath
                        });
                    }
                }
            }
        }

        public static (string FamilyKey, string Suffix) ExtractFamilyKey(string fileName)
        {
            string ext = Path.GetExtension(fileName);
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string origBase = baseName;
            var suffixes = new List<string>();

            // 最大5回まで末尾のサフィックス（コピー、日付、バージョン）を順次剥ぎ取る
            for (int iter = 0; iter < 5; iter++)
            {
                bool changed = false;

                // 1. コピーサフィックス除去
                var copyMatch = CopySuffixRegex.Match(baseName);
                if (copyMatch.Success)
                {
                    string matched = copyMatch.Value.Trim(' ', '-', '_');
                    if (!string.IsNullOrEmpty(matched)) suffixes.Add(matched);
                    baseName = baseName.Substring(0, copyMatch.Index);
                    changed = true;
                }

                // 2. 日付スタンプ除去
                var dateMatch = DateStampRegex.Match(baseName);
                if (dateMatch.Success)
                {
                    string matched = dateMatch.Value.Trim(' ', '-', '_');
                    if (!string.IsNullOrEmpty(matched)) suffixes.Add(matched);
                    baseName = baseName.Substring(0, dateMatch.Index);
                    changed = true;
                }

                // 3. バージョン・世代トークン除去
                var verMatch = VersionTokenRegex.Match(baseName);
                if (verMatch.Success)
                {
                    string matched = verMatch.Value.Trim(' ', '-', '_');
                    if (!string.IsNullOrEmpty(matched)) suffixes.Add(matched);
                    baseName = baseName.Substring(0, verMatch.Index);
                    changed = true;
                }

                baseName = baseName.TrimEnd(' ', '_', '-');
                if (!changed || string.IsNullOrEmpty(baseName)) break;
            }

            if (string.IsNullOrEmpty(baseName))
            {
                return (origBase.ToLowerInvariant() + ext.ToLowerInvariant(), string.Empty);
            }

            string combinedSuffix = string.Join(" ", suffixes);
            return (baseName.ToLowerInvariant() + ext.ToLowerInvariant(), combinedSuffix);
        }

        #endregion

        #region 2. 展開済みアーカイブ検出 ＆ 3. 墓場フォルダー判定 (Bottom-Up O(N) 集約)

        private sealed class FolderAggInfo
        {
            public string Path { get; set; } = string.Empty;
            public int DirectFileCount { get; set; }
            public long DirectBytes { get; set; }
            public DateTime DirectMaxWrite { get; set; } = DateTime.MinValue;
            public DateTime DirectMaxAccess { get; set; } = DateTime.MinValue;
            public bool DirectHasRecentAccess { get; set; }

            // 配下全集計（自身＋子孫フォルダー）
            public int TotalFileCount { get; set; }
            public long TotalBytes { get; set; }
            public DateTime TotalMaxWrite { get; set; } = DateTime.MinValue;
            public DateTime TotalMaxAccess { get; set; } = DateTime.MinValue;
            public bool TotalHasRecentAccess { get; set; }
        }

        private static Dictionary<string, FolderAggInfo> BuildFolderAggregationMap(
            Dictionary<string, List<ScannedFileEntry>> dirMap,
            DateTime recentAccessCutoff,
            DateTime now,
            string? targetRoot = null)
        {
            var aggMap = new Dictionary<string, FolderAggInfo>(StringComparer.OrdinalIgnoreCase);
            string? normTarget = string.IsNullOrEmpty(targetRoot) ? null : targetRoot.TrimEnd('\\', '/');

            // 1. 各ディレクトリの直下ファイル情報を算出 (O(N))
            foreach (var (dirPath, files) in dirMap)
            {
                var normPath = dirPath.TrimEnd('\\', '/');
                if (!aggMap.TryGetValue(normPath, out var info))
                {
                    info = new FolderAggInfo { Path = normPath };
                    aggMap[normPath] = info;
                }

                info.DirectFileCount = files.Count;
                long bytes = 0;
                DateTime maxWrite = DateTime.MinValue;
                DateTime maxAccess = DateTime.MinValue;
                bool hasRecent = false;

                for (int i = 0; i < files.Count; i++)
                {
                    var f = files[i];
                    bytes += f.Length;
                    if (f.LastWriteTime > maxWrite) maxWrite = f.LastWriteTime;
                    if (f.LastAccessTime > maxAccess) maxAccess = f.LastAccessTime;
                    if (!hasRecent && f.LastAccessTime >= recentAccessCutoff && f.LastAccessTime <= now.AddDays(1))
                    {
                        hasRecent = true;
                    }
                }

                info.DirectBytes = bytes;
                info.DirectMaxWrite = maxWrite;
                info.DirectMaxAccess = maxAccess;
                info.DirectHasRecentAccess = hasRecent;

                // 初期値は直下ファイル集計
                info.TotalFileCount = info.DirectFileCount;
                info.TotalBytes = info.DirectBytes;
                info.TotalMaxWrite = info.DirectMaxWrite;
                info.TotalMaxAccess = info.DirectMaxAccess;
                info.TotalHasRecentAccess = info.DirectHasRecentAccess;

                // 親ディレクトリもツリー上に存在することを保証（空の親ディレクトリ救済）
                // 【ADR 91】targetRoot より上位には絶対に遡らない（Root境界保護）
                string? parent = System.IO.Path.GetDirectoryName(normPath);
                while (!string.IsNullOrEmpty(parent))
                {
                    if (normTarget != null && !parent.StartsWith(normTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }

                    if (!aggMap.ContainsKey(parent))
                    {
                        aggMap[parent] = new FolderAggInfo { Path = parent };
                    }

                    if (normTarget != null && string.Equals(parent, normTarget, StringComparison.OrdinalIgnoreCase))
                    {
                        break; // targetRoot 自体で打ち切り
                    }

                    parent = System.IO.Path.GetDirectoryName(parent);
                }
            }

            // 2. パスの深さ（区切り文字数）降順でソート（葉から親へ）
            var sortedNodes = aggMap.Values
                .OrderByDescending(n => n.Path.Count(c => c == '\\' || c == '/'))
                .ToList();

            // 3. ボトムアップ集約 (O(D)): 子の TotalXxx を親の TotalXxx に加算
            foreach (var node in sortedNodes)
            {
                // targetRoot 自体は親を持たないのでスキップ
                if (normTarget != null && string.Equals(node.Path, normTarget, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string? parent = System.IO.Path.GetDirectoryName(node.Path);
                if (!string.IsNullOrEmpty(parent) &&
                    (normTarget == null || parent.StartsWith(normTarget, StringComparison.OrdinalIgnoreCase)) &&
                    aggMap.TryGetValue(parent, out var parentInfo))
                {
                    parentInfo.TotalFileCount += node.TotalFileCount;
                    parentInfo.TotalBytes += node.TotalBytes;
                    if (node.TotalMaxWrite > parentInfo.TotalMaxWrite) parentInfo.TotalMaxWrite = node.TotalMaxWrite;
                    if (node.TotalMaxAccess > parentInfo.TotalMaxAccess) parentInfo.TotalMaxAccess = node.TotalMaxAccess;
                    if (node.TotalHasRecentAccess) parentInfo.TotalHasRecentAccess = true;
                }
            }

            return aggMap;
        }

        private static void DetectExtractedArchives(
            Dictionary<string, List<ScannedFileEntry>> dirMap,
            Dictionary<string, FolderAggInfo> aggMap,
            List<AuditItem> results)
        {
            foreach (var (dirPath, files) in dirMap)
            {
                var archives = files.Where(f => ArchiveExtensions.Contains(Path.GetExtension(f.Name))).ToList();
                if (archives.Count == 0) continue;

                foreach (var arch in archives)
                {
                    string archBaseName = Path.GetFileNameWithoutExtension(arch.Name);
                    // .tar.gz などの二重拡張子対応
                    if (archBaseName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
                    {
                        archBaseName = Path.GetFileNameWithoutExtension(archBaseName);
                    }

                    // 同一ディレクトリ内に、アーカイブベース名と同名のサブディレクトリが存在するか O(1) 判定
                    string expectedSubDir = Path.Combine(dirPath, archBaseName).TrimEnd('\\', '/');
                    if (aggMap.TryGetValue(expectedSubDir, out var subAgg) && subAgg.TotalFileCount > 0)
                    {
                        long dirBytes = subAgg.TotalBytes;
                        int dirCount = subAgg.TotalFileCount;

                        bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                        string detail = isJa
                            ? $"同一階層に展開済みフォルダー「{archBaseName}/」が存在 ({FormatHelper.FormatBytes(dirBytes, 1)} / {dirCount:N0}件)"
                            : $"Extracted folder \"{archBaseName}/\" exists in same folder ({FormatHelper.FormatBytes(dirBytes, 1)} / {dirCount:N0} files)";

                        var breakdown = new List<ScoreFactorItem>
                        {
                            new ScoreFactorItem { NameJa = "同一フォルダに展開済フォルダ存在", NameEn = "Extracted folder exists in same directory", Points = 90 }
                        };

                        results.Add(new AuditItem
                        {
                            FullPath = arch.FullPath,
                            FileName = arch.Name,
                            DirectoryPath = arch.DirectoryPath,
                            Size = arch.Length,
                            LastWriteTime = arch.LastWriteTime,
                            LastAccessTime = arch.LastAccessTime,
                            IssueType = AuditIssueType.ExtractedArchive,
                            Detail = detail,
                            WasteScore = 90, // 展開済みならZIPは高確率で残骸
                            ScoreBreakdown = breakdown,
                            RelatedActivePath = expectedSubDir
                        });
                    }
                }
            }
        }

        private static void DetectGraveyardTrees(
            Dictionary<string, FolderAggInfo> aggMap,
            List<AuditItem> results,
            DateTime dormantCutoff,
            DateTime now,
            string? targetRoot = null)
        {
            string? normTarget = string.IsNullOrEmpty(targetRoot) ? null : targetRoot.TrimEnd('\\', '/');

            foreach (var agg in aggMap.Values)
            {
                // 【ADR 91】targetRoot が指定されている場合、監査対象外のフォルダーや targetRoot 自体は墓場候補にしない
                if (normTarget != null)
                {
                    if (!agg.Path.StartsWith(normTarget, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(agg.Path, normTarget, StringComparison.OrdinalIgnoreCase)) continue;
                }

                // 最小規模: 3ファイル以上 かつ 1MB 以上
                if (agg.TotalFileCount < 3) continue;
                if (agg.TotalBytes < 1024 * 1024) continue;

                // 配下全ファイルの Max(LastWriteTime)
                if (agg.TotalMaxWrite >= dormantCutoff) continue; // 3年以内に1件でも更新があれば墓場ではない
                if (agg.TotalHasRecentAccess) continue; // 現場が閲覧中なら保護

                // フォルダー名のヒューリスティクス判定
                string dir = agg.Path;
                string dirName = Path.GetFileName(dir.TrimEnd('\\', '/'));
                bool hasGraveyardKeyword = GraveyardKeywords.Any(kw => dirName.Contains(kw, StringComparison.OrdinalIgnoreCase));

                // スコア計算
                int score = 80;
                var breakdown = new List<ScoreFactorItem>
                {
                    new ScoreFactorItem { NameJa = "配下全ファイル3年以上未更新・閲覧ゼロ", NameEn = "All files unedited/unread >3 years", Points = 80 }
                };

                if (hasGraveyardKeyword)
                {
                    score += 10;
                    breakdown.Add(new ScoreFactorItem { NameJa = $"墓場キーワード含有 ({dirName})", NameEn = $"Graveyard keyword in folder name ({dirName})", Points = 10 });
                }
                double yearsOld = (now - agg.TotalMaxWrite).TotalDays / 365.25;
                if (yearsOld > 5.0)
                {
                    score += 5;
                    breakdown.Add(new ScoreFactorItem { NameJa = $"5年以上完全未更新 ({yearsOld:F1}年)", NameEn = $"Unmodified for >5 years ({yearsOld:F1} yrs)", Points = 5 });
                }
                if (score > 95) score = 95;

                bool isJa = LocalizationService.Instance.CurrentLanguage == AppLanguage.Japanese;
                string detail = isJa
                    ? $"配下全ファイル ({agg.TotalFileCount:N0}件 / {FormatHelper.FormatBytes(agg.TotalBytes, 1)}) が{yearsOld:F1}年間未更新・閲覧ゼロ"
                    : $"All {agg.TotalFileCount:N0} files ({FormatHelper.FormatBytes(agg.TotalBytes, 1)}) unedited and unread for {yearsOld:F1} years";

                // 代表エントリとしてフォルダー自体を AuditItem として登録
                results.Add(new AuditItem
                {
                    FullPath = dir,
                    FileName = $"📁 {dirName}/ (フォルダー全体)",
                    DirectoryPath = Path.GetDirectoryName(dir) ?? dir,
                    Size = agg.TotalBytes,
                    LastWriteTime = agg.TotalMaxWrite,
                    LastAccessTime = agg.TotalMaxAccess,
                    IssueType = AuditIssueType.GraveyardTree,
                    Detail = detail,
                    WasteScore = score,
                    ScoreBreakdown = breakdown,
                    RelatedActivePath = dir
                });
            }
        }

        #endregion
    }
}
