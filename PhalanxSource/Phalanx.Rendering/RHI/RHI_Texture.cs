// Port of SpartanEngine source/rhi/RHI_Texture.h/.cpp + vulkan/Vulkan_Texture.cpp @ 4c07baa (creation,
// staging upload of all slices/mips, layout tracking, views). Not ported yet: file load/save, compression,
// streaming (RHI_TextureStreaming), UpdateRegion, descriptor-cache invalidation, debug breadcrumbs.
// Synced with upstream 67a6e8b: pixel sizes come from rhi_format_to_bytes, 3D textures stage one slice with mip
// depth clamped to 1, CalculateMipChainSize drives the dedicated-allocation threshold. Still deviating: per-layer/rtv/dsv views are capped at rhi_max_render_target_count.
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal enum RHI_Texture_Type
{
    Type2D,
    Type2DArray,
    Type3D,
    TypeCube,
    Max
}

[Flags]
internal enum RHI_Texture_Flags : uint
{
    None                          = 0,
    RHI_Texture_Srv               = 1U << 0,
    RHI_Texture_Uav               = 1U << 1,
    RHI_Texture_Rtv               = 1U << 2,
    RHI_Texture_Vrs               = 1U << 3,
    RHI_Texture_ClearBlit         = 1U << 4,
    RHI_Texture_PerMipViews       = 1U << 5,
    RHI_Texture_Greyscale         = 1U << 6,
    RHI_Texture_Transparent       = 1U << 7,
    RHI_Texture_Srgb              = 1U << 8,
    RHI_Texture_Mappable          = 1U << 9,
    RHI_Texture_ConcurrentSharing = 1U << 10,
    RHI_Texture_Compress          = 1U << 11,
    RHI_Texture_DeferUpload       = 1U << 12,
    RHI_Texture_Stream            = 1U << 13
}

internal sealed class RHI_Texture_Mip
{
    public byte[] bytes = Array.Empty<byte>();
}

internal sealed class RHI_Texture_Slice
{
    public List<RHI_Texture_Mip> mips = new();
    public uint GetMipCount() => (uint)mips.Count;
}

internal sealed unsafe class RHI_Texture : Phalanx.World.RHI_Texture, IDisposable
{
    private uint m_width, m_height, m_depth, m_mip_count, m_bits_per_channel, m_channel_count;
    private RHI_Format m_format = RHI_Format.Max;
    private RHI_Texture_Type m_type = RHI_Texture_Type.Max;
    private RHI_Texture_Flags m_flags;
    private readonly RHI_Image_Layout[] m_layouts = new RHI_Image_Layout[RHI_Definitions.rhi_max_mip_count];
    private List<RHI_Texture_Slice> m_slices;
    private ulong m_object_size;

    // api resources
    private Image m_rhi_resource;
    private RHI_Allocation? m_allocation;
    private ImageView m_rhi_srv;                                                                     // all mips
    private readonly ImageView[] m_rhi_srv_mips   = new ImageView[RHI_Definitions.rhi_max_mip_count];  // one per mip
    private readonly ImageView[] m_rhi_srv_layers = new ImageView[RHI_Definitions.rhi_max_render_target_count];
    private readonly ImageView[] m_rhi_rtv        = new ImageView[RHI_Definitions.rhi_max_render_target_count];
    private readonly ImageView[] m_rhi_dsv        = new ImageView[RHI_Definitions.rhi_max_render_target_count];
    private ImageView m_rhi_rtv_multiview, m_rhi_dsv_multiview;

    public string Name { get; }

