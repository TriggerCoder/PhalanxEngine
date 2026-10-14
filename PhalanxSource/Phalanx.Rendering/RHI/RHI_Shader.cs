// Port of SpartanEngine source/rhi/RHI_Shader.h/.cpp + vulkan/Vulkan_Shader.cpp @ 4c07baa
// Front end: Rendering/ShaderCompiler (shaderc, GLSL) instead of DXC (HLSL), reflection: RHI_SpirvReflection instead
// of SPIRV-Cross. Deviations:
// - Entry point is "main" for every stage (GLSL), spartan's main_vs/main_ps/... names are HLSL only. There are no
//   target profiles. The DXC arguments map to shaderc: -fspv-target-env=vulkan1.3 (target env), -fspv-preserve-bindings
//   (preserve bindings), resource names survive optimization through ShaderCompiler.KeepNames, the register shifts are written into the GLSL bindings (common_resources.glsl),
//   -fvk-invert-y / dx layout / -Zpc have no GLSL equivalent (GLSL is column major and std140/std430 by declaration).
// - Stages: Vertex, Pixel (fragment) and Compute; hull/domain/mesh/ray tracing arrive with their features.
// - Include processing comes from ShaderCompiler.Preprocess (pragma-once + #line markers); the names/paths/sources
//   lists are rebuilt from its #line markers. Spartan reverses them so the main file comes first, same here.
// - The hash is XxHash64 over the expanded source and the sorted defines (spartan: std::hash + rhi_hash_combine, the
//   defines in unordered_map order), plus the stage and the vertex type. Spartan leaves both out (RHI_Shader.cpp:292-297)
//   and so does its pipeline state hash (RHI_PipelineState.cpp:93-102), so one vertex shader compiled for two vertex
//   types maps both to the first cached pipeline and its vertex input state.
// - Async compilation runs on the .NET thread pool (spartan: ThreadPool::AddTask).
// - CompileFromSpirv clears the previous reflection and retires the previous module (spartan @ 4c07baa appends to
//   m_descriptors and overwrites m_rhi_resource without queueing it for deletion, Vulkan_Shader.cpp:245-272).
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Silk.NET.Vulkan;
using static Phalanx.Rendering.RHI.RHI_Implementation;

namespace Phalanx.Rendering.RHI;

internal enum RHI_ShaderCompilationState
{
    Idle,
    Compiling,
    Succeeded,
    Failed
}

internal sealed unsafe class RHI_Shader : IDisposable
{
    private static readonly Regex LineMarkerRe = new(@"^#line 1 ""([^""]+)""$", RegexOptions.Compiled | RegexOptions.Multiline);
    private static ShaderCompiler? s_compiler;
    private static readonly object s_compiler_lock = new();

    private string m_file_path = "";
    private string m_preprocessed_source = "";
    private List<string> m_names = new();
    private List<string> m_file_paths = new();
    private List<string> m_sources = new();
    private readonly Dictionary<string, string> m_defines = new();
    private List<RHI_Descriptor> m_descriptors = new();
    private volatile RHI_ShaderCompilationState m_compilation_state = RHI_ShaderCompilationState.Idle;
    private RHI_Shader_Type m_shader_type = RHI_Shader_Type.Max;
    private RHI_Vertex_Type m_vertex_type = RHI_Vertex_Type.Max;
    private RHI_InputLayout? m_input_layout;
    private ulong m_hash;
    private uint[] m_spirv = Array.Empty<uint>();
    private ShaderModule m_rhi_resource;

    private RHI_Shader? m_reload;
    private volatile bool m_reloading;
    private volatile bool m_reload_ready;
    private bool m_reload_failed;
    private Task? m_task;

    public string ObjectName { get; private set; } = "";
    public string? LastError { get; private set; }

