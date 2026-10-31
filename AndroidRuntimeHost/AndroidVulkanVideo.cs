using System.Runtime.InteropServices;
using Android.Views;
using RecompOne.Runtime.Hle;
using RecompOne.Runtime.Hle.Vulkan;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Result = Silk.NET.Vulkan.Result;

namespace CrashBandicoot.AndroidRuntime;

/// <summary>
/// Vulkan renderer on the TextureView surface. Built and used on the game
/// thread only. Surface loss and relayout are handled like
/// <see cref="AndroidEglContext"/>: rebuild from the current SurfaceTexture.
/// </summary>
sealed unsafe class AndroidVulkanVideo : IAndroidVideo
{
    const int MaxSurfaceRecreateFailures = 120;

    public static AndroidVulkanVideo? Current { get; private set; }

    readonly Func<Surface> _surfaceSource;
    VkGpu? _gpu;
    VkBackend? _backend;
    VkSwapchain? _swapchain;
    KhrAndroidSurface? _khrAndroid;
    Surface? _surface;
    bool _ownsSurface;
    volatile int _expectedWidth;
    volatile int _expectedHeight;
    int _resizedForWidth;
    int _resizedForHeight;
    int _surfaceFailures;
    bool _disposed;

    public IGpuBackend Backend => _backend!;
    public VkBackend Vulkan => _backend!;
    public string Name => "Vulkan";
    public string DeviceName => _gpu?.DeviceName ?? "?";
    public string ApiVersion => _gpu?.ApiVersionText ?? "?";
    public string PresentModeLabel => _swapchain?.PresentMode.ToString() ?? "?";
    public int SurfaceWidth => _swapchain is { Width: > 0 } s ? s.Width : _expectedWidth;
    public int SurfaceHeight => _swapchain is { Height: > 0 } s ? s.Height : _expectedHeight;

    AndroidVulkanVideo(Func<Surface> surfaceSource) => _surfaceSource = surfaceSource;

    /// <summary>Throws when the phone has no usable Vulkan driver; the caller falls back to GLES.</summary>
    public static AndroidVulkanVideo Create(Surface surface, Func<Surface> surfaceSource, string pipelineCachePath)
    {
        var video = new AndroidVulkanVideo(surfaceSource);
        try
        {
            video.Init(surface, pipelineCachePath);
            Current = video;
            return video;
        }
        catch
        {
            video.Dispose();
            throw;
        }
    }

    void Init(Surface surface, string pipelineCachePath)
    {
        _gpu = VkGpu.CreateInstance([KhrSurface.ExtensionName, KhrAndroidSurface.ExtensionName], "Crash Bandicoot");
        if (!_gpu.Vk.TryGetInstanceExtension(_gpu.Instance, out KhrAndroidSurface khrAndroid))
            throw new InvalidOperationException("VK_KHR_android_surface is not available.");
        _khrAndroid = khrAndroid;

        var vkSurface = CreateSurface(surface, owns: false);
        try
        {
            _gpu.CreateDevice(vkSurface);
        }
        catch
        {
            _gpu.KhrSurface.DestroySurface(_gpu.Instance, vkSurface, null);
            throw;
        }
        _swapchain = new VkSwapchain(_gpu, vkSurface)
        {
            FallbackSize = () => (_expectedWidth, _expectedHeight),
        };
        _backend = new VkBackend(_gpu)
        {
            Mobile = true,
            PipelineCachePath = pipelineCachePath,
        };
        _backend.Init();
    }

    SurfaceKHR CreateSurface(Surface surface, bool owns)
    {
        nint window = ANativeWindow_fromSurface(Android.Runtime.JNIEnv.Handle, surface.Handle);
        if (window == 0)
            throw new InvalidOperationException("ANativeWindow_fromSurface returned null.");
        // PAL speed caps the clock at 25; the display itself stays at 60.
        AndroidDisplayPacing.ApplySurface(surface,
            Math.Max(60.0, RecompOne.Runtime.Host.FrameClock.TargetHz));

        var info = new AndroidSurfaceCreateInfoKHR
        {
            SType = StructureType.AndroidSurfaceCreateInfoKhr,
            Window = (nint*)window,
        };
        SurfaceKHR vkSurface;
        var result = _khrAndroid!.CreateAndroidSurface(_gpu!.Instance, &info, null, &vkSurface);
        // The VkSurfaceKHR holds its own reference to the window.
        ANativeWindow_release(window);
        if (result != Result.Success)
            throw new InvalidOperationException($"vkCreateAndroidSurfaceKHR failed ({result}).");
        if (_ownsSurface && !ReferenceEquals(_surface, surface)) _surface?.Dispose();
        _surface = surface;
        _ownsSurface = owns;
        return vkSurface;
    }

