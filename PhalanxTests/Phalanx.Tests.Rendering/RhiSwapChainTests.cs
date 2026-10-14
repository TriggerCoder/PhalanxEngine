using Phalanx.Rendering.RHI;
using Silk.NET.Vulkan;
using Xunit;
using static Phalanx.Rendering.RHI.RHI_Texture_Flags;

namespace Phalanx.Tests;

// no gpu: swapchain selection rules (spartan Vulkan_SwapChain.cpp @ 4c07baa)
public class RhiSwapChainLogicTests
{
    private static readonly (Format, ColorSpaceKHR)[] Sdr = { (Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr), (Format.R8G8B8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr) };
    private static readonly (Format, ColorSpaceKHR)[] Hdr = Sdr.Append((Format.A2B10G10R10UnormPack32, ColorSpaceKHR.SpaceHdr10ST2084Ext)).ToArray();

    [Theory]
    [InlineData((int)RHI_Present_Mode.Immediate)]
    [InlineData((int)RHI_Present_Mode.Mailbox)]
    [InlineData((int)RHI_Present_Mode.Fifo)]
    public void Present_mode_falls_back_to_fifo(int mode)
    {
        Assert.Equal(PresentModeKHR.FifoKhr, RHI_SwapChain.SelectPresentMode((RHI_Present_Mode)mode, new[] { PresentModeKHR.FifoKhr }));
        var all = new[] { PresentModeKHR.FifoKhr, PresentModeKHR.MailboxKhr, PresentModeKHR.ImmediateKhr };
        Assert.Equal(RHI_Implementation.ToVulkan((RHI_Present_Mode)mode), RHI_SwapChain.SelectPresentMode((RHI_Present_Mode)mode, all));
    }

    [Fact]
    public void Hdr10_selects_pq_color_space_or_falls_back_to_sdr()
    {
        Assert.Equal(ColorSpaceKHR.SpaceHdr10ST2084Ext, RHI_SwapChain.get_color_space(RHI_Format.R10G10B10A2_Unorm));
        Assert.Equal(ColorSpaceKHR.SpaceSrgbNonlinearKhr, RHI_SwapChain.get_color_space(RHI_Format.R8G8B8A8_Unorm));

        var hdr = RHI_SwapChain.SelectFormat(RHI_SwapChain.FormatHdr, Hdr, is_nvidia: false);
        Assert.Equal((RHI_Format.R10G10B10A2_Unorm, ColorSpaceKHR.SpaceHdr10ST2084Ext, false), hdr);

        var fallback = RHI_SwapChain.SelectFormat(RHI_SwapChain.FormatHdr, Sdr, is_nvidia: false);
        Assert.Equal((RHI_Format.R8G8B8A8_Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr, true), fallback);

        // nvidia swaps rgba for bgra (spartan :132), and bgra-only surfaces fall back to bgra on any vendor
        Assert.Equal(RHI_Format.B8R8G8A8_Unorm, RHI_SwapChain.SelectFormat(RHI_SwapChain.FormatSdr, Sdr, is_nvidia: true).format);
        var bgra_only = new[] { (Format.B8G8R8A8Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr) };
        Assert.Equal((RHI_Format.B8R8G8A8_Unorm, ColorSpaceKHR.SpaceSrgbNonlinearKhr, true), RHI_SwapChain.SelectFormat(RHI_SwapChain.FormatSdr, bgra_only, false));
    }

    [Fact]
    public void Extent_follows_locked_surface_or_clamps()
    {
        var min = new Extent2D(16, 16); var max = new Extent2D(4096, 2048);
        Assert.Equal((800u, 600u), RHI_SwapChain.SelectExtent(new Extent2D(800, 600), min, max, 1920, 1080));
        var free = new Extent2D(uint.MaxValue, uint.MaxValue);
        Assert.Equal((1920u, 1080u), RHI_SwapChain.SelectExtent(free, min, max, 1920, 1080));
        Assert.Equal((4096u, 2048u), RHI_SwapChain.SelectExtent(free, min, max, 10000, 10000));
        Assert.Equal((16u, 16u), RHI_SwapChain.SelectExtent(free, min, max, 1, 1));
    }
}

// lavapipe + xvfb, validation on
[Collection("gpu")]
public class RhiSwapChainGpuTests
{
    private static void SkipIfNoGpu()
    {
        Skip.IfNot(Phalanx.Rendering.RenderDevice.IsVulkanAvailable(out string reason), reason);
    }

