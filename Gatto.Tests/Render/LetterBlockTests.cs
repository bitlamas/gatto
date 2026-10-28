using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Session;
using Gatto.Repl;
using Gatto.Repl.Render;
using Gatto.Terminal;
using Xunit;

namespace Gatto.Tests.Render;

//a watched letter draws as its own folded block on its own band, keyed on the record's watch field and never on its text
public sealed class LetterBlockTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private const string Path = "C:\\work\\.gatto\\mailbox\\qwen3.8-flash-next\\2026-10-28-planner-ask.md";

    private const string File =
        "<!-- letter:2026-10-28-planner-ask -->\n" +
        "to: gatto/qwen3.8-flash-next\n" +
        "from: seat/planner/5b6a8a37\n" +
        "date: 2026-10-28T10:02:14-04:00\n" +
        "re: the build\n" +
        "kind: letter\n" +
        "\n" +
        "line one\n" +
        "line two\n" +
        "line three\n" +
        "\n" +
        "line five\n" +
        "<!-- /letter -->\n";

    private static ChatMessage Record(string? watch = Path) => new("user", Path + "\n\n" + File, Watch: watch);

    private static List<string> Plain(TranscriptItem item, int width) =>
        [.. item.Render(width, T, GlyphSet.Unicode).Select(r => TermText.StripAnsiForWidth(r).TrimEnd())];

    [Fact]
    public void THE_PARSE_TAKES_THE_HEADER_AS_FIELDS_AND_DROPS_THE_PATH_LINE_AND_BOTH_MARKERS()
    {
        var l = LetterText.Parse(Path, Path + "\n\n" + File);
        Assert.Equal(["to", "from", "date", "re", "kind"], l.Fields.Select(f => f.Key));
        Assert.Equal("seat/planner/5b6a8a37", l.Get("from"));
        Assert.Equal("the build", l.Get("re"));
        Assert.Equal(["line one", "line two", "line three", "", "line five"], l.Body);
    }

    [Fact]
    public void A_FILE_WITH_NO_FRAME_KEEPS_EVERY_LINE_AS_ITS_BODY()
    {
        var l = LetterText.Parse(Path, Path + "\n\nfrom: someone\nplain notes\n");
        Assert.Empty(l.Fields);
        Assert.Equal(["from: someone", "plain notes"], l.Body);
    }

    [Theory]
    [InlineData("C:\\work\\.gatto\\mailbox\\qwen3.8-flash-next\\a.md", "qwen3.8-flash-next\\a.md")]
    [InlineData("C:\\work\\.gatto\\mailbox\\system\\planner\\a.md", "system\\planner\\a.md")]
    [InlineData("D:\\notes\\inbox\\a.md", "inbox\\a.md")]
    [InlineData("C:\\a.md", "a.md")]
    public void THE_HEAD_NAMES_THE_PATH_FROM_THE_MAILBOX_ROOT(string path, string shown)
    {
        Assert.Equal(shown, LetterText.ShortPath(path));
        Assert.Equal("New mail: " + shown, LetterText.Head(path));
    }

    [Fact]
    public void A_FOLDED_LETTER_IS_ITS_HEAD_FROM_RE_KIND_ONE_BODY_ROW_AND_THE_LINK()
    {
        var rows = Plain(LetterItem.From(Record()), 80);
        Assert.Equal([
            "♯ New mail: qwen3.8-flash-next\\2026-10-28-planner-ask.md",
            "  from seat/planner/5b6a8a37 · re the build · kind letter",
            "  line one",
            "  ⎿ … 4 more · click to expand",
        ], rows);
    }

    [Fact]
    public void A_FOLDED_LETTER_NEVER_PASSES_FOUR_ROWS_AND_CUTS_ITS_FIRST_LINE_WITH_THE_ELLIPSIS()
    {
        var item = LetterItem.From(new ChatMessage("user", Path + "\n\n" + File.Replace("line one", string.Join(" ", Enumerable.Repeat("word", 40))), Watch: Path));
        var rows = Plain(item, 40);
        Assert.Equal(4, rows.Count);
        Assert.EndsWith("…", rows[2]);
        Assert.True(UnicodeWidth.Of(rows[2]) <= 40);
        Assert.StartsWith("  ⎿ … ", rows[3]);
    }

    [Fact]
    public void AN_OPEN_LETTER_SHOWS_EVERY_FIELD_AND_THE_WHOLE_BODY_AND_NEVER_A_MARKER()
    {
        var item = LetterItem.From(Record());
        item.Collapsed = false;
        var rows = Plain(item, 200);
        Assert.Equal("  to gatto/qwen3.8-flash-next · from seat/planner/5b6a8a37 · date 2026-10-28T10:02:14-04:00 · re the build · kind letter", rows[1]);
        Assert.Equal(["  line one", "  line two", "  line three", "", "  line five", "  ⎿ click to collapse"], rows[2..]);
        Assert.DoesNotContain(rows, r => r.Contains("<!--"));
    }

    [Fact]
    public void THE_BODY_IS_IN_THE_DIM_INK_AND_THE_LINK_WORDS_IN_THE_LINK_INK_NEVER_THE_OPERATORS_WHITE()
    {
        var rows = LetterItem.From(Record()).Render(80, T, GlyphSet.Unicode);
        Assert.Contains(Ansi.Fg(T.Map(Theme.Dim), true) + "line one", rows[2]);
        Assert.Contains(Ansi.Fg(T.Map(Theme.Thought), true) + "click to expand", rows[3]);
        Assert.Contains(Ansi.Fg(T.Map(Theme.Warn), true) + "♯ New mail", rows[0]);
        Assert.DoesNotContain(rows, r => r.Contains(Ansi.Fg(T.Map(Theme.Bright), true)));
    }

    [Fact]
    public void EVERY_ROW_IS_ON_THE_LETTER_BAND_AND_NONE_ON_THE_USER_BAND()
    {
        var letter = Ansi.Bg(T.Map(Theme.LetterBg), true);
        var user = Ansi.Bg(T.Map(Theme.UserInputBg), true);
        var rows = LetterItem.From(Record()).Render(80, T, GlyphSet.Unicode);
        Assert.All(rows, r => Assert.StartsWith(letter, r));
        Assert.DoesNotContain(rows, r => r.Contains(user));
    }

    [Fact]
    public void A_BODY_THAT_FITS_ITS_ONE_ROW_HAS_NO_LINK_ROW_AND_NOTHING_TO_OPEN_TO_AND_DATE_INCLUDED()
    {
        var item = LetterItem.From(new ChatMessage("user", Path + "\n\n<!-- letter:x -->\nto: t\nfrom: a\ndate: d\nre: b\nkind: letter\n\nhi\n<!-- /letter -->\n", Watch: Path));
        Assert.False(item.Opens(80));
        Assert.Equal(["♯ New mail: qwen3.8-flash-next\\2026-10-28-planner-ask.md", "  from a · re b · kind letter", "  hi"], Plain(item, 80));
    }

    [Fact]
    public void THE_WATCH_FIELD_IS_WRITTEN_ONLY_WHEN_THE_RECORD_HOLDS_A_LETTER_AND_READ_BACK()
    {
        var letter = SessionStore.ChatJson(Record());
        var typed = SessionStore.ChatJson(Record(watch: null));
        using var doc = JsonDocument.Parse(letter);
        Assert.Equal(Path, doc.RootElement.GetProperty("watch").GetString());
        Assert.Equal(Path, SessionStore.ParseChatElement(doc.RootElement).Watch);
        using var plain = JsonDocument.Parse(typed);
        Assert.False(plain.RootElement.TryGetProperty("watch", out _));
        Assert.Null(SessionStore.ParseChatElement(plain.RootElement).Watch);
    }

    [Fact]
    public void A_RESUME_REBUILDS_A_LETTER_AS_ITS_BLOCK_AND_THE_SAME_TEXT_TYPED_AS_THE_USERS_WORDS()
    {
        var lines = new[] { SessionStore.ChatJson(new ChatMessage("system", "sys")), SessionStore.ChatJson(Record()), SessionStore.ChatJson(Record(watch: null)) };
        var (_, model) = TranscriptStore.Rebuild(lines, T, "generalist", GlyphSet.Unicode);
        Assert.IsType<LetterItem>(model.Items[0]);
        Assert.IsType<UserEchoItem>(model.Items[1]);
    }

    [Fact]
    public void THE_QUEUE_ROW_OF_A_LETTER_READS_NEW_MAIL_AND_ITS_SHORT_PATH()
    {
        var q = new DispatchQueue(10);
        q.EnqueueWatch(Path + "\n\n" + File);
        var view = Assert.Single(q.Texts());
        Assert.Equal(("New mail: qwen3.8-flash-next\\2026-10-28-planner-ask.md", true), (view.Text, view.Task));
    }

    [Fact]
    public void THE_ROUND_BOUNDARY_TAKE_HANDS_A_LETTER_ITS_PATH_AND_TYPED_WORDS_NONE()
    {
        var q = new DispatchQueue(10);
        q.EnqueueWatch(Path + "\n\n" + File);
        q.TryEnqueue("typed");
        var taken = q.TakeQueuedForTurn(int.MaxValue);
        Assert.Equal([Path, null], taken.Select(e => e.WatchPath));
    }
}
