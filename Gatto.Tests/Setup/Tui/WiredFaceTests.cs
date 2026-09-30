using System.Reflection;
using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the four gaps that kept the TUI face out of gatto setup, each one measured against the shipped plain face
public class WiredFaceTests
{
    private const string Version = "0.5.0";
    private const string Build = "1a2b3c4";

    private sealed class Keys(ConsoleKeyInfo[] k) : IKeySource
    {
        private int _i;
        public bool KeyAvailable => _i < k.Length;
        public ConsoleKeyInfo ReadKey() =>
            _i < k.Length ? k[_i++] : throw new InvalidOperationException("the key source ran dry");
    }

    private static ConsoleKeyInfo Sp(ConsoleKey key) => new('\0', key, false, false, false);

    private static (RecordingSurface S, TuiWizardSurface F) Face(int width = 100, params ConsoleKeyInfo[] keys) =>
        Face(null, width, keys);

    private static (RecordingSurface S, TuiWizardSurface F) Face(
        Func<bool, IPollClock>? clock, int width = 100, params ConsoleKeyInfo[] keys)
    {
        var s = new RecordingSurface { Width = width };
        return (s, new TuiWizardSurface(s, new Keys(keys), new Theme(new TermCaps(true, true)),
            Version, Build, () => 0, clock));
    }

    //the wiring itself

