using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Tests.Repl;

//the background panel's words with no child running: the title's count, the watch row and the watch's detail
public class BackgroundRowsTests
{
    private static readonly GlyphSet G = GlyphSet.Unicode;

    [Fact]
    public void The_count_says_the_shells_then_the_watch_or_the_watch_alone()
    {
        Assert.Equal("3 shells · watch", BackgroundPanel.Count(3, true, G));
        Assert.Equal("1 shell", BackgroundPanel.Count(1, false, G));
        Assert.Equal("watch", BackgroundPanel.Count(0, true, G));
    }

    [Fact]
    public void The_watch_row_is_the_word_watch_with_its_folder_or_its_paused_words_as_the_column()
    {
        var armed = BackgroundPanel.Rows([], new WatchView("inbox", false, 0, 6, []), G).Single();
        var paused = BackgroundPanel.Rows([], new WatchView("inbox", true, 1, 6, []), G).Single();

        Assert.Equal(("watch", "watch", "inbox"), (armed.Label, armed.Key, armed.Columns![0]));
        Assert.Equal("watch", paused.Label);
        Assert.Equal("paused · 1 letter since your last message", paused.Columns![0]);
    }

    [Fact]
    public void The_watchs_detail_lists_the_letters_it_handed_over()
    {
        var rows = BackgroundPanel.WatchDetail(new WatchView("inbox", false, 2, 6, ["a.md", "b.md"]), G);

        Assert.Equal(["inbox", "2 of 6 letters since your last message", "a.md", "b.md"], rows.Select(r => r.Text));
        Assert.Equal("⎿ ", rows[2].Gutter);
    }

    [Fact]
    public void A_shells_command_rows_wrap_and_its_output_rows_do_not()
    {
        var rows = BackgroundPanel.ShellRows("first line\nsecond line", "out one\nout two", "", G);

        Assert.Equal(["first line", "second line"], rows.Take(2).Select(r => r.Text));
        Assert.All(rows.Take(2), r => Assert.True(r.Wrap));
        Assert.Contains(rows.Skip(2), r => r.Text.Contains("out one", StringComparison.Ordinal));
        Assert.All(rows.Skip(2), r => Assert.False(r.Wrap));
    }
}
