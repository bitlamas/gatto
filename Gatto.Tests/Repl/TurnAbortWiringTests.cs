//only the real loop shows the abort handle reading the turn's CTS live rather than a snapshot, so this drives the rich loop
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Repl;

public class TurnAbortWiringTests : IDisposable
{
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-abortwiring-").FullName;
    public void Dispose() { try { Directory.Delete(_cwd, recursive: true); } catch { } }

    //a tool body runs inside a live turn, the only vantage from which the handle's mid-turn value can be read.
    private sealed class MidTurnProbeTool(TurnAbortHandle handle, bool alsoAbort) : ITool
    {
        public CancellationTokenSource? Seen { get; private set; }
        //sample inside the tool, since the repl disposes the turn CTS when the turn ends and touching its token afterwards throws
        public bool SeenIsLive { get; private set; }
        public bool Ran { get; private set; }
        public string Name => "probe";
        public string Description => "reads the turn-abort handle from inside a turn";
        public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        {
            Ran = true;
            Seen = handle.Current?.Invoke();
            SeenIsLive = Seen is not null && !Seen.IsCancellationRequested && Seen.Token.CanBeCanceled;
            if (alsoAbort) handle.RequestAbort();
            return Task.FromResult(new ToolResult("probed"));
        }
    }

    private static (RichReplHarness H, MidTurnProbeTool Probe) Rig(
        string cwd, TurnAbortHandle handle, bool alsoAbort)
    {
        var probe = new MidTurnProbeTool(handle, alsoAbort);
        var reg = new ToolRegistry();
        reg.Register(probe);
        var h = new RichReplHarness(cwd, tools: reg, turnAbort: handle);
        //the second queued round gives the loop something to dequeue. an empty queue throws inside the fake client and reads as a harness fault.
        h.Client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "probe", "{}")),
            new StreamEvent.Finished("tool_calls", null));
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("second round"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("go").Line("/quit");
        return (h, probe);
    }

    //the handle must answer non-null during a turn and null after it, so a snapshot taken at either time fails one half
    [Fact]
    public async Task RichLoop_ArmsTheHandle_WithALiveReadOfTheTurnCts()
    {
        var handle = new TurnAbortHandle();
        Assert.Null(handle.Current);                       //the handle is unarmed before the loop starts.
        var (h, probe) = Rig(_cwd, handle, alsoAbort: false);

        await h.RunAsync();

        Assert.True(probe.Ran, "the scripted turn never reached the tool — the harness script did not dispatch");
        Assert.NotNull(handle.Current);                    //the rich loop must have armed the handle.
        Assert.NotNull(probe.Seen);
        Assert.True(probe.SeenIsLive, "the handle answered with a CTS that was not the live turn's");
        Assert.Null(handle.Current!.Invoke());
    }

    //an abort raised in the tool seam mid-turn must end the turn, and the cancellation row on screen is the oracle
    [Fact]
    public async Task RequestAbort_MidTurn_EndsTheTurn_ThroughTheRealRichLoop()
    {
        var handle = new TurnAbortHandle();
        var (h, probe) = Rig(_cwd, handle, alsoAbort: true);

        await h.RunAsync();

        Assert.True(probe.Ran);
        Assert.Contains("turn cancelled", h.ScreenText(), StringComparison.Ordinal);
        //the turn ended first, so the second round's text must never reach the transcript.
        Assert.DoesNotContain("second round", h.ScreenText(), StringComparison.Ordinal);
    }

    //the twin test holds everything fixed except the abort, so a cancellation row can only come from the abort itself
    [Fact]
    public async Task WithoutTheAbort_TheSameScriptRunsTheSecondRound()
    {
        var handle = new TurnAbortHandle();
        var (h, probe) = Rig(_cwd, handle, alsoAbort: false);

        await h.RunAsync();

        Assert.True(probe.Ran);
        Assert.Contains("second round", h.ScreenText(), StringComparison.Ordinal);
        Assert.DoesNotContain("turn cancelled", h.ScreenText(), StringComparison.Ordinal);
    }

    //the rich branch never runs in a test process, so a source check pins that one shared handle reaches the prompter and the repl

    private static string GattoAppSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Gatto", "Cli", "GattoApp.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException(
            $"could not find Gatto/Cli/GattoApp.cs above {AppContext.BaseDirectory} — this test reads the repo source");
    }

    [Fact]
    public void GattoApp_HandsTheSameHandle_ToBothThePrompterAndTheRepl()
    {
        var src = GattoAppSource();

        Assert.Contains("turnAbort = new TurnAbortHandle();", src, StringComparison.Ordinal);
        //this is the prompter's half. without it esc cancels the call and the turn runs on.
        Assert.Contains("abort: turnAbort", src, StringComparison.Ordinal);
        //this is the repl's half. without it the handle is never armed and an abort is a permanent no-op.
        Assert.Contains("turnAbort: turnAbort", src, StringComparison.Ordinal);
    }
}
