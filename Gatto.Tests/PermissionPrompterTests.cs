using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests;

//the panel answers on digits and arrows only, and commits nothing to the transcript. row probes compare ordinally, culture collation gives ESC zero weight
public class RichPermissionPrompterTests
{
    //a fake key source must throw when the script runs out, one that returns default keys forever hangs the runner
    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("test scripted too few keys");
    }

    //runs a probe before each ReadKey, the only way to see live panel state from a synchronous Ask
    private sealed class ProbingScriptedKeys(Action<int> probeBeforeRead, IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        private int _n;
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey()
        {
            probeBeforeRead(_n++);
            return _q.Count > 0 ? _q.Dequeue() : throw new InvalidOperationException("test scripted too few keys");
        }
    }

    private sealed class ThrowingKeys : IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => throw new InvalidOperationException("pump died");
    }

    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly char Esc = Convert.ToChar(0x1B);

    private static ConsoleKeyInfo K(char c) => new(c, ConsoleKey.Spacebar, false, false, false);
    private static ConsoleKeyInfo Special(ConsoleKey k) => new('\0', k, false, false, false);

    //a digit that selects and confirms in one press.
    private static ConsoleKeyInfo Digit(char c) => new(c, (ConsoleKey)((int)ConsoleKey.D1 + (c - '1')), false, false, false);

    private static ConsoleKeyInfo Enter => Special(ConsoleKey.Enter);
    private static ConsoleKeyInfo EscKey => Special(ConsoleKey.Escape);
    private static ConsoleKeyInfo Down => Special(ConsoleKey.DownArrow);

    private static (RecordingSurface Surface, RichPermissionPrompter Prompter) Build(params ConsoleKeyInfo[] keys)
    {
        var surface = new RecordingSurface { Width = 80 };
        var prompter = new RichPermissionPrompter(surface, T, new ScriptedKeys(keys));
        return (surface, prompter);
    }

    private static readonly PermissionRequest ShellRequest =
        new("shell", "git status --short", "git status");

    private static readonly PermissionRequest NoOfferRequest =
        new("frobnicate", "{\"x\":1}", null);

    private static readonly PermissionRequest WriteRequest =
        new("write_file", @"C:\proj\sub\a.txt (+2 lines)", @"C:\proj",
            PreviewLines: new[] { "one", "two" }, PreviewTotalLines: 2);

    private static readonly PermissionRequest CheckpointRequest =
        new("checkpoint", "M  a.cs\n?? b.cs", null);

    //display only, the stored grant and the gate match stay absolute. the option row and the gloss both read FormatOffer, so they can't drift

    [Fact]
    public void FormatOffer_WriteDirUnderTheProject_RendersProjectRelative()
    {
        var r = WriteRequest with { GrantOffer = @"C:\proj\sub" };
        Assert.Equal(@"proj\sub\*", PermissionPromptText.FormatOffer(r, cwd: @"C:\proj"));
    }

    [Fact]
    public void FormatOffer_WriteDirIsTheProjectItself_RendersTheProjectName()
    {
        var r = WriteRequest with { GrantOffer = @"C:\proj" };
        Assert.Equal(@"proj\*", PermissionPromptText.FormatOffer(r, cwd: @"C:\proj"));
    }

    [Fact]
    public void FormatOffer_OutsideTheProject_StaysAbsolute()
    {
        var r = WriteRequest with { GrantOffer = @"D:\other\place" };
        Assert.Equal(@"D:\other\place\*", PermissionPromptText.FormatOffer(r, cwd: @"C:\proj"));
    }

    [Fact]
    public void FormatOffer_ShellPrefix_IsUntouchedByTheCwd()
    {
        var r = ShellRequest with { GrantOffer = "git status" };
        Assert.Equal("\"git status\"", PermissionPromptText.FormatOffer(r, cwd: @"C:\proj"));
    }

    //the live chrome the app builds on the rich REPL path, so tests render what ships
    private static (RecordingSurface S, ChromePainter P, ChromeHandle H) Armed(int width = 80)
    {
        var surface = new RecordingSurface { Width = width, Height = 0 };
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

    private static RichPermissionPrompter Prompter(
        RecordingSurface s, ChromeHandle h, bool denyReason = false, string? provider = null,
        params ConsoleKeyInfo[] keys) =>
        new(s, T, new ScriptedKeys(keys), pump: null, chrome: h, denyReason: denyReason,
            webSearchProvider: provider);

    private static string[] Plain(IReadOnlyList<string>? rows) =>
        (rows ?? Array.Empty<string>()).Select(r => TermText.StripAnsiForWidth(r).TrimEnd()).ToArray();

    //the transcript rows the compositor would paint, so an empty result proves nothing was committed
    private static string[] Scrollback(ChromePainter p) =>
        p.Model.Items.SelectMany(i => i.Render(p.Width, T, glyphs: GlyphSet.Unicode))
            .Select(r => TermText.StripAnsiForWidth(r).TrimEnd()).ToArray();

    //the rows the inline path wrote, every row opens with a homing \r that survives the ANSI strip
    private static string[] InlineRows(RecordingSurface s) =>
        TermText.StripAnsiForWidth(s.Text).Split('\n').Select(r => r.Trim('\r').TrimEnd()).ToArray();

    //option 1 is Yes and the cursor starts there, so a bare Enter answers. an answered prompt leaves no panel rows and nothing in the transcript
    [Fact]
    public void Yes_ResolvesOnce_NoScrollbackCommit()
    {
        var (s, p, h) = Armed();
        var decision = Prompter(s, h, keys: Digit('1')).AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Once, decision.Answer);
        Assert.Null(decision.Reason);
        Assert.Null(p.State.PanelRows);
        Assert.Empty(Scrollback(p));
    }

    [Fact]
    public void Enter_ConfirmsTheCursor_WhichStartsOnYes()
    {
        var (s, p, h) = Armed();
        Assert.Equal(PermissionAnswer.Once, Prompter(s, h, keys: Enter).Ask(ShellRequest));
        Assert.Empty(Scrollback(p));
    }

    //option 2 is Always, and it shows only when the gate has a scope to offer
    [Fact]
    public void AlwaysOffer_RendersOption2_AndPersists()
    {
        var (s, p, h) = Armed();
        IReadOnlyList<string>? live = null;
        var prompter = new RichPermissionPrompter(s, T,
            new ProbingScriptedKeys(n => { if (n == 0) live = p.State.PanelRows; }, new[] { Digit('2') }),
            pump: null, chrome: h);

        Assert.Equal(PermissionAnswer.Always, prompter.Ask(ShellRequest));
        Assert.Contains(Plain(live), row => row == "  2. Yes, always allow \"git status\" on this project");
        Assert.Empty(Scrollback(p));
    }

    //with no offer the panel shows two options, so number 2 is No. the deny path picks the last option
    [Fact]
    public void NoOffer_RendersTwoOptions_AndNumberTwoDenies()
    {
        var (s, p, h) = Armed();
        IReadOnlyList<string>? live = null;
        var prompter = new RichPermissionPrompter(s, T,
            new ProbingScriptedKeys(n => { if (n == 0) live = p.State.PanelRows; }, new[] { Digit('2') }),
            pump: null, chrome: h);

        Assert.Equal(PermissionAnswer.Deny, prompter.Ask(NoOfferRequest));
        var rows = Plain(live);
        Assert.Contains(rows, row => row == "❯ 1. Yes");
        Assert.Contains(rows, row => row == "  2. No");
        Assert.DoesNotContain(rows, row => row.Contains("always allow", StringComparison.Ordinal));
    }

    [Fact]
    public void No_ResolvesDeny()
    {
        var (s, p, h) = Armed();
        var decision = Prompter(s, h, keys: Digit('3')).AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Null(decision.Reason);        //the denyReason flag defaults to off here, so the decision holds no reason
        Assert.Empty(Scrollback(p));
    }

    //an unrecognised outcome must deny and warn, ShowForTest fakes the ones the widget can't produce

    private static readonly SelectOutcome[] UnexpectedOutcomes =
    {
        new SelectOutcome.Moved(1, 0, Array.Empty<int>()),
        new SelectOutcome.FreeText("whatever"),
        new SelectOutcome.Multi(Array.Empty<int>()),
    };

    public static TheoryData<int> UnexpectedOutcomeIndices() => new() { 0, 1, 2 };

    [Theory]
    [MemberData(nameof(UnexpectedOutcomeIndices))]
    public void UnrecognisedOutcome_FailsClosed_AndSaysSoThroughTheWarningSink(int which)
    {
        var (s, _, h) = Armed();
        var warnings = new List<string>();
        var prompter = new RichPermissionPrompter(s, T, new ScriptedKeys(Array.Empty<ConsoleKeyInfo>()), pump: null, chrome: h,
            warn: new Gatto.Core.WarningSink(warnings.Add))
        {
            ShowForTest = _ => UnexpectedOutcomes[which],
        };

        var decision = prompter.AskWithReason(ShellRequest);

        //an unrecognised outcome must deny, Cancel would end the turn for something the user never did
        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        //the deny must also warn, a silent one looks the same as the user pressing No
        var message = Assert.Single(warnings);
        Assert.Contains(UnexpectedOutcomes[which].GetType().Name, message, StringComparison.Ordinal);
        Assert.Contains("denied", message, StringComparison.Ordinal);
    }

    //the ShellRequest offer gives three options at indices 0, 1 and 2, and those picks plus Esc must not warn
    private static readonly SelectOutcome[] RecognisedOutcomes =
    {
        new SelectOutcome.Chosen(0), new SelectOutcome.Chosen(1), new SelectOutcome.Chosen(2),
        new SelectOutcome.Cancelled(),
    };

    public static TheoryData<int> RecognisedOutcomeIndices() => new() { 0, 1, 2, 3 };

    [Theory]
    [MemberData(nameof(RecognisedOutcomeIndices))]
    public void RecognisedOutcomes_NeverWarn(int which)
    {
        //an ordinary outcome must not warn, or it buries the real one. keep this a Theory so a failure names the outcome
        var (s, _, h) = Armed();
        var warnings = new List<string>();
        var prompter = new RichPermissionPrompter(s, T, new ScriptedKeys(Array.Empty<ConsoleKeyInfo>()),
            pump: null, chrome: h, warn: new Gatto.Core.WarningSink(warnings.Add))
        {
            ShowForTest = _ => RecognisedOutcomes[which],
        };

        prompter.AskWithReason(ShellRequest);
        Assert.Empty(warnings);
    }

    [Fact]
    public void UnrecognisedOutcome_WithNoSink_StillFailsClosed()
    {
        //the sink is optional and null outside a session, and its absence must not change the deny
        var (s, _, h) = Armed();
        var prompter = new RichPermissionPrompter(s, T, new ScriptedKeys(Array.Empty<ConsoleKeyInfo>()), pump: null, chrome: h)
        {
            ShowForTest = _ => new SelectOutcome.Moved(-1, 0, Array.Empty<int>()),
        };

        Assert.Equal(PermissionAnswer.Deny, prompter.AskWithReason(ShellRequest).Answer);
    }

    //esc withdraws the question, and the gate renders Cancel as cancelled by user
    [Fact]
    public void Esc_ReturnsCancel()
    {
        var (s, p, h) = Armed();
        var decision = Prompter(s, h, keys: EscKey).AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Cancel, decision.Answer);
        Assert.Null(decision.Reason);
        Assert.Null(p.State.PanelRows);
        Assert.Empty(Scrollback(p));
    }

    //the y, a and n keys are inert and the panel keeps waiting, so only the Enter after them answers
    [Theory]
    [InlineData('y')]
    [InlineData('a')]
    [InlineData('n')]
    public void LettersAreDead(char letter)
    {
        var (s, p, h) = Armed();
        var decision = Prompter(s, h, keys: new[] { K(letter), Enter }).AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Once, decision.Answer);   //only Enter answered, so the letter changed nothing
    }

    //two Down presses reach the deny row, and Enter confirms there
    [Fact]
    public void ArrowsWalkTheOptions_AndEnterConfirmsWhereTheyStopped()
    {
        var (s, p, h) = Armed();
        Assert.Equal(PermissionAnswer.Deny,
            Prompter(s, h, keys: new[] { Down, Down, Enter }).Ask(ShellRequest));
    }

    //runs the live render path, prompt then result, so the result text never becomes a transcript row
    private static string[] AnswerThenResult(
        ChromeHandle h, ChromePainter p, RichPermissionPrompter prompter, PermissionRequest request,
        ToolResult result, out PermissionAnswer answer)
    {
        var call = new ToolCall("i", "shell", """{"command":"rm -rf /"}""");
        var r = h.Renderer!;
        r.BeginTurn();
        r.OnToolCallStart(call);
        answer = prompter.AskWithReason(request).Answer;
        Assert.Empty(Scrollback(p));       //answering commits nothing to the transcript on its own.
        r.OnToolResult(call, result);
        return Scrollback(p);
    }

    [Fact]
    public void DenyNotesTheOutcome_ResultRowIsDeniedGlyph()
    {
        var (s, p, h) = Armed();
        var rows = AnswerThenResult(h, p, Prompter(s, h, keys: Digit('3')), ShellRequest,
            new ToolResult("The user declined this tool call. Do not retry it — ask the user what to do instead.",
                IsError: true),
            out var answer);

        Assert.Equal(PermissionAnswer.Deny, answer);
        Assert.Contains("  ⎿ ✗ denied", rows);
        Assert.DoesNotContain(rows, row => row.Contains("declined", StringComparison.Ordinal));
    }

    //the deny reason shows on the result row, quoted from PrepareReason's model copy, so the user sees the model received it
    [Fact]
    public void DenyWithReason_ResultRowCarriesTheReason()
    {
        var (s, p, h) = Armed();
        var keys = new List<ConsoleKeyInfo> { Digit('3') };
        keys.AddRange("use read_file instead".Select(K));
        keys.Add(Enter);
        var rows = AnswerThenResult(h, p, Prompter(s, h, denyReason: true, keys: keys.ToArray()),
            ShellRequest,
            new ToolResult("blocked: The user declined this tool call: use read_file instead", IsError: true),
            out var answer);

        Assert.Equal(PermissionAnswer.Deny, answer);
        Assert.Contains("  ⎿ ✗ denied — \"use read_file instead\"", rows);
        //the gate's nudge prose stays out of the transcript.
        Assert.DoesNotContain(rows, row => row.Contains("declined", StringComparison.Ordinal));
    }

    [Fact]
    public void CancelNotesTheOutcome_ResultRowIsCancelled()
    {
        var (s, p, h) = Armed();
        var rows = AnswerThenResult(h, p, Prompter(s, h, keys: EscKey), ShellRequest,
            new ToolResult("cancelled by user", IsError: true), out var answer);

        Assert.Equal(PermissionAnswer.Cancel, answer);
        Assert.Contains("  ⎿ ✗ cancelled", rows);
    }

    //an Always answer runs the tool as usual, and an extra row names the scope just granted
    [Fact]
    public void AlwaysNotesTheOutcome_DimFollowUpLine_CarriesScope()
    {
        var (s, p, h) = Armed();
        var rows = AnswerThenResult(h, p, Prompter(s, h, keys: Digit('2')), WriteRequest,
            new ToolResult("", Gloss: "2 lines"), out var answer);

        Assert.Equal(PermissionAnswer.Always, answer);
        Assert.Contains(@"  auto-approved on this project: C:\proj\*", rows);
    }

    //a subagent shares the prompter but its inner call has no row, so its outcome must paint nothing
    [Theory]
    [InlineData('3')]   //the No case
    [InlineData('2')]   //the Always case
    public void SubagentPrompt_NotesNothing_TheOuterRunAgentRowIsUntouched(char answer)
    {
        var (s, p, h) = Armed();
        var r = h.Renderer!;
        var outer = new ToolCall("i", "run_agent", """{"agent":"code-reviewer"}""");
        r.BeginTurn();
        r.OnToolCallStart(outer);

        //the inner call runs through the same prompter, marked with its agent label.
        var decision = Prompter(s, h, keys: Digit(answer))
            .AskWithReason(ShellRequest with { Agent = "code-reviewer" });
        Assert.NotEqual(PermissionAnswer.Once, decision.Answer);   //the subagent prompt still answers, suppression only stops the paint

        r.OnToolResult(outer, new ToolResult(new string('x', 40), Gloss: "412 words"));

        var rows = Scrollback(p);
        Assert.Contains("  ⎿ ✓ 412 words · click to expand · ~10 tok", rows);
        Assert.DoesNotContain(rows, row => row.Contains("denied", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains("auto-approved", StringComparison.Ordinal));
        //the tint comes from the model's role that ToolBlockItem stamps, which differs from the renderer's role here
        var raw = p.Model.Items.SelectMany(i => i.Render(p.Width, T, glyphs: GlyphSet.Unicode)).ToArray();
        Assert.StartsWith(T.Paint(ItemRender.ToolMarkerOf(glyphs: GlyphSet.Unicode), T.RoleTint(p.Model.Role)), raw[0], StringComparison.Ordinal);
        Assert.DoesNotContain(T.Paint(ItemRender.ToolMarkerOf(glyphs: GlyphSet.Unicode), Theme.Err), raw[0], StringComparison.Ordinal);
    }

    //a top-level call must still note its outcome, or the suppression check would pass by noting nothing
    [Fact]
    public void TopLevelPrompt_StillNotesTheOutcome()
    {
        var (s, p, h) = Armed();
        var rows = AnswerThenResult(h, p, Prompter(s, h, keys: Digit('3')), ShellRequest,
            new ToolResult("declined", IsError: true), out _);

        Assert.Contains("  ⎿ ✗ denied", rows);
    }

    //a Yes answer leaves no mark, so the result row reads as an ungated call's row
    [Fact]
    public void YesNotesNothing_ResultRowIsUntouched()
    {
        var (s, p, h) = Armed();
        var rows = AnswerThenResult(h, p, Prompter(s, h, keys: Digit('1')), ShellRequest,
            new ToolResult("", Gloss: "exit 0"), out var answer);

        Assert.Equal(PermissionAnswer.Once, answer);
        Assert.Contains("  \u23bf \u2713 ran \u00b7 no output", rows);   //the shell block's words for exit 0 with empty output, the Yes answer still leaves no mark
        Assert.DoesNotContain(rows, row => row.Contains("auto-approved", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains("denied", StringComparison.Ordinal));
    }

    //probe the panel at every key read, an end-only check cannot tell a panel that was up throughout from one never shown
    [Fact]
    public void Armed_PromptLivesInThePanel()
    {
        var (s, p, h) = Armed();
        var panelSeen = false;
        var prompter = new RichPermissionPrompter(s, T,
            new ProbingScriptedKeys(_ => panelSeen |= p.State.PanelUp, new[] { Digit('1') }),
            pump: null, chrome: h);

        prompter.Ask(ShellRequest);

        Assert.True(panelSeen, "the prompt never reached ChromeState.PanelRows");
        Assert.Null(p.State.PanelRows);
    }

    //row order: title, detail block between two full-width rules, question, numbered options, a blank, footer
    [Fact]
    public void Armed_PanelRendersTitleQuestionOptionsDetailAndFooter()
    {
        var (s, p, h) = Armed();
        IReadOnlyList<string>? live = null;
        var prompter = new RichPermissionPrompter(s, T,
            new ProbingScriptedKeys(n => { if (n == 0) live = p.State.PanelRows; }, new[] { Digit('1') }),
            pump: null, chrome: h);

        prompter.Ask(WriteRequest);

        //the expected title keeps the full path, shortening reads the process working directory and the runner's one differs
        var rows = Plain(live);
        var rule = new string('-', s.Width);
        Assert.Equal(@"  Write new file C:\proj\sub\a.txt", rows[0]);
        Assert.Equal(rule, rows[1]);
        Assert.Equal("  1  one", rows[2]);
        Assert.Equal("  2  two", rows[3]);
        Assert.Equal(rule, rows[4]);
        Assert.Equal("  Do you want to write new file a.txt?", rows[5]);
        //one blank row must follow the question, as on every question screen
        Assert.Equal("", rows[6]);
        Assert.Equal("❯ 1. Yes", rows[7]);
        Assert.Equal(@"  2. Yes, always allow C:\proj\* on this project", rows[8]);
        Assert.Equal("  3. No", rows[9]);
        Assert.Equal("", rows[10]);
        Assert.Equal("  Esc to cancel", rows[^1]);
    }

    //the agent label must sit above the title, so the user sees who is asking before the question.
    [Fact]
    public void Subagent_LabelRendersAboveTitle()
    {
        var (s, p, h) = Armed();
        IReadOnlyList<string>? live = null;
        var prompter = new RichPermissionPrompter(s, T,
            new ProbingScriptedKeys(n => { if (n == 0) live = p.State.PanelRows; }, new[] { Digit('1') }),
            pump: null, chrome: h);

        prompter.Ask(ShellRequest with { Agent = "code-reviewer" });

        var rows = Plain(live);
        Assert.Equal("  [code-reviewer]", rows[0]);
        Assert.Equal("  shell command", rows[1]);
    }

    //a checkpoint request offers no scope, so it takes the generic path and the no-reason arm (CheckpointGate calls Ask)
    [Fact]
    public void Checkpoint_PromptRendersOnNewPanel()
    {
        var (s, p, h) = Armed();
        IReadOnlyList<string>? live = null;
        var prompter = new RichPermissionPrompter(s, T,
            new ProbingScriptedKeys(n => { if (n == 0) live = p.State.PanelRows; }, new[] { Digit('1') }),
            pump: null, chrome: h);

        Assert.Equal(PermissionAnswer.Once, prompter.Ask(CheckpointRequest));

        var rows = Plain(live);
        Assert.NotEmpty(rows);
        Assert.Equal("  checkpoint", rows[0]);
        Assert.Contains(rows, row => row == "  Do you want to proceed?");
        Assert.Contains(rows, row => row == "  M  a.cs");
        Assert.Contains(rows, row => row == "  ?? b.cs");
    }

    //an aborted key read must clear the panel, or it covers a composer the user can't see
    [Fact]
    public void Armed_AbortedKeyRead_ClearsThePanel()
    {
        var (s, p, h) = Armed();
        var prompter = new RichPermissionPrompter(s, T, new ThrowingKeys(), pump: null, chrome: h);

        Assert.Throws<InvalidOperationException>(() => prompter.Ask(ShellRequest));
        Assert.Null(p.State.PanelRows);
    }

    //a handle over a torn-down painter counts as unarmed, so the prompt writes inline
    [Fact]
    public void ArmedButTornDownPainter_FallsBackToInline()
    {
        var (s, p, h) = Armed();
        p.Teardown();
        s.Clear();

        var prompter = new RichPermissionPrompter(s, T, new ScriptedKeys(new[] { Digit('1') }),
            pump: null, chrome: h);
        Assert.Equal(PermissionAnswer.Once, prompter.Ask(ShellRequest));

        Assert.Contains(InlineRows(s), row => row == "  shell command");
        Assert.Contains(InlineRows(s), row => row == "❯ 1. Yes");
    }

    //with no ChromeHandle the widget's inline path must render the same block
    [Fact]
    public void Unarmed_RendersTheWidgetBlockInline()
    {
        var (s, prompter) = Build(Digit('3'));
        Assert.Equal(PermissionAnswer.Deny, prompter.Ask(ShellRequest));

        var rows = InlineRows(s);
        Assert.Contains(rows, row => row == "  shell command");
        Assert.Contains(rows, row => row == "  Do you want to proceed?");
        Assert.Contains(rows, row => row == "  3. No");
        Assert.Contains(rows, row => row == "  git status --short");
    }

    [Fact]
    public void HostileGrantOffer_EscByteIsStripped()
    {
        var (s, p, h) = Armed();
        IReadOnlyList<string>? live = null;
        var prompter = new RichPermissionPrompter(s, T,
            new ProbingScriptedKeys(n => { if (n == 0) live = p.State.PanelRows; }, new[] { Digit('1') }),
            pump: null, chrome: h);

        prompter.Ask(new PermissionRequest("shell", "x", Esc + "[31mrm"));

        var joined = string.Join("\n", live ?? Array.Empty<string>());
        Assert.DoesNotContain(Esc + "[31m", joined, StringComparison.Ordinal);   //the injected escape must not reach the panel as a live sequence.
        Assert.Contains("[31mrm", joined, StringComparison.Ordinal);             //the offered text must still appear, as inert literal text.
    }

    [Fact]
    public void HostileGrantOffer_NewlineIsStripped()
    {
        var (s, p, h) = Armed();
        IReadOnlyList<string>? live = null;
        var prompter = new RichPermissionPrompter(s, T,
            new ProbingScriptedKeys(n => { if (n == 0) live = p.State.PanelRows; }, new[] { Digit('1') }),
            pump: null, chrome: h);

        prompter.Ask(new PermissionRequest("shell", "x", "rm\n  allow? forged"));

        //stripping the newline stops a forged row, so the whole offer stays on one option row.
        Assert.Single(Plain(live), row => row.Contains("always allow", StringComparison.Ordinal));
        Assert.DoesNotContain(Plain(live), row => row.TrimStart() == "allow? forged");
    }

    [Fact]
    public void HostileToolNameAndSummary_RenderWithZeroLiveEscapes()
    {
        var (s, p, h) = Armed();
        IReadOnlyList<string>? live = null;
        var prompter = new RichPermissionPrompter(s, T,
            new ProbingScriptedKeys(n => { if (n == 0) live = p.State.PanelRows; }, new[] { Digit('1') }),
            pump: null, chrome: h);

        prompter.Ask(new PermissionRequest(Esc + "[31mevil", "a" + Esc + "[0m\rforged", null));

        //the ESC[0m run is the theme's own reset, request-supplied escapes must not survive and both injections show as inert text
        var joined = string.Join("\n", live ?? Array.Empty<string>());
        Assert.DoesNotContain(Esc + "[31m", joined, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', joined);
        Assert.Contains("[31mevil", joined, StringComparison.Ordinal);
        Assert.Contains("a[0mforged", joined, StringComparison.Ordinal);
    }

    //preview lines arrive raw from core, so the panel must render a CR and any escape inert
    [Fact]
    public void WritePreviewCarryingRawCrAndEsc_RendersInert()
    {
        var (s, p, h) = Armed();
        IReadOnlyList<string>? live = null;
        var prompter = new RichPermissionPrompter(s, T,
            new ProbingScriptedKeys(n => { if (n == 0) live = p.State.PanelRows; }, new[] { Digit('1') }),
            pump: null, chrome: h);

        prompter.Ask(WriteRequest with
        {
            PreviewLines = new[] { "hello\r", Esc + "[5mblink" },
            PreviewTotalLines = 2,
        });

        var joined = string.Join("\n", live ?? Array.Empty<string>());
        Assert.DoesNotContain('\r', joined);
        Assert.DoesNotContain(Esc + "[5m", joined, StringComparison.Ordinal);
        //strip the paint before comparing, a dim gutter sits between the number and the text
        var plainRows = string.Join("\n", (live ?? Array.Empty<string>()).Select(TermText.StripAnsiForWidth));
        Assert.Contains("1  hello", plainRows, StringComparison.Ordinal);
        Assert.Contains("2  [5mblink", plainRows, StringComparison.Ordinal);
    }

    //the command is what the user authorizes, so it must reach the panel whole, the binding pre-wraps it
    [Fact]
    public void Armed_HugeShellCommand_IsNeverElidedOrTruncated()
    {
        var (s, p, h) = Armed(width: 60);
        var command = "python -c " + string.Join(" ", Enumerable.Range(1, 40).Select(i => $"arg{i}"));
        IReadOnlyList<string>? live = null;
        var prompter = new RichPermissionPrompter(s, T,
            new ProbingScriptedKeys(n => { if (n == 0) live = p.State.PanelRows; }, new[] { Digit('1') }),
            pump: null, chrome: h);

        prompter.Ask(new PermissionRequest("shell", command, "python"));

        var rows = Plain(live);
        Assert.DoesNotContain(rows, row => row.Contains('…'));   //an ellipsis in a row would mean the widget or the binding cut the command.
        Assert.DoesNotContain(rows, row => row.Contains('⋯'));   //no elision marker may appear, the command must reach the panel whole
        Assert.Contains(rows, row => row.Contains("arg40", StringComparison.Ordinal));   //the last argument must still appear, so no row dropped the tail.
        foreach (var row in live!)
            Assert.True(UnicodeWidth.Of(TermText.StripAnsiForWidth(row)) <= 60,
                $"panel row overflows the terminal: '{row}'");
    }

    //a non-shell request past the cap does elide, keeping head and tail around a visible marker.
    [Fact]
    public void Armed_HugeNonShellContext_ElidesToHeadAndTail()
    {
        var (s, p, h) = Armed();
        s.Height = 60;   //a window tall enough that the short-window cut leaves the elided detail whole
        var summary = string.Join("\n", Enumerable.Range(1, 18).Select(i => $"line-{i}"));
        IReadOnlyList<string>? live = null;
        var prompter = new RichPermissionPrompter(s, T,
            new ProbingScriptedKeys(n => { if (n == 0) live = p.State.PanelRows; }, new[] { Digit('1') }),
            pump: null, chrome: h);

        prompter.Ask(new PermissionRequest("run_agent", summary, null));

        var rows = Plain(live);
        Assert.Contains(rows, row => row == "  ⋯ +5 lines ⋯");
        Assert.Contains(rows, row => row == "  line-10");
        Assert.Contains(rows, row => row == "  line-18");
        Assert.DoesNotContain(rows, row => row == "  line-11");
    }

    //the inline path re-reads the width each render, rows the resize left behind keep the old 80-wide frame
    [Fact]
    public void NoPainter_ResizeMidPrompt_RowsFit()
    {
        var surface = new RecordingSurface { Width = 80 };
        var command = "python -c " + string.Join(" ", Enumerable.Range(1, 30).Select(i => $"arg{i}"));
        var keys = new ProbingScriptedKeys(
            n => { if (n == 0) surface.Width = 34; },
            new[] { Down, Digit('1') });
        var prompter = new RichPermissionPrompter(surface, T, keys);

        Assert.Equal(PermissionAnswer.Once,
            prompter.Ask(new PermissionRequest("shell", command, "python")));

        var up = System.Text.RegularExpressions.Regex.Match(surface.Text, Esc + @"\[(\d+)A");
        Assert.True(up.Success, "no in-place repaint fired");
        //strip the \r opener from every row, a lone control byte counts as one cell in UnicodeWidth
        var afterResize = surface.Text[(up.Index + up.Length)..];
        foreach (var raw in afterResize.Split('\n'))
        {
            var row = Gatto.Tests.Fakes.TerminalReplay.StripRowOpener(raw);
            var width = UnicodeWidth.Of(TermText.StripAnsiForWidth(row));
            Assert.True(width <= 34, $"row overflows the resized surface: width={width}, max=34 — '{row}'");
        }
    }

    //the reason capture is the whole panel, the options are resolved by then
    [Fact]
    public void No_ResolvesDeny_ReasonCaptureInsidePanel()
    {
        var (s, p, h) = Armed();
        IReadOnlyList<string>? duringCapture = null;
        var keys = new ProbingScriptedKeys(
            n => { if (n == 1) duringCapture = p.State.PanelRows; },
            new[] { Digit('3'), K('n'), K('o'), K('p'), K('e'), Enter });
        var prompter = new RichPermissionPrompter(s, T, keys, pump: null, chrome: h, denyReason: true);

        var decision = prompter.AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Equal("nope", decision.Reason);
        Assert.NotNull(duringCapture);
        Assert.Contains(Plain(duringCapture), row => row.Contains(ReasonPrefixText.TrimEnd(), StringComparison.Ordinal));
        //the capture panel holds no title, no options and no footer.
        Assert.DoesNotContain(Plain(duringCapture), row => row.Contains("Yes", StringComparison.Ordinal));
        Assert.DoesNotContain(Plain(duringCapture), row => row.Contains("Esc to cancel", StringComparison.Ordinal));
        Assert.Null(p.State.PanelRows);   //once the reason is captured the panel must be gone.
        Assert.Empty(Scrollback(p));      //the reason reaches the model through the gate, so nothing enters the transcript.
    }

    //a plain Enter submits with no reason, so the fast deny path stays fast
    [Fact]
    public void AskWithReason_PlainEnter_DeniesWithNullReason()
    {
        var (s, p, h) = Armed();
        var decision = Prompter(s, h, denyReason: true, keys: new[] { Digit('3'), Enter })
            .AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Null(decision.Reason);
    }

    //esc inside the reason editor keeps the deny, a cancel there would tell the model something false
    [Fact]
    public void AskWithReason_EscapeInsideTheReasonLine_SkipsTheReason_ButKeepsTheDeny()
    {
        var (s, p, h) = Armed();
        var decision = Prompter(s, h, denyReason: true, keys: new[] { Digit('3'), K('x'), EscKey })
            .AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Null(decision.Reason);
        Assert.Null(p.State.PanelRows);
    }

    //a dead pump during the reason line must throw OperationCanceledException
    [Fact]
    public void AskWithReason_DeadPumpDuringTheReasonLine_RaisesTheHistoricalCancellation()
    {
        var (s, p, h) = Armed();
        var keys = new ProbingScriptedKeys(
            n => { if (n >= 1) throw new InvalidOperationException("pump died"); },
            new[] { Digit('3') });
        var prompter = new RichPermissionPrompter(s, T, keys, pump: null, chrome: h, denyReason: true);

        Assert.Throws<OperationCanceledException>(() => prompter.AskWithReason(ShellRequest));
        Assert.Null(p.State.PanelRows);
    }

    [Fact]
    public void AskWithReason_Once_NeverPromptsForAReason()
    {
        var (s, p, h) = Armed();
        //only one key is scripted, and the fake key source throws when it runs dry, so a second read fails the test.
        Assert.Equal(PermissionAnswer.Once,
            Prompter(s, h, denyReason: true, keys: Digit('1')).AskWithReason(ShellRequest).Answer);
    }

    [Fact]
    public void DenyReasonOff_IsTheDefault_AndConsumesNoExtraKey()
    {
        var (s, p, h) = Armed();
        Assert.Equal(PermissionAnswer.Deny,
            Prompter(s, h, keys: Digit('3')).AskWithReason(ShellRequest).Answer);
    }

    //the Ask path never reaches the reason step, whatever the switch says, and CheckpointGate discards a reason anyway
    [Fact]
    public void Ask_StillDeniesWithZeroExtraKeystrokes_EvenWithDenyReasonOn()
    {
        var (s, p, h) = Armed();
        Assert.Equal(PermissionAnswer.Deny, Prompter(s, h, denyReason: true, keys: Digit('3')).Ask(ShellRequest));
    }

    //unarmed there is no panel, so the reason line reads from the surface inline.
    [Fact]
    public void Unarmed_AskWithReason_ReadsTheReasonInline()
    {
        var surface = new RecordingSurface { Width = 80 };
        var prompter = new RichPermissionPrompter(surface, T,
            new ScriptedKeys(new[] { Digit('3'), K('h'), K('i'), Enter }), denyReason: true);

        var decision = prompter.AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Equal("hi", decision.Reason);
        Assert.Contains(ReasonPrefixText, surface.Text, StringComparison.Ordinal);
    }

    //the inline path writes its notices to the surface, so a test reads them there. one PrepareReason call gives both the model's copy and the truncation verdict

    //digit 3 is No only when the request offers a scope, so callers pass just the reason keys
    private static (RecordingSurface Surface, RichPermissionPrompter Prompter) InlineReason(
        IEnumerable<ConsoleKeyInfo> reasonKeys)
    {
        var surface = new RecordingSurface { Width = 80 };
        var keys = new List<ConsoleKeyInfo> { Digit('3') };
        keys.AddRange(reasonKeys);
        keys.Add(Enter);
        return (surface, new RichPermissionPrompter(surface, T, new ScriptedKeys(keys), denyReason: true));
    }

    //each attempt ends with Enter, the shape a re-prompt needs, and one attempt covers the ordinary case
    private static (RecordingSurface Surface, RichPermissionPrompter Prompter) InlineAttempts(
        params string[] attempts)
    {
        var surface = new RecordingSurface { Width = 80 };
        var keys = new List<ConsoleKeyInfo> { Digit('3') };
        foreach (var attempt in attempts) { keys.AddRange(Typed(attempt)); keys.Add(Enter); }
        return (surface, new RichPermissionPrompter(surface, T, new ScriptedKeys(keys), denyReason: true));
    }

    //a stop during the inline reason read ends it, the read takes the turn's token like the menu does
    [Fact]
    public void A_stop_during_the_inline_reason_read_ends_it()
    {
        using var cts = new CancellationTokenSource();
        var keys = new StopOnSecondRead(cts, Digit('3'), K('x'), Enter);
        var prompter = new RichPermissionPrompter(new RecordingSurface { Width = 80 }, T, keys, denyReason: true,
            abort: new TurnAbortHandle { Current = () => cts });

        Assert.Throws<OperationCanceledException>(() => prompter.AskWithReason(ShellRequest));
    }

    //the second key read stops the turn, the moment a ctrl+break lands while the reason is being typed
    private sealed class StopOnSecondRead(CancellationTokenSource cts, params ConsoleKeyInfo[] keys) : IKeySource
    {
        private int _reads;
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey()
        {
            if (++_reads == 2) cts.Cancel();
            return keys[_reads - 1];
        }
    }

    private static IEnumerable<ConsoleKeyInfo> Typed(string s) => s.Select(K);

    //the wording is duplicated here as the oracle, and the cap comes from MaxReasonLength so the two can't differ
    private static string ReasonPrefixText =>
        $"reason (optional, ≤{PermissionGate.MaxReasonLength} chars, Enter to skip): ";

    //the core filter drops the tabs, so the 200 x's reach the model whole and no truncation notice appears
    private static string TabPaddedReason => new string('x', 200) + new string('\t', 60);

    //the cap falls inside the leading blanks, so the cut is real and nothing survives it
    private static string CapSwallowedReason =>
        new string(' ', PermissionGate.MaxReasonLength) + "real text";

    [Fact]
    public void AskWithReason_ReasonOverTheModelCap_ShowsATruncationNotice()
    {
        //read the cap from MaxReasonLength, so the test fails when the notice and the cap drift apart
        var longReason = new string('x', PermissionGate.MaxReasonLength + 44);
        var (surface, prompter) = InlineReason(Typed(longReason));

        var decision = prompter.AskWithReason(ShellRequest);

        //the prompter passes the reason through, PermissionGate caps it on the way to the model
        Assert.Equal(longReason, decision.Reason);
        //the echo shows the full raw text, so a notice naming the real cap must follow
        Assert.Contains("  " + ReasonPrefixText + longReason + "\n", surface.Text,
            StringComparison.Ordinal);
        Assert.Contains("  (only the first " + PermissionGate.MaxReasonLength + " chars reach the model)\n",
            surface.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AskWithReason_TabPaddedReasonTheModelGetsInFull_ShowsNoTruncationNotice()
    {
        var (surface, prompter) = InlineReason(Typed(TabPaddedReason));

        var decision = prompter.AskWithReason(ShellRequest);

        Assert.Equal(TabPaddedReason, decision.Reason);
        //one call builds the model's copy and the notice, so asserting both pins agreement by construction
        var model = PermissionGate.PrepareReason(decision.Reason);
        Assert.Equal(new string('x', 200), model.Text);
        Assert.False(model.Truncated);
        Assert.DoesNotContain("chars reach the model", surface.Text, StringComparison.Ordinal);
    }

    //the notice reports what PrepareReason decided, so a character the two sanitizers treat differently cannot move it
    [Theory]
    [InlineData("use\tread_file\tinstead")]      //the tab case, the display expands it and core drops it
    [InlineData("no\u2028thanks\u2029really")]   //the line separator case, which both filters keep.
    [InlineData("\u202Edangerous\u202C path")]   //the bidi override case, which both filters keep.
    [InlineData("stop\u0085that\u0090now")]      //the C1 control case, which both filters drop.
    [InlineData("  padded  ")]                   //the padded case, which only the core filter trims.
    public void AskWithReason_UnderCapReason_NeverShowsATruncationNotice(string reason)
    {
        var (surface, prompter) = InlineReason(Typed(reason));

        var decision = prompter.AskWithReason(ShellRequest);

        Assert.False(PermissionGate.PrepareReason(decision.Reason).Truncated);   //the model receives the reason whole, so the notice is absent.
        Assert.DoesNotContain("chars reach the model", surface.Text, StringComparison.Ordinal);
    }

    //a reason that reaches the model as nothing re-prompts, empty Enter still skips and a truncated reason still closes

    //the reason reaches the model as none of it, so the notice shows and the returned reason proves a re-prompt
    [Fact]
    public void AskWithReason_ReasonThatWouldReachTheModelAsNothing_RepromptsRatherThanClosing()
    {
        Assert.True(PermissionGate.PrepareReason("\t\t").LostEntirely);   //the fixture must lose the whole reason, otherwise the flow below proves nothing
        var (surface, prompter) = InlineAttempts("\t\t", "use read_file instead");

        var decision = prompter.AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Equal("use read_file instead", decision.Reason);   //the second attempt must be the answer that reaches the gate.
        Assert.Contains("  " + RichPermissionPrompter.NothingReachedModel + "\n", surface.Text,
            StringComparison.Ordinal);
        //nothing was cut, so the cap notice must not show
        Assert.DoesNotContain("chars reach the model", surface.Text, StringComparison.Ordinal);
    }

    //when the cap leaves nothing, the prompt re-asks and shows only the nothing-reached notice
    [Fact]
    public void AskWithReason_CapSwallowsTheWholeReason_Reprompts_AndNeverShowsTheCapNotice()
    {
        var swallowed = PermissionGate.PrepareReason(CapSwallowedReason);
        Assert.Null(swallowed.Text);          //a null text means the model would get only the bare deny nudge.
        Assert.True(swallowed.Truncated);     //the real text sits past the cap, so the cut is real
        Assert.True(swallowed.LostEntirely);  //nothing survived the cut, so both flags hold at once

        var (surface, prompter) = InlineAttempts(CapSwallowedReason, "ok");

        var decision = prompter.AskWithReason(ShellRequest);

        Assert.Equal("ok", decision.Reason);
        Assert.Contains("  " + RichPermissionPrompter.NothingReachedModel + "\n", surface.Text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("chars reach the model", surface.Text, StringComparison.Ordinal);
    }

    //the prompt names the model's cap so the user knows the limit, and no keystroke cap exists
    [Fact]
    public void ReasonPrompt_NamesTheModelCap()
    {
        var (surface, prompter) = InlineAttempts("hi");
        prompter.AskWithReason(ShellRequest);

        Assert.Contains(ReasonPrefixText, surface.Text, StringComparison.Ordinal);
        Assert.Contains(PermissionGate.MaxReasonLength.ToString(), ReasonPrefixText, StringComparison.Ordinal);
    }

    //the model gets most of the reason, so a cut reason closes with its notice. one scripted attempt fails loudly on a re-prompt
    [Fact]
    public void AskWithReason_TruncatedButSurvivingReason_ClosesWithItsNotice_NeverReprompts()
    {
        var longReason = new string('x', PermissionGate.MaxReasonLength + 44);
        var model = PermissionGate.PrepareReason(longReason);
        Assert.True(model.Truncated);
        Assert.False(model.LostEntirely);   //content survived the cut, which separates it from the lost case

        var (surface, prompter) = InlineAttempts(longReason);

        var decision = prompter.AskWithReason(ShellRequest);

        Assert.Equal(longReason, decision.Reason);
        Assert.Contains("  (only the first " + PermissionGate.MaxReasonLength + " chars reach the model)\n",
            surface.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(RichPermissionPrompter.NothingReachedModel, surface.Text, StringComparison.Ordinal);
    }

    //the inline branch needs its own test, an armed twin cannot catch a deleted inline skip
    [Fact]
    public void Unarmed_AskWithReason_PlainEnter_SkipsTheReason()
    {
        var (_, prompter) = InlineAttempts("");

        var decision = prompter.AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Null(decision.Reason);
    }

    //a key source that runs dry during the re-prompt must raise the dead-pump cancellation, and only the inline branch tests it
    [Fact]
    public void Unarmed_AskWithReason_DeadSourceDuringTheReprompt_RaisesTheHistoricalCancellation()
    {
        var (_, prompter) = InlineAttempts("\t\t");   //the single attempt is lost, so the re-prompt finds no keys left.

        Assert.Throws<OperationCanceledException>(() => prompter.AskWithReason(ShellRequest));
    }

    //check the panel at the second attempt's first read, a notice anywhere would satisfy a weaker check
    [Fact]
    public void AskWithReason_Armed_ReasonThatWouldReachTheModelAsNothing_KeepsThePanelOpen()
    {
        var (s, p, h) = Armed();
        IReadOnlyList<string>? panelAtReprompt = null;
        //read 0 is the option digit, reads 1 to 3 are the first attempt, and read 4 begins the second.
        var keys = new ProbingScriptedKeys(
            n => { if (n == 4) panelAtReprompt = p.State.PanelRows; },
            new[] { Digit('3'), K('\t'), K('\t'), Enter, K('o'), K('k'), Enter });
        var prompter = new RichPermissionPrompter(s, T, keys, pump: null, chrome: h, denyReason: true);

        var decision = prompter.AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Equal("ok", decision.Reason);                    //only the second attempt can produce this reason.
        Assert.NotNull(panelAtReprompt);                        //the panel stayed open into the second attempt.
        var rows = Plain(panelAtReprompt);
        Assert.Contains(rows, row => row.Contains(RichPermissionPrompter.NothingReachedModel, StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Contains(ReasonPrefixText.TrimEnd(), StringComparison.Ordinal));   //the reason prefix must be in those rows, so the user can answer again.
        Assert.Null(p.State.PanelRows);                         //the panel is gone once the answer is final.
        Assert.Empty(Scrollback(p));                            //the re-prompt commits nothing to the transcript either.
    }

    //the armed twin of the inline test. no truncation notice appears, the prefix already names the cap
    [Fact]
    public void AskWithReason_Armed_TruncatedButSurvivingReason_ClosesWithoutReprompting()
    {
        var longReason = new string('x', PermissionGate.MaxReasonLength + 44);
        var model = PermissionGate.PrepareReason(longReason);
        Assert.True(model.Truncated);
        Assert.False(model.LostEntirely);   //content survived the cut, which is what separates this shape from the lost case

        var (s, p, h) = Armed();
        var keys = new List<ConsoleKeyInfo> { Digit('3') };
        keys.AddRange(Typed(longReason));
        keys.Add(Enter);
        var prompter = new RichPermissionPrompter(s, T, new ScriptedKeys(keys), pump: null, chrome: h,
            denyReason: true);

        var decision = prompter.AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Equal(longReason, decision.Reason);
        Assert.Null(p.State.PanelRows);
        Assert.Empty(Scrollback(p));
    }

    //esc during the re-prompt skips the reason and keeps the deny, so the user is never trapped by the loop
    [Fact]
    public void AskWithReason_Armed_EscDuringTheReprompt_SkipsTheReason_AndKeepsTheDeny()
    {
        var (s, p, h) = Armed();
        var decision = Prompter(s, h, denyReason: true,
                keys: new[] { Digit('3'), K('\t'), Enter, EscKey })
            .AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Null(decision.Reason);
        Assert.Null(p.State.PanelRows);
    }

    //a plain Enter is still a skip inside the re-prompt, or the loop would make silence unreachable.
    [Fact]
    public void AskWithReason_Armed_EmptyEnterDuringTheReprompt_StillSkips()
    {
        var (s, p, h) = Armed();
        var decision = Prompter(s, h, denyReason: true,
                keys: new[] { Digit('3'), K('\t'), Enter, Enter })
            .AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Null(decision.Reason);
        Assert.Null(p.State.PanelRows);
    }

    //the re-prompt must not spin on a source that can no longer answer, so this test pins the exit
    [Fact]
    public void AskWithReason_Armed_DeadPumpDuringTheReprompt_RaisesTheHistoricalCancellation()
    {
        var (s, p, h) = Armed();
        var keys = new ProbingScriptedKeys(
            n => { if (n >= 4) throw new InvalidOperationException("pump died"); },
            new[] { Digit('3'), K('\t'), K('\t'), Enter });
        var prompter = new RichPermissionPrompter(s, T, keys, pump: null, chrome: h, denyReason: true);

        Assert.Throws<OperationCanceledException>(() => prompter.AskWithReason(ShellRequest));
        Assert.Null(p.State.PanelRows);
    }

    //the provider name comes in through the constructor, as the app wires it, and a null one gives the bare title
    [Fact]
    public void WebSearchProvider_ReachesTheTitle()
    {
        var (s, p, h) = Armed();
        IReadOnlyList<string>? live = null;
        var prompter = new RichPermissionPrompter(s, T,
            new ProbingScriptedKeys(n => { if (n == 0) live = p.State.PanelRows; }, new[] { Digit('1') }),
            pump: null, chrome: h, denyReason: false, webSearchProvider: "tavily");

        prompter.Ask(new PermissionRequest("web_search", """{"query":"gatto","count":3}""",
            "web_search"));

        Assert.Equal("  Web search using tavily", Plain(live)[0]);
        Assert.Contains(Plain(live), row => row == "  \"gatto\" · 3 results");
    }

    //esc ends the turn as well as the call, through two seams, so each half needs its own oracle and negative twin

    //a session must replay, an unpaired tool call, a misplaced answer or an empty assistant record is a permanent 400
    private static void AssertReplayable(Conversation convo)
    {
        var msgs = convo.Messages.ToList();
        var answers = msgs.Select((m, i) => (m, i))
            .Where(x => x.m.Role == "tool" && x.m.ToolCallId is not null)
            .ToList();

        foreach (var (msg, callIndex) in msgs.Select((m, i) => (m, i)).Where(x => x.m.Role == "assistant"))
            foreach (var call in msg.ToolCalls ?? (IReadOnlyList<ToolCall>)Array.Empty<ToolCall>())
            {
                var matches = answers.Where(a => a.m.ToolCallId == call.Id).ToList();
                Assert.True(matches.Count == 1,
                    $"call {call.Id} has {matches.Count} matching tool records — a chat template 400s on either count");
                Assert.True(matches[0].i > callIndex,
                    $"call {call.Id} is answered at index {matches[0].i}, BEFORE the assistant record at {callIndex}");
            }

        var requested = msgs.Where(m => m.Role == "assistant")
            .SelectMany(m => m.ToolCalls ?? (IReadOnlyList<ToolCall>)Array.Empty<ToolCall>())
            .Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var (msg, i) in answers)
            Assert.True(requested.Contains(msg.ToolCallId!),
                $"tool record at index {i} answers {msg.ToolCallId}, which no assistant record ever requested");

        Assert.DoesNotContain(msgs, m => m.Role == "assistant"
            && string.IsNullOrEmpty(m.Content)
            && (m.ToolCalls is null || m.ToolCalls.Count == 0));
    }

    //a real tool name, so the gate classifies this spy as production does, and no expectation in the name
    private sealed class ExecutionSpyTool(string name) : Gatto.Core.Tools.ITool
    {
        public bool Executed { get; private set; }
        public string Name => name;
        public string Description => "records whether it executed";
        public System.Text.Json.JsonElement ParametersSchema =>
            System.Text.Json.JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public Task<ToolResult> ExecuteAsync(System.Text.Json.JsonElement args, Gatto.Core.Tools.IToolContext ctx, CancellationToken ct)
        {
            Executed = true;
            return Task.FromResult(new ToolResult("ran"));
        }
    }

    //concatenate these frames, the delta shape ends in runs of braces that no raw string literal parses cleanly
    private static string ToolCallFrame(int index, string id, string name, string argsJson) =>
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"tool_calls\":[{\"index\":" + index
        + ",\"id\":\"" + id + "\",\"function\":{\"name\":\"" + name + "\",\"arguments\":"
        + System.Text.Json.JsonSerializer.Serialize(argsJson) + "}}]},\"finish_reason\":null}]}\n\n";

    private static string FinishFrame(string reason) =>
        "data: {\"choices\":[{\"index\":0,\"delta\":{},\"finish_reason\":\"" + reason + "\"}]}\n\n";

    private static string TextFrame(string text) =>
        "data: {\"choices\":[{\"index\":0,\"delta\":{\"content\":"
        + System.Text.Json.JsonSerializer.Serialize(text) + "},\"finish_reason\":null}]}\n\n";

    //drive the real gate, the loop and a real server: esc must end the turn with a replayable history, and the count comes from RequestCount
    [Fact]
    public async Task EscCancel_HistoryWellFormed_TurnEnds()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            ToolCallFrame(0, "t1", "write_file", """{"path":"a.txt","content":"x"}"""),
            FinishFrame("tool_calls"),
            "data: [DONE]\n\n",
        }));

        using var cts = new CancellationTokenSource();
        var abort = new TurnAbortHandle { Current = () => cts };
        var (s, _, h) = Armed();
        var prompter = new RichPermissionPrompter(s, T, new ScriptedKeys(new[] { EscKey }),
            pump: null, chrome: h, denyReason: false, webSearchProvider: null, abort: abort);

        var root = Directory.CreateTempSubdirectory("gatto-r1-").FullName;
        try
        {
            var spy = new ExecutionSpyTool("write_file");
            var (loop, convo, obs) = LoopOver(server, root, prompter, spy);

            var outcome = await loop.RunTurnAsync(convo, "go", obs, cts.Token);

            //esc ends the whole turn, the call included
            Assert.Equal(TurnOutcome.Cancelled, outcome.Outcome);
            //one request only, so the model cannot re-ask for a withdrawn call.
            Assert.Equal(1, server.RequestCount);
            //the cancelled call must sit in the history with an answer that repeats the gate's cancel message.
            var (_, result) = Assert.Single(obs.Results);
            Assert.True(result.IsError);
            Assert.Equal("blocked: " + PermissionGate.CancelMessage, result.Text);
            var answer = Assert.Single(convo.Messages, m => m.Role == "tool");
            Assert.Equal("t1", answer.ToolCallId);
            Assert.Equal("blocked: " + PermissionGate.CancelMessage, answer.Content);
            AssertReplayable(convo);
            Assert.False(spy.Executed);
            Assert.True(cts.IsCancellationRequested);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    //with no handle wired the same esc blocks the call and the turn runs on, so the handle is the whole difference
    [Fact]
    public async Task EscCancel_WithoutTheHandle_BlocksTheCallButTheTurnRunsOn()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            ToolCallFrame(0, "t1", "write_file", """{"path":"a.txt","content":"x"}"""),
            FinishFrame("tool_calls"),
            "data: [DONE]\n\n",
        }));
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            TextFrame("fine"),
            FinishFrame("stop"),
            "data: [DONE]\n\n",
        }));

        using var cts = new CancellationTokenSource();
        var (s, _, h) = Armed();
        var prompter = new RichPermissionPrompter(s, T, new ScriptedKeys(new[] { EscKey }),
            pump: null, chrome: h);   //no abort handle is wired, so only the call is cancelled.

        var root = Directory.CreateTempSubdirectory("gatto-r1-").FullName;
        try
        {
            var spy = new ExecutionSpyTool("write_file");
            var (loop, convo, obs) = LoopOver(server, root, prompter, spy);

            var outcome = await loop.RunTurnAsync(convo, "go", obs, cts.Token);

            Assert.NotEqual(TurnOutcome.Cancelled, outcome.Outcome);
            Assert.Equal(2, server.RequestCount);                      //the loop asks the model a second time.
            Assert.False(cts.IsCancellationRequested);
            //assert the exact cancel message, a bare IsError check passes on any error text and on a null reference too
            Assert.Equal("blocked: " + PermissionGate.CancelMessage,
                Assert.Single(obs.Results).Item2.Text);
            AssertReplayable(convo);
            Assert.False(spy.Executed);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    //with the handle wired, a Yes answer must abort nothing, and an implementation that aborts on every prompt would pass every other test
    [Fact]
    public async Task Answering_Yes_NeverAborts_EvenWithTheHandleWired()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            ToolCallFrame(0, "t1", "frobnicate", "{}"),
            FinishFrame("tool_calls"),
            "data: [DONE]\n\n",
        }));
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            TextFrame("done"),
            FinishFrame("stop"),
            "data: [DONE]\n\n",
        }));

        using var cts = new CancellationTokenSource();
        var abort = new TurnAbortHandle { Current = () => cts };
        var (s, _, h) = Armed();
        var prompter = new RichPermissionPrompter(s, T, new ScriptedKeys(new[] { Digit('1') }),
            pump: null, chrome: h, denyReason: false, webSearchProvider: null, abort: abort);

        var root = Directory.CreateTempSubdirectory("gatto-r1-").FullName;
        try
        {
            var spy = new ExecutionSpyTool("frobnicate");
            var (loop, convo, obs) = LoopOver(server, root, prompter, spy);

            var outcome = await loop.RunTurnAsync(convo, "go", obs, cts.Token);

            Assert.False(cts.IsCancellationRequested);
            Assert.Equal(TurnOutcome.Completed, outcome.Outcome);
            Assert.True(spy.Executed);
            AssertReplayable(convo);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    //a deny must not abort the turn, the nudge asks the model to keep going in another way
    [Fact]
    public async Task Denying_NeverAborts_EvenWithTheHandleWired()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            ToolCallFrame(0, "t1", "frobnicate", "{}"),
            FinishFrame("tool_calls"),
            "data: [DONE]\n\n",
        }));
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            TextFrame("ok"),
            FinishFrame("stop"),
            "data: [DONE]\n\n",
        }));

        using var cts = new CancellationTokenSource();
        var abort = new TurnAbortHandle { Current = () => cts };
        var (s, _, h) = Armed();
        //the frobnicate tool offers its own name, so digit 3 answers No
        var prompter = new RichPermissionPrompter(s, T, new ScriptedKeys(new[] { Digit('3') }),
            pump: null, chrome: h, denyReason: false, webSearchProvider: null, abort: abort);

        var root = Directory.CreateTempSubdirectory("gatto-r1-").FullName;
        try
        {
            var spy = new ExecutionSpyTool("frobnicate");
            var (loop, convo, obs) = LoopOver(server, root, prompter, spy);

            var outcome = await loop.RunTurnAsync(convo, "go", obs, cts.Token);

            Assert.False(cts.IsCancellationRequested);
            Assert.Equal(TurnOutcome.Completed, outcome.Outcome);
            Assert.Equal(2, server.RequestCount);
            Assert.False(spy.Executed);
            Assert.Contains(PermissionGate.DenyNudge, Assert.Single(obs.Results).Item2.Text, StringComparison.Ordinal);
            AssertReplayable(convo);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    //ctrl+break cancels the turn while the prompt waits on a key nobody types: the prompt ends, the call is answered and never runs
    [Fact]
    public async Task CtrlBreak_WhileAPermissionPromptWaits_EndsThePromptAndTheTurn()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            ToolCallFrame(0, "t1", "write_file", """{"path":"a.txt","content":"x"}"""),
            FinishFrame("tool_calls"),
            "data: [DONE]\n\n",
        }));

        using var cts = new CancellationTokenSource();
        var abort = new TurnAbortHandle { Current = () => cts };
        var src = new System.Collections.Concurrent.BlockingCollection<ConsoleKeyInfo>();
        var pump = new InputPump(new KeyInputSource(new FeedKeys(src)));
        pump.Start();
        var surface = new SyncSurface();
        var prompter = new RichPermissionPrompter(surface, T, new NeverKeys(), pump, abort: abort);

        var root = Directory.CreateTempSubdirectory("gatto-u0352-").FullName;
        try
        {
            var spy = new ExecutionSpyTool("write_file");
            var (loop, convo, obs) = LoopOver(server, root, prompter, spy);
            var turn = loop.RunTurnAsync(convo, "go", obs, cts.Token);

            //wait for the prompt to paint, which is the event that proves the read is blocked on a key.
            var painted = System.Diagnostics.Stopwatch.StartNew();
            while (!surface.Text.Contains("Esc to cancel", StringComparison.Ordinal) && painted.ElapsedMilliseconds < 30000) await Task.Delay(5);
            Assert.Contains("Esc to cancel", surface.Text, StringComparison.Ordinal);

            cts.Cancel();
            Assert.True(await Task.WhenAny(turn, Task.Delay(10_000)) == turn, "the turn did not end after its token was cancelled while the prompt waited for a key");

            var outcome = await turn;
            Assert.Equal(TurnOutcome.Cancelled, outcome.Outcome);
            var answer = Assert.Single(convo.Messages, m => m.Role == "tool");
            Assert.Equal("t1", answer.ToolCallId);
            Assert.Equal("cancelled", answer.Content);
            AssertReplayable(convo);
            Assert.False(spy.Executed);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    //build the shared rig from the production composition, CheckAsync on the tool-call hook and nothing stubbed between the answer and the record
    private static (AgentLoop Loop, Conversation Convo, RecordingObserver Obs) LoopOver(
        FakeOpenAiServer server, string root, IPermissionPrompter prompter, Gatto.Core.Tools.ITool tool)
    {
        var store = PermissionStore.Load(root, out _);
        var hooks = new HookBus();
        hooks.On(HookEvent.ToolCall, new PermissionGate(store, prompter, autoYes: false).CheckAsync);
        var reg = new Gatto.Core.Tools.ToolRegistry();
        reg.Register(tool);
        var client = new OpenAiCompatClient(new HttpClient(), "local", new EndpointConfig(server.BaseUrl));
        var loop = new AgentLoop(client, reg, hooks, new TestToolContext(root), "m");
        return (loop, new Conversation(null), new RecordingObserver());
    }


    //an abort between parallel calls must answer every call, or later requests fail on the history, and its twin pins the other face
    [Fact]
    public async Task EscCancel_WithParallelCalls_AnswersEveryCall_AndTheHistoryStillReplays()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            ToolCallFrame(0, "t1", "write_file", """{"path":"a.txt","content":"x"}"""),
            ToolCallFrame(1, "t2", "write_file", """{"path":"b.txt","content":"y"}"""),
            FinishFrame("tool_calls"),
            "data: [DONE]\n\n",
        }));

        using var cts = new CancellationTokenSource();
        var abort = new TurnAbortHandle { Current = () => cts };
        var (s, _, h) = Armed();
        var keys = new CountingKeys(new[] { EscKey, EscKey });
        var prompter = new RichPermissionPrompter(s, T, keys,
            pump: null, chrome: h, denyReason: false, webSearchProvider: null, abort: abort);

        var root = Directory.CreateTempSubdirectory("gatto-r1-").FullName;
        try
        {
            var spy = new ExecutionSpyTool("write_file");
            var (loop, convo, obs) = LoopOver(server, root, prompter, spy);

            var outcome = await loop.RunTurnAsync(convo, "go", obs, cts.Token);

            Assert.Equal(TurnOutcome.Cancelled, outcome.Outcome);
            Assert.Equal(1, server.RequestCount);
            //every call is answered in call order, which is the property under test.
            AssertReplayable(convo);
            Assert.Equal(new[] { "t1", "t2" },
                convo.Messages.Where(m => m.Role == "tool").Select(m => m.ToolCallId).ToArray());
            Assert.Equal(new[] { "blocked: " + PermissionGate.CancelMessage, "cancelled" },
                convo.Messages.Where(m => m.Role == "tool").Select(m => m.Content).ToArray());
            Assert.False(spy.Executed);
            //esc ends the round, so the second call never opens a prompt
            Assert.Equal(1, keys.Prompts);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    //esc at the last call of a round ends the turn there, no next request goes out on the stopped turn and no truncated record is written
    [Fact]
    public async Task EscCancel_OnTheOnlyCall_EndsTheTurnWithNoFurtherRequest()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            ToolCallFrame(0, "t1", "write_file", """{"path":"a.txt","content":"x"}"""),
            FinishFrame("tool_calls"),
            "data: [DONE]\n\n",
        }));

        using var cts = new CancellationTokenSource();
        var abort = new TurnAbortHandle { Current = () => cts };
        var (s, _, h) = Armed();
        var prompter = new RichPermissionPrompter(s, T, new CountingKeys(new[] { EscKey }),
            pump: null, chrome: h, denyReason: false, webSearchProvider: null, abort: abort);

        var root = Directory.CreateTempSubdirectory("gatto-r1-").FullName;
        try
        {
            var (loop, convo, obs) = LoopOver(server, root, prompter, new ExecutionSpyTool("write_file"));

            var outcome = await loop.RunTurnAsync(convo, "go", obs, cts.Token);

            Assert.Equal(TurnOutcome.Cancelled, outcome.Outcome);
            Assert.Equal(1, server.RequestCount);
            Assert.DoesNotContain(convo.Messages, m => m.Role == "assistant" && (m.Content ?? "").EndsWith("[truncated]", StringComparison.Ordinal));
            AssertReplayable(convo);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    //a yes scripted for the second call finds no prompt to answer, so nothing runs inside the stopped turn
    [Fact]
    public async Task EscCancel_WithParallelCalls_NoLaterPromptOpens_AndNothingRuns()
    {
        await using var server = new FakeOpenAiServer();
        server.Enqueue(new FakeResponse(Frames: new[]
        {
            ToolCallFrame(0, "t1", "write_file", """{"path":"a.txt","content":"x"}"""),
            ToolCallFrame(1, "t2", "write_file", """{"path":"b.txt","content":"y"}"""),
            FinishFrame("tool_calls"),
            "data: [DONE]\n\n",
        }));

        using var cts = new CancellationTokenSource();
        var abort = new TurnAbortHandle { Current = () => cts };
        var (s, _, h) = Armed();
        //esc aborts on t1, and the scripted Yes approves t2 at a panel that should never open
        var keys = new CountingKeys(new[] { EscKey, Digit('1') });
        var prompter = new RichPermissionPrompter(s, T, keys,
            pump: null, chrome: h, denyReason: false, webSearchProvider: null, abort: abort);

        var root = Directory.CreateTempSubdirectory("gatto-r1-").FullName;
        try
        {
            var spy = new ExecutionSpyTool("write_file");
            var (loop, convo, obs) = LoopOver(server, root, prompter, spy);

            var outcome = await loop.RunTurnAsync(convo, "go", obs, cts.Token);

            Assert.Equal(TurnOutcome.Cancelled, outcome.Outcome);
            Assert.Equal(1, keys.Prompts);
            Assert.False(spy.Executed);
            //the first call is refused with the gate's cancel message and the second is answered cancelled without running
            var toolMsgs = convo.Messages.Where(m => m.Role == "tool").ToList();
            Assert.Equal(new[] { "t1", "t2" }, toolMsgs.Select(m => m.ToolCallId).ToArray());
            Assert.Equal("blocked: " + PermissionGate.CancelMessage, toolMsgs[0].Content);
            Assert.Equal("cancelled", toolMsgs[1].Content);
            Assert.True(toolMsgs[1].IsError);
            AssertReplayable(convo);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    //a prompt that opens after its turn was stopped closes at once and reads no key, the same throw a ctrl+break gives a prompt
    [Fact]
    public void A_prompt_asked_in_a_stopped_turn_closes_without_reading_a_key()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var (s, _, h) = Armed();
        var keys = new CountingKeys(new[] { Digit('1') });
        var prompter = new RichPermissionPrompter(s, T, keys,
            pump: null, chrome: h, denyReason: false, webSearchProvider: null, abort: new TurnAbortHandle { Current = () => cts });

        Assert.Throws<OperationCanceledException>(() => prompter.AskWithReason(new PermissionRequest("shell", "git status", "git")));
        Assert.Equal(0, keys.Prompts);
    }

    //count the panels raised, each esc-answered panel reads exactly one key
    private sealed class CountingKeys(IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public int Prompts { get; private set; }
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey()
        {
            Prompts++;
            return _q.Count > 0 ? _q.Dequeue() : throw new InvalidOperationException("test scripted too few keys");
        }
    }

    //esc outside a turn must be a no-op, and both null shapes are covered, each its own null check
    [Fact]
    public void EscOutsideTurn_NoOp()
    {
        Assert.Null(Record.Exception(() => new TurnAbortHandle().RequestAbort()));
        Assert.Null(Record.Exception(() => new TurnAbortHandle { Current = () => null }.RequestAbort()));
    }

    //the Current source is a function, the Repl swaps the token at each boundary and role swap, so a captured one aborts an ended turn
    [Fact]
    public void RequestAbort_ReadsTheCtsLive_NeverTheOneCapturedAtWiringTime()
    {
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();
        var live = first;
        var handle = new TurnAbortHandle { Current = () => live };

        handle.RequestAbort();
        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);

        live = second;                       //the source moves to a new token, as at a turn boundary.
        handle.RequestAbort();

        Assert.True(second.IsCancellationRequested);
    }

    //a source disposed between the read and the cancel is a benign race, so the prompter must not take the session down
    [Fact]
    public void RequestAbort_OnADisposedCts_DoesNotThrow()
    {
        var cts = new CancellationTokenSource();
        cts.Dispose();
        var handle = new TurnAbortHandle { Current = () => cts };

        Assert.Null(Record.Exception(handle.RequestAbort));
    }

    //the abort must work on every entry point, and CheckpointGate enters through Ask, so esc there must abort the turn too
    [Fact]
    public void Ask_EscAbortsTheTurn_JustLikeAskWithReason()
    {
        using var cts = new CancellationTokenSource();
        var abort = new TurnAbortHandle { Current = () => cts };
        var (s, _, h) = Armed();
        var prompter = new RichPermissionPrompter(s, T, new ScriptedKeys(new[] { EscKey }),
            pump: null, chrome: h, denyReason: false, webSearchProvider: null, abort: abort);

        Assert.Equal(PermissionAnswer.Cancel, prompter.Ask(CheckpointRequest));
        Assert.True(cts.IsCancellationRequested);
    }

    //with a pump the prompt holds focus for its whole life, so type-ahead cannot answer it and its keys cannot leak into the composer
    [Fact]
    public void WithPump_AnswersViaFocus_TypeAheadSurvivesInComposer()
    {
        var src = new System.Collections.Concurrent.BlockingCollection<ConsoleKeyInfo>();
        var pump = new InputPump(new KeyInputSource(new FeedKeys(src)));
        pump.Start();
        src.Add(new ConsoleKeyInfo('t', ConsoleKey.T, false, false, false));   //this key is composer type-ahead, typed before the prompt exists.
        //wait on the composer channel count, KeyAvailable is true before the key is routed and focus pushed then sends it to the prompt
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (pump.ComposerPending == 0 && sw.ElapsedMilliseconds < 30000) Thread.Sleep(5);
        Assert.True(pump.ComposerPending > 0);

        //the prompt and the polling thread write this surface at once, so it needs a synchronized builder.
        var surface = new SyncSurface();
        var prompter = new RichPermissionPrompter(surface, new Theme(TermCaps.Plain), new NeverKeys(), pump);
        var ask = Task.Run(() => prompter.Ask(ShellRequest));
        //wait for the render, it comes after focus is pushed, so the prompt scope is on top before any key can reach the composer
        var renderSw = System.Diagnostics.Stopwatch.StartNew();
        while (!surface.Text.Contains("Do you want to proceed?", StringComparison.Ordinal)
               && renderSw.ElapsedMilliseconds < 30000) Thread.Sleep(5);
        Assert.Contains("Do you want to proceed?", surface.Text, StringComparison.Ordinal);
        src.Add(new ConsoleKeyInfo('3', ConsoleKey.D3, false, false, false));   //this key answers option 3, which is No.
        var answer = WaitForAnswer(ask);
        Assert.True(answer.HasValue, "prompt did not answer via the pump focus");
        Assert.Equal(PermissionAnswer.Deny, answer);

        //the prompt must leave the type-ahead alone, so the composer still holds that exact t
        Assert.True(pump.Composer.KeyAvailable);
        var leftover = ReadKeyBounded(pump.Composer);
        Assert.True(leftover.HasValue, "composer had no key to read despite KeyAvailable being true");
        Assert.Equal(ConsoleKey.T, leftover!.Value.Key);
        Assert.Equal('t', leftover.Value.KeyChar);
    }

    //a helper holds the blocking wait, an analyzer rule bans blocking calls in a test method, and the deadline still fails the test
    private static PermissionAnswer? WaitForAnswer(Task<PermissionAnswer> ask, int ms = 30000)
        => ask.Wait(ms) ? ask.Result : null;

    //the composer source yields a ComposerInput, so a key must be unwrapped from it
    private static ConsoleKeyInfo? ReadKeyBounded(IComposerSource source, int ms = 30000)
    {
        var read = Task.Run(source.Read);
        if (!read.Wait(ms)) return null;
        return Assert.IsType<ComposerInput.Key>(read.Result).K;
    }

    private sealed class FeedKeys(System.Collections.Concurrent.BlockingCollection<ConsoleKeyInfo> q)
        : Gatto.Terminal.IKeySource
    {
        public bool KeyAvailable => q.Count > 0;
        public ConsoleKeyInfo ReadKey() => q.Take();
    }

    private sealed class NeverKeys : IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey()
        {
            Thread.Sleep(Timeout.Infinite);
            return default;
        }
    }

    //lock the fake's storage, so the race the test exercises stays in the routing logic
    private sealed class SyncSurface : ITermSurface
    {
        private readonly System.Text.StringBuilder _sb = new();
        public int Width { get; set; } = 80;
        public int Height { get; set; }
        public void Write(string s) { lock (_sb) _sb.Append(s); }
        public string Text { get { lock (_sb) return _sb.ToString(); } }
    }
}

