using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AstraSize.Models;
using FolderMorpher.Models;

namespace FolderMorpher.Services.ServerSearch
{
    /// <summary>
    /// Windows Search Service (WSP / Search.CollatorDSO OLE DB Provider) を利用して、
    /// Windows Server および WSP 互換 NAS（Synology 等）から候補ファイル一覧を秒速で借りるアクセラレーター。
    /// （ADR 94: Server Search Accelerator）
    /// </summary>
    public sealed class WindowsSearchProvider : IServerSearchProvider
    {
        private const string ConnectionString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";
        private const int DefaultCommandTimeoutSeconds = 15;
        private const int MaxCandidateResults = 2000;

        public string Name => "Windows Search (WSP / OLE DB)";

        private const uint SC_MANAGER_CONNECT = 0x0001;
        private const uint SERVICE_QUERY_STATUS = 0x0004;
        private const int SERVICE_RUNNING = 0x00000004;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct SERVICE_STATUS
        {
            public uint dwServiceType;
            public uint dwCurrentState;
            public uint dwControlsAccepted;
            public uint dwWin32ExitCode;
            public uint dwServiceSpecificExitCode;
            public uint dwCheckPoint;
            public uint dwWaitHint;
        }

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr OpenService(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool QueryServiceStatus(IntPtr hService, out SERVICE_STATUS lpServiceStatus);

        [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool CloseServiceHandle(IntPtr hSCObject);

        /// <summary>
        /// ローカルマシン上で Windows Search サービス（WSearch）が稼働中（Running）かを確認。
        /// サービス停止中・無効化環境での OleDbConnection ネイティブ COM クラッシュを完全に防止。
        /// </summary>
        public static bool IsSearchServiceRunning()
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return false;

                IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
                if (scm == IntPtr.Zero) return false;

                try
                {
                    IntPtr service = OpenService(scm, "WSearch", SERVICE_QUERY_STATUS);
                    if (service == IntPtr.Zero) return false;

                    try
                    {
                        if (QueryServiceStatus(service, out var status))
                        {
                            return status.dwCurrentState == SERVICE_RUNNING;
                        }
                        return false;
                    }
                    finally
                    {
                        CloseServiceHandle(service);
                    }
                }
                finally
                {
                    CloseServiceHandle(scm);
                }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 実行環境（ローカルOS）に Search.CollatorDSO OLE DB プロバイダーが登録されており、
        /// かつ Windows Search サービスが稼働中（Running）かを安全に確認。
        /// GitHub Actions ランナーや Server Core 等のプロバイダ未導入/サービス停止環境でのネイティブ COM クラッシュを防止。
        /// </summary>
        public static bool IsProviderInstalled()
        {
            try
            {
                if (!OperatingSystem.IsWindows()) return false;
                var comType = Type.GetTypeFromProgID("Search.CollatorDSO");
                if (comType == null) return false;

                // プロバイダーProgIDが存在しても、WSearch サービスが停止・無効化されている場合は COM が壊れるため利用不可
                return IsSearchServiceRunning();
            }
            catch
            {
                return false;
            }
        }

        public Task<bool> CanHandleAsync(string targetPath, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(targetPath)) return Task.FromResult(false);

            // Windows OS 上で動作しており、かつ Search.CollatorDSO プロバイダーが登録されているか
            if (!OperatingSystem.IsWindows() || !IsProviderInstalled()) return Task.FromResult(false);

            string norm = PathCanonicalizer.Normalize(targetPath);
            // ローカルドライブ (C:\等) または UNC パス (\\server\share等)
            return Task.FromResult(norm.Length >= 2 && (norm[1] == ':' || norm.StartsWith(@"\\")));
        }

        public async Task<IReadOnlyList<string>?> QueryCandidatesAsync(string targetPath, SearchQuery query, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(targetPath) || query == null) return null;
            if (!OperatingSystem.IsWindows() || !IsProviderInstalled()) return null;

            string normalizedPath = PathCanonicalizer.Normalize(targetPath);
            string? sql = BuildSearchSql(normalizedPath, query);
            if (string.IsNullOrEmpty(sql)) return null;

            return await Task.Run(() =>
            {
                var candidates = new List<string>(128);

                try
                {
                    using var connection = new OleDbConnection(ConnectionString);
                    connection.Open();

                    using var command = connection.CreateCommand();
                    command.CommandText = sql;
                    command.CommandTimeout = DefaultCommandTimeoutSeconds;

                    using var reader = command.ExecuteReader();
                    while (reader.Read())
                    {
                        if (ct.IsCancellationRequested) break;

                        // 0列目: System.ItemPathDisplay または System.ItemUrl
                        string? path = reader.GetValue(0)?.ToString();
                        if (!string.IsNullOrEmpty(path))
                        {
                            // file: プレフィックスが付いている場合の除去
                            if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                            {
                                path = path.Substring(5).TrimStart('/');
                                path = path.Replace('/', '\\');
                            }

                            candidates.Add(path);
                            if (candidates.Count >= MaxCandidateResults)
                            {
                                break;
                            }
                        }
                    }

                    return (IReadOnlyList<string>)candidates;
                }
                catch (OleDbException)
                {
                    // サーバー側で Windows Search が無効、Remote Query 禁止、SCOPE 未インデックス等
                    return null;
                }
                catch (Exception)
                {
                    // その他の予期せぬ OLE DB / COM エラーは透過的フォールバック
                    return null;
                }
            }, ct);
        }

