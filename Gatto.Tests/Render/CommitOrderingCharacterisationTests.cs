using System;
using System.Collections.Generic;
using System.Linq;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//these tests record the order the two commit channels produce: the model keeps no prompt echo while CommitTap emits the decision rows
public sealed class CommitOrderingCharacterisationTests
{
    private const int Width = 100;   //the width keeps every probe line unwrapped.
    private static readonly Theme T = new(new TermCaps(true, true));
    private static StatusInfo Status(string role) => new(@"C:\proj", "qwen", role, new CtxState(), @"C:\Users\x");

    private static (StreamRenderer R, TranscriptModel M, List<string> Committed) Make()
    {
        var s = new RecordingSurface { Width = Width, Height = 0 };
        var gate = new object();
        var painter = new ChromePainter(s, T, gate, new TranscriptModel("coder"))
        {
            Frame = new InputFrame(s, T, "coder", Status("coder"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        var ticker = new ChromeTicker(painter, gate);
        var committed = new List<string>();
        var r = new StreamRenderer(painter, s, T, "coder", ticker, gate,
            reasoning: ReasoningMode.Collapsed, model: painter.Model, convoTail: () => null)
        { CommitTap = rows => committed.AddRange(rows) };
        return (r, painter.Model, committed);
    }

    //the rows the compositor would see, rendered through the live path in item order
    private static List<string> Scrollback(TranscriptModel m) =>
        m.Items.SelectMany(i => i.Render(Width, T, glyphs: GlyphSet.Unicode)).ToList();

    //the index of the first row whose text holds the needle, compared ordinally so the escape byte still counts
    private static int RowOf(IReadOnlyList<string> rows, string needle)
    {
        for (var i = 0; i < rows.Count; i++)
            if (TermText.StripAnsiForWidth(rows[i]).Contains(needle, StringComparison.Ordinal)) return i;
        Assert.Fail($"no row carries \"{needle}\". Rows:\n  "
            + string.Join("\n  ", rows.Select(r => r.Replace("\x1b", "<ESC>", StringComparison.Ordinal))));
        return -1;   //this line is unreachable, because the fail above ends the method.
    }

    private static void AssertBefore(IReadOnlyList<string> rows, string earlier, string later, string channel)
    {
        var a = RowOf(rows, earlier);
        var b = RowOf(rows, later);
        Assert.True(a < b, $"{channel}: expected \"{earlier}\" (row {a}) ABOVE \"{later}\" (row {b}).");
    }

    //fixture rows for CommitPrompt, whose exact text does not matter since the probe only asks whether a model row holds them
    private static readonly string[] Decision = { "  allow?", "  ✓ allowed once" };

    private static readonly ToolCall Call = new("i", "shell", """{"command":"git status"}""");

    //assert both channels at once: a text probe finds the decision rows in the seam and none of their text in the model
    private static void AssertPromptEchoIsModelSilentButStillCommitted(
        TranscriptModel m, List<string> committed)
    {
        //the model channel: no item of any class holds the prompt's text.
        var rows = Scrollback(m);
        Assert.DoesNotContain(rows, row => row.Contains("allow?", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains("allowed once", StringComparison.Ordinal));

        //the seam still emits the rows by design. pin the residue so a rewrite cannot drop it by accident, and so removing it later stays a deliberate act.
        Assert.Contains(committed, row => row.Contains("allowed once", StringComparison.Ordinal));
    }

    //covers the direct CommitPrompt path, where no tool block is in flight
    [Fact]
    public void R2_direct_commit_prompt_appends_no_prompt_echo_to_the_model()
    {
        var (r, m, committed) = Make();

        r.BeginTurn();
        r.CommitPrompt(Decision);

        AssertPromptEchoIsModelSilentButStillCommitted(m, committed);
        Assert.Empty(m.Items);   //the direct path had no tool block, so the model stays completely empty.
    }

    //covers the OnToolResult flush: the prompt fires while the bullet is still chrome, so the tool block stays and the echo does not
    [Fact]
    public void R2_flush_at_tool_result_appends_no_prompt_echo_to_the_model()
    {
        var (r, m, committed) = Make();

        r.BeginTurn();
        r.OnToolCallStart(Call);
        r.CommitPrompt(Decision);
        r.OnToolResult(Call, new ToolResult("ok", Gloss: "exit 0"));

        AssertPromptEchoIsModelSilentButStillCommitted(m, committed);
        Assert.Single(m.Items, i => i is ToolBlockItem);
        Assert.Single(m.Items);
    }

    //covers the EndTurn flush: a bullet whose result never arrives is still rescued as a cancelled block, with no decision echo
    [Fact]
    public void R2_flush_at_end_turn_appends_no_prompt_echo_to_the_model()
    {
        var (r, m, committed) = Make();

        r.BeginTurn();
        r.OnToolCallStart(Call);
        r.CommitPrompt(Decision);
        r.EndTurn();   //no result arrives before the turn ends.

        AssertPromptEchoIsModelSilentButStillCommitted(m, committed);
        Assert.Single(m.Items, i => i is ToolBlockItem);
        Assert.Single(m.Items);
    }

    //one warning, both channels: at the seam the decision rows precede the warning, while the model shows the warning after the tool block
    [Fact]
    public void R2_channels_diverge_around_a_warning_raised_while_the_bullet_is_chrome()
    {
        var (r, m, committed) = Make();

        r.BeginTurn();
        r.OnToolCallStart(Call);
        r.CommitPrompt(Decision);
        r.OnWarning("turn cancelled");                          //the warning is buffered because the bullet is still chrome.
        r.OnToolResult(Call, new ToolResult("ok", Gloss: "exit 0"));

        AssertBefore(committed, "allowed once", "turn cancelled", "commit seam");
        AssertBefore(committed, "git status", "allowed once", "commit seam");

        var rows = Scrollback(m);
        Assert.DoesNotContain(rows, row => row.Contains("allowed once", StringComparison.Ordinal));
        AssertBefore(rows, "git status", "turn cancelled", "scrollback");
        Assert.Collection(m.Items,
            i => Assert.IsType<ToolBlockItem>(i),
            i => Assert.IsType<SystemLineItem>(i));
    }

    //replay the auto-compaction order, with a last summary line that has no terminator so the seal's flush commits it
    [Fact]
    public void Characterisation_autocompact_commits_opening_notice_then_the_whole_summary_then_the_outcome_notice()
    {
        var (r, m, committed) = Make();

        r.BeginTurn();
        r.OnWarning("auto-compacting at 88 pct - summarizing");   //matches the notice raised before the await.
        r.PassthroughReasoning = true;                            //matches passthrough being switched on by the compaction call.
        r.OnReasoningDelta("SUMMARY-HEAD line of the compaction summary\n");
        r.OnReasoningDelta("SUMMARY-TAIL line, unterminated");     //the delta ends without a newline, so the seal commits it.
        r.PassthroughReasoning = false;                            //turning passthrough off seals the open block.
        r.OnWarning("auto-compacted at 88 pct - continuing");      //matches the notice raised after the rebuild.
        r.EndTurn();

        //check the scrollback that the compositor paints.
        var rows = Scrollback(m);
        AssertBefore(rows, "auto-compacting at 88 pct", "SUMMARY-HEAD", "scrollback");
        AssertBefore(rows, "SUMMARY-HEAD", "SUMMARY-TAIL", "scrollback");
        AssertBefore(rows, "SUMMARY-TAIL", "auto-compacted at 88 pct - continuing", "scrollback");

        //check the same order at the commit seam, in emission order.
        AssertBefore(committed, "auto-compacting at 88 pct", "SUMMARY-HEAD", "commit seam");
        AssertBefore(committed, "SUMMARY-HEAD", "SUMMARY-TAIL", "commit seam");
        AssertBefore(committed, "SUMMARY-TAIL", "auto-compacted at 88 pct - continuing", "commit seam");

        //check that the model holds one block per event, in the order the events arrived.
        Assert.Collection(m.Items,
            i => Assert.IsType<SystemLineItem>(i),
            i => Assert.IsType<ReasoningItem>(i),
            i => Assert.IsType<SystemLineItem>(i));
    }

    //a warning raised while the passthrough block is open closes that block first, so the unflushed tail commits above it and one block survives
    [Fact]
    public void Characterisation_a_warning_raised_while_the_passthrough_block_is_open_commits_the_open_tail_above_it()
    {
        var (r, m, committed) = Make();

        r.BeginTurn();
        r.PassthroughReasoning = true;
        r.OnReasoningDelta("SUMMARY-HEAD line of the compaction summary\n");
        r.OnReasoningDelta("SUMMARY-TAIL line, unterminated");   //the block stays open and the tail is unflushed here.
        r.OnWarning("auto-compacted at 88 pct - continuing");     //the warning arrives while the block is still open.
        r.PassthroughReasoning = false;
        r.EndTurn();

        var rows = Scrollback(m);
        AssertBefore(rows, "SUMMARY-HEAD", "SUMMARY-TAIL", "scrollback");
        AssertBefore(rows, "SUMMARY-TAIL", "auto-compacted at 88 pct - continuing", "scrollback");

        AssertBefore(committed, "SUMMARY-HEAD", "SUMMARY-TAIL", "commit seam");
        AssertBefore(committed, "SUMMARY-TAIL", "auto-compacted at 88 pct - continuing", "commit seam");

        //one reasoning block with the notice after it, rather than a summary split around the notice
        Assert.Collection(m.Items,
            i => Assert.IsType<ReasoningItem>(i),
            i => Assert.IsType<SystemLineItem>(i));
    }
}
