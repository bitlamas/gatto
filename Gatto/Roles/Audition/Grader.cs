using Gatto.Core.Client;
using Gatto.Core.Loop;

namespace Gatto.Roles.Audition;

//one task's result, with the label kept on it and the elapsed time left at zero for the runner to stamp

//a task that ran has no skip, and one not cut for silence has no allowance. a skip is neither a pass nor a fail, and the silence is the one that expired
internal sealed record AuditionTaskResult(string TaskId, bool Pass,
    IReadOnlyList<FailureShape> Shapes, TimeSpan Elapsed, string Label = "",
    bool Skipped = false, TimeSpan? Silence = null, string Act = "")
{
    //use this to count failures rather than !Pass, which counts a skipped task as a failure
    public bool Failed => !Pass && !Skipped;
}

//pass or fail comes from the task's own artifact predicate and the shapes from the transcript, every rule a structural fact and no judge
internal static class Grader
{
    //words that mean the model noticed a problem, used only to suppress IgnoredError, an unlisted phrasing costs a missed fire
    private static readonly string[] ErrorWords =
        ["error", "fail", "wrong", "missing", "absent", "cannot", "unable", "refused",
         "not found", "no such", "does not exist", "blocked", "denied", "timed out",
         //the "n't" entry covers every negation contraction, over-suppression is the safe direction here
         "n't"];

    public static AuditionTaskResult Grade(BatteryTask task, BatteryInstance instance,
        string scratchDir, IReadOnlyList<ChatMessage> transcript, TurnResult turn,
        IReadOnlyList<string> advertisedTools)
    {
        var shapes = new List<FailureShape>();
        var calls = Transcript.AllCalls(transcript).ToList();

        if (calls.Count == 0) shapes.Add(FailureShape.NoToolCall);

        if (transcript.Any(m => m.IsError && m.Content is { } c
                && c.StartsWith(LoopErrors.MalformedArgumentsPrefix, StringComparison.Ordinal)))
            shapes.Add(FailureShape.MalformedArguments);

        if (SailedPastAnError(transcript)) shapes.Add(FailureShape.IgnoredError);
        if (RepeatedItself(calls)) shapes.Add(FailureShape.RepeatLoop);
        //gated on task.AbsentFile, the fabrication check is only decidable where the fixture guarantees a file cannot exist
        if (task.AbsentFile is not null && Transcript.FabricatedContent(instance.Prompt, transcript))
            shapes.Add(FailureShape.FabricatedResult);

        //keyed on turn.FinishReason length, TruncationKind.Length really means the round cap and would report a runaway when a subagent just used its rounds
        if (string.Equals(turn.FinishReason, "length", StringComparison.Ordinal))
            shapes.Add(FailureShape.Runaway);

        if (calls.Any(c => !advertisedTools.Contains(c.Name, StringComparer.Ordinal)))
            shapes.Add(FailureShape.UnknownTool);

        var pass = task.Artifact(instance, scratchDir, transcript);
        return new AuditionTaskResult(task.Id, pass, shapes, TimeSpan.Zero, task.Label,
            Act: task.Act);
    }

    //an errored tool result, each error paired with the next thing the model did. a tool call between the error and the next words counts as noticing it
    private static bool SailedPastAnError(IReadOnlyList<ChatMessage> transcript)
    {
        for (var i = 0; i < transcript.Count; i++)
        {
            if (!transcript[i].IsError) continue;

            var acted = false;
            for (var j = i + 1; j < transcript.Count; j++)
            {
                if (transcript[j].ToolCalls is { Count: > 0 }) { acted = true; break; }
                if (transcript[j].Role == "assistant" && !string.IsNullOrWhiteSpace(transcript[j].Content))
                {
                    if (!ErrorWords.Any(w => transcript[j].Content!.Contains(w, StringComparison.OrdinalIgnoreCase)))
                        return true;
                    break;                 //the model acknowledged it in words, this error is not the shape
                }
            }
            if (acted) continue;           //a tool call between the error and the next words counts as noticing it
        }
        return false;
    }

    //the same tool with the same arguments three times running, two is a retry and exactly the recovery the battery wants
    private static bool RepeatedItself(IReadOnlyList<ToolCall> calls)
    {
        var run = 1;
        for (var i = 1; i < calls.Count; i++)
        {
            var same = calls[i].Name == calls[i - 1].Name
                && string.Equals(calls[i].ArgumentsJson, calls[i - 1].ArgumentsJson, StringComparison.Ordinal);
            run = same ? run + 1 : 1;
            if (run >= 3) return true;
        }
        return false;
    }
}
