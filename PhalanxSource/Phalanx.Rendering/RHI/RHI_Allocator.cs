// GPU memory allocation (phalanx-rhi-plan.md §7). Spartan uses VMA through RHI_Device::MemoryBufferCreate /
// MemoryTextureCreate; we put our own allocator (option C) behind RHI_Allocator so VMA (option A) can replace it
// without touching callers.
using Silk.NET.Vulkan;

namespace Phalanx.Rendering.RHI;

// which pool an allocation comes from (spartan picks VMA_MEMORY_USAGE_* + host access flags per resource)
internal enum RHI_Memory_Usage
{
    GpuOnly,   // device local, never mapped
    Upload,    // host visible + coherent, persistently mapped, cpu writes / gpu reads (staging, constant buffers)
    Readback,  // host visible + coherent, cached preferred, persistently mapped, gpu writes / cpu reads
    Max
}

// what is bound to the memory: linear resources (buffers, linear images) and optimal images never share a block,
// which makes bufferImageGranularity irrelevant inside a block
internal enum RHI_Memory_Kind
{
    Linear,
    Optimal
}

internal sealed class RHI_Allocation
{
    public DeviceMemory Memory { get; init; }
    public ulong Offset { get; init; }
    public ulong Size { get; init; }
    public uint MemoryTypeIndex { get; init; }
    public RHI_Memory_Usage Usage { get; init; }
    public bool IsDedicated { get; init; }
    public nint MappedData { get; init; }   // 0 when not host visible; already includes Offset
    public string Name { get; init; } = "";
    internal object? Block { get; init; }   // allocator private
    internal bool Freed { get; set; }
}

internal readonly record struct RHI_Allocator_Stats(
    int BlockCount, int DedicatedCount, int AllocationCount, ulong BytesReserved, ulong BytesUsed);

internal interface RHI_Allocator : IDisposable
{
    // requirements come from vkGet*MemoryRequirements2; dedicated_resource is the image or buffer handle used for
    // VkMemoryDedicatedAllocateInfo when the allocator decides on (or the driver asks for) a dedicated allocation
    RHI_Allocation Allocate(in MemoryRequirements requirements, RHI_Memory_Usage usage, RHI_Memory_Kind kind,
                            bool dedicated_required, Image dedicated_image, Silk.NET.Vulkan.Buffer dedicated_buffer, string name);
    void Free(RHI_Allocation allocation);
    RHI_Allocator_Stats GetStats();
}

// the pure policy, shared by the allocator and the tests
internal static class RHI_Allocator_Policy
{
    public const ulong BlockSizeMax = 64UL * 1024 * 1024;
    public const ulong BlockSizeMin = 4UL * 1024 * 1024;

    // 64 MB blocks, smaller on small heaps (an eighth of the heap, power of two, at least 4 MB)
    public static ulong BlockSize(ulong heap_size)
    {
        ulong size = BlockSizeMax;
        while (size > BlockSizeMin && size > heap_size / 8)
            size >>= 1;
        return size;
    }

    // large resources (render targets) get their own VkDeviceMemory, like VMA does for > half a block
    public static bool UseDedicated(ulong size, ulong block_size, bool dedicated_required) =>
        dedicated_required || size > block_size / 2;

    public static (MemoryPropertyFlags required, MemoryPropertyFlags preferred, MemoryPropertyFlags not_preferred) Flags(RHI_Memory_Usage usage) => usage switch
    {
        RHI_Memory_Usage.GpuOnly  => (MemoryPropertyFlags.DeviceLocalBit, 0, MemoryPropertyFlags.HostVisibleBit),
        RHI_Memory_Usage.Upload   => (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, 0, MemoryPropertyFlags.HostCachedBit),
        RHI_Memory_Usage.Readback => (MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPropertyFlags.HostCachedBit, 0),
        _ => throw new ArgumentOutOfRangeException(nameof(usage))
    };

    // the allowed type with all required flags, the most preferred and the fewest not preferred flags; -1 if none.
    // gpu-only falls back to any allowed type when nothing is device local (some software drivers).
    public static int FindMemoryType(uint type_bits, RHI_Memory_Usage usage, IReadOnlyList<MemoryPropertyFlags> types)
    {
        var (required, preferred, not_preferred) = Flags(usage);
        int index = FindMemoryType(type_bits, required, preferred, not_preferred, types);
        if (index < 0 && usage == RHI_Memory_Usage.GpuOnly)
            index = FindMemoryType(type_bits, 0, 0, not_preferred, types);
        return index;
    }

    public static int FindMemoryType(uint type_bits, MemoryPropertyFlags required, MemoryPropertyFlags preferred,
                                     MemoryPropertyFlags not_preferred, IReadOnlyList<MemoryPropertyFlags> types)
    {
        int best = -1, best_score = int.MinValue;
        for (int i = 0; i < types.Count && i < 32; i++)
        {
            if ((type_bits & (1u << i)) == 0 || (types[i] & required) != required)
                continue;
            int score = System.Numerics.BitOperations.PopCount((uint)(types[i] & preferred)) * 2
                      - System.Numerics.BitOperations.PopCount((uint)(types[i] & not_preferred));
            if (score > best_score) { best = i; best_score = score; }
        }
        return best;
    }

    // blocks are keyed by memory type and kind, so linear and optimal never share one
    public static int BlockKey(uint memory_type_index, RHI_Memory_Kind kind) => (int)memory_type_index * 2 + (int)kind;
}
