using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Web;
using Gatto.Repl.Render;
using Gatto.Roles;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the rich face draws the gate's row under the reply that ends a turn, and the gate's reading changes no request
public sealed class CitedRowRichTests
{
    private static StreamEvent[] Reply(string text) => [new StreamEvent.TextDelta(text), new StreamEvent.Finished("stop", null)];

    private static RichReplHarness Harness(bool gated)
    {
        var hooks = new HookBus();
        var sink = new Action<IReadOnlyList<string>>?[1];
        var h = new RichReplHarness(Path.GetTempPath(), width: 100, hooks: hooks, citedSink: sink);
        if (gated)
        {
            var gate = new CitationGate(new CitationLedger(() => null), _ => null) { Armed = true, Cited = links => sink[0]?.Invoke(links) };
            hooks.On(HookEvent.MessageEnd, gate.OnMessageEndAsync);
            h.Loop.OnShown = gate.OnShown;
        }
        h.Client.EnqueueTurn(Reply("Per https://typed.test/doc and https://made.up.test/page, yes."));
        h.Keys.Line("is https://typed.test/doc right?").Line("/quit");
        return h;
    }

    [Fact]
    public async Task THE_ROW_NAMES_THE_UNSEEN_LINK_UNDER_THE_REPLY()
    {
        var h = Harness(gated: true);

        await h.RunAsync();

        var screen = h.ScreenText();
        Assert.Contains("♯ cited, not seen this session: made.up.test/page", screen);
        Assert.True(screen.IndexOf("cited, not seen", StringComparison.Ordinal) > screen.IndexOf("Per https://typed.test/doc", StringComparison.Ordinal), screen);
    }

    [Fact]
    public async Task THE_GATES_READING_CHANGES_NO_REQUEST()
    {
        var gated = Harness(gated: true);
        var plain = Harness(gated: false);

        await gated.RunAsync();
        await plain.RunAsync();

        static string Body(RichReplHarness h) => JsonSerializer.Serialize(h.Client.Requests.Select(r => r.Messages.Select(m => new { m.Role, m.Content })));
        Assert.Equal(Body(plain), Body(gated));
        Assert.DoesNotContain("cited, not seen", plain.ScreenText());
    }
}
