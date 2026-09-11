using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderMorpher.Services
{
    public class LinkFixItem
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FileType { get; set; } = string.Empty; // ショートカット / Excel
        public string OldTarget { get; set; } = string.Empty;
        public string NewTarget { get; set; } = string.Empty;
        public bool IsFixed { get; set; }
        public string Status { get; set; } = "検出";
        public OfficeLinkItem? AssociatedOfficeItem { get; set; }
        public bool NeedsFix => !IsFixed && !string.IsNullOrEmpty(NewTarget) && !string.Equals(OldTarget, NewTarget, StringComparison.OrdinalIgnoreCase);
    }

    public class LinkFixService
    {
        private readonly OfficeLinkFixService _officeLinkService = new();
        public async Task<List<LinkFixItem>> ScanShortcutsAsync(
            string searchDirectory,
            string oldPathPattern,
            string newPathPattern,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var results = new List<LinkFixItem>();
                if (!Directory.Exists(searchDirectory)) return results;

                var dir = new DirectoryInfo(searchDirectory);

                dynamic? wsh = null;
                try
                {
                    var wshType = Type.GetTypeFromProgID("WScript.Shell");
                    if (wshType != null) wsh = Activator.CreateInstance(wshType);
                }
                catch { }

                int scanned = 0;
                foreach (var fi in SafeFileEnumerator.EnumerateFilesSafe(searchDirectory, "*.lnk", null, ct))
                {
                    if (ct.IsCancellationRequested) break;
                    scanned++;
                    if (scanned % 50 == 0) progress?.Report($"走査中: {scanned:N0} 件走査済み...");

                    if (wsh == null) continue;

                    try
                    {
                        var shortcut = wsh.CreateShortcut(fi.FullName);
                        string target = shortcut.TargetPath;
                        if (!string.IsNullOrEmpty(target) &&
                            (string.IsNullOrEmpty(oldPathPattern) || target.Contains(oldPathPattern, StringComparison.OrdinalIgnoreCase)))
                        {
                            string updatedTarget = string.IsNullOrEmpty(oldPathPattern)
                                ? target
                                : target.Replace(oldPathPattern, newPathPattern, StringComparison.OrdinalIgnoreCase);

                            results.Add(new LinkFixItem
                            {
                                FilePath = fi.FullName,
                                FileName = fi.Name,
                                FileType = "ショートカット (.lnk)",
                                OldTarget = target,
                                NewTarget = updatedTarget,
                                Status = "置換候補"
                            });
                        }
                    }
                    catch { }
                }

                return results;
            }, ct);
        }

        public async Task<int> ExecuteFixAsync(
            List<LinkFixItem> targets,
            IProgress<(string Path, bool Success)>? progress,
            CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                int successCount = 0;
                dynamic? wsh = null;
                try
                {
                    var wshType = Type.GetTypeFromProgID("WScript.Shell");
                    if (wshType != null) wsh = Activator.CreateInstance(wshType);
                }
                catch { }

                foreach (var item in targets)
                {
                    if (ct.IsCancellationRequested) break;
                    if (!item.NeedsFix) continue;

                    try
                    {
                        if (item.AssociatedOfficeItem != null)
                        {
                            var officeList = new List<OfficeLinkItem> { item.AssociatedOfficeItem };
                            int offResult = _officeLinkService.ExecuteOfficeFixAsync(officeList, null, ct).GetAwaiter().GetResult();
                            if (offResult > 0)
                            {
                                item.IsFixed = true;
                                item.Status = "修復完了 (バックアップ済)";
                                successCount++;
                                progress?.Report((item.FilePath, true));
                            }
                            else
                            {
                                item.Status = item.AssociatedOfficeItem.Status;
                                progress?.Report((item.FilePath, false));
                            }
                        }
                        else if (item.FileType.Contains(".lnk") && wsh is not null)
                        {
                            // バックアップ作成
                            string bakPath = item.FilePath + ".bak";
                            if (!File.Exists(bakPath))
                            {
                                File.Copy(item.FilePath, bakPath);
                            }

                            dynamic shortcut = wsh.CreateShortcut(item.FilePath);
                            shortcut.TargetPath = item.NewTarget;
                            shortcut.Save();

                            // Verify: 保存した .lnk を再度開き直して TargetPath が意図通り更新されたかを本番検証
                            dynamic verifyShortcut = wsh.CreateShortcut(item.FilePath);
                            string verifiedTarget = verifyShortcut.TargetPath;
                            if (string.Equals(verifiedTarget?.TrimEnd('\\'), item.NewTarget.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                            {
                                item.IsFixed = true;
                                item.Status = "修復完了 (検証済・バックアップ済)";
                                successCount++;
                                progress?.Report((item.FilePath, true));
                            }
                            else
                            {
                                // Verify NG: 意図したパスに書き換わっていないため、直前の .bak から即座に自動ロールバック
                                try
                                {
                                    if (File.Exists(bakPath))
                                    {
                                        File.Copy(bakPath, item.FilePath, overwrite: true);
                                    }
                                }
                                catch { }

                                item.Status = $"修復検証失敗 (自動復元済, 書込値: {verifiedTarget})";
                                progress?.Report((item.FilePath, false));
                            }
                        }
                    }
                    catch
                    {
                        // 例外発生時もバックアップから自動ロールバック
                        try
                        {
                            string bakPath = item.FilePath + ".bak";
                            if (File.Exists(bakPath))
                            {
                                File.Copy(bakPath, item.FilePath, overwrite: true);
                            }
                        }
                        catch { }

                        item.Status = "修復失敗 (自動復元済)";
                        progress?.Report((item.FilePath, false));
                    }
                }

                return successCount;
            }, ct);
        }

        /// <summary>
        /// GPO（グループポリシー）ログオンスクリプトや社内配布用のPowerShellスクリプトを自動生成する
        /// </summary>
        public void GenerateGpoLogonScript(string outputPath, string oldPath, string newPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# ==========================================================================");
            sb.AppendLine("# FolderMorpher - 全社PC用 ショートカット一括修復 ログオンスクリプト");
            sb.AppendLine($"# 旧パス: {oldPath}");
            sb.AppendLine($"# 新パス: {newPath}");
            sb.AppendLine($"# 生成日時: {DateTime.Now:yyyy/MM/dd HH:mm:ss}");
            sb.AppendLine("# 適用対象: GPO ユーザーの構成 > ポリシー > Windowsの設定 > スクリプト (ログオン)");
            sb.AppendLine("# ==========================================================================");
            sb.AppendLine();
            sb.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
            sb.AppendLine("$logFile = \"$env:TEMP\\FolderMorpher_LinkFix.log\"");
            sb.AppendLine("function Log($msg) { Add-Content -Path $logFile -Value \"[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] $msg\" -Encoding UTF8 }");
            sb.AppendLine();
            sb.AppendLine($"$oldPath = \"{oldPath.Replace("\"", "`\"")}\"");
            sb.AppendLine($"$newPath = \"{newPath.Replace("\"", "`\"")}\"");
            sb.AppendLine();
            sb.AppendLine("Log \"=== ショートカット修復スクリプト開始 ===\"");
            sb.AppendLine();
            sb.AppendLine("# 走査対象ディレクトリ（デスクトップ、ドキュメント、クイックアクセス、スタートメニュー等）");
            sb.AppendLine("$targetDirs = @(");
            sb.AppendLine("    [Environment]::GetFolderPath('Desktop'),");
            sb.AppendLine("    [Environment]::GetFolderPath('MyDocuments'),");
            sb.AppendLine("    \"$env:APPDATA\\Microsoft\\Windows\\Recent\",");
            sb.AppendLine("    \"$env:APPDATA\\Microsoft\\Windows\\Start Menu\\Programs\"");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("$wsh = New-Object -ComObject WScript.Shell");
            sb.AppendLine("$fixCount = 0");
            sb.AppendLine();
            sb.AppendLine("foreach ($dir in $targetDirs) {");
            sb.AppendLine("    if (-not (Test-Path $dir)) { continue }");
            sb.AppendLine("    Get-ChildItem -Path $dir -Filter '*.lnk' -Recurse -File | ForEach-Object {");
            sb.AppendLine("        try {");
            sb.AppendLine("            $shortcut = $wsh.CreateShortcut($_.FullName)");
            sb.AppendLine("            $target = $shortcut.TargetPath");
            sb.AppendLine("            if ($target -and $target.IndexOf($oldPath, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {");
            sb.AppendLine("                $newTarget = $target.Replace($oldPath, $newPath, [System.StringComparison]::OrdinalIgnoreCase)");
            sb.AppendLine("                # バックアップ作成");
            sb.AppendLine("                $bak = $_.FullName + '.bak'");
            sb.AppendLine("                if (-not (Test-Path $bak)) { Copy-Item $_.FullName $bak -Force }");
            sb.AppendLine("                $shortcut.TargetPath = $newTarget");
            sb.AppendLine("                $shortcut.Save()");
            sb.AppendLine("                $fixCount++");
            sb.AppendLine("                Log \"[修復成功] $($_.FullName) -> $newTarget\"");
            sb.AppendLine("            }");
            sb.AppendLine("        } catch {");
            sb.AppendLine("            Log \"[修復失敗] $($_.FullName): $_\"");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("Log \"=== 完了: $fixCount 件のショートカットを修復しました ===\"");
            sb.AppendLine();

            File.WriteAllText(outputPath, sb.ToString(), Encoding.UTF8);
        }
    }
}
