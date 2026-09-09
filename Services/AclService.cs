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
        public async Task<AclSnapshot> CreateSnapshotAsync(string path, string note = "変更前のバックアップ")
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
                Sddl = sddl
            };

            var file = Path.Combine(_snapshotDir, $"{snapshot.Id}.json");
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true });
            await File.WriteAllTextAsync(file, json);

            return snapshot;
        }

        public async Task<List<AclSnapshot>> GetSnapshotsAsync(string path)
        {
            var list = new List<AclSnapshot>();
            if (!Directory.Exists(_snapshotDir)) return list;

            foreach (var file in Directory.GetFiles(_snapshotDir, "*.json"))
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

            return list.OrderByDescending(s => s.Timestamp).ToList();
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
        /// 差分適用 (Delta Apply): 読み取り時からの変化点（Added / Removed / Modified）のみをピンポイントで適用する。
        /// 【安全原則】変更されていない既存ACEにはOS上・メモリ上ともに1ビットも触れない（ノータッチ）。
        /// 変更が0件の場合はSetAccessControl自体をスキップする。
        /// </summary>
        public async Task<(bool hasModified, int addedCount, int removedCount, int modifiedCount, AclSnapshot? snapshot)> ApplyLiveAclDeltaWithRollbackAsync(
            string path,
            IEnumerable<SimAclEntry> originalEntries,
            IEnumerable<SimAclEntry> currentEntries,
            bool inherit,
            bool originalInherit)
        {
            var dirInfo = new DirectoryInfo(path);
            if (!dirInfo.Exists) throw new DirectoryNotFoundException($"指定フォルダが存在しません: {path}");

            bool inheritChanged = (inherit != originalInherit);

            // High 1: 継承OFFへの切替時は、旧継承ACEも明示ACEに変換されるため全ルールを突合対象とする
            var origList = (inheritChanged && !inherit)
                ? originalEntries.Select(e => { var clone = e.Clone(); clone.IsInherited = false; return clone; }).ToList()
                : originalEntries.Where(e => !e.IsInherited).ToList();
            var curList = currentEntries.Where(e => !e.IsInherited).ToList();

            var toRemove = new List<SimAclEntry>();
            var toModify = new List<(SimAclEntry oldEntry, SimAclEntry newEntry)>();
            var toAdd = new List<SimAclEntry>();

            var remainingOrig = new List<SimAclEntry>(origList);
            var remainingCur = new List<SimAclEntry>(curList);

            // High 2: 1. 完全一致（MatchesExact）ペアの除外（ノータッチ原則）
            for (int i = remainingCur.Count - 1; i >= 0; i--)
            {
                var cur = remainingCur[i];
                var matchedOrig = remainingOrig.FirstOrDefault(o => o.MatchesExact(cur));
                if (matchedOrig != null)
                {
                    remainingCur.RemoveAt(i);
                    remainingOrig.Remove(matchedOrig);
                }
            }

            // High 2: 2. キー一致（MatchesKey）ペアの抽出（権限変更 -> toModify）
            for (int i = remainingCur.Count - 1; i >= 0; i--)
            {
                var cur = remainingCur[i];
                var matchedOrig = remainingOrig.FirstOrDefault(o => o.MatchesKey(cur));
                if (matchedOrig != null)
                {
                    toModify.Add((matchedOrig, cur));
                    remainingCur.RemoveAt(i);
                    remainingOrig.Remove(matchedOrig);
                }
            }

            // High 2: 3. 残余は追加・削除
            toRemove.AddRange(remainingOrig);
            toAdd.AddRange(remainingCur);

            bool hasModified = inheritChanged || toRemove.Count > 0 || toAdd.Count > 0 || toModify.Count > 0;

            if (!hasModified)
            {
                // 変更が全くない場合はAPI呼び出しを行わず即座に正常終了
                return (false, 0, 0, 0, null);
            }

            // 変更前の完全DACLをバックアップ（ロールバック用）
            var snapshot = await CreateSnapshotAsync(path, $"差分変更前バックアップ (変更: +{toAdd.Count}, -{toRemove.Count}, ~{toModify.Count})");

            var sec = dirInfo.GetAccessControl(AccessControlSections.Access);

            if (inheritChanged)
            {
                // High 1: 継承無効化時は既存の継承ACEを明示ACEとして確実に保持する (preserveInheritance: true)
                // ロックアウト事故を100%防止
                sec.SetAccessRuleProtection(!inherit, preserveInheritance: true);
            }

            // 変更ACEの旧ルール削除
            foreach (var (oldEntry, _) in toModify)
            {
                var identity = new NTAccount(oldEntry.AccountName);
                var rule = new FileSystemAccessRule(identity, oldEntry.Rights, oldEntry.InheritanceFlags, oldEntry.PropagationFlags, oldEntry.AccessType);
                sec.RemoveAccessRuleSpecific(rule);
            }

            // 削除ACEのピンポイント削除
            foreach (var entry in toRemove)
            {
                var identity = new NTAccount(entry.AccountName);
                var rule = new FileSystemAccessRule(identity, entry.Rights, entry.InheritanceFlags, entry.PropagationFlags, entry.AccessType);
                sec.RemoveAccessRuleSpecific(rule);
            }

            // 変更ACEの新ルール追加 (アカウント名は完全修飾名を持つ方を優先)
            foreach (var (oldEntry, newEntry) in toModify)
            {
                var accName = oldEntry.AccountName.Contains('\\') ? oldEntry.AccountName : newEntry.AccountName;
                var identity = new NTAccount(accName);
                var rule = new FileSystemAccessRule(identity, newEntry.Rights, newEntry.InheritanceFlags, newEntry.PropagationFlags, newEntry.AccessType);
                sec.AddAccessRule(rule);
            }

            // 追加ACEのピンポイント追加
            foreach (var entry in toAdd)
            {
                var identity = new NTAccount(entry.AccountName);
                var rule = new FileSystemAccessRule(identity, entry.Rights, entry.InheritanceFlags, entry.PropagationFlags, entry.AccessType);
                sec.AddAccessRule(rule);
            }

            dirInfo.SetAccessControl(sec);

            return (true, toAdd.Count, toRemove.Count, toModify.Count, snapshot);
        }

        public void RollbackToSnapshot(string path, AclSnapshot snapshot)
        {
            var dir = new DirectoryInfo(path);
            var sec = new DirectorySecurity();
            // DACL(AccessControlSections.Access)のみを復元して安全・確実にロールバック
            sec.SetSecurityDescriptorSddlForm(snapshot.Sddl, AccessControlSections.Access);
            dir.SetAccessControl(sec);
        }
    }
}
