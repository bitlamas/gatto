using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Repl.Input;
using Gatto.Repl.Render;
using Gatto.Repl.Render.Chrome;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Render;

//a tool's image record draws as one dim block live, from the store and on replay
public sealed class ImageBlockRenderTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static ChatMessage Record() =>
        new Conversation(null).ImageRecord("images read this round: a.png (70 bytes), b.png (2 KB); already shown: c.png",
            [new ImageRef(@"C:\proj\a.png", 70, "aa", "image/png"), new ImageRef(@"C:\proj\b.png", 2048, "bb", "image/png")], "read_file");

    private static (StreamRenderer R, TranscriptModel M) Rich()
    {
        var s = new RecordingSurface { Width = 80, Height = 0 };
        var gate = new object();
        var status = new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x");
        var painter = new ChromePainter(s, T, gate) { Frame = new InputFrame(s, T, "coder", status, glyphs: GlyphSet.Unicode), RoleForTint = "coder" };
        var r = new StreamRenderer(painter, s, T, "coder", new ChromeTicker(painter, gate), gate, model: painter.Model, convoTail: () => null);
        return (r, painter.Model);
    }

    private static List<string> Visible(TranscriptItem item) =>
        [.. item.Render(80, T, GlyphSet.Unicode).Select(TermText.StripAnsiForWidth).Where(r => r.Length > 0)];

    [Fact]
    public void LIVE_STORE_AND_REPLAY_DRAW_ONE_ROW_PER_IMAGE_UNDER_THE_SYSTEM_MARKER()
    {
        var (live, liveModel) = Rich();
        live.OnImagesDelivered(Record());

        var convo = new Conversation("sys");
        convo.Append(Record());
        var (_, stored) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(liveModel, convo), T, "coder", GlyphSet.Unicode);

        var (replay, replayModel) = Rich();
        Gatto.Repl.Repl.ReplayTranscript(convo.Messages, replay, _ => Assert.Fail("an image record is never echoed as typed"),
            replay.CommitBackground, replay.CommitLetter, replay.CommitImages);

        var items = new[] { liveModel.Items.Single(), stored.Items.Single(), replayModel.Items.Single() }
            .Select(i => Assert.IsType<ImageBlockItem>(i)).ToList();
        Assert.Equal(
            [GlyphSet.Unicode.Sharp + " image a.png, 70 bytes", "  image b.png, 2 KB", "  already shown: c.png"],
            Visible(items[0]));
        Assert.All(items, i => Assert.Equal(Visible(items[0]), Visible(i)));
    }

    [Fact]
    public void THE_STORE_SAVES_THE_RECORD_ONCE_AND_NO_EVENT()
    {
        var (live, model) = Rich();
        var convo = new Conversation("sys");
        convo.Append(Record());
        live.OnImagesDelivered(convo.Messages[^1]);

        var lines = TranscriptStore.BuildLines(model, convo);

        Assert.Equal(2, lines.Count);
        Assert.Contains("\"by\":\"read_file\"", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void THE_COMPACT_LAST_EXCHANGE_LOOKS_PAST_AN_IMAGE_RECORD_FOR_THE_USERS_WORDS()
    {
        var convo = new Conversation("sys");
        convo.AddUser("what is in the scan");
        convo.Append(Record());
        convo.AddAssistant("a cat");

        Assert.Equal(("what is in the scan", "a cat"), Gatto.Repl.Repl.LastTextExchange(convo));
    }

    [Fact]
    public void CONTEXT_COUNTS_AN_IMAGE_RECORD_ON_ITS_OWN_LINE_WITH_ITS_PICTURES()
    {
        var sent = new List<ChatMessage> { new("system", "sys"), new("user", "look"), Record() };
        var parts = ContextCount.Parts(new ContextInputs("m", "sys", [], [], [],
            new RequestShape([], null, null, ReasoningHistory.All, LastSent: sent), 65_536, null, 1.0, null, null));

        Assert.Equal("look", parts.Single(p => p.Label == "user").Text);
        var images = parts.Single(p => p.Label == "images");
        Assert.Equal("2", images.Detail);
        Assert.Contains("a.png", images.Text, StringComparison.Ordinal);
    }

    //a picture has no text, so the images row carries the per-picture estimate on top of its line's
    [Fact]
    public async Task CONTEXT_S_IMAGES_ROW_CARRIES_THE_PICTURES_ESTIMATE()
    {
        var sent = new List<ChatMessage> { new("system", "sys"), new("user", "look"), Record() };
        var figures = await ContextCount.BuildAsync(new ContextInputs("m", "sys", [], [], [],
            new RequestShape([], null, null, ReasoningHistory.All, LastSent: sent), 65_536, null, 1.0, null, null), null, default);

        var row = figures.Parts.Single(p => p.Label == "images");
        Assert.Equal((int)Math.Round(Record().Content!.Length / 4.0) + 2 * ContextBudget.ImageTokensEstimate, row.Tokens);
    }

    //the user's own pictures ride the user row by the same estimate, so the rows add up to what the request carries
    [Fact]
    public async Task CONTEXT_S_USER_ROW_CARRIES_ITS_TYPED_PICTURES()
    {
        var sent = new List<ChatMessage> { new("system", "sys"), new("user", "look", Images: [new ImageRef(@"C:\proj\t.png", 70, "tt", "image/png")]) };
        var figures = await ContextCount.BuildAsync(new ContextInputs("m", "sys", [], [], [],
            new RequestShape([], null, null, ReasoningHistory.All, LastSent: sent), 65_536, null, 1.0, null, null), null, default);

        Assert.Equal(1 + ContextBudget.ImageTokensEstimate, figures.Parts.Single(p => p.Label == "user").Tokens);
    }

    //the summarize measure goes through Estimate, so a conversation with pictures measures larger than the same words without
    [Fact]
    public void THE_HARSH_SHAPE_MEASURES_PICTURES()
    {
        var without = Record() with { Images = null };
        Assert.True(ContextBudget.Estimate([Record()]) > ContextBudget.Estimate([without]));
    }

    [Fact]
    public void THE_HARSH_SHAPE_KEEPS_AN_IMAGE_RECORDS_LINE_AND_DROPS_ITS_PICTURES()
    {
        var shaped = Compactor.HarshShape([new ChatMessage("system", "sys"), new ChatMessage("user", "go"), Record()], 100_000, 1.0);

        var last = shaped[^1];
        Assert.Equal("read_file", last.By);
        Assert.Null(last.Images);
        Assert.Equal(Record().Content, last.Content);
    }
}

//the plain face has no block, so a tool's pictures print as gatto's own line and never as the user's words. it swaps Console, so it joins the serial collection
[Collection("e2e")]
public sealed class ImageRecordPlainTests : IDisposable
{
    private readonly TextReader _in = Console.In;
    private readonly TextWriter _out = Console.Out;

    public void Dispose()
    {
        Console.SetIn(_in);
        Console.SetOut(_out);
    }

    private static ChatMessage Record() =>
        new Conversation(null).ImageRecord("images read this round: a.png (70 bytes)", [new ImageRef(@"C:\proj\a.png", 70, "aa", "image/png")], "read_file");

    [Fact]
    public void THE_PLAIN_RENDERER_PRINTS_THE_LINE_LIVE()
    {
        var written = new StringWriter();
        Console.SetOut(written);

        new PlainRenderer().OnImagesDelivered(Record());

        Assert.Equal($"\n{GlyphSet.Unicode.Sharp} images read this round: a.png (70 bytes)\n", written.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task THE_PLAIN_REPLAY_PRINTS_THE_LINE_AND_NO_USER_ECHO()
    {
        var convo = new Conversation("sys");
        convo.AddUser("read the scan");
        convo.Append(Record());
        convo.AddAssistant("a cat");
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new Gatto.Core.Tools.ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var repl = new Gatto.Repl.Repl(loop, convo, "generalist", "m", null, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), _ => new Gatto.Repl.RoleSwitchResult(false, "", null, null, "unused"), resumedFrom: "an earlier session");
        var written = new StringWriter();
        Console.SetOut(written);
        Console.SetIn(new StringReader("/quit\n"));

        await repl.RunAsync(CancellationToken.None);

        var text = written.ToString();
        Assert.Contains(GlyphSet.Unicode.Sharp + " images read this round: a.png (70 bytes)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("> images read", text, StringComparison.Ordinal);
        Assert.Contains("> read the scan", text, StringComparison.Ordinal);
    }
}
