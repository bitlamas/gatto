using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Render;

//one shape for a background end wherever it is drawn: live, from the store, and through the rich replay
public sealed class BackgroundEndRenderTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private const string Head = "background task 1 ended · exit 2 · 3s";
    private static readonly string Output = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"out line {i}")) + "\n(exit code 2)";

    private static ChatMessage Record() => new("user", Head + "\n" + Output, IsError: true, Gloss: "exit 2", Background: 1);

    private static (StreamRenderer R, TranscriptModel M) Rich()
    {
        var s = new RecordingSurface { Width = 80, Height = 0 };
        var gate = new object();
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        var painter = new ChromePainter(s, T, gate) { Frame = new InputFrame(s, T, "coder", status, glyphs: GlyphSet.Unicode), RoleForTint = "coder" };
        var r = new StreamRenderer(painter, s, T, "coder", new ChromeTicker(painter, gate), gate, model: painter.Model, convoTail: () => null);
        return (r, painter.Model);
    }

    private static List<string> Visible(TranscriptItem item, int width = 80) =>
        [.. item.Render(width, T, GlyphSet.Unicode).Select(TermText.StripAnsiForWidth)];

    [Fact]
    public void LIVE_STORE_AND_REPLAY_DRAW_THE_SAME_ROWS_COLLAPSED_AND_OPEN()
    {
        var (live, liveModel) = Rich();
        live.CommitBackground(Record());

        var convo = new Conversation("sys");
        convo.Append(Record());
        var (_, stored) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(liveModel, convo), T, "coder", GlyphSet.Unicode);

        var (replay, replayModel) = Rich();
        Gatto.Repl.Repl.ReplayTranscript(convo.Messages, replay, _ => Assert.Fail("a background end is never echoed as typed"), replay.CommitBackground);

        var items = new[] { liveModel.Items.Single(), stored.Items.Single(), replayModel.Items.Single() }
            .Select(i => Assert.IsType<BackgroundEndItem>(i)).ToList();
        var collapsed = Visible(items[0]).Where(r => r.Length > 0).ToList();
        Assert.Equal(GlyphSet.Unicode.Sharp + " " + Head, collapsed[0].Trim());
        Assert.Contains(GlyphSet.Unicode.Elbow, collapsed[1], StringComparison.Ordinal);
        Assert.DoesNotContain(collapsed, r => r.Contains("out line 3", StringComparison.Ordinal));   //closed, a long output never prints open
        Assert.All(items, i => Assert.Equal(Visible(items[0]), Visible(i)));

        foreach (var i in items) i.Collapsed = false;
        var open = Visible(items[0]);
        Assert.Contains(open, r => r.Contains("out line 3", StringComparison.Ordinal));
        Assert.All(items, i => Assert.Equal(open, Visible(i)));
    }

    //the block holds the end, so the store writes the chat record and no event for it, and a resume draws it once
    [Fact]
    public void THE_STORE_SAVES_THE_RECORD_ONCE_AND_NO_EVENT()
    {
        var (live, model) = Rich();
        var convo = new Conversation("sys");
        convo.Append(Record());
        live.CommitBackground(convo.Messages[^1]);

        var lines = TranscriptStore.BuildLines(model, convo);

        Assert.Equal(2, lines.Count);
        Assert.DoesNotContain(lines, l => l.Contains("\"event\"", StringComparison.Ordinal));
        Assert.Contains("\"background\":1", lines[1], StringComparison.Ordinal);
    }

    //the record holds the middle dot as written, and the ascii set draws its own
    [Fact]
    public void THE_ASCII_SET_DRAWS_THE_HEAD_WITH_ITS_OWN_DOT()
    {
        var item = BackgroundEndItem.From(Record(), T, GlyphSet.Ascii, "coder");
        var head = TermText.StripAnsiForWidth(item.Render(80, T, GlyphSet.Ascii)[0]);

        Assert.Equal($"{GlyphSet.Ascii.Sharp} background task 1 ended {GlyphSet.Ascii.Dot} exit 2 {GlyphSet.Ascii.Dot} 3s", head.Trim());
    }
}

//the plain face has no block, so a restored end prints its head line as gatto's own line and nothing of its output. it swaps Console, so it joins the serial collection
[Collection("e2e")]
public sealed class BackgroundEndPlainReplayTests : IDisposable
{
    private readonly TextReader _in = Console.In;
    private readonly TextWriter _out = Console.Out;

    public void Dispose()
    {
        Console.SetIn(_in);
        Console.SetOut(_out);
    }

    [Fact]
    public async Task THE_PLAIN_REPLAY_PRINTS_THE_HEAD_LINE_ALONE()
    {
        var convo = new Conversation("sys");
        convo.AddUser("start the build");
        convo.AddAssistant("started");
        convo.Append(new ChatMessage("user", "background task 1 ended · exit 0 · 3s\nBuild succeeded.", Gloss: "exit 0", Background: 1));
        convo.AddAssistant("it passed");
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var repl = new Gatto.Repl.Repl(loop, convo, "generalist", "m", null, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), _ => new Gatto.Repl.RoleSwitchResult(false, "", null, null, "unused"), resumedFrom: "an earlier session");
        var written = new StringWriter();
        Console.SetOut(written);
        Console.SetIn(new StringReader("/quit\n"));

        await repl.RunAsync(CancellationToken.None);

        var text = written.ToString();
        Assert.Contains(GlyphSet.Unicode.Sharp + " background task 1 ended · exit 0 · 3s", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Build succeeded.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("> background task", text, StringComparison.Ordinal);
        Assert.Contains("> start the build", text, StringComparison.Ordinal);
    }
}
