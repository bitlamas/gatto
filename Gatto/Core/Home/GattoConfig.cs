using System.Text.Json;
using Gatto.Core.Client;

namespace Gatto.Core.Home;

//the message is what the user sees, so it must read on its own, and a subclass must add facts without changing it
public class GattoConfigException(string message) : Exception(message);

//the shape every nested config section shares: present or absent, an object, strict about unknown keys, with per-key checks at the call site
internal static class ConfigSection
{
    //returns the section or null when absent, with where threaded so the message stays byte-identical (and the oracles stay exact rather than substring)
    public static JsonElement? Read(JsonElement root, string name, string[] allowedKeys, string where)
    {
        if (!root.TryGetProperty(name, out var section)) return null;

        if (section.ValueKind != JsonValueKind.Object)
            throw new GattoConfigException($"{where} has a \"{name}\" with the wrong type — must be an object");

        foreach (var k in section.EnumerateObject())
            if (!allowedKeys.Contains(k.Name))
                throw new GattoConfigException(
                    $"unknown key in {where} {name}: {k.Name} — {Known(allowedKeys)}");

        return section;
    }

    //the list of accepted keys appended to every unknown-key refusal, built from the allowlist array so the prose can't drift from it
    public static string Known(string[] allowedKeys) => "known keys: " + string.Join(", ", allowedKeys);
}

//collapsed streams live then folds to a one-row summary, expanded streams and stays open
public enum ReasoningMode { Collapsed, Expanded }

//the ordered web_search provider chain and the settings each provider needs, validated at load so a bad chain fails at startup
public sealed record SearchConfig(
    IReadOnlyList<string> Providers,
    string? TavilyApiKey,
    string? SearxngUrl)
{
    public static readonly SearchConfig Default = new(new[] { "ddg" }, null, null);
}

