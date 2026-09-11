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

        /// <summary>
        /// テスト用ACLリーダーフック（通常時はnull、テスト時に特定パスのアクセス拒否をシミュレート可能）
        /// </summary>
        internal Func<DirectoryInfo, FileSystemSecurity?>? AclReaderHook { get; set; }

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
                                    MembershipPath = Strings.RevTargetGroupSelf
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

                                    var domainPrefixedName = !string.IsNullOrEmpty(_adService.CurrentDomainName)
                                        ? $"{_adService.CurrentDomainName.Split('.')[0]}\\{gSam}"
                                        : gSam;

                                    memberships.Add(new PrincipalGroupMembership
                                    {
                                        GroupName = domainPrefixedName,
                                        DisplayName = string.IsNullOrEmpty(gDisp) ? gSam : gDisp,
                                        Sid = sidStr,
                                        IsDirect = isDirect,
                                        NestingDepth = isDirect ? 1 : 2,
                                        MembershipPath = isDirect ? Strings.RevDirectMembership : string.Format(Strings.RevNestedMembership, 2)
                                    });
                                }
                            }

                            if (memberships.Count > 0)
                            {
                                return (
                                    memberships.OrderByDescending(m => m.IsDirect).ThenBy(m => m.GroupName).ToList(),
                                    EffectiveAccessResolutionMode.ActiveDirectory,
                                    string.Format(Strings.RevResAdConnected, memberships.Count)
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
                        var wi = WindowsIdentity.GetCurrent();
                        if (wi.Groups != null)
                        {
                            foreach (var groupRef in wi.Groups)
                            {
                                try
                                {
                                    var ntAccount = (NTAccount)groupRef.Translate(typeof(NTAccount));
                                    memberships.Add(new PrincipalGroupMembership
                                    {
                                        GroupName = ntAccount.Value,
                                        DisplayName = ntAccount.Value.Contains('\\') ? ntAccount.Value.Split('\\')[1] : ntAccount.Value,
                                        Sid = groupRef.Value,
                                        IsDirect = true,
                                        NestingDepth = 1,
                                        MembershipPath = Strings.RevDirectMembership
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
                                string.Format(Strings.RevResCurrentLogonUser + " ({0})", memberships.Count)
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
                    Strings.RevResLocalOnly
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
                if (!dir.Exists) throw new DirectoryNotFoundException(string.Format(Strings.RevErrFolderNotFound, rootPath));

                var (memberships, resMode, resStatus) = preResolvedGroups != null
                    ? (preResolvedGroups, EffectiveAccessResolutionMode.ActiveDirectory, Strings.RevResPreResolved)
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
                    // ドメイン修飾がない場合のみクリーン名をキーに追加（同名ローカルグループとの誤爆防止）
                    if (!g.GroupName.Contains('\\'))
                    {
                        var cleanG = g.GroupName.Contains('\\') ? g.GroupName.Split('\\')[1] : g.GroupName;
                        if (!groupMap.ContainsKey(cleanG)) groupMap[cleanG] = g;
                    }
                }

                int scannedCount = 0;
                int foundCount = 0;

                void Traverse(DirectoryInfo currentDir, int depth, EffectiveFolderAccessItem? parentItem)
                {
                    if (ct.IsCancellationRequested) return;

                    scannedCount++;
                    if (scannedCount % 25 == 0)
                    {
                        progress?.Report((scannedCount, foundCount));
                    }

                    EffectiveFolderAccessItem? currentItem = null;
                    bool scanFailed = false;

                    try
                    {
                        var sec = AclReaderHook != null
                            ? (AclReaderHook(currentDir) ?? currentDir.GetAccessControl(AccessControlSections.Access))
                            : currentDir.GetAccessControl(AccessControlSections.Access);
                        currentItem = EvaluateEffectiveAccessOnAcl(sec, targetAccount, targetNames, groupMap, currentDir.FullName, currentDir.Name);
                    }
                    catch
                    {
                        // アクセス権取得失敗 (UnauthorizedAccessException または Restricted)
                        scanFailed = true;
                    }

                    // 変化点（基準点・飛び地・遮断・走査不能・明示化境界・権限変更・通常継承）の判定
                    if (scanFailed)
                    {
                        // ⚠️ 走査不能 (管理者自身の権限不足・排他ロック・ネットワークエラー等)
                        var unavailItem = new EffectiveFolderAccessItem
                        {
                            FolderPath = currentDir.FullName,
                            FolderName = currentDir.Name,
                            PermissionLevel = EffectivePermissionLevel.None,
                            AllowedRights = 0,
                            DeniedRights = 0,
                            HasDeny = false,
                            IsInherited = false,
                            ChangeType = EffectiveAccessChangeType.ScanUnavailable,
                            GrantSource = Strings.RevChangeUnavailable,
                            GrantPathTrace = Strings.RevTraceUnavailable
                        };

                        lock (report.UnavailableFolders)
                        {
                            report.UnavailableFolders.Add(unavailItem);
                        }

                        // 親として配下の子に渡すため currentItem を unavailItem に設定
                        currentItem = unavailItem;
                    }
                    else if (currentItem != null)
                    {
                        if (depth == 0)
                        {
                            // 🏁 基準点: 走査ルート自身（親が存在しないため飛び地ではなくBaseline）
                            currentItem.ChangeType = EffectiveAccessChangeType.Baseline;
                        }
                        else if (parentItem != null && parentItem.ChangeType == EffectiveAccessChangeType.ScanUnavailable)
                        {
                            // ❓ 判定不能: 親フォルダーが走査不能だったため、飛び地か通常継承か判定不能
                            currentItem.ChangeType = EffectiveAccessChangeType.Unknown;
                            currentItem.GrantPathTrace = Strings.RevTraceParentUnavailable;
                        }
                        else if (parentItem == null || parentItem.PermissionLevel == EffectivePermissionLevel.None)
                        {
                            // 🚨 飛び地 (獲得): 親はアクセス不可だったが、このフォルダでアクセス権を獲得！
                            currentItem.ChangeType = EffectiveAccessChangeType.EnclaveGranted;
                            lock (report.AccessibleFolders) { report.EnclaveCount++; }
                        }
                        else
                        {
                            // 親もアクセス可能だった場合
                            bool sameRights = currentItem.PermissionLevel == parentItem.PermissionLevel
                                           && currentItem.AllowedRights == parentItem.AllowedRights;

                            if (sameRights)
                            {
                                if (currentItem.IsInherited)
                                {
                                    // 🔗 通常継承: 親と同一権限をそのまま継承
                                    currentItem.ChangeType = EffectiveAccessChangeType.InheritedSame;
                                }
                                else
                                {
                                    // 🔧 明示化境界: 実効権限は同一だが明示ACE化された境界
                                    currentItem.ChangeType = EffectiveAccessChangeType.ExplicitBoundary;
                                    lock (report.AccessibleFolders) { report.ExplicitBoundaryCount++; }
                                }
                            }
                            else
                            {
                                // ⚡ 権限変更: 親と異なる権限レベルへ昇格または変更
                                currentItem.ChangeType = EffectiveAccessChangeType.PermissionChanged;
                            }
                        }

                        lock (report.AccessibleFolders)
                        {
                            report.AccessibleFolders.Add(currentItem);
                            foundCount++;
                            if (currentItem.PermissionLevel == EffectivePermissionLevel.FullControl) report.FullControlCount++;
                            else if (currentItem.PermissionLevel == EffectivePermissionLevel.Modify) report.ModifyCount++;
                            else report.ReadOnlyCount++;
                        }
                    }
                    else if (parentItem != null && parentItem.PermissionLevel != EffectivePermissionLevel.None)
                    {
                        // ⛔ 遮断（権限消失）: 親ではアクセスできたのに、この階層で継承切断またはDenyによりアクセス権が消失！
                        var severedItem = new EffectiveFolderAccessItem
                        {
                            FolderPath = currentDir.FullName,
                            FolderName = currentDir.Name,
                            PermissionLevel = EffectivePermissionLevel.None,
                            AllowedRights = 0,
                            DeniedRights = 0,
                            HasDeny = true,
                            IsInherited = false,
                            ChangeType = EffectiveAccessChangeType.InheritanceSevered,
                            GrantSource = Strings.RevGrantSeveredSource,
                            GrantPathTrace = string.Format(Strings.RevTraceSeveredFormat, parentItem.FolderName, parentItem.FormattedRights)
                        };

                        lock (report.SeveredFolders)
                        {
                            report.SeveredFolders.Add(severedItem);
                        }
                    }
                    // ※親が ScanUnavailable で子のACLが読め、かつ対象アカウントにアクセス権がない場合（currentItem == null）は、
                    // 通常のアクセス不能フォルダーと同様にレポーティング対象外とする（数万件のアクセス不能フォルダによるツリー肥大化・氾濫の防止）。

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

                            Traverse(sub, depth + 1, currentItem);
                        }
                    }
                    catch { }
                }

                Traverse(dir, 0, null);

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

                // SIDへの変換を試みる（SIDによる絶対的一意照合）
                string? ruleSid = null;
                try
                {
                    ruleSid = rule.IdentityReference.Translate(typeof(SecurityIdentifier)).Value;
                }
                catch { }

                // 1. 直接付与チェック (アカウント名完全一致、またはドメイン未指定時のクリーン名一致、またはSID一致)
                bool isDirectMatch = targetNames.Contains(id) ||
                                     (!targetAccount.Contains('\\') && targetNames.Contains(idClean)) ||
                                     (!string.IsNullOrEmpty(ruleSid) && targetNames.Contains(ruleSid));

                // 2. グループ所属チェック (SID一致最優先 -> 完全修飾名一致 -> ドメイン未指定ACEの場合のみクリーン名一致)
                bool isGroupMatch = false;
                if (!string.IsNullOrEmpty(ruleSid) && groupMap.ContainsKey(ruleSid))
                {
                    isGroupMatch = true;
                }
                else if (groupMap.ContainsKey(id))
                {
                    isGroupMatch = true;
                }
                else if (!id.Contains('\\') && groupMap.ContainsKey(idClean))
                {
                    isGroupMatch = true;
                }

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
                GrantSource = grantSources.Distinct().FirstOrDefault() ?? Strings.RevGrantDefault,
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

            string? ruleSid = null;
            try
            {
                ruleSid = rule.IdentityReference.Translate(typeof(SecurityIdentifier)).Value;
            }
            catch { }

            bool isDirect = targetNames.Contains(id) ||
                            targetNames.Contains(idClean) ||
                            (!string.IsNullOrEmpty(ruleSid) && targetNames.Contains(ruleSid));

            PrincipalGroupMembership? matchedGroup = null;
            if (!string.IsNullOrEmpty(ruleSid) && groupMap.TryGetValue(ruleSid, out var mgSid))
            {
                matchedGroup = mgSid;
            }
            else if (groupMap.TryGetValue(id, out var mgFull))
            {
                matchedGroup = mgFull;
            }
            else if (!id.Contains('\\') && groupMap.TryGetValue(idClean, out var mgClean))
            {
                matchedGroup = mgClean;
            }

            bool isGroup = matchedGroup != null;
            bool isSpecial = IsSpecialWorldPrincipal(idClean);

            var inhStr = isInherited ? $" {Strings.RevGrantInherited}" : "";

            if (isDirect)
            {
                grantSources.Add($"{Strings.RevGrantDirect} ({idClean}){inhStr}");
                grantTraces.Add($"Direct: {idClean}{inhStr}");
            }
            else if (isGroup && matchedGroup != null)
            {
                var badge = matchedGroup.IsDirect ? "👥" : "👥🔗";
                var depthStr = matchedGroup.IsDirect ? "" : $" ({Strings.RevGrantDepth} {matchedGroup.NestingDepth})";
                grantSources.Add($"{badge} {matchedGroup.GroupName} {Strings.RevGrantVia}{depthStr}{inhStr}");
                grantTraces.Add($"{matchedGroup.MembershipPath}{inhStr}");
            }
            else if (isSpecial)
            {
                grantSources.Add($"🌐 {idClean} {Strings.RevGrantVia}{inhStr}");
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
