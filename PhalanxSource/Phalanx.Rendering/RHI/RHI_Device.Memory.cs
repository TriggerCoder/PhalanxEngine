// Port of SpartanEngine RHI_Device::Memory* (Vulkan_Device.cpp, VMA there) @ 4c07baa (step 2), on RHI_Allocator.
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
        BufferCreateInfo info = new() { SType = StructureType.BufferCreateInfo, Size = size, Usage = usage, SharingMode = SharingMode.Exclusive };
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

    public static RHI_Allocation MemoryTextureCreate(out Image image, in ImageCreateInfo info, string name)
    {
        Check(RHI_Context.Vk.CreateImage(RHI_Context.Device, in info, null, out image), "vkCreateImage");
        SetResourceName(image.Handle, RHI_Resource_Type.Image, name);

        MemoryDedicatedRequirements dedicated = new() { SType = StructureType.MemoryDedicatedRequirements };
        MemoryRequirements2 requirements = new() { SType = StructureType.MemoryRequirements2, PNext = &dedicated };
        ImageMemoryRequirementsInfo2 req_info = new() { SType = StructureType.ImageMemoryRequirementsInfo2, Image = image };
        RHI_Context.Vk.GetImageMemoryRequirements2(RHI_Context.Device, &req_info, &requirements);

        RHI_Memory_Kind kind = info.Tiling == ImageTiling.Optimal ? RHI_Memory_Kind.Optimal : RHI_Memory_Kind.Linear;
        bool dedicated_required = dedicated.RequiresDedicatedAllocation || dedicated.PrefersDedicatedAllocation;
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
    public static ulong MemoryGetAllocatedMb() => Allocator.GetStats().BytesReserved / (1024 * 1024);
    public static ulong MemoryGetTotalMb() => (s_allocator?.GetHeapSize(device_local: true) ?? 0) / (1024 * 1024);
    // TODO(port): VK_EXT_memory_budget for the real figure (spartan), this ignores other processes
    public static ulong MemoryGetAvailableMb() { ulong total = MemoryGetTotalMb(), used = MemoryGetAllocatedMb(); return total > used ? total - used : 0; }
}
