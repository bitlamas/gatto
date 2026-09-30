using System.Text.RegularExpressions;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//never cut a free-text draft with an ellipsis, the user is typing it now. read rows from the painter and sweep widths, an edge defect shows at only some
public class FreeTextWrapTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    //long enough to wrap and built from real words, so it has break points. no character repeats its neighbour, so a dropped or doubled cell changes the string
    private const string Draft =
        "the quick brown fox jumps over a lazy dog while nine zebras vex my bold judge from Kyiv";

    private static ConsoleKeyInfo Digit(char d) =>
        new(d, (ConsoleKey)((int)ConsoleKey.D0 + (d - '0')), false, false, false);

    private static ConsoleKeyInfo Special(ConsoleKey k) => new('\0', k, false, false, false);

    private static ConsoleKeyInfo[] Typed(string s) =>
        [.. s.Select(c => new ConsoleKeyInfo(c, ConsoleKey.NoName, false, false, false))];

    //the key source reads the panel and the caret in one go while Show runs, so rows and cursor can't be a frame apart. a read outside Show finds no live state
    private sealed class AtLastKey(ChromePainter painter, IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public IReadOnlyList<string>? Rows { get; private set; }
        public (int Row, int Col)? Caret { get; private set; }
        //the hook fires with six keys still queued, so those keys repaint after the resize, and a widget that ignores the new width fails
        public Action? PartWay { get; set; }
        public bool KeyAvailable => false;

        public ConsoleKeyInfo ReadKey()
        {
            if (_q.Count == 6) PartWay?.Invoke();
            //read at every key and keep the last non-empty panel, reading only at the final key would depend on paint order
            if (painter.State.PanelRows is { Count: > 0 } live)
            {
                Rows = live;
                Caret = painter.State.PanelCaret;
            }
            return _q.Dequeue();
        }
    }

    private static (RecordingSurface S, ChromePainter P, ChromeHandle H) Armed(int width)
    {
        var surface = new RecordingSurface { Width = width, Height = 0 };
        var gate = new object();
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        var painter = new ChromePainter(surface, T, gate)
        {
            Frame = new InputFrame(surface, T, "coder", status, glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
        var renderer = new StreamRenderer(painter, surface, T, "coder", new ChromeTicker(painter, gate), gate);
        return (surface, painter, new ChromeHandle { Painter = painter, Renderer = renderer });
    }

    private static SelectSpec FreeTextSpec() => new(
        ["Write new file test.txt"], "Do you want to proceed?",
        [new SelectOption("Yes"), new SelectOption("No")]) { FreeTextLabel = "Type my own answer…" };

    private static (string[] Rows, (int Row, int Col)? Caret) DraftAt(
        int width, string draft = Draft, Action<RecordingSurface>? resize = null)
    {
        var (surface, painter, handle) = Armed(width);
        var keys = new AtLastKey(painter,
            [Digit('3'), .. Typed(draft), Special(ConsoleKey.Enter)]);
        if (resize is not null) keys.PartWay = () => resize(surface);
        new SelectPrompt(surface, T, keys, pump: null, chrome: handle).Show(FreeTextSpec());

        var rows = (keys.Rows ?? []).Select(TermText.StripAnsiForWidth).ToArray();
        return (rows, keys.Caret);
    }

    //return the numbered draft row and the continuations below it. match the number rather than the text, so a missing row fails instead of returning nothing
    private static string[] DraftBlock(string[] rows)
    {
        var first = FirstRow(rows);
        var column = LabelColumn(rows[first]);

        //a continuation row is blank across the label column and has text after it. take the column from the first row, the caret glyph widens that one
        var last = first;
        while (last + 1 < rows.Length
               && rows[last + 1].Length > column
               && rows[last + 1][..column].Trim().Length == 0
               && rows[last + 1].Trim().Length > 0)
            last++;
        return rows[first..(last + 1)];
    }

    //match the number without anchoring at the line start, the cursor row has the caret glyph before it
    private static int FirstRow(string[] rows)
    {
        var at = Array.FindIndex(rows, r => Regex.IsMatch(r, @"^.{0,4}3\. "));
        Assert.True(at >= 0, "no row carries the free-text option's number:\n" + string.Join("\n", rows));
        return at;
    }

    //where the label text starts on the numbered row
    private static int LabelColumn(string row) =>
        row.IndexOf("3. ", StringComparison.Ordinal) + "3. ".Length;

    //join the rows into the one sentence the user reads, with the hanging indent removed
    private static string Rejoined(string[] block)
    {
        var head = block[0][LabelColumn(block[0])..];
        return string.Join(" ", new[] { head.TrimEnd() }
            .Concat(block.Skip(1).Select(r => r.Trim())));
    }

    //the rejoined rows must equal the draft exactly, with no ellipsis glyph at any tested width.
    [Theory]
    [InlineData(40)] [InlineData(52)] [InlineData(61)] [InlineData(72)]
    [InlineData(80)] [InlineData(97)] [InlineData(100)] [InlineData(120)]
    public void THE_WHOLE_DRAFT_IS_ON_SCREEN_AT_EVERY_WIDTH(int width)
    {
        var (rows, _) = DraftAt(width);
        var block = DraftBlock(rows);

        Assert.Equal(Draft, Rejoined(block));
        Assert.DoesNotContain(GlyphSet.Unicode.Ellipsis, string.Join("\n", block), StringComparison.Ordinal);
    }

    //the widths are measured to need more than one row, or the whole-draft claim is about a row that never wrapped
    [Theory]
    [InlineData(40)] [InlineData(52)] [InlineData(80)]
    public void AND_AT_THESE_WIDTHS_IT_REALLY_DOES_WRAP(int width) =>
        Assert.True(DraftBlock(DraftAt(width).Rows).Length > 1,
            $"the draft fits one row at {width}, so this width proves nothing about wrapping");

    //a continuation row starts at the label column, or it reads as another option. only wrapped widths are tested, a wide row proves nothing
    [Theory]
    [InlineData(40)] [InlineData(52)] [InlineData(61)] [InlineData(80)]
    public void THE_CONTINUATIONS_HANG_UNDER_THE_LABEL_COLUMN(int width)
    {
        var block = DraftBlock(DraftAt(width).Rows);
        var labelColumn = LabelColumn(block[0]);

        //the block finder uses the hanging indent, so without this guard a dropped indent gives a one-row block and the loop runs on nothing
        Assert.True(block.Length > 1,
            $"the draft is one row at {width}, so the hanging-indent assertions below run on nothing");

        foreach (var row in block.Skip(1))
        {
            Assert.Equal(labelColumn, row.Length - row.TrimStart().Length);
            Assert.DoesNotMatch(new Regex(@"^.{0,4}\d+\. "), row);
        }
    }

    //the caret sits at the end of the last row, a rows-only assertion can't see which row it points at
    [Theory]
    [InlineData(40)] [InlineData(52)] [InlineData(80)] [InlineData(120)]
    public void THE_CARET_LANDS_AT_THE_END_OF_THE_LAST_CONTINUATION(int width)
    {
        var (rows, caret) = DraftAt(width);
        var block = DraftBlock(rows);
        var first = FirstRow(rows);

        Assert.NotNull(caret);
        Assert.Equal(first + block.Length - 1, caret!.Value.Row);
        Assert.Equal(UnicodeWidth.Of(rows[caret.Value.Row].TrimEnd()), caret.Value.Col);
    }

    //paint at 120, resize to 60, paint again, and the property must hold on the second render where a cached width shows
    [Fact]
    public void THE_DRAFT_SURVIVES_A_RESIZE_MID_PROMPT()
    {
        var (rows, caret) = DraftAt(120, resize: s => s.Width = 60);
        var block = DraftBlock(rows);

        Assert.Equal(Draft, Rejoined(block));
        Assert.True(block.Length > 1, "the draft did not re-wrap at the narrower width");
        foreach (var row in block)
            Assert.True(UnicodeWidth.Of(row) <= 60, $"a row is {UnicodeWidth.Of(row)} cells at 60: '{row}'");

        Assert.NotNull(caret);
        Assert.Equal(block.Length - 1, caret!.Value.Row - FirstRow(rows));
    }

    //the draft is the only label that escapes truncation, an option row must not grow a second line
    [Fact]
    public void A_NON_DRAFT_LABEL_STILL_TRUNCATES_AT_A_NARROW_WIDTH()
    {
        var (surface, painter, handle) = Armed(40);
        var spec = new SelectSpec(["Write new file test.txt"], "Do you want to proceed?",
            [new SelectOption("Yes, allow this call and every other call that looks anything like it")]);
        //reuse the painter's surface, the panel composes at the width the painter reads and a fresh surface would compose at its own
        var keys = new AtLastKey(painter, [Special(ConsoleKey.UpArrow), Special(ConsoleKey.Enter)]);
        new SelectPrompt(surface, T, keys, pump: null, chrome: handle).Show(spec);

        var rows = (keys.Rows ?? []).Select(TermText.StripAnsiForWidth).ToArray();
        var at = Array.FindIndex(rows, r => Regex.IsMatch(r, @"^.{0,4}1\. "));
        Assert.True(at >= 0, "no row carries option 1:\n" + string.Join("\n", rows));
        var row = rows[at];

        Assert.DoesNotContain("anything like it", row, StringComparison.Ordinal);
        Assert.True(UnicodeWidth.Of(row) <= 40, $"the truncated row is {UnicodeWidth.Of(row)} cells");

        //absence of the tail proves nothing on its own, a wrapped row lacks it too. assert that no continuation row follows
        var column = LabelColumn(row.Replace("1. ", "3. ", StringComparison.Ordinal));
        Assert.False(at + 1 < rows.Length
            && rows[at + 1].Length > column
            && rows[at + 1][..column].Trim().Length == 0
            && rows[at + 1].Trim().Length > 0,
            "a continuation row appeared under a non-draft label:\n" + string.Join("\n", rows));
    }
}
