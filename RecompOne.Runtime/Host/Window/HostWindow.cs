using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;
using Silk.NET.Windowing;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Host.Window;

namespace RecompOne.Runtime.Host;

/// <summary>Thrown when the game window closes while embedded in a host form (instead of killing the process).</summary>
public sealed class GameSessionEndedException : Exception
{
    public GameSessionEndedException() : base("Game session ended.") { }
}

internal static class HostWindow
{
    const int GwlStyle = -16;
    const int WsChild = 0x40000000;
    const int WsVisible = 0x10000000;
    const int WsClipSiblings = 0x04000000;
    const int WsCaption = 0x00C00000;
    const int WsThickFrame = 0x00040000;
    const int WsMinimizeBox = 0x00020000;
    const int WsMaximizeBox = 0x00010000;
    const int WsSysMenu = 0x00080000;
    const int WsPopup = unchecked((int)0x80000000);
    const int SwMaximize = 3;
    const int SwRestore = 9;
    const uint GaRoot = 2;

    static IWindow? _window;
    static GL? _gl;
    static ImGuiController? _imgui;
    static bool _headless;
    static Gpu? _gpu;

    static uint _displayTex;
    static uint _vramTex;
    static uint _ramTex;
    static Hle.GlBackend? _glBackend;

    static byte[] _rgbDisplay = [];
    static byte[] _rgbVram = [];
    static byte[] _ramFront = new byte[Memory.RamLogger.Width * Memory.RamLogger.Height * 4];
    static byte[] _ramBack = new byte[Memory.RamLogger.Width * Memory.RamLogger.Height * 4];
    static Task? _ramTask;
    static volatile bool _ramReady;
    static int _ramFrame;

    static bool _layoutPending = true;
    static bool _closed;
    static DiscPickerPopup? _discPicker;

    /// <summary>When set before <see cref="Initialize"/>, the Silk window is parented into this HWND.</summary>
    static nint _embedParent;
    static nint _embedChild;
    static bool _embedded;

    /// <summary>Parent HWND for the next game session (0 = standalone window).</summary>
    public static void SetEmbedParent(nint hwnd) => _embedParent = hwnd;

    public static bool IsEmbedded => _embedded;

