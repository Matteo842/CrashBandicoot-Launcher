using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace RecompOne.Runtime.Hle.Vulkan;

// Desktop window: the game is shown through ImGui (Output panel), as with
// GlBackend. The present pass renders into an owned image (GL's present
// texture) and the window's frame is the ImGui pass drawn into the swapchain.
public sealed unsafe partial class VkBackend
{
    RenderPass _texPass;
    VkTex? _presentImage;
    Framebuffer _presentImageFb;

    /// <summary>The image <see cref="RenderPresentImage"/> last rendered into (null before the first frame).</summary>
    internal VkTex? PresentImage => _presentImage;
    /// <summary>Bumped whenever <see cref="PresentImage"/> is replaced (ImGui must re-register it).</summary>
    internal int PresentImageVersion { get; private set; }
    internal bool PresentImageNearest { get; private set; }
    internal int PresentNativeHeight => _presentNativeH;
    internal float PresentAspectRatio => _presentAspect;

    internal Vk Api => _vk;
    internal Device Dev => _dev;
    internal PipelineCache Cache => _cache;
    internal Sampler NearestSampler => _nearest;
    internal Sampler LinearSampler => _linear;

    /// <summary>Command buffer being recorded, with no render pass open.</summary>
    internal CommandBuffer Commands
    {
        get
        {
            EndPass();
            return Cb;
        }
    }

    /// <summary>Colour-only RGBA8 pass that leaves its target ready to sample (present image, CRT output).</summary>
    internal RenderPass TexturePass
    {
        get
        {
            if (_texPass.Handle == 0) _texPass = CreateSampledPass(Format.R8G8B8A8Unorm);
            return _texPass;
        }
    }

