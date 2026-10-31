using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Hardware;
using RecompOne.Runtime.Hle.Vulkan;
using RecompOne.Runtime.Host.Window;

namespace RecompOne.Runtime.Host;

// Vulkan game window (Renderer setting "Vulkan"): a GLFW window without a GL
// context, the HLE renderer on VkBackend and ImGui on VkImGuiController. Any
// failure while opening it falls back to the OpenGL chain (CreateGlWindow).
internal static partial class HostWindow
{
    static VkGpu? _vkGpu;
    static VkBackend? _vkBackend;
    static VkSwapchain? _vkSwapchain;
    static VkImGuiController? _imguiVk;
    static nint _vkPresentId, _vkCrtId, _vkVramId, _vkRamId;
    static int _vkPresentVersion, _vkCrtVersion;
    static bool _vkPresentLinear;
    static byte[] _rgbaVram = [];

    static bool UsingVulkan => _vkBackend != null;

    static string VulkanName => $"Vulkan {_vkGpu?.ApiVersionText} ({_vkGpu?.DeviceName})";

    static void ResetVulkanState()
    {
        _vkGpu = null;
        _vkBackend = null;
        _vkSwapchain = null;
        _imguiVk = null;
        _vkPresentId = _vkCrtId = _vkVramId = _vkRamId = 0;
        _vkPresentVersion = _vkCrtVersion = 0;
        _vkPresentLinear = false;
    }

    /// <summary>
    /// Open the window with a Vulkan surface, device and HLE renderer. False
    /// (and nothing left behind) when the driver, GLFW or the GPU cannot.
    /// </summary>
    static unsafe bool TryCreateVulkanWindow(WindowOptions options)
    {
        try
        {
            Console.WriteLine("[Host] creating Vulkan window…");
            Console.Out.Flush();
            // No GL context: Silk must neither set a swap interval nor swap buffers.
            _window = Silk.NET.Windowing.Window.Create(options with
            {
                API = GraphicsAPI.DefaultVulkan,
                VSync = false,
                ShouldSwapAutomatically = false,
            });
            InstallGlfwErrorLog();
            DisableFullscreenAutoIconify();
            _window.Initialize();
            if (_window.VkSurface is not { } surfaceSource)
                throw new InvalidOperationException("the window has no Vulkan surface support");

            var names = surfaceSource.GetRequiredExtensions(out uint count);
            var extensions = SilkMarshal.PtrToStringArray((nint)names, (int)count);
            _vkGpu = VkGpu.CreateInstance(extensions, "Crash Bandicoot");
            var surface = surfaceSource.Create<AllocationCallbacks>(_vkGpu.Instance.ToHandle(), null).ToSurface();
            try
            {
                _vkGpu.CreateDevice(surface);
            }
            catch
            {
                _vkGpu.KhrSurface.DestroySurface(_vkGpu.Instance, surface, null);
                throw;
            }

            var window = _window;
            _vkSwapchain = new VkSwapchain(_vkGpu, surface)
            {
                // Wayland surfaces report no size of their own.
                FallbackSize = () => (window.FramebufferSize.X, window.FramebufferSize.Y),
                VSync = ConfigManager.View.VSync,
            };
            _window.FramebufferResize += _ => _vkSwapchain?.MarkStale();

            // The VRAM image is sized from the internal resolution at Init.
            Hle.GlVram.Scale = ConfigManager.View.InternalResolution;
            _vkBackend = new VkBackend(_vkGpu)
            {
                PipelineCachePath = Path.Combine(AppPaths.Root, "cache", "vulkan-pipelines.bin"),
            };
            _vkBackend.Init();
            Console.WriteLine($"[Host] {VulkanName} window created");
            return true;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[Host] Vulkan unavailable ({e.GetType().Name}: {e.Message}); falling back to OpenGL");
            DisposeVulkan();
            try { _window?.Dispose(); }
            catch { /* half-initialized window */ }
            _window = null;
            return false;
        }
    }

    static void LoadVulkanRenderer()
    {
        int internalRes = ConfigManager.View.InternalResolution;
        Hle.GpuHle.Active = true;
        Hle.GpuHle.Backend = _vkBackend;
        ApplyHleSettings(internalRes);
        _imguiVk = new VkImGuiController(_vkBackend!, _vkSwapchain!, _window!, new ImGuiInputContext(_input!), ConfigureImGui);
    }

