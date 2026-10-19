using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Phalanx;
using Xunit;
using Phalanx.Math;
using Phalanx.Geometry;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;
using Phalanx.Rendering;

namespace Phalanx.Tests;

// Layout source of truth: shaders/shared_buffers.glsl (std140 for uniform buffers, std430 for storage
// buffers and push constants). Phalanx.Generators emits the structs and the SharedBufferLayout table at build
// time; SpirvLayoutReflectionTests cross-checks the table against real SPIR-V produced by shaderc.
public class SharedBufferLayoutTests
{
    public static IEnumerable<object[]> Structs() => SharedBufferLayout.Expected.Select(e => new object[] { e.Type.Name });

    private static (Type Type, string Layout, int Size, (string Field, int Offset, int ArrayStride)[] Fields) Find(string name) =>
        SharedBufferLayout.Expected.Single(e => e.Type.Name == name);

    private static int ManagedSize(Type t) =>
        (int)typeof(Unsafe).GetMethod(nameof(Unsafe.SizeOf))!.MakeGenericMethod(t).Invoke(null, null)!;

    [Theory]
    [MemberData(nameof(Structs))]
    public void Size_matches_glsl_layout(string name)
    {
        var e = Find(name);
        Assert.Equal(e.Size, Marshal.SizeOf(e.Type));
        Assert.Equal(e.Size, ManagedSize(e.Type));
    }

    [Theory]
    [MemberData(nameof(Structs))]
    public void Field_offsets_match_glsl_layout(string name)
    {
        var e = Find(name);
        foreach (var (field, offset, _) in e.Fields)
            Assert.True(offset == (int)Marshal.OffsetOf(e.Type, field), $"{name}.{field}: expected {offset}, got {Marshal.OffsetOf(e.Type, field)}");
    }

    [Theory]
    [MemberData(nameof(Structs))]
    public void Struct_follows_std_rules(string name)
    {
        // independent of the generator: re-derive the std140/std430 alignment constraints from the C# field types
        var e = Find(name);
        int structAlign = e.Layout == "std140" ? 16 : 4;
        foreach (var (field, offset, stride) in e.Fields)
        {
            Type ft = e.Type.GetField(field)!.FieldType;
            int align = ft == typeof(Vector3) || ft == typeof(Vector4) || ft == typeof(Matrix4x4) ? 16
                      : ft == typeof(Vector2) || ft == typeof(double) ? 8
                      : ft.IsPrimitive ? 4
                      : (e.Layout == "std140" ? 16 : (stride % 16 == 0 ? 16 : stride % 8 == 0 ? 8 : 4)); // arrays
            Assert.True(offset % align == 0, $"{name}.{field} at {offset} violates {e.Layout} alignment {align}");
            if (stride != 0 && e.Layout == "std140")
                Assert.True(stride % 16 == 0, $"{name}.{field}: std140 array stride {stride} must be a multiple of 16");
            structAlign = MathEx.Max(structAlign, align);
        }
        Assert.True(e.Size % structAlign == 0, $"{name}: size {e.Size} not a multiple of alignment {structAlign}");
    }

    [Fact]
    public void Known_std140_and_std430_offsets()
    {
        // hand-computed spot checks
        // FrameBufferData (std140): 12 mat4 = 768, then 4 vec2
        Assert.Equal(768, (int)Marshal.OffsetOf<FrameBufferData>(nameof(FrameBufferData.resolution_render)));
        // vec3 + float share one 16-byte slot
        Assert.Equal((int)Marshal.OffsetOf<FrameBufferData>(nameof(FrameBufferData.camera_position)) + 12,
                     (int)Marshal.OffsetOf<FrameBufferData>(nameof(FrameBufferData.camera_near)));
        Assert.Equal(0, (int)Marshal.OffsetOf<FrameBufferData>(nameof(FrameBufferData.time)) % 8);
        // PulledVertex (std430): 3 floats + 3 uints = 24, scalar aligned so the array stride matches the cpu vertex
        Assert.Equal(12, (int)Marshal.OffsetOf<PulledVertex>(nameof(PulledVertex.uv)));
        Assert.Equal(24, Unsafe.SizeOf<PulledVertex>());
        Assert.Equal(Unsafe.SizeOf<RHI_Vertex_PosTexNorTan>(), Unsafe.SizeOf<PulledVertex>());
        // push constants: 4 uints + vec4 values[5]
        Assert.Equal(96, Unsafe.SizeOf<PassBufferData>());
        // VkDrawIndexedIndirectCommand / VkDispatchIndirectCommand are scalar-only, unchanged
        Assert.Equal(20, Unsafe.SizeOf<IndirectDrawArgs>());
        Assert.Equal(12, Unsafe.SizeOf<IndirectDispatchArgs>());
        // GeometryInfo vec4 rows must be 16-byte aligned in std430 (HLSL packed them at 56)
        Assert.Equal(64, (int)Marshal.OffsetOf<GeometryInfo>(nameof(GeometryInfo.object_to_world_0)));
        Assert.Equal(160, Unsafe.SizeOf<GeometryInfo>());
    }