    public static void Initialize(string title)
    {
        ConfigManager.Load();
        ResetSessionState();

        try
        {
            var embed = _embedParent != 0;
            var options = WindowOptions.Default with
            {
                Size = new Vector2D<int>(1280, 720),
                // Hide off-screen until SetParent — avoids a brief second-window flash.
                Position = embed ? new Vector2D<int>(-32000, -32000) : new Vector2D<int>(100, 100),
                Title = title,
                VSync = false,
                UpdatesPerSecond = 0,
                FramesPerSecond = 0,
                WindowBorder = embed ? WindowBorder.Hidden : WindowBorder.Resizable,
                WindowState = !embed && ConfigManager.View.Fullscreen ? WindowState.Fullscreen : WindowState.Normal,
                API = new GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.Default, new APIVersion(4, 5)),
            };
            _window = Silk.NET.Windowing.Window.Create(options);
            _window.Load += OnLoad;
            _window.Render += OnRender;
            _window.Closing += OnClosing;
            _window.Initialize();

            // GLFW defaults to a generic icon — use the exe/ApplicationIcon instead.
            if (_window.Native?.Win32 is { } win32 && win32.Hwnd != 0)
                ApplyProcessIcon(win32.Hwnd);

            if (embed)
                TryEmbedIntoParent(_embedParent);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] window unavailable {e.Message}");
            _headless = true;
        }
    }

    static void ResetSessionState()
    {
        _closed = false;
        _layoutPending = true;
        _headless = false;
        _gpu = null;
        _gl = null;
        _imgui = null;
        _glBackend = null;
        _displayTex = _vramTex = _ramTex = 0;
        _discPicker = null;
        _embedded = false;
        _embedChild = 0;
        _ramTask = null;
        _ramReady = false;
        _ramFrame = 0;
    }

    static void TryEmbedIntoParent(nint parent)
    {
        if (_window?.Native?.Win32 is not { } win32 || parent == 0)
            return;

        var child = win32.Hwnd;
        if (child == 0) return;

        // Borderless child of the launcher panel.
        var style = GetWindowLong(child, GwlStyle);
        style &= ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu | WsPopup);
        style |= WsChild | WsVisible | WsClipSiblings;
        SetWindowLong(child, GwlStyle, style);

        SetParent(child, parent);
        _embedChild = child;
        _embedded = true;
        FitEmbeddedToParent();
    }

    /// <summary>Keep the OpenGL child sized to the host panel (call on panel resize).</summary>
    public static void FitEmbeddedToParent()
    {
        if (!_embedded || _embedChild == 0 || _embedParent == 0) return;
        if (!GetClientRect(_embedParent, out var rc)) return;
        int w = Math.Max(1, rc.Right - rc.Left);
        int h = Math.Max(1, rc.Bottom - rc.Top);
        MoveWindow(_embedChild, 0, 0, w, h, true);
        if (_window != null)
            _window.Size = new Vector2D<int>(w, h);
    }

    public static void Present(Gpu? gpu)
    {
        _gpu = gpu;
        if (_headless || _window == null) return;
        try { _window.DoEvents(); }
        catch (Exception e) {
            Console.WriteLine(e.Message);
        }
        if (_window.IsClosing) { EndSession(); return; }
        InputManager.Poll();
        if (InputManager.ConsumeTopBarToggle())
        {
            ConfigManager.View.HideTopBar = !ConfigManager.View.HideTopBar;
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (InputManager.ConsumeFullscreenToggle())
        {
            ConfigManager.View.Fullscreen = !ConfigManager.View.Fullscreen;
            SetFullscreen(ConfigManager.View.Fullscreen);
            ConfigManager.SaveView(PanelManager.Panels);
        }
        if (InputManager.ConsumeSessionMarker())
            RecompOne.Runtime.Diagnostics.SessionLog.Marker();
        _window.DoRender();
    }

    /// <summary>Pump window/input events without presenting a frame (for pad sampling mid-frame).</summary>
    public static void PumpInput()
    {
        if (_headless || _window == null) return;
        try { _window.DoEvents(); } catch { }
        if (_window.IsClosing) { EndSession(); return; }
        InputManager.Poll();
    }

    internal static void Pump()
    {
        if (_headless || _window == null) return;
        try { _window.DoEvents(); } catch { }
        if (_window.IsClosing) { EndSession(); return; }
        _window.DoRender();
    }

    static void EndSession()
    {
        var embedded = _embedded;
        Runtime.Shutdown();
        if (embedded)
            throw new GameSessionEndedException();
        Environment.Exit(0);
    }

    public static void Shutdown()
    {
        if (!_headless && _window != null && !_window.IsClosing)
            _window.Close();
        InputManager.Shutdown();
        _embedParent = 0;
        _embedChild = 0;
        _embedded = false;
        // Avoid launcher SaveView seeing a dangling ImGui context after the session ends.
        try { if (ImGui.GetCurrentContext() != IntPtr.Zero) ImGui.SetCurrentContext(IntPtr.Zero); }
        catch { /* ImGui may already be torn down */ }
    }

    public static void SetFullscreen(bool on)
    {
        if (_window == null) return;
        if (_embedded && _embedParent != 0)
        {
            // Maximize the launcher shell instead of exclusive fullscreen on the child.
            var root = GetAncestor(_embedParent, GaRoot);
            if (root != 0)
                ShowWindow(root, on ? SwMaximize : SwRestore);
            FitEmbeddedToParent();
            return;
        }
        _window.WindowState = on ? WindowState.Fullscreen : WindowState.Normal;
    }

    /// <summary>
    /// Enable 16:9 horizontal FOV expansion (side margins). Off keeps classic 4:3 without stretch.
    /// Cinema levels (Crash 1 Intro/Ending) keep 4:3 framing with black pillars while widescreen is on.
    /// </summary>
    public static void ApplyWidescreen(bool on)
    {
        Hle.GpuHle.WideAspect = on ? 16f / 9f : 0f;
        Hle.GpuHle.RefreshWideFov();
    }

    public static bool IsKeyDown(Key k) => InputManager.IsKeyDown(k);

    public static void RequestDiscPath() => _discPicker?.Show();

    public static void WaitForValidDisc() // wait for disc path to be valid before running it!!
    {
        if (_headless || _window == null) return;

        while (StartupNotice.NeedsAck)
        {
            try { _window.DoEvents(); } catch { }
            if (_window.IsClosing) { EndSession(); return; }
            InputManager.Poll();
            _window.DoRender();
        }

        while (true)
        {
            var path = ConfigManager.Game.CdPath;
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path)) return;

            try { _window.DoEvents(); } catch { }
            if (_window.IsClosing) { EndSession(); return; }
            InputManager.Poll();
            _window.DoRender();
        }
    }

    const int WmSetIcon = 0x0080;
    const nint IconSmall = 0;
    const nint IconBig = 1;

    static void ApplyProcessIcon(nint hwnd)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || hwnd == 0) return;
            _ = ExtractIconEx(exe, 0, out var large, out var small, 1);
            if (large != 0)
                SendMessage(hwnd, WmSetIcon, IconBig, large);
            if (small != 0)
                SendMessage(hwnd, WmSetIcon, IconSmall, small);
        }
        catch
        {
            // keep default window icon
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint SetParent(nint hWndChild, nint hWndNewParent);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int GetWindowLong(nint hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool MoveWindow(nint hWnd, int x, int y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    static extern nint SendMessage(nint hWnd, int msg, nint wParam, nint lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string lpszFile, int nIconIndex, out nint phiconLarge, out nint phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool GetClientRect(nint hWnd, out Rect lpRect);

    [DllImport("user32.dll", SetLastError = true)]
    static extern nint GetAncestor(nint hWnd, uint gaFlags);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    static void OnLoad()
    {
        var input = _window!.CreateInput();
        InputManager.Initialize(input);

        _gl = GL.GetApi(_window);
        _gl.ClearColor(0.08f, 0.08f, 0.08f, 1f);

        var fb = _window!.FramebufferSize;
        _gl.Viewport(0, 0, (uint)fb.X, (uint)fb.Y);
        _window.FramebufferResize += size => _gl?.Viewport(0, 0, (uint)size.X, (uint)size.Y);
        _displayTex = CreateTexture(_gl);
        _vramTex= CreateTexture(_gl);
        _ramTex = CreateTexture(_gl);

        Hle.GlVram.Scale = ConfigManager.View.NativeResolution ? 1 : 4;
        _glBackend = new Hle.GlBackend(_gl);
        _glBackend.InitGl();
        Hle.GpuHle.Active = _glBackend.Ready;
        Hle.GpuHle.Backend = _glBackend;
        Hle.GpuHle.NativeResolution = ConfigManager.View.NativeResolution;
        ApplyWidescreen(ConfigManager.View.Widescreen);

        _imgui = new ImGuiController(_gl, _window, input, null, ConfigureImGui);

        PanelManager.Register(new OutputPanel());
        PanelManager.Register(new VramViewerPanel());
        PanelManager.Register(new CpuStatePanel());
        PanelManager.Register(new RamMapPanel());
        PanelManager.Register(new MemoryEditorPanel());
        PanelManager.Register(new SpuViewerPanel());
        PanelManager.Register(new CdDebugPanel());
        PanelManager.Register(new ConsolePanel());
        PanelManager.Register(new OverlayEventsPanel());
        PanelManager.Register(new SettingsPopup());
        PanelManager.Register(new Modding.ModsPopup());
        PanelManager.Register(new AboutPopup());

        SettingsRegistry.Register(new InputSettingsSection());
        SettingsRegistry.Register(new DisplaySettingsSection());
        SettingsRegistry.Register(new AudioSettingsSection());

        _discPicker = new DiscPickerPopup();
        PanelManager.Register(_discPicker);

        ConfigManager.ApplyViewToPanels(PanelManager.Panels);

        var cdPath = ConfigManager.Game.CdPath;
        if (string.IsNullOrWhiteSpace(cdPath) || !File.Exists(cdPath))
            _discPicker.Show();
    }

    static void ConfigureImGui()
    {
        var io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.DockingEnable;
        io.ConfigWindowsMoveFromTitleBarOnly = true;
        unsafe { io.NativePtr->IniFilename = null; }

        // Load saved sizes/positions for debug panels, but always re-dock Output
        // to the center — a bad interface.ini (or launcher SaveView with a stale
        // ImGui context) used to leave Output as a tiny floating 640×480 window.
        _ = Config.ConfigManager.ApplyImGuiLayout();
        _layoutPending = true;
    }

    static void OnRender(double dt)
    {
        var gl = _gl!;
        _imgui!.Update((float)dt);
    
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        var fbDef = _window!.FramebufferSize;
        gl.Viewport(0, 0, (uint)fbDef.X, (uint)fbDef.Y);
        gl.ClearColor(0.08f, 0.08f, 0.08f, 1f);
        gl.Clear(ClearBufferMask.ColorBufferBit);

        Runtime.RamLog.Tick();
        Memory.RamLogger.TrackReads =
            PanelManager.Get<RamMapPanel>()?.IsOpen == true ||
            PanelManager.Get<MemoryEditorPanel>()?.IsOpen == true;

        var gpu = _gpu;
        if (gpu != null)
        {

            if (Hle.GpuHle.Active && _glBackend is { Ready: true } && gpu.DisplayEnabled)
            {
                var wf = _window!.FramebufferSize;
                var (tex, tw, th, aspect) = _glBackend.PresentDisplay(
                    gpu.DisplayX, gpu.DisplayY,
                    gpu.DisplayWidth, gpu.DisplayHeight,
                    gpu.Display24Bit,
                    outW: wf.X, outH: wf.Y);
                if (tex != 0) OutputPanel.SetTexture(tex, tw, th, aspect);
                gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
                gl.Viewport(0, 0, (uint)wf.X, (uint)wf.Y);
            }
            else
            {
                UploadDisplayTexture(gl, gpu);
            }

            if (PanelManager.Get<VramViewerPanel>()?.IsOpen == true)
                UploadVramTexture(gl, gpu);
        }

        if (PanelManager.Get<RamMapPanel>()?.IsOpen == true)
        {
            QueueRamConvert();
            if (_ramReady) FlushRamTexture(gl);
        }

        if (!ConfigManager.View.HideTopBar)
            MainMenuBar.Draw();

        DrawDockspace();
        PanelManager.DrawPanels();
        MenuRegistry.DrawWindows();
        Modding.ModLoadingPopup.Draw();
        NoticePopup.Draw();
        if (StartupNotice.NeedsAck) StartupNotice.Draw();
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        gl.Viewport(0, 0, (uint)fbDef.X, (uint)fbDef.Y);
        _imgui.Render();
    }

    static void DrawDockspace()
    {
        var viewport = ImGui.GetMainViewport();
        ImGui.SetNextWindowPos(viewport.WorkPos);
        ImGui.SetNextWindowSize(viewport.WorkSize);
        ImGui.SetNextWindowViewport(viewport.ID);

        const ImGuiWindowFlags hostFlags = ImGuiWindowFlags.NoDocking | 
                                           ImGuiWindowFlags.NoTitleBar |
                                           ImGuiWindowFlags.NoCollapse |
                                           ImGuiWindowFlags.NoResize |
                                           ImGuiWindowFlags.NoMove |
                                           ImGuiWindowFlags.NoBringToFrontOnFocus |
                                           ImGuiWindowFlags.NoBackground;

        ImGui.PushStyleVar(ImGuiStyleVar.WindowRounding, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.Begin("##DockHost", hostFlags);
        ImGui.PopStyleVar(3);
        uint dockId = ImGui.GetID("##MainDock");
        // Only count real content/debug panels — Settings/Mods popups must not
        // flip DockSpace flags (old code used an invalid 4096 flag when count<=1).
        int contentOpen = PanelManager.Panels.Count(p =>
            p.IsOpen &&
            p is not AboutPopup and not SettingsPopup and not Modding.ModsPopup and not DiscPickerPopup);
        var dockFlags = ImGuiDockNodeFlags.PassthruCentralNode | ImGuiDockNodeFlags.AutoHideTabBar;
        if (contentOpen <= 1)
            dockFlags |= ImGuiDockNodeFlags.NoDockingSplit | ImGuiDockNodeFlags.NoUndocking;
        ImGui.DockSpace(dockId, Vector2.Zero, dockFlags);

        if (_layoutPending)
        {
            _layoutPending = false;
            DockBuilder.SetupCenterLayout(dockId, viewport.WorkSize, "Output");
        }

        ImGui.End();
    }

    static void OnClosing()
    {
        if (_closed) return;
        _closed = true;
        ConfigManager.SaveView(PanelManager.Panels);
        ConfigManager.SaveGame();
        PanelManager.Shutdown();
        _glBackend?.Dispose();
        _imgui?.Dispose();
        _gl?.DeleteTexture(_displayTex);
        _gl?.DeleteTexture(_vramTex);
        _gl?.DeleteTexture(_ramTex);
    }

    static uint CreateTexture(GL gl)
    {
        var tex = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, tex);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Nearest);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        return tex;
    }

    static void UploadDisplayTexture(GL gl, Gpu gpu)
    {
        int w = gpu.DisplayWidth, h = gpu.DisplayHeight;
        if (!gpu.DisplayEnabled || w <= 0 || h <= 0) return;
        int needed = w * h * 3;
        if (_rgbDisplay.Length < needed) _rgbDisplay = new byte[needed];
        ConvertDisplay(gpu, w, h);
        gl.BindTexture(TextureTarget.Texture2D, _displayTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgb, (uint)w, (uint)h, 0,
            PixelFormat.Rgb, PixelType.UnsignedByte, _rgbDisplay.AsSpan(0, needed));
        OutputPanel.SetTexture(_displayTex, w, h);
    }

    static ushort[] _vramView = new ushort[Gpu.VramWidth * Gpu.VramHeight];
    static void UploadVramTexture(GL gl, Gpu gpu)
    {
        const int sz = Gpu.VramWidth * Gpu.VramHeight * 3;
        if (_rgbVram.Length < sz) _rgbVram = new byte[sz];
        ushort[] src;
        if (Hle.GpuHle.Active && _glBackend is { Ready: true })
        {
            _glBackend.ReadVram(0, 0, Gpu.VramWidth, Gpu.VramHeight, _vramView);
            src = _vramView;
        }
        else src = gpu.Vram;
        ConvertVramToBuffer(src, _rgbVram);
        gl.BindTexture(TextureTarget.Texture2D, _vramTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgb, Gpu.VramWidth, Gpu.VramHeight, 0, PixelFormat.Rgb, PixelType.UnsignedByte, _rgbVram.AsSpan(0, sz));
        VramViewerPanel.SetTexture(_vramTex, Gpu.VramWidth, Gpu.VramHeight);
    }

    static void QueueRamConvert()
    {
        if (_ramTask is { IsCompleted: false }) return;
        if (++_ramFrame < 6) return;
        _ramFrame = 0;
        var psMem = Runtime.Mem as Memory.PSMemory;
        if (psMem == null) return;
        var ram = psMem.RamBuffer;
        var back = _ramBack;
        _ramTask = Task.Run(() => Runtime.RamLog.BuildTexture(ram, back))
            .ContinueWith(_ =>
            {
                (_ramFront, _ramBack) = (_ramBack, _ramFront);
                _ramReady = true;
            }, TaskContinuationOptions.ExecuteSynchronously);
    }

    static void FlushRamTexture(GL gl)
    {
        _ramReady = false;
        gl.BindTexture(TextureTarget.Texture2D, _ramTex);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba,
            Memory.RamLogger.Width, Memory.RamLogger.Height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, _ramFront);
        RamMapPanel.SetTexture(_ramTex);
    }

    static void ConvertDisplay(Gpu gpu, int w, int h)
    {
        var vram = gpu.Vram;
        int dx = gpu.DisplayX, dy = gpu.DisplayY;
        int o = 0;
        if (gpu.Display24Bit)
        {
            for (int y = 0; y < h; y++)
            {
                int lineByte = ((dy + y) * Gpu.VramWidth + dx) * 2;
                for (int x = 0; x < w; x++)
                {
                    int bo = lineByte + x * 3;
                    _rgbDisplay[o++] = VramByte(vram, bo);
                    _rgbDisplay[o++] = VramByte(vram, bo + 1);
                    _rgbDisplay[o++] = VramByte(vram, bo + 2);
                }
            }
        }
        else
        {
            for (int y = 0; y < h; y++)
            {
                int line = ((dy + y) & (Gpu.VramHeight - 1)) * Gpu.VramWidth;
                for (int x = 0; x < w; x++)
                {
                    ushort px = vram[line + ((dx + x) & (Gpu.VramWidth - 1))];
                    _rgbDisplay[o++] = (byte)((px & 0x1F) << 3);
                    _rgbDisplay[o++] = (byte)(((px >> 5) & 0x1F) << 3);
                    _rgbDisplay[o++] = (byte)(((px >> 10) & 0x1F) << 3);
                }
            }
        }
    }

    static void ConvertVramToBuffer(ushort[] vram, byte[] output)
    {
        int o = 0;
        for (int y = 0; y < Gpu.VramHeight; y++)
        for (int x = 0; x < Gpu.VramWidth; x++)
        {
            ushort px = vram[y * Gpu.VramWidth + x];
            output[o++] = (byte)((px & 0x1F) << 3);
            output[o++] = (byte)(((px >> 5) & 0x1F) << 3);
            output[o++] = (byte)(((px >> 10) & 0x1F) << 3);
        }
    }

    static byte VramByte(ushort[] vram, int byteOffset)
    {
        int hw = (byteOffset >> 1) & (Gpu.VramWidth * Gpu.VramHeight - 1);
        ushort v = vram[hw];
        return (byte)((byteOffset & 1) == 0 ? v & 0xFF : v >> 8);
    }
}
