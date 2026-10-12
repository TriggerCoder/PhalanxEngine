using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Silk.NET.Shaderc;

namespace Phalanx;

public enum ShaderStage { Vertex, Fragment, Compute }

public sealed class ShaderCompileException : Exception
{
    public ShaderCompileException(string message) : base(message) { }
}

/// <summary>
/// Runtime GLSL -> SPIR-V compiler (shaderc via Silk.NET.Shaderc, native binaries from Silk.NET.Shaderc.Native).
/// <para>#include "file" is resolved here, not by a native callback: files are looked up relative to the including file,
/// then in the shader root, each file is expanded once (pragma-once semantics, the headers also have guards), and
/// GL_GOOGLE_cpp_style_line_directive #line markers keep error messages pointing at the original file and line.</para>
/// <para>The expanded source plus defines, stage, entry point and options are hashed (XxHash64) to key a disk cache of
/// .spv files, so any change to an included header invalidates dependents automatically.</para>
/// </summary>
public sealed unsafe class ShaderCompiler : IDisposable
{
    private static readonly Regex IncludeRe = new(@"^\s*#\s*include\s+[""<]([^"">]+)["">]", RegexOptions.Compiled);
    private static readonly Regex VersionRe = new(@"^\s*#\s*version\b", RegexOptions.Compiled);
    private const uint CacheFormatVersion = 1;

    private readonly Shaderc api;
    private readonly Compiler* compiler;
    private readonly object compiler_lock = new(); // a shaderc compiler object is not thread safe
    private readonly string shader_root;
    private readonly string? cache_directory;
    private bool disposed;

    public bool Optimize { get; set; } = true;
    public bool DebugInfo { get; set; }

    /// <param name="shaderRoot">folder with the .glsl/.vert/.frag/.comp files, defaults to &lt;app&gt;/shaders</param>
    /// <param name="cacheDirectory">where compiled .spv files are cached, null disables the disk cache</param>
    public ShaderCompiler(string? shaderRoot = null, string? cacheDirectory = null)
    {
        shader_root = Path.GetFullPath(shaderRoot ?? Path.Combine(AppContext.BaseDirectory, "shaders"));
        cache_directory = cacheDirectory;
        if (cache_directory != null)
            Directory.CreateDirectory(cache_directory);
        api = Shaderc.GetApi();
        compiler = api.CompilerInitialize();
        if (compiler == null)
            throw new InvalidOperationException("shaderc_compiler_initialize failed");
    }

    public string ShaderRoot => shader_root;

    public static ShaderStage StageFromPath(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".vert" => ShaderStage.Vertex,
        ".frag" => ShaderStage.Fragment,
        ".comp" => ShaderStage.Compute,
        var e   => throw new ArgumentException($"unknown shader extension '{e}', expected .vert/.frag/.comp", nameof(path)),
    };

    /// <summary>Compiles a shader file (path relative to the shader root, or absolute). Returns SPIR-V words.</summary>
    public uint[] CompileFile(string path, IReadOnlyDictionary<string, string>? defines = null, string entryPoint = "main")
    {
        string full = Path.IsPathRooted(path) ? path : Path.Combine(shader_root, path);
        string expanded = Preprocess(full);
        return CompileSource(expanded, StageFromPath(full), Path.GetFileName(full), defines, entryPoint, alreadyExpanded: true);
    }

    /// <summary>Compiles GLSL source. #include directives are resolved against the shader root unless alreadyExpanded.</summary>
    public uint[] CompileSource(string source, ShaderStage stage, string name = "inline.glsl",
                                IReadOnlyDictionary<string, string>? defines = null, string entryPoint = "main", bool alreadyExpanded = false)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        string expanded = alreadyExpanded ? source : Expand(source, Path.Combine(shader_root, name), new HashSet<string>(StringComparer.OrdinalIgnoreCase), insertLineExtension: true);

        // sorted so the key does not depend on dictionary order
        var sortedDefines = defines?.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray() ?? Array.Empty<KeyValuePair<string, string>>();
        string key = CacheKey(expanded, stage, entryPoint, sortedDefines);
        string? cachePath = cache_directory != null ? Path.Combine(cache_directory, $"{Path.GetFileNameWithoutExtension(name)}.{stage.ToString().ToLowerInvariant()}.{key}.spv") : null;
        if (cachePath != null && File.Exists(cachePath))
        {
            byte[] cached = File.ReadAllBytes(cachePath);
            if (cached.Length >= 4 && cached.Length % 4 == 0)
                return MemoryMarshal.Cast<byte, uint>(cached).ToArray();
        }

        uint[] spirv = CompileNative(expanded, stage, name, entryPoint, sortedDefines);

