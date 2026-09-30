using System.Text.Json;
using Gatto.Core.Loop;
using Gatto.Core.Tools;

namespace Gatto.Roles;

//the gate makes the model restate the task against the brief, and until that passes every write gets a nudge appended
public sealed class GroundingGate
{
    //only these results get the nudge
    private static readonly HashSet<string> DeliverableTools =
        new(StringComparer.Ordinal) { "write_file", "edit_file" };

    //the reminder text, it is appended after a blank line
    public const string NudgeReminder =
        "[grounding gate] You have not passed task_restate yet. Before producing more deliverables, " +
        "call task_restate on the brief to confirm you are answering the task that was actually asked.";

    private bool _grounded;

    public GroundingGate() => RestateTool = new TaskRestateTool(this);

    //the task_restate tool this gate owns, registered while the gate is armed. it is the only thing that grounds the session
    public ITool RestateTool { get; }

    //true once a restatement has passed the anchor check, and it stays true until Reset()
    public bool IsGrounded => _grounded;

    //whether the nudge is live for the current role. the gate is only built when the launch role wants grounding, so it starts true
    public bool Armed { get; set; } = true;

    internal void MarkGrounded() => _grounded = true;

    //clears the grounded latch. the /new and /compact commands rotate to a fresh session, whose context no longer holds the checked restatement
    public void Reset() => _grounded = false;

    //appends the reminder to a write or edit result while ungrounded, and returns null so every other result passes through untouched
    public Task<ToolResult?> NudgeToolResultAsync(HookPayload payload)
    {
        if (!Armed || _grounded || payload.Result is not { } result) return Task.FromResult<ToolResult?>(null);
        var name = payload.Call?.Name;
        if (name is null || !DeliverableTools.Contains(name)) return Task.FromResult<ToolResult?>(null);
        return Task.FromResult<ToolResult?>(result with { Text = result.Text + "\n\n" + NudgeReminder });
    }
}

//reads the brief, scores the restatement and grounds the gate on a pass. a missing brief file is an ordinary result, malformed input throws
file sealed class TaskRestateTool(GroundingGate gate) : ITool
{
    public string Name => "task_restate";

    //the tool is always registered, so this is what keeps it out of a disarmed role's tool list
    public bool IsAvailable => gate.Armed;

    public string Description =>
        "FIRST action of a task: restate the task in your own words and pin the deliverables, " +
        "acceptance criteria, and out-of-scope items. Checked mechanically against the brief -- if " +
        "your restatement misses the brief's key terms you may be answering a different task, and you " +
        "will be asked to re-read and restate. Call this before doing any work.";

    public JsonElement ParametersSchema => JsonDocument.Parse("""
        {"type":"object","properties":{
          "brief_file":{"type":"string","description":"The brief/task file you are restating (e.g. docs/M1-BRIEF.md)."},
          "task":{"type":"string","description":"The task in your own words, 2-6 sentences."},
          "deliverables":{"type":"array","items":{"type":"string"},"description":"Exact output paths you will produce."},
          "acceptance":{"type":"array","items":{"type":"string"},"description":">=3 bullets of what 'done' means."},
          "out_of_scope":{"type":"array","items":{"type":"string"},"description":">=1 explicit non-goal."}
        },"required":["brief_file","task","deliverables","acceptance","out_of_scope"]}
        """).RootElement;

    public async Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        var briefFile = RequiredString(args, "brief_file");
        var task = RequiredString(args, "task");
        var deliverables = RequiredStringArray(args, "deliverables");
        var acceptance = RequiredStringArray(args, "acceptance");
        var outOfScope = RequiredStringArray(args, "out_of_scope");

        var abs = Path.IsPathRooted(briefFile) ? briefFile : Path.Combine(ctx.Cwd, briefFile);
        if (!File.Exists(abs))
            //a missing brief file is an ordinary result naming the path, so the model can correct it
            return new ToolResult($"task_restate: brief_file not found: {briefFile}");

        var brief = await File.ReadAllTextAsync(abs, ct);
        var anchors = Grounding.ExtractAnchors(brief);
        var restatement = string.Join("\n",
            new[] { task }.Concat(deliverables).Concat(acceptance).Concat(outOfScope));
        var score = Grounding.CheckRestatement(restatement, anchors);

        if (score.Passed)
        {
            gate.MarkGrounded();
            return new ToolResult(
                $"GROUNDED ({score.K}/{score.N} anchors matched). Restatement accepted -- proceed with the work you described.");
        }

        var missing = string.Join(", ", score.Missing.Take(8));
        return new ToolResult(
            $"Your restatement matches only {score.K}/{score.N} of the brief's key terms ({Path.GetFileName(briefFile)}). " +
            $"You may be answering a different task than the one asked. Missing terms: {missing}. " +
            $"Re-read {briefFile} carefully and call task_restate again.");
    }

    private static string RequiredString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new ArgumentException($"missing required parameter: {name}");

    private static IReadOnlyList<string> RequiredStringArray(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
            throw new ArgumentException($"missing required parameter: {name}");
        var list = new List<string>();
        foreach (var e in v.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.String)
                throw new ArgumentException($"parameter {name} must be an array of strings");
            list.Add(e.GetString()!);
        }
        return list;
    }
}
