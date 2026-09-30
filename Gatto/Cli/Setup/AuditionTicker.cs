using System.Diagnostics;
using Gatto.Repl;
using Gatto.Repl.Render;
using Gatto.Roles.Audition;

namespace Gatto.Cli.Setup;

//the audition's one progress line, reused from TickLine. the timer keeps it moving between task events, and plain mode commits one line per task
internal sealed class AuditionTicker : IProgress<AuditionProgress>, IDisposable
{
    private readonly TickLine _tick;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Timer? _timer;
    private readonly object _gate = new();
    private string _task = "";
    private bool _done;

    //how often the row repaints, matching ChromeTicker so gatto's motion reads at one speed
    private const int FrameMs = 140;

    private readonly Gatto.Terminal.GlyphSet _glyphs;
    private readonly Gatto.Repl.Render.PurrFrames _frames;

    //the purr this ticker uses, chosen once at construction. a null value keeps pool index 0, which every test gets
    public AuditionTicker(TextWriter output, bool rich, Gatto.Terminal.GlyphSet glyphs,
        Func<int>? windowWidth = null, Gatto.Repl.Render.PurrFrames? frames = null)
    {
        _glyphs = glyphs;
        _frames = frames ?? Gatto.Repl.Render.PurrFrames.Full;
        _tick = new TickLine(output, rich, paint: null, windowWidth);
        //no timer in plain mode, since TickLine.Live does nothing there and the ticks would be wasted work
        if (rich) _timer = new Timer(_ => Repaint(), null, FrameMs, FrameMs);
    }

    //the row as a pure function, so a test needs no clock or console. it takes the full purr, since an audition is gatto working rather than waiting
    internal static string Row(long elapsedMs, string task, Gatto.Terminal.GlyphSet? glyphs,
        PurrFrames? frames = null) =>
        ChromeTicker.PurrHead(Cats.Face(glyphs), elapsedMs, frames ?? PurrFrames.Full)
        + (task.Length > 0 ? " " + (glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Dot + " " + task : "");

    public void Report(AuditionProgress p)
    {
        switch (p.Stage)
        {
            //a note is something the user keeps, so it survives the erase
            case AuditionStage.Note:
                lock (_gate) _tick.Note("  " + p.Text);
                break;

            //the reuse event is not forwarded here, since this wizard started that server and said so. the CLI prints it, where it explains why nothing loaded
            case AuditionStage.Reusing:
                break;

            case AuditionStage.Started:
                lock (_gate) { _task = $"task {p.Index} of {p.Total}"; }
                Repaint();
                break;

            //in rich mode the live row moves on to the next task, the count is the progress. plain mode commits one line per task
            case AuditionStage.Done when !_tick.Rich:
                lock (_gate) _tick.Note($"  {(p.Pass ? _glyphs.Ok : _glyphs.Bad)} {p.TaskId}  {p.Label}");
                break;
        }
    }

    private void Repaint()
    {
        lock (_gate)
        {
            if (_done) return;
            _tick.Live("  " + Row(_clock.ElapsedMilliseconds, _task, _glyphs, _frames));
        }
    }

    //takes the row down before anything else is written, and latches so a tick in flight can't repaint over it
    public void Finish()
    {
        lock (_gate)
        {
            _done = true;
            _tick.Finish();
        }
    }

    public void Dispose()
    {
        _timer?.Dispose();
        Finish();
        _tick.Dispose();
    }
}
