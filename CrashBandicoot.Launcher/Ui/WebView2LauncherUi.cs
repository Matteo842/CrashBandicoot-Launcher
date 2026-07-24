using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CrashBandicoot.Launcher.Ui;

/// <summary>Windows system-WebView implementation of <see cref="ILauncherUi"/>.</summary>
public sealed class WebView2LauncherUi : ILauncherUi
{
    readonly WebView2 _web = new() { Dock = DockStyle.Fill };
    bool _disposed;

    public Control? AsControl => _web;

    public bool Visible
    {
        get => _web.Visible;
        set => _web.Visible = value;
    }

    public event EventHandler<LauncherUiMessageEventArgs>? MessageReceived;

    public async Task InitializeAsync(string uiDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uiDirectory);
        var index = Path.Combine(uiDirectory, "index.html");
        if (!File.Exists(index))
            throw new FileNotFoundException("Launcher UI missing: " + index);

        var env = await CoreWebView2Environment.CreateAsync().ConfigureAwait(true);
        await _web.EnsureCoreWebView2Async(env).ConfigureAwait(true);

        _web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
        _web.CoreWebView2.Settings.IsStatusBarEnabled = false;
        _web.CoreWebView2.Settings.AreDevToolsEnabled = true;

        _web.CoreWebView2.WebMessageReceived += OnWebMessage;

        _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "crash.launcher",
            uiDirectory,
            CoreWebView2HostResourceAccessKind.Allow);
        _web.CoreWebView2.Navigate("https://crash.launcher/index.html");
    }

    public void PostJson(string json)
    {
        if (_disposed || _web.CoreWebView2 == null) return;
        _web.CoreWebView2.PostWebMessageAsJson(json);
    }

    void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        MessageReceived?.Invoke(this, new LauncherUiMessageEventArgs(e.WebMessageAsJson));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_web.CoreWebView2 != null)
                _web.CoreWebView2.WebMessageReceived -= OnWebMessage;
        }
        catch
        {
            // disposing
        }

        _web.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
