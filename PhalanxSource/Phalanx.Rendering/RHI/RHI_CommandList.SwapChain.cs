// Port of SpartanEngine Vulkan_CommandList.cpp blit(texture, swapchain) (:2234), copy(texture, swapchain) (:2464)
// and the swapchain layout barriers used by RHI_Device::EndFrame's prepare_for_present @ 4c07baa.
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe partial class RHI_CommandList
{
    // swapchain images track their own layout per image (undefined after (re)creation)
    public void InsertBarrier(RHI_SwapChain swapchain, ImageLayout layout)
    {
        if (m_state != RHI_CommandListState.Recording)
            throw new InvalidOperationException($"{Name}: InsertBarrier while {m_state}");
        ImageLayout old = swapchain.GetLayout();
        if (old == layout)
            return;
        InsertBarrierImage(swapchain.GetRhiRt(), ImageAspectFlags.ColorBit, old, layout);
        swapchain.SetLayout(layout);
    }

    // spartan: RHI_Device::EndFrame -> prepare_for_present
    public void PrepareForPresent(RHI_SwapChain swapchain)
    {
        RenderPassEnd(); // a pending clear must run before the image leaves general
        InsertBarrier(swapchain, ImageLayout.PresentSrcKhr);
    }

    // spartan: blit(RHI_Texture* source, RHI_SwapChain* destination). nearest when the sizes match, linear otherwise.
    public void Blit(RHI_Texture source, RHI_SwapChain destination)
    {
        if ((source.GetFlags() & RHI_Texture_Flags.RHI_Texture_ClearBlit) == 0)
            throw new InvalidOperationException($"{source.Name}: the texture needs the RHI_Texture_ClearBlit flag");
        if (source.GetWidth() > destination.Width || source.GetHeight() > destination.Height)
            throw new InvalidOperationException($"{source.Name}: the source is larger than the swapchain");
        if (!destination.IsImageAcquired)
            throw new InvalidOperationException("Blit to a swapchain without an acquired image");

        InsertBarrier(source, RHI_Image_Layout.General);
        InsertBarrier(destination, ImageLayout.General);

        ImageBlit region = new()
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
        };
        region.SrcOffsets[1] = new Offset3D((int)source.GetWidth(), (int)source.GetHeight(), 1);
        region.DstOffsets[1] = new Offset3D((int)destination.Width, (int)destination.Height, 1);
        bool same_size = source.GetWidth() == destination.Width && source.GetHeight() == destination.Height;
        RHI_Context.Vk.CmdBlitImage(m_rhi_resource, source.GetRhiResource(), ImageLayout.General, destination.GetRhiRt(), ImageLayout.General,
                                    1, &region, same_size ? Filter.Nearest : Filter.Linear);

        // spartan transitions to present here
        InsertBarrier(destination, ImageLayout.PresentSrcKhr);
    }

    // spartan: copy(RHI_Texture* source, RHI_SwapChain* destination). same size and format.
    public void Copy(RHI_Texture source, RHI_SwapChain destination)
    {
        if (source.GetWidth() != destination.Width || source.GetHeight() != destination.Height)
            throw new InvalidOperationException($"{source.Name}: copy needs the swapchain's size");
        if (source.GetFormat() != destination.Format)
            throw new InvalidOperationException($"{source.Name}: copy needs the swapchain's format ({destination.Format})");
        if (!destination.IsImageAcquired)
            throw new InvalidOperationException("Copy to a swapchain without an acquired image");

        InsertBarrier(source, RHI_Image_Layout.General);
        InsertBarrier(destination, ImageLayout.General);
        ImageCopy region = new()
        {
            SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            Extent         = new Extent3D(source.GetWidth(), source.GetHeight(), 1)
        };
        RHI_Context.Vk.CmdCopyImage(m_rhi_resource, source.GetRhiResource(), ImageLayout.General, destination.GetRhiRt(), ImageLayout.General, 1, &region);
        InsertBarrier(destination, ImageLayout.PresentSrcKhr);
    }
}
