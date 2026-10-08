using Gatto.Cli.Setup;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//an ordinary wizard sentence is body text and dim is something you opt into. assert against the paint, an enum check passes while the widget paints it grey
public class WizardVoiceTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static string Dim(string s) => T.Paint(s, Theme.Dim);
    private static string Accent(string s) => T.Paint(s, Theme.Accent);

    //renders a wizard body through the real widget seam at a real width, nothing fakes the paint
    private static string Render(params WizardRow[] rows)
    {
        var surface = new RecordingSurface { Width = 80 };
        new SelectPrompt(surface, T, new ScriptedKeySource(Digit('1'))).Show(new SelectSpec(
            TitleRows: [],
            Question: new PromptQuestion("carry on?"),
            Options: [new SelectOption("yes")],
            BodyRows: WizardPaint.Body(rows)));
        return surface.Text;
    }

    private static ConsoleKeyInfo Digit(char d) =>
        new(d, (ConsoleKey)((int)ConsoleKey.D0 + (d - '0')), false, false, false);

    private sealed class ScriptedKeySource(params ConsoleKeyInfo[] keys) : Gatto.Terminal.IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() =>
            _q.Count > 0 ? _q.Dequeue() : throw new InvalidOperationException("scripted too few keys");
    }

    //the face's two sinks are one console in production, so both write here
    private sealed class SurfaceWriter(RecordingSurface s) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void Write(char v) => s.Write(v.ToString());
        public override void Write(string? v) { if (v is not null) s.Write(v); }
        public override void WriteLine(string? v) => s.Write((v ?? "") + "\n");
        public override void WriteLine() => s.Write("\n");
    }

    //enough of a world for the flow to reach its first few screens
    private sealed class FakeAllProbes : ISetupProbes
    {
        public Gatto.Core.Hardware.HardwareSnapshot? Hardware() => null;

        //display only, so the honest default is null
        public Gatto.Cli.Setup.HardwareNames HardwareNames() => default;
        public string? LlamaServerPath() => null;
        public bool HasResolvableModel() => true;
        public Gatto.Core.Acquire.ConnectProbe? ProbeServer(IReadOnlyList<int>? skip = null) => null;
        public string? GattoServingOn(int port) => null;

        public Gatto.Core.Acquire.ConnectProbe? ProbeAt(string baseUrl) => null;
        public IReadOnlyList<string> Roots { get; init; } = [];
        public ScanResult Scan(string? r) => new([], Roots);
        public (string Path, long Bytes)? ProjectorFor(string p) => null;
        public string? ArchitectureOf(string p) => null;
        public MoveOffer? MoveOfferFor(string p) => null;
        public (Gatto.Core.Models.FitRegime Alone, Gatto.Core.Models.FitRegime WithProjector)? PairFit(string p, long b) => null;
        public TypedIdOutcome EvaluateTypedId(string id) => new TypedIdOutcome.Unreachable(false);
        public AuditionCheck RunAudition(string p, string? repoId, IProgress<Gatto.Roles.Audition.AuditionProgress>? progress, CancellationToken ct) =>
            new(AuditionOutcome.Passed);
        public string? ExistingModel { get; init; }
        //the id another file already holds, null means no collision
        public string? Colliding { get; init; }
        public string? ExistingEndpoint { get; init; }
        public string? ExistingModelFor(string ggufPath) => ExistingModel;
        public (Gatto.Roles.IdClash Kind, string? Id) ClashFor(string ggufPath, string? incomingRepoId) =>
            Colliding is { Length: > 0 }
                ? (Gatto.Roles.IdClash.DifferentModel, Colliding)
                : (Gatto.Roles.IdClash.Free, null);
        //these fakes model an installed gatto, and RunningFrom is a fixed path so the screen renders a chosen state
        public string RunningFrom() => @"C:\Users\you\Downloads\gatto\gatto.exe";

        public (Gatto.Cli.InstallState State, string Dir) InstallStatus() =>
            (Gatto.Cli.InstallState.Installed, @"C:\Programs\gatto");

        public string? InstalledVersion() => null;


        public string RunningVersion() => Gatto.Core.GattoVersion.String;
        //the update question is already answered, so these tests never raise it
        public bool? UpdateConsent() => UpdateAnswered;
        public bool? UpdateAnswered { get; init; } = false;
        public string? ExistingEndpointFor(string baseUrl) => ExistingEndpoint;
        public Gatto.Roles.LlamaAsset? ChooseLlamaAsset() =>
            new("z.zip", null, "a graphics card", "Vulkan");
        public Gatto.Core.Tools.ProbeResult VerifyLlamaServer(string p) =>
            new(Gatto.Core.Tools.ProbeShape.ClassicServer, "d",
                //a ClassicServer probe must name its build, the real probe reads it from the same banner
                Build: Gatto.Roles.LlamaAssetSteering.PinnedRelease);
        public int ContextFor(string p) => 8192;
        public Gatto.Core.Acquire.ProveOutcome ProveIt(string? id,
            IProgress<Gatto.Roles.Audition.AuditionProgress>? progress = null) => new(true, "hi", TimeSpan.FromSeconds(1));
        public string ConfigPath() => @"C:\home\gatto.json";
    }

    [Fact]
    public void An_ordinary_wizard_sentence_is_NOT_painted_dim()
    {
        //the whole voice rule in one assertion, the default tone rendered
        var screen = Render(new WizardRow("It needs a server to load the models."));

        Assert.DoesNotContain(Dim("It needs a server to load the models."), screen, StringComparison.Ordinal);
        Assert.Contains("It needs a server to load the models.", screen, StringComparison.Ordinal);
    }

    [Fact]
    public void A_row_can_still_ASK_to_be_dim_because_the_default_flipping_is_not_the_same_as_dim_dying()
    {
        //the fallback row still has to be able to ask for dim, or it competes with the real link
        var screen = Render(new WizardRow("All builds (in case the one above doesn't work):", RowTone.Aside));

        Assert.Contains(Dim("All builds (in case the one above doesn't work):"), screen, StringComparison.Ordinal);
    }

    [Fact]
    public void A_HIGHLIGHT_on_a_white_row_paints_ACCENT_rather_than_silently_doing_nothing()
    {
        //a highlight on a white row must paint accent, it no longer stands out by contrast alone
        var screen = Render(new WizardRow(
            @"Extract it somewhere you will remember. C:\llama is a good choice.",
            Highlight: [@"C:\llama"]));

        Assert.Contains(Accent(@"C:\llama"), screen, StringComparison.Ordinal);
    }

    [Fact]
    public void TWO_spans_on_one_row_are_BOTH_accented()
    {
        //a row can highlight two spans, both paint accent so neither is picked over the other
        var screen = Render(new WizardRow(
            "According to your hardware, an AMD integrated adapter, you need the Vulkan build.",
            Highlight: ["an AMD integrated adapter", "Vulkan"]));

        Assert.Contains(Accent("an AMD integrated adapter"), screen, StringComparison.Ordinal);
        Assert.Contains(Accent("Vulkan"), screen, StringComparison.Ordinal);
    }

    //the row is printed straight to the writer, so assert on the rendered line, the command accented and no backticks
    [Fact]
    public void THE_NEXT_STEP_LINE_ACCENTS_THE_COMMAND_INSTEAD_OF_FENCING_IT()
    {
        var surface = new RecordingSurface { Width = 80 };
        var face = new SetupFace(surface, T, new ScriptedKeySource(), new SurfaceWriter(surface));

        //the fixture comes from Epilogue.LeaveSentence so the test checks the line the product draws
        var line = Gatto.Cli.Setup.Tui.Epilogue.LeaveSentence(
            Gatto.Cli.Setup.Tui.Epilogue.LeaveLead.NothingWritten, null, []);
        face.End(new WizardScreen.Terminal("left", [], line, Success: true));

        Assert.Contains(Accent("gatto setup"), surface.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("`", surface.Text, StringComparison.Ordinal);
        Assert.Contains("run gatto setup again whenever you're ready",
            TerminalReplay.Plain(surface.Text), StringComparison.Ordinal);
    }

    //the rich face's back row through the real widget

    [Fact]
    public void THE_RICH_FACE_MAPS_THE_APPENDED_BACK_ROW_TO_THE_BACK_KEY()
    {
        //the appended row sits one past the options, so drive the real SelectPrompt with the digit a user presses
        var surface = new RecordingSurface { Width = 80 };
        var keys = new ScriptedKeySource(Digit('3'));
        var face = new SetupFace(surface, T, keys, new SurfaceWriter(surface));
        var choice = new WizardScreen.Choice("k", "pick one",
            [new ChoiceOption("alpha", "The Alpha"), new ChoiceOption("beta", "The Beta")])
            { AllowBack = true };

        var answer = face.Choose(choice);

        Assert.Equal(SetupFlow.BackKey, answer);
        //assert on the committed went-back row, the back label is only on screen until the block is erased
        Assert.Contains("→ went back", TerminalReplay.Plain(surface.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void THE_RICH_FACE_STILL_RETURNS_THE_REAL_OPTION_when_back_is_offered_beside_it()
    {
        //the appended row must not shift the digits, so row 2 stays the second option
        var surface = new RecordingSurface { Width = 80 };
        var keys = new ScriptedKeySource(Digit('2'));
        var face = new SetupFace(surface, T, keys, new SurfaceWriter(surface));
        var choice = new WizardScreen.Choice("k", "pick one",
            [new ChoiceOption("alpha", "The Alpha"), new ChoiceOption("beta", "The Beta")])
            { AllowBack = true };

        Assert.Equal("beta", face.Choose(choice));
    }

    [Fact]
    public void THE_RICH_FACE_OFFERS_NO_BACK_ROW_WHEN_THE_SCREEN_DOES_NOT_ALLOW_IT()
    {
        var surface = new RecordingSurface { Width = 80 };
        var keys = new ScriptedKeySource(Digit('1'));
        var face = new SetupFace(surface, T, keys, new SurfaceWriter(surface));
        var choice = new WizardScreen.Choice("k", "pick one",
            [new ChoiceOption("alpha", "The Alpha"), new ChoiceOption("beta", "The Beta")]);

        Assert.Equal("alpha", face.Choose(choice));
        Assert.DoesNotContain(SetupFace.BackLabelOf(Gatto.Terminal.GlyphSet.Unicode), TerminalReplay.Plain(surface.Text), StringComparison.Ordinal);
    }

    [Fact]
    public void NO_WIZARD_COPY_ANYWHERE_FENCES_A_COMMAND_IN_BACKTICKS()
    {
        //a terminal has no markdown renderer, so sweep every string the flow can show
        var flow = new SetupFlow(new FakeAllProbes());
        var screen = flow.StartPastOpening();
        var seen = new List<string>();
        for (var step = 0; step < 6 && screen is not null; step++)
        {
            seen.AddRange(Text(screen));
            screen = screen switch
            {
                WizardScreen.Choice c when c.Options.Count > 0 => flow.Answer(c.Options[0].Key),
                _ => null,
            };
        }
        //sweep every LeaveLead, the leave line is composed so one constant would miss two
        foreach (var lead in Enum.GetValues<Gatto.Cli.Setup.Tui.Epilogue.LeaveLead>())
            seen.Add(Gatto.Cli.Setup.Tui.Epilogue.LeaveSentence(lead, "a-model",
                [Gatto.Cli.Setup.Tui.Epilogue.LeaveQuestion.Install]).Text);

        Assert.DoesNotContain(seen, line => line.Contains('`'));
    }

    private static IEnumerable<string> Text(WizardScreen s) => s switch
    {
        //the question is nullable, for a screen whose body already asked
        WizardScreen.Choice c => [c.Question ?? "", .. c.Options.Select(o => o.Label),
                                  .. (c.BodyRows ?? []).Select(r => r.Text)],
        WizardScreen.Ask a => [a.Label ?? "", a.Placeholder ?? "",
                               .. (a.BodyRows ?? []).Select(r => r.Text)],
        WizardScreen.Info i => i.Rows.Select(r => r.Text),
        //a null NextStep contributes no row, there is no sentence there to sweep
        WizardScreen.Terminal t =>
            [.. t.Rows.Select(r => r.Text), .. t.NextStep is { } n ? new[] { n.Text } : []],
        _ => [],
    };

    [Fact]
    public void A_FACE_PRINTED_ROW_WRAPS_INTO_THE_WIZARDS_GUTTER_not_the_terminals_edge()
    {
        //rows a face prints itself go through the same wrap, so the continuation keeps the two-space gutter
        var surface = new RecordingSurface { Width = 40 };
        var face = new SetupFace(surface, T, new ScriptedKeySource(), new SurfaceWriter(surface));

        face.Show(new WizardScreen.Info("oops",
            ["couldn't create the model: this sentence is comfortably wider than forty columns"]));

        var rows = TerminalReplay.ScreenAt(surface.Text, 40).Split('\n')
            .Where(r => r.Trim().Length > 0).ToList();

        Assert.True(rows.Count > 1, "the sentence must occupy more than one row at this width");
        //every row sits in the two-space gutter and none reaches the terminal edge
        Assert.All(rows, r => Assert.StartsWith("  ", r, StringComparison.Ordinal));
        Assert.All(rows, r => Assert.True(r.Length <= 40, $"row overflows the surface: '{r}'"));

        //nothing is lost to the wrapping
        Assert.Equal("couldn't create the model: this sentence is comfortably wider than forty columns",
            string.Join(" ", rows.Select(r => r.Trim())));
    }

    [Fact]
    public void A_highlight_that_is_not_IN_the_row_degrades_to_the_plain_row()
    {
        //the highlight is literal text, so a stale span degrades to a plain row instead of a torn escape
        var screen = Render(new WizardRow("no such span here", Highlight: ["C:\\nowhere"]));

        Assert.Contains("no such span here", screen, StringComparison.Ordinal);
    }

    //no-default choices reach the face

    //a no-default choice renders no cursor row, so press Esc and read the frame
    [Fact]
    public void A_NO_DEFAULT_CHOICE_RENDERS_NO_CURSOR_ON_ITS_FIRST_FRAME()
    {
        var surface = new RecordingSurface { Width = 80 };
        var face = new SetupFace(surface, T, new ScriptedKeySource(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false)),
            new SurfaceWriter(surface));

        face.Choose(new WizardScreen.Choice("k", "A newer gatto is running than the one installed",
            [new ChoiceOption("yes", "Update the installed copy"), new ChoiceOption("no", "Leave it")],
            NoDefault: true));

        Assert.DoesNotContain("❯", surface.Text, StringComparison.Ordinal);
    }

    //the same screen without the flag still highlights a row, or an empty frame would pass the guard above
    [Fact]
    public void THE_SAME_CHOICE_WITHOUT_THE_FLAG_STILL_HIGHLIGHTS_A_ROW()
    {
        var surface = new RecordingSurface { Width = 80 };
        var face = new SetupFace(surface, T, new ScriptedKeySource(new ConsoleKeyInfo('\0', ConsoleKey.Escape, false, false, false)),
            new SurfaceWriter(surface));

        face.Choose(new WizardScreen.Choice("k", "A newer gatto is running than the one installed",
            [new ChoiceOption("yes", "Update the installed copy"), new ChoiceOption("no", "Leave it")]));

        Assert.Contains("❯", surface.Text, StringComparison.Ordinal);
    }
}
