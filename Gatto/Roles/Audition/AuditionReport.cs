using System.Text;
using Gatto.Core;

namespace Gatto.Roles.Audition;

//the report's palette, named by meaning so the caller picks the colour, Roles can't reach the terminal layer
internal enum ReportInk
{
    //a pass, and the verdict line when it passes
    Ok,
    //a failure, and the verdict line when it fails
    Fail,
    //supporting detail like task labels and timings, dim so the eye can skip it
    Dim,
    //the one figure a user hunts for, only one thing gets this accent, an accent on everything is nothing
    Accent,
}

//the audition report, a user-facing surface, so no emoji and a failure is said in words a novice can act on
internal static class AuditionReport
{
    //the only home of the speed bands, and the words describe the wait so nobody reads them as a verdict on the model. a band's word keeps its tail
    internal static string SpeedNote(double tokensPerSecond) => tokensPerSecond switch
    {
        >= 80 => "great, most turns in seconds",
        >= 50 => "good for daily driving",
        >= 20 => "usable",
        >= 5 => "slow, expect waiting",
        _ => "unusable, minutes per answer",
    };

    //the served-by value, null for an unrecorded server and for gatto's own pinned build (the pin matches as a prefix with its hyphen)
    internal static string? ServerNote(string? buildInfo)
    {
        if (buildInfo is not { Length: > 0 } b) return null;

        var pin = Gatto.Roles.LlamaAssetSteering.PinnedRelease;
        var ours = string.Equals(b, pin, StringComparison.Ordinal)
                   || b.StartsWith(pin + "-", StringComparison.Ordinal);

        return ours ? null : b;
    }

    //rounds the rate to what the screen shows, and the band reads this value so the text and the figure can't disagree
    internal static double Shown(double r) =>
        r >= 10 ? Math.Round(r, 0, MidpointRounding.AwayFromZero)
        : r >= 1 ? Math.Round(r, 1, MidpointRounding.AwayFromZero)
        : Math.Round(r, 2, MidpointRounding.AwayFromZero);

    //formats the shown figure, with the precision the value deserves, and a rate that would round to zero prints <0.01 tok/s
    private static string Rate(double shown) =>
        shown <= 0 ? "<0.01 tok/s"
        : shown >= 10 ? $"~{shown:F0} tok/s"
        : $"~{shown:0.##} tok/s";

    //delegates to TaskWords, the one home of the sentences the wizard's fail screens also draw
    private static IEnumerable<string> Reasons(AuditionTaskResult task) => TaskWords.Reasons(task);