    private static void SkipIfNoDisplay()
    {
        Skip.If(OperatingSystem.IsLinux() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) &&
                string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")), "no display (run under xvfb-run to include it)");
    }

    private static void AssertClean() =>
        Assert.True(!RHI_Device.ValidationMessages.Any(m => m.Contains("ErrorBit", StringComparison.Ordinal)), string.Join("\n", RHI_Device.ValidationMessages));

    private static void RunWindowed(Action<Silk.NET.Windowing.IWindow, RHI_SwapChain> body, RHI_Present_Mode mode = RHI_Present_Mode.Fifo)
    {
        SkipIfNoGpu();
        SkipIfNoDisplay();
        var options = Silk.NET.Windowing.WindowOptions.DefaultVulkan with { Size = new Silk.NET.Maths.Vector2D<int>(320, 240), Title = "phalanx swapchain test" };
        using Silk.NET.Windowing.IWindow window = Silk.NET.Windowing.Window.Create(options);
        window.Initialize();
        RHI_Device.Initialize(RHI_ClearSample.GetWindowExtensions(window), validation: true);
        try
        {
            RHI_SwapChain swapchain = RHI_Device.CreateSwapChain(window, mode);
            body(window, swapchain);
            RHI_Device.QueueWaitAll();
            RHI_Device.DeletionQueueFlush();
            AssertClean();
        }
        finally
        {
            RHI_SwapChain.IsMinimizedOverride = null;
            RHI_Device.Destroy();
        }
    }

    private static void ClearFrame(RHI_SwapChain swapchain)
    {
        Assert.True(RHI_Device.AcquireSwapChainImage());
        RHI_CommandList cmd = RHI_Device.BeginFrame();
        swapchain.SetLayout(ImageLayout.Undefined);
        cmd.InsertBarrier(swapchain, ImageLayout.General);
        cmd.ClearRenderTarget(swapchain.GetRhiRtv(), ImageLayout.General, swapchain.Width, swapchain.Height, 0, 0, 1, 1);
        RHI_Device.EndFrame();
    }

    [SkippableFact]
    public void Resize_recreates_the_swapchain_several_times()
    {
        RunWindowed((window, swapchain) =>
        {
            ClearFrame(swapchain);
            int creates = swapchain.CreateCount;
            (int w, int h)[] sizes = { (200, 150), (400, 300), (128, 96), (320, 240) };
            foreach (var (w, h) in sizes)
            {
                // the os resize reaches the swapchain through the window's framebuffer resize callback
                window.Size = new Silk.NET.Maths.Vector2D<int>(w, h);
                for (int i = 0; i < 20 && window.FramebufferSize.X != w; i++) { window.DoEvents(); Thread.Sleep(10); }
                window.DoEvents();
                Assert.Equal(((uint)w, (uint)h), (swapchain.Width, swapchain.Height));
                for (int i = 0; i < 3; i++)
                    ClearFrame(swapchain);
            }
            Assert.Equal(creates + sizes.Length, swapchain.CreateCount);
            swapchain.Resize(320, 240); // same size: no recreation (spartan early-out)
            Assert.Equal(creates + sizes.Length, swapchain.CreateCount);
            swapchain.Resize(0, 0);     // invalid: ignored
            Assert.Equal(creates + sizes.Length, swapchain.CreateCount);
        });
    }

    [SkippableFact]
    public void Vsync_switch_recreates_with_the_new_present_mode()
    {
        RunWindowed((window, swapchain) =>
        {
            Assert.True(swapchain.GetVsync());
            Assert.Equal(PresentModeKHR.FifoKhr, swapchain.GetPresentModeVk());
            ClearFrame(swapchain);

            swapchain.SetVsync(false);
            Assert.True(swapchain.IsDirty());
            Assert.False(swapchain.GetVsync());
            ClearFrame(swapchain); // recreated on acquire/present
            Assert.False(swapchain.IsDirty());
            Assert.Contains(swapchain.GetPresentModeVk(), new[] { PresentModeKHR.ImmediateKhr, PresentModeKHR.FifoKhr });

            swapchain.SetVsync(true);
            ClearFrame(swapchain);
            Assert.Equal(PresentModeKHR.FifoKhr, swapchain.GetPresentModeVk());

            // hdr is not offered by xlib/lavapipe: the request falls back to sdr cleanly
            swapchain.SetHdr(true);
            ClearFrame(swapchain);
            Assert.False(swapchain.IsDirty());
            Assert.Equal(swapchain.IsHdr(), swapchain.GetColorSpace() == ColorSpaceKHR.SpaceHdr10ST2084Ext);
        });
    }

