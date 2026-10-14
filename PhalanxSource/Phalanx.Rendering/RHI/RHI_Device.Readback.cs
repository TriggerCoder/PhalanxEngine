// Readback helpers (not in spartan, which reads back through RHI_Buffer_Type::Readback buffers in specific passes).
// Used by the tests and tools: copy a texture subresource or a buffer into a readback buffer and return the bytes.
using Silk.NET.Vulkan;

namespace Phalanx.Rendering.RHI;

internal static unsafe partial class RHI_Device
{
    public static byte[] ReadbackTexture(RHI_Texture texture, uint array_index, uint mip_index)
    {
        uint width  = System.Math.Max(1u, texture.GetWidth() >> (int)mip_index);
        uint height = System.Math.Max(1u, texture.GetHeight() >> (int)mip_index);
        uint depth  = texture.Type == RHI_Texture_Type.Type3D ? System.Math.Max(1u, texture.GetDepth() >> (int)mip_index) : 1;
        ulong size  = RHI_Texture.CalculateMipSize(width, height, depth, texture.GetFormat());

        using var readback = new RHI_Buffer(RHI_Buffer_Type.Readback, size, 1, default, true, $"{texture.Name}_readback");
        RHI_CommandList cmd_list = RHI_CommandList.ImmediateExecutionBegin(RHI_Queue_Type.Graphics);
        cmd_list.InsertBarrier(texture, RHI_Image_Layout.General, 0, texture.GetMipCount());
        cmd_list.InsertBarrierMemory();
        BufferImageCopy region = new()
        {
            ImageSubresource = new ImageSubresourceLayers(texture.GetAspectMask(only_depth: texture.IsDepthFormat()), mip_index, array_index, 1),
            ImageExtent      = new Extent3D(width, height, depth)
        };
        RHI_Context.Vk.CmdCopyImageToBuffer(cmd_list.GetRhiResource(), texture.GetRhiResource(), ImageLayout.General, readback.GetRhiResource(), 1, in region);
        cmd_list.InsertBarrierMemory(host_read: true);
        RHI_CommandList.ImmediateExecutionEnd(cmd_list);
        return new ReadOnlySpan<byte>((void*)readback.GetMappedData(), (int)size).ToArray();
    }

    public static byte[] ReadbackBuffer(RHI_Buffer buffer, ulong offset = 0, ulong size = 0)
    {
        if (size == 0) size = buffer.GetObjectSize() - offset;
        using var readback = new RHI_Buffer(RHI_Buffer_Type.Readback, size, 1, default, true, $"{buffer.Name}_readback");
        RHI_CommandList cmd_list = RHI_CommandList.ImmediateExecutionBegin(RHI_Queue_Type.Graphics);
        cmd_list.InsertBarrierMemory();
        cmd_list.CopyBuffer(buffer.GetRhiResource(), offset, readback.GetRhiResource(), 0, size);
        cmd_list.InsertBarrierMemory(host_read: true);
        RHI_CommandList.ImmediateExecutionEnd(cmd_list);
        return new ReadOnlySpan<byte>((void*)readback.GetMappedData(), (int)size).ToArray();
    }
}
