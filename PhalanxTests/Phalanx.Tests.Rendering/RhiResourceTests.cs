using Phalanx.Rendering.RHI;
using Silk.NET.Vulkan;
using Xunit;
using static Phalanx.Rendering.RHI.RHI_Texture_Flags;

namespace Phalanx.Tests;

// no gpu: format tables, mip math, copy regions, buffer/texture flag mapping (spartan @ 4c07baa)
public class RhiResourceLogicTests
{
    public static IEnumerable<object[]> Formats() =>
        Enum.GetValues<RHI_Format>().Where(f => f != RHI_Format.Max).Select(f => new object[] { (int)f });

    [Theory]
    [MemberData(nameof(Formats))]
    public void Every_format_has_bits_channels_and_bytes(int index)
    {
        var format = (RHI_Format)index;
        Assert.InRange(RHI_Definitions.rhi_format_to_bits_per_channel(format), 4u, 32u);
        Assert.InRange(RHI_Definitions.rhi_to_format_channel_count(format), 1u, 4u);
        if (RHI_Texture.IsCompressedFormat(format))
            Assert.Throws<ArgumentException>(() => RHI_Definitions.rhi_format_to_bytes(format)); // upstream 67a6e8b
        else
            Assert.InRange(RHI_Definitions.rhi_format_to_bytes(format), 1u, 16u);
    }

    [Fact]
    public void Mip_chain_size_is_tight_and_bytes_per_pixel_follows_the_format()
    {
        // 64x64 rgba8 full chain: 4 * (4096+1024+256+64+16+4+1) = 21844 (the old estimate w*h*mips*bpp is 114688)
        using var tex = new RHI_Texture(RHI_Texture_Type.Type2D, 64, 64, 1, 7, RHI_Format.R8G8B8A8_Unorm, RHI_Texture_Flags.RHI_Texture_Srv, "chain");
        Assert.Equal(21844UL, tex.CalculateMipChainSize());
        Assert.Equal(21844UL - 16384, tex.CalculateMipChainSize(1));
        Assert.Equal(tex.CalculateMipChainSize(), tex.GetObjectSize());
        using var hdr = new RHI_Texture(RHI_Texture_Type.Type2D, 4, 4, 1, 1, RHI_Format.R11G11B10_Float, RHI_Texture_Flags.RHI_Texture_Srv, "r11");
        Assert.Equal(4u, hdr.GetBytesPerPixel());
        using var vol = new RHI_Texture(RHI_Texture_Type.Type3D, 8, 8, 4, 4, RHI_Format.R8_Unorm, RHI_Texture_Flags.RHI_Texture_Srv, "vol");
        Assert.Equal(256UL + 4 * 4 * 2 + 2 * 2 * 1 + 1, vol.CalculateMipChainSize()); // mip depth clamps at 1
    }

    [Theory]
    [InlineData((int)RHI_Format.R8_Unorm, 1)]
    [InlineData((int)RHI_Format.R16_Float, 2)]
    [InlineData((int)RHI_Format.R8G8B8A8_Unorm, 4)]
    [InlineData((int)RHI_Format.B8R8G8A8_Unorm, 4)]
    [InlineData((int)RHI_Format.R11G11B10_Float, 4)]
    [InlineData((int)RHI_Format.R10G10B10A2_Unorm, 4)]
    [InlineData((int)RHI_Format.R16G16B16A16_Float, 8)]
    [InlineData((int)RHI_Format.R32G32B32_Float, 12)]
    [InlineData((int)RHI_Format.R32G32B32A32_Float, 16)]
    [InlineData((int)RHI_Format.D32_Float, 4)]
    [InlineData((int)RHI_Format.D32_Float_S8X24_Uint, 8)]
    public void Uncompressed_pixel_size_matches_the_vulkan_format(int format_index, int bytes)
    {
        var format = (RHI_Format)format_index;
        Assert.Equal((ulong)bytes, RHI_Texture.CalculateMipSize(1, 1, 1, format));
        if (format != RHI_Format.D32_Float_S8X24_Uint) // packed d32s8 is 5 bytes in vulkan, spartan sizes 8
            Assert.Equal((uint)bytes, RHI_Definitions.rhi_format_to_bytes(format));
    }

