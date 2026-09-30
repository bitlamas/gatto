using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;

namespace Gatto.Roles;

//one file of a model. exactly one is Active, checked at load, and the quant name is a label off the file name
public sealed record ModelFile(string Path, string? Quant, bool Active);

//the exactly-one-active rule lives only here, a quant switch reaches it through Model.Load
internal static class ActiveFile
{
    //the one active file, or a refusal saying which way the list is wrong: two actives need one deleted, none needs one picked
    public static ModelFile Of(IReadOnlyList<ModelFile> files, string id, string path)
    {
        var active = files.Where(f => f.Active).ToList();
        if (active.Count == 1) return active[0];
        throw new GattoConfigException(active.Count == 0
            ? $"model '{id}' profile at {path} has no active file — exactly one entry in \"files\" needs \"active\": true"
            : $"model '{id}' profile at {path} has {active.Count} active files — exactly one entry in \"files\" may be \"active\": true");
    }
}

//where a model came from, so a hub row is matched by repo. null for one adopted off local disk, where a repo id would be a guess
public sealed record ModelSource(string RepoId, string File);

//one model's serving profile, loaded from ~\.gatto\models\<id>\. its fields become the server's argv
public sealed record ModelProfile(
    IReadOnlyList<ModelFile> Files,
    int Port,
    int Context,
    int? GpuLayers,
    string? CacheTypeK,
    string? CacheTypeV,
    JsonElement? Sampling,
    IReadOnlyDictionary<string, JsonElement?>? Thinking,
    IReadOnlyList<string> ExtraArgs,
    int? Seed = null,   //optional seed becomes --seed N, absent means the server picks at random
    //the server binary for this model, overriding the global one, which serves when this is absent
    string? LlamaServer = null,
    //how much prior reasoning goes on each request: all keeps the prefix stable and a Laguna-style model in its channel, none is cheapest
    ReasoningHistory ReasoningHistory = ReasoningHistory.All,
    //the key the spawned server wants: --api-key for it, a bearer header on every request, and absent leaves argv untouched
    string? ApiKey = null,
    //an override of the global memory index budget, so a model can change the number but never re-arm memory the user turned off
    int? MemoryIndexBudget = null,
    //the multimodal projector for this model. it is not armed even when one sits beside the weights, since it costs VRAM
    string? MmProj = null,
    //may gatto start this server when a launch finds it down: null means ask, true serves, false waits for an explicit /model
    bool? AutoServe = null,
    //the repo and file this model came from, so a hub row matches by repo. null for one adopted off local disk, where a repo name would be a guess
    ModelSource? Source = null,
    //this model's own preferred thinking level, used when the role's thinking key is absent, and stored as the level its own map has
    ThinkingLevel? DefaultEffort = null)
{
    //the weights this profile serves. the loader already refused a list without exactly one active file, so this just reads the answer
    public string ActivePath => Files.Single(f => f.Active).Path;

    //redacted by hand, the synthesized ToString would print the api key into any log line. a member added to this record belongs in this list too
    public override string ToString() =>
        $"ModelProfile {{ Files = {Files.Count}, ActivePath = {ActivePath}, Source = {Source}, " +
        $"Port = {Port}, Context = {Context}, " +
        $"GpuLayers = {GpuLayers}, CacheTypeK = {CacheTypeK}, CacheTypeV = {CacheTypeV}, " +
        //the arg count rather than the args, which can hold a hand-written --api-key. null args count as zero, a report must not throw
        $"Sampling = {Sampling}, Thinking = {Thinking}, ExtraArgs = [{ExtraArgs?.Count ?? 0} args], " +
        $"Seed = {Seed}, LlamaServer = {LlamaServer}, ReasoningHistory = {ReasoningHistory}, " +
        $"ApiKey = {(ApiKey is null ? "null" : "***")}, MemoryIndexBudget = {MemoryIndexBudget}, " +
        $"AutoServe = {AutoServe}, " +
        $"MmProj = {MmProj}, DefaultEffort = {DefaultEffort} }}";
}

