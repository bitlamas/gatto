using System.Text;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//a watched folder hands each new letter over once, as its path and body, read once its writer lets go. it pauses after K letters with no typed message between
public sealed class WatchTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-watch-").FullName;
    private readonly List<string> _got = new();
    private readonly List<string> _rows = new();
    private Watch? _watch;

    public void Dispose()
    {
        _watch?.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Folder => Path.Combine(_root, "inbox");

    private Watch Arm(string? folder = null)
    {
        _watch = Watch.Arm(folder ?? Folder, (_, text) => { lock (_got) _got.Add(text); return true; }, row => { lock (_rows) _rows.Add(row); });
        _watch.Start();
        return _watch;
    }

    private List<string> Got() { lock (_got) return [.. _got]; }

    private void WaitFor(int count) => Waits.Until(() => Got().Count >= count, () => $"{Got().Count} of {count}: " + string.Join(" | ", Got()));

    //long enough for a scan that should not happen to have happened
    private static Task Settle() => Task.Delay(400);

    private string Letter(string name, string body)
    {
        var path = Path.Combine(Folder, name);
        File.WriteAllText(path, body);
        return path;
    }

    [Fact]
    public void A_CREATED_LETTER_ARRIVES_AS_ITS_ABSOLUTE_PATH_A_BLANK_LINE_AND_ITS_BODY()
    {
        Arm();
        var path = Letter("a.md", "hello\nworld");
        WaitFor(1);
        Assert.Equal(path + "\n\nhello\nworld", Assert.Single(Got()));
        Assert.True(Path.IsPathRooted(path));
    }

    [Fact]
    public async Task AN_EXCLUSIVE_WRITER_IS_WAITED_FOR_AND_THE_LETTER_IS_READ_ONCE_IT_CLOSES()
    {
        Arm();
        var path = Path.Combine(Folder, "held.md");
        using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            writer.Write(Encoding.UTF8.GetBytes("first half "));
            await Settle();
            Assert.Empty(Got());
            writer.Write(Encoding.UTF8.GetBytes("second half"));
        }
        WaitFor(1);
        Assert.Equal(path + "\n\nfirst half second half", Assert.Single(Got()));
    }

    [Fact]
    public async Task A_WRITER_THAT_ALLOWS_SHARED_READ_IS_STILL_WAITED_FOR_AND_NEVER_READ_MID_WRITE()
    {
        Arm();
        var path = Path.Combine(Folder, "shared.md");
        using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
        {
            writer.Write(Encoding.UTF8.GetBytes("first half "));
            writer.Flush();
            await Settle();
            Assert.Empty(Got());
            writer.Write(Encoding.UTF8.GetBytes("second half"));
        }
        WaitFor(1);
        Assert.Equal(path + "\n\nfirst half second half", Assert.Single(Got()));
    }

    [Fact]
    public void A_LETTER_RENAMED_INTO_PLACE_ARRIVES()
    {
        Arm();
        var temp = Path.Combine(Folder, "draft.tmp");
        File.WriteAllText(temp, "written whole first");
        File.Move(temp, Path.Combine(Folder, "draft.md"));
        WaitFor(1);
        Assert.EndsWith("draft.md\n\nwritten whole first", Assert.Single(Got()));
    }

    [Fact]
    public void A_LETTER_MOVED_IN_FROM_ANOTHER_FOLDER_ARRIVES()
    {
        Arm();
        var outside = Path.Combine(_root, "elsewhere.md");
        File.WriteAllText(outside, "from outside");
        File.Move(outside, Path.Combine(Folder, "moved.md"));
        WaitFor(1);
        Assert.EndsWith("moved.md\n\nfrom outside", Assert.Single(Got()));
    }

    [Fact]
    public void A_SECOND_LETTER_IS_A_SECOND_ENTRY_IN_THE_ORDER_THEY_CAME()
    {
        Arm();
        Letter("b.md", "first");
        WaitFor(1);
        Letter("a.md", "second");
        WaitFor(2);
        Assert.Equal(["first", "second"], Got().Select(t => t.Split("\n\n", 2)[1]));
    }

    [Fact]
    public void LETTERS_PRESENT_AT_ARM_ARRIVE_IN_NAME_ORDER()
    {
        Directory.CreateDirectory(Folder);
        Letter("2026-10-27-c.md", "c");
        Letter("2026-10-27-a.md", "a");
        Letter("2026-10-27-b.md", "b");
        Arm();
        WaitFor(3);
        Assert.Equal(["a", "b", "c"], Got().Select(t => t.Split("\n\n", 2)[1]));
    }

    [Fact]
    public async Task A_RESCAN_HANDS_OVER_ONLY_WHAT_WAS_NOT_SEEN()
    {
        var w = Arm();
        Letter("a.md", "a");
        WaitFor(1);
        w.Rescan();
        w.Rescan();
        await Settle();
        Assert.Single(Got());
    }

    [Fact]
    public async Task AFTER_OFF_A_NEW_LETTER_IS_NOT_HANDED_OVER()
    {
        var w = Arm();
        w.Dispose();
        Letter("late.md", "late");
        await Settle();
        Assert.Empty(Got());
    }

    [Fact]
    public async Task A_WRITER_STILL_HOLDING_ITS_LETTER_AT_OFF_HANDS_NOTHING_OVER_WHEN_IT_CLOSES()
    {
        var w = Arm();
        using (var writer = new FileStream(Path.Combine(Folder, "slow.md"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            writer.Write(Encoding.UTF8.GetBytes("body"));
            await Settle();
            w.Dispose();
        }
        await Settle();
        Assert.Empty(Got());
    }

    [Fact]
    public async Task A_FILE_THAT_IS_NOT_MARKDOWN_IS_NOT_A_LETTER()
    {
        Arm();
        File.WriteAllText(Path.Combine(Folder, "notes.txt"), "no");
        File.WriteAllText(Path.Combine(Folder, "page.mdx"), "no");
        await Settle();
        Assert.Empty(Got());
    }

    [Fact]
    public async Task A_LETTER_DELETED_WHILE_ITS_WRITER_HOLDS_IT_IS_NOT_HANDED_OVER_AND_THE_WATCH_GOES_ON()
    {
        Arm();
        var path = Path.Combine(Folder, "gone.md");
        using (var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Delete))
        {
            writer.Write(Encoding.UTF8.GetBytes("never read"));
            await Settle();
            File.Delete(path);
        }
        Letter("next.md", "next");
        WaitFor(1);
        await Settle();
        Assert.Equal(["next"], Got().Select(t => t.Split("\n\n", 2)[1]));
    }

    [Fact]
    public void A_LETTER_PAST_THE_TOOL_RESULT_BUDGET_ARRIVES_AS_ITS_PATH_AND_ITS_SIZE()
    {
        Arm();
        var path = Letter("big.md", new string('x', 60_000));
        WaitFor(1);
        Assert.Equal(path + "\n\nthis letter holds 60000 characters, more than a tool result may carry; read_file reads it", Assert.Single(Got()));
    }

    [Fact]
    public async Task SIX_LETTERS_PAUSE_THE_WATCH_WITH_ITS_ROW_AND_THE_SEVENTH_WAITS()
    {
        var w = Arm();
        for (var i = 1; i <= 7; i++) Letter($"{i}.md", $"letter {i}");
        WaitFor(6);
        await Settle();
        Assert.Equal(6, Got().Count);   //at most K ahead of what was taken
        w.Taken(Enumerable.Repeat(EntryKind.Watch, 6));
        Assert.True(w.Paused);
        Assert.Equal(["watch paused after 6 letters, /watch to resume"], _rows);
        await Settle();
        Assert.Equal(6, Got().Count);
    }

    [Fact]
    public async Task SIX_TYPED_MESSAGES_NEVER_PAUSE_AND_A_TYPED_MESSAGE_RESETS_THE_COUNT()
    {
        var w = Arm();
        for (var i = 0; i < 6; i++) w.Typed();
        Assert.False(w.Paused);
        w.Taken([EntryKind.Watch, EntryKind.Watch, EntryKind.Watch, EntryKind.Watch, EntryKind.Watch, EntryKind.Plain, EntryKind.Watch]);
        Assert.False(w.Paused);
        Assert.Equal(Watch.K - 1, w.Budget());
        await Task.CompletedTask;
    }

    [Fact]
    public void A_TYPED_MESSAGE_RESETS_THE_COUNT_AND_LEAVES_A_PAUSE_IN_PLACE()
    {
        var w = Arm();
        w.Taken(Enumerable.Repeat(EntryKind.Watch, 6));
        Assert.True(w.Paused);
        w.Typed();
        Assert.True(w.Paused);
        Assert.Equal(0, w.Budget());
    }

    [Fact]
    public async Task A_LETTER_THAT_LANDS_DURING_THE_PAUSE_ARRIVES_ONCE_ON_RESUME()
    {
        var w = Arm();
        w.Taken(Enumerable.Repeat(EntryKind.Watch, 6));
        Letter("during.md", "during");
        await Settle();
        Assert.Empty(Got());
        w.Lift();
        WaitFor(1);
        w.Rescan();
        await Settle();
        Assert.Single(Got());
    }

    [Fact]
    public async Task A_BACKLOG_OF_TWENTY_HANDS_OVER_SIX_AND_THE_LIFT_THE_NEXT_SIX()
    {
        Directory.CreateDirectory(Folder);
        for (var i = 1; i <= 20; i++) Letter($"{i:00}.md", $"letter {i}");
        var w = Arm();
        WaitFor(6);
        await Settle();
        Assert.Equal(Enumerable.Range(1, 6).Select(i => $"letter {i}"), Got().Select(t => t.Split("\n\n", 2)[1]));
        w.Taken(Enumerable.Repeat(EntryKind.Watch, 6));
        w.Lift();
        WaitFor(12);
        await Settle();
        Assert.Equal(Enumerable.Range(1, 12).Select(i => $"letter {i}"), Got().Select(t => t.Split("\n\n", 2)[1]));
    }

    [Fact]
    public void AN_ARM_WITH_A_BACKLOG_LETTER_HELD_OPEN_RETURNS_AT_ONCE_AND_THE_LETTER_ARRIVES_WHEN_RELEASED()
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "held.md");
        var writer = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        writer.Write(Encoding.UTF8.GetBytes("held at arm"));
        var started = System.Diagnostics.Stopwatch.StartNew();
        Arm();
        Assert.True(started.ElapsedMilliseconds < 1000, $"the arm took {started.ElapsedMilliseconds} ms");
        writer.Dispose();
        WaitFor(1);
        Assert.Equal(path + "\n\nheld at arm", Assert.Single(Got()));
    }

    [Fact]
    public void THE_GITIGNORE_IS_WRITTEN_ONCE_AND_NEVER_OVERWRITTEN()
    {
        Arm().Dispose();
        var ignore = Path.Combine(Folder, ".gitignore");
        Assert.Equal("*\n", File.ReadAllText(ignore));
        File.WriteAllText(ignore, "kept\n");
        Arm();
        Assert.Equal("kept\n", File.ReadAllText(ignore));
    }

    [Fact]
    public void A_MODEL_ID_A_PATH_CANNOT_HOLD_BECOMES_A_FOLDER_WITH_HYPHENS()
    {
        Assert.Equal("qwen3.8-flash-next", Gatto.Repl.Repl.MailboxName("qwen3.8-flash-next"));
        Assert.Equal("openrouter-qwen-free", Gatto.Repl.Repl.MailboxName("openrouter/qwen:free"));
    }
}
