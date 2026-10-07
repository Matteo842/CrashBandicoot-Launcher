using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace CrashBandicoot.Launcher.Linux;

/// <summary>Entry point of the graphical Linux launcher (Avalonia on X11 / XWayland).</summary>
static class LinuxGui
{
    const string IconResource = "CrashBandicoot.icon.png";

    static bool _windowShown;

    /// <summary>"v2.1" from the assembly version (without the +commit suffix).</summary>
    public static string VersionLabel { get; } = "v" + (Assembly.GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?").Split('+')[0];

    /// <summary>
    /// Run the launcher. Returns false (and nothing was shown) when there is no
    /// X11 display to open it on; the caller then falls back to starting the game directly.
    /// </summary>
    public static bool TryRun(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Console.WriteLine("[Launcher] no X11 display (DISPLAY is not set), starting without the launcher window");
            return false;
        }

        try
        {
            Console.WriteLine("[Launcher] opening launcher window");
            exitCode = AppBuilder.Configure<LauncherApp>()
                .UsePlatformDetect()
                .With(new X11PlatformOptions
                {
                    // Software rendering: always works (VMs, odd GL drivers) and the menu is light.
                    // The retained framebuffer lets the crate animation repaint only its own area.
                    RenderingMode = [X11RenderingMode.Software],
                    UseRetainedFramebuffer = true,
                    EnableSessionManagement = false,
                })
                .With(new FontManagerOptions { DefaultFamilyName = LinuxTheme.DefaultFamilyName })
                .StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);
            return true;
        }
        catch (Exception ex) when (!_windowShown)
        {
            Console.Error.WriteLine($"[Launcher] launcher window unavailable: {ex.GetBaseException().Message}");
            return false;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[Launcher] FAIL: " + ex);
            LinuxLaunch.Notify($"The launcher stopped: {ex.GetBaseException().Message}\n\nDetails: {LinuxLaunch.LogPath}",
                error: true);
            exitCode = 1;
            return true;
        }
    }

    public static WindowIcon? LoadWindowIcon()
    {
        try
        {
            using var stream = typeof(LinuxGui).Assembly.GetManifestResourceStream(IconResource);
            return stream == null ? null : new WindowIcon(stream);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The 256×256 app icon (PNG bytes), for the desktop menu entry.</summary>
    public static byte[]? IconPng()
    {
        using var stream = typeof(LinuxGui).Assembly.GetManifestResourceStream(IconResource);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    sealed class LauncherApp : Application
    {
        public override void Initialize()
        {
            RequestedThemeVariant = ThemeVariant.Dark;
            Styles.Add(new FluentTheme());
            LinuxTheme.AddResources(Resources);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new LauncherWindow();
                window.Opened += (_, _) => _windowShown = true;
                desktop.MainWindow = window;
            }
            base.OnFrameworkInitializationCompleted();
        }
    }
}
