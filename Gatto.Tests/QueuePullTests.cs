using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Term;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a queued message draws verbatim and wrapped like a sent user message. up-arrow pulls the whole queue into the composer, ahead of the typed text
public class QueuePullTests : IDisposable
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-queue-pull-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    //the recall case gets a real history file, so the editor's own machinery runs rather than a stub that agrees with anything
    private History H(params string[] entries)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".txt");
        //history stores one json string per line, so plain text lines would make the recall case pass for the wrong reason
        if (entries.Length > 0)
            File.WriteAllLines(path, entries.Select(e => System.Text.Json.JsonSerializer.Serialize(e)));
        return new History(path);
    }

    //scripted keys as the IComposerSource LineEditor drains
    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IComposerSource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        public ComposerInput Read() => new ComposerInput.Key(_q.Dequeue());
    }

    private static ConsoleKeyInfo Key(ConsoleKey k) => new('\0', k, false, false, false);
    private static ConsoleKeyInfo Ch(char c) => new(c, ConsoleKey.NoName, false, false, false);
    private static ConsoleKeyInfo[] Type(string s) => [.. s.Select(Ch)];
    private static ConsoleKeyInfo Enter() => Key(ConsoleKey.Enter);

    //pulled messages keep their order, sit before the text already typed, and leave the cursor at the end.
    [Fact]
    public void UP_ON_THE_TOP_ROW_PULLS_EVERY_QUEUED_MESSAGE_INTO_THE_COMPOSER()
    {
        var q = new DispatchQueue(10);
        q.TryEnqueue("one");
        q.TryEnqueue("two");

        var keys = new ScriptedKeys([.. Type("typed"), Key(ConsoleKey.UpArrow), Enter()]);
        var editor = new LineEditor(keys, H());

        Assert.Equal("one\n\ntwo\n\ntyped", editor.Read(_ => { }, pullQueue: q.DrainAll));
        Assert.Equal(0, q.Count);
    }

    //assert on the last published view, that's what positions the terminal cursor. the returned string says nothing about the caret
    [Fact]
    public void AND_THE_CURSOR_IS_LEFT_AT_THE_END_OF_WHAT_WAS_TYPED()
    {
        var q = new DispatchQueue(10);
        q.TryEnqueue("one");
        q.TryEnqueue("two");

        var views = new List<EditorView>();
        var keys = new ScriptedKeys([.. Type("typed"), Key(ConsoleKey.UpArrow), Enter()]);
        new LineEditor(keys, H()).Read(views.Add, pullQueue: q.DrainAll);

        var last = views[^1];
        Assert.Equal(4, last.CursorLine);          //the buffer is the two pulled messages with blank separators, then the typed line, which is line four.
        Assert.Equal("typed".Length, last.CursorCol);
    }

    //a pull must take the permits with the messages, or the dispatcher wakes on text that's already in the composer. after one take, a second must time out
    [Fact]
    public async Task THE_PULL_CONSUMES_EVERY_PERMIT_SO_NO_PHANTOM_SURVIVES()
    {
        var q = new DispatchQueue(10);
        q.TryEnqueue("one");
        q.TryEnqueue("two");

        var keys = new ScriptedKeys([Key(ConsoleKey.UpArrow), Enter()]);
        new LineEditor(keys, H()).Read(_ => { }, pullQueue: q.DrainAll);

        q.TryEnqueue("after");
        using var ok = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal("after", await q.TakeAsync(ok.Token));

        using var brief = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => q.TakeAsync(brief.Token));
    }

    //the pull is a boundary action, so on a lower line the arrow only moves the cursor
    [Fact]
    public void UP_ON_A_LOWER_LINE_MOVES_THE_CURSOR_AND_LEAVES_THE_QUEUE_ALONE()
    {
        var q = new DispatchQueue(10);
        q.TryEnqueue("queued");

        var views = new List<EditorView>();
        //shift-enter opens a second line, so the cursor sits below the top row and up is an ordinary move there
        var keys = new ScriptedKeys(
            [.. Type("first"), new('\r', ConsoleKey.Enter, true, false, false), .. Type("second"),
             Key(ConsoleKey.UpArrow), Enter()]);
        var text = new LineEditor(keys, H()).Read(views.Add, pullQueue: q.DrainAll);

        Assert.Equal("first\nsecond", text);
        Assert.Equal(1, q.Count);
        Assert.Equal(0, views[^1].CursorLine);
    }

    //a pull that answers empty must not swallow the history recall
    [Fact]
    public void UP_WITH_AN_EMPTY_QUEUE_STILL_RECALLS_HISTORY()
    {
        var q = new DispatchQueue(10);
        var keys = new ScriptedKeys([Key(ConsoleKey.UpArrow), Enter()]);

        var text = new LineEditor(keys, H("remembered")).Read(_ => { }, pullQueue: q.DrainAll);

        Assert.Equal("remembered", text);
    }

    private static ChromePainter Painter(int width, int height)
    {
        var surface = new RecordingSurface { Width = width, Height = height };
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        return new ChromePainter(surface, T, new object())
        {
            Frame = new InputFrame(surface, T, "coder", status, glyphs: GlyphSet.Unicode),
            RoleForTint = "coder",
        };
    }

    //return only the queue rows that hold content, or a row count passes on one unwrapped row beside a blank
    private static IReadOnlyList<ChromeRow> QueueRows(ChromePainter p, int width, int height) =>
        [.. p.ComposeChromeBlock(width, height).Rows
            .Where(r => r.Region == ChromeRegion.Queue && r.Visible.Trim().Length > 0)];

    //a message wider than the row must render whole across wrapped rows. a wrap defect only shows where a row ends exactly at the edge
    [Theory]
    [InlineData(40)] [InlineData(52)] [InlineData(61)] [InlineData(80)] [InlineData(120)]
    public void A_QUEUED_MESSAGE_WIDER_THAN_THE_ROW_RENDERS_WHOLE(int width)
    {
        const string Message =
            "please read the whole file before you answer and tell me what the second function does";
        var p = Painter(width, 40);
        p.State.QueueTexts = [Message];

        var rows = QueueRows(p, width, 40);
        var rejoined = string.Join(" ", rows.Select(r => r.Visible.Trim()))
            .Replace(GlyphSet.Unicode.Prompt + " ", "", StringComparison.Ordinal);

        Assert.Equal(Message, rejoined);
        Assert.DoesNotContain(GlyphSet.Unicode.Ellipsis, string.Join("\n", rows.Select(r => r.Visible)),
            StringComparison.Ordinal);
    }

    //these widths must really produce more than one row, or the whole-render test never sees a wrap
    [Theory]
    [InlineData(40)] [InlineData(52)] [InlineData(61)]
    public void AND_AT_THESE_WIDTHS_IT_REALLY_DOES_WRAP(int width)
    {
        var p = Painter(width, 40);
        p.State.QueueTexts = ["please read the whole file before you answer and tell me what the second function does"];

        Assert.True(QueueRows(p, width, 40).Count > 1, $"one row at {width}: nothing wrapped");
    }

    //exactly one blank row separates the queue from the purr above, so assert where the queue sits rather than how many rows there are
    [Fact]
    public void A_BLANK_ROW_SEPARATES_THE_QUEUE_FROM_THE_PURR_ABOVE_IT()
    {
        var p = Painter(80, 40);
        p.State.PurrText = "purrrrr    4m 37s";
        p.State.QueueTexts = ["why do you use read_file instead of recall_memory?"];

        var rows = p.ComposeChromeBlock(80, 40).Rows;
        var purr = rows.ToList().FindIndex(r => r.Visible.Contains("purrrrr", StringComparison.Ordinal));
        var queue = rows.ToList().FindIndex(r => r.Region == ChromeRegion.Queue
            && r.Visible.Trim().Length > 0);

        Assert.True(purr >= 0 && queue > purr, $"purr at {purr}, queue at {queue}");
        Assert.Equal(purr + 2, queue);
        Assert.Equal("", rows[purr + 1].Visible.Trim());
    }

    //exactly one blank row sits between the queue and the composer's top rule, a second blank or none would still pass the test above
    [Fact]
    public void AND_EXACTLY_ONE_BLANK_ROW_STILL_SEPARATES_IT_FROM_THE_COMPOSER_BELOW()
    {
        var p = Painter(80, 40);
        p.State.PurrText = "purrrrr    4m 37s";
        p.State.QueueTexts = ["why do you use read_file instead of recall_memory?"];

        var rows = p.ComposeChromeBlock(80, 40).Rows.ToList();
        var last = rows.FindLastIndex(r => r.Region == ChromeRegion.Queue && r.Visible.Trim().Length > 0);

        Assert.True(last >= 0, "no queue row was painted");
        Assert.Equal("", rows[last + 1].Visible.Trim());
        Assert.NotEqual("", rows[last + 2].Visible.Trim());   //the row after the blank must be the composer's top rule
    }

    //the blank above the queue is in the queue's region, so the height clamp drops it with the rows. a small terminal would keep a blank separating nothing
    [Fact]
    public void THE_SEPARATOR_IS_DROPPED_WITH_THE_BLOCK_IT_SEPARATES()
    {
        var dropped = 0;
        var kept = 0;
        for (var h = 6; h <= 24; h++)
        {
            var p = Painter(60, h);
            p.State.PurrText = "purring";
            p.State.QueueTexts =
                [.. Enumerable.Range(1, 12).Select(i => $"message number {i} with several words in it")];

            var all = p.ComposeChromeBlock(60, h).Rows.Where(r => r.Region == ChromeRegion.Queue).ToList();
            var content = all.Count(r => r.Visible.Trim().Length > 0);
            if (content == 0)
            {
                Assert.True(all.Count == 0, $"height {h}: {all.Count} queue row(s) left with nothing to separate");
                dropped++;
            }
            else
            {
                kept++;
            }
        }
        //both sides of the boundary must be visited, or the loop is a claim about heights that never dropped anything
        Assert.True(dropped > 0, "no height dropped the queue, so the rule was never tested");
        Assert.True(kept > 0, "no height kept the queue, so the fixture says nothing about the normal case");
    }

    //a queue taller than a third of the screen rolls the oldest up. the composer sits below, so the newest stay nearest it
    [Fact]
    public void A_TALL_QUEUE_ROLLS_THE_OLDEST_UP_AND_KEEPS_THE_NEWEST_NEAREST_THE_COMPOSER()
    {
        var p = Painter(60, 21);                                   //height 21 puts the queue's roll-up ceiling at seven rows.
        p.State.QueueTexts = [.. Enumerable.Range(1, 10).Select(i => "m" + i)];

        var rows = QueueRows(p, 60, 21);

        Assert.Equal(-1, rows[0].RegionRow);                       //region row minus one marks the roll-up line, which must lead the block.
        Assert.Contains("(+3 more)", rows[0].Visible, StringComparison.Ordinal);
        Assert.Contains("m10", rows[^1].Visible, StringComparison.Ordinal);
        Assert.DoesNotContain(rows, r => r.Visible.Contains("m3", StringComparison.Ordinal));
    }

    //the whole queue shows with no roll-up row when it fits, or a build that always rolled something up would pass the test above
    [Fact]
    public void A_QUEUE_THAT_FITS_SHOWS_ALL_OF_IT_WITH_NO_ROLLUP()
    {
        var p = Painter(60, 40);
        p.State.QueueTexts = ["a", "b", "c"];

        var rows = QueueRows(p, 60, 40);

        Assert.Equal(3, rows.Count);
        Assert.DoesNotContain(rows, r => r.RegionRow < 0);
        Assert.Equal([0, 1, 2], rows.Select(r => r.RegionRow));
    }

    //the chrome rows must never outgrow the viewport, and nothing else in this file would catch an overgrown one
    [Theory]
    [InlineData(10)] [InlineData(16)] [InlineData(24)] [InlineData(40)]
    public void THE_CHROME_NEVER_OUTGROWS_THE_VIEWPORT(int height)
    {
        var p = Painter(60, height);
        p.State.QueueTexts = [.. Enumerable.Range(1, 12).Select(i => $"message number {i} with several words in it")];

        Assert.True(p.ComposeChromeBlock(60, height).Rows.Count <= height,
            $"the chrome is {p.ComposeChromeBlock(60, height).Rows.Count} rows on a {height}-row screen");
    }

    //rows must hold the message's own words, an ellipsis means a truncated render came back
    [Fact]
    public void NO_ROW_IS_A_GIST()
    {
        var p = Painter(80, 40);
        var message = new string('x', 400);
        p.State.QueueTexts = [message];

        var rows = QueueRows(p, 80, 40);

        Assert.Equal(400, rows.Sum(r => r.Visible.Trim().Length) - GlyphSet.Unicode.Prompt.Length - 1);
    }

    //esc on an empty composer mid-turn must abort the turn, and nothing drops the newest queued message
    [Fact]
    public void ESC_ON_AN_EMPTY_COMPOSER_MID_TURN_ABORTS_AND_DOES_NOT_TOUCH_THE_QUEUE()
    {
        Assert.Equal(Gatto.Repl.Repl.CancelAction.AbortTurn,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.Escape, hasSelection: false, turnInFlight: true,
                composerEmpty: true));
    }

    //with no turn running, esc passes through to the editor and leaves the queue alone. a queued message stays where it was
    [Fact]
    public void ESC_AT_REST_LEAVES_THE_QUEUE_ALONE()
    {
        Assert.Equal(Gatto.Repl.Repl.CancelAction.PassThrough,
            Gatto.Repl.Repl.DecideCancel(ConsoleKey.Escape, hasSelection: false, turnInFlight: false,
                composerEmpty: true));

        var q = new DispatchQueue(10);
        q.TryEnqueue("survives");
        var keys = new ScriptedKeys([Key(ConsoleKey.Escape), Enter()]);
        new LineEditor(keys, H()).Read(_ => { }, pullQueue: q.DrainAll, chords: new ArmedChord());

        Assert.Equal(1, q.Count);
    }
}
