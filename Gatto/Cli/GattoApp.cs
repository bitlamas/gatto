using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Web;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Memory;
using Gatto.Core.Tools;
using Gatto.Extensions;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Roles;
using Gatto.Roles.Agents;
using Gatto.Terminal;

namespace Gatto.Cli;

file sealed class AppToolContext(string cwd, string home, IUserPrompter? prompter) : IToolContext
{
    public string Cwd => cwd;
    public string HomePath => home;
    public IUserPrompter? Prompter => prompter;
}

//the role, endpoint, model and composition resolved for a launch or a /role switch, plus the memory index lines its budget dropped
file sealed record RoleResolution(
    RoleFile Role, Model? Model, EndpointConfig Endpoint, string EndpointName, string ModelString, Composition Comp,
    int MemoryTruncatedLines);

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

    public async Task<CompactionResult?> CompactAsync(
        CompactionReason reason, string currentUserPrompt, ITurnObserver observer, CancellationToken ct)
    {
        var compactor = new Compactor(client, model, loop.RequestShape);
        var summary = await compactor.SummarizeAsync(convo, observer, ct, windowTokens: window, ratio: usage.Ratio, midTurn: true);
        LastFailure = compactor.LastFailure;
        if (summary is null) return null;
        var (rebuilt, _) = Compactor.RebuildMidTurn(summary, convo, currentUserPrompt, observer, sessions, recompose, dot);
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

    //the help text is the first thing a stranger reads, so it uses the user's words rather than harness

    //internal so a guard drives the string that ships, since a guard over a copy is an oracle about the copy
    internal const string Help = """
        gatto, a terminal assistant that runs AI models on your own computer
        usage: gatto [role|command] [options]
          roles:    generalist (default) | coder | oracle | <any ~/.gatto/roles/*.json>
          commands: setup | serve start [model] [--detach] | serve stop|status | doctor | status | audition | model new [gguf] [id] | update | uninstall
          options:  -m <model>  -p <prompt>  -e <endpoint>  --effort <level>  --continue[ <id>]  --yes  --auto  --version  -h
        """;

    //the null ctx is the production path, and only the outermost return holds a fatal exit open so it can be read
    public static async Task<int> RunAsync(string[] argv) => FatalPause.Hold(await RunAsync(argv, null));

    //internal, so the harness's ctx never joins the public surface, and no default argument, which would make one-argument calls ambiguous
    internal static async Task<int> RunAsync(string[] argv, CommandContext? ctx)
    {
        //first, before anything spawns a child, so a missing DLL kills the process instead of pinning it behind a dialog nobody sees
        Gatto.Core.ProcessErrorMode.FailFastOnMissingDll();

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
            return await RunInnerAsync(argv, ctx);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(
                Environment.GetEnvironmentVariable("GATTO_DEBUG") == "1" ? ex.ToString() : ex.Message);
            return 1;
        }
    }

    //the label half of a wizard answer, and null when the ask registers no control keys
    private static string? Label(Gatto.Repl.WizardAnswer? a) => a?.Label;

    //the ask a quant command uses, or null when the session cannot ask, in its own method so the conditional has a return type
    private static Func<Gatto.Repl.WizardAsk, bool>? Confirming(Gatto.Core.Tools.IUserPrompter? prompter) =>
        prompter is Gatto.Repl.IWizardPrompter asker
            ? ask => SwapConfirm.Confirmed(Label(asker.AskOne(ask)))
            : null;

    private static async Task<int> RunInnerAsync(string[] argv, CommandContext? ctx = null)
    {
        try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch (Exception) { }  //a legacy host may refuse this, and it is never fatal

        ParsedArgs args;
        try { args = ArgRouter.Parse(argv); }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine(Help);
            return 2;
        }

        if (args.Command == "help") { Console.WriteLine(Help); return 0; }
        //the version line takes the context's stamp, so a golden can pin it without going red on every commit
        if (args.Command == "version")
        {
            var v = ctx?.Stamp ?? VersionStamp.Running;
            Console.WriteLine(CommandBanner.VersionLine(v.Version, v.Build, v.Dev));
            return 0;
        }
        if (args.Command == "serve") return await RunServeAsync(args, ctx);
        if (args.Command == "doctor") return await RunDoctorAsync(ctx);
        if (args.Command == "status") return await RunStatusAsync(ctx);
        if (args.Command == "model") return await RunModelAsync(args, ctx);
        if (args.Command == "audition") return await RunAuditionAsync(args);
        if (args.Command == "uninstall") return RunUninstall(ctx);
        if (args.Command == "update")
            return await UpdateCommand.RunAsync(ctx ?? CommandContext.Production(), CancellationToken.None);
        //the wizard falls through to the launch path instead of returning, so the setup screen's start option really starts gatto
        var launchAfterSetup = false;
        if (args.Command == "setup")
        {
            var (setupExit, thenLaunch) = RunSetup();
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
            var (offerExit, offerLaunch) = RunSetup();
            if (!offerLaunch) return offerExit;
            launchAfterSetup = true;
        }

        //config resolution runs before anything is created, so a launch that dies on a config error leaves the home untouched
        GattoConfig config;
        try { config = GattoConfig.Load(home); }
        catch (GattoConfigException ex) { Console.Error.WriteLine(ex.Message); return 2; }

        GattoHome.EnsureInitialized(home);
        var rolesDir = Path.Combine(home, "roles");
        var modelsDir = Path.Combine(home, "models");
        //shipped files are written before anything reads them, a user edit survives, and a write failure warns rather than aborting the launch
        try { ShippedExtensions.EnsureWritten(home); }
        catch (Exception ex) { Console.Error.WriteLine($"! extension: could not write shipped files: {ex.Message}"); }

        //an id names the session's folder, so the process moves there first and every read below takes that folder
        string? continueById = null;
        //the folder is gone, and goneFolder names it, or null when the record holds no folder at all
        var folderGone = false;
        string? goneFolder = null;
        if (args.Continue && args.ContinueId is { } earlyId)
        {
            try { continueById = SessionStore.ResolvePathById(home, earlyId); }
            catch (GattoConfigException ex) { Console.Error.WriteLine(ex.Message); return 2; }
            var storedFolder = SessionStore.CwdOf(continueById);
            if (storedFolder is not null && Directory.Exists(storedFolder)) Environment.CurrentDirectory = storedFolder;
            else { folderGone = true; goneFolder = storedFolder; }
        }

        var cwd = Environment.CurrentDirectory;

        //project memory: resolved once here from the home setting and every .gatto.json ruling, which can only remove it
        var memoryOn = ProjectFileConfig.EffectiveMemoryEnabled(cwd, config.MemoryEnabled);
        //the memory budget is the profile override, else gatto.json, else derived from the context window and clamped
        int MemoryBudget(Model? p) =>
            p?.Profile.MemoryIndexBudget
            ?? config.MemoryIndexBudget
            ?? DerivedMemoryBudget(p?.Profile.Context);
        //the shared load, with the model passed in so a caller composes against the model armed at call time
        MemoryIndex.LoadResult LoadMemory(Model? p) => memoryOn
            ? MemoryIndex.Compose(MemoryDir.FindProjectRoot(cwd), MemoryBudget(p))
            : new MemoryIndex.LoadResult(null, 0);
        //the session's -m, seeded at launch and moved by every switch, so ResolveRole reads it rather than the stale args.Model
        string? sessionModel = args.Model;

        //declared here so both closures below see the filled list, and it must be read through the variable rather than captured
        var policyLines = new List<(string Extension, string Line)>();

        //stderr until the rich loop rebinds it to a ♯ system line, so a mid-session diagnostic never corrupts the alt-screen frame
        var warn = new Gatto.Core.WarningSink(m => Console.Error.WriteLine($"! {m}"));
        var staleDefaultWarned = new HashSet<string>(StringComparer.Ordinal);   //a stale default is said once per process, since /role and the policy pass resolve again
        //null until the prompters are built below, so an extension that asks at load time gets the no-interactive-input error
        IUserPrompter? prompter = null;
        var sessions = new SessionStore(home, cwd);

        //extensions

        //loaded before the role resolves, since an extension can contribute the endpoint the launch names. a throwing script fails that extension alone
        var extensionsDir = Path.Combine(home, "extensions");
        Action<string> extDiag = m => warn.Warn($"extension: {m}");
        //the configured searxng origin is the only SSRF exemption config can grant, exactly that scheme, host and port
        var allowedOrigins = config.Search.SearxngUrl is { } sxUrl
            ? new[] { UrlGuard.Origin(new Uri(sxUrl)) }
            : null;
        var (extApi, ledger, _) = ExtensionHost.BuildApi(   //scripts get the guarded fetch and this session's citation ledger, which stays in memory until the session file exists
            home, cwd,
            ledgerPath: () => sessions.CurrentPath is { } sp
                ? Path.Combine(Path.GetDirectoryName(sp)!, Path.GetFileNameWithoutExtension(sp) + ".ledger.jsonl")
                : null,
            prompter: () => prompter,
            log: extDiag,
            configSection: SectionReader(config.Raw),
            allowedOrigins: allowedOrigins);
        //every name a built-in registers below, so an extension that would collide is skipped here and never lends the launch its endpoint
        var reservedTools = new List<string>
        {
            new ReadFileTool().Name, new WriteFileTool().Name, new EditFileTool().Name,
            new GlobTool().Name, new GrepTool().Name, new ShellTool().Name, "task_restate",
        };
        if (memoryOn) reservedTools.AddRange(new[] { "memory_write", "recall_memory" });
        var loadedExtensions = ExtensionHost.Survivors(
            ExtensionHost.LoadAll(extensionsDir, extApi, extDiag), reservedTools, config.Endpoints.Keys, extDiag);
        //the table every resolution reads: the endpoints of gatto.json plus the ones the surviving extensions contributed
        var endpoints = new Dictionary<string, EndpointConfig>(config.Endpoints, StringComparer.Ordinal);
        var endpointOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var ext in loadedExtensions)
            foreach (var (epName, epConfig) in ext.Registrations.Endpoints)
            {
                endpoints[epName] = epConfig;
                endpointOwner[epName] = ext.Name;
            }

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

        //a non-local session's saved level, parsed here since Core holds only the name, and a bad name is a config error naming its key
        ThinkingLevel? SavedCloudEffort(GattoConfig cfg, string onEndpoint, string modelId)
        {
            if (cfg.EffortDefaultFor(onEndpoint, modelId) is not { } levelName) return null;
            try { return Thinking.Parse(levelName); }
            catch (GattoConfigException ex)
            {
                throw new GattoConfigException($"gatto.json defaults.{onEndpoint}.effort.{modelId}: {ex.Message}");
            }
        }

        //one resolution of role, endpoint, model and composition, run at launch and by /role, which fails before any client is built
        RoleResolution ResolveRole(string roleArg)
        {
            var available = RoleFile.ListNames(rolesDir);
            if (!available.Contains(roleArg, StringComparer.OrdinalIgnoreCase))
                throw new GattoConfigException(available.Count > 0
                    ? $"unknown role '{roleArg}'. Available roles: {string.Join(", ", available)}"
                    : $"unknown role '{roleArg}'. No role files found in {rolesDir}");
            //the role name takes the on-disk casing here, since the checks below are ordinal and gatto ORACLE must report oracle
            roleArg = available.First(n => string.Equals(n, roleArg, StringComparison.OrdinalIgnoreCase));
            var role = RoleFile.Load(rolesDir, roleArg);
            //the --effort flag wins over the role's own thinking, like -e wins over its endpoint
            if (args.Effort is { } flagEffort) role = role with { ThinkingRequested = flagEffort };

            //the -e endpoint is read here too, so a /role with no endpoint of its own keeps the launch one
            var endpointName = launchEndpoint ?? role.Endpoint ?? config.DefaultEndpoint;
            if (!endpoints.TryGetValue(endpointName, out var ep))
                throw new GattoConfigException(args.Endpoint is not null
                    ? $"-e names endpoint '{endpointName}', which is not defined in gatto.json. Endpoints: {string.Join(", ", endpoints.Keys.OrderBy(k => k, StringComparer.Ordinal))}"
                    : role.Endpoint is not null
                        ? $"role '{role.Name}' targets endpoint '{endpointName}', which is not defined in gatto.json"
                        : LaunchModel.MissingDefaultEndpoint(endpointName));

            //the endpoint's thinking-map keys are parsed here, so a bad key fails the launch with the endpoint named
            if (ep.Thinking is not null)
                foreach (var key in ep.Thinking.Keys)
                {
                    try { Thinking.Parse(key); }
                    catch (GattoConfigException ex)
                    {
                        throw new GattoConfigException($"endpoint '{endpointName}' thinking map: {ex.Message}");
                    }
                }

            var endpointIsLocal = endpointName == "local";

            //the model is resolved first, -m over the role file over the endpoint's saved model, so a shipped role with no model of its own still launches
            Model? model = null;
            EndpointConfig endpoint;
            string modelString;
            if (endpointIsLocal)
            {
                //a local endpoint takes -m as a model id
                var effectiveModel = RoleFile.EffectiveModel(role, sessionModel, config.ModelDefaultFor("local"));
                if (effectiveModel is null)
                    throw new GattoConfigException(
                        $"role '{role.Name}' has no model for local endpoint '{endpointName}'. Set one via " +
                        "-m <model>, \"model\" in the role file, or defaults.local.model in gatto.json (see: gatto doctor)");

                role = role with { Model = effectiveModel };

                model = Model.Load(modelsDir, effectiveModel);
                modelString = effectiveModel;
                //the model holds the api key and the endpoint's client sends it, so it goes on the EndpointConfig, untouched when there is none
                var resolved = model.Profile.ApiKey is { Length: > 0 } modelApiKey
                    ? ep with { ApiKey = modelApiKey }
                    : ep;
                endpoint = resolved.BaseUrl is null   //an explicit base_url wins, else the model's port, so a null base_url never reaches the client's fail-closed backstop
                    ? resolved with { BaseUrl = $"http://127.0.0.1:{model.Profile.Port}" }
                    : resolved;
            }
            else
            {
                //the -m flag is a verbatim model string on a cloud endpoint. a listed endpoint hears the role only when the role was written for it, an unlisted one as before
                var explicitModel = ep.Models is { Count: > 0 }
                    ? sessionModel ?? (role.Endpoint == endpointName ? role.Model : null)
                    : sessionModel ?? role.Model;
                var choice = LaunchModel.Resolve(endpointName, ep.Models, explicitModel, config.ModelDefaultFor(endpointName));
                if (choice.Warning is { } stale && staleDefaultWarned.Add(stale)) warn.Warn(stale);
                var effectiveModel = choice.Model;
                if (ep.Models is { Count: > 0 } listed && !listed.Contains(effectiveModel, StringComparer.Ordinal))
                    throw new GattoConfigException(
                        $"endpoint '{endpointName}' has no model '{effectiveModel}'. Models: {string.Join(", ", listed)}");
                if (effectiveModel is null)
                    throw new GattoConfigException(
                        $"role '{role.Name}' has no model for cloud endpoint '{endpointName}'. Set one via " +
                        $"-m <model>, \"model\" in the role file, or defaults.{endpointName}.model in gatto.json (see: gatto doctor)");

                role = role with { Model = effectiveModel };

                modelString = effectiveModel;
                //a folder of this name supplies its model parts here too, the serving, the port and the api key stay the endpoint's
                model = Model.TryLoad(modelsDir, effectiveModel);
                endpoint = ep;   //config requires an explicit base_url on a cloud endpoint
            }

            var contextFiles = ContextFiles.Collect(
                cwd, config.ContextCompat, config.ContextHome ? Path.Combine(home, "GATTO.md") : null);
            //reloaded beside the context files at launch and on every /role, so the memory budget follows the model the role resolves to
            var memory = LoadMemory(model);
            var comp = RoleComposition.Compose(role, model, contextFiles, endpoint.Thinking, cwd: cwd, date: DateTime.Now,
                memoryIndex: memory.Text, memoryTruncatedLines: memory.TruncatedLines, memoryNudge: memoryOn,
                policyLines: policyLines,
                savedEffort: endpointIsLocal ? null : SavedCloudEffort(config, endpointName, modelString));

            return new RoleResolution(role, model, endpoint, endpointName, modelString, comp, memory.TruncatedLines);
        }

        RoleResolution launchRes;
        try { launchRes = ResolveRole(args.Role); }
        catch (GattoConfigException ex) { Console.Error.WriteLine(ex.Message); return 2; }
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
        //set once from the launch resolution, so a refused /role and a later /effort cannot change what shell children read
        Environment.SetEnvironmentVariable("GATTO_ENDPOINT", launchRes.EndpointName);
        Environment.SetEnvironmentVariable("GATTO_MODEL", launchRes.ModelString);
        Environment.SetEnvironmentVariable("GATTO_EFFORT", EffortSwitch.Name(launchRes.Comp.Thinking));
        var role = launchRes.Role;
        var model = launchRes.Model;
        //both halves of memory consent in one nullable, so with memory off nothing banks and nothing reads
        PiggybackSeam? piggyback = memoryOn
            ? new(Bank: cands => MemoryPiggyback.BankCandidates(MemoryDir.FindProjectRoot(cwd), cands),
                  ComposeIndex: () => MemoryIndex.Compose(MemoryDir.FindProjectRoot(cwd), MemoryBudget(model)))   //read at call time, since /role and /model reassign model and the budget follows it
            : null;

        //seeded with the launch reading, so the first reload after the banner does not repeat the over-budget line
        var memoryNag = new MemoryNagLatch();
        if (launchRes.MemoryTruncatedLines > 0)
            memoryNag.ShouldWarn(MemoryBudget(model), launchRes.MemoryTruncatedLines);
        var endpoint = launchRes.Endpoint;
        var endpointName = launchRes.EndpointName;
        var modelString = launchRes.ModelString;
        var comp = launchRes.Comp;
        //the prefix recomposeSystem falls back to when a mid-session .gatto.json edit is malformed, so it holds the last one that composed
        var lastComposedSystem = comp.SystemText;
        var lastComposedSources = comp.Sources;
        //the base URL the client below is built for, /role refuses any switch that resolves to another one
        var launchBaseUrl = endpoint.BaseUrl;
        //a cloud endpoint is no llama-server, so nothing below sends it a /props probe
        var cloud = CloudEndpoint.Is(endpoint);
        //gatto starts, probes, sizes and stops only the server of the endpoint named local, a model folder found on another endpoint supplies its model parts alone
        bool ServedHere() => endpointName == "local";

        var tools = new ToolRegistry();
        tools.Register(new ReadFileTool());
        tools.Register(new WriteFileTool());
        tools.Register(new EditFileTool());
        tools.Register(new GlobTool());
        tools.Register(new GrepTool());
        tools.Register(new ShellTool());
        //the memory tools go on the main registry only, and subagents build their own list without them
        if (memoryOn)
        {
            tools.Register(new MemoryWriteTool(() => MemoryBudget(model)));   //a closure over model, so after a /model the advisory and the prefix load read the same budget
            tools.Register(new RecallMemoryTool());
        }

        var hooks = new HookBus();
        hooks.OnHandlerError += (evt, ex) => warn.Warn($"hook error ({evt}): {ex.Message}");
        //the theme is resolved on the interactive REPL path only, so a -p run stays byte-pure and never sends the background query
        ThemeMode themeMode = ThemeMode.Dark;
        //hoisted so the launch notices render through the themed layer, and the prompters share it so the background probe runs once
        Theme? launchTheme = null;
        IPermissionPrompter? permPrompter;

        //the launch asker must be pump-less, the pump's reader thread does not exist until Repl.RunRichAsync starts it
        IWizardPrompter? launchAsker = null;
        InputPump? pump = null;
        InputDeafnessWatchdog? deafWatch = null;
        //one late-bound bridge from the prompters to the chrome, built only on the rich path and armed once the loop owns the screen
        ChromeHandle? chrome = null;
        //armed by the rich loop and a no-op until then, the plain path has no Esc key to wire it to
        TurnAbortHandle? turnAbort = null;
        //built alongside the prompters so the rich picker shares their surface, pump and chrome, and the -p branch assigns it only for definite assignment
        IListPicker replPicker;
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
        var toolCtx = new AppToolContext(cwd, home, prompter);
        var permissions = PermissionStore.Load(home, cwd, out var permWarning);   //a corrupt permissions file loads as no grants, so every mutating tool prompts, with a warning
        if (permWarning is not null) Console.Error.WriteLine($"! {permWarning}");

        //one shared switch that auto-allows every permission and skips the checkpoint, seeded from the home's wild key unless a .gatto.json up the path forbids it
        var wildForbiddenBy = ProjectFileConfig.EffectiveWildAllowed(cwd, out var forbiddingFile) ? null : forbiddingFile;
        var wildState = new WildState { On = permissions.Wild && wildForbiddenBy is null, ForbiddenBy = wildForbiddenBy };

        //always built and wired, since HookBus has no unregister, and Armed follows the current role so a /role into grounding still nudges
        var groundingGate = new GroundingGate { Armed = comp.Gates.Contains("grounding") };
        tools.Register(groundingGate.RestateTool);   //task_restate grounds the session on a 50% match against the brief, and IsAvailable hides it from a role that is not armed
        hooks.OnToolResult(groundingGate.NudgeToolResultAsync);   //a soft nudge on write and edit results while ungrounded, it never blocks a tool call
        Action resetGrounding = groundingGate.Reset;

        //the session's own /auto, which survives a /role, and only toggleAuto changes it
        var autoOff = args.Auto;
        var checkpointApproval = new CheckpointApproval();   //a single-use latch, so the permission gate passes the one shell call the checkpoint approved and no later one
        var checkpointGate = new CheckpointGate(permPrompter, cmd => RunReadOnlyShell(cmd, cwd), checkpointApproval, wild: wildState)
        {
            Enabled = comp.Checkpoints && !autoOff,   //the current role and the session's /auto decide it, and --yes never stands in for --auto
        };
        hooks.On(HookEvent.ToolCall, checkpointGate.CheckAsync);   //registered before the permission gate, since tool_call handlers run in order and the checkpoint pauses first

        var gate = new PermissionGate(permissions, permPrompter, autoYes: args.Yes, approval: checkpointApproval, wild: wildState, home: home);
        hooks.On(HookEvent.ToolCall, gate.CheckAsync);
        //the wire log is read once here from GATTO_DEBUG_WIRE and never in the request path, so off costs a null field
        var wireLog = WireLog.FromFlag(
            Environment.GetEnvironmentVariable("GATTO_DEBUG_WIRE"),
            WireLog.RunDirectory(home, DateTimeOffset.UtcNow, Environment.ProcessId));
        var client = new OpenAiCompatClient(
            new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) })   //only the connect is bounded, so a black-holed host fails fast while a long local generation runs on
                { Timeout = System.Threading.Timeout.InfiniteTimeSpan },
            endpointName, endpoint, wireLog,
            //ask for progress only from a local llama-server with a known base URL
            returnProgress: endpointName == "local" && launchBaseUrl is not null);
        //a dropped attached image must warn the user, the sink paints a ♯ row in the REPL and stderr for -p
        client.OnImageUnavailable = m => warn.Warn(m);
        //every request on this client feeds the footer's cost and quota, run_agent children included since they share it
        var usageMeter = new UsageMeter(endpoint.Quota);
        client.OnUsage = u => _ = usageMeter.Record(u);
        //a separate client for the probes, untimed like every client, and each probe holds a 2 second deadline of its own
        var probeHttp = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) })
            { Timeout = Timeout.InfiniteTimeSpan };
        var probeDeadline = TimeSpan.FromSeconds(2);
        //the model's window on a local endpoint, the endpoint's on a cloud one, and no enforcement when neither is configured
        var contextBudget = ServingFor(ServedHere(), model, endpoint, launchBaseUrl).Window;
        //cross-check /props at every launch and let the live answer win in both directions (it does not run on the endpoint gatto serves)
        if (!ServedHere() && launchBaseUrl is not null && !cloud)
        {
            var probedCtx = await ServeProbe.ProbeAsync(probeHttp, launchBaseUrl, CancellationToken.None, endpoint.ApiKey, probeDeadline);
            var resolved = ConnectContext.Resolve(contextBudget, probedCtx?.NCtx);
            contextBudget = resolved.Budget;
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
        if (ContextWarning.Compose(contextBudget) is string ctxWarn)
            warn.Warn(ctxWarn);   //a null budget turns off elision and compaction, and a /model cannot reach null since a profile's context is an int
        //a model property, so a cloud endpoint with no model takes the default
        var reasoningHistory = model?.Profile.ReasoningHistory ?? ReasoningHistory.All;

        //launch probe

        //one bounded read of /props through the same DescribeProbe as /model, so launch, /model and the per-turn probe agree on a mismatch
        ServingProbe? launchServing = null;
        //one source for the exit hint and the reuse line, so both agree about what is loaded
        var serverAlreadyUp = false;
        long? servedBytes = null;
        if (args.Prompt is null && ServedHere() && model is not null && launchBaseUrl is not null)   //interactive only, since -p never reads the result, and with no local model or base URL the chip is absent
        {
            //a loading server of ours answers nothing, so wait for it on a live line first, or the launch asks for a second start
            if (Uri.TryCreate(launchBaseUrl, UriKind.Absolute, out var waitUri))
            {
                using var loadingLine = new TickLine(Console.Out, launchTheme is not null, blankBeforeFirst: true);
                await AutoServeAsk.AwaitOursLoadingAsync(
                    new ServeManager(home, ServerBinaryFor(model, config)), waitUri.Port,
                    Gatto.Roles.Audition.AuditionRunner.ReadyBudget,
                    LoadingLine.Over(loadingLine, glyphs, launchTheme), id => SizeWordsOnDiskOrNull(home, id),
                    CancellationToken.None);
            }
            var launchLoaded = await ServeProbe.ProbeAsync(probeHttp, launchBaseUrl, CancellationToken.None, endpoint.ApiKey, probeDeadline);   //the chat client's own key, since /props sits behind the server's api key and a keyed server would read as unknown
            //computed once, so the refusal and the chip cannot end up describing two different servers
            var launchMismatch = ModelSwitch.DescribeProbe(model, launchLoaded, modelsDir);
            launchServing = new ServingProbe(
                launchMismatch?.Name, launchLoaded?.NCtx,
                launchLoaded?.Capability ?? ThinkCapability.None, launchLoaded?.Vision,
                launchMismatch?.OnTheShelf ?? false);

            //an explicit -m against a server holding another model refuses, while a bare gatto opens on what runs and -m X attaches to X
            if (args.Model is not null && launchMismatch is not null)   //the mismatch comes from DescribeProbe, where unknown never counts as one
            {
                Console.Error.WriteLine(ServeNotice.Refusing(launchLoaded?.ModelPath));
                return 1;
            }

            //say once that gatto is reusing what it found, so a user who left a server up knows nothing started behind their back
            if (launchLoaded is not null)
            {
                serverAlreadyUp = true;
                servedBytes = Gatto.Core.Acquire.ModelDiscovery.SetBytesOrNull(launchLoaded.ModelPath);   //every input is observed, the served file's size and serve.json for the age, and a server gatto did not start has no age
                //a server already up is a normal event, so it is said through SayReusing rather than the warning sink
                ServeNotice.SayReusing(
                    new CliSurface(Console.Out, launchTheme, glyphs), glyphs,
                    launchLoaded.ModelPath,
                    new ServeManager(home, config.LlamaServer ?? "").DescribeRunning()?.Started,
                    DateTimeOffset.Now,
                    servedBytes);
            }
        }

        //non-interactive has no prompter to ask with and the table refuses on its own, so a scripted run cannot spawn a server
        var autoServe = AutoServe.Decide(
            interactive: args.Prompt is null,
            //the one rule for whether gatto serves this endpoint, a folder on another endpoint never starts a server
            gattoServesThisEndpoint: ServingFor(ServedHere(), model, endpoint, launchBaseUrl).GattoServes,
            serverAnswered: serverAlreadyUp,
            consent: model?.Profile.AutoServe,
            //the launch asker is pump-less, the /model add site keeps prompter where the pump is live
            canAsk: launchAsker is not null);

        //the single caller, reached on the interactive launch only, so -p can never spawn a process
        if (autoServe is AutoServeAction.Serve && model is not null)   //an absent auto_serve means yes with nothing asked, and false still vetoes in the decision above
        {
            var manager = new ServeManager(home, ServerBinaryFor(model, config));
            //the start is watched on a live line, rich only under a theme. a plain launch gets the same rungs as committed lines, and a warning takes the line down first.
            using var startingLine = new TickLine(Console.Out, launchTheme is not null, blankBeforeFirst: true);
            //measured before the wait, so the last rung can name the size while the wait is still running
            var loadingBytes = Gatto.Core.Acquire.ModelDiscovery.SetBytesOrNull(model.Profile.ActivePath);
            serverAlreadyUp = await AutoServeAsk.StartAndWaitAsync(
                manager, model, m => { startingLine.Finish(); warn.Warn(m); }, CancellationToken.None,
                glyphs, LoadingLine.Over(startingLine, glyphs, launchTheme),
                loadingBytes is { } b ? SizeWords.Gb(b, approx: true) : null);
            if (serverAlreadyUp) servedBytes = loadingBytes;
        }

        //read after any start so -p and an auto-served launch both see the server, and only a server holding this model speaks for its window
        if (ServedHere() && model is not null && launchBaseUrl is not null && !cloud)
        {
            var served = await ServeProbe.ProbeAsync(probeHttp, launchBaseUrl, CancellationToken.None, endpoint.ApiKey, probeDeadline);
            if (served is not null && ModelSwitch.DescribeProbe(model, served, modelsDir) is null)
            {
                var window = ModelWindow.Resolve(model.Profile.Context, served.NCtx);
                contextBudget = window.Budget;
                if (window.Line is string windowLine) warn.Warn(windowLine);
            }
        }

        //a closure so the Repl never references Gatto.Roles, capturing the same mutable model and base URL so a /model switch is picked up
        Func<ServingProbe> probeServing = () =>
        {
            if (launchBaseUrl is null || cloud) return new ServingProbe(null, null);
            //a session gatto does not serve reads only the server's vision bit, since there is no served model to compare
            if (!ServedHere() || model is null)
            {
                var connectLoaded = ServeProbe.ProbeAsync(probeHttp, launchBaseUrl, CancellationToken.None, endpoint.ApiKey, probeDeadline)
                    .GetAwaiter().GetResult();
                return new ServingProbe(null, connectLoaded?.NCtx, ThinkCapability.None, connectLoaded?.Vision);   //an unanswered probe leaves Vision null, which is unknown, so the refusal on false lets the attach go
            }
            var loaded = ServeProbe.ProbeAsync(probeHttp, launchBaseUrl, CancellationToken.None, endpoint.ApiKey, probeDeadline)
                .GetAwaiter().GetResult();
            var named = ModelSwitch.DescribeProbe(model, loaded, modelsDir);
            return new ServingProbe(named?.Name, loaded?.NCtx,
                loaded?.Capability ?? ThinkCapability.None, loaded?.Vision,
                named?.OnTheShelf ?? false);
        };

        //extension commit

        //every extension that committed, in discovery order, and a tool-less one is a legal load with no tool row
        var (committedExt, committedExtensions) = ExtensionHost.Commit(loadedExtensions, tools, hooks, extDiag);
        //the early pass should have skipped it already, and a launch must not run on the endpoint of an extension that did not commit
        if (endpointOwner.TryGetValue(endpointName, out var owner) && !committedExtensions.Any(e => e.Name == owner))
        {
            Console.Error.WriteLine($"endpoint '{endpointName}' comes from extension '{owner}', which was skipped");
            return 2;
        }
        //a read-class claim counts only from a vetted shipped extension, and an unvetted one still prompts
        ExtensionHost.GrantVettedReadClass(committedExt, ShippedExtensions.IsVetted, gate.AllowReadClass);   //keyed on the owning extension, so a colliding tool from an unvetted one gets nothing

        //recompose once here, so the first turn's prompt already has the policy block
        foreach (var ext in committedExtensions)
            if (ext.Registrations.Policy is { } pol) policyLines.Add((ext.Name, pol));
        if (policyLines.Count > 0)
        {
            //both locals hold a composed value from before the extensions loaded, so both are reassigned here
            launchRes = ResolveRole(args.Role);
            comp = launchRes.Comp;
            lastComposedSystem = comp.SystemText;
            lastComposedSources = comp.Sources;
        }

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
        { Armed = comp.Gates.Contains("citations") };
        hooks.OnToolResult(citationGate.OnToolResultAsync);   //always registered and after the grounding nudge, so it sees the nudged result and a /role can still arm it

        //run_agent: a definition may only name the six core built-ins, so a sub-agent's registry can never hold run_agent, ask_user or an extension tool
        var agentDefs = AgentDefinitions.LoadAll(
            Path.Combine(home, "agents"),
            Path.Combine(cwd, ".gatto", "agents"),
            SubagentWiring.Builtins().Keys.ToHashSet(StringComparer.Ordinal),
            m => warn.Warn($"agent: {m}"));
        //a one-slot holder the Repl fills once its renderer exists, and on -p it stays empty so stdout stays byte-pure
        var subagentProgress = new Action<string>?[1];
        var runAgent = new RunAgentTool(
            definitions: () => agentDefs,
            subagentWiring: SubagentWiring.Build(gate, diag: warn.Warn),   //a fresh registry per agent over the shared gate, which tags each of its prompts with the agent's name
            client: client,
            requestSettings: () => (modelString, comp.Sampling, comp.ThinkingBody),
            nudgeAppend: model?.Nudges?.Append ?? "",
            parentCtx: toolCtx,
            progress: m => subagentProgress[0]?.Invoke(m),
            contextBudget: contextBudget,
            reasoningHistory: reasoningHistory,
            marks: CommandBanner.MarksFor(home));
        tools.Register(runAgent);   //main registry only, so a sub-agent cannot call run_agent (depth 1)

        var loop = new AgentLoop(client, tools, hooks, toolCtx, modelString, comp.Sampling, comp.ThinkingBody, contextBudget,
            promptSuffix: comp.ThinkingSuffix,
            reasoningHistory: reasoningHistory);

        //one sequence per composition this process makes, so every system message the session stores reads a higher number than the one before
        var baselineSeq = 0;
        ThinkingMark ThinkingOf(Composition c) => new(c.Thinking.ToString().ToLowerInvariant(), c.ThinkingBody?.GetRawText());
        //the thinking is the one in force, which the effort switch and the toggle move away from what a fresh composition picks
        SessionBaseline NextBaseline(BaselineSources sources) => new(++baselineSeq, role.Name, modelString,
            reasoningHistory == ReasoningHistory.None ? "none" : "all", ThinkingOf(comp), BaselineMarks.ToolsOf(tools.Specs()), sources, endpointName);

        //hoisted above switchRole and switchModel, so their rebuilds read the toggle state and the footer keeps the capability
        var reasoningMap = model?.Profile.Thinking ?? endpoint.Thinking;
        var binaryOn = Gatto.Roles.Thinking.BinaryOnLevel(reasoningMap);   //the map's single on-level, for a Toggle model only
        var thinkCap = Gatto.Roles.Thinking.CapabilityOf(reasoningMap, launchServing?.Capability ?? ThinkCapability.None);   //the map decides: one on-level is a Toggle, two or more are Levels, and no map falls to the template sniff

        //a Toggle model stores its choice in the same ModelDefaultEffort slot, on as the map's on-level and off as None
        bool DefaultThinkOn(Model? m) =>
            m?.Profile.DefaultEffort is { } persisted ? persisted != ThinkingLevel.None : config.Think != "off";
        var thinkOn = DefaultThinkOn(model);

        //the seam /effort, /role and /model drive: map bodies when there is a map, the generic enable_thinking kwarg otherwise
        (JsonElement? Body, string? Suffix) ToggleEntry(bool on) => binaryOn is { } onLevel
            ? Gatto.Roles.Thinking.ToggleBody(reasoningMap, onLevel, on)   //map bodies, and ToggleBody guards an empty on-body
            : (ThinkCapabilitySniff.EnableThinkingBody(on), null);
        string ApplyToggle(bool on)
        {
            var (body, suffix) = ToggleEntry(on);
            comp = comp with { ThinkingBody = body, ThinkingSuffix = suffix };
            loop.UpdateOverrides(modelString, comp.Sampling, comp.ThinkingBody, comp.ThinkingSuffix);
            return "thinking " + (on ? "on" : "off");
        }

        //the one source for the footer's thinking slot, so a rebuild cannot disagree with the model's real capability
        string? ThinkingFooterName() =>
            thinkCap == ThinkCapability.Toggle
                ? "thinking " + (thinkOn ? "on" : "off")
                : (model?.Profile.Thinking ?? endpoint.Thinking) is null ? null : EffortSwitch.Name(comp.Thinking);

        //unavailable only when thinkCap is None or AlwaysOn and no map is configured (a degenerate map can still offer a level)
        bool ThinkingUnavailable() =>
            thinkCap is ThinkCapability.None or ThinkCapability.AlwaysOn
            && (model?.Profile.Thinking ?? endpoint.Thinking) is null;

        //re-walks the context files and the memory index on each call, so /new, /compact and auto-compaction see edits and no ordinary turn does
        Func<ComposedSystem> recomposeSystem = () =>
        {
            IReadOnlyList<(string Path, string Content)> contextFiles;
            try
            {
                //first, so a throw here leaves memory untouched and the degrade cannot half-apply a write
                contextFiles = ContextFiles.Collect(
                    cwd, config.ContextCompat, config.ContextHome ? Path.Combine(home, "GATTO.md") : null);
            }
            catch (GattoConfigException ex)   //never throws: a bad .gatto.json warns and hands back the last prefix that composed, since /new is how an edit is picked up
            {
                warn.Warn($"context files not re-read: {ex.Message}");
                return new ComposedSystem(lastComposedSystem, NextBaseline(lastComposedSources));
            }

            var memory = LoadMemory(model);
            //always call it, the zero case clears the latch so a later truncation is news again
            if (memoryNag.ShouldWarn(MemoryBudget(model), memory.TruncatedLines))
                warn.Warn(MemoryTruncationWarning(memory.TruncatedLines));
            var composed = RoleComposition.Compose(
                role, model, contextFiles,
                endpoint.Thinking, cwd: cwd, date: DateTime.Now,
                memoryIndex: memory.Text, memoryTruncatedLines: memory.TruncatedLines, memoryNudge: memoryOn,
                policyLines: policyLines);
            lastComposedSystem = composed.SystemText;
            lastComposedSources = composed.Sources;
            return new ComposedSystem(lastComposedSystem, NextBaseline(composed.Sources));
        };

        //a closure so Repl never references Gatto.Roles, and null means the current role has no checkpoints to toggle
        Func<bool?> toggleAuto = () =>
        {
            if (!comp.Checkpoints) return null;   //flipping autoOff here would arm a checkpoint early for the next role that wants one
            autoOff = !autoOff;   //the shared flag, so every later /role reads the same session intent
            checkpointGate.Enabled = comp.Checkpoints && !autoOff;
            return checkpointGate.Enabled;
        };

        //re-runs the launch precedence and mutates the launch locals in place, so recomposeSystem sees the new role at the next /new
        Func<string, RoleSwitchResult> switchRole = requestedName =>
        {
            RoleResolution res;
            try { res = ResolveRole(requestedName); }
            catch (GattoConfigException ex) { return new RoleSwitchResult(false, "", null, null, ex.Message); }

            if (!string.Equals(res.Endpoint.BaseUrl, launchBaseUrl, StringComparison.OrdinalIgnoreCase))   //the client's base URL is fixed at construction, so a role on another URL is refused or it would talk to the old server
                return new RoleSwitchResult(false, "", null, null,
                    $"role '{res.Role.Name}' resolves to {res.Endpoint.BaseUrl ?? "(no base_url)"}, but this " +
                    $"session is connected to {launchBaseUrl}. Restart gatto to switch to a different " +
                    "endpoint or a model served on a different address");

            //a pinned local base_url lets two models share one URL, and llama-server ignores the request's model field, so a different model is refused
            if (endpointName == "local" &&
                !string.Equals(res.Model?.Id, model?.Id, StringComparison.OrdinalIgnoreCase))
                return new RoleSwitchResult(false, "", null, null,
                    res.Model is null   //a cloud endpoint whose base_url matches the pinned local one, refused with an endpoint message since no model can be named
                        ? $"role '{res.Role.Name}' targets endpoint '{res.EndpointName}', but this session is " +
                          $"serving local model '{model!.Id}'. Restart gatto to switch endpoints"
                        : $"role '{res.Role.Name}' uses model '{res.Model.Id}', but this session is serving " +
                          $"'{model!.Id}'. Restart gatto to switch models");

            role = res.Role; model = res.Model; endpoint = res.Endpoint;
            endpointName = res.EndpointName; modelString = res.ModelString; comp = res.Comp;
            lastComposedSystem = comp.SystemText;   //updated here, the degrade target must follow the newly armed role
            lastComposedSources = comp.Sources;
            //the newly armed model may have a tighter memory.index_budget, so warn here rather than stay silent
            if (memoryNag.ShouldWarn(MemoryBudget(model), res.MemoryTruncatedLines))
                warn.Warn(MemoryTruncationWarning(res.MemoryTruncatedLines));
            loop.UpdateOverrides(modelString, comp.Sampling, comp.ThinkingBody, comp.ThinkingSuffix);

            //the session's own auto setting wins, /role must not re-arm checkpoints the user turned off
            checkpointGate.Enabled = comp.Checkpoints && !autoOff;
            groundingGate.Armed = comp.Gates.Contains("grounding");
            if (groundingGate.Armed) groundingGate.Reset();   //rearmed for the new role, its freshly composed prompt must be restated
            //armed for the new role, the ledger has been recording upstream all along so a switch never starts blind
            citationGate.Armed = comp.Gates.Contains("citations");

            //recompose overwrote the toggle body, so re-apply the session's on/off choice after a /role
            if (thinkCap == ThinkCapability.Toggle) ApplyToggle(thinkOn);
            return new RoleSwitchResult(true, role.Name, modelString, comp.SystemText, null,
                ThinkingFooterName(), NextBaseline(comp.Sources));
        };

        //set once setEffort exists below, so a switch on a listed endpoint can apply the new model's level through the same path
        Func<string, bool, EffortResult>? applyEffort = null;

        //arms a model and leaves the role's gates alone, the decision is in ModelSwitch.Decide and the side effects here only on success
        Func<string, bool, ModelSwitchResult> switchModel = (requestedModel, confirmed) =>
        {
            //an endpoint that names its models has no server to swap, so the switch is the name the next request carries
            var route = ModelSwitch.RouteOf(ServedHere(), endpoint.Models is { Count: > 0 }, cloud);
            if (route == SwitchRoute.ByName)
            {
                if (endpoint.Models is { Count: > 0 } offered && !offered.Contains(requestedModel, StringComparer.Ordinal))
                    return new ModelSwitchResult(false, requestedModel, null, null,
                        $"endpoint {endpointName} has no model '{requestedModel}'. Models: {string.Join(", ", offered)}", null, null, null);
                //re-resolved for the new name, so its folder's parts replace the old one's and a model with no folder drops them
                var previousSession = sessionModel;
                sessionModel = requestedModel;
                RoleResolution switched;
                try { switched = ResolveRole(role.Name); }
                catch (GattoConfigException ex)
                {
                    sessionModel = previousSession;
                    return new ModelSwitchResult(false, requestedModel, null, null, ex.Message, null, null, null);
                }
                var systemBefore = lastComposedSystem;
                role = switched.Role; model = switched.Model; modelString = switched.ModelString; comp = switched.Comp;
                lastComposedSystem = comp.SystemText;
                lastComposedSources = comp.Sources;
                //re-derived for the new model by the launch's own expression, or a switch between shapes keeps the old map's toggle or levels
                reasoningMap = model?.Profile.Thinking ?? endpoint.Thinking;
                binaryOn = Gatto.Roles.Thinking.BinaryOnLevel(reasoningMap);
                thinkCap = Gatto.Roles.Thinking.CapabilityOf(reasoningMap, launchServing?.Capability ?? ThinkCapability.None);
                if (thinkCap == ThinkCapability.Toggle) { thinkOn = DefaultThinkOn(model); ApplyToggle(thinkOn); }
                else
                {
                    //the new model takes its own level as a launch would, never the one the previous model was on
                    var switchedLevel = role.ThinkingRequested ?? SavedCloudEffortOrNull(requestedModel)
                        ?? model?.Profile.DefaultEffort ?? ThinkingLevel.Medium;
                    applyEffort?.Invoke(EffortSwitch.Name(switchedLevel), false);
                }
                loop.UpdateOverrides(modelString, comp.Sampling, comp.ThinkingBody, comp.ThinkingSuffix);
                //the system text changes only when a folder's parts came or went, else only the baseline moves so a later continue finds the model the session ended on
                return new ModelSwitchResult(true, requestedModel,
                    string.Equals(systemBefore, comp.SystemText, StringComparison.Ordinal) ? null : comp.SystemText,
                    contextBudget, null, null, null,
                    ThinkingFooterName(), thinkCap == ThinkCapability.Toggle, ThinkingUnavailable(), NextBaseline(lastComposedSources));
            }

            //refused here as well as at the Repl's gate, so a remote endpoint never reaches the serving machinery below
            if (route == SwitchRoute.Refuse)
                return new ModelSwitchResult(false, requestedModel, null, null, UnmanagedSession.ModelUnavailable(launchBaseUrl), null, null, null);

            IReadOnlyList<(string Path, string Content)> contextFiles;
            //a malformed .gatto.json fails the switch and leaves the session as it was, nothing is armed yet
            try
            {
                contextFiles = ContextFiles.Collect(
                    cwd, config.ContextCompat, config.ContextHome ? Path.Combine(home, "GATTO.md") : null);
            }
            catch (GattoConfigException ex)
            {
                return new ModelSwitchResult(false, requestedModel, null, null, ex.Message, null, null, null);
            }

            //the index is re-read against the model being switched to, so the load is a delegate rather than a value read up front
            var outcome = ModelSwitch.Decide(
                modelsDir, home, requestedModel, role, contextFiles, endpoint.Thinking, launchBaseUrl, cwd,
                loadMemory: LoadMemory,
                memoryNudge: memoryOn);
            if (!outcome.Success || outcome.Model is null || outcome.Composition is null)
                return new ModelSwitchResult(false, outcome.ModelId, outcome.SystemText, outcome.ContextBudget,
                    outcome.Message, null, null, null);   //no probe on a failed switch, nothing changed

            //captured before the swap, since model still names the outgoing one and the deed's confirm and failure message name it
            var outgoing = model;
            var serveManager = new ServeManager(home, ServerBinaryFor(outgoing, config));

            //a delegate rather than the prompter, so the deed does not know which face answers (a driver can make it throw)
            Func<Gatto.Repl.WizardAsk, string?>? deedAsk =
                prompter is Gatto.Repl.IWizardPrompter deedAsker
                    ? a => Label(deedAsker.AskOne(a))
                    : null;

            //one delegate, the starter is called twice (incoming and restore) and the restore path must stay fakeable
            var deed = new ModelSwitchDeed.Seams(
                Swap: (served, to) =>
                {
                    var (back, cannotRestore) = RestoreTarget(modelsDir, served);
                    return SwapServerAsync(serveManager, back, to, warn.Warn, glyphs, cannotRestore)
                        .GetAwaiter().GetResult();
                },
                Start: m => AutoServeAsk.StartAndWaitAsync(serveManager, m, warn.Warn, CancellationToken.None, glyphs)
                    .GetAwaiter().GetResult());

            if (ModelSwitchDeed.Run(outcome, outgoing, serveManager,   //the serving change, then the persist, then the arm, in that order
                    deedAsk, confirmed, warn.Warn, deed) is { } settled)   //confirmed means the wizard's done step already asked, and it reaches only the deed's swap confirm
                return settled;

            //read outcome.Model here, the variable still names the outgoing model until the assignments below
            if (memoryNag.ShouldWarn(MemoryBudget(outcome.Model), outcome.MemoryTruncatedLines))
                warn.Warn(MemoryTruncationWarning(outcome.MemoryTruncatedLines));
            model = outcome.Model;
            comp = outcome.Composition;
            lastComposedSystem = comp.SystemText;   //updated, the degrade target must follow the newly armed model
            lastComposedSources = comp.Sources;
            modelString = outcome.Model.Id;
            contextBudget = outcome.Model.Profile.Context;
            reasoningHistory = outcome.Model.Profile.ReasoningHistory;
            //the switch arms the session's model, so a later /role sees it instead of the launch-time -m
            sessionModel = outcome.Model.Id;

            loop.UpdateOverrides(modelString, comp.Sampling, comp.ThinkingBody, comp.ThinkingSuffix);
            loop.UpdateContextBudget(contextBudget);
            loop.UpdateReasoningHistory(reasoningHistory);
            runAgent.UpdateContextBudget(contextBudget);
            runAgent.UpdateReasoningHistory(reasoningHistory);

            //only after the switch is committed, a failed load or persist never reaches this line
            var loaded = ServeProbe.ProbeAsync(probeHttp, launchBaseUrl!, CancellationToken.None, endpoint.ApiKey, probeDeadline)   //only after the persist succeeded, so a doomed switch spends no round trip
                .GetAwaiter().GetResult();
            var mismatch = ModelSwitch.DescribeProbe(outcome.Model, loaded, modelsDir)?.Name;
            //the incoming model's server may split its context across slots too, the launch rule again
            if (loaded is not null && mismatch is null)
            {
                var window = ModelWindow.Resolve(outcome.Model.Profile.Context, loaded.NCtx);
                contextBudget = window.Budget;
                loop.UpdateContextBudget(contextBudget);
                runAgent.UpdateContextBudget(contextBudget);
                if (window.Line is string windowLine) warn.Warn(windowLine);
            }

            //re-derived for the new model, or a switch between shapes would re-apply the old map's toggle body over the new one
            reasoningMap = model.Profile.Thinking ?? endpoint.Thinking;
            binaryOn = Gatto.Roles.Thinking.BinaryOnLevel(reasoningMap);
            //a failed probe leaves a model with no map inert, a model with a map is decided by the map either way
            thinkCap = Gatto.Roles.Thinking.CapabilityOf(reasoningMap, loaded?.Capability ?? ThinkCapability.None);   //before the ApplyToggle below, and a map decides the capability whatever the server's template says
            //the incoming model's own persisted default decides what thinkOn starts as
            if (thinkCap == ThinkCapability.Toggle) { thinkOn = DefaultThinkOn(model); ApplyToggle(thinkOn); }

            //adds a line on success, since outcome.Message is null there, and outgoing names the model the session was on
            return new ModelSwitchResult(true, outcome.ModelId, outcome.SystemText, contextBudget,
                SwapConfirm.SwitchNotice(confirmed, outgoing?.Id, outcome.ModelId) ?? outcome.Message,
                mismatch, loaded?.NCtx, ThinkingFooterName(), thinkCap == ThinkCapability.Toggle,
                ThinkingUnavailable(), NextBaseline(comp.Sources));
        };

        //read from disk on each open, since the launch snapshot would miss a default written this session or keep one whose write failed
        string? CurrentDefaultFor(string onEndpoint)
        {
            try { return GattoConfig.Load(home).ModelDefaultFor(onEndpoint); }
            catch (GattoConfigException) { return null; }   //a config that will not parse marks no default, and the rows still list
        }

        //the rows come ready-made since Repl cannot enumerate models, the loaded mark from the probe and current from the armed model
        Func<IReadOnlyList<PickerItem>> listModels = () =>
        {
            //an endpoint that names its models lists those, with nothing to probe
            if (!ServedHere() && endpoint.Models is { Count: > 0 } offered)
                return ListedModelRows.Build(offered, modelString, CurrentDefaultFor(endpointName));

            var loaded = launchBaseUrl is null
                ? null
                : ServeProbe.ProbeAsync(probeHttp, launchBaseUrl, CancellationToken.None, endpoint.ApiKey, probeDeadline)
                    .GetAwaiter().GetResult();

            //read once per open, so no two rows can disagree about the default
            var currentDefault = CurrentDefaultFor("local");

            var rows = new List<PickerItem>();
            foreach (var id in Model.ListIds(modelsDir))
            {
                Model p;
                try { p = Model.Load(modelsDir, id); }
                catch (GattoConfigException) { continue; }   //a model that will not load is not offered

                var ctx = p.Profile.Context >= 1024
                    ? (p.Profile.Context / 1024) + "k"
                    : p.Profile.Context.ToString();
                var label = $"{id,-16} {ctx,6}   {Path.GetFileNameWithoutExtension(p.Profile.ActivePath)}";

                rows.Add(new PickerItem(id, label,
                    Marked: Model.MatchesLoaded(p, loaded) == true,   //only a known match marks the row, unknown stays unmarked
                    Current: string.Equals(id, model?.Id, StringComparison.OrdinalIgnoreCase),
                    //read from disk, so a session that switched models shows the two marks on different rows
                    Default: string.Equals(id, currentDefault, StringComparison.OrdinalIgnoreCase)));
            }
            return rows;
        };

        //the port switchModel's guard checks, else 1235 when the session has no base URL to parse
        var sessionPortForScaffold = launchBaseUrl is not null
            && Uri.TryCreate(launchBaseUrl, UriKind.Absolute, out var launchUri)
            ? launchUri.Port
            : 1235;

        //hoisted above switchRole so those closures can read them

        //read fresh, since a level saved this session must show, and a file that will not load saves nothing to read
        ThinkingLevel? SavedCloudEffortOrNull(string modelId)
        {
            try { return SavedCloudEffort(GattoConfig.Load(home), endpointName, modelId); }
            catch (GattoConfigException) { return null; }
        }

        //the level a picker marks as default, the local profile's or the endpoint entry's for the model in use
        ThinkingLevel? SavedEffortNow() => ServedHere() && model is not null ? model.Profile.DefaultEffort : SavedCloudEffortOrNull(modelString);

        //the level goes into the local model's profile.json, or with no local model into the endpoint's entry under the model in use
        string PersistEffort(ThinkingLevel level)
        {
            try
            {
                if (ServedHere() && model is not null)   //a folder used on another endpoint is never edited from there, its effort is saved per endpoint
                {
                    ModelDefaultEffort.Set(modelsDir, model.Id, level);
                    model = model with { Profile = model.Profile with { DefaultEffort = level } };   //so the picker's default mark does not lag the write
                    return $", saved as {model.Id}'s default";
                }
                GattoConfigWriter.SetEndpointEffort(home, endpointName, modelString, EffortSwitch.Name(level));
                return $", saved as {modelString}'s default";
            }
            catch (GattoConfigException ex) { return $", not saved: {ex.Message}"; }
        }

        //re-resolves the thinking entry at the clamped level and mutates comp, so the footer and recompose read it, and a /role resets it
        Func<string, bool, EffortResult> setEffort = (levelArg, persist) =>
        {
            try
            {
                if (ThinkingUnavailable())   //a model with no reasoning switch refuses, there is nothing to apply or save
                    return new EffortResult(false, null, "this model doesn't support changing the reasoning mode.");

                if (thinkCap == ThinkCapability.Toggle)
                {
                    //a Toggle model takes any argument: none or off means off, anything else means on
                    var (on, toggleName) = EffortSwitch.OnToggle(levelArg);
                    thinkOn = on;
                    var toggleMessage = $"effort: {toggleName}";
                    //the map's own on-level when there is a map, Max when there is none, and DefaultThinkOn only asks whether the level is None
                    toggleMessage += persist
                        ? PersistEffort(on ? (binaryOn ?? ThinkingLevel.Max) : ThinkingLevel.None)
                        : ", only for this session";
                    var toggled = ApplyToggle(on);
                    return new EffortResult(true, toggled, toggleMessage, ThinkingOf(comp));
                }

                var cap = model?.Nudges?.ThinkingCap ?? ThinkingLevel.Max;
                var (effective, name, message) = EffortSwitch.Resolve(levelArg, cap, model?.Id);
                var map = model?.Profile.Thinking ?? endpoint.Thinking;

                //nothing is ever sent with no map, so the level name stays null and the footer chip hidden
                if (map is null)
                {
                    comp = comp with { Thinking = effective, ThinkingBody = null, ThinkingSuffix = null };
                    loop.UpdateOverrides(modelString, comp.Sampling, comp.ThinkingBody, comp.ThinkingSuffix);
                    return new EffortResult(true, null,
                        $"effort: {name}, display only: no thinking map configured", ThinkingOf(comp));
                }

                //the line and the chip report the level the map actually resolves to, and a null result is display-only like no map
                var landed = Thinking.LandedLevel(map, effective);
                if (landed is null)
                {
                    comp = comp with { Thinking = effective, ThinkingBody = null, ThinkingSuffix = null };
                    loop.UpdateOverrides(modelString, comp.Sampling, comp.ThinkingBody, comp.ThinkingSuffix);
                    return new EffortResult(true, null,
                        $"effort: {name}, display only: no thinking map entry at or below this level", ThinkingOf(comp));
                }

                var (body, suffix) = Thinking.ResolveEntry(map, effective);
                var landedName = EffortSwitch.Name(landed.Value);
                comp = comp with { Thinking = landed.Value, ThinkingBody = body, ThinkingSuffix = suffix };
                loop.UpdateOverrides(modelString, comp.Sampling, comp.ThinkingBody, comp.ThinkingSuffix);

                //a level the map moved says so and names the level asked of the map, and one it left keeps the cap message
                var landedMessage = landed.Value == effective
                    ? message
                    : $"effort: {landedName}, requested {name}, the nearest level this model declares";   //when the cap and the map both move it, only the map's move is reported, and the chip still matches what was sent

                //persist the resolved level, the s key and a switch's own apply are the session-only ways
                landedMessage += persist ? PersistEffort(landed.Value) : ", only for this session";

                return new EffortResult(true, landedName, landedMessage, ThinkingOf(comp));
            }
            catch (GattoConfigException ex) { return new EffortResult(false, null, ex.Message); }
        };

        //one row per level this model's map declares, or none and on for a Toggle, and empty when there is nothing to offer
        Func<IReadOnlyList<PickerItem>> listEfforts = () =>   //built fresh on each open, so a /model shows at the next /effort
        {
            if (thinkCap == ThinkCapability.Toggle)
                return new List<PickerItem>
                {
                    new("none", "none", Marked: !thinkOn, Current: !thinkOn,
                        Default: SavedEffortNow() == ThinkingLevel.None),
                    //any persisted level other than None means on was saved, a map-less model stores the Max sentinel
                    new("on", "on", Marked: thinkOn, Current: thinkOn,
                        Default: SavedEffortNow() is { } d && d != ThinkingLevel.None),
                };

            //no map means no rows, and a degenerate map with a lone on-level still has rows to show
            if ((model?.Profile.Thinking ?? endpoint.Thinking) is not { } effortMap) return Array.Empty<PickerItem>();

            var savedLevel = SavedEffortNow();
            return effortMap.Keys
                .Select(Thinking.Parse)
                .OrderBy(l => l)
                .Select(l =>
                {
                    var levelName = EffortSwitch.Name(l);
                    return new PickerItem(levelName, levelName,
                        Marked: l == comp.Thinking, Current: l == comp.Thinking,
                        Default: savedLevel == l);
                })
                .ToList();
        };

        applyEffort = setEffort;

        //always applied, otherwise a binary map resolves through the role default and the footer lies about thinking on
        if (thinkCap == ThinkCapability.Toggle)
            ApplyToggle(thinkOn);

        //resolved once, every downstream read must use this same path or the transcript and the context disagree
        string? continuePath = null;
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

        //a replay keeps the thinking the session was sent with, through the effort switch so the loop, the footer and the children's GATTO_EFFORT agree
        void ResumeThinking(Conversation resumed, ThinkingMark stored)
        {
            var level = thinkCap == ThinkCapability.Toggle
                ? (stored.BodyJson == ThinkingMark.CompactJson(ToggleEntry(true).Body?.GetRawText()) ? "on" : "none")
                : stored.Level;
            if (level is null) return;
            if (setEffort(level, false).Thinking is { } applied) resumed.SetThinking(applied);
            Environment.SetEnvironmentVariable("GATTO_EFFORT", EffortSwitch.Name(comp.Thinking));
        }

        //the model's own instructions as the composition joins them, for the note that says they changed
        string? ModelAppendText()
        {
            var parts = new[] { model?.SystemAppend, model?.Nudges?.Append }.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
            return parts.Count == 0 ? null : string.Join("\n\n", parts);
        }

        ResumeDecision? resumeDecision = null;
        ResumeDelta? resumeDelta = null;
        string? resumeLine = null;
        var convo = new Conversation(comp.SystemText, baseline: NextBaseline(comp.Sources));
        convo.SetGattoRole(role.Name);   //the launch role name, so messages from before a /role replay with their own tint
        string? resumedFrom = null;
        if (continuePath is { } contPath && sessions.LoadPath(contPath) is { } prior)
        {
            //reload the prior transcript without its stored system message, the decision below picks the one that leads
            convo.Load(prior.Where(m => m.Role != "system"));

            var priorSystem = prior.FirstOrDefault(m => m.Role == "system")?.Content;
            var stored = SessionStore.LoadBaseline(contPath);
            resumeDecision = ResumePlan.Decide(stored, convo.Baseline!, folderExists: !folderGone, effortFlagGiven: args.Effort is not null);
            if (resumeDecision.Reset is null && priorSystem is not null && stored is not null)
            {
                //a replay sends the stored text byte for byte, its compaction block included, so the server finds the prefix it holds
                convo.ReplaceSystem(priorSystem, stored);
                baselineSeq = stored.Seq;
                if (args.Effort is null && stored.Thinking != ThinkingOf(comp)) ResumeThinking(convo, stored.Thinking);
                //what changed since the model last saw the sources rides the first message the user sends
                resumeDelta = ResumePlan.Delta(ResumePlan.EffectiveSources(stored, prior), comp.Sources);
                if (ResumeUpdate.Text(resumeDelta, role.Append, ModelAppendText()) is { } updateText)
                    convo.SetPendingUpdate(new SessionUpdate(updateText, comp.Sources, stored.Seq));
            }
            else
            {
                //a reset leads with the launch composition, one sequence above the stored one
                baselineSeq = (stored?.Seq ?? 0) + 1;
                var fresh = convo.Baseline! with { Seq = baselineSeq };
                //the last marker, a GATTO.md that documents it can put an earlier one in the system prompt
                var idx = priorSystem?.LastIndexOf(Compactor.ContextMarker, StringComparison.Ordinal) ?? -1;
                //re-append the compaction block, the fresh system dropped it and the model resumes with no context behind the lead
                convo.ReplaceSystem(idx >= 0 ? convo.Messages[0].Content + "\n\n" + priorSystem![idx..] : convo.Messages[0].Content!, fresh);
            }

            //restore from the sidecar beside the resumed file, they stay in memory and a new save writes its own
            ledger.RestoreFrom(Path.Combine(
                Path.GetDirectoryName(contPath)!,
                Path.GetFileNameWithoutExtension(contPath) + ".ledger.jsonl"));
            resumedFrom = SessionStamp(contPath);

            //printed on stdout, or on stderr under -p where stdout is model text by contract
            (args.Prompt is null ? Console.Out : Console.Error)
                .WriteLine($"gatto: session {Path.GetFullPath(contPath)}");

            //computed once from the decision and the update that was set, never from a second comparison
            resumeLine = ResumeUpdate.Line(resumeDecision!, convo.PendingUpdate is null ? null : resumeDelta, goneFolder, cwd);
            if (args.Prompt is not null && resumeLine is not null) Console.Error.WriteLine(resumeLine);
        }

        if (args.Prompt is not null)   //the -p one-shot path
        {
            //stderr only, the -p contract keeps stdout for model text
            if (WildNotice(permissions, wildState) is { } wildLine) Console.Error.WriteLine(wildLine);
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
                    : new OneShotCompaction(client, modelString, loop, convo, contextBudget, oneShotUsage, sessions, recomposeSystem, glyphs.Dot),
                oneShotUsage, oneShotArmedAt);
            try
            {
                var r = await loop.RunTurnAsync(convo, args.Prompt, new OneShotObserver(), oneShotCts.Token);
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
            }
        }

        //stdout is not byte-pure here, so the notice goes before the Repl banner
        if (WildNotice(permissions, wildState) is { } wildNotice)
            Console.WriteLine(wildState.On ? $"{wildNotice} {glyphs.Dot} /wild to turn off" : wildNotice);

        //the one helper /role and /model also read, so a rebuild matches the launch footer
        string? initialThinkingName = ThinkingFooterName();

        //the same registry the loop advertises from, so /tools describes this session, and committedExt adds which script owns each tool
        var toolOrigins = committedExt.ToDictionary(c => c.Name, c => c.Extension.Name, StringComparer.Ordinal);
        Func<IReadOnlyList<Gatto.Repl.ToolInfo>> listTools = () => tools.Specs()
            .Select(s => new Gatto.Repl.ToolInfo(s.Name, s.Description, toolOrigins.GetValueOrDefault(s.Name)))
            .ToList();
        //the same list the prompt was composed from, so /tools cannot show a policy the model was never told
        var policyRows = policyLines.ToList();
        Func<IReadOnlyList<(string Extension, string Line)>> listPolicy = () => policyRows;

        //said the first time this home runs an interactive session on a cloud endpoint, and the marker file is what makes it once
        string? cloudNotice = null;
        var cloudMarker = Path.Combine(home, "cloud-notice-shown");
        if (cloud && args.Prompt is null && !File.Exists(cloudMarker))
        {
            cloudNotice = CloudEndpoint.Notice(glyphs.Cloud, modelString);
            try { File.WriteAllText(cloudMarker, ""); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }   //a home that cannot take the marker says the notice again next launch
        }

        var slotsHttp = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) })   //untimed, since a timed client aborts each poll and the ticker's own ceiling is the deadline, and ConnectTimeout still fails fast
            { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        Gatto.Core.Client.ISlotsReader? slotsReader = endpointName == "local" && launchBaseUrl is not null   //a local llama-server only, the fallback for a server that streams no progress
            //the key the server was started with, without it a keyed server stops the prefill indicator
            ? new Gatto.Core.Client.HttpSlotsReader(slotsHttp, launchBaseUrl, endpoint.ApiKey)
            : null;

        //a non-local endpoint that names no models has no rows to offer, the local shelf is not its to switch to
        var unlistedRemote = !ServedHere() && endpoint.Models is not { Count: > 0 };

        //the hint reads the same two locals as the reuse line, so the two sentences cannot disagree about what is loaded
        var repl = new Gatto.Repl.Repl(loop, convo, role.Name, modelString, sessions, client, endpoint.Context, contextBudget, cwd, recomposeSystem, toggleAuto, switchRole, resetGrounding, wildState, on => permissions.SetWild(on, persist: true), hooks, themeMode, subagentProgress, resumedFrom: resumedFrom, reasoning: config.ReasoningMode, thinkingName: initialThinkingName, setEffort: setEffort, listEfforts: listEfforts, glyphs: glyphs, purrSet: Gatto.Repl.Render.PurrFrames.RandomFromPool, versionLine: CommandBanner.DottedVersion(Gatto.Core.GattoVersion.String, Gatto.Core.GattoVersion.Build ?? "", CommandBanner.IsDevBuild(), glyphs), pump: pump, chrome: chrome, switchModel: switchModel, setDefault: id => ModelSwitch.Persist(home, endpointName, id),   //the role name as ResolveRole cased it from disk, which the banner and the status line key on
                    launchServing: launchServing, probeServing: probeServing, listModels: unlistedRemote ? null : listModels, picker: replPicker, slotsReader: slotsReader, warn: warn, altScreen: config.AltScreen, dumpOnExit: config.DumpOnExit, thinkingIsToggle: thinkCap == ThinkCapability.Toggle, thinkingIsUnavailable: ThinkingUnavailable(), mouseEnabled: config.Mouse && config.AltScreen && pump is not null, wheelLines: config.WheelLines, copyOnSelect: config.CopyOnSelect, resumedPath: continuePath, resumeLine: resumeLine,
            modelMarkLegend: !ServedHere() ? "current" : "weights loaded",
            //read at the moment a connection is lost, through the current model, so a /model switch is followed
            serverGone: () => model is { } served
                ? ServeLines.GoneLine(new ServeManager(home, ServerBinaryFor(served, config)).Dead(served.Profile.Port), glyphs)
                : null,
                    autoCompact: config.AutoCompact, permissions: permissions, listTools: listTools, listPolicy: listPolicy, memoryWarning: launchRes.MemoryTruncatedLines > 0 ? MemoryTruncationWarning(launchRes.MemoryTruncatedLines) : null, cloudNotice: cloudNotice, cloud: cloud, readUsage: usageMeter.Read, onUsageChanged: repaint => usageMeter.Changed = repaint,piggyback: piggyback, turnAbort: turnAbort,
            //a session with no model talks to a server gatto does not manage, so the notice says that instead of offering a model fix
            unmanagedNotice: !ServedHere() && endpoint.Models is not { Count: > 0 } && !cloud ? UnmanagedSession.ModelUnavailable(launchBaseUrl) : null,
            cloudSwitchHint: unlistedRemote && cloud ? CloudSwitchHint(endpointName) : null,
            unmanagedVisionNotice: !ServedHere() ? UnmanagedSession.VisionUnavailable(launchBaseUrl) : null,
            deafWatch: deafWatch,
            //read when /context asks, so a /role or /model since is followed. the shape is the loop's last request, its tools split by origin
            contextInputs: usage =>
            {
                var shape = loop.RequestShape;
                return new ContextInputs(modelString, comp.SystemText, comp.SystemParts ?? [],
                    [.. shape.Tools.Where(t => !tools.IsExtension(t.Name))], [.. shape.Tools.Where(t => tools.IsExtension(t.Name))],
                    shape, contextBudget, loop.ArmedAutoCompactAt, usage.Ratio, usage.LastPromptTokens, loop.LastTimings, loop.LastUsage);
            },
            //a cloud endpoint has no llama-server routes, any other may and falls back to the estimate when it does not
            tokenCounter: cloud ? null : client,
            //a connect session has no profile.json to point at, so the hint is null there
            visionFixHint: ServedHere() && model is not null
                ? UnmanagedSession.VisionFixBesideTheModel(model.Profile.ActivePath)
                : null,
            //re-read on every call, a launch snapshot would keep showing the file the session started on
            modelFiles: !ServedHere() || model is null ? null : () =>
            {
                try
                {
                    return [.. Gatto.Roles.Model.Load(modelsDir, model.Id).Profile.Files
                        .Select(f => (f.Quant ?? Path.GetFileNameWithoutExtension(f.Path), f.Active))];
                }
                //a profile that went unloadable mid-session returns an empty list, this must not take the REPL down
                catch (GattoConfigException) { return []; }
            },
            //the bodies live in QuantEdit, which refuses before it asks (the confirm needs the wizard prompter and the serving state here)
            switchQuant: !ServedHere() || model is null ? null : (Func<string, string>)(name =>
                QuantEdit.Switch(modelsDir, model, name,
                    () => QuantServing(home, model, config) is Gatto.Roles.ServingState.ServingThis,
                    Confirming(prompter),
                    //the same stop, start and restore the model switch uses, so there is one copy of the hardest half
                    restart: (previous, reloaded) => SwapServerAsync(
                        new Gatto.Roles.ServeManager(home, ServerBinaryFor(reloaded, config)),
                        previous, reloaded, warn.Warn,
                        glyphs).GetAwaiter().GetResult())),
            //forgetting a file is its own command, the switch only changes which one is active
            removeQuant: !ServedHere() || model is null ? null : (Func<string, string>)(name =>
                QuantEdit.Remove(modelsDir, model, name,
                    () => QuantServing(home, model, config) is Gatto.Roles.ServingState.ServingThis,
                    Confirming(prompter))))
;
        var replExit = await repl.RunAsync(CancellationToken.None);

        //the key stops only the server of a session on the local endpoint. a connect session never used the server this home records.
        InkedLine? serverLine = null;
        if (config.StopServerOnExit && ServedHere() && model is not null && launchBaseUrl is not null)
        {
            var stopped = ServeLines.AtExit.AfterStop(
                new ServeManager(home, config.LlamaServer ?? "").Stop(Gatto.Roles.NullServeListener.Instance),
                serverAlreadyUp, glyphs);
            //a failed stop is a real failure, so it goes to the warning sink and no server line is shown
            if (stopped is { WentWrong: true }) warn.Warn(stopped.Text);
            else serverLine = stopped?.Words;
        }
        else serverLine = ServeNotice.ExitHint(serverAlreadyUp, servedBytes, glyphs);

        //one writer and one order for everything read at the end, so a pipe cannot get the lines out of order
        ExitBlock.Write(new CliSurface(Console.Out, launchTheme, glyphs), glyphs,
            farewell: repl.Ended?.Farewell ?? false, resume: repl.Ended?.Resume ?? false, serverLine);
        return replExit;
    }

    //the one truncation sentence for the launch line and every reload warning, so the wording cannot drift and truncation is never silent

    //2% of the model's context, clamped to 1000 and 4000 tokens, since the index is in every prefix and must stay glanceable
    internal static int DerivedMemoryBudget(int? modelContext)
    {
        const int floor = 1000, ceiling = 4000;
        if (modelContext is not int ctx || ctx <= 0) return floor;   //an unknown context takes the floor rather than a guess
        return Math.Clamp(ctx * 2 / 100, floor, ceiling);
    }

    internal static string MemoryTruncationWarning(int facts) =>
        $"memory index over budget: {facts} facts not shown; prune .gatto\\memory\\";

    //the stop reason a headless caller reads off the -p JSON, and a length cut is a finish reason of length on a completed turn
    internal static string OutcomeString(TurnResult r) => r.Outcome switch   //a length cut stays completed with exit 0, and only this string tells it from a clean stop
    {
        TurnOutcome.Cancelled => "cancelled",
        TurnOutcome.Truncated => r.Truncation == TruncationKind.Stream ? "truncated_stream" : "truncated_length",
        _ => r.FinishReason == "length" ? "truncated_length" : "completed",
    };

    //the launch line about wild mode: on and where it is stored, or off because a project's .gatto.json forbids what the home says. null when wild is plainly off
    internal static string? WildNotice(PermissionStore permissions, WildState wild) =>
        wild.On ? "wild mode: ON for this project, stored in your gatto home"
        : permissions.Wild && wild.ForbiddenBy is { } by ? $"wild mode: off, {TermText.Sanitize(by)} sets \"wild\": false for this project"
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

    //routes start|stop|status to ServeManager, where start without a target falls back to gatto.json's default_model. a config error exits 2, anything else 1

    //the size words for a model id alone, null when this home cannot load the id
    private static string? SizeWordsOnDiskOrNull(string home, string modelId)
    {
        try
        {
            var model = Gatto.Roles.Model.Load(Path.Combine(home, "models"), modelId);
            return Gatto.Core.Acquire.ModelDiscovery.SetBytesOrNull(model.Profile.ActivePath) is { } bytes
                ? SizeWords.Gb(bytes, approx: true)
                : null;
        }
        catch (Exception) { return null; }
    }

    //the audition knows its model by id only, so it reads the size the same way every other site does
    private static string? AuditionSize(string home, string modelId) => SizeWordsOnDiskOrNull(home, modelId);

    private static async Task<int> RunServeAsync(ParsedArgs args, CommandContext? context = null)
    {
        var ctx = context ?? CommandContext.Production();
        var home = ctx.Home;
        GattoHome.EnsureInitialized(home);

        GattoConfig config;
        try { config = GattoConfig.Load(home); }
        catch (GattoConfigException ex) { Console.Error.WriteLine(ex.Message); return 2; }

        var manager = new ServeManager(home, config.LlamaServer ?? "");

        //only serve status --json stays bare, a parser reads that one and a cat above the object breaks it
        var framed = !(args.Subcommand == "status" && args.Json);
        //the frame opens here and closes in the finally, and the glyph set is passed so a plain console does not draw tofu
        var glyphs = CommandBanner.GlyphsFor(home);
        if (framed)
            CommandBanner.WriteHeader(ctx.Out, ctx.Theme,   //the header names the command only, the manager prints the model in the body
                "serve " + (args.Subcommand ?? "status"), glyphs, ctx.Stamp);

        //every word this command prints is composed here, so the manager can report a fact without knowing how it reads
        var lines = new ServeLines(ctx.Surface(), glyphs);

        try
        {
            switch (args.Subcommand)
            {
                case "start":
                    var modelId = args.Target ?? config.DefaultModel
                        ?? throw new GattoConfigException(
                            "gatto serve start needs a model. Pass one (gatto serve start <model>) or set default_model in gatto.json");
                    var model = Model.Load(Path.Combine(home, "models"), modelId);
                    //print the exposure warning and continue, on stderr so redirecting a foreground start's log doesn't hide it
                    if (ServeArgs.ExposureWarning(model.Profile) is { } exposure)
                        Console.Error.WriteLine(exposure);
                    //the loading row is for a wait the user watches, and a foreground start already has llama-server's own log on screen
                    if (!args.Detach)
                    {
                        //the window is the server's while it runs, and Restore gives the old title back on every exit
                        var title = TerminalTitle.Deed(Console.IsOutputRedirected);
                        title?.Apply(TerminalTitle.Glyph + " llama-server");
                        try { return await manager.StartAsync(model, lines, CancellationToken.None, foreground: true); }
                        finally { title?.Restore(); }
                    }
                    {
                        using var waitLine = new TickLine(ctx.Out, ctx.Theme is not null);
                        var bytes = Gatto.Core.Acquire.ModelDiscovery.SetBytesOrNull(model.Profile.ActivePath);
                        var words = bytes is { } size ? SizeWords.Gb(size, approx: true) : null;
                        var row = LoadingLine.Over(waitLine, glyphs, ctx.Theme);
                        var detached = new ServeLines(ctx.Surface(), glyphs,
                            new StartWatch(e => row.Draw(e, model.Id, words), waitLine.Finish, words));
                        var code = await manager.StartAsync(model, detached, CancellationToken.None);
                        waitLine.Finish();
                        return code;
                    }
                case "stop":
                {
                    //the body goes through ServeLines, so a raw write can't appear between the banner and the rows under it
                    return await manager.StopAsync(lines, CancellationToken.None);
                }
                default: //the status arm, which a bare gatto serve also reaches (ArgRouter allows nothing else)
                {
                    //the --json path skips ServeLines and the banner, a parser has to get the object alone
                    if (args.Json) return await manager.StatusJsonAsync(ctx.Out, CancellationToken.None);
                    return await manager.StatusAsync(lines, CancellationToken.None);
                }
            }
        }
        catch (GattoConfigException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        finally
        {
            //the closing blank follows the same fact as the header above, so a json run writes none
            if (framed) ctx.Surface().Close();
        }
    }

    //exit 0 is a model that passed, 1 is a model that failed, 2 is a problem on our side. the local config catch keeps a harness fault from exiting 1
    private static async Task<int> RunAuditionAsync(ParsedArgs args)
    {
        var home = GattoHome.Resolve();
        GattoHome.EnsureInitialized(home);

        //the closing blank goes through cli.Close in a finally, and frameOpen keeps a refusal before the header from leaving a blank with no header
        var cli = new CliSurface(Console.Out, theme: null, glyphs: null);
        var frameOpen = false;
        try
        {
            var config = GattoConfig.Load(home);
            var modelId = args.Target ?? config.DefaultModel
                ?? throw new GattoConfigException(
                    "gatto audition <model>: no model given and no \"default_model\" set in gatto.json");

            //the theme and Sanitize enter here, since Cli may reach Repl and Roles takes both as parameters so it never does
            var theme = CommandBanner.Chrome(home);   //output-only detection, so a redirect to a file stays byte-pure with one line per task
            var paint = InkFor(theme);
            //one glyph set is chosen for this run and shared by the header and the voice below
            var glyphs = CommandBanner.GlyphsFor(home);
            //no stamp on this header, RunAuditionAsync takes no CommandContext and threading one through would be a second seam
            CommandBanner.WriteHeader(Console.Out, theme, "audition",   //the header names the command only, the report's own model row names the model
                glyphs);
            frameOpen = true;

            using var console = new AuditionConsole(Console.Out, theme is not null, paint);
            var verdict = await Gatto.Roles.Audition.AuditionRunner
                .RunAsync(home, modelId, console, CancellationToken.None,
                    engineMarks: CommandBanner.MarksFor(home),
                    //the runner is in Roles and has no vocabulary, so a serve failure is reported through ServeLines handed in here
                    serveVoice: w => new ServeLines(new CliSurface(w, null, glyphs), glyphs),
                    //the runner commits each loading rung as a note, with the words taken from LoadingRow
                    loadingWords: elapsed => LoadingRow.Words(elapsed, modelId, AuditionSize(home, modelId), glyphs));
            console.Finish();       //the ticks come down before the report goes up

            cli.Blank();
            Console.WriteLine(Gatto.Roles.Audition.AuditionReport.Render(verdict, TermText.Sanitize,
                CommandBanner.MarksFor(home), paint));

            //both badge keys come off the verdict, and a model adopted off disk keeps a null repo id
            Gatto.Roles.Audition.BadgeWriter.Write(home, verdict);
            return verdict.Pass ? 0 : 1;
        }
        //indent the failure like every other line this command prints, a message at column 0 reads as gatto crashing
        catch (GattoConfigException ex)
        {
            //the tail is printed as subordinate rows through CliSurface.Under, the one indent this layer writes
            var err = new CliSurface(Console.Error, theme: null, glyphs: null);
            err.Blank();
            foreach (var line in ex.Message.Split('\n'))
                err.Under(line.TrimEnd('\r'));
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            //a disk failure is our problem, so it exits 2 instead of reading as a verdict about the model
            Console.Error.WriteLine($"audition could not run: {ex.Message}");
            return 2;
        }
        finally
        {
            if (frameOpen) cli.Close();
        }
    }

    //routes gatto setup and gatto model, which differ only in modelSegmentOnly, so the lifecycle has one home and its order holds
    private static (int Code, bool Launch) RunSetup(bool modelSegmentOnly = false, string? command = null)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)   //refused before the banner with exit 2 and a pointer to gatto doctor, since a wizard with nobody to answer would invent answers
        {
            Console.Error.WriteLine(
                "gatto setup needs a terminal it can ask questions in. Run it directly rather than "
                + "through a pipe or a script. To check an existing setup instead, run: gatto doctor");
            return (2, false);
        }

        var home = GattoHome.Resolve();

        //one glyph set for the run, asked once and handed to every painter
        var glyphs = CommandBanner.GlyphsFor(home);

        //nothing is written on the way in, the home appears at the first real write so looking at the wizard leaves nothing behind
        var theme = CommandBanner.Chrome(home);

        //the wizard prints no banner, but CommandBanner.Chrome still runs to resolve the theme the face needs

        //the TUI face is the one this command builds, from the same flow the plain face got. the version is read off the build
        var surface = new ConsoleSurface();
        var chrome = theme ?? new Theme(TermCaps.Detect(), ThemeMode.Dark);
        //the face defaults no timer, the pulse is passed in by name and disposed at the end. the glyph set is resolved here beside the theme and handed to the face
        var wizardCommand = command ?? (modelSegmentOnly ? "gatto model" : "gatto setup");   //the words the user typed, so gatto model new is not reported as gatto model

        using var face = new Setup.Tui.TuiWizardSurface(
            surface, new ConsoleKeySource(), chrome,
            Gatto.Core.GattoVersion.String, Gatto.Core.GattoVersion.Build ?? "",
            command: wizardCommand,
            pulse: () => new Gatto.Terminal.TimerPurrPulse(),
            glyphs: glyphs,
            //the purr is drawn once here from the pool, so nothing a test constructs can reach the draw
            fullPurr: Gatto.Repl.Render.PurrFrames.RandomFromPool());

        //the wizard runs on the alt buffer and gives it back on every exit, WizardSession owns that lifecycle
        var alt = new AltScreen(surface);

        //rich comes from the banner and the probes write into the face, so their lines reach the user at the epilogue
        var probes = new Setup.LiveSetupProbes(home, face.Glyphs, face.Notes,
            purrSet: Gatto.Repl.Render.PurrFrames.RandomFromPool,
            rich: theme is not null,
            //these lines go to the console, Dispose runs after the record has printed
            afterWalk: Console.Out,
            working: face.Working);
        var flow = new Setup.SetupFlow(probes);
        using (probes)
        {
            //the scrollback is composed after the wizard returns, from the facts true then, with the width and the clock read here
            var exit = Setup.WizardSession.Run(
                alt, () => Setup.SetupRunner.Run(flow, face, home, modelSegmentOnly),
                () => Setup.WizardSession.Scrollback(flow, () => face.LastPainted, surface.Width, face.Glyphs,
                    VersionStamp.Running, DateOnly.FromDateTime(DateTime.Now), wizardCommand, theme),
                Console.Out, notes: () => face.CapturedNotes, command: wizardCommand);

            //the shipped files are written after the wizard and only when the home exists, since writing them is what creates it
            if (Directory.Exists(home))
            {
                try { ShippedExtensions.EnsureWritten(home); }
                catch (Exception ex) { Console.Error.WriteLine($"! setup: could not write shipped files: {ex.Message}"); }
            }

            //the caller launches the REPL, and a server setup started stays up for the session
            if (!flow.StartReplWhenDone) return (exit, false);   //false when entered at the model segment, which has no REPL to start

            //the countdown runs after the record and before the REPL, as a live line (the record is composed once)
            var cli = new CliSurface(Console.Out, theme, glyphs);
            Setup.Tui.Countdown.Run(cli.Live, System.Threading.Thread.Sleep, glyphs: glyphs);
            cli.Blank();   //ends the live line, so the next row starts at column zero
            //the row is composed through the painter, so it degrades to the same plain sentence when theme is null
            if (probes.HandOverServer())
                cli.Row(
                    ("the server stays up for this session", CliInk.Plain),
                    ($" {glyphs.Dot} ", CliInk.Dim),
                    CliSurface.Command("gatto serve stop"),
                    (" when you're done.", CliInk.Plain));
            return (exit, true);
        }
    }


    //the previous model is restarted at most once, and false leaves the caller's session on the model it already had
    private static async Task<bool> SwapServerAsync(
        ServeManager manager, Gatto.Roles.Model? previous, Gatto.Roles.Model incoming, Action<string> say,
        Gatto.Terminal.GlyphSet? glyphs, string? cannotRestore = null)
    {
        //the stop's rows go to NullServeListener, this path says what happened in its own sentence below
        await manager.StopAsync(Gatto.Roles.NullServeListener.Instance, CancellationToken.None).ConfigureAwait(false);

        if (await AutoServeAsk.StartAndWaitAsync(manager, incoming, say, CancellationToken.None, glyphs ?? Gatto.Terminal.GlyphSet.Unicode)
            .ConfigureAwait(false)) return true;

        if (previous is null)
        {
            //the reason the previous model did not load comes in cannotRestore, and it is said here
            say(cannotRestore ?? "nothing is serving now. gatto serve start when you're ready.");
            return false;
        }

        say($"loading {previous.Id} back{(glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Ellipsis}");
        if (await AutoServeAsk.StartAndWaitAsync(manager, previous, say, CancellationToken.None, glyphs ?? Gatto.Terminal.GlyphSet.Unicode)
            .ConfigureAwait(false)) return false;

        //with both models gone the sentence names a reboot, memory a dead process still holds is the likely cause
        say($"couldn't restart {previous.Id} either; a reboot may be needed to reclaim GPU memory.");
        return false;
    }

    //the model a failed /model swap puts back, loaded from the id the server held. a model that will not load returns the sentence to say in its place
    internal static (Gatto.Roles.Model? Model, string? CannotRestore) RestoreTarget(string modelsDir, string servedId)
    {
        try { return (Gatto.Roles.Model.Load(modelsDir, servedId), null); }
        catch (GattoConfigException ex)
        {
            return (null, $"couldn't load {servedId} to put it back: {ex.Message}. nothing is serving now. "
                + "gatto serve start when you're ready.");
        }
    }


    //interactive only, it refuses without a terminal. nothing is removed until the map and the PATH entry are in place, so gatto can always say what it did

    //the context, the read-only site and the irreversible acts are three records. the deeds stay out of the rendering record, or a reader takes it for output only
    internal static int RunUninstall(
        CommandContext? context = null, UninstallSite? site = null, UninstallActs? acts = null)
    {
        var ctx = context ?? CommandContext.Production();
        if (!ctx.Interactive)
        {
            Console.Error.WriteLine(
                "gatto uninstall needs a terminal it can ask questions in. Run it directly rather "
                + "than through a pipe or a script.");
            return 2;
        }

        var home = ctx.Home;
        var where = site ?? UninstallSite.Probe();
        var deeds = acts ?? UninstallActs.Production;
        var installDir = where.InstallDir;

        //the full banner, written after the refusal so a piped run's error is the only thing it writes
        var glyphs = CommandBanner.GlyphsFor(home);   //one glyph set per run, two painters must not disagree
        CommandBanner.Write(ctx.Out, ctx.Theme, "uninstall", glyphs: glyphs,
            stamp: ctx.Stamp);

        //the theme is resolved once, the surface and the prompter both take it from the context
        var cli = ctx.Surface();
        var ask = ctx.Prompter();

        //the screen offers to stop a running server, advice to run gatto serve stop afterwards is unfollowable once gatto is gone
        ServeManager? serve = null;
        Gatto.Roles.RunningInfo? running = null;
        try
        {
            var cfg = GattoConfig.Load(home);
            serve = new ServeManager(home, cfg.LlamaServer ?? "");
            running = serve.DescribeRunning();
        }
        catch (Exception) { } //no readable config means nothing to say about a server

        //the map is built from the same reads the deletion uses, a second set could disagree with what gets removed
        string? llamaServer = null;
        var models = new List<(string, string)>();
        try
        {
            var config = GattoConfig.Load(home);
            llamaServer = config.LlamaServer;
            var modelsDir = Path.Combine(home, "models");
            foreach (var id in Gatto.Roles.Model.ListIds(modelsDir))
            {
                try { models.Add((id, Gatto.Roles.Model.Load(modelsDir, id).Profile.ActivePath)); }
                catch (GattoConfigException) { } //a model whose profile will not load is skipped rather than failing the whole map
            }
        }
        catch (Exception) { } //a home with no readable config still has an exe and a PATH entry to remove

        //both consent screens go through SelectPrompt, nothing here asks for a typed y or n
        var consent = UninstallConsent.Ask(ask, home, running);
        if (consent is null)
        {
            cli.Say(UninstallConsent.NothingRemoved);
            cli.Close();
            return 0;
        }

        var deleteHome = consent.Value.DeleteHome;

        //the install facts come from the site probe. an absent exe or PATH entry prints no line, RemovesAnything is asked rather than assumed
        var install = where.Facts;

        //the caller reads whether a weights file has no model row and passes the fact in, the map never reads the disk
        var map = Uninstall.Map(installDir, home, deleteHome, install, llamaServer, models,
            consent.Value.StopServer ? running : null,
            otherWeightFiles: Uninstall.HasUnnamedWeights(home, models));
        cli.Blank();
        UninstallConsent.WriteMap(cli, map);

        //the sibling notice goes before the confirm, since it changes what a yes achieves. it stays outside the map, whose rows are all the confirm covers
        if (deleteHome && Uninstall.SiblingNotice(where.SiblingGattos) is { } sibling)
        {
            cli.Blank();
            cli.Say(sibling);
        }

        cli.Blank();

        //a yes that removes nothing is a confirmation not worth asking for, and the map above has already printed
        if (!Uninstall.RemovesAnything(map))
        {
            cli.Say("there is nothing to remove. This gatto is not installed on this machine.");
            cli.Close();
            return 0;
        }

        //the confirm covers the map printed above
        if (!UninstallConsent.Confirm(ask))
        {
            cli.Say(UninstallConsent.NothingRemoved);
            cli.Close();
            return 0;
        }

        //the stop comes before anything is removed, so a declined one can name the running server
        if (consent.Value.StopServer && serve is not null)
        {
            //the stop writes through the writer it is handed, so it gets ServeLines built over this screen's layer
            serve.StopAsync(new ServeLines(cli, glyphs), CancellationToken.None)
                .GetAwaiter().GetResult();
        }
        else if (running is { } left)
            cli.Say(UninstallConsent.LeftRunning(left));

        //remove the PATH entry first, so a failure here leaves an installed gatto rather than a dangling entry. the outcome is captured for the sentence below
        var pathRemoved = false;
        if (install.OnPath)
        {
            if (deeds.RemovePathEntry(installDir) is { } pathProblem)
                Console.Error.WriteLine("! " + pathProblem);
            else
                pathRemoved = true;
        }

        if (deleteHome)
        {
            //every entry that would not go is reported on its own line. the keep set from the map is passed to the delete, so the weights files it promised stay
            var treeFailures = deeds.RemoveTree(home, Uninstall.KeptUnder(map, home));
            foreach (var failure in treeFailures)
                Console.Error.WriteLine($"! couldn't remove {failure.Path}: {failure.Why}");

            //the notice is added on top of the per-entry failures above
            if (Uninstall.PartialRemovalNotice(home, treeFailures, where.SiblingGattos) is { } note)
                cli.Say(note);
        }

        //the off-PATH sentence is said only when the entry was really removed, and it goes through cli.Say like the rest
        if (pathRemoved) cli.Say("gatto is off your PATH.");
        if (install.ExePresent)
        {
            cli.Say("the program file removes itself as this exits.");
            deeds.ArmSweeper(installDir);
        }
        //the closing row for the banner above, on every path that printed one
        cli.Close();
        return 0;
    }

    //a detached shell waits for this process to exit, a running image cannot delete itself. armed last, and best-effort, a failed sweeper leaves one folder behind
    internal static void ArmSweeper(string installDir)
    {
        try
        {
            var pid = Environment.ProcessId;
            var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe")
            {
                Arguments = "-NoProfile -WindowStyle Hidden -Command "
                    + $"\"Wait-Process -Id {pid} -ErrorAction SilentlyContinue; "
                    + $"Start-Sleep -Milliseconds 300; "
                    + $"Remove-Item -LiteralPath '{installDir}' -Recurse -Force -ErrorAction SilentlyContinue\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception)
        {
            Console.Error.WriteLine($"! couldn't remove {installDir} automatically. Delete it by hand.");
        }
    }

    //the size of a model is the sum of its shards, so read ModelDiscovery.SetBytesOrNull rather than one file's length

    //the probes' dispose lives in RunSetup, for gatto setup and for /model add

    //maps ReportInk onto the theme, so the colour decision stays in Cli where Theme lives
    private static Func<string, Gatto.Roles.Audition.ReportInk, string>? InkFor(Theme? theme) =>
        theme is null ? null : (text, ink) => theme.Paint(text, ink switch
        {
            Gatto.Roles.Audition.ReportInk.Ok => Theme.Ok,
            Gatto.Roles.Audition.ReportInk.Fail => Theme.Err,
            Gatto.Roles.Audition.ReportInk.Accent => Theme.Accent,
            _ => Theme.Dim,
        });

    //the engine half of the setup question, where FirstRunDoor asks about the model. it asks for the value only, and a config that will not load answers false
    private static bool NoEngineIn(string home)
    {
        try { return string.IsNullOrWhiteSpace(GattoConfig.Load(home).LlamaServer); }
        catch (GattoConfigException) { return false; }
    }

    //both refusals run before the banner, a cat above a usage error has already printed output
    private static Task<int> RunModelAsync(ParsedArgs args, CommandContext? ctx = null)
    {
        //an argument or an unknown subcommand is refused, bare new still opens the shelf
        if (args.Target is not null || (args.Subcommand is { } sub && sub != "new"))
        {
            Console.Error.WriteLine(
                "gatto model takes no argument. Run gatto model, press m to list the models on this "
                + "machine, and type the path to your .gguf there.");
            Console.Error.WriteLine();
            return Task.FromResult(2);
        }

        //read ctx?.Interactive rather than the console properties, which no test can inject. the first-run door reads the same expression, so the two cannot disagree
        if (!(ctx?.Interactive ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected)))
        {
            Console.Error.WriteLine(
                "gatto model needs a terminal it can ask questions in. Run it directly rather than "
                + "through a pipe or a script. To see the models you have, use /model inside a session.");
            Console.Error.WriteLine();
            return Task.FromResult(2);
        }

        var home = ctx?.Home ?? GattoHome.Resolve();

        //both arms, NotConfigured never reads llama_server and the segment this enters skips the engine check
        if (FirstRunDoor.NotConfigured(home) || NoEngineIn(home))
        {
            Console.Error.WriteLine(
                "gatto model sets up a model on a machine that already has an engine. This one "
                + "doesn't yet. Run gatto setup first.");
            Console.Error.WriteLine();
            return Task.FromResult(2);
        }

        //from here the command is the wizard at the model segment, with no EnsureInitialized, since opening it must not create a home
        return Task.FromResult(RunSetup(modelSegmentOnly: true, command: ModelInvocation(args.Subcommand)).Code);   //no banner either, the alt buffer hides it and the restore would put it above the wizard's closing record
    }

    //the words that reached the wizard, kept for its header and title so new is not dropped
    internal static string ModelInvocation(string? subcommand) =>
        subcommand == "new" ? "gatto model new" : "gatto model";

    //read-only, so a missing home is left for the checks to report. the HttpClient here is real, and the tests drive Doctor with their own probes

    //internal so a test can drive it with its own records, the dispatch line above stays uncovered
    internal static async Task<int> RunDoctorAsync(
        CommandContext? context = null, DoctorProbes? outside = null)
    {
        var ctx = context ?? CommandContext.Production();
        var probes = outside ?? DoctorProbes.Production;
        var home = ctx.Home;
        var cwd = Environment.CurrentDirectory;
        using var http = DoctorClient(probes.Transport);
        //the running-server row reads through ServeManager.DescribeRunning, which never spawns. an unreadable config leaves it null, and the config checks report that
        Func<Gatto.Roles.RunningInfo?>? describe = null;
        var configTheme = "auto";
        try
        {
            var cfgForServe = GattoConfig.Load(home);
            configTheme = cfgForServe.Theme;
            var mgr = new ServeManager(home, cfgForServe.LlamaServer ?? "");
            describe = () => mgr.DescribeRunning();
        }
        catch (GattoConfigException) { } //doctor's config checks own this failure

        //colour follows stdout alone, doctor never reads a key. an unloadable config keeps the auto theme, so a broken gatto.json cannot stop doctor
        var theme = ctx.Theme;
        CommandBanner.WriteHeader(ctx.Out, theme, "doctor", CommandBanner.GlyphsFor(ctx.Home),
            stamp: ctx.Stamp);
        var code = await new Doctor(http, describeRunning: describe, theme: theme,
            //the update check runs on a diagnostic's budget, 1s whole exchange and 500ms to connect
            checkUpdate: probes.CheckUpdate)
            .RunAsync(home, cwd, ctx.Out, CancellationToken.None);
        ctx.Surface().Close();   //the frame's closing row, paired with the header above it
        return code;
    }

    //the client the doctor reads through carries no timeout, each check holds its own deadline (internal so a test can read it)
    internal static HttpClient DoctorClient(HttpMessageHandler transport) =>
        new(transport) { Timeout = Timeout.InfiniteTimeSpan };

    //routes gatto status to Status, read-only and with no EnsureInitialized like doctor
    private static async Task<int> RunStatusAsync(CommandContext? context = null)
    {
        var ctx = context ?? CommandContext.Production();
        var cwd = Environment.CurrentDirectory;
        //untimed, the health read beneath status holds a 5s deadline of its own
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
            { Timeout = Timeout.InfiniteTimeSpan };
        //the one-line banner takes the glyph set so a host without those glyphs still draws the cat
        CommandBanner.WriteHeader(ctx.Out, ctx.Theme, "status", CommandBanner.GlyphsFor(ctx.Home),
            stamp: ctx.Stamp);
        var code = await Status.RunAsync(ctx.Home, cwd, ctx.Out, http, CancellationToken.None);
        ctx.Surface().Close();   //the frame's closing row, paired with the header above it
        return code;
    }

    //blocks for the synchronous gate and drains both streams at once so neither pipe fills. a timeout kills it and returns a placeholder rather than throwing
    internal static string RunReadOnlyShell(string command, string cwd)   //internal as a test seam
    {
        var psi = ReadOnlyShellStartInfo(command, cwd);
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException($"failed to start {psi.FileName}");
        //closing stdin here gives a command that reads it an EOF at once
        proc.StandardInput.Close();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(10_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { } //a failed kill still returns the placeholder
            return "(git status timed out)";
        }

        //a child holding the inherited stdout handle would block the drains, so the bound covers them. a truncated git status is worse than none, so it fails closed
        if (!Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 5_000))
        {
            try { proc.Kill(entireProcessTree: true); } catch { } //a failed kill still returns the placeholder
            return "(git status unavailable: a child process still holds the output pipe)";
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(stderr) ? $"'{command}' exited {proc.ExitCode}" : stderr.Trim());
        return stdout.TrimEnd();
    }

    //the start info of RunReadOnlyShell, in a place a test can read without spawning. stdin is redirected and closed by the caller, so a reader gets an EOF at once
    internal static ProcessStartInfo ReadOnlyShellStartInfo(string command, string cwd)
    {
        var exe = "powershell.exe";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            if (dir.Length > 0 && File.Exists(Path.Combine(dir.Trim(), "pwsh.exe"))) { exe = "pwsh.exe"; break; }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = cwd,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        //pin the read encoding, PS 5.1 would mangle non-ASCII output
        Gatto.Core.Tools.PowerShellUtf8.PinReadEncoding(psi);
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(Gatto.Core.Tools.PowerShellUtf8.WithPrelude(command));
        return psi;
    }


    //whether gatto serves this endpoint and whose window sizes the session: a folder's port, auto_serve and context count only on the endpoint gatto serves
    internal static (bool GattoServes, int? Window) ServingFor(bool servedHere, Gatto.Roles.Model? model,
        EndpointConfig endpoint, string? baseUrl) =>
        servedHere && model is not null
            ? (baseUrl is not null, model.Profile.Context)
            : (false, endpoint.Context);

    //the binary a server starts with, the model's override or else the config's. a manager built only to read takes the config's own
    private static string ServerBinaryFor(Gatto.Roles.Model? model, GattoConfig config) =>
        model?.Profile.LlamaServer ?? config.LlamaServer ?? "";

    //is this model the one the running server holds, asked once so a switch names it. the binary does not decide this, Serving reads serve.json

    private static Gatto.Roles.ServingState QuantServing(
        string home, Gatto.Roles.Model model, GattoConfig config) =>
        new Gatto.Roles.ServeManager(home, ServerBinaryFor(model, config)).Serving(model.Id);

}
