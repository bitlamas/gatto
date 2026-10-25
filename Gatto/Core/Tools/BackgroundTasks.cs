using System.Diagnostics;
using System.Text;
using Gatto.Core.Sandbox;

namespace Gatto.Core.Tools;

//one task as the registry started it. the generation is the registry's at the start, so an end composed after a /new is told apart
public sealed record BackgroundTask(int Number, string Command, DateTime StartedUtc, int? DeadlineMs, int Generation);

//a task's one end, composed once with one cause, the text headed by the line the model and the screen read first
public sealed record BackgroundEnd(int Number, int Generation, string Text, string Gloss, bool IsError, string Cause, TimeSpan Elapsed);

//a task as a list shows it, its gloss null while it runs
public sealed record BackgroundState(BackgroundTask Task, TimeSpan Elapsed, string? Gloss);

//the first cause to take a task's end, so an exit that races a kill or a deadline gives exactly one end
internal sealed class EndClaim
{
    private Taken? _taken;

    internal sealed record Taken(string Cause, TimeSpan Elapsed, string Gloss);

    public Taken? Value => Volatile.Read(ref _taken);

    public bool TryTake(Taken taken) => Interlocked.CompareExchange(ref _taken, taken, null) is null;
}

//the shell commands a session started in the background, one registry per process. it owns each child, its drains and its one end, and tells nobody how it looks
public sealed class BackgroundTasks : IDisposable
{
    //the causes an end names, an exit or the kill that took it
    public const string Exit = "exit";
    public const string ByTasks = "/tasks";
    public const string AtTimeout = "timeout";
    public const string AtNew = "/new";
    public const string AtQuit = "/quit";

    private readonly object _lock = new();
    private readonly SortedDictionary<int, Entry> _tasks = new();
    //the registry's own token, cancelled only when the registry ends, so a turn's Esc never reaches a task's drains
    private readonly CancellationTokenSource _cts = new();
    private int _next;
    private int _generation;

    //where each end goes, set by the face that armed the registry. called on the task's own thread
    public Action<BackgroundEnd>? Ended { get; set; }

    public int Generation { get { lock (_lock) return _generation; } }

    private sealed class Entry(BackgroundTask task, ShellTool.Started started)
    {
        public readonly BackgroundTask Task = task;
        public readonly ShellTool.Started Started = started;
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public readonly EndClaim Claim = new();
        public readonly StringBuilder Out = new();
        public readonly StringBuilder Err = new();
        public readonly object Gate = new();
    }

    //takes the child the tool started, numbered 1, 2, 3 in the process and never reused
    internal int Start(ShellTool.Started started, string command, int? deadlineMs)
    {
        //the tree kill matches children against this start time, which cannot be read once the exit is seen, so it is read now
        if (started.Job is null)
            try { _ = started.Process.StartTime; }
            catch (InvalidOperationException) { }   //it has already exited, so there is no tree left to kill
        Entry entry;
        lock (_lock)
        {
            var task = new BackgroundTask(++_next, command, DateTime.UtcNow, deadlineMs, _generation);
            entry = new Entry(task, started);
            _tasks[task.Number] = entry;
        }
        _ = Task.Run(() => RunAsync(entry), CancellationToken.None);
        return entry.Task.Number;
    }

    //the tasks still running, oldest first, read from the end a kill takes at once rather than from an exit observed later
    public IReadOnlyList<BackgroundState> Running
    {
        get { lock (_lock) return [.. _tasks.Values.Where(e => e.Claim.Value is null).Select(e => new BackgroundState(e.Task, e.Clock.Elapsed, null))]; }
    }

    //running and ended tasks whose end the model has not taken yet, oldest first
    public IReadOnlyList<BackgroundState> Snapshot
    {
        get
        {
            lock (_lock)
                return [.. _tasks.Values.Select(e => e.Claim.Value is { } t
                    ? new BackgroundState(e.Task, t.Elapsed, t.Gloss)
                    : new BackgroundState(e.Task, e.Clock.Elapsed, null))];
        }
    }

    //the end reached the model in a turn that kept it, so the task is forgotten. a task still running stays
    public void Delivered(int number)
    {
        lock (_lock)
            if (_tasks.TryGetValue(number, out var e) && e.Claim.Value is not null) _tasks.Remove(number);
    }

