// Managed SPIR-V reflection, replaces the SPIRV-Cross calls of SpartanEngine vulkan/Vulkan_Shader.cpp @ 4c07baa
// (spirv_resources_to_descriptors + RHI_Shader::Reflect, lines 31-80 and 204-235). Plan §4 option (b): no native
// library, AOT safe, reads only what the descriptor set layouts need (set, binding, type, array size, block size,
// active-ness, name).
//
// Classification follows spirv-cross' ShaderResources:
//   UniformConstant + OpTypeImage sampled=1          -> separate_images          -> Image
//   UniformConstant + OpTypeImage sampled=2          -> storage_images           -> TextureStorage
//   Uniform + Block                                  -> uniform_buffers          -> ConstantBuffer
//   StorageBuffer, or Uniform + BufferBlock          -> storage_buffers          -> StructuredBuffer
//   PushConstant                                     -> push_constant_buffers    -> PushConstantBuffer
//   UniformConstant + OpTypeAccelerationStructureKHR -> acceleration_structures  -> AccelerationStructure
// Phalanx additions (GLSL, spartan never reflects them): separate_samplers -> Sampler, sampled_images -> CombinedImageSampler.
// Texel buffers and input attachments are rejected (spartan has none either).
//
// Struct size is spirv-cross' get_declared_struct_size: offset of the last member + its declared size.
// "used" is spirv-cross' get_active_interface_variables: a variable counts when a function body accesses it (load,
// store, access chain, atomics, image texel pointer, array length, copies, call arguments). The entry point interface
// can't be used: with preserve bindings glslang lists unreferenced bindings there too. Phalanx modules have one entry
// point, so the whole module is scanned (spirv-cross walks the call graph of the selected entry point).
// Order of the output matches spartan's Reflect(): images, storage images, storage buffers, uniform buffers,
// push constants, acceleration structures (then the phalanx-only sampler kinds).
using System.Text;

namespace Phalanx.Rendering.RHI;

internal static class RHI_SpirvReflection
{
    public const uint Magic = 0x07230203;

    // opcodes
    private const int OpName = 5, OpEntryPoint = 15, OpTypeBool = 20, OpTypeInt = 21, OpTypeFloat = 22, OpTypeVector = 23,
                      OpTypeMatrix = 24, OpTypeImage = 25, OpTypeSampler = 26, OpTypeSampledImage = 27, OpTypeArray = 28,
                      OpTypeRuntimeArray = 29, OpTypeStruct = 30, OpTypePointer = 32, OpConstant = 43, OpSpecConstant = 50,
                      OpVariable = 59, OpDecorate = 71, OpMemberDecorate = 72, OpTypeAccelerationStructureKHR = 5341;
    // decorations
    private const uint DecBlock = 2, DecBufferBlock = 3, DecRowMajor = 4, DecArrayStride = 6, DecMatrixStride = 7,
                       DecBinding = 33, DecDescriptorSet = 34, DecOffset = 35;
    // storage classes
    private const uint ScUniformConstant = 0, ScUniform = 2, ScPushConstant = 9, ScStorageBuffer = 12;
    private const uint DimBuffer = 5, DimSubpassData = 6;

    internal enum Kind { Other, Bool, Int, Float, Vector, Matrix, Image, Sampler, SampledImage, Array, RuntimeArray, Struct, Pointer, AccelerationStructure }

    internal sealed class TypeInfo
    {
        public Kind kind;
        public uint width;          // int/float
        public uint element;        // vector/matrix/array/runtime array/pointer pointee
        public uint count;          // vector components, matrix columns
        public uint length_id;      // array length constant
        public uint storage_class;  // pointer
        public uint image_dim, image_sampled;
        public uint[] members = Array.Empty<uint>();
    }

    public readonly record struct EntryPoint(uint ExecutionModel, string Name, uint[] Interface);

    public sealed class Module
    {
        public uint Version;
        public List<EntryPoint> EntryPoints = new();
        internal readonly Dictionary<uint, TypeInfo> types = new();
        internal readonly Dictionary<uint, ulong> constants = new();
        internal readonly Dictionary<uint, string> names = new();
        internal readonly Dictionary<(uint id, uint dec), uint> decorations = new();          // value or 1 for flags
        internal readonly Dictionary<(uint id, uint member, uint dec), uint> member_decorations = new();
        internal readonly List<(uint id, uint type, uint storage_class)> variables = new();
        internal readonly HashSet<uint> referenced = new(); // ids used as a pointer operand inside function bodies
    }