public sealed record GattoConfig(
    IReadOnlyDictionary<string, EndpointConfig> Endpoints,
    string DefaultEndpoint,
    string? DefaultModel,
    string? LlamaServer = null,
    bool ContextCompat = false,
    bool ContextHome = true,
    string Theme = "auto",
    //auto, unicode or ascii, where auto reads the host like the theme setting and the explicit values are for a misjudged host
    string Glyphs = "auto",
    string Reasoning = "collapsed",
    SearchConfig? SearchOrNull = null,
    string? WeightsRoot = null,
    string? Think = null,
    //alt_screen false keeps a TTY on the main-screen renderer, and dump_on_exit prints the transcript at exit instead of only the --continue hint
    bool AltScreen = true,
    bool DumpOnExit = false,
    //mouse capture for wheel scroll and click, with wheel_lines per notch, and alt_screen false turns it off silently rather than as an error
    bool Mouse = true,
    int WheelLines = 3,
    //auto-copy a selection at drag-release, on top of the Ctrl+C copy, and it stays a silent no-op when mouse is off rather than an error
    bool CopyOnSelect = false,
    //auto-compact trigger as a fraction of context capacity, with false disabling the trigger (null)
    double? AutoCompact = 0.8,
    //offer a free-text reason after a Deny, which reaches the model verbatim, with the default leaving n denying instantly
    bool DenyReason = false,
    //the memory section's consent switch and index budget, neither read here, and a null budget means not configured so a default can be derived
    bool MemoryEnabled = true,
    int? MemoryIndexBudget = null,
    //tri-state, where null means never asked so no network happens, and it ships with its TopKeys entry and writer or the reader refuses it
    bool? UpdateCheck = null,
    //the publisher the shelf opens on, validated against the uploader allowlist at load so a typo is refused
    string? DefaultPublisher = null,
    //true stops the server this home started when the user quits, so the first window to quit stops it for the others
    bool StopServerOnExit = false)
{
    //never null: Load resolves the section or its default, and the nullable field keeps test instances terse
    public SearchConfig Search => SearchOrNull ?? SearchConfig.Default;

    //expanded maps to Expanded and every other value to Collapsed, so a hand-built show or hide can't quietly mean Expanded
    public ReasoningMode ReasoningMode => Reasoning == "expanded" ? ReasoningMode.Expanded : ReasoningMode.Collapsed;

    //the cloned gatto.json root for the extension host's Gatto.Config.Section, null on hand-built instances
    public JsonElement? Raw { get; init; }

    //the one statement of what gatto.json accepts, asserted against the schema in both directions, so a key added here needs a schema entry
    internal static readonly string[] TopKeys =
        { "$schema", "endpoints", "default_endpoint", "default_model", "default_publisher", "llama_server", "context_files", "theme", "glyphs", "reasoning", "regions", "search", "weights_root", "think", "alt_screen", "dump_on_exit", "stop_server_on_exit", "mouse", "wheel_lines", "copy_on_select", "auto_compact", "deny_reason", "memory", "update_check", "extensions" };
    internal static readonly string[] ValidThemes = { "dark", "light", "auto" };
    //the same three-value shape as theme: auto asks the host, the two names answer for it
    internal static readonly string[] ValidGlyphs = { "auto", "unicode", "ascii" };
    internal static readonly string[] EndpointKeys = { "base_url", "key_env", "context", "thinking" };
    internal static readonly string[] ContextFilesKeys = { "compat", "home" };
    internal static readonly string[] SearchKeys = { "providers", "tavily", "searxng" };
    internal static readonly string[] MemoryKeys = { "enabled", "index_budget" };
    internal static readonly string[] ValidProviders = { "ddg", "tavily", "searxng" };
    //arrays rather than inline comparisons, so the schema has one statement of each key set to match
    internal static readonly string[] TavilyKeys = { "apiKey" };
    internal static readonly string[] SearxngKeys = { "url" };

    public static GattoConfig Load(string homePath)
    {
        var path = Path.Combine(homePath, "gatto.json");
        if (!File.Exists(path))
            throw new GattoConfigException(
                //the message names the command that creates the file, since a bare launch writes nothing
                $"no gatto.json at {path} — run gatto setup to create one, or write it by hand with an \"endpoints\" map and \"default_endpoint\"");

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex) { throw new GattoConfigException($"gatto.json is not valid JSON: {ex.Message}"); }

        if (root.ValueKind != JsonValueKind.Object)
            throw new GattoConfigException("gatto.json must be a JSON object");

        foreach (var prop in root.EnumerateObject())
            if (!TopKeys.Contains(prop.Name))
                throw new GattoConfigException(
                    $"unknown key in gatto.json: {prop.Name} — {ConfigSection.Known(TopKeys)}");

        //each extension owns the keys inside its own object, so gatto checks only that every value is an object and validates nothing below it
        if (root.TryGetProperty("extensions", out var extensions))
        {
            if (extensions.ValueKind != JsonValueKind.Object)
                throw new GattoConfigException("gatto.json has an \"extensions\" with the wrong type — must be an object");
            foreach (var ext in extensions.EnumerateObject())
                if (ext.Value.ValueKind != JsonValueKind.Object)
                    throw new GattoConfigException($"gatto.json extensions.{ext.Name} must be an object");
        }

        if (!root.TryGetProperty("endpoints", out var eps) || eps.ValueKind != JsonValueKind.Object)
            throw new GattoConfigException("gatto.json needs an \"endpoints\" object");

        var endpoints = new Dictionary<string, EndpointConfig>();
        foreach (var ep in eps.EnumerateObject())
        {
            if (ep.Value.ValueKind != JsonValueKind.Object)
                throw new GattoConfigException($"endpoint '{ep.Name}' must be a JSON object");

            foreach (var k in ep.Value.EnumerateObject())
                if (!EndpointKeys.Contains(k.Name))
                    throw new GattoConfigException(
                        $"unknown key in gatto.json endpoint '{ep.Name}': {k.Name} — {ConfigSection.Known(EndpointKeys)}");

            //only the local endpoint may leave out base_url, since the active model's port gives it, the others need one
            string? baseUrl = null;
            if (ep.Value.TryGetProperty("base_url", out var bu))
            {
                if (bu.ValueKind != JsonValueKind.String)
                    throw new GattoConfigException($"endpoint '{ep.Name}' has a base_url with the wrong type");
                baseUrl = bu.GetString();
                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var buUri) || buUri.Scheme is not ("http" or "https"))
                    throw new GattoConfigException($"endpoint '{ep.Name}' base_url must be an absolute http(s) URL, got '{baseUrl}'");
            }
            else if (ep.Name != "local")
            {
                throw new GattoConfigException($"endpoint '{ep.Name}' needs a base_url");
            }

            string? keyEnv = null;
            if (ep.Value.TryGetProperty("key_env", out var ke))
            {
                if (ke.ValueKind != JsonValueKind.String)
                    throw new GattoConfigException($"endpoint '{ep.Name}' has a key_env with the wrong type");
                keyEnv = ke.GetString();
            }

            int? context = null;
            if (ep.Value.TryGetProperty("context", out var cx))
            {
                if (cx.ValueKind != JsonValueKind.Number || !cx.TryGetInt32(out var cxv))
                    throw new GattoConfigException($"endpoint '{ep.Name}' has a context with the wrong type");
                context = cxv;
            }

            //the level names are checked later, when a role resolves the map, since Core must not reference Roles
            Dictionary<string, JsonElement?>? thinking = null;
            if (ep.Value.TryGetProperty("thinking", out var th))
            {
                if (th.ValueKind != JsonValueKind.Object)
                    throw new GattoConfigException($"endpoint '{ep.Name}' has a \"thinking\" with the wrong type — must be an object");

                thinking = new Dictionary<string, JsonElement?>();
                foreach (var prop in th.EnumerateObject())
                {
                    if (prop.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Object))
                        throw new GattoConfigException($"endpoint '{ep.Name}' has a \"thinking\" value for \"{prop.Name}\" with the wrong type — must be an object or null");

                    if (prop.Value.ValueKind == JsonValueKind.Object)
                        HarnessManagedKeys.Check(prop.Value, key =>
                            $"endpoint '{ep.Name}' \"thinking\".\"{prop.Name}\" must not set \"{key}\" — it is managed by gatto");

                    //a JSON null maps to a C# null, so nothing is sent for that level, and the key's absence never reaches this loop
                    thinking[prop.Name] = prop.Value.ValueKind == JsonValueKind.Null ? null : prop.Value.Clone();
                }
            }

            endpoints[ep.Name] = new EndpointConfig(baseUrl, keyEnv, context, thinking);
        }

        var defaultEp = root.TryGetProperty("default_endpoint", out var de) && de.ValueKind == JsonValueKind.String
            ? de.GetString()! : throw new GattoConfigException("gatto.json needs default_endpoint");
        if (!endpoints.ContainsKey(defaultEp))
            throw new GattoConfigException($"default_endpoint '{defaultEp}' is not defined under endpoints");

        string? defaultModel = null;
        if (root.TryGetProperty("default_model", out var dm) && dm.ValueKind != JsonValueKind.Null)
        {
            if (dm.ValueKind != JsonValueKind.String)
                throw new GattoConfigException("gatto.json has a \"default_model\" with the wrong type — must be a string");
            defaultModel = dm.GetString();
        }

        //the shelf's publisher checked against the compiled allowlist, and the refusal names every slug so a user can see the whole set
        string? defaultPublisher = null;
        if (root.TryGetProperty("default_publisher", out var dp) && dp.ValueKind != JsonValueKind.Null)
        {
            if (dp.ValueKind != JsonValueKind.String)
                throw new GattoConfigException("gatto.json has a \"default_publisher\" with the wrong type — must be a string");
            defaultPublisher = dp.GetString();
            var orgs = Gatto.Core.Acquire.UploaderAllowlist.Load().Orgs;
            //an empty string is refused like an unknown slug, since leaving the key out is the only way to mean no preference
            if (!orgs.Contains(defaultPublisher, StringComparer.Ordinal))
                throw new GattoConfigException(
                    $"gatto.json has an unknown \"default_publisher\": '{defaultPublisher}' — known publishers: "
                    + string.Join(", ", orgs)
                    + ". The list is compiled in, so adding one is a new gatto release.");
        }

        string? llamaServer = null;
        if (root.TryGetProperty("llama_server", out var ls))
        {
            if (ls.ValueKind != JsonValueKind.String)
                throw new GattoConfigException("gatto.json has a \"llama_server\" with the wrong type — must be a string");
            llamaServer = ls.GetString();
        }

        var contextCompat = false;
        var contextHome = true;
        if (ConfigSection.Read(root, "context_files", ContextFilesKeys, "gatto.json") is { } cf)
        {
            if (cf.TryGetProperty("compat", out var compat))
            {
                if (compat.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new GattoConfigException("gatto.json context_files.compat has the wrong type — must be a boolean");
                contextCompat = compat.GetBoolean();
            }

            if (cf.TryGetProperty("home", out var homeLayer))
            {
                if (homeLayer.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new GattoConfigException("gatto.json context_files.home has the wrong type — must be a boolean");
                contextHome = homeLayer.GetBoolean();
            }
        }


        //dark, light or auto (the default), where auto probes the terminal background on the rich path and anything else is refused
        var theme = "auto";
        if (root.TryGetProperty("theme", out var th2))
        {
            if (th2.ValueKind != JsonValueKind.String)
                throw new GattoConfigException("gatto.json has a \"theme\" with the wrong type — must be a string");
            theme = th2.GetString()!;
            if (!ValidThemes.Contains(theme))
                throw new GattoConfigException(
                    $"gatto.json has an invalid \"theme\": '{theme}' — valid values are dark, light, auto");
        }

        //the glyph vocabulary, validated like theme, where an invalid value names the three valid ones rather than falling back and drawing tofu
        var glyphs = "auto";
        if (root.TryGetProperty("glyphs", out var gl))
        {
            if (gl.ValueKind != JsonValueKind.String)
                throw new GattoConfigException("gatto.json has a \"glyphs\" with the wrong type — must be a string");
            glyphs = gl.GetString()!;
            if (!ValidGlyphs.Contains(glyphs))
                throw new GattoConfigException(
                    $"gatto.json has an invalid \"glyphs\": '{glyphs}' — valid values are auto, unicode, ascii");
        }

        //reasoning display only, where collapsed folds to a one-row summary and expanded stays open, and a legacy show or hide hard-errors with the new name
        var reasoning = "collapsed";
        if (root.TryGetProperty("reasoning", out var rs))
        {
            if (rs.ValueKind != JsonValueKind.String)
                throw new GattoConfigException("gatto.json has a \"reasoning\" with the wrong type — must be a string");
            reasoning = rs.GetString()!;
            if (reasoning is not ("collapsed" or "expanded"))
                throw new GattoConfigException(reasoning is "show" or "hide"
                    ? $"gatto.json has a pre-3.5B \"reasoning\": '{reasoning}' — it is now " +
                      $"'{(reasoning == "show" ? "expanded" : "collapsed")}'; please update gatto.json"
                    : $"gatto.json has an invalid \"reasoning\": '{reasoning}' — valid values are collapsed, expanded");
        }

        //the session default for a Toggle-capable model's reasoning switch, where absent leaves the server's default and other models ignore it
        string? think = null;
        if (root.TryGetProperty("think", out var tk))
        {
            if (tk.ValueKind != JsonValueKind.String)
                throw new GattoConfigException("gatto.json has a \"think\" with the wrong type — must be a string");
            think = tk.GetString()!;
            if (think is not ("on" or "off"))
                throw new GattoConfigException(
                    $"gatto.json has an invalid \"think\": '{think}' — valid values are on, off");
        }

        //the search section is validated at startup, so a provider listed without its settings fails here rather than at the first search
        SearchConfig? search = null;
        if (root.TryGetProperty("search", out var sc))
        {
            if (sc.ValueKind != JsonValueKind.Object)
                throw new GattoConfigException("gatto.json has a \"search\" with the wrong type — must be an object");

            foreach (var k in sc.EnumerateObject())
                if (!SearchKeys.Contains(k.Name))
                    throw new GattoConfigException(
                        $"unknown key in gatto.json search: {k.Name} — {ConfigSection.Known(SearchKeys)}");

            var providers = new List<string> { "ddg" };
            if (sc.TryGetProperty("providers", out var pv))
            {
                if (pv.ValueKind != JsonValueKind.Array)
                    throw new GattoConfigException("gatto.json search.providers has the wrong type — must be an array of strings");
                providers.Clear();
                foreach (var item in pv.EnumerateArray())
                {
                    var name = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                    if (name is null || !ValidProviders.Contains(name))
                        throw new GattoConfigException(
                            "gatto.json search.providers contains an invalid provider — valid values are ddg, tavily, searxng");
                    if (providers.Contains(name))
                        throw new GattoConfigException($"gatto.json search.providers: '{name}' is listed twice");
                    providers.Add(name);
                }
                if (providers.Count == 0)
                    throw new GattoConfigException("gatto.json search.providers must not be empty");
            }

            string? tavilyKey = null;
            if (sc.TryGetProperty("tavily", out var tav))
            {
                if (tav.ValueKind != JsonValueKind.Object)
                    throw new GattoConfigException("gatto.json has a \"search.tavily\" with the wrong type — must be an object");
                foreach (var k in tav.EnumerateObject())
                    if (!TavilyKeys.Contains(k.Name))
                        throw new GattoConfigException(
                            $"unknown key in gatto.json search.tavily: {k.Name} — {ConfigSection.Known(TavilyKeys)}");
                if (tav.TryGetProperty("apiKey", out var ak))
                {
                    if (ak.ValueKind != JsonValueKind.String)
                        throw new GattoConfigException("gatto.json search.tavily.apiKey has the wrong type — must be a string");
                    tavilyKey = ak.GetString();
                }
            }

            string? searxngUrl = null;
            if (sc.TryGetProperty("searxng", out var sxc))
            {
                if (sxc.ValueKind != JsonValueKind.Object)
                    throw new GattoConfigException("gatto.json has a \"search.searxng\" with the wrong type — must be an object");
                foreach (var k in sxc.EnumerateObject())
                    if (!SearxngKeys.Contains(k.Name))
                        throw new GattoConfigException(
                            $"unknown key in gatto.json search.searxng: {k.Name} — {ConfigSection.Known(SearxngKeys)}");
                if (sxc.TryGetProperty("url", out var su))
                {
                    if (su.ValueKind != JsonValueKind.String)
                        throw new GattoConfigException("gatto.json search.searxng.url has the wrong type — must be a string");
                    searxngUrl = su.GetString();
                    if (!Uri.TryCreate(searxngUrl, UriKind.Absolute, out var sxUri) || sxUri.Scheme is not ("http" or "https"))
                        throw new GattoConfigException($"gatto.json search.searxng.url must be an absolute http(s) URL, got '{searxngUrl}'");
                }
            }

            if (providers.Contains("tavily") && string.IsNullOrWhiteSpace(tavilyKey))
                throw new GattoConfigException("search.providers lists 'tavily' but search.tavily.apiKey is missing");
            if (providers.Contains("searxng") && string.IsNullOrWhiteSpace(searxngUrl))
                throw new GattoConfigException("search.providers lists 'searxng' but search.searxng.url is missing");

            search = new SearchConfig(providers, tavilyKey, searxngUrl);
        }

        //where bare gatto model new scans for unconfigured .gguf files, and the CLI names this key when it is unset rather than crashing
        string? weightsDir = null;
        if (root.TryGetProperty("weights_root", out var md))
        {
            if (md.ValueKind != JsonValueKind.String)
                throw new GattoConfigException("gatto.json has a \"weights_root\" with the wrong type — must be a string");
            weightsDir = md.GetString();
        }

        //the alt-screen opt-out and dump-on-exit keys
        var altScreen = true;
        if (root.TryGetProperty("alt_screen", out var asc))
        {
            if (asc.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GattoConfigException("gatto.json has an \"alt_screen\" with the wrong type — must be a boolean");
            altScreen = asc.GetBoolean();
        }

        var dumpOnExit = false;
        if (root.TryGetProperty("dump_on_exit", out var doe))
        {
            if (doe.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GattoConfigException("gatto.json has a \"dump_on_exit\" with the wrong type — must be a boolean");
            dumpOnExit = doe.GetBoolean();
        }

        var stopServerOnExit = false;
        if (root.TryGetProperty("stop_server_on_exit", out var ssoe))
        {
            if (ssoe.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GattoConfigException("""gatto.json has a "stop_server_on_exit" with the wrong type — must be a boolean""");
            stopServerOnExit = ssoe.GetBoolean();
        }

        //mouse capture and the lines per wheel notch, with no cross-validation: mouse true and alt_screen false is a silent no-mouse
        var mouse = true;
        if (root.TryGetProperty("mouse", out var ms))
        {
            if (ms.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GattoConfigException("gatto.json has a \"mouse\" with the wrong type — must be a boolean");
            mouse = ms.GetBoolean();
        }

        var wheelLines = 3;
        if (root.TryGetProperty("wheel_lines", out var wl))
        {
            if (wl.ValueKind != JsonValueKind.Number || !wl.TryGetInt32(out var wlv))
                throw new GattoConfigException("gatto.json has a \"wheel_lines\" with the wrong type — must be a number");
            if (wlv < 1)
                throw new GattoConfigException($"gatto.json has an invalid \"wheel_lines\": {wlv} — must be >= 1");
            wheelLines = wlv;
        }

        //auto-copy on select, boolean like mouse and checked the same way, with no cross-validation
        var copyOnSelect = false;
        if (root.TryGetProperty("copy_on_select", out var cos))
        {
            if (cos.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GattoConfigException("gatto.json has a \"copy_on_select\" with the wrong type — must be a boolean");
            copyOnSelect = cos.GetBoolean();
        }

        //deny_reason, off by default, which lets a Deny be followed by an optional one-line reason for the model
        var denyReason = false;
        if (root.TryGetProperty("deny_reason", out var dr))
        {
            if (dr.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GattoConfigException("gatto.json has a \"deny_reason\" with the wrong type — must be a boolean");
            denyReason = dr.GetBoolean();
        }

        //a present but non-boolean value is a config error rather than a silent no, as every other typed key here does
        bool? updateCheck = null;
        if (root.TryGetProperty("update_check", out var uc))
        {
            if (uc.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new GattoConfigException("gatto.json has an \"update_check\" with the wrong type — must be a boolean");
            updateCheck = uc.GetBoolean();
        }

        //the auto-compact threshold, where false disables the trigger and a number must sit in 0.5 to 0.95 or startup refuses it
        double? autoCompact = 0.8;
        if (root.TryGetProperty("auto_compact", out var ac))
        {
            if (ac.ValueKind == JsonValueKind.False) autoCompact = null;
            else if (ac.ValueKind == JsonValueKind.Number && ac.GetDouble() is >= 0.5 and <= 0.95)
                autoCompact = ac.GetDouble();
            else
                throw new GattoConfigException(
                    $"gatto.json has an invalid \"auto_compact\": {ac.GetRawText()} — must be a number 0.5–0.95, or false");
        }

        //the memory section: enabled defaults to true. an absent index_budget stays null, the launch derives the budget from the context
        var memoryEnabled = true;
        int? memoryIndexBudget = null;
        if (ConfigSection.Read(root, "memory", MemoryKeys, "gatto.json") is { } mem)
        {
            if (mem.TryGetProperty("enabled", out var menabled))
            {
                if (menabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new GattoConfigException("gatto.json memory.enabled has the wrong type — must be a boolean");
                memoryEnabled = menabled.GetBoolean();
            }

            if (mem.TryGetProperty("index_budget", out var mbudget))
            {
                if (mbudget.ValueKind != JsonValueKind.Number || !mbudget.TryGetInt32(out var mbudgetv) || mbudgetv < 1)
                    throw new GattoConfigException(
                        $"gatto.json has an invalid \"memory.index_budget\": {mbudget.GetRawText()} — must be a number >= 1");
                memoryIndexBudget = mbudgetv;
            }
        }

        return new GattoConfig(endpoints, defaultEp, defaultModel, llamaServer, contextCompat, contextHome, theme, glyphs, reasoning, search, weightsDir, think, altScreen, dumpOnExit, mouse, wheelLines, copyOnSelect, autoCompact, denyReason, memoryEnabled, memoryIndexBudget, updateCheck, defaultPublisher, stopServerOnExit)
        { Raw = root };
    }
}
