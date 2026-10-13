// The first-slice frame loop (spartan: the clear part of Renderer::Tick + RHI_Device::BeginFrame/EndFrame), kept
// inside RHI because it touches Vulkan types and unsafe code (architecture rule 10). RenderDevice forwards here.
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace Phalanx.Rendering.RHI;

internal static class RHI_ClearSample
{
    /// <summary>
    /// Opens the RHI on <paramref name="window"/> (created by Phalanx.Platform.Window, not yet initialized is fine),
    /// clears the back buffer to the colour every frame and presents, until the window closes or
    /// <paramref name="max_frames"/> frames were presented (0 = until closed). Returns the number of presented frames.
    /// </summary>
    public static unsafe int RunClearLoop(IWindow window, float r, float g, float b, float a, int max_frames = 0, bool validation = false)
    {
        if (!window.IsInitialized)
            window.Initialize();
        if (window.VkSurface is not { } surface_source)
            throw new NotSupportedException("The window was not created with the Vulkan API");

        byte** names = surface_source.GetRequiredExtensions(out uint count);
        var extensions = new List<string>();
        for (uint i = 0; i < count; i++)
            extensions.Add(Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)names[i])!);

        RHI_Device.Initialize(extensions, validation);
        int presented = 0;
        try
        {
            using var swapchain = new RHI_SwapChain(surface_source, (uint)window.FramebufferSize.X, (uint)window.FramebufferSize.Y,
                                                    RHI_Present_Mode.Fifo, RHI_SwapChain.BufferCount, hdr: false, "swapchain");
            window.FramebufferResize += size => swapchain.Resize((uint)size.X, (uint)size.Y);
            while (!window.IsClosing && (max_frames == 0 || presented < max_frames))
            {
                window.DoEvents();
                if (window.IsClosing)
                    break;

                if (!swapchain.AcquireNextImage())
                    continue;

                RHI_CommandList cmd = RHI_Device.BeginFrame();
                // the whole image is cleared, so the old contents can be discarded (undefined -> general)
                cmd.InsertBarrierImage(swapchain.GetRhiRt(), ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.General);
                cmd.ClearRenderTarget(swapchain.GetRhiRtv(), ImageLayout.General, swapchain.Width, swapchain.Height, r, g, b, a);
                cmd.InsertBarrierImage(swapchain.GetRhiRt(), ImageAspectFlags.ColorBit, ImageLayout.General, ImageLayout.PresentSrcKhr);

                RHI_Work work = RHI_Device.EndFrame(swapchain.GetImageAcquiredSemaphore(), swapchain.GetRenderingCompleteSemaphore());
                swapchain.SetAcquireConsumer(work);
                swapchain.Present();
                presented++;
            }
            RHI_Device.QueueWaitAll();
        }
        finally
        {
            RHI_Device.Destroy();
        }
        return presented;
    }

    /// <summary>
    /// Headless check: creates a device without a window, clears a small offscreen image through dynamic rendering,
    /// copies it back and returns the RGBA8 bytes of the first pixel. Used by the tests (lavapipe works).
    /// </summary>
    public static unsafe byte[] ClearOffscreenAndReadPixel(float r, float g, float b, float a, bool validation, out IReadOnlyList<string> validation_messages)
    {
        const uint size = 4;
        RHI_Device.Initialize(Array.Empty<string>(), validation);
        try
        {
            RHI_Allocation image_memory = RHI_Device.MemoryTextureCreate2D(out Image image, size, size, RHI_Format.R8G8B8A8_Unorm,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit, "offscreen_target");
            RHI_Allocation buffer_memory = RHI_Device.MemoryBufferCreate(out Silk.NET.Vulkan.Buffer buffer, size * size * 4,
                BufferUsageFlags.TransferDstBit, RHI_Memory_Usage.Readback, default, "offscreen_readback");
            ImageViewCreateInfo view_info = new()
            {
                SType            = StructureType.ImageViewCreateInfo,
                Image            = image,
                ViewType         = ImageViewType.Type2D,
                Format           = Silk.NET.Vulkan.Format.R8G8B8A8Unorm,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
            };
            RHI_Implementation.Check(RHI_Context.Vk.CreateImageView(RHI_Context.Device, in view_info, null, out ImageView view), "vkCreateImageView");
            try
            {
                RHI_CommandList cmd = RHI_Device.BeginFrame();
                cmd.InsertBarrierImage(image, ImageAspectFlags.ColorBit, ImageLayout.Undefined, ImageLayout.General);
                cmd.ClearRenderTarget(view, ImageLayout.General, size, size, r, g, b, a);
                cmd.InsertBarrierImage(image, ImageAspectFlags.ColorBit, ImageLayout.General, ImageLayout.General);
                cmd.CopyImageToBuffer(image, ImageLayout.General, buffer, size, size);
                RHI_Device.EndFrame().Wait();
                return new ReadOnlySpan<byte>((void*)buffer_memory.MappedData, 4).ToArray();
            }
            finally
            {
                RHI_Device.QueueWaitAll();
                RHI_Context.Vk.DestroyImageView(RHI_Context.Device, view, null);
                RHI_Device.MemoryTextureDestroy(image, image_memory);
                RHI_Device.MemoryBufferDestroy(buffer, buffer_memory);
            }
        }
        finally
        {
            validation_messages = RHI_Device.ValidationMessages;
            RHI_Device.Destroy();
        }
    }
}