    /// <summary>The compiler every RHI_Shader uses, defaults to &lt;app&gt;/shaders with no disk cache. KeepNames is forced on.</summary>
    public static ShaderCompiler Compiler
    {
        get { lock (s_compiler_lock) return s_compiler ??= new ShaderCompiler { KeepNames = true }; }
        set { lock (s_compiler_lock) { value.KeepNames = true; s_compiler = value; } }
    }

    public static ShaderStage ToShaderStage(RHI_Shader_Type type) => type switch
    {
        RHI_Shader_Type.Vertex  => ShaderStage.Vertex,
        RHI_Shader_Type.Pixel   => ShaderStage.Fragment,
        RHI_Shader_Type.Compute => ShaderStage.Compute,
        _ => throw new NotSupportedException($"{type} shaders are not supported yet (vertex, pixel and compute only)")
    };

    // compilation
    public void Compile(RHI_Shader_Type shader_type, string file_path, bool async, RHI_Vertex_Type vertex_type = RHI_Vertex_Type.Max)
    {
        string full = Path.IsPathRooted(file_path) ? file_path : Path.Combine(Compiler.ShaderRoot, file_path);
        if (!File.Exists(full))
        {
            Log.LogError($"\"{full}\" doesn't exist.");
            return;
        }

        // a live shader must never be seen half compiled: compile into a staged copy and swap in ApplyReload
        if (m_compilation_state == RHI_ShaderCompilationState.Succeeded && m_rhi_resource.Handle != 0)
        {
            if (m_reloading)
                return;

            var staged = new RHI_Shader();
            foreach (var kv in m_defines) staged.m_defines[kv.Key] = kv.Value;
            m_reloading = true;
            void reload()
            {
                staged.Compile(shader_type, full, false, vertex_type);
                m_reload       = staged;
                m_reload_ready = true;
            }
            if (async) m_task = Task.Run(reload); else reload();
            return;
        }

        // clear
        m_input_layout = null;
        m_descriptors  = new List<RHI_Descriptor>();

        m_shader_type = shader_type;
        m_vertex_type = vertex_type;
        if (m_shader_type == RHI_Shader_Type.Vertex)
            m_input_layout = new RHI_InputLayout();

        // load
        LoadFromDrive(full);

        // compile
        m_compilation_state = RHI_ShaderCompilationState.Idle;
        void compile()
        {
            m_compilation_state = RHI_ShaderCompilationState.Compiling;
            ShaderModule resource = RHI_Compile();
            if (m_rhi_resource.Handle != 0)
                RHI_Device.DeletionQueueAdd(RHI_Resource_Type.Shader, m_rhi_resource.Handle);
            m_rhi_resource      = resource;
            m_compilation_state = m_rhi_resource.Handle != 0 ? RHI_ShaderCompilationState.Succeeded : RHI_ShaderCompilationState.Failed;

            if (m_compilation_state != RHI_ShaderCompilationState.Succeeded)
            {
                string defines_str = string.Join(", ", m_defines.Select(d => $"{d.Key} = {d.Value}"));
                Log.LogError(defines_str.Length == 0
                    ? $"Failed to compile shader \"{ObjectName}\". {LastError}"
                    : $"Failed to compile shader \"{ObjectName}\" with definitions \"{defines_str}\". {LastError}");
            }
        }
        if (async) m_task = Task.Run(compile); else compile();
    }

    /// <summary>Blocks until an async Compile (or reload) finished. Phalanx addition for tests and loaders.</summary>
    public void WaitForCompilation() => m_task?.Wait();

