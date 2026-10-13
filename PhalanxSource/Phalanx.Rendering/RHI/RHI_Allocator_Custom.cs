// Our allocator (phalanx-rhi-plan.md §7, option C): per (memory type, linear/optimal) lists of blocks sub-allocated
// with RHI_Allocator_FreeList, dedicated allocations for large resources or when the driver asks, host-visible
// memory mapped once for its lifetime. Thread safe (one lock, allocation is not a hot path in Spartan either).
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe class RHI_Allocator_Custom : RHI_Allocator
{
    private sealed class Block
    {
        public DeviceMemory Memory;
        public RHI_Allocator_FreeList FreeList = null!;
        public nint Mapped;
        public int Key;
    }

    private readonly object m_mutex = new();
    private readonly MemoryPropertyFlags[] m_types;
    private readonly uint[] m_type_heap;
    private readonly ulong[] m_heap_size;
    private readonly Dictionary<int, List<Block>> m_blocks = new();
    private int m_dedicated_count, m_allocation_count;
    private ulong m_bytes_dedicated, m_bytes_used;

    public IReadOnlyList<MemoryPropertyFlags> MemoryTypes => m_types;

    public RHI_Allocator_Custom()
    {
        RHI_Context.Vk.GetPhysicalDeviceMemoryProperties(RHI_Context.DevicePhysical, out PhysicalDeviceMemoryProperties props);
        m_types     = new MemoryPropertyFlags[props.MemoryTypeCount];
        m_type_heap = new uint[props.MemoryTypeCount];
        for (int i = 0; i < m_types.Length; i++)
        {
            m_types[i]     = props.MemoryTypes[i].PropertyFlags;
            m_type_heap[i] = props.MemoryTypes[i].HeapIndex;
        }
        m_heap_size = new ulong[props.MemoryHeapCount];
        for (int i = 0; i < m_heap_size.Length; i++)
            m_heap_size[i] = props.MemoryHeaps[i].Size;
    }

    public ulong GetHeapSize(bool device_local)
    {
        ulong total = 0;
        RHI_Context.Vk.GetPhysicalDeviceMemoryProperties(RHI_Context.DevicePhysical, out PhysicalDeviceMemoryProperties props);
        for (int i = 0; i < props.MemoryHeapCount; i++)
            if (((props.MemoryHeaps[i].Flags & MemoryHeapFlags.DeviceLocalBit) != 0) == device_local)
                total += props.MemoryHeaps[i].Size;
        return total;
    }

    public RHI_Allocation Allocate(in MemoryRequirements requirements, RHI_Memory_Usage usage, RHI_Memory_Kind kind,
                                   bool dedicated_required, Image dedicated_image, Silk.NET.Vulkan.Buffer dedicated_buffer, string name)
    {
        int type = RHI_Allocator_Policy.FindMemoryType(requirements.MemoryTypeBits, usage, m_types);
        if (type < 0)
            throw new RHI_Exception(Result.ErrorOutOfDeviceMemory, $"{name}: no memory type for {usage} in bits 0x{requirements.MemoryTypeBits:x}");
        bool host_visible = (m_types[type] & MemoryPropertyFlags.HostVisibleBit) != 0;
        // a block is shared by every usage that resolves to its memory type (one type on uma/software drivers),
        // so host-visible memory is always mapped
        bool mapped       = host_visible;
        ulong block_size  = RHI_Allocator_Policy.BlockSize(m_heap_size[m_type_heap[type]]);

        lock (m_mutex)
        {
            if (RHI_Allocator_Policy.UseDedicated(requirements.Size, block_size, dedicated_required))
            {
                DeviceMemory memory = AllocateMemory(requirements.Size, (uint)type, dedicated_image, dedicated_buffer, name);
                nint data = mapped ? Map(memory) : 0;
                m_dedicated_count++;
                m_allocation_count++;
                m_bytes_dedicated += requirements.Size;
                m_bytes_used      += requirements.Size;
                return new RHI_Allocation { Memory = memory, Offset = 0, Size = requirements.Size, MemoryTypeIndex = (uint)type, Usage = usage,
                                            IsDedicated = true, MappedData = data, Name = name };
            }

            int key = RHI_Allocator_Policy.BlockKey((uint)type, kind);
            if (!m_blocks.TryGetValue(key, out List<Block>? blocks))
                m_blocks[key] = blocks = new List<Block>();

            foreach (Block block in blocks)
                if (TryAllocate(block, requirements, usage, name) is { } allocation)
                    return allocation;

            Block created = new()
            {
                Memory   = AllocateMemory(block_size, (uint)type, default, default, $"block_{type}_{kind}_{blocks.Count}"),
                FreeList = new RHI_Allocator_FreeList(block_size),
                Key      = key
            };
            created.Mapped = mapped ? Map(created.Memory) : 0;
            blocks.Add(created);
            return TryAllocate(created, requirements, usage, name) ?? throw new RHI_Exception(Result.ErrorOutOfDeviceMemory, $"{name}: does not fit an empty block");
        }
    }

    private RHI_Allocation? TryAllocate(Block block, in MemoryRequirements requirements, RHI_Memory_Usage usage, string name)
    {
        ulong offset = block.FreeList.Allocate(requirements.Size, System.Math.Max(1UL, requirements.Alignment));
        if (offset == RHI_Allocator_FreeList.Invalid)
            return null;
        m_allocation_count++;
        m_bytes_used += requirements.Size;
        return new RHI_Allocation
        {
            Memory = block.Memory, Offset = offset, Size = requirements.Size, MemoryTypeIndex = (uint)(block.Key / 2), Usage = usage,
            MappedData = block.Mapped != 0 ? block.Mapped + (nint)offset : 0, Name = name, Block = block
        };
    }

    public void Free(RHI_Allocation allocation)
    {
        lock (m_mutex)
        {
            if (allocation.Freed)
                throw new InvalidOperationException($"{allocation.Name}: freed twice");
            allocation.Freed = true;
            m_allocation_count--;
            m_bytes_used -= allocation.Size;

            if (allocation.IsDedicated)
            {
                m_dedicated_count--;
                m_bytes_dedicated -= allocation.Size;
                RHI_Context.Vk.FreeMemory(RHI_Context.Device, allocation.Memory, null); // unmaps implicitly
                return;
            }

            Block block = (Block)allocation.Block!;
            block.FreeList.Free(allocation.Offset);
            // keep one empty block per pool to avoid churn, release the others (vma does the same)
            List<Block> blocks = m_blocks[block.Key];
            if (block.FreeList.IsEmpty && blocks.Count(b => b.FreeList.IsEmpty) > 1)
            {
                blocks.Remove(block);
                RHI_Context.Vk.FreeMemory(RHI_Context.Device, block.Memory, null);
            }
        }
    }

    public RHI_Allocator_Stats GetStats()
    {
        lock (m_mutex)
        {
            int block_count = 0;
            ulong reserved = m_bytes_dedicated;
            foreach (List<Block> blocks in m_blocks.Values)
                foreach (Block b in blocks) { block_count++; reserved += b.FreeList.Size; }
            return new RHI_Allocator_Stats(block_count, m_dedicated_count, m_allocation_count, reserved, m_bytes_used);
        }
    }

    private static DeviceMemory AllocateMemory(ulong size, uint type, Image image, Silk.NET.Vulkan.Buffer buffer, string name)
    {
        MemoryDedicatedAllocateInfo dedicated = new() { SType = StructureType.MemoryDedicatedAllocateInfo, Image = image, Buffer = buffer };
        MemoryAllocateInfo info = new()
        {
            SType           = StructureType.MemoryAllocateInfo,
            PNext           = image.Handle != 0 || buffer.Handle != 0 ? &dedicated : null,
            AllocationSize  = size,
            MemoryTypeIndex = type
        };
        Check(RHI_Context.Vk.AllocateMemory(RHI_Context.Device, in info, null, out DeviceMemory memory), "vkAllocateMemory");
        RHI_Device.SetResourceName(memory.Handle, RHI_Resource_Type.DeviceMemory, name);
        return memory;
    }

    private static nint Map(DeviceMemory memory)
    {
        void* data;
        Check(RHI_Context.Vk.MapMemory(RHI_Context.Device, memory, 0, Vk.WholeSize, 0, &data), "vkMapMemory");
        return (nint)data;
    }

    public void Dispose()
    {
        lock (m_mutex)
        {
            if (m_allocation_count > 0)
                Log.LogWarning($"RHI_Allocator: {m_allocation_count} allocation(s) still alive at shutdown ({m_bytes_used} bytes)");
            foreach (List<Block> blocks in m_blocks.Values)
                foreach (Block b in blocks)
                    RHI_Context.Vk.FreeMemory(RHI_Context.Device, b.Memory, null);
            m_blocks.Clear();
        }
    }
}
