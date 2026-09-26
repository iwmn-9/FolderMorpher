using AstraSize.Services;
using FolderMorpher.Models;
using FolderMorpher.Services;
using FolderMorpher.Services.Testing;

namespace FolderMorpher.Host;

public static class TestCommandRunner
{
    public static Task<bool> RunRegressionAsync() => RegressionTestSuite.RunAllTestsAsync();

    public static async Task RunScanAsync(string path)
    {
        Console.WriteLine($"[TEST-SCAN] Target: {path}");
        var (root, summary) = await new DiskScanService().ScanPathAsync(path, null, CancellationToken.None);
        Console.WriteLine($"[TEST-SCAN] Mode: {summary.ScanMode}, TotalSize: {root.SizeBytes} bytes, Files: {summary.TotalFiles}, Folders: {summary.TotalFolders}, Time: {summary.ElapsedSeconds}s");
        Console.WriteLine("[TEST-SCAN] ALL PASSED");
    }

    public static async Task RunSuiteAsync(string directory)
    {
        Console.WriteLine($"[TEST] === FolderMorpher 自動統合テスト開始: {directory} ===");
        var auditService = new AuditReportService();
        var options = new AuditOptions
        {
            TargetDirectory = directory,
            CheckDuplicates = true,
            CheckDormant = true,
            DormantYearsThreshold = 3.0,
            CheckPathLimits = true,
            MinFileSizeBytes = 1000
        };
        var (summary, items) = await auditService.RunAuditAsync(options, null, CancellationToken.None);
        Console.WriteLine($"[TEST-AUDIT] 総ファイル: {summary.TotalFilesScanned}, 重複: {summary.DuplicateCount}, 休眠: {summary.DormantCount}, 禁則: {summary.InvalidCharCount}, 課題数: {items.Count}");

        var csvPath = Path.Combine(directory, "test_audit.csv");
        auditService.ExportAuditCsv(csvPath, items);
        if (!File.Exists(csvPath)) throw new IOException("Audit CSV was not created.");

        var linkService = new LinkFixService();
        var gpoPath = Path.Combine(directory, "test_gpo.ps1");
        linkService.GenerateGpoLogonScript(gpoPath, @"\\OldServer\Share", @"\\NewServer\Share");
        if (!File.Exists(gpoPath)) throw new IOException("GPO script was not created.");

        var mediaService = new MediaOptimizerService();
        var mediaOptions = new MediaOptimizeOptions { TargetDirectory = directory, MinImageSizeBytes = 10 };
        var (images, videos) = await mediaService.ScanMediaAsync(mediaOptions, null, CancellationToken.None);
        Console.WriteLine($"[TEST-MEDIA] 画像: {images.Count}, 動画: {videos.Count}, 聖域保護画像: {images.Count(item => item.IsExcluded)}");

        var videoPath = Path.Combine(directory, "test_video_nightly.bat");
        mediaService.GenerateVideoCompressBatch(videoPath, videos);
        if (!File.Exists(videoPath)) throw new IOException("Video batch was not created.");

        var excelPath = Path.Combine(directory, "test_report.xlsx");
        new ExcelReportService().GenerateComprehensiveReport(excelPath, directory, summary, items, null, images.Concat(videos).ToList());
        if (!File.Exists(excelPath)) throw new IOException("Excel report was not created.");
        Console.WriteLine($"[TEST-EXCEL] Excelレポート出力: 成功 (サイズ: {new FileInfo(excelPath).Length} bytes)");
        Console.WriteLine("[TEST] === 全テスト完了: ALL PASSED ===");
    }
}