    [SkippableFact]
    public void Blit_and_copy_to_the_back_buffer_read_back()
    {
        RunWindowed((window, swapchain) =>
        {
            uint w = swapchain.Width, h = swapchain.Height;
            byte[] Fill(RHI_Format format, byte r, byte g, byte b)
            {
                var data = new byte[w * h * 4];
                bool bgra = format == RHI_Format.B8R8G8A8_Unorm;
                for (int i = 0; i < data.Length; i += 4) { data[i] = bgra ? b : r; data[i + 1] = g; data[i + 2] = bgra ? r : b; data[i + 3] = 255; }
                return data;
            }
            byte[] ReadBack(RHI_Texture source, bool blit)
            {
                Assert.True(RHI_Device.AcquireSwapChainImage());
                RHI_Allocation memory = RHI_Device.MemoryBufferCreate(out Silk.NET.Vulkan.Buffer buffer, w * h * 4, BufferUsageFlags.TransferDstBit, RHI_Memory_Usage.Readback, default, "backbuffer_readback");
                RHI_CommandList cmd = RHI_Device.BeginFrame();
                if (blit) RHI_Device.BlitToBackBuffer(cmd, source); else cmd.Copy(source, swapchain);
                Assert.Equal(ImageLayout.PresentSrcKhr, swapchain.GetLayout());
                cmd.InsertBarrier(swapchain, ImageLayout.General);
                cmd.CopyImageToBuffer(swapchain.GetRhiRt(), ImageLayout.General, buffer, w, h);
                RHI_Device.EndFrame().Wait();
                var bytes = new byte[w * h * 4];
                System.Runtime.InteropServices.Marshal.Copy(memory.MappedData, bytes, 0, bytes.Length);
                RHI_Device.MemoryBufferDestroy(buffer, memory);
                return bytes;
            }
            byte[] Pixel(byte[] bytes, RHI_Format format) =>
                format == RHI_Format.B8R8G8A8_Unorm ? new[] { bytes[2], bytes[1], bytes[0], bytes[3] } : bytes[..4];

            // blit: rgba8 texture into whatever format the swapchain got (blit converts)
            var slice = new RHI_Texture_Slice(); slice.mips.Add(new RHI_Texture_Mip { bytes = Fill(RHI_Format.R8G8B8A8_Unorm, 255, 128, 0) });
            using (var tex = new RHI_Texture(RHI_Texture_Type.Type2D, w, h, 1, 1, RHI_Format.R8G8B8A8_Unorm, RHI_Texture_Srv | RHI_Texture_ClearBlit, "blit_src", new() { slice }))
                Assert.Equal(new byte[] { 255, 128, 0, 255 }, Pixel(ReadBack(tex, blit: true), swapchain.Format));

            // copy: same size and format
            var slice2 = new RHI_Texture_Slice(); slice2.mips.Add(new RHI_Texture_Mip { bytes = Fill(swapchain.Format, 10, 20, 30) });
            using (var tex = new RHI_Texture(RHI_Texture_Type.Type2D, w, h, 1, 1, swapchain.Format, RHI_Texture_Srv | RHI_Texture_ClearBlit, "copy_src", new() { slice2 }))
                Assert.Equal(new byte[] { 10, 20, 30, 255 }, Pixel(ReadBack(tex, blit: false), swapchain.Format));

            // spartan's asserts: ClearBlit flag and size
            using var no_flag = new RHI_Texture(RHI_Texture_Type.Type2D, 8, 8, 1, 1, RHI_Format.R8G8B8A8_Unorm, RHI_Texture_Srv | RHI_Texture_Rtv, "no_flag");
            Assert.True(RHI_Device.AcquireSwapChainImage());
            RHI_CommandList c = RHI_Device.BeginFrame();
            Assert.Throws<InvalidOperationException>(() => c.Blit(no_flag, swapchain));
            Assert.Throws<InvalidOperationException>(() => c.Copy(no_flag, swapchain));
            RHI_Device.EndFrame().Wait();
        });
    }

