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
    }

    public class LinkFixService
    {
        public async Task<List<LinkFixItem>> ScanAndFixLinksAsync(
            string searchDirectory,
            string oldPathPattern,
            string newPathPattern,
            bool dryRun,
            CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var results = new List<LinkFixItem>();
                if (!Directory.Exists(searchDirectory)) return results;

                var dir = new DirectoryInfo(searchDirectory);

                // 1. Process Shortcuts (.lnk)
                dynamic? wsh = null;
                try
                {
                    var wshType = Type.GetTypeFromProgID("WScript.Shell");
                    if (wshType != null) wsh = Activator.CreateInstance(wshType);
                }
                catch { }

                foreach (var fi in dir.EnumerateFiles("*.lnk", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) break;
                    if (wsh == null) break;

                    try
                    {
                        var shortcut = wsh.CreateShortcut(fi.FullName);
                        string target = shortcut.TargetPath;
                        if (!string.IsNullOrEmpty(target) && target.Contains(oldPathPattern, StringComparison.OrdinalIgnoreCase))
                        {
                            string updatedTarget = target.Replace(oldPathPattern, newPathPattern, StringComparison.OrdinalIgnoreCase);
                            var item = new LinkFixItem
                            {
                                FilePath = fi.FullName,
                                FileName = fi.Name,
                                FileType = "ショートカット (.lnk)",
                                OldTarget = target,
                                NewTarget = updatedTarget,
                                Status = dryRun ? "置換候補 (シミュレーション)" : "置換完了"
                            };

                            if (!dryRun)
                            {
                                shortcut.TargetPath = updatedTarget;
                                shortcut.Save();
                                item.IsFixed = true;
                            }

                            results.Add(item);
                        }
                    }
                    catch { }
                }

                // 2. Process Modern Excel (.xlsx)
                foreach (var fi in dir.EnumerateFiles("*.xlsx", SearchOption.AllDirectories))
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        bool hasOldLink = false;
                        using (var zip = ZipFile.Open(fi.FullName, dryRun ? ZipArchiveMode.Read : ZipArchiveMode.Update))
                        {
                            foreach (var entry in zip.Entries)
                            {
                                if (entry.FullName.Contains("externalLinks", StringComparison.OrdinalIgnoreCase) && entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                                {
                                    using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
                                    string content = reader.ReadToEnd();
                                    if (content.Contains(oldPathPattern, StringComparison.OrdinalIgnoreCase))
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
                                Status = dryRun ? "外部リンク検出 (シミュレーション)" : "置換対象"
                            });
                        }
                    }
                    catch { }
                }

                return results;
            }, ct);
        }
    }
}
