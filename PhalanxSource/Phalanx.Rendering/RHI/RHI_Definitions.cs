// Port of SpartanEngine source/rhi/RHI_Definitions.h @ 4c07baa (the enums the first RHI slice needs).
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
