using Phalanx.Rendering;
using Phalanx.Rendering.RHI;
using Silk.NET.Vulkan;
using Xunit;

namespace Phalanx.Tests;

// no gpu needed: tables and selection logic of the RHI (spartan @ 4c07baa RHI_Implementation.h / Vulkan_Device.cpp / Vulkan_SwapChain.cpp)
public class RhiTableTests
{
    [Fact]
    public void Format_table_covers_every_enum_value_plus_unknown()
    {
        Assert.Equal((int)RHI_Format.Max + 1, RHI_Implementation.VulkanFormat.Length);
        Assert.Equal(Format.Undefined, RHI_Implementation.ToVulkan(RHI_Format.Max));
    }

    [Theory]
    [InlineData((int)RHI_Format.R8_Unorm,            Format.R8Unorm)]
    [InlineData((int)RHI_Format.R16_Float,           Format.R16Sfloat)]
    [InlineData((int)RHI_Format.R11G11B10_Float,     Format.B10G11R11UfloatPack32)]
    [InlineData((int)RHI_Format.R8G8B8A8_Unorm,      Format.R8G8B8A8Unorm)]
    [InlineData((int)RHI_Format.R10G10B10A2_Unorm,   Format.A2B10G10R10UnormPack32)]
    [InlineData((int)RHI_Format.R16G16B16A16_Snorm,  Format.R16G16B16A16SNorm)]
    [InlineData((int)RHI_Format.D32_Float_S8X24_Uint, Format.D32SfloatS8Uint)]
    [InlineData((int)RHI_Format.BC1_Unorm,           Format.BC1RgbUnormBlock)]
    [InlineData((int)RHI_Format.ASTC,                Format.Astc4x4UnormBlock)]
    [InlineData((int)RHI_Format.B8R8G8A8_Unorm,      Format.B8G8R8A8Unorm)]
    public void Format_maps_like_spartan(int format_index, Format expected)
    {
        RHI_Format format = (RHI_Format)format_index;
        Assert.Equal(expected, RHI_Implementation.ToVulkan(format));
        Assert.Equal(format, RHI_Implementation.FromVulkan(expected));
    }

    [Fact]
    public void Image_layout_and_object_type_tables_match_their_enums()
    {
        Assert.Equal((int)RHI_Image_Layout.Max + 1, RHI_Implementation.VulkanImageLayout.Length);
        Assert.Equal(ImageLayout.PresentSrcKhr, RHI_Implementation.ToVulkan(RHI_Image_Layout.Present_Source));
        Assert.Equal((int)RHI_Resource_Type.Max + 1, RHI_Implementation.VulkanObjectType.Length);
        Assert.Equal(ObjectType.CommandBuffer, RHI_Implementation.VulkanObjectType[(int)RHI_Resource_Type.CommandList]);
        Assert.Equal(ObjectType.AccelerationStructureKhr, RHI_Implementation.VulkanObjectType[(int)RHI_Resource_Type.AccelerationStructure]);
    }

    [Fact]
    public void Present_modes_and_versions()
    {
        Assert.Equal(PresentModeKHR.ImmediateKhr, RHI_Implementation.ToVulkan(RHI_Present_Mode.Immediate));
        Assert.Equal(PresentModeKHR.MailboxKhr, RHI_Implementation.ToVulkan(RHI_Present_Mode.Mailbox));
        Assert.Equal(PresentModeKHR.FifoKhr, RHI_Implementation.ToVulkan(RHI_Present_Mode.Fifo));
        Assert.Equal("1.3.0", RHI_Implementation.VersionToString(Vk.Version13));
        Assert.Equal(RHI_PhysicalDevice_Type.Cpu, RHI_Implementation.FromVulkan(PhysicalDeviceType.Cpu));
    }

    [Fact]
    public void Command_list_counts_match_spartan()
    {
        Assert.Equal(16, RHI_Queue.CommandListCount(RHI_Queue_Type.Graphics));
        Assert.Equal(12, RHI_Queue.CommandListCount(RHI_Queue_Type.Compute));
        Assert.Equal(4, RHI_Queue.CommandListCount(RHI_Queue_Type.Copy));
    }

    const QueueFlags G = QueueFlags.GraphicsBit, C = QueueFlags.ComputeBit, T = QueueFlags.TransferBit;

    [Fact]
    public void Queue_families_prefer_dedicated_compute_and_copy()
    {
        // typical discrete gpu: 0 = universal, 1 = transfer only, 2 = async compute
        Assert.True(RHI_Device.DetectQueueFamilyIndices(new[] { G | C | T, T, C | T }, out uint g, out uint c, out uint t));
        Assert.Equal((0u, 2u, 1u), (g, c, t));
    }

