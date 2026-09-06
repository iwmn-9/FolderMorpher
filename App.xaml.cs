using System;
using System.IO;
using System.Windows;

namespace AstraSize
{
    public partial class App : Application
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AstraSize",
            "debug_startup.log");

        public App()
        {
            var dir = Path.GetDirectoryName(LogPath)!;
            Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] App starting...\n");

            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] AppDomain Unhandled: {e.ExceptionObject}\n");
            };

            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] TaskScheduler Unobserved: {e.Exception}\n");
                e.SetObserved();
            };

            DispatcherUnhandledException += (s, args) =>
            {
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] Dispatcher Unhandled: {args.Exception}\n");
                args.Handled = true;
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] OnStartup fired.\n");
            base.OnStartup(e);

            string? snapshotPath = null;
            bool collapseSidebar = false;
            int selectTab = 0;
            string? testSuiteDir = null;
            string? forceLang = null;
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--snapshot" && i + 1 < e.Args.Length)
                {
                    snapshotPath = e.Args[i + 1];
                }
                else if (e.Args[i] == "--tab" && i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], out int tabIdx))
                {
                    selectTab = tabIdx;
                }
                else if (e.Args[i] == "--collapse")
                {
                    collapseSidebar = true;
                }
                else if (e.Args[i] == "--test-suite" && i + 1 < e.Args.Length)
                {
                    testSuiteDir = e.Args[i + 1];
                }
                else if (e.Args[i] == "--lang" && i + 1 < e.Args.Length)
                {
                    forceLang = e.Args[i + 1];
                }
            }

            if (!string.IsNullOrEmpty(testSuiteDir))
            {
                Task.Run(async () =>
                {
                    try
                    {
                        Console.WriteLine($"[TEST] === FolderMorpher 自動統合テスト開始: {testSuiteDir} ===");

                        // 1. Audit Test
                        var auditService = new FolderMorpher.Services.AuditReportService();
                        var opt = new FolderMorpher.Models.AuditOptions
                        {
                            TargetDirectory = testSuiteDir,
                            CheckDuplicates = true,
                            CheckDormant = true,
                            DormantYearsThreshold = 3.0,
                            CheckPathLimits = true,
                            MinFileSizeBytes = 1000
                        };
                        using var cts = new System.Threading.CancellationTokenSource();
                        var (summary, items) = await auditService.RunAuditAsync(opt, null, cts.Token);
                        Console.WriteLine($"[TEST-AUDIT] 総ファイル: {summary.TotalFilesScanned}, 重複: {summary.DuplicateCount}, 休眠: {summary.DormantCount}, 禁則: {summary.InvalidCharCount}, 課題数: {items.Count}");

                        // 2. CSV Export Test
                        string csvOut = Path.Combine(testSuiteDir, "test_audit.csv");
                        auditService.ExportAuditCsv(csvOut, items);
                        Console.WriteLine($"[TEST-CSV] CSV出力: {(File.Exists(csvOut) ? "成功" : "失敗")}");

                        // 3. Robocopy Archive Script Test
                        string batOut = Path.Combine(testSuiteDir, "test_archive.bat");
                        auditService.GenerateArchiveRobocopyScript(batOut, items, testSuiteDir, @"C:\Archive_Dest");
                        Console.WriteLine($"[TEST-BAT] 退避バッチ出力: {(File.Exists(batOut) ? "成功" : "失敗")}");

                        // 4. GPO Script Test
                        var linkFixService = new FolderMorpher.Services.LinkFixService();
                        string gpoOut = Path.Combine(testSuiteDir, "test_gpo.ps1");
                        linkFixService.GenerateGpoLogonScript(gpoOut, @"\\OldServer\Share", @"\\NewServer\Share");
                        Console.WriteLine($"[TEST-GPO] GPOスクリプト出力: {(File.Exists(gpoOut) ? "成功" : "失敗")}");

                        // 5. Media Optimizer Test
                        var mediaService = new FolderMorpher.Services.MediaOptimizerService();
                        var mediaOpt = new FolderMorpher.Models.MediaOptimizeOptions
                        {
                            TargetDirectory = testSuiteDir,
                            MinImageSizeBytes = 10
                        };
                        var (images, videos) = await mediaService.ScanMediaAsync(mediaOpt, null, cts.Token);
                        Console.WriteLine($"[TEST-MEDIA] 画像: {images.Count}, 動画: {videos.Count}, 聖域保護画像: {images.Count(i => i.IsExcluded)}");

                        string videoBatOut = Path.Combine(testSuiteDir, "test_video_nightly.bat");
                        mediaService.GenerateVideoCompressBatch(videoBatOut, videos);
                        Console.WriteLine($"[TEST-VIDEO-BAT] 夜間動画バッチ出力: {(File.Exists(videoBatOut) ? "成功" : "失敗")}");

                        // 6. Excel Report Test
                        var excelService = new FolderMorpher.Services.ExcelReportService();
                        string excelOut = Path.Combine(testSuiteDir, "test_report.xlsx");
                        excelService.GenerateComprehensiveReport(excelOut, testSuiteDir, summary, items, null, images.Concat(videos).ToList());
                        Console.WriteLine($"[TEST-EXCEL] Excelレポート出力: {(File.Exists(excelOut) ? "成功 (サイズ: " + new FileInfo(excelOut).Length + " bytes)" : "失敗")}");

                        Console.WriteLine("[TEST] === 全テスト完了: ALL PASSED ===");
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TEST-ERROR] {ex}");
                        Environment.Exit(1);
                    }
                });
                return;
            }

            if (!string.IsNullOrEmpty(snapshotPath))
            {
                EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler(async (sender, args) =>
                {
                    if (sender is MainWindow mw)
                    {
                        try
                        {
                            if (forceLang == "en") FolderMorpher.Services.LocalizationService.Instance.SetLanguage(FolderMorpher.Services.AppLanguage.English);
                            else if (forceLang == "ja") FolderMorpher.Services.LocalizationService.Instance.SetLanguage(FolderMorpher.Services.AppLanguage.Japanese);

                            if (selectTab == 1) mw.NavTabLiveAcl.IsChecked = true;
                            else if (selectTab == 2) mw.NavTabSimulation.IsChecked = true;
                            else if (selectTab == 3) mw.NavTabLinkFix.IsChecked = true;
                            else if (selectTab == 4) mw.NavTabAudit.IsChecked = true;
                            else if (selectTab == 5) mw.NavTabMedia.IsChecked = true;
                            else mw.NavTabStorage.IsChecked = true;

                            if (collapseSidebar)
                            {
                                mw.SidebarToggleButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                            }

                            await System.Threading.Tasks.Task.Delay(800);
                            mw.UpdateLayout();

                            int w = (int)mw.ActualWidth;
                            int h = (int)mw.ActualHeight;
                            if (w <= 0) w = 1480;
                            if (h <= 0) h = 920;

                            var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                            rtb.Render(mw);

                            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                            using (var fs = File.Create(snapshotPath))
                            {
                                enc.Save(fs);
                            }
                            File.AppendAllText(LogPath, $"Snapshot saved to {snapshotPath}\n");
                        }
                        catch (Exception ex)
                        {
                            File.AppendAllText(LogPath, $"Snapshot error: {ex}\n");
                        }
                        finally
                        {
                            Shutdown(0);
                        }
                    }
                }));
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] OnExit fired, ExitCode={e.ApplicationExitCode}\n");
            base.OnExit(e);
        }
    }
}