    /// <summary>
    /// Render the source chosen by <see cref="PresentDisplay"/> into an owned
    /// <paramref name="w"/>×<paramref name="h"/> image, like GlBackend's present
    /// texture. False when the backend is not ready.
    /// </summary>
    public bool RenderPresentImage(int w, int h)
    {
        if (!Ready || w <= 0 || h <= 0) return false;
        EndPass();
        // Nearest when native scale, or when the player asked for crisp pixels.
        PresentImageNearest = GlVram.Scale <= 1 || GpuHle.PresentNearest;
        var pass = TexturePass;
        if (_presentImage == null || _presentImage.W != w || _presentImage.H != h)
        {
            if (_presentImage != null)
            {
                var oldTex = _presentImage;
                var oldFb = _presentImageFb;
                Defer(() =>
                {
                    _vk.DestroyFramebuffer(_dev, oldFb, null);
                    oldTex.Destroy(_gpu);
                });
            }
            _presentImage = CreateSampledTarget(w, h, Format.R8G8B8A8Unorm);
            _presentImageFb = CreateFramebuffer(pass, _presentImage.View, default, w, h);
            PresentImageVersion++;
        }

        EnsurePresentPipelines(pass);
        var source = _presentSrc ?? _vram;
        EnsureLayout(source, ImageLayout.ShaderReadOnlyOptimal);
        var set = SetFor(source, source, present: true);

        var cb = Cb;
        BeginTexturePass(pass, _presentImageFb, w, h);
        _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _presentRgb24 ? _present24Pipe : _presentPipe);
        _vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _presentLayout, 0, 1, &set, 0, null);
        var push = _presentPush;
        _vk.CmdPushConstants(cb, _presentLayout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PresentPush), &push);
        _vk.CmdDraw(cb, 3, 1, 0, 0);
        _vk.CmdEndRenderPass(cb);
        _presentImage.Layout = ImageLayout.ShaderReadOnlyOptimal;
        return true;
    }

    /// <summary>Begin <paramref name="pass"/> on a w×h target with a full viewport and scissor.</summary>
    internal void BeginTexturePass(RenderPass pass, Framebuffer framebuffer, int w, int h)
    {
        EndPass();
        var cb = Cb;
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = pass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)w, (uint)h)),
        };
        _vk.CmdBeginRenderPass(cb, &begin, SubpassContents.Inline);
        var viewport = new Viewport(0, 0, w, h, 0f, 1f);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)w, (uint)h));
        _vk.CmdSetViewport(cb, 0, 1, &viewport);
        _vk.CmdSetScissor(cb, 0, 1, &scissor);
    }

    /// <summary>
    /// Colour pass whose target is fully overwritten (no load) and left in
    /// SHADER_READ_ONLY. Ordered against all earlier and later work, like the
    /// target passes.
    /// </summary>
    internal RenderPass CreateSampledPass(Format format)
    {
        var color = new AttachmentDescription
        {
            Format = format,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };
        var deps = stackalloc SubpassDependency[2];
        deps[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.AllCommandsBit,
            DstStageMask = PipelineStageFlags.AllGraphicsBit,
            SrcAccessMask = AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
        };
        deps[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.AllGraphicsBit,
            DstStageMask = PipelineStageFlags.AllCommandsBit,
            SrcAccessMask = AccessFlags.MemoryWriteBit,
            DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &color,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = deps,
        };
        RenderPass pass;
        VkGpu.Check(_vk.CreateRenderPass(_dev, &info, null, &pass), "vkCreateRenderPass(sampled)");
        return pass;
    }

    internal VkTex CreateSampledTarget(int w, int h, Format format)
        => VkTex.Create(_gpu, w, h, format,
            ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageAspectFlags.ColorBit);

    internal Framebuffer CreateColorFramebuffer(RenderPass pass, VkTex target)
        => CreateFramebuffer(pass, target.View, default, target.W, target.H);

    internal ShaderModule CreateShader(string base64) => CreateShaderModule(base64);

    internal Pipeline CreateGraphicsPipeline(RenderPass pass, PipelineLayout layout, PipelineShaderStageCreateInfo* stages,
        PipelineVertexInputStateCreateInfo* vertexInput, PipelineColorBlendAttachmentState* attachment)
        => CreatePipeline(pass, layout, stages, vertexInput, null, attachment, depthBias: false);

    /// <summary>Clear a whole colour image to opaque black and leave it ready to sample.</summary>
    internal void ClearToBlack(VkTex tex)
    {
        EndPass();
        Barrier(tex, ImageLayout.TransferDstOptimal);
        var color = new ClearColorValue(0f, 0f, 0f, 1f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        _vk.CmdClearColorImage(Cb, tex.Image, ImageLayout.TransferDstOptimal, &color, 1, &range);
        Barrier(tex, ImageLayout.ShaderReadOnlyOptimal);
    }

    /// <summary>Sampled RGBA8 image filled from the CPU with <see cref="UploadTexture"/> (ImGui fonts, debug views).</summary>
    internal VkTex CreateUploadTexture(int w, int h)
        => VkTex.Create(_gpu, w, h, Format.R8G8B8A8Unorm,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);

    /// <summary>Copy tightly packed RGBA8 pixels into the whole of <paramref name="tex"/>.</summary>
    internal void UploadTexture(VkTex tex, ReadOnlySpan<byte> rgba)
    {
        ulong size = (ulong)tex.W * (ulong)tex.H * 4;
        if ((ulong)rgba.Length < size) throw new ArgumentException("pixel data too small", nameof(rgba));
        EndPass();
        var (buffer, offset, ptr) = Alloc(size);
        rgba[..(int)size].CopyTo(new Span<byte>((void*)ptr, (int)size));
        Barrier(tex, ImageLayout.TransferDstOptimal);
        var region = new BufferImageCopy
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D((uint)tex.W, (uint)tex.H, 1),
        };
        _vk.CmdCopyBufferToImage(Cb, buffer, tex.Image, ImageLayout.TransferDstOptimal, 1, &region);
        Barrier(tex, ImageLayout.ShaderReadOnlyOptimal);
    }

    /// <summary>Host-visible memory valid until this frame's commands finished (vertex/index usage).</summary>
    internal (Buffer buffer, ulong offset, nint ptr) AllocFrame(ulong size, ulong align = 16)
    {
        EndPass();
        return Alloc(size, align);
    }

    /// <summary>Run <paramref name="action"/> once the GPU is done with everything recorded so far.</summary>
    internal void DeferUntilIdle(Action action) => Defer(action);

    /// <summary>
    /// Window frame: clear the next swapchain image, let <paramref name="record"/>
    /// draw into it (render pass open, arguments: command buffer, swapchain
    /// pass, width, height), then submit and queue it. Allocate everything the
    /// callback needs before calling: no allocation may happen inside the pass.
    /// False when the frame was dropped (resized, minimized or lost surface).
    /// </summary>
    internal bool PresentOverlay(VkSwapchain swapchain, ClearColorValue clear, Action<CommandBuffer, RenderPass, int, int> record)
    {
        if (!TryAcquire(swapchain, out uint index)) return false;
        var cb = Cb;
        BeginSwapchainPass(swapchain, index, clear);
        try
        {
            record(cb, swapchain.RenderPass, swapchain.Width, swapchain.Height);
        }
        finally
        {
            _vk.CmdEndRenderPass(cb);
        }
        return SubmitAndPresent(swapchain, index);
    }

    VkCrt? _crt;
    bool _crtFailed;
    int _crtFrameVersion;

    /// <summary>Bumped whenever the CRT output image is replaced (ImGui must re-register it).</summary>
    internal int CrtOutputVersion => _crt?.OutputVersion ?? 0;

    /// <summary>
    /// Run the CRT filter over the last present image into an image of exactly
    /// <paramref name="width"/>×<paramref name="height"/> screen pixels, like
    /// GlBackend.RenderCrt. Null when the filter is unavailable.
    /// </summary>
    internal VkTex? RenderCrt(int width, int height)
    {
        if (!Ready || _presentImage == null || width <= 0 || height <= 0) return null;
        if (_crt == null && !_crtFailed)
        {
            // Built on first use so players who never enable the filter pay nothing.
            var crt = new VkCrt(this);
            try
            {
                crt.Init();
                _crt = crt;
            }
            catch (Exception ex)
            {
                crt.Dispose();
                _crtFailed = true;
                LastDiagnostic = $"crt: {ex.Message}";
                Console.Error.WriteLine($"[VkBackend] CRT filter unavailable: {ex.Message}");
            }
        }
        if (_crt == null) return null;
        if (_crtFrameVersion != PresentImageVersion)
        {
            _crt.ForgetFrame();
            _crtFrameVersion = PresentImageVersion;
        }
        return _crt.Render(_presentImage, _presentNativeH, _presentAspect, width, height);
    }

    void DisposeDesktop()
    {
        _crt?.Dispose();
        _crt = null;
        if (_presentImageFb.Handle != 0) _vk.DestroyFramebuffer(_dev, _presentImageFb, null);
        _presentImageFb = default;
        _presentImage?.Destroy(_gpu);
        _presentImage = null;
        if (_texPass.Handle != 0) _vk.DestroyRenderPass(_dev, _texPass, null);
        _texPass = default;
    }
}
