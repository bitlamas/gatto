namespace Gatto.Core.Loop;

//the lines a stopped turn warns with, kept apart so the middle dot they hold has one home, which each face redraws in its own glyph set
internal static class TurnLines
{
    public static string Discarded(string what, int begun) =>
        begun > 0 ? $"{what} · {Plural.Of(begun, "tool call")} discarded, not run" : what;

    public const string RemainingCancelled = "turn cancelled · remaining tool calls cancelled, not run";

    //what a cut stream's record ends with, for the model's next request. the screen says it on the turn's line instead, live and on a resume
    public const string TruncatedMark = " [truncated]";
}
