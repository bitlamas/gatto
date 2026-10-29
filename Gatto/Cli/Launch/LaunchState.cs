using System.Text.Json;
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Session;
using Gatto.Core.Loop;
using Gatto.Core.Memory;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Prompts;
using Gatto.Roles;
using Gatto.Roles.Agents;
using Gatto.Roles.Models;
using Gatto.Roles.Serve;
using Gatto.Terminal;

namespace Gatto.Cli.Launch;

//the role, endpoint, model and composition resolved for a launch or a /role switch, plus the memory index lines its budget dropped
internal sealed record RoleResolution(
    RoleFile Role, Model? Model, EndpointConfig Endpoint, string EndpointName, string ModelString, Composition Comp,
    int MemoryTruncatedLines);

//one launch's role, model and thinking with the switches that move them, so /role, /model and /effort read what the last switch wrote
internal sealed class LaunchState(
    ParsedArgs args, GattoConfig config, string home, string cwd, string rolesDir, string modelsDir,
    IReadOnlyDictionary<string, EndpointConfig> endpoints, string? launchEndpoint, string? sessionModel, WarningSink warn,
    bool memoryOn, List<(string Extension, string Line)> policyLines, GlyphSet glyphs)
{
    //a stale default is said once per process, since /role and the policy pass resolve again
    private readonly HashSet<string> _staleDefaultWarned = new(StringComparer.Ordinal);

    //the session's -m, which every switch moves, so ResolveRole reads it rather than the stale args.Model
    internal string? SessionModel = sessionModel;
    internal RoleFile Role = null!;
    internal Model? Model;
    internal EndpointConfig Endpoint = null!;
    internal string EndpointName = "";
    internal string ModelString = "";
    internal Composition Comp = null!;
    internal string LastComposedSystem = "";
    internal BaselineSources LastComposedSources = null!;
    internal string? LaunchBaseUrl;
    internal bool Cloud;
    internal int? ContextBudget;
    internal ReasoningHistory ReasoningHistory;
    //one bounded read of /props at launch, for the window, the vision bit and the thinking capability
    internal ServingProbe? LaunchServing;
    internal IReadOnlyDictionary<string, JsonElement?>? ReasoningMap;
    internal ThinkingLevel? BinaryOn;
    internal ThinkCapability ThinkCap;
    internal bool ThinkOn;
    //one sequence per composition this process makes, so every system message the session stores reads a higher number than the one before
    internal int BaselineSeq;
    internal IReadOnlyList<RoleFile> RoleFiles = [];
    internal readonly ToolRegistry Tools = new();
    internal readonly MemoryNagLatch MemoryNag = new();

    //the services the switches call, null until UseServices
    private AgentLoop _loop = null!;
    private RunAgentTool _runAgent = null!;
    private CitationGate _citationGate = null!;
    private HttpClient _probeHttp = null!;
    private TimeSpan _probeDeadline;
    private IUserPrompter? _prompter;
    private Theme? _launchTheme;

    internal void UseServices(
        AgentLoop loop, RunAgentTool runAgent, CitationGate citationGate, HttpClient probeHttp, TimeSpan probeDeadline,
        IUserPrompter? prompter, Theme? launchTheme)
    {
        _loop = loop;
        _runAgent = runAgent;
        _citationGate = citationGate;
        _probeHttp = probeHttp;
        _probeDeadline = probeDeadline;
        _prompter = prompter;
        _launchTheme = launchTheme;
    }

    //the time every role resolution spent reading context files and the memory index, which the launch span reads once
    internal long ContextFilesTicks;
    internal long MemoryTicks;

    //the memory budget is the profile override, else gatto.json, else derived from the context window and clamped
    internal int MemoryBudget(Model? p) =>
        p?.Profile.MemoryIndexBudget
        ?? config.MemoryIndexBudget
        ?? GattoApp.DerivedMemoryBudget(p?.Profile.Context);

    //the shared load, with the model passed in so a caller composes against the model armed at call time
    internal MemoryIndex.LoadResult LoadMemory(Model? p) => memoryOn
        ? MemoryIndex.Compose(MemoryDir.FindProjectRoot(cwd), MemoryBudget(p))
        : new MemoryIndex.LoadResult(null, 0);

    //a non-local session's saved level, parsed here since Core holds only the name, and a bad name is a config error naming its key
    internal static ThinkingLevel? SavedCloudEffort(GattoConfig cfg, string onEndpoint, string modelId)
    {
        if (cfg.EffortDefaultFor(onEndpoint, modelId) is not { } levelName) return null;
        try { return Thinking.Parse(levelName); }
        catch (GattoConfigException ex)
        {
            throw new GattoConfigException($"gatto.json defaults.{onEndpoint}.effort.{modelId}: {ex.Message}");
        }
    }

    //one resolution of role, endpoint, model and composition, run at launch and by /role, which fails before any client is built
    internal RoleResolution ResolveRole(string roleArg)
    {
        var available = RoleFile.ListNames(rolesDir);
        if (!available.Contains(roleArg, StringComparer.OrdinalIgnoreCase))
            throw new GattoConfigException(available.Count > 0
                ? $"unknown role '{roleArg}'. Available roles: {string.Join(", ", available)}"
                : $"unknown role '{roleArg}'. No role files found in {rolesDir}");
        //the role name takes the on-disk casing here, since the checks below are ordinal and gatto ORACLE must report oracle
        roleArg = available.First(n => string.Equals(n, roleArg, StringComparison.OrdinalIgnoreCase));
        return ResolveLoaded(RoleFile.Load(rolesDir, roleArg));
    }

    //the resolution of a role already in hand, so run_agent can check a merged role that was never a file
    internal RoleResolution ResolveLoaded(RoleFile role)
    {
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
            var effectiveModel = RoleFile.EffectiveModel(role, SessionModel, config.ModelDefaultFor("local"));
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
                ? SessionModel ?? (role.Endpoint == endpointName ? role.Model : null)
                : SessionModel ?? role.Model;
            var choice = LaunchModel.Resolve(endpointName, ep.Models, explicitModel, config.ModelDefaultFor(endpointName));
            if (choice.Warning is { } stale && _staleDefaultWarned.Add(stale)) warn.Warn(stale);
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

        var readFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        var contextFiles = ContextFiles.Collect(
            cwd, config.ContextCompat, config.ContextHome ? Path.Combine(home, "GATTO.md") : null);
        var memoryFrom = System.Diagnostics.Stopwatch.GetTimestamp();
        ContextFilesTicks += memoryFrom - readFrom;
        //reloaded beside the context files at launch and on every /role, so the memory budget follows the model the role resolves to
        var memory = LoadMemory(model);
        MemoryTicks += System.Diagnostics.Stopwatch.GetTimestamp() - memoryFrom;
        var comp = RoleComposition.Compose(role, model, contextFiles, endpoint.Thinking, cwd: cwd, date: DateTime.Now,
            memoryIndex: memory.Text, memoryTruncatedLines: memory.TruncatedLines, memoryNudge: memoryOn,
            policyLines: policyLines,
            savedEffort: endpointIsLocal ? null : SavedCloudEffort(config, endpointName, modelString));

        return new RoleResolution(role, model, endpoint, endpointName, modelString, comp, memory.TruncatedLines);
    }

    //the role files run_agent may call, one warning per file that fails to load, since a broken file is never a dead launch
    internal IReadOnlyList<RoleFile> LoadRoleFiles(string? warned = null)
    {
        var loaded = new List<RoleFile>();
        foreach (var name in RoleFile.ListNames(rolesDir))
        {
            try { loaded.Add(RoleFile.Load(rolesDir, name)); }
            catch (GattoConfigException ex)
            {
                if (!string.Equals(name, warned, StringComparison.OrdinalIgnoreCase)) warn.Warn(ex.Message);
            }
        }
        return loaded;
    }

    //gatto starts, probes, sizes and stops only the server of the endpoint named local, a model folder found on another endpoint supplies its model parts alone
    internal bool ServedHere() => EndpointName == "local";

    //a role's tools key hides the six it does not name, so the loop and /tools read the one registry and the manifest keeps its order
    internal void ArmSeatTools(RoleFile r) =>
        Tools.Hide(r.Tools is null ? [] : SubagentWiring.Builtins().Keys.Where(n => !r.Tools.Contains(n)));

    //a delegate to the Repl, so it never references Gatto.Roles, and it reads the model and base URL a /model switch left
    internal ServingProbe ProbeServing()
    {
        if (LaunchBaseUrl is null || Cloud) return new ServingProbe(null);
        //a session gatto does not serve reads only the server's vision bit, since there is no served model to compare
        if (!ServedHere() || Model is null)
        {
            var connectLoaded = ServeProbe.ProbeAsync(_probeHttp, LaunchBaseUrl, CancellationToken.None, Endpoint.ApiKey, _probeDeadline)
                .GetAwaiter().GetResult();
            return new ServingProbe(connectLoaded?.NCtx, ThinkCapability.None, connectLoaded?.Vision);   //an unanswered probe leaves Vision null, which is unknown, so the refusal on false lets the attach go
        }
        var loaded = ServeProbe.ProbeAsync(_probeHttp, LaunchBaseUrl, CancellationToken.None, Endpoint.ApiKey, _probeDeadline)
            .GetAwaiter().GetResult();
        return new ServingProbe(loaded?.NCtx, loaded?.Capability ?? ThinkCapability.None, loaded?.Vision);
    }

    //the launch's resolution for the model the server holds, by the route a by-name /model takes, or null with nothing moved when it resolves to another address
    internal RoleResolution? ResolveServed(string roleName, string servedId)
    {
        var previous = SessionModel;
        SessionModel = servedId;
        RoleResolution res;
        try { res = ResolveRole(roleName); }
        catch (GattoConfigException) { SessionModel = previous; return null; }
        //a model whose profile names another port would send every request where no server answered
        if (!string.Equals(res.Endpoint.BaseUrl, LaunchBaseUrl, StringComparison.OrdinalIgnoreCase))
        {
            SessionModel = previous;
            return null;
        }
        return res;
    }

    internal static ThinkingMark ThinkingOf(Composition c) => new(c.Thinking.ToString().ToLowerInvariant(), c.ThinkingBody?.GetRawText());

    //the thinking is the one in force, which the effort switch and the toggle move away from what a fresh composition picks
    internal SessionBaseline NextBaseline(BaselineSources sources) => new(++BaselineSeq, Role.Name, ModelString,
        ReasoningHistory == ReasoningHistory.None ? "none" : "all", ThinkingOf(Comp), BaselineMarks.ToolsOf(Tools.Specs()), sources, EndpointName);

    //a Toggle model stores its choice in the same ModelDefaultEffort slot, on as the map's on-level and off as None
    internal bool DefaultThinkOn(Model? m) =>
        m?.Profile.DefaultEffort is { } persisted ? persisted != ThinkingLevel.None : config.Think != "off";

    //the seam /effort, /role and /model drive: map bodies when there is a map, the generic enable_thinking kwarg otherwise
    internal (JsonElement? Body, string? Suffix) ToggleEntry(bool on) => BinaryOn is { } onLevel
        ? Gatto.Roles.Models.Thinking.ToggleBody(ReasoningMap, onLevel, on)   //map bodies, and ToggleBody guards an empty on-body
        : (ThinkCapabilitySniff.EnableThinkingBody(on), null);

    internal string ApplyToggle(bool on)
    {
        var (body, suffix) = ToggleEntry(on);
        Comp = Comp with { ThinkingBody = body, ThinkingSuffix = suffix };
        _loop.UpdateOverrides(ModelString, Comp.Sampling, Comp.ThinkingBody, Comp.ThinkingSuffix);
        return "thinking " + (on ? "on" : "off");
    }

    //the one source for the footer's thinking slot, so a rebuild cannot disagree with the model's real capability
    internal string? ThinkingFooterName() =>
        ThinkCap == ThinkCapability.Toggle
            ? "thinking " + (ThinkOn ? "on" : "off")
            : (Model?.Profile.Thinking ?? Endpoint.Thinking) is null ? null : EffortSwitch.Name(Comp.Thinking);

    //unavailable only when ThinkCap is None or AlwaysOn and no map is configured (a degenerate map can still offer a level)
    internal bool ThinkingUnavailable() =>
        ThinkCap is ThinkCapability.None or ThinkCapability.AlwaysOn
        && (Model?.Profile.Thinking ?? Endpoint.Thinking) is null;

    //re-walks the context files and the memory index on each call, so /new, /compact and auto-compaction see edits and no ordinary turn does
    internal ComposedSystem RecomposeSystem()
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
            return new ComposedSystem(LastComposedSystem, NextBaseline(LastComposedSources));
        }

        var memory = LoadMemory(Model);
        //always call it, the zero case clears the latch so a later truncation is news again
        if (MemoryNag.ShouldWarn(MemoryBudget(Model), memory.TruncatedLines))
            warn.Warn(GattoApp.MemoryTruncationWarning(memory.TruncatedLines));
        var composed = RoleComposition.Compose(
            Role, Model, contextFiles,
            Endpoint.Thinking, cwd: cwd, date: DateTime.Now,
            memoryIndex: memory.Text, memoryTruncatedLines: memory.TruncatedLines, memoryNudge: memoryOn,
            policyLines: policyLines);
        LastComposedSystem = composed.SystemText;
        LastComposedSources = composed.Sources;
        return new ComposedSystem(LastComposedSystem, NextBaseline(composed.Sources));
    }

    //re-runs the launch precedence and moves the state in place, so RecomposeSystem sees the new role at the next /new
    internal RoleSwitchResult SwitchRole(string requestedName)
    {
        RoleResolution res;
        try { res = ResolveRole(requestedName); }
        catch (GattoConfigException ex) { return new RoleSwitchResult(false, "", null, null, ex.Message); }

        if (!string.Equals(res.Endpoint.BaseUrl, LaunchBaseUrl, StringComparison.OrdinalIgnoreCase))   //the client's base URL is fixed at construction, so a role on another URL is refused or it would talk to the old server
            return new RoleSwitchResult(false, "", null, null,
                $"role '{res.Role.Name}' resolves to {res.Endpoint.BaseUrl ?? "(no base_url)"}, but this " +
                $"session is connected to {LaunchBaseUrl}. Restart gatto to switch to a different " +
                "endpoint or a model served on a different address");

        //a pinned local base_url lets two models share one URL, and llama-server ignores the request's model field, so a different model is refused
        if (EndpointName == "local" &&
            !string.Equals(res.Model?.Id, Model?.Id, StringComparison.OrdinalIgnoreCase))
            return new RoleSwitchResult(false, "", null, null,
                res.Model is null   //a cloud endpoint whose base_url matches the pinned local one, refused with an endpoint message since no model can be named
                    ? $"role '{res.Role.Name}' targets endpoint '{res.EndpointName}', but this session is " +
                      $"serving local model '{Model!.Id}'. Restart gatto to switch endpoints"
                    : $"role '{res.Role.Name}' uses model '{res.Model.Id}', but this session is serving " +
                      $"'{Model!.Id}'. Restart gatto to switch models");

        Role = res.Role; Model = res.Model; Endpoint = res.Endpoint;
        _loop.TraceRole = Role.Name;   //the next turn span names the role this switch armed
        EndpointName = res.EndpointName; ModelString = res.ModelString; Comp = res.Comp;
        RoleFiles = LoadRoleFiles();   //only on a switch that commits, since run_agent's description rides every request and a refused switch must move no prefix
        ArmSeatTools(Role);   //the manifest rides every request too, so only a committed switch may change it
        LastComposedSystem = Comp.SystemText;   //updated here, the degrade target must follow the newly armed role
        LastComposedSources = Comp.Sources;
        //the newly armed model may have a tighter memory.index_budget, so warn here rather than stay silent
        if (MemoryNag.ShouldWarn(MemoryBudget(Model), res.MemoryTruncatedLines))
            warn.Warn(GattoApp.MemoryTruncationWarning(res.MemoryTruncatedLines));
        _loop.UpdateOverrides(ModelString, Comp.Sampling, Comp.ThinkingBody, Comp.ThinkingSuffix);

        //armed for the new role, the ledger has been recording upstream all along so a switch never starts blind
        _citationGate.Armed = Comp.Gates.Contains("citations");

        //recompose overwrote the toggle body, so re-apply the session's on/off choice after a /role
        if (ThinkCap == ThinkCapability.Toggle) ApplyToggle(ThinkOn);
        var switchedTint = Role.ParsedTint();
        _launchTheme?.SetRoleTint(Role.Name, switchedTint?.Dark ?? Theme.Accent, switchedTint?.Light);
        return new RoleSwitchResult(true, Role.Name, ModelString, Comp.SystemText, null,
            ThinkingFooterName(), NextBaseline(Comp.Sources), switchedTint, Role.Avatar, Role.FirstMessage);
    }

    //arms a model and leaves the role's gates alone, the decision is in ModelSwitch.Decide and the side effects here only on success
    internal ModelSwitchResult SwitchModel(string requestedModel, bool confirmed)
    {
        //an endpoint that names its models has no server to swap, so the switch is the name the next request carries
        var route = ModelSwitch.RouteOf(ServedHere(), Endpoint.Models is { Count: > 0 }, Cloud);
        if (route == SwitchRoute.ByName)
        {
            if (Endpoint.Models is { Count: > 0 } offered && !offered.Contains(requestedModel, StringComparer.Ordinal))
                return new ModelSwitchResult(false, requestedModel, null, null,
                    $"endpoint {EndpointName} has no model '{requestedModel}'. Models: {string.Join(", ", offered)}", null, null);
            //re-resolved for the new name, so its folder's parts replace the old one's and a model with no folder drops them
            var previousSession = SessionModel;
            SessionModel = requestedModel;
            RoleResolution switched;
            try { switched = ResolveRole(Role.Name); }
            catch (GattoConfigException ex)
            {
                SessionModel = previousSession;
                return new ModelSwitchResult(false, requestedModel, null, null, ex.Message, null, null);
            }
            var systemBefore = LastComposedSystem;
            Role = switched.Role; Model = switched.Model; ModelString = switched.ModelString; Comp = switched.Comp;
            _loop.TraceRole = Role.Name;   //a /model can swap the role too, so the next turn span follows it
            LastComposedSystem = Comp.SystemText;
            LastComposedSources = Comp.Sources;
            //re-derived for the new model by the launch's own expression, or a switch between shapes keeps the old map's toggle or levels
            ReasoningMap = Model?.Profile.Thinking ?? Endpoint.Thinking;
            BinaryOn = Gatto.Roles.Models.Thinking.BinaryOnLevel(ReasoningMap);
            ThinkCap = Gatto.Roles.Models.Thinking.CapabilityOf(ReasoningMap, LaunchServing?.Capability ?? ThinkCapability.None);
            if (ThinkCap == ThinkCapability.Toggle) { ThinkOn = DefaultThinkOn(Model); ApplyToggle(ThinkOn); }
            else
            {
                //the new model takes its own level as a launch would, never the one the previous model was on
                var switchedLevel = Role.ThinkingRequested ?? SavedCloudEffortOrNull(requestedModel)
                    ?? Model?.Profile.DefaultEffort ?? ThinkingLevel.Medium;
                SetEffort(EffortSwitch.Name(switchedLevel), false);
            }
            _loop.UpdateOverrides(ModelString, Comp.Sampling, Comp.ThinkingBody, Comp.ThinkingSuffix);
            //the system text changes only when a folder's parts came or went, else only the baseline moves so a later continue finds the model the session ended on
            return new ModelSwitchResult(true, requestedModel,
                string.Equals(systemBefore, Comp.SystemText, StringComparison.Ordinal) ? null : Comp.SystemText,
                ContextBudget, null, null,
                ThinkingFooterName(), ThinkCap == ThinkCapability.Toggle, ThinkingUnavailable(), NextBaseline(LastComposedSources));
        }

        //refused here as well as at the Repl's gate, so a remote endpoint never reaches the serving machinery below
        if (route == SwitchRoute.Refuse)
            return new ModelSwitchResult(false, requestedModel, null, null, UnmanagedSession.ModelUnavailable(LaunchBaseUrl), null, null);

        IReadOnlyList<(string Path, string Content)> contextFiles;
        //a malformed .gatto.json fails the switch and leaves the session as it was, nothing is armed yet
        try
        {
            contextFiles = ContextFiles.Collect(
                cwd, config.ContextCompat, config.ContextHome ? Path.Combine(home, "GATTO.md") : null);
        }
        catch (GattoConfigException ex)
        {
            return new ModelSwitchResult(false, requestedModel, null, null, ex.Message, null, null);
        }

        //the index is re-read against the model being switched to, so the load is a delegate rather than a value read up front
        var outcome = ModelSwitch.Decide(
            modelsDir, home, requestedModel, Role, contextFiles, Endpoint.Thinking, LaunchBaseUrl, cwd,
            loadMemory: LoadMemory,
            memoryNudge: memoryOn);
        if (!outcome.Success || outcome.Model is null || outcome.Composition is null)
            return new ModelSwitchResult(false, outcome.ModelId, outcome.SystemText, outcome.ContextBudget,
                outcome.Message, null, null);   //no probe on a failed switch, nothing changed

        //captured before the swap, since model still names the outgoing one and the deed's confirm and failure message name it
        var outgoing = Model;
        var serveManager = new ServeManager(home, ServeCommand.ServerBinaryFor(outgoing, config));

        //a delegate rather than the prompter, so the deed does not know which face answers (a driver can make it throw)
        Func<Gatto.Repl.Prompts.WizardAsk, string?>? deedAsk =
            _prompter is Gatto.Repl.Prompts.IWizardPrompter deedAsker
                ? a => GattoApp.Label(deedAsker.AskOne(a))
                : null;

        //one delegate, the starter is called twice (incoming and restore) and the restore path must stay fakeable
        var deed = new ModelSwitchDeed.Seams(
            Swap: (served, to) =>
            {
                var (back, cannotRestore) = ServeCommand.RestoreTarget(modelsDir, served);
                return ServeCommand.SwapServerAsync(serveManager, back, to, warn.Warn, glyphs, cannotRestore)
                    .GetAwaiter().GetResult();
            },
            Start: m => AutoServeAsk.StartAndWaitAsync(serveManager, m, warn.Warn, CancellationToken.None, glyphs)
                .GetAwaiter().GetResult());

        if (ModelSwitchDeed.Run(outcome, outgoing, serveManager,   //the serving change, then the persist, then the arm, in that order
                deedAsk, confirmed, warn.Warn, deed) is { } settled)   //confirmed means the wizard's done step already asked, and it reaches only the deed's swap confirm
            return settled;

        //read outcome.Model here, Model still names the outgoing model until the assignments below
        if (MemoryNag.ShouldWarn(MemoryBudget(outcome.Model), outcome.MemoryTruncatedLines))
            warn.Warn(GattoApp.MemoryTruncationWarning(outcome.MemoryTruncatedLines));
        Model = outcome.Model;
        Comp = outcome.Composition;
        LastComposedSystem = Comp.SystemText;   //updated, the degrade target must follow the newly armed model
        LastComposedSources = Comp.Sources;
        ModelString = outcome.Model.Id;
        ContextBudget = outcome.Model.Profile.Context;
        ReasoningHistory = outcome.Model.Profile.ReasoningHistory;
        //the switch arms the session's model, so a later /role sees it instead of the launch-time -m
        SessionModel = outcome.Model.Id;

        _loop.UpdateOverrides(ModelString, Comp.Sampling, Comp.ThinkingBody, Comp.ThinkingSuffix);
        _loop.UpdateContextBudget(ContextBudget);
        _loop.UpdateReasoningHistory(ReasoningHistory);
        _runAgent.UpdateContextBudget(ContextBudget);
        _runAgent.UpdateReasoningHistory(ReasoningHistory);

        //only after the switch is committed, a failed load or persist never reaches this line
        var loaded = ServeProbe.ProbeAsync(_probeHttp, LaunchBaseUrl!, CancellationToken.None, Endpoint.ApiKey, _probeDeadline)   //only after the persist succeeded, so a doomed switch spends no round trip
            .GetAwaiter().GetResult();
        var mismatch = ModelSwitch.DescribeProbe(outcome.Model, loaded, modelsDir);
        //the incoming model's server may split its context across slots too, the launch rule again
        if (loaded is not null && mismatch is null)
        {
            var window = ModelWindow.Resolve(outcome.Model.Profile.Context, loaded.NCtx);
            ContextBudget = window.Budget;
            _loop.UpdateContextBudget(ContextBudget);
            _runAgent.UpdateContextBudget(ContextBudget);
            if (window.Line is string windowLine) warn.Warn(windowLine);
        }

        //re-derived for the new model, or a switch between shapes would re-apply the old map's toggle body over the new one
        ReasoningMap = Model.Profile.Thinking ?? Endpoint.Thinking;
        BinaryOn = Gatto.Roles.Models.Thinking.BinaryOnLevel(ReasoningMap);
        //a failed probe leaves a model with no map inert, a model with a map is decided by the map either way
        ThinkCap = Gatto.Roles.Models.Thinking.CapabilityOf(ReasoningMap, loaded?.Capability ?? ThinkCapability.None);   //before the ApplyToggle below, and a map decides the capability whatever the server's template says
        //the incoming model's own persisted default decides what ThinkOn starts as
        if (ThinkCap == ThinkCapability.Toggle) { ThinkOn = DefaultThinkOn(Model); ApplyToggle(ThinkOn); }

        //adds a line on success, since outcome.Message is null there, and outgoing names the model the session was on
        return new ModelSwitchResult(true, outcome.ModelId, outcome.SystemText, ContextBudget,
            SwapConfirm.SwitchNotice(confirmed, outgoing?.Id, outcome.ModelId) ?? outcome.Message,
            loaded?.NCtx, ThinkingFooterName(), ThinkCap == ThinkCapability.Toggle,
            ThinkingUnavailable(), NextBaseline(Comp.Sources));
    }

    //read from disk on each open, since the launch snapshot would miss a default written this session or keep one whose write failed
    internal string? CurrentDefaultFor(string onEndpoint)
    {
        try { return GattoConfig.Load(home).ModelDefaultFor(onEndpoint); }
        catch (GattoConfigException) { return null; }   //a config that will not parse marks no default, and the rows still list
    }

    //the rows come ready-made since Repl cannot enumerate models, the loaded mark from the probe and current from the armed model
    internal IReadOnlyList<PickerItem> ListModels()
    {
        //an endpoint that names its models lists those, with nothing to probe
        if (!ServedHere() && Endpoint.Models is { Count: > 0 } offered)
            return ListedModelRows.Build(offered, ModelString, CurrentDefaultFor(EndpointName),
                id => ModelLevels.For(Endpoint.Thinking, SavedCloudEffortOrNull(id),
                    knownToggle: id == ModelString && ThinkCap == ThinkCapability.Toggle, thinkOnByDefault: config.Think != "off"));

        var loaded = LaunchBaseUrl is null
            ? null
            : ServeProbe.ProbeAsync(_probeHttp, LaunchBaseUrl, CancellationToken.None, Endpoint.ApiKey, _probeDeadline)
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
            var file = Path.GetFileNameWithoutExtension(p.Profile.ActivePath);
            var label = $"{id,-16} {ctx,6}   {file}";
            var isSession = string.Equals(id, Model?.Id, StringComparison.OrdinalIgnoreCase);
            //a map-less model's toggle is the server's sniff, known for the model in use alone
            var (levels, effort) = ModelLevels.For(p.Profile.Thinking, p.Profile.DefaultEffort,
                knownToggle: isSession && ThinkCap == ThinkCapability.Toggle, thinkOnByDefault: config.Think != "off");

            rows.Add(new PickerItem(id, label,
                Marked: Model.MatchesLoaded(p, loaded) == true,   //only a known match marks the row, unknown stays unmarked
                Current: string.Equals(id, Model?.Id, StringComparison.OrdinalIgnoreCase),
                //read from disk, so a session that switched models shows the two marks on different rows
                Default: string.Equals(id, currentDefault, StringComparison.OrdinalIgnoreCase),
                Columns: [ctx, file], Levels: levels, DefaultEffort: effort));
        }
        return rows;
    }

    //read fresh, since a level saved this session must show, and a file that will not load saves nothing to read
    internal ThinkingLevel? SavedCloudEffortOrNull(string modelId)
    {
        try { return SavedCloudEffort(GattoConfig.Load(home), EndpointName, modelId); }
        catch (GattoConfigException) { return null; }
    }

    //the level a picker marks as default, the local profile's or the endpoint entry's for the model in use
    internal ThinkingLevel? SavedEffortNow() => ServedHere() && Model is not null ? Model.Profile.DefaultEffort : SavedCloudEffortOrNull(ModelString);

    //the level goes into the local model's profile.json, or with no local model into the endpoint's entry under the model in use
    internal string PersistEffort(ThinkingLevel level)
    {
        try
        {
            if (ServedHere() && Model is not null)   //a folder used on another endpoint is never edited from there, its effort is saved per endpoint
            {
                ModelDefaultEffort.Set(modelsDir, Model.Id, level);
                Model = Model with { Profile = Model.Profile with { DefaultEffort = level } };   //so the picker's default mark does not lag the write
                return $", saved as {Model.Id}'s default";
            }
            GattoConfigWriter.SetEndpointEffort(home, EndpointName, ModelString, EffortSwitch.Name(level));
            return $", saved as {ModelString}'s default";
        }
        catch (GattoConfigException ex) { return $", not saved: {ex.Message}"; }
    }

    //d on /model writes the cursor row's own level, resolved through that model's map and never the session model's. null on success
    internal string? SetModelDefaultEffort(string id, string levelName)
    {
        try
        {
            if (!ServedHere() && Endpoint.Models is { Count: > 0 })
            {
                GattoConfigWriter.SetEndpointEffort(home, EndpointName, id, EffortSwitch.Name(ModelLevels.Resolve(Endpoint.Thinking, levelName)));
                return null;
            }
            var target = Model.Load(modelsDir, id);
            var level = ModelLevels.Resolve(target.Profile.Thinking, levelName);
            ModelDefaultEffort.Set(modelsDir, id, level);
            if (Model is { } inUse && string.Equals(inUse.Id, id, StringComparison.OrdinalIgnoreCase))
                Model = inUse with { Profile = inUse.Profile with { DefaultEffort = level } };   //so the marks do not lag the write
            return null;
        }
        catch (GattoConfigException ex) { return ex.Message; }
    }

    //re-resolves the thinking entry at the clamped level and mutates comp, so the footer and recompose read it, and a /role resets it
    internal EffortResult SetEffort(string levelArg, bool persist)
    {
        try
        {
            if (ThinkingUnavailable())   //a model with no reasoning switch refuses, there is nothing to apply or save
                return new EffortResult(false, null, "this model doesn't support changing the reasoning mode.");

            if (ThinkCap == ThinkCapability.Toggle)
            {
                //a Toggle model takes any argument: none or off means off, anything else means on
                var (on, toggleName) = EffortSwitch.OnToggle(levelArg);
                ThinkOn = on;
                var toggleMessage = $"effort: {toggleName}";
                //the map's own on-level when there is a map, Max when there is none, and DefaultThinkOn only asks whether the level is None
                toggleMessage += persist
                    ? PersistEffort(on ? (BinaryOn ?? ThinkingLevel.Max) : ThinkingLevel.None)
                    : ", only for this session";
                var toggled = ApplyToggle(on);
                return new EffortResult(true, toggled, toggleMessage, ThinkingOf(Comp));
            }

            var (effective, name, message) = EffortSwitch.Resolve(levelArg);
            var map = Model?.Profile.Thinking ?? Endpoint.Thinking;

            //nothing is ever sent with no map, so the level name stays null and the footer chip hidden
            if (map is null)
            {
                Comp = Comp with { Thinking = effective, ThinkingBody = null, ThinkingSuffix = null };
                _loop.UpdateOverrides(ModelString, Comp.Sampling, Comp.ThinkingBody, Comp.ThinkingSuffix);
                return new EffortResult(true, null,
                    $"effort: {name}, display only: no thinking map configured", ThinkingOf(Comp));
            }

            //the line and the chip report the level the map actually resolves to, and a null result is display-only like no map
            var landed = Thinking.LandedLevel(map, effective);
            if (landed is null)
            {
                Comp = Comp with { Thinking = effective, ThinkingBody = null, ThinkingSuffix = null };
                _loop.UpdateOverrides(ModelString, Comp.Sampling, Comp.ThinkingBody, Comp.ThinkingSuffix);
                return new EffortResult(true, null,
                    $"effort: {name}, display only: no thinking map entry at or below this level", ThinkingOf(Comp));
            }

            var (body, suffix) = Thinking.ResolveEntry(map, effective);
            var landedName = EffortSwitch.Name(landed.Value);
            Comp = Comp with { Thinking = landed.Value, ThinkingBody = body, ThinkingSuffix = suffix };
            _loop.UpdateOverrides(ModelString, Comp.Sampling, Comp.ThinkingBody, Comp.ThinkingSuffix);

            //a level the map moved says so and names the level asked of the map, and one it left keeps the cap message
            var landedMessage = landed.Value == effective
                ? message
                : $"effort: {landedName}, requested {name}, the nearest level this model declares";   //when the cap and the map both move it, only the map's move is reported, and the chip still matches what was sent

            //persist the resolved level, the s key and a switch's own apply are the session-only ways
            landedMessage += persist ? PersistEffort(landed.Value) : ", only for this session";

            return new EffortResult(true, landedName, landedMessage, ThinkingOf(Comp));
        }
        catch (GattoConfigException ex) { return new EffortResult(false, null, ex.Message); }
    }

    //one row per level this model's map declares, or none and on for a Toggle, and empty when there is nothing to offer
    internal IReadOnlyList<PickerItem> ListEfforts()   //built fresh on each open, so a /model shows at the next /effort
    {
        if (ThinkCap == ThinkCapability.Toggle)
            return new List<PickerItem>
            {
                new("none", "none", Marked: !ThinkOn, Current: !ThinkOn,
                    Default: SavedEffortNow() == ThinkingLevel.None, Sentence: ModelLevels.NoneSentence),
                //any persisted level other than None means on was saved, a map-less model stores the Max sentinel
                new("on", "on", Marked: ThinkOn, Current: ThinkOn,
                    Default: SavedEffortNow() is { } d && d != ThinkingLevel.None, Sentence: ModelLevels.OnSentence),
            };

        //no map means no rows, and a degenerate map with a lone on-level still has rows to show
        if ((Model?.Profile.Thinking ?? Endpoint.Thinking) is not { } effortMap) return Array.Empty<PickerItem>();

        var savedLevel = SavedEffortNow();
        return effortMap.Keys
            .Select(Thinking.Parse)
            .OrderBy(l => l)
            .Select(l =>
            {
                var levelName = EffortSwitch.Name(l);
                var (body, suffix) = Thinking.ResolveEntry(effortMap, l);
                return new PickerItem(levelName, levelName,
                    Marked: l == Comp.Thinking, Current: l == Comp.Thinking,
                    Default: savedLevel == l, Sentence: ModelLevels.Sentence(body, suffix, glyphs));
            })
            .ToList();
    }

    //a replay keeps the thinking the session was sent with, through the effort switch so the loop, the footer and the children's GATTO_EFFORT agree
    internal void ResumeThinking(Conversation resumed, ThinkingMark stored)
    {
        var level = ThinkCap == ThinkCapability.Toggle
            ? (stored.BodyJson == ThinkingMark.CompactJson(ToggleEntry(true).Body?.GetRawText()) ? "on" : "none")
            : stored.Level;
        if (level is null) return;
        if (SetEffort(level, false).Thinking is { } applied) resumed.SetThinking(applied);
        Environment.SetEnvironmentVariable("GATTO_EFFORT", EffortSwitch.Name(Comp.Thinking));
    }

    //the model's own instructions as the composition holds them, for the note that says they changed
    internal string? ModelAppendText() => string.IsNullOrWhiteSpace(Model?.SystemAppend) ? null : Model.SystemAppend;

    //the checks /role makes, on the called role merged with the one this session runs, so the child never falls back
    internal string? RefuseRole(RoleFile called)
    {
        var merged = RoleMerge.ForChild(called, Role);
        //another endpoint is refused by name before its model resolves, since the merge hands it the caller's model and that refusal would name the wrong thing
        var wanted = launchEndpoint ?? merged.Endpoint ?? config.DefaultEndpoint;
        if (endpoints.TryGetValue(wanted, out var wantedEp) && wantedEp.BaseUrl is not null   //a local endpoint with no base_url takes the model's port, which only the full resolution knows
            && !string.Equals(wantedEp.BaseUrl, LaunchBaseUrl, StringComparison.OrdinalIgnoreCase))
            return $"role '{called.Name}' wants endpoint '{wanted}', this session is on '{EndpointName}'";
        RoleResolution res;
        try { res = ResolveLoaded(merged); }
        catch (GattoConfigException ex) { return ex.Message; }
        //the model first, since on local with no base_url each model has its own port and another model also differs in URL
        if (EndpointName == "local" && !string.Equals(res.Model?.Id, Model?.Id, StringComparison.OrdinalIgnoreCase))
            return $"role '{called.Name}' wants model '{res.ModelString}', this session serves '{ModelString}'";
        if (!string.Equals(res.Endpoint.BaseUrl, LaunchBaseUrl, StringComparison.OrdinalIgnoreCase))
            return $"role '{called.Name}' wants endpoint '{res.EndpointName}', this session is on '{EndpointName}'";
        return null;
    }

    //a child is a fresh conversation, so the context files read at its call move no prefix the caller holds. no extension tool reaches it, so no policy line does
    internal (Composition Comp, string ModelString) ComposeChild(RoleFile called)
    {
        var merged = RoleMerge.ForChild(called, Role);
        if (args.Effort is { } flagEffort) merged = merged with { ThinkingRequested = flagEffort };
        var files = merged.ContextFiles
            ? ContextFiles.Collect(cwd, config.ContextCompat, config.ContextHome ? Path.Combine(home, "GATTO.md") : null)
            : Array.Empty<(string Path, string Content)>();
        if (called.Model is null)
            return (RoleComposition.Compose(merged, Model, files, Endpoint.Thinking, cwd: cwd, date: DateTime.Now,
                memoryIndex: null, memoryNudge: false, policyLines: null,
                savedEffort: EndpointName == "local" ? null : SavedCloudEffort(config, EndpointName, ModelString)), ModelString);
        //the refusal check resolved the same merged role before this runs, so a throw here is a file changed in between and the loop reports it
        var res = ResolveLoaded(called.ThinkingRequested is null ? merged with { ThinkingRequested = Comp.Thinking } : merged);   //another model with no level of its own takes the session's, and --effort still wins inside
        return (RoleComposition.Compose(res.Role, res.Model, files, res.Endpoint.Thinking, cwd: cwd, date: DateTime.Now,
            memoryIndex: null, memoryNudge: false, policyLines: null,
            savedEffort: res.EndpointName == "local" ? null : SavedCloudEffort(config, res.EndpointName, res.ModelString)), res.ModelString);
    }

    //what every run_agent request carries, read at call time so a child follows the last switch
    internal (string Model, JsonElement? Sampling, JsonElement? Body, string? Suffix) RequestSettings() =>
        (ModelString, Comp.Sampling, Comp.ThinkingBody, Comp.ThinkingSuffix);
}