    [Theory]
    [InlineData((int)RHI_Format.BC1_Unorm, 4, 4, 8)]
    [InlineData((int)RHI_Format.BC1_Unorm, 5, 5, 32)]
    [InlineData((int)RHI_Format.BC1_Unorm, 1, 1, 8)]
    [InlineData((int)RHI_Format.BC3_Unorm, 8, 4, 32)]
    [InlineData((int)RHI_Format.BC5_Unorm, 4, 4, 16)]
    [InlineData((int)RHI_Format.BC7_Unorm, 16, 16, 256)]
    [InlineData((int)RHI_Format.ASTC, 6, 6, 64)]
    public void Block_compressed_sizes(int format_index, int w, int h, int bytes)
    {
        var format = (RHI_Format)format_index;
        Assert.True(RHI_Texture.IsCompressedFormat(format));
        Assert.Equal((ulong)bytes, RHI_Texture.CalculateMipSize((uint)w, (uint)h, 1, format));
    }

    [Fact]
    public void Mip_chain_sizes_and_copy_regions()
    {
        // 2d array 16x8, 3 layers, 5 mips: 16x8 8x4 4x2 2x1 1x1
        var (regions, size) = RHI_Texture.BuildCopyRegions(RHI_Texture_Type.Type2DArray, 16, 8, 3, 5, RHI_Format.R8G8B8A8_Unorm, ImageAspectFlags.ColorBit, 16);
        Assert.Equal(15, regions.Length);
        uint[] widths = { 16, 8, 4, 2, 1 }, heights = { 8, 4, 2, 1, 1 };
        for (int layer = 0; layer < 3; layer++)
            for (int mip = 0; mip < 5; mip++)
            {
                BufferImageCopy r = regions[mip + layer * 5];
                Assert.Equal(widths[mip], r.ImageExtent.Width);
                Assert.Equal(heights[mip], r.ImageExtent.Height);
                Assert.Equal((uint)mip, r.ImageSubresource.MipLevel);
                Assert.Equal((uint)layer, r.ImageSubresource.BaseArrayLayer);
                Assert.Equal(0UL, r.BufferOffset % 16);
            }
        // 512 + 128 + 32 + 8(->16 aligned start of next) ...; last region ends at size
        BufferImageCopy last = regions[^1];
        Assert.Equal(last.BufferOffset + 4, size);
        for (int i = 1; i < regions.Length; i++)
            Assert.True(regions[i].BufferOffset >= regions[i - 1].BufferOffset + RHI_Texture.CalculateMipSize(regions[i - 1].ImageExtent.Width, regions[i - 1].ImageExtent.Height, 1, RHI_Format.R8G8B8A8_Unorm));
    }

    [Fact]
    public void Volume_regions_use_one_layer_and_shrinking_depth()
    {
        var (regions, _) = RHI_Texture.BuildCopyRegions(RHI_Texture_Type.Type3D, 8, 8, 4, 4, RHI_Format.R8_Unorm, ImageAspectFlags.ColorBit, 4);
        Assert.Equal(4, regions.Length);
        Assert.Equal(new uint[] { 4, 2, 1, 1 }, regions.Select(r => r.ImageExtent.Depth));
        Assert.All(regions, r => Assert.Equal(0u, r.ImageSubresource.BaseArrayLayer));
    }

    [Fact]
    public void Texture_flag_values_match_spartan()
    {
        Assert.Equal(1u << 0, (uint)RHI_Texture_Srv);
        Assert.Equal(1u << 4, (uint)RHI_Texture_ClearBlit);
        Assert.Equal(1u << 10, (uint)RHI_Texture_ConcurrentSharing);
        Assert.Equal(1u << 13, (uint)RHI_Texture_Stream);
        Assert.Equal(13u, RHI_Definitions.rhi_max_mip_count);
    }

