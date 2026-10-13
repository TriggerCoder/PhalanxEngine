using Phalanx;
using Xunit;

namespace Phalanx.Tests;

// Independent oracle for the generated layouts: compile a probe shader that declares every shared struct in the
// block type it is used with (std140 UBO, std430 push constant, std430 SSBO), then read the Offset / ArrayStride
// decorations straight out of the SPIR-V binary. Replaces the old glslangValidator + spirv-cross Python check.
public class SpirvLayoutReflectionTests
{
    private const int OpName = 5, OpMemberName = 6, OpTypeRuntimeArray = 29, OpDecorate = 71, OpMemberDecorate = 72;
    private const int DecorationArrayStride = 6, DecorationOffset = 35;

    private static string ReadString(uint[] words, int start, int end)
    {
        var bytes = new List<byte>();
        for (int i = start; i < end; i++)
            for (int b = 0; b < 4; b++)
            {
                byte c = (byte)(words[i] >> (8 * b));
                if (c == 0) return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(c);
            }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    [Fact]
    public void Generated_offsets_match_SPIR_V_decorations()
    {
        var src = new System.Text.StringBuilder("#version 460\n#extension GL_GOOGLE_include_directive : require\n#include \"shared_buffers.glsl\"\n");
        int i = 0;
        foreach (var e in SharedBufferLayout.Expected)
        {
            string n = e.Type.Name;
            if (n == nameof(PassBufferData)) src.Append($"layout(push_constant, std430) uniform B{i} {{ {n} data; }} b{i};\n");
            else if (e.Layout == "std140")   src.Append($"layout(std140, set = 0, binding = {i}) uniform B{i} {{ {n} data; }} b{i};\n");
            else                             src.Append($"layout(std430, set = 1, binding = {i}) readonly buffer B{i} {{ {n} data[]; }} b{i};\n");
            i++;
        }
        src.Append("layout(local_size_x = 1) in;\nvoid main() {}\n");

        using var compiler = new ShaderCompiler(cacheDirectory: null) { Optimize = false }; // keep names and unused blocks
        uint[] spv = compiler.CompileSource(src.ToString(), ShaderStage.Compute, "layout_probe.comp");

        var names = new Dictionary<uint, string>();
        var memberNames = new Dictionary<(uint, uint), string>();
        var offsets = new Dictionary<(uint, uint), int>();
        var arrayStride = new Dictionary<uint, int>();
        var runtimeArrayElem = new Dictionary<uint, uint>();
        for (int w = 5; w < spv.Length;)
        {
            int count = (int)(spv[w] >> 16), op = (int)(spv[w] & 0xffff);
            if (op == OpName) names[spv[w + 1]] = ReadString(spv, w + 2, w + count);
            else if (op == OpMemberName) memberNames[(spv[w + 1], spv[w + 2])] = ReadString(spv, w + 3, w + count);
            else if (op == OpMemberDecorate && spv[w + 3] == DecorationOffset) offsets[(spv[w + 1], spv[w + 2])] = (int)spv[w + 4];
            else if (op == OpDecorate && spv[w + 2] == DecorationArrayStride) arrayStride[spv[w + 1]] = (int)spv[w + 3];
            else if (op == OpTypeRuntimeArray) runtimeArrayElem[spv[w + 1]] = spv[w + 2];
            w += count;
        }

        int checkedMembers = 0;
        foreach (var e in SharedBufferLayout.Expected)
        {
            uint id = names.Single(kv => kv.Value == e.Type.Name).Key;
            foreach (var (field, offset, _) in e.Fields)
            {
                uint idx = memberNames.Single(kv => kv.Key.Item1 == id && kv.Value == field).Key.Item2;
                Assert.True(offsets[(id, idx)] == offset, $"{e.Type.Name}.{field}: SPIR-V offset {offsets[(id, idx)]}, generated {offset}");
                checkedMembers++;
            }
            // storage buffer element size == runtime array stride
            foreach (var (arr, elem) in runtimeArrayElem)
                if (elem == id)
                    Assert.True(arrayStride[arr] == e.Size, $"{e.Type.Name}: SPIR-V array stride {arrayStride[arr]}, generated size {e.Size}");
        }
        Assert.Equal(SharedBufferLayout.Expected.Sum(e => e.Fields.Length), checkedMembers);
    }
}