//a model directory: its serving profile plus the signal files an audition leaves behind, and a fresh model has none of them
public sealed record Model(string Id, ModelProfile Profile, string? SystemAppend, JsonElement? Repair, Nudges? Nudges)
{
    //gatto's own path to the weights, the one name for it, so it can't be confused with the loaded path the server reports
    public string ActivePath => Profile.ActivePath;

    private static readonly string[] ProfileKeys =
    {
        //model_path is retired with no shim, so an old profile that still has it is refused by the unknown-key path
        "files", "source", "port", "context", "gpu_layers",
        "cache_type_k", "cache_type_v", "sampling", "thinking", "extra_args", "seed", "llama_server",
        "reasoning_history", "api_key", "memory", "mmproj", "auto_serve", "default_effort",
    };

    //a model's memory section holds only index_budget, since the enabled switch is global and can't be set per model
    private static readonly string[] MemoryKeys = { "index_budget" };

    public static Model Load(string modelsDir, string id)
    {
        var dir = Path.Combine(modelsDir, id);
        var profilePath = Path.Combine(dir, "profile.json");
        if (!File.Exists(profilePath))
            throw new GattoConfigException($"no model '{id}' — add {profilePath}");

        var profile = LoadProfile(profilePath, id);

        var systemAppendPath = Path.Combine(dir, "system-append.md");
        var systemAppend = File.Exists(systemAppendPath) ? File.ReadAllText(systemAppendPath) : null;

        JsonElement? repair = null;
        var repairPath = Path.Combine(dir, "repair.json");
        if (File.Exists(repairPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(repairPath));
                repair = doc.RootElement.Clone();   //clone it, the JsonDocument is disposed at the end of the block
            }
            catch (JsonException ex)
            {
                throw new GattoConfigException($"model '{id}' repair.json at {repairPath} is not valid JSON: {ex.Message}");
            }
        }

