using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ZenConnect.Server;

#if WINDOWS
using System.Windows.Forms;
using ZenConnect.Client;
#endif

namespace ZenConnect;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        bool serverMode = args.Any(arg =>
            string.Equals(arg, "--server", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--server-only", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "-s", StringComparison.OrdinalIgnoreCase));

#if WINDOWS
        if (serverMode || !OperatingSystem.IsWindows())
        {
            ZenWebServer.RunAsync(args).GetAwaiter().GetResult();
            return;
        }

        using var cts = new CancellationTokenSource();
        Microsoft.AspNetCore.Builder.WebApplication? serverApp = null;
        if (ShouldStartLocalServer())
        {
            try
            {
                // Do not await before Application.Run. A console-style async Main has no
                // synchronization context and can resume on an MTA worker, which breaks
                // WinForms clipboard/OLE operations despite the STAThread attribute.
                serverApp = ZenWebServer.StartBackgroundAsync("http://localhost:5080", cts.Token)
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ZenConnect] Background server note: {ex.Message}");
            }
        }

        try
        {
            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm());
        }
        finally
        {
            if (serverApp != null)
            {
                cts.Cancel();
                try
                {
                    serverApp.StopAsync().GetAwaiter().GetResult();
                    serverApp.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                }
            }
        }
#else
        ZenWebServer.RunAsync(args).GetAwaiter().GetResult();
#endif
    }

#if WINDOWS
    private static bool ShouldStartLocalServer()
    {
        string? configured = Environment.GetEnvironmentVariable("ZENCONNECT_SERVER_URL");
        return Uri.TryCreate(configured, UriKind.Absolute, out Uri? uri) && uri.IsLoopback;
    }
#endif
}