    [Theory]
    [InlineData((int)RHI_Buffer_Type.Vertex, false, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit, (int)RHI_Memory_Usage.GpuOnly)]
    [InlineData((int)RHI_Buffer_Type.Instance, true, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit, (int)RHI_Memory_Usage.Upload)]
    [InlineData((int)RHI_Buffer_Type.Index, false, BufferUsageFlags.IndexBufferBit | BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.TransferSrcBit, (int)RHI_Memory_Usage.GpuOnly)]
    [InlineData((int)RHI_Buffer_Type.Storage, false, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndirectBufferBit, (int)RHI_Memory_Usage.GpuOnly)]
    [InlineData((int)RHI_Buffer_Type.Storage, true, BufferUsageFlags.StorageBufferBit | BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit | BufferUsageFlags.IndirectBufferBit, (int)RHI_Memory_Usage.Upload)]
    [InlineData((int)RHI_Buffer_Type.Constant, true, BufferUsageFlags.UniformBufferBit | BufferUsageFlags.TransferDstBit, (int)RHI_Memory_Usage.Upload)]
    [InlineData((int)RHI_Buffer_Type.Upload, true, BufferUsageFlags.TransferSrcBit, (int)RHI_Memory_Usage.Upload)]
    [InlineData((int)RHI_Buffer_Type.Readback, true, BufferUsageFlags.TransferDstBit, (int)RHI_Memory_Usage.Readback)]
    public void Buffer_usage_and_memory_per_type(int type, bool mappable, BufferUsageFlags usage, int memory)
    {
        var (u, m) = RHI_Buffer.GetUsageAndMemory((RHI_Buffer_Type)type, mappable);
        Assert.Equal(usage, u);
        Assert.Equal((RHI_Memory_Usage)memory, m);
    }

    [Theory]
    [InlineData(64u, 256UL, 256u)]
    [InlineData(256u, 256UL, 256u)]
    [InlineData(300u, 256UL, 512u)]
    [InlineData(48u, 0UL, 48u)]
    [InlineData(48u, 64UL, 64u)]
    public void Constant_stride_alignment(uint stride, ulong alignment, uint expected) =>
        Assert.Equal(expected, RHI_Buffer.AlignConstantStride(stride, alignment));
}

// lavapipe or any vulkan 1.3 driver, validation on; each test asserts no validation errors
[Collection("gpu")]
public class RhiResourceGpuTests
{
    private static void Run(Action body)
    {
        Skip.IfNot(Phalanx.Rendering.RenderDevice.IsVulkanAvailable(out string reason), reason);
        RHI_Device.Initialize(Array.Empty<string>(), validation: true);
        try
        {
            body();
            RHI_Device.QueueWaitAll();
            RHI_Device.DeletionQueueFlush();
            Assert.Equal(0, RHI_Device.MemoryGetStats().AllocationCount); // everything released through the deletion queue
            Assert.True(!RHI_Device.ValidationMessages.Any(m => m.Contains("ErrorBit", StringComparison.Ordinal)), string.Join("\n", RHI_Device.ValidationMessages));
        }
        finally
        {
            RHI_Device.Destroy();
        }
    }

