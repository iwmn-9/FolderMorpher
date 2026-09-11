using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using AstraSize.Models;
using FolderMorpher.Services;

namespace AstraSize.Services
{
    public class AclService
    {
        private readonly string _snapshotDir;

        public AclService()
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            _snapshotDir = Path.Combine(appData, "FolderMorpher", "AclSnapshots");
            Directory.CreateDirectory(_snapshotDir);

            // 旧 AstraSize のスナップショットが存在する場合、FolderMorpher へ自動移行・マージ
            try
            {
                var oldSnapshotDir = Path.Combine(appData, "AstraSize", "AclSnapshots");
                if (Directory.Exists(oldSnapshotDir))
                {
                    foreach (var file in Directory.GetFiles(oldSnapshotDir, "*.json"))
                    {
                        var destFile = Path.Combine(_snapshotDir, Path.GetFileName(file));
                        if (!File.Exists(destFile))
                        {
                            File.Copy(file, destFile);
                        }
                    }
                }
            }
            catch { }
        }

        public FolderAclNode GetFolderAcl(string path, int maxDepth = 2)
        {
            var dirInfo = new DirectoryInfo(path);
            if (!dirInfo.Exists)
            {
                throw new DirectoryNotFoundException($"指定されたフォルダが存在しません: {path}");
            }

            return ReadAclRecursive(dirInfo, 0, maxDepth);
        }

