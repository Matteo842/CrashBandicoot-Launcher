using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace RecompOne.Runtime.Hle.Vulkan;

/// <summary>
/// Vulkan port of <see cref="GlCrt"/> (MattiasCRT, from crtemu.h in Mattias
/// Gustavsson's CRT View, MIT). Same passes, buffers and timing: the
/// persistence and ghost buffers at the native line count in half float, a
/// fixed 60 Hz persistence step, and the final pass at the exact output size.
/// Shaders: Shaders/crt*.vert/frag. Built on first use, desktop window only.
/// </summary>
internal sealed unsafe class VkCrt : IDisposable
{
    const double StepSeconds = 1.0 / 60.0;
    const int Cur = 0, StateA = 1, StateB = 2, Scratch = 3, Ghost = 4;
    const Format LowFormat = Format.R16G16B16A16Sfloat;

    [StructLayout(LayoutKind.Sequential)]
    struct CrtPush
    {
        public float Vec0X, Vec0Y, ResX, ResY, K;
        public int Mode;
        public float Time, Lines, FlipY, Pad0;
    }

    readonly VkBackend _be;
    readonly Vk _vk;
    readonly Device _dev;
    RenderPass _lowPass;
    DescriptorSetLayout _dsl;
    PipelineLayout _layout;
    DescriptorPool _pool;
    ShaderModule _vs, _fullVs, _downFs, _blurFs, _crtFs;
    Pipeline _downPipe, _blurPipe, _crtPipe;
    readonly Dictionary<(ulong, ulong, ulong), DescriptorSet> _sets = [];

    readonly VkTex?[] _tex = new VkTex?[5];
    readonly Framebuffer[] _fbo = new Framebuffer[5];
    int _lowW, _lowH, _lines = 240, _state = StateA;
    VkTex? _out;
    Framebuffer _outFbo;
    readonly Stopwatch _clock = Stopwatch.StartNew();
    double _lastSeconds, _stepDebt;

    /// <summary>The image the last <see cref="Render"/> wrote (ready to sample).</summary>
    public VkTex? Output => _out;
    /// <summary>Bumped whenever <see cref="Output"/> is replaced.</summary>
    public int OutputVersion { get; private set; }

    public VkCrt(VkBackend backend)
    {
        _be = backend;
        _vk = backend.Api;
        _dev = backend.Dev;
    }

    public void Init()
    {
        _lowPass = _be.CreateSampledPass(LowFormat);

        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        for (uint i = 0; i < 3; i++)
            bindings[i] = new DescriptorSetLayoutBinding
            {
                Binding = i,
                DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        var dslInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings,
        };
        DescriptorSetLayout dsl;
        VkGpu.Check(_vk.CreateDescriptorSetLayout(_dev, &dslInfo, null, &dsl), "vkCreateDescriptorSetLayout(crt)");
        _dsl = dsl;

        var range = new PushConstantRange(ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(CrtPush));
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &dsl,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range,
        };
        PipelineLayout layout;
        VkGpu.Check(_vk.CreatePipelineLayout(_dev, &layoutInfo, null, &layout), "vkCreatePipelineLayout(crt)");
        _layout = layout;
        _pool = CreatePool();

