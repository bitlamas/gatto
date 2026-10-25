using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Roles.Serve;

namespace Gatto.Roles.Agents;

//runs a named role in a fresh loop sharing only the parent's permission gate, its registry holds no run_agent so it can't nest
public sealed class RunAgentTool : ITool
{
    private readonly Func<IReadOnlyList<RoleFile>> _roles;
    //null when the role may run here, else the sentence the model reads, since the checks need launch state this tool cannot see
    private readonly Func<RoleFile, string?> _refuseRole;
    //the child's prefix and the model string its requests carry, the caller's unless the role names a model
    private readonly Func<RoleFile, (Composition Comp, string ModelString)> _composeChild;
    private readonly Func<RoleFile, (ToolRegistry Tools, HookBus Hooks)> _subagentWiring;
    private readonly IChatClient _client;
    private readonly Func<(string Model, JsonElement? Sampling, JsonElement? Body, string? Suffix)> _requestSettings;
    private readonly IToolContext _parentCtx;
    private readonly Action<string>? _progress;
    //handed in, Roles can't reach the glyph table (one table, chosen once at launch)
    private readonly EngineMarks _marks;
    //changed by /model when the window resizes, read fresh at each dispatch so a swap is picked up
    private int? _contextBudget;

    public RunAgentTool(
        Func<IReadOnlyList<RoleFile>> roles,
        Func<RoleFile, string?> refuseRole,
        Func<RoleFile, (Composition Comp, string ModelString)> composeChild,
        Func<RoleFile, (ToolRegistry Tools, HookBus Hooks)> subagentWiring,
        IChatClient client,
        Func<(string Model, JsonElement? Sampling, JsonElement? Body, string? Suffix)> requestSettings,
        IToolContext parentCtx,
        Action<string>? progress = null,
        int? contextBudget = null,
        Gatto.Core.Loop.ReasoningHistory reasoningHistory = Gatto.Core.Loop.ReasoningHistory.All,
        EngineMarks? marks = null)
    {
        _reasoningHistory = reasoningHistory;
        _roles = roles;
        _refuseRole = refuseRole;
        _composeChild = composeChild;
        _subagentWiring = subagentWiring;
        _client = client;
        _requestSettings = requestSettings;
        _parentCtx = parentCtx;
        _progress = progress;
        _marks = marks ?? EngineMarks.Unicode;
        _contextBudget = contextBudget;
    }

    //the seam /model uses to set a new context budget
    public void UpdateContextBudget(int? newBudget) => _contextBudget = newBudget;

    //the seam /model uses for the reasoning-history policy, a subagent inherits it so a parent set to all isn't contradicted
    public void UpdateReasoningHistory(Gatto.Core.Loop.ReasoningHistory policy) => _reasoningHistory = policy;

    private Gatto.Core.Loop.ReasoningHistory _reasoningHistory;

    internal int? ContextBudgetForTest => _contextBudget;

    public string Name => "run_agent";
    //read on every request, so a list that a /role reloads reaches the next one, and between two /role calls the text never moves
    public string Description
    {
        get
        {
            var sb = new StringBuilder("Run a named role on a task. Available roles:");
            foreach (var r in _roles())
            {
                sb.Append("\n- ").Append(r.Name);
                if (!string.IsNullOrEmpty(r.Description)) sb.Append(": ").Append(r.Description);
            }
            return sb.ToString();
        }
    }

    //true only when at least one role loads, an empty roles folder offers no run_agent to the model
    public bool IsAvailable => _roles().Count > 0;