    /// <summary>Frame on the Vulkan window: same steps as the OpenGL OnRender.</summary>
    static void OnRenderVulkan(double dt)
    {
        Diagnostics.FrameTimer.Mark(Diagnostics.FrameTimer.Phase.Events);
        var imgui = _imguiVk!;
        var backend = _vkBackend!;
        // Pad input for the pause menu is queued before ImGui's NewFrame.
        PauseMenuPad.Update();
        imgui.Update((float)dt);
        ApplyUiScale();
        Diagnostics.FrameTimer.Mark(Diagnostics.FrameTimer.Phase.Ui);

        bool ramMapOpen = TickDebugViews();

        var gpu = _gpu;
        if (gpu != null)
        {
            if (_frozen)
            {
                // Paused (HoldWhilePaused): keep the last present image. Re-presenting
                // idle render targets would drop the 16:9 margins after a few frames.
            }
            else if (Hle.GpuHle.Active && backend.Ready && gpu.DisplayEnabled)
            {
                var wf = _window!.FramebufferSize;
                int displayY = gpu.DisplayY;
                int displayHeight = gpu.DisplayHeight;
                CropNtscOverscan(ref displayY, ref displayHeight);
                var (tw, th, aspect) = backend.PresentDisplay(
                    gpu.DisplayX, displayY,
                    gpu.DisplayWidth, displayHeight,
                    gpu.Display24Bit,
                    outW: wf.X, outH: wf.Y);
                if (tw > 0 && th > 0 && backend.RenderPresentImage(tw, th) && backend.PresentImage is { } image)
                {
                    bool linear = !backend.PresentImageNearest;
                    if (_vkPresentId == 0 || backend.PresentImageVersion != _vkPresentVersion || linear != _vkPresentLinear)
                    {
                        _vkPresentId = imgui.Register(_vkPresentId, image, linear);
                        _vkPresentVersion = backend.PresentImageVersion;
                        _vkPresentLinear = linear;
                    }
                    OutputPanel.SetTexture((uint)_vkPresentId, tw, th, aspect, displayHeight);
                }
            }

            if (PanelManager.Get<VramViewerPanel>()?.IsOpen == true)
                UploadVramTextureVulkan(gpu);
        }

        if (ramMapOpen)
        {
            QueueRamConvert();
            if (_ramReady) FlushRamTextureVulkan();
        }
        Diagnostics.FrameTimer.Mark(Diagnostics.FrameTimer.Phase.Render);

        DrawUi();
        imgui.Render();
        Diagnostics.FrameTimer.Mark(Diagnostics.FrameTimer.Phase.Ui);
    }

    /// <summary>Whether <paramref name="id"/> is the game image (the CRT filter applies to it).</summary>
    internal static bool IsPresentTexture(uint id)
    {
        if (UsingVulkan) return _vkPresentId != 0 && id == (uint)_vkPresentId;
        return _glBackend != null && id == _glBackend.PresentTexture;
    }

    /// <summary>
    /// CRT filter over the last game image into a texture of exactly
    /// <paramref name="width"/>×<paramref name="height"/> screen pixels. 0 when unavailable.
    /// </summary>
    internal static uint RenderCrt(int width, int height)
    {
        if (!UsingVulkan) return _glBackend?.RenderCrt(width, height) ?? 0;
        if (_vkBackend!.RenderCrt(width, height) is not { } tex) return 0;
        if (_vkCrtId == 0 || _vkBackend.CrtOutputVersion != _vkCrtVersion)
        {
            // Drawn 1:1 on screen pixels; nearest keeps the shadow mask crisp.
            _vkCrtId = _imguiVk!.Register(_vkCrtId, tex, linear: false);
            _vkCrtVersion = _vkBackend.CrtOutputVersion;
        }
        return (uint)_vkCrtId;
    }

    static void UploadVramTextureVulkan(Gpu gpu)
    {
        const int n = Gpu.VramWidth * Gpu.VramHeight;
        if (_rgbaVram.Length < n * 4) _rgbaVram = new byte[n * 4];
        ushort[] src;
        if (Hle.GpuHle.Active && _vkBackend is { Ready: true })
        {
            _vkBackend.ReadVram(0, 0, Gpu.VramWidth, Gpu.VramHeight, _vramView);
            src = _vramView;
        }
        else src = gpu.Vram;
        int o = 0;
        for (int i = 0; i < n; i++)
        {
            ushort px = src[i];
            _rgbaVram[o++] = (byte)((px & 0x1F) << 3);
            _rgbaVram[o++] = (byte)(((px >> 5) & 0x1F) << 3);
            _rgbaVram[o++] = (byte)(((px >> 10) & 0x1F) << 3);
            _rgbaVram[o++] = 0xFF;
        }
        _vkVramId = _imguiVk!.Upload(_vkVramId, Gpu.VramWidth, Gpu.VramHeight, _rgbaVram, linear: false);
        VramViewerPanel.SetTexture((uint)_vkVramId, Gpu.VramWidth, Gpu.VramHeight);
    }

    static void FlushRamTextureVulkan()
    {
        if (_ramFront == null) return;
        _ramReady = false;
        _vkRamId = _imguiVk!.Upload(_vkRamId, Memory.RamLogger.Width, Memory.RamLogger.Height, _ramFront, linear: false);
        RamMapPanel.SetTexture((uint)_vkRamId);
    }

    static void DisposeVulkan()
    {
        try { _vkBackend?.Dispose(); }
        catch (Exception e) { Console.Error.WriteLine($"[Host] Vulkan backend dispose: {e.Message}"); }
        try { _vkSwapchain?.Dispose(); }
        catch (Exception e) { Console.Error.WriteLine($"[Host] Vulkan swapchain dispose: {e.Message}"); }
        try { _vkGpu?.Dispose(); }
        catch (Exception e) { Console.Error.WriteLine($"[Host] Vulkan device dispose: {e.Message}"); }
        _vkBackend = null;
        _vkSwapchain = null;
        _vkGpu = null;
    }
}
