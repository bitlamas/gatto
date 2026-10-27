using System.Text.Json;
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
using Xunit;

namespace Gatto.Tests.Render;

//a message delivered at a round boundary draws as a ❯ block between the round's results and the next reply, and a resume draws it in the same place
public sealed class QueuedDeliveryRenderTests
{
    private static readonly Theme T = new(new TermCaps(true, true));
    private static readonly GlyphSet U = GlyphSet.Unicode;

    private sealed record Wired(StreamRenderer R, TranscriptModel Model, Conversation Convo);

    private static Wired Wire(int width, int height)
    {
        var s = new VtScreenSurface(width, height);
        var gate = new object();
        var model = new TranscriptModel("coder");
        var convo = new Conversation("sys");
        var painter = new ChromePainter(s, T, gate, model, glyphs: U)
        {
            Frame = new InputFrame(s, T, "coder", new StatusInfo(@"C:\proj", "qwen", "coder", new CtxState(), @"C:\Users\x"), glyphs: U),
            RoleForTint = "coder",
        };
        painter.State.Composer = new EditorView(new List<string> { "" }, 0, 0);
        var renderer = new StreamRenderer(painter, s, T, "coder", new ChromeTicker(painter, gate), gate, model: model,
            convoTail: () => convo.Messages.Count > 0 ? convo.Messages[^1] : null, glyphs: U);
        painter.AltScreen.Enter();
        painter.Repaint();
        return new Wired(renderer, model, convo);
    }

    private static List<string> Rows(TranscriptItem item, int width) =>
        item.Render(width, T, U).Select(r => TermText.StripAnsiForWidth(r).TrimEnd()).ToList();

    private static List<string> ModelRows(TranscriptModel m, int width) => m.Items.SelectMany(i => Rows(i, width)).ToList();

    private sealed class Shell : ITool
    {
        public string Name => "shell";
        public string Description => "runs";
        public JsonElement ParametersSchema => JsonDocument.Parse("{\"type\":\"object\"}").RootElement;
        public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct) =>
            Task.FromResult(new ToolResult("tests passed: 42", Gloss: "exit 0"));
    }

    //the turn runs to its end, so the last reply's open text is committed as the screen shows it
    private static async Task<Wired> DeliveredMidTurn(int width)
    {
        var w = Wire(width, 30);
        var client = new FakeChatClient();
        var call = new ToolCall("c1", "shell", JsonSerializer.Serialize(new { command = "dotnet test" }));
        client.EnqueueTurn(new StreamEvent.ToolCallReady(call), new StreamEvent.Finished("tool_calls", null));
        client.EnqueueTurn(new StreamEvent.TextDelta("Both done."), new StreamEvent.Finished("stop", null));
        var tools = new ToolRegistry();
        tools.Register(new Shell());
        var loop = new AgentLoop(client, tools, new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        string[]? queued = ["also check the build"];
        w.R.CommitUser(["run the tests"]);
        w.R.BeginTurn();
        await loop.RunTurnAsync(w.Convo, "run the tests", w.R, default, takeQueued: () =>
        {
            var t = queued ?? [];
            queued = null;
            return t;
        });
        w.R.EndTurn();
        return w;
    }

    [Fact]
    public async Task THE_DELIVERED_BLOCK_SITS_BETWEEN_THE_TOOL_BLOCK_AND_THE_NEXT_REPLY()
    {
        var w = await DeliveredMidTurn(120);
        var kinds = w.Model.Items.Select(i => i.GetType().Name).ToList();
        var tool = kinds.IndexOf(nameof(ToolBlockItem));
        var delivered = w.Model.Items.Select((item, i) => (item, i))
            .Single(p => p.item is UserEchoItem u && u.RawLines.SequenceEqual(["also check the build"])).i;

        Assert.True(tool >= 0 && tool < delivered, string.Join(", ", kinds));
        Assert.Contains("Both done.", string.Join("\n", w.Model.Items.Skip(delivered + 1).SelectMany(i => Rows(i, 120))));
    }

    [Theory]
    [InlineData(80)]
    [InlineData(120)]
    public async Task ITS_RESUME_DRAWS_THE_SAME_ROWS(int width)
    {
        var w = await DeliveredMidTurn(width);
        var (_, resumed) = TranscriptStore.Rebuild(TranscriptStore.BuildLines(w.Model, w.Convo), T, "coder", U);

        Assert.Equal(ModelRows(w.Model, width), ModelRows(resumed, width));
    }
}
