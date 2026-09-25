using System;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Linq;
using System.Net.NetworkInformation;
using System.Security.Principal;
using System.Threading.Tasks;
using AstraSize.Models;

namespace AstraSize.Services
{
    public class ActiveDirectoryService
    {
        private static readonly List<AdPrincipalItem> PresetEnterpriseRoles = new()
        {
            new AdPrincipalItem { AccountName = "Domain Admins", DisplayName = "ドメイン管理者 (Domain Admins)", PrincipalType = AdPrincipalType.Group, Domain = "CORP", Description = "システム全権限" },
            new AdPrincipalItem { AccountName = "Management_G", DisplayName = "役員会・経営企画 (Management_G)", PrincipalType = AdPrincipalType.Group, Domain = "CORP", Description = "役員・重要機密アクセス" },
            new AdPrincipalItem { AccountName = "GeneralAffairs_G", DisplayName = "総務部 (GeneralAffairs_G)", PrincipalType = AdPrincipalType.Group, Domain = "CORP", Description = "総務・庶務グループ" },
            new AdPrincipalItem { AccountName = "Accounting_G", DisplayName = "経理財務部 (Accounting_G)", PrincipalType = AdPrincipalType.Group, Domain = "CORP", Description = "会計・財務データ" },
            new AdPrincipalItem { AccountName = "Sales_Division_G", DisplayName = "営業統括部 (Sales_Division_G)", PrincipalType = AdPrincipalType.Group, Domain = "CORP", Description = "全営業グループ" },
            new AdPrincipalItem { AccountName = "Dev_Engineering_G", DisplayName = "開発エンジニアリング (Dev_Engineering_G)", PrincipalType = AdPrincipalType.Group, Domain = "CORP", Description = "技術・プロダクト開発" },
            new AdPrincipalItem { AccountName = "HR_Personnel_G", DisplayName = "人事部 (HR_Personnel_G)", PrincipalType = AdPrincipalType.Group, Domain = "CORP", Description = "人事評価・採用データ" },
            new AdPrincipalItem { AccountName = "Internal_Audit_G", DisplayName = "内部監査室 (Internal_Audit_G)", PrincipalType = AdPrincipalType.Group, Domain = "CORP", Description = "読取専用・監査権限" },
            new AdPrincipalItem { AccountName = "All_Employees_G", DisplayName = "全社員 (All_Employees_G)", PrincipalType = AdPrincipalType.Group, Domain = "CORP", Description = "全社共通ドキュメント" },
            new AdPrincipalItem { AccountName = "tanaka.taro", DisplayName = "田中 太郎 (部長)", PrincipalType = AdPrincipalType.User, Domain = "CORP", Description = "総務部 部長" },
            new AdPrincipalItem { AccountName = "sato.hanako", DisplayName = "佐藤 花子 (マネージャー)", PrincipalType = AdPrincipalType.User, Domain = "CORP", Description = "営業1課 マネージャー" },
            new AdPrincipalItem { AccountName = "suzuki.ichiro", DisplayName = "鈴木 一郎 (リード)", PrincipalType = AdPrincipalType.User, Domain = "CORP", Description = "開発部 リードエンジニア" }
        };

        public bool IsDomainJoined { get; private set; }
        public string CurrentDomainName { get; private set; } = string.Empty;

        public ActiveDirectoryService()
        {
            try
            {
                var domain = IPGlobalProperties.GetIPGlobalProperties().DomainName;
                if (!string.IsNullOrWhiteSpace(domain))
                {
                    CurrentDomainName = domain;
                    IsDomainJoined = true;
                }
            }
            catch
            {
                IsDomainJoined = false;
            }
        }

