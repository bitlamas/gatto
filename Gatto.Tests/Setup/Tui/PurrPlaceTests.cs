using Gatto.Cli.Setup.Tui;
using Gatto.Repl;
using Gatto.Tests.Fakes;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the activity line sits at the right of the title row on both paths. the oracle is the lit screen, since the subject is a row's position
public class PurrPlaceTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private sealed class NoKeys : IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => throw new InvalidOperationException("no key expected");
    }

    private sealed class Pulse : IPurrPulse
    {
        private Action? _tick;
        public void Start(Action tick, TimeSpan after, TimeSpan every) => _tick = tick;
        public void Stop() => _tick = null;
        public void Dispose() => Stop();
        public void Fire() => _tick?.Invoke();
    }

    private const string Question = "Which model should gatto start with?";

    private static Screen Body() => new(
        [], Region.List, Question,
        [PaintedRow.Of("  a row"), PaintedRow.Of("  another")], null,
        [new FooterKey("Enter", "choose")]);

    private static (TuiWizardSurface Face, VtScreenSurface Screen, Pulse Beat) Purring()
    {
        var screen = new VtScreenSurface(100, 40);
        var pulse = new Pulse();
        var face = new TuiWizardSurface(screen, new NoKeys(), T, "0.5.0", "1a2b3c4", () => 0,
            pulse: () => pulse);

        face.Paint((_, _) => Body());
        face.Notes.WriteLine("probing the server");   //a note starts the pulse and never draws a line itself
        return (face, screen, pulse);
    }

    //the setup path puts the activity line where the watching screens put theirs, on the row with the question
    [Fact]
    public void THE_SETUP_ROADS_PURR_SHARES_THE_TITLE_ROW()
    {
        var (_, screen, beat) = Purring();

        beat.Fire();

        var title = Assert.Single(screen.Viewport,
            r => r.Contains(Question, StringComparison.Ordinal));
        Assert.Contains(Cats.Face(GlyphSet.Unicode), title, StringComparison.Ordinal);
    }

    //no row below the frame may show the face, since an assertion on the title alone passes when both places draw it
    [Fact]
    public void AND_NOTHING_PURRS_BELOW_THE_FRAME()
    {
        var (_, screen, beat) = Purring();

        beat.Fire();

        var rows = screen.Viewport.ToArray();
        var footer = Array.FindLastIndex(rows, r => r.Contains("Enter", StringComparison.Ordinal));
        Assert.True(footer >= 0, "the frame's footer is not on the screen");
        Assert.DoesNotContain(Cats.Face(GlyphSet.Unicode),
            string.Join("\n", rows.Skip(footer + 1)), StringComparison.Ordinal);
    }

    //the matcher must find the face where it really is, or its absence below the frame says nothing about the screen
    [Fact]
    public void THE_MATCHER_FINDS_THE_CAT_WHERE_IT_IS()
    {
        var (_, screen, beat) = Purring();

        beat.Fire();

        Assert.Contains(Cats.Face(GlyphSet.Unicode),
            string.Join("\n", screen.Viewport), StringComparison.Ordinal);
    }

    //the ASCII face is 9 cells and the Unicode one 8, so the stamp's column must not come from the face's width
    [Fact]
    public void THE_BUILD_STAMPS_COLUMN_DOES_NOT_DEPEND_ON_WHICH_FACE_THE_TITLE_WEARS()
    {
        const string Stamp = "build 1a2b3c4";

        static int StampColumn(GlyphSet g)
        {
            var screen = new VtScreenSurface(80, 40);
            var pulse = new Pulse();
            var face = new TuiWizardSurface(screen, new NoKeys(), T, "0.5.0", "1a2b3c4", () => 0,
                pulse: () => pulse, glyphs: g);
            face.Paint((_, _) => Body());
            face.Notes.WriteLine("probing the server");
            pulse.Fire();

            var title = Assert.Single(screen.Viewport,
                r => r.Contains(Question, StringComparison.Ordinal));
            Assert.Contains(Cats.Face(g), title, StringComparison.Ordinal);

            var head = Assert.Single(screen.Viewport, r => r.Contains(Stamp, StringComparison.Ordinal));
            return UnicodeWidth.Of(head[..head.IndexOf(Stamp, StringComparison.Ordinal)]);
        }

        var unicode = StampColumn(GlyphSet.Unicode);
        Assert.Equal(unicode, StampColumn(GlyphSet.Ascii));
        Assert.True(unicode > 0, "the stamp is at column 0, so the frame drew no head");
    }
}
