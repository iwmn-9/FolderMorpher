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
        /// Deploy skeleton folders and apply simulated ACLs to actual filesystem
        /// </summary>
        public async Task<(int CreatedCount, List<string> Logs)> DeploySkeletonAsync(
            IEnumerable<SimFolderNode> rootNodes,
            string destinationRoot,
            IProgress<(string Status, int Count)> progress,
            CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                int count = 0;
                var logs = new List<string>();

                if (!Directory.Exists(destinationRoot))
                {
                    Directory.CreateDirectory(destinationRoot);
                    logs.Add($"[作成] ルート作成: {destinationRoot}");
                }

                foreach (var root in rootNodes)
                {
                    DeployNodeRecursive(root, destinationRoot, ref count, logs, progress, ct);
                }

                return (count, logs);
            }, ct);
        }

        private static void DeployNodeRecursive(
            SimFolderNode node,
            string currentParentPath,
            ref int count,
            List<string> logs,
            IProgress<(string Status, int Count)> progress,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var folderPath = Path.Combine(currentParentPath, node.Name);
            try
            {
                if (!Directory.Exists(folderPath))
                {
                    Directory.CreateDirectory(folderPath);
                    count++;
                    progress.Report(($"フォルダ作成中: {node.Name}", count));
                    logs.Add($"[作成] {folderPath}");
                }

                // Apply ACL if configured
                if (node.AclEntries.Count > 0)
                {
                    try
                    {
                        var di = new DirectoryInfo(folderPath);
                        var ds = di.GetAccessControl();

                        if (!node.InheritAcl)
                        {
                            ds.SetAccessRuleProtection(true, true);
                        }

                        foreach (var acl in node.AclEntries)
                        {
                            try
                            {
                                var sid = new NTAccount(acl.AccountName);
                                var rule = new FileSystemAccessRule(
                                    sid,
                                    acl.Rights,
                                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                                    PropagationFlags.None,
                                    acl.AccessType);
                                ds.AddAccessRule(rule);
                                logs.Add($"  [権限付与] {acl.AccountName} -> {acl.FormattedRights}");
                            }
                            catch (Exception aex)
                            {
                                logs.Add($"  [権限警告] アカウント '{acl.AccountName}' の解決失敗: {aex.Message}");
                            }
                        }

                        di.SetAccessControl(ds);
                    }
                    catch (Exception ex)
                    {
                        logs.Add($"  [ACL適用失敗] {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                logs.Add($"[エラー] 作成失敗 {folderPath}: {ex.Message}");
            }

            foreach (var child in node.Children)
            {
                DeployNodeRecursive(child, folderPath, ref count, logs, progress, ct);
            }
        }

        /// <summary>
        /// Generate Robocopy script with multi-source mapping support
        /// </summary>
        public string GenerateRobocopyScript(IEnumerable<SimFolderNode> rootNodes, string targetRoot, bool copyAcl = true, int threads = 16)
        {
            var copyFlags = copyAcl ? "/COPYALL" : "/COPY:DAT";
            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("chcp 65001 > nul");
            sb.AppendLine("echo ==================================================================");
            sb.AppendLine("echo   FolderMorpher - High Performance Robocopy Batch");
            sb.AppendLine($"echo   Target Root : {targetRoot}");
            sb.AppendLine($"echo   Generated   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine("echo ==================================================================");
            sb.AppendLine();

            void AppendRoboNode(SimFolderNode node, string currentTarget)
            {
                var targetFolder = Path.Combine(currentTarget, node.Name);

                foreach (var src in node.MappedSourcePaths)
                {
                    if (string.IsNullOrWhiteSpace(src)) continue;
                    sb.AppendLine($"echo [移行実行] \"{src}\" ➔ \"{targetFolder}\"");
                    sb.AppendLine($"robocopy \"{src}\" \"{targetFolder}\" /E {copyFlags} /DCOPY:DAT /R:2 /W:3 /MT:{threads} /NP /TEE /LOG+:\"%TEMP%\\FolderMorpher_Robocopy_{DateTime.Now:yyyyMMdd}.log\"");
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
        /// Generate PowerShell ACL application script
        /// </summary>
        public string GeneratePowerShellAclScript(IEnumerable<SimFolderNode> rootNodes, string targetRoot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# FolderMorpher Auto-Generated ACL Provisioning Script");
            sb.AppendLine($"# Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"$TargetRoot = \"{targetRoot}\"");
            sb.AppendLine();

            foreach (var root in rootNodes)
            {
                AppendAclPsScript(root, "$TargetRoot", sb);
            }

            return sb.ToString();
        }

        private static void AppendAclPsScript(SimFolderNode node, string parentVar, StringBuilder sb)
        {
            var pathVar = $"$p_{Math.Abs(node.Id.GetHashCode())}";
            sb.AppendLine($"{pathVar} = Join-Path {parentVar} \"{node.Name}\"");
            sb.AppendLine($"if (!(Test-Path {pathVar})) {{ New-Item -ItemType Directory -Path {pathVar} -Force | Out-Null }}");

            if (!node.InheritAcl)
            {
                sb.AppendLine($"# Disable inheritance");
                sb.AppendLine($"icacls {pathVar} /inheritance:d | Out-Null");
            }

            foreach (var acl in node.AclEntries)
            {
                string rightCode = acl.IsFullControl ? "(F)" :
                                   acl.IsModify ? "(M)" :
                                   acl.IsReadExecute ? "(RX)" :
                                   acl.IsRead ? "(R)" :
                                   acl.IsWrite ? "(W)" : "(M)";
                sb.AppendLine($"icacls {pathVar} /grant \"{acl.AccountName}:(OI)(CI){rightCode}\" | Out-Null");
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
