using Gatto.Cli.Setup;
using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Tests.Fakes;

//drive a wizard face with scripted keys and read the screen back. the surface and the plain writer share one buffer, so their interleaving is visible
internal sealed class WizardRig(int width = 80, Gatto.Terminal.GlyphSet? glyphs = null)
{
    public static readonly Theme T = new(new TermCaps(true, true));

    public readonly RecordingSurface Surface = new() { Width = width };
    private readonly List<ConsoleKeyInfo> _keys = [];

    //the key source of the last face, so the clock can ask whether a scripted key waits. the source registers itself, since a separate count would be a second answer
    private Keys? _source;

    //capture the screen at every block on input, since the final screen erases the prompt block and the raw stream holds text erased later
    public List<string> Frames { get; } = [];

    //the rows the full-screen face composed at the same moments as Frames, empty on the plain face. a replay hides row overflow, so a width failure shows only here
    public List<IReadOnlyList<string>> PaintedFrames { get; } = [];

    private Gatto.Cli.Setup.Tui.TuiWizardSurface? _tui;

    //one method captures both lists, so they cannot record different moments.
    private void Snapshot()
    {
        Frames.Add(TerminalReplay.Plain(Surface.Text));
        if (_tui is not null) PaintedFrames.Add(_tui.LastPainted);
    }

    public SetupFace Face(params ConsoleKeyInfo[] keys)
    {
        _keys.AddRange(keys);
        //this face's only wait is a watch poll, so the watch flag is always true here.
        return new SetupFace(Surface, T, new Keys(this, [.. _keys]), new Writer(Surface),
            clock: () => new RigClock(this, isWatchPoll: true), glyphs: glyphs);
    }

    //the face that ships. the other member is the plain fallback that draws numbered options, so a harness must say which one it ran
    public Gatto.Cli.Setup.Tui.TuiWizardSurface TuiFace(params ConsoleKeyInfo[] keys)
    {
        _keys.AddRange(keys);
        return _tui = new Gatto.Cli.Setup.Tui.TuiWizardSurface(
            Surface, new Keys(this, [.. _keys]), T, "0.5.0", "1a2b3c4",
            clock: isWatchPoll => new RigClock(this, isWatchPoll), glyphs: glyphs,
            pulse: Pulse is { } beat ? () => beat : null,
            nowMs: PollTime ? () => Interlocked.Read(ref _pollNow) : null);
    }

    //the pulse the next TuiFace purrs with between screens, none by default so no frame changes off the clock
    public FakePulse? Pulse { get; set; }

    //the next face's clock moves only by the polls that took no key. a chord's window is then counted in polls, and load cannot stretch it
    public bool PollTime { get; set; }
    private long _pollNow;

    //a poll that took no key spent its whole budget
    private void Spent(TimeSpan budget) => Interlocked.Add(ref _pollNow, (long)budget.TotalMilliseconds);

    //the clock never reports a key and never sleeps, so the run stays deterministic, and only its snapshots capture a watch screen's frames
    private sealed class RigClock(WizardRig rig, bool isWatchPoll) : IPollClock
    {
        private int _ticks;

        //200 polls max, the exception names the frame. a missing file would spin forever, and a hang names no screen
        private const int Budget = 200;

        //then real time up to a ceiling, since a watch can wait on work another thread does and a busy thread pool starts it late. the screen's clock keeps moving, a watch checks its thing on that clock
        private static readonly TimeSpan LateCeiling = TimeSpan.FromSeconds(30);
        private System.Diagnostics.Stopwatch? _late;

        public long ElapsedMs { get; private set; }