    [Fact]
    public void Queue_families_fall_back_to_a_single_universal_family()
    {
        // lavapipe and many integrated gpus expose one family
        Assert.True(RHI_Device.DetectQueueFamilyIndices(new[] { G | C | T }, out uint g, out uint c, out uint t));
        Assert.Equal((0u, 0u, 0u), (g, c, t));
        // graphics/compute families imply transfer even when the bit is not reported
        Assert.True(RHI_Device.DetectQueueFamilyIndices(new[] { G | C }, out _, out _, out t));
        Assert.Equal(0u, t);
    }

    [Fact]
    public void Queue_families_without_graphics_fail()
    {
        Assert.False(RHI_Device.DetectQueueFamilyIndices(new[] { C | T, T }, out _, out _, out _));
        Assert.False(RHI_Device.DetectQueueFamilyIndices(Array.Empty<QueueFlags>(), out _, out _, out _));
    }

    [Fact]
    public void Physical_devices_sort_discrete_first_then_by_memory()
    {
        var list = new List<RHI_PhysicalDevice>();
        RHI_PhysicalDevice.Register(list, new RHI_PhysicalDevice(0, 0, 0, RHI_PhysicalDevice_Type.Cpu, "llvmpipe", 1, default));
        RHI_PhysicalDevice.Register(list, new RHI_PhysicalDevice(0, 0, 0x8086, RHI_PhysicalDevice_Type.Integrated, "igpu", 2, default));
        RHI_PhysicalDevice.Register(list, new RHI_PhysicalDevice(0, 0, 0x10DE, RHI_PhysicalDevice_Type.Discrete, "small", 4, default));
        RHI_PhysicalDevice.Register(list, new RHI_PhysicalDevice(0, 0, 0x1002, RHI_PhysicalDevice_Type.Discrete, "big", 16, default));
        Assert.Equal(new[] { "big", "small", "igpu", "llvmpipe" }, list.Select(d => d.Name));
        Assert.True(list[0].IsAmd());
        Assert.Equal("Nvidia", list[1].GetVendorName());
    }

    [Fact]
    public void Driver_and_api_versions_decode_per_vendor()
    {
        RHI_PhysicalDevice Make(uint vendor, uint driver, string info = "", uint api = (1u << 22) | (3u << 12) | 275) =>
            new(api, driver, vendor, RHI_PhysicalDevice_Type.Discrete, "gpu", 8UL << 30, default, info);

        Assert.Equal("1.3.275", Make(0x10DE, 0).GetApiVersion());
        Assert.Equal("1.4.0", RHI_PhysicalDevice.DecodeApiVersion((1u << 29) | (1u << 22) | (4u << 12))); // the variant bits are masked off
        Assert.Equal("535.104.5.0", Make(0x10DE, (535u << 22) | (104u << 14) | (5u << 6)).GetDriverVersion());
        Assert.Equal("24.1.1", Make(0x1002, 0x800000, "24.1.1 (LLPC)").GetDriverVersion());
        Assert.Equal("Unknown AMD driver", Make(0x1002, 0x800000).GetDriverVersion());
        Assert.Equal("101.4502", Make(0x8086, (101u << 14) | 4502).GetDriverVersion());
        Assert.Equal("24.0.5 (Mesa 24.0.5)", Make(0x10005, (24u << 22) | (0u << 12) | 5, "Mesa 24.0.5").GetDriverVersion());
        Assert.Equal("24.0.5", Make(0x10005, (24u << 22) | 5).GetDriverVersion());
        Assert.Equal(8192UL, Make(0x10DE, 0).GetMemoryMb());

        string description = RHI_Device.DescribePhysicalDevice(Make(0x10DE, (535u << 22) | (104u << 14) | (5u << 6)));
        Assert.Equal("gpu (8192 MB), vendor Nvidia, driver 535.104.5.0, Vulkan 1.3.275", description);
    }

