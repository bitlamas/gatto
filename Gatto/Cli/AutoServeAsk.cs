using Gatto.Repl;
using Gatto.Roles;

namespace Gatto.Cli;

//starts a model's server and waits for it, reporting progress as it goes
internal static class AutoServeAsk
{
    //this path starts nothing, it only waits for a server of ours still loading on the launch port. the line's number is the elapsed time the ready poll measured
    internal static async Task<ServerReadiness?> AwaitOursLoadingAsync(
        ServeManager manager, int port, TimeSpan budget, LoadingLine row,
        Func<string, string?>? sizeOf, CancellationToken ct)
    {
        if (manager.DescribeRunning() is not { } running || running.Port != port) return null;

        //resolve the size from the recorded id so the last rung can show it, a null size would read as a failure to measure
        var modelId = Gatto.Terminal.TermText.Sanitize(running.Model);
        var size = sizeOf?.Invoke(running.Model);
        return await manager.AwaitReadyAsync(port, budget,
            elapsed => row.Draw(elapsed, modelId, size), ct).ConfigureAwait(false);
    }

    //one clock spans the start and the wait, so the row's number never drops back to zero (the clock parameter is for a test)
    public static async Task<bool> StartAndWaitAsync(
        ServeManager manager, Model model, Action<string> say, CancellationToken ct,
        Gatto.Terminal.GlyphSet glyphs, LoadingLine? row = null, string? size = null,
        Func<TimeSpan>? clock = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var now = clock ?? (() => sw.Elapsed);
        //this capture owns no live line, so the take-down is a no-op, and the caller that owns a screen passes its own
        var watch = row is null ? null
            : new StartWatch(_ => row.Draw(now(), model.Id, size), () => { }, size);
        watch?.Draw(TimeSpan.Zero);
        Action<TimeSpan>? tick = watch is null ? null : watch.Draw;

        //capture the words plain, a failure replays them one row at a time through the warning sink
        var log = new StringWriter();
        var lines = new ServeLines(new CliSurface(log, null, glyphs), glyphs, watch);
        if (await manager.StartAsync(model, lines, ct).ConfigureAwait(false) != 0)
        {
            say($"couldn't start the server for {model.Id}.");
            //one say per log line, so each line becomes its own warning row
            foreach (var said in log.ToString().Split('\n'))
                if (said.TrimEnd('\r') is { } text && !string.IsNullOrWhiteSpace(text))
                    say(Gatto.Terminal.TermText.Sanitize(text));
            return false;
        }

        //the wait draws the same row the start drew, on the same clock, so a normal wait never goes through the warning sink
        var readiness = await manager.AwaitReadyAsync(
            model.Profile.Port, Gatto.Roles.Audition.AuditionRunner.ReadyBudget,
            tick ?? (_ => { }), ct).ConfigureAwait(false);

        switch (readiness)
        {
            case ServerReadiness.Ready:
                return true;
            case ServerReadiness.Died:
                //the process is gone, waiting longer can't help
                say($"the server for {model.Id} started and then exited.");
                return false;
            default:
                //still loading is not a failure, a big model on a small machine can outlast any budget and the row already said so
                return true;
        }
    }
}
