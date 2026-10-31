using Silk.NET.Vulkan;

namespace RecompOne.Runtime.Hle.Vulkan;

// Frame end: pick the display source like GlBackend.PresentDisplay, then draw
// it straight into the swapchain at the fitted rectangle (one pass, where GL
// renders a present texture first and composites it afterwards).
public sealed unsafe partial class VkBackend
{
    VkTex? _presentSrc;
    PresentPush _presentPush;
    bool _presentRgb24;
    int _presentNativeH = 240;
    float _presentAspect = GpuHle.BaseAspect;
    int _presentFbW, _presentFbH;
    VkTex? _lastCrtOut;

    public void Present(in HleDispEnv disp) => PresentDisplay(disp.X, disp.Y, disp.W, disp.H, disp.Rgb24);

    /// <summary>
    /// Choose what the next <see cref="PresentTo"/> shows. Same source rules as
    /// GL; returns the size GL would have rendered its present texture at.
    /// </summary>
    public (int w, int h, float aspect) PresentDisplay(int dispX, int dispY, int w, int h, bool rgb24 = false, int outW = 0, int outH = 0)
    {
        if (!Ready || w <= 0 || h <= 0) return (0, 0, GpuHle.OutputAspect);
        _classifyValid = false;
        _frame++;
        Flush();

        for (int i = 0; i < _rts.Length; i++)
        {
            if (_rts[i] is not { } rt) continue;
            if (_frame - rt.LastDrawFrame > 300)
            {
                if (rt.Dirty) Writeback(rt);
                DestroyRt(rt);
                _rts[i] = null;
            }
        }

        VkDisplayRt? src = null;
        if (!rgb24)
            foreach (var rt in _rts)
            {
                if (rt == null || (!rt.Dirty && _frame - rt.LastDrawFrame > 4)) continue;
                if (dispX < rt.X || dispY < rt.Y || dispX + w > rt.X + rt.W || dispY + h > rt.Y + rt.H) continue;
                if (src == null || rt.LastDrawFrame > src.LastDrawFrame) src = rt;
            }

        bool showWide = GpuHle.ShouldPresentWide(src is { Margin: > 0 }, src is { HasWideWorld: true });
        int w1x = showWide ? w + src!.Margin * 2 : w;
        int h1x = h;
        float aspect = showWide ? GpuHle.WideAspect : GpuHle.OutputAspect;
        _presentNativeH = h1x;
        _presentAspect = aspect;
        _presentRgb24 = rgb24;

        var push = new PresentPush();
        if (rgb24)
        {
            push.OriginX = dispX; push.OriginY = dispY;
            push.SizeX = w; push.SizeY = h;
            push.Mode = GlVram.Scale;
            _presentSrc = _vram;
        }
        else if (src != null)
        {
            int ox, ow, texW;
            if (showWide) { ox = 0; ow = src.Wide1x; texW = src.Wide1x; }
            else if (src.Margin > 0) { ox = src.Margin + dispX - src.X; ow = w; texW = src.Wide1x; }
            else { ox = dispX - src.X; ow = w; texW = src.W; }
            push.OriginX = ox; push.OriginY = dispY - src.Y;
            push.SizeX = ow; push.SizeY = h1x;
            push.TexSizeX = texW; push.TexSizeY = src.H;
            _presentSrc = src.Color;
        }
        else
        {
            push.OriginX = dispX; push.OriginY = dispY;
            push.SizeX = w; push.SizeY = h;
            push.TexSizeX = VramShadow.Width; push.TexSizeY = VramShadow.Height;
            _presentSrc = _vram;
        }
        int presentScale = GlVram.Scale;
        if (!rgb24)
            push.Mode = presentScale <= 1 || GpuHle.PresentNearest ? 1 : 0;
        _presentPush = push;

        int fbW = w1x * presentScale, fbH = h1x * presentScale;
        if (outW > 0 && outH > 0)
        {
            int visibleW = outW;
            int visibleH = Math.Max(1, (int)MathF.Round(visibleW / aspect));
            if (visibleH > outH)
            {
                visibleH = outH;
                visibleW = Math.Max(1, (int)MathF.Round(visibleH * aspect));
            }
            if ((long)visibleW * visibleH < (long)fbW * fbH) { fbW = visibleW; fbH = visibleH; }
        }
        _presentFbW = fbW;
        _presentFbH = fbH;

        if (_frameFlushes > 0)
        {
            DrawnFrames++;
            LastFrameFlushes = _frameFlushes;
            LastFrameWritebacks = _frameWritebacks;
            LastFrameVertices = _frameVertices;
            LastFrameCpuReads = _frameCpuReads;
            _frameCpuReads = 0;
            _frameFlushes = 0;
            _frameWritebacks = 0;
            _frameVertices = 0;
        }
        return (fbW, fbH, aspect);
    }