        /// <summary>
        /// Search AD users and groups asynchronously with intelligent fallback
        /// </summary>
        public async Task<List<AdPrincipalItem>> SearchPrincipalsAsync(string filter = "", bool includeUsers = true, bool includeGroups = true)
        {
            return await Task.Run(() =>
            {
                var results = new List<AdPrincipalItem>();

                // Try AD lookup if domain is present
                if (IsDomainJoined)
                {
                    try
                    {
                        using var entry = new DirectoryEntry($"LDAP://{CurrentDomainName}");
                        using var searcher = new DirectorySearcher(entry);
                        searcher.PageSize = 50;

                        var searchFilter = BuildLdapFilter(filter, includeUsers, includeGroups);
                        searcher.Filter = searchFilter;
                        searcher.PropertiesToLoad.AddRange(new[] { "sAMAccountName", "displayName", "objectClass", "description" });

                        var searchResults = searcher.FindAll();
                        foreach (SearchResult sr in searchResults)
                        {
                            var sam = GetProperty(sr, "sAMAccountName");
                            if (string.IsNullOrEmpty(sam)) continue;

                            var disp = GetProperty(sr, "displayName");
                            var desc = GetProperty(sr, "description");
                            var objClasses = sr.Properties["objectClass"];
                            bool isGroup = false;
                            if (objClasses != null)
                            {
                                foreach (var oc in objClasses)
                                {
                                    if (oc?.ToString()?.Equals("group", StringComparison.OrdinalIgnoreCase) == true)
                                    {
                                        isGroup = true;
                                        break;
                                    }
                                }
                            }

                            results.Add(new AdPrincipalItem
                            {
                                AccountName = sam,
                                DisplayName = string.IsNullOrEmpty(disp) ? sam : $"{disp} ({sam})",
                                PrincipalType = isGroup ? AdPrincipalType.Group : AdPrincipalType.User,
                                Domain = CurrentDomainName.Split('.').FirstOrDefault()?.ToUpperInvariant() ?? "DOMAIN",
                                Description = desc
                            });
                        }
                    }
                    catch
                    {
                        // Domain lookup failed (offline, firewalled, or permission restricted)
                        // Fall back gracefully to local/preset
                    }
                }

                // If no results from AD or not in domain, do NOT supply dummy enterprise roles
                // Return empty list so UI displays proper guidance for local PC environment
                return results
                    .OrderBy(p => p.PrincipalType == AdPrincipalType.Group ? 0 : 1)
                    .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });
        }

        private static string BuildLdapFilter(string keyword, bool includeUsers, bool includeGroups)
        {
            var cleanKeyword = string.IsNullOrWhiteSpace(keyword) ? "*" : $"*{keyword.Trim()}*";
            var typeClause = "";
            if (includeUsers && includeGroups)
            {
                typeClause = "(|(objectCategory=person)(objectCategory=group))";
            }
            else if (includeUsers)
            {
                typeClause = "(objectCategory=person)";
            }
            else if (includeGroups)
            {
                typeClause = "(objectCategory=group)";
            }

            return $"(&{typeClause}(|(sAMAccountName={cleanKeyword})(displayName={cleanKeyword})))";
        }

        private static string GetProperty(SearchResult sr, string propName)
        {
            if (sr.Properties.Contains(propName) && sr.Properties[propName].Count > 0)
            {
                return sr.Properties[propName][0]?.ToString() ?? string.Empty;
            }
            return string.Empty;
        }