    //source census over RunSetup's body, so it catches the plain face coming back and cannot say whether the wizard works
    [Fact]
    public void GATTO_SETUP_CONSTRUCTS_THE_TUI_FACE_ON_THE_ALT_SCREEN()
    {
        var source = Gatto.Tests.Census.SourceTree.Read(Path.Combine(
            Gatto.Tests.Census.SourceTree.RepoRoot(), "Gatto", "Cli", "GattoApp.cs"));
        var body = source[source.IndexOf("private static (int Code, bool Launch) RunSetup(", StringComparison.Ordinal)..];
        //stop at the first closing brace in column four, a longer slice would answer about GattoApp's next member
        body = body[..body.IndexOf("\n    }\n", StringComparison.Ordinal)];

        //one string only RunSetup says, so the slice is proven to be its body before the negative assertions
        Assert.Contains("gatto setup needs a terminal it can ask questions in", body, StringComparison.Ordinal);

        Assert.Contains("new Setup.Tui.TuiWizardSurface(", body, StringComparison.Ordinal);
        Assert.Contains("Setup.WizardSession.Run(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("new Setup.SetupFace(", body);

        //probes are handed the face's writer, the third argument after the glyph set. a console writer would put a server log on the painted frame
        Assert.Contains("new Setup.LiveSetupProbes(home, face.Glyphs, face.Notes", body,
            StringComparison.Ordinal);
        Assert.DoesNotContain("new Setup.LiveSetupProbes(home, face.Glyphs, Console.Out", body);

        //the session member composes what reaches the scrollback, so the face passes it the last frame and no console writer
        Assert.Contains("Setup.WizardSession.Scrollback(", body, StringComparison.Ordinal);
        Assert.DoesNotContain("home), () => face.LastPainted, Console.Out", body);

        //the countdown runs on the real clock between the record and the REPL, so the record is there before the REPL starts
        Assert.Contains("Setup.Tui.Countdown.Run(", body, StringComparison.Ordinal);
        Assert.Contains("System.Threading.Thread.Sleep", body, StringComparison.Ordinal);

        //the countdown repaints through cli.Live, so the body holds no raw console write, and the header prints the words the entry passed
        Assert.Contains("Setup.Tui.Countdown.Run(cli.Live,", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Console.Out.Write(", body);
        Assert.Contains("var wizardCommand = command ??", body, StringComparison.Ordinal);
    }

    //a bool cannot tell gatto model from gatto model new, so the entry passes the words and the header prints them
    [Fact]
    public void THE_MODEL_ENTRY_NAMES_THE_WORDS_TYPED()
    {
        Assert.Equal("gatto model", Gatto.Cli.GattoApp.ModelInvocation(null));
        Assert.Equal("gatto model new", Gatto.Cli.GattoApp.ModelInvocation("new"));

        var source = Gatto.Tests.Census.SourceTree.Read(Path.Combine(
            Gatto.Tests.Census.SourceTree.RepoRoot(), "Gatto", "Cli", "GattoApp.cs"));
        var body = source[source.IndexOf("private static Task<int> RunModelAsync(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    }\n", StringComparison.Ordinal)];
        Assert.Contains("gatto model takes no argument", body, StringComparison.Ordinal);   //the known match that shows the slice is this method's body
        Assert.Contains("RunSetup(modelSegmentOnly: true, command: ModelInvocation(args.Subcommand))", body,
            StringComparison.Ordinal);
    }

    //the footer is composed from the screen's own options, and the labels here match no other screen's
    [Fact]
    public void A_KEYS_ONLY_FOOTER_SAYS_WHAT_ITS_OWN_OPTIONS_SAY()
    {
        var (_, f) = Face(100, Sp(ConsoleKey.Enter));
        f.Choose(new WizardScreen.Choice("k", "Well?",
            [new("a", "carry on"), new("b", "stop here")], KeysOnly: true));

        Assert.Equal("  Enter carry on   Esc stop here", f.LastPainted[^1]);
    }

    //the escape key answers the second option whatever its label says, so no keys-only screen can part the key from the word
    [Fact]
    public void ESC_ANSWERS_THE_SECOND_OPTION_ON_ANY_KEYS_ONLY_SCREEN()
    {
        var (_, f) = Face(100, Sp(ConsoleKey.Escape));
        var answer = f.Choose(new WizardScreen.Choice("k", "Well?",
            [new("a", "carry on"), new("b", "stop here")], KeysOnly: true));

        Assert.Equal("b", answer);
    }

    //a keys-only screen has exactly two options, and a third is refused rather than left unreachable
    [Fact]
    public void A_KEYS_ONLY_SCREEN_WITH_A_THIRD_OPTION_IS_REFUSED()
    {
        var (_, f) = Face(100, Sp(ConsoleKey.Enter));
        var boom = Assert.Throws<InvalidOperationException>(() => f.Choose(
            new WizardScreen.Choice("k", "Well?",
                [new("a", "one"), new("b", "two"), new("c", "three")], KeysOnly: true)));

        Assert.Contains("exactly two options", boom.Message, StringComparison.Ordinal);
    }

    //the check is an equality, so one option and none are refused too, since a key there has nothing to answer
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_KEYS_ONLY_SCREEN_WITH_TOO_FEW_OPTIONS_IS_REFUSED(int count)
    {
        var (_, f) = Face(100, Sp(ConsoleKey.Enter));
        var options = Enumerable.Range(0, count).Select(i => new ChoiceOption($"k{i}", $"option {i}")).ToArray();

        var boom = Assert.Throws<InvalidOperationException>(() => f.Choose(
            new WizardScreen.Choice("k", "Well?", options, KeysOnly: true)));

        Assert.Contains("exactly two options", boom.Message, StringComparison.Ordinal);
    }

    //gap 1: the words

    //the screen's prose arrives in BodyRows, so a face that composes its body from the options alone drops it
    [Fact]
    public void A_CHOICE_SCREEN_RENDERS_ITS_PROSE_NOT_JUST_ITS_OPTIONS()
    {
        var (_, f) = Face(100, Sp(ConsoleKey.Enter));
        f.Choose(new WizardScreen.Choice("k", "Ready?",
            [new("yes", "Yes"), new("no", "No")],
            BodyRows: [new WizardRow("gatto is an AI assistant that lives in your terminal.")]));

        var screen = string.Join("\n", f.LastPainted);
        Assert.Contains("gatto is an AI assistant that lives in your terminal.", screen, StringComparison.Ordinal);
        Assert.Contains("1. Yes", screen, StringComparison.Ordinal);
    }

    //the wrap goes through WizardRows at the live width, so both faces break a long row in the same place
    [Fact]
    public void A_LONG_BODY_ROW_WRAPS_INSIDE_THE_FRAME()
    {
        var sentence = string.Join(" ", Enumerable.Repeat("word", 40));
        var (_, f) = Face(60, Sp(ConsoleKey.Enter));
        f.Choose(new WizardScreen.Choice("k", null, [new("ok", "Ok")], BodyRows: [new WizardRow(sentence)]));

        Assert.All(f.LastPainted, r => Assert.True(UnicodeWidth.Of(r) <= 60, $"row overflows 60: {r}"));
        //all 40 words are still on the screen, a truncated row would drop some
        Assert.Equal(40, string.Join(" ", f.LastPainted).Split("word").Length - 1);
    }

    //the typed ask has a body region too, and it draws above the validation complaint
    [Fact]
    public void AN_ASK_RENDERS_ITS_BODY_AND_ITS_COMPLAINT_TOGETHER()
    {
        var (_, f) = Face(100, Sp(ConsoleKey.Enter), Sp(ConsoleKey.Escape));
        f.Ask(new WizardScreen.Ask("k", "Where is it?", _ => "that is not a path",
            BodyRows: [new WizardRow("gatto looked in the usual places and found nothing.")]));

        var screen = string.Join("\n", f.LastPainted);
        Assert.Contains("gatto looked in the usual places", screen, StringComparison.Ordinal);
        Assert.Contains("that is not a path", screen, StringComparison.Ordinal);
    }

    //gap 2: the watch

    //a watching screen polls its predicate rather than the key source, which here is empty and throws if read
    [Fact]
    public void A_WATCHING_SCREEN_RESOLVES_ITSELF_WITH_NO_KEY_AT_ALL()
    {
        var (_, f) = Face(100);
        var answer = f.Choose(
            new WizardScreen.Choice("model.download", null, [new("again", "Look again")], Watching: true),
            watch: () => true);

        Assert.Equal(SetupFlow.Landed, answer);
    }

    //the wait goes through the injected clock, so no test sleeps and the poll count is observable
    [Fact]
    public void A_WATCH_THAT_IS_NOT_READY_YET_LOOKS_AGAIN_RATHER_THAN_BLOCKING()
    {
        var looks = 0;
        var waits = 0;
        var (_, f) = Face(_ => new CountingClock(() => waits++), 100);

        var answer = f.Choose(
            new WizardScreen.Choice("model.download", null, [new("again", "Look again")], Watching: true),
            watch: () => ++looks >= 3);

        Assert.Equal(SetupFlow.Landed, answer);
        Assert.Equal(3, looks);
        //the predicate is asked before the first wait, so a file already on disk costs the user no wait
        Assert.Equal(2, waits);
    }

    private sealed class CountingClock(Action onWait) : IPollClock
    {
        public long ElapsedMs => 0;
        public bool WaitForKey(TimeSpan budget) { onWait(); return false; }
    }

    //only a watching screen touches the clock, so an ordinary screen cannot pick up a poll loop
    [Fact]
    public void A_SCREEN_THAT_DOES_NOT_WATCH_NEVER_POLLS()
    {
        var waits = 0;
        var (_, f) = Face(_ => new CountingClock(() => waits++), 100, Sp(ConsoleKey.Enter));

        f.Choose(new WizardScreen.Choice("k", "Ready?", [new("yes", "Yes")]), watch: () => true);
        Assert.Equal(0, waits);
    }

    //gap 3: the version

    //version and build have no defaults, so a call site cannot print a literal that quietly stops matching the build
    [Fact]
    public void THE_VERSION_AND_BUILD_HAVE_NO_DEFAULTS_TO_FALL_BACK_ON()
    {
        var ctor = typeof(TuiWizardSurface).GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
        foreach (var name in new[] { "version", "build" })
        {
            var p = ctor.GetParameters().Single(x => x.Name == name);
            Assert.False(p.HasDefaultValue, $"`{name}` has a default again — a banner that prints a literal is a banner that can be wrong silently");
        }
    }

    //gap 5: the probes write into the face

    private static (RecordingSurface S, TuiWizardSurface F) ClockedFace(Func<long> now, params ConsoleKeyInfo[] keys)
        => ClockedFace(now, null, keys);

    //no pulse means no purr and never the real timer, which is what a test wants unless it is about the purr
    private static (RecordingSurface S, TuiWizardSurface F) ClockedFace(
        Func<long> now, FakePulse? pulse, params ConsoleKeyInfo[] keys)
    {
        var s = new RecordingSurface { Width = 100 };
        return (s, new TuiWizardSurface(s, new Keys(keys), new Theme(new TermCaps(true, true)),
            Version, Build, now, pulse: pulse is null ? null : () => pulse));
    }

    //the probe runs before the key is answered, which is when output would reach the painted frame
    private sealed class ProbingKeys(Action beforeAnswer, ConsoleKeyInfo answer) : IKeySource
    {
        private bool _ran;
        public bool KeyAvailable => true;
        public ConsoleKeyInfo ReadKey()
        {
            if (!_ran) { _ran = true; beforeAnswer(); }
            return answer;
        }
    }

    //forty lines of probe output leave the painted rows the same and the transcript forty lines longer
    [Fact]
    public void FORTY_LINES_OF_PROBE_OUTPUT_LEAVE_THE_FRAME_BYTE_IDENTICAL()
    {
        var s = new RecordingSurface { Width = 100 };
        TuiWizardSurface? face = null;
        var keys = new ProbingKeys(
            () => { for (var i = 0; i < 40; i++) face!.Notes.WriteLine($"llama-server: line {i}"); },
            Sp(ConsoleKey.Enter));
        //the keys need the face and the face needs the keys, so face is filled in once f exists
        var f = new TuiWizardSurface(s, keys, new Theme(new TermCaps(true, true)), Version, Build, () => 0);
        face = f;

        var screen = new WizardScreen.Choice("k", "Ready?", [new("yes", "Yes")],
            BodyRows: [new WizardRow("a sentence the probe must not scribble on.")]);
        //one option and no typed field is the shape that draws the blank before the footer rule, and the probe's words stay the subject

        f.Paint((_, _) => ScreenOf(screen));
        var frameBefore = f.LastPainted.ToArray();
        var screenBefore = s.Text;

        f.Choose(screen);

        //the frame model can stay identical while the screen grows, so both are asserted and the screen is the oracle
        Assert.Equal(frameBefore, f.LastPainted);
        Assert.DoesNotContain("llama-server:", s.Text);

        //the repaint before every key read makes the surface grow, so the test allows that and only rules out the probe's words
        Assert.StartsWith(screenBefore, s.Text, StringComparison.Ordinal);
        Assert.Equal(40, f.CapturedNotes.Count);
        Assert.Equal("llama-server: line 0", f.CapturedNotes[0]);
        Assert.Equal("llama-server: line 39", f.CapturedNotes[39]);
    }

    //the screen the face would paint for this choice, so the test can snapshot the frame before the probe and compare it after
    private static Screen ScreenOf(WizardScreen.Choice c) => new(
        c.Strip, Region.List, c.Question,
        [.. WizardRows.Plain(c.BodyRows, 100, glyphs: GlyphSet.Unicode), "",
         .. c.Options.Select((o, i) => (i == 0 ? "❯ " : "  ") + $"{i + 1}. {o.Label}"),
         //this helper mirrors the face row for row, so the comparison is about the probe rather than a pinned composition
         ""],
        //the footer of a one-option screen says Enter next, so the helper writes the same word
        null, [new("Enter", c.Options.Count == 1 ? "next" : "choose"), new("Esc", "leave")]);

    //the note arms the pulse rather than drawing the purr itself, so the test checks the armed delay of one second
    [Fact]
    public void A_PROBE_THAT_ANSWERS_INSIDE_A_SECOND_DRAWS_NO_PURR()
    {
        var now = 0L;
        var pulse = new FakePulse();
        var (s, f) = ClockedFace(() => now, pulse, Sp(ConsoleKey.Enter));
        f.Paint((_, _) => new Screen([], Region.List, "Ready?", ["  1. Yes"], null, []));
        var painted = s.Text.Length;

        f.Notes.WriteLine("starting");
        now = 900;
        f.Notes.WriteLine("still starting");

        Assert.Equal(painted, s.Text.Length);
        Assert.Equal(2, f.CapturedNotes.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), pulse.After);
    }

    //past a second the purr draws, and a second note keeps the pulse rather than restarting it, so a chattering probe still purrs
    [Fact]
    public void A_PROBE_STILL_RUNNING_PAST_A_SECOND_DRAWS_THE_PURR()
    {
        var now = 0L;
        var pulse = new FakePulse();
        var (s, f) = ClockedFace(() => now, pulse, Sp(ConsoleKey.Enter));
        f.Paint((_, _) => new Screen([], Region.List, "Ready?", ["  1. Yes"], null, []));
        var frame = f.LastPainted.ToArray();
        var painted = s.Text.Length;

        f.Notes.WriteLine("starting");
        now = 900;
        f.Notes.WriteLine("still starting");
        Assert.Equal(1, pulse.Starts);          //both notes share the one pulse, so the deadline does not move

        now = 1100;
        pulse.Fire();

        Assert.True(s.Text.Length > painted, "nothing was drawn past the one-second mark");
        Assert.Contains(Cats.Face(GlyphSet.Unicode), s.Text, StringComparison.Ordinal);
        //the purr republishes the frame, so LastPainted moves, in the title and keys rows only
        var after = f.LastPainted.ToArray();
        Assert.Equal(frame.Length, after.Length);
        var moved = frame.Zip(after).Count(p => !string.Equals(p.First, p.Second, StringComparison.Ordinal));
        //two rows move, the purr in the title row and looking… in the keys row, since the working state has two halves
        Assert.Equal(2, moved);
        Assert.Contains(Cats.Face(GlyphSet.Unicode),
            after[Array.FindIndex(after, r => r.Contains("Ready?", StringComparison.Ordinal))],
            StringComparison.Ordinal);
    }

    //the notes go through WriteLine(string), so the sink must override every overload that takes text. a missed one keeps the count and drops the words
    [Fact]
    public void EVERY_WRITER_OVERLOAD_CARRIES_ITS_CONTENT_TO_THE_TRANSCRIPT()
    {
        var (_, f) = ClockedFace(() => 0, Sp(ConsoleKey.Enter));

        f.Notes.Write("A");
        f.Notes.Write('B');
        f.Notes.Write('\n');
        f.Notes.WriteLine("C");
        f.Notes.Write("D\n");
        f.Notes.WriteLine("E".AsSpan());
        f.Notes.Write(new[] { 'F', '\n' }, 0, 2);

        Assert.Equal(["AB", "C", "D", "E", "F"], f.CapturedNotes);
    }

    //the wait clock resets on each paint, so a slow probe on one screen leaves the next screen's probe silent
    [Fact]
    public void A_SLOW_PROBE_ON_ONE_SCREEN_DOES_NOT_MAKE_THE_NEXT_SCREENS_PROBE_PURR()
    {
        var now = 0L;
        var pulse = new FakePulse();
        var (s, f) = ClockedFace(() => now, pulse, Sp(ConsoleKey.Enter));

        f.Paint((_, _) => new Screen([], Region.List, "First", ["  1. Yes"], null, []));
        f.Notes.WriteLine("starting");
        now = 1100;
        pulse.Fire();
        Assert.Contains(Cats.Face(GlyphSet.Unicode), s.Text, StringComparison.Ordinal);   //the cat face shows the first wait purred, so the silence after the next paint is the reset

        //the paint stops the old pulse, so the next note arms a fresh one. a late tick from the old wait writes nothing
        f.Paint((_, _) => new Screen([], Region.List, "Second", ["  1. Yes"], null, []));
        var afterPaint = s.Text.Length;
        pulse.Fire();
        Assert.Equal(afterPaint, s.Text.Length);

        now = 1150;
        f.Notes.WriteLine("instant");
        Assert.Equal(2, pulse.Starts);
        Assert.Equal(TimeSpan.FromMilliseconds(1000), pulse.After);
        Assert.Equal(afterPaint, s.Text.Length);
    }

    //a ticker repaints its line in place, so only the text that ends with a newline becomes a transcript line
    [Fact]
    public void A_TICKERS_OVERWRITTEN_FRAMES_DO_NOT_BECOME_TRANSCRIPT_LINES()
    {
        var (_, f) = ClockedFace(() => 0, Sp(ConsoleKey.Enter));

        f.Notes.Write("measuring .\r");
        f.Notes.Write("measuring ..\r");
        f.Notes.Write("measuring ...\r");
        f.Notes.WriteLine("measured: 47.7 tok/s");

        Assert.Equal(["measured: 47.7 tok/s"], f.CapturedNotes);
    }

    //the captured notes go above the closing frame after the restore, so a failed engine start still has the server's words
    [Fact]
    public void THE_CAPTURED_NOTES_REACH_THE_EPILOGUE_ABOVE_THE_CLOSING_FRAME()
    {
        var alt = new AltScreen(new RecordingSurface { Width = 100 }, title: null);
        var epilogue = new StringWriter();

        WizardSession.Run(alt, () => 0, () => ["the closing frame"], epilogue,
            notes: () => ["llama-server: could not bind port 1235"], registerHooks: false);

        var text = epilogue.ToString();
        Assert.Contains("could not bind port 1235", text, StringComparison.Ordinal);
        Assert.True(text.IndexOf("could not bind", StringComparison.Ordinal)
                  < text.IndexOf("the closing frame", StringComparison.Ordinal),
            "the probe's words must come above the summary, not after it");
    }

    //gap 4: the screen comes back

    //the alt screen is given back even when the session throws, so the restore guard covers the failure path too
    [Fact]
    public void THE_ALT_SCREEN_IS_RESTORED_EVEN_WHEN_THE_WALK_THROWS()
    {
        var surface = new RecordingSurface { Width = 100 };
        var alt = new AltScreen(surface, title: null);
        var epilogue = new StringWriter();

        Assert.Throws<InvalidOperationException>(() => WizardSession.Run(
            alt, () => throw new InvalidOperationException("boom"), () => ["the last frame"],
            epilogue, registerHooks: false));

        Assert.False(alt.Active, "the alt buffer was not given back");
        Assert.Contains("the last frame", epilogue.ToString(), StringComparison.Ordinal);
    }

    //the epilogue is read after the session returns, so the frame kept is the one the run ended with
    [Fact]
    public void THE_ALT_SCREEN_IS_RESTORED_ON_THE_ORDINARY_EXIT_AND_THE_LAST_FRAME_IS_KEPT()
    {
        var surface = new RecordingSurface { Width = 100 };
        var alt = new AltScreen(surface, title: null);
        var epilogue = new StringWriter();
        var frame = new List<string> { "before the walk" };

        var code = WizardSession.Run(
            alt, () => { frame[0] = "after the walk"; return 3; }, () => frame, epilogue, registerHooks: false);

        Assert.Equal(3, code);
        Assert.False(alt.Active);
        Assert.Contains("after the walk", epilogue.ToString(), StringComparison.Ordinal);
    }

    //the notes flush lives in the finally, so the server's words reach the epilogue on the throw path too
    [Fact]
    public void THE_CAPTURED_NOTES_REACH_THE_EPILOGUE_EVEN_WHEN_THE_WALK_THROWS()
    {
        var alt = new AltScreen(new RecordingSurface { Width = 100 }, title: null);
        var epilogue = new StringWriter();

        Assert.Throws<InvalidOperationException>(() => WizardSession.Run(
            alt, () => throw new InvalidOperationException("boom"),
            () => ["the closing frame"],
            epilogue,
            notes: () => ["llama-server: could not bind port 1235"],
            registerHooks: false));

        var text = epilogue.ToString();
        Assert.Contains("could not bind port 1235", text, StringComparison.Ordinal);

        //the throw keeps the order too, the server's words above the closing frame
        Assert.True(text.IndexOf("could not bind", StringComparison.Ordinal)
                  < text.IndexOf("the closing frame", StringComparison.Ordinal),
            "the probe's words must come above the summary on the throw path as well");
        Assert.False(alt.Active);
    }

    //the buffer must be entered before the session runs, or a test that never took the screen would still pass
    [Fact]
    public void THE_WALK_RUNS_WITH_THE_ALT_BUFFER_ALREADY_UP()
    {
        var surface = new RecordingSurface { Width = 100 };
        var alt = new AltScreen(surface, title: null);
        var upDuringWalk = false;

        WizardSession.Run(alt, () => { upDuringWalk = alt.Active; return 0; }, () => [], new StringWriter(),
            registerHooks: false);

        Assert.True(upDuringWalk, "the walk ran on the main screen");
        Assert.Contains(Ansi.AltScreenEnter, surface.Text, StringComparison.Ordinal);
        Assert.Contains(Ansi.AltScreenExit, surface.Text, StringComparison.Ordinal);
    }
}
