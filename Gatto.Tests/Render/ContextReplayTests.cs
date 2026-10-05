using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Repl.Render;
using Gatto.Terminal;

namespace Gatto.Tests.Render;

//a committed /context report keeps its figures in the session file, so a resumed session draws it again at the width it replays at
public class ContextReplayTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly GlyphSet U = GlyphSet.Unicode;

    private static readonly ContextFigures Figures = new(65_536, 41_410, true, false, false, 0.8,
    [
        new("system prompt", ContextGroup.Prefix, 1_180), new("role", ContextGroup.Prefix, 210, "coder"),
        new("user", ContextGroup.Messages, 1_350), new("reasoning", ContextGroup.Reasoning, 6_410),
        new("tool results", ContextGroup.ToolResults, 18_940, "64"),
    ], new ContextCache(40_880, 222));

    private static (List<string> Lines, CommandEchoItem? Rebuilt) RoundTrip(CommandEchoItem item)
    {
        var model = new TranscriptModel("coder");
        model.Append(item);
        var lines = TranscriptStore.BuildLines(model, new Conversation("sys")).ToList();
        var (_, rebuilt) = TranscriptStore.Rebuild(lines, T, "coder", glyphs: U);
        return (lines, rebuilt.Items.OfType<CommandEchoItem>().SingleOrDefault());
    }

    private static CommandEchoItem Committed(int width) =>
        new([.. Gatto.Repl.ContextReport.Rows(Figures, width, T, U).Select(r => r.Rendered)], null) { Context = Figures };

    [Fact]
    public void The_figures_survive_the_session_file()
    {
        var (_, rebuilt) = RoundTrip(Committed(120));

        var f = rebuilt!.Context!;
        Assert.Equal(Figures.Window, f.Window);
        Assert.Equal(Figures.Total, f.Total);
        Assert.Equal(Figures.Exact, f.Exact);
        Assert.Equal(Figures.FellBack, f.FellBack);
        Assert.Equal(Figures.BeforeFirstRequest, f.BeforeFirstRequest);
        Assert.Equal(Figures.AutoCompactAt, f.AutoCompactAt);
        Assert.Equal(Figures.Parts, f.Parts);
        Assert.Equal(Figures.Cache, f.Cache);
    }

    //committed at 120 and replayed at 90, the bar is drawn for 90 and nothing is cut
    [Fact]
    public void A_replay_at_a_narrower_width_draws_the_report_again()
    {
        var (_, rebuilt) = RoundTrip(Committed(120));

        var rows = rebuilt!.Render(90, T, U).Select(TermText.StripAnsiForWidth).ToList();

        Assert.Contains(rows, r => r.Trim().Length == 86 && r.Trim().All(c => c is '█' or '░'));
        Assert.DoesNotContain(rows, r => r.Contains('…'));
    }

    [Fact]
    public void A_command_echo_without_figures_writes_no_context_field()
    {
        var (lines, rebuilt) = RoundTrip(new CommandEchoItem(["  some rows"], null));

        Assert.DoesNotContain(lines, l => l.Contains("\"context\"", StringComparison.Ordinal));
        Assert.Null(rebuilt!.Context);
    }

    //a field that does not parse is dropped and the rows it was saved with are drawn
    [Fact]
    public void A_malformed_context_field_falls_back_to_the_rows()
    {
        var (lines, _) = RoundTrip(Committed(120));
        var broken = lines.Select(l => l.Contains("\"context\"", StringComparison.Ordinal)
            ? l.Replace("\"total\":41410", "\"total\":\"many\"", StringComparison.Ordinal) : l).ToList();

        var (_, model) = TranscriptStore.Rebuild(broken, T, "coder", glyphs: U);

        var item = model.Items.OfType<CommandEchoItem>().Single();
        Assert.Null(item.Context);
        Assert.NotEmpty(item.LogicalRows);
    }
}
