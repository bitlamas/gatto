using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gatto.Cli.Diagnose;
using Gatto.Cli.Install;
using Gatto.Cli.Launch;
using Gatto.Cli.Setup;
using Gatto.Cli.Update;
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Session;
using Gatto.Core.Web;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Memory;
using Gatto.Core.Tools;
using Gatto.Extensions;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Prompts;
using Gatto.Repl.Render.Chrome;
using Gatto.Roles;
using Gatto.Roles.Agents;
using Gatto.Roles.Models;
using Gatto.Roles.Serve;
using Gatto.Terminal;

namespace Gatto.Cli;

file sealed class AppToolContext(string cwd, string home, IUserPrompter? prompter, SessionStore sessions,
    Gatto.Core.Sandbox.SandboxState? sandbox) : IToolContext
{
    public string Cwd => cwd;
    public string HomePath => home;
    public IUserPrompter? Prompter => prompter;
    public SessionIdentity? Session => sessions.Identity;
    public IReadOnlyList<string> SessionFiles => new[] { sessions.CurrentPath, sessions.ResumedFrom }.OfType<string>().ToList();
    public Gatto.Core.Sandbox.SandboxState? Sandbox => sandbox;

    //null at construction, and the rich face alone sets it when it starts, so -p and the plain face run the flag in the foreground
    public BackgroundTasks? Tasks { get; set; }
}

file sealed class OneShotObserver : ITurnObserver
{
    public void OnTextDelta(string t) => Console.Write(t);
    public void OnReasoningDelta(string t) { }
    public void OnToolCallStart(ToolCall c) => Console.Error.WriteLine($"[tool] {c.Name}");
    public void OnToolResult(ToolCall c, ToolResult r) { }
    public void OnWarning(string m) => Console.Error.WriteLine($"! {m}");
    public void OnUsage(Usage u) { }
}

//a -p run compacts like a session, and its summary streams as reasoning so stdout keeps model text
file sealed class OneShotCompaction(
    IChatClient client, string model, AgentLoop loop, Conversation convo, int? window, ContextUsageState usage,
    SessionStore sessions, Func<ComposedSystem> recompose, string dot) : ICompactionHandler
{
    public string? LastFailure { get; private set; }

    public Task<CompactionResult?> CompactAsync(
        CompactionReason reason, string currentUserPrompt, ITurnObserver observer, CancellationToken ct) =>
        CompactAsync(reason, currentUserPrompt, observer, 0, ct);

    public Task<CompactionResult?> CompactAsync(
        CompactionReason reason, string currentUserPrompt, ITurnObserver observer, long traceParent, CancellationToken ct) =>
        CompactAsync(reason, Compactor.PromptOnly(currentUserPrompt, convo), observer, traceParent, ct);

    public async Task<CompactionResult?> CompactAsync(
        CompactionReason reason, TurnSoFar turn, ITurnObserver observer, long traceParent, CancellationToken ct)
    {
        var compactor = new Compactor(client, model, loop.RequestShape, loop.Trace);
        var summary = await compactor.SummarizeAsync(convo, observer, ct, windowTokens: window, ratio: usage.Ratio, midTurn: true,
            reason: reason.ToString(), traceParent: traceParent);
        LastFailure = compactor.LastFailure;
        if (summary is null) return null;
        var (rebuilt, _) = Compactor.RebuildMidTurn(summary, convo, turn, observer, sessions, recompose, dot);
        return rebuilt;
    }
}

public static class GattoApp
{
    //what bare /model says on a cloud endpoint that lists no models, where a typed name is the whole switch
    internal static string CloudSwitchHint(string endpointName) =>
        $"endpoint {endpointName} lists no models: type /model <name> to switch to any model it serves";

    //an extension's section comes from the extensions object first, so it needs no new top-level key and search keeps working
    internal static Func<string, JsonElement?> SectionReader(JsonElement? raw) => name =>
        raw is not { } r ? null
        : r.TryGetProperty("extensions", out var ext) && ext.ValueKind == JsonValueKind.Object && ext.TryGetProperty(name, out var inner) ? inner
        : r.TryGetProperty(name, out var top) ? top
        : null;

    //the help text is the first thing a stranger reads, so it uses the user's words rather than harness. internal so a guard drives the string that ships, since a guard over a copy is an oracle about the copy
    internal const string Help = """
        gatto, a terminal assistant that runs AI models on your own computer
        usage: gatto [role|command] [options]
          roles:    {roles}
          commands: setup | serve start [model] [--detach] | serve stop|status | doctor | status | audition | model new [gguf] [id] | update | uninstall
          options:  -m <model>  -p <prompt>  -e <endpoint>  --effort <level>  --continue[ <id>]  --yes  --sandbox  --version  -h
        """;

    //the roles line is read from the home at print time, so the help never names a default the config overrode or a role that is not on disk
    internal static string HelpFor(string home)
    {
        var roles = RoleFile.ListNames(Path.Combine(home, "roles"));
        string? configured = null;
        try { configured = GattoConfig.Load(home).DefaultRole; }
        catch (GattoConfigException) { }   //a home with no config still gets its help, and the launch reports the config error itself
        return HelpText(roles, configured ?? "generalist");
    }

    //every role on disk, the default first and marked, and a home with no roles folder yet reads the four shipped names
    internal static string HelpText(IReadOnlyList<string> roles, string defaultRole)
    {
        var names = roles.Count > 0 ? roles : new[] { "generalist", "coder", "oracle", "raw" };
        var line = string.Join(" | ", names.OrderBy(r => r == defaultRole ? 0 : 1).Select(r => r == defaultRole ? r + " (default)" : r));
        if (roles.Count == 0) line += " | <any ~\\.gatto\\roles\\*.json>";
        return Help.Replace("{roles}", line, StringComparison.Ordinal);
    }

    //the null ctx is the production path, and only the outermost return holds a fatal exit open so it can be read
    public static async Task<int> RunAsync(string[] argv) => FatalPause.Hold(await RunAsync(argv, null));

    //a test's launch probe deadline, carried in its own async flow so a parallel test never sees it, and null in production
    private static readonly AsyncLocal<TimeSpan?> ProbeDeadlineOverride = new();

    //gives a fake server the room the suite's load can take, for as long as the returned scope lives
    internal static IDisposable ProbeDeadlineScope(TimeSpan deadline)
    {
        ProbeDeadlineOverride.Value = deadline;
        return new ProbeDeadlineReset();
    }

    private sealed class ProbeDeadlineReset : IDisposable
    {
        public void Dispose() => ProbeDeadlineOverride.Value = null;
    }

    //a test's own NuGet and temp folders for the sandbox, carried like the probe deadline, so a launch under test never reads or unplants the machine's
    private static readonly AsyncLocal<(string NuGet, string HttpCache, string TempParent)?> SandboxFoldersOverride = new();

    internal static IDisposable SandboxFoldersScope(string nuget, string httpCache, string tempParent)
    {
        SandboxFoldersOverride.Value = (nuget, httpCache, tempParent);
        return new SandboxFoldersReset();
    }

    //the folders a launch takes when no scope names its own, null in production. a test run sets them so the suite never touches the machine's
    internal static (string NuGet, string HttpCache, string TempParent)? SandboxFoldersDefault { get; set; }

    private sealed class SandboxFoldersReset : IDisposable
    {
        public void Dispose() => SandboxFoldersOverride.Value = null;
    }

    //a test's trace switch, carried in its own async flow like the probe deadline, so a parallel test never writes a trace
    private static readonly AsyncLocal<bool> TraceOverride = new();

    internal static IDisposable TraceScope()
    {
        TraceOverride.Value = true;
        return new TraceReset();
    }

    private sealed class TraceReset : IDisposable
    {
        public void Dispose() => TraceOverride.Value = false;
    }

