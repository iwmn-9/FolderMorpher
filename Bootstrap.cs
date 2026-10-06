using System;
using System.Linq;

namespace AstraSize;

/// <summary>One published executable, with separate GUI and host processes.</summary>
public static class Bootstrap
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(arg => string.Equals(arg, "--host", StringComparison.OrdinalIgnoreCase)))
        {
            FolderMorpher.Host.Program.RunAsync(args).GetAwaiter().GetResult();
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