    public void SetExpectedSize(int width, int height)
    {
        _expectedWidth = width;
        _expectedHeight = height;
    }

    public void SetPresentHz(double targetHz)
    {
        if (_surface != null)
            AndroidDisplayPacing.ApplySurface(_surface, targetHz);
    }

    public (int w, int h, float aspect) Prepare(int dispX, int dispY, int w, int h, bool rgb24, int surfaceWidth, int surfaceHeight)
        => _backend!.PresentDisplay(dispX, dispY, w, h, rgb24, surfaceWidth, surfaceHeight);

    public void Composite(int surfaceWidth, int surfaceHeight, float aspect)
    {
        var swapchain = _swapchain!;
        if (_expectedWidth > 0 && _expectedHeight > 0 && swapchain.Width > 0 && SizeDiffers(swapchain))
        {
            bool alreadyTried = _resizedForWidth == _expectedWidth && _resizedForHeight == _expectedHeight;
            if (!alreadyTried)
            {
                _resizedForWidth = _expectedWidth;
                _resizedForHeight = _expectedHeight;
                int oldW = swapchain.Width, oldH = swapchain.Height;
                // The window usually reports the new buffer size itself; only
                // rebuild the surface (as EGL does) when it does not.
                swapchain.MarkStale();
                if ((!swapchain.EnsureValid() || SizeDiffers(swapchain)) && ReplaceSurface())
                    swapchain.EnsureValid();
                Android.Util.Log.Info("CrashGPU",
                    $"Vulkan swapchain resized ({oldW}x{oldH} → {swapchain.Width}x{swapchain.Height}, view {_expectedWidth}x{_expectedHeight}).");
            }
        }

        if (swapchain.SurfaceLost && !ReplaceSurface())
        {
            // Mid-relayout: drop the frame, keep the recorded work moving.
            _backend!.SubmitPending();
            return;
        }

        if (_backend!.PresentTo(swapchain))
            _surfaceFailures = 0;
    }

    bool SizeDiffers(VkSwapchain swapchain)
        => Math.Abs(swapchain.Width - _expectedWidth) > 8 || Math.Abs(swapchain.Height - _expectedHeight) > 8;

    // Vulkan queues the image inside Composite.
    public void Swap() { }

    bool ReplaceSurface()
    {
        Surface? fresh = null;
        try
        {
            fresh = _surfaceSource();
            _swapchain!.ReplaceSurface(() => CreateSurface(fresh, owns: true));
            _surfaceFailures = 0;
            return true;
        }
        catch (Exception ex)
        {
            if (fresh != null && !ReferenceEquals(fresh, _surface)) fresh.Dispose();
            Android.Util.Log.Warn("CrashGPU", $"Vulkan surface recreate failed: {ex.Message}");
            if (++_surfaceFailures >= MaxSurfaceRecreateFailures)
                throw new InvalidOperationException("The Android video surface cannot be recovered.");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (Current == this) Current = null;
        try { _backend?.Dispose(); } catch (Exception ex) { Android.Util.Log.Warn("CrashGPU", $"Vulkan backend dispose: {ex.Message}"); }
        try { _swapchain?.Dispose(); } catch (Exception ex) { Android.Util.Log.Warn("CrashGPU", $"Vulkan swapchain dispose: {ex.Message}"); }
        try { _gpu?.Dispose(); } catch (Exception ex) { Android.Util.Log.Warn("CrashGPU", $"Vulkan device dispose: {ex.Message}"); }
        if (_ownsSurface) _surface?.Dispose();
        _surface = null;
    }

    [DllImport("android")]
    static extern nint ANativeWindow_fromSurface(nint env, nint surface);

    [DllImport("android")]
    static extern void ANativeWindow_release(nint window);
}
