using System.Text.RegularExpressions;
using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the wizard owns the cursor on its screens and gives it back on every exit, while a session entry leaves it to the compositor
public class CursorTests
{
    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : Gatto.Terminal.IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(k);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("the face asked for a key the script does not have");
    }

    //a clock that always says a key is ready, since a test host has no console and the bounded waits would hang
    private sealed class Ready : Gatto.Repl.IPollClock
    {
        public bool WaitForKey(TimeSpan budget) => true;
        public long ElapsedMs => 0;
    }

    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);
    private static ConsoleKeyInfo Typed(char c) => new(c, ConsoleKey.A, false, false, false);
    private static readonly ConsoleKeyInfo Esc = new('\u001b', ConsoleKey.Escape, false, false, false);

    //assert the order of the hide and the show, since a run that showed first still holds both escapes
    [Fact]
    public void THE_TAKEOVER_HIDES_THE_CURSOR_AND_THE_RESTORE_SHOWS_IT()
    {
        var surface = new RecordingSurface { Width = 100 };
        var alt = new AltScreen(surface, title: null);

        WizardSession.Run(alt, () => 0, () => ["the closing frame"], new StringWriter(),
            registerHooks: false);

        var text = surface.Text;
        var enter = text.IndexOf(Ansi.AltScreenEnter, StringComparison.Ordinal);
        var hide = text.IndexOf(Ansi.HideCursor, StringComparison.Ordinal);
        var show = text.LastIndexOf(Ansi.ShowCursor, StringComparison.Ordinal);

        Assert.True(hide > enter, "the takeover did not hide the cursor");
        Assert.True(show > hide, "the cursor was never given back");
    }

    //the restore must run from a finally, since a crash is the way a user reaches it. a terminal left without a cursor looks hung
    [Fact]
    public void THE_CURSOR_COMES_BACK_WHEN_THE_WALK_THROWS()
    {
        var surface = new RecordingSurface { Width = 100 };
        var alt = new AltScreen(surface, title: null);

        Assert.Throws<InvalidOperationException>(() => WizardSession.Run(
            alt, () => throw new InvalidOperationException("boom"), () => ["the last frame"],
            new StringWriter(), registerHooks: false));

        var text = surface.Text;
        Assert.True(text.LastIndexOf(Ansi.ShowCursor, StringComparison.Ordinal)
                  > text.IndexOf(Ansi.HideCursor, StringComparison.Ordinal),
            "the walk threw and took the cursor with it");
    }

    //the hide is a parameter, since the compositor sets the cursor on every paint and hiding there would be a second writer
    [Fact]
    public void A_TAKEOVER_THAT_DID_NOT_ASK_TO_HIDE_THE_CURSOR_DOES_NOT()
    {
        var surface = new RecordingSurface { Width = 100 };

        new AltScreen(surface, title: null).Enter("a session");

        Assert.Contains(Ansi.AltScreenEnter, surface.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Ansi.HideCursor, surface.Text);
    }

    //paints a setup screen with the real face, driven by a script of keys.
    private static (RecordingSurface Surface, IReadOnlyList<string> Frame) Painted(
        WizardScreen.Choice screen, params ConsoleKeyInfo[] keys)
    {
        var surface = new RecordingSurface { Width = 100, Height = 40 };
        var face = new TuiWizardSurface(surface, new Keys(keys),
            new Theme(new TermCaps(true, true)), "0.5.0", "1a2b3c4", () => 0,
            clock: _ => new Ready());
        try { face.Choose(screen); }
        catch (InvalidOperationException) { } //the script ran out, and the last frame is what is wanted.
        return (surface, face.LastPainted);
    }

    private static WizardScreen.Choice DoorScreen() =>
        new("a-door", "Where is llama-server?",
            [new ChoiceOption("1", "browse for it")],
            Door: "type the path to llama-server.exe…")
        { Strip = [] };

    //reads the last Cup followed by ShowCursor from the bytes the face wrote, since recomputing the position would agree with the code under test
    private static (int Row, int Col)? LastShownAt(string text)
    {
        var m = Regex.Matches(text, "\u001b\\[(\\d+);(\\d+)H\u001b\\[\\?25h").LastOrDefault();
        return m is null ? null : (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
    }

    //the column comes from the caret glyph in the painted frame, since recomputing the offset would agree by construction
    [Fact]
    public void A_FOCUSED_DOOR_PUTS_THE_CURSOR_ON_THE_CELL_THE_CARET_IS_DRAWN_IN()
    {
        //the script tabs to the door, then types a character so it holds a draft, the state the cursor rule names
        var (surface, frame) = Painted(DoorScreen(), Key(ConsoleKey.Tab), Typed('q'));

        var caret = GlyphSet.Unicode.Bar;
        var row = frame.Select((r, i) => (Text: r, Index: i))
            .Where(r => r.Text.Contains(caret, StringComparison.Ordinal))
            .ToList();
        Assert.True(row.Count == 1,
            $"expected exactly one row drawing a caret, found {row.Count}:\n{string.Join("\n", frame)}");

        var drawn = row[0].Text.IndexOf(caret, StringComparison.Ordinal);
        var at = LastShownAt(surface.Text);

        Assert.True(at is not null, "the focused door never showed the cursor");
        Assert.Equal((row[0].Index + 1, drawn + 1), at!.Value);
    }

    //the same screen with focus elsewhere: an unfocused door draws no caret and shows no cursor, otherwise the first defect is only moved
    [Fact]
    public void AN_UNFOCUSED_DOOR_TAKES_NO_CURSOR()
    {
        var (surface, frame) = Painted(DoorScreen());

        Assert.DoesNotContain(frame, r => r.Contains(GlyphSet.Unicode.Bar, StringComparison.Ordinal));
        Assert.Null(LastShownAt(surface.Text));
        Assert.EndsWith(Ansi.HideCursor, surface.Text, StringComparison.Ordinal);
    }

    //a screen with no door must end its paint with the cursor hidden
    [Fact]
    public void A_SCREEN_WITH_NO_DOOR_HIDES_THE_CURSOR()
    {
        var (surface, _) = Painted(
            new WizardScreen.Choice("plain", "What can this machine run?",
                [new ChoiceOption("1", "carry on")]) { Strip = [] });

        Assert.EndsWith(Ansi.HideCursor, surface.Text, StringComparison.Ordinal);
        Assert.Null(LastShownAt(surface.Text));
    }

    //fitting drops the blank row above the door, so the caret row is counted from the bottom. this renders at a height that forces the fit
    [Fact]
    public void A_FITTED_FRAME_STILL_PUTS_THE_CURSOR_ON_THE_CARET()
    {
        var surface = new RecordingSurface { Width = 100, Height = 9 };
        var face = new TuiWizardSurface(surface, new Keys([Key(ConsoleKey.Tab), Typed('q')]),
            new Theme(new TermCaps(true, true)), "0.5.0", "1a2b3c4", () => 0,
            clock: _ => new Ready());
        try { face.Choose(DoorScreen()); }
        catch (InvalidOperationException) { }

        var frame = face.LastPainted;
        //the fit is proved against the same screen on a taller terminal, since counting surviving blanks would lie about the strip's own blank row
        var whole = Painted(DoorScreen(), Key(ConsoleKey.Tab), Typed('q')).Frame;
        Assert.True(frame.Count < whole.Count,
            $"the fit did not run: {frame.Count} rows against {whole.Count} on a tall terminal");
        Assert.True(frame.Count <= 9, $"the frame is {frame.Count} rows in 9");

        var caret = GlyphSet.Unicode.Bar;
        var index = frame.ToList().FindIndex(r => r.Contains(caret, StringComparison.Ordinal));
        Assert.True(index >= 0, $"the fitted frame lost its door:\n{string.Join("\n", frame)}");

        var drawn = frame[index].IndexOf(caret, StringComparison.Ordinal);
        Assert.Equal((index + 1, drawn + 1), LastShownAt(surface.Text));
    }

}
