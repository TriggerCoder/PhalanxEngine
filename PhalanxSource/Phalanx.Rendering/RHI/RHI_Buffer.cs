// Port of SpartanEngine source/rhi/RHI_Buffer.h + vulkan/Vulkan_Buffer.cpp @ 4c07baa
// Deviations: no ShaderBindingTable (ray tracing step), no device address / acceleration structure usage yet,
// Non-mappable Storage buffers with initial data
// are staged like vertex/index buffers (spartan asserts mappable there). Vertex/index/instance buffers also get
// TRANSFER_SRC so they can be copied or read back.
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Phalanx.Rendering.RHI;

internal enum RHI_Buffer_Type
{
    Vertex,
    Index,
    Instance,
    Storage,
    Constant,
    ShaderBindingTable,
    Upload,
    Readback,
    Max
}

internal sealed unsafe class RHI_Buffer : IDisposable
{
    private RHI_Buffer_Type m_type = RHI_Buffer_Type.Max;
    private uint m_stride_unaligned;
    private uint m_stride;
    private uint m_element_count;
    private uint m_offset;
    private ulong m_object_size;
    private nint m_data_gpu;
    private bool m_mappable;
    private bool first_update = true;
    private Buffer m_rhi_resource;
    private RHI_Allocation? m_allocation;

    public string Name { get; }

    public RHI_Buffer(RHI_Buffer_Type type, ulong stride, uint element_count, ReadOnlySpan<byte> data, bool mappable, string name)
    {
        if (type == RHI_Buffer_Type.Max) throw new ArgumentOutOfRangeException(nameof(type));
        if (stride == 0) throw new ArgumentOutOfRangeException(nameof(stride));
        if (element_count == 0) throw new ArgumentOutOfRangeException(nameof(element_count));
        if (string.IsNullOrEmpty(name)) throw new ArgumentException("Name the buffer to aid the validation layer", nameof(name));
        if (type == RHI_Buffer_Type.Constant && !mappable) throw new ArgumentException("Constant buffers must be mappable");
        if (type == RHI_Buffer_Type.ShaderBindingTable) throw new NotSupportedException("Shader binding tables arrive with ray tracing");
        if ((type == RHI_Buffer_Type.Upload || type == RHI_Buffer_Type.Readback) && !mappable) throw new ArgumentException("Upload and readback buffers must be mappable");

        m_type             = type;
        m_stride_unaligned = (uint)stride;
        m_stride           = m_stride_unaligned;
        m_element_count    = element_count;
        m_object_size      = stride * element_count;
        m_mappable         = mappable;
        Name               = name;

        RHI_CreateResource(data);
    }

    // spartan's usage/memory choice per type, pure so it can be tested without a gpu
    public static (BufferUsageFlags usage, RHI_Memory_Usage memory) GetUsageAndMemory(RHI_Buffer_Type type, bool mappable)
    {
        const BufferUsageFlags transfer_dst = BufferUsageFlags.TransferDstBit;
        switch (type)
        {
            case RHI_Buffer_Type.Vertex:
            case RHI_Buffer_Type.Instance:
            case RHI_Buffer_Type.Index:
            {
                // vertex pulling: vertex, instance and index buffers can also be bound as storage buffers
                BufferUsageFlags usage = (type == RHI_Buffer_Type.Index ? BufferUsageFlags.IndexBufferBit : BufferUsageFlags.VertexBufferBit)
                                       | BufferUsageFlags.StorageBufferBit | transfer_dst
                                       | BufferUsageFlags.TransferSrcBit; // deviation: readback/copies (spartan omits it)
                return (usage, mappable ? RHI_Memory_Usage.Upload : RHI_Memory_Usage.GpuOnly);
            }
            case RHI_Buffer_Type.Storage:
                return (BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | transfer_dst | BufferUsageFlags.IndirectBufferBit,
                        mappable ? RHI_Memory_Usage.Upload : RHI_Memory_Usage.GpuOnly);
            case RHI_Buffer_Type.Upload:   return (BufferUsageFlags.TransferSrcBit, RHI_Memory_Usage.Upload);
            case RHI_Buffer_Type.Readback: return (transfer_dst, RHI_Memory_Usage.Readback);
            case RHI_Buffer_Type.Constant: return (BufferUsageFlags.UniformBufferBit | transfer_dst, RHI_Memory_Usage.Upload);
            default: throw new NotSupportedException(type.ToString());
        }
    }

    // spartan: constant buffer stride is rounded up to minUniformBufferOffsetAlignment
    public static uint AlignConstantStride(uint stride, ulong min_alignment) =>
        min_alignment > 0 && min_alignment != stride ? (uint)((stride + min_alignment - 1) & ~(min_alignment - 1)) : stride;

    private void RHI_CreateResource(ReadOnlySpan<byte> data)
    {
        RHI_DestroyResource();

        if (m_type == RHI_Buffer_Type.Constant)
        {
            m_stride      = AlignConstantStride(m_stride, RHI_Device.PropertyMinUniformBufferOffsetAlignment);
            m_object_size = (ulong)m_stride * m_element_count;
        }

        var (usage, memory) = GetUsageAndMemory(m_type, m_mappable);
        bool stage = !data.IsEmpty && memory == RHI_Memory_Usage.GpuOnly;
        if (!data.IsEmpty && (ulong)data.Length > m_object_size)
            data = data[..(int)m_object_size];

        m_allocation = RHI_Device.MemoryBufferCreate(out m_rhi_resource, m_object_size, usage, memory, stage ? default : data, Name);
        if (stage)
            RHI_Device.StageBuffer(m_rhi_resource, 0, data);

        m_data_gpu = m_mappable ? RHI_Device.MemoryGetMappedDataFromBuffer(m_allocation) : 0;
    }

