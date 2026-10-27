using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Render;

//an open ask_user block shows each question whole and under it what was chosen, its rows wrapped in the window and never cut
public sealed class AskBlockTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly GlyphSet U = GlyphSet.Unicode;
    private static readonly GlyphSet[] Sets = { GlyphSet.Unicode, GlyphSet.Ascii };

    private const string Typed = "gatto-lite, since the binary already carries that name in the installer, the docs and the site's download page";

    private static string Args(params (string Question, string Header)[] qs) =>
        JsonSerializer.Serialize(new { questions = qs.Select(q => new { question = q.Question, header = q.Header, options = new[] { "one", "two", "three" } }) });

    private static string Result(params (string Header, string[] Selected)[] answers) =>
        JsonSerializer.Serialize(answers.Select(a => new { header = a.Header, selected = a.Selected }));

    private static readonly (string Question, string Header)[] Four =
    {
        ("Which colour theme should the site default to?", "Theme"),
        ("What should the package be called on the shelf?", "Name"),
        ("Which platforms should the first build target?", "Targets"),
        ("Should the release notes thank the people who walked the build?", "Notes"),
    };

    private static readonly (string Header, string[] Selected)[] FourAnswers =
    {
        ("Theme", new[] { "dark" }), ("Name", new[] { Typed }), ("Targets", new[] { "Windows x64", "Windows ARM64" }), ("Notes", Array.Empty<string>()),
    };

    private static ToolBlockItem Block(string args, string result, bool open = true, bool error = false, string? gloss = "Theme: dark")
    {
        var r = new ToolResult(result, IsError: error, Gloss: error ? null : gloss);
        return new ToolBlockItem("ask_user", ItemRender.HeaderArgs("ask_user", args, U), ItemRender.ToolGloss(r, T, U), true, "coder")
            { Collapsed = !open, FullResult = result, RawGloss = r.Gloss, Parts = ItemRender.ToolGlossParts(r, T, U), IsError = error, AskArgs = args };
    }

    private static ToolBlockItem FourBlock(bool open = true) => Block(Args(Four), Result(FourAnswers), open);

    private static List<string> Visible(IEnumerable<string> rows) => [.. rows.Select(r => TermText.StripAnsiForWidth(r).TrimEnd())];

    //the body's rows as drawn, the rail and the footer left out
    private static List<RenderedRow> BodyRows(ToolBlockItem b, int width, GlyphSet g)
    {
        var l = b.Layout(width, T, g);
        var end = l.FooterRow >= 0 ? l.FooterRow : l.Rows.Count;
        return [.. l.Rows.Skip(l.OutputFirst).Take(end - l.OutputFirst)];
    }

    private static string Squeeze(string s) => string.Concat(s.Where(c => !char.IsWhiteSpace(c)));

    [Fact]
    public void THE_OPEN_BLOCK_SHOWS_EACH_QUESTION_AND_WHAT_WAS_CHOSEN()
    {
        var b = FourBlock();
        b.View = ShellView.All;
        var rows = Visible(b.Render(100, T, U));
        var text = string.Join("\n", rows);
        foreach (var (q, _) in Four) Assert.Contains($"{U.Box.Vertical} {q}", rows);
        Assert.Contains($"{U.Box.Vertical}   dark", rows);
        Assert.Contains($"{U.Box.Vertical}   Windows x64", rows);
        Assert.Contains($"{U.Box.Vertical}   Windows ARM64", rows);
        Assert.Contains($"{U.Box.Vertical}   skipped", rows);
        foreach (var unchosen in new[] { "one", "two", "three" }) Assert.DoesNotContain(rows, r => r.EndsWith(" " + unchosen, StringComparison.Ordinal));
        Assert.Contains("show less", text, StringComparison.Ordinal);
    }

    [Fact]
    public void EVERY_ROW_WRAPS_AND_NONE_IS_CUT()
    {
        var b = FourBlock();
        foreach (var g in Sets)
            for (var width = 30; width <= 160; width++)
            {
                foreach (var view in new[] { ShellView.Window, ShellView.All })
                {
                    b.View = view;
                    var body = BodyRows(b, width, g);
                    var joined = Squeeze(string.Concat(body.Select(r => TermText.StripAnsiForWidth(r.Text)).Select(r => r.Length > 2 ? r[2..] : "")));
                    Assert.DoesNotContain(g.Ellipsis, joined, StringComparison.Ordinal);
                    Assert.All(body, r => Assert.True(UnicodeWidth.Of(TermText.StripAnsiForWidth(r.Text)) <= width, $"{width}: {r.Text}"));
                    var expected = view == ShellView.All
                        ? new[] { Four[0].Question, "dark", Four[1].Question, Typed, Four[2].Question, "Windows x64", "Windows ARM64", Four[3].Question, "skipped" }
                        : new[] { Four[0].Question, "dark", Four[1].Question, Typed, Four[2].Question };
                    Assert.Equal(Squeeze(string.Concat(expected)), joined);
                }
            }
    }

    [Fact]
    public void A_SKIP_READS_SKIPPED_IN_THE_NOTE_INK()
    {
        var b = FourBlock();
        b.View = ShellView.All;
        Assert.Contains(b.Render(100, T, U), r => r.Contains(T.Paint("skipped", Theme.Dim), StringComparison.Ordinal));
    }

    [Fact]
    public void NINE_BODY_ROWS_SHOW_5_AND_THE_FOOTER_COUNTS_4_MORE()
    {
        var l = FourBlock().Layout(100, T, U);
        Assert.Equal($"{U.Box.Vertical}   {U.Ellipsis} 4 more {U.Dot} show all", TermText.StripAnsiForWidth(l.Rows[l.FooterRow].Text).TrimEnd());
    }

    [Fact]
    public void FOUR_BODY_ROWS_SHOW_WHOLE_WITH_NO_FOOTER()
    {
        var b = Block(Args(Four[0], Four[1]), Result(("Theme", new[] { "dark" }), ("Name", new[] { Typed + " " + Typed })));
        for (var width = 30; width <= 160; width++)
        {
            var l = b.Layout(width, T, U);
            Assert.True(BodyRows(b, width, U).Count(r => r.Continuation) > 0, $"the answer must wrap at {width}");
            Assert.Equal(-1, l.FooterRow);
            Assert.False(l.Hides);
        }
        var s = new VtScreenSurface(100, 40);
        var model = new TranscriptModel("coder");
        var closed = Block(Args(Four[0], Four[1]), Result(("Theme", new[] { "dark" }), ("Name", new[] { Typed + " " + Typed })), open: false);
        model.Append(closed);
        var p = new ChromePainter(s, T, new object(), model)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: U),
            RoleForTint = "coder",
        };
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        p.AltScreen.Enter();
        p.Repaint();
        p.Focus.Cycle(0, 100, p.ViewportRows());
        Assert.False(closed.Collapsed);
        Assert.Equal(ShellView.Window, closed.View);
        p.Focus.Cycle(0, 100, p.ViewportRows());
        Assert.True(closed.Collapsed);
    }

    [Fact]
    public void ABOVE_SHOWN_AND_MORE_ADD_UP_AT_EVERY_POSITION()
    {
        var b = FourBlock();
        for (var step = 0; step < 8; step++)
        {
            var l = b.Layout(100, T, U);
            var footer = TermText.StripAnsiForWidth(l.Rows[l.FooterRow].Text);
            var above = footer.Contains("above", StringComparison.Ordinal) ? int.Parse(footer.Split(U.Up)[1].Trim().Split(' ')[0]) : 0;
            var more = footer.Contains("more", StringComparison.Ordinal) ? int.Parse(footer.Split(U.Ellipsis)[1].Trim().Split(' ')[0]) : 0;
            var shown = BodyRows(b, 100, U).Count(r => !r.Continuation && TermText.StripAnsiForWidth(r.Text).Trim().Length > 1);
            Assert.Equal(9, above + shown + more);
            b.ScrollWindow(-1);
        }
    }

    [Fact]
    public void TEXT_THAT_DOES_NOT_PARSE_TAKES_PLAIN_ROWS()
    {
        var args = Args(Four);
        var cases = new[]
        {
            "the user chose dark",
            Result(("Theme", new[] { "dark" })),
            Result(("Name", new[] { "dark" }), ("Theme", new[] { "x" }), ("Targets", new[] { "y" }), ("Notes", new[] { "z" })),
        };
        foreach (var text in cases)
        {
            var ask = Block(args, text);
            var plain = new ToolBlockItem("ask_user", "", ask.Gloss, true, "coder") { Collapsed = false, FullResult = text, RawGloss = ask.RawGloss, Parts = ask.Parts };
            Assert.Equal(Visible(plain.Render(60, T, U)).Skip(1), Visible(ask.Render(60, T, U)).Skip(1));
        }
    }

    [Fact]
    public void AN_ERROR_RESULT_KEEPS_TODAYS_ROW_AND_BODY()
    {
        const string text = "interactive input unavailable in non-interactive mode";
        var ask = Block(Args(Four), text, error: true);
        var plain = new ToolBlockItem("ask_user", ask.Args, ask.Gloss, true, "coder")
            { Collapsed = false, FullResult = text, Parts = ask.Parts, IsError = true };
        foreach (var width in new[] { 40, 100 })
            Assert.Equal(Visible(plain.Render(width, T, U)), Visible(ask.Render(width, T, U)));
    }

    [Fact]
    public void THE_HEADER_COUNTS_THE_QUESTIONS()
    {
        Assert.Equal("4 questions", ItemRender.HeaderArgs("ask_user", Args(Four), U));
        Assert.Equal("1 question", ItemRender.HeaderArgs("ask_user", Args(Four[0]), U));
        Assert.Equal("", ItemRender.HeaderArgs("ask_user", "{\"questions\":\"none\"}", U));
        Assert.StartsWith("ask_user 4 questions", Visible(FourBlock(open: false).Render(100, T, U))[0][2..], StringComparison.Ordinal);
    }

    private static (StreamRenderer R, TranscriptModel M) Rich(GlyphSet g)
    {
        var s = new RecordingSurface { Width = 120, Height = 0 };
        var gate = new object();
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        var painter = new ChromePainter(s, T, gate) { Frame = new InputFrame(s, T, "coder", status, glyphs: g), RoleForTint = "coder" };
        var r = new StreamRenderer(painter, s, T, "coder", new ChromeTicker(painter, gate), gate, model: painter.Model, convoTail: () => null, glyphs: g);
        return (r, painter.Model);
    }

    private static (ToolBlockItem Live, ToolBlockItem Resumed) LiveAndResumed(ToolCall call, ToolResult result, GlyphSet g)
    {
        var (live, model) = Rich(g);
        var convo = new Conversation("sys");
        convo.AddAssistant("", new[] { call });
        live.BeginTurn();
        live.OnToolCallStart(call);
        live.OnToolResult(call, result);
        live.EndTurn();
        convo.AddToolResult(call.Id, result);
        var (_, stored) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(model, convo), T, "coder", g);
        return (model.Items.OfType<ToolBlockItem>().Single(), stored.Items.OfType<ToolBlockItem>().Single());
    }

    [Fact]
    public void A_RESUMED_ASK_DRAWS_THE_ROWS_IT_DREW_LIVE()
    {
        var call = new ToolCall("a1", "ask_user", Args(Four));
        var (live, resumed) = LiveAndResumed(call, new ToolResult(Result(FourAnswers), Gloss: "Theme: dark"), U);
        foreach (var open in new[] { false, true })
            foreach (var width in new[] { 80, 120 })
            {
                live.Collapsed = resumed.Collapsed = !open;
                Assert.Equal(Visible(live.Render(width, T, U)), Visible(resumed.Render(width, T, U)));
            }
        live.Collapsed = false;
        Assert.Contains(Visible(live.Render(120, T, U)), r => r.EndsWith(Four[0].Question, StringComparison.Ordinal));
    }

    [Fact]
    public void A_READS_RANGE_DRAWS_THE_ASCII_DASH_UNDER_THE_ASCII_SET()
    {
        var call = new ToolCall("r1", "read_file", JsonSerializer.Serialize(new { path = "a.cs", offset = 20, limit = 91 }));
        var (live, resumed) = LiveAndResumed(call, new ToolResult("one\ntwo", Gloss: "2 lines"), GlyphSet.Ascii);
        foreach (var b in new[] { live, resumed })
        {
            var header = Visible(b.Render(120, T, GlyphSet.Ascii))[0];
            Assert.Contains("(lines 20-110)", header, StringComparison.Ordinal);
            Assert.DoesNotContain(GlyphSet.Unicode.Range, header, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_COPY_ACROSS_A_QUESTION_AND_A_WRAPPED_ANSWER_YIELDS_TWO_LINES()
    {
        const int width = 60;
        var s = new VtScreenSurface(width, 40);
        var model = new TranscriptModel("coder");
        model.Append(Block(Args(Four[1]), Result(("Name", new[] { Typed }))));
        var p = new ChromePainter(s, T, new object(), model)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: U),
            RoleForTint = "coder",
        };
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        p.AltScreen.Enter();
        p.Repaint();
        var rows = s.Viewport.ToList();
        var y1 = rows.FindIndex(v => v.Contains(Four[1].Question, StringComparison.Ordinal));
        var y2 = rows.FindIndex(v => v.Contains("download page", StringComparison.Ordinal));
        Assert.True(y1 >= 0 && y2 > y1, string.Join("\n", rows));
        p.Mouse.Handle(new MouseEvent(0, y1, MouseKind.Press, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(width - 1, y2, MouseKind.Move, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(width - 1, y2, MouseKind.Release, MouseButton.Left, 0, 0), p.Width, p.ViewportRows());
        Assert.Equal(Four[1].Question + "\n" + Typed, p.Selection.CopyText(p.Width, T, null, U)?.Replace("\r\n", "\n"));
    }
}
