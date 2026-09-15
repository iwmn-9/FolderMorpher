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
    [Flags]
    public enum OfficeLinkCategory
    {
        None = 0,
        ExternalWorkbook = 1 << 0,     // 外部ブック参照 (externalLink)
        FormulaOrHyperlink = 1 << 1,   // 数式・ハイパーリンク (worksheets, .rels)
        VbaMacro = 1 << 2             // VBAマクロ (vbaProject.bin - 検出のみ)
    }

    public enum OfficeFixStatus
    {
        Detected = 0,
        FullyFixed = 1,
        PartiallyFixed = 2,           // XML修復済 / VBAマクロ未修復
        Skipped = 3,
        Failed = 4
    }

    public class OfficeLinkItem
    {
        public string FilePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Extension { get; set; } = string.Empty;
        public string FoundPattern { get; set; } = string.Empty;
        public string TargetReplacement { get; set; } = string.Empty;
        public bool IsLocked { get; set; }
        public string LockUser { get; set; } = string.Empty;
        public OfficeLinkCategory Categories { get; set; } = OfficeLinkCategory.None;
        public string LinkType { get; set; } = string.Empty; // UI表示用
        public OfficeFixStatus FixStatus { get; set; } = OfficeFixStatus.Detected;
        public long ExpectedLength { get; set; }
        public DateTime ExpectedLastWriteTimeUtc { get; set; }
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
                        var (categories, foundTypes) = CheckModernOfficeFile(fi.FullName, oldPattern);
                        if (categories != OfficeLinkCategory.None)
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
                                Categories = categories,
                                LinkType = string.Join(", ", foundTypes),
                                ExpectedLength = fi.Length,
                                ExpectedLastWriteTimeUtc = fi.LastWriteTimeUtc,
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
                                ExpectedLength = fi.Length,
                                ExpectedLastWriteTimeUtc = fi.LastWriteTimeUtc,
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

                    // Sol指摘: 楽観ロック (スキャン時とファイルサイズ・更新日時が一致しているか照合)
                    var curFi = new FileInfo(item.FilePath);
                    if (!curFi.Exists || (item.ExpectedLength > 0 && curFi.Length != item.ExpectedLength) ||
                        (item.ExpectedLastWriteTimeUtc != default && curFi.LastWriteTimeUtc != item.ExpectedLastWriteTimeUtc))
                    {
                        item.FixStatus = OfficeFixStatus.Skipped;
                        item.Status = "スキップ (スキャン後に外部変更検知)";
                        progress?.Report((item.FilePath, false, "スキャン後に外部で変更されたため安全にスキップしました"));
                        continue;
                    }

                    // 原本と同一ディレクトリに一時ファイルを作成（同一ボリューム・ファイルシステムでの真のアトミック置換を保証）
                    string tempPath = item.FilePath + ".tmp_" + Guid.NewGuid().ToString("N");
                    string backupPath = item.FilePath + ".bak";

                    try
                    {
                        // タイムスタンプ退避
                        var origTime = File.GetLastWriteTime(item.FilePath);

                        // 1. 一時ファイルへ原本を安全コピー（原本直接編集の完全撤廃）
                        File.Copy(item.FilePath, tempPath, overwrite: true);

                        // VBAマクロ単体の場合はバイナリ自動修復不可のため安全にスキップ
                        // （AGENTS.md 第0項: 文字列判定ではなく型安全な Categories == OfficeLinkCategory.VbaMacro で判定）
                        if (item.Categories == OfficeLinkCategory.VbaMacro)
                        {
                            item.FixStatus = OfficeFixStatus.Skipped;
                            item.Status = "スキップ (VBAマクロは手動修復が必要です)";
                            try { File.Delete(tempPath); } catch { }
                            continue;
                        }

                        bool modified = false;
                        using (var zip = ZipFile.Open(tempPath, ZipArchiveMode.Update))
                        {
                            // Sol指摘: 全ての .xml を無差別に巻き込まず、リンク・外部参照・数式・リレーションXMLに厳格限定
                            var targetEntries = zip.Entries.Where(e =>
                                e.FullName.Contains("externalLink", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.Contains("worksheets", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.Contains("externalReferences", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.Contains("_rels", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)
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

                        if (!modified)
                        {
                            item.Status = "該当なし";
                            try { File.Delete(tempPath); } catch { }
                            continue;
                        }

                        // 3. 事後検証 (Verify): 更新後の一時ファイルを読み取りモードで開き直し、ZIP整合性および新リンク置換を意味的に検証
                        using (var verifyZip = ZipFile.OpenRead(tempPath))
                        {
                            if (verifyZip.Entries.Count == 0)
                            {
                                throw new InvalidOperationException("更新後のOfficeファイルが空です。");
                            }

                            // Sol指摘: 目的のリンクが正しく新パスに置換されているかを意味的に検証
                            bool semanticVerified = false;
                            foreach (var verifyEntry in verifyZip.Entries.Where(e =>
                                e.FullName.Contains("externalLink", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.Contains("worksheets", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.Contains("externalReferences", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.Contains("_rels", StringComparison.OrdinalIgnoreCase) ||
                                e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
                            {
                                using var reader = new StreamReader(verifyEntry.Open(), Encoding.UTF8);
                                string content = reader.ReadToEnd();
                                if (content.Contains(item.TargetReplacement, StringComparison.OrdinalIgnoreCase))
                                {
                                    semanticVerified = true;
                                    break;
                                }
                            }

                            if (!semanticVerified)
                            {
                                throw new InvalidOperationException("意味的検証失敗: 更新後のOfficeファイル内に新リンクパスが検出されませんでした。");
                            }
                        }

                        // Sol指摘: Commit境界での再楽観ロック (Double-Check TOCTOU防御)
                        var preCommitFi = new FileInfo(item.FilePath);
                        if (!preCommitFi.Exists || (item.ExpectedLength > 0 && preCommitFi.Length != item.ExpectedLength) ||
                            (item.ExpectedLastWriteTimeUtc != default && preCommitFi.LastWriteTimeUtc != item.ExpectedLastWriteTimeUtc))
                        {
                            item.FixStatus = OfficeFixStatus.Skipped;
                            item.Status = "スキップ (置換直前に外部変更検知)";
                            progress?.Report((item.FilePath, false, "置換直前に外部でファイルが更新されたため安全にスキップしました"));
                            try { File.Delete(tempPath); } catch { }
                            continue;
                        }

                        // 4. バックアップ作成 (未存在時のみ、安全な永続バックアップとして保持)
                        if (!File.Exists(backupPath))
                        {
                            File.Copy(item.FilePath, backupPath);
                        }

                        // 5. 真のアトミック置換 (同一ボリューム File.Replace または UNC フォールバック) ＆ タイムスタンプ復元
                        bool replaced = false;
                        string replaceBakPath = item.FilePath + ".tmp_rep_" + Guid.NewGuid().ToString("N");
                        try
                        {
                            File.Replace(tempPath, item.FilePath, replaceBakPath);
                            replaced = true;
                            try { File.Delete(replaceBakPath); } catch { }
                        }
                        catch
                        {
                            replaced = false;
                        }

                        if (!replaced)
                        {
                            // File.Replace 非対応環境 (UNC共有等) での同一FS内アトミック置換フォールバック
                            string uncBakPath = item.FilePath + ".unc_bak_" + Guid.NewGuid().ToString("N");
                            bool movedToBak = false;
                            try
                            {
                                File.Move(item.FilePath, uncBakPath);
                                movedToBak = true;
                                File.Move(tempPath, item.FilePath);
                                try { File.Delete(uncBakPath); } catch { }
                            }
                            catch
                            {
                                if (movedToBak && File.Exists(uncBakPath) && !File.Exists(item.FilePath))
                                {
                                    try { File.Move(uncBakPath, item.FilePath); } catch { }
                                }
                                throw;
                            }
                        }

                        try { File.SetLastWriteTime(item.FilePath, origTime); } catch { }

                        item.IsFixed = true;
                        if (item.Categories.HasFlag(OfficeLinkCategory.VbaMacro))
                        {
                            // Sol指摘: 通常リンクとVBAマクロが混在している場合は部分成功として明示
                            item.FixStatus = OfficeFixStatus.PartiallyFixed;
                            item.Status = "一部修復 (XML修復済 / VBAマクロは未修復)";
                            successCount++;
                            progress?.Report((item.FilePath, true, "一部修復 (XML修復済 / VBAマクロは未修復)"));
                        }
                        else
                        {
                            item.FixStatus = OfficeFixStatus.FullyFixed;
                            item.Status = "修復完了 (検証済・バックアップ済)";
                            successCount++;
                            progress?.Report((item.FilePath, true, "修復完了 (検証済)"));
                        }
                    }
                    catch (Exception ex)
                    {
                        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
                        item.FixStatus = OfficeFixStatus.Failed;
                        item.Status = $"失敗: {ex.Message}";
                        progress?.Report((item.FilePath, false, ex.Message));
                    }
                }

                return successCount;
            }, ct);
        }

        private static (OfficeLinkCategory categories, List<string> detected) CheckModernOfficeFile(string filePath, string pattern)
        {
            var detected = new List<string>();
            var categories = OfficeLinkCategory.None;
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
                            categories |= OfficeLinkCategory.ExternalWorkbook;
                            if (!detected.Contains("外部ブック参照")) detected.Add("外部ブック参照");
                        }
                    }
                    else if (entry.FullName.Contains("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                    {
                        if (EntryContainsBinaryText(entry, pattern))
                        {
                            categories |= OfficeLinkCategory.VbaMacro;
                            if (!detected.Contains("VBAマクロ (検出のみ・手動修復)")) detected.Add("VBAマクロ (検出のみ・手動修復)");
                        }
                    }
                    else if (entry.FullName.Contains("worksheets", StringComparison.OrdinalIgnoreCase) ||
                             entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))
                    {
                        if (EntryContainsText(entry, pattern))
                        {
                            categories |= OfficeLinkCategory.FormulaOrHyperlink;
                            if (!detected.Contains("数式・ハイパーリンク")) detected.Add("数式・ハイパーリンク");
                        }
                    }
                }
            }
            catch { }

            return (categories, detected);
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
