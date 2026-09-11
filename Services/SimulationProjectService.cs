using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AstraSize.Models;

namespace AstraSize.Services
{
    public class SimulationProjectService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true
        };

        /// <summary>
        /// Save project to .fmorph file
        /// </summary>
        public async Task SaveProjectAsync(FolderMorphProject project, string filePath)
        {
            project.LastModifiedAt = DateTime.Now;
            var json = JsonSerializer.Serialize(project, JsonOptions);
            await File.WriteAllTextAsync(filePath, json, Encoding.UTF8);
        }

        /// <summary>
        /// Load project from .fmorph file and re-link parent references and levels
        /// </summary>
        public async Task<FolderMorphProject?> LoadProjectAsync(string filePath)
        {
            if (!File.Exists(filePath)) return null;

            var json = await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            var project = JsonSerializer.Deserialize<FolderMorphProject>(json, JsonOptions);
            if (project != null)
            {
                foreach (var root in project.RootFolders)
                {
                    LinkParentsAndLevels(root, null, 0);
                }
            }
            return project;
        }

        private static void LinkParentsAndLevels(SimFolderNode node, SimFolderNode? parent, int level)
        {
            node.Parent = parent;
            node.Level = level;
            foreach (var child in node.Children)
            {
                LinkParentsAndLevels(child, node, level + 1);
            }
        }

        /// <summary>
        /// Build a mock tree from a scanned FileItemNode
        /// </summary>
        public SimFolderNode ConvertToSimNode(FileItemNode sourceNode)
        {
            var sim = new SimFolderNode
            {
                Name = sourceNode.Name,
                EstimatedSizeBytes = sourceNode.SizeBytes,
                InheritAcl = true,
                Level = 0
            };
            sim.MappedSourcePaths.Add(sourceNode.FullPath);

            PopulateChildren(sourceNode, sim, 1, 5);

            return sim;
        }

        private static void PopulateChildren(FileItemNode src, SimFolderNode dst, int currentLevel, int maxLevel)
        {
            if (currentLevel >= maxLevel) return;

            foreach (var child in src.Children)
            {
                if (!child.IsDirectory) continue;

                var childSim = new SimFolderNode
                {
                    Name = child.Name,
                    EstimatedSizeBytes = child.SizeBytes,
                    InheritAcl = true,
                    Level = currentLevel,
                    Parent = dst
                };
                childSim.MappedSourcePaths.Add(child.FullPath);

                dst.Children.Add(childSim);
                PopulateChildren(child, childSim, currentLevel + 1, maxLevel);
            }
        }

        /// <summary>
        /// Generate human-friendly difference review between Before (Current) and After (Target Sim)
        /// </summary>
        public List<SimDiffItem> GenerateDiffReview(FileItemNode? currentScannedTree, IEnumerable<SimFolderNode> targetRoots)
        {
            var diffs = new List<SimDiffItem>();
            var allSimNodes = new List<SimFolderNode>();

            void CollectNodes(SimFolderNode n)
            {
                allSimNodes.Add(n);
                foreach (var c in n.Children) CollectNodes(c);
            }
            foreach (var r in targetRoots) CollectNodes(r);

            // 1. Check all target folders
            foreach (var node in allSimNodes)
            {
                var item = new SimDiffItem
                {
                    TargetPath = node.RelativePath,
                    TargetDetail = $"階層レベル: {node.LevelPillText} ({node.FormattedSize})"
                };

                // Case A: 統合 (N:1)
                if (node.MappedSourcePaths.Count > 1)
                {
                    item.DiffType = "統合・集約";
                    item.DiffTypeBadgeBackground = "#D97706"; // Amber
                    item.SourcePath = string.Join("\n", node.MappedSourcePaths.Select(p => $"• {p}"));
                    item.SourceDetail = $"{node.MappedSourcePaths.Count}箇所の現行フォルダを1つに統合";
                }
                // Case B: 単一紐づけ (1:1)
                else if (node.MappedSourcePaths.Count == 1)
                {
                    var src = node.MappedSourcePaths[0];
                    item.SourcePath = src;

                    // Check if path or hierarchy changed
                    var srcLeaf = Path.GetFileName(src.TrimEnd('\\', '/'));
                    if (!string.Equals(srcLeaf, node.Name, StringComparison.OrdinalIgnoreCase) || node.Level > 1)
                    {
                        item.DiffType = "階層移動";
                        item.DiffTypeBadgeBackground = "#2563EB"; // Blue
                        item.SourceDetail = $"現行: {srcLeaf} ➔ 新階層へリロケート";
                    }
                    else
                    {
                        item.DiffType = "構造維持";
                        item.DiffTypeBadgeBackground = "#475569"; // Slate
                        item.SourceDetail = "同名階層で移行";
                    }
                }
                // Case C: 新規作成
                else
                {
                    item.DiffType = "新規作成";
                    item.DiffTypeBadgeBackground = "#059669"; // Emerald
                    item.SourcePath = "(現行データなし)";
                    item.SourceDetail = "新環境で新設される空フォルダ";
                }

                // ACL diff details
                if (node.AclEntries.Count > 0)
                {
                    foreach (var acl in node.AclEntries)
                    {
                        item.AclChanges.Add($"＋ {acl.DisplayName}: [{acl.FormattedRights}] を付与");
                    }
                }
                else
                {
                    item.AclChanges.Add(node.InheritAcl ? "🔗 親フォルダのアクセス権を継承" : "🛡️ 固有権限 (エントリなし)");
                }

                diffs.Add(item);
            }

            return diffs;
        }

        /// <summary>
        /// スケルトン展開の実行計画 (SkeletonDeployPlan) を事前構築する。
        /// Preview 表示と Commit 実行で完全に同一の計画オブジェクトを貫通させる。
        /// </summary>
        public SkeletonDeployPlan BuildDeployPlan(
            IEnumerable<SimFolderNode> rootNodes,
            string destinationRoot,
            FileItemNode? sourceRootNode = null)
        {
            var plan = new SkeletonDeployPlan
            {
                DestinationRoot = destinationRoot,
                DiffReviews = GenerateDiffReview(sourceRootNode, rootNodes)
            };

            void CollectActions(SimFolderNode node, string currentParentPath, string currentRelativePath)
            {
                var relPath = string.IsNullOrEmpty(currentRelativePath) ? node.Name : Path.Combine(currentRelativePath, node.Name);
                var fullPath = Path.Combine(currentParentPath, node.Name);
                bool exists = Directory.Exists(fullPath);

                plan.FolderActions.Add(new SkeletonFolderAction
                {
                    RelativePath = relPath,
                    FullTargetPath = fullPath,
                    IsExisting = exists,
                    InheritAcl = node.InheritAcl,
                    AclEntries = node.AclEntries.Select(x => x.Clone()).ToList()
                });

                foreach (var child in node.Children)
                {
                    CollectActions(child, fullPath, relPath);
                }
            }

            foreach (var root in rootNodes)
            {
                CollectActions(root, destinationRoot, "");
            }

            return plan;
        }

        /// <summary>
        /// Plan First 方式: 事前構築・プレビューされた SkeletonDeployPlan をコミット実行する
        /// </summary>
        public async Task<DeploySkeletonResult> DeploySkeletonAsync(
            SkeletonDeployPlan plan,
            IProgress<(string Status, int Count)>? progress = null,
            CancellationToken ct = default)
        {
            return await Task.Run(() =>
            {
                var result = new DeploySkeletonResult();

                try
                {
                    if (!Directory.Exists(plan.DestinationRoot))
                    {
                        Directory.CreateDirectory(plan.DestinationRoot);
                        result.Logs.Add($"[作成] ルート作成: {plan.DestinationRoot}");
                    }
                    else
                    {
                        result.Logs.Add($"[既存] ルート既存検知: {plan.DestinationRoot}");
                    }

                    foreach (var action in plan.FolderActions)
                    {
                        ct.ThrowIfCancellationRequested();

                        var folderPath = action.FullTargetPath;
                        bool isNewFolder = false;

                        try
                        {
                            bool currentExists = Directory.Exists(folderPath);

                            // 外部競合検知: 計画時 (action.IsExisting) と現在の実態 (currentExists) の不一致を防御
                            if (currentExists != action.IsExisting)
                            {
                                string conflictMsg = action.IsExisting
                                    ? $"[外部競合検知] 計画時は既存だったフォルダが外部で削除されています: {folderPath}"
                                    : $"[外部競合検知] 計画時は存在しなかったフォルダが外部で新規作成されています: {folderPath}";
                                result.Logs.Add(conflictMsg);
                                result.ConflictCount++;
                                // 外部競合したノードは計画外の変更事故を防ぐためスキップ
                                continue;
                            }

                            if (!currentExists)
                            {
                                Directory.CreateDirectory(folderPath);
                                result.CreatedCount++;
                                isNewFolder = true;
                                result.DeployedFolderPaths.Add(folderPath);
                                progress?.Report(($"フォルダ作成中: {Path.GetFileName(folderPath)}", result.CreatedCount));
                                result.Logs.Add($"[作成] {folderPath}");
                            }
                            else
                            {
                                result.SkippedExistingCount++;
                                result.Logs.Add($"[既存保持] {folderPath} は既に存在するため作成をスキップし、既存NTFS権限を保護しました");
                            }

                            // 既存フォルダの場合は意図しない権限破壊を防ぐため、新規作成されたフォルダのみに適用
                            if (isNewFolder && (!action.InheritAcl || action.AclEntries.Count > 0))
                            {
                                try
                                {
                                    var di = new DirectoryInfo(folderPath);
                                    var ds = di.GetAccessControl();

                                    if (!action.InheritAcl)
                                    {
                                        // ADR 10: 継承無効化時は SetAccessRuleProtection(true, false) で親由来の不要ルールを複製保持させない
                                        ds.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                                    }

                                    if (action.AclEntries.Count > 0)
                                    {
                                        // Canonical ACL Apply Contract:
                                        // 継承有効時(InheritAcl == true)は親から受け継ぐべき継承ACE(IsInherited)を除外し、明示ACE(!IsInherited)のみを適用。
                                        // 継承無効時(InheritAcl == false)は親を遮断した上で全ACEを明示的ルールとして付与。
                                        var targetEntries = action.InheritAcl
                                            ? action.AclEntries.Where(a => !a.IsInherited)
                                            : action.AclEntries;

                                        // Windows Canonical DACL Ordering: Deny rules FIRST, then Allow rules
                                        var orderedEntries = targetEntries
                                            .OrderBy(a => a.AccessType == AccessControlType.Deny ? 0 : 1);

                                        foreach (var entry in orderedEntries)
                                        {
                                            try
                                            {
                                                var id = new NTAccount(entry.AccountName);
                                                var rule = new FileSystemAccessRule(
                                                    id,
                                                    entry.Rights,
                                                    entry.InheritanceFlags,
                                                    entry.PropagationFlags,
                                                    entry.AccessType);
                                                ds.AddAccessRule(rule);
                                            }
                                            catch (Exception aex)
                                            {
                                                result.Errors.Add($"アカウント '{entry.AccountName}' の解決失敗: {aex.Message}");
                                                result.Logs.Add($"  [権限警告] アカウント '{entry.AccountName}' の解決失敗: {aex.Message}");
                                            }
                                        }
                                    }

                                    di.SetAccessControl(ds);
                                    result.AclAppliedCount++;
                                    result.Logs.Add($"[ACL適用] {folderPath} (継承: {action.InheritAcl}, ルール数: {action.AclEntries.Count})");
                                }
                                catch (Exception ex)
                                {
                                    result.Errors.Add($"{folderPath} のACL適用失敗: {ex.Message}");
                                    result.Logs.Add($"[ACL警告] {folderPath}: {ex.Message}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            result.Errors.Add($"{folderPath} の作成失敗: {ex.Message}");
                            result.Logs.Add($"[エラー] {folderPath}: {ex.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Errors.Add($"ルート準備失敗: {ex.Message}");
                    result.Logs.Add($"[重大エラー] ルート作成失敗: {ex.Message}");
                }

                return result;
            }, ct);
        }

        /// <summary>
        /// Deploy skeleton folders and apply simulated ACLs to actual filesystem (互換用オーバーロード)
        /// </summary>
        public async Task<DeploySkeletonResult> DeploySkeletonAsync(
            IEnumerable<SimFolderNode> rootNodes,
            string destinationRoot,
            IProgress<(string Status, int Count)>? progress = null,
            CancellationToken ct = default)
        {
            var plan = BuildDeployPlan(rootNodes, destinationRoot);
            return await DeploySkeletonAsync(plan, progress, ct);
        }

        /// <summary>
        /// Generate Robocopy script with multi-source mapping and descendant /XD exclusion support.
        /// copyAcl: false = 新ACL設計維持モード (/COPY:DAT: データのみ転送し、新設計ACLを維持)
        /// copyAcl: true  = 旧ACL完全維持モード (/COPYALL: 旧環境のアクセス権をそのまま引き継ぐ)
        /// </summary>
        public string GenerateRobocopyScript(IEnumerable<SimFolderNode> rootNodes, string targetRoot, bool copyAcl = false, int threads = 16)
        {
            var copyFlags = copyAcl ? "/COPYALL" : "/COPY:DAT";
            var modeDesc = copyAcl
                ? "旧環境ACL完全維持モード (/COPYALL: 旧環境のアクセス権をそのまま移行先に引き継ぎます)"
                : "新設計ACL維持モード（推奨） (/COPY:DAT: データのみ転送し、FolderMorpherで設計・展開した新ACLを保持します)";

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 > nul");
            sb.AppendLine("echo ==================================================================");
            sb.AppendLine("echo   FolderMorpher - High Performance Robocopy Batch");
            sb.AppendLine($"echo   Target Root   : {targetRoot}");
            sb.AppendLine($"echo   Transfer Mode : {modeDesc}");
            sb.AppendLine($"echo   Generated     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("echo ==================================================================");
            sb.AppendLine();

            void CollectDescendantSources(SimFolderNode parent, List<string> accumulator)
            {
                foreach (var child in parent.Children)
                {
                    foreach (var s in child.MappedSourcePaths)
                    {
                        if (!string.IsNullOrWhiteSpace(s))
                        {
                            accumulator.Add(s);
                        }
                    }
                    CollectDescendantSources(child, accumulator);
                }
            }

            bool IsSubPath(string parentPath, string childPath)
            {
                try
                {
                    var p = Path.GetFullPath(parentPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var c = Path.GetFullPath(childPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    return c.StartsWith(p + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || c.StartsWith(p + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }

            void AppendRoboNode(SimFolderNode node, string currentTarget)
            {
                var targetFolder = Path.Combine(currentTarget, node.Name);

                var descendantSources = new List<string>();
                CollectDescendantSources(node, descendantSources);

                foreach (var src in node.MappedSourcePaths)
                {
                    if (string.IsNullOrWhiteSpace(src)) continue;

                    var excludedDirs = descendantSources
                        .Where(ds => IsSubPath(src, ds))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    var xdParam = excludedDirs.Count > 0
                        ? " /XD " + string.Join(" ", excludedDirs.Select(d => $"\"{d}\""))
                        : "";

                    sb.AppendLine($"echo [移行実行] \"{src}\" ➔ \"{targetFolder}\"");
                    sb.AppendLine($"robocopy \"{src}\" \"{targetFolder}\" /E {copyFlags} /DCOPY:DAT /R:2 /W:3 /MT:{threads} /NP /TEE{xdParam} /LOG+:\"%TEMP%\\FolderMorpher_Robocopy_{DateTime.Now:yyyyMMdd}.log\"");
                    sb.AppendLine();
                }

                foreach (var child in node.Children)
                {
                    AppendRoboNode(child, targetFolder);
                }
            }

            foreach (var r in rootNodes)
            {
                AppendRoboNode(r, targetRoot);
            }

            sb.AppendLine("echo ------------------------------------------------------------------");
            sb.AppendLine("echo すべての転送ジョブが完了しました。ログは %TEMP% を確認してください。");
            sb.AppendLine("pause");
            return sb.ToString();
        }

        /// <summary>
        /// Generate PowerShell ACL application script using native .NET FileSystemAccessRule.
        /// Fully mirrors C# DeploySkeletonAsync semantics without icacls syntax truncation or privilege mismatch.
        /// </summary>
        public string GeneratePowerShellAclScript(IEnumerable<SimFolderNode> rootNodes, string targetRoot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine("# FolderMorpher Auto-Generated ACL Provisioning Script (.NET Canonical Engine)");
            sb.AppendLine($"# Generated  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"# TargetRoot : {targetRoot}");
            sb.AppendLine("# ==============================================================================");
            sb.AppendLine();
            sb.AppendLine($"$TargetRoot = \"{targetRoot}\"");
            sb.AppendLine();
            sb.AppendLine(@"function Set-FolderMorpherAcl {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][bool]$Inherit,
        [Parameter(Mandatory=$true)][array]$Rules
    )
    if (!(Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path -Force | Out-Null
    }
    $item = Get-Item -LiteralPath $Path
    $acl = $item.GetAccessControl([System.Security.AccessControl.AccessControlSections]::Access)
    if (-not $Inherit) {
        # ADR 10: 継承無効化時は親由来の不要ルールを破棄 (SetAccessRuleProtection(true, false))
        $acl.SetAccessRuleProtection($true, $false)
    }
    foreach ($r in $Rules) {
        try {
            $account = New-Object System.Security.Principal.NTAccount($r.Account)
            $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
                $account,
                [System.Security.AccessControl.FileSystemRights]$r.Rights,
                [System.Security.AccessControl.InheritanceFlags]$r.Inheritance,
                [System.Security.AccessControl.PropagationFlags]$r.Propagation,
                [System.Security.AccessControl.AccessControlType]$r.AccessType
            )
            $acl.AddAccessRule($rule)
        } catch {
            Write-Warning ""ACL entry failed for $($r.Account) on ${Path}: $_""
        }
    }
    $item.SetAccessControl($acl)
}");
            sb.AppendLine();

            foreach (var root in rootNodes)
            {
                AppendAclPsScript(root, "$TargetRoot", sb);
            }

            sb.AppendLine();
            sb.AppendLine("Write-Host 'すべてのACLプロビジョニングが完了しました。' -ForegroundColor Green");
            return sb.ToString();
        }

        private static void AppendAclPsScript(SimFolderNode node, string parentVar, StringBuilder sb)
        {
            var pathVar = $"$p_{Math.Abs(node.Id.GetHashCode())}";
            sb.AppendLine($"{pathVar} = Join-Path {parentVar} \"{node.Name}\"");

            var inheritParam = node.InheritAcl ? "$true" : "$false";

            // Canonical ACL Apply Contract:
            // 継承ON時は明示ACE(!IsInherited)のみを適用。継承OFF時は全ACEを明示化。
            var targetEntries = node.InheritAcl
                ? node.AclEntries.Where(a => !a.IsInherited)
                : node.AclEntries;

            var orderedEntries = targetEntries
                .OrderBy(a => a.AccessType == AccessControlType.Deny ? 0 : 1)
                .ToList();

            if (orderedEntries.Count == 0)
            {
                sb.AppendLine($"Set-FolderMorpherAcl -Path {pathVar} -Inherit {inheritParam} -Rules @()");
            }
            else
            {
                var ruleItems = new List<string>();
                foreach (var acl in orderedEntries)
                {
                    var rightsInt = (int)acl.Rights;
                    var inhInt = (int)acl.InheritanceFlags;
                    var propInt = (int)acl.PropagationFlags;
                    var accTypeStr = acl.AccessType == AccessControlType.Deny ? "Deny" : "Allow";
                    var comment = $"{acl.AccessType} {acl.FormattedRights}";

                    ruleItems.Add($"        [pscustomobject]@{{ Account = \"{acl.AccountName}\"; Rights = {rightsInt}; Inheritance = {inhInt}; Propagation = {propInt}; AccessType = \"{accTypeStr}\" }} # {comment}");
                }

                sb.AppendLine($"Set-FolderMorpherAcl -Path {pathVar} -Inherit {inheritParam} -Rules @(");
                sb.AppendLine(string.Join("," + Environment.NewLine, ruleItems));
                sb.AppendLine(")");
            }

            foreach (var child in node.Children)
            {
                AppendAclPsScript(child, pathVar, sb);
            }
        }

        /// <summary>
        /// Export Excel/CSV design ledger matrix
        /// </summary>
        public string ExportDesignMatrixCsv(IEnumerable<SimFolderNode> rootNodes)
        {
            var sb = new StringBuilder();
            // UTF-8 BOM
            sb.Append('\uFEFF');
            sb.AppendLine("階層パス,フォルダ名,階層レベル,移行元マッピング,元容量,継承状態,アカウント,権限種別,アクセス許可");

            void WriteNode(SimFolderNode node)
            {
                var mappingStr = node.MappedSourcePaths.Count == 0 ? "(新設)" : string.Join(" | ", node.MappedSourcePaths);
                if (node.AclEntries.Count == 0)
                {
                    sb.AppendLine($"\"{EscapeCsv(node.RelativePath)}\",\"{EscapeCsv(node.Name)}\",\"{node.LevelPillText}\",\"{EscapeCsv(mappingStr)}\",\"{node.FormattedSize}\",\"{node.InheritStatusBadge}\",\"(設定なし)\",\"-\",\"-\"");
                }
                else
                {
                    foreach (var acl in node.AclEntries)
                    {
                        sb.AppendLine($"\"{EscapeCsv(node.RelativePath)}\",\"{EscapeCsv(node.Name)}\",\"{node.LevelPillText}\",\"{EscapeCsv(mappingStr)}\",\"{node.FormattedSize}\",\"{node.InheritStatusBadge}\",\"{EscapeCsv(acl.DisplayName)}\",\"{acl.AccessType}\",\"{acl.FormattedRights}\"");
                    }
                }

                foreach (var c in node.Children) WriteNode(c);
            }

            foreach (var root in rootNodes)
            {
                WriteNode(root);
            }

            return sb.ToString();
        }

        private static string EscapeCsv(string s) => s.Replace("\"", "\"\"");
    }
}
