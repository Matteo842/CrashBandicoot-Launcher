namespace CrashBandicoot.Launcher.Ui;

/// <summary>
/// HTML launcher surface. Protocol is JSON messages matching <c>Ui/index.html</c>.
/// Windows uses WebView2; Linux should use Photino/WebKitGTK implementing this same contract.
/// See <c>Ui/UI_STRATEGY.md</c>.
/// </summary>
public interface ILauncherUi : IAsyncDisposable, IDisposable
{
    /// <summary>
    /// WinForms child used by <see cref="LauncherHost"/> today.
    /// Future top-level hosts (Photino) may return null and own the window themselves.
    /// </summary>
    Control? AsControl { get; }

    bool Visible { get; set; }

    /// <summary>Raised on the UI thread when the page posts a JSON object message.</summary>
    event EventHandler<LauncherUiMessageEventArgs>? MessageReceived;

    /// <summary>
    /// Initialize the engine and navigate to <c>index.html</c> under <paramref name="uiDirectory"/>.
    /// </summary>
    Task InitializeAsync(string uiDirectory);

    /// <summary>Push a JSON object string to the page (camelCase fields as produced by the host).</summary>
    void PostJson(string json);
}

public sealed class LauncherUiMessageEventArgs : EventArgs
{
    public LauncherUiMessageEventArgs(string json) => Json = json;

    public string Json { get; }
}
