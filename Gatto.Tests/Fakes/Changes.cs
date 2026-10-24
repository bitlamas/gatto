namespace Gatto.Tests.Fakes;

//one signal every fake a test reads fires on each change, so a wait wakes on the change itself
public sealed class Changes
{
    private readonly object _gate = new();
    private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

    //the next change. take it before reading the condition, so a change between the read and the await is never missed
    public Task Next { get { lock (_gate) return _next.Task; } }

    public void Mark()
    {
        TaskCompletionSource fired;
        lock (_gate) (fired, _next) = (_next, new(TaskCreationOptions.RunContinuationsAsynchronously));
        fired.SetResult();
    }

    //true once the condition holds, false at the bound or when the stop task ends. an unsignalled condition reads state no change marks, so it is spun
    public async Task<bool> UntilAsync(Func<bool> holds, TimeSpan bound, Task? stop = null, bool unsignalled = false)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var spinner = new SpinWait();
        while (true)
        {
            var next = Next;
            if (holds()) return true;
            var left = bound - clock.Elapsed;
            if (left <= TimeSpan.Zero || stop is { IsCompleted: true }) return false;
            if (unsignalled)
            {
                spinner.SpinOnce();
                await Task.Yield();
                continue;
            }
            try { await (stop is null ? next : Task.WhenAny(next, stop)).WaitAsync(left); }
            catch (TimeoutException) { return holds(); }
        }
    }
}
