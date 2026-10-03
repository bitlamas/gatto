using System.Text.Json;
using Gatto.Core.Home;
using Gatto.Core.Memory;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;
using Gatto.Extensions;
using Gatto.Roles;
using Gatto.Terminal;

namespace Gatto.Cli;

//every failed line ends with a fix to type, one line per check, and a single failure makes the exit non-zero
public sealed class Doctor(
    HttpClient http,
    Func<string, ProbeResult>? probeBinary = null,
    Func<Gatto.Roles.RunningInfo?>? describeRunning = null,
    Theme? theme = null,
    //the update fetch is injected like every outside-world seam here, null adds no line so the test suite stays hermetic
    Func<CancellationToken, Task<UpdateState?>>? checkUpdate = null,
    //the historical set behind the extensions check, a test injects it and production passes null
    IReadOnlySet<string>? shippedHistorical = null)
{
    //info lines draw as i and never affect the exit code, Ok true keeps them out of the fail count
    private sealed record CheckResult(bool Ok, string Text, bool Info = false);

    //each network check holds its own deadline, since the client carries none
    private static readonly TimeSpan ReadDeadline = TimeSpan.FromSeconds(5);

    private static CancellationTokenSource DeadlineFor(CancellationToken ct)
    {
        var read = CancellationTokenSource.CreateLinkedTokenSource(ct);
        read.CancelAfter(ReadDeadline);
        return read;
    }

    public async Task<int> RunAsync(string home, string cwd, TextWriter output, CancellationToken ct)
    {
        var configPath = Path.Combine(home, "gatto.json");
        var modelsDir = Path.Combine(home, "models");
        var rolesDir = Path.Combine(home, "roles");
        var extensionsDir = Path.Combine(home, "extensions");

        GattoConfig? config = null;
        string? configError = null;
        try { config = GattoConfig.Load(home); }
        catch (GattoConfigException ex) { configError = ex.Message; }

        var modelIds = Model.ListIds(modelsDir);
        var loadedModels = new Dictionary<string, (Model? Model, string? Error)>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in modelIds)
        {
            try { loadedModels[id] = (Model.Load(modelsDir, id), null); }
            catch (GattoConfigException ex) { loadedModels[id] = (null, ex.Message); }
        }

        //the glyph set is resolved once here and handed to every check that draws a mark, the same resolver the banner uses
        var g = CommandBanner.GlyphsFor(home);

        //the checks print in the numbered order, and config and models are resolved once above so the checks that need them never re-parse
        var results = new List<CheckResult>
        {
            await CheckServerAsync(config, loadedModels, g, ct).ConfigureAwait(false),
        };

        //every non-local endpoint with a base_url gets a reachability check, the local one keeps CheckServerAsync
        if (config is not null)
            foreach (var (name, ep) in config.Endpoints)
                if (name != "local" && ep.BaseUrl is not null)
                    results.Add(await CheckEndpointReachableAsync(name, ep, g, ct).ConfigureAwait(false));

        results.AddRange(new List<CheckResult>
        {
            CheckConfig(config, configError, configPath, g),
            CheckModels(modelsDir, Gatto.Core.Acquire.ModelLocation.SuggestedDir(home, config?.WeightsRoot),
                modelIds, loadedModels, config, g),
            CheckRoles(rolesDir, config, g),
            CheckExtensions(home, extensionsDir, g, shippedHistorical),
            CheckLlamaServer(config, modelIds, g),
            CheckPermissions(home, cwd, g),
            CheckContextFiles(home, cwd, config, g),
            CheckProjectFiles(cwd, g),
            CheckMemory(cwd, config, g),
        });

        //info lines are appended after the pass and fail block so they stay grouped, a null producer contributes no line
        if (CheckDownloadsLocation(loadedModels, g) is { } dlLine) results.Add(dlLine);
        if (CheckAutoCompactVeto(cwd, config, g) is { } acLine) results.Add(acLine);
        if (CheckRunningServer(loadedModels, g) is { } runLine) results.Add(runLine);
        if (CheckSingleServerConsistency(rolesDir, config, loadedModels, g) is { } oneLine) results.Add(oneLine);
        //doctor checks for updates whatever update_check says, typing the command is the consent
        if (checkUpdate is not null)
            results.Add(await CheckUpdateAsync(checkUpdate, SelfInstall.IsInstalled(), ct).ConfigureAwait(false));

        //no version header here, the banner every command opens with already says it
        foreach (var r in results)
            output.WriteLine(r.Info
                ? Paint("i", Theme.Dim) + " " + r.Text
                : Paint(r.Ok ? g.Ok : g.Bad, r.Ok ? Theme.Ok : Theme.Err) + " " + r.Text);

        return results.All(r => r.Ok) ? 0 : 1;
    }


    //an info line even when the version is behind, that is not a fault of this machine. the budget is 1s for the whole exchange and 500ms to connect
    private static async Task<CheckResult> CheckUpdateAsync(
        Func<CancellationToken, Task<UpdateState?>> fetch, bool installed, CancellationToken ct)
    {
        var state = await fetch(ct).ConfigureAwait(false);

        if (state is null)
            //quiet and true: gatto could not ask, and that is not a failure of the setup
            return new CheckResult(true, "updates: couldn't reach GitHub just now", Info: true);

        return new CheckResult(true,
            UpdateCheck.Line(state, Gatto.Core.GattoVersion.String, installed) is { } line
                ? "updates: " + line
                : "updates: " + UpdateCheck.SilenceNote(state.Latest, Gatto.Core.GattoVersion.String),
            Info: true);
    }

    //only the glyph is painted, a fix line reads best in the terminal's foreground. a null theme hands the text back plain, so a piped doctor stays clean
    private string Paint(string s, RgbColor color) => theme is null ? s : theme.Paint(s, color);

    //1. server reachable

    private async Task<CheckResult> CheckServerAsync(
        GattoConfig? config, IReadOnlyDictionary<string, (Model? Model, string? Error)> loadedModels, Gatto.Terminal.GlyphSet g, CancellationToken ct)
    {
        if (config is null)
            return new CheckResult(false,
                "server reachable: cannot determine " + g.Dot + " gatto.json failed to load " + g.Dot + " fix gatto.json (see the check below), then run: gatto doctor");

        if (!config.Endpoints.TryGetValue("local", out var localEp))
            return new CheckResult(true, "server reachable: skipped " + g.Dot + " no \"local\" endpoint configured (cloud-only setup)");

        string url;
        //the local endpoint's own entry, since the default endpoint's model may belong to a cloud endpoint
        var localModel = config.ModelDefaultFor("local");
        var modelHint = localModel ?? "<model>";
        if (localEp.BaseUrl is not null)
        {
            url = localEp.BaseUrl;
        }
        else if (localModel is null)
        {
            return new CheckResult(false,
                "server reachable: no local.base_url and no defaults.local.model to derive a port from " + g.Dot + " " +
                "run: set defaults.local.model in gatto.json, or set an explicit \"base_url\" on the \"local\" endpoint");
        }
        else if (!loadedModels.TryGetValue(localModel, out var active) || active.Model is null)
        {
            var reason = loadedModels.TryGetValue(localModel, out var e) ? e.Error : "no such model";
            return new CheckResult(false,
                $"server reachable: cannot derive a port {g.Dot} model '{localModel}' failed to load ({reason}) {g.Dot} " +
                $"run: gatto serve start {modelHint} after fixing the model");
        }
        else
        {
            url = $"http://127.0.0.1:{active.Model.Profile.Port}";
        }

        using var read = DeadlineFor(ct);
        try
        {
            using var resp = await http.GetAsync(url.TrimEnd('/') + "/health", read.Token).ConfigureAwait(false);
            return resp.IsSuccessStatusCode
                ? new CheckResult(true, $"server reachable: {url}")
                : new CheckResult(false, $"server reachable: {url} responded {(int)resp.StatusCode} {g.Dot} run: gatto serve start {modelHint}");
        }
        catch (Exception)
        {
            return new CheckResult(false, $"server reachable: {url} unreachable {g.Dot} run: gatto serve start {modelHint}");
        }
    }

    //1b. every non-local endpoint with a base_url answers /v1/models

    private async Task<CheckResult> CheckEndpointReachableAsync(
        string name, Gatto.Core.Client.EndpointConfig ep, Gatto.Terminal.GlyphSet g, CancellationToken ct)
    {
        var url = ep.BaseUrl!.TrimEnd('/') + "/v1/models";
        using var read = DeadlineFor(ct);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            Gatto.Core.Client.EndpointAuth.Apply(req, ep);     //key_env'd endpoints must not 401 falsely
            using var resp = await http.SendAsync(req, read.Token).ConfigureAwait(false);

            if (resp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new CheckResult(false,
                    $"endpoint '{name}': {url} rejected auth {g.Dot} set key_env in gatto.json if this server needs a key, " +
                    "or check this is the right port (wrong port: something else may own it) " + g.Dot + " then run: gatto doctor");

            var body = await resp.Content.ReadAsStringAsync(read.Token).ConfigureAwait(false);
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("data", out var data) &&
                    data.ValueKind == System.Text.Json.JsonValueKind.Array)
                    return new CheckResult(true, $"endpoint '{name}': {url} answers ({data.GetArrayLength()} model(s))");
            }
            catch (System.Text.Json.JsonException) { }
            return new CheckResult(false,
                $"endpoint '{name}': {url} answered but is not OpenAI-shaped {g.Dot} something else owns this port {g.Dot} " +
                "run: check the port in gatto.json");
        }
        catch (Exception)
        {
            return new CheckResult(false,
                $"endpoint '{name}': {url} unreachable {g.Dot} server not running / wrong port {g.Dot} run: start your server, then: gatto doctor");
        }
    }


    //info: the model file lives in Downloads

    //the folder and the containment rule come from ModelLocation, which the wizard's move offer asks too
    private static CheckResult? CheckDownloadsLocation(
        IReadOnlyDictionary<string, (Model? Model, string? Error)> loadedModels, Gatto.Terminal.GlyphSet g)
    {
        var dl = Gatto.Core.Acquire.ModelLocation.DownloadsDir;
        foreach (var (id, (model, _)) in loadedModels)
            if (model is not null && Gatto.Core.Acquire.ModelLocation.IsInside(dl, model.Profile.ActivePath))
                return new CheckResult(true,
                    $"model '{id}': model file is inside Downloads, which Windows can be set to clean automatically {g.Dot} " +
                    "consider moving it and updating the model's files", Info: true);
        return null;
    }

    //info: auto-compaction vetoed by a project file

    //names the project file that turned auto-compaction off, and stays silent when gatto.json did it. it resolves through EffectiveAutoCompact, the Repl's own call
    private static CheckResult? CheckAutoCompactVeto(string cwd, GattoConfig? config, Gatto.Terminal.GlyphSet g)
    {
        if (config?.AutoCompact is null) return null;

        try
        {
            if (ProjectFileConfig.EffectiveAutoCompact(cwd, configured: true, out var disabledBy)
                || disabledBy is null)
                return null;

            return new CheckResult(true,
                $"auto-compaction: disabled by {Short(disabledBy, cwd)} {g.Dot} long sessions will elide instead",
                Info: true);
        }
        catch (GattoConfigException) { return null; }
    }

    //info: a gatto-managed server is running

    private CheckResult? CheckRunningServer(
        IReadOnlyDictionary<string, (Model? Model, string? Error)> loadedModels, Gatto.Terminal.GlyphSet g)
    {
        if (describeRunning?.Invoke() is not { } run) return null;
        //the size is the whole model's, so sum the shard set rather than price the active file
        var size = "";
        if (loadedModels.TryGetValue(run.Model, out var p) && p.Model is not null
            && Gatto.Core.Acquire.ModelDiscovery.SetBytesOrNull(p.Model.Profile.ActivePath) is { } bytes)
            size = ", " + SizeWords.Gb(bytes, approx: true);
        return new CheckResult(true,
            $"llama-server is running {g.Dot} {run.Model} (port {run.Port}, started {run.Started}{size}) {g.Dot} gatto serve stop to eject",
            Info: true);
    }

    //info: two roles target different gatto-managed servers. delete this line when several servers are supported, a stopgap built from what doctor already has

    private static CheckResult? CheckSingleServerConsistency(
        string rolesDir, GattoConfig? config,
        IReadOnlyDictionary<string, (Model? Model, string? Error)> loadedModels, Gatto.Terminal.GlyphSet g)
    {
        if (config is null) return null;
        var byPort = new Dictionary<int, string>();   //port to the first role name seen
        foreach (var name in RoleFile.ListNames(rolesDir))
        {
            RoleFile role;
            try { role = RoleFile.Load(rolesDir, name); }
            catch (GattoConfigException) { continue; }
            var endpointName = role.Endpoint ?? config.DefaultEndpoint;
            if (endpointName != "local") continue;
            var model = RoleFile.EffectiveModel(role, null, config.ModelDefaultFor("local"));
            if (model is null || !loadedModels.TryGetValue(model, out var loaded) || loaded.Model is null) continue;
            var port = loaded.Model.Profile.Port;
            if (!byPort.ContainsKey(port)) byPort[port] = name;
            if (byPort.Count > 1)
            {
                var two = byPort.Values.Take(2).ToArray();
                return new CheckResult(true,
                    $"roles '{two[0]}' and '{name}' target different gatto-managed servers; gatto serves " +
                    "one at a time " + g.Dot + " switching needs gatto serve stop first", Info: true);
            }
        }
        return null;
    }

    //2. gatto.json parses and has no unknown keys

    private static CheckResult CheckConfig(GattoConfig? config, string? error, string configPath, Gatto.Terminal.GlyphSet g) =>
        config is not null
            ? new CheckResult(true, $"gatto.json parses ({configPath})")
            : new CheckResult(false, $"gatto.json: {error} {g.Dot} fix: edit {configPath}, then run: gatto doctor");

    //3. models consistent

    private static CheckResult CheckModels(
        string modelsDir, string weightsRoot, IReadOnlyList<string> modelIds,
        IReadOnlyDictionary<string, (Model? Model, string? Error)> loadedModels, GattoConfig? config, Gatto.Terminal.GlyphSet g)
    {
        if (modelIds.Count == 0)
            return new CheckResult(true,
                $"models: none configured under {modelsDir}, weights root {weightsRoot} (nothing to check)");

        var problems = new List<string>();
        foreach (var id in modelIds)
        {
            var (model, error) = loadedModels[id];
            if (error is not null) { problems.Add($"'{id}': {error}"); continue; }
            //shared with audition's pre-flight, and shard-aware because a split model is the whole set, so a missing shard cannot serve
            var gone = Gatto.Core.Acquire.ModelDiscovery.MissingFiles(
                model!.Profile.ActivePath, model.Profile.MmProj);
            if (gone.Model is { Count: > 0 } weights)
                problems.Add($"'{id}': model file not found at {weights[0]}"
                    + (weights.Count > 1 ? $" (and {weights.Count - 1} more of its shards)" : ""));
            //the projector gets its own sentence naming it, a missing mmproj starts a server that dies inside llama.cpp
            if (gone.Projector is { Count: > 0 } projector)
                problems.Add($"'{id}': the vision projector it loads is not found at {projector[0]}");
        }

        //an explicit local.base_url port must match the active model's port, or gatto talks to the wrong server
        if (config is not null
            && config.Endpoints.TryGetValue("local", out var localEp)
            && localEp.BaseUrl is not null
            && config.DefaultModel is not null
            && loadedModels.TryGetValue(config.DefaultModel, out var active)
            && active.Model is not null
            && Uri.TryCreate(localEp.BaseUrl, UriKind.Absolute, out var uri)
            && uri.Port != active.Model.Profile.Port)
        {
            problems.Add(
                $"local.base_url port {uri.Port} does not match model '{config.DefaultModel}' port {active.Model.Profile.Port}");
        }

        return problems.Count == 0
            //both folders get named: profiles under models, weight files under the weights root the user can move
            ? new CheckResult(true,
                $"models: {modelIds.Count} model(s) under {modelsDir}, weights root {weightsRoot}, all consistent")
            : new CheckResult(false,
                $"models: {string.Join("; ", problems)} " + g.Dot + " fix the listed model(s) or their files under {modelsDir}, or gatto.json's local.base_url");
    }

    //4. roles parse and validate

    private static CheckResult CheckRoles(string rolesDir, GattoConfig? config, Gatto.Terminal.GlyphSet g)
    {
        var names = RoleFile.ListNames(rolesDir);
        if (names.Count == 0)
        {
            //derive this path from the home under check, a fixed default sends the user to a home they are not using
            var manifest = Path.Combine(Path.GetDirectoryName(rolesDir) ?? "", ".shipped");
            return new CheckResult(false, $"roles: no role files found under {rolesDir} {g.Dot} run: gatto to write them (a prior deletion sticks: remove the roles/* lines from {manifest} first)");
        }

        if (config is null)
            return new CheckResult(false,
                "roles: cannot validate " + g.Dot + " gatto.json failed to load " + g.Dot + " fix gatto.json (see the check below), then run: gatto doctor");

        var problems = new List<string>();
        foreach (var name in names)
        {
            RoleFile role;
            try { role = RoleFile.Load(rolesDir, name); }
            catch (GattoConfigException ex) { problems.Add($"'{name}': {ex.Message}"); continue; }

            var endpointName = role.Endpoint ?? config.DefaultEndpoint;
            //doctor runs no extension, so an endpoint outside gatto.json may be a contributed one and is left to the launch
            if (!config.Endpoints.ContainsKey(endpointName)) continue;

            //the same EffectiveModel precedence the launch uses, and a cloud role with no model fails here too, so the condition needs no isLocal guard
            var effectiveModel = RoleFile.EffectiveModel(role, null, config.ModelDefaultFor(endpointName));
            if (effectiveModel is null)
            {
                //a role with no model is the normal unconfigured state, so the line names the first-run fix
                problems.Add($"'{name}': no model configured yet {g.Dot} set defaults.{endpointName}.model in gatto.json, " +
                    "add \"model\" to this role, or launch with -m <model>");
            }
        }

        return problems.Count == 0
            ? new CheckResult(true, $"roles: {names.Count} role(s) under {rolesDir} all valid")
            : new CheckResult(false, $"roles: {string.Join("; ", problems)} " + g.Dot + $" fix the listed role file(s) under {rolesDir}");
    }

    //extensions compile

    private static CheckResult CheckExtensions(string home, string extensionsDir, Gatto.Terminal.GlyphSet g,
        IReadOnlySet<string>? shippedHistorical)
    {
        var failures = ExtensionHost.CompileCheck(extensionsDir);   //compiles the script, nothing runs it
        var count = ExtensionDiscovery.Discover(extensionsDir).Count;
        if (failures.Count == 0)
            return new CheckResult(true, $"extensions: {count} script(s) compile");

        //doctor stays read-only, so an older unmodified shipped copy that fails to compile reads as the next launch's repair
        var stale = shippedHistorical is null
            ? ShippedExtensions.StaleShippedPaths(home)
            : ShippedExtensions.StaleShippedPaths(home, ShippedExtensions.Files, shippedHistorical);

        var blocking = new List<string>();
        var excused = new List<string>();
        foreach (var f in failures)
        {
            var hit = stale.FirstOrDefault(s => f.StartsWith(
                Path.GetFullPath(Path.Combine(home, s.Replace('/', Path.DirectorySeparatorChar))),
                StringComparison.OrdinalIgnoreCase));
            if (hit is null) { blocking.Add(f); continue; }
            var name = Path.GetFileName(hit);
            if (!excused.Contains(name, StringComparer.Ordinal)) excused.Add(name);
        }

        if (blocking.Count > 0)
            return new CheckResult(false,
                $"extensions: {string.Join("; ", blocking)} " + g.Dot + $" fix the listed extension script(s) under {extensionsDir}");

        var named = string.Join("; ", excused.Select(e => $"{e} is an older shipped copy, the next gatto launch updates it"));
        return new CheckResult(true, $"extensions: {count} script(s) compile " + g.Dot + $" {named}");
    }

    //5. llama_server is set and exists (skipped with no local model configured)

    private CheckResult CheckLlamaServer(GattoConfig? config, IReadOnlyList<string> modelIds, Gatto.Terminal.GlyphSet g)
    {
        if (modelIds.Count == 0)
            return new CheckResult(true, "llama_server: skipped " + g.Dot + " no local models configured");

        if (config is null)
            return new CheckResult(false,
                "llama_server: cannot check " + g.Dot + " gatto.json failed to load " + g.Dot + " fix gatto.json (see the check below), then run: gatto doctor");

        if (string.IsNullOrWhiteSpace(config.LlamaServer))
            return new CheckResult(false, "llama_server: not set " + g.Dot + " run: set \"llama_server\" in gatto.json to your llama-server.exe path");

        if (!File.Exists(config.LlamaServer))
            return new CheckResult(false,
                $"llama_server: {config.LlamaServer} does not exist {g.Dot} run: fix \"llama_server\" in gatto.json to point at your llama-server.exe");

        //probe --version under a 5 second deadline, existence is not shape, and probeBinary is the test seam while production runs the real probe
        var probe = (probeBinary ?? (path => LlamaServerProbe.Run(path, TimeSpan.FromSeconds(5))))(config.LlamaServer);
        return probe.Shape switch
        {
            ProbeShape.ClassicServer => new CheckResult(true, $"llama_server: {config.LlamaServer} ({probe.Detail})"),
            ProbeShape.TimedOut => new CheckResult(false,
                $"llama_server: {config.LlamaServer} {g.Dot} {probe.Detail} {g.Dot} run: check this is llama-server.exe and not a hung binary, then: gatto doctor"),
            ProbeShape.NotClassic => new CheckResult(false,
                $"llama_server: {config.LlamaServer} {g.Dot} {probe.Detail}"),
            //the config names one of the release's other programs, so name the server file that belongs there
            ProbeShape.KnownSibling when probe.SiblingName is { Length: > 0 } sibling =>
                new CheckResult(false,
                    $"llama_server: {config.LlamaServer} {g.Dot} that is {sibling}, not the server {g.Dot} run: "
                    + $"point it at {Gatto.Core.Acquire.LlamaSiblings.ServerName} in the same folder, "
                    + "then: gatto doctor"),
            //the missing C++ runtime is the one cause named here, it rests on an exclusion rather than a guess
            ProbeShape.DllNotFound when probe.VcRuntimeAbsent => new CheckResult(false,
                $"llama_server: {config.LlamaServer} {g.Dot} {probe.Detail} {g.Dot} run: install the Microsoft "
                + "\"Visual C++ Redistributable (x64)\", then: gatto doctor"),
            //no discriminator means no cause, so hint at the folder rather than assert CUDA
            ProbeShape.DllNotFound => new CheckResult(false,
                $"llama_server: {config.LlamaServer} {g.Dot} {probe.Detail} {g.Dot} run: check everything from "
                + "the download was extracted into the same folder (a CUDA build also needs its "
                + "cudart zip extracted beside it), then: gatto doctor"),
            _ => new CheckResult(false,
                $"llama_server: {config.LlamaServer} {g.Dot} {probe.Detail} {g.Dot} run: check the binary is the classic llama-server.exe, then: gatto doctor"),
        };
    }

    //6. this folder's grants in the home parse

    private static CheckResult CheckPermissions(string home, string cwd, Gatto.Terminal.GlyphSet g)
    {
        var path = PermissionStore.PathFor(home, cwd);
        if (!File.Exists(path))
            return new CheckResult(true, "permissions: none granted for this folder");

        //reuse the warning Load already produces as the failure text, rather than parsing the file again
        PermissionStore.Load(home, cwd, out var warning);
        return warning is null
            ? new CheckResult(true, $"permissions: {path}")
            : new CheckResult(false, $"{warning} {g.Dot} fix or delete {path}, then run: gatto doctor");
    }

    //7. context files: the effective collected set

    //the files actually loaded for this folder, through the same Collect call the launch makes. paths in load order, the nearest last
    private static CheckResult CheckContextFiles(string home, string cwd, GattoConfig? config,
        Gatto.Terminal.GlyphSet g)
    {
        var defaultPath = Path.Combine(home, "GATTO.md");
        if (config is null)
            return new CheckResult(false,
                "context files: cannot determine " + g.Dot + " gatto.json failed to load " + g.Dot + " fix gatto.json (see the gatto.json check above), then run: gatto doctor");

        IReadOnlyList<(string Path, string Content)> files;
        bool effectiveCompat;
        try
        {
            files = ContextFiles.Collect(
                cwd, config.ContextCompat, config.ContextHome ? defaultPath : null, out effectiveCompat);
        }
        catch (GattoConfigException ex)
        {
            //a malformed .gatto.json is the one throwing path, and with no effective set this check cannot do its job
            return new CheckResult(false,
                $"context files: cannot be collected {g.Dot} {ex.Message} {g.Dot} fix the .gatto.json named below, then run: gatto doctor");
        }

        if (files.Count > 0)
        {
            //ask the set whether it holds the home file (the home folder can be an ancestor of cwd, then it's in the set too)
            var listsHome = files.Any(f => PathsEqual(f.Path, defaultPath));
            var note = listsHome ? "" : config.ContextHome ? HomeAbsentNote(defaultPath, g) : $" {g.Dot} home layer off";
            note += CwdFileNote(g, cwd, files, config.ContextCompat, config.ContextHome ? defaultPath : null);
            //name the shadowed file, a count of 1 loaded reads as if compat did something. the shadow check stays unconditional, effective compat is per-directory
            var shadowed = ContextFiles.ShadowedFallbacks(files.Select(f => f.Path));
            if (shadowed.Count > 0)
                note += $" {g.Dot} shadowed by GATTO.md: " + string.Join(", ", shadowed.Select(p => Short(p, cwd)));
            return new CheckResult(true,
                $"context files: {files.Count} loaded for {Tilde(cwd)} (compat: {(effectiveCompat ? "true" : "false")}) " + g.Dot + " "
                + string.Join($" {g.Right} ", files.Select(f => Short(f.Path, cwd))) + note);
        }

        //the empty arm is green and uses the same WhyMissing classification as the non-empty one, so a comment-only home file is never denied
        var cwdNote = CwdFileNote(g, cwd, files, config.ContextCompat, config.ContextHome ? defaultPath : null);
        return config.ContextHome
            ? new CheckResult(true, EmptySetLine(cwd, defaultPath, g) + cwdNote)
            : new CheckResult(true,
                $"context files: none loaded for {Tilde(cwd)} {g.Dot} no GATTO.md here or above it, and context_files.home is false {g.Dot} "
                + "run: write a GATTO.md for this project, or set home back to true in gatto.json" + cwdNote);
    }

    //the reason comes from ContextFiles.WhyMissing, so a note never says the home file is missing when it is there
    private static string HomeAbsentNote(string homePath, Gatto.Terminal.GlyphSet g) =>
        ContextFiles.WhyMissing(homePath) switch
    {
        ContextFiles.MissingReason.Absent => $" {g.Dot} no home GATTO.md ({Tilde(homePath)})",
        ContextFiles.MissingReason.Unreadable => $" {g.Dot} home GATTO.md not readable ({Tilde(homePath)})",
        ContextFiles.MissingReason.CommentOnly => $" {g.Dot} home GATTO.md is comment-only ({Tilde(homePath)})",
        _ => $" {g.Dot} home GATTO.md not loaded ({Tilde(homePath)})",
    };

    //the empty-set sentence comes from the same WhyMissing the non-empty arm uses, and only Absent says the file is not there
    private static string EmptySetLine(string cwd, string homePath, Gatto.Terminal.GlyphSet g) =>
        ContextFiles.WhyMissing(homePath) switch
        {
            ContextFiles.MissingReason.CommentOnly =>
                $"context files: none loaded for {Tilde(cwd)} {g.Dot} no GATTO.md here or above it, and the home GATTO.md is comment-only ({Tilde(homePath)}) {g.Dot} "
                + "run /init in the REPL to write one",
            ContextFiles.MissingReason.Unreadable =>
                $"context files: none loaded for {Tilde(cwd)} {g.Dot} no GATTO.md here or above it, and the home GATTO.md is not readable ({Tilde(homePath)}) {g.Dot} "
                + "run: fix its permissions, or write a GATTO.md for this project",
            _ =>
                $"context files: none loaded for {Tilde(cwd)} {g.Dot} no GATTO.md in {Tilde(Path.GetDirectoryName(homePath)!)}, here, or above it {g.Dot} "
                + "run /init in the REPL to write one",
        };

    //report this folder's own GATTO.md whenever it is not in the set, and every file above it the walk picks and the strip empties. homeNamed is the home file the home note already names
    private static string CwdFileNote(Gatto.Terminal.GlyphSet g, string cwd, IReadOnlyList<(string Path, string Content)> files, bool compat, string? homeNamed)
    {
        var ancestors = string.Concat(ContextFiles.CommentOnlyAncestors(cwd, compat)
            .Where(p => homeNamed is null || !PathsEqual(p, homeNamed))
            .Select(p => $" {g.Dot} {Short(p, cwd)} is comment-only"));
        var here = Path.Combine(cwd, "GATTO.md");
        if (files.Any(f => PathsEqual(f.Path, here))) return ancestors;
        return ContextFiles.WhyMissing(here) switch
        {
            ContextFiles.MissingReason.CommentOnly => $" {g.Dot} GATTO.md here is comment-only",
            ContextFiles.MissingReason.Unreadable => $" {g.Dot} GATTO.md here is not readable",
            _ => "",
        } + ancestors;
    }

    //8. project memory: the effective state and what decided it

    //whether memory is on for this folder and which file turned it off, resolved through the same EffectiveMemoryEnabled call the launch makes
    private static CheckResult CheckMemory(string cwd, GattoConfig? config, Gatto.Terminal.GlyphSet g)
    {
        if (config is null)
            return new CheckResult(false,
                "memory: cannot determine " + g.Dot + " gatto.json failed to load " + g.Dot + " fix gatto.json (see the gatto.json check above), then run: gatto doctor");

        bool on;
        string? disabledBy;
        try
        {
            on = ProjectFileConfig.EffectiveMemoryEnabled(cwd, config.MemoryEnabled, out disabledBy);
        }
        catch (GattoConfigException ex)
        {
            //a malformed .gatto.json here names what it costs, with no ruling this line cannot answer its own question
            return new CheckResult(false,
                $"memory: cannot determine {g.Dot} {ex.Message} {g.Dot} fix the .gatto.json named below, then run: gatto doctor");
        }

        if (!on)
            return new CheckResult(true, disabledBy is null
                ? "memory: disabled by \"memory\": {\"enabled\": false} in gatto.json " + g.Dot + " no facts are read or written"
                : $"memory: disabled by {Short(disabledBy, cwd)} {g.Dot} no facts are read or written");

        var facts = MemoryDir.FactFiles(MemoryDir.FindProjectRoot(cwd)).Count;
        //report the effective budget and which source decided it, a derived number with no provenance sends the operator hunting
        var budget = config.MemoryIndexBudget is int b
            ? $"{b} tokens (gatto.json)"
            : $"{GattoApp.DerivedMemoryBudget(null)} tokens (derived {g.Dot} no model armed; a model's context sets it, capped 1000{g.Range}4000)";
        return new CheckResult(true,
            $"memory: enabled {g.Dot} {facts} fact{(facts == 1 ? "" : "s")} in {Short(Path.Combine(cwd, ".gatto", "memory"), cwd)}, index budget {budget}");
    }

    //the shortest of the cwd-relative path and the ~-collapsed one, so a header row stays on one line
    private static string Short(string path, string cwd)
    {
        var best = Tilde(path);
        var rel = Path.GetRelativePath(cwd, path);
        return rel.Length < best.Length ? rel : best;
    }

    internal static string Tilde(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 && path.StartsWith(profile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[profile.Length..]
            : path;
    }

    private static bool PathsEqual(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    //per-directory context_files.compat overrides from .gatto.json

    private static CheckResult CheckProjectFiles(string cwd, Gatto.Terminal.GlyphSet g)
    {
        var found = new List<string>();      //present and readable (with or without an actual ruling)
        var problems = new List<string>();   //malformed content, or present but unreadable

        string? dir = Path.GetFullPath(cwd);
        while (dir is not null)
        {
            var path = Path.Combine(dir, ProjectFileConfig.FileName);
            if (File.Exists(path))
            {
                try
                {
                    var compat = ProjectFileConfig.TryReadCompat(dir);
                    if (compat is not null)
                    {
                        found.Add($"{ProjectFileConfig.FileName} at {dir} (compat: {(compat.Value ? "true" : "false")})");
                    }
                    else
                    {
                        //an unreadable file here gets a ✗ like a corrupt permissions.json. a well-formed file with no ruling is named but stays a check
                        if (IsReadable(path))
                            found.Add($"{ProjectFileConfig.FileName} at {dir} (no ruling {g.Dot} context_files.compat not set)");
                        else
                            problems.Add($"{path} is present but unreadable");
                    }
                }
                catch (GattoConfigException ex)
                {
                    problems.Add(ex.Message);
                }
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        if (problems.Count > 0)
        {
            //a bad file in one folder must never hide a valid one elsewhere, list both with the problems first
            var all = found.Count == 0 ? problems : problems.Concat(found);
            return new CheckResult(false,
                $"{string.Join("; ", all)} " + g.Dot + " fix the listed .gatto.json file(s), then run: gatto doctor");
        }

        return found.Count == 0
            ? new CheckResult(true, $"no {ProjectFileConfig.FileName} on the walk")
            : new CheckResult(true, string.Join("; ", found));
    }

    private static bool IsReadable(string path)
    {
        //a cheap doctor-only probe that tells unreadable apart from readable but pinning nothing
        try
        {
            File.ReadAllText(path);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}

//the endpoint, model and role defaults plus the serve state and the session count for this folder. read-only, reusing StatusAsync and CountForCwd
public static class Status
{
    public static async Task<int> RunAsync(string home, string cwd, TextWriter output, HttpClient http, CancellationToken ct)
    {
        //no status header here, CommandBanner already prints the command's name and version
        output.WriteLine($"home: {home}");

        GattoConfig? config = null;
        try { config = GattoConfig.Load(home); }
        catch (GattoConfigException ex) { output.WriteLine($"gatto.json: {ex.Message}"); }

        if (config is not null)
        {
            //status runs no extension, so a default endpoint outside gatto.json is named as one an extension may contribute
            var where = config.Endpoints.TryGetValue(config.DefaultEndpoint, out var ep)
                ? ep.BaseUrl ?? "(derived from the active model's port)"
                : "not in gatto.json, an extension may contribute it";
            output.WriteLine($"endpoint: {config.DefaultEndpoint} ({where})");
            output.WriteLine($"default_model: {config.DefaultModel ?? "(none)"}");
        }
        output.WriteLine("role: generalist (default; override with gatto <role>)");

        var manager = new ServeManager(home, config?.LlamaServer ?? "", spawn: null, lookup: null,
            http, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        //this report is plain text, so the surface takes no theme
        var glyphs = CommandBanner.GlyphsFor(home);
        await manager.StatusAsync(
            new ServeLines(new CliSurface(output, null, glyphs), glyphs), ct).ConfigureAwait(false);

        var sessions = new SessionStore(home, cwd);
        output.WriteLine($"sessions for this project: {sessions.CountForCwd()}");
        return 0;
    }
}