    [Fact]
    public void Swapchain_format_present_mode_and_alpha_selection()
    {
        var bgra_only = new[] { (Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr) };
        Assert.Equal(RHI_Format.B8R8G8A8_Unorm, RHI_SwapChain.SelectSdrFormat(bgra_only));
        var both = new[] { (Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr), (Format.R8G8B8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr) };
        Assert.Equal(RHI_Format.R8G8B8A8_Unorm, RHI_SwapChain.SelectSdrFormat(both));
        Assert.Throws<NotSupportedException>(() => RHI_SwapChain.SelectSdrFormat(new[] { (Format.R16G16B16A16Sfloat, ColorSpaceKHR.SpaceSrgbNonlinearKhr) }));

        Assert.Equal(PresentModeKHR.MailboxKhr, RHI_SwapChain.SelectPresentMode(RHI_Present_Mode.Mailbox, new[] { PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr }));
        Assert.Equal(PresentModeKHR.FifoKhr, RHI_SwapChain.SelectPresentMode(RHI_Present_Mode.Immediate, new[] { PresentModeKHR.FifoKhr }));

        Assert.Equal(CompositeAlphaFlagsKHR.OpaqueBitKhr, RHI_SwapChain.SelectCompositeAlpha(CompositeAlphaFlagsKHR.OpaqueBitKhr | CompositeAlphaFlagsKHR.InheritBitKhr));
        Assert.Equal(CompositeAlphaFlagsKHR.InheritBitKhr, RHI_SwapChain.SelectCompositeAlpha(CompositeAlphaFlagsKHR.InheritBitKhr));
    }

    [Fact]
    public void Memory_type_selection_respects_type_bits_and_flags()
    {
        var types = new[]
        {
            MemoryPropertyFlags.DeviceLocalBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
        };
        Assert.Equal(0, RHI_Allocator_Policy.FindMemoryType(0b111, RHI_Memory_Usage.GpuOnly, types));   // not host visible preferred
        Assert.Equal(1, RHI_Allocator_Policy.FindMemoryType(0b111, RHI_Memory_Usage.Upload, types));
        Assert.Equal(2, RHI_Allocator_Policy.FindMemoryType(0b100, RHI_Memory_Usage.Upload, types));
        Assert.Equal(-1, RHI_Allocator_Policy.FindMemoryType(0b001, RHI_Memory_Usage.Readback, types));
    }
}

// needs a vulkan 1.3 driver (any gpu, or mesa lavapipe on linux ci); skipped otherwise.
// the rhi is a set of statics, so these must not run in parallel with each other.
[Collection("gpu")]
public class RhiGpuTests
{
    [SkippableFact]
    public void Offscreen_clear_reads_back_the_clear_colour_without_validation_errors()
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);

        byte[] pixel = RenderDevice.ClearOffscreenAndReadPixel(1.0f, 0.5f, 0.25f, 1.0f, validation: true, out IReadOnlyList<string> messages);

        // unorm8: round(x * 255)
        Assert.Equal(new byte[] { 255, 128, 64, 255 }, pixel);
        Assert.True(!messages.Any(m => m.Contains("ErrorBit", StringComparison.Ordinal)), string.Join("\n", messages));
    }

    [SkippableFact]
    public void Device_initializes_queues_and_shuts_down_twice()
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);

        for (int i = 0; i < 2; i++)
        {
            RHI_Device.Initialize(Array.Empty<string>(), validation: false);
            try
            {
                Assert.True(RHI_Device.IsInitialized);
                Assert.NotEqual(0, RHI_Device.GetQueue(RHI_Queue_Type.Graphics).NativeQueue.Handle);
                Assert.Same(RHI_Device.GetQueue(RHI_Queue_Type.Graphics), RHI_Device.GetQueue(RHI_Queue_Type.Present));
                Assert.True(RHI_Device.PropertyMaxTexture2dDimension >= 4096);
            }
            finally
            {
                RHI_Device.Destroy();
            }
            Assert.False(RHI_Device.IsInitialized);
        }
    }

    [SkippableFact]
    public void Window_clear_presents_frames()
    {
        Skip.IfNot(RenderDevice.IsVulkanAvailable(out string reason), reason);
        Skip.If(OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")), "no display (run under xvfb-run to include it)");

        var options = Silk.NET.Windowing.WindowOptions.DefaultVulkan with { Size = new Silk.NET.Maths.Vector2D<int>(320, 240), Title = "phalanx rhi test" };
        using Silk.NET.Windowing.IWindow window = Silk.NET.Windowing.Window.Create(options);
        int presented = RenderDevice.RunClearLoop(window, 0.392f, 0.584f, 0.929f, 1.0f, maxFrames: 30, validation: true);
        Assert.Equal(30, presented);
        Assert.True(!RHI_Device.ValidationMessages.Any(m => m.Contains("ErrorBit", StringComparison.Ordinal)), string.Join("\n", RHI_Device.ValidationMessages));
    }
}

[CollectionDefinition("gpu", DisableParallelization = true)]
public class GpuCollection { }
