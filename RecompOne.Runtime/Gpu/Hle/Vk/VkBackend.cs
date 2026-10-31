using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace RecompOne.Runtime.Hle.Vulkan;

/// <summary>
/// Vulkan HLE renderer: a port of <see cref="GlBackend"/> with the same VRAM
/// model (one upscaled 1024x512 image), the same display render targets with
/// 16:9 margins and depth, and the same batching. Differences:
/// <list type="bullet">
/// <item>All PS1 blend modes use one dual-source blend state (as GLES).</item>
/// <item>Vertices go into persistently mapped memory (no buffer orphaning).</item>
/// <item>Every pipeline is built up front and kept in an on-disk pipeline cache,
/// so walking into new geometry never compiles anything.</item>
/// <item>Feedback reads (mask test, primitives drawn straight into VRAM) sample
/// a copy instead of a texture barrier.</item>
/// </list>
/// Everything runs on the thread that owns the backend (the game core).
/// </summary>
public sealed unsafe partial class VkBackend : IGpuBackend, IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct VkVertex { public float X, Y; public uint Color; public int Clut, Texpage; public float U, V, InvZ; }

    [StructLayout(LayoutKind.Sequential)]
    struct PrimPush
    {
        public float PosBiasX, PosBiasY, FbInvX, FbInvY, WideCoreX, WideCoreY, SetMask, FilterStrength;
        public int TwAndX, TwAndY, TwOrX, TwOrY;
        public int CheckMask, WideMode, FilterMode, Dedither, ScaledDither, Scale, BlendPass, Pad0;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PresentPush
    {
        public float OriginX, OriginY, SizeX, SizeY, TexSizeX, TexSizeY;
        public int Mode, Pad0;
    }

    sealed class Slot
    {
        public CommandBuffer Cb;
        public Fence Fence;
        public Semaphore Acquire;
        public bool Submitted;
        public readonly List<VkChunk> Chunks = [];
        public readonly List<Action> Deferred = [];
        public ulong Bytes;
    }

    const int MaxVerts = 0x40000;
    const int SlotCount = 3;
    const ulong ChunkSize = 4ul << 20;
    // Split very long frames (level loads upload a lot) so staging memory stays bounded.
    const ulong SlotByteLimit = 96ul << 20;
    const int BlendOff = 0, BlendAdd = 1, BlendRevSub = 2;
    const int DepthStates = 5;

    readonly VkGpu _gpu;
    readonly Vk _vk;
    readonly Device _dev;

    VkTex _vram = null!;
    VkTex? _vramRead;
    VkTex _stage = null!;
    VkTex? _rtRead;
    VkTex? _scratch;
    VkChunk? _readback;
    readonly VkDisplayRt?[] _rts = new VkDisplayRt?[2];
    long _rtStamp;
    long _frame;
    int _frameFlushes, _frameWritebacks, _frameVertices, _frameCpuReads;

    RenderPass _vramPass, _rtPass;
    Framebuffer _vramFb;
    DescriptorSetLayout _dsl;
    PipelineLayout _primLayout, _presentLayout;
    DescriptorPool _pool;
    Sampler _nearest, _linear;
    PipelineCache _cache;
    ShaderModule _primVs, _primFs, _fullVs, _presentFs, _present24Fs;
    readonly Pipeline[] _vramPipes = new Pipeline[3];
    readonly Pipeline[] _rtPipes = new Pipeline[DepthStates * 3];
    RenderPass _presentPipesPass;
    Pipeline _presentPipe, _present24Pipe;
    CommandPool _cmdPool;

    readonly Slot[] _slots = new Slot[SlotCount];
    int _cur;
    readonly List<VkChunk> _freeChunks = [];
    readonly Dictionary<(ulong, ulong, bool), DescriptorSet> _sets = [];

    bool _passOpen;
    VkDisplayRt? _passRt;

    // Dirty rectangle (1x VRAM coordinates) of VRAM not yet copied to _vramRead.
    int _readDirtyX0 = 0, _readDirtyY0 = 0, _readDirtyX1 = -1, _readDirtyY1 = -1;

    readonly VkVertex[] _verts = new VkVertex[MaxVerts];
    int _count;
    bool _batchHasSemi;

    HleDrawEnv _env;
    VkDisplayRt? _kTarget;
    bool _kTransparent;
    bool _kSubtractBatch;
    int _kBlend, _kSetMask, _kCheckMask;
    WidePrimitiveMode _kWideMode;
    WidePrimitiveMode _pendingWideMode;
    int _kTwAndX, _kTwAndY, _kTwOrX, _kTwOrY;
    int _kClipX0, _kClipY0, _kClipX1, _kClipY1;
    bool _classifyValid;
    VkDisplayRt? _classifiedRt;

    public bool Ready { get; private set; }
    public string LastDiagnostic { get; private set; } = "ok";
    public int LastFrameFlushes { get; private set; }
    public int LastFrameWritebacks { get; private set; }
    public int LastFrameVertices { get; private set; }
    public int LastFrameCpuReads { get; private set; }
    public long DrawnFrames { get; private set; }

    /// <summary>Mobile GPU: skip the extra filter taps at 8x, as the GLES path does.</summary>
    public bool Mobile { get; set; }

    /// <summary>Where the driver's compiled pipelines are kept between runs.</summary>
    public string? PipelineCachePath { get; set; }

    public VkGpu Gpu => _gpu;

    public VkBackend(VkGpu gpu)
    {
        _gpu = gpu;
        _vk = gpu.Vk;
        _dev = gpu.Device;
    }

    public void Init()
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        CreateCommandResources();
        CreateSamplersAndLayouts();
        _vramPass = CreateTargetPass(withDepth: false);
        _rtPass = CreateTargetPass(withDepth: true);
        CreateShaderModules();
        LoadPipelineCache();
        CreatePrimPipelines();

        int w = VramShadow.Width * GlVram.Scale, h = VramShadow.Height * GlVram.Scale;
        _vram = VkTex.Create(_gpu, w, h, Format.R8G8B8A8Unorm,
            ImageUsageFlags.SampledBit | ImageUsageFlags.ColorAttachmentBit |
            ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        _stage = VkTex.Create(_gpu, VramShadow.Width, VramShadow.Height, Format.R8G8B8A8Unorm,
            ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit, ImageAspectFlags.ColorBit);
        _vramFb = CreateFramebuffer(_vramPass, _vram.View, default, w, h);

        BeginSlot(_slots[_cur]);
        ClearColorImage(_vram);
        RecompOne.Runtime.Host.FramePacing.WarmNativeWideJit();

        _kClipX1 = 1023; _kClipY1 = 511;
        Ready = true;
        double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Console.WriteLine($"[VkBackend] init {ms:0} ms on {_gpu.DeviceName} (Vulkan {_gpu.ApiVersionText}), depth {_gpu.DepthFormat}");
    }

    // ---------------------------------------------------------------- setup

    void CreateCommandResources()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _gpu.QueueFamily,
        };
        CommandPool pool;
        VkGpu.Check(_vk.CreateCommandPool(_dev, &poolInfo, null, &pool), "vkCreateCommandPool");
        _cmdPool = pool;
        for (int i = 0; i < SlotCount; i++)
        {
            var slot = new Slot();
            var alloc = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _cmdPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer cb;
            VkGpu.Check(_vk.AllocateCommandBuffers(_dev, &alloc, &cb), "vkAllocateCommandBuffers");
            slot.Cb = cb;
            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            Fence fence;
            VkGpu.Check(_vk.CreateFence(_dev, &fenceInfo, null, &fence), "vkCreateFence");
            slot.Fence = fence;
            var semInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            Semaphore sem;
            VkGpu.Check(_vk.CreateSemaphore(_dev, &semInfo, null, &sem), "vkCreateSemaphore");
            slot.Acquire = sem;
            _slots[i] = slot;
        }
    }

    void CreateSamplersAndLayouts()
    {
        _nearest = CreateSampler(Filter.Nearest);
        _linear = CreateSampler(Filter.Linear);

        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        for (uint i = 0; i < 2; i++)
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
            BindingCount = 2,
            PBindings = bindings,
        };
        DescriptorSetLayout dsl;
        VkGpu.Check(_vk.CreateDescriptorSetLayout(_dev, &dslInfo, null, &dsl), "vkCreateDescriptorSetLayout");
        _dsl = dsl;

        _primLayout = CreatePipelineLayout((uint)sizeof(PrimPush), ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit);
        _presentLayout = CreatePipelineLayout((uint)sizeof(PresentPush), ShaderStageFlags.FragmentBit);
        _pool = CreateDescriptorPool();
    }

    Sampler CreateSampler(Filter filter)
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 0f,
        };
        Sampler sampler;
        VkGpu.Check(_vk.CreateSampler(_dev, &info, null, &sampler), "vkCreateSampler");
        return sampler;
    }

    PipelineLayout CreatePipelineLayout(uint pushSize, ShaderStageFlags stages)
    {
        var range = new PushConstantRange(stages, 0, pushSize);
        var dsl = _dsl;
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &dsl,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range,
        };
        PipelineLayout layout;
        VkGpu.Check(_vk.CreatePipelineLayout(_dev, &info, null, &layout), "vkCreatePipelineLayout");
        return layout;
    }

    DescriptorPool CreateDescriptorPool()
    {
        var size = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 512);
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = 256,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };
        DescriptorPool pool;
        VkGpu.Check(_vk.CreateDescriptorPool(_dev, &info, null, &pool), "vkCreateDescriptorPool");
        return pool;
    }

    RenderPass CreateTargetPass(bool withDepth)
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = Format.R8G8B8A8Unorm,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.ColorAttachmentOptimal,
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
        };
        attachments[1] = new AttachmentDescription
        {
            Format = _gpu.DepthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Load,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.DepthStencilAttachmentOptimal,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var depthRef = new AttachmentReference(1, ImageLayout.DepthStencilAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
            PDepthStencilAttachment = withDepth ? &depthRef : null,
        };
        // Passes on the same target follow each other with only transfers in
        // between: order every earlier write before this pass, and this pass
        // before everything after it.
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
            AttachmentCount = withDepth ? 2u : 1u,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = deps,
        };
        RenderPass pass;
        VkGpu.Check(_vk.CreateRenderPass(_dev, &info, null, &pass), "vkCreateRenderPass");
        return pass;
    }

    Framebuffer CreateFramebuffer(RenderPass pass, ImageView color, ImageView depth, int w, int h)
    {
        var views = stackalloc ImageView[2];
        views[0] = color;
        views[1] = depth;
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = pass,
            AttachmentCount = depth.Handle != 0 ? 2u : 1u,
            PAttachments = views,
            Width = (uint)w,
            Height = (uint)h,
            Layers = 1,
        };
        Framebuffer fb;
        VkGpu.Check(_vk.CreateFramebuffer(_dev, &info, null, &fb), "vkCreateFramebuffer");
        return fb;
    }

    void CreateShaderModules()
    {
        _primVs = CreateShaderModule(VkShaderCode.PrimVert);
        _primFs = CreateShaderModule(VkShaderCode.PrimFrag);
        _fullVs = CreateShaderModule(VkShaderCode.FullscreenVert);
        _presentFs = CreateShaderModule(VkShaderCode.PresentFrag);
        _present24Fs = CreateShaderModule(VkShaderCode.Present24Frag);
    }

    ShaderModule CreateShaderModule(string base64)
    {
        byte[] code = Convert.FromBase64String(base64);
        fixed (byte* p = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode = (uint*)p,
            };
            ShaderModule module;
            VkGpu.Check(_vk.CreateShaderModule(_dev, &info, null, &module), "vkCreateShaderModule");
            return module;
        }
    }

    void LoadPipelineCache()
    {
        byte[] data = [];
        try
        {
            if (PipelineCachePath != null && File.Exists(PipelineCachePath))
                data = File.ReadAllBytes(PipelineCachePath);
        }
        catch { data = []; }

        fixed (byte* p = data)
        {
            var info = new PipelineCacheCreateInfo
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = (nuint)data.Length,
                PInitialData = data.Length > 0 ? p : null,
            };
            PipelineCache cache;
            // A cache from another driver version is rejected by the header
            // check inside the driver; retry empty if it errors outright.
            if (_vk.CreatePipelineCache(_dev, &info, null, &cache) != Result.Success)
            {
                info.InitialDataSize = 0;
                info.PInitialData = null;
                VkGpu.Check(_vk.CreatePipelineCache(_dev, &info, null, &cache), "vkCreatePipelineCache");
            }
            _cache = cache;
        }
    }

    void SavePipelineCache()
    {
        if (PipelineCachePath == null || _cache.Handle == 0) return;
        try
        {
            nuint size = 0;
            if (_vk.GetPipelineCacheData(_dev, _cache, &size, null) != Result.Success || size == 0) return;
            var data = new byte[(int)size];
            fixed (byte* p = data)
                if (_vk.GetPipelineCacheData(_dev, _cache, &size, p) != Result.Success) return;
            Directory.CreateDirectory(Path.GetDirectoryName(PipelineCachePath)!);
            File.WriteAllBytes(PipelineCachePath, data.AsSpan(0, (int)size).ToArray());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[VkBackend] pipeline cache save failed: {ex.Message}");
        }
    }

    void CreatePrimPipelines()
    {
        for (int blend = 0; blend < 3; blend++)
            _vramPipes[blend] = CreatePrimPipeline(_vramPass, depthAttachment: false, 0, blend);
        for (int depth = 0; depth < DepthStates; depth++)
            for (int blend = 0; blend < 3; blend++)
                _rtPipes[depth * 3 + blend] = CreatePrimPipeline(_rtPass, depthAttachment: true, depth, blend);
        SavePipelineCache();
    }

    Pipeline CreatePrimPipeline(RenderPass pass, bool depthAttachment, int depthState, int blend)
    {
        var entry = (byte*)Marshal.StringToHGlobalAnsi("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = _primVs,
                PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = _primFs,
                PName = entry,
            };

            var binding = new VertexInputBindingDescription(0, (uint)sizeof(VkVertex), VertexInputRate.Vertex);
            var attrs = stackalloc VertexInputAttributeDescription[6];
            attrs[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32Sfloat, 0);
            attrs[1] = new VertexInputAttributeDescription(1, 0, Format.R32Uint, 8);
            attrs[2] = new VertexInputAttributeDescription(2, 0, Format.R32Sint, 12);
            attrs[3] = new VertexInputAttributeDescription(3, 0, Format.R32Sint, 16);
            attrs[4] = new VertexInputAttributeDescription(4, 0, Format.R32G32Sfloat, 20);
            attrs[5] = new VertexInputAttributeDescription(5, 0, Format.R32Sfloat, 28);
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 6,
                PVertexAttributeDescriptions = attrs,
            };

            bool depthTest = depthState != 0;
            bool depthWrite = depthState is 1 or 3;
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = depthTest,
                DepthWriteEnable = depthWrite,
                DepthCompareOp = depthState >= 3 ? CompareOp.LessOrEqual : CompareOp.Less,
                MinDepthBounds = 0f,
                MaxDepthBounds = 1f,
            };

            // color = src * BlendColor.rgb + dst * BlendColor.a, alpha = src.a.
            var attachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = blend != BlendOff,
                SrcColorBlendFactor = BlendFactor.Src1Color,
                DstColorBlendFactor = BlendFactor.Src1Alpha,
                ColorBlendOp = blend == BlendRevSub ? BlendOp.ReverseSubtract : BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.Zero,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };

            return CreatePipeline(pass, _primLayout, stages, &vertexInput,
                depthAttachment ? &depthStencil : null, &attachment, depthBias: depthAttachment);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entry);
        }
    }

    Pipeline CreatePipeline(RenderPass pass, PipelineLayout layout, PipelineShaderStageCreateInfo* stages,
        PipelineVertexInputStateCreateInfo* vertexInput, PipelineDepthStencilStateCreateInfo* depthStencil,
        PipelineColorBlendAttachmentState* attachment, bool depthBias)
    {
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };
        var viewport = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };
        var raster = new PipelineRasterizationStateCreateInfo
        {
            SType = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
            DepthBiasEnable = depthBias,
            LineWidth = 1f,
        };
        var multisample = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        var blend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = attachment,
        };
        var dynamics = stackalloc DynamicState[3];
        dynamics[0] = DynamicState.Viewport;
        dynamics[1] = DynamicState.Scissor;
        dynamics[2] = DynamicState.DepthBias;
        var dynamic = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = depthBias ? 3u : 2u,
            PDynamicStates = dynamics,
        };
        var info = new GraphicsPipelineCreateInfo
        {
            SType = StructureType.GraphicsPipelineCreateInfo,
            StageCount = 2,
            PStages = stages,
            PVertexInputState = vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState = &viewport,
            PRasterizationState = &raster,
            PMultisampleState = &multisample,
            PDepthStencilState = depthStencil,
            PColorBlendState = &blend,
            PDynamicState = &dynamic,
            Layout = layout,
            RenderPass = pass,
            Subpass = 0,
        };
        Pipeline pipeline;
        VkGpu.Check(_vk.CreateGraphicsPipelines(_dev, _cache, 1, &info, null, &pipeline), "vkCreateGraphicsPipelines");
        return pipeline;
    }

    Pipeline CreatePresentPipeline(RenderPass pass, ShaderModule fragment)
    {
        var entry = (byte*)Marshal.StringToHGlobalAnsi("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = _fullVs,
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
            return CreatePipeline(pass, _presentLayout, stages, &vertexInput, null, &attachment, depthBias: false);
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entry);
        }
    }

    void EnsurePresentPipelines(RenderPass pass)
    {
        if (_presentPipesPass.Handle == pass.Handle && _presentPipe.Handle != 0) return;
        if (_presentPipe.Handle != 0)
        {
            var old0 = _presentPipe; var old1 = _present24Pipe;
            Defer(() => { _vk.DestroyPipeline(_dev, old0, null); _vk.DestroyPipeline(_dev, old1, null); });
        }
        _presentPipe = CreatePresentPipeline(pass, _presentFs);
        _present24Pipe = CreatePresentPipeline(pass, _present24Fs);
        _presentPipesPass = pass;
        SavePipelineCache();
    }

    // ------------------------------------------------------------ submission

    CommandBuffer Cb => _slots[_cur].Cb;

    void BeginSlot(Slot slot)
    {
        _vk.ResetCommandBuffer(slot.Cb, 0);
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        VkGpu.Check(_vk.BeginCommandBuffer(slot.Cb, &begin), "vkBeginCommandBuffer");
    }

    void SubmitSlot(Semaphore wait = default, Semaphore signal = default)
    {
        EndPass();
        var slot = _slots[_cur];
        VkGpu.Check(_vk.EndCommandBuffer(slot.Cb), "vkEndCommandBuffer");
        var cb = slot.Cb;
        var stage = PipelineStageFlags.ColorAttachmentOutputBit;
        var info = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cb,
            WaitSemaphoreCount = wait.Handle != 0 ? 1u : 0u,
            PWaitSemaphores = &wait,
            PWaitDstStageMask = &stage,
            SignalSemaphoreCount = signal.Handle != 0 ? 1u : 0u,
            PSignalSemaphores = &signal,
        };
        var result = _vk.QueueSubmit(_gpu.Queue, 1, &info, slot.Fence);
        if (result != Result.Success)
        {
            LastDiagnostic = $"submit: {result}";
            throw new InvalidOperationException($"vkQueueSubmit failed ({result}).");
        }
        slot.Submitted = true;
    }

    void NextSlot()
    {
        _cur = (_cur + 1) % SlotCount;
        var slot = _slots[_cur];
        WaitSlot(slot);
        BeginSlot(slot);
    }

    void WaitSlot(Slot slot)
    {
        if (slot.Submitted)
        {
            var fence = slot.Fence;
            VkGpu.Check(_vk.WaitForFences(_dev, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
            _vk.ResetFences(_dev, 1, &fence);
            slot.Submitted = false;
        }
        foreach (var action in slot.Deferred) action();
        slot.Deferred.Clear();
        foreach (var chunk in slot.Chunks)
        {
            chunk.Used = 0;
            if (chunk.Size == ChunkSize) _freeChunks.Add(chunk);
            else chunk.Destroy(_gpu);
        }
        slot.Chunks.Clear();
        slot.Bytes = 0;
    }

    /// <summary>Submit everything recorded so far and wait for the GPU (CPU reads).</summary>
    void SubmitAndWait()
    {
        int submitted = _cur;
        SubmitSlot();
        NextSlot();
        // NextSlot only waited for the slot it reuses; wait for this one too.
        var slot = _slots[submitted];
        if (slot.Submitted)
        {
            var fence = slot.Fence;
            VkGpu.Check(_vk.WaitForFences(_dev, 1, &fence, true, ulong.MaxValue), "vkWaitForFences");
        }
    }

    /// <summary>Run <paramref name="action"/> once the GPU is done with everything recorded so far.</summary>
    void Defer(Action action) => _slots[_cur].Deferred.Add(action);

    (Buffer buffer, ulong offset, nint ptr) Alloc(ulong size, ulong align = 16)
    {
        var slot = _slots[_cur];
        if (slot.Bytes + size > SlotByteLimit && slot.Bytes > 0)
        {
            SubmitSlot();
            NextSlot();
            slot = _slots[_cur];
        }
        VkChunk? chunk = slot.Chunks.Count > 0 ? slot.Chunks[^1] : null;
        ulong offset = chunk == null ? 0 : (chunk.Used + align - 1) & ~(align - 1);
        if (chunk == null || offset + size > chunk.Size)
        {
            if (size <= ChunkSize && _freeChunks.Count > 0)
            {
                chunk = _freeChunks[^1];
                _freeChunks.RemoveAt(_freeChunks.Count - 1);
            }
            else
                chunk = VkChunk.Create(_gpu, Math.Max(size, ChunkSize),
                    BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit | BufferUsageFlags.TransferSrcBit);
            slot.Chunks.Add(chunk);
            offset = 0;
        }
        chunk.Used = offset + size;
        slot.Bytes += size;
        return (chunk.Buffer, offset, (nint)(chunk.Ptr + offset));
    }

    // --------------------------------------------------------------- barriers

    void Barrier(VkTex tex, ImageLayout layout)
    {
        var barrier = MakeBarrier(tex, layout);
        _vk.CmdPipelineBarrier(Cb, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 1, &barrier);
        tex.Layout = layout;
    }

    void Barrier(VkTex a, ImageLayout layoutA, VkTex b, ImageLayout layoutB)
    {
        var barriers = stackalloc ImageMemoryBarrier[2];
        barriers[0] = MakeBarrier(a, layoutA);
        barriers[1] = MakeBarrier(b, layoutB);
        _vk.CmdPipelineBarrier(Cb, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.AllCommandsBit,
            0, 0, null, 0, null, 2, barriers);
        a.Layout = layoutA;
        b.Layout = layoutB;
    }

    static ImageMemoryBarrier MakeBarrier(VkTex tex, ImageLayout layout) => new()
    {
        SType = StructureType.ImageMemoryBarrier,
        SrcAccessMask = AccessFlags.MemoryWriteBit,
        DstAccessMask = AccessFlags.MemoryReadBit | AccessFlags.MemoryWriteBit,
        OldLayout = tex.Layout,
        NewLayout = layout,
        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        Image = tex.Image,
        SubresourceRange = new ImageSubresourceRange(tex.Aspect, 0, 1, 0, 1),
    };

    /// <summary>Barrier only on a layout change (no write since the last barrier).</summary>
    void EnsureLayout(VkTex tex, ImageLayout layout)
    {
        if (tex.Layout != layout) Barrier(tex, layout);
    }

    void ClearColorImage(VkTex tex)
    {
        EndPass();
        Barrier(tex, ImageLayout.TransferDstOptimal);
        var color = new ClearColorValue(0f, 0f, 0f, 0f);
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        _vk.CmdClearColorImage(Cb, tex.Image, ImageLayout.TransferDstOptimal, &color, 1, &range);
    }

    // ----------------------------------------------------------------- passes

    void BeginPass(VkDisplayRt? rt)
    {
        if (_passOpen && _passRt == rt) return;
        EndPass();
        Framebuffer fb;
        RenderPass pass;
        int w, h;
        if (rt != null)
        {
            EnsureLayout(_vram, ImageLayout.ShaderReadOnlyOptimal);
            if (_rtRead != null) EnsureLayout(_rtRead, ImageLayout.ShaderReadOnlyOptimal);
            EnsureLayout(rt.Color, ImageLayout.ColorAttachmentOptimal);
            fb = rt.Framebuffer; pass = _rtPass; w = rt.TexW; h = rt.TexH;
        }
        else
        {
            // Only VRAM-target draws sample the read copy (Flush creates it).
            if (_vramRead != null) EnsureLayout(_vramRead, ImageLayout.ShaderReadOnlyOptimal);
            EnsureLayout(_vram, ImageLayout.ColorAttachmentOptimal);
            fb = _vramFb; pass = _vramPass; w = _vram.W; h = _vram.H;
        }
        var info = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = pass,
            Framebuffer = fb,
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)w, (uint)h)),
        };
        _vk.CmdBeginRenderPass(Cb, &info, SubpassContents.Inline);
        _passOpen = true;
        _passRt = rt;
    }

    void EndPass()
    {
        if (!_passOpen) return;
        _vk.CmdEndRenderPass(Cb);
        _passOpen = false;
        _passRt = null;
    }

    /// <summary>Clear part of the open pass's attachments (pixel rectangle of the target).</summary>
    void ClearInPass(int x, int y, int w, int h, int targetW, int targetH, bool color, float r, float g, float b, float a, bool depth)
    {
        int x0 = Math.Clamp(x, 0, targetW), y0 = Math.Clamp(y, 0, targetH);
        int x1 = Math.Clamp(x + w, 0, targetW), y1 = Math.Clamp(y + h, 0, targetH);
        if (x1 <= x0 || y1 <= y0) return;
        var attachments = stackalloc ClearAttachment[2];
        uint n = 0;
        if (color)
        {
            attachments[n].AspectMask = ImageAspectFlags.ColorBit;
            attachments[n].ColorAttachment = 0;
            attachments[n].ClearValue = new ClearValue(new ClearColorValue(r, g, b, a));
            n++;
        }
        if (depth)
        {
            attachments[n].AspectMask = ImageAspectFlags.DepthBit;
            attachments[n].ClearValue = new ClearValue(depthStencil: new ClearDepthStencilValue(1f, 0));
            n++;
        }
        var rect = new ClearRect(new Rect2D(new Offset2D(x0, y0), new Extent2D((uint)(x1 - x0), (uint)(y1 - y0))), 0, 1);
        _vk.CmdClearAttachments(Cb, n, attachments, 1, &rect);
    }

    // ----------------------------------------------------------- descriptors

    DescriptorSet SetFor(VkTex a, VkTex b, bool present = false)
    {
        var key = (a.View.Handle, b.View.Handle, present);
        if (_sets.TryGetValue(key, out var set)) return set;

        var layout = _dsl;
        var alloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        if (_vk.AllocateDescriptorSets(_dev, &alloc, &set) != Result.Success)
        {
            // Pool exhausted: retire it whole once the GPU is done with it.
            var old = _pool;
            Defer(() => _vk.DestroyDescriptorPool(_dev, old, null));
            _sets.Clear();
            _pool = CreateDescriptorPool();
            alloc.DescriptorPool = _pool;
            VkGpu.Check(_vk.AllocateDescriptorSets(_dev, &alloc, &set), "vkAllocateDescriptorSets");
        }

        var infos = stackalloc DescriptorImageInfo[2];
        infos[0] = new DescriptorImageInfo(_nearest, a.View, ImageLayout.ShaderReadOnlyOptimal);
        infos[1] = new DescriptorImageInfo(present ? _linear : _nearest, b.View, ImageLayout.ShaderReadOnlyOptimal);
        var writes = stackalloc WriteDescriptorSet[2];
        for (uint i = 0; i < 2; i++)
            writes[i] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = set,
                DstBinding = i,
                DescriptorCount = 1,
                DescriptorType = DescriptorType.CombinedImageSampler,
                PImageInfo = &infos[i],
            };
        _vk.UpdateDescriptorSets(_dev, 2, writes, 0, null);
        _sets[key] = set;
        return set;
    }

    /// <summary>Drop cached sets that reference <paramref name="tex"/> (it is being destroyed).</summary>
    void ForgetSets(VkTex tex)
    {
        ulong view = tex.View.Handle;
        List<DescriptorSet>? dead = null;
        foreach (var (key, set) in _sets)
            if (key.Item1 == view || key.Item2 == view)
                (dead ??= []).Add(set);
        if (dead == null) return;
        foreach (var (key, _) in _sets.Where(kv => kv.Key.Item1 == view || kv.Key.Item2 == view).ToList())
            _sets.Remove(key);
        var pool = _pool;
        var sets = dead.ToArray();
        Defer(() =>
        {
            if (_pool.Handle != pool.Handle) return; // pool already retired
            fixed (DescriptorSet* p = sets)
                _vk.FreeDescriptorSets(_dev, pool, (uint)sets.Length, p);
        });
    }

    public void Dispose()
    {
        if (_dev.Handle == 0) return;
        _vk.DeviceWaitIdle(_dev);
        foreach (var slot in _slots)
            if (slot != null)
            {
                slot.Submitted = false;
                WaitSlot(slot);
            }
        SavePipelineCache();
        foreach (var chunk in _freeChunks) chunk.Destroy(_gpu);
        _freeChunks.Clear();
        _readback?.Destroy(_gpu);
        DisposeDesktop();
        foreach (var rt in _rts) if (rt != null) DestroyRtNow(rt);
        _vram?.Destroy(_gpu);
        _vramRead?.Destroy(_gpu);
        _stage?.Destroy(_gpu);
        _rtRead?.Destroy(_gpu);
        _scratch?.Destroy(_gpu);
        if (_vramFb.Handle != 0) _vk.DestroyFramebuffer(_dev, _vramFb, null);
        foreach (var p in _vramPipes) if (p.Handle != 0) _vk.DestroyPipeline(_dev, p, null);
        foreach (var p in _rtPipes) if (p.Handle != 0) _vk.DestroyPipeline(_dev, p, null);
        if (_presentPipe.Handle != 0) _vk.DestroyPipeline(_dev, _presentPipe, null);
        if (_present24Pipe.Handle != 0) _vk.DestroyPipeline(_dev, _present24Pipe, null);
        foreach (var m in new[] { _primVs, _primFs, _fullVs, _presentFs, _present24Fs })
            if (m.Handle != 0) _vk.DestroyShaderModule(_dev, m, null);
        if (_cache.Handle != 0) _vk.DestroyPipelineCache(_dev, _cache, null);
        if (_vramPass.Handle != 0) _vk.DestroyRenderPass(_dev, _vramPass, null);
        if (_rtPass.Handle != 0) _vk.DestroyRenderPass(_dev, _rtPass, null);
        if (_pool.Handle != 0) _vk.DestroyDescriptorPool(_dev, _pool, null);
        if (_primLayout.Handle != 0) _vk.DestroyPipelineLayout(_dev, _primLayout, null);
        if (_presentLayout.Handle != 0) _vk.DestroyPipelineLayout(_dev, _presentLayout, null);
        if (_dsl.Handle != 0) _vk.DestroyDescriptorSetLayout(_dev, _dsl, null);
        if (_nearest.Handle != 0) _vk.DestroySampler(_dev, _nearest, null);
        if (_linear.Handle != 0) _vk.DestroySampler(_dev, _linear, null);
        foreach (var slot in _slots)
            if (slot != null)
            {
                _vk.DestroyFence(_dev, slot.Fence, null);
                _vk.DestroySemaphore(_dev, slot.Acquire, null);
            }
        if (_cmdPool.Handle != 0) _vk.DestroyCommandPool(_dev, _cmdPool, null);
        _cmdPool = default;
        Ready = false;
    }
}
