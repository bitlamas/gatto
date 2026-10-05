//the loop pieces are internal statics, so these tests can drive them without the private loop method
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class ReplLinearLoopTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static StatusInfo Status() =>
        new(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");

    private static (RecordingSurface Surface, ChromePainter Painter, ChromeTicker Ticker,
        StreamRenderer Renderer, object Gate) Wire()
    {
        var surface = new RecordingSurface { Width = 80, Height = 30 };
        var gate = new object();
        var painter = new ChromePainter(surface, T, gate) { RoleForTint = "coder" };
        painter.Frame = new InputFrame(surface, T, "coder", Status(), glyphs: GlyphSet.Unicode);
        var ticker = new ChromeTicker(painter, gate);
        var renderer = new StreamRenderer(painter, surface, T, "coder", ticker, gate,
            model: painter.Model, convoTail: () => null);
        return (surface, painter, ticker, renderer, gate);
    }

    private static string Visible(string s) => Gatto.Terminal.TermText.StripAnsiForWidth(s);

    //sample the purr during the body, cleanup hides it after, and drive the loop from the submission
    [Theory]
    [InlineData("/model", null)]                       //a tui command runs without a turn or cat.
    [InlineData("/model qwen3.6-35b-a3b", null)]
    [InlineData("/help", null)]
    [InlineData("hello there", "(=^･ω･^=)")]           //plain text gets a purring turn.
    [InlineData("/compact", "(=^･ω･^=)")]              //commands that reach the model purr as well.
    [InlineData("/init", "(=^･ω･^=)")]
    [InlineData("/remember something worth keeping", "(=^･ω･^=)")]
    public async Task DispatchBracket_PurrsOnlyForSubmissionsThatReachTheModel(
        string submission, string? expectedFace)
    {
        var (_, painter, ticker, renderer, gate) = Wire();
        string? purrDuringBody = "not sampled";

        await Gatto.Repl.Repl.DispatchBracketAsync(
            gate, painter, ticker, () => renderer,
            queued: Array.Empty<string>(), userLines: submission.Split('\n'), kaomoji: "(=^･ω･^=)",
            body: async () =>
            {
                if (expectedFace is null)
                {
                    //asserting absence needs a fixed wait, since no event arrives to wait for
                    await Task.Delay(400);
                    lock (gate) purrDuringBody = painter.State.PurrText;
                    return false;
                }

                //wait for the purr to appear, a flat delay measures the scheduler and fails under suite load
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
                while (DateTime.UtcNow < deadline)
                {
                    lock (gate) purrDuringBody = painter.State.PurrText;
                    if (!string.IsNullOrEmpty(purrDuringBody)) break;
                    await Task.Delay(15);
                }
                return false;
            });

        if (expectedFace is null) Assert.Null(purrDuringBody);
        else Assert.Contains(expectedFace, purrDuringBody ?? "", StringComparison.Ordinal);
    }

    [Theory]
    //the submissions that reach the model: plain text, and the commands that summarise or rewrite into prompts
    [InlineData("hello there", true)]
    [InlineData("/compact", true)]
    [InlineData("/init", true)]
    [InlineData("/remember something worth keeping", true)]
    //the tui commands below do not reach the model.
    [InlineData("/model", false)]
    [InlineData("/model qwen3.6-35b-a3b", false)]
    [InlineData("/help", false)]
    [InlineData("/role", false)]
    [InlineData("/tools", false)]
    [InlineData("/permissions", false)]
    [InlineData("/new", false)]
    [InlineData("/quit", false)]
    public void ReachesModel_CLASSIFIES_EVERY_COMMAND_BY_WHAT_ITS_DISPATCH_ACTUALLY_DOES(
        string input, bool expected)
    {
        //three slash commands do reach the model, so each row was read from its dispatch branch
        Assert.Equal(expected, Gatto.Repl.SlashCommands.ReachesModel(input));
    }

    [Fact]
    public void EVERY_COMMAND_IN_THE_TABLE_IS_CLASSIFIED_DELIBERATELY()
    {
        //a new command inherits false silently, so pinning the full set forces a deliberate choice
        Assert.Equal(
            ["/compact", "/init", "/remember"],
            Gatto.Repl.SlashCommands.All.Where(c => c.ReachesModel).Select(c => c.Name).Order());
    }

    [Fact]
    public async Task DispatchBracket_PurrSpansToolExecution_AndStopsAfterTheTurn()
    {
        var (_, painter, ticker, renderer, gate) = Wire();
        string? purrDuringTool = null;

        var quit = await Gatto.Repl.Repl.DispatchBracketAsync(
            gate, painter, ticker, () => renderer,
            queued: Array.Empty<string>(), userLines: new[] { "run the tool" }, kaomoji: "(=^･ω･^=)",
            body: async () =>
            {
                //nothing inside a turn may stop the purr, and the fake must set State.Tool or the ticker reads prefill
                lock (gate) painter.State.Tool = ("shell", "ls");
                var deadline = DateTime.UtcNow.AddSeconds(30);
                while (DateTime.UtcNow < deadline)
                {
                    lock (gate) purrDuringTool = painter.State.PurrText;
                    if (purrDuringTool is not null) break;
                    await Task.Delay(20);
                }
                return false;
            });

        Assert.False(quit);
        Assert.NotNull(purrDuringTool);
        Assert.Contains("purr", purrDuringTool!, StringComparison.Ordinal);
        Assert.DoesNotContain("reading context", purrDuringTool!, StringComparison.Ordinal);
        lock (gate) Assert.Null(painter.State.PurrText);   //the bracket's finally stops the ticker, so the purr row is gone once the turn ends
    }

    //the progress row must still show after an earlier round of the turn already emitted text
    [Fact]
    public async Task A_PROGRESS_CHUNK_REACHES_THE_PURR_ROW_THROUGH_THE_RENDERER()
    {
        var (_, painter, ticker, renderer, gate) = Wire();
        ticker.StartTurn("x", promptTokensEstimate: 85_000);
        ticker.MarkStreaming();   //start the turn mid-stream, as it is after an earlier round already emitted text

        renderer.OnPromptProgress(total: 15_063, processed: 4_096);

        string? seen = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            lock (gate) seen = painter.State.PurrText;
            if (seen is not null && seen.Contains("reading context", StringComparison.Ordinal)) break;
            await Task.Delay(20);
        }
        ticker.StopTurn();

        Assert.NotNull(seen);
        Assert.EndsWith("· 4.1k/15.1k", seen!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DispatchBracket_CommitsTheUserEcho_AndPublishesTheQueue()
    {
        var (surface, painter, ticker, renderer, gate) = Wire();

        await Gatto.Repl.Repl.DispatchBracketAsync(
            gate, painter, ticker, () => renderer,
            queued: new[] { "queued next" }, userLines: new[] { "hello there" }, kaomoji: "(=^･ω･^=)",
            body: () => Task.FromResult(false));

        Assert.Contains("❯ hello there", Visible(surface.Text), StringComparison.Ordinal);
        lock (gate) Assert.Equal(new[] { "queued next" }, painter.State.QueueTexts);
    }

    [Fact]
    public async Task DispatchBracket_BodyThrows_StillStopsTheTicker_AndEndsTheTurn()
    {
        var (_, painter, ticker, renderer, gate) = Wire();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Gatto.Repl.Repl.DispatchBracketAsync(gate, painter, ticker, () => renderer,
                Array.Empty<string>(), new[] { "boom" }, "(=^･ω･^=)",
                body: () => throw new InvalidOperationException("turn blew up")));

        lock (gate)
        {
            Assert.Null(painter.State.PurrText);
            Assert.Null(painter.State.TailRaw);   //a null tail means EndTurn ran even though the body threw
        }
    }

    [Fact]
    public async Task DispatchBracket_UsesTheCurrentRenderer_AfterARoleSwap()
    {
        //a role switch replaces the renderer mid-turn, so the bracket's finally must re-read it to flush the new one's tail
        var (surface, painter, ticker, renderer, gate) = Wire();
        var current = renderer;

        await Gatto.Repl.Repl.DispatchBracketAsync(gate, painter, ticker, () => current,
            Array.Empty<string>(), new[] { "hi" }, "(=^･ω･^=)",
            body: () =>
            {
                current = new StreamRenderer(painter, surface, T, "oracle", ticker, gate);
                current.OnTextDelta("swapped");   //the line has no newline yet, so only the new renderer holds it as a tail
                return Task.FromResult(false);
            });

        //the text only reaches the screen if the flush used the current renderer
        Assert.Contains("swapped", Visible(surface.Text), StringComparison.Ordinal);
        lock (gate) Assert.Null(painter.State.TailRaw);
    }

    //the finally must run EndTurn before StopTurn, or the aborted tool call never reaches the transcript
    [Fact]
    public async Task DispatchBracket_AbortedMidTool_CommitsTheBulletToScrollback_ButNotTheDecision()
    {
        var (surface, painter, ticker, renderer, gate) = Wire();

        await Gatto.Repl.Repl.DispatchBracketAsync(gate, painter, ticker, () => renderer,
            Array.Empty<string>(), new[] { "clean the build" }, "(=^･ω･^=)",
            body: () =>
            {
                renderer.OnToolCallStart(new Gatto.Core.Client.ToolCall("i", "shell", """{"cmd":"rm -rf ./build"}"""));
                renderer.CommitPrompt(new[] { "  allow?", "  ✓ allowed once" });
                return Task.FromResult(false);   //the turn ends with no tool result, as a ctrl+c would
            });

        var screen = Visible(surface.Text);
        Assert.Contains("○ shell rm -rf ./build", screen, StringComparison.Ordinal);
        Assert.Contains("⎿ ✗ cancelled", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("allowed once", screen, StringComparison.Ordinal);
        lock (gate) Assert.Null(painter.State.Tool);
    }

    //a degraded painter paints nothing, so the composer echo rewrites the line in place, otherwise the user cannot see what they type
    [Fact]
    public void EchoComposerInline_WritesThePromptAndTheCursorLine()
    {
        var surface = new RecordingSurface { Width = 80, Height = 30 };

        Gatto.Repl.Repl.EchoComposerInline(surface, new EditorView(new List<string> { "one", "two" }, 1, 3), glyphs: GlyphSet.Unicode);

        var text = surface.Text;
        Assert.Contains("❯ two", Visible(text), StringComparison.Ordinal);
        Assert.Contains(Ansi.ClearLine, text, StringComparison.Ordinal);   //the clear sequence proves the row was rewritten in place
    }

    [Fact]
    public void EchoComposerInline_SanitizesTheEcho()
    {
        var surface = new RecordingSurface { Width = 80, Height = 30 };

        Gatto.Repl.Repl.EchoComposerInline(surface, new EditorView(new List<string> { "a\u001b[31mb" }, 0, 0), glyphs: GlyphSet.Unicode);

        //composer text is untrusted input, so the fallback must never paint raw csi codes
        Assert.DoesNotContain("\u001b[31m", surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void CtrlC_MidTurn_AbortsTheTurn_NeverTheLoop()
    {
        //an in-flight turn wins over the double-tap window.
        Assert.Equal(Gatto.Repl.Repl.CtrlCAction.AbortTurn, Gatto.Repl.Repl.DecideCtrlC(turnInFlight: true, composerEmpty: true, nowMs: 5000, lastAtRestMs: 4999));
    }

    [Fact]
    public void CtrlC_AtRest_FirstPressHints_SecondWithinWindowQuits()
    {
        //a zero last press leaves no window open, so the first press can only hint
        Assert.Equal(Gatto.Repl.Repl.CtrlCAction.HintExit, Gatto.Repl.Repl.DecideCtrlC(false, composerEmpty: true, nowMs: 1000, lastAtRestMs: 0));
        //500ms after the first press is still inside the window
        Assert.Equal(Gatto.Repl.Repl.CtrlCAction.Quit, Gatto.Repl.Repl.DecideCtrlC(false, composerEmpty: true, nowMs: 1500, lastAtRestMs: 1000));
        //3000ms after the last press the window has closed, so this one only hints
        Assert.Equal(Gatto.Repl.Repl.CtrlCAction.HintExit, Gatto.Repl.Repl.DecideCtrlC(false, composerEmpty: true, nowMs: 4000, lastAtRestMs: 1000));
    }

    [Fact]
    public void CtrlC_AtRest_WithComposerText_DoesNothing_EvenInsideTheWindow()
    {
        //the mouse-on editor ignores Ctrl+C while the composer holds text, so the mouse-off path must neither hint nor quit
        Assert.Equal(Gatto.Repl.Repl.CtrlCAction.None, Gatto.Repl.Repl.DecideCtrlC(false, composerEmpty: false, nowMs: 1000, lastAtRestMs: 0));
        Assert.Equal(Gatto.Repl.Repl.CtrlCAction.None, Gatto.Repl.Repl.DecideCtrlC(false, composerEmpty: false, nowMs: 1500, lastAtRestMs: 1000));
        //a turn in flight still aborts with text in the composer, as the mouse-on ladder does
        Assert.Equal(Gatto.Repl.Repl.CtrlCAction.AbortTurn, Gatto.Repl.Repl.DecideCtrlC(true, composerEmpty: false, nowMs: 1500, lastAtRestMs: 1000));
    }

    //the cancel ladder has its own tests in CancelLadderTests

    [Fact]
    public void CtrlC_AtRest_LoopExits_ChromeTornDownExactlyOnce_ExitZero()
    {
        //this fixture never drives RunLinearLoopAsync, so a wiring bug in the real loop escapes it
        var (surface, painter, ticker, _, gate) = Wire();
        var chrome = new ChromeHandle { Painter = painter, Renderer = null };
        using var loopCts = new CancellationTokenSource();
        var stopped = false;
        painter.AltScreen.Enter();   //entering the alt buffer matches a real rich session.
        painter.Repaint();           //a real session has painted before a quit, so the fixture paints once too

        //a quit cancels the loop token, so this cancel stands in for one
        Assert.Equal(Gatto.Repl.Repl.CtrlCAction.Quit, Gatto.Repl.Repl.DecideCtrlC(false, composerEmpty: true, nowMs: 1200, lastAtRestMs: 1000));
        loopCts.Cancel();

        var exit = 1;
        try
        {
            while (!loopCts.IsCancellationRequested) { }   //the dispatcher's loop, which exits as soon as the token is cancelled
            exit = 0;
        }
        finally
        {
            Gatto.Repl.Repl.TeardownChrome(gate, painter, ticker, chrome, () => stopped = true, fault: null);
        }

        Assert.Equal(0, exit);
        Assert.True(stopped);
        Assert.False(painter.Alive);           //only teardown clears Alive, so false here means it ran
        Assert.Null(chrome.Painter);           //the handle is disarmed, so a prompt after this falls back inline
        Assert.Contains(Ansi.AltScreenExit, surface.Text, StringComparison.Ordinal);   //the exit sequence means the main buffer came back

        var afterFirst = surface.Text.Length;
        Gatto.Repl.Repl.TeardownChrome(gate, painter, ticker, chrome, () => { }, fault: null);
        Assert.Equal(afterFirst, surface.Text.Length);   //teardown is idempotent, so a second call writes nothing
    }

    [Fact]
    public void TeardownChrome_WithFault_TearsDownFirst_ThenRethrowsTheSameFault()
    {
        var (_, painter, ticker, renderer, gate) = Wire();
        var chrome = new ChromeHandle { Painter = painter, Renderer = renderer };
        var fault = new InvalidOperationException("composer paint fault");
        var stopped = false;

        var thrown = Assert.Throws<InvalidOperationException>(() =>
            Gatto.Repl.Repl.TeardownChrome(gate, painter, ticker, chrome, () => stopped = true, fault));

        Assert.Same(fault, thrown);            //the fault is rethrown through ExceptionDispatchInfo, so the same instance reaches the caller
        Assert.True(stopped);
        Assert.False(painter.Alive);
        Assert.Null(chrome.Painter);
        Assert.Null(chrome.Renderer);
    }

    [Fact]
    public void TeardownChrome_WithFault_SickConsole_TheFaultWinsNotTheWriteFailure()
    {
        var throwing = new ThrowingWriteSurface();
        var gate = new object();
        var painter = new ChromePainter(throwing, T, gate)
        { Frame = new InputFrame(throwing, T, "coder", Status(), glyphs: GlyphSet.Unicode) };
        var ticker = new ChromeTicker(painter, gate);
        throwing.FailFromNowOn = true;
        var fault = new IOException("original paint fault");

        var thrown = Assert.Throws<IOException>(() =>
            Gatto.Repl.Repl.TeardownChrome(gate, painter, ticker, chrome: null, () => { }, fault));

        Assert.Same(fault, thrown);
    }

    [Fact]
    public void DispatcherLoopShape_RawThrowFromLoopBody_StillTearsDownTheChrome_AndEscapes()
    {
        var (_, painter, ticker, _, gate) = Wire();
        var stopped = false;

        Action act = () =>
        {
            try { throw new IOException("dispatcher-thread paint blew up"); }
            finally
            {
                Gatto.Repl.Repl.TeardownChrome(gate, painter, ticker, chrome: null,
                    () => stopped = true, fault: null);
            }
        };
        var thrown = Assert.Throws<IOException>(act);

        Assert.Equal("dispatcher-thread paint blew up", thrown.Message);
        Assert.True(stopped);
        Assert.False(painter.Alive);
    }

    [Fact]
    public void SystemLines_CarryNoParentheses()
    {
        foreach (var line in new[]
                 {
                     Gatto.Repl.Repl.NewConversationLine,
                     Gatto.Repl.Repl.WildUsage, Gatto.Repl.Repl.RoleLine("coder"),
                     Gatto.Repl.Repl.AutoLine(true), Gatto.Repl.Repl.AutoLine(false), Gatto.Repl.Repl.AutoLine(null),
                     Gatto.Repl.Repl.WildLine(true, glyphs: GlyphSet.Unicode), Gatto.Repl.Repl.WildLine(false, glyphs: GlyphSet.Unicode),
                 })
        {
            Assert.False(line.StartsWith('('), $"parenthesized system message: {line}");
        }

        Assert.Equal("new conversation", Gatto.Repl.Repl.NewConversationLine);
        Assert.Equal("role: coder", Gatto.Repl.Repl.RoleLine("coder"));
    }

    [Fact]
    public void SystemLines_CommitAsSharpRows()
    {
        var (surface, _, _, renderer, _) = Wire();

        renderer.CommitSystem(Gatto.Repl.Repl.NewConversationLine);
        renderer.CommitSystem(Gatto.Repl.Repl.RoleLine("coder"));

        var visible = Visible(surface.Text);
        Assert.Contains("♯ new conversation", visible, StringComparison.Ordinal);
        Assert.Contains("♯ role: coder", visible, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpBlock_CommitsAsPlainGutterRows_NotASharpLine()
    {
        var (surface, _, _, renderer, _) = Wire();

        renderer.CommitPlain(SlashCommands.RenderRich(T));

        var visible = Visible(surface.Text);
        Assert.Contains("/quit", visible, StringComparison.Ordinal);
        Assert.DoesNotContain("♯", visible, StringComparison.Ordinal);   //the help block is content, so it gets no sharp row marker
        foreach (var row in visible.Split('\n').Where(r => r.Contains("/quit", StringComparison.Ordinal)))
            Assert.StartsWith("  ", row, StringComparison.Ordinal);      //content rows hang two spaces into the gutter
    }

    [Fact]
    public void CommitUser_StripsControlBytesFromTheSubmittedLine()
    {
        var (surface, _, _, renderer, _) = Wire();
        var baseline = new RecordingSurface { Width = 80, Height = 30 };
        var g2 = new object();
        var p2 = new ChromePainter(baseline, T, g2) { Frame = new InputFrame(baseline, T, "coder", Status(), glyphs: GlyphSet.Unicode) };
        var clean = new StreamRenderer(p2, baseline, T, "coder", new ChromeTicker(p2, g2), g2,
            model: p2.Model, convoTail: () => null);

        renderer.CommitUser(new[] { "safe\u001b[2Aforged" });
        clean.CommitUser(new[] { "safeforged" });

        //the injected csi is stripped, so the row shows no more escapes than a clean one
        Assert.Equal(Count(baseline.Text, "\u001b"), Count(surface.Text, "\u001b"));
        Assert.Contains("safe[2Aforged", Visible(surface.Text), StringComparison.Ordinal);
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private sealed class ThrowingWriteSurface : ITermSurface
    {
        public int Width => 80;
        public int Height => 30;
        public bool FailFromNowOn;
        public void Write(string s)
        {
            if (FailFromNowOn) throw new InvalidOperationException("console handle is invalid");
        }
    }

    //the parked composer never repaints, so a restore has to ask for one, or the message stays invisible

    private sealed class ParkedComposerSource : IComposerSource
    {
        public bool KeyAvailable => false;
        public ComposerInput Read() => throw new InvalidOperationException("the composer is parked");
    }

    private static LineEditor ParkedComposer() =>
        new(new ParkedComposerSource(),
            new History(Path.Combine(Directory.CreateTempSubdirectory("gatto-rc-").FullName, "h.txt")));

    [Fact]
    public void RestoreComposer_WritesTheMessageBack_AndAsksForARepaint()
    {
        var editor = ParkedComposer();
        EditorView? painted = null;
        var restore = Gatto.Repl.Repl.BuildRestoreComposer(new object(), editor, v => painted = v, () => false);

        restore("describe shot.jpg");

        Assert.NotNull(painted);
        Assert.Equal("describe shot.jpg", string.Join("\n", painted!.Lines));
        Assert.Equal("describe shot.jpg".Length, painted.CursorCol);
    }

    [Fact]
    public void RestoreComposer_OnATornDownSession_NeitherWritesNorPaints()
    {
        var editor = ParkedComposer();
        var painted = 0;
        var restore = Gatto.Repl.Repl.BuildRestoreComposer(new object(), editor, _ => painted++, () => true);

        restore("describe shot.jpg");

        Assert.Equal(0, painted);
        Assert.Equal("", string.Join("\n", editor.Snapshot().Lines));
    }
}

//test output is redirected, so caps report plain and the real RunPlainAsync tail runs
[Collection("e2e")]
public class ReplProbeSeamTests : IDisposable
{
    private readonly TextReader _origIn = Console.In;
    private readonly TextWriter _origOut = Console.Out;

    public void Dispose()
    {
        Console.SetIn(_origIn);
        Console.SetOut(_origOut);
    }

    private static Gatto.Repl.Repl ReplWith(
        string modelName = "gemma-4-26b", int? contextBudget = null, Conversation? convo = null,
        ServingProbe? launchServing = null, Func<ServingProbe>? probeServing = null)
    {
        var client = new FakeChatClient();
        for (var i = 0; i < 5; i++)
            client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        return new Gatto.Repl.Repl(
            loop, convo ?? new Conversation("sys"), "generalist", modelName, null, client, null, contextBudget,
            Path.GetTempPath(),
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"),
            launchServing: launchServing, probeServing: probeServing);
    }

    private static async Task<string> Feed(Gatto.Repl.Repl repl, string input)
    {
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader(input + "\n/quit\n"));
        await repl.RunAsync(CancellationToken.None);
        return sw.ToString();
    }

    [Fact]
    public void Construction_SeedsServingFromLaunchServing()
    {
        //the app probes once at launch, so the chip is already set before the first turn
        var repl = ReplWith(launchServing: new ServingProbe("gemma-4-26b", null));
        Assert.Equal("gemma-4-26b", repl.ServingForTest);
    }

    [Fact]
    public void Construction_NoLaunchServing_ServingStartsNull()
    {
        var repl = ReplWith();
        Assert.Null(repl.ServingForTest);
    }

    [Fact]
    public async Task Turn_ReProbes_ChipClearsWhenTheServerCatchesUp()
    {
        //a mismatch shows the chip, and the next turn's re-probe clears it once the server agrees
        var serving = new[] { "qwen3.6-35b" };   //the fake probe reads this cell, so the test can change the answer.
        var repl = ReplWith(modelName: "gemma-4-26b", probeServing: () => new ServingProbe(serving[0], null));

        await Feed(repl, "hello");
        Assert.Equal("qwen3.6-35b", repl.ServingForTest);

        serving[0] = null!;   //a null mismatch now stands for a server that agrees
        await Feed(repl, "hello again");
        Assert.Null(repl.ServingForTest);
    }

    [Fact]
    public async Task Turn_ProbeReturnsUnknown_NeverPaintsAMismatch_EvenAfterAPriorOne()
    {
        //an unknown probe must clear the chip and never leave or invent a mismatch
        var serving = new[] { "gemma-4-26b" };
        var repl = ReplWith(modelName: "qwen3.6-35b", probeServing: () => new ServingProbe(serving[0], null));

        await Feed(repl, "hello");
        Assert.Equal("gemma-4-26b", repl.ServingForTest);   //a genuine mismatch is on the chip before the unknown probe

        serving[0] = null!;   //null here stands for a probe that could not answer
        await Feed(repl, "hello again");
        Assert.Null(repl.ServingForTest);   //an unknown probe result is never reported as a mismatch.
    }

    [Fact]
    public async Task Turn_NoProbeServingClosure_LeavesServingUntouched_NeverThrows()
    {
        var repl = ReplWith(launchServing: new ServingProbe("gemma-4-26b", null), probeServing: null);

        await Feed(repl, "hello");

        Assert.Equal("gemma-4-26b", repl.ServingForTest);   //with no probe closure the launch chip is left as it was
    }

    [Fact]
    public async Task Turn_ContextFitWarning_FiresOnce_NotEveryTurn_WhileStillOverBudget()
    {
        //the conversation starts over budget, so the warning fires once and the next turn must stay quiet
        var convo = new Conversation(new string('x', 5000));
        var repl = ReplWith(modelName: "gemma-4-26b", contextBudget: 1000, convo: convo);

        var first = await Feed(repl, "hello");
        Assert.Contains(
            "history is ~1k tokens; gemma-4-26b holds 1000 — run /compact before your next message", first);

        var second = await Feed(repl, "hello again");   //still over budget, so it is the same crossing and the warning must stay silent
        Assert.DoesNotContain("run /compact before your next message", second);
    }

    [Fact]
    public async Task Turn_ProbedWindowSmallerThanDeclared_IsTheBindingConstraint()
    {
        //a hand-started server may run a smaller window than the model declares, so the probed window binds and the warning names it
        var convo = new Conversation(new string('x', 5000));   //5000 chars is about 1250 tokens, from the chars÷4 estimate
        var repl = ReplWith(modelName: "gemma-4-26b", contextBudget: 1_000_000, convo: convo,
            probeServing: () => new ServingProbe(null, 1000));

        var output = await Feed(repl, "hello");

        Assert.Contains("server's actual window is 1000", output);
        Assert.Contains("run /compact before your next message", output);
    }

    [Fact]
    public async Task Turn_ContextFitWarning_NeverFires_WhenUnderBudget()
    {
        var repl = ReplWith(modelName: "gemma-4-26b", contextBudget: 1_000_000);

        var output = await Feed(repl, "hello");

        Assert.DoesNotContain("run /compact before your next message", output);
    }

    [Fact]
    public async Task Turn_ContextFitWarning_IsAdvisoryOnly_TheTurnStillReachesTheModel()
    {
        //the warning is advisory, so an over-budget turn is still sent
        var convo = new Conversation(new string('x', 5000));
        var repl = ReplWith(modelName: "gemma-4-26b", contextBudget: 1000, convo: convo);

        var output = await Feed(repl, "hello");

        Assert.Contains("ok", output);   //the model's own reply proves the turn ran.
    }

    [Fact]
    public void ShouldWarnContextFit_FiresOnce_StrictlyOverBudget()
    {
        Assert.False(Gatto.Repl.Repl.ShouldWarnContextFit(100, 100, alreadyWarned: false));  //exactly at budget is not over yet.
        Assert.True(Gatto.Repl.Repl.ShouldWarnContextFit(101, 100, alreadyWarned: false));
        Assert.False(Gatto.Repl.Repl.ShouldWarnContextFit(101, 100, alreadyWarned: true));   //a reported crossing must not warn again.
    }

    [Fact]
    public void ShouldWarnContextFit_NullOrZeroBudget_NeverFires()
    {
        Assert.False(Gatto.Repl.Repl.ShouldWarnContextFit(1_000_000, null, alreadyWarned: false));
        Assert.False(Gatto.Repl.Repl.ShouldWarnContextFit(1_000_000, 0, alreadyWarned: false));
    }

}
