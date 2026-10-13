using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Phalanx.Generators;

// Pure layout logic (no Roslyn types) so it is easy to reason about and unit test.
// C# port of the former Python generator (tools/gen_shared_buffers.py, removed).
public sealed class GlslField
{
    public string Name = "", Glsl = "", CsType = "", Elem = "", Comment = "";
    public string? Default;
    public int Count, Stride, Offset, Size, Line;
}

public sealed class GlslStruct
{
    public string Name = "", Layout = "";
    public int Size, Align, Line;
    public List<GlslField> Fields = new List<GlslField>();
}

public sealed class LayoutDiagnostic : IEquatable<LayoutDiagnostic>
{
    public string Id = "", Message = "";
    public bool IsError;
    public int Line, Column, Length; // zero based

    public bool Equals(LayoutDiagnostic? o) => o != null && Id == o.Id && Message == o.Message && Line == o.Line && Column == o.Column;
    public override bool Equals(object? obj) => Equals(obj as LayoutDiagnostic);
    public override int GetHashCode() => (Id + Message + Line).GetHashCode();
}

public sealed class LayoutResult
{
    public List<GlslStruct> Structs = new List<GlslStruct>();
    public List<LayoutDiagnostic> Diagnostics = new List<LayoutDiagnostic>();
    public Dictionary<string, (string Elem, int Count, int Stride, int Size)> Arrays = new Dictionary<string, (string, int, int, int)>();
    public string Commit = "unknown", Sha256 = "";
}

public static class SharedBufferLayoutEngine
{
    // glsl type -> (C# type, size, base alignment)
    static readonly Dictionary<string, (string Cs, int Size, int Align)> Types = new Dictionary<string, (string, int, int)>
    {
        ["float"] = ("float", 4, 4), ["uint"] = ("uint", 4, 4), ["int"] = ("int", 4, 4), ["double"] = ("double", 8, 8),
        ["vec2"] = ("Vector2", 8, 8), ["vec3"] = ("Vector3", 12, 16), ["vec4"] = ("Vector4", 16, 16), ["mat4"] = ("Matrix4x4", 64, 16),
    };
    static readonly Dictionary<string, string> ArrayName = new Dictionary<string, string>
    {
        ["float"] = "Float", ["uint"] = "UInt", ["int"] = "Int", ["vec2"] = "Vec2", ["vec3"] = "Vec3", ["vec4"] = "Vec4", ["mat4"] = "Mat4", ["double"] = "Double",
    };

    static readonly Regex StructStart = new Regex(@"^\s*//\s*@layout\s+(std140|std430)\b.*$");
    static readonly Regex StructName  = new Regex(@"^\s*struct\s+(\w+)\s*$");
    static readonly Regex FieldRe     = new Regex(@"^\s*(\w+)\s+(\w+)\s*(?:\[(\d+)\])?\s*;\s*(?://\s*(.*))?$");
    static readonly Regex DefaultRe   = new Regex(@"@default\((.*?)\)(?:\s|$)");
    static readonly Regex HlslOffRe   = new Regex(@"@hlsl-offset\s+\d+\s*");

    static int AlignUp(int v, int a) => (v + a - 1) / a * a;

