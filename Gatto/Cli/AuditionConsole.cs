using Gatto.Roles.Audition;

namespace Gatto.Cli;

//the live tick is one line rewritten in place and erased before the report. each tick goes out on the calling thread so one can't arrive after the report
internal sealed class AuditionConsole : IProgress<AuditionProgress>, IDisposable
{
    private readonly TickLine _tick;

    public AuditionConsole(
        TextWriter output,
        bool rich,
        Func<string, ReportInk, string>? paint = null,
        Func<int>? windowWidth = null,
        Gatto.Terminal.GlyphSet? glyphs = null)
        //bind the palette here, TickLine must not know ReportInk (that enum lives in Roles, and one shade isn't worth a dependency edge)
    {
        _glyphs = glyphs ?? Gatto.Terminal.GlyphSet.Unicode;
        _tick = new TickLine(output, rich,
            paint is null ? null : s => paint(s, ReportInk.Dim), windowWidth);
    }

    private readonly Gatto.Terminal.GlyphSet _glyphs;

    public void Report(AuditionProgress p)
    {
        switch (p.Stage)
        {
            //a note is a line the user keeps and must survive the erase (gatto started a server on their machine)
            case AuditionStage.Reusing:      //news at the CLI: it is why nothing loaded
            case AuditionStage.Note:
                _tick.Note("  " + p.Text);
                break;

            case AuditionStage.Started:
                _tick.Live($"  ({p.Index} of {p.Total}) {p.Label}{_glyphs.Ellipsis}");   //no-op in a log
                break;

            case AuditionStage.Done when _tick.Rich:
                _tick.Live($"  ({p.Index} of {p.Total}) {p.Label} {(p.Pass ? "✓" : "✗")}");
                break;

            //in a log, commit one line per task and only on Done, a Started line would say the same thing twice
            case AuditionStage.Done:
                _tick.Note($"  {(p.Pass ? "✓" : "✗")} {p.TaskId}  {p.Label}");
                break;
        }
    }

    //take the live line down before the report renders
    public void Finish() => _tick.Finish();

    public void Dispose() => _tick.Dispose();
}
