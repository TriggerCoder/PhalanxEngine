using Phalanx;
using Xunit;
using Phalanx.Geometry;
using Phalanx.World;
using Phalanx.Input;
using Phalanx.Platform;
using Phalanx.Caching;
using Phalanx.Rendering;

namespace Phalanx.Tests;

public class ShaderCompilerTests : IDisposable
{
    private const uint SpirvMagic = 0x07230203;
    private readonly string cache = Path.Combine(Path.GetTempPath(), "phalanx-shader-cache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
    }

    [Fact]
    public void Shaders_are_copied_to_the_output_directory()
    {
        foreach (string f in new[] { "shared_buffers.glsl", "common_resources.glsl", "blit.comp" })
            Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, "shaders", f)), f);
    }

    [Fact]
    public void Compiles_blit_comp_with_includes_at_runtime()
    {
        using var compiler = new ShaderCompiler(cacheDirectory: cache);
        uint[] spv = compiler.CompileFile("blit.comp");
        Assert.Equal(SpirvMagic, spv[0]);
        Assert.True(spv.Length > 100);
    }

    [Fact]
    public void Disk_cache_is_keyed_by_source_includes_and_defines()
    {
        using var compiler = new ShaderCompiler(cacheDirectory: cache);
        uint[] a = compiler.CompileFile("blit.comp");
        Assert.Single(Directory.GetFiles(cache, "*.spv"));

        uint[] b = compiler.CompileFile("blit.comp"); // cache hit, no new file
        Assert.Single(Directory.GetFiles(cache, "*.spv"));
        Assert.Equal(a, b);

        var defines = new Dictionary<string, string> { ["THREAD_GROUP_COUNT_X"] = "16", ["THREAD_GROUP_COUNT_Y"] = "16" };
        uint[] c = compiler.CompileFile("blit.comp", defines); // different defines -> different key
        Assert.Equal(2, Directory.GetFiles(cache, "*.spv").Length);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void Include_change_invalidates_cache()
    {
        string root = Path.Combine(cache, "src");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "inc.glsl"), "const float k = 1.0;\n");
        File.WriteAllText(Path.Combine(root, "t.comp"), "#version 460\n#extension GL_GOOGLE_include_directive : require\n#include \"inc.glsl\"\nlayout(local_size_x = 1) in;\nlayout(std430, binding = 0) buffer B { float v; };\nvoid main() { v = k; }\n");
        using var compiler = new ShaderCompiler(root, Path.Combine(cache, "spv"));
        compiler.CompileFile("t.comp");
        File.WriteAllText(Path.Combine(root, "inc.glsl"), "const float k = 2.0;\n");
        compiler.CompileFile("t.comp");
        Assert.Equal(2, Directory.GetFiles(Path.Combine(cache, "spv"), "*.spv").Length);
    }

    [Fact]
    public void Errors_report_the_original_file_and_line()
    {
        string root = Path.Combine(cache, "err");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "bad.glsl"), "// line 1\nconst float x = undefined_symbol;\n");
        File.WriteAllText(Path.Combine(root, "e.comp"), "#version 460\n#include \"bad.glsl\"\nlayout(local_size_x = 1) in;\nvoid main() {}\n");
        using var compiler = new ShaderCompiler(root, cacheDirectory: null);
        var ex = Assert.Throws<ShaderCompileException>(() => compiler.CompileFile("e.comp"));
        Assert.Contains("bad.glsl:2", ex.Message);
        Assert.Contains("undefined_symbol", ex.Message);
    }

    [Fact]
    public void Missing_include_is_a_clear_error()
    {
        using var compiler = new ShaderCompiler(cacheDirectory: null);
        var ex = Assert.Throws<ShaderCompileException>(() =>
            compiler.CompileSource("#version 460\n#include \"nope.glsl\"\nvoid main() {}\n", ShaderStage.Compute));
        Assert.Contains("nope.glsl", ex.Message);
    }
}