    public static LayoutResult Process(string text)
    {
        var r = new LayoutResult();
        using (var sha = SHA256.Create())
            r.Sha256 = string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(text)).Select(b => b.ToString("x2")));
        var cm = Regex.Match(text, @"shared_buffers\.h @ (\w+)");
        if (cm.Success) r.Commit = cm.Groups[1].Value;

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var lm = StructStart.Match(lines[i]);
            if (!lm.Success) continue;
            string layout = lm.Groups[1].Value;
            int j = i + 1;
            var nm = j < lines.Length ? StructName.Match(lines[j]) : Match.Empty;
            if (!nm.Success) { Err(r, "PLX001", "'// @layout' must be followed by 'struct Name'", i, lines[i]); continue; }
            var s = new GlslStruct { Name = nm.Groups[1].Value, Layout = layout, Line = j };
            j++;
            if (j >= lines.Length || lines[j].Trim() != "{") { Err(r, "PLX001", $"{s.Name}: expected '{{'", j, j < lines.Length ? lines[j] : ""); continue; }
            int off = 0, maxal = 4;
            for (j++; j < lines.Length && !lines[j].TrimStart().StartsWith("};"); j++)
            {
                string line = lines[j];
                string t = line.Trim();
                if (t.Length == 0 || t.StartsWith("//")) continue;
                var fm = FieldRe.Match(line);
                if (!fm.Success || !Types.TryGetValue(fm.Groups[1].Value, out var ti))
                {
                    Err(r, "PLX001", $"{s.Name}: cannot parse member '{t}' (supported: {string.Join(", ", Types.Keys)}, fixed-size arrays)", j, line);
                    continue;
                }
                string glsl = fm.Groups[1].Value, name = fm.Groups[2].Value, comment = fm.Groups[4].Value;
                int n = fm.Groups[3].Success ? int.Parse(fm.Groups[3].Value, CultureInfo.InvariantCulture) : 0;
                int size = ti.Size, al = ti.Align, stride = 0;
                if (n > 0)
                {
                    stride = AlignUp(size, al);
                    if (layout == "std140") { al = Math.Max(al, 16); stride = AlignUp(stride, 16); }
                }
                if (off % al != 0)
                {
                    int need = AlignUp(off, al);
                    Err(r, "PLX002", $"{s.Name}.{name}: {layout} requires offset {need} but the member is at {off}; add {(need - off) / 4} explicit 'uint _padN;' member(s) before it", j, line);
                    off = need;
                }
                maxal = Math.Max(maxal, al);
                var dm = DefaultRe.Match(comment);
                string cmt = HlslOffRe.Replace(DefaultRe.Replace(comment, ""), "").Trim();
                string csType = ti.Cs;
                if (n > 0)
                {
                    bool padded = stride != size;
                    csType = $"{ArrayName[glsl]}{(padded ? "Std140" : "")}x{n}";
                    r.Arrays[csType] = (ti.Cs, n, stride, size);
                }
                s.Fields.Add(new GlslField
                {
                    Name = name, Glsl = glsl, CsType = csType, Elem = ti.Cs, Count = n, Stride = stride, Offset = off, Size = size, Line = j,
                    Default = dm.Success && n == 0 ? CsDefault(dm.Groups[1].Value, ti.Cs) : null, Comment = cmt,
                });
                off += n > 0 ? stride * n : size;
            }
            int structAl = layout == "std140" ? Math.Max(maxal, 16) : maxal;
            if (off % structAl != 0)
            {
                Err(r, "PLX003", $"{s.Name}: size {off} is not a multiple of its {layout} alignment {structAl}; add {(AlignUp(off, structAl) - off) / 4} explicit tail 'uint _padN;' member(s)", j, j < lines.Length ? lines[j] : "");
                off = AlignUp(off, structAl);
            }
            s.Size = off; s.Align = structAl;
            r.Structs.Add(s);
            i = j;
        }
        if (r.Structs.Count == 0)
            r.Diagnostics.Add(new LayoutDiagnostic { Id = "PLX004", Message = "no '// @layout' structs found in shared_buffers.glsl", IsError = true });
        return r;
    }

    static void Err(LayoutResult r, string id, string msg, int line, string text)
    {
        int col = text.Length - text.TrimStart().Length;
        r.Diagnostics.Add(new LayoutDiagnostic { Id = id, Message = msg, IsError = true, Line = line, Column = col, Length = Math.Max(1, text.Trim().Length) });
    }

    static string CsDefault(string e, string cs)
    {
        e = e.Trim();
        var m = Regex.Match(e, @"^vec([234])\((.*)\)$");
        if (m.Success)
        {
            int n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var parts = m.Groups[2].Value.Split(',').Select(p => p.Trim()).ToList();
            if (parts.Count == 1) parts = Enumerable.Repeat(parts[0], n).ToList();
            return $"new Vector{n}(" + string.Join(", ", parts.Select(p => Regex.IsMatch(p, @"^-?[0-9.]+$") ? p + "f" : p)) + ")";
        }
        if ((cs == "float" || cs == "double") && Regex.IsMatch(e, @"^-?[0-9.]+$")) return e + (cs == "float" ? "f" : "");
        return e;
    }

    public static string EmitStructs(LayoutResult r)
    {
        var sb = new StringBuilder();
        void W(string s) => sb.Append(s).Append('\n');
        W("// <auto-generated>");
        W("// Generated at build time by Phalanx.Generators from shaders/shared_buffers.glsl (source of truth).");
        W($"// Header sha256: {r.Sha256}");
        W($"// Originally translated from SpartanEngine shared_buffers.h @ {r.Commit}.");
        W("// Layout: std140 (uniform) / std430 (storage, push constant) offsets, LayoutKind.Explicit.");
        W("// Matrices: Matrix4x4 bytes are uploaded as-is; GLSL column_major mat4 sees transpose(M), shaders write M * v.");
        W("// </auto-generated>");
        W("#nullable enable");
        W("using System;\nusing System.Numerics;\nusing System.Runtime.CompilerServices;\nusing System.Runtime.InteropServices;\n\nnamespace Phalanx.Rendering;\n");
        W("public static class SharedBuffers\n{");
        W($"    public const string SpartanCommit = \"{r.Commit}\";");
        W($"    public const string HeaderSha256  = \"{r.Sha256}\";\n}}\n");
        foreach (var kv in r.Arrays.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var (elem, n, stride, size) = kv.Value;
            if (stride != size)
            {
                W($"// std140 array element: {elem} padded to a {stride}-byte stride");
                W($"[StructLayout(LayoutKind.Explicit, Size = {stride})]\npublic struct {kv.Key}Element {{ [FieldOffset(0)] public {elem} Value; }}\n");
                W($"[InlineArray({n})]\npublic struct {kv.Key} {{ private {kv.Key}Element _e0; }}\n");
            }
            else W($"[InlineArray({n})]\npublic struct {kv.Key} {{ private {elem} _e0; }}\n");
        }
        foreach (var s in r.Structs)
        {
            W($"// {s.Layout}, {s.Size} bytes");
            W($"[StructLayout(LayoutKind.Explicit, Size = {s.Size})]");
            W($"public partial struct {s.Name}\n{{");
            foreach (var f in s.Fields)
            {
                string d = f.Default != null ? $" = {f.Default};" : ";";
                string c = f.Comment.Length > 0 ? " // " + f.Comment : "";
                W($"    [FieldOffset({f.Offset})] public {f.CsType} {f.Name}{d}{c}");
            }
            if (s.Fields.Any(f => f.Default != null)) W($"\n    public {s.Name}() {{ }}");
            W("}\n");
        }
        // expected layout table, consumed by Phalanx.Tests
        W("public static class SharedBufferLayout\n{");
        W("    public static readonly (Type Type, string Layout, int Size, (string Field, int Offset, int ArrayStride)[] Fields)[] Expected =\n    {");
        foreach (var s in r.Structs)
            W($"        (typeof({s.Name}), \"{s.Layout}\", {s.Size}, new (string, int, int)[] {{ {string.Join(", ", s.Fields.Select(f => $"(\"{f.Name}\", {f.Offset}, {f.Stride})"))} }}),");
        W("    };\n}");
        return sb.ToString();
    }
}
