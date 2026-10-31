using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace RecompOne.Runtime.Hle.Vulkan;

/// <summary>
/// Swapchain of one window surface, with the render pass and framebuffers the
/// final present pass draws into. Never blocks on vsync when the driver offers
/// MAILBOX or IMMEDIATE: the frame clock paces the game (as EGL swap interval 0).
/// </summary>
public sealed unsafe class VkSwapchain : IDisposable
{
    readonly VkGpu _gpu;
    SurfaceKHR _surface;
    SwapchainKHR _swapchain;
    Image[] _images = [];
    ImageView[] _views = [];
    Framebuffer[] _framebuffers = [];
    Semaphore[] _renderDone = [];
    bool _stale = true;

    public RenderPass RenderPass { get; private set; }
    public Format Format { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public PresentModeKHR PresentMode { get; private set; }
    public SurfaceKHR Surface => _surface;
    internal SwapchainKHR Handle => _swapchain;
    internal Framebuffer FramebufferAt(uint index) => _framebuffers[index];
    internal Semaphore RenderDoneAt(uint index) => _renderDone[index];

    /// <summary>Called with the surface's own size when it reports none (0xFFFFFFFF).</summary>
    public Func<(int w, int h)>? FallbackSize { get; set; }

    public VkSwapchain(VkGpu gpu, SurfaceKHR surface)
    {
        _gpu = gpu;
        _surface = surface;
    }

    public void MarkStale() => _stale = true;

    bool _vsync;

    /// <summary>
    /// Wait for vblank (FIFO) instead of MAILBOX/IMMEDIATE: the desktop VSync
    /// setting. Takes effect on the next frame (the swapchain is rebuilt).
    /// </summary>
    public bool VSync
    {
        get => _vsync;
        set
        {
            if (_vsync == value) return;
            _vsync = value;
            _stale = true;
        }
    }

    /// <summary>The window surface is gone: the host must <see cref="ReplaceSurface"/>.</summary>
    public bool SurfaceLost { get; private set; }

    public void MarkSurfaceLost()
    {
        SurfaceLost = true;
        _stale = true;
    }

    /// <summary>
    /// Swap the window surface (Android rebuilds it on relayout). The old one is
    /// destroyed first: a window can back only one VkSurfaceKHR at a time.
    /// Takes ownership of what <paramref name="create"/> returns.
    /// </summary>
    public void ReplaceSurface(Func<SurfaceKHR> create)
    {
        _gpu.Vk.DeviceWaitIdle(_gpu.Device);
        DestroySwapchain(destroyHandle: true);
        if (_surface.Handle != 0)
            _gpu.KhrSurface.DestroySurface(_gpu.Instance, _surface, null);
        _surface = default;
        _stale = true;
        SurfaceLost = true;
        _surface = create();
        SurfaceLost = false;
    }

    /// <summary>Rebuild if stale. False while the surface has no drawable size.</summary>
    public bool EnsureValid()
    {
        if (!_stale && _swapchain.Handle != 0) return true;
        if (_surface.Handle == 0) return false;
        return Recreate();
    }

    bool Recreate()
    {
        var vk = _gpu.Vk;
        var khr = _gpu.KhrSurface;
        SurfaceCapabilitiesKHR caps;
        var capsResult = khr.GetPhysicalDeviceSurfaceCapabilities(_gpu.Physical, _surface, &caps);
        if (capsResult == Result.ErrorSurfaceLostKhr) SurfaceLost = true;
        if (capsResult != Result.Success)
            return false;

        uint w = caps.CurrentExtent.Width, h = caps.CurrentExtent.Height;
        if (w == uint.MaxValue || h == uint.MaxValue)
        {
            var size = FallbackSize?.Invoke() ?? (0, 0);
            w = (uint)Math.Clamp(size.w, (int)caps.MinImageExtent.Width, (int)Math.Max(caps.MinImageExtent.Width, caps.MaxImageExtent.Width));
            h = (uint)Math.Clamp(size.h, (int)caps.MinImageExtent.Height, (int)Math.Max(caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
        }
        if (w == 0 || h == 0) return false;

        var format = PickFormat();
        var mode = PickPresentMode();
        uint imageCount = Math.Max(caps.MinImageCount + 1, 3);
        if (caps.MaxImageCount > 0) imageCount = Math.Min(imageCount, caps.MaxImageCount);
        var alpha = (caps.SupportedCompositeAlpha & CompositeAlphaFlagsKHR.OpaqueBitKhr) != 0
            ? CompositeAlphaFlagsKHR.OpaqueBitKhr
            : (caps.SupportedCompositeAlpha & CompositeAlphaFlagsKHR.InheritBitKhr) != 0
                ? CompositeAlphaFlagsKHR.InheritBitKhr
                : (CompositeAlphaFlagsKHR)((uint)caps.SupportedCompositeAlpha & (uint)-(int)caps.SupportedCompositeAlpha);
        var transform = (caps.SupportedTransforms & SurfaceTransformFlagsKHR.IdentityBitKhr) != 0
            ? SurfaceTransformFlagsKHR.IdentityBitKhr
            : caps.CurrentTransform;

        var old = _swapchain;
        var info = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = format.Format,
            ImageColorSpace = format.ColorSpace,
            ImageExtent = new Extent2D(w, h),
            ImageArrayLayers = 1,
            ImageUsage = ImageUsageFlags.ColorAttachmentBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = transform,
            CompositeAlpha = alpha,
            PresentMode = mode,
            Clipped = true,
            OldSwapchain = old,
        };
        vk.DeviceWaitIdle(_gpu.Device);
        SwapchainKHR fresh;
        var result = _gpu.KhrSwapchain.CreateSwapchain(_gpu.Device, &info, null, &fresh);
        DestroySwapchain(destroyHandle: false);
        if (old.Handle != 0) _gpu.KhrSwapchain.DestroySwapchain(_gpu.Device, old, null);
        _swapchain = default;
        if (result == Result.ErrorSurfaceLostKhr || result == Result.ErrorNativeWindowInUseKhr) SurfaceLost = true;
        if (result != Result.Success)
        {
            Console.WriteLine($"[VkBackend] vkCreateSwapchainKHR failed ({result})");
            return false;
        }
        _swapchain = fresh;
        Width = (int)w;
        Height = (int)h;
        PresentMode = mode;

        if (RenderPass.Handle == 0 || Format != format.Format)
        {
            if (RenderPass.Handle != 0) vk.DestroyRenderPass(_gpu.Device, RenderPass, null);
            Format = format.Format;
            RenderPass = CreateRenderPass(format.Format);
        }

        uint count = 0;
        _gpu.KhrSwapchain.GetSwapchainImages(_gpu.Device, _swapchain, &count, null);
        _images = new Image[count];
        fixed (Image* p = _images)
            _gpu.KhrSwapchain.GetSwapchainImages(_gpu.Device, _swapchain, &count, p);
        _views = new ImageView[count];
        _framebuffers = new Framebuffer[count];
        _renderDone = new Semaphore[count];
        for (int i = 0; i < count; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _images[i],
                ViewType = ImageViewType.Type2D,
                Format = Format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            ImageView view;
            VkGpu.Check(vk.CreateImageView(_gpu.Device, &viewInfo, null, &view), "vkCreateImageView(swapchain)");
            _views[i] = view;
            var fbInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = RenderPass,
                AttachmentCount = 1,
                PAttachments = &view,
                Width = w,
                Height = h,
                Layers = 1,
            };
            Framebuffer fb;
            VkGpu.Check(vk.CreateFramebuffer(_gpu.Device, &fbInfo, null, &fb), "vkCreateFramebuffer(swapchain)");
            _framebuffers[i] = fb;
            var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            Semaphore sem;
            VkGpu.Check(vk.CreateSemaphore(_gpu.Device, &semInfo, null, &sem), "vkCreateSemaphore");
            _renderDone[i] = sem;
        }
        _stale = false;
        Console.WriteLine($"[VkBackend] swapchain {w}x{h} {Format} {mode}, {count} images");
        return true;
    }

    SurfaceFormatKHR PickFormat()
    {
        uint count = 0;
        _gpu.KhrSurface.GetPhysicalDeviceSurfaceFormats(_gpu.Physical, _surface, &count, null);
        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* p = formats)
            _gpu.KhrSurface.GetPhysicalDeviceSurfaceFormats(_gpu.Physical, _surface, &count, p);
        // UNORM, not sRGB: the GL path writes the PS1 colours without conversion.
        foreach (var wanted in new[] { Format.R8G8B8A8Unorm, Format.B8G8R8A8Unorm })
            foreach (var f in formats)
                if (f.Format == wanted) return f;
        return formats.Length > 0 ? formats[0] : new SurfaceFormatKHR(Format.R8G8B8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr);
    }

