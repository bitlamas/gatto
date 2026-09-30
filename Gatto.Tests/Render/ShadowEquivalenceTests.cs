using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//the model's render must match the rows the commit path received, blank separators included. tables are exempt, the model owns them in rich mode.
public sealed class ShadowEquivalenceTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static StatusInfo Status(string role) => new(@"C:\proj", "qwen", role, new CtxState(), @"C:\Users\x");

    private static (StreamRenderer R, TranscriptModel M, System.Collections.Generic.List<string> Committed) Make(string role = "coder")
    {
        var s = new RecordingSurface { Width = 80, Height = 0 };
        var gate = new object();
        var painter = new ChromePainter(s, T, gate) { Frame = new InputFrame(s, T, role, Status(role), glyphs: GlyphSet.Unicode), RoleForTint = role };
        var ticker = new ChromeTicker(painter, gate);
        var model = new TranscriptModel(role);
        var committed = new System.Collections.Generic.List<string>();
        var r = new StreamRenderer(painter, s, T, role, ticker, gate, model: model, convoTail: () => null) { CommitTap = rows => committed.AddRange(rows) };
        return (r, model, committed);
    }

    private static string[] ModelRender(TranscriptModel m) =>
        m.Items.SelectMany(i => i.Render(80, T, glyphs: GlyphSet.Unicode)).ToArray();

    [Fact]
    public void Model_render_equals_the_committed_rows_across_a_full_turn()
    {
        var (r, m, committed) = Make();

        r.BeginTurn();
        r.CommitUser(new[] { "do the thing" });
        r.OnTextDelta("Here is a plan:\n");
        r.OnTextDelta("first paragraph line\n");
        r.OnTextDelta("\n");                         //a lone newline delta forms the paragraph break.
        r.OnTextDelta("second paragraph\n");
        var call = new ToolCall("c1", "read_file", "{\"path\":\"a\"}");
        r.OnToolCallStart(call);
        r.OnToolResult(call, new ToolResult("file body"));
        r.OnTextDelta("all done\n");
        r.EndTurn();
        r.CommitCompletion("⟨face⟩ purred for 3s · ↓ 5");

        Assert.Equal(committed.ToArray(), ModelRender(m));
    }

    [Fact]
    public void Model_render_equals_committed_rows_for_a_wrapped_and_fenced_answer()
    {
        var (r, m, committed) = Make();
        r.BeginTurn();
        r.CommitUser(new[] { "show code" });
        r.OnTextDelta("A long assistant line that will wrap several times across the eighty column width here.\n");
        r.OnTextDelta("```markdown\n");
        r.OnTextDelta("print('hi')\n");
        r.OnTextDelta("```\n");
        r.OnTextDelta("- a bullet item\n");
        r.EndTurn();

        Assert.Equal(committed.ToArray(), ModelRender(m));
    }

    [Fact]
    public void Model_render_equals_committed_rows_for_a_fence_line_that_wraps()
    {
        //the fence must wrap at 80, a short fence can't tell the two commit paths apart
        var (r, m, committed) = Make();
        r.BeginTurn();
        r.CommitUser(new[] { "show code" });
        r.OnTextDelta("```text\n");
        r.OnTextDelta("A long paragraph inside a fenced block that has to wrap several times before it "
                    + "reaches the end of an eighty column terminal window.\n");
        r.OnTextDelta("```\n");
        r.EndTurn();

        Assert.Equal(committed.ToArray(), ModelRender(m));
        Assert.True(committed.Count > 3, "the fenced line must have wrapped");
    }

    //with a model present, BufferTableLine returns early, so the commit rows keep prose and the model draws a grid. assert both sides, or one path copies the other.
    [Fact]
    public void Rich_mode_tables_are_model_owned_so_the_two_paths_diverge()
    {
        var (r, m, committed) = Make();
        r.BeginTurn();
        r.CommitUser(new[] { "table please" });
        r.OnTextDelta("| name | qty |\n");
        r.OnTextDelta("| --- | --- |\n");
        r.OnTextDelta("| bolt | 2 |\n");
        r.EndTurn();

        var modelText = string.Join("\n", ModelRender(m).Select(TermText.StripAnsiForWidth));
        var committedText = string.Join("\n", committed.Select(TermText.StripAnsiForWidth));

        //the model draws a box grid, so borders appear and the markdown delimiter row is consumed.
        Assert.Contains("┌", modelText, StringComparison.Ordinal);
        Assert.DoesNotContain("---", modelText, StringComparison.Ordinal);

        //the commit path never treats the source as a table, so raw pipes survive and no border is drawn.
        Assert.Contains("| --- | --- |", committedText, StringComparison.Ordinal);
        Assert.DoesNotContain("┌", committedText, StringComparison.Ordinal);
    }

    //a highlighted fence is model-owned like a table, the commit rows keep the literal colour and the model paints roles over the same text
    [Fact]
    public void Rich_mode_highlighted_fences_are_model_owned_so_the_two_paths_differ_only_in_colour()
    {
        var nl = ((char)10).ToString();
        var (r, m, committed) = Make();
        r.BeginTurn();
        r.CommitUser(new[] { "show code" });
        r.OnTextDelta("```python" + nl);
        r.OnTextDelta("print('hi')" + nl);
        r.OnTextDelta("```" + nl);
        r.EndTurn();

        var model = ModelRender(m);
        Assert.Equal(committed.Select(TermText.StripAnsiForWidth), model.Select(TermText.StripAnsiForWidth));
        Assert.Contains(model, row => row.Contains(Ansi.Fg(Theme.VsMethod, true) + "print", StringComparison.Ordinal));
        Assert.DoesNotContain(committed, row => row.Contains(Ansi.Fg(Theme.VsMethod, true), StringComparison.Ordinal));
    }

    [Fact]
    public void Model_render_equals_committed_rows_with_a_system_warning()
    {
        var (r, m, committed) = Make();
        r.BeginTurn();
        r.CommitUser(new[] { "go" });
        r.OnTextDelta("working\n");
        r.OnWarning("context 85% full");
        r.OnTextDelta("more\n");
        r.EndTurn();

        Assert.Equal(committed.ToArray(), ModelRender(m));
    }
}
