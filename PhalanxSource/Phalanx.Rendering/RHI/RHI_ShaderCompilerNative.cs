// shaderc interop behind Rendering/ShaderCompiler (rule 10: unsafe code stays in Phalanx.Rendering.RHI).
// Spartan's equivalent is RHI_DirectXShaderCompiler.cpp (DXC); Phalanx compiles GLSL with shaderc instead.
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Shaderc;

namespace Phalanx.Rendering.RHI;

internal sealed unsafe class RHI_ShaderCompilerNative : IDisposable
{
    private readonly Shaderc api;
    private readonly Compiler* compiler;
    private readonly object compiler_lock = new(); // a shaderc compiler object is not thread safe
    private bool disposed;

    public RHI_ShaderCompilerNative()
    {
        api = Shaderc.GetApi();
        compiler = api.CompilerInitialize();
        if (compiler == null)
            throw new InvalidOperationException("shaderc_compiler_initialize failed");
    }

    public uint[] Compile(string source, ShaderStage stage, string name, string entryPoint, KeyValuePair<string, string>[] defines, bool optimize, bool debugInfo)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (compiler_lock)
        {
            CompileOptions* options = api.CompileOptionsInitialize();
            CompilationResult* result = null;
            try
            {
                api.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
                api.CompileOptionsSetTargetSpirv(options, SpirvVersion.Shaderc16);
                api.CompileOptionsSetOptimizationLevel(options, optimize ? OptimizationLevel.Performance : OptimizationLevel.Zero);
                // spartan passes -fspv-preserve-bindings to dxc (Vulkan_Shader.cpp:122): unused bindings stay in the module,
                // so reflection (and the descriptor set layouts) do not change with the optimizer's dead-code elimination
                api.CompileOptionsSetPreserveBindings(options, true);
                if (debugInfo)
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