    //internal, so the harness's ctx never joins the public surface, and no default argument, which would make one-argument calls ambiguous
    internal static async Task<int> RunAsync(string[] argv, CommandContext? ctx)
    {
        var launchedAt = Stopwatch.GetTimestamp();   //the launch span's start, read before any other work
        //first, before anything spawns a child, so a missing DLL kills the process instead of pinning it behind a dialog nobody sees
        ProcessErrorMode.FailFastOnMissingDll();

        //this dispatch stays before the config load, or a broken config silences the child. the word comes from HardwareProbe.ChildWord, which ArgRouter.Reserved reads.
        if (argv.Length == 1 && argv[0] == Gatto.Core.Hardware.HardwareProbe.ChildWord)
        {
            //the child must encode utf-8 here too, since this returns before RunInnerAsync pins Console.OutputEncoding and the parent decodes utf-8
            try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (Exception) { }

            //write through ctx?.Out so the harness owns the writer, and keep the Windows test on this line, answering with the protocol's error line elsewhere
            (ctx?.Out ?? Console.Out).Write(
                ctx?.ProbeMachine is { } fake ? fake()
                : OperatingSystem.IsWindows() ? Gatto.Cli.Hardware.MachineProbeCommand.Report()
                : "error_probe=the hardware child reads Windows interfaces only\n");
            return 0;
        }

        //one file check per launch, silent and best-effort, since the leftover unlocks when the old process exits and the next launch clears it
        SelfInstall.SweepOld(Path.GetDirectoryName(Environment.ProcessPath) ?? "");

        //a killed download leaves a partial behind, so this sweep runs on every launch and never creates the folder
        UpdateDownload.Sweep(ctx?.Home ?? GattoHome.Resolve());

        try
        {
            return await RunInnerAsync(argv, ctx, launchedAt);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                Environment.GetEnvironmentVariable("GATTO_DEBUG") == "1" ? ex.ToString() : ex.Message);
            return 1;
        }
    }

    //the label half of a wizard answer, and null when the ask registers no control keys
    internal static string? Label(Gatto.Repl.Prompts.WizardAnswer? a) => a?.Label;

    //the ask a quant command uses, or null when the session cannot ask, in its own method so the conditional has a return type
    private static Func<Gatto.Repl.Prompts.WizardAsk, bool>? Confirming(Gatto.Core.Tools.IUserPrompter? prompter) =>
        prompter is Gatto.Repl.Prompts.IWizardPrompter asker
            ? ask => SwapConfirm.Confirmed(Label(asker.AskOne(ask)))
            : null;

    private static async Task<int> RunInnerAsync(string[] argv, CommandContext? ctx = null, long launchedAt = 0)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (Exception) { }  //a legacy host may refuse this, and it is never fatal

        ParsedArgs args;
        try { args = ArgRouter.Parse(argv); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(HelpFor(ctx?.Home ?? GattoHome.Resolve()));
            return 2;
        }

        if (args.Command == "help") { Console.WriteLine(HelpFor(ctx?.Home ?? GattoHome.Resolve())); return 0; }
        //the version line takes the context's stamp, so a golden can pin it without going red on every commit
        if (args.Command == "version")
        {
            var v = ctx?.Stamp ?? VersionStamp.Running;
            Console.WriteLine(CommandBanner.VersionLine(v.Version, v.Build, v.Dev));
            return 0;
        }
        if (args.Command == "serve") return await ServeCommand.RunServeAsync(args, ctx);
        if (args.Command == "doctor") return await DoctorCommand.RunDoctorAsync(ctx);
        if (args.Command == "status") return await StatusCommand.RunStatusAsync(ctx);
        if (args.Command == "model") return await SetupCommand.RunModelAsync(args, ctx);
        if (args.Command == "audition") return await AuditionCommand.RunAuditionAsync(args);
        if (args.Command == "uninstall") return UninstallCommand.RunUninstall(ctx);
        if (args.Command == "update")
            return await UpdateCommand.RunAsync(ctx ?? CommandContext.Production(), CancellationToken.None);
        //the wizard falls through to the launch path instead of returning, so the setup screen's start option really starts gatto
        var launchAfterSetup = false;
        if (args.Command == "setup")
        {
            var (setupExit, thenLaunch) = SetupCommand.RunSetup();
            if (!thenLaunch) return setupExit;
            launchAfterSetup = true;
        }
        if (args.Command != "launch" && !launchAfterSetup)
        {
            Console.Error.WriteLine($"{args.Command}: coming in a later plan");
            return 2;
        }

        //one source of truth for the home, so a ctx and this path cannot disagree, and no Production() call when ctx is null
        var home = ctx?.Home ?? GattoHome.Resolve();

        //the glyph set is read once per run, so every painter in this run agrees on the vocabulary
        var glyphs = CommandBanner.GlyphsFor(home);

        //the question is whether the home has no models and no default model, asked before anything can scaffold it. a piped or -p run never sees it.
        if (launchAfterSetup is false && args.Prompt is null
            && (ctx?.Interactive ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected))
            && FirstRunDoor.NotConfigured(home)
            && SelfInstall.Probe(SelfInstall.DefaultDir(), Environment.ProcessPath,
                   File.Exists(SelfInstall.ExeIn(SelfInstall.DefaultDir())), dirOnPath: false,
                   SelfInstall.IsFrameworkDependent(Environment.ProcessPath))
               != InstallState.Installed)
        {
            //the context is built here rather than beside home, so Chrome's terminal query happens only on the branch that paints
            var door = (ctx ?? CommandContext.Production()).Surface();
            door.Say("gatto isn't set up on this machine yet.");
            door.Blank();
            var (offerExit, offerLaunch) = SetupCommand.RunSetup();
            if (!offerLaunch) return offerExit;
            launchAfterSetup = true;
        }

        using var launch = new SessionLaunch(args, home, glyphs, launchedAt);
        return await launch.RunAsync();
    }

    //the launch of a session, one step per stage with what a later stage reads held in a field, so no stage reaches into another through a captured local
    private sealed class SessionLaunch(ParsedArgs args, string home, GlyphSet glyphs, long launchedAt) : IDisposable
    {
        private GattoConfig config = null!;
        private long homeTicks, shippedTicks, probeTicks;
        private string rolesDir = "", modelsDir = "", cwd = "", launchRole = "";
        private string? continueById, goneFolder, sessionModel, continuePath, resumeLine, resumedFrom;
        private bool folderGone, memoryOn, serverAlreadyUp, servedInSession;
        private List<(string Extension, string Line)> policyLines = [];
        private List<string> launchWarnings = [];
        private Gatto.Core.WarningSink warn = null!;
        private IUserPrompter? prompter;
        private SessionStore sessions = null!;
        private Gatto.Core.Trace trace = null!;
        private Gatto.Core.Span launchSpan;
        private Action<string> extDiag = null!;
        private Gatto.Core.Web.CitationLedger ledger = null!;
        private IReadOnlyList<LoadedExtension> loadedExtensions = [];
        private Dictionary<string, EndpointConfig> endpoints = null!;
        private Dictionary<string, string> endpointOwner = null!;
        private LaunchState state = null!;
        private RoleResolution launchRes = null!;
        private PiggybackSeam? piggyback;
        private ToolRegistry tools = null!;
        private HookBus hooks = null!;
        private ThemeMode themeMode = ThemeMode.Dark;
        private Theme? launchTheme;
        private IPermissionPrompter? permPrompter;
        private IWizardPrompter? launchAsker;
        private InputPump? pump;
        private InputDeafnessWatchdog? deafWatch;
        private ChromeHandle? chrome;
        private TurnAbortHandle? turnAbort;
        private IListPicker replPicker = null!;
        private TimeSpan probeDeadline;
        private HttpClient probeHttp = null!;
        private long? servedBytes;
        private (string, CliInk)[]? servedLine;
        private Gatto.Core.Sandbox.SandboxState? sandbox;
        private string? sandboxLine, residueLine;
        private IToolContext toolCtx = null!;
        private PermissionStore permissions = null!;
        private WildState wildState = null!;
        private PermissionGate gate = null!;
        private OpenAiCompatClient client = null!;
        private UsageMeter usageMeter = null!;
        private Gatto.Core.Commands.CommandRegistry commands = null!;
        private IReadOnlyList<(LoadedExtension Extension, string Name, bool ReadClass)> committedExt = [];
        private Action<string>?[] subagentProgress = [];
        private AgentLoop loop = null!;
        private Conversation convo = null!;

        //the steps in the order the launch has always run them, each refusal an exit code and every other step falling through to the next
        public async Task<int> RunAsync()
        {
            if (LoadHome() is { } homeExit) return homeExit;
            Begin();
            if (RefuseEmptyContinue() is { } continueExit) return continueExit;
            LoadExtensions();
            if (ResolveLaunch() is { } roleExit) return roleExit;
            RegisterTools();
            BuildPrompters();
            if (await ProbeLaunchAsync() is { } probeExit) return probeExit;
            await StartSandboxAsync();
            BuildClient();
            await CheckWindowAsync();
            await ServeAsync();
            if (CommitExtensions() is { } commitExit) return commitExit;
            BuildLoop();
            Resume();
            return args.Prompt is { } prompt ? await RunOneShotAsync(prompt) : await RunReplAsync();
        }

        //the sandbox lives as long as the launch, so its labels come off when the session ends
        public void Dispose()
        {
            if (OperatingSystem.IsWindows()) sandbox?.Dispose();
        }

        private int? LoadHome()
        {
            //config resolution runs before anything is created, so a launch that dies on a config error leaves the home untouched
            var homeFrom = Stopwatch.GetTimestamp();
            try { config = GattoConfig.Load(home); }
            catch (GattoConfigException ex) { Console.Error.WriteLine(ex.Message); return 2; }

            GattoHome.EnsureInitialized(home);
            homeTicks = Stopwatch.GetTimestamp() - homeFrom;
            rolesDir = Path.Combine(home, "roles");
            modelsDir = Path.Combine(home, "models");
            //shipped files are written before anything reads them, a user edit survives, and a write failure warns rather than aborting the launch
            var shippedFrom = Stopwatch.GetTimestamp();
            try { ShippedExtensions.EnsureWritten(home); }
            catch (Exception ex) { Console.Error.WriteLine($"! extension: could not write shipped files: {ex.Message}"); }
            shippedTicks = Stopwatch.GetTimestamp() - shippedFrom;

            //an id names the session's folder, so the process moves there first and every read below takes that folder
            continueById = null;
            //the folder is gone, and goneFolder names it, or null when the record holds no folder at all
            folderGone = false;
            goneFolder = null;
            if (args.Continue && args.ContinueId is { } earlyId)
            {
                try { continueById = SessionStore.ResolvePathById(home, earlyId); }
                catch (GattoConfigException ex) { Console.Error.WriteLine(ex.Message); return 2; }
                var storedFolder = SessionStore.CwdOf(continueById);
                if (storedFolder is not null && Directory.Exists(storedFolder)) Environment.CurrentDirectory = storedFolder;
                else { folderGone = true; goneFolder = storedFolder; }
            }

            cwd = Environment.CurrentDirectory;
            return null;
        }

        //a bare --continue with nothing to resume is a usage error, a fresh session in its place would read as the resume the user asked for
        private int? RefuseEmptyContinue()
        {
            if (!args.Continue || continueById is not null || sessions.LatestPathForCwd() is not null) return null;
            Console.Error.WriteLine("--continue: no saved session in this folder");
            return 2;
        }

        private void Begin()
        {
            //project memory: resolved once here from the home setting and every .gatto.json ruling, which can only remove it
            memoryOn = ProjectFileConfig.EffectiveMemoryEnabled(cwd, config.MemoryEnabled);
            //the session's -m, seeded at launch and moved by every switch, so ResolveRole reads it rather than the stale args.Model
            sessionModel = args.Model;

            //one list, filled at the extension commit below and read through this reference by every composition the launch state makes
            policyLines = new List<(string Extension, string Line)>();

            //the lines said after the banner, the sink's own only when the alt screen will cover what stderr printed
            launchWarnings = new List<string>();
            var carryWarnings = args.Prompt is null && Gatto.Repl.Repl.RichFace(TermCaps.Detect(), config.AltScreen);
            //stderr until the rich loop rebinds it to a ♯ system line, so a mid-session diagnostic never corrupts the alt-screen frame
            warn = new Gatto.Core.WarningSink(LaunchSink(launchWarnings, carryWarnings));
            //null until the prompters are built below, so an extension that asks at load time gets the no-interactive-input error
            prompter = null;
            sessions = new SessionStore(home, cwd);
            //read once here like the wire log, and handed down, so nothing below reads the environment for it
            trace = TraceOverride.Value || Environment.GetEnvironmentVariable("GATTO_TRACE") == "1"
                ? new Gatto.Core.Trace(launchedAt, DateTimeOffset.Now - Stopwatch.GetElapsedTime(launchedAt),
                    () => sessions.CurrentPath is { } tp ? SessionFiles.Sidecar(tp, SessionFiles.Trace) : null,
                    () => sessions.Identity.IdText,
                    GattoVersion.Build is { Length: > 0 } sha ? $"{GattoVersion.String}+{sha}" : GattoVersion.String,
                    Environment.ProcessId)
                : Gatto.Core.Trace.Off;
            sessions.Trace = trace;
            launchSpan = trace.OpenFrom(Gatto.Core.Trace.Launch, launchedAt);
            probeTicks = 0;
        }

        private void LoadExtensions()
        {
            //loaded before the role resolves, since an extension can contribute the endpoint the launch names. a throwing script fails that extension alone
            var extensionsDir = Path.Combine(home, "extensions");
            extDiag = m => warn.Warn($"extension: {m}");
            //the configured searxng origin is the only SSRF exemption config can grant, exactly that scheme, host and port
            var allowedOrigins = config.Search.SearxngUrl is { } sxUrl
                ? new[] { UrlGuard.Origin(new Uri(sxUrl)) }
                : null;
            (var extApi, ledger, _) = ExtensionHost.BuildApi(   //scripts get the guarded fetch and this session's citation ledger, which stays in memory until the session file exists
                home, cwd,
                ledgerPath: () => sessions.CurrentPath is { } sp ? SessionFiles.Sidecar(sp, SessionFiles.Ledger) : null,
                prompter: () => prompter,
                log: extDiag,
                configSection: SectionReader(config.Raw),
                allowedOrigins: allowedOrigins);
            //every name a built-in registers below, so an extension that would collide is skipped here and never lends the launch its endpoint
            var reservedTools = new List<string>
            {
                new ReadFileTool().Name, new WriteFileTool().Name, new EditFileTool().Name,
                new GlobTool().Name, new GrepTool().Name, new ShellTool().Name,
            };
            if (memoryOn) reservedTools.AddRange(new[] { "memory_write", "recall_memory" });
            loadedExtensions = ExtensionHost.Survivors(
                ExtensionHost.LoadAll(extensionsDir, extApi, extDiag), reservedTools, config.Endpoints.Keys, extDiag);
            //the table every resolution reads: the endpoints of gatto.json plus the ones the surviving extensions contributed
            endpoints = new Dictionary<string, EndpointConfig>(config.Endpoints, StringComparer.Ordinal);
            endpointOwner = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var ext in loadedExtensions)
                foreach (var (epName, epConfig) in ext.Registrations.Endpoints)
                {
                    endpoints[epName] = epConfig;
                    endpointOwner[epName] = ext.Name;
                }
        }

        private int? ResolveLaunch()
        {
            //a continue with no -e goes back to the endpoint the session was recorded on, and on a cloud endpoint with no -m to its model. a record with no endpoint is from before the field and changes nothing
            var launchEndpoint = args.Endpoint;
            if (args.Continue && (continueById ?? sessions.LatestPathForCwd()) is { } resumePath
                && SessionStore.LoadBaseline(resumePath) is { Endpoint: { } recordedOn } recorded)
            {
                if (!endpoints.ContainsKey(recordedOn))
                {
                    Console.Error.WriteLine($"this session was recorded on endpoint '{recordedOn}', which no longer exists. Endpoints: {string.Join(", ", endpoints.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
                    return 2;
                }
                launchEndpoint ??= recordedOn;
                if (launchEndpoint == recordedOn && recordedOn != "local") sessionModel ??= recorded.Model;
            }

            //one instance holds what the launch resolves and every switch moves, so no switch reads a copy another switch left behind
            state = new LaunchState(args, config, home, cwd, rolesDir, modelsDir, endpoints, launchEndpoint, sessionModel, warn, memoryOn, policyLines, glyphs);

            var picked = LaunchRole.Resolve(args.Role, config.DefaultRole, RoleFile.ListNames(rolesDir), name =>
            {
                try { RoleFile.Load(rolesDir, name); return null; }
                catch (GattoConfigException ex) { return ex; }
            });
            if (picked.Refusal is { } refusal) { Console.Error.WriteLine(refusal); return 2; }
            launchRole = picked.Role;
            string? brokenName = picked.BrokenName, brokenMessage = picked.BrokenMessage;
            try { launchRes = state.ResolveRole(launchRole); }
            catch (GattoConfigException ex)
            {
                //a fallback that fails too leaves nothing to run, so both messages go out
                if (brokenMessage is not null) Console.Error.WriteLine(brokenMessage);
                Console.Error.WriteLine(ex.Message);
                return 2;
            }
            if (brokenMessage is not null) warn.Warn(brokenMessage + $", running {launchRole} instead");
            //read here and at each /role, so a file added mid-session reaches run_agent at the next /role
            state.RoleFiles = state.LoadRoleFiles(brokenName);
            //a session continues on the endpoint it was recorded on, checked before anything starts. a record with no endpoint is from before the field and gets no check
            if (args.Continue && (continueById ?? sessions.LatestPathForCwd()) is { } recordedPath
                && SessionStore.LoadBaseline(recordedPath)?.Endpoint is { } recordedEndpoint
                && recordedEndpoint != launchRes.EndpointName)
            {
                Console.Error.WriteLine(endpoints.ContainsKey(recordedEndpoint)
                    ? $"this session was recorded on endpoint '{recordedEndpoint}' and -e names '{launchRes.EndpointName}'. Leave -e out, or give -e {recordedEndpoint}, to continue it"
                    : $"this session was recorded on endpoint '{recordedEndpoint}', which no longer exists. Endpoints: {string.Join(", ", endpoints.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
                return 2;
            }

            Adopt(launchRes);
            //both halves of memory consent in one nullable, so with memory off nothing banks and nothing reads
            piggyback = memoryOn
                ? new(Bank: cands => MemoryPiggyback.BankCandidates(MemoryDir.FindProjectRoot(cwd), cands),
                      ComposeIndex: () => MemoryIndex.Compose(MemoryDir.FindProjectRoot(cwd), state.MemoryBudget(state.Model)))   //read at call time, since /role and /model reassign model and the budget follows it
                : null;

            //seeded with the launch reading, so the first reload after the banner does not repeat the over-budget line
            if (launchRes.MemoryTruncatedLines > 0)
                state.MemoryNag.ShouldWarn(state.MemoryBudget(state.Model), launchRes.MemoryTruncatedLines);
            //the base URL the client below is built for, /role refuses any switch that resolves to another one
            state.LaunchBaseUrl = state.Endpoint.BaseUrl;
            //a cloud endpoint is no llama-server, so nothing below sends it a /props probe
            state.Cloud = CloudEndpoint.Is(state.Endpoint);
            return null;
        }

        //run for the launch resolution and again when the launch arms the model a server already holds
        private void Adopt(RoleResolution r)
        {
            //set from the launch resolution only, so a refused /role and a later /effort cannot change what shell children read
            Environment.SetEnvironmentVariable("GATTO_ENDPOINT", r.EndpointName);
            Environment.SetEnvironmentVariable("GATTO_MODEL", r.ModelString);
            Environment.SetEnvironmentVariable("GATTO_EFFORT", EffortSwitch.Name(r.Comp.Thinking));
            state.Role = r.Role;
            state.Model = r.Model;
            state.Endpoint = r.Endpoint;
            state.EndpointName = r.EndpointName;
            state.ModelString = r.ModelString;
            state.Comp = r.Comp;
            //the prefix RecomposeSystem falls back to when a mid-session .gatto.json edit is malformed, so it holds the last one that composed
            state.LastComposedSystem = state.Comp.SystemText;
            state.LastComposedSources = state.Comp.Sources;
        }

        private void RegisterTools()
        {
            tools = state.Tools;
            tools.Register(new ReadFileTool());
            tools.Register(new WriteFileTool());
            tools.Register(new EditFileTool());
            tools.Register(new GlobTool());
            tools.Register(new GrepTool());
            tools.Register(new ShellTool());
            state.ArmSeatTools(state.Role);
            //the memory tools go on the main registry only, and subagents build their own list without them
            if (memoryOn)
            {
                tools.Register(new MemoryWriteTool(() => state.MemoryBudget(state.Model)));   //a closure over model, so after a /model the advisory and the prefix load read the same budget
                tools.Register(new RecallMemoryTool());
            }
        }

        private void BuildPrompters()
        {
            hooks = new HookBus { Trace = trace };
            hooks.OnHandlerError += (evt, ex) => warn.Warn($"hook error ({evt}): {ex.Message}");
            //the theme is resolved on the interactive REPL path only, so a -p run stays byte-pure and never sends the background query
            themeMode = ThemeMode.Dark;
            //hoisted so the launch notices render through the themed layer, and the prompters share it so the background probe runs once
            launchTheme = null;

            //the launch asker must be pump-less, the pump's reader thread does not exist until Repl.RunRichAsync starts it
            launchAsker = null;
            pump = null;
            deafWatch = null;
            //one late-bound bridge from the prompters to the chrome, built only on the rich path and armed once the loop owns the screen
            chrome = null;
            //armed by the rich loop and a no-op until then, the plain path has no Esc key to wire it to
            turnAbort = null;
            //built alongside the prompters so the rich picker shares their surface, pump and chrome, and the -p branch assigns it only for definite assignment
            if (args.Prompt is not null)
            {
                //the -p flag nulls them even when a console is attached
                permPrompter = null;   //a -p run nulls both prompters even with a TTY attached, so ask_user and an unapproved tool end in an error instead of a hang
                prompter = null;
                replPicker = new PlainListPicker(Console.Out, glyphs);
            }
            else
            {
                var rich = Gatto.Repl.Repl.IsRichTerminal();   //the test Repl.RunAsync uses, so the permission prompt matches the turn output around it
                themeMode = Theme.ResolveMode(config.Theme, rich, TermCaps.QueryBackgroundColor);
                if (rich)
                {
                    //with mouse capture on the pump reads the native console source, with it off the plain key source
                    var mouseEnabled = config.Mouse && config.AltScreen;
                    IInputSource source = mouseEnabled
                        ? new ConsoleInputSource(new Win32ConsoleInputReader())
                        : new KeyInputSource(new ConsoleKeySource());
                    pump = new InputPump(source);
                    deafWatch = InputDeafnessWatchdog.For(source, Console.IsInputRedirected, () => new Win32ConsoleInputWriter());
                    chrome = new ChromeHandle();
                    turnAbort = new TurnAbortHandle();
                    var promptSurface = new ConsoleSurface();
                    var theme = new Theme(TermCaps.Detect(), themeMode);
                    if (state.Role.ParsedTint() is { } launchTint) theme.SetRoleTint(state.Role.Name, launchTint.Dark, launchTint.Light);   //the prompters paint with this theme, told before their first row
                    launchTheme = theme;
                    //the first provider in the configured chain, named raw so the title matches the gloss on the result row
                    permPrompter = new RichPermissionPrompter(
                        promptSurface, theme, new ConsoleKeySource(), pump, chrome, config.DenyReason,
                        webSearchProvider: config.Search.Providers.FirstOrDefault(),
                        abort: turnAbort, warn: warn, glyphs: glyphs);
                    prompter = new RichPrompter(
                        promptSurface, theme, new ConsoleKeySource(), pump, chrome, abort: turnAbort,
                        glyphs: glyphs);
                    //no pump here, nothing else is reading the console at that moment
                    launchAsker = new RichPrompter(
                        promptSurface, theme, new ConsoleKeySource(), pump: null, chrome, abort: turnAbort,
                        glyphs: glyphs);
                    //the picker's fail-safe arm must be audible, and on the rich path the ♯ row replaces a stderr write that would corrupt the frame
                    replPicker = new RichListPicker(promptSurface, theme, new ConsoleKeySource(), pump, chrome, warn,
                        glyphs);
                }
                else
                {
                    permPrompter = new PlainPermissionPrompter(config.DenyReason);
                    prompter = Console.IsInputRedirected ? null : new ConsolePrompter();
                    //the plain prompter is already pump-less, so the launch ask reuses it
                    launchAsker = prompter as IWizardPrompter;
                    replPicker = new PlainListPicker(Console.Out, glyphs);
                }
            }
        }

        private async Task<int?> ProbeLaunchAsync()
        {
            //each probe holds a 2 second deadline of its own unless a test scoped a longer one
            probeDeadline = ProbeDeadlineOverride.Value ?? TimeSpan.FromSeconds(2);
            //a separate client for the probes, untimed like every client, its connect bounded by the same deadline
            probeHttp = new HttpClient(new SocketsHttpHandler { ConnectTimeout = probeDeadline })
                { Timeout = Timeout.InfiniteTimeSpan };

            //the launch probe, a bare launch arms the shelf model the server holds, before the client, the window and the thinking are built from the default
            serverAlreadyUp = false;
            servedBytes = null;
            servedLine = null;
            servedInSession = false;
            if (state.ServedHere() && state.Model is not null && state.LaunchBaseUrl is not null)   //a one-shot probes too, since it is one bounded read and never a start
            {
                //a loading server of ours answers nothing, so wait for it on a live line first, or the launch asks for a second start. -p keeps stdout for the model and reads it as unanswered
                if (args.Prompt is null && Uri.TryCreate(state.LaunchBaseUrl, UriKind.Absolute, out var waitUri))
                {
                    using var loadingLine = new TickLine(Console.Out, launchTheme is not null, blankBeforeFirst: true);
                    await AutoServeAsk.AwaitOursLoadingAsync(
                        new ServeManager(home, ServeCommand.ServerBinaryFor(state.Model, config)), waitUri.Port,
                        Gatto.Roles.Audition.AuditionRunner.ReadyBudget,
                        LoadingLine.Over(loadingLine, glyphs, launchTheme), id => SizeWordsOnDiskOrNull(home, id),
                        CancellationToken.None);
                }
                var loadedFrom = Stopwatch.GetTimestamp();
                var launchLoaded = await ServeProbe.ProbeAsync(probeHttp, state.LaunchBaseUrl, CancellationToken.None, state.Endpoint.ApiKey, probeDeadline);   //the chat client's own key, since /props sits behind the server's api key and a keyed server would read as unknown
                probeTicks += Stopwatch.GetTimestamp() - loadedFrom;
                //computed once, so the refusal and the arm cannot end up describing two different servers
                var launchMismatch = ModelSwitch.DescribeProbe(state.Model, launchLoaded, modelsDir);
                state.LaunchServing = new ServingProbe(launchLoaded?.NCtx, launchLoaded?.Capability ?? ThinkCapability.None, launchLoaded?.Vision);

                //an explicit -m against a server holding another model refuses, and -m X attaches to X
                if (args.Model is not null && launchMismatch is not null)   //the mismatch comes from DescribeProbe, where unknown never counts as one
                {
                    Console.Error.WriteLine(ServeNotice.Refusing(launchLoaded?.ModelPath));
                    return 1;
                }

                if (launchLoaded is not null)
                {
                    serverAlreadyUp = true;
                    servedBytes = Gatto.Core.Acquire.ModelDiscovery.SetBytesOrNull(launchLoaded.ModelPath);   //the exit hint's size, read off the served file
                    var port = new Uri(state.LaunchBaseUrl).Port;
                    var defaultId = state.Model.Id;
                    var unnamed = Model.MatchesLoaded(state.Model, launchLoaded) is null;
                    //an unclaimed file or a model on another port has no profile to arm here, so the default stays and the line says what the server holds
                    servedLine = unnamed ? ServeNotice.UsingNoFile(defaultId, port)
                        : launchMismatch is not { } held ? ServeNotice.Using(defaultId, port)
                        : !held.OnTheShelf ? ServeNotice.UsingOffShelf(defaultId, port, held.Name)
                        : state.ResolveServed(launchRole, held.Name) is { } servedRes ? Arm(servedRes, port)
                        : ServeNotice.UsingOtherPort(defaultId, port, held.Name);
                    //the launch screen goes unread once a server is up, so anything other than the default being served is said inside the session
                    servedInSession = unnamed || launchMismatch is not null;
                }
            }
            return null;
        }

        //the claimed model's resolution replaces the default's before anything is built from it
        private (string, CliInk)[] Arm(RoleResolution res, int port)
        {
            Adopt(res);
            return ServeNotice.Using(res.ModelString, port);
        }

        //the sandbox is one per process: the command line, then the home, then a .gatto.json up the path, and none of them turns it off
        private async Task StartSandboxAsync()
        {
            if (!OperatingSystem.IsWindows()) return;
            var sandboxFolders = SandboxFoldersOverride.Value ?? SandboxFoldersDefault;
            sandbox = new Gatto.Core.Sandbox.SandboxState(cwd, home, config.SandboxGrants ?? [], sandboxFolders?.NuGet, sandboxFolders?.HttpCache, sandboxFolders?.TempParent);
            //every launch that opens a session looks for labels a killed gatto left, so nothing is built on a labelled tree whether the mode is on or not
            using var sandboxTick = args.Prompt is null ? new TickLine(Console.Out, launchTheme is not null, blankBeforeFirst: true) : null;
            var sandboxSource = SandboxSource(args.Sandbox, config.Sandbox, cwd);
            if (await PrepareSandboxAsync(sandbox, sandboxSource, sandboxTick, glyphs, launchTheme) is { } residue) residueLine = TermText.Sanitize(residue);
            if (sandboxSource is not null) sandboxLine = TermText.Sanitize(sandbox.Line);
        }

        private void BuildClient()
        {
            toolCtx = new AppToolContext(cwd, home, prompter, sessions, sandbox);
            permissions = PermissionStore.Load(home, cwd, out var permWarning);   //a corrupt permissions file loads as no grants, so every mutating tool prompts, with a warning
            if (permWarning is not null) Console.Error.WriteLine($"! {permWarning}");

            //one shared switch that auto-allows every permission, seeded from the folder's grant or gatto.json's wild unless a .gatto.json up the path forbids it
            var wildForbiddenBy = ProjectFileConfig.EffectiveWildAllowed(cwd, out var forbiddingFile) ? null : forbiddingFile;
            wildState = new WildState { On = (permissions.Wild || config.Wild) && wildForbiddenBy is null, ForbiddenBy = wildForbiddenBy };

            gate = new PermissionGate(permissions, permPrompter, autoYes: args.Yes, wild: wildState, home: home);
            hooks.On(HookEvent.ToolCall, gate.CheckAsync);
            //the wire log is read once here from GATTO_DEBUG_WIRE and never in the request path, so off costs a null field
            var wireLog = WireLog.FromFlag(
                Environment.GetEnvironmentVariable("GATTO_DEBUG_WIRE"),
                WireLog.RunDirectory(home, DateTimeOffset.UtcNow, Environment.ProcessId));
            client = new OpenAiCompatClient(
                new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })   //only the connect is bounded, so a black-holed host fails fast while a long local generation runs on
                    { Timeout = System.Threading.Timeout.InfiniteTimeSpan },
                state.EndpointName, state.Endpoint, wireLog,
                //ask for progress only from a local llama-server with a known base URL
                returnProgress: state.EndpointName == "local" && state.LaunchBaseUrl is not null,
                trace: trace);
            //a dropped attached image must warn the user, the sink paints a ♯ row in the REPL and stderr for -p
            client.OnImageUnavailable = m => warn.Warn(m);
            //every request on this client feeds the footer's cost and quota, run_agent children included since they share it
            usageMeter = new UsageMeter(state.Endpoint.Quota);
            client.OnUsage = u => _ = usageMeter.Record(u);
            //the model's window on a local endpoint, the endpoint's on a cloud one, and no enforcement when neither is configured
            state.ContextBudget = ServeCommand.ServingFor(state.ServedHere(), state.Model, state.Endpoint, state.LaunchBaseUrl).Window;
        }

        private async Task CheckWindowAsync()
        {
            //cross-check /props at every launch and let the live answer win in both directions (it does not run on the endpoint gatto serves)
            if (!state.ServedHere() && state.LaunchBaseUrl is not null && !state.Cloud)
            {
                var probeFrom = Stopwatch.GetTimestamp();
                var probedCtx = await ServeProbe.ProbeAsync(probeHttp, state.LaunchBaseUrl, CancellationToken.None, state.Endpoint.ApiKey, probeDeadline);
                probeTicks += Stopwatch.GetTimestamp() - probeFrom;
                var resolved = ConnectContext.Resolve(state.ContextBudget, probedCtx?.NCtx);
                state.ContextBudget = resolved.Budget;
                //warn with the line Resolve returned, so the budget and the message cannot disagree
                if (resolved.Line is string ctxLine) warn.Warn(ctxLine);
            }
            //interactive only, and an unconsented run stops at the caller before the fetch
            if (args.Prompt is null)
            {
                var cachedUpdate = UpdateCheck.ReadCache(home);
                if (UpdateCheck.DueForRepl(config.UpdateCheck, cachedUpdate, DateTimeOffset.Now))
                {
                    //1s to connect on the client and 3s for the whole exchange on the check's own token, so a dead host cannot stall the check
                    using var updateHttp = UpdateCheck.Client(TimeSpan.FromSeconds(1));
                    using var updateRead = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    //the line is built from the cached state, this path does not download an update
                    if (await UpdateCheck.FetchAsync(updateHttp, DateTimeOffset.Now, updateRead.Token) is { State: var fresh })
                    {
                        UpdateCheck.WriteCache(home, fresh);
                        cachedUpdate = fresh;
                    }
                }
                //computed from the same install read gatto update makes, so the advice is something the user can act on
                if (UpdateCheck.Line(cachedUpdate, Gatto.Core.GattoVersion.String, SelfInstall.IsInstalled()) is { } updateLine)
                    warn.Warn(updateLine);
            }

            //said once at launch and after the context resolution, since before the probe a connect endpoint with no context would warn falsely
            if (ContextWarning.Compose(state.ContextBudget) is string ctxWarn)
                warn.Warn(ctxWarn);   //a null budget turns off elision and compaction, and a /model cannot reach null since a profile's context is an int
            //a model property, so a cloud endpoint with no model takes the default
            state.ReasoningHistory = state.Model?.Profile.ReasoningHistory ?? ReasoningHistory.All;
        }

        private async Task ServeAsync()
        {
            //said after the launch's warnings, so a user who left a server up knows nothing started behind their back. a line the session says is not said here too
            if (servedLine is not null && (args.Prompt is not null || !servedInSession))
                ServeNotice.SayUsing(new CliSurface(args.Prompt is null ? Console.Out : Console.Error, launchTheme, glyphs),
                    glyphs, servedLine, blank: args.Prompt is null);   //stderr under -p, where stdout is the model's text

            //non-interactive has no prompter to ask with and the table refuses on its own, so a scripted run cannot spawn a server
            var autoServe = AutoServe.Decide(
                interactive: args.Prompt is null,
                //the one rule for whether gatto serves this endpoint, a folder on another endpoint never starts a server
                gattoServesThisEndpoint: ServeCommand.ServingFor(state.ServedHere(), state.Model, state.Endpoint, state.LaunchBaseUrl).GattoServes,
                serverAnswered: serverAlreadyUp,
                consent: state.Model?.Profile.AutoServe,
                //the launch asker is pump-less, the /model add site keeps prompter where the pump is live
                canAsk: launchAsker is not null);

            //the single caller, reached on the interactive launch only, so -p can never spawn a process
            if (autoServe is AutoServeAction.Serve && state.Model is not null)   //an absent auto_serve means yes with nothing asked, and false still vetoes in the decision above
            {
                var manager = new ServeManager(home, ServeCommand.ServerBinaryFor(state.Model, config));
                //the start is watched on a live line, rich only under a theme. a plain launch gets the same rungs as committed lines, and a warning takes the line down first.
                using var startingLine = new TickLine(Console.Out, launchTheme is not null, blankBeforeFirst: true);
                //measured before the wait, so the last rung can name the size while the wait is still running
                var loadingBytes = Gatto.Core.Acquire.ModelDiscovery.SetBytesOrNull(state.Model.Profile.ActivePath);
                serverAlreadyUp = await AutoServeAsk.StartAndWaitAsync(
                    manager, state.Model, m => { startingLine.Finish(); warn.Warn(m); }, CancellationToken.None,
                    glyphs, LoadingLine.Over(startingLine, glyphs, launchTheme),
                    loadingBytes is { } b ? SizeWords.Gb(b, approx: true) : null);
                if (serverAlreadyUp) servedBytes = loadingBytes;
            }
            else if (autoServe is AutoServeAction.Hint && state.Model is not null)   //a declined start still says so, or the session opens on a server that never answers
                warn.Warn($"nothing is serving {state.Model.Id}. gatto serve start {state.Model.Id} when you want it.");

            //read after any start so -p and an auto-served launch both see the server, and only a server holding this model speaks for its window
            if (state.ServedHere() && state.Model is not null && state.LaunchBaseUrl is not null && !state.Cloud)
            {
                var servedFrom = Stopwatch.GetTimestamp();
                var served = await ServeProbe.ProbeAsync(probeHttp, state.LaunchBaseUrl, CancellationToken.None, state.Endpoint.ApiKey, probeDeadline);
                probeTicks += Stopwatch.GetTimestamp() - servedFrom;
                if (served is not null && ModelSwitch.DescribeProbe(state.Model, served, modelsDir) is null)
                {
                    var window = ModelWindow.Resolve(state.Model.Profile.Context, served.NCtx);
                    state.ContextBudget = window.Budget;
                    if (window.Line is string windowLine) warn.Warn(windowLine);
                }
            }
        }

        private int? CommitExtensions()
        {
            commands = new Gatto.Core.Commands.CommandRegistry();
            //every extension that committed, in discovery order, and a tool-less one is a legal load with no tool row
            (committedExt, var committedExtensions) = ExtensionHost.Commit(loadedExtensions, tools, hooks, commands, Gatto.Repl.Commands.SlashCommands.All.Select(c => c.Name), extDiag);
            //the early pass should have skipped it already, and a launch must not run on the endpoint of an extension that did not commit
            if (endpointOwner.TryGetValue(state.EndpointName, out var owner) && !committedExtensions.Any(e => e.Name == owner))
            {
                Console.Error.WriteLine($"endpoint '{state.EndpointName}' comes from extension '{owner}', which was skipped");
                return 2;
            }
            //a read-class claim counts only from a vetted shipped extension, and an unvetted one still prompts
            ExtensionHost.GrantVettedReadClass(committedExt, ShippedExtensions.IsVetted, gate.AllowReadClass);   //keyed on the owning extension, so a colliding tool from an unvetted one gets nothing

            //recompose once here, so the first turn's prompt already has the policy block
            foreach (var ext in committedExtensions)
                if (ext.Registrations.Policy is { } pol) policyLines.Add((ext.Name, pol));
            if (policyLines.Count > 0)
            {
                //the resolution and the state both hold a composition from before the extensions loaded, so both are reassigned here
                launchRes = state.ResolveRole(launchRole);
                state.Comp = launchRes.Comp;
                state.LastComposedSystem = state.Comp.SystemText;
                state.LastComposedSources = state.Comp.Sources;
            }
            return null;
        }

        private void BuildLoop()
        {
            //the same ledger the fetch recorder writes, or every citation would score as fabricated
            var citationGate = new CitationGate(ledger, p =>
            {
                try
                {
                    var abs = Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(cwd, p));
                    return File.Exists(abs) ? File.ReadAllText(abs) : null;
                }
                catch (Exception) { return null; }
            })
            { Armed = state.Comp.Gates.Contains("citations") };
            hooks.OnToolResult(citationGate.OnToolResultAsync);   //always registered, so a /role can still arm it

            //a one-slot holder the Repl fills once its renderer exists, and on -p it stays empty so stdout stays byte-pure
            subagentProgress = new Action<string>?[1];
            var runAgent = new RunAgentTool(   //a role may only name the six core built-ins, so a child's registry can never hold run_agent, ask_user or an extension tool
                roles: () => state.RoleFiles,
                refuseRole: state.RefuseRole,
                composeChild: state.ComposeChild,
                subagentWiring: SubagentWiring.Build(gate, diag: warn.Warn),   //a fresh registry per call over the shared gate, which tags each of its prompts with the role's name
                client: client,
                requestSettings: state.RequestSettings,
                parentCtx: toolCtx,
                progress: m => subagentProgress[0]?.Invoke(m),
                contextBudget: state.ContextBudget,
                reasoningHistory: state.ReasoningHistory,
                marks: CommandBanner.MarksFor(home));
            tools.Register(runAgent);   //main registry only, so a sub-agent cannot call run_agent (depth 1)

            loop = new AgentLoop(client, tools, hooks, toolCtx, state.ModelString, state.Comp.Sampling, state.Comp.ThinkingBody, state.ContextBudget,
                promptSuffix: state.Comp.ThinkingSuffix,
                reasoningHistory: state.ReasoningHistory, trace: trace) { TraceRole = state.Role.Name };
            //the services the switches use, handed over once the loop exists, since the role resolves before any of them does
            state.UseServices(loop, runAgent, citationGate, probeHttp, probeDeadline, prompter, launchTheme);

            //the launch's thinking shape, which /role, /effort and the footer read and a /model derives again
            state.ReasoningMap = state.Model?.Profile.Thinking ?? state.Endpoint.Thinking;
            state.BinaryOn = Gatto.Roles.Models.Thinking.BinaryOnLevel(state.ReasoningMap);   //the map's single on-level, for a Toggle model only
            state.ThinkCap = Gatto.Roles.Models.Thinking.CapabilityOf(state.ReasoningMap, state.LaunchServing?.Capability ?? ThinkCapability.None);   //the map decides: one on-level is a Toggle, two or more are Levels, and no map falls to the template sniff
            state.ThinkOn = state.DefaultThinkOn(state.Model);

            //always applied, otherwise a binary map resolves through the role default and the footer lies about thinking on
            if (state.ThinkCap == ThinkCapability.Toggle)
                state.ApplyToggle(state.ThinkOn);
        }

        private void Resume()
        {
            //resolved once, every downstream read must use this same path or the transcript and the context disagree
            continuePath = null;
            if (args.Continue)
            {
                if (continueById is not null)
                {
                    continuePath = continueById;
                }
                else
                {
                    continuePath = sessions.LatestPathForCwd();
                }
            }

            ResumeDecision? resumeDecision = null;
            ResumeDelta? resumeDelta = null;
            resumeLine = null;
            convo = new Conversation(state.Comp.SystemText, baseline: state.NextBaseline(state.Comp.Sources));
            convo.SetGattoRole(state.Role.Name);   //the launch role name, so messages from before a /role replay with their own tint
            resumedFrom = null;
            var resumeSpan = continuePath is null ? default : trace.Open(Gatto.Core.Trace.Resume);
            if (continuePath is { } contPath && sessions.LoadPath(contPath) is { } prior)
            {
                //a resume is the same session, so its next file and its shell children carry the resumed one's identity
                if (SessionStore.IdentityOf(contPath) is { } resumed) sessions.Adopt(resumed, contPath);
                //reload the prior transcript without its stored system message, the decision below picks the one that leads
                convo.Load(prior.Where(m => m.Role != "system"));

                var priorSystem = prior.FirstOrDefault(m => m.Role == "system")?.Content;
                var stored = SessionStore.LoadBaseline(contPath);
                resumeDecision = ResumePlan.Decide(stored, convo.Baseline!, folderExists: !folderGone, effortFlagGiven: args.Effort is not null);
                if (resumeDecision.Reset is null && priorSystem is not null && stored is not null)
                {
                    //a replay sends the stored text byte for byte, its compaction block included, so the server finds the prefix it holds
                    convo.ReplaceSystem(priorSystem, stored);
                    state.BaselineSeq = stored.Seq;
                    if (args.Effort is null && stored.Thinking != LaunchState.ThinkingOf(state.Comp)) state.ResumeThinking(convo, stored.Thinking);
                    //what changed since the model last saw the sources rides the first message the user sends
                    resumeDelta = ResumePlan.Delta(ResumePlan.EffectiveSources(stored, prior), state.Comp.Sources);
                    if (ResumeUpdate.Text(resumeDelta, state.Role.Append, state.ModelAppendText()) is { } updateText)
                        convo.SetPendingUpdate(new SessionUpdate(updateText, state.Comp.Sources, stored.Seq));
                }
                else
                {
                    //a reset leads with the launch composition, one sequence above the stored one
                    state.BaselineSeq = (stored?.Seq ?? 0) + 1;
                    var fresh = convo.Baseline! with { Seq = state.BaselineSeq };
                    //the last marker, a GATTO.md that documents it can put an earlier one in the system prompt
                    var idx = priorSystem?.LastIndexOf(Compactor.ContextMarker, StringComparison.Ordinal) ?? -1;
                    //re-append the compaction block, the fresh system dropped it and the model resumes with no context behind the lead
                    convo.ReplaceSystem(idx >= 0 ? convo.Messages[0].Content + "\n\n" + priorSystem![idx..] : convo.Messages[0].Content!, fresh);
                }

                //restore from the sidecar beside the resumed file, they stay in memory and a new save writes its own
                ledger.RestoreFrom(SessionFiles.Sidecar(contPath, SessionFiles.Ledger));
                resumedFrom = sessions.Identity.Name is { } resumedName ? $"{resumedName} ({SessionStamp(contPath)})" : SessionStamp(contPath);   //a named session is resumed by its name
                //the count is gatto's estimate, the server's own arrives on the first request after this mark
                resumeSpan.Set("records", prior.Count);
                if (trace.Enabled) resumeSpan.Set("est_tokens", ContextBudget.Estimate(convo.Messages));
                resumeSpan.Dispose();
                trace.MarkResume();

                //printed on stdout, or on stderr under -p where stdout is model text by contract
                (args.Prompt is null ? Console.Out : Console.Error)
                    .WriteLine($"gatto: session {Path.GetFullPath(contPath)}");

                //computed once from the decision and the update that was set, never from a second comparison
                resumeLine = ResumeUpdate.Line(resumeDecision!, convo.PendingUpdate is null ? null : resumeDelta, goneFolder, cwd);
                if (args.Prompt is not null && resumeLine is not null) Console.Error.WriteLine(resumeLine);
            }
        }

        //the launch span closes where the user can first act, before the -p turn or when the session first waits for input
        private void CloseLaunch()
        {
            static long Us(long ticks) => ticks * 1_000_000 / Stopwatch.Frequency;
            launchSpan.Set("home_us", Us(homeTicks));
            launchSpan.Set("shipped_us", Us(shippedTicks));
            launchSpan.Set("context_files_us", Us(state.ContextFilesTicks));
            launchSpan.Set("memory_us", Us(state.MemoryTicks));
            launchSpan.Set("probe_us", Us(probeTicks));
            launchSpan.Dispose();
        }

        private async Task<int> RunOneShotAsync(string prompt)
        {
            //stderr only, the -p contract keeps stdout for model text
            if (WildNotice(permissions, wildState, config.Wild) is { } wildLine) Console.Error.WriteLine(wildLine);
            if (residueLine is not null) Console.Error.WriteLine(residueLine);
            if (sandboxLine is not null) Console.Error.WriteLine(sandboxLine);
            using var oneShotCts = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, e) =>
            {
                e.Cancel = true;   //the first Ctrl+C cancels the turn instead of killing the process
                try { oneShotCts.Cancel(); } catch (ObjectDisposedException) { }
            };
            Console.CancelKeyPress += cancelHandler;
            //set for the whole -p run, so a kill mid-run still saves what was written
            loop.OnRoundPersisted = sessions.Save;
            //a headless run compacts on the same threshold and project veto as a session, so a long task outlives the window instead of ending on a 400
            var oneShotArmedAt = Gatto.Repl.Repl.ArmedAutoCompactAt(config.AutoCompact, cwd);
            var oneShotUsage = new ContextUsageState();
            oneShotUsage.SeedFrom(convo.Messages);   //a --continue keeps the last reply's cut, a fresh conversation has no reply and seeds nothing
            loop.EnableAutoCompact(
                oneShotArmedAt is null ? null
                    : new OneShotCompaction(client, state.ModelString, loop, convo, state.ContextBudget, oneShotUsage, sessions, state.RecomposeSystem, glyphs.Dot),
                oneShotUsage, oneShotArmedAt);
            if (resumedFrom is null) Gatto.Repl.Repl.SeedFirstMessage(convo, state.Role.FirstMessage);   //a resumed transcript already opened with it
            CloseLaunch();
            try
            {
                var r = await loop.RunTurnAsync(convo, prompt, new OneShotObserver(), oneShotCts.Token);
                sessions.Save(convo);   //this sets CurrentPath, so the stop-reason line must come after it
                Console.WriteLine();
                WriteStopReasonJson(r, sessions.CurrentPath);
                return r.Outcome == TurnOutcome.Completed ? 0 : 1;
            }
            catch (GattoConnectionException ex)
            {
                //no JSON line here, a headless caller reads that absence as gatto itself failing
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;   //a static event, so detach it and the handler cannot outlive this run
                trace.Flush();
            }
        }

        private async Task<int> RunReplAsync()
        {
            if (residueLine is not null) launchWarnings.Add(residueLine);
            //said after the banner with the launch warnings, a line written before it is under the alt screen. its own row, since it is this launch's state and never saved
            string? wildLaunchNotice = WildNotice(permissions, wildState, config.Wild) is { } wildNotice
                ? (wildState.On ? $"{wildNotice} {glyphs.Dot} /safe to turn off" : wildNotice)
                : null;

            //the one helper /role and /model also read, so a rebuild matches the launch footer
            string? initialThinkingName = state.ThinkingFooterName();

            //the same registry the loop advertises from, so /tools describes this session, and committedExt adds which script owns each tool
            var toolOrigins = committedExt.ToDictionary(c => c.Name, c => c.Extension.Name, StringComparer.Ordinal);
            Func<IReadOnlyList<Gatto.Repl.Prompts.ToolInfo>> listTools = () => tools.Specs()
                .Select(s => new Gatto.Repl.Prompts.ToolInfo(s.Name, s.Description, toolOrigins.GetValueOrDefault(s.Name)))
                .ToList();
            //the same list the prompt was composed from, so /tools cannot show a policy the model was never told
            var policyRows = policyLines.ToList();
            Func<IReadOnlyList<(string Extension, string Line)>> listPolicy = () => policyRows;

            //said the first time this home runs an interactive session on a cloud endpoint, and the marker file is what makes it once
            string? cloudNotice = null;
            var cloudMarker = Path.Combine(home, "cloud-notice-shown");
            if (state.Cloud && args.Prompt is null && !File.Exists(cloudMarker))
            {
                cloudNotice = CloudEndpoint.Notice(glyphs.Cloud, state.ModelString);
                try { File.WriteAllText(cloudMarker, ""); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   //a home that cannot take the marker says the notice again next launch
            }

            var slotsHttp = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) })   //untimed, since a timed client aborts each poll and the ticker's own ceiling is the deadline, and ConnectTimeout still fails fast
                { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            Gatto.Core.Client.ISlotsReader? slotsReader = state.EndpointName == "local" && state.LaunchBaseUrl is not null   //a local llama-server only, the fallback for a server that streams no progress
                //the key the server was started with, without it a keyed server stops the prefill indicator
                ? new Gatto.Core.Client.HttpSlotsReader(slotsHttp, state.LaunchBaseUrl, state.Endpoint.ApiKey)
                : null;

            //a non-local endpoint that names no models has no rows to offer, the local shelf is not its to switch to
            var unlistedRemote = !state.ServedHere() && state.Endpoint.Models is not { Count: > 0 };

            //the hint reads the same two locals as the reuse line, so the two sentences cannot disagree about what is loaded
            var repl = new Gatto.Repl.Repl(loop, convo, state.Role.Name, state.ModelString, sessions, client, state.Endpoint.Context, state.ContextBudget, cwd, state.RecomposeSystem, state.SwitchRole, wildState, on => permissions.SetWild(on, persist: true), hooks, themeMode, subagentProgress, resumedFrom: resumedFrom, reasoning: config.ReasoningMode, thinkingName: initialThinkingName, setEffort: state.SetEffort, listEfforts: state.ListEfforts, glyphs: glyphs, purrSet: Gatto.Repl.Render.Chrome.PurrFrames.RandomFromPool, versionLine: CommandBanner.DottedVersion(Gatto.Core.GattoVersion.String, Gatto.Core.GattoVersion.Build ?? "", CommandBanner.IsDevBuild(), glyphs), pump: pump, chrome: chrome, switchModel: state.SwitchModel, setDefault: id => ModelSwitch.Persist(home, state.EndpointName, id),   //the role name as ResolveRole cased it from disk, which the banner and the status line key on
                        launchServing: state.LaunchServing, probeServing: state.ProbeServing, listModels: unlistedRemote ? null : state.ListModels, picker: replPicker, slotsReader: slotsReader, warn: warn, altScreen: config.AltScreen, dumpOnExit: config.DumpOnExit, thinkingIsToggle: state.ThinkCap == ThinkCapability.Toggle, thinkingIsUnavailable: state.ThinkingUnavailable(), mouseEnabled: config.Mouse && config.AltScreen && pump is not null, wheelLines: config.WheelLines, copyOnSelect: config.CopyOnSelect, resumedPath: continuePath, resumeLine: resumeLine,
                modelMarkLegend: !state.ServedHere() ? "current" : "loaded",
                setDefaultEffort: state.SetModelDefaultEffort,
                //read at the moment a connection is lost, through the current model, so a /model switch is followed
                serverGone: () => state.Model is { } served
                    ? ServeLines.GoneLine(new ServeManager(home, ServeCommand.ServerBinaryFor(served, config)).Dead(served.Profile.Port), glyphs)
                    : null,
                        autoCompact: config.AutoCompact, permissions: permissions, listTools: listTools, listPolicy: listPolicy, commands: commands, memoryWarning: launchRes.MemoryTruncatedLines > 0 ? MemoryTruncationWarning(launchRes.MemoryTruncatedLines) : null, cloudNotice: cloudNotice, sandboxNotice: sandboxLine, wildNotice: wildLaunchNotice, servedNotice: servedInSession && servedLine is not null ? TermText.Sanitize(ServeNotice.Text(servedLine)) : null, launchWarnings: launchWarnings, cloud: state.Cloud, readUsage: usageMeter.Read, onUsageChanged: repaint => usageMeter.Changed = repaint,piggyback: piggyback, turnAbort: turnAbort,
                //a session with no model talks to a server gatto does not manage, so the notice says that instead of offering a model fix
                unmanagedNotice: !state.ServedHere() && state.Endpoint.Models is not { Count: > 0 } && !state.Cloud ? UnmanagedSession.ModelUnavailable(state.LaunchBaseUrl) : null,
                cloudSwitchHint: unlistedRemote && state.Cloud ? CloudSwitchHint(state.EndpointName) : null,
                unmanagedVisionNotice: !state.ServedHere() ? UnmanagedSession.VisionUnavailable(state.LaunchBaseUrl) : null,
                deafWatch: deafWatch,
                wildFromHome: config.Wild, wildPersisted: () => permissions.Wild, sandbox: sandbox,
                defaultRole: config.DefaultRole ?? "generalist",
                roleTint: state.Role.ParsedTint(), roleAvatar: state.Role.Avatar, firstMessage: state.Role.FirstMessage,
                armTasks: tasks => ((AppToolContext)toolCtx).Tasks = tasks,
                //read when /context asks, so a /role or /model since is followed. the shape is the loop's last request, its tools split by origin
                contextInputs: usage =>
                {
                    var shape = loop.RequestShape;
                    return new ContextInputs(state.ModelString, state.Comp.SystemText, state.Comp.SystemParts ?? [],
                        [.. shape.Tools.Where(t => !tools.IsExtension(t.Name))], [.. shape.Tools.Where(t => tools.IsExtension(t.Name))],
                        shape, state.ContextBudget, loop.ArmedAutoCompactAt, usage.Ratio, usage.LastPromptTokens, loop.LastTimings, loop.LastUsage);
                },
                //a cloud endpoint has no llama-server routes, any other may and falls back to the estimate when it does not
                tokenCounter: state.Cloud ? null : client,
                //a connect session has no profile.json to point at, so the hint is null there
                visionFixHint: state.ServedHere() && state.Model is not null
                    ? UnmanagedSession.VisionFixBesideTheModel(state.Model.Profile.ActivePath)
                    : null,
                //re-read on every call, a launch snapshot would keep showing the file the session started on
                modelFiles: !state.ServedHere() || state.Model is null ? null : () =>
                {
                    try
                    {
                        return [.. Gatto.Roles.Models.Model.Load(modelsDir, state.Model.Id).Profile.Files
                            .Select(f => (f.Quant ?? Path.GetFileNameWithoutExtension(f.Path), f.Active))];
                    }
                    //a profile that went unloadable mid-session returns an empty list, this must not take the REPL down
                    catch (GattoConfigException) { return []; }
                },
                //the bodies live in QuantEdit, which refuses before it asks (the confirm needs the wizard prompter and the serving state here)
                switchQuant: !state.ServedHere() || state.Model is null ? null : (Func<string, string>)(name =>
                    QuantEdit.Switch(modelsDir, state.Model, name,
                        () => ServeCommand.QuantServing(home, state.Model, config) is Gatto.Roles.Serve.ServingState.ServingThis,
                        Confirming(prompter),
                        //the same stop, start and restore the model switch uses, so there is one copy of the hardest half
                        restart: (previous, reloaded) => ServeCommand.SwapServerAsync(
                            new Gatto.Roles.Serve.ServeManager(home, ServeCommand.ServerBinaryFor(reloaded, config)),
                            previous, reloaded, warn.Warn,
                            glyphs).GetAwaiter().GetResult())),
                //forgetting a file is its own command, the switch only changes which one is active
                removeQuant: !state.ServedHere() || state.Model is null ? null : (Func<string, string>)(name =>
                    QuantEdit.Remove(modelsDir, state.Model, name,
                        () => ServeCommand.QuantServing(home, state.Model, config) is Gatto.Roles.Serve.ServingState.ServingThis,
                        Confirming(prompter))))
    ;
            repl.OnReady = CloseLaunch;
            var replExit = await repl.RunAsync(CancellationToken.None);
            trace.Flush();

            //the key stops only the server of a session on the local endpoint. a connect session never used the server this home records.
            InkedLine? serverLine = null;
            if (config.StopServerOnExit && state.ServedHere() && state.Model is not null && state.LaunchBaseUrl is not null)
            {
                var stopped = ServeLines.AtExit.AfterStop(
                    new ServeManager(home, config.LlamaServer ?? "").Stop(Gatto.Roles.Serve.NullServeListener.Instance),
                    serverAlreadyUp, glyphs);
                //a failed stop is a real failure, so it goes to the warning sink and no server line is shown
                if (stopped is { WentWrong: true }) warn.Warn(stopped.Text);
                else serverLine = stopped?.Words;
            }
            else serverLine = ServeNotice.ExitHint(serverAlreadyUp, servedBytes, glyphs);

            //one writer and one order for everything read at the end, so a pipe cannot get the lines out of order
            ExitBlock.Write(new CliSurface(Console.Out, launchTheme, glyphs), glyphs,
                farewell: repl.Ended?.Farewell ?? false, resume: repl.Ended?.Resume ?? false, serverLine, endedTasks: repl.Ended?.EndedTasks ?? 0);
            return replExit;
        }
    }

    internal const int MemoryBudgetFloor = 1700, MemoryBudgetCeiling = 4000;

    //a slice of the model's context clamped to a floor that carries the facts that change a first move and a ceiling that keeps the block glanceable in every prefix
    internal static int DerivedMemoryBudget(int? modelContext)
    {
        if (modelContext is not int ctx || ctx <= 0) return MemoryBudgetFloor;   //an unknown context takes the floor rather than a guess
        return Math.Clamp(ctx * 2 / 100, MemoryBudgetFloor, MemoryBudgetCeiling);
    }

    //the one truncation sentence for the launch line and every reload warning, so the wording cannot drift and truncation is never silent
    internal static string MemoryTruncationWarning(int facts) =>
        $"memory index over budget: {facts} facts not shown; prune .gatto\\memory\\";

    //the stop reason a headless caller reads off the -p JSON, and a length cut is a finish reason of length on a completed turn
    internal static string OutcomeString(TurnResult r) => r.Outcome switch   //a length cut stays completed with exit 0, and only this string tells it from a clean stop
    {
        TurnOutcome.Cancelled => "cancelled",
        TurnOutcome.Truncated => r.Truncation == TruncationKind.Stream ? "truncated_stream" : "truncated_length",
        _ => r.FinishReason == "length" ? "truncated_length" : "completed",
    };

    //the launch sink: stderr always, and the carried list too when the face will say the line again after the banner
    internal static Action<string> LaunchSink(List<string> carried, bool carry) => m =>
    {
        Console.Error.WriteLine($"! {m}");
        if (carry) carried.Add(m);
    };

    //the residue sweep and the plant each walk a packages folder's every entry, so an interactive launch draws them on the serve start's live line, one line for both. -p passes no line
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static async Task<string?> PrepareSandboxAsync(Gatto.Core.Sandbox.SandboxState sandbox, string? source, TickLine? line, GlyphSet glyphs, Theme? theme)
    {
        var residue = sandbox.HasResidue();
        if (!residue && source is null) return null;
        string? Work()
        {
            var said = residue ? sandbox.RemoveResidue() : null;
            if (source is not null) sandbox.TurnOn(source);
            return said;
        }
        if (line is null) return Work();
        var words = residue ? $"removing labels left by an earlier session{glyphs.Ellipsis}" : $"preparing the sandbox{glyphs.Ellipsis}";
        var work = Task.Run(Work);
        var from = Stopwatch.GetTimestamp();
        var frame = 0;
        do
        {
            line.LivePainted(LoadingRow.Render(words, Stopwatch.GetElapsedTime(from), glyphs, theme, frame++), glyphs);
            await Task.WhenAny(work, Task.Delay(ResidueFrame)).ConfigureAwait(false);
        } while (!work.IsCompleted);
        line.Finish();
        return await work.ConfigureAwait(false);
    }

    private static readonly TimeSpan ResidueFrame = TimeSpan.FromMilliseconds(140);   //the pulse's step, the audition ticker's own

    //what turns the sandbox on, the command line first, then the home, then the nearest .gatto.json that asks. null leaves it off
    internal static string? SandboxSource(bool flag, bool fromHome, string cwd) =>
        flag ? "--sandbox"
        : fromHome ? "gatto.json"
        : ProjectFileConfig.EffectiveSandbox(cwd, out var by) ? by
        : null;

    //the launch line about wild mode: on and which source turned it on, or off because a project's .gatto.json forbids what the home says. null when wild is plainly off
    internal static string? WildNotice(PermissionStore permissions, WildState wild, bool wildFromHome) =>
        wild.On && !permissions.Wild && wildFromHome ? "wild mode: ON, gatto.json sets \"wild\": true"
        : wild.On ? "wild mode: ON for this project, stored in your gatto home"
        : (permissions.Wild || wildFromHome) && wild.ForbiddenBy is { } by ? $"wild mode: off, {TermText.Sanitize(by)} sets \"wild\": false for this project"
        : null;

    //call it after sessions.Save, since CurrentPath is null before the first save, and a connection failure exits 1 with no line at all
    private static void WriteStopReasonJson(TurnResult r, string? sessionPath)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))   //a JSON writer, so last_error is escaped
        {
            w.WriteStartObject();
            w.WriteString("outcome", OutcomeString(r));
            w.WriteNumber("rounds", r.Rounds);
            if (sessionPath is not null) w.WriteString("session_path", sessionPath); else w.WriteNull("session_path");
            if (r.LastError is not null) w.WriteString("last_error", r.LastError); else w.WriteNull("last_error");
            w.WriteNumber("schema_version", SessionStore.SchemaVersion);
            w.WriteEndObject();
        }
        Console.Error.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
    }

    //the human stamp for a session file, read from the ticks in its name, neutral when they will not parse
    private static string SessionStamp(string sessionPath)
    {
        var stem = Path.GetFileNameWithoutExtension(sessionPath);
        var dash = stem.LastIndexOf('-');
        //a 19-digit stem can parse as a long yet overflow DateTime, so the neutral label must hold there too
        if (dash >= 0 && long.TryParse(stem[(dash + 1)..], out var ticks) && ticks <= DateTime.MaxValue.Ticks)
            return new DateTime(ticks, DateTimeKind.Utc).ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        return "previous session";
    }

    //the size words for a model id alone, null when this home cannot load the id
    private static string? SizeWordsOnDiskOrNull(string home, string modelId)
    {
        try
        {
            var model = Gatto.Roles.Models.Model.Load(Path.Combine(home, "models"), modelId);
            return Gatto.Core.Acquire.ModelDiscovery.SetBytesOrNull(model.Profile.ActivePath) is { } bytes
                ? SizeWords.Gb(bytes, approx: true)
                : null;
        }
        catch (Exception) { return null; }
    }

    //the audition knows its model by id only, so it reads the size the same way every other site does
    internal static string? AuditionSize(string home, string modelId) => SizeWordsOnDiskOrNull(home, modelId);

}
