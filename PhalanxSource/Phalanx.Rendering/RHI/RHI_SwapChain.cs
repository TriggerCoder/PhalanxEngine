// Port of SpartanEngine source/rhi/RHI_SwapChain.h + vulkan/Vulkan_SwapChain.cpp @ 4c07baa (first slice).
// The surface comes from Silk.NET windowing's IVkSurface (Core's Phalanx.Platform window), so Core never sees Vulkan.
// Differences: no HDR toggle at runtime yet, no RTSS check, acquire-semaphore reuse waits on the consuming command
// list's fence (spartan waits on the consumer's timeline value).
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe class RHI_SwapChain : IDisposable
{
    public const int BufferCount = 2;
    public const int AcquireSemaphoreCount = 3;
    public const RHI_Format FormatSdr = RHI_Format.R8G8B8A8_Unorm;
    public const RHI_Format FormatHdr = RHI_Format.R10G10B10A2_Unorm;

    private SurfaceKHR m_rhi_surface;
    private SwapchainKHR m_rhi_swapchain;
    private Image[] m_rhi_rt = Array.Empty<Image>();
    private ImageView[] m_rhi_rtv = Array.Empty<ImageView>();
    private ImageLayout[] m_layouts = Array.Empty<ImageLayout>();
    private RHI_SyncPrimitive[] m_image_acquired_semaphore = Array.Empty<RHI_SyncPrimitive>();
    private RHI_CommandList?[] m_image_acquired_consumer = Array.Empty<RHI_CommandList?>();
    private RHI_SyncPrimitive[] m_rendering_complete_semaphore = Array.Empty<RHI_SyncPrimitive>();
    private uint m_semaphore_index;
    private uint m_acquired_semaphore_index;
    private uint m_buffer_count;
    private RHI_Format m_format;
    private readonly RHI_Present_Mode m_present_mode;

    public string Name { get; }
    public uint Width { get; private set; }
    public uint Height { get; private set; }
    public uint ImageIndex { get; private set; }
    public bool IsImageAcquired { get; private set; }
    public RHI_Format Format => m_format;
    public uint GetBufferCount() => m_buffer_count;
    public Image GetRhiRt() => m_rhi_rt[ImageIndex];
    public ImageView GetRhiRtv() => m_rhi_rtv[ImageIndex];
    public SwapchainKHR GetRhiResource() => m_rhi_swapchain;

    public RHI_SwapChain(IVkSurface surface_source, uint width, uint height, RHI_Present_Mode present_mode, uint buffer_count, bool hdr, string name)
    {
        if (RHI_Context.Surface == null || RHI_Context.Swapchain == null)
            throw new InvalidOperationException("RHI_Device was initialized without the window's surface extensions");

        Name           = name;
        Width          = width;
        Height         = height;
        m_present_mode = present_mode;
        m_buffer_count = buffer_count;
        m_format       = hdr ? FormatHdr : FormatSdr;

        // create surface once
        VkNonDispatchableHandle handle = surface_source.Create<AllocationCallbacks>(new VkHandle(RHI_Context.Instance.Handle), null);
        m_rhi_surface = new SurfaceKHR(handle.Handle);
        Check(RHI_Context.Surface.GetPhysicalDeviceSurfaceSupport(RHI_Context.DevicePhysical, RHI_Device.GetQueueIndex(RHI_Queue_Type.Graphics), m_rhi_surface, out Silk.NET.Core.Bool32 present_support), "vkGetPhysicalDeviceSurfaceSupportKHR");
        if (!present_support)
            throw new NotSupportedException("The graphics queue does not support presentation to this surface");

        Create();
    }

    public RHI_SyncPrimitive? GetImageAcquiredSemaphore() => IsImageAcquired ? m_image_acquired_semaphore[m_acquired_semaphore_index] : null;
    public RHI_SyncPrimitive? GetRenderingCompleteSemaphore() => IsImageAcquired ? m_rendering_complete_semaphore[ImageIndex] : null;
    public ImageLayout GetLayout() => m_layouts[ImageIndex];
    public void SetLayout(ImageLayout layout) => m_layouts[ImageIndex] = layout;

    public void Resize(uint width, uint height)
    {
        if (width == 0 || height == 0)
            return;
        Width  = width;
        Height = height;
        Create();
    }

    // spartan: SetVsync -> recreate with fifo/immediate (kept for later)

    private void Create()
    {
        KhrSurface khr_surface = RHI_Context.Surface!;
        Check(khr_surface.GetPhysicalDeviceSurfaceCapabilities(RHI_Context.DevicePhysical, m_rhi_surface, out SurfaceCapabilitiesKHR capabilities), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

        // skip if the window is minimized
        if (capabilities.CurrentExtent.Width == 0 || capabilities.CurrentExtent.Height == 0)
        {
            Log.LogWarning("Window is minimized, swapchain creation skipped");
            return;
        }

        // format: fall back to sdr, then to bgra (nvidia exposes bgra only, like spartan's check)
        ColorSpaceKHR color_space = ColorSpaceKHR.SpaceSrgbNonlinearKhr;
        SurfaceFormatKHR[] formats = GetSurfaceFormats();
        if (m_format == FormatHdr && !formats.Any(f => f.Format == ToVulkan(FormatHdr) && f.ColorSpace == ColorSpaceKHR.SpaceHdr10ST2084Ext))
        {
            Log.LogWarning("HDR swapchain format not supported by the surface, falling back to SDR");
            m_format = FormatSdr;
        }
        if (m_format == FormatHdr)
            color_space = ColorSpaceKHR.SpaceHdr10ST2084Ext;
        else
            m_format = SelectSdrFormat(formats.Select(f => (f.Format, f.ColorSpace)).ToArray());

        // only wait when replacing an in-flight swapchain
        if (m_rhi_swapchain.Handle != 0)
            RHI_Device.QueueWaitAll();
        DestroyViews();

        // match the surface extent when the platform locks it
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            Width  = capabilities.CurrentExtent.Width;
            Height = capabilities.CurrentExtent.Height;
        }
        else
        {
            Width  = System.Math.Clamp(Width,  capabilities.MinImageExtent.Width,  capabilities.MaxImageExtent.Width);
            Height = System.Math.Clamp(Height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height);
        }

        uint min_image_count = System.Math.Max(m_buffer_count, capabilities.MinImageCount);
        if (capabilities.MaxImageCount > 0)
            min_image_count = System.Math.Min(min_image_count, capabilities.MaxImageCount);

        SwapchainKHR old = m_rhi_swapchain;
        SwapchainCreateInfoKHR info = new()
        {
            SType            = StructureType.SwapchainCreateInfoKhr,
            Surface          = m_rhi_surface,
            MinImageCount    = min_image_count,
            ImageFormat      = ToVulkan(m_format),
            ImageColorSpace  = color_space,
            ImageExtent      = new Extent2D(Width, Height),
            ImageArrayLayers = 1,
            ImageUsage       = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform     = capabilities.CurrentTransform,
            CompositeAlpha   = SelectCompositeAlpha(capabilities.SupportedCompositeAlpha),
            PresentMode      = SelectPresentMode(m_present_mode, GetPresentModes()),
            Clipped          = true,
            OldSwapchain     = old
        };
        Check(RHI_Context.Swapchain!.CreateSwapchain(RHI_Context.Device, in info, null, out m_rhi_swapchain), "vkCreateSwapchainKHR");
        if (old.Handle != 0)
            RHI_Context.Swapchain.DestroySwapchain(RHI_Context.Device, old, null);

        // images + views
        uint count = 0;
        Check(RHI_Context.Swapchain.GetSwapchainImages(RHI_Context.Device, m_rhi_swapchain, ref count, null), "vkGetSwapchainImagesKHR");
        m_rhi_rt = new Image[count];
        fixed (Image* p = m_rhi_rt)
            Check(RHI_Context.Swapchain.GetSwapchainImages(RHI_Context.Device, m_rhi_swapchain, ref count, p), "vkGetSwapchainImagesKHR");
        m_buffer_count = count;
        m_rhi_rtv = new ImageView[count];
        m_layouts = new ImageLayout[count];
        for (int i = 0; i < count; i++)
        {
            ImageViewCreateInfo view_info = new()
            {
                SType            = StructureType.ImageViewCreateInfo,
                Image            = m_rhi_rt[i],
                ViewType         = ImageViewType.Type2D,
                Format           = ToVulkan(m_format),
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1)
            };
            Check(RHI_Context.Vk.CreateImageView(RHI_Context.Device, in view_info, null, out m_rhi_rtv[i]), "vkCreateImageView");
            RHI_Device.SetResourceName(m_rhi_rt[i].Handle, RHI_Resource_Type.Image, $"{Name}_image_{i}");
            RHI_Device.SetResourceName(m_rhi_rtv[i].Handle, RHI_Resource_Type.ImageView, $"{Name}_view_{i}");
            m_layouts[i] = ImageLayout.Undefined;
        }

        // sync: acquire semaphores rotate independently from images, rendering complete is per image
        if (m_image_acquired_semaphore.Length == 0)
        {
            int acquire_count = System.Math.Max(AcquireSemaphoreCount, (int)count + 1);
            m_image_acquired_semaphore = new RHI_SyncPrimitive[acquire_count];
            m_image_acquired_consumer  = new RHI_CommandList?[acquire_count];
            for (int i = 0; i < acquire_count; i++)
                m_image_acquired_semaphore[i] = new RHI_SyncPrimitive(RHI_SyncPrimitive_Type.Semaphore, $"{Name}_image_acquired_{i}");
        }
        foreach (RHI_SyncPrimitive s in m_rendering_complete_semaphore)
            s.Dispose();
        m_rendering_complete_semaphore = new RHI_SyncPrimitive[count];
        for (int i = 0; i < count; i++)
            m_rendering_complete_semaphore[i] = new RHI_SyncPrimitive(RHI_SyncPrimitive_Type.Semaphore, $"{Name}_rendering_complete_{i}");

        IsImageAcquired = false;
    }

    // returns false when nothing was acquired (minimized, or out of date twice)
    public bool AcquireNextImage()
    {
        IsImageAcquired = false;
        if (m_rhi_swapchain.Handle == 0)
            return false;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint index = m_semaphore_index;
            RHI_SyncPrimitive signal = m_image_acquired_semaphore[index];

            // wait only for the submission that consumed this semaphore
            m_image_acquired_consumer[index]?.WaitForExecution();
            m_image_acquired_consumer[index] = null;

            uint image_index = 0;
            Result result = RHI_Context.Swapchain!.AcquireNextImage(RHI_Context.Device, m_rhi_swapchain, 100_000_000, signal.GetSemaphore(), default, ref image_index);
            if (result == Result.Success || result == Result.SuboptimalKhr)
            {
                ImageIndex = image_index;
                m_acquired_semaphore_index = index;
                m_semaphore_index = (index + 1) % (uint)m_image_acquired_semaphore.Length;
                IsImageAcquired = true;
                return true;
            }
            if (result == Result.ErrorOutOfDateKhr)
            {
                Create();
                continue;
            }
            if (result == Result.Timeout || result == Result.NotReady)
                return false;
            Check(result, "vkAcquireNextImageKHR");
        }
        return false;
    }

    // records that cmd_list waits on the current acquire semaphore (so it isn't re-signaled while still pending)
    public void SetAcquireConsumer(RHI_CommandList cmd_list) => m_image_acquired_consumer[m_acquired_semaphore_index] = cmd_list;

    public void Present()
    {
        if (!IsImageAcquired)
            return;
        RHI_Queue queue = RHI_Device.GetQueue(RHI_Queue_Type.Present);
        if (!queue.Present(m_rhi_swapchain, ImageIndex, m_rendering_complete_semaphore[ImageIndex]))
            Create();
        IsImageAcquired = false;
    }

    // ------------------------------------------------------------------------------------------------------------
    // selection helpers, pure so they can be tested without a gpu

    // spartan prefers rgba8 and falls back to bgra8 (nvidia), both srgb non-linear colour space
    public static RHI_Format SelectSdrFormat(IReadOnlyList<(Silk.NET.Vulkan.Format format, ColorSpaceKHR color_space)> supported)
    {
        foreach (RHI_Format candidate in new[] { RHI_Format.R8G8B8A8_Unorm, RHI_Format.B8R8G8A8_Unorm })
            if (supported.Any(s => s.format == ToVulkan(candidate) && s.color_space == ColorSpaceKHR.SpaceSrgbNonlinearKhr))
                return candidate;
        // a surface that reports format undefined accepts anything
        if (supported.Count == 1 && supported[0].format == Silk.NET.Vulkan.Format.Undefined)
            return RHI_Format.R8G8B8A8_Unorm;
        throw new NotSupportedException("The surface supports neither R8G8B8A8_UNORM nor B8G8R8A8_UNORM");
    }

    // the requested mode if supported, fifo otherwise (always available per spec)
    public static PresentModeKHR SelectPresentMode(RHI_Present_Mode requested, IReadOnlyCollection<PresentModeKHR> supported)
    {
        PresentModeKHR mode = ToVulkan(requested);
        if (supported.Contains(mode))
            return mode;
        return PresentModeKHR.FifoKhr;
    }

    public static CompositeAlphaFlagsKHR SelectCompositeAlpha(CompositeAlphaFlagsKHR supported)
    {
        foreach (CompositeAlphaFlagsKHR flag in new[] { CompositeAlphaFlagsKHR.OpaqueBitKhr, CompositeAlphaFlagsKHR.PreMultipliedBitKhr, CompositeAlphaFlagsKHR.PostMultipliedBitKhr, CompositeAlphaFlagsKHR.InheritBitKhr })
            if ((supported & flag) != 0)
                return flag;
        return CompositeAlphaFlagsKHR.OpaqueBitKhr;
    }

    private SurfaceFormatKHR[] GetSurfaceFormats()
    {
        uint count = 0;
        Check(RHI_Context.Surface!.GetPhysicalDeviceSurfaceFormats(RHI_Context.DevicePhysical, m_rhi_surface, ref count, null), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* p = formats)
            Check(RHI_Context.Surface.GetPhysicalDeviceSurfaceFormats(RHI_Context.DevicePhysical, m_rhi_surface, ref count, p), "vkGetPhysicalDeviceSurfaceFormatsKHR");
        return formats;
    }

    private PresentModeKHR[] GetPresentModes()
    {
        uint count = 0;
        Check(RHI_Context.Surface!.GetPhysicalDeviceSurfacePresentModes(RHI_Context.DevicePhysical, m_rhi_surface, ref count, null), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* p = modes)
            Check(RHI_Context.Surface.GetPhysicalDeviceSurfacePresentModes(RHI_Context.DevicePhysical, m_rhi_surface, ref count, p), "vkGetPhysicalDeviceSurfacePresentModesKHR");
        return modes;
    }

    private void DestroyViews()
    {
        foreach (ImageView view in m_rhi_rtv)
            if (view.Handle != 0)
                RHI_Context.Vk.DestroyImageView(RHI_Context.Device, view, null);
        m_rhi_rtv = Array.Empty<ImageView>();
    }

    public void Dispose()
    {
        if (RHI_Context.Api == null || RHI_Context.Device.Handle == 0)
            return;
        RHI_Device.QueueWaitAll();
        DestroyViews();
        foreach (RHI_SyncPrimitive s in m_image_acquired_semaphore) s.Dispose();
        foreach (RHI_SyncPrimitive s in m_rendering_complete_semaphore) s.Dispose();
        m_image_acquired_semaphore = Array.Empty<RHI_SyncPrimitive>();
        m_rendering_complete_semaphore = Array.Empty<RHI_SyncPrimitive>();
        if (m_rhi_swapchain.Handle != 0)
        {
            RHI_Context.Swapchain!.DestroySwapchain(RHI_Context.Device, m_rhi_swapchain, null);
            m_rhi_swapchain = default;
        }
        if (m_rhi_surface.Handle != 0)
        {
            RHI_Context.Surface!.DestroySurface(RHI_Context.Instance, m_rhi_surface, null);
            m_rhi_surface = default;
        }
    }
}
