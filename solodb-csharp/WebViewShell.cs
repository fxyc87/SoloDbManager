using System;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;

namespace SoloDbManager;

/// <summary>
/// A minimal WinForms shell that hosts a WebView2 control pointed at the
/// embedded web server. This turns the web app into a native desktop window
/// (no external browser needed). WebView2 uses the Edge runtime which is
/// preinstalled on Windows 11.
/// </summary>
public static class WebViewShell
{
    public static void Run(string url, Action onClosed)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var form = new MainForm(url, onClosed);
        Application.Run(form);
    }
}

internal sealed class MainForm : Form
{
    private readonly WebView2 _webView;
    private readonly string _url;
    private readonly Action _onClosed;
    private bool _closing;

    public MainForm(string url, Action onClosed)
    {
        _url = url;
        _onClosed = onClosed;

        Text = "SoloDB Manager";
        Width = 1280;
        Height = 820;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Application;

        _webView = new WebView2
        {
            Dock = DockStyle.Fill,
        };
        Controls.Add(_webView);

        FormClosing += OnFormClosing;
        Load += OnLoad;
    }

    private async void OnLoad(object? sender, EventArgs e)
    {
        try
        {
            await _webView.EnsureCoreWebView2Async();
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled =
                Environment.GetEnvironmentVariable("SOLODB_DEVTOOLS") == "1";
            // External links (target=_blank) open in the system default browser.
            _webView.CoreWebView2.NewWindowRequested += (s, ev) =>
            {
                if (ev.Uri != null)
                {
                    ev.Handled = true;
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = ev.Uri,
                        UseShellExecute = true,
                    });
                }
            };
            _webView.Source = new Uri(_url);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Failed to initialize WebView2.\n\n" + ex.Message +
                "\n\nWebView2 Runtime is preinstalled on Windows 11. If it's " +
                "missing, install it from https://developer.microsoft.com/microsoft-edge/webview2/",
                "SoloDB Manager",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            Close();
        }
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        _closing = true;
        try { _onClosed(); } catch { }
    }
}
