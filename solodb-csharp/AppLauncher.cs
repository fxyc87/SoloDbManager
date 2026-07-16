using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SoloDbManager;

/// <summary>
/// On startup: prints the listen URL (clickable in modern terminals), opens
/// it in the user's default browser, and minimizes the console window on
/// Windows so the browser takes focus.
/// </summary>
public static class AppLauncher
{
    public static void Launch(string url)
    {
        // Print a clearly-delimited banner with the URL. Modern terminals
        // (Windows Terminal, VS Code, ConEmu) auto-linkify http(s) URLs, so
        // Ctrl+Click / Cmd+Click opens them. We also emit an ANSI hyperlink
        // escape sequence as a hint for terminals that support OSC 8.
        var border = new string('=', 56);
        Console.Error.WriteLine();
        Console.Error.WriteLine(border);
        // OSC 8 hyperlink (ignored by terminals that don't support it, rendered
        // as plain text otherwise).
        Console.Error.Write("\u001b]8;;" + url + "\u001b\\");
        Console.Error.Write("  SoloDB Manager 已启动 → " + url);
        Console.Error.WriteLine("\u001b]8;;\u001b\\");
        Console.Error.WriteLine("  (Ctrl+Click the link, or it opens in your browser automatically.)");
        Console.Error.WriteLine("  Press Ctrl+C to stop.");
        Console.Error.WriteLine(border);
        Console.Error.WriteLine();

        // Open the default browser. UseShellExecute is required on .NET Core.
        try
        {
            var ps = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            };
            Process.Start(ps);
        }
        catch
        {
            // Fallbacks for Linux/macOS.
            try
            {
                var runner = OperatingSystem.IsLinux() ? "xdg-open"
                           : OperatingSystem.IsMacOS() ? "open"
                           : null;
                if (runner != null)
                    Process.Start(runner, url);
            }
            catch { /* ignore — the printed URL is still clickable */ }
        }

        // Minimize the console window on Windows so the browser gets focus.
        if (OperatingSystem.IsWindows())
        {
            try { MinimizeConsoleWindow(); } catch { }
        }
    }

    private static void MinimizeConsoleWindow()
    {
        var handle = GetConsoleWindow();
        if (handle == IntPtr.Zero) return;
        // SW_MINIMIZE = 6, SW_RESTORE = 9, SW_SHOWMINNOACTIVE = 7
        ShowWindow(handle, 6);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
