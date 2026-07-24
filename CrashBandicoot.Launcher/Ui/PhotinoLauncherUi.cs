namespace CrashBandicoot.Launcher.Ui;

/// <summary>
/// Placeholder for the future Linux (and optional cross-platform) Photino / WebKitGTK host.
/// Same <c>Ui/index.html</c> and JSON protocol as <see cref="WebView2LauncherUi"/>.
/// Wire it from <see cref="LauncherUiFactory"/> when a Linux RID is added — do not use RmlUi here.
/// </summary>
public sealed class PhotinoLauncherUi : ILauncherUi
{
    const string NotReady =
        "PhotinoLauncherUi is not implemented yet. " +
        "Add Photino.NET / PhotinoX for Linux WebKitGTK and implement ILauncherUi against the same index.html. " +
        "See Ui/UI_STRATEGY.md.";

    public Control? AsControl => null;

    public bool Visible
    {
        get => false;
        set { _ = value; }
    }

    public event EventHandler<LauncherUiMessageEventArgs>? MessageReceived;

    public Task InitializeAsync(string uiDirectory)
    {
        _ = uiDirectory;
        _ = MessageReceived; // keep event for future wiring
        return Task.FromException(new NotImplementedException(NotReady));
    }

    public void PostJson(string json)
    {
        _ = json;
        throw new NotImplementedException(NotReady);
    }

    public void Dispose() { }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
