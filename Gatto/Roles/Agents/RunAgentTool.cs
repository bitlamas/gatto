using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;

namespace Gatto.Roles.Agents;

//runs a named sub-agent in a fresh loop sharing only the parent's permission gate, its registry holds no run_agent so it can't nest
public sealed class RunAgentTool : ITool
{
    private readonly Func<IReadOnlyList<AgentDefinition>> _definitions;
    private readonly Func<AgentDefinition, (ToolRegistry Tools, HookBus Hooks)> _subagentWiring;
    private readonly IChatClient _client;
    private readonly Func<(string Model, JsonElement? Sampling, JsonElement? Body)> _requestSettings;
    private readonly string _nudgeAppend;
    private readonly IToolContext _parentCtx;
    private readonly Action<string>? _progress;
    //handed in, Roles can't reach the glyph table (one table, chosen once at launch)
    private readonly EngineMarks _marks;
    //changed by /model when the window resizes, read fresh at each dispatch so a swap is picked up
    private int? _contextBudget;

    public RunAgentTool(
        Func<IReadOnlyList<AgentDefinition>> definitions,
        Func<AgentDefinition, (ToolRegistry Tools, HookBus Hooks)> subagentWiring,
        IChatClient client,
        Func<(string Model, JsonElement? Sampling, JsonElement? Body)> requestSettings,
        string nudgeAppend,
        IToolContext parentCtx,
        Action<string>? progress = null,
        int? contextBudget = null,
        Gatto.Core.Loop.ReasoningHistory reasoningHistory = Gatto.Core.Loop.ReasoningHistory.All,
        EngineMarks? marks = null)
    {
        _reasoningHistory = reasoningHistory;
        _definitions = definitions;
        _subagentWiring = subagentWiring;
        _client = client;
        _requestSettings = requestSettings;
        _nudgeAppend = nudgeAppend;
        _parentCtx = parentCtx;
        _progress = progress;
        _marks = marks ?? EngineMarks.Unicode;
        _contextBudget = contextBudget;

        //the description lists the agents as they were at construction, one "- name: description" line each
        var sb = new StringBuilder("Run a named subagent on a task. Available agents:\n");
        foreach (var d in definitions())
            sb.Append("- ").Append(d.Name).Append(": ").Append(d.Description).Append('\n');
        Description = sb.ToString().TrimEnd('\n');
    }

    //the seam /model uses to set a new context budget
    public void UpdateContextBudget(int? newBudget) => _contextBudget = newBudget;

    //the seam /model uses for the reasoning-history policy, a subagent inherits it so a parent set to all isn't contradicted
    public void UpdateReasoningHistory(Gatto.Core.Loop.ReasoningHistory policy) => _reasoningHistory = policy;

    private Gatto.Core.Loop.ReasoningHistory _reasoningHistory;

    internal int? ContextBudgetForTest => _contextBudget;

    public string Name => "run_agent";
    public string Description { get; }

    //true only when at least one agent exists, an empty agents folder offers no run_agent to the model
    public bool IsAvailable => _definitions().Count > 0;

    public JsonElement ParametersSchema => ToolArgs.Schema("""
        {"type":"object","properties":{"agent":{"type":"string"},"task":{"type":"string"}},"required":["agent","task"]}
        """);

    public async Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        var agentName = ToolArgs.RequiredString(args, "agent");
        var task = ToolArgs.RequiredString(args, "task");

        var defs = _definitions();
        var def = defs.FirstOrDefault(d => d.Name == agentName);
        if (def is null)
            //throw here, the loop turns it into an error result with exactly this message
            throw new ArgumentException(
                $"no agent named '{agentName}' — available: {string.Join(", ", defs.Select(d => d.Name))}");

        //the sub-agent starts from its own prompt plus the model's nudge and no parent history, sharing the parent's tool context
        var (subTools, subHooks) = _subagentWiring(def);
        var convo = new Conversation(ComposeSystemPrompt(def.SystemPrompt, _nudgeAppend));
        var (model, sampling, body) = _requestSettings();

        //the client's StreamAsync runs once per round, so this counts turns exactly and is where the progress line fires
        var rounds = 0;
        var countingClient = new RoundCountingClient(_client, () =>
        {
            rounds++;
            _progress?.Invoke($"{def.Name} {_marks.Dot} turn {rounds}");
        });

        //the parent's context budget flows down, so a file-heavy sub-agent gets the same tool-result elision as the main loop
        var subLoop = new AgentLoop(
            countingClient, subTools, subHooks, _parentCtx, model,
            samplingOverrides: sampling, bodyOverrides: body, budgetTokens: _contextBudget, maxRounds: def.MaxTurns,
            reasoningHistory: _reasoningHistory);

        var observer = new FinalTextObserver();
        var sw = Stopwatch.StartNew();
        var result = await subLoop.RunTurnAsync(convo, task, observer, ct);
        sw.Stop();

        //the observer holds raw deltas, this text is model output so it is stripped here before it becomes a tool result
        var (text, stripped) = ControlTokens.Strip(observer.FinalText);
        if (result.Outcome == TurnOutcome.Truncated)
            //a spent turn budget returns the partial work, and the round count tells the cap apart from a dropped stream
            text = rounds >= def.MaxTurns
                ? $"[run_agent: turn limit ({def.MaxTurns}) reached — partial result]\n" + text
                : "[run_agent: response truncated — partial result]\n" + text;

        //one round is the common case, a question needing no tool call finishes that way
        var gloss = GlossOf(def.Name, rounds, sw.Elapsed, _marks);
        //the warning row never shows for a sub-agent, so the gloss line is the only place a strip count can appear
        if (stripped > 0) gloss += $" {_marks.Dot} stripped {stripped}";
        return new ToolResult(text, Gloss: gloss);
    }

    internal static string GlossOf(string name, long rounds, TimeSpan elapsed, EngineMarks marks) =>
        $"{name} {marks.Dot} {Plural.Of(rounds, "turn")} {marks.Dot} {ElapsedText.Of(elapsed)}";

    //the agent's own prompt plus the model's nudge append, joined with a blank line, an empty nudge adds nothing
    private static string ComposeSystemPrompt(string body, string nudgeAppend) =>
        string.IsNullOrWhiteSpace(nudgeAppend) ? body : body + "\n\n" + nudgeAppend;

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

        public Task<int?> TryGetContextLengthAsync(CancellationToken ct = default) =>
            inner.TryGetContextLengthAsync(ct);
    }
}
