using System.Numerics;
using System.Runtime.InteropServices;
using ImGuiNET;
using RecompOne.Runtime.Hle.Vulkan;
using Silk.NET.Input;
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace RecompOne.Runtime.Host.Window;

/// <summary>
/// ImGui renderer for the Vulkan game window, a C# port of imgui_impl_vulkan
/// (MIT, Omar Cornut) on top of <see cref="VkBackend"/>: vertices go into the
/// backend's per-frame memory and the UI is recorded into the backend's
/// command buffer, then drawn into the swapchain image by
/// <see cref="VkBackend.PresentOverlay"/>. ImGui texture ids are small numbers
/// mapped to descriptor sets (<see cref="Register"/>, <see cref="Upload"/>).
/// </summary>
sealed unsafe class VkImGuiController : ImGuiControllerBase
{
    sealed class Entry
    {
        public DescriptorSet Set;
        public ulong View;
        public bool Linear;
        /// <summary>Image owned by the controller (fonts, CPU uploads), else borrowed.</summary>
        public VkTex? Owned;
    }

    readonly VkBackend _be;
    readonly VkSwapchain _swapchain;
    readonly Vk _vk;
    readonly Device _dev;
    DescriptorSetLayout _dsl;
    PipelineLayout _layout;
    DescriptorPool _pool;
    ShaderModule _vs, _fs;
    Pipeline _pipeline;
    RenderPass _pipelinePass;
    readonly Dictionary<nint, Entry> _textures = [];
    nint _nextId = 1;
    nint _fontId;

    /// <summary>Window background behind the dock space, as the GL path's glClear.</summary>
    public ClearColorValue ClearColor { get; set; } = new(0.08f, 0.08f, 0.08f, 1f);

    public VkImGuiController(VkBackend backend, VkSwapchain swapchain, IView view, IInputContext input, Action? onConfigureIO = null)
        : base(view, input)
    {
        _be = backend;
        _swapchain = swapchain;
        _vk = backend.Api;
        _dev = backend.Dev;
        Start(onConfigureIO);
    }

    protected override void CreateDeviceResources()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var dslInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        DescriptorSetLayout dsl;
        VkGpu.Check(_vk.CreateDescriptorSetLayout(_dev, &dslInfo, null, &dsl), "vkCreateDescriptorSetLayout(imgui)");
        _dsl = dsl;