        Nudges? nudges = null;
        var nudgesPath = Path.Combine(dir, "nudges.json");
        if (File.Exists(nudgesPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(nudgesPath));
                nudges = Nudges.Parse(doc.RootElement);
            }
            catch (JsonException ex)
            {
                throw new GattoConfigException($"model '{id}' nudges.json at {nudgesPath} is not valid JSON: {ex.Message}");
            }
            catch (GattoConfigException ex)
            {
                throw new GattoConfigException($"model '{id}' nudges.json at {nudgesPath}: {ex.Message}");
            }
        }

        //scorecard.json is deliberately not read here, its presence means nothing to the loader

        return new Model(id, profile, systemAppend, repair, nudges);
    }

    private static ModelProfile LoadProfile(string path, string id)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            root = doc.RootElement.Clone();   //the root must outlive the disposed doc
        }
        catch (JsonException ex)
        {
            throw new GattoConfigException($"model '{id}' profile at {path} is not valid JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Object)
            throw new GattoConfigException($"model '{id}' profile at {path} must be a JSON object");

        foreach (var prop in root.EnumerateObject())
            if (!ProfileKeys.Contains(prop.Name))
                throw new GattoConfigException($"unknown key in model '{id}' profile at {path}: {prop.Name}");

        var files = ReadFiles(root, id, path);
        var source = ReadSource(root, id, path);
        var port = GetRequiredInt(root, id, path, "port");
        var context = GetRequiredInt(root, id, path, "context");

        int? gpuLayers = null;
        if (root.TryGetProperty("gpu_layers", out var gl))
        {
            if (gl.ValueKind != JsonValueKind.Number || !gl.TryGetInt32(out var glv))
                throw new GattoConfigException($"model '{id}' profile at {path} has a \"gpu_layers\" with the wrong type — must be an integer");
            gpuLayers = glv;
        }

        var cacheTypeK = GetOptionalString(root, id, path, "cache_type_k");
        var cacheTypeV = GetOptionalString(root, id, path, "cache_type_v");

        JsonElement? sampling = null;
        if (root.TryGetProperty("sampling", out var sm))
        {
            if (sm.ValueKind != JsonValueKind.Object)
                throw new GattoConfigException($"model '{id}' profile at {path} has a \"sampling\" with the wrong type — must be an object");
            HarnessManagedKeys.Check(sm, key =>
                $"model '{id}' profile at {path} \"sampling\" must not set \"{key}\" — it is managed by gatto");
            sampling = sm.Clone();
        }

        Dictionary<string, JsonElement?>? thinking = null;
        if (root.TryGetProperty("thinking", out var th))
        {
            if (th.ValueKind != JsonValueKind.Object)
                throw new GattoConfigException($"model '{id}' profile at {path} has a \"thinking\" with the wrong type — must be an object");

            thinking = new Dictionary<string, JsonElement?>();
            foreach (var prop in th.EnumerateObject())
            {
                try
                {
                    Thinking.Parse(prop.Name);   //parse for validation only, the result isn't used
                }
                catch (GattoConfigException ex)
                {
                    throw new GattoConfigException($"model '{id}' profile at {path} has an invalid \"thinking\" key: {ex.Message}");
                }

                if (prop.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
                    throw new GattoConfigException($"model '{id}' profile at {path} has a \"thinking\" value for \"{prop.Name}\" with the wrong type — must be an object or null");

                if (prop.Value.ValueKind == JsonValueKind.Object)
                    HarnessManagedKeys.Check(prop.Value, key =>
                        $"model '{id}' profile at {path} \"thinking\".\"{prop.Name}\" must not set \"{key}\" — it is managed by gatto");

                //a JSON null means send nothing at this level. a missing key is a different thing, ResolveMap tells them apart
                thinking[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.Clone();
            }
        }

        var extraArgs = new List<string>();
        if (root.TryGetProperty("extra_args", out var ea))
        {
            if (ea.ValueKind != JsonValueKind.Array)
                throw new GattoConfigException($"model '{id}' profile at {path} has an \"extra_args\" with the wrong type — must be an array of strings");
            foreach (var item in ea.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new GattoConfigException($"model '{id}' profile at {path} has a non-string entry in \"extra_args\"");
                extraArgs.Add(item.GetString()!);
            }
        }

        int? seed = null;
        if (root.TryGetProperty("seed", out var sd))
        {
            if (sd.ValueKind != JsonValueKind.Number || !sd.TryGetInt32(out var sdv))
                throw new GattoConfigException($"model '{id}' profile at {path} has a \"seed\" with the wrong type — must be an integer");
            seed = sdv;
        }

        var llamaServer = GetOptionalString(root, id, path, "llama_server");

        //the multimodal projector file. when absent, --mmproj isn't passed
        var mmproj = GetOptionalString(root, id, path, "mmproj");

        //a present auto_serve must be a real bool, a broken one is an error. absent stays null
        bool? autoServe = null;
        if (root.TryGetProperty("auto_serve", out var asv))
        {
            if (asv.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GattoConfigException(
                    $"model '{id}' profile at {path} has an invalid \"auto_serve\": {asv.GetRawText()} — must be true or false");
            autoServe = asv.GetBoolean();
        }

        var reasoningHistory = ReasoningHistory.All;
        if (root.TryGetProperty("reasoning_history", out var rh))
        {
            reasoningHistory = (rh.ValueKind == JsonValueKind.String ? rh.GetString() : null) switch
            {
                "all" => ReasoningHistory.All,
                "none" => ReasoningHistory.None,
                //the recent value must throw here too (a deleted policy must fail loud rather than quietly pick another one)
                _ => throw new GattoConfigException(
                    $"model '{id}' profile at {path} has an invalid \"reasoning_history\": {rh.GetRawText()} — must be \"all\" or \"none\""),
            };
        }

        //no api_key means no --api-key flag and no Authorization header
        var apiKey = GetOptionalString(root, id, path, "api_key");
        if (apiKey is not null)
        {
            //a blank key must throw, and the value is trimmed (edge whitespace can't survive an HTTP header intact)
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new GattoConfigException(
                    $"model '{id}' profile at {path} has a blank \"api_key\" — give it a value, or remove the key to serve without one");
            apiKey = apiKey.Trim();
        }

        //extra_args can't hold the key, it's refused at load. the client reads the api_key field only, so server and client would disagree and 401
        var strayKeyFlag = extraArgs.FirstOrDefault(IsApiKeyFlag);
        if (strayKeyFlag is not null)
            throw new GattoConfigException(
                $"model '{id}' profile at {path} passes \"{RedactFlagValue(strayKeyFlag)}\" in \"extra_args\" — " +
                (apiKey is null
                    ? "move the value to the profile's \"api_key\" field instead: a key set through extra_args arms the server but leaves gatto's own client without the credential, so every request fails with 401"
                    : "remove it and keep the \"api_key\" field: gatto composes the flag AND sends the matching Authorization header, while the extra_args value would arm the server with a key the client never sends"));

        //llama-server refuses a flag and its value in one entry, with a space or with =. keep this after the api-key check, its message quotes the entry and would print the key
        var malformed = extraArgs.FirstOrDefault(IsFlagWithCrammedValue);
        if (malformed is not null)
        {
            var split = malformed.Contains(' ') ? malformed.Split(' ', 2) : malformed.Split('=', 2);
            throw new GattoConfigException(
                $"model '{id}' profile at {path} passes \"{malformed}\" as one \"extra_args\" entry — " +
                "llama-server takes a flag and its value as separate arguments and accepts no \"=\" form, " +
                $"so write it as two entries: \"{split[0]}\", \"{split[1]}\"");
        }

        //per-model override of the global memory.index_budget, enabled is global-only and stays off this list
        int? memoryIndexBudget = null;
        if (ConfigSection.Read(root, "memory", MemoryKeys, $"model '{id}' profile at {path}") is { } mem)
        {
            if (mem.TryGetProperty("index_budget", out var mbudget))
            {
                if (mbudget.ValueKind != JsonValueKind.Number || !mbudget.TryGetInt32(out var mbudgetv) || mbudgetv < 1)
                    throw new GattoConfigException(
                        $"model '{id}' profile at {path} has an invalid \"memory.index_budget\": {mbudget.GetRawText()} — must be a number >= 1");
                memoryIndexBudget = mbudgetv;
            }
        }

        //default_effort is checked with Thinking.Parse at load, an invalid level must fail here, not quietly fall to the role's default later
        ThinkingLevel? defaultEffort = null;
        if (root.TryGetProperty("default_effort", out var de))
        {
            if (de.ValueKind != JsonValueKind.String)
                throw new GattoConfigException($"model '{id}' profile at {path} has a \"default_effort\" with the wrong type — must be a string");
            try { defaultEffort = Thinking.Parse(de.GetString()); }
            catch (GattoConfigException ex)
            {
                throw new GattoConfigException($"model '{id}' profile at {path} has an invalid \"default_effort\": {ex.Message}");
            }
        }

        return new ModelProfile(files, port, context, gpuLayers, cacheTypeK, cacheTypeV, sampling, thinking, extraArgs, seed, llamaServer, reasoningHistory, apiKey, memoryIndexBudget, mmproj, autoServe, source, defaultEffort);
    }

    //does the loaded file belong to this model, null means unknown (no probe, no path, no normalization). never show unknown as a mismatch
    public static bool? MatchesLoaded(Model model, LoadedModel? loaded)
    {
        if (loaded is null) return null;

        //the catch below already answers null here, this line is just for reading. if that catch ever narrows, this line is the guard
        if (loaded.ModelPath is not { Length: > 0 }) return null;

        try
        {
            var a = Path.GetFullPath(model.Profile.ActivePath);
            var b = Path.GetFullPath(loaded.ModelPath);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return null;   //a malformed path on either side gives null, unknown rather than mismatch
        }
    }

    //read the files list and enforce exactly one active right here, downstream never re-checks. every refusal names the key and the model
    private static IReadOnlyList<ModelFile> ReadFiles(JsonElement root, string id, string path)
    {
        if (!root.TryGetProperty("files", out var files))
            throw new GattoConfigException(
                $"model '{id}' profile at {path} has no \"files\" — a model names the weights it holds "
                + "as a list of {{ path, quant, active }}");
        if (files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
            throw new GattoConfigException(
                $"model '{id}' profile at {path} has a \"files\" with the wrong type — must be a non-empty array");

        var list = new List<ModelFile>();
        foreach (var f in files.EnumerateArray())
        {
            if (f.ValueKind != JsonValueKind.Object)
                throw new GattoConfigException(
                    $"model '{id}' profile at {path} has a \"files\" entry that is not an object");
            if (!f.TryGetProperty("path", out var fp) || fp.ValueKind != JsonValueKind.String
                || fp.GetString() is not { Length: > 0 } filePath)
                throw new GattoConfigException(
                    $"model '{id}' profile at {path} has a \"files\" entry with no \"path\"");
            var quant = f.TryGetProperty("quant", out var q) && q.ValueKind == JsonValueKind.String
                ? q.GetString() : null;
            //absent active reads false here, one bad profile should get one complaint from the list-level check below
            var active = f.TryGetProperty("active", out var a) && a.ValueKind == JsonValueKind.True;
            list.Add(new ModelFile(filePath, quant, active));
        }

        ActiveFile.Of(list, id, path);   //refuses two actives or none, each with its own message
        return list;
    }

    //source is absent for a model adopted off local disk, when present both halves are required so gatto can go back to the origin
    private static ModelSource? ReadSource(JsonElement root, string id, string path)
    {
        if (!root.TryGetProperty("source", out var s)) return null;
        if (s.ValueKind != JsonValueKind.Object)
            throw new GattoConfigException(
                $"model '{id}' profile at {path} has a \"source\" with the wrong type — must be an object");
        if (!s.TryGetProperty("repo_id", out var r) || r.GetString() is not { Length: > 0 } repo
            || !s.TryGetProperty("file", out var f) || f.GetString() is not { Length: > 0 } file)
            throw new GattoConfigException(
                $"model '{id}' profile at {path} has a \"source\" missing \"repo_id\" or \"file\"");
        return new ModelSource(repo, file);
    }

    //the subfolders holding a profile.json, sorted. a missing models folder gives an empty list
    public static IReadOnlyList<string> ListIds(string modelsDir)
    {
        if (!Directory.Exists(modelsDir)) return Array.Empty<string>();

        return Directory.GetDirectories(modelsDir)
            .Where(d => File.Exists(Path.Combine(d, "profile.json")))
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    //the matched token itself can be the secret, so keep the flag name and cut the value to ***
    private static string RedactFlagValue(string arg)
    {
        var eq = arg.IndexOf('=');
        return eq < 0 ? arg : string.Concat(arg.AsSpan(0, eq), "=***");
    }

    //long flags only on purpose: arity of a short flag is unknowable here, and a false positive would refuse a model that works
    private static bool IsFlagWithCrammedValue(string arg)
    {
        if (!arg.StartsWith("--", StringComparison.Ordinal)) return false;

        var cut = arg.IndexOfAny(new[] { ' ', '=' });
        if (cut <= 2 || cut == arg.Length - 1) return false;   //no split point, or an empty side

        var name = arg[2..cut];
        return char.IsAsciiLetter(name[0])
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
    }

    //does this token set the server's key? both spellings count, and --api-key-file too (gatto's client reads no file)
    private static bool IsApiKeyFlag(string arg) =>
        arg is "--api-key" or "--api-key-file"
        || arg.StartsWith("--api-key=", StringComparison.Ordinal)
        || arg.StartsWith("--api-key-file=", StringComparison.Ordinal);

    private static int GetRequiredInt(JsonElement root, string id, string path, string key)
    {
        if (!root.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var iv))
            throw new GattoConfigException($"model '{id}' profile at {path} needs a \"{key}\" integer");
        return iv;
    }

    private static string? GetOptionalString(JsonElement root, string id, string path, string key)
    {
        if (!root.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        if (v.ValueKind != JsonValueKind.String)
            throw new GattoConfigException($"model '{id}' profile at {path} has a \"{key}\" with the wrong type — must be a string");
        return v.GetString();
    }
}