        _fullVs = _be.CreateShader(VkShaderCode.FullscreenVert);
        _vs = _be.CreateShader(VkShaderCode.CrtVert);
        _downFs = _be.CreateShader(VkShaderCode.CrtDownFrag);
        _blurFs = _be.CreateShader(VkShaderCode.CrtBlurFrag);
        _crtFs = _be.CreateShader(VkShaderCode.CrtFrag);
        _downPipe = CreatePipeline(_lowPass, _fullVs, _downFs);
        _blurPipe = CreatePipeline(_lowPass, _fullVs, _blurFs);
        _crtPipe = CreatePipeline(_be.TexturePass, _vs, _crtFs);
    }

    DescriptorPool CreatePool()
    {
        var size = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 3 * 64);
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 64,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };
        DescriptorPool pool;
        VkGpu.Check(_vk.CreateDescriptorPool(_dev, &info, null, &pool), "vkCreateDescriptorPool(crt)");
        return pool;
    }

    Pipeline CreatePipeline(RenderPass pass, ShaderModule vertex, ShaderModule fragment)
    {
        var entry = (byte*)Marshal.StringToHGlobalAnsi("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertex,
                PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragment,
                PName = entry,
            };
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
            };
            var attachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = false,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            return _be.CreateGraphicsPipeline(pass, _layout, stages, &vertexInput, &attachment);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entry);
        }
    }

    /// <summary>
    /// Filter <paramref name="frame"/> into an owned w×h image, top row first
    /// (shown by ImGui). Same steps as <see cref="GlCrt.Render"/>.
    /// </summary>
    public VkTex? Render(VkTex frame, int nativeH, float aspect, int width, int height)
    {
        _lines = Math.Clamp(nativeH, 120, 576);
        int lowH = _lines;
        int lowW = Math.Clamp((int)MathF.Round(lowH * aspect), 64, 2048);
        EnsureLow(lowW, lowH);
        EnsureOut(width, height);

        double now = _clock.Elapsed.TotalSeconds;
        _stepDebt = Math.Min(_stepDebt + Math.Max(0.0, now - _lastSeconds), StepSeconds * 4);
        _lastSeconds = now;

        // crtemu samples its backbuffer bilinearly: every tap uses the linear sampler.
        var push = new CrtPush { Vec0X = 1f / lowW, Vec0Y = 1f / lowH };
        Pass(_downPipe, Cur, frame, frame, frame, push);

        while (_stepDebt >= StepSeconds)
        {
            // Phosphor persistence: state = max(cur, blur(state) * 0.96).
            int next = _state == StateA ? StateB : StateA;
            Blur(_tex[_state]!, Scratch, 1f / lowW, 0f, 0, 0f, frame);
            Blur(_tex[Scratch]!, next, 0f, 1f / lowH, 1, 0.96f, frame);
            _state = next;
            _stepDebt -= StepSeconds;
        }
        // Ghost image: blur(max(cur, state * 0.32)).
        Blur(_tex[_state]!, Scratch, 1f / lowW, 0f, 2, 0.32f, frame);
        Blur(_tex[Scratch]!, Ghost, 0f, 1f / lowH, 0, 0f, frame);

        var crt = new CrtPush
        {
            ResX = width,
            ResY = height,
            // crtemu runs its clock at 1.5x; wrap hourly to keep float precision.
            Time = (float)(1.5 * (now % 3600.0)),
            Lines = _lines,
            FlipY = 1f,
        };
        Draw(_crtPipe, _be.TexturePass, _outFbo, width, height, frame, _tex[_state]!, _tex[Ghost]!, crt);
        _out!.Layout = ImageLayout.ShaderReadOnlyOptimal;
        return _out;
    }

    void Blur(VkTex source, int target, float dx, float dy, int mode, float k, VkTex unused)
    {
        var push = new CrtPush { Vec0X = dx, Vec0Y = dy, Mode = mode, K = k };
        // uCur is the freshly downscaled frame.
        Pass(_blurPipe, target, source, _tex[Cur]!, unused, push);
    }

    void Pass(Pipeline pipe, int target, VkTex t0, VkTex t1, VkTex t2, CrtPush push)
    {
        Draw(pipe, _lowPass, _fbo[target], _lowW, _lowH, t0, t1, t2, push);
        _tex[target]!.Layout = ImageLayout.ShaderReadOnlyOptimal;
    }

    void Draw(Pipeline pipe, RenderPass pass, Framebuffer fb, int w, int h, VkTex t0, VkTex t1, VkTex t2, CrtPush push)
    {
        var set = SetFor(t0, t1, t2);
        _be.BeginTexturePass(pass, fb, w, h);
        var cb = _be.Commands;
        _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipe);
        _vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        _vk.CmdPushConstants(cb, _layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(CrtPush), &push);
        _vk.CmdDraw(cb, 3, 1, 0, 0);
        _vk.CmdEndRenderPass(cb);
    }

    DescriptorSet SetFor(VkTex t0, VkTex t1, VkTex t2)
    {
        var key = (t0.View.Handle, t1.View.Handle, t2.View.Handle);
        if (_sets.TryGetValue(key, out var set)) return set;
        var dsl = _dsl;
        var alloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &dsl,
        };
        if (_vk.AllocateDescriptorSets(_dev, &alloc, &set) != Result.Success)
        {
            RetirePool();
            alloc.DescriptorPool = _pool;
            VkGpu.Check(_vk.AllocateDescriptorSets(_dev, &alloc, &set), "vkAllocateDescriptorSets(crt)");
        }
        var sampler = _be.LinearSampler;
        var infos = stackalloc DescriptorImageInfo[3];
        infos[0] = new DescriptorImageInfo(sampler, t0.View, ImageLayout.ShaderReadOnlyOptimal);
        infos[1] = new DescriptorImageInfo(sampler, t1.View, ImageLayout.ShaderReadOnlyOptimal);
        infos[2] = new DescriptorImageInfo(sampler, t2.View, ImageLayout.ShaderReadOnlyOptimal);
        var writes = stackalloc WriteDescriptorSet[3];
        for (uint i = 0; i < 3; i++)
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &infos[i],
            };
        _vk.UpdateDescriptorSets(_dev, 3, writes, 0, null);
        _sets[key] = set;
        return set;
    }

    /// <summary>Drop every cached set (an image was replaced); the old pool goes once the GPU is done.</summary>
    void RetirePool()
    {
        var old = _pool;
        _be.DeferUntilIdle(() => _vk.DestroyDescriptorPool(_dev, old, null));
        _sets.Clear();
        _pool = CreatePool();
    }

    void EnsureLow(int width, int height)
    {
        if (width == _lowW && height == _lowH) return;
        RetirePool();
        for (int i = 0; i < _tex.Length; i++)
        {
            DestroyLater(_tex[i], _fbo[i]);
            var tex = _be.CreateSampledTarget(width, height, LowFormat);
            // Fresh persistence starts black, never undefined memory.
            _be.ClearToBlack(tex);
            _tex[i] = tex;
            _fbo[i] = _be.CreateColorFramebuffer(_lowPass, tex);
        }
        _lowW = width;
        _lowH = height;
        _state = StateA;
        _stepDebt = 0;
    }

    void EnsureOut(int width, int height)
    {
        if (_out != null && _out.W == width && _out.H == height) return;
        RetirePool();
        DestroyLater(_out, _outFbo);
        _out = _be.CreateSampledTarget(width, height, Format.R8G8B8A8Unorm);
        _outFbo = _be.CreateColorFramebuffer(_be.TexturePass, _out);
        OutputVersion++;
    }

    /// <summary>The frame image was replaced: forget sets that point at it.</summary>
    public void ForgetFrame() => RetirePool();

    void DestroyLater(VkTex? tex, Framebuffer fb)
    {
        if (tex == null) return;
        var gpu = _be.Gpu;
        _be.DeferUntilIdle(() =>
        {
            if (fb.Handle != 0) _vk.DestroyFramebuffer(_dev, fb, null);
            tex.Destroy(gpu);
        });
    }

    public void Dispose()
    {
        var gpu = _be.Gpu;
        for (int i = 0; i < _tex.Length; i++)
        {
            if (_fbo[i].Handle != 0) _vk.DestroyFramebuffer(_dev, _fbo[i], null);
            _tex[i]?.Destroy(gpu);
            _tex[i] = null;
            _fbo[i] = default;
        }
        if (_outFbo.Handle != 0) _vk.DestroyFramebuffer(_dev, _outFbo, null);
        _out?.Destroy(gpu);
        _out = null;
        foreach (var p in new[] { _downPipe, _blurPipe, _crtPipe })
            if (p.Handle != 0) _vk.DestroyPipeline(_dev, p, null);
        foreach (var m in new[] { _vs, _fullVs, _downFs, _blurFs, _crtFs })
            if (m.Handle != 0) _vk.DestroyShaderModule(_dev, m, null);
        if (_pool.Handle != 0) _vk.DestroyDescriptorPool(_dev, _pool, null);
        if (_layout.Handle != 0) _vk.DestroyPipelineLayout(_dev, _layout, null);
        if (_dsl.Handle != 0) _vk.DestroyDescriptorSetLayout(_dev, _dsl, null);
        if (_lowPass.Handle != 0) _vk.DestroyRenderPass(_dev, _lowPass, null);
        _pool = default;
    }
}
