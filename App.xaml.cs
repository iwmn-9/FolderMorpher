using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace AstraSize
{
    public partial class App : Application
    {
        private static readonly string LogPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FolderMorpher",
            "debug_startup.log");

        private static void DeleteIpcTestDatabase(string path)
        {
            foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
            {
                try { File.Delete(path + suffix); }
                catch { /* An IPC failure should keep its original diagnostic. */ }
            }
        }

        private static T FindControl<T>(Window window, string name) where T : FrameworkElement =>
            window.FindName(name) as T ?? throw new InvalidOperationException($"UI test control missing: {name}");

        private static bool SearchButtonShowsRun(Button button) => button.Content?.ToString() is "🔍 検索" or "🔍 Search";
        private static bool SearchButtonShowsStop(Button button) => button.Content?.ToString() is "■ 中止" or "■ Stop";

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
                var testId = Guid.NewGuid().ToString("N");
                var testDbPath = Path.Combine(Path.GetTempPath(), $"FolderMorpher_IpcTreeCache_{testId}.db");
                var testSettingsPath = Path.Combine(Path.GetTempPath(), $"FolderMorpher_IpcSettings_{testId}.json");
                Environment.SetEnvironmentVariable("FOLDERMORPHER_TEST_IPC_ID", testId);
                Environment.SetEnvironmentVariable("FOLDERMORPHER_TEST_TREE_CACHE_DB", testDbPath);
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
                        var settings = await host.GetAppSettingsAsync();
                        if (settings.StorageTabPaths == null || settings.Language is not ("ja" or "en"))
                            throw new InvalidOperationException("Settings DTO roundtrip failed.");
                        settings.CloseHostOnWindowClose = true;
                        settings.AdminSidebarCollapsed = false;
                        settings.ScanDurationsSeconds["search-content|C:"] = 12.5;
                        await host.SaveAppSettingsAsync(settings);
                        var savedSettings = await host.GetAppSettingsAsync();
                        if (!savedSettings.CloseHostOnWindowClose || savedSettings.AdminSidebarCollapsed ||
                            !savedSettings.ScanDurationsSeconds.TryGetValue("search-content|C:", out var duration) || duration != 12.5)
                            throw new InvalidOperationException("Window and scan estimate settings did not roundtrip over IPC.");
                        settings.CloseHostOnWindowClose = false;
                        settings.AdminSidebarCollapsed = true;
                        settings.ScanDurationsSeconds.Clear();
                        await host.SaveAppSettingsAsync(settings);
                        await host.SetLanguageAsync(settings.Language);
                        FolderMorpher.UI.PresentationTestRunner.VerifyStorageNodePresentation();
                        FolderMorpher.UI.PresentationTestRunner.VerifyAuditAndSimulationPresentation();

                        var testRoot = Path.Combine(Path.GetTempPath(), $"FolderMorpher_IpcTest_{Guid.NewGuid():N}");
                        Directory.CreateDirectory(testRoot);
                        try
                        {
                            var sourceChildPath = Path.Combine(testRoot, "source-folder");
                            Directory.CreateDirectory(sourceChildPath);
                            var browsedFolders = await host.BrowseChildFoldersAsync(testRoot);
                            if (!browsedFolders.Contains(sourceChildPath))
                                throw new InvalidOperationException("Folder scope hierarchy did not roundtrip over IPC.");
                            var matchFile = Path.Combine(sourceChildPath, "ipc-search-match.txt");
                            await File.WriteAllTextAsync(matchFile, "FolderMorpher IPC roundtrip");
                            using var testCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(45));
                            MainWindow? searchWindow = null;
                            await Dispatcher.InvokeAsync(() =>
                            {
                                searchWindow = new MainWindow();
                                var dialogTimer = new System.Windows.Threading.DispatcherTimer
                                {
                                    Interval = TimeSpan.FromMilliseconds(120)
                                };
                                dialogTimer.Tick += (_, _) =>
                                {
                                    var dialog = Current.Windows.OfType<Window>().FirstOrDefault(window => window.Title == "IPC dialog");
                                    if (dialog != null) { dialogTimer.Stop(); dialog.Close(); }
                                };
                                dialogTimer.Start();
                                var dialogResult = AppDialog.Show("Dialog smoke test", "IPC dialog", MessageBoxButton.YesNo, MessageBoxImage.Question);
                                dialogTimer.Stop();
                                if (dialogResult != MessageBoxResult.No)
                                    throw new InvalidOperationException("App dialog lost the safe negative result.");
                                FolderMorpher.UI.PresentationTestRunner.VerifyRuntimeLocalization(searchWindow);
                                FolderMorpher.UI.PresentationTestRunner.VerifyAuditProvisionalDisplay(searchWindow);
                                FindControl<TextBox>(searchWindow, "SearchDirectTargetTextBox").Text = testRoot;
                                FindControl<TextBox>(searchWindow, "SearchInputBox").Text = "ipc-search-match";
                                FindControl<Button>(searchWindow, "SearchClearButton").RaiseEvent(new RoutedEventArgs(
                                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                                if (FindControl<TextBox>(searchWindow, "SearchInputBox").Text.Length != 0 ||
                                    searchWindow.SearchResults.Count != 0 ||
                                    FindControl<ProgressBar>(searchWindow, "SearchProgressBar").Visibility != Visibility.Collapsed ||
                                    !SearchButtonShowsRun(FindControl<Button>(searchWindow, "SearchExecuteButton")))
                                    throw new InvalidOperationException("Search clear did not restore an idle UI.");
                            });
                            await Task.Delay(400, testCts.Token);
                            await Dispatcher.InvokeAsync(() =>
                            {
                                if (searchWindow!.SearchResults.Count != 0 ||
                                    !SearchButtonShowsRun(FindControl<Button>(searchWindow, "SearchExecuteButton")))
                                    throw new InvalidOperationException("An empty search restarted after Clear.");
                                FindControl<TextBox>(searchWindow, "SearchInputBox").Text = "ipc-search-match";
                                FindControl<Button>(searchWindow, "SearchExecuteButton").RaiseEvent(new RoutedEventArgs(
                                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                                if (!SearchButtonShowsStop(FindControl<Button>(searchWindow, "SearchExecuteButton")))
                                    throw new InvalidOperationException("Search button did not change to Stop while running.");
                                FindControl<Button>(searchWindow, "SearchExecuteButton").RaiseEvent(new RoutedEventArgs(
                                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                                if (!SearchButtonShowsRun(FindControl<Button>(searchWindow, "SearchExecuteButton")) ||
                                    FindControl<TextBlock>(searchWindow, "SearchStatusText").Text is not ("検索を中断しました。" or "Search canceled."))
                                    throw new InvalidOperationException("Search Stop did not respond immediately.");
                                searchWindow.Close();
                            });
                            Console.WriteLine("[TEST-IPC] Search Clear/Stop UI: SUCCESS");
                            var scan = await host.ScanStorageAsync(
                                new FolderMorpher.Contracts.StorageScanRequestDto { TargetPath = testRoot }, null, testCts.Token);
                            var availability = await host.GetStorageAvailabilityAsync(testRoot);
                            if (availability == null || availability.AvailableBytes < 0 || availability.TotalBytes <= 0)
                                throw new InvalidOperationException("Available storage space did not roundtrip through IPC.");
                            if (await host.GetStorageAvailabilityAsync(sourceChildPath) == null)
                                throw new InvalidOperationException("Nested folder storage availability did not roundtrip through IPC.");
                            if (await host.GetStorageAvailabilityAsync(Path.Combine(testRoot, "missing-folder")) != null)
                                throw new InvalidOperationException("Missing storage path reported available space.");
                            if (scan.RootNode == null || scan.TotalFiles < 1 || scan.RootNode.Children.Count < 1)
                                throw new InvalidOperationException($"Storage DTO roundtrip failed: {scan.ErrorMessage}");
                            if (Math.Abs(scan.RootNode.Children[0].PercentageOfRoot - 100.0) > 0.01)
                                throw new InvalidOperationException("Storage percentage was lost at the IPC boundary.");
                            if (!scan.RootNode.Children[0].HasUnloadedChildren || scan.RootNode.Children[0].Children.Count != 0)
                                throw new InvalidOperationException("Storage scan did not return a lazy child folder.");
                            var loadedChildren = await host.GetStorageChildrenAsync(testRoot, sourceChildPath);
                            if (!loadedChildren.Any(child => child.FullPath == matchFile))
                                throw new InvalidOperationException("Storage lazy expansion DTO roundtrip failed.");
                            var topFiles = await host.GetStorageTopFilesAsync(testRoot, scan.RootNode, testCts.Token);
                            if (!topFiles.Any(file => file.FullPath == matchFile))
                                throw new InvalidOperationException("Storage top files DTO roundtrip failed.");
                            var forecast = await host.AnalyzeStorageHistoryAsync(new()
                            {
                                new() { Id = "history-1", Timestamp = DateTime.UtcNow.AddDays(-2), TotalBytes = 1024 },
                                new() { Id = "history-2", Timestamp = DateTime.UtcNow.AddDays(-1), TotalBytes = 2048 },
                                new() { Id = "history-3", Timestamp = DateTime.UtcNow, TotalBytes = 3072 }
                            }, 4096, testCts.Token);
                            if (forecast.TotalScans != 3 || forecast.LinearRegression is not { Slope: > 0 })
                                throw new InvalidOperationException("Storage forecast DTO roundtrip failed.");
                            Console.WriteLine($"[TEST-IPC] StorageScan: {scan.TotalFiles} file(s)");
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
                            var coldSearchRoot = Path.Combine(testRoot, "cold-search-cache");
                            var coldSearchChild = Path.Combine(coldSearchRoot, "nested");
                            Directory.CreateDirectory(coldSearchChild);
                            var coldSearchFile = Path.Combine(coldSearchChild, "plain.txt");
                            await File.WriteAllTextAsync(coldSearchFile, "cold-cache-content-token", testCts.Token);
                            var coldContentQuery = new FolderMorpher.Contracts.SearchQueryDto
                            {
                                RawQuery = "content:cold-cache-content-token",
                                ContentKeyword = "cold-cache-content-token"
                            };
                            var coldHits = await host.SearchAsync(coldSearchRoot, coldContentQuery, null, testCts.Token);
                            var coldTree = await host.LoadCachedTreeAsync(coldSearchRoot);
                            if (!coldHits.Any(item => item.FullPath == coldSearchFile) ||
                                !await host.HasCachedTreeAsync(coldSearchRoot) || coldTree?.FileCount != 1 || coldTree.FolderCount != 1)
                                throw new InvalidOperationException("Cold content search did not populate TreeCache through IPC.");
                            Console.WriteLine("[TEST-IPC] Cold search to TreeCache: SUCCESS");
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
                            if (searchJob.DiscoveredDirectories < 2 ||
                                searchJob.DiscoveredDirectories != searchJob.ProcessedDirectories)
                                throw new InvalidOperationException("Search directory frontier progress did not cross IPC.");
                            var liveBatch = await host.GetSearchJobResultsAsync(searchJobId, 0, 256);
                            if (liveBatch.Results.Count == 0 ||
                                !liveBatch.Results.Any(item => string.Equals(item.FullPath, matchFile, StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidOperationException("Live search hits did not cross IPC before final result delivery.");
                            var liveTail = await host.GetSearchJobResultsAsync(searchJobId, liveBatch.NextSequence, 256);
                            if (liveTail.Results.Count != 0 || liveTail.HadGap)
                                throw new InvalidOperationException("Search result cursor repeated or lost a batch.");
                            Console.WriteLine($"[TEST-IPC] Host Job: {searchJob.SearchResults.Count} search result(s)");
                            await host.ReleaseJobAsync(searchJobId);

                            var cachedJobId = await host.StartJobAsync(new FolderMorpher.Contracts.HostJobRequestDto
                            {
                                Kind = FolderMorpher.Contracts.HostJobKind.CachedSearch,
                                TargetPath = testRoot,
                                SearchQuery = query
                            });
                            FolderMorpher.Contracts.HostJobStatusDto cachedJob;
                            do
                            {
                                cachedJob = await host.GetJobStatusAsync(cachedJobId);
                                if (cachedJob.State == FolderMorpher.Contracts.HostJobState.Failed)
                                    throw new InvalidOperationException($"Cached Host search job failed: {cachedJob.Error}");
                                if (cachedJob.State == FolderMorpher.Contracts.HostJobState.Running)
                                    await Task.Delay(50, testCts.Token);
                            } while (cachedJob.State == FolderMorpher.Contracts.HostJobState.Running);
                            if (cachedJob.SearchResults?.Any(item => string.Equals(item.FullPath, matchFile, StringComparison.OrdinalIgnoreCase)) != true)
                                throw new InvalidOperationException("Cached Host search job lost its results.");
                            var cachedBatch = await host.GetSearchJobResultsAsync(cachedJobId, 0, 256);
                            if (!cachedBatch.Results.Any(item => string.Equals(item.FullPath, matchFile, StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidOperationException("Cached name hits did not cross the batch IPC route.");
                            await host.ReleaseJobAsync(cachedJobId);
                            Console.WriteLine("[TEST-IPC] Cached Search Job: SUCCESS");

                            MainWindow? countWindow = null;
                            await Dispatcher.InvokeAsync(() =>
                            {
                                countWindow = new MainWindow();
                                FindControl<TextBox>(countWindow, "SearchDirectTargetTextBox").Text = testRoot;
                                FindControl<TextBox>(countWindow, "SearchInputBox").Text = "ipc-search-match";
                                FindControl<CheckBox>(countWindow, "SearchContentCheckBox").IsChecked = true;
                                FindControl<Button>(countWindow, "SearchExecuteButton").RaiseEvent(new RoutedEventArgs(
                                    System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                            });
                            while (await Dispatcher.InvokeAsync(() =>
                                SearchButtonShowsStop(FindControl<Button>(countWindow!, "SearchExecuteButton"))))
                                await Task.Delay(50, testCts.Token);
                            await Dispatcher.InvokeAsync(() =>
                            {
                                var countText = FindControl<TextBlock>(countWindow!, "SearchKpiHitCountText").Text;
                                var visibleCount = countWindow!.SearchResults.Count;
                                if (visibleCount < 1 ||
                                    !countWindow.SearchResults.Any(item => string.Equals(item.FullPath, matchFile, StringComparison.OrdinalIgnoreCase)) ||
                                    !countText.StartsWith($"{visibleCount:N0}", StringComparison.Ordinal))
                                    throw new InvalidOperationException($"Cached name hit disappeared from live search count: {countText}.");
                                countWindow.Close();
                            });
                            Console.WriteLine("[TEST-IPC] Cached name + live search UI count: SUCCESS");

                            // Keep an old snapshot, then change the original files behind it.
                            var freshnessRoot = Path.Combine(testRoot, "freshness");
                            Directory.CreateDirectory(freshnessRoot);
                            var deletedHit = Path.Combine(freshnessRoot, "fresh-match-deleted.txt");
                            var oldRename = Path.Combine(freshnessRoot, "fresh-match-before-rename.txt");
                            var renamedHit = Path.Combine(freshnessRoot, "fresh-match-after-rename.txt");
                            var retainedHit = Path.Combine(freshnessRoot, "fresh-match-retained.txt");
                            var addedHit = Path.Combine(freshnessRoot, "fresh-match-added.txt");
                            await File.WriteAllTextAsync(deletedHit, "old", testCts.Token);
                            await File.WriteAllTextAsync(oldRename, "old", testCts.Token);
                            await File.WriteAllTextAsync(retainedHit, "old", testCts.Token);
                            await host.ScanStorageAsync(new FolderMorpher.Contracts.StorageScanRequestDto
                                { TargetPath = freshnessRoot }, null, testCts.Token);
                            File.Delete(deletedHit);
                            File.Move(oldRename, renamedHit);
                            await File.WriteAllTextAsync(retainedHit, new string('x', 300), testCts.Token);
                            await File.WriteAllTextAsync(addedHit, "name-only hit", testCts.Token);

                            MainWindow? freshnessWindow = null;
                            await Dispatcher.InvokeAsync(() =>
                            {
                                freshnessWindow = new MainWindow();
                                FindControl<TextBox>(freshnessWindow, "SearchDirectTargetTextBox").Text = freshnessRoot;
                                FindControl<TextBox>(freshnessWindow, "SearchInputBox").Text = "fresh-match";
                            });
                            try
                            {
                                // Debounced typing retains the fast, explicitly labeled snapshot preview.
                                await Task.Delay(400, testCts.Token);
                                while (await Dispatcher.InvokeAsync(() =>
                                    SearchButtonShowsStop(FindControl<Button>(freshnessWindow!, "SearchExecuteButton"))))
                                    await Task.Delay(50, testCts.Token);
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    if (freshnessWindow!.SearchResults.Count != 3 ||
                                        !freshnessWindow.SearchResults.Any(item => item.FullPath == deletedHit))
                                        throw new InvalidOperationException("Cached search preview was lost or was presented as current data.");
                                    FolderMorpher.UI.PresentationTestRunner.VerifySearchSnapshotStatus(freshnessWindow, 3);
                                    FindControl<Button>(freshnessWindow, "SearchRefreshButton").RaiseEvent(new RoutedEventArgs(
                                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                                });
                                while (await Dispatcher.InvokeAsync(() =>
                                    SearchButtonShowsStop(FindControl<Button>(freshnessWindow!, "SearchExecuteButton"))))
                                    await Task.Delay(50, testCts.Token);
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    var current = freshnessWindow!.SearchResults;
                                    if (current.Count != 3 || current.Any(item => item.FullPath == deletedHit || item.FullPath == oldRename) ||
                                        !current.Any(item => item.FullPath == renamedHit) || !current.Any(item => item.FullPath == addedHit) ||
                                        current.Single(item => item.FullPath == retainedHit).SizeBytes != new FileInfo(retainedHit).Length ||
                                        !FindControl<TextBlock>(freshnessWindow, "SearchKpiHitCountText").Text.StartsWith("3", StringComparison.Ordinal))
                                        throw new InvalidOperationException("Refresh retained stale paths/metadata or missed new live files.");
                                });

                                File.Delete(retainedHit);
                                File.Delete(renamedHit);
                                var contentHit = Path.Combine(freshnessRoot, "content-only.txt");
                                await File.WriteAllTextAsync(contentHit, "fresh-match inside the file", testCts.Token);
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    FindControl<CheckBox>(freshnessWindow!, "SearchContentCheckBox").IsChecked = true;
                                    FindControl<Button>(freshnessWindow!, "SearchExecuteButton").RaiseEvent(new RoutedEventArgs(
                                        System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                                });
                                while (await Dispatcher.InvokeAsync(() =>
                                    SearchButtonShowsStop(FindControl<Button>(freshnessWindow!, "SearchExecuteButton"))))
                                    await Task.Delay(50, testCts.Token);
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    var current = freshnessWindow!.SearchResults;
                                    if (current.Count != 2 || !current.Any(item => item.FullPath == addedHit) ||
                                        !current.Any(item => item.FullPath == contentHit && !string.IsNullOrEmpty(item.ContentSnippet)) ||
                                        !FindControl<TextBlock>(freshnessWindow, "SearchKpiHitCountText").Text.StartsWith("2", StringComparison.Ordinal))
                                        throw new InvalidOperationException("Content search merged stale cache hits or lost verified name/content hits.");
                                });
                            }
                            finally { await Dispatcher.InvokeAsync(() => freshnessWindow?.Close()); }
                            Console.WriteLine("[TEST-IPC] Search freshness: snapshot, delete, rename, create, metadata, live name/content count SUCCESS");

                            var unreadOfficePath = Path.Combine(testRoot, "unread-search-test.docx");
                            await File.WriteAllTextAsync(unreadOfficePath, "not a ZIP document", testCts.Token);
                            var unreadJobId = await host.StartJobAsync(new FolderMorpher.Contracts.HostJobRequestDto
                            {
                                Kind = FolderMorpher.Contracts.HostJobKind.Search,
                                TargetPath = testRoot,
                                SearchQuery = new FolderMorpher.Contracts.SearchQueryDto
                                {
                                    RawQuery = "content:unread-token",
                                    ContentKeyword = "unread-token"
                                }
                            });
                            FolderMorpher.Contracts.HostJobStatusDto unreadJob;
                            do
                            {
                                unreadJob = await host.GetJobStatusAsync(unreadJobId);
                                if (unreadJob.State == FolderMorpher.Contracts.HostJobState.Failed)
                                    throw new InvalidOperationException($"Unread-file search job failed: {unreadJob.Error}");
                                if (unreadJob.State == FolderMorpher.Contracts.HostJobState.Running)
                                    await Task.Delay(50, testCts.Token);
                            } while (unreadJob.State == FolderMorpher.Contracts.HostJobState.Running);
                            if (unreadJob.UnreadFiles < 1)
                                throw new InvalidOperationException("Unread Office file was incorrectly reported as a complete non-match.");
                            if (unreadJob.ContentProcessedCount < 1)
                                throw new InvalidOperationException("Content processing progress did not cross IPC.");
                            await host.ReleaseJobAsync(unreadJobId);
                            Console.WriteLine("[TEST-IPC] Search unread-file coverage: SUCCESS");

                            var auditContent = new byte[128 * 1024];
                            new Random(73).NextBytes(auditContent);
                            var auditOriginalPath = Path.Combine(testRoot, "audit-original.dat");
                            var auditCopyPath = Path.Combine(testRoot, "audit-copy.dat");
                            await File.WriteAllBytesAsync(auditOriginalPath, auditContent);
                            await File.WriteAllBytesAsync(auditCopyPath, auditContent);
                            var auditOldDate = DateTime.Now.AddYears(-4);
                            foreach (var path in new[] { auditOriginalPath, auditCopyPath })
                            {
                                File.SetLastWriteTime(path, auditOldDate);
                                File.SetLastAccessTime(path, auditOldDate);
                            }

                            var auditJobId = await host.StartJobAsync(new FolderMorpher.Contracts.HostJobRequestDto
                            {
                                Kind = FolderMorpher.Contracts.HostJobKind.AuditScan,
                                AuditRequest = new FolderMorpher.Contracts.AuditScanRequestDto
                                {
                                    TargetPath = testRoot,
                                    CheckDuplicates = true,
                                    CheckDormant = true,
                                    CheckVersionFamilies = false,
                                    CheckExtractedArchives = false,
                                    CheckGraveyardTrees = false,
                                    CheckPathLimits = false,
                                    BandwidthLimit = 2 // Auto wire value; AuditBandwidthLimit is linked into both UI and Core.
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
                            var auditBatch = await host.GetAuditJobResultsAsync(auditJobId, 0, 256);
                            var streamedCopy = auditBatch.Results.FirstOrDefault(item => item.FullPath == auditCopyPath);
                            var finalCopy = completedAuditReport.Items.FirstOrDefault(item => item.FullPath == auditCopyPath);
                            if (auditBatch.HadGap || streamedCopy == null || finalCopy == null ||
                                finalCopy.WasteScore != 165 || finalCopy.ScoreBreakdown.Sum(factor => factor.Points) != 165 ||
                                !finalCopy.IssueTypes.Contains(0) || // AuditIssueType.Duplicate
                                !finalCopy.IssueTypes.Contains(1) || // AuditIssueType.Dormant
                                completedAuditReport.Items.Count(item => item.FullPath == auditCopyPath) != 1)
                                throw new InvalidOperationException("Combined audit score or progressive IPC delivery failed.");
                            var sortedAuditIds = await host.SortAuditIdsAsync(completedAuditReport.ReportId,
                                completedAuditReport.Items.Select(item => item.AuditId).ToList(), "Size", true, testCts.Token);
                            if (sortedAuditIds.Count != completedAuditReport.Items.Count)
                                throw new InvalidOperationException("Audit sort IDs were lost across IPC.");
                            var ignoreCount = await host.GetAuditIgnoreCountAsync();
                            if ((await host.GetAuditIgnoresAsync()).Count != ignoreCount)
                                throw new InvalidOperationException("Audit ignore list/count DTO roundtrip failed.");
                            Console.WriteLine($"[TEST-IPC] Audit Job: {completedAuditReport.Summary.TotalFilesScanned} file(s)");
                            await host.ReleaseJobAsync(auditJobId);
                            var coldAuditRoot = Path.Combine(testRoot, "cold-audit-cache");
                            Directory.CreateDirectory(coldAuditRoot);
                            await File.WriteAllTextAsync(Path.Combine(coldAuditRoot, "note.txt"), "audit cache fixture", testCts.Token);
                            await host.RunAuditScanAsync(new FolderMorpher.Contracts.AuditScanRequestDto
                            {
                                TargetPath = coldAuditRoot,
                                CheckDuplicates = false,
                                CheckDormant = false,
                                CheckPathLimits = false
                            }, null, testCts.Token);
                            var coldAuditTree = await host.LoadCachedTreeAsync(coldAuditRoot);
                            if (!await host.HasCachedTreeAsync(coldAuditRoot) || coldAuditTree?.FileCount != 1)
                                throw new InvalidOperationException("Cold audit did not populate TreeCache through IPC.");
                            Console.WriteLine("[TEST-IPC] Cold audit to TreeCache: SUCCESS");

                            MainWindow? auditCancelWindow = null;
                            await Dispatcher.InvokeAsync(() =>
                            {
                                auditCancelWindow = new MainWindow();
                                FindControl<TextBox>(auditCancelWindow, "AuditPathTextBox").Text = testRoot;
                                var start = FindControl<Button>(auditCancelWindow, "AuditStartButton");
                                start.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                                if (!start.Content.ToString()!.Contains("中止") && !start.Content.ToString()!.Contains("Stop"))
                                    throw new InvalidOperationException("Audit start did not become a stop action.");
                                start.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                                if (start.IsEnabled)
                                    throw new InvalidOperationException("Audit stop allowed a concurrent restart before cancellation settled.");
                            });
                            while (await Dispatcher.InvokeAsync(() =>
                                !FindControl<Button>(auditCancelWindow!, "AuditStartButton").IsEnabled))
                                await Task.Delay(50, testCts.Token);
                            await Dispatcher.InvokeAsync(() =>
                            {
                                var start = FindControl<Button>(auditCancelWindow!, "AuditStartButton");
                                if (start.Content.ToString()!.Contains("中止") || start.Content.ToString()!.Contains("Stop"))
                                    throw new InvalidOperationException("Audit stop did not restore its start action.");
                                if (FindControl<Button>(auditCancelWindow!, "AuditExportExcelButton").IsEnabled ||
                                    FindControl<Button>(auditCancelWindow!, "AuditDeleteSelectedButton").IsEnabled)
                                    throw new InvalidOperationException("Canceled audit exposed an incomplete report for export or deletion.");
                                auditCancelWindow!.Close();
                            });
                            Console.WriteLine("[TEST-IPC] Audit UI stop/cancel: SUCCESS");

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
                            project.RootFolders[0].MappedSourcePaths.Add(sourceChildPath);
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
                            var ocrImage = Path.Combine(testRoot, "ocr-invoice.png");
                            using (var image = new System.Drawing.Bitmap(1000, 220))
                            using (var graphics = System.Drawing.Graphics.FromImage(image))
                            using (var font = new System.Drawing.Font("Arial", 48))
                            {
                                graphics.Clear(System.Drawing.Color.White);
                                graphics.DrawString("INVOICE 2026 ALPHA", font, System.Drawing.Brushes.Black, 30, 65);
                                image.Save(ocrImage, System.Drawing.Imaging.ImageFormat.Png);
                            }
                            using var ocrCts = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(2));
                            var ocrHits = await host.SearchAsync(testRoot, new FolderMorpher.Contracts.SearchQueryDto
                            {
                                ContentKeyword = "INVOICE", IncludeOcr = true, Extensions = new() { ".png" }
                            }, null, ocrCts.Token);
                            if (ocrHits.Count != 1 || !ocrHits[0].IsOcrEstimated)
                                throw new InvalidOperationException("Embedded OCR did not roundtrip through the published Host.");
                            var filteredOcr = await host.SearchAsync(testRoot, new FolderMorpher.Contracts.SearchQueryDto
                            {
                                ContentKeyword = "INVOICE", IncludeOcr = true, Extensions = new() { ".txt" }
                            }, null, ocrCts.Token);
                            if (filteredOcr.Count != 0) throw new InvalidOperationException("OCR bypassed extension filters.");
                            Console.WriteLine("[TEST-IPC] Embedded OCR inference and filters: SUCCESS");
                            var baselineHistory = await host.GetStorageHistoryAsync(testRoot);
                            var blockedPath = Path.Combine(testRoot, "unavailable-branch");
                            Directory.CreateDirectory(blockedPath);
                            var blockedDirectory = new DirectoryInfo(blockedPath);
                            var originalAcl = System.IO.FileSystemAclExtensions.GetAccessControl(blockedDirectory);
                            try
                            {
                                var blockedAcl = System.IO.FileSystemAclExtensions.GetAccessControl(blockedDirectory);
                                blockedAcl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                                    System.Security.Principal.WindowsIdentity.GetCurrent().User!, System.Security.AccessControl.FileSystemRights.ListDirectory,
                                    System.Security.AccessControl.AccessControlType.Deny));
                                System.IO.FileSystemAclExtensions.SetAccessControl(blockedDirectory, blockedAcl);
                                var partial = await host.ScanStorageAsync(new FolderMorpher.Contracts.StorageScanRequestDto { TargetPath = testRoot }, null, ocrCts.Token);
                                if (partial.IsCompleteCoverage || !partial.UnavailableFolders.Contains(blockedPath))
                                    throw new InvalidOperationException("Unavailable storage branch was reported as complete.");
                                var afterPartial = await host.GetStorageHistoryAsync(testRoot);
                                if (afterPartial.Count != baselineHistory.Count)
                                    throw new InvalidOperationException("Partial storage scan overwrote complete history.");
                                var coverageCsv = Path.Combine(testRoot, "partial-export.csv");
                                await host.ExportStorageScanAsync(coverageCsv, testRoot, new() { partial.RootNode! }, ocrCts.Token);
                                if (!File.ReadAllText(coverageCsv).Contains(blockedPath))
                                    throw new InvalidOperationException("Partial storage export lost unavailable scope.");
                            }
                            finally
                            {
                                var restoreAcl = new System.Security.AccessControl.DirectorySecurity();
                                restoreAcl.SetSecurityDescriptorSddlForm(originalAcl.GetSecurityDescriptorSddlForm(System.Security.AccessControl.AccessControlSections.Access), System.Security.AccessControl.AccessControlSections.Access);
                                System.IO.FileSystemAclExtensions.SetAccessControl(blockedDirectory, restoreAcl);
                            }
                            Console.WriteLine("[TEST-IPC] Partial storage coverage/history/export: SUCCESS");


                        }
                        finally
                        {
                            Directory.Delete(testRoot, recursive: true);
                        }
                        if (status.ProcessId == FolderMorpher.HostClient.FolderMorpherHostClient.Instance.LaunchedHostProcessId)
                        {
                            if (!await FolderMorpher.HostClient.FolderMorpherHostClient.Instance.RequestShutdownIfRunningAsync(cancelActiveJobs: false))
                                throw new InvalidOperationException("Host refused a graceful shutdown with no active jobs.");
                            using var hostProcess = System.Diagnostics.Process.GetProcessById(status.ProcessId);
                            using var shutdownCts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                            await hostProcess.WaitForExitAsync(shutdownCts.Token);
                            Console.WriteLine("[TEST-IPC] Graceful Host shutdown: SUCCESS");
                        }
                        Console.WriteLine("[TEST-IPC] ALL IPC TESTS PASSED");
                        Console.Out.Flush();
                        FolderMorpher.HostClient.FolderMorpherHostClient.Instance.Dispose();
                        DeleteIpcTestDatabase(testDbPath);
                        var partialDirectory = Path.Combine(Path.GetTempPath(), "FolderMorpher_IpcPartial_" + testId);
                        if (Directory.Exists(partialDirectory)) Directory.Delete(partialDirectory, true);
                        if (File.Exists(testSettingsPath)) File.Delete(testSettingsPath);
                        Environment.Exit(pong ? 0 : 1);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[TEST-IPC-FATAL] {ex}");
                        Console.Out.Flush();
                        FolderMorpher.HostClient.FolderMorpherHostClient.Instance.StopLaunchedHostForTests();
                        DeleteIpcTestDatabase(testDbPath);
                        if (File.Exists(testSettingsPath)) File.Delete(testSettingsPath);
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
                        bool allPassed = await FolderMorpher.Host.TestCommandRunner.RunRegressionAsync();
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
                        await FolderMorpher.Host.TestCommandRunner.RunScanAsync(testScanPath);
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
                        await FolderMorpher.Host.TestCommandRunner.RunSuiteAsync(testSuiteDir);
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

            SnapshotRunner.Configure(snapshotPath, selectTab, forceLang, LogPath, code => Shutdown(code));
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