    private static void CollectPointerOperands(int op, ReadOnlySpan<uint> ins, HashSet<uint> referenced)
    {
        void Add(int index, ReadOnlySpan<uint> i) { if (index < i.Length) referenced.Add(i[index]); }
        switch (op)
        {
            case 61: case 65: case 66: case 67: case 60: case 68: case 83: // load, access chains, texel pointer, array length, copy object
                Add(3, ins); break;
            case 62: Add(1, ins); break;                                    // store
            case 63: case 64: Add(1, ins); Add(2, ins); break;              // copy memory (sized)
            case 57: for (int k = 4; k < ins.Length; k++) Add(k, ins); break; // function call arguments
            case 228: Add(1, ins); break;                                   // atomic store
            case >= 227 and <= 242: Add(3, ins); break;                     // other atomics
            case 169: Add(4, ins); Add(5, ins); break;                      // select between pointers
        }
    }

    public static Module Parse(ReadOnlySpan<uint> words)
    {
        if (words.Length < 5 || words[0] != Magic)
            throw new ArgumentException("not a SPIR-V module (bad magic or too short)");
        var m = new Module { Version = words[1] };
        bool in_function = false;
        int i = 5;
        while (i < words.Length)
        {
            uint w = words[i];
            int count = (int)(w >> 16), op = (int)(w & 0xFFFF);
            if (count == 0 || i + count > words.Length)
                throw new ArgumentException($"malformed SPIR-V instruction at word {i}");
            ReadOnlySpan<uint> ins = words.Slice(i, count);
            if (op == 54) in_function = true;       // OpFunction
            else if (op == 56) in_function = false; // OpFunctionEnd
            else if (in_function) CollectPointerOperands(op, ins, m.referenced);
            switch (op)
            {
                case OpName:
                    m.names[ins[1]] = ReadString(ins[2..]);
                    break;
                case OpEntryPoint:
                {
                    string name = ReadString(ins[3..], out int name_words);
                    m.EntryPoints.Add(new EntryPoint(ins[1], name, ins[(3 + name_words)..].ToArray()));
                    break;
                }
                case OpDecorate:
                    m.decorations[(ins[1], ins[2])] = count > 3 ? ins[3] : 1u;
                    break;
                case OpMemberDecorate:
                    m.member_decorations[(ins[1], ins[2], ins[3])] = count > 4 ? ins[4] : 1u;
                    break;
                case OpTypeBool:   m.types[ins[1]] = new TypeInfo { kind = Kind.Bool, width = 32 }; break;
                case OpTypeInt:    m.types[ins[1]] = new TypeInfo { kind = Kind.Int, width = ins[2] }; break;
                case OpTypeFloat:  m.types[ins[1]] = new TypeInfo { kind = Kind.Float, width = ins[2] }; break;
                case OpTypeVector: m.types[ins[1]] = new TypeInfo { kind = Kind.Vector, element = ins[2], count = ins[3] }; break;
                case OpTypeMatrix: m.types[ins[1]] = new TypeInfo { kind = Kind.Matrix, element = ins[2], count = ins[3] }; break;
                case OpTypeImage:  m.types[ins[1]] = new TypeInfo { kind = Kind.Image, element = ins[2], image_dim = ins[3], image_sampled = ins[7] }; break;
                case OpTypeSampler: m.types[ins[1]] = new TypeInfo { kind = Kind.Sampler }; break;
                case OpTypeSampledImage: m.types[ins[1]] = new TypeInfo { kind = Kind.SampledImage, element = ins[2] }; break;
                case OpTypeArray:  m.types[ins[1]] = new TypeInfo { kind = Kind.Array, element = ins[2], length_id = ins[3] }; break;
                case OpTypeRuntimeArray: m.types[ins[1]] = new TypeInfo { kind = Kind.RuntimeArray, element = ins[2] }; break;
                case OpTypeStruct: m.types[ins[1]] = new TypeInfo { kind = Kind.Struct, members = ins[2..].ToArray() }; break;
                case OpTypePointer: m.types[ins[1]] = new TypeInfo { kind = Kind.Pointer, storage_class = ins[2], element = ins[3] }; break;
                case OpTypeAccelerationStructureKHR: m.types[ins[1]] = new TypeInfo { kind = Kind.AccelerationStructure }; break;
                case OpConstant:
                case OpSpecConstant: // the default value, like spirv-cross
                    if (count >= 4)
                        m.constants[ins[2]] = count >= 5 ? ins[3] | ((ulong)ins[4] << 32) : ins[3];
                    break;
                case OpVariable:
                    m.variables.Add((ins[2], ins[1], ins[3]));
                    break;
            }
            i += count;
        }
        return m;
    }

