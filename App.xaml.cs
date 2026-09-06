using System;
using System.IO;
using System.Windows;

namespace AstraSize
{
    public partial class App : Application
    {
        public App()
        {
            DispatcherUnhandledException += (s, args) =>
            {
                try
                {
                    var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var logDir = Path.Combine(appData, "AstraSize");
                    Directory.CreateDirectory(logDir);
                    File.AppendAllText(Path.Combine(logDir, "crash.log"), $"[{DateTime.Now}] {args.Exception}\n");
                    MessageBox.Show($"アプリケーションエラー:\n{args.Exception.Message}\n\n{args.Exception.InnerException?.Message}", "AstraSize", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                args.Handled = true;
            };
        }
    }
}
