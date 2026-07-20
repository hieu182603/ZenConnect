using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

class Program
{
    [DllImport("user32.dll")]
    static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    static void Main()
    {
        string currentDir = AppDomain.CurrentDomain.BaseDirectory;
        string serverDir = Path.Combine(currentDir, "ScreenReviewServer", "bin", "Release", "net8.0", "publish");
        string serverExe = Path.Combine(serverDir, "ScreenReviewServer.exe");

        string clientDir = Path.Combine(currentDir, "ZenConnectClient", "bin", "Release", "net8.0-windows", "publish");
        string clientExe = Path.Combine(clientDir, "ZenConnect.exe");

        // Start server hidden
        Process serverProcess = null;
        if (File.Exists(serverExe))
        {
            var serverStartInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                WorkingDirectory = serverDir,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
                UseShellExecute = false
            };
            serverStartInfo.EnvironmentVariables["ASPNETCORE_URLS"] = "http://localhost:5080";
            try
            {
                serverProcess = Process.Start(serverStartInfo);
            }
            catch {}
        }

        // Start client normally
        if (File.Exists(clientExe))
        {
            var clientStartInfo = new ProcessStartInfo
            {
                FileName = clientExe,
                WorkingDirectory = clientDir
            };
            try
            {
                Process clientProcess = Process.Start(clientStartInfo);
                clientProcess.WaitForExit();
            }
            catch {}
        }

        // Kill server when client closes
        if (serverProcess != null && !serverProcess.HasExited)
        {
            try
            {
                serverProcess.Kill();
            }
            catch {}
        }
    }
}