    private static byte[] Pattern(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [SkippableFact]
    public void Gpu_only_buffers_are_staged_and_read_back_exactly()
    {
        Run(() =>
        {
            foreach (RHI_Buffer_Type type in new[] { RHI_Buffer_Type.Vertex, RHI_Buffer_Type.Index, RHI_Buffer_Type.Instance, RHI_Buffer_Type.Storage })
            {
                byte[] data = Pattern(3 * 1000 * 12, (int)type);
                using var buffer = new RHI_Buffer(type, 12, 3000, data, mappable: false, $"buffer_{type}");
                Assert.Equal(0, buffer.GetMappedData());
                Assert.Equal(RHI_Memory_Usage.GpuOnly, buffer.GetAllocation()!.Usage);
                Assert.Equal(data, RHI_Device.ReadbackBuffer(buffer));

                // sub-region upload goes through staging too
                byte[] patch = Pattern(100, 99);
                buffer.UploadSubRegion(patch, 1000);
                byte[] back = RHI_Device.ReadbackBuffer(buffer);
                Assert.Equal(patch, back[1000..1100]);
                Assert.Equal(data[..1000], back[..1000]);
            }
            Assert.Equal(0UL, RHI_Device.StagingInFlightBytes);
        });
    }

    [SkippableFact]
    public void Mappable_buffers_advance_one_stride_per_update()
    {
        Run(() =>
        {
            using var constant = new RHI_Buffer(RHI_Buffer_Type.Constant, 48, 4, default, mappable: true, "constant");
            Assert.Equal(0UL, constant.GetStride() % RHI_Device.PropertyMinUniformBufferOffsetAlignment);
            Assert.Equal(48u, constant.GetStrideUnaligned());

            RHI_CommandList cmd = RHI_Device.BeginFrame();
            for (int i = 0; i < 4; i++)
            {
                constant.Update(cmd, Enumerable.Repeat((byte)(i + 1), 48).ToArray());
                Assert.Equal((uint)(i * constant.GetStride()), constant.GetOffset());
            }
            Assert.Throws<InvalidOperationException>(() => constant.Update(cmd, new byte[48]));   // out of slots
            constant.ResetOffset();
            constant.Update(cmd, new byte[48]);
            Assert.Equal(0u, constant.GetOffset());
            RHI_Device.EndFrame().Wait();

            for (int i = 1; i < 4; i++)
                Assert.Equal((byte)(i + 1), System.Runtime.InteropServices.Marshal.ReadByte(constant.GetMappedData(), (int)(i * constant.GetStride())));
            Assert.Throws<ArgumentException>(() => new RHI_Buffer(RHI_Buffer_Type.Constant, 16, 1, default, mappable: false, "bad"));
        });
    }

    [SkippableFact]
    public void Texture_2d_with_mips_uploads_and_reads_back_every_mip()
    {
        Run(() =>
        {
            const uint size = 64, mips = 7;
            var slice = new RHI_Texture_Slice();
            for (uint m = 0; m < mips; m++)
            {
                uint s = System.Math.Max(1u, size >> (int)m);
                slice.mips.Add(new RHI_Texture_Mip { bytes = Pattern((int)(s * s * 4), (int)m) });
            }
            using var texture = new RHI_Texture(RHI_Texture_Type.Type2D, size, size, 1, mips, RHI_Format.R8G8B8A8_Unorm,
                RHI_Texture_Srv | RHI_Texture_PerMipViews, "tex2d", new List<RHI_Texture_Slice> { slice });

            Assert.NotEqual(0UL, texture.GetRhiResource().Handle);
            Assert.NotEqual(0UL, texture.GetRhiSrv().Handle);
            for (uint m = 0; m < mips; m++)
            {
                Assert.NotEqual(0UL, texture.GetRhiSrvMip(m).Handle);
                Assert.Equal(RHI_Image_Layout.General, texture.GetLayout(m));
                Assert.Equal(slice.mips[(int)m].bytes, RHI_Device.ReadbackTexture(texture, 0, m));
            }
            Assert.Equal(RHI_Image_Layout.Max, texture.GetLayout(mips)); // untracked beyond the chain
        });
    }

    [SkippableFact]
    public void Cube_and_array_layers_upload_per_layer()
    {
        Run(() =>
        {
            foreach (var (type, layers) in new[] { (RHI_Texture_Type.TypeCube, 6u), (RHI_Texture_Type.Type2DArray, 4u) })
            {
                var slices = new List<RHI_Texture_Slice>();
                for (uint l = 0; l < layers; l++)
                {
                    var s = new RHI_Texture_Slice();
                    s.mips.Add(new RHI_Texture_Mip { bytes = Pattern(16 * 16 * 8, (int)(l * 10)) });
                    s.mips.Add(new RHI_Texture_Mip { bytes = Pattern(8 * 8 * 8, (int)(l * 10 + 1)) });
                    slices.Add(s);
                }
                using var texture = new RHI_Texture(type, 16, 16, layers, 2, RHI_Format.R16G16B16A16_Float, RHI_Texture_Srv, $"tex_{type}", slices);
                for (uint l = 0; l < layers; l++)
                    for (uint m = 0; m < 2; m++)
                        Assert.Equal(slices[(int)l].mips[(int)m].bytes, RHI_Device.ReadbackTexture(texture, l, m));
                if (type == RHI_Texture_Type.Type2DArray)
                    for (uint l = 0; l < layers; l++)
                        Assert.NotEqual(0UL, texture.GetRhiSrvLayer(l).Handle);
            }

            // volume: one region per mip with shrinking depth
            var volume = new RHI_Texture_Slice();
            volume.mips.Add(new RHI_Texture_Mip { bytes = Pattern(8 * 8 * 4, 5) });
            volume.mips.Add(new RHI_Texture_Mip { bytes = Pattern(4 * 4 * 2, 6) });
            using var tex3d = new RHI_Texture(RHI_Texture_Type.Type3D, 8, 8, 4, 2, RHI_Format.R8_Unorm, RHI_Texture_Srv, "tex3d", new List<RHI_Texture_Slice> { volume });
            Assert.Equal(volume.mips[0].bytes, RHI_Device.ReadbackTexture(tex3d, 0, 0));
            Assert.Equal(volume.mips[1].bytes, RHI_Device.ReadbackTexture(tex3d, 0, 1));
        });
    }

    [SkippableFact]
    public void Render_targets_get_views_and_tracked_layouts()
    {
        Run(() =>
        {
            using var color = new RHI_Texture(RHI_Texture_Type.Type2D, 32, 32, 1, 1, RHI_Format.R8G8B8A8_Unorm,
                RHI_Texture_Rtv | RHI_Texture_Srv | RHI_Texture_Uav | RHI_Texture_ClearBlit, "rt_color");
            using var depth = new RHI_Texture(RHI_Texture_Type.Type2D, 32, 32, 1, 1, RHI_Format.D32_Float,
                RHI_Texture_Rtv | RHI_Texture_Srv, "rt_depth");
            using var array = new RHI_Texture(RHI_Texture_Type.Type2DArray, 16, 16, 3, 1, RHI_Format.R16G16B16A16_Float,
                RHI_Texture_Rtv | RHI_Texture_Srv, "rt_array");
            using var cube = new RHI_Texture(RHI_Texture_Type.TypeCube, 16, 16, 6, 1, RHI_Format.R8G8B8A8_Unorm,
                RHI_Texture_Rtv | RHI_Texture_Srv, "rt_cube");

            Assert.True(color.IsRtv() && !color.IsDsv());
            Assert.True(depth.IsDsv() && !depth.IsRtv());
            Assert.NotEqual(0UL, color.GetRhiRtv().Handle);
            Assert.NotEqual(0UL, depth.GetRhiDsv().Handle);
            Assert.Equal(0UL, depth.GetRhiRtv().Handle);
            Assert.NotEqual(0UL, array.GetRhiRtvMultiview().Handle);
            for (uint i = 0; i < 6; i++) Assert.NotEqual(0UL, cube.GetRhiRtv(i).Handle);
            Assert.Equal(RHI_Image_Layout.General, color.GetLayout(0));

            // the layout tracker emits nothing once the texture sits in general, and the clear lands
            RHI_CommandList cmd = RHI_Device.BeginFrame();
            cmd.InsertBarrier(color, RHI_Image_Layout.General);
            cmd.ClearRenderTarget(color.GetRhiRtv(), ImageLayout.General, 32, 32, 0, 1, 0, 1);
            RHI_Device.EndFrame().Wait();
            byte[] pixels = RHI_Device.ReadbackTexture(color, 0, 0);
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, pixels[..4]);
        });
    }

