using Gatto.Core;

namespace Gatto.Roles.Audition;

//the words for what a task did, one home for the report and the wizard, with each face adding its own glyph
internal static class TaskWords
{
    //the fabrication sentence is a constant because it renders twice, as this table's row and as the headline
    internal const string Fabrication = "made up the contents of a file that doesn't exist";

    //every failure shape in a sentence, since the enum name tells a novice nothing and a shape is there to be read aloud
    private static string InWords(FailureShape shape, AuditionTaskResult task) => shape switch
    {
        //the sentence names the task's own deed, the generic wording reads as a claim about the run. a result with no act falls back to the shipped sentence
        FailureShape.NoToolCall => task.Act is { Length: > 0 } act
            ? $"answered in words, never {act}"
            : "never called a tool",
        FailureShape.MalformedArguments => "asked for it in a way gatto couldn't read",
        FailureShape.IgnoredError => "a step failed and it carried on as if it had worked",
        FailureShape.RepeatLoop => "got stuck repeating the same step",
        //the sentence keeps "doesn't exist", the phrase is the whole finding rather than decoration on it
        FailureShape.FabricatedResult => Fabrication,
        FailureShape.Runaway => "kept talking until gatto cut it off",
        FailureShape.UnknownTool => "asked for a tool gatto doesn't have",

        //the two cut sentences are evidence of whose problem it was, both tasks are failed either way and neither may grow a hedge
        FailureShape.StoppedAtCap => "stopped at the token cap, still generating, no answer produced",
        FailureShape.Stalled => task.Silence is { } quiet
            //the allowance that actually expired, the first-token wait and a mid-generation gap are different numbers
            ? $"stopped, nothing came back for {Roughly(quiet)}"
            : "stopped, nothing came back at all",

        _ => shape.ToString(),
    };

    //a silence in the words a waiting person uses

    //the plural comes off the rounded number the reader sees, and one second is the floor of the seconds arm
    private static string Roughly(TimeSpan t) =>
        t.TotalSeconds < 90
            ? Plural.Of((long)Math.Max(1, Math.Round(t.TotalSeconds)), "second")
            : Plural.Of((long)Math.Round(t.TotalMinutes), "minute");

    //what a task that never ran says, the model was never asked so the row is not a failure and not a pass
    private const string SkippedReason = "not run: the result was already clear";

    //what a failed task says when no shape fired, the wrong answer is a matter for this sentence and not for a new shape
    private const string NoShapeReason = "produced the wrong result";

    //every reason the task failed, its shapes or the plain fact that the answer was wrong
    public static IEnumerable<string> Reasons(AuditionTaskResult task) =>
        task.Skipped ? [SkippedReason]
        : task.Shapes.Count > 0 ? task.Shapes.Select(s => InWords(s, task))
        : [NoShapeReason];

    //the one sentence a row shows, null when there is nothing to explain, and a passing row that tripped a shape still says why
    public static string? Why(AuditionTaskResult task) =>
        task.Pass && task.Shapes.Count == 0 ? null : string.Join("; ", Reasons(task));
}