        /// <summary>
        /// Windows Search SQL 構文を生成します。
        /// </summary>
        public static string? BuildSearchSql(string targetPath, SearchQuery query)
        {
            string norm = PathCanonicalizer.Normalize(targetPath);
            string serverName = string.Empty;
            string scopeUrl;

            if (norm.StartsWith(@"\\"))
            {
                // UNC: \\server\share\dir
                int nextSlash = norm.IndexOf('\\', 2);
                if (nextSlash > 2)
                {
                    serverName = norm.Substring(2, nextSlash - 2);
                }
                else
                {
                    serverName = norm.Substring(2);
                }
                scopeUrl = "file:" + norm.Replace('\\', '/');
            }
            else
            {
                // Local: C:\dir
                scopeUrl = "file:" + norm.Replace('\\', '/');
            }

            var fromClause = string.IsNullOrEmpty(serverName)
                ? "SystemIndex"
                : $"\"{serverName}\".SystemIndex";

            var whereClauses = new List<string>();
            whereClauses.Add($"SCOPE = '{EscapeSqlString(scopeUrl)}'");

            if (!query.IncludeFolders)
            {
                whereClauses.Add("System.ItemType != 'Directory'");
            }

            if (query.Extensions != null && query.Extensions.Count > 0)
            {
                var extClauses = new List<string>();
                foreach (var rawExt in query.Extensions)
                {
                    if (string.IsNullOrWhiteSpace(rawExt)) continue;
                    string ext = rawExt.StartsWith(".") ? rawExt : "." + rawExt;
                    extClauses.Add($"System.FileExtension = '{EscapeSqlString(ext)}'");
                }
                if (extClauses.Count > 0)
                {
                    whereClauses.Add("(" + string.Join(" OR ", extClauses) + ")");
                }
            }

            // ContentKeyword (content:"..." 構文) の SQL 条件生成
            if (!string.IsNullOrWhiteSpace(query.ContentKeyword))
            {
                string escapedContent = EscapeContainsTerm(query.ContentKeyword);
                whereClauses.Add($"(CONTAINS(System.Search.Contents, '\"{escapedContent}\"') OR CONTAINS(System.FileName, '\"{escapedContent}\"') OR System.FileName LIKE '%{EscapeLike(query.ContentKeyword)}%')");
            }

            // キーワードグループ（AND-of-ORs）の SQL 条件生成
            if (query.KeywordGroups != null && query.KeywordGroups.Count > 0)
            {
                for (int i = 0; i < query.KeywordGroups.Count; i++)
                {
                    var group = query.KeywordGroups[i];
                    if (group == null || group.Count == 0) continue;

                    var groupParts = new List<string>();
                    for (int j = 0; j < group.Count; j++)
                    {
                        string kw = group[j];
                        if (string.IsNullOrWhiteSpace(kw)) continue;

                        string escapedKw = EscapeContainsTerm(kw);
                        if (query.SearchContentMode)
                        {
                            // 本文またはファイル名
                            groupParts.Add($"(CONTAINS(System.Search.Contents, '\"{escapedKw}\"') OR CONTAINS(System.FileName, '\"{escapedKw}\"') OR System.FileName LIKE '%{EscapeLike(kw)}%')");
                        }
                        else
                        {
                            // ファイル名のみ
                            groupParts.Add($"(CONTAINS(System.FileName, '\"{escapedKw}\"') OR System.FileName LIKE '%{EscapeLike(kw)}%')");
                        }
                    }

                    if (groupParts.Count > 0)
                    {
                        whereClauses.Add("(" + string.Join(" OR ", groupParts) + ")");
                    }
                }
            }

            // ExactPhrases（引用符完全一致）
            if (query.ExactPhrases != null && query.ExactPhrases.Count > 0)
            {
                for (int i = 0; i < query.ExactPhrases.Count; i++)
                {
                    string phrase = query.ExactPhrases[i];
                    if (string.IsNullOrWhiteSpace(phrase)) continue;

                    string escapedPhrase = EscapeContainsTerm(phrase);
                    if (query.SearchContentMode)
                    {
                        whereClauses.Add($"(CONTAINS(System.Search.Contents, '\"{escapedPhrase}\"') OR System.FileName LIKE '%{EscapeLike(phrase)}%')");
                    }
                    else
                    {
                        whereClauses.Add($"System.FileName LIKE '%{EscapeLike(phrase)}%'");
                    }
                }
            }

            var sb = new StringBuilder();
            sb.Append($"SELECT TOP {MaxCandidateResults} System.ItemPathDisplay FROM {fromClause}");
            if (whereClauses.Count > 0)
            {
                sb.Append(" WHERE ");
                sb.Append(string.Join(" AND ", whereClauses));
            }

            return sb.ToString();
        }

        private static string EscapeSqlString(string value) => value.Replace("'", "''");

        private static string EscapeContainsTerm(string term) => term.Replace("\"", "\"\"").Replace("'", "''");

        private static string EscapeLike(string value) => value.Replace("'", "''").Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
    }
}
