using Gatto.Cli;
using Gatto.Roles;
using Gatto.Roles.Audition;
using Gatto.Terminal;

namespace Gatto.Tests.Copy;

//the audition's kept rows through the seams the CLI uses, so the row order, the tally and the band are checked as rendered
public class AuditionCorpusTests
{
    private static readonly EngineMarks Marks = EngineMarks.Unicode;

    //mirrors GattoApp.InkFor, the mapping from meaning to colour, so the golden pins what Render composes under that seam
    private static Func<string, ReportInk, string>? PaintFor(CorpusMode mode)
    {
        var theme = CorpusDriver.ThemeFor(mode);
        return theme is null
            ? null
            : (text, ink) => theme.Paint(text, ink switch
            {
                ReportInk.Ok => Theme.Ok,
                ReportInk.Fail => Theme.Err,
                ReportInk.Accent => Theme.Accent,
                _ => Theme.Dim,
            });
    }

    private static AuditionStamp Stamp => new(CorpusFacts.Build, CorpusFacts.ModelId + ".gguf",
        Quant: "Q4_K_M", Context: 131_072, SamplingNote: "temperature 1.0, top_k 40");

    private static AuditionTaskResult Task(string id, bool pass, string label, double secs,
        IReadOnlyList<FailureShape>? shapes = null, bool skipped = false) =>
        new(id, pass, shapes ?? [], TimeSpan.FromSeconds(secs), label, Skipped: skipped);

    //the three verdicts the report can open with

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void THE_REPORT_OF_A_PASSED_MODEL(CorpusMode mode)
    {
        //a pass with one failed task on purpose, the tally beside verified is what keeps the wizard and the report from disagreeing
        var verdict = new AuditionVerdict(Pass: true, Disqualified: false,
            [
                Task("B1", true, "answers with the right tool", 2.4),
                Task("B2", true, "reads back what it fetched", 5.1),
                Task("B3", false, "keeps the schema", 8.0, [FailureShape.MalformedArguments]),
                Task("B4", true, "refuses the impossible", 3.3),
                Task("B5", true, "finishes the loop", 6.6),
            ],
            Stamp, TimeSpan.FromSeconds(84), DecodeTokS: 62.4);

        CopyGolden.Check($"audition-report-pass-{mode}", CorpusDriver.Visible(
            AuditionReport.Render(verdict, TermText.Sanitize, Marks, PaintFor(mode),
                today: CorpusFacts.Today)));
    }

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void THE_REPORT_OF_A_DISQUALIFIED_MODEL(CorpusMode mode)
    {
        var verdict = new AuditionVerdict(Pass: false, Disqualified: true,
            [
                Task("B1", true, "answers with the right tool", 2.1),
                Task("B2", false, "runs what it says", 4.9,
                    [FailureShape.FabricatedResult, FailureShape.MalformedArguments]),
            ],
            Stamp, TimeSpan.FromSeconds(41), DecodeTokS: 18.9);

        CopyGolden.Check($"audition-report-fabrication-{mode}", CorpusDriver.Visible(
            AuditionReport.Render(verdict, TermText.Sanitize, Marks, PaintFor(mode),
                today: CorpusFacts.Today)));
    }

    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void THE_REPORT_OF_A_RUN_CUT_SHORT(CorpusMode mode)
    {
        //two failures of the three that ran and two unrun rows marked with the dot, and the speed row shows its not-reported arm
        var verdict = new AuditionVerdict(Pass: false, Disqualified: false,
            [
                Task("B1", false, "answers with the right tool", 3.2, [FailureShape.NoToolCall]),
                Task("B2", false, "reads back what it fetched", 6.0, [FailureShape.Stalled]),
                Task("B3", true, "keeps the schema", 4.4),
                Task("B4", false, "refuses the impossible", 0, skipped: true),
                Task("B5", false, "finishes the loop", 0, skipped: true),
            ],
            Stamp, TimeSpan.FromSeconds(63), DecodeTokS: null);

        CopyGolden.Check($"audition-report-stopped-{mode}", CorpusDriver.Visible(
            AuditionReport.Render(verdict, TermText.Sanitize, Marks, PaintFor(mode),
                today: CorpusFacts.Today)));
    }

    //the speed bands, guarded on the rendered row

    [Theory]
    [InlineData(95.0)]    //great
    [InlineData(62.4)]    //good
    [InlineData(30.0)]    //usable
    [InlineData(7.2)]     //slow
    [InlineData(0.4)]     //unusable
    public void THE_SPEED_BAND_ARRIVES_WHOLE(double rate)
    {
        //assert the rendered row, the word and its tail are one unit. a census would watch the table sit intact while a render cuts the row to fit
        var verdict = new AuditionVerdict(Pass: true, Disqualified: false,
            [], Stamp, TimeSpan.FromSeconds(10), rate);

        var rendered = AuditionReport.Render(verdict, TermText.Sanitize, Marks,
            today: CorpusFacts.Today);
        var note = AuditionReport.SpeedNote(AuditionReport.Shown(rate));

        Assert.Contains("generating · " + note, rendered, StringComparison.Ordinal);
    }

    //the rows the runner reports while it runs

    //a pipe is the truth here, the live ticks are TickLine's contract. check the sentences and the gutter here, a Started event commits nothing
    [Theory]
    [InlineData(CorpusMode.Dark)]
    [InlineData(CorpusMode.Plain)]
    public void THE_PROGRESS_ROWS_AS_THE_RUNNER_REPORTS(CorpusMode mode)
    {
        var w = new StringWriter();
        using var console = new AuditionConsole(w, rich: false, PaintFor(mode),
            windowWidth: () => 80, glyphs: CorpusDriver.Glyphs);

        console.Report(AuditionProgress.Reusing(
            $"using the server already running for {CorpusFacts.ModelId}"));
        console.Report(AuditionProgress.Started(Battery.Tasks[0], 1, 5));
        console.Report(AuditionProgress.Done(Battery.Tasks[0], 1, 5, pass: true));
        console.Report(AuditionProgress.Started(Battery.Tasks[1], 2, 5));
        console.Report(AuditionProgress.Done(Battery.Tasks[1], 2, 5, pass: false));
        console.Report(AuditionProgress.Note(
            "stopped early. Enough tasks had failed that the result was already clear"));
        console.Finish();

        CopyGolden.Check($"audition-progress-{mode}", CorpusDriver.Visible(w.ToString()));
    }
}
