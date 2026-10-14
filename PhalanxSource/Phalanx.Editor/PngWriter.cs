// a minimal rgba8 png encoder (zlib stored by System.IO.Compression), for offscreen captures
using System.IO.Compression;

namespace Phalanx.Editor;

internal static class PngWriter
{
    public static void Write(string path, uint width, uint height, byte[] rgba)
    {
        using var file = File.Create(path);
        file.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        var ihdr = new byte[13];
        BE(ihdr, 0, width); BE(ihdr, 4, height);
        ihdr[8] = 8; ihdr[9] = 6; // 8 bit rgba
        Chunk(file, "IHDR", ihdr);

        var raw = new MemoryStream();
        for (uint y = 0; y < height; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgba, (int)(y * width * 4), (int)(width * 4));
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, leaveOpen: true))
            raw.WriteTo(zs);
        Chunk(file, "IDAT", z.ToArray());
        Chunk(file, "IEND", Array.Empty<byte>());
    }

    private static void BE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var head = new byte[8];
        BE(head, 0, (uint)data.Length);
        for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        s.Write(head);
        s.Write(data);
        uint crc = Crc(head.AsSpan(4, 4), 0xFFFFFFFFu);
        crc = Crc(data, crc) ^ 0xFFFFFFFFu;
        var c = new byte[4]; BE(c, 0, crc); s.Write(c);
    }

    private static uint Crc(ReadOnlySpan<byte> data, uint crc)
    {
        foreach (byte b in data)
        {
            crc ^= b;
            for (int k = 0; k < 8; k++)
                crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
        return crc;
    }
}
