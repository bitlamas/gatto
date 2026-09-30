using System.Text.Json;
using Gatto.Core.Tools;
using Gatto.Extensions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;

namespace Gatto.Tests;

//a DLL cached by another build of the host binds the surface that build had, so this build must not load it
[Collection("e2e")]
public sealed class ExtensionStaleCacheTests : IClassFixture<RoslynWarmupFixture>, IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-ext-stale-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    //the result record as the previous build shipped it, three parameters, and just enough api around it for a script to compile
    private const string OldSurface = """
        using System;
        using System.Text.Json;
        using System.Threading;
        using System.Threading.Tasks;
        namespace Gatto.Core.Tools
        {
            public sealed record ToolResult(string Text, bool IsError = false, string Gloss = null);
            public interface IToolContext { }
        }
        namespace Gatto.Extensions
        {
            public sealed class GattoApi
            {
                public void Register(string name, string description, string parametersSchema,
                    Func<JsonElement, Gatto.Core.Tools.IToolContext, CancellationToken, Task<Gatto.Core.Tools.ToolResult>> execute,
                    bool readClass = false) { }
            }
            public sealed class ScriptGlobals { public GattoApi Gatto => null; }
        }
        """;

    private const string Script = """Gatto.Register("stale_tool", "s", "{\"type\":\"object\"}", (a, c, t) => System.Threading.Tasks.Task.FromResult(new Gatto.Core.Tools.ToolResult("ok")));""";

    private static IEnumerable<MetadataReference> Framework() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is var f && (f.StartsWith("System.", StringComparison.Ordinal) || f is "netstandard.dll" or "mscorlib.dll"))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));

    private static byte[] Emit(Compilation c)
    {
        using var ms = new MemoryStream();
        var r = c.Emit(ms);
        Assert.True(r.Success, string.Join("\n", r.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return ms.ToArray();
    }

    //the script compiled against the old surface under this host's own assembly name and version, as the previous build emitted it
    private static byte[] StaleDll()
    {
        var version = typeof(ExtensionHost).Assembly.GetName().Version!.ToString();
        var surface = CSharpCompilation.Create(
            typeof(ExtensionHost).Assembly.GetName().Name,
            new[] { CSharpSyntaxTree.ParseText(OldSurface), CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")]") },
            Framework(),
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var refs = Framework().Append(MetadataReference.CreateFromImage(Emit(surface)));
        var script = CSharpCompilation.CreateScriptCompilation(
            "stale",
            CSharpSyntaxTree.ParseText(Script, CSharpParseOptions.Default.WithKind(SourceCodeKind.Script)),
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, scriptClassName: "Submission#0"),
            returnType: typeof(object),
            globalsType: typeof(ScriptGlobals));
        return Emit(script);
    }

    private sealed class Ctx : IToolContext
    {
        public string Cwd => "C:\\cwd";
        public string HomePath => "C:\\home";
        public IUserPrompter? Prompter => null;
    }

    //the previous build keyed its cache with the same api, Roslyn and assembly versions this build carries
    [Fact]
    public async Task A_dll_cached_by_another_build_of_the_same_version_is_recompiled_and_removed()
    {
        File.WriteAllText(Path.Combine(_dir, "stale.csx"), Script);
        var src = ExtensionDiscovery.Discover(_dir).Single();
        var key = ExtensionDiscovery.ComputeHash(src, GattoApi.HostApiVersion,
            typeof(CSharpScript).Assembly.GetName().Version!.ToString(),
            typeof(ExtensionHost).Assembly.GetName().Version!.ToString())[..12];
        var cache = Directory.CreateDirectory(Path.Combine(_dir, ".cache")).FullName;
        var stale = Path.Combine(cache, $"stale-{key}.dll");
        File.WriteAllBytes(stale, StaleDll());
        var start = ExtensionHost.CompileCount;

        var diags = new List<string>();
        var loaded = ExtensionHost.LoadAll(_dir, ExtensionHostTests.NewApi(out _, out _), diags.Add);
        var tool = loaded.Single().Registrations.Tools.Single().Tool;
        var result = await tool.ExecuteAsync(JsonDocument.Parse("{}").RootElement, new Ctx(), CancellationToken.None);

        Assert.Equal("ok", result.Text);
        Assert.Empty(diags);
        Assert.Equal(start + 1, ExtensionHost.CompileCount);
        Assert.False(File.Exists(stale));
    }
}
