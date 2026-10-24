using System.Text;
using Gatto.Terminal;

namespace Gatto.Tests.Fakes;

//a surface a prompt thread writes while the test reads it, so its text sits behind a lock and each write wakes a waiting test
public sealed class SyncSurface : ITermSurface
{
    private readonly StringBuilder _sb = new();
    private TaskCompletionSource _wrote = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public int Width { get; set; } = 80;
    public int Height { get; set; }

    public void Write(string s)
    {
        TaskCompletionSource wrote;
        lock (_sb)
        {
            _sb.Append(s);
            (wrote, _wrote) = (_wrote, new(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        wrote.SetResult();
    }

    public string Text { get { lock (_sb) return _sb.ToString(); } }

    //true once the text holds the needle. each write ends one wait, so the bound only caps a render that never comes
    public async Task<bool> WaitForAsync(string needle, TimeSpan bound)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            Task next;
            lock (_sb)
            {
                if (_sb.ToString().Contains(needle, StringComparison.Ordinal)) return true;
                next = _wrote.Task;
            }
            var left = bound - clock.Elapsed;
            if (left <= TimeSpan.Zero) return false;
            try { await next.WaitAsync(left); }
            catch (TimeoutException) { return Text.Contains(needle, StringComparison.Ordinal); }
        }
    }

    //the same wait for a test that holds no async context
    public bool WaitFor(string needle, TimeSpan bound) => WaitForAsync(needle, bound).GetAwaiter().GetResult();
}
