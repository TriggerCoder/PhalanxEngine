// The first-slice frame loop (spartan: the clear part of Renderer::Tick + RHI_Device::BeginFrame/EndFrame), kept
// inside RHI because it touches Vulkan types and unsafe code (architecture rule 10). RenderDevice forwards here.
using Silk.NET.Vulkan;
using Silk.NET.Windowing;

namespace Phalanx.Rendering.RHI;

internal static class RHI_ClearSample
{
    // the instance extensions the window's surface needs
    public static unsafe List<string> GetWindowExtensions(IWindow window)
    {
        if (window.VkSurface is not { } surface_source)
            throw new NotSupportedException("The window was not created with the Vulkan API");
        byte** names = surface_source.GetRequiredExtensions(out uint count);
        var extensions = new List<string>();
        for (uint i = 0; i < count; i++)
            extensions.Add(Silk.NET.Core.Native.SilkMarshal.PtrToString((nint)names[i])!);
        return extensions;
    }

    /// <summary>
    /// Opens the RHI on <paramref name="window"/> (created by Phalanx.Platform.Window, not yet initialized is fine),
    /// clears the back buffer to the colour every frame and presents, until the window closes or
    /// <paramref name="max_frames"/> frames were presented (0 = until closed). Returns the number of presented frames.
    /// </summary>
    public static unsafe int RunClearLoop(IWindow window, float r, float g, float b, float a, int max_frames = 0, bool validation = false, int resize_every = 0)
    {
        if (!window.IsInitialized)
            window.Initialize();
        if (window.VkSurface is not { } surface_source)
            throw new NotSupportedException("The window was not created with the Vulkan API");

        RHI_Device.Initialize(GetWindowExtensions(window), validation);
        int presented = 0, frames = 0;
        var base_size = window.Size;
        try
        {
            RHI_SwapChain swapchain = RHI_Device.CreateSwapChain(window, RHI_Present_Mode.Fifo, hdr: false, "swapchain");
            while (!window.IsClosing && (max_frames == 0 || presented < max_frames))
            {
                // resize demo: alternate between the original and a smaller size, the swapchain follows the event
                if (resize_every > 0 && frames > 0 && frames % resize_every == 0)
                {
                    bool small = (frames / resize_every) % 2 == 1;
                    window.Size = small ? new Silk.NET.Maths.Vector2D<int>(System.Math.Max(64, base_size.X / 2), System.Math.Max(64, base_size.Y / 2)) : base_size;
                }
                frames++;

                window.DoEvents();
                if (window.IsClosing)
                    break;

                if (!RHI_Device.AcquireSwapChainImage())
                {
                    if (max_frames > 0 && frames > max_frames * 4) break; // minimized for too long in a bounded run
                    continue;
                }

                RHI_CommandList cmd = RHI_Device.BeginFrame();
                // the whole image is cleared, so the old contents can be discarded (undefined -> general)
                swapchain.SetLayout(ImageLayout.Undefined);
                cmd.InsertBarrier(swapchain, ImageLayout.General);
                cmd.ClearRenderTarget(swapchain.GetRhiRtv(), ImageLayout.General, swapchain.Width, swapchain.Height, r, g, b, a);
                RHI_Device.EndFrame(); // prepare_for_present + submit + present
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
