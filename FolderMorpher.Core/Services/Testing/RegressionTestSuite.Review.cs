using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text;
using System.Text.Json;
using AstraSize.Models;
using FolderMorpher.Models;

namespace FolderMorpher.Services.Testing;

public static partial class RegressionTestSuite
{
    private sealed class ReviewProgress<T>(Action<T> report) : IProgress<T> { public void Report(T value) => report(value); }

    private static void RequireReview(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void TestReviewOfficeXmlAndMediaOptions()
    {
        RequireReview(SafeFileReplace.HasEquivalentDacl("D:(A;;FR;;;WD)", "D:AI(A;;FR;;;WD)"), "Auto-inheritance bookkeeping was treated as an access change.");
        RequireReview(!SafeFileReplace.HasEquivalentDacl("D:(A;;FR;;;WD)", "D:P(A;;FR;;;WD)") &&
            !SafeFileReplace.HasEquivalentDacl("D:(A;;FR;;;WD)", "D:(A;;FW;;;WD)") &&
            !SafeFileReplace.HasEquivalentDacl("D:(A;;FR;;;WD)", "D:(A;ID;FR;;;WD)") &&
            !SafeFileReplace.HasEquivalentDacl("D:(D;;FW;;;WD)(A;;FR;;;WD)", "D:(A;;FR;;;WD)(D;;FW;;;WD)") &&
            !SafeFileReplace.HasEquivalentDacl("D:NO_ACCESS_CONTROL", "D:"), "Replacement DACL comparison ignored a security difference.");
        RequireReview(EffectiveAccessService.DeterminePermissionLevel(System.Security.AccessControl.FileSystemRights.WriteData) == EffectivePermissionLevel.Custom &&
            EffectiveAccessService.DeterminePermissionLevel(System.Security.AccessControl.FileSystemRights.Delete) == EffectivePermissionLevel.Custom, "Custom access was omitted.");
        foreach (int dimension in new[] { -1, 0, 63, 32769 })
        {
            bool rejected = false;
            try { new MediaOptimizeOptions { MaxDimension = dimension }.Validate(); }
            catch (ArgumentOutOfRangeException) { rejected = true; }
            RequireReview(rejected, "Unsafe image dimensions accepted.");
        }
        string fixture = Path.Combine(Path.GetTempPath(), "FM_ReviewDacl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            string original = Path.Combine(fixture, "original.txt"), temporary = Path.Combine(fixture, "temporary.txt");
            File.WriteAllText(original, "before"); File.WriteAllText(temporary, "after");
            var dacl = new System.Security.AccessControl.FileSecurity();
            dacl.SetAccessRuleProtection(true, false);
            dacl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            System.IO.FileSystemAclExtensions.SetAccessControl(new FileInfo(original), dacl);
            string expected = System.IO.FileSystemAclExtensions.GetAccessControl(new FileInfo(original)).GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access);
            SafeFileReplace.Replace(temporary, original);
            RequireReview(File.ReadAllText(original) == "after" && System.IO.FileSystemAclExtensions.GetAccessControl(new FileInfo(original)).GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access) == expected, "Replacement changed the protected DACL.");
        }
        finally { SafeDeleteDirectory(fixture); }
        var xml = OfficeLinkDocument.Parse("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship TargetMode=\"External\" Target=\"file:///old/book.xlsx\" /></Relationships>");
        RequireReview(OfficeLinkDocument.Replace(xml, "/old/", "/new&amp/日本語/") == 1, "External link not updated.");
        RequireReview(OfficeLinkDocument.Values(OfficeLinkDocument.Parse(xml.ToString())).Single().Contains("/new&amp/日本語/"), "XML escaping changed the replacement value.");
        var cells = OfficeLinkDocument.Parse("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><c><v>file:///old/book.xlsx</v></c><f>'file:///old/[book.xlsx]Sheet1'!A1</f></worksheet>");
        RequireReview(OfficeLinkDocument.Replace(cells, "/old/", "/new/") == 1 && cells.Descendants().Single(e => e.Name.LocalName == "v").Value.Contains("/old/"), "Normal cell text was modified as a link.");
        RequireReview(!OfficeLinkDocument.Values(OfficeLinkDocument.Parse("<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"/>")).Any(), "XML namespace falsely classified as a link.");
    }

    private static async Task TestReviewEncodingAndOcrAsync()
    {
        foreach (string character in new[] { "é", "日", "😀" })
        {
            byte[] bytes = Encoding.UTF8.GetBytes(new string('A', 4095) + character + "TAIL");
            using var stream = new MemoryStream(bytes);
            RequireReview(ContentExtractionService.DetectTextEncoding(stream).CodePage == Encoding.UTF8.CodePage, "UTF-8 split at sampling boundary was misclassified.");
        }
        var entries = Enumerable.Range(0, SearchEngineService.MaxRetainedResults + 23)
            .Select(i => new TreeCacheSearchEntry(@"C:\fixture\match" + i + ".txt", "match" + i + ".txt", 2, false, DateTime.UtcNow, DateTime.UtcNow));
        SearchProgressReport? countReport = null;
        var retained = await new SearchEngineService().SearchCachedEntriesAsync(entries, SearchQueryParser.Parse("match"), new ReviewProgress<SearchProgressReport>(p => countReport = p));
        RequireReview(retained.Count == SearchEngineService.MaxRetainedResults && countReport?.HitCount == SearchEngineService.MaxRetainedResults + 23 && countReport.TotalHitBytes == 2 * (SearchEngineService.MaxRetainedResults + 23), "Total matches were confused with retained rows.");
        string root = Path.Combine(Path.GetTempPath(), "FM_ReviewOcr_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // No installed Python, adjacent checkout, or model download may be needed.
            string? folderOverride = Environment.GetEnvironmentVariable("FOLDERMORPHER_OCR_DIR");
            string? filunestOverride = Environment.GetEnvironmentVariable("FILUNEST_OCR_DIR");
            try
            {
                Environment.SetEnvironmentVariable("FOLDERMORPHER_OCR_DIR", null);
                Environment.SetEnvironmentVariable("FILUNEST_OCR_DIR", null);
                OcrWorkerService.ResetEnvironmentCache();
                RequireReview(OcrWorkerService.IsEnvironmentAvailable(), "Embedded OCR runtime unavailable.");
                await using var client = await OcrWorkerService.TryCreateClientAsync();
                RequireReview(client != null, "Embedded OCR worker did not become ready.");
                string image = Path.Combine(root, "invoice.png");
                using (var bitmap = new Bitmap(1000, 220))
                using (var graphics = Graphics.FromImage(bitmap))
                using (var font = new Font("Arial", 48))
                {
                    graphics.Clear(Color.White);
                    graphics.DrawString("INVOICE 2026 ALPHA", font, Brushes.Black, 30, 65);
                    bitmap.Save(image, ImageFormat.Png);
                }
                var result = await client!.ProcessFileAsync(image);
                RequireReview(result.Success && result.Pages.Any(p => p.FullText.Contains("INVOICE", StringComparison.OrdinalIgnoreCase)), "Real embedded OCR inference failed: " + result.Warning);
                var query = SearchQueryParser.Parse("INVOICE|missing");
                query.SearchContentMode = true;
                RequireReview(OcrWorkerService.TryMatchOcrDocument(result, query, out _, out _), "OCR OR semantics differ from content search.");
                query = SearchQueryParser.Parse("INVOICE missing");
                query.SearchContentMode = true;
                RequireReview(!OcrWorkerService.TryMatchOcrDocument(result, query, out _, out _), "OCR AND requirement bypassed.");
            }
            finally
            {
                Environment.SetEnvironmentVariable("FOLDERMORPHER_OCR_DIR", folderOverride);
                Environment.SetEnvironmentVariable("FILUNEST_OCR_DIR", filunestOverride);
            }
        }
        finally { SafeDeleteDirectory(root); }
    }

    private static async Task TestReviewMigrationExecutionAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "FM_ReviewMigration_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string first = Path.Combine(root, "source & one!");
            string second = Path.Combine(root, "source two");
            string moved = Path.Combine(first, "moved");
            string target = Path.Combine(root, "target");
            string relocated = Path.Combine(target, "relocated");
            Directory.CreateDirectory(moved);
            Directory.CreateDirectory(second);
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(first, "root.txt"), "root-direct");
            File.WriteAllText(Path.Combine(second, "second.txt"), "second-source");
            File.WriteAllText(Path.Combine(moved, "child.txt"), "moved-child");
            File.WriteAllText(Path.Combine(target, "obsolete.txt"), "obsolete");
            string runner = Path.Combine(root, "MigrationRunner.ps1");
            using (var resource = typeof(MigrationPackageService).Assembly.GetManifestResourceStream("FolderMorpher.MigrationRunner.ps1")!)
            using (var output = File.Create(runner)) await resource.CopyToAsync(output);
            string plan = Path.Combine(root, "MigrationPlan.json");
            var units = new[] {
                new MigrationCopyUnit(first, target, new List<string> { moved }, 1),
                new MigrationCopyUnit(second, target, new List<string>(), 1),
                new MigrationCopyUnit(moved, relocated, new List<string>(), 2)
            };
            File.WriteAllText(plan, JsonSerializer.Serialize(new { Units = units, Threads = 2, CopyAcl = false }));
            async Task<int> Execute(int wave, bool dry = false)
            {
                var start = new ProcessStartInfo("pwsh.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (string arg in new[] { "-NoProfile", "-File", runner, "-PlanPath", plan, "-Wave", wave.ToString(), "-Mode", "CUTOVER" }) start.ArgumentList.Add(arg);
                if (dry) start.ArgumentList.Add("-DryRun");
                using var process = Process.Start(start)!;
                var stdout = process.StandardOutput.ReadToEndAsync();
                var stderr = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try { await process.WaitForExitAsync(timeout.Token); }
                catch { process.Kill(true); throw; }
                string error = await stderr;
                await stdout;
                if (process.ExitCode != 0 && !error.Contains("Conflicting source files")) Console.WriteLine("  Migration fixture rejected: " + error);
                return process.ExitCode;
            }
            RequireReview(await Execute(1, true) == 0 && File.Exists(Path.Combine(target, "obsolete.txt")) && !File.Exists(Path.Combine(target, "root.txt")), "Dry-run mutated target.");
            RequireReview(await Execute(2) == 0 && await Execute(1) == 0, "N:1 cutover failed.");
            RequireReview(File.ReadAllText(Path.Combine(target, "root.txt")) == "root-direct" && File.ReadAllText(Path.Combine(target, "second.txt")) == "second-source", "N:1 union or root-direct files lost.");
            RequireReview(File.Exists(Path.Combine(relocated, "child.txt")) && !Directory.Exists(Path.Combine(target, "moved")) && !File.Exists(Path.Combine(target, "obsolete.txt")), "Moved child copied twice or another wave deleted.");
            File.WriteAllText(Path.Combine(second, "root.txt"), "conflicting-content");
            File.WriteAllText(Path.Combine(target, "keep-on-failure.txt"), "keep");
            RequireReview(await Execute(1) != 0 && File.Exists(Path.Combine(target, "keep-on-failure.txt")) && File.ReadAllText(Path.Combine(target, "root.txt")) == "root-direct", "Collision did not abort before writes/removals.");
            var parent = new SimFolderNode { Name = "Root" };
            parent.MappedSourcePaths.Add(first);
            foreach (string name in new[] { "A", "B" }) parent.Children.Add(new SimFolderNode { Name = name, Parent = parent });
            var waves = new MigrationPackageService().PlanWaves(new[] { parent }, new MigrationPackageOptions { Policy = MigrationSplitPolicy.ByTopLevelFolder, TargetRoot = Path.Combine(root, "split-target") });
            RequireReview(waves.SelectMany(w => w.CopyUnits).Any(u => u.Source == first), "Wave splitting discarded parent source mapping.");
        }
        finally { SafeDeleteDirectory(root); }
    }
}
