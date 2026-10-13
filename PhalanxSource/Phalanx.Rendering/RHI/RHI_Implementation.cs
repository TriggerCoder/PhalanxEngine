// Port of SpartanEngine source/rhi/RHI_Implementation.h @ 4c07baa (vulkan side): the RHI_Context globals and the
// enum -> Vk lookup tables. The d3d12 tables are not ported (Phalanx is Vulkan only).
using Silk.NET.Core;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Phalanx.Rendering.RHI;

// spartan: RHI_Context (static members), here owned by RHI_Device
internal static class RHI_Context
{
    public static Vk?             Api;
    public static Instance        Instance;
    public static PhysicalDevice  DevicePhysical;
    public static Device          Device;
    public static uint            ApiVersion;
    public static bool            Validation;
    public static bool            Gpu_Markers;
    public static ExtDebugUtils?  DebugUtils;
    public static DebugUtilsMessengerEXT DebugMessenger;
    public static KhrSurface?     Surface;
    public static KhrSwapchain?   Swapchain;
    public static string[]        ExtensionsInstance = System.Array.Empty<string>();
    public static string[]        ExtensionsDevice   = System.Array.Empty<string>();

    public static Vk Vk => Api ?? throw new InvalidOperationException("RHI_Device.Initialize has not been called");
}

internal static class RHI_Implementation
{
    // indexed by RHI_Format
    public static readonly Format[] VulkanFormat =
    {
        // R
        Format.R8Unorm,
        Format.R8Uint,
        Format.R16Unorm,
        Format.R16Uint,
        Format.R16Sfloat,
        Format.R32Uint,
        Format.R32Sfloat,
        // RG
        Format.R8G8Unorm,
        Format.R16G16Sfloat,
        Format.R32G32Sfloat,
        // RGB
        Format.B10G11R11UfloatPack32,
        Format.R32G32B32Sfloat,
        // RGBA
        Format.R8G8B8A8Unorm,
        Format.A2B10G10R10UnormPack32,
        Format.R16G16B16A16Unorm,
        Format.R16G16B16A16SNorm,
        Format.R16G16B16A16Sfloat,
        Format.R32G32B32A32Sfloat,
        // Depth
        Format.D16Unorm,
        Format.D32Sfloat,
        Format.D32SfloatS8Uint,
        // Compressed
        Format.BC1RgbUnormBlock,
        Format.BC3UnormBlock,
        Format.BC5UnormBlock,
        Format.BC7UnormBlock,
        Format.Astc4x4UnormBlock,
        // Surface
        Format.B8G8R8A8Unorm,
        // Unknown
        Format.Undefined
    };

    // indexed by RHI_Image_Layout
    public static readonly ImageLayout[] VulkanImageLayout =
    {
        ImageLayout.General,
        ImageLayout.FragmentShadingRateAttachmentOptimalKhr,
        ImageLayout.PresentSrcKhr,
        ImageLayout.Undefined
    };

    // indexed by RHI_Resource_Type
    public static readonly ObjectType[] VulkanObjectType =
    {
        ObjectType.Fence,
        ObjectType.Semaphore,
        ObjectType.ShaderModule,
        ObjectType.Sampler,
        ObjectType.QueryPool,
        ObjectType.DeviceMemory,
        ObjectType.Buffer,
        ObjectType.CommandBuffer,
        ObjectType.CommandPool,
        ObjectType.Image,
        ObjectType.ImageView,
        ObjectType.DescriptorSet,
        ObjectType.DescriptorSetLayout,
        ObjectType.Pipeline,
        ObjectType.PipelineLayout,
        ObjectType.Queue,
        ObjectType.AccelerationStructureKhr,
        ObjectType.Unknown
    };

    public static Format ToVulkan(RHI_Format format) => VulkanFormat[(int)format];
    public static ImageLayout ToVulkan(RHI_Image_Layout layout) => VulkanImageLayout[(int)layout];

    public static RHI_Format FromVulkan(Format format)
    {
        int index = Array.IndexOf(VulkanFormat, format);
        return index < 0 ? RHI_Format.Max : (RHI_Format)index;
    }

    public static PresentModeKHR ToVulkan(RHI_Present_Mode mode) => mode switch
    {
        RHI_Present_Mode.Immediate => PresentModeKHR.ImmediateKhr,
        RHI_Present_Mode.Mailbox   => PresentModeKHR.MailboxKhr,
        _                          => PresentModeKHR.FifoKhr,
    };

    public static RHI_PhysicalDevice_Type FromVulkan(PhysicalDeviceType type) => type switch
    {
        PhysicalDeviceType.IntegratedGpu => RHI_PhysicalDevice_Type.Integrated,
        PhysicalDeviceType.DiscreteGpu   => RHI_PhysicalDevice_Type.Discrete,
        PhysicalDeviceType.VirtualGpu    => RHI_PhysicalDevice_Type.Virtual,
        PhysicalDeviceType.Cpu           => RHI_PhysicalDevice_Type.Cpu,
        _                                => RHI_PhysicalDevice_Type.Max,
    };

    public static uint VersionMajor(uint version) => version >> 22;
    public static uint VersionMinor(uint version) => (version >> 12) & 0x3FF;
    public static uint VersionPatch(uint version) => version & 0xFFF;
    public static string VersionToString(uint version) => $"{VersionMajor(version)}.{VersionMinor(version)}.{VersionPatch(version)}";

    // spartan: SP_ASSERT_VK
    public static void Check(Result result, string what)
    {
        if (result != Result.Success)
            throw new RHI_Exception(result, what);
    }
}

internal sealed class RHI_Exception : Exception
{
    public Result Result { get; }
    public RHI_Exception(Result result, string what) : base($"{what} failed: {result}") { Result = result; }
}
