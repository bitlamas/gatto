using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests;

public sealed class ToolBodyTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static string Lines(int from, int n, string nl = "\n") =>
        string.Join(nl, Enumerable.Range(from, n).Select(i => $"text of line {i}"));

    //the read builder
    [Fact]
    public void A_read_with_no_offset_numbers_from_1()
    {
        var rows = ToolBody.ReadFile(Lines(1, 3), "3 lines", 1)!;
        Assert.Equal(new int?[] { 1, 2, 3 }, rows.Select(r => r.Number));
        Assert.All(rows, r => Assert.Equal((BodyKind.Numbered, BodyInk.Text), (r.Kind, r.Ink)));
        Assert.Equal("text of line 2", rows[1].Text);
    }

    [Fact]
    public void A_read_with_an_offset_of_218_numbers_from_218()
    {
        Assert.Equal(218, ToolBody.StartOf("read_file", "{\"path\":\"a.cs\",\"offset\":218,\"limit\":28}"));
        var rows = ToolBody.ReadFile(Lines(218, 28), "28 lines", 218)!;
        Assert.Equal(218, rows[0].Number);
        Assert.Equal(245, rows[^1].Number);
    }

    [Fact]
    public void An_offset_of_0_or_below_numbers_from_1()
    {
        Assert.Equal(1, ToolBody.StartOf("read_file", "{\"path\":\"a.cs\",\"offset\":0}"));
        Assert.Equal(1, ToolBody.StartOf("read_file", "{\"path\":\"a.cs\",\"offset\":-4}"));
        Assert.Equal(1, ToolBody.StartOf("read_file", "{\"path\":\"a.cs\"}"));
        Assert.Equal(1, ToolBody.StartOf("read_file", "{\"path\":\"a.cs\",\"offset\":\"x\"}"));
        Assert.Equal(1, ToolBody.StartOf("read_file", "not json"));
        Assert.Equal(1, ToolBody.StartOf("grep", "{\"offset\":40}"));
    }

    [Fact]
    public void A_file_that_ends_in_a_newline_has_no_numbered_row_past_its_end()
    {
        var rows = ToolBody.ReadFile(Lines(1, 3) + "\n", "4 lines", 1)!;
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows[^1].Number);
    }

    [Fact]
    public void A_truncated_read_ends_on_a_note_and_a_file_line_that_reads_truncated_stays_numbered()
    {
        var cut = ToolBody.ReadFile(Lines(1, 4, "\r\n") + "\r\n[truncated]", "4 lines (truncated)", 1)!;
        Assert.Equal(5, cut.Count);
        Assert.Equal((BodyKind.Note, BodyInk.Note, (int?)null), (cut[^1].Kind, cut[^1].Ink, cut[^1].Number));
        Assert.Equal("[truncated]", cut[^1].Text);

        var own = ToolBody.ReadFile(Lines(1, 3) + "\n[truncated]", "4 lines", 1)!;
        Assert.Equal(4, own.Count);
        Assert.Equal((BodyKind.Numbered, 4), (own[^1].Kind, own[^1].Number ?? 0));
    }

    [Fact]
    public void A_text_whose_line_count_differs_from_the_gloss_is_not_a_read()
    {
        Assert.Null(ToolBody.ReadFile(Lines(1, 3), "4 lines", 1));
        Assert.Null(ToolBody.ReadFile(Lines(1, 3), "2 lines", 1));
        Assert.Null(ToolBody.ReadFile(Lines(1, 3), "3 lines (truncated)", 1));
        Assert.Null(ToolBody.ReadFile(Lines(1, 3), "read 3 lines", 1));
    }

    [Fact]
    public void A_read_with_no_gloss_is_not_a_read() =>
        Assert.Null(ToolBody.ReadFile(Lines(1, 3), null, 1));

    [Fact]
    public void CRLF_and_LF_texts_give_the_same_rows()
    {
        Assert.Equal(ToolBody.ReadFile(Lines(1, 5), "5 lines", 1), ToolBody.ReadFile(Lines(1, 5, "\r\n"), "5 lines", 1));
        Assert.Equal(ToolBody.Plain(Lines(1, 5)), ToolBody.Plain(Lines(1, 5, "\r\n")));
    }

    [Fact]
    public void A_read_of_0_lines_has_no_rows() =>
        Assert.Empty(ToolBody.ReadFile("", "0 lines", 1)!);

    //the grep builder
    [Fact]
    public void Grep_rows_group_under_one_heading_per_path_and_each_match_names_its_heading()
    {
        var p = GrepRows.Parse("a.cs:7:     one\nb.cs:7: two\nb.cs:9: three")!;
        var rows = ToolBody.Grep(p);
        Assert.Equal(new[] { BodyKind.Heading, BodyKind.Numbered, BodyKind.Heading, BodyKind.Numbered, BodyKind.Numbered }, rows.Select(r => r.Kind));
        Assert.Equal(("a.cs", BodyInk.Heading), (rows[0].Text, rows[0].Ink));
        Assert.Equal(("one", 7, 0, BodyInk.Text), (rows[1].Text, rows[1].Number ?? 0, rows[1].Heading, rows[1].Ink));
        Assert.Equal(("two", 7, 2), (rows[3].Text, rows[3].Number ?? 0, rows[3].Heading));
        Assert.Equal(2, rows[4].Heading);
        Assert.Equal(-1, rows[0].Heading);
    }

    [Fact]
    public void A_path_that_returns_after_another_gets_a_second_heading()
    {
        var rows = ToolBody.Grep(GrepRows.Parse("a.cs:1: x\nb.cs:2: y\na.cs:3: z")!);
        Assert.Equal(new[] { "a.cs", "b.cs", "a.cs" }, rows.Where(r => r.Kind == BodyKind.Heading).Select(r => r.Text));
        Assert.Equal(4, rows[^1].Heading);
    }

    [Fact]
    public void Cap_notes_end_the_body_as_notes()
    {
        var rows = ToolBody.Grep(GrepRows.Parse("a.cs:1: x\n[capped at 200 matches]\n[capped at 50000 chars]")!);
        Assert.Equal(new[] { BodyKind.Heading, BodyKind.Numbered, BodyKind.Note, BodyKind.Note }, rows.Select(r => r.Kind));
        Assert.Equal(("[capped at 50000 chars]", BodyInk.Note), (rows[^1].Text, rows[^1].Ink));
    }

    [Fact]
    public void A_grep_with_no_match_has_no_rows() =>
        Assert.Empty(ToolBody.Grep(GrepRows.Parse(Globbing.NoMatches)!));

    //the plain builder
    [Fact]
    public void A_glob_cap_line_is_a_note()
    {
        var rows = ToolBody.Plain("a.cs\r\nb.cs\r\n[capped at 200 matches]");
        Assert.Equal(new[] { BodyKind.Plain, BodyKind.Plain, BodyKind.Note }, rows.Select(r => r.Kind));
        Assert.Equal(BodyInk.Note, rows[^1].Ink);
        Assert.Equal(BodyKind.Plain, ToolBody.Plain("[capped at 200 matches]\na.cs")[0].Kind);
    }

    [Fact]
    public void An_error_text_takes_the_error_ink()
    {
        Assert.All(ToolBody.Plain("not found\nsecond", error: true), r => Assert.Equal(BodyInk.Err, r.Ink));
        Assert.All(ToolBody.Plain("found\nsecond"), r => Assert.Equal(BodyInk.Text, r.Ink));
    }

    [Fact]
    public void An_empty_text_has_no_rows() => Assert.Empty(ToolBody.Plain(""));

    [Fact]
    public void A_plain_text_keeps_every_line_and_a_last_empty_one()
    {
        var rows = ToolBody.Plain("a\n\nb\n");
        Assert.Equal(new[] { "a", "", "b", "" }, rows.Select(r => r.Text));
        Assert.All(rows, r => Assert.Null(r.Number));
    }

    //both paths
    [Fact]
    public void Both_paths_fill_the_start_from_the_same_arguments()
    {
        var call = new ToolCall("c1", "read_file", JsonSerializer.Serialize(new { path = "a.cs", offset = 218, limit = 3 }));
        var result = new ToolResult(Lines(218, 3), Gloss: "3 lines");
        var s = new VtScreenSurface(120, 40);
        var gate = new object();
        var model = new TranscriptModel("coder");
        var p = new ChromePainter(s, T, gate, model)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        var r = new StreamRenderer(p, s, T, "coder", new ChromeTicker(p, gate), gate, model: model, convoTail: () => null);
        p.AltScreen.Enter();
        r.BeginTurn();
        r.OnToolCallStart(call);
        r.OnToolResult(call, result);
        r.EndTurn();
        var live = model.Items.OfType<ToolBlockItem>().Single();

        var convo = new Gatto.Core.Loop.Conversation("sys");
        convo.AddUser("go");
        convo.SetGattoRole("coder");
        convo.AddAssistant("", new[] { call });
        convo.AddToolResult("c1", result);
        var (_, rebuilt) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(new TranscriptModel("coder"), convo), T, "coder", glyphs: GlyphSet.Unicode);
        var replay = rebuilt.Items.OfType<ToolBlockItem>().Single();

        Assert.Equal(218, live.BodyStart);
        Assert.Equal(218, replay.BodyStart);
    }
}