    public JsonElement ParametersSchema => ToolArgs.Schema("""
        {"type":"object","properties":{"role":{"type":"string"},"task":{"type":"string"}},"required":["role","task"]}
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        var roleName = ToolArgs.RequiredString(args, "role");
        var task = ToolArgs.RequiredString(args, "task");

        var roles = _roles();
        var role = roles.FirstOrDefault(r => r.Name == roleName);
        if (role is null)
            //throw here, the loop turns it into an error result with exactly this message
            throw new ArgumentException(
                $"no role named '{roleName}' — available: {string.Join(", ", roles.Select(r => r.Name))}");
        if (_refuseRole(role) is { } why) throw new InvalidOperationException(why);

        //the child starts from its own composition and no parent history, sharing the parent's tool context
        var (comp, model) = _composeChild(role);
        var (subTools, subHooks) = _subagentWiring(role);
        var convo = new Conversation(comp.SystemText);
        var (callerModel, sampling, body, suffix) = _requestSettings();
        //a role that asks for a level, or runs another model, gets the composition's settings, else the caller's, which follow /effort, the toggle and a resume
        if (role.ThinkingRequested is not null || !string.Equals(model, callerModel, StringComparison.OrdinalIgnoreCase))
            (sampling, body, suffix) = (comp.Sampling, comp.ThinkingBody, comp.ThinkingSuffix);

        //the client's StreamAsync runs once per round, so this counts turns exactly and is where the progress line fires
        var rounds = 0;
        var countingClient = new RoundCountingClient(_client, () =>
        {
            rounds++;
            _progress?.Invoke($"{role.Name} {_marks.Dot} turn {rounds}");
        });

        //the parent's context budget flows down, so a file-heavy sub-agent gets the same tool-result elision as the main loop
        var subLoop = new AgentLoop(
            countingClient, subTools, subHooks, new ChildToolContext(_parentCtx), model,
            samplingOverrides: sampling, bodyOverrides: body, budgetTokens: _contextBudget, maxRounds: role.Turns, promptSuffix: suffix,
            reasoningHistory: _reasoningHistory, trace: ctx.Trace);

        var observer = new FinalTextObserver();
        var sw = Stopwatch.StartNew();
        //the parent's trace and tool.exec span, so the child's spans land in the parent's sidecar under this call
        var result = await subLoop.RunTurnAsync(convo, task, observer, ct, traceParent: ctx.TraceParent, agent: role.Name);
        sw.Stop();

        //the observer holds raw deltas, this text is model output so it is stripped here before it becomes a tool result
        var (text, stripped) = ControlTokens.Strip(observer.FinalText);
        if (result.Outcome == TurnOutcome.Truncated)
            //a spent turn budget returns the partial work, and the round count tells the cap apart from a dropped stream
            text = rounds >= role.Turns
                ? $"[run_agent: turn limit ({role.Turns}) reached — partial result]\n" + text
                : "[run_agent: response truncated — partial result]\n" + text;

        //one round is the common case, a question needing no tool call finishes that way
        var gloss = GlossOf(role.Name, rounds, sw.Elapsed, _marks);
        //the warning row never shows for a sub-agent, so the gloss line is the only place a strip count can appear
        if (stripped > 0) gloss += $" {_marks.Dot} stripped {stripped}";
        return new ToolResult(text, Gloss: gloss);
    }

    internal static string GlossOf(string name, long rounds, TimeSpan elapsed, EngineMarks marks) =>
        $"{name} {marks.Dot} {Plural.Of(rounds, "turn")} {marks.Dot} {ElapsedText.Of(elapsed)}";

    //keeps only the last round's text, a round ending in tool calls has its text cleared
    private sealed class FinalTextObserver : ITurnObserver
    {
        private readonly StringBuilder _final = new();
        public string FinalText => _final.ToString();
        public void OnTextDelta(string t) => _final.Append(t);
        public void OnReasoningDelta(string t) { }
        public void OnToolCallStart(ToolCall c) => _final.Clear();
        public void OnToolResult(ToolCall c, ToolResult r) { }
        public void OnWarning(string m) { }
        public void OnUsage(Usage u) { }
    }

    //fires the callback once per round-trip, the per-round hook the loop doesn't expose
    private sealed class RoundCountingClient(IChatClient inner, Action onRound) : IChatClient
    {
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, CancellationToken ct = default)
        {
            onRound();
            return inner.StreamAsync(request, ct);
        }

        //forwarded whole, or a child's request spans would lose their parent
        public IAsyncEnumerable<StreamEvent> StreamAsync(ChatRequest request, long traceParent, CancellationToken ct)
        {
            onRound();
            return inner.StreamAsync(request, traceParent, ct);
        }

        public Task<int?> TryGetContextLengthAsync(CancellationToken ct = default) =>
            inner.TryGetContextLengthAsync(ct);
    }
}

//the parent's context for a child's loop with no background tasks, since the child answers before any end could reach it
file sealed class ChildToolContext(IToolContext parent) : IToolContext
{
    public string Cwd => parent.Cwd;
    public string HomePath => parent.HomePath;
    public IUserPrompter? Prompter => parent.Prompter;
    public SessionIdentity? Session => parent.Session;
    public Gatto.Core.Sandbox.SandboxState? Sandbox => parent.Sandbox;
    public BackgroundTasks? Tasks => null;
    public Gatto.Core.Trace Trace => parent.Trace;
    public long TraceParent => parent.TraceParent;
}
