using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Memory;

namespace RecompOne.Runtime;

public enum RunMode { Retail, Devkit }

public static class Runtime
{
    static IRuntimePlatformHost? _platformHost;

    public static CpuContext? Cpu { get; private set; }
    public static IMemory? Mem { get; private set; }
    public static Gpu? Gpu;
    public static Spu? Spu;
    public static Cdrom.CdController? Cd;

    public static RunMode Mode { get; private set; } = RunMode.Retail;
    public static void SetMode(RunMode mode) => Mode = mode; //devkit vs retail, devkits reads from sim and has more ram
    public static string CdPath => Config.ConfigManager.Game.CdPath;

    /// <summary>
    /// Installs a non-desktop host before Initialize. Android uses this to
    /// present software VRAM and keep all writable data in app storage.
    /// Passing null restores the normal desktop host.
    /// </summary>
    public static void SetPlatformHost(IRuntimePlatformHost? host) => _platformHost = host;
    
    public static Config.ViewConfig View => Config.ConfigManager.View;
    public static void SaveView() => Config.ConfigManager.SaveView(Host.Window.PanelManager.Panels);
    
    public static Hardware.MemoryCard CardA = new(AppPaths.CardAPath) { Enabled = true };
    public static Hardware.MemoryCard CardB = new(AppPaths.CardBPath) { Enabled = true };
    public static readonly Memory.RamLogger RamLog = new();
    public static readonly Dispatch.OverlayEventLog OverlayLog = new();

    /// <summary>Parent HWND for the next session (0 = standalone Silk window).</summary>
    public static void SetEmbedParent(nint hwnd) => HostWindow.SetEmbedParent(hwnd);

    /// <summary>Resize an embedded OpenGL child to its host panel.</summary>
    public static void FitEmbeddedWindow() => HostWindow.FitEmbeddedToParent();

    /// <summary>
    /// Optional host (e.g. WinForms launcher) that owns the outer shell chrome.
    /// When set, embedded fullscreen is applied here instead of maximizing only.
    /// </summary>
    static Action<bool>? _hostFullscreen;

    public static void SetHostFullscreenHandler(Action<bool>? handler) => _hostFullscreen = handler;

    internal static bool TryHostFullscreen(bool on)
    {
        if (_hostFullscreen == null) return false;
        _hostFullscreen(on);
        return true;
    }

    /// <summary>Apply / leave fullscreen (shell + immersive chrome).</summary>
    public static void SetFullscreen(bool on) => HostWindow.SetFullscreen(on);

    /// <summary>Queue a fullscreen toggle (safe from WinForms key routing).</summary>
    public static void RequestFullscreenToggle() => Host.InputManager.RequestFullscreenToggle();

    /// <summary>Queue a developer-menu toggle (safe from WinForms key routing).</summary>
    public static void RequestCheatMenuToggle() => Host.InputManager.RequestCheatMenuToggle();

    /// <summary>Queue a pause-menu toggle (safe from WinForms key routing).</summary>
    public static void RequestPauseMenuToggle() => Host.InputManager.RequestPauseMenuToggle();

    public static void Initialize(string title)
    {
        Diagnostics.ConsoleMirror.Install();
        Diagnostics.SessionLog.Start($"title={title}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                Diagnostics.SessionLog.Exception("UnhandledException", ex);
            else
                Diagnostics.SessionLog.Error($"UnhandledException: {e.ExceptionObject}");
            Diagnostics.SessionLog.Stop();
        };
        if (_platformHost != null)
        {
            _platformHost.Initialize(title);
            _platformHost.SetMasterVolume(Config.ConfigManager.Game.Muted ? 0f : Config.ConfigManager.Game.MasterVolume);
        }
        else
        {
            HostWindow.Initialize(title);
            Audio.Initialize();
            Audio.SetMasterVolume(Config.ConfigManager.Game.Muted ? 0f : Config.ConfigManager.Game.MasterVolume);
        }
        if (Event.HasAnyListeners<RuntimeReadyEvent>())
        {
            Event.Dispatch(new RuntimeReadyEvent());
        }
    }

    public static void WaitForValidDisc()
    {
        if (_platformHost != null) _platformHost.WaitForValidDisc();
        else HostWindow.WaitForValidDisc();
    }
    
    public static void ShowNotice(string message)
    {
        if (_platformHost != null) _platformHost.ShowNotice(message);
        else Host.Window.NoticePopup.Show(message);
    }

    public static void SetStartupNotice(string message, string title = "Notice", string ackKey = "StartupNoticeAck")
    {
        if (_platformHost != null) _platformHost.ShowNotice($"{title}: {message}");
        else Host.Window.StartupNotice.Set(message, title, ackKey);
    }

    public static void SetContext(CpuContext c, IMemory m)
    {
        Cpu = c;
        Mem = m;
    }

    public static void PresentFrame()
    {
        if (_platformHost != null)
        {
            _platformHost.Present(Gpu);
            _platformHost.AttachAudio(Spu);
        }
        else
        {
            HostWindow.Present(Gpu);
            Audio.Attach(Spu);
        }
        FrameClock.Throttle();
        Sdk.LibCd.Tick();
        if (Mem != null) { Bios.BiosB.RefreshPad(Mem); Sdk.LibPad.Refresh(Mem); }
        Host.FramePacing.PulseVblankIrq();
    }

    public static void DispatchIrq(int irq)
    {
        if (Cpu != null && Mem != null)
            Interrupts.Deliver(irq, Cpu, Mem);
    }

    public static void Shutdown()
    {
        if (_platformHost != null)
            _platformHost.Shutdown();
        else
        {
            Audio.Shutdown();
            HostWindow.Shutdown();
        }
        Diagnostics.SessionLog.Stop();
    }
}
