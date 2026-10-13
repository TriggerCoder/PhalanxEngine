// Placeholder allocator for the first slice: one vkAllocateMemory per resource (dedicated allocation).
// Spartan uses VMA (Vulkan_Device.cpp: MemoryBufferCreate / MemoryTextureCreate); the replacement is planned in
// phalanx-rhi-plan.md (step 4). Only the offscreen test target and its readback buffer use this today.
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal static unsafe class RHI_Memory
{
    // the first memory type allowed by type_bits that has all the required flags, -1 if none. pure, testable.
    public static int FindMemoryType(uint type_bits, MemoryPropertyFlags required, IReadOnlyList<MemoryPropertyFlags> types)
    {
        for (int i = 0; i < types.Count; i++)
            if ((type_bits & (1u << i)) != 0 && (types[i] & required) == required)
                return i;
        return -1;
    }

    private static MemoryPropertyFlags[] GetMemoryTypes()
    {
        RHI_Context.Vk.GetPhysicalDeviceMemoryProperties(RHI_Context.DevicePhysical, out PhysicalDeviceMemoryProperties props);
        var types = new MemoryPropertyFlags[props.MemoryTypeCount];
        for (int i = 0; i < types.Length; i++)
            types[i] = props.MemoryTypes[i].PropertyFlags;
        return types;
    }

    private static DeviceMemory Allocate(MemoryRequirements req, MemoryPropertyFlags flags, string name)
    {
        int type = FindMemoryType(req.MemoryTypeBits, flags, GetMemoryTypes());
        if (type < 0)
            throw new NotSupportedException($"No memory type with {flags} for {name}");
        MemoryAllocateInfo info = new() { SType = StructureType.MemoryAllocateInfo, AllocationSize = req.Size, MemoryTypeIndex = (uint)type };
        Check(RHI_Context.Vk.AllocateMemory(RHI_Context.Device, in info, null, out DeviceMemory memory), "vkAllocateMemory");
        RHI_Device.SetResourceName(memory.Handle, RHI_Resource_Type.DeviceMemory, name);
        return memory;
    }

    public static (Image image, DeviceMemory memory) CreateImage2D(uint width, uint height, RHI_Format format, ImageUsageFlags usage, string name)
    {
        ImageCreateInfo info = new()
        {
            SType         = StructureType.ImageCreateInfo,
            ImageType     = ImageType.Type2D,
            Format        = ToVulkan(format),
            Extent        = new Extent3D(width, height, 1),
            MipLevels     = 1,
            ArrayLayers   = 1,
            Samples       = SampleCountFlags.Count1Bit,
            Tiling        = ImageTiling.Optimal,
            Usage         = usage,
            SharingMode   = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined
        };
        Check(RHI_Context.Vk.CreateImage(RHI_Context.Device, in info, null, out Image image), "vkCreateImage");
        RHI_Device.SetResourceName(image.Handle, RHI_Resource_Type.Image, name);
        RHI_Context.Vk.GetImageMemoryRequirements(RHI_Context.Device, image, out MemoryRequirements req);
        DeviceMemory memory = Allocate(req, MemoryPropertyFlags.DeviceLocalBit, name);
        Check(RHI_Context.Vk.BindImageMemory(RHI_Context.Device, image, memory, 0), "vkBindImageMemory");
        return (image, memory);
    }

    public static (Buffer buffer, DeviceMemory memory) CreateReadbackBuffer(ulong size, string name)
    {
        BufferCreateInfo info = new() { SType = StructureType.BufferCreateInfo, Size = size, Usage = BufferUsageFlags.TransferDstBit, SharingMode = SharingMode.Exclusive };
        Check(RHI_Context.Vk.CreateBuffer(RHI_Context.Device, in info, null, out Buffer buffer), "vkCreateBuffer");
        RHI_Device.SetResourceName(buffer.Handle, RHI_Resource_Type.Buffer, name);
        RHI_Context.Vk.GetBufferMemoryRequirements(RHI_Context.Device, buffer, out MemoryRequirements req);
        DeviceMemory memory = Allocate(req, MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, name);
        Check(RHI_Context.Vk.BindBufferMemory(RHI_Context.Device, buffer, memory, 0), "vkBindBufferMemory");
        return (buffer, memory);
    }

    public static byte[] Read(DeviceMemory memory, ulong size)
    {
        void* data;
        Check(RHI_Context.Vk.MapMemory(RHI_Context.Device, memory, 0, size, 0, &data), "vkMapMemory");
        var bytes = new byte[size];
        new ReadOnlySpan<byte>(data, (int)size).CopyTo(bytes);
        RHI_Context.Vk.UnmapMemory(RHI_Context.Device, memory);
        return bytes;
    }

    public static void Free(DeviceMemory memory) => RHI_Context.Vk.FreeMemory(RHI_Context.Device, memory, null);
}