    /// <summary>Reflects the descriptors of one entry point (null = the first one), like spartan's RHI_Shader::Reflect.</summary>
    public static List<RHI_Descriptor> Reflect(ReadOnlySpan<uint> spirv, RHI_Shader_Type shader_stage, string? entry_point = null)
    {
        Module m = Parse(spirv);

        // spartan selects the entry point by name (ray tracing libraries carry several)
        if (entry_point != null && m.EntryPoints.Count > 0 && !m.EntryPoints.Any(e => e.Name == entry_point))
            throw new ArgumentException($"the module has no entry point named \"{entry_point}\"");
        HashSet<uint> active = m.referenced;

        var buckets = new List<RHI_Descriptor>[8];
        for (int b = 0; b < buckets.Length; b++) buckets[b] = new();

        foreach (var (id, type_id, storage_class) in m.variables)
        {
            if (storage_class is not (ScUniformConstant or ScUniform or ScPushConstant or ScStorageBuffer))
                continue;
            TypeInfo ptr = m.types[type_id];
            uint base_id = ptr.element;

            // unwrap arrays (spartan: type.array[0], phalanx multiplies arrays of arrays, which is vulkan's descriptorCount)
            bool is_array = false, runtime = false;
            uint array_length = 1;
            while (m.types[base_id].kind is Kind.Array or Kind.RuntimeArray)
            {
                TypeInfo a = m.types[base_id];
                is_array = true;
                if (a.kind == Kind.RuntimeArray)
                    runtime = true;
                else
                    array_length *= (uint)m.constants[a.length_id];
                base_id = a.element;
            }
            TypeInfo base_type = m.types[base_id];

            RHI_Descriptor_Type type;
            int bucket;
            switch (storage_class)
            {
                case ScPushConstant: type = RHI_Descriptor_Type.PushConstantBuffer; bucket = 4; break;
                case ScStorageBuffer: type = RHI_Descriptor_Type.StructuredBuffer; bucket = 2; break;
                case ScUniform:
                    if (m.decorations.ContainsKey((base_id, DecBufferBlock))) { type = RHI_Descriptor_Type.StructuredBuffer; bucket = 2; }
                    else { type = RHI_Descriptor_Type.ConstantBuffer; bucket = 3; }
                    break;
                default: // UniformConstant
                    switch (base_type.kind)
                    {
                        case Kind.Image:
                            if (base_type.image_dim == DimBuffer)
                                throw new NotSupportedException($"{VarName(m, id, base_id)}: texel buffers are not supported (spartan has none)");
                            if (base_type.image_dim == DimSubpassData)
                                throw new NotSupportedException($"{VarName(m, id, base_id)}: input attachments are not supported");
                            if (base_type.image_sampled == 2) { type = RHI_Descriptor_Type.TextureStorage; bucket = 1; }
                            else { type = RHI_Descriptor_Type.Image; bucket = 0; }
                            break;
                        case Kind.Sampler:               type = RHI_Descriptor_Type.Sampler; bucket = 6; break;
                        case Kind.SampledImage:          type = RHI_Descriptor_Type.CombinedImageSampler; bucket = 7; break;
                        case Kind.AccelerationStructure: type = RHI_Descriptor_Type.AccelerationStructure; bucket = 5; break;
                        default: continue;
                    }
                    break;
            }

            // this only matters for textures
            RHI_Image_Layout layout = type is RHI_Descriptor_Type.TextureStorage or RHI_Descriptor_Type.Image or RHI_Descriptor_Type.CombinedImageSampler
                ? RHI_Image_Layout.General : RHI_Image_Layout.Max;

            uint size = 0;
            if (type is RHI_Descriptor_Type.ConstantBuffer or RHI_Descriptor_Type.PushConstantBuffer)
                size = (uint)DeclaredStructSize(m, base_id);

            uint length = !is_array ? 0 : runtime ? RHI_Definitions.rhi_max_array_size : array_length;
            if (is_array && length == 0)
                length = RHI_Definitions.rhi_max_array_size; // spartan: a zero length array is unbounded

            buckets[bucket].Add(new RHI_Descriptor(
                VarName(m, id, base_id),
                type,
                layout,
                m.decorations.TryGetValue((id, DecBinding), out uint binding) ? binding : 0,
                RHI_Definitions.rhi_shader_type_to_mask(shader_stage),
                size,
                is_array,
                length,
                m.decorations.TryGetValue((id, DecDescriptorSet), out uint set) ? set : 0,
                active.Contains(id)));
        }

        var result = new List<RHI_Descriptor>();
        foreach (var b in buckets) result.AddRange(b);
        return result;
    }

