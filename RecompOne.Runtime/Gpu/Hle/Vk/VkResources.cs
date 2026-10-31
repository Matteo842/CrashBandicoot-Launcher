using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace RecompOne.Runtime.Hle.Vulkan;

/// <summary>Image + memory + view with its last recorded layout.</summary>
internal sealed unsafe class VkTex
{
    public Image Image;
    public DeviceMemory Memory;
    public ImageView View;
    public Format Format;
    public ImageAspectFlags Aspect;
    public int W, H;
    /// <summary>Layout after every command recorded so far (single queue, in order).</summary>
    public ImageLayout Layout = ImageLayout.Undefined;

    public static VkTex Create(VkGpu gpu, int w, int h, Format format, ImageUsageFlags usage, ImageAspectFlags aspect)
    {
        var vk = gpu.Vk;
        var tex = new VkTex { Format = format, Aspect = aspect, W = w, H = h };
        var info = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D((uint)w, (uint)h, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        Image image;
        VkGpu.Check(vk.CreateImage(gpu.Device, &info, null, &image), "vkCreateImage");
        tex.Image = image;

        MemoryRequirements req;
        vk.GetImageMemoryRequirements(gpu.Device, image, &req);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = gpu.FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        DeviceMemory memory;
        VkGpu.Check(vk.AllocateMemory(gpu.Device, &alloc, null, &memory), "vkAllocateMemory(image)");
        tex.Memory = memory;
        VkGpu.Check(vk.BindImageMemory(gpu.Device, image, memory, 0), "vkBindImageMemory");

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        ImageView view;
        VkGpu.Check(vk.CreateImageView(gpu.Device, &viewInfo, null, &view), "vkCreateImageView");
        tex.View = view;
        return tex;
    }

    public void Destroy(VkGpu gpu)
    {
        var vk = gpu.Vk;
        if (View.Handle != 0) vk.DestroyImageView(gpu.Device, View, null);
        if (Image.Handle != 0) vk.DestroyImage(gpu.Device, Image, null);
        if (Memory.Handle != 0) vk.FreeMemory(gpu.Device, Memory, null);
        View = default; Image = default; Memory = default;
    }
}

/// <summary>Persistently mapped host-visible buffer, bump-allocated per submission.</summary>
internal sealed unsafe class VkChunk
{
    public Buffer Buffer;
    public DeviceMemory Memory;
    public byte* Ptr;
    public ulong Size;
    public ulong Used;
    public bool NeedsInvalidate;

    public static VkChunk Create(VkGpu gpu, ulong size, BufferUsageFlags usage, bool readback = false)
    {
        var vk = gpu.Vk;
        var chunk = new VkChunk { Size = size };
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        Buffer buffer;
        VkGpu.Check(vk.CreateBuffer(gpu.Device, &info, null, &buffer), "vkCreateBuffer");
        chunk.Buffer = buffer;

        MemoryRequirements req;
        vk.GetBufferMemoryRequirements(gpu.Device, buffer, &req);
        uint type = readback
            ? gpu.FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit, MemoryPropertyFlags.HostCachedBit)
            : gpu.FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        chunk.NeedsInvalidate = (gpu.MemoryFlags(type) & MemoryPropertyFlags.HostCoherentBit) == 0;
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = type,
        };
        DeviceMemory memory;
        VkGpu.Check(vk.AllocateMemory(gpu.Device, &alloc, null, &memory), "vkAllocateMemory(buffer)");
        chunk.Memory = memory;
        VkGpu.Check(vk.BindBufferMemory(gpu.Device, buffer, memory, 0), "vkBindBufferMemory");
        void* ptr;
        VkGpu.Check(vk.MapMemory(gpu.Device, memory, 0, Vk.WholeSize, 0, &ptr), "vkMapMemory");
        chunk.Ptr = (byte*)ptr;
        return chunk;
    }

    public void Invalidate(VkGpu gpu)
    {
        if (!NeedsInvalidate) return;
        var range = new MappedMemoryRange
        {
            SType = StructureType.MappedMemoryRange,
            Memory = Memory,
            Offset = 0,
            Size = Vk.WholeSize,
        };
        gpu.Vk.InvalidateMappedMemoryRanges(gpu.Device, 1, &range);
    }

    public void Destroy(VkGpu gpu)
    {
        var vk = gpu.Vk;
        if (Memory.Handle != 0) vk.UnmapMemory(gpu.Device, Memory);
        if (Buffer.Handle != 0) vk.DestroyBuffer(gpu.Device, Buffer, null);
        if (Memory.Handle != 0) vk.FreeMemory(gpu.Device, Memory, null);
        Buffer = default; Memory = default; Ptr = null;
    }
}

/// <summary>A display framebuffer (optionally with 16:9 side margins), as GlDisplayRt.</summary>
internal sealed class VkDisplayRt
{
    public int X, Y, W, H;
    public int Margin;
    public VkTex Color = null!;
    public VkTex Depth = null!;
    public Framebuffer Framebuffer;
    public bool Dirty;
    public bool HasWideWorld;
    public long Stamp;
    public long LastDrawFrame;

    public int Wide1x => W + Margin * 2;
    public int TexW => Wide1x * GlVram.Scale;
    public int TexH => H * GlVram.Scale;

    public bool Covers(int cx0, int cy0, int cx1, int cy1)
        => cx0 <= X && cx1 >= X + W - 1 && cy0 <= Y && cy1 >= Y + H - 1;

    public bool Intersects(int rx, int ry, int rw, int rh)
        => rx < X + W && X < rx + rw && ry < Y + H && Y < ry + rh;
}
