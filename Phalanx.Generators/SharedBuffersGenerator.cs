using System.Collections.Immutable;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Phalanx.Generators;

[Generator(LanguageNames.CSharp)]
public sealed class SharedBuffersGenerator : IIncrementalGenerator
{
    static DiagnosticDescriptor Descriptor(string id, string title) =>
        new DiagnosticDescriptor(id, title, "{0}", "Phalanx.SharedBuffers", DiagnosticSeverity.Error, isEnabledByDefault: true);

    static readonly ImmutableDictionary<string, DiagnosticDescriptor> Descriptors = new[]
    {
        Descriptor("PLX001", "shared_buffers.glsl parse error"),
        Descriptor("PLX002", "missing explicit padding"),
        Descriptor("PLX003", "missing explicit tail padding"),
        Descriptor("PLX004", "no shared structs"),
    }.ToImmutableDictionary(d => d.Id);

    static readonly DiagnosticDescriptor Missing = new DiagnosticDescriptor("PLX005", "shared_buffers.glsl not found",
        "shaders/shared_buffers.glsl is not an AdditionalFiles item; no shared GPU structs were generated", "Phalanx.SharedBuffers",
        DiagnosticSeverity.Warning, isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // incremental: only re-runs when the header text changes
        var headers = context.AdditionalTextsProvider
            .Where(f => Path.GetFileName(f.Path).Equals("shared_buffers.glsl", System.StringComparison.OrdinalIgnoreCase))
            .Select((f, ct) => (Path: f.Path, Text: f.GetText(ct)?.ToString() ?? ""))
            .Select((h, _) => (h.Path, Result: SharedBufferLayoutEngine.Process(h.Text)))
            .Select((h, _) => new GeneratorOutput(h.Path, SharedBufferLayoutEngine.EmitStructs(h.Result), h.Result.Diagnostics.ToImmutableArray()));

        context.RegisterSourceOutput(headers.Collect(), (spc, outputs) =>
        {
            if (outputs.Length == 0) { spc.ReportDiagnostic(Diagnostic.Create(Missing, Location.None)); return; }
            var o = outputs[0];
            foreach (var d in o.Diagnostics)
            {
                var span = new LinePositionSpan(new LinePosition(d.Line, d.Column), new LinePosition(d.Line, d.Column + d.Length));
                var loc = Location.Create(o.Path, new TextSpan(0, 0), span);
                spc.ReportDiagnostic(Diagnostic.Create(Descriptors[d.Id], loc, d.Message));
            }
            spc.AddSource("SharedBuffers.g.cs", SourceText.From(o.Source, System.Text.Encoding.UTF8));
        });
    }

    sealed class GeneratorOutput : System.IEquatable<GeneratorOutput>
    {
        public readonly string Path, Source;
        public readonly ImmutableArray<LayoutDiagnostic> Diagnostics;
        public GeneratorOutput(string path, string source, ImmutableArray<LayoutDiagnostic> diagnostics) { Path = path; Source = source; Diagnostics = diagnostics; }
        public bool Equals(GeneratorOutput? o) => o != null && Path == o.Path && Source == o.Source && Diagnostics.SequenceEqual(o.Diagnostics);
        public override bool Equals(object? obj) => Equals(obj as GeneratorOutput);
        public override int GetHashCode() => Source.GetHashCode();
    }
}
