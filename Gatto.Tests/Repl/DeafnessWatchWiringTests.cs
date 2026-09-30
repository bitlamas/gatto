using Gatto.Core.Client;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the rich loop arms the watchdog for a turn and for a modal wait, and its row reaches the screen
[Collection("e2e")]
public class DeafnessWatchWiringTests
{
    private sealed class CountingWriter : IConsoleInputWriter
    {
        public int Writes;
        public bool Write(INPUT_RECORD record) { Interlocked.Increment(ref Writes); return true; }
    }

    private static InputDeafnessWatchdog DeafWatch(CountingWriter writer) =>
        new(() => 0, writer, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(20));

    [Fact]
    public async Task A_TURN_ARMS_THE_WATCHDOG_AND_ITS_ROW_REACHES_THE_SCREEN()
    {
        var writer = new CountingWriter();
        var h = new RichReplHarness(Path.GetTempPath(), width: 120, deafWatch: DeafWatch(writer));
        h.Client.FirstEventDelayMs = 5000;
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("a long turn");

        await h.RunUntilAsync(() => h.Saw(InputDeafnessWatchdog.Row));

        Assert.True(writer.Writes >= 1);
    }

    [Fact]
    public async Task A_MODAL_WAIT_AT_REST_ARMS_THE_WATCHDOG()
    {
        var writer = new CountingWriter();
        var h = new RichReplHarness(Path.GetTempPath(), width: 120, deafWatch: DeafWatch(writer));
        InputPump.FocusScope? focus = null;

        await h.RunUntilAsync(() =>
        {
            if (focus is null && h.Pump.HasModalWaitSink) focus = h.Pump.PushFocus();
            return h.Saw(InputDeafnessWatchdog.Row);
        });
        focus?.Dispose();

        Assert.True(writer.Writes >= 1);
    }

    //an arm that outlives its turn or its prompt keeps writing into the console of a session at rest.
    [Fact]
    public async Task A_FINISHED_TURN_AND_A_CLOSED_PROMPT_LEAVE_NOTHING_ARMED()
    {
        var watch = new InputDeafnessWatchdog(() => 0, new CountingWriter(), TimeSpan.FromHours(1), TimeSpan.Zero);
        var h = new RichReplHarness(Path.GetTempPath(), width: 120, deafWatch: watch);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("a short turn");
        InputPump.FocusScope? focus = null;
        var stage = 0;

        await h.RunUntilAsync(() =>
        {
            switch (stage)
            {
                case 0 when h.Saw("done") && watch.ArmedDepth == 0:
                    focus = h.Pump.PushFocus();
                    stage = 1;
                    return false;
                case 1 when watch.ArmedDepth == 1:
                    focus!.Dispose();
                    stage = 2;
                    return false;
                case 2:
                    return watch.ArmedDepth == 0;
                default:
                    return false;
            }
        });

        Assert.Equal(2, stage);
    }

    //the negative half. a session at rest with no prompt open arms nothing, so it writes nothing into the console across many check periods.
    [Fact]
    public async Task AT_REST_NOTHING_IS_WRITTEN()
    {
        var writer = new CountingWriter();
        var h = new RichReplHarness(Path.GetTempPath(), width: 120, deafWatch: DeafWatch(writer));
        var clock = new System.Diagnostics.Stopwatch();

        await h.RunUntilAsync(() =>
        {
            if (!clock.IsRunning && h.Pump.HasModalWaitSink) clock.Start();
            return clock.ElapsedMilliseconds > 300;
        });

        Assert.Equal(0, writer.Writes);
        Assert.False(h.Saw(InputDeafnessWatchdog.Row));
    }
}