    PresentModeKHR PickPresentMode()
    {
        uint count = 0;
        _gpu.KhrSurface.GetPhysicalDeviceSurfacePresentModes(_gpu.Physical, _surface, &count, null);
        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* p = modes)
            _gpu.KhrSurface.GetPhysicalDeviceSurfacePresentModes(_gpu.Physical, _surface, &count, p);
        // FIFO is the one mode every driver must offer.
        if (_vsync) return PresentModeKHR.FifoKhr;
        if (modes.Contains(PresentModeKHR.MailboxKhr)) return PresentModeKHR.MailboxKhr;
        if (modes.Contains(PresentModeKHR.ImmediateKhr)) return PresentModeKHR.ImmediateKhr;
        return PresentModeKHR.FifoKhr;
    }

    RenderPass CreateRenderPass(Format format)
    {
        var color = new AttachmentDescription
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };
        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &color,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };
        RenderPass pass;
        VkGpu.Check(_gpu.Vk.CreateRenderPass(_gpu.Device, &info, null, &pass), "vkCreateRenderPass(present)");
        return pass;
    }

    void DestroySwapchain(bool destroyHandle)
    {
        var vk = _gpu.Vk;
        foreach (var fb in _framebuffers) if (fb.Handle != 0) vk.DestroyFramebuffer(_gpu.Device, fb, null);
        foreach (var view in _views) if (view.Handle != 0) vk.DestroyImageView(_gpu.Device, view, null);
        foreach (var sem in _renderDone) if (sem.Handle != 0) vk.DestroySemaphore(_gpu.Device, sem, null);
        _framebuffers = [];
        _views = [];
        _renderDone = [];
        _images = [];
        if (destroyHandle && _swapchain.Handle != 0)
        {
            _gpu.KhrSwapchain.DestroySwapchain(_gpu.Device, _swapchain, null);
            _swapchain = default;
        }
    }

    public void Dispose()
    {
        _gpu.Vk.DeviceWaitIdle(_gpu.Device);
        DestroySwapchain(destroyHandle: true);
        if (RenderPass.Handle != 0) _gpu.Vk.DestroyRenderPass(_gpu.Device, RenderPass, null);
        RenderPass = default;
        if (_surface.Handle != 0) _gpu.KhrSurface.DestroySurface(_gpu.Instance, _surface, null);
        _surface = default;
    }
}
