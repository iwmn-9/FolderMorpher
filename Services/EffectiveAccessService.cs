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
        /// When query fails or in local mode, strictly avoids falling back to the current running user
        /// if the target account is a different person.
        /// </summary>
        public async Task<(List<PrincipalGroupMembership> Memberships, EffectiveAccessResolutionMode Mode, string StatusText)> 
            ResolveMembershipsAsync(string accountName)
        {
            return await Task.Run(() =>
            {
                var memberships = new List<PrincipalGroupMembership>();
                var cleanAccount = accountName.Contains('\\') ? accountName.Split('\\')[1] : accountName;

                bool isCurrentLogonUser =
                    string.Equals(accountName, Environment.UserName, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(accountName, $"{Environment.UserDomainName}\\{Environment.UserName}", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(accountName, WindowsIdentity.GetCurrent().Name, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(cleanAccount, Environment.UserName, StringComparison.OrdinalIgnoreCase);

                if (_adService.IsDomainJoined && !string.IsNullOrWhiteSpace(_adService.CurrentDomainName))
                {
                    try
                    {
                        using var entry = new DirectoryEntry($"LDAP://{_adService.CurrentDomainName}");
                        
                        // Search for both user (person) or group
                        using var searcher = new DirectorySearcher(entry)
                        {
                            PageSize = 500,
                            Filter = $"(|(&(objectCategory=person)(sAMAccountName={cleanAccount}))(&(objectCategory=group)(sAMAccountName={cleanAccount})))"
                        };
                        searcher.PropertiesToLoad.AddRange(new[] { "distinguishedName", "memberOf", "objectSid", "sAMAccountName", "displayName", "objectClass" });

                        var targetResult = searcher.FindOne();
                        if (targetResult != null)
                        {
                            var targetDn = targetResult.Properties["distinguishedName"][0]?.ToString();
                            var directDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            if (targetResult.Properties.Contains("memberOf"))
                            {
                                foreach (var mo in targetResult.Properties["memberOf"])
                                {
                                    if (mo != null) directDns.Add(mo.ToString()!);
                                }
                            }

                            bool isGroupTarget = false;
                            if (targetResult.Properties.Contains("objectClass"))
                            {
                                foreach (var oc in targetResult.Properties["objectClass"])
                                {
                                    if (string.Equals(oc?.ToString(), "group", StringComparison.OrdinalIgnoreCase))
                                    {
                                        isGroupTarget = true;
                                        break;
                                    }
                                }
                            }

                            // If target itself is a group, add it as the primary direct group
                            if (isGroupTarget)
                            {
                                var gSam = targetResult.Properties["sAMAccountName"][0]?.ToString() ?? cleanAccount;
                                var gDisp = targetResult.Properties.Contains("displayName") && targetResult.Properties["displayName"].Count > 0
                                    ? targetResult.Properties["displayName"][0]?.ToString() ?? gSam
                                    : gSam;

                                string sidStr = "";
                                if (targetResult.Properties.Contains("objectSid") && targetResult.Properties["objectSid"].Count > 0)
                                {
                                    try
                                    {
                                        var sidBytes = (byte[])targetResult.Properties["objectSid"][0];
                                        sidStr = new SecurityIdentifier(sidBytes, 0).Value;
                                    }
                                    catch { }
                                }

                                memberships.Add(new PrincipalGroupMembership
                                {
                                    GroupName = gSam,
                                    DisplayName = gDisp,
                                    Sid = sidStr,
                                    IsDirect = true,
                                    NestingDepth = 0,
                                    MembershipPath = "対象グループ自身"
                                });
                            }

                            if (!string.IsNullOrEmpty(targetDn))
                            {
                                // LDAP_MATCHING_RULE_IN_CHAIN (1.2.840.113556.1.4.1941)
                                // Recursively retrieves ALL groups the principal belongs to across arbitrary nesting depth
                                using var groupSearcher = new DirectorySearcher(entry)
                                {
                                    PageSize = 500,
                                    Filter = $"(member:1.2.840.113556.1.4.1941:={targetDn})"
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

                            if (memberships.Count > 0)
                            {
                                return (
                                    memberships.OrderByDescending(m => m.IsDirect).ThenBy(m => m.GroupName).ToList(),
                                    EffectiveAccessResolutionMode.ActiveDirectory,
                                    $"AD多重ネスト解決完了 ({memberships.Count} グループ)"
                                );
                            }
                        }
                    }
                    catch
                    {
                        // Fallback handling below
                    }
                }

                // If AD search didn't yield groups, ONLY use WindowsIdentity if the target IS the currently logged-on user!
                if (isCurrentLogonUser)
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
                                        MembershipPath = "ローカル所属 (実行ユーザー)"
                                    });
                                }
                                catch { }
                            }
                        }

                        if (memberships.Count > 0)
                        {
                            return (
                                memberships.OrderByDescending(m => m.IsDirect).ThenBy(m => m.GroupName).ToList(),
                                EffectiveAccessResolutionMode.CurrentLogonUserLocal,
                                $"実行中ユーザー ({Environment.UserName}) の所属グループを使用 ({memberships.Count} グループ)"
                            );
                        }
                    }
                    catch { }
                }

                // When investigating a DIFFERENT account and AD resolution failed:
                // DO NOT impersonate or contaminate with current user's groups!
                // Strictly evaluate direct ACL entries granted to targetAccount itself.
                return (
                    memberships,
                    EffectiveAccessResolutionMode.DirectAclOnly,
                    $"ADグループ未解決 (対象 '{accountName}' の直接付与ACEのみ判定)"
                );
            });
        }

        public async Task<List<PrincipalGroupMembership>> GetGroupMembershipsAsync(string accountName)
        {
            var res = await ResolveMembershipsAsync(accountName);
            return res.Memberships;
        }

        /// <summary>
        /// Recursively audits the target root folder and finds all folders accessible by targetAccount and its resolved groups.
        /// Defaults to unlimited search depth (int.MaxValue) with cooperative cancellation.
        /// </summary>
        public async Task<EffectiveAccessAuditReport> ScanEffectiveAccessAsync(
            string rootPath,
            string targetAccount,
            List<PrincipalGroupMembership>? preResolvedGroups = null,
            int maxDepth = int.MaxValue,
            IProgress<(int scanned, int found)>? progress = null,
            CancellationToken ct = default)
        {
            return await Task.Run(async () =>
            {
                var dir = new DirectoryInfo(rootPath);
                if (!dir.Exists) throw new DirectoryNotFoundException($"指定フォルダが存在しません: {rootPath}");

                var (memberships, resMode, resStatus) = preResolvedGroups != null
                    ? (preResolvedGroups, EffectiveAccessResolutionMode.ActiveDirectory, "事前解決済みグループセットを使用")
                    : await ResolveMembershipsAsync(targetAccount);

                var report = new EffectiveAccessAuditReport
                {
                    TargetAccountName = targetAccount,
                    TargetDisplayName = targetAccount,
                    RootFolderPath = dir.FullName,
                    ScanTimestamp = DateTime.Now,
                    GroupMemberships = memberships,
                    ResolutionMode = resMode,
                    ResolutionStatusText = resStatus
                };

                // Build lookup maps for fast matching
                var targetNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                targetNames.Add(targetAccount);

                var cleanTarget = targetAccount.Contains('\\') ? targetAccount.Split('\\')[1] : targetAccount;
                // M1対策: アカウントに明示的なドメイン指定がない場合のみクリーン名（ユーザー名単体）をフォールバックとして許容
                // （DOMAIN\User が指定されている場合にローカルPCの同名 User と誤爆しないよう保護）
                if (!targetAccount.Contains('\\'))
                {
                    targetNames.Add(cleanTarget);
                }

                var groupMap = new Dictionary<string, PrincipalGroupMembership>(StringComparer.OrdinalIgnoreCase);
                foreach (var g in memberships)
                {
                    groupMap[g.GroupName] = g;
                    if (!string.IsNullOrEmpty(g.DisplayName)) groupMap[g.DisplayName] = g;
                    if (!string.IsNullOrEmpty(g.Sid)) groupMap[g.Sid] = g;
                    var cleanG = g.GroupName.Contains('\\') ? g.GroupName.Split('\\')[1] : g.GroupName;
                    if (!groupMap.ContainsKey(cleanG)) groupMap[cleanG] = g;
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

                            // M5対策: ジャンクションやシンボリックリンクによる無限循環・重複スキャンを防止
                            if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                            {
                                continue;
                            }

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
        /// Core evaluation logic adhering to Windows Canonical DACL Ordering:
        /// 1. Explicit Deny ACEs
        /// 2. Explicit Allow ACEs
        /// 3. Inherited Deny ACEs
        /// 4. Inherited Allow ACEs
        /// 
        /// Under Windows AccessCheck rules:
        /// - Explicit Allow rules precede Inherited Deny rules (explicit child permissions take precedence over inherited parent denies).
        /// - Explicit Deny rules override all Allow rules for matching rights bits.
        /// </summary>
        public EffectiveFolderAccessItem? EvaluateEffectiveAccessOnAcl(
            FileSystemSecurity sec,
            string targetAccount,
            HashSet<string> targetNames,
            Dictionary<string, PrincipalGroupMembership> groupMap,
            string folderPath,
            string folderName)
        {
            var rawRules = sec.GetAccessRules(true, true, typeof(NTAccount));

            var explicitDenyRules = new List<FileSystemAccessRule>();
            var explicitAllowRules = new List<FileSystemAccessRule>();
            var inheritedDenyRules = new List<FileSystemAccessRule>();
            var inheritedAllowRules = new List<FileSystemAccessRule>();

            foreach (FileSystemAccessRule rule in rawRules)
            {
                var id = rule.IdentityReference.Value;
                var idClean = id.Contains('\\') ? id.Split('\\')[1] : id;

                // M1対策: targetAccount に明示的なドメイン修飾がある場合は完全一致のみ、ドメイン未指定の場合のみクリーン名(idClean)での照合を許容
                bool isDirectMatch = targetNames.Contains(id) || (!targetAccount.Contains('\\') && targetNames.Contains(idClean));
                bool isGroupMatch = groupMap.ContainsKey(id) || groupMap.ContainsKey(idClean);
                bool isSpecialPrincipal = IsSpecialWorldPrincipal(idClean);

                if (!isDirectMatch && !isGroupMatch && !isSpecialPrincipal)
                {
                    continue;
                }

                // CRITICAL: If an ACE has InheritOnly propagation flag, it applies ONLY to child items, NOT to this container itself!
                if (rule.PropagationFlags.HasFlag(PropagationFlags.InheritOnly))
                {
                    continue;
                }

                if (!rule.IsInherited)
                {
                    if (rule.AccessControlType == AccessControlType.Deny) explicitDenyRules.Add(rule);
                    else explicitAllowRules.Add(rule);
                }
                else
                {
                    if (rule.AccessControlType == AccessControlType.Deny) inheritedDenyRules.Add(rule);
                    else inheritedAllowRules.Add(rule);
                }
            }

            if (explicitDenyRules.Count == 0 && explicitAllowRules.Count == 0 &&
                inheritedDenyRules.Count == 0 && inheritedAllowRules.Count == 0)
            {
                return null;
            }

            // Step 1: Explicit Deny
            FileSystemRights explicitDenied = 0;
            foreach (var r in explicitDenyRules)
            {
                explicitDenied |= r.FileSystemRights;
            }

            // Step 2: Explicit Allow (Rights not blocked by Explicit Deny)
            FileSystemRights explicitAllowed = 0;
            var grantSources = new List<string>();
            var grantTraces = new List<string>();

            foreach (var r in explicitAllowRules)
            {
                var effectiveBits = r.FileSystemRights & ~explicitDenied;
                if (effectiveBits != 0)
                {
                    explicitAllowed |= effectiveBits;
                    RecordGrantTrace(r, targetNames, groupMap, grantSources, grantTraces, isInherited: false);
                }
            }

            // Step 3: Inherited Deny
            // CRUCIAL: Inherited Deny cannot revoke permissions explicitly granted on this object
            FileSystemRights effectiveInheritedDenied = 0;
            foreach (var r in inheritedDenyRules)
            {
                effectiveInheritedDenied |= (r.FileSystemRights & ~explicitAllowed);
            }

            FileSystemRights totalDenied = explicitDenied | effectiveInheritedDenied;

            // Step 4: Inherited Allow (Rights not blocked by totalDenied)
            FileSystemRights inheritedAllowed = 0;
            foreach (var r in inheritedAllowRules)
            {
                var effectiveBits = r.FileSystemRights & ~totalDenied;
                if (effectiveBits != 0)
                {
                    inheritedAllowed |= effectiveBits;
                    RecordGrantTrace(r, targetNames, groupMap, grantSources, grantTraces, isInherited: true);
                }
            }

            FileSystemRights totalEffectiveRights = explicitAllowed | inheritedAllowed;
            if (totalEffectiveRights == 0) return null;

            var level = DeterminePermissionLevel(totalEffectiveRights);
            if (level == EffectivePermissionLevel.None) return null;

            bool isInheritedOnly = (explicitAllowed == 0 && inheritedAllowed != 0);

            return new EffectiveFolderAccessItem
            {
                FolderPath = folderPath,
                FolderName = folderName,
                PermissionLevel = level,
                AllowedRights = totalEffectiveRights,
                DeniedRights = totalDenied,
                HasDeny = totalDenied != 0,
                IsInherited = isInheritedOnly,
                GrantSource = grantSources.Distinct().FirstOrDefault() ?? "付与",
                GrantPathTrace = string.Join(" / ", grantTraces.Distinct())
            };
        }

        private static void RecordGrantTrace(
            FileSystemAccessRule rule,
            HashSet<string> targetNames,
            Dictionary<string, PrincipalGroupMembership> groupMap,
            List<string> grantSources,
            List<string> grantTraces,
            bool isInherited)
        {
            var id = rule.IdentityReference.Value;
            var idClean = id.Contains('\\') ? id.Split('\\')[1] : id;

            bool isDirect = targetNames.Contains(id) || targetNames.Contains(idClean);
            bool isGroup = groupMap.TryGetValue(id, out var matchedGroup) || groupMap.TryGetValue(idClean, out matchedGroup);
            bool isSpecial = IsSpecialWorldPrincipal(idClean);

            var inhStr = isInherited ? " (継承)" : "";

            if (isDirect)
            {
                grantSources.Add($"👤 直接付与 ({idClean}){inhStr}");
                grantTraces.Add($"Direct: {idClean}{inhStr}");
            }
            else if (isGroup && matchedGroup != null)
            {
                var badge = matchedGroup.IsDirect ? "👥" : "👥🔗";
                var depthStr = matchedGroup.IsDirect ? "" : $" (深度 {matchedGroup.NestingDepth})";
                grantSources.Add($"{badge} {matchedGroup.GroupName} 経由{depthStr}{inhStr}");
                grantTraces.Add($"{matchedGroup.MembershipPath}{inhStr}");
            }
            else if (isSpecial)
            {
                grantSources.Add($"🌐 {idClean} 経由{inhStr}");
                grantTraces.Add($"Special: {idClean}{inhStr}");
            }
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
