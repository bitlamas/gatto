using System.Text.Json;
using System.Text.RegularExpressions;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Core.Web;

namespace Gatto.Extensions;

//tools and hooks staged by one extension load and committed only after the whole script runs clean. a read-class tool skips the checkpoint and permission gate
public sealed class StagedRegistrations
{
    public IReadOnlyList<(ITool Tool, bool ReadClass)> Tools { get; }
    public IReadOnlyList<(string Evt, Func<HookPayload, Task> Handler)> Hooks { get; }

    //the extension's one line of standing policy, null when it declared none (a load with a line and no tools is valid)
    public string? Policy { get; }

    public StagedRegistrations(
        IReadOnlyList<(ITool Tool, bool ReadClass)> tools,
        IReadOnlyList<(string Evt, Func<HookPayload, Task> Handler)> hooks,
        string? policy)
    {
        Tools = tools;
        Hooks = hooks;
        Policy = policy;
    }
}

//the surface a .csx extension sees as the global Gatto, registrations stage until TakeStaged, and reads expose only what the host wired in
public sealed class GattoApi
{
    //bump on any additive change to this surface, the extension cache hash includes the version so a host upgrade recompiles every .csx
    public const string HostApiVersion = "4";

    private static readonly Regex NameRx = new("^[a-z0-9_]+$", RegexOptions.Compiled);
    private static readonly string[] ValidEvents = { "tool_call", "tool_result", "message_end", "session_summary" };

    //the character budget for a policy line, measured from the shipped prompt lines (each one sits in every session's prefix)
    private const int MaxPolicyLine = 200;

    private readonly Action<string> _log;
    private List<(ITool Tool, bool ReadClass)> _tools = new();
    private List<(string Evt, Func<HookPayload, Task> Handler)> _hooks = new();
    private string? _policy;

    public string Home { get; }
    public string Cwd { get; }
    public GattoHttp Http { get; }
    public GattoLedger Ledger { get; }
    public GattoUi Ui { get; }
    public GattoConfigSurface Config { get; }

    //the extension whose registrations are being staged, null before BeginExtension (for diagnostics and logs)
    public string? CurrentExtension { get; private set; }

    public GattoApi(
        string home,
        string cwd,
        Func<string, FetchOptions?, CancellationToken, Task<FetchResult>> fetch,
        Action<string, string?, bool> recordFetch,
        Func<IUserPrompter?> prompter,
        Action<string> log,
        Func<string, JsonElement?>? configSection = null)
    {
        Home = home;
        Cwd = cwd;
        Http = new GattoHttp(fetch);
        Ledger = new GattoLedger(recordFetch);
        Ui = new GattoUi(prompter);
        Config = new GattoConfigSurface(configSection);
        _log = log;
    }

    public void Log(string message) => _log(message);