    public void CompileFromSpirv(RHI_Shader_Type type, ReadOnlySpan<uint> spirv, string name)
    {
        if (spirv.IsEmpty)
        {
            m_compilation_state = RHI_ShaderCompilationState.Failed;
            return;
        }

        m_shader_type = type;
        ObjectName    = name;

        ShaderModule module = CreateModule(spirv);
        if (module.Handle == 0)
        {
            m_compilation_state = RHI_ShaderCompilationState.Failed;
            return;
        }

        // deviation (see header): start from a clean reflection and retire the previous module
        m_descriptors = RHI_SpirvReflection.Reflect(spirv, m_shader_type, GetEntryPoint());
        if (m_rhi_resource.Handle != 0)
            RHI_Device.DeletionQueueAdd(RHI_Resource_Type.Shader, m_rhi_resource.Handle);
        m_spirv             = spirv.ToArray();
        m_hash              = XxHash64.HashToUInt64(MemoryMarshal.AsBytes(spirv));
        m_rhi_resource      = module;
        m_compilation_state = RHI_ShaderCompilationState.Succeeded;
    }

    public RHI_ShaderCompilationState GetCompilationState() => m_compilation_state;
    public bool IsCompiled() => m_compilation_state == RHI_ShaderCompilationState.Succeeded;

    // hot reload
    public bool IsReloading() => m_reloading;
    public bool ReloadFailed() => m_reload_failed;

    public void ApplyReload()
    {
        if (!m_reload_ready)
            return;

        RHI_Shader staged = m_reload!;
        m_reload        = null;
        m_reload_ready  = false;
        m_reload_failed = !staged.IsCompiled();

        // the sources always follow the disk, so a failed reload is not retried until the file changes again
        m_names      = staged.m_names;
        m_file_paths = staged.m_file_paths;
        m_sources    = staged.m_sources;

        // a failed reload keeps serving the last good module
        if (!m_reload_failed)
        {
            RHI_Device.DeletionQueueAdd(RHI_Resource_Type.Shader, m_rhi_resource.Handle);
            m_rhi_resource          = staged.m_rhi_resource;
            staged.m_rhi_resource   = default;
            m_descriptors           = staged.m_descriptors;
            m_preprocessed_source   = staged.m_preprocessed_source;
            m_spirv                 = staged.m_spirv;
            m_hash                  = staged.m_hash;
            m_input_layout          = staged.m_input_layout;
        }

        m_reloading = false;
    }

    // source
    public void LoadFromDrive(string file_path)
    {
        ObjectName  = Path.GetFileNameWithoutExtension(file_path);
        m_file_path = file_path;
        m_names.Clear();
        m_file_paths.Clear();
        m_sources.Clear();

        ShaderCompiler compiler = Compiler;
        m_preprocessed_source = compiler.Preprocess(file_path);

        // the main file, then every include in order of first appearance (spartan's order after its reverse)
        m_names.Add(Path.GetFileName(file_path));
        m_file_paths.Add(file_path);
        m_sources.Add(File.ReadAllText(file_path));
        foreach (Match match in LineMarkerRe.Matches(m_preprocessed_source))
        {
            string display = match.Groups[1].Value;
            string path    = Path.IsPathRooted(display) ? display : Path.GetFullPath(Path.Combine(compiler.ShaderRoot, display));
            if (m_file_paths.Contains(path) || !File.Exists(path))
                continue;
            m_names.Add(Path.GetFileName(path));
            m_file_paths.Add(path);
            m_sources.Add(File.ReadAllText(path));
        }

        m_hash = ComputeHash(m_preprocessed_source, m_defines, m_shader_type, m_vertex_type);
    }

    public static ulong ComputeHash(string preprocessed_source, IReadOnlyDictionary<string, string> defines,
                                    RHI_Shader_Type shader_type = RHI_Shader_Type.Max, RHI_Vertex_Type vertex_type = RHI_Vertex_Type.Max)
    {
        // the key/value delimiters live in the assembly's data, a stackalloc per define would grow the frame with each one
        ReadOnlySpan<byte> key_delimiter   = new byte[] { 0 };
        ReadOnlySpan<byte> value_delimiter = new byte[] { 1 };

        var h = new XxHash64();
        h.Append(stackalloc byte[] { (byte)shader_type, (byte)vertex_type });
        h.Append(Encoding.UTF8.GetBytes(preprocessed_source));
        foreach (var kv in defines.OrderBy(d => d.Key, StringComparer.Ordinal))
        {
            h.Append(key_delimiter);
            h.Append(Encoding.UTF8.GetBytes(kv.Key));
            h.Append(value_delimiter);
            h.Append(Encoding.UTF8.GetBytes(kv.Value));
        }
        return h.GetCurrentHashAsUInt64();
    }

