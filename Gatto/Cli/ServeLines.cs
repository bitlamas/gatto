using Gatto.Roles;
using Gatto.Terminal;

namespace Gatto.Cli;

//what a watched start hands the lines: how to draw the wait, how to take it down, and the size of what loads
internal sealed record StartWatch(Action<TimeSpan> Draw, Action Finish, string? Size = null);

//every word a serve run prints is composed here (the manager reports facts, this layer picks the words and inks)
internal sealed class ServeLines(CliSurface cli, GlyphSet glyphs, StartWatch? watch = null)
    : IServeListener
{
    //a committed row takes the live line down first (the live line ends with no newline, so the row would print over it)
    private void Row(params (string Text, CliInk Ink)[] parts)
    {
        watch?.Finish();
        cli.Row(parts);
    }

    private void Under(string text)
    {
        watch?.Finish();
        cli.Under(text);
    }

    //the one separator, dim with a space each side, used between facts and never inside a sentence
    private (string Text, CliInk Ink) Sep => (" " + glyphs.Dot + " ", CliInk.Dim);

    private static (string Text, CliInk Ink) Model(string id) => (id, CliInk.Model);

    private static (string Text, CliInk Ink) Machinery(string text) => (text, CliInk.Dim);

    private static (string Text, CliInk Ink) Happened(string text) => (text, CliInk.Plain);

    //a fact row states what is and ends bare, the sentence below it says what to do and keeps its full stop
    public void Refused(string model, int pid)
    {
        Row(Happened("already serving "), Model(model), Machinery($" (pid {pid})"));
        Row(Happened("stop it with "), CliSurface.Command("gatto serve stop"), Happened("."));
    }

    public void Ready(string model, int port, int pid) =>
        Row(Happened("serving "), Model(model), Sep,
            Machinery($"ready on http://127.0.0.1:{port} (pid {pid})"));

    //the poll measured this, drawn by the wait row rather than printed here
    public void Loading(TimeSpan elapsed) => watch?.Draw(elapsed);

    //gatto stopped waiting and the child kept going, so this line gives the same facts the ready line does
    public void StillLoading(string model, int pid)
    {
        //the size is why the wait is long, and the reader's only chance to see it once the poll gives up
        var tail = watch?.Size is { } size ? $", {size} (pid {pid})" : $" (pid {pid})";
        Row(Happened("left it loading "), Model(model), Machinery(tail));
        Row(Happened("check with "), CliSurface.Command("gatto serve status"), Happened("."));
    }

    //the Ctrl+C hint stays in the row (it ends this run, it isn't advice about a later one)
    public void ServingForeground(string model, int pid) =>
        Row(Happened("serving "), Model(model), Machinery($" (pid {pid})"), Sep,
            Happened("Ctrl+C to stop"));

    public void Exited(string model) =>
        Row(Happened("llama-server exited"), Sep, Model(model), Happened(" is no longer serving"));

    public void Stopping(string model, int pid) =>
        Row(Happened("stopping "), Model(model), Machinery($" (pid {pid}){glyphs.Ellipsis}"));

    //the one rendering for a kill that failed, it explains a failure so it keeps its full stops
    public void KillFailed(int pid, string message)
    {
        Row(Happened("couldn't stop "), Machinery($"pid {pid}"),
            Happened(": " + message.TrimEnd('.') + "."));
        Row(Happened("stop it in Task Manager, or run "), CliSurface.Command("gatto"),
            Happened(" as administrator."));
    }

    public void DiedDuringLoad(IReadOnlyList<string> tail, bool logAbsent)
    {
        Row(Happened("llama-server exited before it became ready"), Sep,
            Machinery("last 10 log lines"));
        if (tail.Count > 0)
            foreach (var line in tail) Under(line);
        else if (logAbsent)
            Under("no serve.log, and a foreground start writes none");
        else
            Under("serve.log was empty");
    }

    public void NotServingOnStop() =>
        Row(Happened("not serving"), Machinery(" (no serve.json)"));

    public void Stopped(string model, int pid) =>
        Row(Happened("stopped "), Model(model), Machinery($" (pid {pid})"));

    //it explains what gatto did not do, so it is a sentence. the pid is left alone, it belongs to another program now
    public void NotOurs(int pid, string processName) =>
        Row(Machinery($"pid {pid}"), Happened(" is "), Model(processName),
            Happened(" now, not llama-server. gatto left it alone and cleared its record."));

    public void StaleOnStop(int pid, string model) =>
        Row(Happened("not serving"), Sep,
            Machinery($"stale serve.json (pid {pid} gone), cleaned up"));

    public void NotServingOnStatus()
    {
        Row(Happened("not serving"));
        Row(Happened("run "), CliSurface.Command("gatto serve start [model]"),
            Happened(" to launch one."));
    }

    public void StaleOnStatus(int pid, string model)
    {
        Row(Happened("not serving"), Sep,
            Machinery($"stale serve.json (pid {pid} is not a running llama-server)"));
        Row(Happened("clear it with "), CliSurface.Command("gatto serve stop"),
            Happened(", or replace it with "), CliSurface.Command("gatto serve start [model]"),
            Happened("."));
    }

    //the code is known only when a foreground start saw the server end, and serve.log belongs only to a detached run
    public void DiedOnStatus(string model, int pid, int? exitCode, string? exitedAt, IReadOnlyList<string>? tail)
    {
        var ended = exitCode is int code
            ? $" exited with code {CodeWords(code)}" + (LocalTime(exitedAt) is { } at ? $" at {at}" : "")
            : " stopped running, exit code unknown";
        Row(Happened("not serving"), Sep, Model(model), Machinery($" (pid {pid})"), Happened(ended));
        if (tail is { Count: > 0 })
        {
            Row(Machinery("last lines of serve.log"));
            foreach (var line in tail) Under(line);
        }
        else if (tail is not null) Under("serve.log was empty");
        Row(Happened("start it again with "), CliSurface.Command($"gatto serve start {model}"), Happened("."));
    }

    //the session's line for a lost connection when the server gatto recorded for this port stopped running, null otherwise
    internal static string? GoneLine(ServeManager.DeadServer? dead, GlyphSet glyphs) =>
        dead is not { } d ? null
        : $"llama-server for {d.Model} is not running"
          + (d.ExitCode is int code ? $" (exited with code {CodeWords(code)})" : "")
          + $" {glyphs.Dot} gatto serve status for more info, and gatto serve start {d.Model} brings it back";

    //a Windows crash code reads as a large negative number, its hex form is the one a search finds
    internal static string CodeWords(int code) =>
        code < 0 ? $"{code} (0x{unchecked((uint)code):X8})" : code.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string? LocalTime(string? utc) =>
        DateTimeOffset.TryParse(utc, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)
            : null;

    public void Status(string model, int port, int pid, bool healthy) =>
        Row(Happened("serving "), Model(model), Sep, Machinery($"port {port} (pid {pid})"), Sep,
            Happened("health: " + (healthy ? "ok" : "not responding")));

    //the log fault leaves the server running, so the row says the server is fine or the reader goes looking for a fault
    public void PumpFault(ServeLogFault fault) =>
        Row(Happened(fault is ServeLogFault.NotOpened
            ? "serve.log could not be opened. the server is fine, and its start-up log is missing."
            : "serve.log could not be written. the server is fine, and its start-up log is incomplete."));

    //the same events at exit, where nobody typed a stop, so the line has to say what happened to their server
    internal static class AtExit
    {
        //true marks a kill that failed, where the server still holds its memory, and the caller sends that line through the warning sink
        internal sealed record Line(InkedLine Words, bool WentWrong)
        {
            public string Text => Words.Text;
        }

        internal static Line? AfterStop(ServeManager.StopOutcome outcome, bool sessionHadServer, GlyphSet glyphs) =>
            outcome.Result switch
            {
                ServeManager.StopResult.Stopped => new(Stopped(outcome.Model, glyphs), false),
                ServeManager.StopResult.KillFailed => new(InkedLine.Of((Failed(outcome.Pid, outcome.Error), CliInk.Plain)), true),
                //a session that never had a server hears nothing, a line on every exit stops being read
                _ => sessionHadServer ? new(InkedLine.Of((NothingClosed(outcome.Result), CliInk.Plain)), false) : null,
            };

        //a fact row that ends bare, and the model it names takes the identifier ink
        private static InkedLine Stopped(string? model, GlyphSet glyphs) =>
            model is null || model == ServeManager.UnknownModelId
                ? InkedLine.Of(("llama-server stopped", CliInk.Plain))
                : InkedLine.Of(("llama-server stopped", CliInk.Plain), ($" {glyphs.Dot} ", CliInk.Dim),
                    (TermText.Sanitize(model), CliInk.Model), (" unloaded", CliInk.Plain));

        //the reader did not type a stop, so a fresh gatto serve stop is the next thing to try
        private static string Failed(int? pid, string? error)
        {
            var reason = TermText.Sanitize(error ?? "no reason given").TrimEnd('.');
            return $"could not stop llama-server, pid {pid}: {reason}. Stop it with gatto serve stop, "
                + "or end it in Task Manager.";
        }

        //each of the three outcomes needs its own sentence, one wording would fit the absent record and miss the other two
        private static string NothingClosed(ServeManager.StopResult result) => result switch
        {
            ServeManager.StopResult.Stale =>
                "gatto's server already stopped on its own.",
            ServeManager.StopResult.NotOurs =>
                "that pid isn't gatto's server anymore, so gatto is leaving it alone.",
            _ => "gatto won't close a server gatto didn't start.",
        };
    }
}
