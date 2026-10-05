using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//with no mouse capture the wheel would arrive as arrow keys and walk the composer's history, so the session turns that mode off for its alt screen
public class WheelModeTests : IDisposable
{
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-wheel-").FullName;
    public void Dispose() { try { Directory.Delete(_cwd, recursive: true); } catch { } }

    private sealed class FakeCtl : IConsoleModeControl
    {
        public uint Mode;
        public uint Get() => Mode;
        public void Set(uint m) => Mode = m;
    }

    [Fact]
    public async Task A_mouse_off_session_turns_alternate_scroll_off_with_the_alt_screen_and_back_on_as_it_leaves()
    {
        var h = new RichReplHarness(_cwd);
        h.Keys.Line("/quit");

        await h.RunAsync();

        var raw = h.RawWritten();
        Assert.Contains(Ansi.AltScreenEnter + Ansi.AlternateScrollOff, raw);
        Assert.True(raw.LastIndexOf(Ansi.AlternateScrollOn, StringComparison.Ordinal) > raw.LastIndexOf(Ansi.AltScreenExit, StringComparison.Ordinal),
            "alternate scroll was not turned back on after the alt screen was left");
    }

    //alt_screen false keeps the session on the plain renderer, which has no alt screen to turn the mode off for
    [Fact]
    public async Task A_session_without_the_alt_screen_writes_no_alternate_scroll_mode()
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(_cwd), "m");
        var repl = new Gatto.Repl.Repl(loop, new Conversation("sys"), "generalist", "m", null, client, null, null, _cwd,
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"), altScreen: false);
        var input = Console.In;
        var output = Console.Out;
        var sw = new StringWriter();
        try
        {
            Console.SetIn(new StringReader("/quit\n"));
            Console.SetOut(sw);
            await repl.RunAsync(CancellationToken.None);
        }
        finally { Console.SetIn(input); Console.SetOut(output); }

        Assert.DoesNotContain("?1007", sw.ToString());
    }

    [Fact]
    public async Task A_mouse_on_session_leaves_alternate_scroll_alone()
    {
        var h = new RichReplHarness(_cwd, mouseEnabled: true, modeControl: new FakeCtl());
        h.Keys.Line("/quit");

        await h.RunAsync();

        Assert.DoesNotContain("?1007", h.RawWritten());
    }
}
