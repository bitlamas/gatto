using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Tests.Fakes;
using Gatto.Core.Hardware;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the summary and the record are written from the same facts, so neither can move without the other. the frame diff waits for the walk that fetches its engine
public class EpilogueTests
{
    //128 GB shared with the graphics chip, so the classifier's arithmetic makes the model budget 88 GB, and the row is computed
    private static readonly HardwareSnapshot Unified = new(128L * 1024 * 1024 * 1024,
        33_982_058_496, GpuKind.Integrated, 96L * 1024 * 1024 * 1024);

    //the corpus's record as label to row. the facts start after the head's own blank row and end at the next blank row.
    private static Dictionary<string, string> DrawnRecord()
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var drawn = File.ReadAllLines(Path.Combine(Golden.Dir, "s9-epilogue-start-100.txt"));
        foreach (var line in drawn.Skip(Array.IndexOf(drawn, "") + 1))
        {
            if (line.Length == 0) break;
            rows[line.Split(' ')[0]] = line;
        }

        Assert.Equal(6, rows.Count);   //the drawn record has six facts, and this file reads all of them
        return rows;
    }

    private static (IReadOnlyList<WizardRow> Summary, IReadOnlyList<WizardRow> Record) BothRenders()
    {
        var (flow, _) = DoneRenderTests.DoneStep(consent: true, snapshot: Unified);
        var summary = Assert.IsType<WizardScreen.Choice>(flow.Emitted[^1]);
        Assert.Equal(SetupFlow.SummaryKey, summary.Key);
        return (summary.BodyRows!, flow.RecordRows());
    }

    private static IReadOnlyList<string> Labels(IEnumerable<WizardRow> rows) =>
        [.. rows.Select(r => r.Text.Split(' ')[0]).Where(l => l.Length > 0)];

    //the record adds the machine row and drops the model's folder line, and every other row is the same one moved
    [Fact]
    public void THE_RECORD_IS_THE_SUMMARY_PLUS_THE_MACHINE_MINUS_THE_FOLDER_LINE()
    {
        var (summary, record) = BothRenders();

        //the order is the corpus's, read off the drawn record rather than typed here
        Assert.Equal(["machine", "engine", "model", "check", "gatto", "config"], Labels(record));
        Assert.Equal([.. DrawnRecord().Keys], Labels(record));

        //the set: the summary leads with the model and hangs its folder under it
        Assert.Equal(["model", "engine", "check", "gatto", "config"], Labels(summary));
        Assert.Contains(summary, r => r.Text.StartsWith("  ", StringComparison.Ordinal)
            && r.Text.Contains(@"\weights\", StringComparison.Ordinal));
        Assert.DoesNotContain(record, r => r.Text.Contains(@"\weights\gemma-4-26B-A4B-it\",
            StringComparison.Ordinal) && !r.Text.Contains(".gguf", StringComparison.Ordinal));

        //the rows both renders hold must be the same text, so one fact worded twice fails
        foreach (var label in new[] { "model", "check", "gatto", "config" })
            Assert.Equal(
                summary.Single(r => r.Text.StartsWith(label + " ", StringComparison.Ordinal)).Text,
                record.Single(r => r.Text.StartsWith(label + " ", StringComparison.Ordinal)).Text);
    }

    //the machine row is the record's own, so the corpus is its only oracle, and every figure in it is computed from the snapshot
    [Fact]
    public void THE_MACHINE_ROW_IS_THE_ONE_THE_CORPUS_DRAWS()
    {
        var (_, record) = BothRenders();
        Assert.Equal(DrawnRecord()["machine"], record[0].Text);
    }

    //each shape draws its own sentence, and a null GraphicsMemoryBytes is the unreadable machine no fixture here has
    [Theory]
    [InlineData("noshare", 34359738368L, 33155784704L, 1073741824L)]
    [InlineData("discrete", 34359738368L, 34281705472L, 8573157376L)]
    [InlineData("cpu-only", 34359738368L, 34281705472L, null)]
    public void EACH_MACHINE_SHAPE_DRAWS_ITS_OWN_ROW(string frame, long installed, long visible,
        long? vram)
    {
        var kind = vram is null ? GpuKind.None : frame == "discrete" ? GpuKind.Discrete : GpuKind.Integrated;
        var (flow, _) = DoneRenderTests.DoneStep(consent: true,
            snapshot: new HardwareSnapshot((ulong)installed, (ulong)visible, kind, (ulong?)vram));

        var drawn = File.ReadAllLines(Path.Combine(Golden.Dir, $"s9-epilogue-{frame}-100.txt"))
            .Single(l => l.StartsWith("machine ", StringComparison.Ordinal));

        Assert.Equal(drawn, flow.RecordRows()[0].Text);
    }

    //three renders and three waits, and the figures are read as a sequence, so 3-3-3 must fail
    [Fact]
    public void THE_COUNTDOWN_COUNTS_DOWN_AND_WAITS_BETWEEN_EVERY_STEP()
    {
        List<string> drawn = [];
        List<TimeSpan> waited = [];

        Countdown.Run(drawn.Add, waited.Add, glyphs: GlyphSet.Unicode);

        Assert.Equal(
            ["starting gatto in 3s\u2026", "starting gatto in 2s\u2026", "starting gatto in 1s\u2026"],
            drawn);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)],
            waited);
    }

    //three seconds, pinned here as the only literal, so a change to the constant has to come and change this test
    [Fact]
    public void THE_COUNTDOWN_IS_THREE_SECONDS()
    {
        Assert.Equal(3, Countdown.Seconds);
    }

    //the countdown's first line must equal the corpus's, since the generator is the oracle for a rendered line
    [Fact]
    public void THE_COUNTDOWNS_FIRST_LINE_IS_THE_ONE_THE_CORPUS_DRAWS()
    {
        var drawn = File.ReadAllLines(Path.Combine(Golden.Dir, "s9-epilogue-start-100.txt"))[^1];

        List<string> lines = [];
        Countdown.Run(lines.Add, _ => { }, glyphs: GlyphSet.Unicode);

        Assert.Equal(drawn, lines[0]);
    }

    //the record is written to the original buffer after the alt screen is given back, or the terminal throws it away on restore
    [Fact]
    public void THE_RECORD_IS_WRITTEN_AFTER_THE_SCREEN_COMES_BACK()
    {
        var surface = new RecordingSurface { Width = 100 };
        var alt = new Gatto.Terminal.AltScreen(surface);

        Gatto.Cli.Setup.WizardSession.Run(
            alt, () => 0,
            () => [Epilogue.Head(Gatto.Terminal.GlyphSet.Unicode, "0.5.0", "1a2b3c4", new DateOnly(2026, 8, 25))],
            new StringWriter(surface.Output), registerHooks: false);

        var text = surface.Text;
        var left = text.IndexOf(Gatto.Terminal.Ansi.AltScreenExit, StringComparison.Ordinal);
        var record = text.IndexOf(GlyphSet.Unicode.Header + "  gatto setup", StringComparison.Ordinal);   //the face, since the terminal title written on entry also says gatto setup

        Assert.True(left >= 0, "the alt screen was never left");
        Assert.True(record >= 0, "the record was never written");
        Assert.True(left < record,
            "the record must be written to the original buffer, after the alt screen is given back");
    }

    //what is printed has one empty line before and after it, and nothing is printed at all when there are no rows
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void THE_PRINTED_ROWS_HAVE_ONE_EMPTY_LINE_BEFORE_AND_AFTER(bool anyRows)
    {
        var surface = new RecordingSurface { Width = 100 };
        var alt = new Gatto.Terminal.AltScreen(surface);
        var epilogue = new StringWriter();
        var notes = anyRows ? new[] { "starting the model" } : Array.Empty<string>();
        var frame = anyRows ? new[] { "# gatto setup", "  model     x.gguf" } : Array.Empty<string>();

        Gatto.Cli.Setup.WizardSession.Run(
            alt, () => 0, () => frame, epilogue, notes: () => notes, registerHooks: false);

        var nl = Environment.NewLine;
        var expected = anyRows ? nl + string.Join(nl, notes.Concat(frame)) + nl + nl : "";
        Assert.Equal(expected, epilogue.ToString());
    }

    //a frame ending in its own empty row would give two, so empty rows at the edges are dropped first
    [Fact]
    public void A_FRAME_THAT_ENDS_IN_AN_EMPTY_ROW_STILL_GETS_ONE_EMPTY_LINE_AFTER()
    {
        var surface = new RecordingSurface { Width = 100 };
        var alt = new Gatto.Terminal.AltScreen(surface);
        var epilogue = new StringWriter();
        //the shape of the leave frame: banner, blank, rule, sentence, rule, blank
        var frame = new[] { "  gatto setup", "", "───", "  Nothing added", "───", "" };

        Gatto.Cli.Setup.WizardSession.Run(alt, () => 0, () => frame, epilogue, registerHooks: false);

        var nl = Environment.NewLine;
        Assert.Equal(nl + string.Join(nl, frame[..^1]) + nl + nl, epilogue.ToString());
    }

    //the record path has the same shape, since Epilogue.Lines adds the closing row only when one is given and still ends empty
    [Fact]
    public void A_RECORD_WITH_NO_CLOSING_ROW_GETS_ONE_EMPTY_LINE_AFTER()
    {
        var surface = new RecordingSurface { Width = 100 };
        var alt = new Gatto.Terminal.AltScreen(surface);
        var epilogue = new StringWriter();
        var record = Epilogue.Lines([new WizardRow("model     x.gguf")], "0.5.0", "1a2b3c4",
            new DateOnly(2026, 9, 13), closing: null, width: 100, glyphs: GlyphSet.Unicode);
        Assert.Equal("", record[^1]);   //the shape under test, pinned so a change in Epilogue.Lines shows up here

        Gatto.Cli.Setup.WizardSession.Run(alt, () => 0, () => record, epilogue, registerHooks: false);

        var nl = Environment.NewLine;
        Assert.Equal(nl + string.Join(nl, record.Take(record.Count - 1)) + nl + nl, epilogue.ToString());
    }

    //the record composed end to end against the corpus frame, every line except the live countdown, on the fetched-engine path so the figures match
    [Fact]
    public void THE_RECORD_IS_THE_FRAME_THE_CORPUS_DRAWS()
    {
        var (flow, _) = DoneRenderTests.DoneStep(consent: true, snapshot: Unified, fetchEngine: true);

        var lines = Epilogue.Lines(flow.RecordRows(), "0.5.0", "1a2b3c4",
            new DateOnly(2026, 8, 25), closing: null, width: 100, glyphs: GlyphSet.Unicode);

        var drawn = File.ReadAllLines(Path.Combine(Golden.Dir, "s9-epilogue-start-100.txt"));
        Assert.Equal(drawn[..^1], lines);
    }

    //a wizard run that proved nothing composes no record. the session then prints the leave block or the last frame, and the tests below pin which
    [Fact]
    public void A_WALK_THAT_REACHED_NO_SUMMARY_COMPOSES_NO_RECORD()
    {
        var flow = new SetupFlow(DoneRenderTests.Machine(consent: true));

        Assert.Empty(flow.RecordRows());
        Assert.Empty(Epilogue.Lines(flow.RecordRows(), "0.5.0", "1a2b3c4",
            new DateOnly(2026, 8, 25), closing: null, width: 100, glyphs: GlyphSet.Unicode));
    }

    private static readonly Gatto.Cli.VersionStamp Stamp = new("0.5.0", "1a2b3c4", Dev: false);

    //a wizard run the user left at its first screen, on the face that ships. the frame a fallback would print is then the real one.
    private static (SetupFlow Flow, TuiWizardSurface Face) LeftAtTheFirstScreen(bool modelRoad, string home)
    {
        var flow = new SetupFlow(new Gatto.Tests.Fakes.WizardProbes());
        var face = new WizardRig(100).TuiFace(WizardRig.Esc, WizardRig.Esc);
        SetupRunner.Run(flow, face, home, modelSegmentOnly: modelRoad);
        Assert.True(flow.Left, "the walk did not leave, so this fixture tests nothing about a leave");
        return (flow, face);
    }

    //a leave before any write prints the header and the leave sentence at column zero, and a fallback would print its rules
    [Theory]
    [InlineData(false, "gatto setup", "Nothing has been written, run gatto setup again whenever you're ready.")]
    [InlineData(true, "gatto model", "nothing added · gatto model whenever you're ready")]
    [InlineData(true, "gatto model new", "nothing added · gatto model whenever you're ready")]
    public void THE_LEAVE_FRAME_HAS_NO_RULES(bool modelRoad, string command, string sentence)
    {
        var home = Directory.CreateTempSubdirectory("gatto-leave-frame-").FullName;
        try
        {
            var (flow, face) = LeftAtTheFirstScreen(modelRoad, home);
            Assert.Contains(face.LastPainted, row => row.Contains('─'));   //the painted frame has the rules a fallback would have printed

            var lines = WizardSession.Scrollback(flow, () => face.LastPainted, 100, GlyphSet.Unicode, Stamp,
                new DateOnly(2026, 8, 25), command, theme: null);

            Assert.Equal([$"(^· ·^)Ⳋ  {command} · v0.5.0 · build 1a2b3c4", "", sentence], lines);
        }
        finally { try { Directory.Delete(home, true); } catch (IOException) { } }
    }

    //the leave block's head is the painted command header, and its command takes the command ink
    [Fact]
    public void THE_LEAVE_FRAME_PAINTS_ITS_HEAD_AND_ITS_COMMAND()
    {
        var home = Directory.CreateTempSubdirectory("gatto-leave-ink-").FullName;
        try
        {
            var theme = new Theme(new TermCaps(true, true));
            var (flow, face) = LeftAtTheFirstScreen(modelRoad: true, home);
            var lines = WizardSession.Scrollback(flow, () => face.LastPainted, 100, GlyphSet.Unicode, Stamp,
                new DateOnly(2026, 8, 25), "gatto model", theme);

            var header = new StringWriter();
            Gatto.Cli.CommandBanner.WriteHeader(header, theme, "model", GlyphSet.Unicode, Stamp);
            Assert.Equal(header.ToString().Split(Environment.NewLine)[1], lines[0]);
            Assert.Contains(theme.Paint("gatto model", Theme.RoleCoder), lines[2], StringComparison.Ordinal);
            Assert.Equal("nothing added · gatto model whenever you're ready", TermText.StripAnsiForWidth(lines[2]));
        }
        finally { try { Directory.Delete(home, true); } catch (IOException) { } }
    }

    //a development image says so in the head on both the leave and the record, and the two stamps must differ
    [Fact]
    public void A_DEV_IMAGE_SAYS_SO_IN_BOTH_HEADS()
    {
        var dev = Stamp with { Dev = true };
        string HeaderOf(Gatto.Cli.VersionStamp stamp)
        {
            var w = new StringWriter();
            Gatto.Cli.CommandBanner.WriteHeader(w, null, "setup", GlyphSet.Unicode, stamp);
            return w.ToString().Split(Environment.NewLine)[1];
        }
        Assert.NotEqual(HeaderOf(Stamp), HeaderOf(dev));

        var home = Directory.CreateTempSubdirectory("gatto-stamp-head-").FullName;
        try
        {
            var (left, face) = LeftAtTheFirstScreen(modelRoad: false, home);
            Assert.Equal(HeaderOf(dev), WizardSession.Scrollback(left, () => face.LastPainted, 100,
                GlyphSet.Unicode, dev, new DateOnly(2026, 8, 25), "gatto setup", theme: null)[0]);
        }
        finally { try { Directory.Delete(home, true); } catch (IOException) { } }

        var (done, _) = DoneRenderTests.DoneStep(consent: true, snapshot: Unified);
        Assert.Equal(HeaderOf(dev) + " · 2026-08-25", WizardSession.Scrollback(done, () => [], 100,
            GlyphSet.Unicode, dev, new DateOnly(2026, 8, 25), "gatto setup", theme: null)[0]);
    }

    //a wizard run that threw prints the last frame it drew, since a composed block would describe a run that ended tidily
    [Fact]
    public void A_WALK_THAT_THREW_KEEPS_ITS_LAST_FRAME()
    {
        var flow = new SetupFlow(DoneRenderTests.Machine(consent: true));
        flow.StartPastEngine();
        flow.Answer("0");
        flow.Answer(SetupFlow.ModelFetchNow);
        flow.Answer(SetupFlow.Landed);
        flow.Answer(SetupFlow.ModelArrivedNext);
        while (flow.NeedsWritesApplied) flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        IReadOnlyList<string> painted = ["the frame on screen when the walk threw"];

        Assert.Same(painted, WizardSession.Scrollback(flow, () => painted, 100, GlyphSet.Unicode, Stamp,
            new DateOnly(2026, 8, 25), "gatto setup", theme: null));
    }

    //gatto model closes on its header and one line naming what the user did with the check, never the frame with its rules
    [Theory]
    [InlineData("passed", "gemma-4-26B-A4B-it added and checked · run gatto, then /model gemma-4-26B-A4B-it to use it")]
    [InlineData("struggled", "gemma-4-26B-A4B-it added, it struggled on the check and you kept it · run gatto, then /model gemma-4-26B-A4B-it to use it")]
    [InlineData("stopped", "gemma-4-26B-A4B-it added, you stopped the check · run gatto, then /model gemma-4-26B-A4B-it to use it")]
    [InlineData("stopped-dead", "gemma-4-26B-A4B-it added, but its server didn't start · gatto doctor checks everything and says what to fix")]
    [InlineData("dead", "gemma-4-26B-A4B-it added, but its server didn't start · gatto doctor checks everything and says what to fix")]
    [InlineData("left", "gemma-4-26B-A4B-it added, you left before checking it · run gatto, then /model gemma-4-26B-A4B-it to use it")]
    public void GATTO_MODEL_CLOSES_ON_ONE_LINE_THAT_SAYS_WHAT_THE_USER_DID(string road, string sentence)
    {
        var dead = new Gatto.Core.Acquire.ProveOutcome(false, "no server", TimeSpan.Zero, StartFailed: true);
        var (flow, done) = DoneRenderTests.DoneStep(consent: true, inSession: true,
            block: road.StartsWith("stopped", StringComparison.Ordinal),
            audition: road == "struggled" ? new AuditionCheck(AuditionOutcome.Failed, null, new DateOnly(2026, 8, 24)) : null,
            prove: road.EndsWith("dead", StringComparison.Ordinal) ? dead : null,
            check: road switch
            {
                "passed" => DoneRenderTests.Passes,
                "struggled" => DoneRenderTests.Struggles,
                "stopped" => DoneRenderTests.Stops,
                "stopped-dead" => [.. DoneRenderTests.Stops, SetupFlow.Finish],
                "left" => [],
                _ => [.. DoneRenderTests.Passes, SetupFlow.Finish],
            });
        if (road == "left") flow.MarkLeaving();   //the runner's own call on Esc at the check offer
        else Assert.Equal("done", ScreenKey.Of(done));
        IReadOnlyList<string> painted = ["── the frame on screen when the walk finished ──"];

        //wide enough that the sentence is one row, the wrap is the leave block's and has its own tests
        var lines = WizardSession.Scrollback(flow, () => painted, 200, GlyphSet.Unicode, Stamp,
            new DateOnly(2026, 8, 25), "gatto model", theme: null);

        Assert.Equal(["(^· ·^)Ⳋ  gatto model · v0.5.0 · build 1a2b3c4", "", sentence], lines);
    }

    //a server that reported its window folds to its own spelling, and a note with no comma is the whole clause
    [Fact]
    public void THE_REPORTED_ARM_FOLDS_TO_ITS_OWN_SPELLING()
    {
        var facts = new Epilogue.Facts(
            null, null, null, null, "check", "gatto", "config",
            new Epilogue.ServerFact("http://127.0.0.1:1234", "a-model", "65,536",
                "reported by the server"));

        var record = Epilogue.Compose(facts, Epilogue.Face.Record, glyphs: GlyphSet.Unicode);

        Assert.StartsWith("server", record[0].Text, StringComparison.Ordinal);
        Assert.EndsWith("context 65,536 (reported by the server)", record[0].Text,
            StringComparison.Ordinal);
    }

    //the closing sentence is CompletionNextStep, computed from what the install did, so the frame is diffed whole
    [Theory]
    [InlineData("installed", SetupFlow.InstallYes)]
    [InlineData("declined", SetupFlow.InstallNo)]
    public void THE_RECORDS_CLOSING_SENTENCE_MATCHES_THE_INSTALL_DEED(string frame, string answer)
    {
        var (flow, _) = DoneRenderTests.DoneStep(consent: true, snapshot: Unified, fetchEngine: true,
            install: Gatto.Cli.InstallState.NotInstalled, installAnswer: answer);

        var lines = Epilogue.Lines(flow.RecordRows(), "0.5.0", "1a2b3c4",
            new DateOnly(2026, 8, 25), flow.CompletionNextStep, width: 100, glyphs: GlyphSet.Unicode);

        Assert.Equal(File.ReadAllLines(Path.Combine(Golden.Dir, $"s9-epilogue-{frame}-100.txt")),
            lines);
    }

    //the leave record is the usual rows plus the leave sentence and a head naming where the run stopped, diffed whole
    [Theory]
    [InlineData(100, "s9-epilogue-left-100.txt")]
    [InlineData(80, "s9-epilogue-left-80-80.txt")]
    public void THE_LEAVE_RECORD_MATCHES_ITS_DRAWN_FRAME(int width, string golden)
    {
        //check: [] stops the run on the offer, and consent: null leaves the update question unreached, which is what the frames draw
        var (flow, _) = DoneRenderTests.DoneStep(consent: null, snapshot: Unified, fetchEngine: true,
            install: Gatto.Cli.InstallState.NotInstalled, check: []);
        flow.MarkLeaving();

        var lines = Epilogue.Lines(flow.RecordRows(), "0.5.0", "1a2b3c4",
            new DateOnly(2026, 8, 25), flow.RecordClosing, width, GlyphSet.Unicode, flow.LeftAt);

        Assert.Equal(File.ReadAllLines(Path.Combine(Golden.Dir, golden)), lines);
    }

    //the corpus draws this width, and a row that overflows at the edge shows up at one width only
    [Fact]
    public void THE_RECORD_FOLDS_AT_EIGHTY_COLUMNS()
    {
        var (flow, _) = DoneRenderTests.DoneStep(consent: true, snapshot: Unified, fetchEngine: true,
            install: Gatto.Cli.InstallState.NotInstalled, installAnswer: SetupFlow.InstallYes);

        var lines = Epilogue.Lines(flow.RecordRows(), "0.5.0", "1a2b3c4",
            new DateOnly(2026, 8, 25), flow.CompletionNextStep, width: 80, glyphs: GlyphSet.Unicode);

        Assert.Equal(File.ReadAllLines(Path.Combine(Golden.Dir, "s9-epilogue-80-80.txt")), lines);
    }

    //config is the last row on both renders, the one row that never changes, and it is appended in a single place
    [Fact]
    public void CONFIG_IS_THE_LAST_ROW_ON_BOTH_RENDERS()
    {
        var (summary, record) = BothRenders();

        Assert.StartsWith("config ", summary[^1].Text, StringComparison.Ordinal);
        Assert.StartsWith("config ", record[^1].Text, StringComparison.Ordinal);
        Assert.StartsWith("gatto ", summary[^2].Text, StringComparison.Ordinal);
        Assert.StartsWith("gatto ", record[^2].Text, StringComparison.Ordinal);
    }
}