    public RHI_Texture(RHI_Texture_Type type, uint width, uint height, uint depth, uint mip_count, RHI_Format format,
                       RHI_Texture_Flags flags, string name, List<RHI_Texture_Slice>? slices = null) : base(width, height)
    {
        if (type == RHI_Texture_Type.TypeCube && depth != 6) throw new ArgumentException("cube textures have 6 faces (depth = 6)", nameof(depth));
        if (mip_count == 0 || mip_count > RHI_Definitions.rhi_max_mip_count) throw new ArgumentOutOfRangeException(nameof(mip_count));
        m_type             = type;
        m_width            = width;
        m_height           = height;
        m_depth            = depth;
        m_mip_count        = mip_count;
        m_format           = format;
        m_flags            = flags;
        Name               = name;
        m_slices           = slices ?? new List<RHI_Texture_Slice>();
        m_channel_count    = RHI_Definitions.rhi_to_format_channel_count(format);
        m_bits_per_channel = RHI_Definitions.rhi_format_to_bits_per_channel(format);
        ClearLayouts();
        ComputeMemoryUsage();

        // render targets need the gpu resource immediately even without cpu data, other textures with empty
        // slices get their data later
        bool is_render_target = (m_flags & (RHI_Texture_Flags.RHI_Texture_Rtv | RHI_Texture_Flags.RHI_Texture_Uav)) != 0;
        bool will_fill_data_later = m_slices.Count == 0 && !is_render_target;
        if (!will_fill_data_later)
            PrepareForGpu();
    }

    // ------------------------------------------------------------------------------------------------------------
    // properties (spartan names)
    public override uint GetWidth() => m_width;
    public override uint GetHeight() => m_height;
    public uint GetDepth() => m_depth;
    public uint GetMipCount() => m_mip_count;
    public uint GetArrayLength() => m_type == RHI_Texture_Type.Type3D ? 1 : m_depth;
    public uint GetBitsPerChannel() => m_bits_per_channel;
    public uint GetBytesPerChannel() => m_bits_per_channel / 8;
    public uint GetBytesPerPixel() => RHI_Definitions.rhi_format_to_bytes(m_format); // upstream 67a6e8b
    public uint GetChannelCount() => m_channel_count;
    public RHI_Format GetFormat() => m_format;
    public RHI_Texture_Type Type => m_type;
    public RHI_Texture_Flags GetFlags() => m_flags;
    public ulong GetObjectSize() => m_object_size;
    public bool HasData() => m_slices.Count > 0 && m_slices[0].mips.Count > 0 && m_slices[0].mips[0].bytes.Length > 0;

    public bool IsSrv() => (m_flags & RHI_Texture_Flags.RHI_Texture_Srv) != 0;
    public bool IsUav() => (m_flags & RHI_Texture_Flags.RHI_Texture_Uav) != 0;
    public bool IsVrs() => (m_flags & RHI_Texture_Flags.RHI_Texture_Vrs) != 0;
    public bool IsRt() => (m_flags & RHI_Texture_Flags.RHI_Texture_Rtv) != 0;
    public bool IsDsv() => IsRt() && IsDepthStencilFormat();
    public bool IsRtv() => IsRt() && IsColorFormat();
    public bool HasPerMipViews() => (m_flags & RHI_Texture_Flags.RHI_Texture_PerMipViews) != 0;
    public bool IsGrayscale() => (m_flags & RHI_Texture_Flags.RHI_Texture_Greyscale) != 0;
    public override bool IsSemiTransparent() => (m_flags & RHI_Texture_Flags.RHI_Texture_Transparent) != 0;
    public override bool IsGreyscaleCpu() => IsGrayscale();

    public static bool IsDepthFormat(RHI_Format f) => f == RHI_Format.D16_Unorm || f == RHI_Format.D32_Float || f == RHI_Format.D32_Float_S8X24_Uint;
    public static bool IsStencilFormat(RHI_Format f) => f == RHI_Format.D32_Float_S8X24_Uint;
    public bool IsDepthFormat() => IsDepthFormat(m_format);
    public bool IsStencilFormat() => IsStencilFormat(m_format);
    public bool IsDepthStencilFormat() => IsDepthFormat() || IsStencilFormat();
    public bool IsColorFormat() => !IsDepthStencilFormat();

