using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//real children, since the pipes, the exit and the kill are the subject. every task the test starts ends with the registry at teardown
public sealed class BackgroundTasksTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-bg-").FullName;
    private readonly BackgroundTasks _tasks = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<BackgroundEnd>> _ends = new();

    private sealed class Ctx(string cwd, BackgroundTasks tasks) : IToolContext
    {
        public string Cwd => cwd;
        public string HomePath => cwd;
        public IUserPrompter? Prompter => null;
        public BackgroundTasks? Tasks => tasks;
    }

    public BackgroundTasksTests() => _tasks.Ended = end => EndOf(end.Number).TrySetResult(end);

    public void Dispose()
    {
        _tasks.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (Exception) { }   //a killed child may still hold the folder for a moment, and an untidy temp folder is not worth failing a test over
    }

    private TaskCompletionSource<BackgroundEnd> EndOf(int n) =>
        _ends.GetOrAdd(n, _ => new TaskCompletionSource<BackgroundEnd>(TaskCreationOptions.RunContinuationsAsynchronously));

    private async Task<BackgroundEnd> Ended(int n) => await EndOf(n).Task.WaitAsync(Waits.Bound);

    private async Task<(int Number, ToolResult Result)> Start(string command, int? timeoutMs = null, CancellationToken ct = default)
    {
        var args = timeoutMs is { } t
            ? JsonSerializer.SerializeToElement(new { command, background = true, timeout_ms = t })
            : JsonSerializer.SerializeToElement(new { command, background = true });
        var r = await new ShellTool().ExecuteAsync(args, new Ctx(_dir, _tasks), ct);
        var n = int.Parse(r.Gloss!["background ".Length..]);
        return (n, r);
    }

    private const string Sleeper = "Start-Sleep -Seconds 120";

    [Fact]
    public async Task THE_START_RESULT_NAMES_THE_TASK_AND_NUMBERS_START_AT_1_AND_NEVER_REPEAT()
    {
        var (first, r) = await Start("exit 0");
        var (second, _) = await Start("exit 0");
        await Ended(first);
        await Ended(second);
        var (third, _) = await Start("exit 0");

        Assert.Equal("started in the background as task 1. Its output and exit code arrive when it ends.", r.Text);
        Assert.Equal("background 1", r.Gloss);
        Assert.False(r.IsError);
        Assert.Equal([1, 2, 3], new[] { first, second, third });
    }

    [Fact]
    public async Task RUNNING_LISTS_RUNNING_TASKS_ONLY_AND_THE_SNAPSHOT_KEEPS_AN_ENDED_ONE_UNTIL_IT_IS_DELIVERED()
    {
        var (slow, _) = await Start(Sleeper);
        var (quick, _) = await Start("Write-Output 'quick'");
        await Ended(quick);

        Assert.Equal([slow], _tasks.Running.Select(t => t.Task.Number));
        Assert.Equal([(slow, (string?)null), (quick, "exit 0")], _tasks.Snapshot.Select(t => (t.Task.Number, t.Gloss)));

        _tasks.Delivered(quick);
        _tasks.Delivered(slow);   //a task still running is not forgotten
        Assert.Equal([slow], _tasks.Snapshot.Select(t => t.Task.Number));
    }

    [Fact]
    public async Task THE_END_AFTER_ITS_HEAD_LINE_IS_THE_FOREGROUND_RESULT_OF_THE_SAME_COMMAND()
    {
        const string command = "Write-Output 'out'; [Console]::Error.WriteLine('err'); exit 3";
        var foreground = await new ShellTool().ExecuteAsync(JsonSerializer.SerializeToElement(new { command }), new TestToolContext(_dir), default);
        var (n, _) = await Start(command);
        var end = await Ended(n);

        var lines = end.Text.Split('\n', 2);
        Assert.StartsWith($"background task {n} ended · exit 3 · ", lines[0], StringComparison.Ordinal);
        Assert.Equal(foreground.Text, lines[1]);
        Assert.Equal(("exit 3", true, BackgroundTasks.Exit), (end.Gloss, end.IsError, end.Cause));
    }

    [Fact]
    public async Task A_KILL_ENDS_THE_TASK_AT_ONCE_AND_ITS_END_NAMES_THE_KILL()
    {
        var (n, _) = await Start(Sleeper);

        Assert.True(_tasks.Kill(n, BackgroundTasks.ByTasks));
        Assert.Empty(_tasks.Running);   //the kill took the end, before the exit arrives
        Assert.False(_tasks.Kill(n, BackgroundTasks.ByTasks));
        var end = await Ended(n);

        Assert.StartsWith($"background task {n} killed by /tasks · ", end.Text, StringComparison.Ordinal);
        Assert.Equal(("killed", true), (end.Gloss, end.IsError));
        Assert.False(_tasks.Kill(99, BackgroundTasks.ByTasks));
    }

    [Fact]
    public async Task KILL_ALL_ADVANCES_THE_GENERATION_FIRST_AND_COUNTS_WHAT_IT_ENDED()
    {
        var (a, _) = await Start(Sleeper);
        var (b, _) = await Start(Sleeper);
        var (done, _) = await Start("exit 0");
        await Ended(done);
        var before = _tasks.Generation;

        Assert.Equal(2, _tasks.KillAll(BackgroundTasks.AtNew));

        Assert.Equal(before + 1, _tasks.Generation);
        Assert.Empty(_tasks.Snapshot);   //the ended task waiting for the model goes with the conversation it was for
        foreach (var n in new[] { a, b })
        {
            var end = await Ended(n);
            Assert.Equal((before, BackgroundTasks.AtNew), (end.Generation, end.Cause));
            Assert.StartsWith($"background task {n} killed at /new · ", end.Text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_DEADLINE_KILLS_THE_TASK_AND_ITS_END_SAYS_SO()
    {
        var (n, _) = await Start(Sleeper, timeoutMs: 300);
        var end = await Ended(n);

        Assert.Equal(BackgroundTasks.AtTimeout, end.Cause);
        Assert.StartsWith($"background task {n} killed at its timeout · ", end.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WITHOUT_A_TIMEOUT_A_TASK_HAS_NO_DEADLINE()
    {
        var (n, _) = await Start("exit 0");
        await Ended(n);
        var (later, _) = await Start(Sleeper, timeoutMs: 60_000);

        Assert.Null(_tasks.Snapshot.Single(t => t.Task.Number == n).Task.DeadlineMs);
        Assert.Equal(60_000, _tasks.Snapshot.Single(t => t.Task.Number == later).Task.DeadlineMs);
    }

    [Fact]
    public async Task A_GRANDCHILD_HOLDING_THE_PIPE_ENDS_THE_DRAIN_AT_THE_GRACE_AND_IS_SAID()
    {
        var (n, _) = await Start("Start-Process -NoNewWindow -FilePath powershell -WorkingDirectory $env:TEMP -ArgumentList "
            + "'-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 30'; Write-Output 'launched'");
        var end = await Ended(n);

        Assert.Contains("launched", end.Text, StringComparison.Ordinal);
        Assert.Contains(ShellTool.PipeHeldNote, end.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_CANCEL_OF_THE_TURN_THAT_STARTED_A_TASK_LEAVES_ITS_DRAINS_RUNNING()
    {
        using var turn = new CancellationTokenSource();
        var (n, _) = await Start("Start-Sleep -Milliseconds 800; Write-Output 'after the cancel'", ct: turn.Token);
        turn.Cancel();
        var end = await Ended(n);

        Assert.Equal(BackgroundTasks.Exit, end.Cause);
        Assert.Contains("after the cancel", end.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(BackgroundTasks.Exit, BackgroundTasks.ByTasks)]
    [InlineData(BackgroundTasks.ByTasks, BackgroundTasks.Exit)]
    [InlineData(BackgroundTasks.AtTimeout, BackgroundTasks.ByTasks)]
    public void THE_FIRST_CAUSE_TO_TAKE_THE_END_IS_THE_ONE_END(string first, string second)
    {
        var claim = new EndClaim();

        Assert.True(claim.TryTake(new EndClaim.Taken(first, TimeSpan.FromSeconds(1), "a")));
        Assert.False(claim.TryTake(new EndClaim.Taken(second, TimeSpan.FromSeconds(2), "b")));
        Assert.Equal(first, claim.Value!.Cause);
    }

    [Theory]
    [InlineData(BackgroundTasks.Exit, "background task 4 ended · exit 2 · 3s")]
    [InlineData(BackgroundTasks.ByTasks, "background task 4 killed by /tasks · 3s")]
    [InlineData(BackgroundTasks.AtTimeout, "background task 4 killed at its timeout · 3s")]
    [InlineData(BackgroundTasks.AtNew, "background task 4 killed at /new · 3s")]
    public void THE_HEAD_LINE_NAMES_THE_CAUSE(string cause, string head)
    {
        Assert.Equal(head, BackgroundTasks.HeadLine(4, cause, 2, TimeSpan.FromSeconds(3)));
    }

    //the task's own child, as the foreground's timeout test reads it. a grandchild is the job's to end, under the sandbox
    [Fact]
    public async Task THE_KILLED_TASK_S_OWN_CHILD_HAS_EXITED_WHEN_ITS_END_ARRIVES()
    {
        var pidFile = Path.Combine(_dir, "pid.txt");
        var (n, _) = await Start($"Set-Content -Path '{pidFile}' -Value $PID; {Sleeper}");
        Waits.Until(() => File.Exists(pidFile) && int.TryParse(SafeRead(pidFile), out _), () => "the child's pid never arrived");
        var pid = int.Parse(SafeRead(pidFile));
        var killedAt = DateTime.Now;

        Assert.True(_tasks.Kill(n, BackgroundTasks.ByTasks));
        await Ended(n);

        try
        {
            using var child = Process.GetProcessById(pid);
            Assert.True(child.HasExited || child.StartTime > killedAt, $"the task's child {pid} is still running");   //a later start is a reused pid
        }
        catch (ArgumentException) { }   //the process is gone, which is the property this test wants
    }

    private static string SafeRead(string path)
    {
        try { return File.ReadAllText(path).Trim(); }
        catch (IOException) { return ""; }
    }
}
