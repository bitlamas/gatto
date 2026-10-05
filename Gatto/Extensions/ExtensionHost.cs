using System.Net;
using System.Reflection;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Core.Web;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Gatto.Extensions;

//one extension that compiled and ran, with the files it loaded from and the tools and hooks staged for the host to commit
public sealed record LoadedExtension(string Name, ExtensionSource Source, StagedRegistrations Registrations);

//load a cached DLL when the key matches, otherwise compile and run it. stage its tools only after it runs clean, a failure drops that extension alone
public static class ExtensionHost
{
    //test seam, only a cache miss bumps it, so a re-run that recompiles nothing leaves it alone
    public static int CompileCount;

    //test seam, it stands in for the host API version in the cache hash, null uses the real one
    public static string? HostApiVersionOverride;

    private const string CacheDirName = ".cache";

    private static string EffectiveHostApiVersion => HostApiVersionOverride ?? GattoApi.HostApiVersion;

    private static string RoslynVersion =>
        typeof(CSharpScript).Assembly.GetName().Version?.ToString() ?? "0";

    //the host's version and module id both go in the cache key, since two builds of one version can hold a different surface
    private static string HostBinaryVersion =>
        (typeof(ExtensionHost).Assembly.GetName().Version?.ToString() ?? "0") + "\0" + typeof(ExtensionHost).Assembly.ManifestModule.ModuleVersionId.ToString("N");

    private static readonly string[] Imports =
    {
        "System", "System.Collections.Generic", "System.Linq", "System.Text.Json",
        "System.Threading", "System.Threading.Tasks",
        "Gatto.Core.Tools", "Gatto.Core.Loop", "Gatto.Core.Web", "Gatto.Core.Client",
    };

    //public API

    //the api graph exactly as launch builds it, one ledger shared by the fetch recorder and the api
    public static (GattoApi Api, CitationLedger Ledger, GuardedFetch Fetch) BuildApi(
        string home, string cwd, Func<string?> ledgerPath, Func<IUserPrompter?> prompter, Action<string> log,
        HttpMessageHandler? handler = null, Func<string, Task<IPAddress[]>>? resolver = null,
        Func<string, System.Text.Json.JsonElement?>? configSection = null,
        IReadOnlyCollection<string>? allowedOrigins = null)
    {
        var ledger = new CitationLedger(ledgerPath);
        var fetch = new GuardedFetch(recorder: ledger.Record, handler: handler, resolver: resolver, allowedOrigins: allowedOrigins);
        var api = new GattoApi(home, cwd, fetch.FetchAsync, ledger.Record, prompter, log, configSection);
        return (api, ledger, fetch);
    }

    //load every extension in discovery order, report each failure and keep the ones that loaded
    public static IReadOnlyList<LoadedExtension> LoadAll(string extensionsDir, GattoApi api, Action<string> diagnostic)
    {
        var loaded = new List<LoadedExtension>();
        var sources = ExtensionDiscovery.Discover(extensionsDir, diagnostic);
        if (sources.Count == 0) return loaded;

        var extRoot = Path.GetFullPath(extensionsDir);
        var cacheDir = Path.Combine(extRoot, CacheDirName);

        foreach (var src in sources)
        {
            try
            {
                var runner = ResolveRunner(src, extRoot, cacheDir, diagnostic);
                if (runner is null) continue;   //a null runner means a compile error, already reported

                api.BeginExtension(src.Name);
                runner(new ScriptGlobals(api)).GetAwaiter().GetResult();   //the script stages its tools onto the api as it runs
                loaded.Add(new LoadedExtension(src.Name, src, api.TakeStaged()));
            }
            catch (Exception ex)
            {
                //any failure while a script runs drops this one extension, and the next BeginExtension re-arms a clean stage
                diagnostic($"{src.EntryPath}: extension '{src.Name}' failed to load: {Unwrap(ex).Message}");
            }
        }

        return loaded;
    }

