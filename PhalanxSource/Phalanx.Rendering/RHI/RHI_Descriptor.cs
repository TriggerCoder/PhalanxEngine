// Port of SpartanEngine source/rhi/RHI_Descriptor.h @ 4c07baa
// RHI_DescriptorBinding.resource is an object reference (RHI_Buffer / RHI_Texture) instead of a void*, its hash uses a
// unique 64-bit object id (RHI_ObjectId, spartan: SpartanObject::m_object_id) where spartan hashes the pointer value.
// Identity hash codes are only 32 bits and not unique, the set cache is keyed on this hash alone.
using System.Runtime.CompilerServices;

namespace Phalanx.Rendering.RHI;

// layout descriptor - immutable, from shader reflection
internal struct RHI_Descriptor
{
    public RHI_Descriptor_Type type;
    public RHI_Image_Layout layout;
    public uint slot;
    public uint stage;
    public uint struct_size;
    public uint array_length;
    public uint space;
    public bool as_array;
    public bool used;
    public string name;

    public RHI_Descriptor(string name, RHI_Descriptor_Type type, RHI_Image_Layout layout, uint slot, uint stage, uint struct_size,
                          bool as_array, uint array_length, uint space = 0, bool used = true)
    {
        this.type         = type;
        this.layout       = layout;
        this.slot         = slot;
        this.stage        = stage;
        this.name         = name;
        this.struct_size  = struct_size;
        this.as_array     = as_array;
        this.array_length = array_length;
        this.space        = space;
        this.used         = used;
    }

    public readonly bool IsStorage() => type == RHI_Descriptor_Type.TextureStorage;
    public readonly bool IsBindless() => space != 0 || (as_array && array_length == RHI_Definitions.rhi_max_array_size);

    public override readonly string ToString() =>
        $"{name}: {type} set {space} binding {slot}{(as_array ? $"[{array_length}]" : "")} stages 0x{stage:x} size {struct_size}{(used ? "" : " (unused)")}";
}

// binding state - mutable, set at runtime
internal struct RHI_DescriptorBinding
{
    public object? resource;
    public ulong range;
    public uint dynamic_offset;
    public uint mip;
    public uint mip_range;
    public uint array_layer;   // rhi_all_mips = all layers (default), otherwise specific layer index
    public RHI_Image_Layout layout;

    public RHI_DescriptorBinding() => Reset();

    public readonly bool IsBound() => resource != null;

    public void Reset()
    {
        resource       = null;
        range          = 0;
        dynamic_offset = 0;
        mip            = 0;
        mip_range      = 0;
        array_layer    = RHI_Definitions.rhi_all_mips;
        layout         = RHI_Image_Layout.Max;
    }

    public readonly ulong GetHash()
    {
        ulong hash = resource == null ? 0UL : RHI_ObjectId.Get(resource);
        hash = RHI_Definitions.rhi_hash_combine(hash, range);
        hash = RHI_Definitions.rhi_hash_combine(hash, mip);
        hash = RHI_Definitions.rhi_hash_combine(hash, mip_range);
        hash = RHI_Definitions.rhi_hash_combine(hash, array_layer);
        hash = RHI_Definitions.rhi_hash_combine(hash, (ulong)layout);
        return hash;
    }
}

// combined descriptor with binding for descriptor set creation
internal struct RHI_DescriptorWithBinding
{
    public RHI_Descriptor descriptor;
    public RHI_DescriptorBinding binding;

    public readonly uint GetSlot() => descriptor.slot;
    public readonly RHI_Descriptor_Type GetDescriptorType() => descriptor.type; // spartan: GetType(), clashes with object.GetType
    public readonly bool IsBound() => binding.IsBound();
}

// unique, never reused ids for any rhi object, without adding a field to each class
internal static class RHI_ObjectId
{
    private sealed class Box { public readonly ulong Value; public Box(ulong v) => Value = v; }
    private static readonly ConditionalWeakTable<object, Box> s_ids = new();
    private static ulong s_next;

    public static ulong Get(object obj) => s_ids.GetValue(obj, static _ => new Box(Interlocked.Increment(ref s_next))).Value;
}
