using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FolderMorpher.Services
{
    public class OfficeLinkItem
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public string FoundPattern { get; set; } = string.Empty;
        public string TargetReplacement { get; set; } = string.Empty;
        public bool IsLocked { get; set; }
        public string LockUser { get; set; } = string.Empty;
        public string LinkType { get; set; } = string.Empty; // 外部ブック参照 / ハイパーリンク / VBAマクロ
        public string Status { get; set; } = "検出";
        public bool IsFixed { get; set; }
    }

    public class OfficeLinkFixService
    {
        private static readonly string[] SupportedModernExtensions = { ".xlsx", ".xlsm", ".docx", ".pptx" };
        private static readonly string[] SupportedLegacyExtensions = { ".xls" };

        public async Task<List<OfficeLinkItem>> ScanOfficeLinksAsync(
            string directoryPath,
            string oldPattern,
            string newPattern,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                var results = new List<OfficeLinkItem>();
                if (!Directory.Exists(directoryPath)) return results;

                var dir = new DirectoryInfo(directoryPath);
                int scannedCount = 0;

                // 全対象ファイルを安全に収集（アクセス拒否で即死しない）
                var files = SafeFileEnumerator.EnumerateFilesSafe(directoryPath, "*.*", null, ct)
                    .Where(f => SupportedModernExtensions.Contains(f.Extension.ToLowerInvariant()) ||
                                SupportedLegacyExtensions.Contains(f.Extension.ToLowerInvariant()))
                    .ToList();

                foreach (var fi in files)
                {
                    ct.ThrowIfCancellationRequested();
                    scannedCount++;
                    if (scannedCount % 20 == 0)
                        progress?.Report($"Officeファイル走査中: {scannedCount}/{files.Count} 件 ({fi.Name})...");

                    // 一時ロックファイル (~$) の場合はスキップ
                    if (fi.Name.StartsWith("~$")) continue;

                    string ext = fi.Extension.ToLowerInvariant();

                    // ロック検知（~$filename が同階層に存在するか）
                    bool isLocked = false;
                    string lockUser = string.Empty;
                    string lockFilePath = Path.Combine(fi.DirectoryName ?? string.Empty, "~$" + fi.Name);
                    if (File.Exists(lockFilePath))
                    {
                        isLocked = true;
                        lockUser = GetLockUser(lockFilePath);
                    }

                    // 1. OpenXML形式 (.xlsx, .xlsm, .docx, .pptx)
                    if (SupportedModernExtensions.Contains(ext))
                    {
                        var foundTypes = CheckModernOfficeFile(fi.FullName, oldPattern);
                        if (foundTypes.Count > 0)
                        {
                            results.Add(new OfficeLinkItem
                            {
                                FilePath = fi.FullName,
                                FileName = fi.Name,
                                Extension = ext,
                                FoundPattern = oldPattern,
                                TargetReplacement = newPattern,
                                IsLocked = isLocked,
                                LockUser = lockUser,
                                LinkType = string.Join(", ", foundTypes),
                                Status = isLocked ? $"ロック中 ({lockUser})" : "置換候補"
                            });
                        }
                    }
                    // 2. Legacy形式 (.xls)
                    else if (SupportedLegacyExtensions.Contains(ext))
                    {
                        bool found = CheckLegacyExcelFile(fi.FullName, oldPattern);
                        if (found)
                        {
                            results.Add(new OfficeLinkItem
                            {
                                FilePath = fi.FullName,
                                FileName = fi.Name,
                                Extension = ext,
                                FoundPattern = oldPattern,
                                TargetReplacement = newPattern,
                                IsLocked = isLocked,
                                LockUser = lockUser,
                                LinkType = "Excel97-2003 バイナリリンク",
                                Status = isLocked ? $"ロック中 ({lockUser})" : "検出 (手動/xlsx変換推奨)"
                            });
                        }
                    }
                }

                progress?.Report($"走査完了: {results.Count} 件のOfficeリンクを検出しました。");
                return results;
            }, ct);
        }

        public async Task<int> ExecuteOfficeFixAsync(
            List<OfficeLinkItem> items,
            IProgress<(string File, bool Success, string Msg)>? progress,
            CancellationToken ct)
        {
            return await Task.Run(() =>
            {
                int successCount = 0;

                foreach (var item in items)
                {
                    ct.ThrowIfCancellationRequested();

                    // ロック中または既に修正済みはスキップ
                    if (item.IsLocked || item.IsFixed) continue;
                    if (string.IsNullOrEmpty(item.TargetReplacement)) continue;

                    string ext = item.Extension.ToLowerInvariant();
                    if (!SupportedModernExtensions.Contains(ext))
                    {
                        // .xls はバイナリ破損リスク回避のため自動置換対象外
                        item.Status = "スキップ (旧xls形式は非破壊保護)";
                        progress?.Report((item.FilePath, false, "旧xls形式は非破壊保護のためスキップ"));
                        continue;
                    }

                    try
                    {
                        // 1. バックアップ作成
                        string backupPath = item.FilePath + ".bak";
                        if (!File.Exists(backupPath))
                        {
                            File.Copy(item.FilePath, backupPath);
                        }

                        // タイムスタンプ退避
                        var origTime = File.GetLastWriteTime(item.FilePath);

                        // 2. ZIP内部のXMLエントリを置換
                        bool modified = false;
                        using (var zip = ZipFile.Open(item.FilePath, ZipArchiveMode.Update))
                        {
                            // externalLinks, sheets, rels, workbook などを対象に置換
                            var targetEntries = zip.Entries.Where(e =>
                                e.FullName.Contains("externalLink", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.Contains("worksheets", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.Contains("_rels", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
                            ).ToList();

                            foreach (var entry in targetEntries)
                            {
                                string content;
                                using (var reader = new StreamReader(entry.Open(), Encoding.UTF8))
                                {
                                    content = reader.ReadToEnd();
                                }

                                if (content.Contains(item.FoundPattern, StringComparison.OrdinalIgnoreCase))
                                {
                                    // 大文字小文字を保持しながら置換
                                    string updated = content.Replace(item.FoundPattern, item.TargetReplacement, StringComparison.OrdinalIgnoreCase);
                                    
                                    // エントリを上書き再書き込み
                                    entry.Delete();
                                    var newEntry = zip.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                                    using var writer = new StreamWriter(newEntry.Open(), Encoding.UTF8);
                                    writer.Write(updated);
                                    modified = true;
                                }
                            }
                        }

                        // 3. タイムスタンプ復元
                        if (modified)
                        {
                            File.SetLastWriteTime(item.FilePath, origTime);
                            item.IsFixed = true;
                            item.Status = "修復完了 (バックアップ済)";
                            successCount++;
                            progress?.Report((item.FilePath, true, "修復完了"));
                        }
                        else
                        {
                            item.Status = "該当なし";
                        }
                    }
                    catch (Exception ex)
                    {
                        item.Status = $"失敗: {ex.Message}";
                        progress?.Report((item.FilePath, false, ex.Message));
                    }
                }

                return successCount;
            }, ct);
        }

        private static List<string> CheckModernOfficeFile(string filePath, string pattern)
        {
            var detected = new List<string>();
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var zip = new ZipArchive(fs, ZipArchiveMode.Read);

                foreach (var entry in zip.Entries)
                {
                    // 外部参照・リレーション・数式XMLの確認
                    if (entry.FullName.Contains("externalLink", StringComparison.OrdinalIgnoreCase))
                    {
                        if (EntryContainsText(entry, pattern))
                        {
                            if (!detected.Contains("外部ブック参照")) detected.Add("外部ブック参照");
                        }
                    }
                    else if (entry.FullName.Contains("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        if (EntryContainsBinaryText(entry, pattern))
                        {
                            if (!detected.Contains("VBAマクロ")) detected.Add("VBAマクロ");
                        }
                    }
                    else if (entry.FullName.Contains("worksheets", StringComparison.OrdinalIgnoreCase) ||
                             entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                    {
                        if (EntryContainsText(entry, pattern))
                        {
                            if (!detected.Contains("数式・ハイパーリンク")) detected.Add("数式・ハイパーリンク");
                        }
                    }
                }
            }
            catch { }

            return detected;
        }

        private static bool EntryContainsText(ZipArchiveEntry entry, string pattern)
        {
            try
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                string text = reader.ReadToEnd();
                return text.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool EntryContainsBinaryText(ZipArchiveEntry entry, string pattern)
        {
            try
            {
                using var stream = entry.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                var bytes = ms.ToArray();
                string ascii = Encoding.ASCII.GetString(bytes);
                string utf16 = Encoding.Unicode.GetString(bytes);
                return ascii.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                       utf16.Contains(pattern, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        private static bool CheckLegacyExcelFile(string filePath, string pattern)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                byte[] buffer = new byte[65536];
                int read;
                string carry = string.Empty;

                while ((read = fs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    string chunk = carry + Encoding.Default.GetString(buffer, 0, read);
                    string chunk16 = carry + Encoding.Unicode.GetString(buffer, 0, read);

                    if (chunk.Contains(pattern, StringComparison.OrdinalIgnoreCase) ||
                        chunk16.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    carry = chunk.Length > 200 ? chunk[^200..] : chunk;
                }
            }
            catch { }

            return false;
        }

        private static string GetLockUser(string lockFilePath)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(lockFilePath);
                if (bytes.Length > 2)
                {
                    // ~$ ファイルは先頭数バイトの後にユーザー名（ASCII）が記録されている
                    int nameLen = bytes[1];
                    if (nameLen > 0 && bytes.Length >= 2 + nameLen)
                    {
                        return Encoding.Default.GetString(bytes, 2, nameLen).Trim('\0', ' ');
                    }
                }
            }
            catch { }
            return "編集中";
        }
    }
}