    [SkippableFact]
    public void Layout_tracking_batches_or_splits_per_mip()
    {
        Run(() =>
        {
            using var texture = new RHI_Texture(RHI_Texture_Type.Type2D, 32, 32, 1, 6, RHI_Format.R8G8B8A8_Unorm,
                RHI_Texture_Uav | RHI_Texture_PerMipViews | RHI_Texture_ClearBlit, "layouts");
            Assert.All(Enumerable.Range(0, 6), m => Assert.Equal(RHI_Image_Layout.General, texture.GetLayout((uint)m)));

            // simulate a lost state on some mips (spartan: InvalidateGpuState) and transition a mixed range
            texture.SetLayoutDirect(1, 2, RHI_Image_Layout.Max);
            RHI_CommandList cmd = RHI_Device.BeginFrame();
            cmd.InsertBarrier(texture, RHI_Image_Layout.General, 0, 4);
            Assert.All(Enumerable.Range(0, 6), m => Assert.Equal(RHI_Image_Layout.General, texture.GetLayout((uint)m)));
            Assert.Throws<ArgumentOutOfRangeException>(() => cmd.InsertBarrier(texture, RHI_Image_Layout.General, 5, 2));
            RHI_Device.EndFrame().Wait();
        });
    }

    [SkippableFact]
    public void Many_uploads_through_the_staging_pool()
    {
        Run(() =>
        {
            long acquires = RHI_Device.StagingAcquireCount;
            var buffers = new List<(RHI_Buffer buffer, byte[] data)>();
            var random = new Random(3);
            for (int i = 0; i < 150; i++)
            {
                byte[] data = Pattern(random.Next(4, 64 * 1024) & ~3, i);
                buffers.Add((new RHI_Buffer(RHI_Buffer_Type.Storage, 4, (uint)(data.Length / 4), data, mappable: false, $"many_{i}"), data));
            }
            var textures = new List<(RHI_Texture texture, byte[] data)>();
            for (int i = 0; i < 30; i++)
            {
                byte[] data = Pattern(32 * 32 * 4, 1000 + i);
                var slice = new RHI_Texture_Slice();
                slice.mips.Add(new RHI_Texture_Mip { bytes = data });
                textures.Add((new RHI_Texture(RHI_Texture_Type.Type2D, 32, 32, 1, 1, RHI_Format.R8G8B8A8_Unorm, RHI_Texture_Srv, $"many_tex_{i}",
                    new List<RHI_Texture_Slice> { slice }), data));
            }
            Assert.Equal(180, RHI_Device.StagingAcquireCount - acquires);
            Assert.Equal(0UL, RHI_Device.StagingInFlightBytes);

            // staging memory is sub-allocated from a few upload blocks, not one VkDeviceMemory each
            Assert.True(RHI_Device.MemoryGetStats().BlockCount < 10, RHI_Device.MemoryGetStats().ToString());

            foreach (var (buffer, data) in buffers.Where((_, i) => i % 15 == 0))
                Assert.Equal(data, RHI_Device.ReadbackBuffer(buffer));
            foreach (var (texture, data) in textures.Where((_, i) => i % 5 == 0))
                Assert.Equal(data, RHI_Device.ReadbackTexture(texture, 0, 0));

            foreach (var (b, _) in buffers) b.Dispose();
            foreach (var (t, _) in textures) t.Dispose();
            Assert.True(RHI_Device.DeletionQueueCount() > 0);
        });
    }
}