        var range = new PushConstantRange(ShaderStageFlags.VertexBit, 0, sizeof(float) * 4);
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &dsl,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &range,
        };
        PipelineLayout layout;
        VkGpu.Check(_vk.CreatePipelineLayout(_dev, &layoutInfo, null, &layout), "vkCreatePipelineLayout(imgui)");
        _layout = layout;

        var size = new DescriptorPoolSize(DescriptorType.CombinedImageSampler, 256);
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = 256,
            PoolSizeCount = 1,
            PPoolSizes = &size,
        };
        DescriptorPool pool;
        VkGpu.Check(_vk.CreateDescriptorPool(_dev, &poolInfo, null, &pool), "vkCreateDescriptorPool(imgui)");
        _pool = pool;

        _vs = _be.CreateShader(VkShaderCode.ImGuiVert);
        _fs = _be.CreateShader(VkShaderCode.ImGuiFrag);

        var io = ImGui.GetIO();
        io.Fonts.GetTexDataAsRGBA32(out IntPtr pixels, out int width, out int height, out _);
        _fontId = Upload(0, width, height, new ReadOnlySpan<byte>((void*)pixels, width * height * 4), linear: true);
        io.Fonts.SetTexID(_fontId);
    }

    /// <summary>
    /// Show <paramref name="tex"/> (owned by the caller, kept alive while shown)
    /// as ImGui texture <paramref name="id"/>; 0 allocates a new id. Call again
    /// whenever the image is replaced. Returns the id.
    /// </summary>
    public nint Register(nint id, VkTex tex, bool linear)
    {
        if (id != 0 && _textures.TryGetValue(id, out var entry))
            Release(entry);
        else
            id = _nextId++;
        _textures[id] = new Entry { Set = CreateSet(tex, linear), View = tex.View.Handle, Linear = linear };
        return id;
    }

    /// <summary>
    /// Copy tightly packed RGBA8 pixels into the controller-owned texture
    /// <paramref name="id"/> (0 creates one). Returns the id.
    /// </summary>
    public nint Upload(nint id, int width, int height, ReadOnlySpan<byte> rgba, bool linear)
    {
        if (id == 0 || !_textures.TryGetValue(id, out var entry) || entry.Owned is not { } tex
            || tex.W != width || tex.H != height || entry.Linear != linear)
        {
            if (id != 0 && _textures.TryGetValue(id, out var old)) Release(old);
            else if (id == 0) id = _nextId++;
            tex = _be.CreateUploadTexture(width, height);
            entry = new Entry { Owned = tex, View = tex.View.Handle, Linear = linear };
            _be.UploadTexture(tex, rgba);
            entry.Set = CreateSet(tex, linear);
            _textures[id] = entry;
            return id;
        }
        _be.UploadTexture(tex, rgba);
        return id;
    }

    DescriptorSet CreateSet(VkTex tex, bool linear)
    {
        var dsl = _dsl;
        var alloc = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _pool,
            DescriptorSetCount = 1,
            PSetLayouts = &dsl,
        };
        DescriptorSet set;
        VkGpu.Check(_vk.AllocateDescriptorSets(_dev, &alloc, &set), "vkAllocateDescriptorSets(imgui)");
        var info = new DescriptorImageInfo(linear ? _be.LinearSampler : _be.NearestSampler, tex.View, ImageLayout.ShaderReadOnlyOptimal);
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = &info,
        };
        _vk.UpdateDescriptorSets(_dev, 1, &write, 0, null);
        return set;
    }

    /// <summary>Free an entry's set (and owned image) once the GPU is done with this frame.</summary>
    void Release(Entry entry)
    {
        var set = entry.Set;
        var owned = entry.Owned;
        var pool = _pool;
        var gpu = _be.Gpu;
        _be.DeferUntilIdle(() =>
        {
            // After Dispose the pool (and every set in it) is already gone.
            if (set.Handle != 0 && _pool.Handle == pool.Handle) FreeSet(pool, set);
            owned?.Destroy(gpu);
        });
        entry.Set = default;
        entry.Owned = null;
    }

    void FreeSet(DescriptorPool pool, DescriptorSet set) => _vk.FreeDescriptorSets(_dev, pool, 1, &set);

    void EnsurePipeline(RenderPass pass)
    {
        if (_pipeline.Handle != 0 && _pipelinePass.Handle == pass.Handle) return;
        if (_pipeline.Handle != 0)
        {
            var old = _pipeline;
            _be.DeferUntilIdle(() => _vk.DestroyPipeline(_dev, old, null));
        }
        var entry = (byte*)Marshal.StringToHGlobalAnsi("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = _vs,
                PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = _fs,
                PName = entry,
            };
            var binding = new VertexInputBindingDescription(0, (uint)sizeof(ImDrawVert), VertexInputRate.Vertex);
            var attrs = stackalloc VertexInputAttributeDescription[3];
            attrs[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32Sfloat, 0);
            attrs[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32Sfloat, 8);
            attrs[2] = new VertexInputAttributeDescription(2, 0, Format.R8G8B8A8Unorm, 16);
            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 3,
                PVertexAttributeDescriptions = attrs,
            };
            var attachment = new PipelineColorBlendAttachmentState
            {
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            };
            _pipeline = _be.CreateGraphicsPipeline(pass, _layout, stages, &vertexInput, &attachment);
            _pipelinePass = pass;
        }
        finally
        {
            Marshal.FreeHGlobal((nint)entry);
        }
    }

    protected override void RenderDrawData(ImDrawDataPtr drawData)
    {
        // Vertices and indices first: nothing may be allocated inside the window pass.
        int vtxCount = drawData.TotalVtxCount, idxCount = drawData.TotalIdxCount;
        Silk.NET.Vulkan.Buffer vtxBuffer = default, idxBuffer = default;
        ulong vtxOffset = 0, idxOffset = 0;
        if (vtxCount > 0 && idxCount > 0)
        {
            ulong vtxBytes = (ulong)vtxCount * (ulong)sizeof(ImDrawVert);
            ulong idxBytes = (ulong)idxCount * sizeof(ushort);
            var (vb, vo, vp) = _be.AllocFrame(vtxBytes);
            var (ib, io, ip) = _be.AllocFrame(idxBytes, 4);
            (vtxBuffer, vtxOffset, idxBuffer, idxOffset) = (vb, vo, ib, io);
            var vdst = (byte*)vp;
            var idst = (byte*)ip;
            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                var list = drawData.CmdLists[n];
                long vlen = (long)list.VtxBuffer.Size * sizeof(ImDrawVert);
                long ilen = (long)list.IdxBuffer.Size * sizeof(ushort);
                System.Buffer.MemoryCopy((void*)list.VtxBuffer.Data, vdst, vlen, vlen);
                System.Buffer.MemoryCopy((void*)list.IdxBuffer.Data, idst, ilen, ilen);
                vdst += vlen;
                idst += ilen;
            }
        }

        _be.PresentOverlay(_swapchain, ClearColor, (cb, pass, width, height) =>
        {
            if (vtxBuffer.Handle == 0) return;
            float fbW = drawData.DisplaySize.X * drawData.FramebufferScale.X;
            float fbH = drawData.DisplaySize.Y * drawData.FramebufferScale.Y;
            if (fbW <= 0 || fbH <= 0) return;
            EnsurePipeline(pass);
            _vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
            var vb = vtxBuffer;
            var vo = vtxOffset;
            _vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &vo);
            _vk.CmdBindIndexBuffer(cb, idxBuffer, idxOffset, IndexType.Uint16);

            // ImGui coordinates are window points; the swapchain is in pixels.
            var viewport = new Viewport(0, 0, width, height, 0f, 1f);
            _vk.CmdSetViewport(cb, 0, 1, &viewport);
            var push = stackalloc float[4];
            push[0] = 2f / drawData.DisplaySize.X;
            push[1] = 2f / drawData.DisplaySize.Y;
            push[2] = -1f - drawData.DisplayPos.X * push[0];
            push[3] = -1f - drawData.DisplayPos.Y * push[1];
            _vk.CmdPushConstants(cb, _layout, ShaderStageFlags.VertexBit, 0, sizeof(float) * 4, push);

            Vector2 clipOff = drawData.DisplayPos;
            Vector2 clipScale = new(width / drawData.DisplaySize.X, height / drawData.DisplaySize.Y);
            nint boundTexture = -1;
            int globalVtx = 0, globalIdx = 0;
            for (int n = 0; n < drawData.CmdListsCount; n++)
            {
                var list = drawData.CmdLists[n];
                for (int i = 0; i < list.CmdBuffer.Size; i++)
                {
                    var cmd = list.CmdBuffer[i];
                    if (cmd.UserCallback != IntPtr.Zero) continue;
                    float x0 = Math.Max((cmd.ClipRect.X - clipOff.X) * clipScale.X, 0f);
                    float y0 = Math.Max((cmd.ClipRect.Y - clipOff.Y) * clipScale.Y, 0f);
                    float x1 = Math.Min((cmd.ClipRect.Z - clipOff.X) * clipScale.X, width);
                    float y1 = Math.Min((cmd.ClipRect.W - clipOff.Y) * clipScale.Y, height);
                    if (x1 <= x0 || y1 <= y0) continue;
                    if (!_textures.TryGetValue(cmd.TextureId, out var tex) || tex.Set.Handle == 0) continue;

                    var scissor = new Rect2D(new Offset2D((int)x0, (int)y0), new Extent2D((uint)(x1 - x0), (uint)(y1 - y0)));
                    _vk.CmdSetScissor(cb, 0, 1, &scissor);
                    if (cmd.TextureId != boundTexture)
                    {
                        var set = tex.Set;
                        _vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
                        boundTexture = cmd.TextureId;
                    }
                    _vk.CmdDrawIndexed(cb, cmd.ElemCount, 1, cmd.IdxOffset + (uint)globalIdx, (int)cmd.VtxOffset + globalVtx, 0);
                }
                globalIdx += list.IdxBuffer.Size;
                globalVtx += list.VtxBuffer.Size;
            }
        });
    }

    protected override void DisposeDeviceResources()
    {
        _be.WaitIdle();
        var gpu = _be.Gpu;
        foreach (var entry in _textures.Values)
            entry.Owned?.Destroy(gpu);
        _textures.Clear();
        if (_pipeline.Handle != 0) _vk.DestroyPipeline(_dev, _pipeline, null);
        if (_vs.Handle != 0) _vk.DestroyShaderModule(_dev, _vs, null);
        if (_fs.Handle != 0) _vk.DestroyShaderModule(_dev, _fs, null);
        if (_pool.Handle != 0) _vk.DestroyDescriptorPool(_dev, _pool, null);
        if (_layout.Handle != 0) _vk.DestroyPipelineLayout(_dev, _layout, null);
        if (_dsl.Handle != 0) _vk.DestroyDescriptorSetLayout(_dev, _dsl, null);
        _pipeline = default;
        _pool = default;
    }
}
