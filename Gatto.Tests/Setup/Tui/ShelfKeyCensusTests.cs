using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//every letter the shelf names must act and one it does not name must do nothing, so press each and compare frames
public class ShelfKeyCensusTests
{
    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : Gatto.Terminal.IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(k);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0 ? _q.Dequeue()
            : throw new InvalidOperationException("out of keys");
    }

    private sealed class Ready : Gatto.Repl.IPollClock
    {
        public bool WaitForKey(TimeSpan budget) => true;
        public long ElapsedMs => 0;
    }

    private static ShelfRow Row(string id) => new(
        id, id.Split('/')[0], new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null),
        FitRegime.FitsGpu, 262144, false, null, 900, false, Params: 3_000_000_000);

    private static WizardScreen.Choice LiveShelf()
    {
        IReadOnlyList<ShelfRow> rows = [Row("unsloth/a"), Row("unsloth/b")];
        var flow = new SetupFlow(new WizardProbes
        {
            Rows = rows,
            Curated = "unsloth",
            Answer = _ => new HubSearchOutcome(rows, null, "unsloth", HiddenByFit: 12),
        });
        return Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine());
    }

    //report what the key did: an answer, a changed frame, or neither.
    private static (string? Answer, bool FrameMoved) Press(WizardScreen.Choice screen, char key)
    {
        static (string?, IReadOnlyList<string>) Run(WizardScreen.Choice s, ConsoleKeyInfo[] keys)
        {
            var face = new TuiWizardSurface(
                new RecordingSurface { Width = 100, Height = 40 },
                new Keys(keys), new Theme(new TermCaps(true, true)), "0.5.0", "1a2b3c4", () => 0,
                clock: _ => new Ready());
            string? answer = null;
            try { answer = face.Choose(s); }
            catch (InvalidOperationException) { } //the throw means the key script ran dry.
            return (answer, face.LastPainted);
        }

        //the baseline presses nothing, so the control frame is the screen as it arrived, since Esc would arm the leave warning and repaint
        var (idleAnswer, idle) = Run(screen, []);
        var (answer, after) = Run(screen, [new(key, ConsoleKey.A, false, false, false)]);

        Assert.Null(idleAnswer);
        return (answer, !idle.SequenceEqual(after));
    }

    //read the named keys from the footer the product composes, so the census cannot follow a copied list
    [Fact]
    public void EVERY_LETTER_THE_SHELF_NAMES_DOES_SOMETHING()
    {
        var screen = LiveShelf();
        var ring = Shelf.Regions(screen.Shelf!, 100, 0, screen.Door is not null);
        var footer = Shelf.Keys(new FocusRing([.. ring], Region.List), ShelfSource.Hub, "leave",
            searchKey: true, GlyphSet.Unicode, lift: true, back: true);

        //read the footer alone, since it is this face's advertisement and the strip belongs to the plain face
        var named = footer.Select(k => k.Key)
            .Where(k => k.Length == 1 && (char.IsLetter(k[0]) || k == "/"))
            .Distinct()
            .ToList();

        Assert.NotEmpty(named);
        foreach (var key in named)
        {
            var (answer, moved) = Press(screen, key[0]);
            Assert.True(answer is not null || moved,
                $"the shelf names '{key}' and pressing it changed nothing, and a key a screen names must do what it says");
        }
    }

    //an unbound letter must leave the screen exactly as it was, otherwise a face reacting to every key passes the check above
    [Theory]
    [InlineData('z')]
    [InlineData('q')]
    public void A_LETTER_THE_SHELF_DOES_NOT_NAME_DOES_NOTHING(char key)
    {
        var (answer, moved) = Press(LiveShelf(), key);

        Assert.Null(answer);
        Assert.False(moved, $"'{key}' is bound to nothing and moved the screen");
    }

    //every key the strip names must produce an answer the flow accepts, since a rejected answer drops the user out of the wizard
    [Fact]
    public void EVERY_KEY_THE_PLAIN_FACES_STRIP_NAMES_IS_ANSWERED_BY_THE_FLOW()
    {
        var screen = LiveShelf();
        var named = ShelfControls.For(screen.Shelf!).ToList();

        Assert.NotEmpty(named);
        Assert.Contains(named, c => c.Key == 'f');

        foreach (var control in named)
        {
            Assert.Equal(control.Answer, ShelfControls.AnswerFor(control.Key));

            IReadOnlyList<ShelfRow> rows = [Row("unsloth/a"), Row("unsloth/b")];
            var flow = new SetupFlow(new WizardProbes
            {
                Rows = rows,
                Curated = "unsloth",
                Answer = _ => new HubSearchOutcome(rows, null, "unsloth", HiddenByFit: 12),
            });
            flow.StartPastEngine();

            Assert.NotNull(flow.Answer(control.Answer));
        }
    }

    //a strip must name no key the answer map cannot serve, so a key added to the list and to nothing else fails here
    [Theory]
    [InlineData('z')]
    [InlineData('p')]
    public void A_KEY_THE_STRIP_DOES_NOT_NAME_MAPS_TO_NO_ANSWER(char key)
    {
        Assert.DoesNotContain(ShelfControls.All, c => c.Key == key);
        Assert.Null(ShelfControls.AnswerFor(key));
    }
}