        public bool WaitForKey(TimeSpan budget)
        {
            ElapsedMs += (long)budget.TotalMilliseconds;
            rig.Snapshot();

            //the clock adds time and returns at once, so a task on another thread would never get to exist. yield, it costs nothing
            Thread.Yield();
            if (isWatchPoll)
            {
                //a watch poll only takes a key with a budget set, otherwise it would eat the whole script
                if (rig.WatchKeyBudget > 0 && rig.KeysPending > 0)
                {
                    rig.WatchKeyBudget--;
                    return true;
                }
            }
            //a pending key resolves a chord wait at once, since no background predicate races for it.
            else if (rig.KeysPending > 0) return true;

            if (++_ticks <= Budget)
            {
                rig.Spent(budget);
                return false;
            }
            _late ??= System.Diagnostics.Stopwatch.StartNew();
            if (_late.Elapsed < LateCeiling)
            {
                Thread.Sleep(10);
                rig.Spent(budget);
                return false;
            }
            throw new InvalidOperationException(
                $"the watch polled {Budget} times and nothing arrived. Either the fixture never puts "
                + "the file where the scan looks, or the screen is watching for something that "
                + "cannot happen. The last frame was:" + Environment.NewLine + rig.Frames[^1]);
        }
    }

    //the screen as a terminal shows it, escapes stripped. use for layout questions.
    public string Plain => TerminalReplay.Plain(Surface.Text);

    //the raw byte stream. replay drops escape runs, so questions about paint must read what was emitted.
    public string Painted => Surface.Text;

    //count how many times needle survives on the final screen. a count of what was written cannot see a collapse, so this reads Plain
    public int Occurrences(string needle)
    {
        int n = 0, at = 0;
        var text = Plain;
        while ((at = text.IndexOf(needle, at, StringComparison.Ordinal)) >= 0) { n++; at += needle.Length; }
        return n;
    }

    public static ConsoleKeyInfo Digit(char d) =>
        new(d, (ConsoleKey)((int)ConsoleKey.D0 + (d - '0')), false, false, false);

    //a printable character key, the typing branch reads only KeyChar so the ConsoleKey here doesn't matter. use Digit when a digit acts as a row number
    public static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.A, false, false, false);

    public static ConsoleKeyInfo Enter => new('\r', ConsoleKey.Enter, false, false, false);

    public static ConsoleKeyInfo Down => new('\0', ConsoleKey.DownArrow, false, false, false);

    //spell Esc with a NUL KeyChar, since every path that reads Escape reads key.Key. one spelling keeps a reader from checking whether two unused fields match
    public static ConsoleKeyInfo Esc => new('\0', ConsoleKey.Escape, false, false, false);

    //keep the real tab character, a focused text field reads KeyChar and a NUL would move the ring yet type nothing
    public static ConsoleKeyInfo Tab => new('\t', ConsoleKey.Tab, false, false, false);

    //how many scripted keys a watching screen may take, zero by default. a flag would let one poll read the whole script
    public int WatchKeyBudget { get; set; }

    //count the scripted keys that no reader has taken, and report zero before any face was built.
    public int KeysPending => _source?.Remaining ?? 0;

    //don't make a space with Ch, the watching footer matches key.Key and a space made that way presses nothing
    public static ConsoleKeyInfo Space => new(' ', ConsoleKey.Spacebar, false, false, false);

    //keep KeyAvailable false, a true here claims the user typed before the prompt and the widget drops those keys
    private sealed class Keys : IKeySource
    {
        private readonly WizardRig rig;
        private readonly Queue<ConsoleKeyInfo> _q;

        public Keys(WizardRig rig, params ConsoleKeyInfo[] keys)
        {
            this.rig = rig;
            _q = new Queue<ConsoleKeyInfo>(keys);
            rig._source = this;
        }

        public int Remaining => _q.Count;
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey()
        {
            //capture the frame before the key leaves the queue, so the frame shows what the user was looking at.
            rig.Snapshot();
            return _q.Count > 0 ? _q.Dequeue() : throw new InvalidOperationException("scripted too few keys");
        }
    }

    private sealed class Writer(RecordingSurface surface) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char value) => surface.Write(value.ToString());
        public override void Write(string? value) { if (value is not null) surface.Write(value); }
        public override void WriteLine(string? value) => surface.Write((value ?? "") + "\n");
        public override void WriteLine() => surface.Write("\n");
    }
}
