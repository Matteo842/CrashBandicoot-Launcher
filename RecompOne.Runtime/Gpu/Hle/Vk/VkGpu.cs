using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace RecompOne.Runtime.Hle.Vulkan;

/// <summary>
/// Vulkan instance, physical device, logical device and the one graphics +
/// present queue the HLE renderer uses. The host creates the window surface
/// between <see cref="CreateInstance"/> and <see cref="CreateDevice"/>.
/// </summary>
public sealed unsafe class VkGpu : IDisposable
{
    public Vk Vk { get; }
    public Instance Instance { get; private set; }
    public PhysicalDevice Physical { get; private set; }
    public Device Device { get; private set; }
    public Queue Queue { get; private set; }
    public uint QueueFamily { get; private set; }
    public KhrSurface KhrSurface { get; private set; } = null!;
    public KhrSwapchain KhrSwapchain { get; private set; } = null!;
    public PhysicalDeviceMemoryProperties MemoryProperties { get; private set; }
    public Format DepthFormat { get; private set; }
    public string DeviceName { get; private set; } = "";
    public string ApiVersionText { get; private set; } = "";
    public string DriverVersionText { get; private set; } = "";
    public uint VendorId { get; private set; }

    VkGpu(Vk vk) => Vk = vk;

    /// <summary>
    /// Load the Vulkan loader and create an instance with the given surface
    /// extensions (VK_KHR_surface plus the platform one). Throws when the
    /// device has no Vulkan driver.
    /// </summary>
    public static VkGpu CreateInstance(IReadOnlyList<string> extensions, string appName)
    {
        var vk = Vk.GetApi();
        var gpu = new VkGpu(vk);
        nint name = SilkMarshal.StringToPtr(appName);
        nint exts = SilkMarshal.StringArrayToPtr(extensions.ToArray());
        try
        {
            var app = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = (byte*)name,
                ApplicationVersion = new Version32(1, 0, 0),
                PEngineName = (byte*)name,
                EngineVersion = new Version32(1, 0, 0),
                ApiVersion = Vk.Version10,
            };
            var info = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &app,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)exts,
            };
            Instance instance;
            Check(vk.CreateInstance(&info, null, &instance), "vkCreateInstance");
            gpu.Instance = instance;
        }
        finally
        {
            SilkMarshal.Free(exts);
            SilkMarshal.Free(name);
        }

        if (!vk.TryGetInstanceExtension(gpu.Instance, out KhrSurface surface))
            throw new InvalidOperationException("VK_KHR_surface is not available.");
        gpu.KhrSurface = surface;
        return gpu;
    }

    /// <summary>Pick a GPU that can draw and present to <paramref name="surface"/>, then create the device.</summary>
    public void CreateDevice(SurfaceKHR surface)
    {
        uint count = 0;
        Check(Vk.EnumeratePhysicalDevices(Instance, &count, null), "vkEnumeratePhysicalDevices");
        if (count == 0) throw new InvalidOperationException("No Vulkan GPU found.");
        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* p = devices)
            Check(Vk.EnumeratePhysicalDevices(Instance, &count, p), "vkEnumeratePhysicalDevices");

        string? reject = null;
        foreach (var candidate in devices)
        {
            if (!TryQueueFamily(candidate, surface, out uint family)) { reject = "no graphics+present queue"; continue; }
            PhysicalDeviceFeatures features;
            Vk.GetPhysicalDeviceFeatures(candidate, &features);
            // Every PS1 blend mode (and the opaque texels of a semi-transparent
            // primitive) is resolved by one dual-source blend state.
            if (!features.DualSrcBlend) { reject = "dualSrcBlend not supported"; continue; }
            if (!HasDeviceExtension(candidate, KhrSwapchain.ExtensionName)) { reject = "no VK_KHR_swapchain"; continue; }
            // Laptops list the integrated GPU too: keep looking for a discrete one.
            PhysicalDeviceProperties candidateProps;
            Vk.GetPhysicalDeviceProperties(candidate, &candidateProps);
            bool discrete = candidateProps.DeviceType == PhysicalDeviceType.DiscreteGpu;
            if (Physical.Handle != 0 && !discrete) continue;
            Physical = candidate;
            QueueFamily = family;
            if (discrete) break;
        }
        if (Physical.Handle == 0)
            throw new InvalidOperationException($"No usable Vulkan GPU ({reject}).");

        PhysicalDeviceProperties props;
        Vk.GetPhysicalDeviceProperties(Physical, &props);
        DeviceName = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "?";
        VendorId = props.VendorID;
        ApiVersionText = $"{props.ApiVersion >> 22}.{(props.ApiVersion >> 12) & 0x3FF}.{props.ApiVersion & 0xFFF}";
        DriverVersionText = $"0x{props.DriverVersion:X}";
        PhysicalDeviceMemoryProperties mem;
        Vk.GetPhysicalDeviceMemoryProperties(Physical, &mem);
        MemoryProperties = mem;
        DepthFormat = PickDepthFormat();

        float priority = 1f;
        var queueInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = QueueFamily,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };
        var enabled = new PhysicalDeviceFeatures { DualSrcBlend = true };
        nint exts = SilkMarshal.StringArrayToPtr(new[] { KhrSwapchain.ExtensionName });
        try
        {
            var info = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueInfo,
                EnabledExtensionCount = 1,
                PpEnabledExtensionNames = (byte**)exts,
                PEnabledFeatures = &enabled,
            };
            Device device;
            Check(Vk.CreateDevice(Physical, &info, null, &device), "vkCreateDevice");
            Device = device;
        }
        finally
        {
            SilkMarshal.Free(exts);
        }

        Queue queue;
        Vk.GetDeviceQueue(Device, QueueFamily, 0, &queue);
        Queue = queue;
        if (!Vk.TryGetDeviceExtension(Instance, Device, out KhrSwapchain swapchain))
            throw new InvalidOperationException("VK_KHR_swapchain could not be loaded.");
        KhrSwapchain = swapchain;
    }

    bool TryQueueFamily(PhysicalDevice device, SurfaceKHR surface, out uint family)
    {
        uint count = 0;
        Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, null);
        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* p = families)
            Vk.GetPhysicalDeviceQueueFamilyProperties(device, &count, p);
        for (uint i = 0; i < count; i++)
        {
            if ((families[i].QueueFlags & QueueFlags.GraphicsBit) == 0) continue;
            Bool32 present = false;
            KhrSurface.GetPhysicalDeviceSurfaceSupport(device, i, surface, &present);
            if (!present) continue;
            family = i;
            return true;
        }
        family = 0;
        return false;
    }

    bool HasDeviceExtension(PhysicalDevice device, string name)
    {
        uint count = 0;
        Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, null);
        var props = new ExtensionProperties[count];
        fixed (ExtensionProperties* p = props)
            Vk.EnumerateDeviceExtensionProperties(device, (byte*)null, &count, p);
        foreach (var prop in props)
            if (SilkMarshal.PtrToString((nint)prop.ExtensionName) == name)
                return true;
        return false;
    }

    Format PickDepthFormat()
    {
        // GL uses DEPTH_COMPONENT24. Vulkan guarantees X8_D24 or D32_SFLOAT.
        foreach (var format in new[] { Format.X8D24UnormPack32, Format.D32Sfloat, Format.D24UnormS8Uint, Format.D16Unorm })
        {
            FormatProperties fp;
            Vk.GetPhysicalDeviceFormatProperties(Physical, format, &fp);
            if ((fp.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
                return format;
        }
        return Format.D16Unorm;
    }

    public uint FindMemoryType(uint typeBits, MemoryPropertyFlags required, MemoryPropertyFlags preferred = 0)
    {
        var mem = MemoryProperties;
        if (preferred != 0)
            for (int i = 0; i < mem.MemoryTypeCount; i++)
                if ((typeBits & (1u << i)) != 0 && (mem.MemoryTypes[i].PropertyFlags & (required | preferred)) == (required | preferred))
                    return (uint)i;
        for (int i = 0; i < mem.MemoryTypeCount; i++)
            if ((typeBits & (1u << i)) != 0 && (mem.MemoryTypes[i].PropertyFlags & required) == required)
                return (uint)i;
        throw new InvalidOperationException($"No Vulkan memory type for {required}.");
    }

    public MemoryPropertyFlags MemoryFlags(uint type) => MemoryProperties.MemoryTypes[(int)type].PropertyFlags;

    public static void Check(Result result, string what)
    {
        if (result != Result.Success)
            throw new InvalidOperationException($"{what} failed ({result}).");
    }

    public void Dispose()
    {
        if (Device.Handle != 0)
        {
            Vk.DeviceWaitIdle(Device);
            Vk.DestroyDevice(Device, null);
            Device = default;
        }
        if (Instance.Handle != 0)
        {
            Vk.DestroyInstance(Instance, null);
            Instance = default;
        }
    }
}
