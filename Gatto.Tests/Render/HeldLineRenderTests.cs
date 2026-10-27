using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//a line a command answers from the composer waits while the reply streams, so the reply stays one block and the line follows it
public sealed class HeldLineRenderTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly GlyphSet U = GlyphSet.Unicode;

    private static (StreamRenderer R, TranscriptModel Model) Wire()
    {
        var s = new VtScreenSurface(120, 30);
        var gate = new object();
        var model = new TranscriptModel("coder");
        var painter = new ChromePainter(s, T, gate, model, glyphs: U)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: U),
            RoleForTint = "coder",
        };
        painter.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        var renderer = new StreamRenderer(painter, s, T, "coder", new ChromeTicker(painter, gate), gate, model: model, glyphs: U);
        painter.AltScreen.Enter();
        painter.Repaint();
        return (renderer, model);
    }

    private static List<string> Kinds(TranscriptModel m) => [.. m.Items.Select(i => i.GetType().Name)];

    private static string Rows(TranscriptModel m) =>
        string.Join("\n", m.Items.SelectMany(i => i.Render(120, T, U)).Select(r => TermText.StripAnsiForWidth(r).TrimEnd()));

    [Fact]
    public void A_LINE_ANSWERED_DURING_A_TEXT_DELTA_FOLLOWS_THE_REPLY_AND_THE_REPLY_STAYS_ONE_BLOCK()
    {
        var (r, model) = Wire();
        r.CommitUser(["go"]);
        r.BeginTurn();
        r.OnTextDelta("part one of the reply, ");
        r.CommitSystemBetweenBlocks("watching C:\\inbox");
        r.OnTextDelta("part two of the reply\n");
        r.EndTurn();

        var rows = Rows(model);
        Assert.Equal(1, rows.Split('\n').Count(row => row.StartsWith("● ", StringComparison.Ordinal)));
        Assert.True(rows.IndexOf("part two", StringComparison.Ordinal) < rows.IndexOf("♯ watching", StringComparison.Ordinal), rows);
        Assert.Equal(nameof(SystemLineItem), Kinds(model)[^1]);
    }

    [Fact]
    public void A_LINE_ANSWERED_DURING_REASONING_WAITS_FOR_THE_TURN()
    {
        var (r, model) = Wire();
        r.CommitUser(["go"]);
        r.BeginTurn();
        r.OnReasoningDelta("thinking about it");
        r.CommitSystemBetweenBlocks("watch off");
        Assert.DoesNotContain(nameof(SystemLineItem), Kinds(model));
        r.OnTextDelta("done\n");
        r.EndTurn();
        Assert.Equal(nameof(SystemLineItem), Kinds(model)[^1]);
    }

    [Fact]
    public void THE_PAUSE_ROW_RAISED_AT_THE_BOUNDARY_FOLLOWS_THE_DELIVERED_LETTERS_AND_PRECEDES_THE_REPLY()
    {
        var (r, model) = Wire();
        r.CommitUser(["go"]);
        r.BeginTurn();
        var call = new Gatto.Core.Client.ToolCall("c1", "shell", "{\"command\":\"dir\"}");
        r.OnToolCallsRecorded([call]);
        r.OnToolCallStart(call);
        r.OnToolResult(call, new Gatto.Core.Tools.ToolResult("listed", Gloss: "exit 0"));
        r.CommitSystemBeforeNextReply("watch paused after 6 letters, /watch to resume");
        r.OnQueuedDelivered(new Gatto.Core.Client.ChatMessage("user", "C:\\inbox\\a.md\n\nletter a"));
        r.OnQueuedDelivered(new Gatto.Core.Client.ChatMessage("user", "C:\\inbox\\b.md\n\nletter b"));
        r.OnTextDelta("read both\n");
        r.EndTurn();

        var rows = Rows(model);
        var b = rows.IndexOf("letter b", StringComparison.Ordinal);
        var paused = rows.IndexOf("♯ watch paused", StringComparison.Ordinal);
        var reply = rows.IndexOf("● read both", StringComparison.Ordinal);
        Assert.True(b < paused && paused < reply, rows);
    }

    [Fact]
    public void AT_REST_THE_PAUSE_ROW_COMMITS_AT_ONCE()
    {
        var (r, model) = Wire();
        r.CommitSystemBeforeNextReply("watch paused after 6 letters, /watch to resume");
        Assert.Equal(nameof(SystemLineItem), Kinds(model)[^1]);
    }

    [Fact]
    public void AT_REST_THE_LINE_COMMITS_AT_ONCE_AND_A_TRANSIENT_ONE_STAYS_TRANSIENT()
    {
        var (r, model) = Wire();
        r.CommitUser(["go"]);
        r.BeginTurn();
        r.OnTextDelta("an answer\n");
        r.EndTurn();
        r.CommitSystemBetweenBlocks("not watching");
        Assert.Equal(nameof(SystemLineItem), Kinds(model)[^1]);
        r.BeginTurn();
        r.OnTextDelta("streaming ");
        r.CommitSystemBetweenBlocks("no background task 9", transient: true);
        r.EndTurn();
        var last = Assert.IsType<SystemLineItem>(model.Items[^1]);
        Assert.True(last.Transient);
    }
}
