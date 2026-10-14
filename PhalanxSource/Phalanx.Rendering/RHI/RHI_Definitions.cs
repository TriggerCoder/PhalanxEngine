// Port of SpartanEngine source/rhi/RHI_Definitions.h @ 4c07baa (the enums the first RHI slice needs).
// TODO(port): the remaining enums (barrier types).
namespace Phalanx.Rendering.RHI;

internal enum RHI_PhysicalDevice_Type
{
    Integrated,
    Discrete,
    Virtual,
    Cpu,
    Max
}

internal enum RHI_Present_Mode
{
    Immediate, // doesn't wait.                  frames are not dropped. tearing.    full on.
    Mailbox,   // waits for v-blank.             frames are dropped.     no tearing. minimizes latency.
    Fifo,      // waits for v-blank, every time. frames are not dropped. no tearing. minimizes stuttering.
}

internal enum RHI_Queue_Type
{
    Graphics,
    Compute,
    Copy,
    Present,
    Max
}

// order matters: it indexes RHI_Implementation.VulkanFormat
internal enum RHI_Format
{
    // R
    R8_Unorm,
    R8_Uint,
    R16_Unorm,
    R16_Uint,
    R16_Float,
    R32_Uint,
    R32_Float,
    // Rg
    R8G8_Unorm,
    R16G16_Float,
    R32G32_Float,
    // Rgb
    R11G11B10_Float,
    R32G32B32_Float,
    // Rgba
    R8G8B8A8_Unorm,
    R10G10B10A2_Unorm,
    R16G16B16A16_Unorm,
    R16G16B16A16_Snorm,
    R16G16B16A16_Float,
    R32G32B32A32_Float,
    // Depth
    D16_Unorm,
    D32_Float,
    D32_Float_S8X24_Uint,
    // Compressed
    BC1_Unorm,
    BC3_Unorm,
    BC5_Unorm,
    BC7_Unorm,
    ASTC,
    // Surface
    B8R8G8A8_Unorm,
    // End
    Max
}

// order matters: it indexes RHI_Implementation.VulkanImageLayout
internal enum RHI_Image_Layout
{
    General,
    Shading_Rate_Attachment,
    Present_Source,
    Max
}

// order matters: it indexes RHI_Implementation.VulkanObjectType
internal enum RHI_Resource_Type
{
    Fence,
    Semaphore,
    Shader,
    Sampler,
    QueryPool,
    DeviceMemory,
    Buffer,
    CommandList,
    CommandPool,
    Image,
    ImageView,
    DescriptorSet,
    DescriptorSetLayout,
    Pipeline,
    PipelineLayout,
    Queue,
    AccelerationStructure,
    Max
}

// order matters: it indexes RHI_Implementation.VulkanFilter / VulkanMipmapMode
internal enum RHI_Filter
{
    Nearest,
    Linear,
}

// order matters: it indexes RHI_Implementation.VulkanSamplerAddressMode
internal enum RHI_Sampler_Address_Mode
{
    Wrap,
    Mirror,
    Clamp,
    ClampToZero,
    MirrorOnce,
}

// order matters: it indexes RHI_Implementation.VulkanCompareOperator
internal enum RHI_Comparison_Function
{
    Never,
    Less,
    Equal,
    LessEqual,
    Greater,
    NotEqual,
    GreaterEqual,
    Always
}

// order matters: it indexes RHI_Implementation.VulkanPrimitiveTopology
internal enum RHI_PrimitiveTopology
{
    TriangleList,
    LineList,
    PointList,
    Max
}

// order matters: it indexes RHI_Implementation.VulkanCullMode
internal enum RHI_CullMode
{
    Back,
    Front,
    None,
    Max
}

// order matters: it indexes RHI_Implementation.VulkanPolygonMode
internal enum RHI_PolygonMode
{
    Solid,
    Wireframe,
    Max
}

// order matters: it indexes RHI_Implementation.VulkanStencilOperation
internal enum RHI_Stencil_Operation
{
    Keep,
    Zero,
    Replace,
    Incr_Sat,
    Decr_Sat,
    Invert,
    Incr,
    Decr
}

// the vertex layouts of RHI_Vertex.cs (Phalanx.Core), Max means no vertex buffer (the shader makes its own vertices)
internal enum RHI_Vertex_Type
{
    Pos,
    PosCol,
    PosUv,
    PosUvNorTan,
    Pos2dUvCol8,
    Max
}

