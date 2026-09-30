using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the hint names only the keys that work, and the arrows are one. every guard here presses the key, since a render cannot say whether a handler exists.
public class DoorReachTests
{
    private static readonly ConsoleKeyInfo Up = new('\0', ConsoleKey.UpArrow, false, false, false);
    private static readonly ConsoleKeyInfo Tab = new('\t', ConsoleKey.Tab, false, false, false);

    private static IEnumerable<ConsoleKeyInfo> Type(string s) => s.Select(WizardRig.Ch);

    //the connect road's door screen, driven through the flow rather than composed
    private static WizardScreen.Choice Road()
    {
        var flow = new SetupFlow(new WizardProbes { Llama = @"C:\llama\llama-server.exe" });
        flow.StartPastOpening();
        var screen = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.ForkConnect));
        Assert.NotNull(screen.Door);
        return screen;
    }

    //down from the last option enters the door. assert the answer rather than the caret, since a render check would pass on a tree that dropped the text.
    [Fact]
    public void DOWN_FROM_THE_LAST_OPTION_ENTERS_THE_DOOR()
    {
        var screen = Road();
        var downs = Enumerable.Repeat(WizardRig.Down, screen.Options.Count);

        var answer = WalkRender.AnswerAfter(screen, 100,
            [.. downs, .. Type("http://127.0.0.1:9999"), WizardRig.Enter]);

        Assert.Equal(ShelfControls.TypedAnswer("http://127.0.0.1:9999"), answer);
    }

    //up brings the keys back, or the door is one-way. assert that Enter answers an option, which is only true if the keys left the door.
    [Fact]
    public void UP_FROM_THE_DOOR_RETURNS_TO_THE_OPTIONS()
    {
        var screen = Road();
        var downs = Enumerable.Repeat(WizardRig.Down, screen.Options.Count);

        var answer = WalkRender.AnswerAfter(screen, 100,
            [.. downs, .. Type("typed-then-abandoned"), Up, WizardRig.Enter]);

        Assert.Contains(answer, screen.Options.Select(o => o.Key));
    }

    //tab still reaches the door, since the arrow is an addition and the old way must keep working
    [Fact]
    public void TAB_STILL_REACHES_THE_DOOR()
    {
        var answer = WalkRender.AnswerAfter(Road(), 100,
            [Tab, .. Type("http://elsewhere:1234"), WizardRig.Enter]);

        Assert.Equal(ShelfControls.TypedAnswer("http://elsewhere:1234"), answer);
    }

    //the hint names Tab and the arrow and nothing else, since the mouse is REPL-only and click was a promise nothing kept
    [Fact]
    public void THE_HINT_NAMES_TAB_AND_THE_ARROW_AND_NOT_THE_MOUSE()
    {
        Assert.Equal("Tab or \u2193 to type", DoorHints.AnywhereOf(GlyphSet.Unicode));
        Assert.Equal("Tab or Down to type", DoorHints.AnywhereOf(GlyphSet.Ascii));
    }

    //a key reads as a word under ASCII, a token keeps its glyph in both sets. both halves are asserted here, since one member could pass either alone.
    [Fact]
    public void THE_KEY_SENSE_IS_WORDS_UNDER_ASCII_AND_THE_TOKEN_SENSE_IS_NOT()
    {
        Assert.Equal("Down", GlyphSet.Ascii.DownKey);
        Assert.Equal("Up", GlyphSet.Ascii.UpKey);
        Assert.Equal("arrows", GlyphSet.Ascii.ArrowsKey);

        //the token sense, the v 53 on the status line is a transliteration and stays one
        Assert.Equal("v", GlyphSet.Ascii.Down);
        Assert.Equal("^", GlyphSet.Ascii.Up);

        Assert.Equal("\u2193", GlyphSet.Unicode.DownKey);
        Assert.Equal("\u2191\u2193", GlyphSet.Unicode.ArrowsKey);
    }

    //no key position composes from the token glyphs. the matcher is tried on a known match and a known miss first, since the pattern stays narrow.
    [Fact]
    public void NO_KEY_HINT_COMPOSES_FROM_THE_TOKEN_GLYPHS()
    {
        static IReadOnlyList<string> Offenders(string name, string src) =>
            [.. System.Text.RegularExpressions.Regex
                .Matches(src, @"\{g\.(?:Up|Down)\}\{g\.(?:Up|Down)\}|\{g\.(?:Up|Down)\} (?:move|choose|next|history|to type)")
                .Select(m => $"{name}: {m.Value}")];

        Assert.NotEmpty(Offenders("planted", "\"{g.Up}{g.Down} move\""));
        Assert.NotEmpty(Offenders("planted", "$\"{g.Up} history\""));
        Assert.Empty(Offenders("planted", "$\"{g.Dot} {g.Down} \" + K(down)"));   //the token sense

        var found = new List<string>();
        foreach (var f in Gatto.Tests.Census.SourceTree.ProductionFiles())
            found.AddRange(Offenders(Path.GetFileName(f), File.ReadAllText(f)));

        Assert.Empty(found);
    }

    //no hint may name a key nothing binds, so the mouse promise is checked on the hints and on the face source. the matcher is tried on a known match first.
    [Fact]
    public void NO_DOOR_HINT_PROMISES_A_MOUSE_THE_WIZARD_FACE_DOES_NOT_HAVE()
    {
        static bool Promises(string hint) =>
            hint.Contains("click", StringComparison.OrdinalIgnoreCase);

        Assert.True(Promises("Tab, click or ↓ to type"), "the matcher cannot see a mouse promise");
        Assert.False(Promises("Tab or ↓ to type"), "the matcher sees one where there is none");

        var face = File.ReadAllText(Path.Combine(Gatto.Tests.Census.SourceTree.RepoRoot(),
            "Gatto", "Cli", "Setup", "Tui", "TuiWizardSurface.cs"));
        Assert.DoesNotContain("MouseController", face, StringComparison.Ordinal);

        foreach (var hint in new[]
                 {
                     DoorHints.AnywhereOf(GlyphSet.Unicode), DoorHints.AnywhereOf(GlyphSet.Ascii),
                     DoorHints.Search, DoorHints.SearchLocal,
                 })
            Assert.False(Promises(hint),
                $"the hint \"{hint}\" names a mouse the wizard face has no controller for");
    }
}
