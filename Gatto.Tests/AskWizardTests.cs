using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//every assertion matches ordinally, a culture-sensitive match ignores ESC. pump: null runs it synchronously, so a dry script throws instead of hanging
public class AskWizardTests
{
    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("test scripted too few keys");
    }

    //record the painter's panel rows before each key, the synchronous wizard gives no other view of the block
    private sealed class SnapshotKeys(ChromePainter painter, IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public List<IReadOnlyList<string>?> Snapshots { get; } = new();
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey()
        {
            Snapshots.Add(painter.State.PanelRows);
            return _q.Count > 0
                ? _q.Dequeue()
                : throw new InvalidOperationException("test scripted too few keys");
        }
    }

    private static readonly Theme T = new(new TermCaps(true, true));

    private static ConsoleKeyInfo Special(ConsoleKey k) => new('\0', k, false, false, false);
    private static ConsoleKeyInfo Digit(char d) => new(d, (ConsoleKey)((int)ConsoleKey.D0 + (d - '0')), false, false, false);
    private static ConsoleKeyInfo Enter() => Special(ConsoleKey.Enter);
    private static ConsoleKeyInfo Left() => Special(ConsoleKey.LeftArrow);
    private static ConsoleKeyInfo Right() => Special(ConsoleKey.RightArrow);
    private static ConsoleKeyInfo Down() => Special(ConsoleKey.DownArrow);
    private static ConsoleKeyInfo Esc() => Special(ConsoleKey.Escape);
    private static ConsoleKeyInfo[] Type(string s) =>
        s.Select(c => new ConsoleKeyInfo(c, ConsoleKey.None, false, false, false)).ToArray();

    private static AskQuestion Q(string header, string question, bool multi, params AskOption[] options) =>
        new(question, header, options, multi);

    //keep the blocking read here, out of a [Fact] body, to satisfy xUnit1031 (the task is already complete)
    private static IReadOnlyList<AskAnswer> Run(
        IReadOnlyList<AskQuestion> questions, out RecordingSurface surface, params ConsoleKeyInfo[] keys)
    {
        surface = new RecordingSurface { Width = 80, Height = 0 };
        var p = new RichPrompter(surface, T, new ScriptedKeys(keys));
        return p.AskAsync(questions, CancellationToken.None).GetAwaiter().GetResult();
    }

    private static IReadOnlyList<AskAnswer> Run(IReadOnlyList<AskQuestion> questions, params ConsoleKeyInfo[] keys)
        => Run(questions, out _, keys);

    //keep the blocking read out of a [Fact] body to satisfy xUnit1031 (the wizard is synchronous, so the task is already complete)
    private static IReadOnlyList<AskAnswer> Drive(RichPrompter p, IReadOnlyList<AskQuestion> questions)
        => p.AskAsync(questions, CancellationToken.None).GetAwaiter().GetResult();

    private static (RecordingSurface S, ChromePainter P, ChromeHandle H) Armed()
    {
        var surface = new RecordingSurface { Width = 80, Height = 0 };
        var gate = new object();
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        var painter = new ChromePainter(surface, T, gate)
        {
            Frame = new InputFrame(surface, T, "coder", status, glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        var renderer = new StreamRenderer(painter, surface, T, "coder", new ChromeTicker(painter, gate), gate,
            model: painter.Model, convoTail: () => null);
        return (surface, painter, new ChromeHandle { Painter = painter, Renderer = renderer });
    }

    //drive the wizard on the armed path and record the panel rows before every key.
    private static (IReadOnlyList<AskAnswer> Answers, List<IReadOnlyList<string>?> Frames, ChromePainter Painter)
        RunArmed(IReadOnlyList<AskQuestion> questions, params ConsoleKeyInfo[] keys)
    {
        var (_, painter, handle) = Armed();
        var script = new SnapshotKeys(painter, keys);
        var p = new RichPrompter(new RecordingSurface { Width = 80 }, T, script, pump: null, chrome: handle);
        var answers = p.AskAsync(questions, CancellationToken.None).GetAwaiter().GetResult();
        return (answers, script.Snapshots, painter);
    }

    //match with an ordinal comparison, the rows hold ESC and a culture-sensitive comparison ignores it
    private static bool Has(IReadOnlyList<string>? frame, string needle) =>
        frame is not null && frame.Any(r => r.Contains(needle, StringComparison.Ordinal));

    private static void AssertFrameHas(IReadOnlyList<string>? frame, string needle)
    {
        Assert.NotNull(frame);
        Assert.True(Has(frame, needle),
            $"no row carries \"{needle}\". Rows:\n  " + string.Join("\n  ",
                frame!.Select(r => r.Replace("\x1b", "<ESC>", StringComparison.Ordinal))));
    }

    //strip the ANSI codes and the trailing spaces, keep the blank rows, so a layout assertion compares rows as the screen shows them
    private static string[] Stripped(IReadOnlyList<string>? frame)
    {
        Assert.NotNull(frame);
        return frame!.Select(TermText.StripAnsiForWidth).Select(r => r.TrimEnd()).ToArray();
    }

    [Fact]
    public void QuestionStep_LayoutIsTheWholeBlock_InOrder()
    {
        //compare every row, a check that some row holds a text passes a block with the parts out of order or with extra rows
        var (_, frames, _) = RunArmed([Q("Lang", "Which?", false, "Go", "Rust")], Digit('1'), Enter());

        Assert.Equal(new[]
        {
            "  Lang",
            "",
            "  Which?",
            //every select prompt puts a blank row after the question, so the question stays apart from its options.
            "",
            "❯ 1. Go",
            "  2. Rust",
            "  3. Type my own answer…",
            "",
            //one question shows no hint to move between questions, no other question exists to move to
            "  Esc to cancel",
        }, Stripped(frames[0]));
    }

    [Fact]
    public void QuestionStep_TwoQuestions_KeepsMoveHint()
    {
        //the hint to move between questions appears only when two or more questions exist.
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go"), Q("Build", "How?", false, "make")],
            Digit('1'), Digit('1'), Enter());

        Assert.Contains("  ← → to move between questions · Esc to cancel",
            Stripped(frames[0]));
    }

    [Fact]
    public void ReviewStep_LayoutIsTheWholeBlock_InOrder()
    {
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go", "Rust"), Q("Build", "How?", false, "make")],
            Digit('2'), Right(), Right(), Enter());

        //the review body uses the BodyRows region, the uniform bold belongs to TitleRows
        Assert.Equal(new[]
        {
            "  Lang  Build",
            "  Review your answers",
            "",
            "  ● Which?",
            "    → Rust",
            "  ● How?",
            "    → No answer. Skipped",
            "",
            "  Ready to submit your answers?",
            "",
            "❯ 1. Submit answers",
            "  2. Cancel",
            "",
            "  Enter to submit · ← to go back · Esc to cancel",
        }, Stripped(frames[3]));
    }

    [Fact]
    public void ReviewStep_CancelRow_CancelsTheWizard()
    {
        //the Cancel row resolves exactly as Esc does, it withdraws the call, discards partial answers and throws the shared cancel message
        var (_, painter, handle) = Armed();
        var script = new SnapshotKeys(painter, [Digit('1'), Digit('2')]);
        var p = new RichPrompter(new RecordingSurface { Width = 80 }, T, script, pump: null, chrome: handle);
        var ex = Assert.Throws<InvalidOperationException>(() =>
            p.AskAsync([Q("Lang", "Which?", false, "Go", "Rust")], CancellationToken.None)
                .GetAwaiter().GetResult());
        Assert.Equal(Gatto.Core.Loop.Permissions.PermissionGate.CancelMessage, ex.Message);
    }

    [Fact]
    public void ResizedMidWizard_EveryRowStillFits_OnBothSteps()
    {
        //shrink the shared surface mid-wizard and check both steps, because the review step renders again at the new width (a private surface proves nothing)
        var (surface, painter, handle) = Armed();
        var frames = new List<IReadOnlyList<string>?>();
        var keys = new[] { Digit('1'), Digit('1'), Enter() };
        var script = new ProbeKeys(n =>
        {
            frames.Add(painter.State.PanelRows);
            if (n == 0) surface.Width = 24;   //shrink the surface before the second question renders.
        }, keys);

        var p = new RichPrompter(surface, T, script, pump: null, chrome: handle);
        Drive(p, [
            Q("A rather long header", "A question long enough to need more than twenty-four cells", false,
                "An option label that is comfortably too wide"),
            Q("Another long header", "And a second one, equally long", false, "Another wide option label")]);

        //frames 1 and 2 are the question step after the shrink and the review step.
        foreach (var frame in frames.Skip(1))
            foreach (var row in Stripped(frame))
                Assert.True(UnicodeWidth.Of(row) <= 24, $"row wider than the terminal: \"{row}\"");
    }

    private sealed class ProbeKeys(Action<int> before, IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        private int _n;
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey()
        {
            before(_n++);
            return _q.Count > 0
                ? _q.Dequeue()
                : throw new InvalidOperationException("test scripted too few keys");
        }
    }

    [Fact]
    public void SingleQuestion_DigitAnswers_ReturnsLabel()
    {
        //a digit answers the question, and with nothing left unanswered the next Enter submits from the review step.
        var answers = Run([Q("Lang", "Which?", false, "Go", "Rust")], Digit('2'), Enter());

        Assert.Equal("Lang", Assert.Single(answers).Header);
        Assert.Equal(["Rust"], answers[0].Selected);
    }

    [Fact]
    public void SingleQuestion_EnterOnCursorAnswers()
    {
        var answers = Run([Q("Lang", "Which?", false, "Go", "Rust")], Down(), Enter(), Enter());
        Assert.Equal(["Rust"], answers[0].Selected);
    }

    [Fact]
    public void FreeText_AutoAdded_AndReturnsVerbatim()
    {
        //the prompter adds the free-text row itself, so the row takes the last position and returns its text verbatim
        var answers = Run([Q("Lang", "Which?", false, "Go", "Rust")],
            [Digit('3'), .. Type("zig, actually"), Enter(), Enter()]);

        Assert.Equal(["zig, actually"], answers[0].Selected);
    }

    [Fact]
    public void FreeText_RowIsLabelled_OnSingleSelectOnly()
    {
        var (_, frames, _) = RunArmed([Q("Lang", "Which?", false, "Go", "Rust")], Digit('1'), Enter());
        AssertFrameHas(frames[0], "Type my own answer…");

        var (_, multiFrames, _) = RunArmed([Q("Lang", "Which?", true, "Go", "Rust")], Down(), Down(), Enter(), Enter());
        Assert.False(Has(multiFrames[0], "Type my own answer…"));
    }

    [Fact]
    public void MultiSelect_TogglesAndNext_ReturnsJoinedSelection()
    {
        //in multi-select a digit toggles and only the Next row resolves, the result keeps option order
        var answers = Run([Q("Lang", "Which?", true, "Go", "Rust", "Zig")],
            Digit('3'), Digit('1'), Down(), Down(), Down(), Enter(), Enter());

        Assert.Equal(["Go", "Zig"], answers[0].Selected);
    }

    [Fact]
    public void Recommended_SuffixRendered_FirstWins()
    {
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", false,
                new AskOption("Go", "Fast compiles", Recommended: true),
                new AskOption("Rust", "Memory safety", Recommended: true))],
            Digit('1'), Enter());

        var frame = frames[0]!;
        var go = Assert.Single(frame, r => r.Contains("Go", StringComparison.Ordinal));
        Assert.Contains("(Recommended)", go, StringComparison.Ordinal);
        var rust = Assert.Single(frame, r => r.Contains("Rust", StringComparison.Ordinal));
        Assert.DoesNotContain("(Recommended)", rust, StringComparison.Ordinal);
        //descriptions appear as the dim child rows of the widget.
        AssertFrameHas(frame, "Fast compiles");
        AssertFrameHas(frame, "Memory safety");
    }

    [Fact]
    public void TabBar_ShowsEveryHeader_ActiveIsTheLiveQuestion()
    {
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go"), Q("Build", "How?", false, "make")],
            Right(), Digit('1'), Digit('1'), Enter());

        //frame 0 shows question 1 as live, and frame 1 shows question 2 after the right arrow.
        Assert.Contains(T.Paint("Lang", Theme.Accent), frames[0]![0], StringComparison.Ordinal);
        Assert.Contains(T.Paint("Build", Theme.Dim), frames[0]![0], StringComparison.Ordinal);
        Assert.Contains(T.Paint("Build", Theme.Accent), frames[1]![0], StringComparison.Ordinal);
        Assert.Contains(T.Paint("Lang", Theme.Dim), frames[1]![0], StringComparison.Ordinal);
    }

    [Fact]
    public void TabBar_ArrowsNavigate_PartialStatePreserved()
    {
        //the widget opens again for each step, so the Moved outcome must hand back its state (a question would otherwise come back blank)
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go", "Rust"), Q("Build", "How?", true, "make", "ninja", "bazel")],
            Right(),
            Digit('2'),
            Down(),
            Left(),
            Right(),
            Right(),
            Enter());

        //frame 1 shows question 2 fresh and unchecked (without it the frame 5 assertions pass on a widget that always renders that shape)
        Assert.Contains(frames[1]!, r => r.Contains("[ ] ninja", StringComparison.Ordinal));
        Assert.Contains(frames[1]!, r => r.Contains('❯') && r.Contains("make", StringComparison.Ordinal));

        //frame 5 shows question 2 again, and both the toggle and the cursor row must return.
        Assert.Contains(frames[5]!, r => r.Contains("[x] ninja", StringComparison.Ordinal));
        Assert.Contains(frames[5]!, r => r.Contains('❯') && r.Contains("ninja", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoAdvance_ForwardOnly_SkippedNeverPullsBackward()
    {
        //a skipped question stays skipped, answering never moves the user backward, so the wizard goes to the review once nothing ahead is unanswered
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go"), Q("Build", "How?", false, "make")],
            Right(), Digit('1'), Enter());

        AssertFrameHas(frames[2], "Review your answers");   //the wizard must not return to question 1.
    }

    [Fact]
    public void AutoAdvance_ForwardToTheNextUnanswered_NotMerelyTheNextIndex()
    {
        //advance goes to the next unanswered question ahead, question 3 here, skipping the answered question 2
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go"), Q("Build", "How?", false, "make"), Q("Style", "What?", false, "tabs")],
            Digit('1'),
            Digit('1'),
            Left(), Left(),
            Digit('1'),   //answers question 1 again, which must move to question 3, the next unanswered
            Digit('1'),
            Enter());

        Assert.Contains(T.Paint("Style", Theme.Accent), frames[5]![0], StringComparison.Ordinal);
        AssertFrameHas(frames[6], "Review your answers");
    }

    [Fact]
    public void AnswerAnswerSkipAnswer_LandsOnReview()
    {
        //an Enter on question 4 opens the review with question 3 shown as skipped
        var (answers, frames, _) = RunArmed(
            [Q("A", "Q-A?", false, "a"), Q("B", "Q-B?", false, "b"),
             Q("C", "Q-C?", false, "c"), Q("D", "Q-D?", false, "d")],
            Digit('1'), Digit('1'), Right(), Digit('1'), Enter());

        AssertFrameHas(frames[4], "Review your answers");
        Assert.Contains("    → No answer. Skipped", Stripped(frames[4]));
        Assert.Empty(answers[2].Selected);
        Assert.Equal(["d"], answers[3].Selected);
    }

    [Fact]
    public void FreeTextDraft_PersistsAcrossQuestionMoves()
    {
        //a typed custom answer that is not submitted must survive the arrow keys. the Moved outcome returns the draft, and the question seeds it again when it opens
        var (answers, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go", "Rust"), Q("Build", "How?", false, "make")],
            [Digit('3'), .. Type("dr"), Right(), Left(), .. Type("aft"),
             Enter(), Digit('1'), Enter()]);

        //frame 5 is question 1 reopened, with the draft still on the row and the cursor on it
        Assert.Contains(Stripped(frames[5]), r => r.Contains("❯ 3. dr"));
        Assert.Equal(["draft"], answers[0].Selected);
        Assert.Equal(["make"], answers[1].Selected);
    }

    [Fact]
    public void BlankEnterOnTheFreeTextRow_SkipsForward()
    {
        //a blank Enter on the free-text row skips forward like the right arrow, and the review shows the question as skipped
        var (answers, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go"), Q("Build", "How?", false, "make")],
            Digit('2'),   //with one option, the free-text row is row 2.
            Enter(),
            Digit('1'),   //answering question 2 opens the review (advance only moves forward and nothing unanswered is ahead)
            Enter());

        AssertFrameHas(frames[2], "How?");
        Assert.Empty(answers[0].Selected);
        Assert.Equal(["make"], answers[1].Selected);
    }

    [Fact]
    public void ArrowingAway_SavesStateOnTheQuestionBEINGLEFT_NotTheOneArrivedAt()
    {
        //check both questions separately, a wizard that saves the state onto the destination passes a check on one frame
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go", "Rust", "Zig"), Q("Build", "How?", true, "make", "ninja", "bazel")],
            Right(),                            //the cursor of question 1 is still at 0.
            Down(), Down(),
            Left(),                             //the cursor of question 2 must be saved on question 2.
            Digit('1'),
            Down(), Down(), Down(), Enter(),    //the Down key clamps, so three presses reach the Next row from any position.
            Enter());

        //question 1 must show its own cursor, whichever row question 2 was left on
        Assert.Contains(frames[4]!, r => r.Contains('❯') && r.Contains("1. Go", StringComparison.Ordinal));
        //question 2 must show the cursor it was left on (the checkbox sits between the number and the label)
        Assert.Contains(frames[5]!, r => r.Contains('❯') && r.Contains("3. [ ] bazel", StringComparison.Ordinal));
    }

    [Fact]
    public void ArrowingPastAQuestion_LeavesItSkipped()
    {
        var answers = Run(
            [Q("Lang", "Which?", false, "Go"), Q("Build", "How?", false, "make")],
            Right(),
            Digit('1'),     //answering question 2 opens the review (advance only moves forward and nothing unanswered is ahead)
            Enter());

        Assert.Equal(2, answers.Count);
        Assert.Equal("Lang", answers[0].Header);
        Assert.Empty(answers[0].Selected);
        Assert.Equal(["make"], answers[1].Selected);
    }

    [Fact]
    public void AllAnswered_LandsOnReview()
    {
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go"), Q("Build", "How?", false, "make")],
            Digit('1'), Digit('1'), Enter());

        AssertFrameHas(frames[2], "Review your answers");
        //each question is a bullet row with its answer on the row below
        var review = Stripped(frames[2]);
        Assert.Contains("  ● Which?", review);
        Assert.Contains("    → Go", review);
        Assert.Contains("  ● How?", review);
        Assert.Contains("    → make", review);
    }

    [Fact]
    public void Review_EnterSubmitsInQuestionOrder()
    {
        //the answers are given out of order, and the result must follow question order. advance only moves forward, so the test steps back.
        var answers = Run(
            [Q("Lang", "Which?", false, "Go"), Q("Build", "How?", false, "make")],
            Right(), Digit('1'), Left(), Left(), Digit('1'), Enter());

        Assert.Equal(["Lang", "Build"], answers.Select(a => a.Header));
        Assert.Equal(["Go"], answers[0].Selected);
        Assert.Equal(["make"], answers[1].Selected);
    }

    [Fact]
    public void Review_SkippedShowsNoAnswer()
    {
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go"), Q("Build", "How?", false, "make")],
            Right(), Digit('1'), Right(), Right(), Enter());

        var review = Stripped(frames[4]);
        Assert.Contains("    → No answer. Skipped", review);
        Assert.Contains("    → make", review);
    }

    [Fact]
    public void Review_MultiSelectAnswerJoinedWithCommas()
    {
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", true, "Go", "Rust", "Zig")],
            Digit('1'), Digit('3'), Down(), Down(), Down(), Enter(), Enter());

        Assert.Contains("    → Go, Zig", Stripped(frames[6]));
    }

    [Fact]
    public void Review_BackEditsThenReturnsToReview()
    {
        //the left arrow on the review reopens the last question, and answering it returns to the review (auto-advance finds nothing unanswered)
        var (answers, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go"), Q("Build", "How?", false, "make", "ninja")],
            Digit('1'), Digit('1'),
            Left(),
            Digit('2'),
            Enter());

        AssertFrameHas(frames[3], "How?");
        Assert.Contains("    → ninja", Stripped(frames[4]));
        Assert.Equal(["ninja"], answers[1].Selected);
    }

    [Fact]
    public void Review_BackIntoAnAnsweredSingleSelect_ParksTheCursorOnItsPick()
    {
        //a revisited single-select question must park the cursor on its earlier pick (a stale cursor would hide the answer)
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go", "Rust", "Zig"), Q("Build", "How?", false, "make")],
            Digit('3'),   //answer question 1 with the third option, so the check can tell the pick from a stale cursor at 0.
            Digit('1'),
            Left(),
            Left(),
            Digit('1'),   //answer again only so the wizard can finish.
            Enter());

        Assert.Contains(frames[4]!, r => r.Contains('❯') && r.Contains("3. Zig", StringComparison.Ordinal));
    }

    [Fact]
    public void Review_BackIntoAnAnsweredMultiSelect_ShowsItsTicksAgain()
    {
        //the ticks return through their own path, a revisited multi-select question must show them or the user loses the set silently
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", true, "Go", "Rust", "Zig")],
            Digit('1'), Digit('3'),
            Down(), Down(), Down(), Enter(),
            Left(),
            Down(), Down(), Down(), Enter(), Enter());

        var reopened = frames[7]!;
        Assert.Contains(reopened, r => r.Contains("[x] Go", StringComparison.Ordinal));
        Assert.Contains(reopened, r => r.Contains("[x] Zig", StringComparison.Ordinal));
        Assert.Contains(reopened, r => r.Contains("[ ] Rust", StringComparison.Ordinal));
    }

    [Fact]
    public void Review_AQuestionResolvedToNothing_AlsoReadsAsSkipped()
    {
        //a question never opened and one answered with an empty set give an empty Selected, and both read as skipped in the review
        var (answers, frames, _) = RunArmed(
            [Q("Lang", "Which?", true, "Go", "Rust")],
            Down(), Down(), Enter(),   //go to the Next row with nothing ticked.
            Enter());

        Assert.Contains("    → No answer. Skipped", Stripped(frames[3]));
        Assert.Empty(answers[0].Selected);
    }

    [Fact]
    public void Review_RepeatedBackKeepsWalkingBack()
    {
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go"), Q("Build", "How?", false, "make")],
            Digit('1'), Digit('1'),
            Left(),
            Left(),
            Digit('1'),
            Enter());

        Assert.Contains(T.Paint("Build", Theme.Accent), frames[3]![0], StringComparison.Ordinal);
        Assert.Contains(T.Paint("Lang", Theme.Accent), frames[4]![0], StringComparison.Ordinal);
    }

    [Fact]
    public void Review_RightArrowDead()
    {
        var (_, frames, _) = RunArmed(
            [Q("Lang", "Which?", false, "Go")],
            Digit('1'), Right(), Right(), Enter());

        AssertFrameHas(frames[1], "Review your answers");
        AssertFrameHas(frames[2], "Review your answers");
        AssertFrameHas(frames[3], "Review your answers");
    }

    [Fact]
    public void Review_TabStripPaintsNoActiveChip()
    {
        //the review comes after every question, so no chip shows as the live one.
        var (_, frames, _) = RunArmed([Q("Lang", "Which?", false, "Go")], Digit('1'), Enter());
        Assert.Contains(T.Paint("Lang", Theme.Dim), frames[1]![0], StringComparison.Ordinal);
        Assert.DoesNotContain(T.Paint("Lang", Theme.Accent), frames[1]![0], StringComparison.Ordinal);
    }

    [Fact]
    public void Review_LeftFromTheFirstQuestion_Clamps()
    {
        var (_, frames, _) = RunArmed([Q("Lang", "Which?", false, "Go")], Left(), Left(), Digit('1'), Enter());
        AssertFrameHas(frames[1], "Which?");
        AssertFrameHas(frames[2], "Which?");
    }

    [Fact]
    public void ZeroOptions_SingleSelect_FreeTextOnly()
    {
        //with no options the free-text row is the only row, the cursor starts on it and typing goes in with no Enter first
        var (answers, frames, _) = RunArmed(
            [Q("Note", "Anything to add?", false)],
            [.. Type("all good"), Enter(), Enter()]);

        AssertFrameHas(frames[0], "Type my own answer…");
        Assert.Equal(["all good"], answers[0].Selected);
    }

    [Fact]
    public void ZeroOptions_MultiSelect_ResolvesSkipped()
    {
        //only the Next row renders, and confirming it gives the same empty Selected as a skipped question
        var (answers, frames, _) = RunArmed([Q("Note", "Any extras?", true)], Enter(), Enter());

        AssertFrameHas(frames[0], "Next");
        Assert.False(Has(frames[0], "Type my own answer…"));
        Assert.Empty(Assert.Single(answers).Selected);
    }

    private sealed class AbortSpy
    {
        public int Calls;
        public TurnAbortHandle Handle { get; }
        public AbortSpy()
        {
            Handle = new TurnAbortHandle();
            Handle.Current = () => { Calls++; return null; };
        }
    }

    private static Exception Cancelled(IReadOnlyList<AskQuestion> questions, TurnAbortHandle? abort, params ConsoleKeyInfo[] keys)
    {
        var p = new RichPrompter(new RecordingSurface { Width = 80 }, T, new ScriptedKeys(keys),
            pump: null, chrome: null, abort: abort);
        return Assert.Throws<InvalidOperationException>(
            () => p.AskAsync(questions, CancellationToken.None).GetAwaiter().GetResult());
    }

    [Fact]
    public void Esc_ThrowsCancelledByUser_AndRequestsAbort()
    {
        var spy = new AbortSpy();
        var ex = Cancelled([Q("Lang", "Which?", false, "Go")], spy.Handle, Esc());

        //the cancel is an ordinary exception with the shared message, an OperationCanceledException would reach the cancelled-token branch and skip the error tool result
        Assert.Equal("cancelled by user", ex.Message);
        Assert.Equal(1, spy.Calls);
    }

    [Fact]
    public void Esc_OnAnyQuestion_Cancels()
    {
        var spy = new AbortSpy();
        var ex = Cancelled([Q("Lang", "Which?", false, "Go"), Q("Build", "How?", false, "make")],
            spy.Handle, Digit('1'), Right(), Esc());
        Assert.Equal("cancelled by user", ex.Message);
        Assert.Equal(1, spy.Calls);
    }

    [Fact]
    public void Esc_OnReview_Cancels()
    {
        var spy = new AbortSpy();
        var ex = Cancelled([Q("Lang", "Which?", false, "Go")], spy.Handle, Digit('1'), Esc());
        Assert.Equal("cancelled by user", ex.Message);
        Assert.Equal(1, spy.Calls);
    }

    [Fact]
    public void Cancellation_NoPartialAnswersEscape()
    {
        //a cancel after a partial answer must leak nothing, no answers on the exception and no return value for the caller
        var ex = Cancelled([Q("Lang", "Which?", false, "Go"), Q("Build", "How?", false, "make")],
            abort: null, Digit('1'), Esc());

        Assert.Equal("cancelled by user", ex.Message);
        Assert.Empty(ex.Data);
        Assert.DoesNotContain("Go", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void NoAbortHandle_EscStillCancels()
    {
        //the plain and -p paths and every prompter built directly pass no abort handle, and Esc must still cancel the call
        Assert.Equal("cancelled by user", Cancelled([Q("Lang", "Which?", false, "Go")], abort: null, Esc()).Message);
    }

    [Fact]
    public void AnsweringNeverRequestsAbort()
    {
        //an abort on a normal answer would kill the turn that the model still works in, with no warning.
        var spy = new AbortSpy();
        var p = new RichPrompter(new RecordingSurface { Width = 80 }, T,
            new ScriptedKeys([Digit('1'), Enter()]), pump: null, chrome: null, abort: spy.Handle);
        Drive(p, [Q("Lang", "Which?", false, "Go")]);
        Assert.Equal(0, spy.Calls);
    }

    [Fact]
    public void CancelledToken_IsObservedBeforeAnyKeyIsRead()
    {
        var p = new RichPrompter(new RecordingSurface { Width = 80 }, T, new ScriptedKeys([]));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(
            () => p.AskAsync([Q("Lang", "Which?", false, "Go")], cts.Token).GetAwaiter().GetResult());
    }

    [Fact]
    public void NoQuestions_ReturnsEmpty_WithoutOpeningAModal()
    {
        //an empty ask must never open a modal, and the empty key script would throw if any key were read
        Assert.Empty(Run(Array.Empty<AskQuestion>()));
    }

    [Fact]
    public void DeadPump_RaisesTheWidgetsOwnContract()
    {
        //a dead pump arrives as an InvalidOperationException from the key source and must pass through with its own message
        var p = new RichPrompter(new RecordingSurface { Width = 80 }, T, new ScriptedKeys([]));
        var ex = Assert.Throws<InvalidOperationException>(
            () => p.AskAsync([Q("Lang", "Which?", false, "Go")], CancellationToken.None).GetAwaiter().GetResult());
        Assert.NotEqual("cancelled by user", ex.Message);
    }

    [Fact]
    public void AnsweredWizard_LeavesNoTranscript_AndClearsThePanel()
    {
        //an answered prompt commits nothing to the transcript, the answers reach the model through the tool result
        var (_, _, painter) = RunArmed(
            [Q("Lang", "Which?", false, "Go", "Rust")], Digit('2'), Enter());

        Assert.Empty(painter.Model.Items);
        Assert.Null(painter.State.PanelRows);
    }

    [Fact]
    public void AnsweredWizard_EmitsNothingAtTheCOMMITSEAM_Either()
    {
        //a clean transcript proves nothing on its own (CommitPrompt adds no item), so check the rows through CommitTap
        var (_, painter, handle) = Armed();
        var committed = new List<string>();
        handle.Renderer!.CommitTap = rows => committed.AddRange(rows);

        var script = new SnapshotKeys(painter, [Digit('2'), Enter()]);
        var p = new RichPrompter(new RecordingSurface { Width = 80 }, T, script, pump: null, chrome: handle);
        Drive(p, [Q("Lang", "Which?", false, "Go", "Rust")]);

        Assert.Empty(committed);
        Assert.Empty(painter.Model.Items);
    }

    [Fact]
    public void CancelledWizard_LeavesNoTranscript_AndClearsThePanel()
    {
        var (_, painter, handle) = Armed();
        var p = new RichPrompter(new RecordingSurface { Width = 80 }, T,
            new ScriptedKeys([Digit('1'), Esc()]), pump: null, chrome: handle);

        Assert.Throws<InvalidOperationException>(
            () => p.AskAsync([Q("Lang", "Which?", false, "Go")], CancellationToken.None).GetAwaiter().GetResult());

        Assert.Empty(painter.Model.Items);
        Assert.Null(painter.State.PanelRows);
    }

    [Fact]
    public void ModelEscapes_RenderInert_ButRawTextIsReturned()
    {
        //an ESC inside a question, option or header must render inert, while the answer returns the raw option string
        var evil = "Rust\x1b[5A\x1b[2Kforged";
        var (answers, frames, _) = RunArmed(
            [Q("La\x1b[31mng", "Which\x1b[2K?", false, "Go", evil)], Digit('2'), Enter());

        Assert.Equal([evil], answers[0].Selected);   //sanitizing for display must not change the returned data.

        //check the stripped frame, a raw frame holding a live escape matches the literal text too
        Assert.Contains(Stripped(frames[0]), r => r.Contains("[5A", StringComparison.Ordinal));
        Assert.Contains(Stripped(frames[0]), r => r.Contains("[31mng", StringComparison.Ordinal));
    }

    private sealed class FeedKeys(System.Collections.Concurrent.BlockingCollection<ConsoleKeyInfo> q) : IKeySource
    {
        public ConsoleKeyInfo ReadKey() => q.Take();
        public bool KeyAvailable => q.Count > 0;
    }

    private sealed class NeverKeys : IKeySource
    {
        public ConsoleKeyInfo ReadKey() =>
            throw new InvalidOperationException("constructor keys must not be used when a pump is present");
        public bool KeyAvailable => false;
    }

    private sealed class SyncSurface : ITermSurface
    {
        private readonly System.Text.StringBuilder _sb = new();
        public int Width { get; set; } = 80;
        public int Height { get; set; }
        public void Write(string s) { lock (_sb) _sb.Append(s); }
        public string Text { get { lock (_sb) return _sb.ToString(); } }
    }

    private static IReadOnlyList<AskAnswer>? WaitForAnswers(Task<IReadOnlyList<AskAnswer>> ask, int ms = 30000)
        => ask.Wait(ms) ? ask.Result : null;

    [Fact]
    public void WithPump_OneFocusScopeSpansEveryStep_TypeAheadSurvivesInComposer()
    {
        //one focus scope must span the whole wizard, or the composer pump takes the keys meant for the next question
        var src = new System.Collections.Concurrent.BlockingCollection<ConsoleKeyInfo>();
        var pump = new InputPump(new KeyInputSource(new FeedKeys(src)));
        pump.Start();
        src.Add(new ConsoleKeyInfo('t', ConsoleKey.T, false, false, false));   //a type-ahead key that must stay with the composer.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while ((src.Count > 0 || !pump.Composer.KeyAvailable) && sw.ElapsedMilliseconds < 30000) Thread.Sleep(5);
        Assert.True(pump.Composer.KeyAvailable);

        var surface = new SyncSurface();
        //the NeverKeys source throws on every read, so the keys can only come through the focus scope
        var p = new RichPrompter(surface, T, new NeverKeys(), pump);
        var ask = Task.Run(() => p.AskAsync(
            [Q("Lang", "Which?", false, "Go", "Rust"), Q("Build", "How?", false, "make")],
            CancellationToken.None));

        var render = System.Diagnostics.Stopwatch.StartNew();
        //the deadline only covers a busy thread pool, a longer one cannot hide a missing render, the assertion fails either way
        while (!surface.Text.Contains("Which?") && render.ElapsedMilliseconds < 30000) Thread.Sleep(5);
        Assert.Contains("Which?", surface.Text, StringComparison.Ordinal);

        src.Add(Digit('2'));
        src.Add(Digit('1'));
        src.Add(Enter());

        var answers = WaitForAnswers(ask);
        Assert.NotNull(answers);
        Assert.Equal(["Rust"], answers![0].Selected);
        Assert.Equal(["make"], answers[1].Selected);

        //the wizard must not consume the type-ahead key, which still waits for the composer.
        Assert.True(pump.Composer.KeyAvailable);
    }
}
