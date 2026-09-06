using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AstraSize.Services
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
        public bool NeedsFix => !IsFixed && !string.IsNullOrEmpty(NewTarget) && !string.Equals(OldTarget, NewTarget, StringComparison.OrdinalIgnoreCase);
    }

    public class LinkFixService
    {
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
                foreach (var fi in dir.EnumerateFiles("*.lnk", SearchOption.AllDirectories))
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

                // Modern Excel (.xlsx)
                foreach (var fi in dir.EnumerateFiles("*.xlsx", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        bool hasOldLink = false;
                        using (var zip = ZipFile.Open(fi.FullName, ZipArchiveMode.Read))
                        {
                            foreach (var entry in zip.Entries)
                            {
                                if (entry.FullName.Contains("externalLinks", StringComparison.OrdinalIgnoreCase) && entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                                {
                                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                                    string content = reader.ReadToEnd();
                                    if (!string.IsNullOrEmpty(oldPathPattern) && content.Contains(oldPathPattern, StringComparison.OrdinalIgnoreCase))
                                    {
                                        hasOldLink = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (hasOldLink)
                        {
                            results.Add(new LinkFixItem
                            {
                                FilePath = fi.FullName,
                                FileName = fi.Name,
                                FileType = "Excel ブック (.xlsx)",
                                OldTarget = oldPathPattern,
                                NewTarget = newPathPattern,
                                Status = "外部リンク検出"
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
                        if (item.FileType.Contains(".lnk") && wsh is not null)
                        {
                            // Create backup
                            string bakPath = item.FilePath + ".bak";
                            if (!File.Exists(bakPath))
                            {
                                File.Copy(item.FilePath, bakPath);
                            }

                            dynamic shortcut = wsh.CreateShortcut(item.FilePath);
                            shortcut.TargetPath = item.NewTarget;
                            shortcut.Save();
                            item.IsFixed = true;
                            item.Status = "修復完了 (バックアップ済)";
                            successCount++;
                            progress?.Report((item.FilePath, true));
                        }
                    }
                    catch
                    {
                        item.Status = "修復失敗";
                        progress?.Report((item.FilePath, false));
                    }
                }

                return successCount;
            }, ct);
        }

        public async Task<List<LinkFixItem>> ScanAndFixLinksAsync(
            string searchDirectory,
            string oldPathPattern,
            string newPathPattern,
            bool dryRun,
            CancellationToken ct)
        {
            return await ScanShortcutsAsync(searchDirectory, oldPathPattern, newPathPattern, null, ct);
        }
    }
}
