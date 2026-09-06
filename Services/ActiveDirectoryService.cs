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
                return results;
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
    }
}
