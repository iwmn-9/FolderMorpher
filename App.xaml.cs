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
                ClientModeState.IsClientMode = IsClientMode;
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
            }

            if (e.Args.Contains("--test-ipc"))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                Task.Run(async () =>
                {
                    try
                    {
                        Console.WriteLine("[TEST-IPC] Connecting to FolderMorpher.Host via Named Pipe...");
                        var host = await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.GetServiceAsync();
                        bool pong = await host.PingAsync();
                        Console.WriteLine($"[TEST-IPC] Ping: {(pong ? "SUCCESS (Pong)" : "FAILED")}");
                        var status = await host.GetStatusAsync();
                        Console.WriteLine($"[TEST-IPC] Host Status: IsRunning={status.IsRunning}, PID={status.ProcessId}, User={status.UserName}");
                        if (!pong || !status.IsRunning || status.ProcessId == Environment.ProcessId)
                            throw new InvalidOperationException("Host must respond from a separate process.");

                        var testRoot = Path.Combine(Path.GetTempPath(), $"FolderMorpher_IpcTest_{Guid.NewGuid():N}");
                        Directory.CreateDirectory(testRoot);
                        try
                        {
                            var matchFile = Path.Combine(testRoot, "ipc-search-match.txt");
                            await File.WriteAllTextAsync(matchFile, "FolderMorpher IPC roundtrip");
                            using var testCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                            var scan = await host.ScanStorageAsync(
                                new FolderMorpher.Contracts.StorageScanRequestDto { TargetPath = testRoot }, null, testCts.Token);
                            if (scan.RootNode == null || scan.TotalFiles < 1 || scan.RootNode.Children.Count < 1)
                                throw new InvalidOperationException($"Storage DTO roundtrip failed: {scan.ErrorMessage}");
                            if (Math.Abs(scan.RootNode.Children[0].PercentageOfRoot - 100.0) > 0.01)
                                throw new InvalidOperationException("Storage percentage was lost at the IPC boundary.");
                            Console.WriteLine($"[TEST-IPC] StorageScan: {scan.TotalFiles} file(s)");
                            var sourceChildPath = Path.Combine(testRoot, "source-folder");
                            Directory.CreateDirectory(sourceChildPath);
                            var sourceFolder = await host.LoadMigrationSourceFolderAsync(testRoot, testCts.Token);
                            if (!sourceFolder.Children.Any(child => child.FullPath == sourceChildPath))
                                throw new InvalidOperationException("Migration source directory DTO roundtrip failed.");
                            var aclTree = await host.LoadAclFolderTreeAsync(testRoot, 2, testCts.Token);
                            if (!aclTree.Children.Any(child => child.FullPath == sourceChildPath))
                                throw new InvalidOperationException("ACL folder tree DTO roundtrip failed.");
                            var createPlan = await host.PrepareFolderCreationAsync(testRoot, "ipc-created-folder", testCts.Token);
                            var createdPath = await host.CommitFolderCreationAsync(createPlan.PlanId, testCts.Token);
                            if (!Directory.Exists(createdPath))
                                throw new InvalidOperationException("Host folder create plan failed verification.");
                            var migrationNode = await host.CreateMigrationNodeFromStorageAsync(
                                sourceFolder.Children.Single(child => child.FullPath == sourceChildPath), 0, 0, testCts.Token);
                            if (migrationNode.Name != "source-folder" || migrationNode.MappedSourcePaths.Single() != sourceChildPath)
                                throw new InvalidOperationException("Migration source ACL node DTO roundtrip failed.");
                            var storageCsv = Path.Combine(testRoot, "ipc-storage.csv");
                            await host.ExportStorageScanAsync(storageCsv, testRoot,
                                new() { scan.RootNode, scan.RootNode.Children[0] }, testCts.Token);
                            if (!File.Exists(storageCsv) || !(await File.ReadAllTextAsync(storageCsv)).Contains("100.0%"))
                                throw new InvalidOperationException("Storage CSV export lost percentage facts.");

                            var query = new FolderMorpher.Contracts.SearchQueryDto { RawQuery = "ipc-search-match", Keywords = new() { "ipc-search-match" } };
                            var cachedResults = await host.SearchInMemoryAsync(testRoot, query, null, testCts.Token);
                            if (!cachedResults.Any(item => string.Equals(item.FullPath, matchFile, StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidOperationException("Fresh Host scan was not searchable from memory.");
                            Console.WriteLine($"[TEST-IPC] In-Memory Search: {cachedResults.Count} result(s)");
                            var results = await host.SearchAsync(testRoot, query, null, testCts.Token);
                            if (!results.Any(item => string.Equals(item.FullPath, matchFile, StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidOperationException("Search DTO roundtrip did not return the fixture file.");
                            Console.WriteLine($"[TEST-IPC] Search: {results.Count} result(s)");
                            var searchCsv = Path.Combine(testRoot, "ipc-search.csv");
                            var searchExcel = Path.Combine(testRoot, "ipc-search.xlsx");
                            await host.ExportSearchResultsAsync(searchCsv, results, testCts.Token);
                            await host.ExportSearchResultsAsync(searchExcel, results, testCts.Token);
                            if (!File.Exists(searchCsv) || !File.Exists(searchExcel) ||
                                !(await File.ReadAllTextAsync(searchCsv)).Contains("ipc-search-match"))
                                throw new InvalidOperationException("Search CSV/Excel export failed at IPC boundary.");

                            var searchJobId = await host.StartJobAsync(new FolderMorpher.Contracts.HostJobRequestDto
                            {
                                Kind = FolderMorpher.Contracts.HostJobKind.Search,
                                TargetPath = testRoot,
                                SearchQuery = query
                            });
                            FolderMorpher.Contracts.HostJobStatusDto searchJob;
                            do
                            {
                                searchJob = await host.GetJobStatusAsync(searchJobId);
                                if (searchJob.State == FolderMorpher.Contracts.HostJobState.Failed)
                                    throw new InvalidOperationException($"Host search job failed: {searchJob.Error}");
                                if (searchJob.State == FolderMorpher.Contracts.HostJobState.Canceled)
                                    throw new InvalidOperationException("Host search job canceled unexpectedly.");
                                if (searchJob.State == FolderMorpher.Contracts.HostJobState.Running)
                                    await Task.Delay(50, testCts.Token);
                            } while (searchJob.State == FolderMorpher.Contracts.HostJobState.Running);
                            if (searchJob.SearchResults?.Any(item => string.Equals(item.FullPath, matchFile, StringComparison.OrdinalIgnoreCase)) != true)
                                throw new InvalidOperationException("Host job result did not survive the RPC roundtrip.");
                            Console.WriteLine($"[TEST-IPC] Host Job: {searchJob.SearchResults.Count} search result(s)");
                            await host.ReleaseJobAsync(searchJobId);

                            var auditJobId = await host.StartJobAsync(new FolderMorpher.Contracts.HostJobRequestDto
                            {
                                Kind = FolderMorpher.Contracts.HostJobKind.AuditScan,
                                AuditRequest = new FolderMorpher.Contracts.AuditScanRequestDto
                                {
                                    TargetPath = testRoot,
                                    CheckDuplicates = false,
                                    CheckDormant = false,
                                    CheckVersionFamilies = false,
                                    CheckExtractedArchives = false,
                                    CheckGraveyardTrees = false,
                                    CheckPathLimits = false
                                }
                            });
                            FolderMorpher.Contracts.HostJobStatusDto auditJob;
                            do
                            {
                                auditJob = await host.GetJobStatusAsync(auditJobId);
                                if (auditJob.State == FolderMorpher.Contracts.HostJobState.Failed)
                                    throw new InvalidOperationException($"Host audit job failed: {auditJob.Error}");
                                if (auditJob.State == FolderMorpher.Contracts.HostJobState.Canceled)
                                    throw new InvalidOperationException("Host audit job canceled unexpectedly.");
                                if (auditJob.State == FolderMorpher.Contracts.HostJobState.Running)
                                    await Task.Delay(50, testCts.Token);
                            } while (auditJob.State == FolderMorpher.Contracts.HostJobState.Running);
                            var completedAuditReport = auditJob.AuditReport ?? throw new InvalidOperationException("Host audit job did not return its report.");
                            if (completedAuditReport.Summary.TotalFilesScanned < 1)
                                throw new InvalidOperationException("Host audit job did not return its report.");
                            Console.WriteLine($"[TEST-IPC] Audit Job: {completedAuditReport.Summary.TotalFilesScanned} file(s)");
                            await host.ReleaseJobAsync(auditJobId);

                            var csvPath = Path.Combine(testRoot, "ipc-audit.csv");
                            await host.ExportAuditCsvAsync(csvPath, completedAuditReport.Items, testCts.Token);
                            var xlsxPath = Path.Combine(testRoot, "ipc-report.xlsx");
                            await host.ExportExcelReportAsync(new FolderMorpher.Contracts.ReportExportDto
                            {
                                OutputPath = xlsxPath,
                                ScannedRoot = testRoot,
                                AuditSummary = completedAuditReport.Summary,
                                AuditItems = completedAuditReport.Items
                            }, testCts.Token);
                            var gpoPath = Path.Combine(testRoot, "ipc-gpo.ps1");
                            await host.GenerateGpoLogonScriptAsync(gpoPath, @"\\Old\Share", @"\\New\Share", testCts.Token);
                            if (!File.Exists(csvPath) || !File.Exists(xlsxPath) || !File.Exists(gpoPath))
                                throw new InvalidOperationException("Host export did not create every requested artifact.");
                            Console.WriteLine("[TEST-IPC] CSV/Excel/GPO export: SUCCESS");

                            var projectPath = Path.Combine(testRoot, "ipc-project.fmorph");
                            var project = new FolderMorpher.Contracts.SimulationProjectDto
                            {
                                ProjectName = "IPC project",
                                SourceRootPath = testRoot,
                                TargetRootPath = Path.Combine(testRoot, "destination"),
                                RootFolders = new() { new FolderMorpher.Contracts.MigrationNodeDto
                                {
                                    Id = Guid.NewGuid().ToString("N"), Name = "planned-folder", InheritAcl = true
                                } }
                            };
                            await host.SaveSimulationProjectAsync(project, projectPath, testCts.Token);
                            var loadedProject = await host.LoadSimulationProjectAsync(projectPath, testCts.Token);
                            if (loadedProject?.RootFolders.SingleOrDefault()?.Name != "planned-folder")
                                throw new InvalidOperationException("Migration project DTO roundtrip failed.");
                            var deployPlan = await host.BuildSkeletonDeployPlanAsync(null, project.RootFolders,
                                project.TargetRootPath, testCts.Token);
                            if (deployPlan.FolderActions.SingleOrDefault()?.RelativePath != "planned-folder" ||
                                deployPlan.DiffReviews.Count != 1)
                                throw new InvalidOperationException("Migration deploy plan DTO roundtrip failed.");
                            var matrixPath = Path.Combine(testRoot, "ipc-matrix.csv");
                            await host.ExportSimulationMatrixAsync(matrixPath, project.RootFolders, testCts.Token);
                            if (!File.Exists(matrixPath)) throw new InvalidOperationException("Migration matrix export failed.");
                            var diffPath = Path.Combine(testRoot, "ipc-migration-diff.csv");
                            await host.ExportMigrationDiffAsync(diffPath, new()
                            {
                                new FolderMorpher.Contracts.MigrationDiffDto
                                {
                                    DiffType = "追加", TargetPath = "planned-folder", TargetDetail = "IPC fixture"
                                }
                            }, testCts.Token);
                            if (!File.Exists(diffPath)) throw new InvalidOperationException("Migration diff export failed.");
                            project.RootFolders[0].MappedSourcePaths.Add(testRoot);
                            var packageOptions = new FolderMorpher.Contracts.MigrationPackageOptionsDto
                            {
                                OutputDirectory = testRoot,
                                TargetRoot = Path.Combine(testRoot, "destination"),
                                IncludeRunbookExcel = false
                            };
                            var waves = await host.PlanMigrationWavesAsync(project.RootFolders, packageOptions, testCts.Token);
                            if (waves.Count == 0) throw new InvalidOperationException("Host migration wave planning failed.");
                            var packageJobId = await host.StartJobAsync(new FolderMorpher.Contracts.HostJobRequestDto
                            {
                                Kind = FolderMorpher.Contracts.HostJobKind.MigrationPackage,
                                MigrationNodes = project.RootFolders,
                                MigrationOptions = packageOptions
                            });
                            FolderMorpher.Contracts.HostJobStatusDto packageJob;
                            do
                            {
                                packageJob = await host.GetJobStatusAsync(packageJobId);
                                if (packageJob.State == FolderMorpher.Contracts.HostJobState.Failed)
                                    throw new InvalidOperationException($"Migration package Host job failed: {packageJob.Error}");
                                if (packageJob.State == FolderMorpher.Contracts.HostJobState.Running)
                                    await Task.Delay(50, testCts.Token);
                            } while (packageJob.State == FolderMorpher.Contracts.HostJobState.Running);
                            if (packageJob.State != FolderMorpher.Contracts.HostJobState.Completed ||
                                string.IsNullOrWhiteSpace(packageJob.PackageDirectory) || !Directory.Exists(packageJob.PackageDirectory))
                                throw new InvalidOperationException("Migration package Host job returned no package.");
                            await host.ReleaseJobAsync(packageJobId);
                            Console.WriteLine("[TEST-IPC] Migration project and matrix: SUCCESS");

                            var acl = await host.GetFolderAclAsync(testRoot, testCts.Token);
                            if (acl == null || acl.Count == 0)
                                throw new InvalidOperationException("ACL read DTO roundtrip returned no entries.");
                            Console.WriteLine($"[TEST-IPC] ACL Read: {acl.Count} entry/entries");
                            var aclState = await host.GetAclFolderStateAsync(testRoot, testCts.Token);
                            var aclPreview = await host.PrepareAclChangeAsync(new FolderMorpher.Contracts.AclChangeRequestDto
                            {
                                FolderPath = testRoot,
                                ExpectedOriginalSddl = aclState.Sddl,
                                InheritanceBefore = aclState.InheritsAcl,
                                InheritanceAfter = aclState.InheritsAcl,
                                OriginalEntries = aclState.Entries,
                                CurrentEntries = aclState.Entries
                            }, testCts.Token);
                            if (aclPreview.HasConflict || aclPreview.AddedCount != 0 || aclPreview.RemovedCount != 0 || aclPreview.ModifiedCount != 0)
                                throw new InvalidOperationException("ACL no-op plan reported a change or conflict.");
                            var aclCommit = await host.CommitAclChangeAsync(aclPreview.PlanId, false, testCts.Token);
                            if (aclCommit.WasConflict || !aclCommit.VerificationSucceeded)
                                throw new InvalidOperationException("ACL no-op plan failed verification.");
                            var aclMatrixPath = Path.Combine(testRoot, "ipc-acl-matrix.csv");
                            await host.ExportAclMatrixAsync(aclMatrixPath, testRoot, testCts.Token);
                            if (!File.Exists(aclMatrixPath)) throw new InvalidOperationException("ACL matrix export failed.");
                            Console.WriteLine("[TEST-IPC] ACL Check/Commit/Verify and matrix: SUCCESS");

                            var membership = await host.ResolveEffectiveMembershipsAsync(Environment.UserName, testCts.Token);
                            var effectiveJobId = await host.StartJobAsync(new FolderMorpher.Contracts.HostJobRequestDto
                            {
                                Kind = FolderMorpher.Contracts.HostJobKind.EffectiveAccessAudit,
                                TargetPath = testRoot,
                                TargetAccount = Environment.UserName,
                                MaxDepth = 1,
                                MembershipResolution = membership
                            });
                            FolderMorpher.Contracts.HostJobStatusDto effectiveJob;
                            do
                            {
                                effectiveJob = await host.GetJobStatusAsync(effectiveJobId);
                                if (effectiveJob.State == FolderMorpher.Contracts.HostJobState.Failed)
                                    throw new InvalidOperationException($"Effective Access Host job failed: {effectiveJob.Error}");
                                if (effectiveJob.State == FolderMorpher.Contracts.HostJobState.Running)
                                    await Task.Delay(50, testCts.Token);
                            } while (effectiveJob.State == FolderMorpher.Contracts.HostJobState.Running);
                            if (effectiveJob.EffectiveAccessReport?.TotalFoldersScanned < 1)
                                throw new InvalidOperationException("Effective Access DTO roundtrip failed.");
                            await host.ReleaseJobAsync(effectiveJobId);
                            Console.WriteLine("[TEST-IPC] Effective Access Host Job: SUCCESS");
                        }
                        finally
                        {
                            Directory.Delete(testRoot, recursive: true);
                        }
                        Console.WriteLine("[TEST-IPC] ALL IPC TESTS PASSED");
                        Console.Out.Flush();
                        FolderMorpher.HostClient.FolderMorpherHostClient.Instance.StopLaunchedHostForTests();
                        Environment.Exit(pong ? 0 : 1);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TEST-IPC-FATAL] {ex}");
                        Console.Out.Flush();
                        FolderMorpher.HostClient.FolderMorpherHostClient.Instance.StopLaunchedHostForTests();
                        Environment.Exit(1);
                    }
                });
                return;
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

            SnapshotRunner.Configure(snapshotPath, selectTab, collapseSidebar, forceLang, LogPath, code => Shutdown(code));
            var mainWindow = new MainWindow();
            MainWindow = mainWindow;
            mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            File.AppendAllText(LogPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] OnExit fired, ExitCode={e.ApplicationExitCode}\n");
            base.OnExit(e);
        }
    }
}
