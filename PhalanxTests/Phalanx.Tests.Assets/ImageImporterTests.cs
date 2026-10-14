using System.IO.Compression;
using Xunit;
using Phalanx.World;
using Phalanx.Caching;
using Phalanx.Assets.Import;

namespace Phalanx.Tests;

// ImageImporter.cpp:542 (Load, the "image_pixels" cache) and ResourceCache::Load for textures. The images are small
// pngs written by the tests.
[Collection("GeneratedCache")]
public sealed class ImageImporterTests : IDisposable
{
    private readonly string? m_previous_root;
    private readonly string m_root;

    public ImageImporterTests()
    {
        m_previous_root = GeneratedCache.Root;
        m_root = Path.Combine(Path.GetTempPath(), "phalanx-image-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(m_root);
        GeneratedCache.Root = m_root;
        GeneratedCache.ResetStatistics();
        ResourceCache.Clear();
    }

    public void Dispose()
    {
        ResourceCache.Clear();
        GeneratedCache.Root = m_previous_root;
        try { Directory.Delete(m_root, true); } catch (IOException) { }
    }

    // a minimal png encoder: colour type 6 (rgba) or 0 (grey), 8 bit, filter 0 rows
    private static byte[] Png(int width, int height, int channels, byte[] pixels)
    {
        static uint Crc(ReadOnlySpan<byte> data)
        {
            uint c = 0xffffffffu;
            foreach (byte b in data)
            {
                c ^= b;
                for (int k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xedb88320u ^ (c >> 1) : c >> 1;
            }
            return c ^ 0xffffffffu;
        }
        var output = new MemoryStream();
        output.Write(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a });
        void Chunk(string type, byte[] data)
        {
            var be = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(be, (uint)data.Length);
            output.Write(be);
            byte[] typed = System.Text.Encoding.ASCII.GetBytes(type).Concat(data).ToArray();
            output.Write(typed);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(be, Crc(typed));
            output.Write(be);
        }
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), (uint)height);
        header[8] = 8; header[9] = (byte)(channels == 4 ? 6 : 0);
        Chunk("IHDR", header);
        var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(pixels, y * width * channels, width * channels);
        }
        var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(raw.ToArray());
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", Array.Empty<byte>());
        return output.ToArray();
    }

    // 4x2 rgba: top row red, green, blue, white; bottom row black with alpha 0..255
    private static byte[] Rgba4x2(byte alpha = 128) => new byte[]
    {
        255, 0, 0, 255,   0, 255, 0, 255,   0, 0, 255, 255,   255, 255, 255, 255,
        0, 0, 0, 255,     0, 0, 0, 255,     0, 0, 0, 255,     0, 0, 0, alpha,
    };

    private string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(m_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public void A_png_decodes_top_down_into_rgba8_with_flags_and_a_full_mip_chain()
    {
        string path = Write("colour.png", Png(4, 2, 4, Rgba4x2()));
        RHI_Texture texture = ImageImporter.Load(path)!;
        Assert.Equal(4u, texture.GetWidth());
        Assert.Equal(2u, texture.GetHeight());
        Assert.Equal("colour", texture.GetTextureName());
        Assert.Equal(path, texture.GetResourceFilePath());
        Assert.Equal(Rgba4x2(), texture.GetCpuMips()[0]);
        Assert.Equal(3, texture.GetCpuMips().Count); // 4x2, 2x1, 1x1
        Assert.Equal(4, texture.GetCpuMips()[2].Length);
        Assert.True(texture.GetCpuFlags().HasFlag(RHI_Texture_Cpu_Flags.Transparent));
        Assert.False(texture.GetCpuFlags().HasFlag(RHI_Texture_Cpu_Flags.Greyscale));
        Assert.True(texture.IsSemiTransparent());

        // fully opaque: no transparent flag
        RHI_Texture opaque = ImageImporter.Load(Write("opaque.png", Png(4, 2, 4, Rgba4x2(255))))!;
        Assert.False(opaque.GetCpuFlags().HasFlag(RHI_Texture_Cpu_Flags.Transparent));
    }

    [Fact]
    public void A_one_channel_png_is_greyscale()
    {
        RHI_Texture texture = ImageImporter.Load(Write("grey.png", Png(2, 2, 1, new byte[] { 0, 64, 128, 255 })))!;
        Assert.True(texture.GetCpuFlags().HasFlag(RHI_Texture_Cpu_Flags.Greyscale));
        Assert.True(texture.IsGreyscaleCpu());
        Assert.Equal(new byte[] { 64, 64, 64, 255 }, texture.GetCpuMips()[0][4..8]);
    }

    [Fact]
    public void The_decoded_pixels_are_cached_by_the_source_bytes()
    {
        string path = Write("cached.png", Png(4, 2, 4, Rgba4x2()));
        RHI_Texture first = ImageImporter.Load(path)!;
        GeneratedCache.Statistics s = GeneratedCache.GetStatistics(ImageImporter.CacheCategory);
        Assert.Equal(0ul, s.hits);
        Assert.Equal(1ul, s.missing);

        RHI_Texture second = ImageImporter.Load(path)!;
        s = GeneratedCache.GetStatistics(ImageImporter.CacheCategory);
        Assert.Equal(1ul, s.hits);
        Assert.Equal(first.GetCpuMips()[0], second.GetCpuMips()[0]);
        Assert.Equal(first.GetCpuFlags(), second.GetCpuFlags());
        Assert.Equal(3, second.GetCpuMips().Count);

        // other bytes at the same path are another key
        Write("cached.png", Png(4, 2, 4, Rgba4x2(255)));
        RHI_Texture third = ImageImporter.Load(path)!;
        Assert.Equal(1ul, GeneratedCache.GetStatistics(ImageImporter.CacheCategory).hits);
        Assert.False(third.GetCpuFlags().HasFlag(RHI_Texture_Cpu_Flags.Transparent));
    }

    [Fact]
    public void A_requested_size_rescales_even_when_only_one_dimension_differs()
    {
        // 4x2 to 2x2: spartan's && (ImageImporter.cpp:693) would keep 4x2
        RHI_Texture texture = ImageImporter.Load(Write("scaled.png", Png(4, 2, 4, Rgba4x2())), 2, 2)!;
        Assert.Equal(2u, texture.GetWidth());
        Assert.Equal(2u, texture.GetHeight());
        Assert.Equal(16, texture.GetCpuMips()[0].Length);
    }

    [Fact]
    public void Missing_or_undecodable_files_load_as_null()
    {
        Assert.Null(ImageImporter.Load(Path.Combine(m_root, "missing.png")));
        Assert.Null(ImageImporter.Load(Write("garbage.png", new byte[] { 1, 2, 3, 4, 5 })));
        Assert.Null(ResourceCache.LoadTexture(Path.Combine(m_root, "missing.png")));
        Assert.Equal(0, ResourceCache.GetTextureCount());
    }

    [Fact]
    public void The_resource_cache_shares_one_texture_per_path()
    {
        string path = Write("shared.png", Png(4, 2, 4, Rgba4x2()));
        RHI_Texture a = ResourceCache.LoadTexture(path)!;
        RHI_Texture b = ResourceCache.LoadTexture(Path.Combine(m_root, ".", "shared.png"))!;
        Assert.Same(a, b);
        Assert.True(ResourceCache.IsCached(path));
        Assert.NotSame(a, ResourceCache.LoadTexture(path, 2, 2)); // another size is another texture
        ResourceCache.Clear();
        Assert.False(ResourceCache.IsCached(path));
        Assert.NotSame(a, ResourceCache.LoadTexture(path));
    }
}