    //decide which loaded extensions stay before any endpoint is resolved, so a launch never builds its client on an extension that is skipped later. a clash skips the whole extension
    public static IReadOnlyList<LoadedExtension> Survivors(
        IReadOnlyList<LoadedExtension> loaded, IEnumerable<string> reservedTools, IEnumerable<string> reservedEndpoints,
        Action<string> diagnostic)
    {
        var tools = new HashSet<string>(reservedTools, StringComparer.Ordinal);
        var endpoints = new HashSet<string>(reservedEndpoints, StringComparer.Ordinal) { "local" };
        var kept = new List<LoadedExtension>();
        foreach (var ext in loaded)
        {
            var staged = ext.Registrations;
            var within = new HashSet<string>(StringComparer.Ordinal);
            string? clash = null;
            foreach (var (tool, _) in staged.Tools)
            {
                if (tools.Contains(tool.Name)) { clash = $"tool '{tool.Name}' collides with an already-registered tool"; break; }
                if (!within.Add(tool.Name)) { clash = $"tool '{tool.Name}' is registered twice by this extension"; break; }
            }
            if (clash is null)
                foreach (var (name, _) in staged.Endpoints)
                    if (endpoints.Contains(name)) { clash = $"endpoint '{name}' collides with an endpoint that already exists"; break; }
            if (clash is not null)
            {
                diagnostic($"extension '{ext.Name}': {clash}, extension skipped");
                continue;
            }
            foreach (var (tool, _) in staged.Tools) tools.Add(tool.Name);
            foreach (var (name, _) in staged.Endpoints) endpoints.Add(name);
            kept.Add(ext);
        }
        return kept;
    }

    //compile only, for doctor, and return one line per error, an empty list means everything compiles
    public static IReadOnlyList<string> CompileCheck(string extensionsDir)
    {
        var failures = new List<string>();
        var sources = ExtensionDiscovery.Discover(extensionsDir);
        if (sources.Count == 0) return failures;

        var extRoot = Path.GetFullPath(extensionsDir);
        foreach (var src in sources)
        {
            try
            {
                var script = CreateScript(src, extRoot);
                foreach (var d in script.Compile())
                    if (d.Severity == DiagnosticSeverity.Error)
                        failures.Add(FormatDiagnostic(src, d));
            }
            catch (Exception ex)
            {
                failures.Add($"{src.EntryPath}: {Unwrap(ex).Message}");
            }
        }
        return failures;
    }

    //a name collision skips the whole extension, and the policy line belongs to a committed extension even with no tools
    public static (IReadOnlyList<(LoadedExtension Extension, string Name, bool ReadClass)> Tools,
                   IReadOnlyList<LoadedExtension> Extensions) Commit(
        IReadOnlyList<LoadedExtension> loaded, ToolRegistry tools, HookBus hooks, Action<string> diagnostic)
    {
        var committed = new List<(LoadedExtension, string, bool)>();
        var committedExtensions = new List<LoadedExtension>();
        foreach (var ext in loaded)
        {
            var staged = ext.Registrations;

            var within = new HashSet<string>(StringComparer.Ordinal);
            string? clash = null;
            foreach (var (tool, _) in staged.Tools)
            {
                if (tools.Get(tool.Name) is not null)
                {
                    clash = $"tool '{tool.Name}' collides with an already-registered tool";
                    break;
                }
                if (!within.Add(tool.Name))
                {
                    clash = $"tool '{tool.Name}' is registered twice by this extension";
                    break;
                }
            }
            if (clash is not null)
            {
                diagnostic($"extension '{ext.Name}': {clash} — extension skipped");
                continue;
            }

            //add the extension after the clash check, a skipped one must not put its policy line in the prompt
            committedExtensions.Add(ext);

            foreach (var (tool, readClass) in staged.Tools)
            {
                tools.Register(tool, extension: true);   //the name was checked for a duplicate already, and validated when it was staged
                committed.Add((ext, tool.Name, readClass));
            }
            foreach (var (evt, handler) in staged.Hooks)
                hooks.On(MapEvent(evt), handler);   //tool_result maps to the observe-only hook event, OnToolResult is never wired
        }
        return (committed, committedExtensions);
    }

    //grant a read-class tool only when its own extension is vetted, so a skipped extension cannot bless a same-named one
    public static void GrantVettedReadClass(
        IReadOnlyList<(LoadedExtension Extension, string Name, bool ReadClass)> committed,
        Func<ExtensionSource, bool> isVetted, Action<string> allowReadClass)
    {
        foreach (var (owner, name, readClass) in committed)
            if (readClass && isVetted(owner.Source))
                allowReadClass(name);
    }

    //internals

