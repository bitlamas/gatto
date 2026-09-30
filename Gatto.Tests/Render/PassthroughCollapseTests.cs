using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//a compaction summary streams on the reasoning channel for dim styling, but the block-close collapse still reads PassthroughReasoning, so it never auto-collapses
public sealed class PassthroughCollapseTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static StatusInfo Status(string role) => new(@"C:\proj", "qwen", role, new CtxState(), @"C:\Users\x");

    private static (StreamRenderer R, TranscriptModel M) Make()
    {
        var s = new RecordingSurface { Width = 80, Height = 0 };
        var gate = new object();
        var painter = new ChromePainter(s, T, gate) { Frame = new InputFrame(s, T, "coder", Status("coder"), glyphs: GlyphSet.Unicode), RoleForTint = "coder" };
        var ticker = new ChromeTicker(painter, gate);
        var model = new TranscriptModel("coder");
        return (new StreamRenderer(painter, s, T, "coder", ticker, gate, model: model, convoTail: () => null), model);
    }

    private static ReasoningItem TheReasoningItem(TranscriptModel m) =>
        Assert.IsType<ReasoningItem>(Assert.Single(m.Items, i => i is ReasoningItem));

    [Fact]
    public void Summary_streamed_under_passthrough_is_not_collapsed_when_the_block_closes_later()
    {
        //the order auto-compact uses: passthrough on, the summary streams, the flag clears, then the warning closes the block
        var (r, m) = Make();
        r.BeginTurn();
        r.PassthroughReasoning = true;
        r.OnReasoningDelta("Task / goal: make chips follow the role accent.\n");
        r.OnReasoningDelta("Immediate next step: edit Theme.cs's chip branch.\n");
        r.PassthroughReasoning = false;                 //the flag clears before the block closes, which is the case the collapse check must survive.
        r.OnWarning("auto-compacted at 88% — continuing");   //the warning arriving after the flag clears is what closes the block.

        Assert.False(TheReasoningItem(m).Collapsed);
    }

    //real model reasoning after a passthrough summary must still auto-collapse, the flag must not colour the next block
    [Fact]
    public void Real_reasoning_after_a_passthrough_summary_still_collapses()
    {
        var (r, m) = Make();
        r.BeginTurn();

        r.PassthroughReasoning = true;
        r.OnReasoningDelta("Task / goal: make chips follow the role accent.\n");
        r.PassthroughReasoning = false;
        r.OnWarning("auto-compacted at 88% — continuing");

        //this second block is real model reasoning, so it must collapse.
        r.OnReasoningDelta("Now I need to find the chip branch.\n");
        r.OnTextDelta("Found it.");                     //the text delta closes the open reasoning block, and that close makes the collapse decision.

        var reasoning = m.Items.OfType<ReasoningItem>().ToList();
        Assert.Equal(2, reasoning.Count);
        Assert.False(reasoning[0].Collapsed);           //the first item is the summary, and it is never auto-collapsed.
        Assert.True(reasoning[1].Collapsed);            //the second item is real reasoning, so it collapses as configured.
    }

    [Fact]
    public void Summary_is_not_collapsed_when_the_block_closes_while_passthrough_is_still_on()
    {
        //the control case: the block closes while the flag is still on, which is what the flag is for
        var (r, m) = Make();
        r.BeginTurn();
        r.PassthroughReasoning = true;
        r.OnReasoningDelta("Task / goal: make chips follow the role accent.\n");
        r.OnWarning("auto-compacted at 88% — continuing");
        r.PassthroughReasoning = false;

        Assert.False(TheReasoningItem(m).Collapsed);
    }
}
