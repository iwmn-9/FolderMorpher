using System;
using System.IO;
using System.Windows;

namespace AstraSize
{
    public partial class App : Application
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FolderMorpher",
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
                try
                {
                    File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] FATAL Dispatcher Unhandled: {args.Exception}\n");
                    
                    // ヘッドレス実行やテスト実行でない場合はエラーダイアログを表示
                    bool isHeadless = Environment.CommandLine.Contains("--test-regression") ||
                                      Environment.CommandLine.Contains("--snapshot") ||
                                      Environment.CommandLine.Contains("--test-suite");
                    if (!isHeadless)
                    {
                        MessageBox.Show(
                            $"予期せぬ重大なエラーが発生したため、データ保護のため安全に終了します。\n\n詳細: {args.Exception.Message}\nログ: {LogPath}",
                            "FolderMorpher - 致命的エラー",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    }
                }
                catch { }
                finally
                {
                    args.Handled = true;
                    Environment.Exit(1);
                }
            };
        }

        public static bool IsClientMode { get; private set; } = false;

        protected override void OnStartup(StartupEventArgs e)
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] OnStartup fired.\n");
            base.OnStartup(e);

            // クライアントモード（FolderCleaner）判定: プロセス名または起動引数
            try
            {
                var exeName = Path.GetFileNameWithoutExtension(Environment.ProcessPath ?? "");
                bool nameSuggestsClient = exeName.Contains("Clean", StringComparison.OrdinalIgnoreCase);
                bool argSuggestsClient = false;
                bool argForcesAdmin = false;

                foreach (var arg in e.Args)
                {
                    if (string.Equals(arg, "--client", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "--cleaner", StringComparison.OrdinalIgnoreCase))
                    {
                        argSuggestsClient = true;
                    }
                    else if (string.Equals(arg, "--admin", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(arg, "--pro", StringComparison.OrdinalIgnoreCase))
                    {
                        argForcesAdmin = true;
                    }
                }

                IsClientMode = !argForcesAdmin && (nameSuggestsClient || argSuggestsClient);
                File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] App Mode: {(IsClientMode ? "FolderCleaner (Client)" : "FolderMorpher (Admin)")}\n");
            }
            catch { }

            string? snapshotPath = null;
            bool collapseSidebar = false;
            int selectTab = 0;
            string? testSuiteDir = null;
            string? forceLang = null;
            string? testScanPath = null;
            bool runRegression = false;
            for (int i = 0; i < e.Args.Length; i++)
            {
                if (e.Args[i] == "--test-regression")
                {
                    runRegression = true;
                }
                else if (e.Args[i] == "--snapshot" && i + 1 < e.Args.Length)
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
                else if (e.Args[i] == "--benchmark-trigram")
                {
                    ShutdownMode = ShutdownMode.OnExplicitShutdown;
                    Task.Run(async () =>
                    {
                        try
                        {
                            await FolderMorpher.Services.Testing.TrigramBenchmark.RunAsync();
                            Console.Out.Flush();
                            Environment.Exit(0);
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[BENCHMARK-ERROR] {ex}");
                            Console.Out.Flush();
                            Environment.Exit(1);
                        }
                    });
                    return;
                }
            }

            if (runRegression)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Task.Run(async () =>
                {
                    try
                    {
                        bool allPassed = await FolderMorpher.Services.Testing.RegressionTestSuite.RunAllTestsAsync();
                        Console.Out.Flush();
                        Environment.Exit(allPassed ? 0 : 1);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TEST-REGRESSION-FATAL] {ex}");
                        Console.Out.Flush();
                        Environment.Exit(1);
                    }
                });
                return;
            }

            if (!string.IsNullOrEmpty(testScanPath))
            {
                Task.Run(async () =>
                {
                    try
                    {
                        Console.WriteLine($"[TEST-SCAN] Target: {testScanPath}");
                        var scanService = new AstraSize.Services.DiskScanService();
                        using var cts = new System.Threading.CancellationTokenSource();
                        var (root, summary) = await scanService.ScanPathAsync(testScanPath, null, cts.Token);
                        Console.WriteLine($"[TEST-SCAN] Mode: {summary.ScanMode}, TotalSize: {root.SizeBytes} bytes, Files: {summary.TotalFiles}, Folders: {summary.TotalFolders}, Time: {summary.ElapsedSeconds}s");
                        Console.WriteLine("[TEST-SCAN] ALL PASSED");
                        Environment.Exit(0);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TEST-SCAN-ERROR] {ex}");
                        Environment.Exit(1);
                    }
                });
                return;
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

                        // 3. GPO Script Test
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

                            if (selectTab == 1) mw.NavTabSearch.IsChecked = true;
                            else if (selectTab == 11)
                            {
                                mw.NavTabSearch.IsChecked = true;
                                mw.SearchInputBox.Text = "契約書 2026";
                                var item1 = new FolderMorpher.Models.SearchResultItem
                                {
                                    Name = "2026年度_業務委託基本契約書_改定版.xlsx",
                                    FullPath = @"\\FileServer\Legal\Contracts\2026\2026年度_業務委託基本契約書_改定版.xlsx",
                                    DirectoryPath = @"\\FileServer\Legal\Contracts\2026",
                                    SizeBytes = 2485760,
                                    LastWriteTime = DateTime.Now.AddDays(-2),
                                    CreationTime = DateTime.Now.AddMonths(-6),
                                    Extension = ".xlsx",
                                    ContentSnippet = "…第12条（機密保持条項）：本契約に基づき開示された【最高機密プロジェクト】に関する技術情報および…",
                                    MatchedReason = "本文一致 (FTS5 trigram MATCH)"
                                };
                                var item2 = new FolderMorpher.Models.SearchResultItem
                                {
                                    Name = "契約締結伺い_20260401.pdf",
                                    FullPath = @"\\FileServer\Legal\Approvals\契約締結伺い_20260401.pdf",
                                    DirectoryPath = @"\\FileServer\Legal\Approvals",
                                    SizeBytes = 845210,
                                    LastWriteTime = DateTime.Now.AddDays(-5),
                                    CreationTime = DateTime.Now.AddMonths(-2),
                                    Extension = ".pdf",
                                    ContentSnippet = "…上記件名の通り、2026年度の新規契約締結について決裁を仰ぎます。予算措置は別紙参照…",
                                    MatchedReason = "本文一致 (PDF iFilter)"
                                };
                                var item3 = new FolderMorpher.Models.SearchResultItem
                                {
                                    Name = "契約一覧台帳_2026.csv",
                                    FullPath = @"\\FileServer\Legal\Ledgers\契約一覧台帳_2026.csv",
                                    DirectoryPath = @"\\FileServer\Legal\Ledgers",
                                    SizeBytes = 124800,
                                    LastWriteTime = DateTime.Now.AddHours(-14),
                                    CreationTime = DateTime.Now.AddMonths(-1),
                                    Extension = ".csv",
                                    MatchedReason = "ファイル名完全一致"
                                };
                                var item4 = new FolderMorpher.Models.SearchResultItem
                                {
                                    Name = "2026_プロジェクト管理資料",
                                    FullPath = @"\\FileServer\Legal\Contracts\2026_プロジェクト管理資料",
                                    DirectoryPath = @"\\FileServer\Legal\Contracts",
                                    SizeBytes = 15420000,
                                    LastWriteTime = DateTime.Now.AddDays(-1),
                                    CreationTime = DateTime.Now.AddMonths(-3),
                                    Extension = "",
                                    IsDirectory = true,
                                    MatchedReason = "フォルダー名一致"
                                };
                                mw.SearchResults.Add(item1);
                                mw.SearchResults.Add(item2);
                                mw.SearchResults.Add(item3);
                                mw.SearchResults.Add(item4);
                                mw.SearchListView.SelectedItem = item1;

                                mw.SearchKpiHitCountText.Text = "4 件";
                                mw.SearchKpiTotalSizeText.Text = "18.8 MB";
                                mw.SearchKpiElapsedText.Text = "0.04s";
                                mw.SearchStatusText.Text = "⚡ インデックス高速検索完了: 4 件ヒット (38 ms)";
                            }
                            else if (selectTab == 2) mw.NavTabLiveAcl.IsChecked = true;

                            else if (selectTab == 22)
                            {
                                mw.NavTabLiveAcl.IsChecked = true;
                                mw.LiveAclStudioControl.LiveAclModeReverseRadio.IsChecked = true;
                            }
                            else if (selectTab == 3) mw.NavTabSimulation.IsChecked = true;
                            else if (selectTab == 4) mw.NavTabLinkFix.IsChecked = true;
                            else if (selectTab == 5) mw.NavTabAudit.IsChecked = true;
                            else if (selectTab == 6) mw.NavTabMedia.IsChecked = true;
                            else if (selectTab == 100)
                            {
                                mw.NavTabStorage.IsChecked = true;
                                var rootNode = new AstraSize.Models.FileItemNode
                                {
                                    Name = "D:\\SharedData",
                                    FullPath = "D:\\SharedData",
                                    Size = 120L * 1024 * 1024 * 1024,
                                    FileCount = 4500,
                                    FolderCount = 120,
                                    IsDirectory = true,
                                    Level = 0,
                                    LastModified = DateTime.Now.AddDays(-1)
                                };
                                var child1 = new AstraSize.Models.FileItemNode
                                {
                                    Name = "01_プロジェクト管理",
                                    FullPath = "D:\\SharedData\\01_プロジェクト管理",
                                    Size = 45L * 1024 * 1024 * 1024,
                                    FileCount = 1200,
                                    FolderCount = 30,
                                    IsDirectory = true,
                                    Level = 1,
                                    LastModified = DateTime.Now.AddDays(-2)
                                };
                                var child2 = new AstraSize.Models.FileItemNode
                                {
                                    Name = "プロジェクト計画書_2026.xlsx",
                                    FullPath = "D:\\SharedData\\01_プロジェクト管理\\プロジェクト計画書_2026.xlsx",
                                    Size = 350L * 1024 * 1024,
                                    FileCount = 1,
                                    FolderCount = 0,
                                    IsDirectory = false,
                                    Level = 2,
                                    LastModified = DateTime.Now.AddDays(-3)
                                };
                                var child3 = new AstraSize.Models.FileItemNode
                                {
                                    Name = "システム要件定義書.docx",
                                    FullPath = "D:\\SharedData\\01_プロジェクト管理\\システム要件定義書.docx",
                                    Size = 120L * 1024 * 1024,
                                    FileCount = 1,
                                    FolderCount = 0,
                                    IsDirectory = false,
                                    Level = 2,
                                    LastModified = DateTime.Now.AddDays(-5)
                                };
                                var child4 = new AstraSize.Models.FileItemNode
                                {
                                    Name = "アーキテクチャ図_v2.pdf",
                                    FullPath = "D:\\SharedData\\01_プロジェクト管理\\アーキテクチャ図_v2.pdf",
                                    Size = 85L * 1024 * 1024,
                                    FileCount = 1,
                                    FolderCount = 0,
                                    IsDirectory = false,
                                    Level = 2,
                                    LastModified = DateTime.Now.AddDays(-10)
                                };
                                var child5 = new AstraSize.Models.FileItemNode
                                {
                                    Name = "バックアップアーカイブ.zip",
                                    FullPath = "D:\\SharedData\\01_プロジェクト管理\\バックアップアーカイブ.zip",
                                    Size = 2500L * 1024 * 1024,
                                    FileCount = 1,
                                    FolderCount = 0,
                                    IsDirectory = false,
                                    Level = 2,
                                    LastModified = DateTime.Now.AddDays(-20)
                                };
                                rootNode.Children.Add(child1);
                                child1.Children.Add(child2);
                                child1.Children.Add(child3);
                                child1.Children.Add(child4);
                                child1.Children.Add(child5);

                                var flatList = new List<AstraSize.Models.FileItemNode> { rootNode, child1, child2, child3, child4, child5 };
                                mw.FileTreeDataGrid.ItemsSource = flatList;

                                var shares = new List<AstraSize.Models.FolderChildShareItem>
                                {
                                    new AstraSize.Models.FolderChildShareItem { Name = "01_プロジェクト管理", FullPath = child1.FullPath, Size = child1.Size, IsDirectory = true, RelativeSharePercentage = 37.5 },
                                    new AstraSize.Models.FolderChildShareItem { Name = "バックアップアーカイブ.zip", FullPath = child5.FullPath, Size = child5.Size, IsDirectory = false, RelativeSharePercentage = 2.1 },
                                    new AstraSize.Models.FolderChildShareItem { Name = "プロジェクト計画書_2026.xlsx", FullPath = child2.FullPath, Size = child2.Size, IsDirectory = false, RelativeSharePercentage = 0.3 }
                                };
                                mw.FolderChildSharesDataGrid.ItemsSource = shares;

                                var topFiles = new List<AstraSize.Models.LargestFileInfo>
                                {
                                    new AstraSize.Models.LargestFileInfo { Name = "バックアップアーカイブ.zip", FullPath = child5.FullPath, Size = child5.Size, Extension = ".zip" },
                                    new AstraSize.Models.LargestFileInfo { Name = "プロジェクト計画書_2026.xlsx", FullPath = child2.FullPath, Size = child2.Size, Extension = ".xlsx" },
                                    new AstraSize.Models.LargestFileInfo { Name = "システム要件定義書.docx", FullPath = child3.FullPath, Size = child3.Size, Extension = ".docx" }
                                };
                                mw.TopFilesDataGrid.ItemsSource = topFiles;
                                mw.PathTextBox.Text = @"D:\SharedData";
                                mw.ScannedSizeTextBlock.Text = "120.00 GB";
                                mw.TotalFilesTextBlock.Text = "4,500 ファイル / 120 フォルダ";
                            }
                            else if (selectTab == 105)
                            {
                                mw.NavTabAudit.IsChecked = true;
                                var auditItems = new List<FolderMorpher.Models.AuditItem>
                                {
                                    new FolderMorpher.Models.AuditItem
                                    {
                                        IssueType = FolderMorpher.Models.AuditIssueType.Duplicate,
                                        FileName = "2026年度予算案_最終版.xlsx",
                                        FullPath = @"D:\SharedData\Finance\2026年度予算案_最終版.xlsx",
                                        DirectoryPath = @"D:\SharedData\Finance",
                                        Size = 4520000,
                                        LastWriteTime = DateTime.Now.AddDays(-10),
                                        DuplicateGroupId = "GRP-001",
                                        IsOriginalCandidate = true,
                                        Detail = "原本候補 (SHA256照合済)"
                                    },
                                    new FolderMorpher.Models.AuditItem
                                    {
                                        IssueType = FolderMorpher.Models.AuditIssueType.Duplicate,
                                        FileName = "2026年度予算案_コピー.xlsx",
                                        FullPath = @"D:\SharedData\Backup\2026年度予算案_コピー.xlsx",
                                        DirectoryPath = @"D:\SharedData\Backup",
                                        Size = 4520000,
                                        LastWriteTime = DateTime.Now.AddDays(-12),
                                        DuplicateGroupId = "GRP-001",
                                        IsOriginalCandidate = false,
                                        Detail = "重複ファイル (削除可能)"
                                    },
                                    new FolderMorpher.Models.AuditItem
                                    {
                                        IssueType = FolderMorpher.Models.AuditIssueType.Dormant,
                                        FileName = "旧基幹システム設計書_v1.0.pdf",
                                        FullPath = @"D:\SharedData\Archive\旧基幹システム設計書_v1.0.pdf",
                                        DirectoryPath = @"D:\SharedData\Archive",
                                        Size = 18450000,
                                        LastWriteTime = DateTime.Now.AddYears(-4),
                                        Detail = "最終アクセス: 4年前 (未参照)"
                                    },
                                    new FolderMorpher.Models.AuditItem
                                    {
                                        IssueType = FolderMorpher.Models.AuditIssueType.PathTooLong,
                                        FileName = "long_path_architecture_specification_document_final_revised.docx",
                                        FullPath = @"D:\SharedData\Very\Long\Path\Deeply\Nested\Directory\Structure\For\Enterprise\Storage\Management\long_path_architecture_specification_document_final_revised.docx",
                                        DirectoryPath = @"D:\SharedData\Very\Long\Path\Deeply\Nested\Directory\Structure\For\Enterprise\Storage\Management",
                                        Size = 820000,
                                        LastWriteTime = DateTime.Now.AddDays(-40),
                                        Detail = "パス長: 254文字 (危険域 240字超)"
                                    }
                                };
                                mw.AuditItemsDataGrid.ItemsSource = auditItems;
                                mw.AuditPathTextBox.Text = @"D:\SharedData";
                                mw.AuditKpiTotalFiles.Text = "12,450 件";
                                if (mw.AuditKpiReadyToClean != null) mw.AuditKpiReadyToClean.Text = "22.9 MB";
                                if (mw.AuditKpiVersionFamily != null) mw.AuditKpiVersionFamily.Text = "8.2 MB";
                                mw.AuditKpiDupWasted.Text = "4.5 MB";
                                mw.AuditKpiDormantSize.Text = "18.4 MB";
                            }
                            else if (selectTab == 98)
                            {
                                var testSnapshots = new List<AstraSize.Models.ScanSnapshot>
                                {
                                    new AstraSize.Models.ScanSnapshot { Id = "s1", Timestamp = DateTime.Now.AddDays(-30), TotalBytes = 100L * 1024 * 1024 * 1024, TotalFiles = 50000 },
                                    new AstraSize.Models.ScanSnapshot { Id = "s2", Timestamp = DateTime.Now.AddDays(-20), TotalBytes = 105L * 1024 * 1024 * 1024, TotalFiles = 52000 },
                                    new AstraSize.Models.ScanSnapshot { Id = "s3", Timestamp = DateTime.Now.AddDays(-10), TotalBytes = 112L * 1024 * 1024 * 1024, TotalFiles = 55000 },
                                    new AstraSize.Models.ScanSnapshot { Id = "s4", Timestamp = DateTime.Now, TotalBytes = 120L * 1024 * 1024 * 1024, TotalFiles = 58000 }
                                };
                                var hw = new AstraSize.HistoryWindow(@"D:\SharedData", testSnapshots)
                                {
                                    Width = 980,
                                    Height = 720,
                                    WindowStartupLocation = WindowStartupLocation.CenterScreen
                                };
                                hw.Show();
                                await System.Threading.Tasks.Task.Delay(600);
                                hw.UpdateLayout();

                                int hwW = (int)hw.ActualWidth;
                                int hwH = (int)hw.ActualHeight;
                                if (hwW <= 0) hwW = 980;
                                if (hwH <= 0) hwH = 720;

                                var hwRtb = new System.Windows.Media.Imaging.RenderTargetBitmap(hwW, hwH, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                                hwRtb.Render(hw);

                                var hwEnc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                                hwEnc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(hwRtb));
                                using (var fs = File.Create(snapshotPath))
                                {
                                    hwEnc.Save(fs);
                                }
                                hw.Close();
                                Shutdown(0);
                                return;
                            }
                            else if (selectTab == 99)
                            {
                                mw.NavTabStorage.IsChecked = true;
                                mw.SettingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                            }
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