    [SkippableFact]
    public void Minimized_window_skips_acquire_and_present()
    {
        RunWindowed((window, swapchain) =>
        {
            ClearFrame(swapchain);
            RHI_SwapChain.IsMinimizedOverride = () => true;
            ulong frames = RHI_Device.FrameIndex;
            for (int i = 0; i < 3; i++)
            {
                Assert.False(RHI_Device.AcquireSwapChainImage());
                // the frame still runs (spartan keeps ticking), it just doesn't present
                RHI_CommandList cmd = RHI_Device.BeginFrame();
                RHI_Device.EndFrame().Wait();
            }
            Assert.Equal(frames + 3, RHI_Device.FrameIndex);
            // a vsync change while minimized is kept until the window comes back (spartan loses it, see rhi4.md)
            swapchain.SetVsync(false);
            Assert.True(swapchain.IsDirty());
            RHI_SwapChain.IsMinimizedOverride = null;
            ClearFrame(swapchain);
            Assert.False(swapchain.IsDirty());
            Assert.False(swapchain.GetVsync());
        });
    }

    [SkippableFact]
    public void Compute_frame_lists_submit_and_wait()
    {
        SkipIfNoGpu();
        RHI_Device.Initialize(Array.Empty<string>(), validation: true);
        try
        {
            using var buffer = new RHI_Buffer(RHI_Buffer_Type.Storage, 4, 64, default, mappable: false, "compute_target");
            for (int frame = 0; frame < 4; frame++)
            {
                RHI_CommandList graphics = RHI_Device.BeginFrame(acquire_compute: true);
                Assert.True(RHI_Device.IsRecording(RHI_Frame_List.ComputeA));
                Assert.True(RHI_Device.IsRecording(RHI_Frame_List.ComputeB));
                Assert.Same(graphics, RHI_Device.Cmd());
                RHI_Device.Bind(RHI_Frame_List.ComputeA);
                Assert.Same(RHI_Device.Cmd(RHI_Frame_List.ComputeA), RHI_Device.Cmd());
                RHI_Device.Bind(RHI_Frame_List.Graphics);

                // A submits early, graphics waits for it on the gpu; B is submitted by EndFrame
                RHI_Work a = RHI_Device.Submit(RHI_Frame_List.ComputeA);
                Assert.False(RHI_Device.IsRecording(RHI_Frame_List.ComputeA));
                RHI_Device.SetFrameWait(a);
                RHI_CommandList b = RHI_Device.Cmd(RHI_Frame_List.ComputeB);
                RHI_Work work = RHI_Device.EndFrame();
                Assert.False(RHI_Device.IsRecording(RHI_Frame_List.ComputeB));
                work.Wait();
                Assert.True(a.IsComplete());
                b.GetWork().Wait();
            }
            // without acquire_compute the compute lists are absent
            RHI_Device.BeginFrame();
            Assert.False(RHI_Device.IsRecording(RHI_Frame_List.ComputeA));
            Assert.Throws<InvalidOperationException>(() => RHI_Device.Cmd(RHI_Frame_List.ComputeA));
            RHI_Device.EndFrame().Wait();
            RHI_Device.QueueWaitAll();
            AssertClean();
        }
        finally
        {
            RHI_Device.Destroy();
        }
    }

    [SkippableFact]
    public void Memory_budget_is_sane_or_absent()
    {
        SkipIfNoGpu();
        RHI_Device.Initialize(Array.Empty<string>(), validation: true);
        try
        {
            ulong total = RHI_Device.MemoryGetTotalMb();
            Assert.True(total > 0);
            if (RHI_Device.MemoryGetBudget(out ulong[] budget, out ulong[] usage, out bool[] local))
            {
                Assert.NotEmpty(budget);
                Assert.Equal(budget.Length, usage.Length);
                Assert.Contains(budget, b => b > 0);
                for (int i = 0; i < budget.Length; i++)
                    Assert.True(usage[i] <= budget[i] || budget[i] == 0, $"heap {i}: usage {usage[i]} > budget {budget[i]}");
            }
            ulong before = RHI_Device.MemoryGetAllocatedMb();
            using var big = new RHI_Buffer(RHI_Buffer_Type.Storage, 1024, 64 * 1024, default, mappable: false, "budget_probe"); // 64 MB
            Assert.True(RHI_Device.MemoryGetAllocatedMb() >= before);
            Assert.True(RHI_Device.MemoryGetAvailableMb() <= RHI_Device.MemoryGetBudgetMb());
            AssertClean();
        }
        finally
        {
            RHI_Device.Destroy();
        }
    }
}
