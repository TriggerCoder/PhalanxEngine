// Port of SpartanEngine RHI_Device::Memory* (Vulkan_Device.cpp, VMA there) @ 4c07baa, on RHI_Allocator.
// Buffers are linear, optimal-tiling images are optimal (separate blocks, so bufferImageGranularity never applies).
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal static unsafe partial class RHI_Device
{
    private static RHI_Allocator_Custom? s_allocator;

    public static RHI_Allocator Allocator => s_allocator ?? throw new InvalidOperationException("RHI_Device is not initialized");

    public static RHI_Allocation MemoryBufferCreate(out Buffer buffer, ulong size, BufferUsageFlags usage, RHI_Memory_Usage memory_usage,
                                                    ReadOnlySpan<byte> data_initial, string name)
    {
        // spartan: concurrent sharing across the graphics/compute/copy families so buffers need no ownership transfers
        uint* families = stackalloc uint[3];
        uint family_count = 0;
        foreach (RHI_Queue_Type type in stackalloc[] { RHI_Queue_Type.Graphics, RHI_Queue_Type.Compute, RHI_Queue_Type.Copy })
        {
            uint family = GetQueueIndex(type);
            bool seen = false;
            for (uint i = 0; i < family_count; i++) seen |= families[i] == family;
            if (!seen) families[family_count++] = family;
        }
        BufferCreateInfo info = new()
        {
            SType = StructureType.BufferCreateInfo, Size = size, Usage = usage,
            SharingMode = family_count > 1 ? SharingMode.Concurrent : SharingMode.Exclusive,
            QueueFamilyIndexCount = family_count > 1 ? family_count : 0,
            PQueueFamilyIndices = family_count > 1 ? families : null
        };
        Check(RHI_Context.Vk.CreateBuffer(RHI_Context.Device, in info, null, out buffer), "vkCreateBuffer");
        SetResourceName(buffer.Handle, RHI_Resource_Type.Buffer, name);

        MemoryDedicatedRequirements dedicated = new() { SType = StructureType.MemoryDedicatedRequirements };
        MemoryRequirements2 requirements = new() { SType = StructureType.MemoryRequirements2, PNext = &dedicated };
        BufferMemoryRequirementsInfo2 req_info = new() { SType = StructureType.BufferMemoryRequirementsInfo2, Buffer = buffer };
        RHI_Context.Vk.GetBufferMemoryRequirements2(RHI_Context.Device, &req_info, &requirements);

        RHI_Allocation allocation = Allocator.Allocate(requirements.MemoryRequirements, memory_usage, RHI_Memory_Kind.Linear,
                                                       dedicated.RequiresDedicatedAllocation, default, buffer, name);
        Check(RHI_Context.Vk.BindBufferMemory(RHI_Context.Device, buffer, allocation.Memory, allocation.Offset), "vkBindBufferMemory");

        if (!data_initial.IsEmpty)
        {
            if (allocation.MappedData == 0)
                throw new NotSupportedException($"{name}: initial data needs a mapped (upload) buffer until the staging pool lands");
            data_initial.CopyTo(new Span<byte>((void*)allocation.MappedData, (int)System.Math.Min((ulong)data_initial.Length, size)));
        }
        return allocation;
    }

    public static void MemoryBufferDestroy(Buffer buffer, RHI_Allocation allocation)
    {
        RHI_Context.Vk.DestroyBuffer(RHI_Context.Device, buffer, null);
        Allocator.Free(allocation);
    }

    // spartan: get_image_usage_flags
    public static ImageUsageFlags GetImageUsageFlags(RHI_Texture texture)
    {
        ImageUsageFlags flags = 0;
        if (texture.IsSrv()) flags |= ImageUsageFlags.SampledBit;
        if (texture.IsUav()) flags |= ImageUsageFlags.StorageBit;
        if (texture.IsVrs()) flags |= ImageUsageFlags.FragmentShadingRateAttachmentBitKhr;
        if (texture.IsDsv()) flags |= ImageUsageFlags.DepthStencilAttachmentBit;
        if (texture.IsRtv()) flags |= ImageUsageFlags.ColorAttachmentBit;
        // staged data or clear/blit participation needs the transfer bits
        if (texture.HasData() || (texture.GetFlags() & RHI_Texture_Flags.RHI_Texture_ClearBlit) != 0)
            flags |= ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit;
        return flags;
    }

    // spartan: get_format_tiling (optimal if the format supports the requested features optimally, else linear)
    private static ImageTiling GetFormatTiling(RHI_Texture texture)
    {
        FormatFeatureFlags required = 0;
        if (texture.IsSrv()) required |= FormatFeatureFlags.SampledImageBit;
        if (texture.IsUav()) required |= FormatFeatureFlags.StorageImageBit;
        if (texture.IsRtv()) required |= FormatFeatureFlags.ColorAttachmentBit;
        if (texture.IsDsv()) required |= FormatFeatureFlags.DepthStencilAttachmentBit;
        RHI_Context.Vk.GetPhysicalDeviceFormatProperties(RHI_Context.DevicePhysical, ToVulkan(texture.GetFormat()), out FormatProperties props);
        if ((props.OptimalTilingFeatures & required) == required) return ImageTiling.Optimal;
        if ((props.LinearTilingFeatures & required) == required) return ImageTiling.Linear;
        throw new NotSupportedException($"{texture.Name}: {texture.GetFormat()} supports neither optimal nor linear tiling for {required}");
    }

    public const ulong TextureDedicatedThreshold = 16UL * 1024 * 1024; // spartan: textures >= 16 MB are dedicated

    // spartan: MemoryTextureCreate(RHI_Texture*)
    public static RHI_Allocation MemoryTextureCreate(RHI_Texture texture, out Image image)
    {
        bool is_3d   = texture.Type == RHI_Texture_Type.Type3D;
        uint* families = stackalloc uint[2] { GetQueueIndex(RHI_Queue_Type.Graphics), GetQueueIndex(RHI_Queue_Type.Compute) };
        bool concurrent = (texture.GetFlags() & RHI_Texture_Flags.RHI_Texture_ConcurrentSharing) != 0 && families[0] != families[1];
        ImageCreateInfo info = new()
        {
            SType         = StructureType.ImageCreateInfo,
            ImageType     = is_3d ? ImageType.Type3D : ImageType.Type2D,
            Flags         = texture.Type == RHI_Texture_Type.TypeCube ? ImageCreateFlags.CreateCubeCompatibleBit : 0,
            Usage         = GetImageUsageFlags(texture),
            Extent        = new Extent3D(texture.GetWidth(), texture.GetHeight(), is_3d ? texture.GetDepth() : 1),
            MipLevels     = texture.GetMipCount(),
            ArrayLayers   = is_3d ? 1 : texture.GetDepth(),
            Format        = ToVulkan(texture.GetFormat()),
            Tiling        = GetFormatTiling(texture),
            InitialLayout = ToVulkan(texture.GetLayout(0)),
            Samples       = SampleCountFlags.Count1Bit,
            SharingMode   = concurrent ? SharingMode.Concurrent : SharingMode.Exclusive,
            QueueFamilyIndexCount = concurrent ? 2u : 0u,
            PQueueFamilyIndices   = concurrent ? families : null
        };

        Result support = RHI_Context.Vk.GetPhysicalDeviceImageFormatProperties(RHI_Context.DevicePhysical, info.Format, info.ImageType, info.Tiling, info.Usage, info.Flags, out _);
        if (support == Result.ErrorFormatNotSupported)
            throw new NotSupportedException($"{texture.Name}: the GPU doesn't support {texture.GetFormat()} with the specified properties");

        ulong texture_size = texture.CalculateMipChainSize(); // upstream 67a6e8b (was w*h*depth*mips*bytes)
        return MemoryTextureCreate(out image, in info, texture.Name, texture_size >= TextureDedicatedThreshold);
    }

    public static RHI_Allocation MemoryTextureCreate(out Image image, in ImageCreateInfo info, string name, bool dedicated_force = false)
    {
        Check(RHI_Context.Vk.CreateImage(RHI_Context.Device, in info, null, out image), "vkCreateImage");
        SetResourceName(image.Handle, RHI_Resource_Type.Image, name);

        MemoryDedicatedRequirements dedicated = new() { SType = StructureType.MemoryDedicatedRequirements };
        MemoryRequirements2 requirements = new() { SType = StructureType.MemoryRequirements2, PNext = &dedicated };
        ImageMemoryRequirementsInfo2 req_info = new() { SType = StructureType.ImageMemoryRequirementsInfo2, Image = image };
        RHI_Context.Vk.GetImageMemoryRequirements2(RHI_Context.Device, &req_info, &requirements);

        RHI_Memory_Kind kind = info.Tiling == ImageTiling.Optimal ? RHI_Memory_Kind.Optimal : RHI_Memory_Kind.Linear;
        bool dedicated_required = dedicated_force || dedicated.RequiresDedicatedAllocation || dedicated.PrefersDedicatedAllocation;
        RHI_Allocation allocation = Allocator.Allocate(requirements.MemoryRequirements, RHI_Memory_Usage.GpuOnly, kind,
                                                       dedicated_required, image, default, name);
        Check(RHI_Context.Vk.BindImageMemory(RHI_Context.Device, image, allocation.Memory, allocation.Offset), "vkBindImageMemory");
        return allocation;
    }

    // convenience used by the samples and tests
    public static RHI_Allocation MemoryTextureCreate2D(out Image image, uint width, uint height, RHI_Format format, ImageUsageFlags usage, string name)
    {
        ImageCreateInfo info = new()
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D, Format = ToVulkan(format),
            Extent = new Extent3D(width, height, 1), MipLevels = 1, ArrayLayers = 1, Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal, Usage = usage, SharingMode = SharingMode.Exclusive, InitialLayout = ImageLayout.Undefined
        };
        return MemoryTextureCreate(out image, in info, name);
    }

    public static void MemoryTextureDestroy(Image image, RHI_Allocation allocation)
    {
        RHI_Context.Vk.DestroyImage(RHI_Context.Device, image, null);
        Allocator.Free(allocation);
    }

    public static nint MemoryGetMappedDataFromBuffer(RHI_Allocation allocation) => allocation.MappedData;

    public static RHI_Allocator_Stats MemoryGetStats() => Allocator.GetStats();
    // spartan: MemoryGetAllocatedMb / MemoryGetAvailableMb / MemoryGetTotalMb (Vulkan_Device.cpp:3104-3160) on
    // VK_EXT_memory_budget. deviations: the loops stop at memoryHeapCount (spartan walks VK_MAX_MEMORY_HEAPS,
    // :3111/:3133), "available" is budget - usage (spartan returns the budget, :3138), see MemoryGetBudgetMb.
    // without the extension the figures come from our own allocator (other processes are invisible then).
    public static bool MemoryGetBudget(out ulong[] budget, out ulong[] usage, out bool[] device_local)
    {
        budget = usage = Array.Empty<ulong>(); device_local = Array.Empty<bool>();
        if (!RHI_Context.MemoryBudget || RHI_Context.Device.Handle == 0)
            return false;
        PhysicalDeviceMemoryBudgetPropertiesEXT b = new() { SType = StructureType.PhysicalDeviceMemoryBudgetPropertiesExt };
        PhysicalDeviceMemoryProperties2 props = new() { SType = StructureType.PhysicalDeviceMemoryProperties2, PNext = &b };
        RHI_Context.Vk.GetPhysicalDeviceMemoryProperties2(RHI_Context.DevicePhysical, &props);
        uint count = props.MemoryProperties.MemoryHeapCount;
        budget = new ulong[count]; usage = new ulong[count]; device_local = new bool[count];
        for (int i = 0; i < count; i++)
        {
            budget[i] = b.HeapBudget[i];
            usage[i]  = b.HeapUsage[i];
            device_local[i] = (props.MemoryProperties.MemoryHeaps[i].Flags & MemoryHeapFlags.DeviceLocalBit) != 0;
        }
        return true;
    }

    public static ulong MemoryGetAllocatedMb()
    {
        if (MemoryGetBudget(out _, out ulong[] usage, out bool[] local))
            return Sum(usage, local) / (1024 * 1024);
        return Allocator.GetStats().BytesReserved / (1024 * 1024);
    }

    public static ulong MemoryGetBudgetMb()
    {
        if (MemoryGetBudget(out ulong[] budget, out _, out bool[] local))
            return Sum(budget, local) / (1024 * 1024);
        return MemoryGetTotalMb();
    }

    public static ulong MemoryGetTotalMb() => (s_allocator?.GetHeapSize(device_local: true) ?? 0) / (1024 * 1024);

    public static ulong MemoryGetAvailableMb() { ulong budget = MemoryGetBudgetMb(), used = MemoryGetAllocatedMb(); return budget > used ? budget - used : 0; }

    // device local heaps only, unless there are none (then every heap)
    private static ulong Sum(ulong[] values, bool[] local)
    {
        bool any_local = local.Any(l => l);
        ulong sum = 0;
        for (int i = 0; i < values.Length; i++)
            if (local[i] || !any_local) sum += values[i];
        return sum;
    }
}
