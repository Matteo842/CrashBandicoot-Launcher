namespace CrashBandicoot.Launcher.Ui;

/// <summary>
/// Launcher menu surface. Protocol is JSON messages
/// (<c>ready</c>, <c>start</c>, <c>state</c>, <c>prepare</c>, …) between the UI and <c>LauncherHost</c>.
/// Windows implementation: <see cref="NativeLauncherUi"/>.
/// </summary>
public interface ILauncherUi : IAsyncDisposable, IDisposable
{
    /// <summary>WinForms child used by <see cref="LauncherHost"/>.</summary>
    Control? AsControl { get; }

    bool Visible { get; set; }

    /// <summary>Raised on the UI thread when the UI posts a JSON object message.</summary>
    event EventHandler<LauncherUiMessageEventArgs>? MessageReceived;

    /// <summary>
    /// Initialize UI assets under <paramref name="uiDirectory"/> (fonts, <c>world_map.png</c>).
    /// </summary>
    Task InitializeAsync(string uiDirectory);

    /// <summary>Push a JSON object string to the UI (camelCase fields as produced by the host).</summary>
    void PostJson(string json);
}

public sealed class LauncherUiMessageEventArgs : EventArgs
{
    public LauncherUiMessageEventArgs(string json) => Json = json;

    public string Json { get; }
}