    [Fact]
    public void The_store_packed_transform_is_the_gpu_packed_instance()
    {
        // EntityStore.Instances is uploaded as is into the PackedInstance buffers (Renderer.Instancing.cs)
        Assert.Equal(Unsafe.SizeOf<PackedInstance>(), Unsafe.SizeOf<Phalanx.World.PackedTransform>());
        foreach (string field in new[] { "pos_x", "pos_y", "pos_z", "rotation_xy", "rotation_zw", "scale_x", "scale_y", "scale_z" })
            Assert.Equal(Marshal.OffsetOf<PackedInstance>(field), Marshal.OffsetOf<Phalanx.World.PackedTransform>(field));
        var position = new System.Numerics.Vector3(1, -2, 3);
        var rotation = System.Numerics.Quaternion.CreateFromYawPitchRoll(0.3f, 1.1f, -0.4f);
        var scale = new System.Numerics.Vector3(2, 0.5f, 1);
        Phalanx.World.PackedTransform packed = Phalanx.World.PackedTransform.Pack(position, rotation, scale);
        PackedInstance instance = Unsafe.As<Phalanx.World.PackedTransform, PackedInstance>(ref packed);
        Assert.Equal(packed.GetMatrix(), instance.GetMatrix());
        var reference = new PackedInstance();
        reference.SetMatrix(System.Numerics.Matrix4x4.CreateScale(scale) * System.Numerics.Matrix4x4.CreateFromQuaternion(rotation) * System.Numerics.Matrix4x4.CreateTranslation(position));
        Assert.Equal(reference.rotation_xy, instance.rotation_xy);
        Assert.Equal(reference.rotation_zw, instance.rotation_zw);
    }

    [Fact]
    public void Matrix_bytes_read_column_major_equal_row_vector_math()
    {
        // upload Matrix4x4 unchanged; GLSL reads column-major, i.e. column j = 4 consecutive floats j*4..j*4+3
        Matrix4x4 m = Matrix4x4.CreateFromYawPitchRoll(0.3f, 1.1f, -0.4f) * Matrix4x4.CreateTranslation(5, -2, 9);
        Vector4 v = new Vector4(1.5f, -3.0f, 2.0f, 1.0f);
        ReadOnlySpan<float> bytes = MemoryMarshal.Cast<Matrix4x4, float>(new ReadOnlySpan<Matrix4x4>(in m));
        // GLSL: result = M_glsl * v, with M_glsl[col][row] = bytes[col * 4 + row]
        Span<float> r = stackalloc float[4];
        for (int row = 0; row < 4; row++)
            r[row] = bytes[0 * 4 + row] * v.X + bytes[1 * 4 + row] * v.Y + bytes[2 * 4 + row] * v.Z + bytes[3 * 4 + row] * v.W;
        Vector4 cpu = Vector4.Transform(v, m); // v * M
        Assert.Equal(cpu.X, r[0], 4); Assert.Equal(cpu.Y, r[1], 4); Assert.Equal(cpu.Z, r[2], 4); Assert.Equal(cpu.W, r[3], 4);
    }

    [Fact]
    public void Generated_code_matches_current_glsl_header()
    {
        // the source generator hashes the header it consumed; this proves the build picked up the current file
        // walk up from bin/ to the repo root, then read the source header (not the copy in bin/shaders)
        string[] header = { "PhalanxSource", "Phalanx.Rendering", "Shaders", "shared_buffers.glsl" };
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, Path.Combine(header))))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        string text = File.ReadAllText(Path.Combine(dir!, Path.Combine(header)));
        string hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
        Assert.Equal(SharedBuffers.HeaderSha256, hash);
    }

    [Fact]
    public void Translated_from_pinned_commit()
    {
        Assert.Equal("4c07baa298ddbc4fecf8704986f85b60c67da4aa", SharedBuffers.SpartanCommit);
    }

    [Fact]
    public void PassBufferData_slot_setters_write_consecutive_floats()
    {
        var p = new PassBufferData();
        p.Set(3, new Vector3(1, 2, 3));
        p.Set(19, 7u);
        Assert.Equal(1.0f, p.Get(3));
        Assert.Equal(3.0f, p.Get(5));
        Assert.Equal(7u, BitConverter.SingleToUInt32Bits(p.Get(19)));
    }
}