        /// <summary>
        /// Get hierarchical tree of OUs and containers with graceful local fallback
        /// </summary>
        public async Task<List<FolderMorpher.Models.AdOuNode>> GetOuHierarchyAsync()
        {
            return await Task.Run(() =>
            {
                var roots = new List<FolderMorpher.Models.AdOuNode>();

                if (IsDomainJoined)
                {
                    try
                    {
                        var domainRootNode = new FolderMorpher.Models.AdOuNode
                        {
                            Name = CurrentDomainName.ToUpperInvariant(),
                            DistinguishedName = $"LDAP://{CurrentDomainName}",
                            NodeType = FolderMorpher.Models.AdOuNodeType.DomainRoot,
                            IsExpanded = true
                        };

                        using var entry = new DirectoryEntry($"LDAP://{CurrentDomainName}");
                        using var searcher = new DirectorySearcher(entry)
                        {
                            PageSize = 250,
                            SearchScope = SearchScope.Subtree,
                            Filter = "(|(objectCategory=organizationalUnit)(objectClass=container))"
                        };
                        searcher.PropertiesToLoad.AddRange(new[] { "distinguishedName", "name", "ou", "cn", "objectClass" });

                        var rawNodes = new List<(string dn, string name, bool isOu)>();
                        foreach (SearchResult sr in searcher.FindAll())
                        {
                            var dn = GetProperty(sr, "distinguishedName");
                            if (string.IsNullOrEmpty(dn)) continue;

                            var name = GetProperty(sr, "name");
                            if (string.IsNullOrEmpty(name)) name = GetProperty(sr, "ou");
                            if (string.IsNullOrEmpty(name)) name = GetProperty(sr, "cn");
                            if (string.IsNullOrEmpty(name)) continue;

                            bool isOu = false;
                            var objClasses = sr.Properties["objectClass"];
                            if (objClasses != null)
                            {
                                foreach (var oc in objClasses)
                                {
                                    if (oc?.ToString()?.Equals("organizationalUnit", StringComparison.OrdinalIgnoreCase) == true)
                                    {
                                        isOu = true;
                                        break;
                                    }
                                }
                            }

                            rawNodes.Add((dn, name, isOu));
                        }

                        // Build tree hierarchy from distinguishedNames
                        var nodeMap = new Dictionary<string, FolderMorpher.Models.AdOuNode>(StringComparer.OrdinalIgnoreCase);

                        // Sort by DN depth (fewest RDNs first)
                        var sorted = rawNodes
                            .OrderBy(n => n.dn.Split(',').Length)
                            .ThenBy(n => n.name)
                            .ToList();

                        foreach (var (dn, name, isOu) in sorted)
                        {
                            var node = new FolderMorpher.Models.AdOuNode
                            {
                                Name = name,
                                DistinguishedName = dn,
                                NodeType = isOu ? FolderMorpher.Models.AdOuNodeType.OrganizationalUnit : FolderMorpher.Models.AdOuNodeType.Container
                            };
                            nodeMap[dn] = node;

                            // Find parent DN (strip first component)
                            var commaIdx = dn.IndexOf(',');
                            string? parentDn = commaIdx >= 0 ? dn.Substring(commaIdx + 1) : null;

                            if (parentDn != null && nodeMap.TryGetValue(parentDn, out var parentNode))
                            {
                                parentNode.Children.Add(node);
                            }
                            else
                            {
                                // Attach to domain root
                                domainRootNode.Children.Add(node);
                            }
                        }

                        roots.Add(domainRootNode);
                        return roots;
                    }
                    catch
                    {
                        // Domain query failed, fall back to local machine structure
                    }
                }

                // Fallback: Local Machine Structure
                var localMachineNode = new FolderMorpher.Models.AdOuNode
                {
                    Name = $"{Environment.MachineName} (ローカルPC)",
                    DistinguishedName = "local:root",
                    NodeType = FolderMorpher.Models.AdOuNodeType.LocalMachine,
                    IsExpanded = true
                };

                var groupsNode = new FolderMorpher.Models.AdOuNode
                {
                    Name = "👥 ローカルグループ",
                    DistinguishedName = "local:groups",
                    NodeType = FolderMorpher.Models.AdOuNodeType.LocalCategory,
                    IsExpanded = true
                };

                var usersNode = new FolderMorpher.Models.AdOuNode
                {
                    Name = "👤 ローカルユーザー",
                    DistinguishedName = "local:users",
                    NodeType = FolderMorpher.Models.AdOuNodeType.LocalCategory,
                    IsExpanded = true
                };

                localMachineNode.Children.Add(groupsNode);
                localMachineNode.Children.Add(usersNode);
                roots.Add(localMachineNode);

                return roots;
            });
        }