// order matters: it indexes RHI_Implementation.VulkanBlendFactor
internal enum RHI_Blend
{
    Zero,
    One,
    Src_Color,
    Inv_Src_Color,
    Src_Alpha,
    Inv_Src_Alpha,
    Dest_Alpha,
    Inv_Dest_Alpha,
    Dest_Color,
    Inv_Dest_Color,
    Src_Alpha_Sat,
    Blend_Factor,
    Inv_Blend_Factor,
    Src1_Color,
    Inv_Src1_Color,
    Src1_Alpha,
    Inv_Src1_Alpha
}

// order matters: it indexes RHI_Implementation.VulkanBlendOperation
internal enum RHI_Blend_Operation
{
    Add,
    Subtract,
    Rev_Subtract,
    Min,
    Max,
    Undefined
}

internal enum RHI_Descriptor_Type
{
    Image,
    TextureStorage,
    PushConstantBuffer,
    ConstantBuffer,
    StructuredBuffer,
    AccelerationStructure,
    // phalanx (GLSL): reflected so a misuse is reported, spartan's dxc path never produces them. Only valid as bindless
    // (the sampler arrays in sets 6/7), a per-pass sampler or sampler2D is rejected by RHI_DescriptorSetLayout.
    Sampler,
    CombinedImageSampler,
    Max
}

// spartan's names (Pixel = fragment, MeshShader = mesh)
internal enum RHI_Shader_Type
{
    Vertex,
    Hull,
    Domain,
    Pixel,
    Compute,
    RayGeneration,
    RayMiss,
    RayHit,
    MeshShader,
    Max
}

// must match the order of appearance in common_resources.glsl (spartan: common_resources_bindless.hlsl),
// the vulkan set index of each is (int)value + 1, set 0 is the per-pass set
internal enum RHI_Device_Bindless_Resource
{
    MaterialTextures,
    MaterialParameters,
    LightParameters,
    Aabbs,
    DrawData,
    SamplersComparison,
    SamplersRegular,
    GeometryVertices,
    GeometryIndices,
    Instances,
    Max
}

internal enum RHI_SyncPrimitive_Type
{
    Fence,
    Semaphore,
    SemaphoreTimeline,
    Max
}

internal enum RHI_CommandListState
{
    Idle,
    Recording,
    Submitted
}

// constants and format helpers of RHI_Definitions.h (spartan names kept, snake_case like the original)
internal static class RHI_Definitions
{
    public const uint rhi_max_mip_count           = 13;
    public const uint rhi_all_mips                = uint.MaxValue;
    public const int  rhi_max_render_target_count = 8;

    // shader register slot shifts (spartan: required to produce spirv from hlsl; phalanx's glsl uses the shifted
    // bindings directly, see common_resources.glsl). 000-099 is the push constant buffer range.
    public const uint rhi_shader_register_shift_u   = 100;
    public const uint rhi_shader_register_shift_b   = 200;
    public const uint rhi_shader_register_shift_t   = 300;
    public const uint rhi_shader_register_shift_s   = 400;
    public const uint rhi_max_resource_slots        = 96;
    public const uint rhi_max_dynamic_offsets       = 16; // structured + constant buffers per pipeline
    public const uint rhi_max_array_size            = 16384;
    public const uint rhi_max_sampler_count         = 9;
    public const uint rhi_max_descriptor_set_count  = 512;
    public const uint rhi_dynamic_offset_empty      = uint.MaxValue;

    // load/clear sentinels of the pipeline state, anything else is a clear value
    public static readonly Phalanx.Math.Color rhi_color_dont_care = new(float.MaxValue, 0.0f, 0.0f, 0.0f);
    public static readonly Phalanx.Math.Color rhi_color_load      = new(float.PositiveInfinity, 0.0f, 0.0f, 0.0f);
    public const float rhi_depth_dont_care   = float.MaxValue;
    public const float rhi_depth_load        = float.PositiveInfinity;
    public const uint  rhi_stencil_dont_care = uint.MaxValue;
    // spartan: numeric_limits<uint32_t>::infinity(), which is 0 for an integer type (RHI_Definitions.h:650), so a
    // stencil clear to 0 reads as "load". Here it is a value no stencil buffer can hold.
    public const uint  rhi_stencil_load      = uint.MaxValue - 1;

    public static ulong rhi_hash_combine(ulong a, ulong b) => unchecked(a * 31 + b);

    public static uint rhi_shader_type_to_mask(RHI_Shader_Type type) => type switch
    {
        RHI_Shader_Type.Vertex        => 1u << 0,
        RHI_Shader_Type.Hull          => 1u << 1,
        RHI_Shader_Type.Domain        => 1u << 2,
        RHI_Shader_Type.Pixel         => 1u << 3,
        RHI_Shader_Type.Compute       => 1u << 4,
        RHI_Shader_Type.RayGeneration => 1u << 5,
        RHI_Shader_Type.RayMiss       => 1u << 6,
        RHI_Shader_Type.RayHit        => 1u << 7,
        RHI_Shader_Type.MeshShader    => 1u << 8,
        _                             => 0
    };