    // spirv-cross get_declared_struct_size
    private static ulong DeclaredStructSize(Module m, uint struct_id)
    {
        TypeInfo t = m.types[struct_id];
        if (t.kind != Kind.Struct || t.members.Length == 0)
            return 0;
        uint last = (uint)(t.members.Length - 1);
        ulong offset = m.member_decorations.TryGetValue((struct_id, last, DecOffset), out uint o) ? o : 0;
        return offset + DeclaredMemberSize(m, struct_id, last);
    }

    // spirv-cross get_declared_struct_member_size
    private static ulong DeclaredMemberSize(Module m, uint struct_id, uint member)
    {
        uint type_id = m.types[struct_id].members[member];
        TypeInfo t = m.types[type_id];
        switch (t.kind)
        {
            case Kind.RuntimeArray:
                return 0;
            case Kind.Array:
            {
                ulong stride = m.decorations.TryGetValue((type_id, DecArrayStride), out uint s) ? s : 0;
                ulong length = 1;
                uint id = type_id;
                while (m.types[id].kind == Kind.Array) { length *= m.constants[m.types[id].length_id]; id = m.types[id].element; }
                return stride * length;
            }
            case Kind.Struct:
                return DeclaredStructSize(m, type_id);
            case Kind.Matrix:
            {
                ulong matrix_stride = m.member_decorations.TryGetValue((struct_id, member, DecMatrixStride), out uint ms) ? ms : 0;
                bool row_major = m.member_decorations.ContainsKey((struct_id, member, DecRowMajor));
                uint rows = m.types[t.element].count;
                return matrix_stride * (row_major ? rows : t.count);
            }
            case Kind.Vector:
                return m.types[t.element].width / 8 * t.count;
            case Kind.Int or Kind.Float or Kind.Bool:
                return t.width / 8;
            default:
                return 0;
        }
    }

    // spirv-cross Resource::name: the variable name, falling back to the block type name for nameless blocks
    private static string VarName(Module m, uint var_id, uint base_id) =>
        m.names.TryGetValue(var_id, out string? n) && n.Length > 0 ? n : m.names.TryGetValue(base_id, out string? tn) ? tn : $"_{var_id}";

    /// <summary>
    /// Removes the source-level debug instructions (OpSource*, OpString, OpLine/OpNoLine, OpModuleProcessed) and keeps
    /// OpName/OpMemberName. Modules with NonSemantic debug info (which reference OpString) are returned unchanged.
    /// </summary>
    public static uint[] StripSourceDebugInfo(uint[] words)
    {
        const int OpSourceContinued = 2, OpSource = 3, OpSourceExtension = 4, OpString = 7, OpLine = 8, OpExtInstImport = 11,
                  OpNoLine = 317, OpModuleProcessed = 330;
        if (words.Length < 5 || words[0] != Magic)
            throw new ArgumentException("not a SPIR-V module");
        var output = new List<uint>(words.Length);
        output.AddRange(words.AsSpan(0, 5).ToArray());
        int i = 5;
        while (i < words.Length)
        {
            int count = (int)(words[i] >> 16), op = (int)(words[i] & 0xFFFF);
            if (count == 0 || i + count > words.Length)
                throw new ArgumentException($"malformed SPIR-V instruction at word {i}");
            if (op == OpExtInstImport && ReadString(words.AsSpan(i + 2, count - 2)).StartsWith("NonSemantic.", StringComparison.Ordinal))
                return words;
            if (op is not (OpSourceContinued or OpSource or OpSourceExtension or OpString or OpLine or OpNoLine or OpModuleProcessed))
                output.AddRange(words.AsSpan(i, count).ToArray());
            i += count;
        }
        return output.ToArray();
    }

    private static string ReadString(ReadOnlySpan<uint> words) => ReadString(words, out _);

    private static string ReadString(ReadOnlySpan<uint> words, out int word_count)
    {
        var bytes = new List<byte>();
        for (int i = 0; i < words.Length; i++)
            for (int b = 0; b < 4; b++)
            {
                byte c = (byte)(words[i] >> (8 * b));
                if (c == 0) { word_count = i + 1; return Encoding.UTF8.GetString(bytes.ToArray()); }
                bytes.Add(c);
            }
        word_count = words.Length;
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
