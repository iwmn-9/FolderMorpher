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
            }

            if (!string.IsNullOrEmpty(snapshotPath))
            {
                EventManager.RegisterClassHandler(typeof(MainWindow), FrameworkElement.LoadedEvent, new RoutedEventHandler(async (sender, args) =>
                {
                    if (sender is MainWindow mw)
                    {
                        try
                        {
                            if (selectTab == 1) mw.NavTabLiveAcl.IsChecked = true;
                            else if (selectTab == 2) mw.NavTabSimulation.IsChecked = true;
                            else if (selectTab == 3) mw.NavTabLinkFix.IsChecked = true;
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