        private FolderAclNode ReadAclRecursive(DirectoryInfo dir, int currentDepth, int maxDepth)
        {
            var node = new FolderAclNode
            {
                Path = dir.FullName,
                Name = dir.Name
            };

            try
            {
                var sec = dir.GetAccessControl(AccessControlSections.Access);
                node.AreAccessRulesProtected = sec.AreAccessRulesProtected;

                var rules = sec.GetAccessRules(true, true, typeof(NTAccount));
                foreach (FileSystemAccessRule rule in rules)
                {
                    node.Entries.Add(new AclEntry
                    {
                        Identity = rule.IdentityReference.Value,
                        DisplayName = rule.IdentityReference.Value,
                        Rights = rule.FileSystemRights,
                        AccessType = rule.AccessControlType,
                        IsInherited = rule.IsInherited
                    });
                }
            }
            catch (Exception ex)
            {
                node.Entries.Add(new AclEntry
                {
                    Identity = "SYSTEM",
                    DisplayName = $"[読み取り失敗: {ex.Message}]",
                    Rights = FileSystemRights.Read,
                    IsInherited = false
                });
            }

            if (currentDepth < maxDepth)
            {
                try
                {
                    foreach (var subDir in dir.GetDirectories())
                    {
                        if ((subDir.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        node.Children.Add(ReadAclRecursive(subDir, currentDepth + 1, maxDepth));
                    }
                }
                catch { }
            }

            return node;
        }

        // Export Access Matrix Ledger (Excel / CSV)
        public string GenerateMatrixCsv(FolderAclNode rootNode)
        {
            var allFolders = new List<FolderAclNode>();
            void Flatten(FolderAclNode n)
            {
                allFolders.Add(n);
                foreach (var c in n.Children) Flatten(c);
            }
            Flatten(rootNode);

            // Collect all unique identities
            var allIdentities = allFolders
                .SelectMany(f => f.Entries.Select(e => e.Identity))
                .Distinct()
                .OrderBy(id => id)
                .ToList();

            var sb = new StringBuilder();
            sb.Append('\uFEFF'); // UTF-8 BOM for Excel

            // Header row
            sb.Append("フォルダパス,フォルダ名,継承設定");
            foreach (var id in allIdentities)
            {
                sb.Append($",\"{id.Replace("\"", "\"\"")}\"");
            }
            sb.AppendLine();

            // Data rows
            foreach (var f in allFolders)
            {
                sb.Append($"\"{f.Path.Replace("\"", "\"\"")}\",");
                sb.Append($"\"{f.Name.Replace("\"", "\"\"")}\",");
                sb.Append(f.AreAccessRulesProtected ? "固有設定(継承無効)" : "親から継承中");

                foreach (var id in allIdentities)
                {
                    var entry = f.Entries.FirstOrDefault(e => e.Identity.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (entry != null)
                    {
                        string mark = $"{entry.FormattedRights}{(entry.IsInherited ? " (継承)" : "")}";
                        sb.Append($",\"{mark}\"");
                    }
                    else
                    {
                        sb.Append(",\"-\"");
                    }
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        public string GetSddl(string path)
        {
            var dir = new DirectoryInfo(path);
            var sec = dir.GetAccessControl(AccessControlSections.Access);
            return sec.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        }

        // Snapshot & Rollback
        private List<string> GetAclWriteDirectories()
        {
            var dirs = new List<string>();
            var configuredWriteDir = AppSettingsService.Instance.GetWriteDirectory("Snapshots");
            if (!string.IsNullOrEmpty(configuredWriteDir))
            {
                var sharedAcl = Path.Combine(configuredWriteDir, "AclSnapshots");
                dirs.Add(sharedAcl);
            }
            if (!dirs.Contains(_snapshotDir, StringComparer.OrdinalIgnoreCase))
            {
                dirs.Add(_snapshotDir);
            }
            return dirs;
        }

        private List<string> GetAclReadDirectories()
        {
            var dirs = new List<string>();
            var configuredReadDir = AppSettingsService.Instance.GetReadDirectory("Snapshots");
            if (!string.IsNullOrEmpty(configuredReadDir))
            {
                var sharedAcl = Path.Combine(configuredReadDir, "AclSnapshots");
                if (Directory.Exists(sharedAcl)) dirs.Add(sharedAcl);
            }
            if (Directory.Exists(_snapshotDir) && !dirs.Contains(_snapshotDir, StringComparer.OrdinalIgnoreCase))
            {
                dirs.Add(_snapshotDir);
            }
            return dirs;
        }

        public async Task<AclSnapshot> CreateSnapshotAsync(string path, string note = "変更前のバックアップ", string changeSummary = "")
        {
            var dir = new DirectoryInfo(path);
            // ADR: SACL(監査権限)によるPrivilegeNotHeldExceptionを防止するため、DACL(AccessControlSections.Access)のみを対象とする
            var sec = dir.GetAccessControl(AccessControlSections.Access);
            var sddl = sec.GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            var snapshot = new AclSnapshot
            {
                TargetPath = path,
                Timestamp = DateTime.Now,
                Note = note,
                ChangeSummary = changeSummary,
                Sddl = sddl
            };

            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });

            // ローカルおよび設定された共有ディレクトリの双方へ保存（チーム内での事故復旧共有）
            var writeDirs = GetAclWriteDirectories();
            foreach (var targetDir in writeDirs)
            {
                try
                {
                    Directory.CreateDirectory(targetDir);
                    var file = Path.Combine(targetDir, $"{snapshot.Id}.json");
                    await File.WriteAllTextAsync(file, json);

                    // 各保存先で直近10世代を保持し、古い切り戻しバックアップを自動ローテーション削除
                    CleanOldSnapshots(targetDir, path, keepCount: 10);
                }
                catch { }
            }

            return snapshot;
        }

        private void CleanOldSnapshots(string dir, string path, int keepCount)
        {
            try
            {
                if (!Directory.Exists(dir)) return;
                var normalized = path.TrimEnd('\\', '/');

                var snapshots = new List<(string filePath, DateTime timestamp)>();
                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var json = File.ReadAllText(file);
                        var s = JsonSerializer.Deserialize<AclSnapshot>(json);
                        if (s != null && string.Equals(s.TargetPath.TrimEnd('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase))
                        {
                            snapshots.Add((file, s.Timestamp));
                        }
                    }
                    catch { }
                }

                if (snapshots.Count > keepCount)
                {
                    var toDelete = snapshots
                        .OrderByDescending(x => x.timestamp)
                        .Skip(keepCount)
                        .ToList();

                    foreach (var old in toDelete)
                    {
                        try { File.Delete(old.filePath); } catch { }
                    }
                }
            }
            catch { }
        }

        public async Task<List<AclSnapshot>> GetSnapshotsAsync(string path)
        {
            var list = new List<AclSnapshot>();
            var readDirs = GetAclReadDirectories();

            foreach (var dir in readDirs)
            {
                if (!Directory.Exists(dir)) continue;

                foreach (var file in Directory.GetFiles(dir, "*.json"))
                {
                    try
                    {
                        var json = await File.ReadAllTextAsync(file);
                        var s = JsonSerializer.Deserialize<AclSnapshot>(json);
                        if (s != null && string.Equals(s.TargetPath.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                        {
                            list.Add(s);
                        }
                    }
                    catch { }
                }
            }

            // スナップショットId（または TargetPath + Timestamp）で重複排除して日時降順ソート
            return list
                .GroupBy(s => s.Id)
                .Select(g => g.First())
                .OrderByDescending(s => s.Timestamp)
                .ToList();
        }

        public (List<SimAclEntry> entries, bool isInherited, string owner) GetSimAclForFolder(string path)
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) throw new DirectoryNotFoundException($"フォルダが見つかりません: {path}");

            var sec = dir.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
            var owner = sec.GetOwner(typeof(NTAccount))?.Value ?? "不明";
            bool isInherited = !sec.AreAccessRulesProtected;

            var list = new List<SimAclEntry>();
            var rules = sec.GetAccessRules(true, true, typeof(NTAccount));
            foreach (FileSystemAccessRule rule in rules)
            {
                bool isGrp = rule.IdentityReference.Value.EndsWith("Groups", StringComparison.OrdinalIgnoreCase)
                          || rule.IdentityReference.Value.Contains("Domain Users")
                          || rule.IdentityReference.Value.Contains("Users")
                          || rule.IdentityReference.Value.Contains("Administrators");

                var entry = new SimAclEntry
                {
                    AccountName = rule.IdentityReference.Value,
                    DisplayName = rule.IdentityReference.Value.Contains('\\')
                        ? rule.IdentityReference.Value.Split('\\')[1]
                        : rule.IdentityReference.Value,
                    PrincipalType = isGrp ? AdPrincipalType.Group : AdPrincipalType.User,
                    Rights = rule.FileSystemRights,
                    AccessType = rule.AccessControlType,
                    IsInherited = rule.IsInherited,
                    InheritanceFlags = rule.InheritanceFlags,
                    PropagationFlags = rule.PropagationFlags
                };
                list.Add(entry);
            }

            return (list, isInherited, owner);
        }

        public void ApplySimAclEntries(string path, IEnumerable<SimAclEntry> entries, bool inherit)
        {
            var dirInfo = new DirectoryInfo(path);
            if (!dirInfo.Exists) throw new DirectoryNotFoundException($"指定フォルダが存在しません: {path}");

            var sec = dirInfo.GetAccessControl(AccessControlSections.Access);
            sec.SetAccessRuleProtection(!inherit, false);

            var existingRules = sec.GetAccessRules(true, false, typeof(NTAccount));
            foreach (FileSystemAccessRule rule in existingRules)
            {
                sec.RemoveAccessRuleSpecific(rule);
            }

            // 継承OFF時（!inherit）は、これまで継承されていたACEも含めて全て明示的ルールへ変換して完全保持する
            var targetEntries = inherit
                ? entries.Where(e => !e.IsInherited).ToList()
                : entries.ToList();

            // Canonical ACL Order: Explicit Deny ACEs MUST precede Explicit Allow ACEs
            var denyEntries = targetEntries.Where(e => e.AccessType == AccessControlType.Deny);
            var allowEntries = targetEntries.Where(e => e.AccessType == AccessControlType.Allow);

            foreach (var entry in denyEntries)
            {
                var identity = new NTAccount(entry.AccountName);
                var rule = new FileSystemAccessRule(identity, entry.Rights, entry.InheritanceFlags, entry.PropagationFlags, AccessControlType.Deny);
                sec.AddAccessRule(rule);
            }

            foreach (var entry in allowEntries)
            {
                var identity = new NTAccount(entry.AccountName);
                var rule = new FileSystemAccessRule(identity, entry.Rights, entry.InheritanceFlags, entry.PropagationFlags, AccessControlType.Allow);
                sec.AddAccessRule(rule);
            }

            dirInfo.SetAccessControl(sec);
        }

        /// <summary>
        /// Live ACL 実行計画 (Change Plan) の構築。
        /// 差分計算（Added / Removed / Modified / Untouched）、継承設定の変更、および期待される変更後ACEセットを同一インスタンスに集約する。
        /// 【安全原則】変更されていない既存ACEにはOS上・メモリ上ともに1ビットも触れない（ノータッチ）。
        /// </summary>
        public AclChangePlan BuildChangePlan(
            string path,
            IEnumerable<SimAclEntry> originalEntries,
            IEnumerable<SimAclEntry> currentEntries,
            bool inherit,
            bool originalInherit,
            string? originalSddl = null)
        {
            var plan = new AclChangePlan
            {
                FolderPath = path,
                FolderName = Path.GetFileName(path.TrimEnd('\\', '/')),
                OriginalSddl = originalSddl ?? string.Empty,
                InheritanceBefore = originalInherit,
                InheritanceAfter = inherit
            };
            if (string.IsNullOrEmpty(plan.FolderName)) plan.FolderName = path;

            bool inheritChanged = plan.InheritanceChanged;

            // 継承OFF切替時: 親からの継承ACEが明示ACEに変換・昇格される件数を集計
            if (inheritChanged && !inherit)
            {
                plan.InheritedAcesPromotedCount = originalEntries.Count(e => e.IsInherited);
            }

            // High 1: 継承OFFへの切替時は、旧継承ACEも明示ACEに変換されるため全ルールを突合対象とする
            var origList = (inheritChanged && !inherit)
                ? originalEntries.Select(e => { var clone = e.Clone(); clone.IsInherited = false; return clone; }).ToList()
                : originalEntries.Where(e => !e.IsInherited).ToList();
            var curList = (inheritChanged && !inherit)
                ? currentEntries.Select(e => { var clone = e.Clone(); clone.IsInherited = false; return clone; }).ToList()
                : currentEntries.Where(e => !e.IsInherited).ToList();

            var remainingOrig = new List<SimAclEntry>(origList);
            var remainingCur = new List<SimAclEntry>(curList);

            // 1. 完全一致 (MatchesExact) ペアの除外 (ノータッチ維持)
            for (int i = remainingCur.Count - 1; i >= 0; i--)
            {
                var cur = remainingCur[i];
                var matchedOrig = remainingOrig.FirstOrDefault(o => o.MatchesExact(cur));
                if (matchedOrig != null)
                {
                    plan.Untouched.Add(cur);
                    // 維持項目は上部サマリー（維持: X件）に集約し、詳細グリッド（DiffItems）には変更対象（追加・削除・変更）のみを表示
                    remainingCur.RemoveAt(i);
                    remainingOrig.Remove(matchedOrig);
                }
            }

            // 2. キー一致 (MatchesKey) ペアの抽出 (権限変更 -> Modified)
            for (int i = remainingCur.Count - 1; i >= 0; i--)
            {
                var cur = remainingCur[i];
                var matchedOrig = remainingOrig.FirstOrDefault(o => o.MatchesKey(cur));
                if (matchedOrig != null)
                {
                    plan.Modified.Add((matchedOrig, cur));
                    var diffDetail = CalculateRightsDiff(matchedOrig.Rights, cur.Rights);
                    plan.DiffItems.Add(new LiveAclDiffItem
                    {
                        DiffType = LiveAclDiffType.Modified,
                        AccountName = cur.AccountName,
                        DisplayName = cur.DisplayName,
                        IconGlyph = cur.IconGlyph,
                        AccessType = cur.AccessType,
                        BeforeRights = matchedOrig.FormattedRights,
                        AfterRights = cur.FormattedRights,
                        Details = diffDetail,
                        AppliesTo = cur.AppliesTo,
                        IsInherited = cur.IsInherited
                    });
                    remainingCur.RemoveAt(i);
                    remainingOrig.Remove(matchedOrig);
                }
            }

            // 3. 残余の削除 (Removed)
            foreach (var rem in remainingOrig)
            {
                plan.Removed.Add(rem);
                plan.DiffItems.Add(new LiveAclDiffItem
                {
                    DiffType = LiveAclDiffType.Removed,
                    AccountName = rem.AccountName,
                    DisplayName = rem.DisplayName,
                    IconGlyph = rem.IconGlyph,
                    AccessType = rem.AccessType,
                    BeforeRights = rem.FormattedRights,
                    AfterRights = "（削除）",
                    Details = $"-{rem.FormattedRights}",
                    AppliesTo = rem.AppliesTo,
                    IsInherited = rem.IsInherited
                });
            }

            // 4. 残余の追加 (Added)
            foreach (var add in remainingCur)
            {
                plan.Added.Add(add);
                plan.DiffItems.Add(new LiveAclDiffItem
                {
                    DiffType = LiveAclDiffType.Added,
                    AccountName = add.AccountName,
                    DisplayName = add.DisplayName,
                    IconGlyph = add.IconGlyph,
                    AccessType = add.AccessType,
                    BeforeRights = "―",
                    AfterRights = add.FormattedRights,
                    Details = $"+{add.FormattedRights}",
                    AppliesTo = add.AppliesTo,
                    IsInherited = add.IsInherited
                });
            }

            // 期待される変更後ACE一覧 (Verify用 ExpectedAfterEntries)
            plan.ExpectedAfterEntries.AddRange(plan.Untouched.Select(x => x.Clone()));
            plan.ExpectedAfterEntries.AddRange(plan.Modified.Select(m => m.NewEntry.Clone()));
            plan.ExpectedAfterEntries.AddRange(plan.Added.Select(x => x.Clone()));

            return plan;
        }

        /// <summary>
        /// Change Plan を受け取り、ピンポイントで本番適用 (Commit) する。
        /// </summary>
        public async Task<(bool hasModified, int addedCount, int removedCount, int modifiedCount, AclSnapshot? snapshot)> ApplyChangePlanWithRollbackAsync(
            AclChangePlan plan,
            bool forceIfConflict = false)
        {
            var dirInfo = new DirectoryInfo(plan.FolderPath);
            if (!dirInfo.Exists) throw new DirectoryNotFoundException($"指定フォルダが存在しません: {plan.FolderPath}");

            if (!plan.HasChanges)
            {
                // 変更が全くない場合はAPI呼び出しを行わず即座に正常終了
                return (false, 0, 0, 0, null);
            }

            var sec = dirInfo.GetAccessControl(AccessControlSections.Access);

            // Medium: 外部ACL変更との競合検出
            if (!string.IsNullOrEmpty(plan.OriginalSddl) && !forceIfConflict)
            {
                string currentSddl = sec.GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                if (!string.Equals(currentSddl, plan.OriginalSddl, StringComparison.OrdinalIgnoreCase))
                {
                    throw new AclConflictException(plan.FolderPath, currentSddl, plan.OriginalSddl);
                }
            }

            // 変更前の完全DACLをバックアップ（ロールバック用）
            string changeSummary = $"変更: +{plan.Added.Count}, -{plan.Removed.Count}, ~{plan.Modified.Count}" + (plan.InheritanceChanged ? $", 継承:{(plan.InheritanceAfter ? "有効" : "無効")}" : "");
            var snapshot = await CreateSnapshotAsync(plan.FolderPath, $"差分変更前バックアップ ({changeSummary})", changeSummary);

            if (plan.InheritanceChanged)
            {
                // High 1: 継承無効化時は既存の継承ACEを明示ACEとして確実に保持する (preserveInheritance: true)
                sec.SetAccessRuleProtection(!plan.InheritanceAfter, preserveInheritance: true);
            }

            // 変更ACEの旧ルール削除
            foreach (var (oldEntry, _) in plan.Modified)
            {
                var identity = new NTAccount(oldEntry.AccountName);
                var rule = new FileSystemAccessRule(identity, oldEntry.Rights, oldEntry.InheritanceFlags, oldEntry.PropagationFlags, oldEntry.AccessType);
                sec.RemoveAccessRuleSpecific(rule);
            }

            // 削除ACEのピンポイント削除
            foreach (var entry in plan.Removed)
            {
                var identity = new NTAccount(entry.AccountName);
                var rule = new FileSystemAccessRule(identity, entry.Rights, entry.InheritanceFlags, entry.PropagationFlags, entry.AccessType);
                sec.RemoveAccessRuleSpecific(rule);
            }

            // 変更ACEの新ルール追加 (アカウント名は完全修飾名を持つ方を優先)
            foreach (var (oldEntry, newEntry) in plan.Modified)
            {
                var accName = oldEntry.AccountName.Contains('\\') ? oldEntry.AccountName : newEntry.AccountName;
                var identity = new NTAccount(accName);
                var rule = new FileSystemAccessRule(identity, newEntry.Rights, newEntry.InheritanceFlags, newEntry.PropagationFlags, newEntry.AccessType);
                sec.AddAccessRule(rule);
            }

            // 追加ACEのピンポイント追加
            foreach (var entry in plan.Added)
            {
                var identity = new NTAccount(entry.AccountName);
                var rule = new FileSystemAccessRule(identity, entry.Rights, entry.InheritanceFlags, entry.PropagationFlags, entry.AccessType);
                sec.AddAccessRule(rule);
            }

            dirInfo.SetAccessControl(sec);

            return (true, plan.Added.Count, plan.Removed.Count, plan.Modified.Count, snapshot);
        }

        /// <summary>
        /// 差分適用 (Delta Apply): 既存互換用ラッパー。BuildChangePlan -> ApplyChangePlanWithRollbackAsync を呼び出す。
        /// </summary>
        public async Task<(bool hasModified, int addedCount, int removedCount, int modifiedCount, AclSnapshot? snapshot)> ApplyLiveAclDeltaWithRollbackAsync(
            string path,
            IEnumerable<SimAclEntry> originalEntries,
            IEnumerable<SimAclEntry> currentEntries,
            bool inherit,
            bool originalInherit,
            string? expectedOriginalSddl = null,
            bool forceIfConflict = false)
        {
            var plan = BuildChangePlan(path, originalEntries, currentEntries, inherit, originalInherit, expectedOriginalSddl);
            return await ApplyChangePlanWithRollbackAsync(plan, forceIfConflict);
        }

        /// <summary>
        /// 適用後の正常性検証 (Verify):
        /// 予定していた ExpectedAfterEntries および InheritanceAfter と、OSから再取得した実態をセマンティック比較する。
        /// </summary>
        public AclVerificationResult VerifyChangePlan(AclChangePlan plan)
        {
            var result = new AclVerificationResult();
            var dirInfo = new DirectoryInfo(plan.FolderPath);
            if (!dirInfo.Exists)
            {
                result.IsSuccess = false;
                result.StatusText = "対象不在";
                result.Discrepancies.Add($"フォルダが存在しません: {plan.FolderPath}");
                return result;
            }

            var (actualEntries, actualInherit, _) = GetSimAclForFolder(plan.FolderPath);

            // 1. 継承フラグの一致確認
            if (actualInherit != plan.InheritanceAfter)
            {
                result.IsSuccess = false;
                result.Discrepancies.Add($"継承設定不一致 (予定: {plan.InheritanceAfter}, 実態: {actualInherit})");
            }

            // 2. 明示ACEの突合 (ExpectedAfterEntries vs actualEntries の明示ルール)
            var actualExplicit = actualEntries.Where(e => !e.IsInherited).ToList();
            var expectedExplicit = plan.ExpectedAfterEntries.Where(e => !e.IsInherited).ToList();

            var remainingActual = new List<SimAclEntry>(actualExplicit);
            foreach (var exp in expectedExplicit)
            {
                var matched = remainingActual.FirstOrDefault(a => a.MatchesExact(exp));
                if (matched != null)
                {
                    remainingActual.Remove(matched);
                }
                else
                {
                    result.IsSuccess = false;
                    result.Discrepancies.Add($"未反映ACE: {exp.DisplayName} ({exp.FormattedRights})");
                }
            }

            foreach (var extra in remainingActual)
            {
                result.IsSuccess = false;
                result.Discrepancies.Add($"予期せぬACE: {extra.DisplayName} ({extra.FormattedRights})");
            }

            result.StatusText = result.IsSuccess ? "正常" : "不一致検知";
            return result;
        }

        public void RollbackToSnapshot(string path, AclSnapshot snapshot)
        {
            var dir = new DirectoryInfo(path);
            var sec = new DirectorySecurity();
            // DACL(AccessControlSections.Access)のみを復元して安全・確実にロールバック
            sec.SetSecurityDescriptorSddlForm(snapshot.Sddl, AccessControlSections.Access);
            dir.SetAccessControl(sec);
        }

        /// <summary>
        /// 2つの権限ビットマスク間の詳細差分（追加・削除されたビット一覧）を算出する
        /// </summary>
        private static string CalculateRightsDiff(FileSystemRights before, FileSystemRights after)
        {
            if (before == after) return "差分なし";

            var added = after & ~before;
            var removed = before & ~after;

            var parts = new List<string>();

            if ((after & FileSystemRights.FullControl) == FileSystemRights.FullControl)
            {
                parts.Add("+フルコントロール");
            }
            else
            {
                if ((added & FileSystemRights.Modify) == FileSystemRights.Modify) parts.Add("+変更");
                if ((added & FileSystemRights.Delete) != 0 || (added & FileSystemRights.DeleteSubdirectoriesAndFiles) != 0) parts.Add("+削除");
                if ((added & FileSystemRights.Write) == FileSystemRights.Write) parts.Add("+書き込み");
                if ((added & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute) parts.Add("+読み取りと実行");
                else if ((added & FileSystemRights.Read) == FileSystemRights.Read) parts.Add("+読み取り");
            }

            if ((removed & FileSystemRights.FullControl) == FileSystemRights.FullControl) parts.Add("-フルコントロール");
            else
            {
                if ((removed & FileSystemRights.Modify) == FileSystemRights.Modify) parts.Add("-変更");
                if ((removed & FileSystemRights.Delete) != 0 || (removed & FileSystemRights.DeleteSubdirectoriesAndFiles) != 0) parts.Add("-削除");
                if ((removed & FileSystemRights.Write) == FileSystemRights.Write) parts.Add("-書き込み");
                if ((removed & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute) parts.Add("-読み取りと実行");
                else if ((removed & FileSystemRights.Read) == FileSystemRights.Read) parts.Add("-読み取り");
            }

            return parts.Count > 0 ? string.Join(", ", parts) : "詳細ビット変更";
        }
    }
}
