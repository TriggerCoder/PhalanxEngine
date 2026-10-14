// Port of SpartanEngine source/rhi/RHI_SwapChain.h + vulkan/Vulkan_SwapChain.cpp @ 4c07baa.
// The surface comes from the Silk.NET window (Core's Phalanx.Platform window), so Core never sees Vulkan; the main
// window's resize arrives through Core's Event bus (EventType.WindowResized) like spartan's m_window_resize_event_handle.
// Deviations (phalanx-rhi4.md): no RTSS check; acquire semaphores are kept across recreation (their consumers are
// tracked as RHI_Work); TRANSFER_SRC is added to the image usage when supported (readback in tests); a pending
// HDR/vsync change survives a minimized Create (spartan drops it, see the doc); SDR falls back to BGRA when RGBA is
// missing on any vendor, not only on NVIDIA.
using Silk.NET.Core.Contexts;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;
using Semaphore = Silk.NET.Vulkan.Semaphore;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe class RHI_SwapChain : IDisposable
{
    public const int BufferCount = 2;
    public const int AcquireSemaphoreCount = 3;
    public const RHI_Format FormatSdr = RHI_Format.R8G8B8A8_Unorm;
    public const RHI_Format FormatHdr = RHI_Format.R10G10B10A2_Unorm; // vulkan: hdr10 pq, the wsi owns the conversion

    // test seam: forces the minimized state (a headless x server cannot minimize)
    internal static Func<bool>? IsMinimizedOverride;

    private readonly IWindow m_window;
    private SurfaceKHR m_rhi_surface;
    private SwapchainKHR m_rhi_swapchain;
    private Image[] m_rhi_rt = Array.Empty<Image>();
    private ImageView[] m_rhi_rtv = Array.Empty<ImageView>();
    private ImageLayout[] m_layouts = Array.Empty<ImageLayout>();
    private RHI_SyncPrimitive[] m_image_acquired_semaphore = Array.Empty<RHI_SyncPrimitive>();
    private RHI_Work[] m_image_acquired_consumer = Array.Empty<RHI_Work>();
    private RHI_SyncPrimitive[] m_rendering_complete_semaphore = Array.Empty<RHI_SyncPrimitive>();
    private uint semaphore_index;
    private uint m_acquired_semaphore_index;
    private uint m_buffer_count;
    private uint m_width, m_height;
    private RHI_Format m_format;
    private RHI_Format m_format_pending = RHI_Format.Max;
    private RHI_Present_Mode m_present_mode;
    private PresentModeKHR m_present_mode_vk;
    private ColorSpaceKHR m_color_space;
    private bool m_is_dirty;
    private bool m_image_acquired;
    private uint m_image_index;
    private ulong m_window_resize_event_handle;
    private Action<Silk.NET.Maths.Vector2D<int>>? m_window_resize_callback;

    public string Name { get; }
    public uint Width => m_width;
    public uint Height => m_height;
    public uint ImageIndex => m_image_index;
    public bool IsImageAcquired => m_image_acquired;
    public RHI_Format Format => m_format;
    public int CreateCount { get; private set; }   // how many times vkCreateSwapchainKHR ran (tests, diagnostics)
    public uint GetWidth() => m_width;
    public uint GetHeight() => m_height;
    public uint GetBufferCount() => m_buffer_count;
    public RHI_Format GetFormat() => m_format;
    public uint GetImageIndex() => m_image_index;
    public Image GetRhiRt() => m_rhi_rt[m_image_index];
    public ImageView GetRhiRtv() => m_rhi_rtv[m_image_index];
    public Image GetRhiRtRaw(uint i) => m_rhi_rt[i];
    public SwapchainKHR GetRhiResource() => m_rhi_swapchain;
    public PresentModeKHR GetPresentModeVk() => m_present_mode_vk;
    public ColorSpaceKHR GetColorSpace() => m_color_space;
    public bool IsHdr() => m_format == FormatHdr;
    public bool IsDirty() => m_is_dirty;

    public RHI_SwapChain(IWindow window, uint width, uint height, RHI_Present_Mode present_mode, uint buffer_count, bool hdr, string name)
    {
        if (RHI_Context.Surface == null || RHI_Context.Swapchain == null)
            throw new InvalidOperationException("RHI_Device was initialized without the window's surface extensions");
        if (!IsValidResolution(width, height)) throw new ArgumentException("Invalid resolution");
        if (buffer_count < 2) throw new ArgumentException("Buffer count can't be less than 2");
        if (window.VkSurface is not { } surface_source) throw new NotSupportedException("The window was not created with the Vulkan API");

        m_window       = window;
        Name           = name;
        m_width        = width;
        m_height       = height;
        m_present_mode = present_mode;
        m_buffer_count = buffer_count;
        m_format       = hdr ? FormatHdr : FormatSdr;

        // create surface once
        VkNonDispatchableHandle handle = surface_source.Create<AllocationCallbacks>(new VkHandle(RHI_Context.Instance.Handle), null);
        m_rhi_surface = new SurfaceKHR(handle.Handle);
        Check(RHI_Context.Surface.GetPhysicalDeviceSurfaceSupport(RHI_Context.DevicePhysical, RHI_Device.GetQueueIndex(RHI_Queue_Type.Graphics), m_rhi_surface, out Silk.NET.Core.Bool32 present_support), "vkGetPhysicalDeviceSurfaceSupportKHR");
        if (!present_support)
            throw new NotSupportedException("Present queue does not support presentation to this surface");

        Create();

        // the main window swapchain tracks os resize through the event bus, other windows through their own callback
        if (ReferenceEquals(window, Phalanx.Platform.Window.GetWindow()))
            m_window_resize_event_handle = Event.Subscribe(Event.EventType.WindowResized, _ => ResizeToWindowSize());
        else
            window.FramebufferResize += m_window_resize_callback = size => { if (size.X > 0 && size.Y > 0) Resize((uint)size.X, (uint)size.Y); };
    }

    // spartan: RHI_Device::IsValidResolution
    public static bool IsValidResolution(uint width, uint height) =>
        width > 4 && height > 4 && (RHI_Device.PropertyMaxTexture2dDimension == 0 || (width <= RHI_Device.PropertyMaxTexture2dDimension && height <= RHI_Device.PropertyMaxTexture2dDimension));

    public RHI_SyncPrimitive? GetImageAcquiredSemaphore() => m_image_acquired ? m_image_acquired_semaphore[m_acquired_semaphore_index] : null;
    public RHI_SyncPrimitive? GetRenderingCompleteSemaphore() => m_image_acquired ? m_rendering_complete_semaphore[m_image_index] : null;
    public ImageLayout GetLayout() => m_layouts[m_image_index];
    public void SetLayout(ImageLayout layout) => m_layouts[m_image_index] = layout;

    private bool IsMinimized() => IsMinimizedOverride?.Invoke() ?? Phalanx.Platform.Window.IsMinimized(m_window);

    // returns true when a swapchain was (re)created, false when skipped because the window is minimized
    private bool Create()
    {
        // get surface capabilities
        KhrSurface khr_surface = RHI_Context.Surface!;
        Check(khr_surface.GetPhysicalDeviceSurfaceCapabilities(RHI_Context.DevicePhysical, m_rhi_surface, out SurfaceCapabilitiesKHR capabilities), "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

        // skip if the window is minimized; a pending format stays pending (spartan applies it first and loses it)
        if (capabilities.CurrentExtent.Width == 0 || capabilities.CurrentExtent.Height == 0 || IsMinimizedOverride?.Invoke() == true)
        {
            Log.LogWarning("Window is minimized, swapchain creation skipped");
            return false;
        }

        // apply pending format change now that we're actually recreating the swapchain
        if (m_format_pending != RHI_Format.Max)
        {
            m_format         = m_format_pending;
            m_format_pending = RHI_Format.Max;
        }

        // check the surface supports the requested format and color space, fall back to SDR if not
        SurfaceFormatKHR[] formats = GetSurfaceFormats();
        var supported = formats.Select(f => (f.Format, f.ColorSpace)).ToArray();
        (RHI_Format format, ColorSpaceKHR color_space, bool fell_back) = SelectFormat(m_format, supported, RHI_Device.GetPrimaryPhysicalDevice().IsNvidia());
        if (fell_back && m_format == FormatHdr)
            Log.LogWarning($"HDR format ({m_format}, {get_color_space(m_format)}) not supported by surface. Falling back to SDR.");
        m_format      = format;
        m_color_space = color_space;

        // only wait when replacing an in-flight swapchain
        if (m_rhi_swapchain.Handle != 0)
            RHI_Device.QueueWaitAll();
        DestroyViews();

        (m_width, m_height) = SelectExtent(capabilities.CurrentExtent, capabilities.MinImageExtent, capabilities.MaxImageExtent, m_width, m_height);

        uint min_image_count = System.Math.Max(m_buffer_count, capabilities.MinImageCount);
        if (capabilities.MaxImageCount > 0)
            min_image_count = System.Math.Min(min_image_count, capabilities.MaxImageCount);

        ImageUsageFlags usage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit;
        if ((capabilities.SupportedUsageFlags & ImageUsageFlags.TransferSrcBit) != 0)
            usage |= ImageUsageFlags.TransferSrcBit; // deviation: allows reading the back buffer back (tests, screenshots)

        m_present_mode_vk = SelectPresentMode(m_present_mode, GetPresentModes());
        SwapchainKHR old = m_rhi_swapchain;
        SwapchainCreateInfoKHR info = new()
        {
            SType            = StructureType.SwapchainCreateInfoKhr,
            Surface          = m_rhi_surface,
            MinImageCount    = min_image_count,
            ImageFormat      = ToVulkan(m_format),
            ImageColorSpace  = m_color_space,
            ImageExtent      = new Extent2D(m_width, m_height),
            ImageArrayLayers = 1,
            ImageUsage       = usage,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform     = capabilities.CurrentTransform,
            CompositeAlpha   = SelectCompositeAlpha(capabilities.SupportedCompositeAlpha),
            PresentMode      = m_present_mode_vk,
            Clipped          = true,
            OldSwapchain     = old
        };
        Check(RHI_Context.Swapchain!.CreateSwapchain(RHI_Context.Device, in info, null, out m_rhi_swapchain), "vkCreateSwapchainKHR");
        if (old.Handle != 0)
            RHI_Context.Swapchain.DestroySwapchain(RHI_Context.Device, old, null);
        CreateCount++;

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
        int acquire_count = System.Math.Max(AcquireSemaphoreCount, (int)count + 1);
        if (m_image_acquired_semaphore.Length != acquire_count)
        {
            foreach (RHI_SyncPrimitive s in m_image_acquired_semaphore) s.Dispose();
            m_image_acquired_semaphore = new RHI_SyncPrimitive[acquire_count];
            m_image_acquired_consumer  = new RHI_Work[acquire_count];
            for (int i = 0; i < acquire_count; i++)
                m_image_acquired_semaphore[i] = new RHI_SyncPrimitive(RHI_SyncPrimitive_Type.Semaphore, $"{Name}_acquire_{i}");
        }
        foreach (RHI_SyncPrimitive s in m_rendering_complete_semaphore)
            s.Dispose();
        m_rendering_complete_semaphore = new RHI_SyncPrimitive[count];
        for (int i = 0; i < count; i++)
            m_rendering_complete_semaphore[i] = new RHI_SyncPrimitive(RHI_SyncPrimitive_Type.Semaphore, $"{Name}_present_{i}");

        // hdr metadata only if hdr is enabled and the extension is there
        if (m_format == FormatHdr && RHI_Context.HdrMetadata != null)
            SetHdrMetadata();

        Log.LogInfo($"Swapchain created with resolution: {m_width}x{m_height}, HDR: {(m_format == FormatHdr ? "enabled" : "disabled")} ({m_format}), VSync: {(m_present_mode == RHI_Present_Mode.Fifo ? "enabled" : "disabled")}");

        // reset state after swapchain recreation
        m_image_index              = 0;
        semaphore_index            = 0;
        m_acquired_semaphore_index = 0;
        m_image_acquired           = false;
        return true;
    }

    private void SetHdrMetadata()
    {
        HdrMetadataEXT metadata = new()
        {
            SType                     = StructureType.HdrMetadataExt,
            DisplayPrimaryRed         = new XYColorEXT(0.708f, 0.292f),
            DisplayPrimaryGreen       = new XYColorEXT(0.170f, 0.797f),
            DisplayPrimaryBlue        = new XYColorEXT(0.131f, 0.046f),
            WhitePoint                = new XYColorEXT(0.3127f, 0.3290f),
            MaxLuminance              = Phalanx.Platform.Display.GetLuminanceMax(),
            MinLuminance              = 0.0f, // vulkan uses zero for unknown metadata
            MaxContentLightLevel      = 0.0f,
            MaxFrameAverageLightLevel = 0.0f
        };
        SwapchainKHR swapchain = m_rhi_swapchain;
        RHI_Context.HdrMetadata!.SetHdrMetadata(RHI_Context.Device, 1, &swapchain, &metadata);
    }

    public void Resize(uint width, uint height)
    {
        if (!IsValidResolution(width, height))
        {
            Log.LogWarning($"{Name}: ignoring invalid resolution {width}x{height}");
            return;
        }
        if (m_width == width && m_height == height && m_rhi_swapchain.Handle != 0)
            return;
        m_width  = width;
        m_height = height;
        if (!Create())
            m_is_dirty = true; // minimized: retry on the next present
        Log.LogInfo($"Resolution has been set to {width}x{height}");
    }

    // spartan: ResizeToWindowSize (EventType::WindowResized handler)
    public void ResizeToWindowSize() => Resize((uint)System.Math.Max(0, m_window.FramebufferSize.X), (uint)System.Math.Max(0, m_window.FramebufferSize.Y));

    // hdr: applied in Create, updating the format here would mismatch pipelines rendering between SetHdr and Create
    public void SetHdr(bool enabled)
    {
        if (enabled && !Phalanx.Platform.Display.GetHdr())
            Log.LogWarning("This display doesn't report HDR support, the surface check decides"); // spartan asserts here
        RHI_Format new_format = enabled ? FormatHdr : FormatSdr;
        if (new_format == RHI_Format.R8G8B8A8_Unorm && RHI_Device.GetPrimaryPhysicalDevice().IsNvidia())
            new_format = RHI_Format.B8R8G8A8_Unorm;
        if (new_format != m_format)
        {
            m_format_pending = new_format;
            m_is_dirty       = true;
        }
    }

    public void SetVsync(bool enabled)
    {
        if ((m_present_mode == RHI_Present_Mode.Fifo) != enabled)
        {
            m_present_mode = enabled ? RHI_Present_Mode.Fifo : RHI_Present_Mode.Immediate;
            m_is_dirty     = true;
        }
    }

    // fifo is always supported, so vsync means fifo
    public bool GetVsync() => m_present_mode == RHI_Present_Mode.Fifo;

    // returns false when nothing was acquired (minimized, or out of date twice)
    public bool AcquireNextImage()
    {
        m_image_acquired = false;

        // when the window is minimized acquisition will fail and it's not necessary either
        if (IsMinimized())
            return false;
        if (m_is_dirty || m_rhi_swapchain.Handle == 0)
            if (!TryRecreate())
                return false;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            uint index = semaphore_index;
            RHI_SyncPrimitive signal = m_image_acquired_semaphore[index];

            // wait only for the submission that consumed this semaphore
            m_image_acquired_consumer[index].Wait();
            m_image_acquired_consumer[index] = default;

            uint image_index = 0;
            Result result = RHI_Context.Swapchain!.AcquireNextImage(RHI_Context.Device, m_rhi_swapchain, 100_000_000, signal.GetSemaphore(), default, ref image_index);
            if (result == Result.Success || result == Result.SuboptimalKhr)
            {
                m_image_index              = image_index;
                m_acquired_semaphore_index = index;
                semaphore_index            = (index + 1) % (uint)m_image_acquired_semaphore.Length;
                m_image_acquired           = true;
                return true;
            }
            if (result == Result.ErrorOutOfDateKhr)
            {
                if (!Create())
                    return false;
                continue;
            }
            if (result == Result.Timeout || result == Result.NotReady)
                return false;
            Check(result, "vkAcquireNextImageKHR");
        }
        return false;
    }

    private bool TryRecreate()
    {
        if (!Create())
            return false;
        m_is_dirty = false;
        return true;
    }

    // records that a submission waits on the current acquire semaphore (so it isn't re-signaled while pending)
    public void SetAcquireConsumer(RHI_Work work) => m_image_acquired_consumer[m_acquired_semaphore_index] = work;
    public void SetAcquireConsumer(RHI_CommandList cmd_list) => SetAcquireConsumer(cmd_list.GetWork());

    public void Present()
    {
        if (!m_image_acquired)
            return;

        bool success = RHI_Device.GetQueue(RHI_Queue_Type.Present).Present(m_rhi_swapchain, m_image_index, m_rendering_complete_semaphore[m_image_index]);
        m_image_acquired = false;
        if (!success)
            m_is_dirty = true;

        // recreate here so no semaphores are destroyed while being waited on; stays dirty while minimized
        if (m_is_dirty)
            TryRecreate();
    }

    // spartan: get_color_space
    public static ColorSpaceKHR get_color_space(RHI_Format format) =>
        format == RHI_Format.R10G10B10A2_Unorm ? ColorSpaceKHR.SpaceHdr10ST2084Ext : ColorSpaceKHR.SpaceSrgbNonlinearKhr;

    // spartan: is_format_and_color_space_supported + the sdr fallback in Create. pure.
    public static (RHI_Format format, ColorSpaceKHR color_space, bool fell_back) SelectFormat(RHI_Format requested,
        IReadOnlyList<(Silk.NET.Vulkan.Format format, ColorSpaceKHR color_space)> supported, bool is_nvidia)
    {
        RHI_Format format = requested;
        if (format == RHI_Format.R8G8B8A8_Unorm && is_nvidia)
            format = RHI_Format.B8R8G8A8_Unorm; // nvidia exposes bgra instead of rgba
        ColorSpaceKHR color_space = get_color_space(format);
        if (supported.Any(s => s.format == ToVulkan(format) && s.color_space == color_space))
            return (format, color_space, false);
        return (SelectSdrFormat(supported), ColorSpaceKHR.SpaceSrgbNonlinearKhr, true);
    }

    // spartan: Create's extent rule. match the surface extent when the platform locks it, else clamp. pure.
    public static (uint width, uint height) SelectExtent(Extent2D current, Extent2D min, Extent2D max, uint width, uint height)
    {
        if (current.Width != uint.MaxValue)
            return (current.Width, current.Height);
        return (System.Math.Clamp(width, min.Width, max.Width), System.Math.Clamp(height, min.Height, max.Height));
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
        if (m_window_resize_event_handle != 0)
        {
            Event.Unsubscribe(Event.EventType.WindowResized, m_window_resize_event_handle);
            m_window_resize_event_handle = 0;
        }
        if (m_window_resize_callback != null)
        {
            m_window.FramebufferResize -= m_window_resize_callback;
            m_window_resize_callback = null;
        }
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
