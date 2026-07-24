namespace CrashBandicoot.Launcher.Ui;

/// <summary>
/// Creates the platform UI host. Swap the body when adding Linux Photino without touching game logic.
/// </summary>
public static class LauncherUiFactory
{
    public static ILauncherUi Create()
    {
        // Priority: full CSS via system WebView (see UI_STRATEGY.md).
        // Linux: return new PhotinoLauncherUi() (or equivalent) when that RID ships.
        return new WebView2LauncherUi();
    }
}
