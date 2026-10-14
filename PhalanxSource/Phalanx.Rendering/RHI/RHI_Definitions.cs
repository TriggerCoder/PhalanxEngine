// Port of SpartanEngine source/rhi/RHI_Definitions.h @ 4c07baa (the enums the first RHI slice needs).
// Step 3 adds the RHI_Definitions constants/format helpers.
// TODO(port): the remaining enums (blend, depth-stencil, rasterizer, shader stage, descriptor, barrier types).
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