    //a cache hit runs the compiled DLL, a miss compiles and emits one, falling back to memory when the emit fails
    private static Func<ScriptGlobals, Task>? ResolveRunner(
        ExtensionSource src, string extRoot, string cacheDir, Action<string> diagnostic)
    {
        var hash12 = ExtensionDiscovery.ComputeHash(src, EffectiveHostApiVersion, RoslynVersion, HostBinaryVersion)[..12];
        var dllPath = Path.Combine(cacheDir, $"{src.Name}-{hash12}.dll");

        //a structurally bad cached DLL falls through to a recompile, a script throw happens later and LoadAll catches it
        if (File.Exists(dllPath) && TryFactoryRunner(dllPath, out var hitRunner))
            return hitRunner;

        var script = CreateScript(src, extRoot);
        Interlocked.Increment(ref CompileCount);

        var errors = script.Compile().Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
        {
            foreach (var d in errors) diagnostic(FormatDiagnostic(src, d));
            return null;
        }

        if (TryEmit(script, cacheDir, dllPath, src.Name) && TryFactoryRunner(dllPath, out var freshRunner))
            return freshRunner;

        //the emit failed, so run in memory this time and cache nothing
        return g => script.RunAsync(g);
    }

    private static Script<object> CreateScript(ExtensionSource src, string extRoot)
    {
        var options = ScriptOptions.Default
            .WithReferences(typeof(GattoApi).Assembly, typeof(System.Text.Json.JsonElement).Assembly)
            .WithImports(Imports)
            .WithFilePath(src.EntryPath);

        var folder = Path.GetDirectoryName(src.EntryPath)!;
        //a folder extension gets the confined resolver, so a load of a sibling works and an escape does not. a single top-level file gets no resolver
        var isFolder = !string.Equals(Path.GetFullPath(folder), extRoot, StringComparison.OrdinalIgnoreCase);
        if (isFolder)
            options = options.WithSourceResolver(new ConfinedSourceResolver(folder));

        var code = File.ReadAllText(src.EntryPath);
        return CSharpScript.Create(code, options, typeof(ScriptGlobals));
    }

    private static bool TryFactoryRunner(string dllPath, out Func<ScriptGlobals, Task> runner)
    {
        runner = null!;
        try
        {
            var asm = Assembly.LoadFrom(dllPath);
            var type = asm.GetType("Submission#0");
            var factory = type?.GetMethod("<Factory>",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (factory is null) return false;

            runner = g =>
            {
                //the array is globals then the previous submission state, null on the first run
                var result = factory.Invoke(null, new object[] { new object?[] { g, null } });
                return (Task)result!;
            };
            return true;
        }
        catch
        {
            return false;   //a cached DLL that would not load, so the caller recompiles
        }
    }

    private static bool TryEmit(Script<object> script, string cacheDir, string dllPath, string name)
    {
        var tmp = dllPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(cacheDir);
            var compilation = script.GetCompilation();
            using (var fs = File.Create(tmp))
            {
                var emit = compilation.Emit(fs);
                if (!emit.Success) { fs.Dispose(); TryDelete(tmp); return false; }
            }
            File.Move(tmp, dllPath, overwrite: true);
            DeleteStale(cacheDir, name, keep: Path.GetFileName(dllPath));
            return true;
        }
        catch
        {
            TryDelete(tmp);
            return false;   //the write was locked or failed, so the caller runs it in memory
        }
    }

    private static void DeleteStale(string cacheDir, string name, string keep)
    {
        try
        {
            foreach (var dll in Directory.EnumerateFiles(cacheDir, $"{name}-*.dll"))
                if (!string.Equals(Path.GetFileName(dll), keep, StringComparison.OrdinalIgnoreCase))
                    TryDelete(dll);   //a locked or in-use stale dll is left alone
        }
        catch { }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static string FormatDiagnostic(ExtensionSource src, Diagnostic d)
    {
        var span = d.Location.GetLineSpan();
        var path = string.IsNullOrEmpty(span.Path) ? src.EntryPath : span.Path;
        var line = span.StartLinePosition.Line + 1;
        return $"{path}:{line}: {d.GetMessage()}";
    }

    private static HookEvent MapEvent(string evt) => evt switch
    {
        "tool_call" => HookEvent.ToolCall,
        "tool_result" => HookEvent.ToolResult,
        "message_end" => HookEvent.MessageEnd,
        "session_summary" => HookEvent.SessionSummary,
        _ => throw new InvalidOperationException($"unknown hook event '{evt}'"),
    };

    private static Exception Unwrap(Exception ex) =>
        ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
}