    public IReadOnlyList<string> GetNames() => m_names;
    public IReadOnlyList<string> GetFilePaths() => m_file_paths;
    public IReadOnlyList<string> GetSources() => m_sources;
    public void SetSource(int index, string source)
    {
        if ((uint)index >= (uint)m_sources.Count)
        {
            Log.LogError($"No source with index {index} exists.");
            return;
        }
        m_sources[index] = source;
    }

    // defines
    public void AddDefine(string define, string value = "1") => m_defines[define] = value;
    public IReadOnlyDictionary<string, string> GetDefines() => m_defines;

    // misc
    public IReadOnlyList<RHI_Descriptor> GetDescriptors() => m_descriptors;
    public string GetFilePath() => m_file_path;
    public RHI_Shader_Type GetShaderStage() => m_shader_type;
    public ulong GetHash() => m_hash;
    public string GetEntryPoint() => "main";
    public RHI_Vertex_Type GetVertexType() => m_vertex_type;
    public RHI_InputLayout? GetInputLayout() => m_input_layout; // only valid for a vertex shader
    public uint GetVertexSize() => m_input_layout?.GetVertexSize() ?? 0;
    public ReadOnlySpan<uint> GetSpirv() => m_spirv;
    public ShaderModule GetRhiResource() => m_rhi_resource;

    // vulkan/Vulkan_Shader.cpp RHI_Compile
    private ShaderModule RHI_Compile()
    {
        var defines = new Dictionary<string, string>(m_defines);
        if (m_shader_type == RHI_Shader_Type.Compute)
            defines["SP_SHADER_STAGE_COMPUTE"] = "1";
        if (m_shader_type == RHI_Shader_Type.MeshShader)
            defines["SP_SHADER_STAGE_MESH"] = "1";

        uint[] spirv;
        try
        {
            spirv = Compiler.CompileSource(m_preprocessed_source, ToShaderStage(m_shader_type), Path.GetFileName(m_file_path), defines, GetEntryPoint(), alreadyExpanded: true);
        }
        catch (Exception e) when (e is ShaderCompileException or NotSupportedException)
        {
            LastError = e.Message;
            return default;
        }

        ShaderModule module = CreateModule(spirv);
        if (module.Handle == 0)
            return default;

        // reflect shader resources (so that descriptor sets can be created later)
        m_descriptors = RHI_SpirvReflection.Reflect(spirv, m_shader_type, GetEntryPoint());
        m_spirv       = spirv;

        // create input layout
        m_input_layout?.Create(m_vertex_type);
        return module;
    }

    private ShaderModule CreateModule(ReadOnlySpan<uint> spirv)
    {
        ShaderModule module;
        fixed (uint* code = spirv)
        {
            ShaderModuleCreateInfo info = new()
            {
                SType    = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(spirv.Length * sizeof(uint)),
                PCode    = code
            };
            Result result = RHI_Context.Vk.CreateShaderModule(RHI_Context.Device, in info, null, out module);
            if (result != Result.Success)
            {
                LastError = $"vkCreateShaderModule failed: {result}";
                return default;
            }
        }
        RHI_Device.SetResourceName(module.Handle, RHI_Resource_Type.Shader, ObjectName);
        return module;
    }

    public void Dispose()
    {
        m_task?.Wait();
        if (m_rhi_resource.Handle != 0)
        {
            RHI_Device.DeletionQueueAdd(RHI_Resource_Type.Shader, m_rhi_resource.Handle);
            m_rhi_resource = default;
        }
        m_reload?.Dispose();
    }
}