    //sanitize is required and runs before paint, a defaulted no-op would leave attacker text unsanitized
    public static string Render(AuditionVerdict verdict, Func<string, string> sanitize,
        EngineMarks marks, Func<string, ReportInk, string>? paint = null, DateOnly? today = null)
    {
        //the date is a parameter so a golden does not move at midnight. production omits it and gets the real clock.
        var stampedOn = today ?? DateOnly.FromDateTime(DateTime.Now);
        var s = new StringBuilder();
        var stamp = verdict.Stamp;
        //a skipped task failed nothing, counting it as a failure would say the model failed tasks it was never asked
        var failed = verdict.Tasks.Where(t => t.Failed).ToList();
        var skipped = verdict.Tasks.Where(t => t.Skipped).ToList();
        string Ink(string text, ReportInk ink) => paint is null ? text : paint(text, ink);

        //the verdict line, first because it is the answer
        if (verdict.Pass)
        {
            //the tally comes from AuditionVerdict.Tally so the headline and the table can't part, and a skipped task counts as neither
            var (passed, took) = verdict.Tally();
            var margin = took > 0 && failed.Count > 0 ? $"{passed} of {took} tasks {marks.Dot} " : "";
            s.AppendLine(Ink(
                $"{marks.Ok} tool-calling verified {marks.Dot} {margin}{stampedOn:yyyy-MM-dd}",
                ReportInk.Ok));
        }
        else if (verdict.Disqualified)
        {
            //fabrication overrides the score, so it is named as the reason rather than listed with the failed tasks
            s.AppendLine(Ink($"{marks.Bad} not verified {marks.Dot} the model {TaskWords.Fabrication}", ReportInk.Fail));
            s.AppendLine(Ink("  (disqualifying at any score: this is the failure a user cannot detect)",
                ReportInk.Dim));
        }
        else
        {
            //every failed task contributes a reason, including one no detector fired on
            var reasons = failed.SelectMany(t => Reasons(t).Select(r => $"{r} ({t.TaskId})"))
                .Distinct().ToList();
            //the denominator is what ran, the battery's size would invite arithmetic that counts unrun tasks as passes
            var ran = verdict.Tally().Ran;
            s.AppendLine(Ink(skipped.Count > 0
                ? $"{marks.Bad} not verified {marks.Dot} failed {failed.Count} of the {ran} tasks that ran"
                : $"{marks.Bad} not verified {marks.Dot} failed {failed.Count} of {verdict.Tasks.Count} tasks",
                ReportInk.Fail));
            if (reasons.Count > 0) s.AppendLine("  " + string.Join("; ", reasons));
        }

        //say the run was stopped early, once, outside the branches above since a disqualified run can be cut short too
        if (skipped.Count > 0)
            s.AppendLine(Ink($"  stopped early. Enough tasks had failed that the result was already clear, so "
                + $"{(skipped.Count == 1 ? "one task was" : $"{skipped.Count} tasks were")} not run",
                ReportInk.Dim));
        s.AppendLine();

        //what each task did, the label column is as wide as the widest label so the timings line up
        var labelWidth = verdict.Tasks.Count == 0 ? 0 : verdict.Tasks.Max(t => t.Label.Length);
        foreach (var t in verdict.Tasks)
        {
            //a failing row always shows its reasons, and a passing row that tripped a shape shows them too
            var why = t.Pass && t.Shapes.Count == 0 ? "" : "  " + string.Join("; ", Reasons(t));
            var label = labelWidth > 0 ? Ink(t.Label.PadRight(labelWidth), ReportInk.Dim) + "  " : "";
            //a skipped row gets no mark and no time, neither happened so nothing was measured
            var glyph = t.Skipped ? Ink(marks.Dot, ReportInk.Dim)
                : Ink(t.Pass ? marks.Ok : marks.Bad, t.Pass ? ReportInk.Ok : ReportInk.Fail);
            var time = Ink(t.Skipped ? "      " : $"{t.Elapsed.TotalSeconds,5:F1}s", ReportInk.Dim);
            s.AppendLine($"  {glyph} {t.TaskId}  {label}{time}{why}");
        }
        s.AppendLine();

        //what was measured, so the result can be attributed to this run
        var rows = new List<(string Key, string Value)>
        {
            ("model", sanitize(stamp.ModelFileName)),
        };
        if (stamp.Quant is { } q) rows.Add(("quant", sanitize(q)));
        rows.Add(("context", stamp.Context.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        rows.Add(("sampling", sanitize(stamp.SamplingNote)));
        //skip the thinking row when the note is empty, an empty row is worse than none
        if (stamp.ThinkingNote is { Length: > 0 } thinking) rows.Add(("thinking", sanitize(thinking)));

        //the rate is rounded once and both the figure and the band read that value, so the row can't disagree with itself
        rows.Add(("speed", verdict.DecodeTokS is { } r
            ? $"{Ink(Rate(Shown(r)), ReportInk.Accent)} generating {marks.Dot} {SpeedNote(Shown(r))}"
            : "not reported " + marks.Dot + " this server sends no timings"));
        rows.Add(("gatto build", stamp.GattoBuild));   //the key says gatto build, since a bare build beside the model's facts reads as the model's own

        //the served-by row appears only when another server served the run, a row that always shows goes unread
        if (ServerNote(stamp.Server) is { } server) rows.Add(("served by", sanitize(server)));

        var keyWidth = rows.Max(r => r.Key.Length);   //as wide as the widest key present, so no value is pushed out of line
        foreach (var (key, value) in rows)
            s.AppendLine($"  {Ink(key.PadRight(keyWidth), ReportInk.Dim)}  {value}");
        s.AppendLine();

        //timing is evidence, it never decides the verdict
        s.Append(Ink($"  ran in {verdict.WallClock.TotalSeconds:F0} s", ReportInk.Dim));
        return s.ToString();
    }
}