    //false when no task has the number or it has already ended
    public bool Kill(int number, string cause)
    {
        Entry? e;
        lock (_lock) _tasks.TryGetValue(number, out e);
        return e is not null && KillEntry(e, cause);
    }

    //ends every running task with the cause, after the generation advances, so no end composed from here on reaches the conversation that asked
    public int KillAll(string cause)
    {
        List<Entry> all;
        lock (_lock)
        {
            _generation++;
            all = [.. _tasks.Values];
            _tasks.Clear();
        }
        return all.Count(e => KillEntry(e, cause));
    }

    private bool KillEntry(Entry e, string cause)
    {
        if (!e.Claim.TryTake(new EndClaim.Taken(cause, e.Clock.Elapsed, "killed"))) return false;
        KillTree(e.Started);
        return true;
    }

    //the job ends every process it holds, a detached grandchild included. off the sandbox the tree is followed by parent links
    private static void KillTree(ShellTool.Started started)
    {
        if (OperatingSystem.IsWindows() && started.Job is { } job)
        {
            try { SandboxNative.TerminateJobObject(job, 1); }
            catch (ObjectDisposedException) { }   //the sandbox closed the job once its last process ended, so the exit came first
            return;
        }
        try { started.Process.Kill(entireProcessTree: true); }
        catch (Exception) { }   //it has already exited, or a child went first, and the exit the task observes ends it either way
    }

    private async Task RunAsync(Entry e)
    {
        var ct = _cts.Token;
        var proc = e.Started.Process;
        var stdoutTask = ShellTool.DrainAsync(e.Started.Stdout, e.Out, e.Gate, ct);
        var stderrTask = ShellTool.DrainAsync(e.Started.Stderr, e.Err, e.Gate, ct);
        try
        {
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                if (e.Task.DeadlineMs is int ms) deadline.CancelAfter(ms);
                try { await proc.WaitForExitAsync(deadline.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    KillEntry(e, AtTimeout);
                    await proc.WaitForExitAsync(ct);
                }
            }
            var exitCode = proc.ExitCode;
            e.Claim.TryTake(new EndClaim.Taken(Exit, e.Clock.Elapsed, $"exit {exitCode}"));

            //the same grace the foreground gives, and a grandchild holding the pipe is said in the text
            var pipeHeld = false;
            try { await Task.WhenAll(stdoutTask, stderrTask).WaitAsync(TimeSpan.FromMilliseconds(ShellTool.DrainGraceMs), ct); }
            catch (TimeoutException) { pipeHeld = true; }

            string outText, errText;
            lock (e.Gate) { outText = e.Out.ToString(); errText = e.Err.ToString(); }
            var body = ShellTool.Compose(outText, errText, exitCode, pipeHeld, e.Started.SandboxProject, e.Started.SandboxRoots);
            var taken = e.Claim.Value!;
            var killed = taken.Cause != Exit;
            var head = HeadLine(e.Task.Number, taken.Cause, exitCode, taken.Elapsed);
            var end = new BackgroundEnd(e.Task.Number, e.Task.Generation, body.Length > 0 ? head + "\n" + body : head,
                taken.Gloss, killed || exitCode != 0, taken.Cause, taken.Elapsed);
            Release(e.Started);
            try { Ended?.Invoke(end); }
            catch (Exception) { }   //a sink that throws must not leave the task half ended, the end is already composed
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Release(e.Started);   //the registry ended with the process, and nobody is left to read the end
        }
    }

    private static void Release(ShellTool.Started started)
    {
        started.Scope?.Dispose();
        started.Process.Dispose();
    }

    //the first line of an end's text, which names the task, its cause and how long it ran
    public static string HeadOf(string? text) => (text ?? "").Split('\n', 2)[0];

    //the line that heads an end, the same words on the screen and in the record
    internal static string HeadLine(int number, string cause, int exitCode, TimeSpan elapsed)
    {
        var took = ElapsedText.Of(elapsed);
        return cause switch
        {
            Exit => $"background task {number} ended · exit {exitCode} · {took}",
            ByTasks => $"background task {number} killed by /tasks · {took}",
            AtTimeout => $"background task {number} killed at its timeout · {took}",
            _ => $"background task {number} killed at {cause} · {took}",
        };
    }

    //the process is ending, so every task still running ends with it
    public void Dispose()
    {
        KillAll(AtQuit);
        _cts.Cancel();
        _cts.Dispose();
    }
}
