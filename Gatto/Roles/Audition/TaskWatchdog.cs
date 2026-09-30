using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Tools;

namespace Gatto.Roles.Audition;

//which allowance cut the task short, or None, and the cut is evidence of whose problem it was rather than a softer verdict
internal enum StopReason
{
    //the task ran to its own conclusion
    None,

    //generation passed TaskLimits.TokenCap, the model was still going and had produced no answer
    TokenCap,

    //nothing arrived for the whole allowance, the model was not generating at all
    Silence,
}

//what bounds a task is its token allowance plus a liveness check, a flat clock would measure the machine and fail a slow one
internal sealed record TaskLimits(int TokenCap, TimeSpan FirstToken, TimeSpan Stall)
{
    //every value here comes from the measured runs, and the margins are wide so a false cut does not become a false verdict
    public static readonly TaskLimits Default =
        new(TokenCap: 2_000, FirstToken: TimeSpan.FromMinutes(2), Stall: TimeSpan.FromSeconds(30));
}

//counts the stream's own deltas rather than the server's final usage, pauses the clock while a tool runs, and cuts by cancelling the linked token
internal sealed class TaskWatchdog : ITurnObserver
{
    private readonly TaskLimits limits;
    private readonly CancellationTokenSource cts;
    private int generated;
    private int reason;                 //the reason code, a StopReason written with Interlocked because the stall timer runs on another thread
    private TimeSpan armed;

    //the first-token allowance is armed here, waiting for the first delta would miss a model that never produces one
    public TaskWatchdog(TaskLimits limits, CancellationTokenSource cts)
    {
        this.limits = limits;
        this.cts = cts;
        Arm(limits.FirstToken);
    }

    //why the task was cut, and a cancellation with no recorded reason is the stall, whose timer had nobody to write it
    public StopReason Reason
    {
        get
        {
            var recorded = (StopReason)Volatile.Read(ref reason);
            if (recorded != StopReason.None) return recorded;
            return cts.IsCancellationRequested ? StopReason.Silence : StopReason.None;
        }
    }

    //the allowance that actually expired, read off the armed value since the two silences have different allowances
    public TimeSpan Silence => armed;

    //tokens seen, exposed so a test can assert the cut happened at the cap and not merely that something cancelled
    public int Generated => Volatile.Read(ref generated);

    public void OnTextDelta(string text) => Tick();
    public void OnReasoningDelta(string text) => Tick();
    public void OnToolCallDelta(string? name = null, string? partialArguments = null) => Tick();

    //the time is gatto's now, so the clock stops until the tool answers
    public void OnToolCallStart(ToolCall call) => Arm(Timeout.InfiniteTimeSpan);

    //a new round means prefill again, which gets the generous first-token allowance
    public void OnToolResult(ToolCall call, ToolResult result) => Arm(limits.FirstToken);

    public void OnWarning(string message) { }
    public void OnUsage(Usage usage) { }

    private void Tick()
    {
        if (Interlocked.Increment(ref generated) > limits.TokenCap)
        {
            Interlocked.CompareExchange(ref reason, (int)StopReason.TokenCap, (int)StopReason.None);
            Cancel();
            return;
        }
        Arm(limits.Stall);
    }

    private void Arm(TimeSpan allowance)
    {
        if (allowance != Timeout.InfiniteTimeSpan) armed = allowance;
        try { cts.CancelAfter(allowance); }
        catch (ObjectDisposedException) { }   //the run ended first, nothing left to cut
    }

    private void Cancel()
    {
        try { cts.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (AggregateException) { }        //a callback threw and the cancel still happened
    }
}
