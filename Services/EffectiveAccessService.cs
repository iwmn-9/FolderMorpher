using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using AstraSize.Models;
using AstraSize.Services;
using FolderMorpher.Models;

namespace FolderMorpher.Services
{
    /// <summary>
    /// Evaluates effective permissions for a target user or group across directory trees,
    /// fully resolving multi-level nested Active Directory and local group memberships.
    /// </summary>
    public class EffectiveAccessService
    {
        private readonly ActiveDirectoryService _adService;

        public EffectiveAccessService(ActiveDirectoryService? adService = null)
        {
            _adService = adService ?? new ActiveDirectoryService();
        }

        /// <summary>
        /// Resolves all direct and deeply nested group memberships for the specified account.
        /// </summary>
        public async Task<List<PrincipalGroupMembership>> GetGroupMembershipsAsync(string accountName)
        {
            return await Task.Run(() =>
            {
                var memberships = new List<PrincipalGroupMembership>();
                var cleanAccount = accountName.Contains('\\') ? accountName.Split('\\')[1] : accountName;

                if (_adService.IsDomainJoined && !string.IsNullOrWhiteSpace(_adService.CurrentDomainName))
                {
                    try
                    {
                        using var entry = new DirectoryEntry($"LDAP://{_adService.CurrentDomainName}");
                        using var searcher = new DirectorySearcher(entry)
                        {
                            PageSize = 500,
                            Filter = $"(&(objectCategory=person)(sAMAccountName={cleanAccount}))"
                        };
                        searcher.PropertiesToLoad.AddRange(new[] { "distinguishedName", "memberOf", "objectSid" });

                        var userResult = searcher.FindOne();
                        if (userResult != null)
                        {
                            var userDn = userResult.Properties["distinguishedName"][0]?.ToString();
                            var directDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            if (userResult.Properties.Contains("memberOf"))
                            {
                                foreach (var mo in userResult.Properties["memberOf"])
                                {
                                    if (mo != null) directDns.Add(mo.ToString()!);
                                }
                            }

                            if (!string.IsNullOrEmpty(userDn))
                            {
                                // LDAP_MATCHING_RULE_IN_CHAIN (1.2.840.113556.1.4.1941)
                                // Recursively retrieves ALL groups the user belongs to across arbitrary nesting depth
                                using var groupSearcher = new DirectorySearcher(entry)
                                {
                                    PageSize = 500,
                                    Filter = $"(member:1.2.840.113556.1.4.1941:={userDn})"
                                };
                                groupSearcher.PropertiesToLoad.AddRange(new[] { "sAMAccountName", "displayName", "distinguishedName", "objectSid" });

                                var groupResults = groupSearcher.FindAll();
                                foreach (SearchResult gr in groupResults)
                                {
                                    var gSam = gr.Properties["sAMAccountName"][0]?.ToString() ?? string.Empty;
                                    var gDisp = gr.Properties.Contains("displayName") && gr.Properties["displayName"].Count > 0
                                        ? gr.Properties["displayName"][0]?.ToString() ?? gSam
                                        : gSam;
                                    var gDn = gr.Properties["distinguishedName"][0]?.ToString() ?? string.Empty;

                                    bool isDirect = directDns.Contains(gDn);

                                    string sidStr = "";
                                    if (gr.Properties.Contains("objectSid") && gr.Properties["objectSid"].Count > 0)
                                    {
                                        try
                                        {
                                            var sidBytes = (byte[])gr.Properties["objectSid"][0];
                                            sidStr = new SecurityIdentifier(sidBytes, 0).Value;
                                        }
                                        catch { }
                                    }

                                    memberships.Add(new PrincipalGroupMembership
                                    {
                                        GroupName = gSam,
                                        DisplayName = string.IsNullOrEmpty(gDisp) ? gSam : gDisp,
                                        Sid = sidStr,
                                        IsDirect = isDirect,
                                        NestingDepth = isDirect ? 1 : 2,
                                        MembershipPath = isDirect ? "直接所属" : "入れ子所属 (AD Chain)"
                                    });
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Fallback on AD query failure
                    }
                }

                // If no domain groups resolved (or local/offline mode), inspect local Windows identity or provide presets
                if (memberships.Count == 0)
                {
                    try
                    {
                        var identity = WindowsIdentity.GetCurrent();
                        if (identity.Groups != null)
                        {
                            foreach (var groupRef in identity.Groups)
                            {
                                try
                                {
                                    var ntAccount = (NTAccount)groupRef.Translate(typeof(NTAccount));
                                    var gName = ntAccount.Value.Contains('\\') ? ntAccount.Value.Split('\\')[1] : ntAccount.Value;
                                    memberships.Add(new PrincipalGroupMembership
                                    {
                                        GroupName = gName,
                                        DisplayName = ntAccount.Value,
                                        Sid = groupRef.Value,
                                        IsDirect = true,
                                        NestingDepth = 1,
                                        MembershipPath = "ローカル所属"
                                    });
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }

                return memberships.OrderByDescending(m => m.IsDirect).ThenBy(m => m.GroupName).ToList();
            });
        }

        /// <summary>
        /// Recursively audits the target root folder and finds all folders accessible by targetAccount and its resolved groups.
        /// </summary>
        public async Task<EffectiveAccessAuditReport> ScanEffectiveAccessAsync(
            string rootPath,
            string targetAccount,
            List<PrincipalGroupMembership>? preResolvedGroups = null,
            int maxDepth = 6,
            IProgress<(int scanned, int found)>? progress = null,
            CancellationToken ct = default)
        {
            return await Task.Run(async () =>
            {
                var dir = new DirectoryInfo(rootPath);
                if (!dir.Exists) throw new DirectoryNotFoundException($"指定フォルダが存在しません: {rootPath}");

                var memberships = preResolvedGroups ?? await GetGroupMembershipsAsync(targetAccount);

                var report = new EffectiveAccessAuditReport
                {
                    TargetAccountName = targetAccount,
                    TargetDisplayName = targetAccount,
                    RootFolderPath = dir.FullName,
                    ScanTimestamp = DateTime.Now,
                    GroupMemberships = memberships
                };

                // Build lookup maps for fast matching
                var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var cleanTarget = targetAccount.Contains('\\') ? targetAccount.Split('\\')[1] : targetAccount;
                targetNames.Add(targetAccount);
                targetNames.Add(cleanTarget);

                var groupMap = new Dictionary<string, PrincipalGroupMembership>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in memberships)
                {
                    groupMap[g.GroupName] = g;
                    if (!string.IsNullOrEmpty(g.Sid)) groupMap[g.Sid] = g;
                }

                int scannedCount = 0;
                int foundCount = 0;

                void Traverse(DirectoryInfo currentDir, int depth)
                {
                    if (ct.IsCancellationRequested) return;

                    scannedCount++;
                    if (scannedCount % 25 == 0)
                    {
                        progress?.Report((scannedCount, foundCount));
                    }

                    try
                    {
                        var sec = currentDir.GetAccessControl(AccessControlSections.Access);
                        var item = EvaluateEffectiveAccessOnAcl(sec, targetAccount, targetNames, groupMap, currentDir.FullName, currentDir.Name);
                        if (item != null)
                        {
                            lock (report.AccessibleFolders)
                            {
                                report.AccessibleFolders.Add(item);
                                foundCount++;
                                if (item.PermissionLevel == EffectivePermissionLevel.FullControl) report.FullControlCount++;
                                else if (item.PermissionLevel == EffectivePermissionLevel.Modify) report.ModifyCount++;
                                else report.ReadOnlyCount++;
                            }
                        }
                    }
                    catch
                    {
                        // Skip inaccessible / restricted folders
                    }

                    if (depth >= maxDepth) return;

                    try
                    {
                        foreach (var sub in currentDir.GetDirectories())
                        {
                            if (ct.IsCancellationRequested) break;
                            Traverse(sub, depth + 1);
                        }
                    }
                    catch { }
                }

                Traverse(dir, 0);

                report.TotalFoldersScanned = scannedCount;
                progress?.Report((scannedCount, foundCount));
                return report;
            }, ct);
        }

        /// <summary>
        /// Core evaluation logic that matches an ACL against the target account and its group set,
        /// resolving Deny precedence and identifying exact grant sources.
        /// </summary>
        public EffectiveFolderAccessItem? EvaluateEffectiveAccessOnAcl(
            FileSystemSecurity sec,
            string targetAccount,
            HashSet<string> targetNames,
            Dictionary<string, PrincipalGroupMembership> groupMap,
            string folderPath,
            string folderName)
        {
            var rules = sec.GetAccessRules(true, true, typeof(NTAccount));

            FileSystemRights allowed = 0;
            FileSystemRights denied = 0;
            bool hasMatchingRule = false;
            bool isInherited = true;

            var grantSources = new List<string>();
            var grantTraces = new List<string>();

            foreach (FileSystemAccessRule rule in rules)
            {
                var id = rule.IdentityReference.Value;
                var idClean = id.Contains('\\') ? id.Split('\\')[1] : id;

                bool isDirectMatch = targetNames.Contains(id) || targetNames.Contains(idClean);
                bool isGroupMatch = groupMap.TryGetValue(id, out var matchedGroup) || groupMap.TryGetValue(idClean, out matchedGroup);
                bool isSpecialPrincipal = IsSpecialWorldPrincipal(idClean);

                if (!isDirectMatch && !isGroupMatch && !isSpecialPrincipal)
                {
                    continue;
                }

                hasMatchingRule = true;
                if (!rule.IsInherited) isInherited = false;

                if (rule.AccessControlType == AccessControlType.Deny)
                {
                    denied |= rule.FileSystemRights;
                }
                else
                {
                    allowed |= rule.FileSystemRights;

                    if (isDirectMatch)
                    {
                        grantSources.Add($"👤 直接付与 ({idClean})");
                        grantTraces.Add($"Direct: {idClean}");
                    }
                    else if (isGroupMatch && matchedGroup != null)
                    {
                        var badge = matchedGroup.IsDirect ? "👥" : "👥🔗";
                        var depthStr = matchedGroup.IsDirect ? "" : $" (深度 {matchedGroup.NestingDepth})";
                        grantSources.Add($"{badge} {matchedGroup.GroupName} 経由{depthStr}");
                        grantTraces.Add(matchedGroup.MembershipPath);
                    }
                    else if (isSpecialPrincipal)
                    {
                        grantSources.Add($"🌐 {idClean} 経由");
                        grantTraces.Add($"Special: {idClean}");
                    }
                }
            }

            if (!hasMatchingRule) return null;

            // NTFS Rule: Deny takes precedence over Allow
            var effective = allowed & (~denied);
            if (effective == 0) return null;

            var level = DeterminePermissionLevel(effective);
            if (level == EffectivePermissionLevel.None) return null;

            return new EffectiveFolderAccessItem
            {
                FolderPath = folderPath,
                FolderName = folderName,
                PermissionLevel = level,
                AllowedRights = allowed,
                DeniedRights = denied,
                HasDeny = denied != 0,
                IsInherited = isInherited,
                GrantSource = grantSources.Distinct().FirstOrDefault() ?? "付与",
                GrantPathTrace = string.Join(" / ", grantTraces.Distinct())
            };
        }

        public static EffectivePermissionLevel DeterminePermissionLevel(FileSystemRights rights)
        {
            if ((rights & FileSystemRights.FullControl) == FileSystemRights.FullControl)
                return EffectivePermissionLevel.FullControl;

            if ((rights & FileSystemRights.Modify) == FileSystemRights.Modify)
                return EffectivePermissionLevel.Modify;

            if ((rights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute)
                return EffectivePermissionLevel.ReadAndExecute;

            if ((rights & FileSystemRights.Read) == FileSystemRights.Read ||
                (rights & FileSystemRights.ListDirectory) == FileSystemRights.ListDirectory)
                return EffectivePermissionLevel.Read;

            return EffectivePermissionLevel.None;
        }

        private static bool IsSpecialWorldPrincipal(string name)
        {
            return name.Equals("Everyone", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Authenticated Users", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Domain Users", StringComparison.OrdinalIgnoreCase);
        }
    }
}
