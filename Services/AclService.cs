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

        // Snapshot & Rollback
        public async Task<AclSnapshot> CreateSnapshotAsync(string path, string note = "変更前のバックアップ")
        {
            var dir = new DirectoryInfo(path);
            var sec = dir.GetAccessControl(AccessControlSections.All);
            var sddl = sec.GetSecurityDescriptorSddlForm(AccessControlSections.All);

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
                    IsInherited = rule.IsInherited
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
            sec.SetAccessRuleProtection(!inherit, true);

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
                var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                var propagation = PropagationFlags.None;
                var rule = new FileSystemAccessRule(identity, entry.Rights, inheritance, propagation, AccessControlType.Deny);
                sec.AddAccessRule(rule);
            }

            foreach (var entry in allowEntries)
            {
                var identity = new NTAccount(entry.AccountName);
                var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
                var propagation = PropagationFlags.None;
                var rule = new FileSystemAccessRule(identity, entry.Rights, inheritance, propagation, AccessControlType.Allow);
                sec.AddAccessRule(rule);
            }

            dirInfo.SetAccessControl(sec);
        }

        public void RollbackToSnapshot(string path, AclSnapshot snapshot)
        {
            var dir = new DirectoryInfo(path);
            var sec = new DirectorySecurity();
            sec.SetSecurityDescriptorSddlForm(snapshot.Sddl, AccessControlSections.All);
            dir.SetAccessControl(sec);
        }
    }
}