        /// <summary>
        /// Get users and groups directly under the selected OU or local container
        /// </summary>
        public async Task<List<AdPrincipalItem>> GetPrincipalsInOuAsync(string ouDistinguishedName, string keyword = "", bool includeUsers = true, bool includeGroups = true)
        {
            return await Task.Run(() =>
            {
                var results = new List<AdPrincipalItem>();

                // Handle local machine category
                if (ouDistinguishedName.StartsWith("local:", StringComparison.OrdinalIgnoreCase))
                {
                    bool queryGroups = ouDistinguishedName.Equals("local:groups", StringComparison.OrdinalIgnoreCase) || ouDistinguishedName.Equals("local:root", StringComparison.OrdinalIgnoreCase);
                    bool queryUsers = ouDistinguishedName.Equals("local:users", StringComparison.OrdinalIgnoreCase) || ouDistinguishedName.Equals("local:root", StringComparison.OrdinalIgnoreCase);

                    try
                    {
                        using var localMachine = new DirectoryEntry($"WinNT://{Environment.MachineName}");
                        foreach (DirectoryEntry child in localMachine.Children)
                        {
                            try
                            {
                                var schema = child.SchemaClassName;
                                bool isGroup = schema.Equals("Group", StringComparison.OrdinalIgnoreCase);
                                bool isUser = schema.Equals("User", StringComparison.OrdinalIgnoreCase);

                                if ((isGroup && queryGroups && includeGroups) || (isUser && queryUsers && includeUsers))
                                {
                                    var name = child.Name;
                                    var desc = child.Properties["Description"]?.Value?.ToString() ?? "";

                                    if (!string.IsNullOrWhiteSpace(keyword))
                                    {
                                        if (!name.Contains(keyword, StringComparison.OrdinalIgnoreCase) &&
                                            !desc.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                                        {
                                            continue;
                                        }
                                    }

                                    results.Add(new AdPrincipalItem
                                    {
                                        AccountName = name,
                                        DisplayName = $"{name} ({Environment.MachineName})",
                                        PrincipalType = isGroup ? AdPrincipalType.Group : AdPrincipalType.User,
                                        Domain = Environment.MachineName,
                                        Description = desc
                                    });
                                }
                            }
                            catch { }
                        }
                    }
                    catch
                    {
                        // Fallback presets if WinNT enumeration fails
                        if (queryGroups && includeGroups)
                        {
                            results.Add(new AdPrincipalItem { AccountName = "Administrators", DisplayName = "Administrators (管理者)", PrincipalType = AdPrincipalType.Group, Domain = Environment.MachineName, Description = "コンピューターの完全な管理者" });
                            results.Add(new AdPrincipalItem { AccountName = "Users", DisplayName = "Users (標準ユーザー)", PrincipalType = AdPrincipalType.Group, Domain = Environment.MachineName, Description = "標準的なアクセス権限" });
                        }
                        if (queryUsers && includeUsers)
                        {
                            results.Add(new AdPrincipalItem { AccountName = Environment.UserName, DisplayName = $"{Environment.UserName} (ログオン中)", PrincipalType = AdPrincipalType.User, Domain = Environment.MachineName, Description = "現在のアカウント" });
                        }
                    }

                    return results
                        .OrderBy(p => p.PrincipalType == AdPrincipalType.Group ? 0 : 1)
                        .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }

                // Handle Active Directory LDAP OU
                try
                {
                    string ldapPath = ouDistinguishedName.StartsWith("LDAP://", StringComparison.OrdinalIgnoreCase)
                        ? ouDistinguishedName
                        : $"LDAP://{ouDistinguishedName}";

                    using var entry = new DirectoryEntry(ldapPath);
                    using var searcher = new DirectorySearcher(entry)
                    {
                        PageSize = 100,
                        SearchScope = SearchScope.OneLevel
                    };

                    searcher.Filter = BuildLdapFilter(keyword, includeUsers, includeGroups);
                    searcher.PropertiesToLoad.AddRange(new[] { "sAMAccountName", "displayName", "objectClass", "description" });

                    foreach (SearchResult sr in searcher.FindAll())
                    {
                        var sam = GetProperty(sr, "sAMAccountName");
                        if (string.IsNullOrEmpty(sam)) continue;

                        var disp = GetProperty(sr, "displayName");
                        var desc = GetProperty(sr, "description");
                        var objClasses = sr.Properties["objectClass"];
                        bool isGroup = false;
                        if (objClasses != null)
                        {
                            foreach (var oc in objClasses)
                            {
                                if (oc?.ToString()?.Equals("group", StringComparison.OrdinalIgnoreCase) == true)
                                {
                                    isGroup = true;
                                    break;
                                }
                            }
                        }

                        results.Add(new AdPrincipalItem
                        {
                            AccountName = sam,
                            DisplayName = string.IsNullOrEmpty(disp) ? sam : $"{disp} ({sam})",
                            PrincipalType = isGroup ? AdPrincipalType.Group : AdPrincipalType.User,
                            Domain = CurrentDomainName.Split('.').FirstOrDefault()?.ToUpperInvariant() ?? "DOMAIN",
                            Description = desc
                        });
                    }
                }
                catch
                {
                    // If LDAP fails, return empty list
                }

                return results
                    .OrderBy(p => p.PrincipalType == AdPrincipalType.Group ? 0 : 1)
                    .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            });
        }
    }
}