        if (cachePath != null)
        {
            // write to a temp file and move, so a concurrent reader never sees a half written file
            string tmp = cachePath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllBytes(tmp, MemoryMarshal.AsBytes(spirv.AsSpan()).ToArray());
            File.Move(tmp, cachePath, overwrite: true);
        }
        return spirv;
    }

    /// <summary>Returns the fully expanded source of a shader file (useful for debugging include resolution).</summary>
    public string Preprocess(string fullPath) =>
        Expand(File.ReadAllText(fullPath), Path.GetFullPath(fullPath), new HashSet<string>(StringComparer.OrdinalIgnoreCase), insertLineExtension: true);

    private string Expand(string source, string filePath, HashSet<string> included, bool insertLineExtension)
    {
        included.Add(filePath);
        var sb = new StringBuilder(source.Length + 256);
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        string display = DisplayName(filePath);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            var m = IncludeRe.Match(line);
            if (m.Success)
            {
                string target = ResolveInclude(m.Groups[1].Value, filePath)
                    ?? throw new ShaderCompileException($"{display}({i + 1}): cannot find include \"{m.Groups[1].Value}\" (searched next to the file and in {shader_root})");
                if (!included.Contains(target))
                {
                    sb.Append("#line 1 \"").Append(DisplayName(target)).Append("\"\n");
                    sb.Append(Expand(File.ReadAllText(target), target, included, insertLineExtension: false));
                    sb.Append('\n');
                }
                sb.Append("#line ").Append(i + 2).Append(" \"").Append(display).Append("\"\n");
                continue;
            }
            sb.Append(line).Append('\n');
            if (insertLineExtension && VersionRe.IsMatch(line))
            {
                // required for '#line N "file"'; must come right after #version
                sb.Append("#extension GL_GOOGLE_cpp_style_line_directive : require\n");
                sb.Append("#line ").Append(i + 2).Append(" \"").Append(display).Append("\"\n");
                insertLineExtension = false;
            }
        }
        return sb.ToString();
    }

    private string? ResolveInclude(string name, string includer)
    {
        string local = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(includer) ?? shader_root, name));
        if (File.Exists(local)) return local;
        string root = Path.GetFullPath(Path.Combine(shader_root, name));
        return File.Exists(root) ? root : null;
    }

    private string DisplayName(string path)
    {
        string rel = Path.GetRelativePath(shader_root, path);
        return (rel.StartsWith("..") ? path : rel).Replace('\\', '/');
    }

    private string CacheKey(string expanded, ShaderStage stage, string entryPoint, KeyValuePair<string, string>[] defines)
    {
        var h = new XxHash64();
        void Add(string s) { h.Append(Encoding.UTF8.GetBytes(s)); h.Append(stackalloc byte[] { 0 }); }
        Add(CacheFormatVersion.ToString());
        Add(expanded);
        Add(stage.ToString());
        Add(entryPoint);
        Add(Optimize ? "O" : "O0");
        Add(DebugInfo ? "g" : "");
        foreach (var kv in defines) { Add(kv.Key); Add(kv.Value); }
        return Convert.ToHexString(h.GetCurrentHash()).ToLowerInvariant();
    }

    private uint[] CompileNative(string source, ShaderStage stage, string name, string entryPoint, KeyValuePair<string, string>[] defines)
    {
        lock (compiler_lock)
        {
            CompileOptions* options = api.CompileOptionsInitialize();
            CompilationResult* result = null;
            try
            {
                api.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
                api.CompileOptionsSetTargetSpirv(options, SpirvVersion.Shaderc16);
                api.CompileOptionsSetOptimizationLevel(options, Optimize ? OptimizationLevel.Performance : OptimizationLevel.Zero);
                if (DebugInfo)
                    api.CompileOptionsSetGenerateDebugInfo(options);
                foreach (var kv in defines)
                    api.CompileOptionsAddMacroDefinition(options, kv.Key, (nuint)Encoding.UTF8.GetByteCount(kv.Key), kv.Value, (nuint)Encoding.UTF8.GetByteCount(kv.Value));

                ShaderKind kind = stage switch
                {
                    ShaderStage.Vertex   => ShaderKind.VertexShader,
                    ShaderStage.Fragment => ShaderKind.FragmentShader,
                    _                    => ShaderKind.ComputeShader,
                };
                result = api.CompileIntoSpv(compiler, source, (nuint)Encoding.UTF8.GetByteCount(source), kind, name, entryPoint, options);
                if (api.ResultGetCompilationStatus(result) != CompilationStatus.Success)
                    throw new ShaderCompileException($"{name}: {api.ResultGetErrorMessageS(result)}");

                int length = (int)api.ResultGetLength(result);
                var bytes = new ReadOnlySpan<byte>(api.ResultGetBytes(result), length);
                return MemoryMarshal.Cast<byte, uint>(bytes).ToArray();
            }
            finally
            {
                if (result != null) api.ResultRelease(result);
                api.CompileOptionsRelease(options);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        api.CompilerRelease(compiler);
        api.Dispose();
    }
}