    public static bool IsCompressedFormat(RHI_Format format) =>
        format is RHI_Format.BC1_Unorm or RHI_Format.BC3_Unorm or RHI_Format.BC5_Unorm or RHI_Format.BC7_Unorm or RHI_Format.ASTC;
    public bool IsCompressedFormat() => IsCompressedFormat(m_format);

    // layouts (per mip, like spartan)
    public RHI_Image_Layout GetLayout(uint mip) => m_layouts[mip];
    public IReadOnlyList<RHI_Image_Layout> GetLayouts() => m_layouts;
    public void SetLayoutDirect(uint mip_index, uint mip_range, RHI_Image_Layout layout)
    {
        if (mip_index >= RHI_Definitions.rhi_max_mip_count || mip_index + mip_range > RHI_Definitions.rhi_max_mip_count)
            throw new ArgumentOutOfRangeException(nameof(mip_index));
        for (uint i = mip_index; i < mip_index + mip_range; i++)
            m_layouts[i] = layout;
    }
    public void ClearLayouts() => Array.Fill(m_layouts, RHI_Image_Layout.Max);

    // rhi
    public Image GetRhiResource() => m_rhi_resource;
    public ImageView GetRhiSrv() => m_rhi_srv;
    public ImageView GetRhiSrvMip(uint i) => m_rhi_srv_mips[i];
    public ImageView GetRhiSrvLayer(uint i) => m_rhi_srv_layers[i];
    public ImageView GetRhiRtv(uint i = 0) => m_rhi_rtv[i];
    public ImageView GetRhiDsv(uint i = 0) => m_rhi_dsv[i];
    public ImageView GetRhiRtvMultiview() => m_rhi_rtv_multiview;
    public ImageView GetRhiDsvMultiview() => m_rhi_dsv_multiview;
    public RHI_Allocation? GetAllocation() => m_allocation;

    // aspect for barriers and copies (spartan: get_aspect_mask)
    public ImageAspectFlags GetAspectMask(bool only_depth = false, bool only_stencil = false)
    {
        if (IsColorFormat()) return ImageAspectFlags.ColorBit;
        ImageAspectFlags mask = 0;
        if (IsDepthFormat() && !only_stencil) mask |= ImageAspectFlags.DepthBit;
        if (IsStencilFormat() && !only_depth) mask |= ImageAspectFlags.StencilBit;
        return mask;
    }

    // ------------------------------------------------------------------------------------------------------------
    // data

    public RHI_Texture_Mip? GetMip(uint array_index, uint mip_index) =>
        array_index < m_slices.Count && mip_index < m_slices[(int)array_index].mips.Count ? m_slices[(int)array_index].mips[(int)mip_index] : null;

    public RHI_Texture_Slice? GetSlice(uint array_index) => array_index < m_slices.Count ? m_slices[(int)array_index] : null;

    // appends a mip of the right size to a slice (spartan: AllocateMip)
    public void AllocateMip(uint slice_index = 0)
    {
        while (m_slices.Count <= slice_index)
            m_slices.Add(new RHI_Texture_Slice());
        RHI_Texture_Slice slice = m_slices[(int)slice_index];
        uint mip_index = (uint)slice.mips.Count;
        uint width  = System.Math.Max(1u, m_width >> (int)mip_index);
        uint height = System.Math.Max(1u, m_height >> (int)mip_index);
        uint depth  = m_type == RHI_Texture_Type.Type3D ? System.Math.Max(1u, m_depth >> (int)mip_index) : 1;
        slice.mips.Add(new RHI_Texture_Mip { bytes = new byte[CalculateMipSize(width, height, depth, m_format, m_bits_per_channel, m_channel_count)] });
        if (m_type != RHI_Texture_Type.Type3D)
            m_depth = System.Math.Max(m_depth, (uint)m_slices.Count);
        m_mip_count = System.Math.Max(m_mip_count, (uint)slice.mips.Count);
    }

    public void ClearData() => m_slices = new List<RHI_Texture_Slice>();

