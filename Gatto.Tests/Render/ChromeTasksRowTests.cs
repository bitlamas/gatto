using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;

namespace Gatto.Tests.Render;

//the one chrome row for every running background task: its words, its place among the fixed rows and where the clamp drops it
public sealed class ChromeTasksRowTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static ChromePainter Painter(int width, int height, GlyphSet? glyphs = null)
    {
        var g = glyphs ?? GlyphSet.Unicode;
        var surface = new RecordingSurface { Width = width, Height = height };
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        return new ChromePainter(surface, T, new object(), glyphs: g) { Frame = new InputFrame(surface, T, "coder", status, glyphs: g) };
    }

    private static readonly (int, string, TimeSpan)[] Two =
    [
        (1, "dotnet test --nologo", TimeSpan.FromSeconds(252)),
        (2, "dotnet watch run\nsecond line", TimeSpan.FromSeconds(63)),
    ];

    private static List<ChromeRow> Rows(ChromePainter p, int width, int height) => [.. p.ComposeChromeBlock(width, height).Rows];

    private static string? TasksLine(ChromePainter p, int width, int height) =>
        Rows(p, width, height).Where(r => r.Region == ChromeRegion.Tasks && r.Visible.Length > 0).Select(r => r.Visible).SingleOrDefault();

    [Fact]
    public void THE_ROW_NAMES_THE_COUNT_AND_EACH_TASK_WITH_ITS_HEAD_AND_ITS_TIMER()
    {
        var p = Painter(120, 40);
        p.State.Tasks = Two;
        var g = GlyphSet.Unicode;

        Assert.Equal($"{g.Ring}  2 background  {g.Dot}  1 dotnet test --nologo {Gatto.Core.ElapsedText.Of(TimeSpan.FromSeconds(252))}  {g.Dot}  2 dotnet watch run {Gatto.Core.ElapsedText.Of(TimeSpan.FromSeconds(63))}", TasksLine(p, 120, 40));

        p.State.Tasks = [Two[0]];
        Assert.Equal($"{g.Ring}  1 background  {g.Dot}  1 dotnet test --nologo {Gatto.Core.ElapsedText.Of(TimeSpan.FromSeconds(252))}", TasksLine(p, 120, 40));
    }

    [Fact]
    public void WITH_NO_TASK_THERE_IS_NO_ROW()
    {
        var p = Painter(120, 40);
        Assert.DoesNotContain(Rows(p, 120, 40), r => r.Region == ChromeRegion.Tasks);
    }

    [Fact]
    public void A_NARROW_ROW_CUTS_THE_HEADS_AND_THEN_ENDS_WITH_THE_MORE_TAIL()
    {
        var p = Painter(50, 40);
        p.State.Tasks = Two;
        var g = GlyphSet.Unicode;

        var line = TasksLine(p, 50, 40)!;
        Assert.True(UnicodeWidth.Of(line) <= 50, line);
        Assert.StartsWith($"{g.Ring}  2 background  {g.Dot}  1 dotnet t", line, StringComparison.Ordinal);
        Assert.Contains(g.Ellipsis, line, StringComparison.Ordinal);
        Assert.EndsWith($"  {g.Dot}  +1 more", line, StringComparison.Ordinal);

        //no head keeps its floor at this width, so the row says only the count and the tail
        Assert.Equal($"{g.Ring}  2 background  {g.Dot}  +2 more", TasksLine(p, 40, 40));
    }

    [Fact]
    public void THE_ROW_SITS_AFTER_THE_PURR_AND_BEFORE_THE_QUEUE()
    {
        var p = Painter(120, 40);
        p.State.PurrText = "purring";
        p.State.Tasks = Two;
        p.State.QueueTexts = ["queued message"];

        var regions = Rows(p, 120, 40).Where(r => r.Visible.Length > 0).Select(r => r.Region).Distinct().ToList();

        Assert.True(regions.IndexOf(ChromeRegion.Purr) < regions.IndexOf(ChromeRegion.Tasks), string.Join(",", regions));
        Assert.True(regions.IndexOf(ChromeRegion.Tasks) < regions.IndexOf(ChromeRegion.Queue), string.Join(",", regions));
    }

    //a tall panel, the /tasks answer among them, takes the room the reservation leaves, so the row it describes stays drawn
    [Fact]
    public void A_TALL_PANEL_LEAVES_THE_ROW_IN_PLACE()
    {
        var p = Painter(100, 24);
        p.State.Tasks = Two;
        p.SetInfoPanel((w, _) => [.. Enumerable.Range(1, 60).Select(i => $"  panel row {i}")]);

        Assert.NotNull(TasksLine(p, 100, 24));
    }

    //the clamp drops the older queue first, then this row, then the notice, so a short window keeps the purr and the newest message
    [Fact]
    public void THE_CLAMP_DROPS_THE_ROW_AFTER_THE_OLDER_QUEUE_AND_BEFORE_THE_NOTICE()
    {
        var gone = new List<string>();
        for (var height = 30; height >= 4 && gone.Count < 3; height--)
        {
            var p = Painter(80, height);
            p.State.PurrText = "purring";
            p.State.Notice = "a notice";
            p.State.Tasks = Two;
            p.State.QueueTexts = ["older one", "older two", "newest message"];
            var rows = Rows(p, 80, height);
            bool Has(Func<ChromeRow, bool> f) => rows.Any(f);
            if (!gone.Contains("older") && !Has(r => r.Visible.Contains("older one"))) gone.Add("older");
            if (!gone.Contains("tasks") && !Has(r => r.Region == ChromeRegion.Tasks)) gone.Add("tasks");
            if (!gone.Contains("notice") && !Has(r => r.Region == ChromeRegion.Notice)) gone.Add("notice");
        }

        Assert.Equal(["older", "tasks", "notice"], gone);
    }

    [Fact]
    public void UNDER_THE_ASCII_SET_THE_ROW_IS_ASCII()
    {
        var p = Painter(120, 40, GlyphSet.Ascii);
        p.State.Tasks = Two;

        var line = TasksLine(p, 120, 40)!;
        Assert.All(line, c => Assert.True(c <= 0x7F, $"U+{(int)c:X4} in {line}"));
        Assert.StartsWith($"{GlyphSet.Ascii.Ring}  2 background", line, StringComparison.Ordinal);
    }
}
