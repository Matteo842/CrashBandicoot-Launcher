namespace CrashBandicoot.Launcher.Ui;

/// <summary>Creates the WinForms launcher UI host.</summary>
public static class LauncherUiFactory
{
    public static ILauncherUi Create() => new NativeLauncherUi();
}
