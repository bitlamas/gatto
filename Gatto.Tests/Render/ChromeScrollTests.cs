using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Render;

//with the mouse on, the chrome is the last rows of one document, so scrolling up moves it down and off the window and the transcript takes its rows
public class ChromeScrollTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private const int W = 60;
    private const int H = 30;

    private static (ChromePainter P, VtScreenSurface S) Wire(bool chromeScrolls = true)
    {
        var s = new VtScreenSurface(W, H);
        var gate = new object();
        var model = new TranscriptModel("coder");
        model.Append(new AssistantBlockItem([.. Enumerable.Range(0, 200).Select(i => $"t{i:000}")], "coder"));
        var p = new ChromePainter(s, T, gate, model)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
            ChromeScrolls = chromeScrolls,
        };
        p.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        p.AltScreen.Enter();
        p.Repaint();
        return (p, s);
    }

    private static List<string> Chrome(ChromePainter p) => [.. p.ComposeChromeBlock(W, H).Rows.Select(r => r.Visible.TrimEnd())];

    private static List<string> Screen(VtScreenSurface s) => [.. s.Viewport.Select(r => r.TrimEnd())];

    private static void Up(ChromePainter p, int rows)
    {
        p.Scroll.ScrollBy(rows, W, p.ViewportRows());
        p.Repaint();
    }

    private static MouseEvent Press(int x, int y) => new(x, y, MouseKind.Press, MouseButton.Left, 0, 0);

    //the status line goes first, then the rule, the composer and the top rule, and each row the chrome gives up goes to the transcript
    [Fact]
    public void Scrolled_up_the_chrome_keeps_its_top_rows_and_the_transcript_takes_the_rest()
    {
        var (p, s) = Wire();
        var chrome = Chrome(p);
        var c = chrome.Count;

        for (var n = 1; n < c; n++)
        {
            Up(p, 1);
            var screen = Screen(s);
            var shown = c - n;
            Assert.Equal(chrome.Take(shown), screen.Skip(H - shown));
            Assert.Contains("t199", screen[H - shown - 1], StringComparison.Ordinal);
            Assert.DoesNotContain(screen, r => r.Contains("Ctrl+End", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void The_jump_hint_shows_only_once_the_chrome_is_gone()
    {
        var (p, s) = Wire();
        var c = Chrome(p).Count;

        Up(p, c);
        var gone = Screen(s);
        Assert.DoesNotContain(gone, r => r.Contains("qwen", StringComparison.Ordinal));
        Assert.Contains("Ctrl+End", gone[^1], StringComparison.Ordinal);

        Up(p, 5);
        Assert.Contains("Ctrl+End", Screen(s)[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_caret_hides_once_its_row_is_scrolled_away()
    {
        var (p, s) = Wire();
        var block = p.ComposeChromeBlock(W, H);
        var below = block.Rows.Count - block.CaretRow;   //rows from the caret's row to the chrome's last row
        Assert.True(s.CursorVisible);

        Up(p, below - 1);
        Assert.True(s.CursorVisible);
        Up(p, 1);
        Assert.False(s.CursorVisible);
    }

    //a press maps through the rows on screen, a chrome row to its index in the whole block and the row above it to the transcript
    [Fact]
    public void A_press_on_a_partly_shown_chrome_maps_to_the_row_drawn_there()
    {
        var (p, s) = Wire();
        var c = Chrome(p).Count;
        Up(p, 2);
        var topRule = H - (c - 2);

        p.Mouse.Handle(Press(3, topRule), W, p.ViewportRows());
        Assert.Equal(0, p.Selection.ChromeCurrent!.Value.Anchor.Row);

        p.Mouse.Handle(Press(3, topRule - 1), W, p.ViewportRows());
        Assert.Null(p.Selection.ChromeCurrent);
        Assert.NotNull(p.Selection.Current);
    }

    //the selection and the copy read the whole chrome block, so a one-row scroll neither clears a chrome selection nor shortens what it copies
    [Fact]
    public void A_chrome_selection_survives_a_one_row_scroll_and_the_rows_stay_whole()
    {
        var (p, s) = Wire();
        var c = Chrome(p).Count;
        Up(p, 1);
        var topRule = H - (c - 1);
        p.Mouse.Handle(Press(2, topRule), W, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(12, topRule, MouseKind.Move, MouseButton.Left, 0, 0), W, p.ViewportRows());
        p.Mouse.Handle(new MouseEvent(12, topRule, MouseKind.Release, MouseButton.Left, 0, 0), W, p.ViewportRows());
        Assert.NotNull(p.Selection.ChromeCurrent);

        Up(p, 1);

        Assert.NotNull(p.Selection.ChromeCurrent);
        Assert.Equal(c, p.ChromeRowInfo.Count);
    }

    //a transcript drag that reaches the partly shown chrome stops on the transcript's last row and scrolls, the edge is where the chrome begins
    [Fact]
    public void A_transcript_drag_into_the_partly_shown_chrome_extends_to_the_transcript_edge()
    {
        var (p, s) = Wire();
        var c = Chrome(p).Count;
        Up(p, 2);
        var topRule = H - (c - 2);
        p.Mouse.Handle(Press(3, 5), W, p.ViewportRows());
        var anchor = p.Selection.Current!.Value.Anchor;

        p.Mouse.Handle(new MouseEvent(3, topRule + 1, MouseKind.Move, MouseButton.Left, 0, 0), W, p.ViewportRows());

        Assert.NotEqual(anchor.Rel, p.Selection.Current!.Value.Head.Rel);
    }

    //a chrome drag that leaves upward clamps to the chrome's first row on screen, wherever the scroll put it
    [Fact]
    public void A_chrome_drag_up_into_the_transcript_clamps_to_the_first_chrome_row_on_screen()
    {
        var (p, s) = Wire();
        var c = Chrome(p).Count;
        Up(p, 2);
        var topRule = H - (c - 2);
        p.Mouse.Handle(Press(3, topRule + 1), W, p.ViewportRows());
        Assert.Equal(1, p.Selection.ChromeCurrent!.Value.Anchor.Row);

        p.Mouse.Handle(new MouseEvent(3, 2, MouseKind.Move, MouseButton.Left, 0, 0), W, p.ViewportRows());

        Assert.Equal(0, p.Selection.ChromeCurrent!.Value.Head.Row);
    }

    //the split takes the composed block as it is, so a capped panel and its hidden-rows marks scroll away as composed
    [Fact]
    public void A_marked_panel_scrolls_away_as_composed()
    {
        var (p, s) = Wire();
        p.SetPanel([.. Enumerable.Range(0, 200).Select(i => $"  p{i:000}")]);
        var chrome = Chrome(p);
        Assert.Contains(chrome, r => r.Contains("more", StringComparison.Ordinal));

        Up(p, 2);

        Assert.Equal(chrome.Take(chrome.Count - 2), Screen(s).Skip(H - (chrome.Count - 2)));
    }

    private const string Pin = "↓ a prompt is waiting for your answer · Ctrl+End or click";

    private static readonly string[] Prompt = ["  Allow touch?", "  1. Yes", "  2. No"];

    //the pin waits until a row of the prompt is off the window, then takes the last row under the chrome rows still on screen
    [Fact]
    public void A_prompt_with_a_row_off_the_window_pins_one_row_at_the_bottom()
    {
        var (p, s) = Wire();
        p.SetPanel(Prompt);
        var chrome = Chrome(p);
        var lastPrompt = chrome.FindLastIndex(r => r.Contains("2. No", StringComparison.Ordinal));
        var below = chrome.Count - lastPrompt;   //the scroll that leaves the prompt's last row as the last chrome row on screen

        Up(p, below - 1);
        Assert.DoesNotContain(Screen(s), r => r == Pin);

        Up(p, 1);
        var screen = Screen(s);
        Assert.Equal(Pin, screen[^1]);
        Assert.Equal(chrome.Take(lastPrompt), screen.Skip(H - 1 - lastPrompt).Take(lastPrompt));
    }

    [Fact]
    public void With_the_chrome_gone_the_pin_takes_the_hints_row()
    {
        var (p, s) = Wire();
        p.SetPanel(Prompt);
        Up(p, 20);
        var screen = Screen(s);

        Assert.Equal(Pin, screen[^1]);
        Assert.DoesNotContain(screen, r => r.EndsWith("more · Ctrl+End or click", StringComparison.Ordinal));
    }

    [Fact]
    public void An_info_panel_scrolled_away_pins_nothing()
    {
        var (p, s) = Wire();
        p.SetInfoPanel((_, _) => ["  context", "  rows", "  more rows"]);
        Up(p, 20);

        Assert.DoesNotContain(Screen(s), r => r == Pin);
    }

    //a prompt that opens while the reader is away keeps the window's top row where it was
    [Fact]
    public void A_prompt_opening_while_away_does_not_move_the_view()
    {
        var (p, s) = Wire();
        Up(p, 20);
        var top = Screen(s)[0];

        p.SetPanel(Prompt);

        Assert.Equal(top, Screen(s)[0]);
        Assert.Equal(Pin, Screen(s)[^1]);
    }

    [Fact]
    public void A_click_on_the_pin_returns_the_view()
    {
        var (p, s) = Wire();
        p.SetPanel(Prompt);
        Up(p, 20);

        p.Mouse.Handle(Press(3, H - 1), W, p.ViewportRows());
        p.Repaint();

        Assert.True(p.Scroll.Following);
        Assert.Contains(Screen(s), r => r.Contains("2. No", StringComparison.Ordinal));
    }

    private static void Stream(ChromePainter p, int lines)
    {
        p.Model.Append(new AssistantBlockItem([.. Enumerable.Range(0, lines).Select(i => $"s{i:00}")], "coder"));
        p.NotifyCommitted();
    }

    private static int HintCount(string row) => int.Parse(row.Split(' ')[1]);

    //a reply that grows below a reader with the chrome gone leaves the window's top row in place, and the hint counts the new rows
    [Fact]
    public void Streaming_while_away_holds_the_view_and_the_hint_count_grows()
    {
        var (p, s) = Wire();
        Up(p, 20);
        var before = Screen(s);

        Stream(p, 7);
        var after = Screen(s);

        Assert.Equal(before[0], after[0]);
        Assert.Equal(HintCount(before[^1]) + 8, HintCount(after[^1]));   //7 rows and the block's leading blank
    }

    //with part of the chrome on screen the view holds its top row too, so the growing reply pushes the chrome further off the bottom
    [Fact]
    public void Streaming_while_partly_away_slides_the_chrome_down()
    {
        var (p, s) = Wire();
        var chrome = Chrome(p);
        Up(p, 1);
        var top = Screen(s)[0];

        Stream(p, 1);
        var after = Screen(s);

        Assert.Equal(top, after[0]);
        Assert.Equal(chrome.Take(chrome.Count - 3), after.Skip(H - (chrome.Count - 3)));
    }

    //with the mouse off nothing moves the chrome, the transcript scrolls above it and the hint sits just above it, as before
    [Fact]
    public void With_the_mouse_off_the_chrome_stays_whole_and_the_hint_sits_above_it()
    {
        var (p, s) = Wire(chromeScrolls: false);
        var chrome = Chrome(p);
        var c = chrome.Count;

        Up(p, 5);
        var screen = Screen(s);

        Assert.Equal(chrome, screen.Skip(H - c));
        Assert.Contains("Ctrl+End", screen[H - c - 1], StringComparison.Ordinal);
        Assert.True(s.CursorVisible);
    }
}
