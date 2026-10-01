using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Unicode;

namespace Gatto.Core.Home;

//the only writer of gatto.json: /model persists the chosen model here so the next launch opens on it
public static class GattoConfigWriter
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        //emit non-ASCII literally, since the default encoder escapes CJK paths into sequences the user has to read by hand
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    };
    //parse the file, set one key and write it back, so keys this writer doesn't own survive untouched

    //the llama server path, written from the local path only, since an endpoint says where to talk rather than which binary serves it
    public static void SetLlamaServer(string homePath, string exePath) =>
        Mutate(homePath, root => root["llama_server"] = exePath,
            "cannot persist the llama-server path");

    //the folder model files are fetched into and scanned, written at the scaffold rather than while typing
    public static void SetWeightsRoot(string homePath, string root) =>
        Mutate(homePath, r => r["weights_root"] = root,
            "cannot persist the weights root");

    //one endpoint's entry only, so a choice made on a cloud session never moves the local default
    public static void SetEndpointDefaultModel(string homePath, string endpoint, string modelId) =>
        Mutate(homePath, root => Entry(root, endpoint)["model"] = modelId,
            "cannot persist the model choice");

    //the level is keyed by model, since two models of one endpoint can declare different levels
    public static void SetEndpointEffort(string homePath, string endpoint, string modelId, string level) =>
        Mutate(homePath, root =>
        {
            var entry = Entry(root, endpoint);
            if (entry["effort"] is not JsonObject effort) entry["effort"] = effort = new JsonObject();
            effort[modelId] = level;
        }, "cannot persist the effort choice");

    private static JsonObject Entry(JsonObject root, string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new GattoConfigException("endpoint name cannot be empty");
        if (root["defaults"] is not JsonObject defaults) root["defaults"] = defaults = new JsonObject();
        if (defaults[endpoint] is not JsonObject entry) defaults[endpoint] = entry = new JsonObject();
        return entry;
    }

    //the older top-level key belongs to the endpoint that is default before this write, so it moves before any edit can change that endpoint
    private static void FoldLegacyDefaultModel(JsonObject root)
    {
        if (root["default_model"] is not JsonValue legacy || !legacy.TryGetValue<string>(out var model))
        {
            root.Remove("default_model");   //absent or null, both mean unset, so removing it changes no meaning
            return;
        }
        var owner = root["default_endpoint"] is JsonValue de && de.TryGetValue<string>(out var name)
            ? name : throw new GattoConfigException("gatto.json needs default_endpoint");
        var entry = Entry(root, owner);
        if (entry["model"] is JsonValue tabled && tabled.TryGetValue<string>(out var t) && t != model)
            throw new GattoConfigException(
                $"gatto.json has \"default_model\" '{model}' and defaults.{owner}.model '{t}'; remove one");
        entry["model"] = model;
        root.Remove("default_model");
    }

    //points default_endpoint at an endpoint that exists, applied after UpsertEndpoint so the config can't name one that is missing
    public static void SetDefaultEndpoint(string homePath, string endpointName)
    {
        if (string.IsNullOrWhiteSpace(endpointName))
            throw new GattoConfigException("default endpoint name cannot be empty");

        Mutate(homePath, root => root["default_endpoint"] = endpointName,
            "cannot persist the endpoint choice");
    }

    //writes only base_url and context for one endpoint, and never a model or llama_server, so the connect path can't grow a model demand
    public static void UpsertEndpoint(string homePath, string name, string baseUrl, int context)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new GattoConfigException("endpoint name cannot be empty");
        //this throw is a backstop only, the real guard is the fit arithmetic that produced the number
        if (context <= 0)
            throw new GattoConfigException(
                $"endpoint '{name}' needs a positive context, got {context}");

        Mutate(homePath, root =>
        {
            if (root["endpoints"] is not JsonObject endpoints)
            {
                endpoints = new JsonObject();
                root["endpoints"] = endpoints;
            }
            if (endpoints[name] is not JsonObject endpoint)
            {
                endpoint = new JsonObject();
                endpoints[name] = endpoint;
            }
            endpoint["base_url"] = baseUrl;
            endpoint["context"] = context;
        }, $"cannot write endpoint '{name}'");
    }

    //records a consent answer as a visible top-level key, and the key's name is the caller's
    public static void SetConsentKey(string homePath, string key, bool value)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new GattoConfigException("consent key cannot be empty");
        Mutate(homePath, root => root[key] = value, $"cannot record consent for '{key}'");
    }

    //read, edit and write the config back atomically, preserving the target's ACL
    private static void Mutate(string homePath, Action<JsonObject> edit, string cannotDoWhat)
    {
        var path = Path.Combine(homePath, "gatto.json");
        if (!File.Exists(path))
            throw new GattoConfigException($"no gatto.json at {path} — {cannotDoWhat}");

        JsonObject root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject
                   ?? throw new GattoConfigException($"gatto.json at {path} must be a JSON object");
        }
        catch (JsonException ex)
        {
            throw new GattoConfigException($"gatto.json is not valid JSON: {ex.Message}");
        }

        FoldLegacyDefaultModel(root);
        edit(root);

        //only the target's explicit ACEs are captured, since inherited ones already come from the directory, and AddAccessRule replays them without privilege
        FileSystemAccessRule[]? targetExplicitRules = null;
        var targetAccessRulesProtected = false;
        if (OperatingSystem.IsWindows())
            targetExplicitRules = CaptureExplicitAcl(path, out targetAccessRulesProtected);

        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, root.ToJsonString(WriteOptions));
            //a Move(overwrite) on NTFS gives the destination the source's descriptor, so the ACEs are stamped on the temp file first
            if (targetExplicitRules is { Length: > 0 } rules && OperatingSystem.IsWindows())
                ApplyExplicitAcl(tmp, rules, targetAccessRulesProtected);
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GattoConfigException($"could not write {path}: {ex.Message}");
        }
        finally
        {
            //delete the temp file on the throw path too, since a stray copy of the secrets in gatto.json is not tidiness
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch (Exception) { } //best effort, so a failed delete can't replace the write error
        }
    }

    //the target's explicit ACEs only, and an unreadable ACL degrades to no preservation rather than failing the write
    [SupportedOSPlatform("windows")]
    private static FileSystemAccessRule[]? CaptureExplicitAcl(string path, out bool rulesProtected)
    {
        rulesProtected = false;
        try
        {
            var acl = new FileInfo(path).GetAccessControl();
            rulesProtected = acl.AreAccessRulesProtected;
            return acl.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
        }
        catch (Exception) { return null; }
    }

    //replay the ACEs with AddAccessRule, since a fresh read assigned straight back is a no-op and SDDL needs a privilege a normal caller lacks
    [SupportedOSPlatform("windows")]
    private static void ApplyExplicitAcl(string tmp, FileSystemAccessRule[] rules, bool rulesProtected)
    {
        try
        {
            var info = new FileInfo(tmp);
            var acl = info.GetAccessControl();
            acl.SetAccessRuleProtection(rulesProtected, false);
            foreach (var rule in rules) acl.AddAccessRule(rule);
            info.SetAccessControl(acl);
        }
        catch (Exception) { } //best effort, the temp file's own ACL is the fallback
    }
}