    /// <summary>
    /// Draw the source picked by <see cref="PresentDisplay"/> into the next
    /// swapchain image and queue it. False when the frame was dropped (surface
    /// resized or lost: the swapchain is rebuilt on the next call).
    /// </summary>
    public bool PresentTo(VkSwapchain swapchain)
    {
        if (!TryAcquire(swapchain, out uint index)) return false;

        int sw = swapchain.Width, sh = swapchain.Height;
        var fitted = DisplayLayout.Fit(sw, sh, _presentAspect, _presentNativeH, GpuHle.IntegerScale);
        int tw = Math.Clamp((int)MathF.Round(fitted.Width), 1, sw);
        int th = Math.Clamp((int)MathF.Round(fitted.Height), 1, sh);

        // CRT (Android): present image at the size GL's present texture has,
        // the filter at exact screen pixels, then a 1:1 copy into the window.
        VkTex? crt = null;
        if (GpuHle.CrtFilter && RenderPresentImage(Math.Max(1, _presentFbW), Math.Max(1, _presentFbH)))
            crt = RenderCrt(tw, th);
        if (crt != null && !ReferenceEquals(crt, _lastCrtOut))
        {
            // The CRT output was reallocated: its old view may not be reused by a cached set.
            if (_lastCrtOut != null) ForgetSets(_lastCrtOut);
            _lastCrtOut = crt;
        }

        EnsurePresentPipelines(swapchain.RenderPass);
        VkTex source;
        PresentPush push;
        bool rgb24 = _presentRgb24;
        if (crt != null)
        {
            source = crt;
            push = new PresentPush { SizeX = tw, SizeY = th, TexSizeX = tw, TexSizeY = th, Mode = 1 };
            rgb24 = false;
        }
        else
        {
            source = _presentSrc ?? _vram;
            push = _presentPush;
        }
        EnsureLayout(source, ImageLayout.ShaderReadOnlyOptimal);
        var set = SetFor(source, source, present: true);

        var cb = Cb;
        BeginSwapchainPass(swapchain, index, new ClearColorValue(0f, 0f, 0f, 1f));
        var viewport = new Viewport((sw - tw) / 2, (sh - th) / 2, tw, th, 0f, 1f);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)sw, (uint)sh));
        _vk.CmdSetViewport(cb, 0, 1, &viewport);
        _vk.CmdSetScissor(cb, 0, 1, &scissor);
        _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, rgb24 ? _present24Pipe : _presentPipe);
        _vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _presentLayout, 0, 1, &set, 0, null);
        _vk.CmdPushConstants(cb, _presentLayout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(PresentPush), &push);
        _vk.CmdDraw(cb, 3, 1, 0, 0);
        _vk.CmdEndRenderPass(cb);

        return SubmitAndPresent(swapchain, index);
    }

    /// <summary>
    /// Acquire the next swapchain image. False when the frame is dropped (the
    /// recorded work is submitted anyway and the swapchain rebuilt next time).
    /// </summary>
    bool TryAcquire(VkSwapchain swapchain, out uint index)
    {
        index = 0;
        if (!Ready) return false;
        EndPass();
        if (!swapchain.EnsureValid())
        {
            SubmitSlot();
            NextSlot();
            return false;
        }

        var slot = _slots[_cur];
        uint acquired;
        var acquire = _gpu.KhrSwapchain.AcquireNextImage(_dev, swapchain.Handle, ulong.MaxValue, slot.Acquire, default, &acquired);
        if (acquire == Result.ErrorOutOfDateKhr || acquire == Result.ErrorSurfaceLostKhr)
        {
            if (acquire == Result.ErrorSurfaceLostKhr) swapchain.MarkSurfaceLost();
            else swapchain.MarkStale();
            SubmitSlot();
            NextSlot();
            return false;
        }
        if (acquire != Result.Success && acquire != Result.SuboptimalKhr)
            throw new InvalidOperationException($"vkAcquireNextImageKHR failed ({acquire}).");
        index = acquired;
        return true;
    }

    void BeginSwapchainPass(VkSwapchain swapchain, uint index, ClearColorValue color)
    {
        var clear = new ClearValue(color);
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = swapchain.RenderPass,
            Framebuffer = swapchain.FramebufferAt(index),
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)swapchain.Width, (uint)swapchain.Height)),
            ClearValueCount = 1,
            PClearValues = &clear,
        };
        _vk.CmdBeginRenderPass(Cb, &begin, SubpassContents.Inline);
    }

    /// <summary>Submit the slot (waiting on the acquire, signalling render done) and queue the image.</summary>
    bool SubmitAndPresent(VkSwapchain swapchain, uint index)
    {
        var slot = _slots[_cur];
        var renderDone = swapchain.RenderDoneAt(index);
        SubmitSlot(slot.Acquire, renderDone);

        var handle = swapchain.Handle;
        var present = new Silk.NET.Vulkan.PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &renderDone,
            SwapchainCount = 1,
            PSwapchains = &handle,
            PImageIndices = &index,
        };
        var result = _gpu.KhrSwapchain.QueuePresent(_gpu.Queue, &present);
        NextSlot();
        if (result == Result.ErrorOutOfDateKhr || result == Result.ErrorSurfaceLostKhr)
        {
            if (result == Result.ErrorSurfaceLostKhr) swapchain.MarkSurfaceLost();
            else swapchain.MarkStale();
            return false;
        }
        if (result != Result.Success && result != Result.SuboptimalKhr)
            LastDiagnostic = $"present: {result}";
        return true;
    }

    /// <summary>Submit work recorded without a present (e.g. the surface is gone).</summary>
    public void SubmitPending()
    {
        if (!Ready) return;
        SubmitSlot();
        NextSlot();
    }

    public void WaitIdle() => _vk.DeviceWaitIdle(_dev);
}
