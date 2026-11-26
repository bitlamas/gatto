using System.Text;
using System.Text.Json.Nodes;
using Gatto.Repl;

namespace Gatto.Cli.Inbox;

//the command the other harness runs: hook is the watcher, open arms the seat's inbox, off closes it, and none of them starts a repl or touches a server
internal static class InboxCommand
{
    //the per-letter and per-wake body bounds, chosen to keep one wake small, not limits the harness sets
    private const int PerLetterChars = 4000;
    private const int WakeChars = 9000;

    //the hand the command takes: readers, writers, environment, home, the process view and clock, the idle tick, and a seam run between the delivered write and the last arm check
    internal sealed record Io(
        TextReader In, bool InRedirected, TextWriter Out, TextWriter Err,
        Func<string, string?> Env, string Home,
        Func<int, InboxState.Proc?> ProcOf, Func<InboxState.Proc, bool> Alive,
        Func<DateTime> Now, TimeSpan Tick, Action? BeforeWake)
    {
        //the state folder stands under the home, so every run of one install reads one claude folder
        internal InboxState State => new(Home);

        //the real console, environment, process view and clock, a five second idle tick and no seam before the wake. a redirected stdout gets utf-8, since this path leaves before the launch pins the console's encoding
        public static Io Production(string home) => new(
            new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false)),
            Console.IsInputRedirected,
            Console.IsOutputRedirected
                ? new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true }
                : Console.Out,
            new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true },
            Environment.GetEnvironmentVariable,
            home,
            InboxState.Proc.Of,
            p => InboxState.Proc.Of(p.Pid)?.Equals(p) ?? false,
            () => DateTime.Now,
            TimeSpan.FromSeconds(5),
            null);
    }

    //the subcommand picks the flow, and only the hook waits: open and off print one line and are done. the model is the one open was told, null for the transcript's rule
    public static async Task<int> RunAsync(string sub, Io io, string? model = null) => sub switch
    {
        "hook" => await Hook(io),
        "open" => Open(io, model),
        _ => Off(io),
    };

    //the session the harness's shell names: an id of eight characters at least and a live process, null when this shell is not one
    private static (string Id, InboxState.Proc Claude)? Session(Io io)
    {
        if (io.Env("CLAUDE_CODE_SESSION_ID") is not { Length: >= 8 } id) return null;
        if (io.Env("CLAUDE_PID") is not { } raw || !int.TryParse(raw, out var pid) || io.ProcOf(pid) is not { } claude) return null;
        return (id, claude);
    }

    //claude sets this to 0 in a session no one attends, as under -p, in the hook and in the tool shell alike. an absent value counts as attended
    private static bool Unattended(Io io) => io.Env("CLAUDE_CODE_SESSION_ATTENDED") == "0";

    //one line to the caller's writer, returned as the exit code so every row reads as the flow's end
    private static int Row(Io io, string line, int exit)
    {
        io.Out.WriteLine(line);
        return exit;
    }

    //arms the seat's inbox for the session this shell runs in: one row of the table, the arm written last so the last open wins
    private static int Open(Io io, string? told)
    {
        var state = io.State;
        try
        {
            if (Session(io) is not { } session) return Row(io, "inbox: not a claude session", 2);
            if (Unattended(io)) return Row(io, "inbox not opened: this session runs unattended (claude -p) and ends with its turn", 2);
            var record = state.ReadSession(session.Id);
            if (record is null)
                return Row(io, "inbox not opened: no record of this session; start a new Claude session in this folder", 2);
            //a told model wins, the transcript is a turn behind on the first turn after a start or a switch
            var model = told ?? ClaudeSeat.NewestModel(record.TranscriptPath) ?? record.Model;
            if (ClaudeSeat.FromModelId(model) is not { } seat)
                return Row(io, "inbox not opened: this model has no claude seat", 2);
            var inbox = ClaudeSeat.Inbox(record.ProjectFolder, seat);
            Watch.CreateFolder(inbox);
            var shown = ClaudeSeat.Shown(inbox, record.ProjectFolder);
            var watching = "; watching from the end of this turn";
            var held = state.ArmFor(inbox);
            //a session that holds the arm changes nothing, and the watcher clause is a measurement: the lock free and a failure newer than the arm
            if (held is { } arm && arm.Arm.SessionId == session.Id && record.Inbox is { } own && InboxState.SameInbox(own, inbox))
            {
                var dead = state.LockFree(session.Id) && state.FailedAfter(session.Id, arm.Written);
                return Row(io, dead
                    ? $"inbox open: {shown}, already open in this session; no watcher is running, see {state.LogPath(session.Id)}"
                    : $"inbox open: {shown}, already open in this session", 0);
            }
            var prev = held?.Arm;
            var moved = prev is not null && prev.SessionId != session.Id;
            var waiting = Waiting(Take(state, session.Id, session.Claude, seat, inbox)) ?? "empty";
            if (moved)
                return Row(io, $"inbox moved: {shown}, from session {prev!.SessionId[..8]} to this session; {waiting}{watching}", 0);
            return Row(io, $"inbox open: {shown}, {waiting}{watching}", 0);
        }
        catch (Exception ex) { return Row(io, "inbox not opened: " + ex.Message, 1); }
    }

    //arms an inbox for a session, run by open and by a stop that follows the model. returns the names held back as known
    private static string[] Take(InboxState state, string sessionId, InboxState.Proc claude, string seat, string inbox)
    {
        var letters = ClaudeSeat.Letters(inbox);
        //a session holds one arm, so its arms to other inboxes go before this one is written
        foreach (var other in state.ArmsNaming(sessionId))
            if (!InboxState.SameInbox(other.Inbox, inbox)) state.DeleteArm(other);
        var known = letters.ToArray();
        state.Update(sessionId, r =>
        {
            if (r is null) return null;
            //taking the record's own inbox back keeps its delivered entries, and their names stay out of the known list
            if (r.Inbox is { } old && InboxState.SameInbox(old, inbox))
            {
                known = [.. letters.Where(n => !r.Delivered.Any(d => d.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))];
                return r with { Known = known };
            }
            return r with { Inbox = inbox, Known = known, Delivered = [] };
        });
        state.WriteArm(new InboxState.Arm(seat, inbox, sessionId, claude));
        return known;
    }

    //the clause naming the letters an arm holds back, null when none wait. open and the move both write it, so their rows cannot drift
    private static string? Waiting(string[] known) =>
        known.Length > 0 ? $"{Gatto.Core.Plural.Of(known.Length, "letter")} already there, not delivered" : null;

    //a turn ran on a model of another seat: a free inbox, or one a gone process holds, takes the arm along, and one a live session holds closes it. null when the model gives no other seat
    private static string? Follow(Io io, InboxState state, InboxState.Session record, string sessionId, InboxState.Proc claude,
        string transcript, InboxState.Arm held)
    {
        if (ClaudeSeat.NewestModel(transcript) is not { } model || ClaudeSeat.FromModelId(model) is not { } seat) return null;
        var inbox = ClaudeSeat.Inbox(record.ProjectFolder, seat);
        if (InboxState.SameInbox(inbox, held.Inbox)) return null;
        var shown = ClaudeSeat.Shown(inbox, record.ProjectFolder);
        if (state.ArmFor(inbox)?.Arm is { } other && other.SessionId != sessionId && io.Alive(other.Claude))
        {
            state.DeleteArm(held);
            return $"inbox closed: this session now runs {model}, and {shown} is open in session {other.SessionId[..8]}; /inbox takes it";
        }
        Watch.CreateFolder(inbox);
        var waiting = Waiting(Take(state, sessionId, claude, seat, inbox));
        return $"inbox moved: {shown}; this session now runs {model}" + (waiting is null ? "" : "; " + waiting);
    }

    //closes the session's inbox: only the arm's holder closes it, and every other case prints its row and changes nothing
    private static int Off(Io io)
    {
        var state = io.State;
        try
        {
            if (Session(io) is not { } session) return Row(io, "inbox: not a claude session", 2);
            var record = state.ReadSession(session.Id);
            var arm = record?.Inbox is { } inbox ? state.ArmFor(inbox)?.Arm : null;
            if (arm is null) return Row(io, "no inbox open", 0);
            if (arm.SessionId != session.Id) return Row(io, "inbox not closed: another session holds it", 0);
            state.DeleteArm(arm);
            return Row(io, "inbox closed", 0);
        }
        catch (Exception ex) { return Row(io, "inbox not closed: " + ex.Message, 1); }
    }

    //the watcher the harness's hooks run: it keeps the session's record current, settles delivered letters, then hands new ones over as a wake
    private static async Task<int> Hook(Io io)
    {
        //typed at a terminal there is no json to read, so this leaves at once
        if (!io.InRedirected) return 0;
        var state = io.State;
        string? sessionId = null;
        FileStream? gate = null;
        Watch? watch = null;
        var pidWritten = false;
        try
        {
            var input = io.In.ReadToEnd();
            //an unattended session ends with its turn and nothing could wake it, so a resume there takes no arm and no watcher holds it open
            if (Unattended(io)) return 0;
            var stdin = JsonNode.Parse(input);
            sessionId = Text(stdin, "session_id");
            var transcript = Text(stdin, "transcript_path");
            var hookEvent = Text(stdin, "hook_event_name");
            var source = Text(stdin, "source");
            var startModel = Text(stdin, "model");
            var claude = io.Env("CLAUDE_PID") is { } raw && int.TryParse(raw, out var pid) ? io.ProcOf(pid) : null;
            //a wake for a broken stdin would say something that is not mail, so every unusable input is one log line and a quiet exit
            if (sessionId is not { Length: >= 8 } || transcript is null || hookEvent is null || claude is null)
            {
                state.LogFailure(sessionId, "inbox hook: its input or environment names no live session");
                return 0;
            }
            var proc = claude.Value;

            if (hookEvent == "SessionStart")
            {
                var folder = io.Env("CLAUDE_PROJECT_DIR") is { Length: > 0 } dir ? Path.GetFullPath(dir) : null;
                if (state.ReadSession(sessionId) is null && folder is null)
                {
                    state.LogFailure(sessionId, "inbox hook: a session start without a project folder");
                    return 0;
                }
                //a start writes the process every time since a resume runs a new one, and takes the model only when the input carried one
                state.Update(sessionId, r => r is null
                    ? new InboxState.Session(folder!, transcript, proc, startModel, null, [], [])
                    : r with { Claude = proc, Model = startModel ?? r.Model });
                //a clear names no model and its transcript holds no answer yet, so the model the process last answered with comes from the id it left
                if (source == "clear" && startModel is null && state.LatestOf(proc, sessionId) is { } left
                    && (ClaudeSeat.NewestModel(left.TranscriptPath) ?? left.Model) is { } carried)
                    state.Update(sessionId, r => r is null || r.Model is not null ? null : r with { Model = carried });
                //a clear or a resume picked inside a running session keeps the process under a new id, so the arm and lists the process held come along
                if (source is "clear" or "resume" && state.ArmByClaude(proc) is { } old && old.SessionId != sessionId)
                {
                    state.WriteArm(old with { SessionId = sessionId });
                    var prior = state.ReadSession(old.SessionId);
                    if (prior is not null)
                        state.Update(sessionId, r => r is null ? null : r with { Inbox = prior.Inbox, Known = prior.Known, Delivered = prior.Delivered });
                }
                //a resume keeps the id and starts a new process, so the arm naming this session takes the new one
                else if (source == "resume" && state.ReadSession(sessionId)?.Inbox is { } resumed
                    && state.ArmFor(resumed) is { } arm && arm.Arm.SessionId == sessionId)
                    state.WriteArm(arm.Arm with { Claude = proc });
            }

            //every run marks the session in use before the sweep reads the ages, so a session resumed or idle for days keeps its arm
            state.Touch(sessionId);
            state.Sweep(io.Now(), io.Alive);

            var record = state.ReadSession(sessionId);
            if (record?.Inbox is not { } inbox) return 0;
            Settle(io, state, sessionId, record, hookEvent == "Stop");

            //a session that lost the arm watches nothing
            if (state.ArmFor(inbox) is not { } holder || holder.Arm.SessionId != sessionId) return 0;

            //a stop's transcript holds the turn's lines, so it names the model the session now runs, and the row of a moved or closed arm is the wake
            if (hookEvent == "Stop" && Follow(io, state, record, sessionId, proc, transcript, holder.Arm) is { } followed)
            {
                io.Err.Write(followed);
                io.Err.Flush();
                return 2;
            }

            //one watcher per session: the lock is held for the hook's life and the operating system frees it when the process dies
            gate = await TakeLock(io, state, sessionId, holder.Written);
            if (gate is null) return 0;
            state.WritePid(sessionId);
            pidWritten = true;
            state.ClearFailure(sessionId);

            //a folder removed under the arm is made again, since the arm still says watch
            Watch.CreateFolder(inbox);

            //a name no longer in the inbox is free again, so a new letter written under it reads as new
            state.Update(sessionId, r =>
            {
                if (r?.Inbox is not { } box) return null;
                var present = ClaudeSeat.Letters(box).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var kept = r.Known.Where(present.Contains).ToArray();
                return kept.Length == r.Known.Length ? null : r with { Known = kept };
            });
            record = state.ReadSession(sessionId);
            if (record?.Inbox is not { } armed || !InboxState.SameInbox(armed, inbox)) return 0;
            //the arm as this watcher starts: an open that writes it again ends this watcher, so the next one starts from the lists that open wrote
            if (state.ArmFor(armed) is not { } start || start.Arm.SessionId != sessionId) return 0;
            bool Holds() => state.ArmFor(armed) is { } now && now.Arm.SessionId == sessionId && now.Written == start.Written;

            //the batch: letters arrive through take, the pass end closes it, and a lost arm or a gone process ends the wait with nothing
            var wake = new TaskCompletionSource<List<(string Path, string Body)>?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var batch = new List<(string Path, string Body)>();
            var held = new List<string>();
            foreach (var name in record.Known) held.Add(Path.Combine(armed, name));
            foreach (var entry in record.Delivered) held.Add(Path.Combine(armed, entry.Name));
            watch = Watch.Arm(armed,
                (_, _) => false,
                row => state.Log(sessionId, row),
                held: held,
                take: (_, path, body) =>
                {
                    batch.Add((path, body));
                    return true;
                },
                passEnded: w =>
                {
                    w.Dispose();
                    wake.TrySetResult([.. batch]);
                },
                tick: io.Tick,
                keepWatching: () =>
                {
                    //a throw on the pump's thread would end the pump unseen, so it ends the wait with the failure instead
                    try
                    {
                        var keep = Holds() && io.Alive(proc);
                        if (!keep) wake.TrySetResult(null);
                        return keep;
                    }
                    catch (Exception ex)
                    {
                        wake.TrySetException(ex);
                        return false;
                    }
                });
            watch.Start();
            var letters = await wake.Task;
            if (letters is null) return 0;

            //the handover is recorded before the wake, so a kill mid-write cannot wake the session for the same letter twice
            var from = ClaudeSeat.Length(transcript);
            var names = letters.Select(l => Path.GetFileName(l.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            state.Update(sessionId, r => r is null ? null : r with
            {
                Delivered = [.. r.Delivered, .. letters.Select(l =>
                    new InboxState.DeliveredEntry(Path.GetFileName(l.Path), sessionId, proc, transcript, from))]
            });
            io.BeforeWake?.Invoke();
            //the last check narrows the takeover window: an arm lost or written again ends the wake with stderr untouched. the entries go, so no later watcher holds the letter back
            if (!Holds())
            {
                state.Update(sessionId, r => r is null ? null
                    : r with { Delivered = [.. r.Delivered.Where(e => !names.Contains(e.Name))] });
                return 0;
            }
            io.Err.Write(WakeText(letters));
            io.Err.Flush();
            //the exit 2 here is the harness's wake, not gatto's usage code
            return 2;
        }
        catch (Exception ex)
        {
            state.LogFailure(sessionId, "inbox hook: " + ex.Message);
            return 0;
        }
        finally
        {
            watch?.Dispose();
            if (pidWritten) state.DeletePid(sessionId!);
            gate?.Dispose();
        }
    }

    //the lock, or null while a current watcher of this session holds it. a watcher that started before the arm was written ends within its tick, since an open moved the arm under it, so the wait outlasts that tick
    private static async Task<FileStream?> TakeLock(Io io, InboxState state, string sessionId, DateTime armWritten)
    {
        var gate = state.TryLock(sessionId);
        if (gate is not null || state.PidWritten(sessionId) is { } started && started >= armWritten) return gate;
        var until = DateTime.UtcNow + io.Tick + io.Tick + TimeSpan.FromSeconds(1);
        while (gate is null && DateTime.UtcNow < until)
        {
            await Task.Delay(50);
            gate = state.TryLock(sessionId);
        }
        return gate;
    }

    //letters recorded delivered are settled before any watch: a received one moves to the archive, a letter for a gone process goes back, the rest stay
    private static void Settle(Io io, InboxState state, string sessionId, InboxState.Session record, bool onStop)
    {
        var inbox = record.Inbox!;
        var seat = Path.GetFileName(inbox);
        var gone = new List<string>();
        foreach (var entry in record.Delivered)
        {
            var path = Path.Combine(inbox, entry.Name);
            //a letter that left the inbox any other way needs no entry any more
            if (!File.Exists(path)) { gone.Add(entry.Name); continue; }
            if (ClaudeSeat.Received(entry.TranscriptPath, path, entry.From))
            {
                var (moved, row) = Archive.Move(path, "claude/" + seat + "/" + entry.SessionId[..8], entry.SessionId, DateTimeOffset.Now);
                if (row is not null) state.Log(sessionId, row);
                if (moved) gone.Add(entry.Name);
                continue;
            }
            //a letter the process never saw goes back so the next watcher offers it again, and a live process gets one row at each turn end
            if (!io.Alive(entry.Claude)) gone.Add(entry.Name);
            else if (onStop) state.Log(sessionId, $"inbox: {entry.Name} is waiting for its turn to be received");
        }
        if (gone.Count == 0) return;
        var names = gone.ToHashSet(StringComparer.OrdinalIgnoreCase);
        state.Update(sessionId, r => r is null ? null
            : r with { Delivered = [.. r.Delivered.Where(e => !names.Contains(e.Name))] });
    }

    //one wake's text: a body past the per-letter bound goes as its path, and past the wake bound no body is included
    private static string WakeText(List<(string Path, string Body)> letters)
    {
        var include = letters.Select(l => l.Body.Length <= PerLetterChars).ToArray();
        if (letters.Where((_, i) => include[i]).Sum(l => l.Body.Length) > WakeChars) Array.Fill(include, false);
        return string.Join("\n\n", letters.Select((l, i) => "inbox: " + l.Path + "\n\n"
            + (include[i] ? l.Body : "the body was not included; read the file at that path and act on what it says")));
    }

    //one string member of the hook's json, null for anything else
    private static string? Text(JsonNode? node, string name) =>
        node is JsonObject o && o[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
