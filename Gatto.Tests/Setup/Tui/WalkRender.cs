using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//renders a whole walk through the real flow and face, and keeps every frame it painted so a test can assert about the sequence
internal static class WalkRender
{
    internal sealed record Captured(string Key, int Width, IReadOnlyList<string> Rows);

    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        //running dry is how a walk ends here, the caller wants the flow asking one more question than the script answers
        public ConsoleKeyInfo ReadKey() => _q.Count > 0 ? _q.Dequeue() : throw new WalkEnded();
    }

    internal sealed class WalkEnded : Exception;

    //the frame's own oracle, an already-built screen through the real face
    public static Captured Paint(Screen screen, int width, string key = "screen")
    {
        var surface = new RecordingSurface { Width = width };
        var face = new TuiWizardSurface(surface, new ScriptedKeys([]), new Theme(new TermCaps(true, true)),
            version: "0.5.0", build: "1a2b3c4", nowMs: () => 0);
        face.Paint((_, _) => screen);
        return new Captured(key, width, face.LastPainted);
    }

    //the script must resolve on its first key, or the captured frame is a later one. a numbered screen passes Enter, an Esc arms the leave chord and repaints

    //the frame the walk comes to rest on, through Choose so the binder stays the subject. script holds the keys to press before the run goes dry
    public static Captured SettledFrame(WizardScreen.Choice c, int width, string key = "choice",
        IEnumerable<ConsoleKeyInfo>? script = null, int height = 0)
    {
        var surface = new RecordingSurface { Width = width, Height = height };
        //nowMs is frozen at 0 here, so the clock has to be eager or the chord's wait never expires and the run hangs
        var face = new TuiWizardSurface(surface, new ScriptedKeys(script ?? []),
            new Theme(new TermCaps(true, true)), version: "0.5.0", build: "1a2b3c4", nowMs: () => 0,
            clock: _ => new AlwaysReady());
        try { face.Choose(c); }
        catch (WalkEnded) { }
        return new Captured(key, width, face.LastPainted);
    }

    //the settled frame's rows inked in a given theme, for a golden that pins colour as well as text
    public static IReadOnlyList<string> Inked(WizardScreen.Choice c, int width, int height, Theme theme,
        IEnumerable<ConsoleKeyInfo>? script = null)
    {
        var surface = new RecordingSurface { Width = width, Height = height };
        var face = new TuiWizardSurface(surface, new ScriptedKeys(script ?? []), theme, version: "0.5.0",
            build: "1a2b3c4", nowMs: () => 0, clock: _ => new AlwaysReady());
        try { face.Choose(c); }
        catch (WalkEnded) { }
        return face.LastInked;
    }

    //what a keystroke answers, beside the frame it left on screen. the script must resolve, a run that goes dry has no answer to report
    public static (string? Answer, IReadOnlyList<string> Rows) Answered(
        WizardScreen.Choice c, int width, IEnumerable<ConsoleKeyInfo> script)
    {
        var surface = new RecordingSurface { Width = width };
        var face = new TuiWizardSurface(surface, new ScriptedKeys(script),
            new Theme(new TermCaps(true, true)), version: "0.5.0", build: "1a2b3c4", nowMs: () => 0,
            clock: _ => new AlwaysReady());
        var answer = face.Choose(c);
        return (answer, face.LastPainted);
    }

    public static Captured Choice(WizardScreen.Choice c, int width, string key = "choice",
        IEnumerable<ConsoleKeyInfo>? script = null)
    {
        var surface = new RecordingSurface { Width = width };
        //the default script arms the chord, so this needs the same eager clock or the second Esc waits a chord out forever
        var face = new TuiWizardSurface(surface, new ScriptedKeys(script ?? [Esc, Esc]),
            new Theme(new TermCaps(true, true)), version: "0.5.0", build: "1a2b3c4", nowMs: () => 0,
            clock: _ => new AlwaysReady());
        face.Choose(c);
        return new Captured(key, width, face.LastPainted);
    }

    //a watching screen rendered at one moment of its work. with no script nothing is pressed, the interval expires and the watch resolves

    //the settled purr is the face's own figure. the clock answers 0 once and watchedMs thereafter, the difference is the wait
    public static Captured AfterWatch(
        WizardScreen.Choice watch, WizardScreen.Choice next, int width, long watchedMs)
    {
        var surface = new RecordingSurface { Width = width };
        var first = true;
        var face = new TuiWizardSurface(surface, new ScriptedKeys([WizardRig.Enter]),
            new Theme(new TermCaps(true, true)), version: "0.5.0", build: "1a2b3c4",
            nowMs: () => { if (first) { first = false; return 0; } return watchedMs; },
            clock: _ => new NeverReady());

        var polls = 0;
        face.Choose(watch, watch: () => ++polls > 1, tick: () => null);
        face.Choose(next);
        return new Captured("choice", width, face.LastPainted);
    }

    //the check parameter names the check's moment, null off an audition. the fullPurr parameter drives another purr frame, and null keeps index 0
    public static Captured Watching(WizardScreen.Choice c, int width, FetchTick? tick,
        string key = "choice", IEnumerable<ConsoleKeyInfo>? script = null, Func<long>? nowMs = null,
        CheckTick? check = null, Gatto.Repl.Render.PurrFrames? fullPurr = null)
    {
        var surface = new RecordingSurface { Width = width };
        var pressing = script is not null;
        var face = new TuiWizardSurface(surface, new ScriptedKeys(script ?? []),
            new Theme(new TermCaps(true, true)), version: "0.5.0", build: "1a2b3c4",
            nowMs: nowMs ?? (() => 0),
            clock: _ => pressing ? new AlwaysReady() : new NeverReady(),
            fullPurr: fullPurr);
        var polls = 0;
        face.Choose(c, watch: () => !pressing && ++polls > 1, tick: () => tick, check: () => check);
        return new Captured(key, width, face.LastPainted);
    }

    //presses the keys and then lets the watch resolve, so a key that was meant to be inert shows up as the wrong answer
    public static string? WatchAfterKeys(WizardScreen.Choice c, int width, FetchTick? tick,
        IEnumerable<ConsoleKeyInfo> keys)
    {
        var surface = new RecordingSurface { Width = width };
        var script = keys.ToList();
        var left = script.Count;
        var face = new TuiWizardSurface(surface, new ScriptedKeys(script),
            new Theme(new TermCaps(true, true)), version: "0.5.0", build: "1a2b3c4",
            nowMs: () => 0, clock: _ => new HandsOver(() => left > 0, () => left--));
        return face.Choose(c, watch: () => left == 0, tick: () => tick);
    }

    //a key is waiting only while the script has one left, and saying so spends it. the counter is the harness's, the face reads through its own copy
    private sealed class HandsOver(Func<bool> ready, Action spend) : IPollClock
    {
        public bool WaitForKey(TimeSpan budget)
        {
            if (!ready()) return false;
            spend();
            return true;
        }

        public long ElapsedMs => 0;
    }

    //the budget always expires, so only the watch can end the screen
    private sealed class NeverReady : IPollClock
    {
        public bool WaitForKey(TimeSpan budget) => false;
        public long ElapsedMs => 0;
    }

    //lets the interval expire before a key arrives, so the loop repaints with whatever tick says now. this is the harness for the motion itself
    public static Captured WatchingOverIntervals(WizardScreen.Choice c, int width,
        Func<FetchTick> tick, int expiries, string key = "choice")
    {
        var surface = new RecordingSurface { Width = width };
        var face = new TuiWizardSurface(surface, new ScriptedKeys([]),
            new Theme(new TermCaps(true, true)), version: "0.5.0", build: "1a2b3c4",
            nowMs: () => 0, clock: _ => new NeverReady());
        //no keys, and the frame is painted expiries plus one times because the loop paints before it asks the watch
        var polls = 0;
        face.Choose(c, watch: () => ++polls > expiries, tick: () => tick());
        return new Captured(key, width, face.LastPainted);
    }

    //the arrival with nothing pressed, returning what Choose answered for the runner to use
    public static string? WatchArrives(WizardScreen.Choice c, int width, FetchTick tick)
    {
        var surface = new RecordingSurface { Width = width };
        var face = new TuiWizardSurface(surface, new ScriptedKeys([]),
            new Theme(new TermCaps(true, true)), version: "0.5.0", build: "1a2b3c4",
            nowMs: () => 0, clock: _ => new AlwaysReady());
        //the empty script is deliberate, an arrival that did not answer throws instead of reading a key nobody supplied
        return face.Choose(c, watch: () => true, tick: () => tick);
    }

    //a key is always waiting, so the watch never spends an interval. the screen reads its elapsed off the tick, so ElapsedMs is zero and unused
    private sealed class AlwaysReady : IPollClock
    {
        public bool WaitForKey(TimeSpan budget) => true;
        public long ElapsedMs => 0;
    }

    //a screen as a user finds it, driven by no key at all. the face paints before its first ReadKey, and Esc on an ask with an offer arms the chord
    public static Captured Ask(WizardScreen.Ask a, int width, string key = "ask")
    {
        var surface = new RecordingSurface { Width = width };
        var face = new TuiWizardSurface(surface, new ScriptedKeys([]),
            new Theme(new TermCaps(true, true)), version: "0.5.0", build: "1a2b3c4", nowMs: () => 0);
        try { face.Ask(a); } catch (WalkEnded) { }
        return new Captured(key, width, face.LastPainted);
    }

    //what one key answers on a screen, through the real Choose so leaving and answering a row stay distinguishable
    public static string? Answer(WizardScreen.Choice c, ConsoleKey key) =>
        Answer(c, new ConsoleKeyInfo('\0', key, false, false, false));

    //the same question with a whole keystroke, since a ConsoleKey leaves KeyChar as nul and the row digits are read from KeyChar
    public static string? Answer(WizardScreen.Choice c, ConsoleKeyInfo key)
    {
        //nowMs is frozen at 0, so AlwaysReady keeps the single scripted key as immediate as a plain ReadKey
        var face = new TuiWizardSurface(new RecordingSurface { Width = 100 },
            new ScriptedKeys([key]),
            new Theme(new TermCaps(true, true)), version: "0.5.0", build: "1a2b3c4", nowMs: () => 0,
            clock: _ => new AlwaysReady());
        return face.Choose(c);
    }

    //what the face answers after a whole script, so a late key's answer can be read. the script must resolve, or Choose reads from an exhausted source
    public static string? AnswerAfter(WizardScreen.Choice c, int width, IEnumerable<ConsoleKeyInfo> script)
    {
        //an intermediate key may arm the chord before the resolving one, so this frozen nowMs needs the eager clock
        var face = new TuiWizardSurface(new RecordingSurface { Width = width },
            new ScriptedKeys(script),
            new Theme(new TermCaps(true, true)), version: "0.5.0", build: "1a2b3c4", nowMs: () => 0,
            clock: _ => new AlwaysReady());
        return face.Choose(c);
    }

    private static readonly ConsoleKeyInfo Esc = new('\0', ConsoleKey.Escape, false, false, false);

    //the same screen at every width from 78 to 120, no row over the width. a single width would miss the edge defect, which fires on the boundary
    public static void Sweep(Func<int, Screen> screenAt, int from = 78, int to = 120)
    {
        for (var w = from; w <= to; w++)
        {
            var cap = Paint(screenAt(w), w);
            foreach (var row in cap.Rows)
                Assert.True(Gatto.Terminal.UnicodeWidth.Of(row) <= w,
                    $"at width {w} a row is {Gatto.Terminal.UnicodeWidth.Of(row)} cells: {row}");
            Assert.DoesNotContain(cap.Rows, r => r.Contains('\n'));
        }
    }
}