    public static uint rhi_format_to_bits_per_channel(RHI_Format format) => format switch
    {
        RHI_Format.R8_Unorm or RHI_Format.R8_Uint or RHI_Format.R8G8_Unorm or RHI_Format.R8G8B8A8_Unorm
            or RHI_Format.B8R8G8A8_Unorm or RHI_Format.BC5_Unorm or RHI_Format.BC7_Unorm or RHI_Format.ASTC => 8,
        RHI_Format.R16_Unorm or RHI_Format.R16_Uint or RHI_Format.R16_Float or RHI_Format.R16G16_Float
            or RHI_Format.R16G16B16A16_Unorm or RHI_Format.R16G16B16A16_Snorm or RHI_Format.R16G16B16A16_Float or RHI_Format.D16_Unorm => 16,
        RHI_Format.R32_Uint or RHI_Format.R32_Float or RHI_Format.R32G32_Float or RHI_Format.R32G32B32_Float
            or RHI_Format.R32G32B32A32_Float or RHI_Format.D32_Float or RHI_Format.D32_Float_S8X24_Uint => 32,
        RHI_Format.R11G11B10_Float => 11,
        RHI_Format.R10G10B10A2_Unorm => 10,
        RHI_Format.BC1_Unorm or RHI_Format.BC3_Unorm => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), "unhandled rhi_format_to_bits_per_channel() case")
    };

    public static uint rhi_to_format_channel_count(RHI_Format format) => format switch
    {
        RHI_Format.R8_Unorm or RHI_Format.R8_Uint or RHI_Format.R16_Unorm or RHI_Format.R16_Uint or RHI_Format.R16_Float
            or RHI_Format.R32_Uint or RHI_Format.R32_Float or RHI_Format.D16_Unorm or RHI_Format.D32_Float => 1,
        RHI_Format.R8G8_Unorm or RHI_Format.R16G16_Float or RHI_Format.R32G32_Float or RHI_Format.D32_Float_S8X24_Uint or RHI_Format.BC5_Unorm => 2,
        RHI_Format.R11G11B10_Float or RHI_Format.R32G32B32_Float or RHI_Format.BC1_Unorm => 3,
        RHI_Format.R8G8B8A8_Unorm or RHI_Format.R10G10B10A2_Unorm or RHI_Format.R16G16B16A16_Unorm or RHI_Format.R16G16B16A16_Snorm
            or RHI_Format.R16G16B16A16_Float or RHI_Format.R32G32B32A32_Float or RHI_Format.BC3_Unorm or RHI_Format.BC7_Unorm
            or RHI_Format.ASTC or RHI_Format.B8R8G8A8_Unorm => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format), "unhandled rhi_to_format_channel_count() case")
    };

    public static uint rhi_format_to_bytes(RHI_Format format) => format switch
    {
        RHI_Format.R8_Unorm or RHI_Format.R8_Uint => 1,
        RHI_Format.R16_Unorm or RHI_Format.R16_Uint or RHI_Format.R16_Float or RHI_Format.R8G8_Unorm or RHI_Format.D16_Unorm => 2,
        RHI_Format.R32_Uint or RHI_Format.R32_Float or RHI_Format.R16G16_Float or RHI_Format.R11G11B10_Float or RHI_Format.R8G8B8A8_Unorm
            or RHI_Format.R10G10B10A2_Unorm or RHI_Format.D32_Float or RHI_Format.B8R8G8A8_Unorm => 4,
        RHI_Format.R32G32_Float or RHI_Format.R16G16B16A16_Unorm or RHI_Format.R16G16B16A16_Snorm or RHI_Format.R16G16B16A16_Float
            or RHI_Format.D32_Float_S8X24_Uint => 8,
        RHI_Format.R32G32B32_Float => 12,
        RHI_Format.R32G32B32A32_Float => 16,
        // upstream 67a6e8b: compressed formats have no per-texel size, use RHI_Texture.CalculateMipSize (block based)
        RHI_Format.BC1_Unorm or RHI_Format.BC3_Unorm or RHI_Format.BC5_Unorm or RHI_Format.BC7_Unorm or RHI_Format.ASTC =>
            throw new ArgumentException($"{format}: compressed formats require block-based sizing (RHI_Texture.CalculateMipSize)", nameof(format)),
        _ => 4 // spartan's default fallback
    };
}
