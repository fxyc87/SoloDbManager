using System.Threading;
using SoloDbManager;

namespace SoloDbManager;

/// <summary>
/// Explicit entry point. The [STAThread] attribute is CRITICAL: the main
/// thread must stay STA (single-threaded apartment) for WebView2 to initialize.
///
/// Top-level statements / async Main would let the runtime CoInitialize the
/// main thread as MTA, which breaks WebView2 (RPC_E_CHANGED_MODE). So we use a
/// classic synchronous [STAThread] Main that ONLY does WinForms work; every
/// other initialization (SQLite, ASP.NET Core) runs on a background MTA thread.
/// </summary>
internal static class EntryPoint
{
    [STAThread]
    private static void Main(string[] args)
    {
        var port = Environment.GetEnvironmentVariable("PORT") ?? "3000";
        var hostname = Environment.GetEnvironmentVariable("HOSTNAME") ?? "127.0.0.1";
        var url = $"http://{hostname}:{port}/";

        if (!OperatingSystem.IsWindows())
        {
            // Non-Windows: no WebView2. Run server inline + open browser.
            NonWindowsRun(hostname, port, url);
            return;
        }

        // Windows: spin up a background MTA thread that owns ALL the non-UI
        // work — SQLite init, WebApplication build, Kestrel run. The main STA
        // thread stays pristine for WinForms + WebView2.
        var ready = new ManualResetEventSlim(false);
        WebAppHandle? handle = null;
        Exception? startupError = null;

        var serverThread = new Thread(() =>
        {
            try
            {
                // SQLite init happens here (MTA thread), not on the main thread.
                NativeLibLoader.InitSQLiteNative();
                SQLitePCL.Batteries_V2.Init();

                var app = WebServerHost.Build(hostname, port);
                app.Lifetime.ApplicationStarted.Register(() => ready.Set());
                handle = new WebAppHandle(app);
                app.RunAsync().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                startupError = ex;
                ready.Set();
            }
        })
        {
            IsBackground = true,
        };
        serverThread.SetApartmentState(ApartmentState.MTA);
        serverThread.Start();

        // Wait for the server to be listening (or fail) before opening the
        // window — so WebView2 loads an already-live URL.
        ready.Wait(TimeSpan.FromSeconds(15));

        if (startupError != null)
        {
            System.Windows.Forms.MessageBox.Show(
                "Failed to start the server:\n\n" + startupError.Message,
                "SoloDB Manager",
                System.Windows.Forms.MessageBoxButtons.OK,
                System.Windows.Forms.MessageBoxIcon.Error);
            return;
        }

        // Open the WebView2 window on the main STA thread. Blocks until closed.
        WebViewShell.Run(url, () =>
        {
            try { handle?.Stop(); } catch { }
            try { serverThread.Join(3000); } catch { }
        });
    }

    private static void NonWindowsRun(string hostname, string port, string url)
    {
        NativeLibLoader.InitSQLiteNative();
        SQLitePCL.Batteries_V2.Init();
        var app = WebServerHost.Build(hostname, port);
        app.Lifetime.ApplicationStarted.Register(() => AppLauncher.Launch(url));
        app.Run();
    }

    private sealed class WebAppHandle
    {
        private readonly Microsoft.AspNetCore.Builder.WebApplication _app;
        public WebAppHandle(Microsoft.AspNetCore.Builder.WebApplication app) => _app = app;
        public void Stop()
        {
            try { _app.StopAsync().Wait(3000); } catch { }
        }
    }
}
