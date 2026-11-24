using Gatto.Cli.Inbox;

namespace Gatto.Tests.Inbox;

//the state folder: records changed only under their gate, arms that take an inbox over, the one-watcher lock, and the sweep of dead weight
public sealed class InboxStateTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-state-").FullName;
    private readonly string _sid = Guid.NewGuid().ToString();
    private InboxState State => new(_home);

    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private InboxState.Session Record(string? inbox = null) =>
        new(@"C:\proj", @"C:\proj\t.jsonl", new InboxState.Proc(4242, 99), null, inbox, [], []);

    private string StateDir => Path.Combine(_home, "claude");
    private string RecordPath => Path.Combine(StateDir, "claude-" + _sid + ".json");

    [Fact]
    public void A_WRITE_THEN_A_READ_ROUND_TRIPS_THE_RECORD_AND_THE_ARM()
    {
        var inbox = Path.Combine(_home, "inbox-seat");
        var arm = new InboxState.Arm("tail-5-5", inbox, _sid, new InboxState.Proc(7, 11));

        State.Update(_sid, _ => Record(inbox));
        State.WriteArm(arm);

        var read = State.ReadSession(_sid);
        Assert.NotNull(read);
        Assert.Equal(@"C:\proj", read.ProjectFolder);
        Assert.Equal(inbox, read.Inbox);
        Assert.Equal(4242, read.Claude.Pid);
        var got = State.ArmFor(inbox);
        Assert.NotNull(got);
        Assert.Equal(_sid, got.Value.Arm.SessionId);
        Assert.Equal(11, got.Value.Arm.Claude.StartTicks);
        Assert.Equal([arm], State.ArmsNaming(_sid));
        Assert.Equal(arm, State.ArmByClaude(arm.Claude));
        Assert.Null(State.ArmByClaude(new InboxState.Proc(7, 12)));   //a reused pid is not the same process
    }

    [Fact]
    public void ONE_INBOX_ARMS_UNDER_ONE_NAME_AND_A_SECOND_INBOX_UNDER_ANOTHER()
    {
        var a = new InboxState.Arm("tail-5-5", Path.Combine(_home, "one"), _sid, new InboxState.Proc(1, 1));
        var b = new InboxState.Arm("tail-5-5", Path.Combine(_home, "two"), _sid, new InboxState.Proc(1, 1));
        State.WriteArm(a);
        State.WriteArm(a);   //the same inbox writes the same file name again
        State.WriteArm(b);
        var armed = Directory.GetFiles(StateDir, "*.armed").Select(Path.GetFileName).ToList();
        Assert.Equal(2, armed.Count);
        Assert.NotNull(State.ArmFor(a.Inbox));
        Assert.NotNull(State.ArmFor(b.Inbox));
        State.DeleteArm(a);
        Assert.Null(State.ArmFor(a.Inbox));
    }

    [Fact]
    public void TWO_UPDATES_KEEP_BOTH_FIELDS_A_LATER_READ_SEES()
    {
        State.Update(_sid, _ => Record());
        State.Update(_sid, r => r! with { Model = "claude-first-5-5" });
        State.Update(_sid, r => r! with { Inbox = @"C:\proj\inbox-b" });
        var read = State.ReadSession(_sid);
        Assert.Equal("claude-first-5-5", read!.Model);
        Assert.Equal(@"C:\proj\inbox-b", read.Inbox);
    }

    [Fact]
    public void AN_UNCHANGED_RECORD_AND_A_NULL_CHANGE_BOTH_WRITE_NOTHING()
    {
        State.Update(_sid, _ => Record());
        var before = File.GetLastWriteTimeUtc(RecordPath);

        State.Update(_sid, r => r);
        Assert.Equal(before, File.GetLastWriteTimeUtc(RecordPath));

        State.Update(_sid, _ => null);
        Assert.Equal(before, File.GetLastWriteTimeUtc(RecordPath));

        var other = Guid.NewGuid().ToString();
        State.Update(other, _ => null);
        Assert.False(File.Exists(Path.Combine(StateDir, "claude-" + other + ".json")));
    }

    [Fact]
    public void A_HELD_RECORD_GATE_MAKES_AN_UPDATE_THROW_THE_BUSY_MESSAGE_AFTER_THE_RETRIES()
    {
        State.Update(_sid, _ => Record());
        Directory.CreateDirectory(StateDir);
        using (new FileStream(Path.Combine(StateDir, "claude-" + _sid + ".rec.lock"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var started = System.Diagnostics.Stopwatch.StartNew();
            var ex = Assert.Throws<IOException>(() => State.Update(_sid, r => r! with { Model = "x" }));
            Assert.Equal("the record of this session is busy", ex.Message);
            Assert.True(started.Elapsed >= TimeSpan.FromSeconds(1.9), "the retries ended early");
        }
    }

    [Fact]
    public void THE_WATCHER_LOCK_IS_GIVEN_ONCE_AND_FREED_BY_THE_KILL_OF_ITS_HOLDER()
    {
        var first = State.TryLock(_sid);
        Assert.NotNull(first);
        Assert.Null(State.TryLock(_sid));   //a second watcher leaves at once
        Assert.False(State.LockFree(_sid));
        first!.Dispose();
        Assert.True(State.LockFree(_sid));

        //the lock's release on death: a child holds the lock, the kill frees it
        var path = Path.Combine(StateDir, "claude-" + _sid + ".lock");
        var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
            $"-NoProfile -Command \"$f=[IO.File]::Open('{path}','OpenOrCreate','ReadWrite','None'); Start-Sleep 60\"")
        { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            Gatto.Tests.Fakes.Waits.Until(() => !State.LockFree(_sid), () => "the child never took the lock");
        }
        finally
        {
            child.Kill();
            child.WaitForExit(5000);
        }
        Gatto.Tests.Fakes.Waits.Until(() => State.LockFree(_sid), () => "the kill did not free the lock");
    }

    //a pid file names a hook when its process lives under the exe's name and started at the file's ticks, and the uninstall ends it
    [Fact]
    public void A_PID_FILE_NAMES_A_LIVE_HOOK_OF_THE_EXE_AND_THE_UNINSTALL_ENDS_IT()
    {
        State.WritePid(_sid);   //the test process carries another name, so its own file names no hook
        Assert.Empty(InboxState.HookPids(_home, "gatto"));
        State.DeletePid(_sid);
        Assert.Empty(Directory.GetFiles(StateDir, "*.pid"));

        var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("powershell.exe",
            "-NoProfile -Command \"Start-Sleep 60\"") { UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var ticks = child.StartTime.ToUniversalTime().Ticks;
            var pid = Path.Combine(StateDir, "claude-" + _sid + ".pid");
            File.WriteAllText(pid, child.Id + "\n" + (ticks + 1));
            Assert.Empty(InboxState.HookPids(_home, "powershell"));   //a reused pid started at other ticks
            File.WriteAllText(pid, child.Id + "\n" + ticks);
            Assert.Equal([child.Id], InboxState.HookPids(_home, "powershell"));
            Assert.Equal([5], Gatto.Cli.Install.UninstallSite.Others([5, child.Id, 7], 7, InboxState.HookPids(_home, "powershell")));
            InboxState.EndHooks(_home, "powershell");
            Assert.True(child.WaitForExit(5000), "the hook was not ended");
        }
        finally
        {
            if (!child.HasExited) child.Kill();
        }
    }

    [Fact]
    public void A_FILE_THAT_PARSES_WITH_A_MEMBER_MISSING_READS_AS_ABSENT()
    {
        Directory.CreateDirectory(StateDir);
        File.WriteAllText(Path.Combine(StateDir, "claude-aaa-000000000000.armed"), "{}");
        File.WriteAllText(RecordPath, "{}");
        Assert.Null(State.ArmFor(Path.Combine(_home, "inbox-seat")));
        Assert.Empty(State.ArmsNaming(_sid));
        Assert.Null(State.ReadSession(_sid));
    }

    [Fact]
    public void A_DELETE_THE_FILE_SYSTEM_REFUSES_THROWS()
    {
        var arm = new InboxState.Arm("tail-5-5", Path.Combine(_home, "inbox-seat"), _sid, new InboxState.Proc(1, 1));
        State.WriteArm(arm);
        using (new FileStream(Directory.GetFiles(StateDir, "*.armed").Single(), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.ThrowsAny<IOException>(() => State.DeleteArm(arm));
        Assert.NotNull(State.ArmFor(arm.Inbox));
    }

    [Fact]
    public void THE_SWEEP_SHEDS_AN_OLD_UNARMED_DEAD_SESSION_AND_KEEPS_THE_LIVE_AND_THE_ARMED()
    {
        var dead = Guid.NewGuid().ToString();
        var live = Guid.NewGuid().ToString();
        var armed = Guid.NewGuid().ToString();
        var inbox = Path.Combine(_home, "inbox-seat");
        var old = DateTime.Now.AddDays(-8);
        Func<InboxState.Proc, bool> alive = p => p.Pid == 1234;

        State.Update(dead, _ => Record());
        State.LockFree(dead);   //a watcher's lock file stands beside the record
        State.Update(live, _ => new InboxState.Session(@"C:\p", @"C:\p\t", new InboxState.Proc(1234, 1), null, null, [], []));
        State.Update(armed, _ => Record(inbox));
        State.WriteArm(new InboxState.Arm("tail-5-5", inbox, armed, new InboxState.Proc(1234, 1)));
        foreach (var file in Directory.GetFiles(StateDir, "claude-*"))
            File.SetLastWriteTime(file, old);

        State.Sweep(DateTime.Now, alive);

        Assert.Equal([], Directory.GetFiles(StateDir, "claude-" + dead + "*").Select(Path.GetFileName));   //the lock goes too
        Assert.True(File.Exists(Path.Combine(StateDir, "claude-" + live + ".json")));
        Assert.True(File.Exists(Path.Combine(StateDir, "claude-" + armed + ".json")));
        Assert.NotNull(State.ArmFor(inbox));   //the arm's process is alive, the arm stays
    }

    [Fact]
    public void THE_SWEEP_ENDS_AN_ARM_WHOSE_PROCESS_IS_GONE_AND_WHOSE_RECORD_AGED_OR_ABSENT()
    {
        var inboxA = Path.Combine(_home, "inbox-a");
        var inboxB = Path.Combine(_home, "inbox-b");
        var aged = Guid.NewGuid().ToString();
        var old = DateTime.Now.AddDays(-8);

        State.Update(aged, _ => Record(inboxA));
        State.WriteArm(new InboxState.Arm("tail-5-5", inboxA, aged, new InboxState.Proc(9, 9)));
        State.WriteArm(new InboxState.Arm("tail-5-5", inboxB, Guid.NewGuid().ToString(), new InboxState.Proc(9, 9)));
        foreach (var file in Directory.GetFiles(StateDir, "claude-*"))
            File.SetLastWriteTime(file, old);

        State.Sweep(DateTime.Now, _ => false);

        Assert.Null(State.ArmFor(inboxA));
        Assert.Null(State.ArmFor(inboxB));   //no record at all and a dead process arms nothing
    }

    [Fact]
    public void A_YOUNG_DEAD_SESSION_KEPS_EVERY_FILE_THE_SWEEP_RUNS()
    {
        State.Update(_sid, _ => Record());
        State.Sweep(DateTime.Now, _ => false);
        Assert.True(File.Exists(RecordPath));
    }
}