    private void RHI_DestroyResource()
    {
        if (m_rhi_resource.Handle == 0)
            return;
        RHI_Device.DescriptorSetInvalidateReferencingResource(this); // spartan Vulkan_Buffer.cpp:27
        RHI_Device.DeletionQueueAdd(RHI_Resource_Type.Buffer, m_rhi_resource.Handle, m_allocation);
        m_rhi_resource = default;
        m_allocation   = null;
        m_data_gpu     = 0;
    }

    // storage and constant buffer updating: each update after the first advances by one stride (per-draw slots),
    // ResetOffset rewinds at the start of the frame
    public void Update(RHI_CommandList cmd_list, ReadOnlySpan<byte> data_cpu, uint size = 0)
    {
        if (!m_mappable) throw new InvalidOperationException("Can't update unmapped buffer");
        if (m_data_gpu == 0) throw new InvalidOperationException("Invalid gpu data");
        if (data_cpu.IsEmpty) throw new ArgumentException("Invalid cpu data");
        if (m_offset + m_stride > m_object_size) throw new InvalidOperationException("Out of memory");

        if (first_update)
            first_update = false;
        else
            m_offset += m_stride;

        uint upload_size = size != 0 ? size : m_stride;
        if ((ulong)m_offset + upload_size > m_object_size)
            throw new InvalidOperationException($"buffer \"{Name}\" overflowed, either it is pushed more times than it has slots or a push is missing its ResetOffset");
        data_cpu[..(int)System.Math.Min(upload_size, (uint)data_cpu.Length)].CopyTo(new Span<byte>((byte*)m_data_gpu + m_offset, (int)upload_size));

        AccessFlags2 dst_access = m_type switch
        {
            RHI_Buffer_Type.Vertex or RHI_Buffer_Type.Instance => AccessFlags2.VertexAttributeReadBit,
            RHI_Buffer_Type.Index    => AccessFlags2.IndexReadBit,
            RHI_Buffer_Type.Storage  => AccessFlags2.ShaderReadBit | AccessFlags2.ShaderWriteBit | AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.IndirectCommandReadBit,
            RHI_Buffer_Type.Constant => AccessFlags2.ShaderReadBit | AccessFlags2.UniformReadBit,
            RHI_Buffer_Type.Upload   => AccessFlags2.TransferReadBit,
            RHI_Buffer_Type.Readback => AccessFlags2.HostReadBit,
            _ => AccessFlags2.None
        };
        BufferMemoryBarrier2 barrier = new()
        {
            SType               = StructureType.BufferMemoryBarrier2,
            SrcStageMask        = PipelineStageFlags2.HostBit,
            SrcAccessMask       = AccessFlags2.HostWriteBit,
            DstStageMask        = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask       = dst_access,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Buffer              = m_rhi_resource,
            Offset              = m_offset,
            Size                = upload_size
        };
        DependencyInfo dependency = new() { SType = StructureType.DependencyInfo, BufferMemoryBarrierCount = 1, PBufferMemoryBarriers = &barrier };
        RHI_Context.Vk.CmdPipelineBarrier2(cmd_list.GetRhiResource(), in dependency);
    }

    public void Update(ReadOnlySpan<byte> data_cpu, uint size = 0) => Update(RHI_Device.Cmd(), data_cpu, size);

    public void ResetOffset() { m_offset = 0; first_update = true; }

    // copy data into a sub-region of this buffer (for vertex/index buffers). spartan uses vkCmdUpdateBuffer on an
    // immediate graphics list; we stage (no 64 KB limit), same result.
    public void UploadSubRegion(ReadOnlySpan<byte> data, ulong offset_bytes)
    {
        ulong size_bytes = (ulong)data.Length;
        if (offset_bytes + size_bytes > m_object_size) throw new ArgumentOutOfRangeException(nameof(offset_bytes));
        if (m_rhi_resource.Handle == 0 || size_bytes == 0) return;
        if (m_data_gpu != 0)
        {
            data.CopyTo(new Span<byte>((byte*)m_data_gpu + offset_bytes, data.Length));
            return;
        }
        RHI_Device.StageBuffer(m_rhi_resource, offset_bytes, data);
    }

    // reads back a sub-region of a mappable buffer (a few bytes the cpu wrote earlier)
    public void ReadSubRegion(Span<byte> data, ulong offset_bytes)
    {
        if (offset_bytes + (ulong)data.Length > m_object_size) throw new ArgumentOutOfRangeException(nameof(offset_bytes));
        if (m_data_gpu == 0) throw new InvalidOperationException("Only mappable buffers can be read back");
        new ReadOnlySpan<byte>((byte*)m_data_gpu + offset_bytes, data.Length).CopyTo(data);
    }

    public uint GetStrideUnaligned() => m_stride_unaligned;
    public uint GetStride() => m_stride;
    public uint GetElementCount() => m_element_count;
    public uint GetOffset() => m_offset;
    public ulong GetObjectSize() => m_object_size;
    public nint GetMappedData() => m_data_gpu;
    public Buffer GetRhiResource() => m_rhi_resource;
    public RHI_Buffer_Type Type => m_type;
    public RHI_Allocation? GetAllocation() => m_allocation;

    public void Dispose() => RHI_DestroyResource();
}