//this class replaces Console.Out and Console.In, so it has to share one collection
[Collection("e2e")]
public class PlainPermissionPrompterTests : IDisposable
{
    private readonly TextWriter _priorOut = Console.Out;
    private readonly TextReader _priorIn = Console.In;
    public void Dispose() { Console.SetOut(_priorOut); Console.SetIn(_priorIn); }

    private static readonly PermissionRequest ShellRequest =
        new("shell", "git status --short", "git status");

    private static readonly PermissionRequest NoOfferRequest =
        new("frobnicate", "{\"x\":1}", null);

    private static (string Output, PermissionAnswer Answer) Run(PermissionRequest request, string input)
    {
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader(input));
        var answer = new PlainPermissionPrompter().Ask(request);
        return (sw.ToString(), answer);
    }

    [Fact]
    public void Once_ViaNumber1()
    {
        var (output, answer) = Run(ShellRequest, "1\n");
        Assert.Equal(PermissionAnswer.Once, answer);
        Assert.Contains("allowed once", output);
    }

    [Fact]
    public void Deny_ViaNumber0()
    {
        var (output, answer) = Run(ShellRequest, "0\n");
        Assert.Equal(PermissionAnswer.Deny, answer);
        Assert.Contains("denied", output);
    }

    [Fact]
    public void Always_ViaNumber2_WhenOfferPresent()
    {
        var (output, answer) = Run(ShellRequest, "2\n");
        Assert.Equal(PermissionAnswer.Always, answer);
        Assert.Contains("git status", output);
    }

    [Fact]
    public void Always_MenuLineAndEcho_CarryTheProjectScopeLabel()
    {
        var (output, answer) = Run(ShellRequest, "2\n");
        Assert.Equal(PermissionAnswer.Always, answer);
        Assert.Contains("2. always (this project) \"git status\"", output);
        //the echo after the answer repeats the menu line's label text.
        Assert.Equal(2, CountOccurrences(output, "always (this project) \"git status\""));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    [Fact]
    public void NoOffer_Number2IsInvalid_ThenReprompts()
    {
        var (output, answer) = Run(NoOfferRequest, "2\n0\n");
        Assert.Equal(PermissionAnswer.Deny, answer);
        Assert.DoesNotContain("2. always", output);
    }

    [Fact]
    public void StdinClosed_FailsDeny()
    {
        var (output, answer) = Run(ShellRequest, "");
        Assert.Equal(PermissionAnswer.Deny, answer);
        Assert.Contains("denied", output);
    }

    [Fact]
    public void Output_ContainsNoEscapeBytes()
    {
        var hostile = new PermissionRequest("shell", "danger\x1b[31m command", "git\x1bstatus");
        var (output, _) = Run(hostile, "1\n");
        Assert.DoesNotContain('\x1b', output);
    }

    [Fact]
    public void Output_ContainsNoEscapeBytes_OnDenyAndAlways()
    {
        var hostile = new PermissionRequest("shell", "danger", "git\x1bstatus");
        var (denyOut, _) = Run(hostile, "0\n");
        Assert.DoesNotContain('\x1b', denyOut);
        var (alwaysOut, _) = Run(hostile, "2\n");
        Assert.DoesNotContain('\x1b', alwaysOut);
    }

    //a model-invented tool name reaches the prompter before the unknown-tool check, so it needs the same sanitize step as the summary.
    [Fact]
    public void HostileToolName_ProducesNoEscapeBytes()
    {
        var hostile = new PermissionRequest("x\x1b[2Jfoo", "cmd", null);
        var (output, _) = Run(hostile, "1\n");
        Assert.DoesNotContain('\x1b', output);
        Assert.Contains("x[2Jfoo", output);   //only the escape byte goes, and the rest shows as inert text.
    }

    private static (string Output, PermissionDecision Decision) RunWithReason(PermissionRequest request, string input)
    {
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader(input));
        //the reason step is opt-in, so this helper turns it on for the tests that judge it.
        var decision = new PlainPermissionPrompter(denyReason: true).AskWithReason(request);
        return (sw.ToString(), decision);
    }

    //the plain reason read names the cap, re-asks when nothing would reach the model and says when the cap cut the reason, as the rich read does
    [Fact]
    public void The_plain_reason_names_its_cap_reasks_on_nothing_and_reports_a_cut()
    {
        var control = new string('\x07', 3);
        var (output, decision) = RunWithReason(ShellRequest, "0\n" + control + "\nok then\n");
        Assert.Contains($"{PermissionGate.MaxReasonLength} chars", output, StringComparison.Ordinal);
        Assert.Contains(RichPermissionPrompter.NothingReachedModel, output, StringComparison.Ordinal);
        Assert.Equal("ok then", decision.Reason);

        var (cutOutput, cut) = RunWithReason(ShellRequest, "0\n" + new string('x', 300) + "\n");
        Assert.Contains($"only the first {PermissionGate.MaxReasonLength} chars reach the model", cutOutput, StringComparison.Ordinal);
        Assert.Equal(300, cut.Reason!.Length);
    }

    [Fact]
    public void AskWithReason_DenyThenPlainEnter_NullReason()
    {
        var (_, decision) = RunWithReason(ShellRequest, "0\n\n");
        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Null(decision.Reason);
    }

    [Fact]
    public void DenyReasonOff_IsTheDefault_AndConsumesNoReasonLine()
    {
        //both prompt paths honour the switch, so each needs its own test, and unread leftover input is the oracle
        var sw = new StringWriter();
        Console.SetOut(sw);
        var reader = new StringReader("0\nlater\n");
        Console.SetIn(reader);

        var decision = new PlainPermissionPrompter().AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Null(decision.Reason);
        Assert.DoesNotContain("reason", sw.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("later", reader.ReadLine());   //the leftover line proves the reason step read nothing.
    }

    [Fact]
    public void DenyReasonOn_ReadsTheReasonLine()
    {
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader("0\nuse read_file instead\n"));

        var decision = new PlainPermissionPrompter(denyReason: true).AskWithReason(ShellRequest);

        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Equal("use read_file instead", decision.Reason);
    }

    [Fact]
    public void AskWithReason_DenyThenTypedReason_CapturesIt()
    {
        var (_, decision) = RunWithReason(ShellRequest, "0\nuse read_file instead\n");
        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Equal("use read_file instead", decision.Reason);
    }

    [Fact]
    public void AskWithReason_Once_NeverReadsAReasonLine()
    {
        //a null reason proves nothing, exhausted stdin gives one too, so assert that no reason prompt printed
        var (output, decision) = RunWithReason(ShellRequest, "1\n");
        Assert.Equal(PermissionAnswer.Once, decision.Answer);
        Assert.Null(decision.Reason);
        Assert.DoesNotContain("reason", output);
    }

    [Fact]
    public void Ask_StillDeniesWithNoReasonPrompt_UnaffectedByAskWithReason()
    {
        //the Ask path must stay unchanged, a bare 0 resolves Deny and reads nothing further
        var (output, answer) = Run(ShellRequest, "0\n");
        Assert.Equal(PermissionAnswer.Deny, answer);
        Assert.Contains("denied", output);
        Assert.DoesNotContain("reason", output);
    }

    [Fact]
    public void AskWithReason_StdinClosedMidReason_FailsDenyWithNullReason()
    {
        //the input selects Deny, then stdin closes before a reason line arrives.
        var (_, decision) = RunWithReason(ShellRequest, "0\n");
        Assert.Equal(PermissionAnswer.Deny, decision.Answer);
        Assert.Null(decision.Reason);
    }
}