    //stage a pre-built tool, its name, description and schema are checked now so a bad tool fails at load
    public void Register(ITool tool, bool readClass = false)
    {
        if (tool is null) throw new InvalidOperationException("tool must not be null");
        ValidateName(tool.Name);
        ValidateDescription(tool.Name, tool.Description);
        if (tool.ParametersSchema.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException($"tool '{tool.Name}' parametersSchema must be a JSON object");
        _tools.Add((tool, readClass));
    }

    //stage a tool defined by a delegate, the schema is parsed once and cloned so it outlives the parse document
    public void Register(
        string name,
        string description,
        string parametersSchema,
        Func<JsonElement, IToolContext, CancellationToken, Task<ToolResult>> execute,
        bool readClass = false)
    {
        ValidateName(name);
        ValidateDescription(name, description);
        if (execute is null) throw new InvalidOperationException($"tool '{name}' execute delegate must not be null");
        var schema = ParseObjectSchema(name, parametersSchema);
        _tools.Add((new DelegateTool(name, description, schema, execute), readClass));
    }

    //stage a hook subscription, tool_result hooks only observe (the result-rewriting OnToolResult seam stays core-owned)
    public void On(string evt, Func<HookPayload, Task> handler)
    {
        if (Array.IndexOf(ValidEvents, evt) < 0)
            throw new InvalidOperationException(
                $"unknown event '{evt}' — valid: {string.Join(", ", ValidEvents)}");
        if (handler is null) throw new InvalidOperationException($"handler for '{evt}' must not be null");
        _hooks.Add((evt, handler));
    }

    //the extension's one line of standing policy (when to reach for its tools), a second declaration fails the load
    public void Policy(string line)
    {
        if (_policy is not null)
            throw new InvalidOperationException(
                $"extension '{CurrentExtension}' declares a second policy line — one is the budget");
        var trimmed = line?.Trim() ?? "";
        if (trimmed.Length == 0)
            throw new InvalidOperationException(
                $"extension '{CurrentExtension}' policy line must not be empty");
        //one line is enforced on the text, so a tab is rejected the same as a newline
        if (trimmed.Any(char.IsControl))
            throw new InvalidOperationException(
                $"extension '{CurrentExtension}' policy line must be one line");
        if (trimmed.Length > MaxPolicyLine)
            throw new InvalidOperationException(
                $"extension '{CurrentExtension}' policy line is {trimmed.Length} characters — the budget is {MaxPolicyLine}");
        _policy = trimmed;
    }

    //start a fresh empty stage for a new extension load, whatever was staged before is dropped
    public void BeginExtension(string name)
    {
        CurrentExtension = name;
        _tools = new();
        _hooks = new();
        _policy = null;
    }

    //hand back the stage and clear it, so the next load starts empty
    public StagedRegistrations TakeStaged()
    {
        var staged = new StagedRegistrations(_tools, _hooks, _policy);
        _tools = new();
        _hooks = new();
        _policy = null;
        return staged;
    }

    private static void ValidateName(string name)
    {
        if (name is null || !NameRx.IsMatch(name))
            throw new InvalidOperationException($"tool name '{name}' must match [a-z0-9_]+");
    }

    private static void ValidateDescription(string name, string description)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new InvalidOperationException($"tool '{name}' description must not be empty");
    }

    private static JsonElement ParseObjectSchema(string name, string parametersSchema)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(parametersSchema);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"tool '{name}' parametersSchema must be a JSON object");
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException($"tool '{name}' parametersSchema must be a JSON object");
            //clone detaches the element from the document this method disposes
            return doc.RootElement.Clone();
        }
    }
}

//the fetch handed to extensions, so every URL passes the host's SSRF guard
public sealed class GattoHttp
{
    private readonly Func<string, FetchOptions?, CancellationToken, Task<FetchResult>> _fetch;
    internal GattoHttp(Func<string, FetchOptions?, CancellationToken, Task<FetchResult>> fetch) => _fetch = fetch;
    public Task<FetchResult> FetchAsync(string url, CancellationToken ct) => _fetch(url, null, ct);
    public Task<FetchResult> FetchAsync(string url, FetchOptions options, CancellationToken ct) => _fetch(url, options, ct);
}

//a section is extensions.<name> in gatto.json, else the top-level key, else null (null also means no config, so every script must default)
public sealed class GattoConfigSurface
{
    private readonly Func<string, JsonElement?> _section;
    internal GattoConfigSurface(Func<string, JsonElement?>? section) => _section = section ?? (_ => null);
    public JsonElement? Section(string name) => _section(name);
}

//the citation recorder handed to extensions, it writes through the host's ledger
public sealed class GattoLedger
{
    private readonly Action<string, string?, bool> _record;
    internal GattoLedger(Action<string, string?, bool> record) => _record = record;
    public void RecordFetch(string url, string? content = null, bool searchOnly = false) =>
        _record(url, content, searchOnly);
}

//the prompt surface handed to extensions, with no prompter it throws the same message ask_user does
public sealed class GattoUi
{
    private readonly Func<IUserPrompter?> _prompter;
    internal GattoUi(Func<IUserPrompter?> prompter) => _prompter = prompter;

    public Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken ct)
    {
        //a null prompter means no interactive surface now, -p or no TTY, and the throw becomes an error tool result rather than a hang
        var prompter = _prompter()
            ?? throw new InvalidOperationException("interactive input unavailable in non-interactive mode");
        return prompter.AskAsync(questions, ct);
    }
}

//an ITool whose behavior is an inline delegate, the schema arrives already parsed and detached
internal sealed class DelegateTool(
    string name,
    string description,
    JsonElement schema,
    Func<JsonElement, IToolContext, CancellationToken, Task<ToolResult>> execute) : ITool
{
    public string Name => name;
    public string Description => description;
    public JsonElement ParametersSchema => schema;
    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct) =>
        execute(args, ctx, ct);
}
