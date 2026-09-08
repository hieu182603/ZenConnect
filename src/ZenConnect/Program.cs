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
    public static async Task Main(string[] args)
    {
        bool serverMode = args.Any(arg =>
            string.Equals(arg, "--server", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "--server-only", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(arg, "-s", StringComparison.OrdinalIgnoreCase));

#if WINDOWS
        if (serverMode || !OperatingSystem.IsWindows())
        {
            await ZenWebServer.RunAsync(args);
            return;
        }

        using var cts = new CancellationTokenSource();
        Microsoft.AspNetCore.Builder.WebApplication? serverApp = null;
        try
        {
            serverApp = await ZenWebServer.StartBackgroundAsync("http://localhost:5080", cts.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ZenConnect] Background server note: {ex.Message}");
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
                    await serverApp.StopAsync();
                    await serverApp.DisposeAsync();
                }
                catch
                {
                }
            }
        }
#else
        await ZenWebServer.RunAsync(args);
#endif
    }
}
