using System.Text;
using System.Text.Json;
using Gatto.Cli.Inbox;

namespace Gatto.Tests.Inbox;

//the command as the other harness meets it: a fake shell, process view and transcript, the rows and exits of the table, and the wake
public sealed class InboxCommandTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-inbox-cmd-").FullName;
    private readonly string _proj = Directory.CreateTempSubdirectory("gatto-inbox-proj-").FullName;
    private readonly string _sid = Guid.NewGuid().ToString();
    private readonly string _other = Guid.NewGuid().ToString();
    private readonly StringWriter _out = new();
    private readonly StringWriter _err = new();
    private static readonly InboxState.Proc Claude = new(4242, 100);
    private static readonly InboxState.Proc Fresh = new(9000, 700);
    private const string Seat = "tail-5-5";

    public void Dispose()
    {
        foreach (var d in new[] { _home, _proj })
        {
            try { Directory.Delete(d, true); } catch (IOException) { }
        }
    }

    private InboxState State => new(_home);
    private string Transcript => Path.Combine(_proj, "t.jsonl");
    private string Inbox => ClaudeSeat.Inbox(_proj, Seat);
    private static string Rel => Path.Combine(".gatto", "mailbox", "claude", Seat);

    //the shell the harness sets: the session id, the pid, and the project folder as the variable brings it
    private Func<string, string?> Env(string? sid = null, int? pid = null, string? dir = null, string? attended = null) =>
        name => name switch
        {
            "CLAUDE_CODE_SESSION_ID" => sid,
            "CLAUDE_PID" => pid?.ToString(),
            "CLAUDE_PROJECT_DIR" => dir,
            "CLAUDE_CODE_SESSION_ATTENDED" => attended,
            _ => null,
        };

    //the hand a test dictates: a fake shell, fake processes, a tick fast enough to wait on, and the seam before the wake
    private InboxCommand.Io Io(
        Func<string, string?> env, TextReader? input = null,
        InboxState.Proc[]? live = null, Func<InboxState.Proc, bool>? alive = null,
        Action? beforeWake = null, bool? redirected = null, int tickMs = 100)
    {
        var procs = live ?? [Claude];
        InboxState.Proc? Of(int pid) =>
            procs.FirstOrDefault(p => p.Pid == pid) is { Pid: not 0 } p ? p : null;
        return new InboxCommand.Io(input ?? new StringReader(""), redirected ?? input is not null, _out, _err,
            env, _home, Of, alive ?? (p => Of(p.Pid)?.Equals(p) ?? false),
            () => DateTime.Now, TimeSpan.FromMilliseconds(tickMs), beforeWake);
    }

    //the session record a test starts from: created whole, as a session start would leave it
    private void Seed(string? sid = null, string? inbox = null, string? model = "claude-tail-5-5",
        string[]? known = null, InboxState.DeliveredEntry[]? delivered = null, InboxState.Proc claude = default)
    {
        var id = sid ?? _sid;
        State.Update(id, _ => new InboxState.Session(_proj, Transcript,
            claude == default ? Claude : claude, model, inbox, known ?? [], delivered ?? []));
    }

    //the hook's stdin as json, absent fields left out, backslashes in paths escaped as json wants
    private static string Stdin(string? sid = null, string hookEvent = "Stop", string? source = null,
        string? model = null, string? transcript = null)
    {
        var fields = new List<string>();
        void Add(string key, string? val)
        {
            if (val is not null)
                fields.Add("\"" + key + "\":\"" + val.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"");
        }
        Add("session_id", sid);
        Add("transcript_path", transcript);
        Add("hook_event_name", hookEvent);
        Add("source", source);
        Add("model", model);
        return "{" + string.Join(",", fields) + "}";
    }

    //one transcript line of a given top-level type holding the letter's notice as the harness writes it
    private static string TranscriptLine(string type, string letterPath) =>
        "{\"type\":\"" + type + "\",\"message\":{\"content\":" + Notice(letterPath) + "}}";

    private static string Notice(string letterPath) => JsonSerializer.Serialize("inbox: " + letterPath,
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    private string WriteLetter(string name, string body = "a body")
    {
        Directory.CreateDirectory(Inbox);
        var path = Path.Combine(Inbox, name);
        File.WriteAllText(path, body, new UTF8Encoding(false));
        return path;
    }

    //the hook's pid file appears when it took the lock, so a test writing the next letter waits for it first
    private async Task WaitWatching(string sid)
    {
        var pid = Path.Combine(_home, "claude", "claude-" + sid + ".pid");
        var until = DateTime.Now.AddSeconds(10);
        while (!File.Exists(pid) && DateTime.Now < until) await Task.Delay(20);
        Assert.True(File.Exists(pid), "the hook never reached the watch");
    }

    //a hook left watching is ended by retiring its arm, so no test leaves a watcher behind
    private async Task StopWatching(Task<int> hook)
    {
        foreach (var file in Directory.GetFiles(Path.Combine(_home, "claude"), "*.armed"))
            File.Delete(file);
        await hook.WaitAsync(TimeSpan.FromSeconds(10));
    }

    //the rows of the table: open, off, and the exits beside them

    [Fact]
    public async Task OPEN_ARMS_THE_SEAT_THE_NEWEST_ANSWER_NAMED()
    {
        Seed(inbox: Inbox, model: "claude-first-5-5");
        File.WriteAllText(Transcript, TranscriptLine("assistant", "anything") + "\n" +
            """{"type":"assistant","message":{"model":"claude-tail-5-5"}}""" + "\n");

        Assert.Equal(0, await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj))));
        Assert.Equal("tail-5-5", State.ArmFor(Inbox)?.Arm.Seat);
    }

    [Fact]
    public async Task OPEN_WITH_NO_ANSWER_YET_ARMS_THE_SEAT_THE_SESSION_START_NAMED()
    {
        Seed(inbox: Inbox);

        Assert.Equal(0, await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj))));
        Assert.Equal($"inbox open: {Rel}, empty; watching from the end of this turn", _out.ToString().TrimEnd());
    }

    [Fact]
    public async Task OPEN_WITH_LETTERS_ALREADY_THERE_COUNTS_THEM()
    {
        Seed(inbox: Inbox);
        WriteLetter("a.md");
        WriteLetter("b.md");

        Assert.Equal(0, await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj))));
        Assert.Equal(
            $"inbox open: {Rel}, 2 letters already there, not delivered; watching from the end of this turn",
            _out.ToString().TrimEnd());
        //known at the arm, so the watcher will not deliver them
        Assert.Equal(["a.md", "b.md"], State.ReadSession(_sid)!.Known);
    }

    [Fact]
    public async Task OPEN_IN_THE_SESSION_THAT_HOLDS_THE_ARM_CHANGES_NOTHING()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));

        Assert.Equal(0, await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj))));
        Assert.Equal($"inbox open: {Rel}, already open in this session", _out.ToString().TrimEnd());
    }

    [Fact]
    public async Task OPEN_SHOWS_NO_WATCHER_WHEN_THE_LOCK_IS_FREE_AND_A_FAILURE_CAME_AFTER_THE_ARM()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        State.LogFailure(_sid, "inbox hook: a planted failure");

        await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj)));

        var row = _out.ToString().TrimEnd();
        Assert.StartsWith($"inbox open: {Rel}, already open in this session; no watcher is running, see ", row,
            StringComparison.Ordinal);
        Assert.Contains(Path.Combine("claude", "claude-" + _sid + ".log"), row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OPEN_KEEPS_THE_WATCHER_ROW_WHEN_ONLY_ORDINARY_LINES_CAME_AFTER_THE_ARM()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        State.Log(_sid, "archive: a.md kept");

        await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj)));

        Assert.Equal($"inbox open: {Rel}, already open in this session", _out.ToString().TrimEnd());
    }

    [Fact]
    public async Task A_HELD_RECORD_LOCK_REFUSES_OPEN_WITH_ONE_ROW_AND_EXIT_ONE()
    {
        Seed(inbox: Inbox);
        Directory.CreateDirectory(Path.Combine(_home, "claude"));
        using var held = new FileStream(Path.Combine(_home, "claude", "claude-" + _sid + ".rec.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        Assert.Equal(1, await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj))));
        Assert.Equal("inbox not opened: the record of this session is busy", _out.ToString().TrimEnd());
    }

    [Fact]
    public async Task OPEN_KEEPS_THE_WATCHER_ROW_WHILE_A_WATCHER_HOLDS_THE_LOCK_AFTER_A_FAILURE()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        State.LogFailure(_sid, "inbox hook: a planted failure");
        using var held = State.TryLock(_sid);

        await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj)));

        Assert.Equal($"inbox open: {Rel}, already open in this session", _out.ToString().TrimEnd());
    }

    //a watcher that took the lock after a failure clears it, so a later open does not name a failure that watcher outlived
    [Fact]
    public async Task A_LATER_WATCHER_CLEARS_THE_FAILURE_THE_OPEN_ROW_WOULD_NAME()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        await Task.Delay(50);   //the stamp must come after the arm's write time, or the row would hide it for that reason alone
        State.LogFailure(_sid, "inbox hook: the record of this session is busy");
        WriteLetter("a.md");

        Assert.Equal(2, await InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript))))
            .WaitAsync(TimeSpan.FromSeconds(10)));
        await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj)));

        Assert.Equal($"inbox open: {Rel}, already open in this session", _out.ToString().TrimEnd());
    }

    [Fact]
    public async Task AN_ARM_THE_FILE_SYSTEM_WILL_NOT_DELETE_REFUSES_OFF_WITH_EXIT_ONE()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        var armFile = Directory.GetFiles(Path.Combine(_home, "claude"), "*.armed").Single();
        using (new FileStream(armFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.Equal(1, await InboxCommand.RunAsync("off", Io(Env(_sid, 4242, _proj))));

        Assert.StartsWith("inbox not closed: ", _out.ToString(), StringComparison.Ordinal);
        Assert.Equal(_sid, State.ArmFor(Inbox)?.Arm.SessionId);
    }

    //the usage errors through the dispatch, which runs before the launch sweeps
    [Fact]
    public async Task A_BARE_OR_UNKNOWN_INBOX_SUBCOMMAND_IS_A_USAGE_ERROR()
    {
        var ctx = new Gatto.Cli.CommandContext(new StringWriter(), _home, true, Theme: null);
        Assert.Equal(2, await Gatto.Cli.GattoApp.RunAsync(["inbox"], ctx));
        Assert.Equal(2, await Gatto.Cli.GattoApp.RunAsync(["inbox", "bogus"], ctx));
    }

    [Fact]
    public async Task A_BROKEN_SHELL_REFUSES_OFF_WITH_ONE_ROW_AND_EXIT_ONE()
    {
        //the arm cannot be reached when the environment itself fails, and the catch owns the row
        Assert.Equal(1, await InboxCommand.RunAsync("off", Io(_ => throw new IOException("no environment"))));
        Assert.Equal("inbox not closed: no environment", _out.ToString().TrimEnd());
    }

    [Fact]
    public async Task OPEN_FROM_A_SECOND_SESSION_MOVES_THE_INBOX_AND_NAMES_THE_LOSER()
    {
        Seed(inbox: Inbox);
        Seed(_other, inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _other, Claude));
        WriteLetter("a.md");

        Assert.Equal(0, await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj))));
        Assert.Equal(
            $"inbox moved: {Rel}, from session {_other[..8]} to this session; 1 letter already there, not delivered; watching from the end of this turn",
            _out.ToString().TrimEnd());
        Assert.Equal(_sid, State.ArmFor(Inbox)?.Arm.SessionId);
    }

    //an inbox moved with nothing in it says empty, as the open row does, never a count of zero
    [Fact]
    public async Task A_MOVE_OF_AN_EMPTY_INBOX_SAYS_EMPTY()
    {
        Seed(inbox: Inbox);
        Seed(_other, inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _other, Claude));

        Assert.Equal(0, await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj))));
        Assert.Equal(
            $"inbox moved: {Rel}, from session {_other[..8]} to this session; empty; watching from the end of this turn",
            _out.ToString().TrimEnd());
    }

    [Fact]
    public async Task OPEN_WITH_NO_RECORD_AND_WITH_NO_SEAT_SAY_SO_WITH_EXIT_TWO()
    {
        Assert.Equal(2, await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj))));
        Assert.Equal("inbox not opened: no record of this session; start a new Claude session in this folder",
            _out.ToString().TrimEnd());

        Seed(inbox: Inbox, model: null);
        Assert.Equal(2, await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj))));
        Assert.Contains("this model has no claude seat", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OPEN_AND_OFF_OUTSIDE_A_SESSION_SAY_SO_WITH_EXIT_TWO()
    {
        Assert.Equal(2, await InboxCommand.RunAsync("open", Io(Env(pid: 4242, dir: _proj))));
        Assert.Equal("inbox: not a claude session", _out.ToString().TrimEnd());
        Assert.Equal(2, await InboxCommand.RunAsync("off", Io(Env(sid: _sid))));
    }

    //an unattended session ends with its turn, so open refuses it and writes no arm
    [Fact]
    public async Task OPEN_IN_AN_UNATTENDED_SESSION_REFUSES_WITH_EXIT_TWO()
    {
        Seed();

        Assert.Equal(2, await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj, attended: "0"))));
        Assert.Equal("inbox not opened: this session runs unattended (claude -p) and ends with its turn", _out.ToString().TrimEnd());
        Assert.Null(State.ArmFor(Inbox));
        Assert.Null(State.ReadSession(_sid)!.Inbox);
    }

    [Fact]
    public async Task OFF_CLOSES_THE_ARM_OF_THE_HOLDING_SESSION()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));

        Assert.Equal(0, await InboxCommand.RunAsync("off", Io(Env(_sid, 4242, _proj))));
        Assert.Equal("inbox closed", _out.ToString().TrimEnd());
        Assert.Null(State.ArmFor(Inbox));
    }

    [Fact]
    public async Task OFF_NAMES_THE_OTHER_HOLDER_AND_CHANGES_NOTHING()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _other, Claude));

        Assert.Equal(0, await InboxCommand.RunAsync("off", Io(Env(_sid, 4242, _proj))));
        Assert.Equal("inbox not closed: another session holds it", _out.ToString().TrimEnd());
        Assert.NotNull(State.ArmFor(Inbox));
    }

    [Fact]
    public async Task OFF_WITHOUT_AN_ARM_SAYS_NO_INBOX_IS_OPEN()
    {
        Seed(inbox: Inbox);

        Assert.Equal(0, await InboxCommand.RunAsync("off", Io(Env(_sid, 4242, _proj))));
        Assert.Equal("no inbox open", _out.ToString().TrimEnd());
    }

    //a switch between two opens leaves one arm, and off closes it: the record follows the last open
    [Fact]
    public async Task TWO_OPENS_WITH_A_SWITCH_BETWEEN_LEAVE_ONE_ARM_AND_OFF_CLOSES_IT()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        //the newest answer names another model, so the second open arms another seat
        File.WriteAllText(Transcript,
            """{"type":"assistant","message":{"model":"claude-mid-5-5"}}""" + "\n");

        await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj)));

        Assert.Equal([State.ArmFor(ClaudeSeat.Inbox(_proj, "mid-5-5"))!.Value.Arm], State.ArmsNaming(_sid));
        Assert.Equal(0, await InboxCommand.RunAsync("off", Io(Env(_sid, 4242, _proj))));
        Assert.Empty(State.ArmsNaming(_sid));
    }

    //taking the record's own inbox back after a takeover keeps its delivered entries out of the known list
    [Fact]
    public async Task AN_OPEN_THAT_TAKES_BACK_ITS_OWN_INBOX_KEEPS_THE_DELIVERED_ENTRIES()
    {
        Seed(inbox: Inbox, delivered: [new InboxState.DeliveredEntry("a.md", _sid, Claude, Transcript)]);
        WriteLetter("a.md");
        WriteLetter("b.md");
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _other, Claude));

        await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj)));

        var record = State.ReadSession(_sid)!;
        Assert.Equal(["b.md"], record.Known);
        Assert.Single(record.Delivered);
    }

    //the hook: records, arms, the wake, the limits

    [Fact]
    public async Task A_TERMINAL_HOOK_READS_NOTHING_AND_LEAVES()
    {
        //a start the hook would act on, typed at a terminal rather than piped, is never read
        var typed = new StringReader(Stdin(_sid, "SessionStart", transcript: Transcript));
        Assert.Equal(0, await InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), typed, redirected: false)));
        Assert.Equal("", _out.ToString() + _err.ToString());
        Assert.False(Directory.Exists(Path.Combine(_home, "claude")));
    }

    //input that is not json is one failure line and a quiet exit
    [Fact]
    public async Task A_STDIN_THAT_IS_NOT_JSON_LOGS_A_FAILURE_AND_LEAVES_QUIETLY()
    {
        Assert.Equal(0, await InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader("not json"))));
        Assert.Equal("", _err.ToString());
        Assert.Contains("inbox hook: ", File.ReadAllText(Path.Combine(_home, "claude", "claude-unknown.log")), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_home, "claude", "claude-unknown.failed")));
    }

    //every hook run sweeps, so a session dead and idle for a week leaves nothing behind once any hook runs
    [Fact]
    public async Task A_HOOK_RUN_SWEEPS_A_DEAD_SESSION_IDLE_FOR_A_WEEK()
    {
        Seed(_other, claude: new InboxState.Proc(77, 77));
        foreach (var file in Directory.GetFiles(Path.Combine(_home, "claude"), "claude-*"))
            File.SetLastWriteTime(file, DateTime.Now.AddDays(-8));

        await InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript))));

        Assert.Null(State.ReadSession(_other));
    }

    [Fact]
    public async Task A_HOOK_RUN_THAT_CHANGES_NO_FIELD_STILL_MARKS_THE_RECORD_IN_USE()
    {
        Seed();
        var record = Path.Combine(_home, "claude", "claude-" + _sid + ".json");
        File.SetLastWriteTime(record, DateTime.Now.AddDays(-8));

        Assert.Equal(0, await InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)))));

        Assert.True(File.GetLastWriteTime(record) > DateTime.Now.AddMinutes(-1));
    }

    //a session in daily use changes no field, so each hook run marks its record young, and a resume a week on still finds its arm
    [Fact]
    public async Task A_RESUME_AFTER_A_WEEK_OF_AN_UNCHANGED_RECORD_KEEPS_THE_ARM()
    {
        var gone = new InboxState.Proc(111, 1);
        Seed(inbox: Inbox, claude: gone);
        Directory.CreateDirectory(Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, gone));
        foreach (var file in Directory.GetFiles(Path.Combine(_home, "claude"), "claude-*"))
            File.SetLastWriteTime(file, DateTime.Now.AddDays(-8));
        var io = Io(Env(_sid, 9000, _proj), new StringReader(Stdin(_sid, "SessionStart", source: "resume", transcript: Transcript)),
            live: [Fresh], alive: _ => false);

        Assert.Equal(0, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal(Fresh, State.ArmFor(Inbox)?.Arm.Claude);
        Assert.True(File.GetLastWriteTime(Path.Combine(_home, "claude", "claude-" + _sid + ".json")) > DateTime.Now.AddMinutes(-1));
    }

    [Fact]
    public async Task A_STDIN_WITH_NO_SESSION_ID_LOGS_ONE_LINE_AND_LEAVES_QUIETLY()
    {
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(hookEvent: "Stop")));

        Assert.Equal(0, await InboxCommand.RunAsync("hook", io));
        Assert.Equal("", _err.ToString());
        Assert.Contains("names no live session",
            File.ReadAllText(Path.Combine(_home, "claude", "claude-unknown.log")), StringComparison.Ordinal);
    }

    //a first start creates the record, a second keeps the inbox and the lists and sets the process
    [Fact]
    public async Task A_FIRST_START_CREATES_THE_RECORD_AND_A_SECOND_KEEPS_ITS_LISTS()
    {
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, "SessionStart", transcript: Transcript)));
        Assert.Equal(0, await InboxCommand.RunAsync("hook", io));
        var made = State.ReadSession(_sid);
        Assert.NotNull(made);
        Assert.Equal(_proj, made.ProjectFolder);
        Assert.Equal(Transcript, made.TranscriptPath);
        Assert.Equal(Claude, made.Claude);

        Seed(inbox: Inbox, known: ["a.md"]);
        //the second start names another folder, as a session started elsewhere would, and the record keeps the first
        var again = Io(Env(_sid, 4242, Path.Combine(_proj, "sub")),
            new StringReader(Stdin(_sid, "SessionStart", source: "resume", transcript: Transcript)));
        Assert.Equal(0, await InboxCommand.RunAsync("hook", again));
        var kept = State.ReadSession(_sid)!;
        Assert.Equal(Inbox, kept.Inbox);
        Assert.Equal(["a.md"], kept.Known);
        Assert.Equal(_proj, kept.ProjectFolder);
    }

    [Fact]
    public async Task A_PROJECT_DIR_WITH_FORWARD_SLASHES_LANDS_AS_A_FULL_PATH()
    {
        var io = Io(Env(_sid, 4242, _proj.Replace('\\', '/')),
            new StringReader(Stdin(_sid, "SessionStart", transcript: Transcript)));
        await InboxCommand.RunAsync("hook", io);
        Assert.Equal(_proj, State.ReadSession(_sid)!.ProjectFolder);
    }

    [Fact]
    public async Task A_START_WITHOUT_A_PROJECT_FOLDER_LOGS_AND_WRITES_NO_RECORD()
    {
        var io = Io(Env(_sid, 4242), new StringReader(Stdin(_sid, "SessionStart", transcript: Transcript)));
        Assert.Equal(0, await InboxCommand.RunAsync("hook", io));
        Assert.Null(State.ReadSession(_sid));
    }

    //a cleared session keeps its process and carries the arm and the lists to the new id
    [Fact]
    public async Task A_CLEAR_MOVES_THE_ARM_AND_THE_LISTS_TO_THE_NEW_ID()
    {
        var reader = new InboxState.Proc(5555, 5);   //the letter's reader lives on, so the settle keeps its entry
        Seed(_other, inbox: Inbox, known: ["a.md"], delivered: [new InboxState.DeliveredEntry("b.md", _other, reader, Transcript)]);
        WriteLetter("a.md");
        WriteLetter("b.md");
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _other, Claude));
        //the session's process is gone by the first tick, so this hook records its moves and leaves
        var io = Io(Env(_sid, 4242, _proj),
            new StringReader(Stdin(_sid, "SessionStart", source: "clear", transcript: Transcript)),
            alive: p => p == reader);

        Assert.Equal(0, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(_sid, State.ArmFor(Inbox)?.Arm.SessionId);
        var moved = State.ReadSession(_sid)!;
        Assert.Equal(["a.md"], moved.Known);
        Assert.Equal(Inbox, moved.Inbox);
        Assert.Equal(["b.md"], moved.Delivered.Select(d => d.Name).ToArray());
    }

    [Fact]
    public async Task A_REUSED_PID_DOES_NOT_MOVE_THE_ARM_ON_A_CLEAR()
    {
        Seed(_other, inbox: Inbox);
        //the arm names the same pid with other start ticks, so it belongs to another process
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _other, new InboxState.Proc(4242, 55)));
        var io = Io(Env(_sid, 4242, _proj),
            new StringReader(Stdin(_sid, "SessionStart", source: "clear", transcript: Transcript)),
            alive: _ => false);

        await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(_other, State.ArmFor(Inbox)?.Arm.SessionId);
    }

    //a resume picked inside a running session keeps the process under another id, so the arm and the lists follow the process as a clear does
    [Fact]
    public async Task A_RESUME_INSIDE_THE_RUNNING_PROCESS_MOVES_THE_ARM_TO_THE_RESUMED_ID()
    {
        Seed(_other, inbox: Inbox, known: ["a.md"]);
        WriteLetter("a.md");
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _other, Claude));
        var io = Io(Env(_sid, 4242, _proj),
            new StringReader(Stdin(_sid, "SessionStart", source: "resume", model: "claude-tail-5-5", transcript: Transcript)),
            alive: _ => false);

        Assert.Equal(0, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(_sid, State.ArmFor(Inbox)?.Arm.SessionId);
        Assert.Equal(Inbox, State.ReadSession(_sid)!.Inbox);
        Assert.Equal(["a.md"], State.ReadSession(_sid)!.Known);
    }

    //a clear names no model and its new transcript holds no answer, so open after a clear arms the seat the process last answered with
    [Fact]
    public async Task OPEN_RIGHT_AFTER_A_CLEAR_ARMS_THE_SEAT_THE_LEFT_SESSION_LAST_ANSWERED_WITH()
    {
        Seed(_other, model: "claude-first-5-5");
        File.WriteAllText(Transcript, """{"type":"assistant","message":{"model":"claude-tail-5-5"}}""" + "\n");
        var fresh = Path.Combine(_proj, "after-clear.jsonl");
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, "SessionStart", source: "clear", transcript: fresh)));

        Assert.Equal(0, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("claude-tail-5-5", State.ReadSession(_sid)!.Model);

        Assert.Equal(0, await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj))));
        Assert.Equal($"inbox open: {Rel}, empty; watching from the end of this turn", _out.ToString().TrimEnd());
    }

    //a resume keeps the id and writes its new process into the arm and the record
    [Fact]
    public async Task A_RESUME_WRITES_ITS_NEW_PROCESS_INTO_THE_ARM_AND_THE_RECORD()
    {
        Seed(inbox: Inbox, claude: new InboxState.Proc(111, 1));
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, new InboxState.Proc(111, 1)));
        var io = Io(Env(_sid, 9000, _proj),
            new StringReader(Stdin(_sid, "SessionStart", source: "resume", transcript: Transcript)),
            live: [Claude, Fresh], alive: _ => false);

        Assert.Equal(0, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(Fresh, State.ArmFor(Inbox)?.Arm.Claude);
        Assert.Equal(Fresh, State.ReadSession(_sid)!.Claude);
    }

    //an unattended resume of an armed session leaves the arm and the record as they were and returns without a watcher
    [Fact]
    public async Task AN_UNATTENDED_RESUME_TAKES_NO_ARM_AND_WATCHES_NOTHING()
    {
        var old = new InboxState.Proc(111, 1);
        Seed(inbox: Inbox, claude: old);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, old));
        WriteLetter("a.md");
        var io = Io(Env(_sid, 9000, _proj, attended: "0"),
            new StringReader(Stdin(_sid, "SessionStart", source: "resume", transcript: Transcript)),
            live: [Claude, Fresh, old]);

        Assert.Equal(0, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(old, State.ArmFor(Inbox)?.Arm.Claude);
        Assert.Equal(old, State.ReadSession(_sid)!.Claude);
        Assert.False(File.Exists(Path.Combine(_home, "claude", "claude-" + _sid + ".pid")));
        Assert.Equal("", _err.ToString());
    }

    //the end of an unattended turn in the arm's own session and process wakes nothing and returns at once
    [Fact]
    public async Task AN_UNATTENDED_STOP_IN_THE_ARMED_SESSION_RETURNS_WITHOUT_A_WAKE()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        WriteLetter("a.md");
        var io = Io(Env(_sid, 4242, _proj, attended: "0"), new StringReader(Stdin(_sid, transcript: Transcript)));

        Assert.Equal(0, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("", _err.ToString());
        Assert.Empty(State.ReadSession(_sid)!.Delivered);
        Assert.NotNull(State.ArmFor(Inbox));
    }

    //an unarmed session watches nothing, a lost arm ends the watcher, and a loss after the batch stops the wake
    [Fact]
    public async Task A_SESSION_WITHOUT_AN_ARM_EXITS_AT_ONCE()
    {
        Seed(inbox: Inbox);
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));

        Assert.Equal(0, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal("", _err.ToString());
    }

    [Fact]
    public async Task A_WATCHER_WHOSE_ARM_IS_LOST_ENDS_WITHIN_A_TICK()
    {
        Seed(inbox: Inbox);
        Directory.CreateDirectory(Inbox);   //open creates the folder, and these tests seed records without it
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));
        var hook = InboxCommand.RunAsync("hook", io);
        await WaitWatching(_sid);

        State.DeleteArm(State.ArmFor(Inbox)!.Value.Arm);

        Assert.Equal(0, await hook.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("", _err.ToString());
    }

    //the arm goes between the delivered write and the wake, and stderr stays empty
    [Fact]
    public async Task A_WATCHER_THAT_LOSES_THE_ARM_AFTER_THE_BATCH_WRITES_NOTHING()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        WriteLetter("a.md");
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)),
            beforeWake: () => State.DeleteArm(State.ArmFor(Inbox)!.Value.Arm));

        Assert.Equal(0, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal("", _err.ToString());
        Assert.True(File.Exists(Path.Combine(Inbox, "a.md")));
        //the wake never went out, so no entry holds the letter back and an open of the same session counts it
        Assert.Empty(State.ReadSession(_sid)!.Delivered);
        await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj)));
        Assert.Contains("1 letter already there, not delivered", _out.ToString(), StringComparison.Ordinal);
    }

    //an open that moves the session's arm while its watcher idles ends that watcher, and the next turn's hook waits out its tick and watches the new inbox
    [Fact]
    public async Task AN_OPEN_ON_ANOTHER_SEAT_HANDS_THE_WATCH_TO_THE_NEXT_TURNS_HOOK()
    {
        Seed(inbox: Inbox);
        Directory.CreateDirectory(Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        var first = InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)), tickMs: 1000));
        await WaitWatching(_sid);
        await Task.Delay(100);   //the open's arm must come after the watcher's pid file

        File.WriteAllText(Transcript, """{"type":"assistant","message":{"model":"claude-mid-5-5"}}""" + "\n");
        Assert.Equal(0, await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj))));
        var second = InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)), tickMs: 1000));

        Assert.Equal(0, await first.WaitAsync(TimeSpan.FromSeconds(10)));
        await WaitWatching(_sid);
        File.WriteAllText(Path.Combine(ClaudeSeat.Inbox(_proj, "mid-5-5"), "a.md"), "for the new seat", new UTF8Encoding(false));
        Assert.Equal(2, await second.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("for the new seat", _err.ToString(), StringComparison.Ordinal);
    }

    //an open that writes the arm again in the same session ends the old watcher too, so the letters that open counted stay undelivered
    [Fact]
    public async Task AN_OPEN_AFTER_AN_OFF_ENDS_THE_OLD_WATCHER()
    {
        Seed(inbox: Inbox);
        Directory.CreateDirectory(Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        var first = InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)), tickMs: 1000));
        await WaitWatching(_sid);
        await Task.Delay(100);

        Assert.Equal(0, await InboxCommand.RunAsync("off", Io(Env(_sid, 4242, _proj))));
        Assert.Equal(0, await InboxCommand.RunAsync("open", Io(Env(_sid, 4242, _proj))));

        Assert.Equal(0, await first.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    //a throw on the watch's thread ends the hook with a failure line, so no hook sits on the lock for good
    [Fact]
    public async Task A_THROW_IN_THE_WATCHERS_CHECK_ENDS_THE_HOOK_WITH_A_FAILURE_LINE()
    {
        Seed(inbox: Inbox);
        Directory.CreateDirectory(Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        var calls = 0;
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)),
            alive: _ => Interlocked.Increment(ref calls) > 1 ? throw new IOException("a planted probe failure") : true);

        Assert.Equal(0, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("a planted probe failure", File.ReadAllText(State.LogPath(_sid)), StringComparison.Ordinal);
        Assert.True(State.LockFree(_sid));
    }

    //an arm file that parses with no fields reads as absent, so a watcher beside it ends when its own arm goes
    [Fact]
    public async Task AN_ARM_FILE_WITH_NO_FIELDS_LEAVES_THE_WATCHER_TO_ITS_OWN_ARM()
    {
        Seed(inbox: Inbox);
        Directory.CreateDirectory(Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        var hook = InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript))));
        await WaitWatching(_sid);

        File.WriteAllText(Path.Combine(_home, "claude", "claude-aaa-000000000000.armed"), "{}");
        await Task.Delay(300);
        State.DeleteArm(State.ArmFor(Inbox)!.Value.Arm);

        Assert.Equal(0, await hook.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.True(State.LockFree(_sid));
    }

    //a folder removed under the arm is made again by the next hook, which watches it
    [Fact]
    public async Task A_REMOVED_INBOX_FOLDER_IS_MADE_AGAIN_AND_WATCHED()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        var hook = InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript))));
        var ignore = Path.Combine(Inbox, ".gitignore");
        var until = DateTime.Now.AddSeconds(10);
        while (!File.Exists(ignore) && DateTime.Now < until) await Task.Delay(20);

        Assert.True(File.Exists(ignore), "the hook never made the folder");
        WriteLetter("a.md", "into the made folder");
        Assert.Equal(2, await hook.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("into the made folder", _err.ToString(), StringComparison.Ordinal);
    }

    //the process behind the pid is the session itself, and its end ends the watcher
    [Fact]
    public async Task A_WATCHER_WHOSE_SESSION_ENDED_ENDS_WITHIN_A_TICK()
    {
        Seed(inbox: Inbox);
        Directory.CreateDirectory(Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)),
            alive: _ => false);

        Assert.Equal(0, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    //through the hook's own sink, a writer holding a letter is waited for
    [Fact]
    public async Task A_LETTER_LOCKED_BY_ITS_WRITER_IS_DELIVERED_ON_RELEASE()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        Directory.CreateDirectory(Inbox);
        var path = Path.Combine(Inbox, "a.md");
        var locked = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));
        var hook = InboxCommand.RunAsync("hook", io);
        await Task.Delay(300);

        using (locked) locked.Write(Encoding.UTF8.GetBytes("a body"));

        Assert.Equal(2, await hook.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("a body", _err.ToString(), StringComparison.Ordinal);
    }

    //known at the arm, delivered once, and a letter that lands later wakes
    [Fact]
    public async Task A_KNOWN_LETTER_IS_NOT_DELIVERED_AND_A_NEW_ONE_WAKES()
    {
        Seed(inbox: Inbox, known: ["a.md"]);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        WriteLetter("a.md", "the known one");
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));
        var hook = InboxCommand.RunAsync("hook", io);
        await WaitWatching(_sid);

        WriteLetter("b.md", "the new one");

        Assert.Equal(2, await hook.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("the new one", _err.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("the known one", _err.ToString(), StringComparison.Ordinal);
        //delivered once: a second watcher of the same session lets the letter wait in the inbox
        var again = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));
        var second = InboxCommand.RunAsync("hook", again);
        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(TimeSpan.FromMilliseconds(500)));
        await StopWatching(second);
    }

    //a delivered name whose letter left the inbox drops its entry and a new letter under it is new
    [Fact]
    public async Task A_NAME_IS_FREED_WHEN_THE_DELIVERED_LETTER_LEFT_THE_INBOX()
    {
        Seed(inbox: Inbox, delivered: [new InboxState.DeliveredEntry("a.md", _sid, Claude, Transcript)]);

        //no file and no arm: the hook settles the entry and leaves
        Assert.Equal(0, await InboxCommand.RunAsync("hook",
            Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)))));
        Assert.Empty(State.ReadSession(_sid)!.Delivered);

        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        WriteLetter("a.md", "written under the freed name");
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));
        Assert.Equal(2, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("written under the freed name", _err.ToString(), StringComparison.Ordinal);
    }

    //a known name whose file left the inbox is free, and a new letter under it wakes
    [Fact]
    public async Task A_KNOWN_NAME_IS_FREED_WHEN_ITS_FILE_LEFT_THE_INBOX()
    {
        Seed(inbox: Inbox, known: ["a.md"]);
        Directory.CreateDirectory(Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        var hook = InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript))));
        var until = DateTime.Now.AddSeconds(10);
        while (State.ReadSession(_sid)!.Known.Length > 0 && DateTime.Now < until) await Task.Delay(20);
        Assert.Empty(State.ReadSession(_sid)!.Known);   //the prune ran, so the name is free

        WriteLetter("a.md", "new under a freed known name");

        Assert.Equal(2, await hook.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("new under a freed known name", _err.ToString(), StringComparison.Ordinal);
    }

    //one wake carries six, the next carries the rest
    [Fact]
    public async Task ONE_WAKE_CARRIES_SIX_AND_THE_NEXT_CARRIES_THE_REST()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        for (var n = 1; n <= 8; n++) WriteLetter(n + ".md", "body " + n);
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));

        Assert.Equal(2, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(6, _err.ToString().Split("inbox: ").Length - 1);
        Assert.Equal(6, State.ReadSession(_sid)!.Delivered.Length);
        _err.GetStringBuilder().Clear();   //the writers serve one flow at a time, so the second wake counts on its own

        var next = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));
        Assert.Equal(2, await InboxCommand.RunAsync("hook", next).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(2, _err.ToString().Split("inbox: ").Length - 1);
        Assert.Equal(8, State.ReadSession(_sid)!.Delivered.Length);
    }

    //one watcher per session, found through the lock
    [Fact]
    public async Task A_SECOND_WATCHER_LEAVES_WHILE_THE_FIRST_HOLDS_THE_LOCK()
    {
        Seed(inbox: Inbox);
        Directory.CreateDirectory(Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));
        var hook = InboxCommand.RunAsync("hook", io);
        await WaitWatching(_sid);

        var second = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));
        Assert.Equal(0, await InboxCommand.RunAsync("hook", second).WaitAsync(TimeSpan.FromSeconds(5)));

        await StopWatching(hook);
    }

    //the shape of the wake, in name order, and out stays empty
    [Fact]
    public async Task THE_WAKE_IS_THE_HEAD_A_BLANK_LINE_AND_THE_BODIES_IN_NAME_ORDER()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        var a = WriteLetter("a.md", "first body");
        WriteLetter("b.md", "second body");
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));

        Assert.Equal(2, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal($"inbox: {a}\n\nfirst body\n\ninbox: {Path.Combine(Inbox, "b.md")}\n\nsecond body",
            _err.ToString());
        Assert.Equal("", _out.ToString());
    }

    //one oversize letter goes as its path, the bodies beside it stay
    [Fact]
    public async Task A_BODY_PAST_THE_PER_LETTER_BOUND_GOES_AS_ITS_PATH_AND_A_SENTENCE()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        var big = WriteLetter("big.md", new string('x', 5000));
        WriteLetter("one.md", new string('y', 2000));
        WriteLetter("two.md", new string('y', 2000));
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));

        Assert.Equal(2, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        var said = _err.ToString();
        Assert.Contains("inbox: " + big, said, StringComparison.Ordinal);
        Assert.Contains("the body was not included; read the file at that path and act on what it says",
            said, StringComparison.Ordinal);
        Assert.DoesNotContain(new string('x', 100), said, StringComparison.Ordinal);
        Assert.Contains(new string('y', 100), said, StringComparison.Ordinal);
        Assert.DoesNotContain("more than a tool result", said, StringComparison.Ordinal);
    }

    //together the bodies pass the wake bound, so every one of them goes as its path
    [Fact]
    public async Task BODIES_PAST_THE_WAKE_BOUND_ALL_GO_AS_THEIR_PATHS()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        for (var n = 1; n <= 5; n++) WriteLetter(n + ".md", new string('z', 2000));
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));

        Assert.Equal(2, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        var said = _err.ToString();
        Assert.Equal(5, said.Split("inbox: ").Length - 1);
        Assert.DoesNotContain("zzz", said, StringComparison.Ordinal);
    }

    //both bounds are passed only above them: a body of exactly the per-letter bound and bodies summing exactly the wake bound all go whole
    [Fact]
    public async Task BODIES_AT_EXACTLY_THE_BOUNDS_GO_WHOLE()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        WriteLetter("1.md", new string('p', 4000));
        WriteLetter("2.md", new string('q', 4000));
        WriteLetter("3.md", new string('r', 1000));
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));

        Assert.Equal(2, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        var said = _err.ToString();
        Assert.Contains(new string('p', 4000), said, StringComparison.Ordinal);
        Assert.Contains(new string('r', 1000), said, StringComparison.Ordinal);
        Assert.DoesNotContain("the body was not included", said, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_LETTER_WITH_NON_ASCII_REACHES_THE_WAKE_INTACT_AND_OUT_STAYS_EMPTY()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        WriteLetter("a.md", "ünicode → body");
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));

        Assert.Equal(2, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("ünicode → body", _err.ToString(), StringComparison.Ordinal);
        Assert.Equal("", _out.ToString());
    }

    //the end of the turn archives what the transcript proves was received
    [Fact]
    public async Task A_RECEIVED_LETTER_MOVES_TO_THE_MIRROR_WITH_ITS_READ_FILE_AT_THE_NEXT_STOP()
    {
        var path = WriteLetter("a.md");
        Seed(inbox: Inbox, delivered: [new InboxState.DeliveredEntry("a.md", _sid, Claude, Transcript)]);
        File.WriteAllText(Transcript, TranscriptLine("queue-operation", path) + "\n" +
            TranscriptLine("user", path) + "\n");

        var exit = await InboxCommand.RunAsync("hook",
            Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript))));

        Assert.Equal(0, exit);
        var kept = Path.Combine(_proj, ".gatto", "mailbox", ".archive", "claude", Seat, "a.md");
        Assert.True(File.Exists(kept));
        Assert.False(File.Exists(path));
        var read = File.ReadAllText(kept + ".read");
        Assert.StartsWith($"read-by: claude/{Seat}/{_sid[..8]}\n", read, StringComparison.Ordinal);
        Assert.Contains("session: " + _sid, read, StringComparison.Ordinal);
        Assert.Contains("read: ", read, StringComparison.Ordinal);
        Assert.Empty(State.ReadSession(_sid)!.Delivered);
    }

    [Fact]
    public async Task A_TAKEN_ARCHIVE_NAME_IS_KEPT_AS_THE_NEXT_FREE_NUMBER()
    {
        var path = WriteLetter("a.md");
        Seed(inbox: Inbox, delivered: [new InboxState.DeliveredEntry("a.md", _sid, Claude, Transcript)]);
        var mirror = Path.Combine(_proj, ".gatto", "mailbox", ".archive", "claude", Seat);
        Directory.CreateDirectory(mirror);
        File.WriteAllText(Path.Combine(mirror, "a.md"), "an older letter");
        File.WriteAllText(Transcript, TranscriptLine("user", path) + "\n");

        await InboxCommand.RunAsync("hook",
            Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript))));

        Assert.True(File.Exists(Path.Combine(mirror, "a-2.md")));
        Assert.Contains("a-2.md", File.ReadAllText(Path.Combine(_home, "claude", "claude-" + _sid + ".log")),
            StringComparison.Ordinal);
    }

    //a letter carried over a clear is read by the session the entry names, and the seat is the folder's after a model switch
    [Fact]
    public async Task THE_READ_FILE_NAMES_THE_ENTRYS_SESSION_AND_THE_FOLDERS_SEAT()
    {
        var path = WriteLetter("a.md");
        Seed(inbox: Inbox, model: "claude-mid-5-5", delivered: [new InboxState.DeliveredEntry("a.md", _other, Claude, Transcript)]);
        File.WriteAllText(Transcript, TranscriptLine("user", path) + "\n");

        await InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript))));

        var read = File.ReadAllText(Path.Combine(_proj, ".gatto", "mailbox", ".archive", "claude", Seat, "a.md.read"));
        Assert.StartsWith($"read-by: claude/{Seat}/{_other[..8]}\nsession: {_other}\n", read, StringComparison.Ordinal);
    }

    //a queue line is no proof, a gone process frees the letter for the next watcher
    [Fact]
    public async Task A_QUEUED_LINE_DOES_NOT_ARCHIVE_AND_A_LIVE_PROCESS_KEPTS_THE_LETTER()
    {
        var path = WriteLetter("a.md");
        Seed(inbox: Inbox, delivered: [new InboxState.DeliveredEntry("a.md", _sid, Claude, Transcript)]);
        File.WriteAllText(Transcript, TranscriptLine("queue-operation", path) + "\n");

        await InboxCommand.RunAsync("hook",
            Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript))));

        Assert.True(File.Exists(path));
        Assert.Single(State.ReadSession(_sid)!.Delivered);
    }

    //a reused name: the notice of an earlier letter under the same path, written before the handover, does not archive the new one
    [Fact]
    public async Task AN_EARLIER_NOTICE_UNDER_THE_SAME_NAME_DOES_NOT_ARCHIVE_THE_NEW_LETTER()
    {
        var path = WriteLetter("a.md");
        File.WriteAllText(Transcript, TranscriptLine("user", path) + "\n");
        Seed(inbox: Inbox, delivered: [new InboxState.DeliveredEntry("a.md", _sid, Claude, Transcript, ClaudeSeat.Length(Transcript))]);

        await InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript))));
        Assert.True(File.Exists(path));
        Assert.Single(State.ReadSession(_sid)!.Delivered);

        File.AppendAllText(Transcript, TranscriptLine("user", path) + "\n");
        await InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript))));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_LETTER_HANDED_TO_A_GONE_PROCESS_IS_DELIVERED_AGAIN()
    {
        WriteLetter("a.md");
        Seed(inbox: Inbox,
            delivered: [new InboxState.DeliveredEntry("a.md", _sid, new InboxState.Proc(77, 77), Transcript)]);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        File.WriteAllText(Transcript, "{}\n");

        var exit = await InboxCommand.RunAsync("hook",
            Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript))))
            .WaitAsync(TimeSpan.FromSeconds(10));

        //the entry left the delivered list in the settle and the wake handed the letter over again
        Assert.Equal(2, exit);
        Assert.Contains("a body", _err.ToString(), StringComparison.Ordinal);
    }

    //a letter landing after the pass closed is refused by the sink and left for the next watcher
    [Fact]
    public async Task A_LETTER_WRITTEN_BETWEEN_THE_BATCH_AND_THE_WAKE_WAITS_FOR_THE_NEXT_WATCHER()
    {
        Seed(inbox: Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        WriteLetter("a.md");
        var io = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)),
            beforeWake: () => WriteLetter("b.md", "the late one"));

        Assert.Equal(2, await InboxCommand.RunAsync("hook", io).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain("b.md", _err.ToString(), StringComparison.Ordinal);
        Assert.Equal(["a.md"], State.ReadSession(_sid)!.Delivered.Select(d => d.Name).ToArray());

        var next = Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript)));
        Assert.Equal(2, await InboxCommand.RunAsync("hook", next).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("the late one", _err.ToString(), StringComparison.Ordinal);
    }

    //the record is changed under its gate, so a change made while the hook watched survives its delivered write
    [Fact]
    public async Task A_RECORD_CHANGE_DURING_THE_WATCH_SURVIVES_THE_DELIVERED_WRITE()
    {
        Seed(inbox: Inbox);
        Directory.CreateDirectory(Inbox);
        State.WriteArm(new InboxState.Arm(Seat, Inbox, _sid, Claude));
        var hook = InboxCommand.RunAsync("hook", Io(Env(_sid, 4242, _proj), new StringReader(Stdin(_sid, transcript: Transcript))));
        await WaitWatching(_sid);
        await Task.Delay(200);   //past the hook's own record writes, so the change below comes while it watches

        State.Update(_sid, r => r is null ? null : r with { Model = "claude-mid-5-5" });
        WriteLetter("a.md");

        Assert.Equal(2, await hook.WaitAsync(TimeSpan.FromSeconds(10)));
        var record = State.ReadSession(_sid)!;
        Assert.Equal("claude-mid-5-5", record.Model);
        Assert.Single(record.Delivered);
    }
}