    private void ComputeMemoryUsage() => m_object_size = CalculateMipChainSize();

    // tightly packed payload of mips first_mip.., excluding gpu alignment and metadata (upstream 67a6e8b)
    public ulong CalculateMipChainSize(uint first_mip = 0)
    {
        ulong size = 0;
        for (uint array_index = 0; array_index < GetArrayLength(); array_index++)
            for (uint mip = first_mip; mip < m_mip_count; mip++)
                size += CalculateMipSize(System.Math.Max(1u, m_width >> (int)mip), System.Math.Max(1u, m_height >> (int)mip),
                    m_type == RHI_Texture_Type.Type3D ? System.Math.Max(1u, m_depth >> (int)mip) : 1, m_format, m_bits_per_channel, m_channel_count);
        return size;
    }

    // spartan: CalculateMipSize. block compressed formats by 4x4 blocks, the rest by rhi_format_to_bytes (upstream
    // 67a6e8b replaced bits/8 * channels, which gave 3 bytes for R11G11B10). bits/channels are kept for the signature.
    public static ulong CalculateMipSize(uint width, uint height, uint depth, RHI_Format format, uint bits_per_channel, uint channel_count)
    {
        if (width == 0 || height == 0 || depth == 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (IsCompressedFormat(format))
        {
            ulong block_size = format == RHI_Format.BC1_Unorm ? 8UL : 16UL; // BC3/BC5/BC7/ASTC 4x4: 16
            ulong blocks_wide = (width + 3UL) / 4, blocks_high = (height + 3UL) / 4;
            return blocks_wide * blocks_high * depth * block_size;
        }
        return (ulong)width * height * depth * RHI_Definitions.rhi_format_to_bytes(format);
    }

    public static ulong CalculateMipSize(uint width, uint height, uint depth, RHI_Format format) =>
        CalculateMipSize(width, height, depth, format, RHI_Definitions.rhi_format_to_bits_per_channel(format), RHI_Definitions.rhi_to_format_channel_count(format));

    // ------------------------------------------------------------------------------------------------------------
    // gpu

    public void PrepareForGpu()
    {
        if (m_rhi_resource.Handle != 0)
            return;
        RHI_CreateResource();
    }

    // spartan: build_copy_regions. one region per mip per slice, offsets aligned to optimalBufferCopyOffsetAlignment
    public static (BufferImageCopy[] regions, ulong size) BuildCopyRegions(RHI_Texture_Type type, uint width, uint height, uint depth,
        uint mip_count, RHI_Format format, ImageAspectFlags aspect, ulong buffer_alignment)
    {
        uint array_length = type == RHI_Texture_Type.Type3D ? 1 : depth;
        var regions = new BufferImageCopy[array_length * mip_count];
        ulong offset = 0;
        for (uint array_index = 0; array_index < array_length; array_index++)
        {
            for (uint mip = 0; mip < mip_count; mip++)
            {
                uint mip_width  = System.Math.Max(1u, width >> (int)mip);
                uint mip_height = System.Math.Max(1u, height >> (int)mip);
                uint mip_depth  = type == RHI_Texture_Type.Type3D ? System.Math.Max(1u, depth >> (int)mip) : 1;
                offset = RHI_Allocator_FreeList.AlignUp(offset, buffer_alignment);
                regions[mip + array_index * mip_count] = new BufferImageCopy
                {
                    BufferOffset     = offset,
                    ImageSubresource = new ImageSubresourceLayers(aspect, mip, array_index, 1),
                    ImageExtent      = new Extent3D(mip_width, mip_height, mip_depth)
                };
                offset += CalculateMipSize(mip_width, mip_height, mip_depth, format);
            }
        }
        return (regions, offset);
    }

    private void Stage()
    {
        var (regions, staging_size) = BuildCopyRegions(m_type, m_width, m_height, m_depth, m_mip_count, m_format,
                                                      GetAspectMask(), RHI_Device.PropertyOptimalBufferCopyOffsetAlignment);
        RHI_Device.StagingBudgetAcquire(staging_size);
        RHI_StagingBuffer staging = RHI_Device.StagingBufferAcquire(staging_size);
        try
        {
            // copy_to_staging_buffer: the region carries the aligned offset the gpu will read from
            byte* mapped = (byte*)staging.MappedData;
            for (uint array_index = 0; array_index < GetArrayLength(); array_index++)
                for (uint mip = 0; mip < m_mip_count; mip++)
                {
                    BufferImageCopy region = regions[mip + array_index * m_mip_count];
                    ulong size = CalculateMipSize(region.ImageExtent.Width, region.ImageExtent.Height, region.ImageExtent.Depth, m_format);
                    if (GetMip(array_index, mip) is { bytes.Length: > 0 } data)
                        data.bytes.AsSpan(0, (int)System.Math.Min(size, (ulong)data.bytes.Length)).CopyTo(new Span<byte>(mapped + region.BufferOffset, (int)size));
                }

            RHI_CommandList cmd_list = RHI_CommandList.ImmediateExecutionBegin(RHI_Queue_Type.Graphics);
            cmd_list.PrepareTextureForUpload(this);
            fixed (BufferImageCopy* p = regions)
                RHI_Context.Vk.CmdCopyBufferToImage(cmd_list.GetRhiResource(), staging.Buffer, m_rhi_resource, ToVulkan(RHI_Image_Layout.General), (uint)regions.Length, p);
            cmd_list.InsertBarrierMemory();
            RHI_CommandList.ImmediateExecutionEnd(cmd_list);
        }
        finally
        {
            RHI_Device.StagingBufferRelease(staging);
            RHI_Device.StagingBudgetRelease(staging_size);
        }
    }

    private void RHI_CreateResource()
    {
        if (m_width == 0 || m_height == 0) throw new InvalidOperationException($"{Name}: width/height can't be zero");

        m_allocation = RHI_Device.MemoryTextureCreate(this, out m_rhi_resource);

        if (HasData())
            Stage();

        // staging already parks the image in general, otherwise transition once
        if (GetLayout(0) != RHI_Image_Layout.General)
        {
            RHI_CommandList cmd_list = RHI_CommandList.ImmediateExecutionBegin(RHI_Queue_Type.Graphics);
            cmd_list.InsertBarrier(this, RHI_Image_Layout.General, 0, m_mip_count);
            RHI_CommandList.ImmediateExecutionEnd(cmd_list);
        }

        // shader resource views
        if (IsSrv() || IsUav())
        {
            m_rhi_srv = CreateImageView(0, m_depth, 0, m_mip_count, "srv");
            if (HasPerMipViews())
                for (uint i = 0; i < m_mip_count; i++)
                    m_rhi_srv_mips[i] = CreateImageView(0, m_depth, i, 1, $"srv_mip_{i}");
            // per-layer 2d views for array textures
            if (m_type == RHI_Texture_Type.Type2DArray && m_depth > 1)
                for (uint i = 0; i < System.Math.Min(m_depth, (uint)RHI_Definitions.rhi_max_render_target_count); i++)
                    m_rhi_srv_layers[i] = CreateImageView2DLayer(i, 0, m_mip_count, $"srv_layer_{i}");
        }

        // render target views
        if (m_type is RHI_Texture_Type.Type2D or RHI_Texture_Type.Type2DArray or RHI_Texture_Type.TypeCube)
        {
            // cube faces and array layers are both encoded into m_depth
            for (uint i = 0; i < System.Math.Min(m_depth, (uint)RHI_Definitions.rhi_max_render_target_count); i++)
            {
                if (IsRtv()) m_rhi_rtv[i] = CreateImageView(i, 1, 0, 1, $"rtv_{i}");
                if (IsDsv()) m_rhi_dsv[i] = CreateImageView(i, 1, 0, 1, $"dsv_{i}");
            }
            if (m_type == RHI_Texture_Type.Type2DArray && m_depth > 1)
            {
                if (IsRtv()) m_rhi_rtv_multiview = CreateImageView(0, m_depth, 0, 1, "rtv_multiview");
                if (IsDsv()) m_rhi_dsv_multiview = CreateImageView(0, m_depth, 0, 1, "dsv_multiview");
            }
        }
        else if (m_type == RHI_Texture_Type.Type3D)
        {
            if (IsRtv()) m_rhi_rtv[0] = CreateImageView(0, 1, 0, 1, "rtv");
        }
    }

    // spartan: create_image_view. cube views always cover the 6 faces, 3d views one layer
    private ImageView CreateImageView(uint array_index, uint array_length, uint mip_index, uint mip_count, string suffix)
    {
        ImageViewType view_type = m_type switch
        {
            RHI_Texture_Type.Type2D      => ImageViewType.Type2D,
            RHI_Texture_Type.Type2DArray => ImageViewType.Type2DArray,
            RHI_Texture_Type.Type3D      => ImageViewType.Type3D,
            RHI_Texture_Type.TypeCube    => ImageViewType.TypeCube,
            _ => throw new InvalidOperationException("Unknown resource type")
        };
        (uint base_layer, uint layer_count) = m_type switch
        {
            RHI_Texture_Type.TypeCube => (0u, 6u),
            RHI_Texture_Type.Type3D   => (0u, 1u),
            _                         => (array_index, array_length)
        };
        return CreateView(view_type, base_layer, layer_count, mip_index, mip_count, suffix);
    }

    // spartan: create_image_view_2d_layer
    private ImageView CreateImageView2DLayer(uint array_index, uint mip_index, uint mip_count, string suffix) =>
        CreateView(ImageViewType.Type2D, array_index, 1, mip_index, mip_count, suffix);

    private ImageView CreateView(ImageViewType view_type, uint base_layer, uint layer_count, uint mip_index, uint mip_count, string suffix)
    {
        ImageViewCreateInfo info = new()
        {
            SType            = StructureType.ImageViewCreateInfo,
            Image            = m_rhi_resource,
            ViewType         = view_type,
            Format           = ToVulkan(m_format),
            SubresourceRange = new ImageSubresourceRange(GetAspectMask(only_depth: IsDepthFormat()), mip_index, mip_count, base_layer, layer_count)
        };
        Check(RHI_Context.Vk.CreateImageView(RHI_Context.Device, in info, null, out ImageView view), "vkCreateImageView");
        RHI_Device.SetResourceName(view.Handle, RHI_Resource_Type.ImageView, $"{Name}_{suffix}");
        return view;
    }

    public void RHI_DestroyResource()
    {
        if (m_rhi_resource.Handle != 0)
            RHI_Device.DescriptorSetInvalidateReferencingResource(this); // spartan Vulkan_Texture.cpp:552
        static void Queue(ref ImageView view)
        {
            if (view.Handle == 0) return;
            RHI_Device.DeletionQueueAdd(RHI_Resource_Type.ImageView, view.Handle);
            view = default;
        }
        Queue(ref m_rhi_srv);
        for (int i = 0; i < m_rhi_srv_mips.Length; i++) Queue(ref m_rhi_srv_mips[i]);
        for (int i = 0; i < m_rhi_srv_layers.Length; i++) Queue(ref m_rhi_srv_layers[i]);
        for (int i = 0; i < m_rhi_rtv.Length; i++) { Queue(ref m_rhi_rtv[i]); Queue(ref m_rhi_dsv[i]); }
        Queue(ref m_rhi_rtv_multiview);
        Queue(ref m_rhi_dsv_multiview);

        ClearLayouts();
        if (m_rhi_resource.Handle != 0)
            RHI_Device.DeletionQueueAdd(RHI_Resource_Type.Image, m_rhi_resource.Handle, m_allocation);
        m_rhi_resource = default;
        m_allocation   = null;
    }

    public void Dispose() => RHI_DestroyResource();
}
