using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Threading;
using System.Threading.Tasks;

namespace AstraSize.Services
{
    public class MigrationProgress
    {
        public string CurrentItem { get; set; } = string.Empty;
        public int FoldersCreated { get; set; }
        public string StatusMessage { get; set; } = string.Empty;
    }

    public class MigrationService
    {
        // 1. Create Folder Structure & ACLs only (The "Skeleton & Permissions" Pre-migration!)
        public async Task<(int foldersCreated, List<string> errors)> CloneStructureAndAclAsync(
            string sourcePath,
            string targetPath,
            IProgress<MigrationProgress>? progress,
            CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                int count = 0;
                var errors = new List<string>();

                var srcDir = new DirectoryInfo(sourcePath);
                if (!srcDir.Exists) throw new DirectoryNotFoundException($"移行元が見つかりません: {sourcePath}");

                var dstDir = new DirectoryInfo(targetPath);
                if (!dstDir.Exists) dstDir.Create();

                void CloneRecursive(DirectoryInfo src, DirectoryInfo dst)
                {
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        // Copy ACL from source to destination
                        var sec = src.GetAccessControl(AccessControlSections.Access);
                        dst.SetAccessControl(sec);
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"権限設定失敗 [{dst.FullName}]: {ex.Message}");
                    }

                    count++;
                    progress?.Report(new MigrationProgress
                    {
                        CurrentItem = dst.FullName,
                        FoldersCreated = count,
                        StatusMessage = $"ガワ作成 & 権限流し込み中: {dst.Name}"
                    });

                    DirectoryInfo[] subDirs;
                    try
                    {
                        subDirs = src.GetDirectories();
                    }
                    catch (Exception ex)
                    {
                        errors.Add($"サブフォルダ読み取り失敗 [{src.FullName}]: {ex.Message}");
                        return;
                    }

                    foreach (var s in subDirs)
                    {
                        if ((s.Attributes & FileAttributes.ReparsePoint) != 0) continue;

                        try
                        {
                            var targetSubPath = Path.Combine(dst.FullName, s.Name);
                            var subDst = Directory.CreateDirectory(targetSubPath);
                            CloneRecursive(s, subDst);
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"フォルダ作成失敗 [{s.Name}]: {ex.Message}");
                        }
                    }
                }

                CloneRecursive(srcDir, dstDir);
                return (count, errors);
            }, ct);
        }

        // 2. Generate and Run Robocopy for data sync
        public string GenerateRobocopyCommand(string sourcePath, string targetPath, bool copyAcl = true, int threads = 16)
        {
            string copyFlags = copyAcl ? "/COPYALL" : "/COPY:DAT";
            return $"robocopy \"{sourcePath.TrimEnd('\\')}\" \"{targetPath.TrimEnd('\\')}\" /E {copyFlags} /DCOPY:DAT /R:1 /W:1 /MT:{threads} /NP";
        }
    }
}
