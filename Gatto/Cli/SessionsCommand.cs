using System.Globalization;
using Gatto.Cli.Diagnose;
using Gatto.Cli.Install;
using Gatto.Core;
using Gatto.Core.Session;

namespace Gatto.Cli;

//gatto sessions sweep <date>: says what goes, asks once, deletes. nothing runs it on a timer or at launch
internal static class SessionsCommand
{
    internal const string Usage = "usage: gatto sessions sweep <YYYY-MM-DD> [--yes]";

    //the ask is a seam for tests, null asks through the context's own prompt
    internal static async Task<int> RunAsync(ParsedArgs args, CommandContext? context = null, UninstallConsent.Asker? ask = null)
    {
        if (args.Subcommand != "sweep" || args.Target is null
            || !DateTime.TryParseExact(args.Target, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var before))
        {
            Console.Error.WriteLine(args.Subcommand is null or "sweep"
                ? Usage
                : $"unknown sessions subcommand '{args.Subcommand}'. {Usage}");
            return 2;
        }

        var ctx = context ?? CommandContext.Production();
        CommandBanner.WriteHeader(ctx.Out, ctx.Theme, "sessions", CommandBanner.GlyphsFor(ctx.Home), stamp: ctx.Stamp);
        var cli = ctx.Surface();
        var date = before.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var plan = await PlanWithTimerAsync(ctx, before).ConfigureAwait(false);
        cli.Say(plan.Transcripts == 0
            ? $"no transcripts last written before {date}"
            : $"{Plural.Of(plan.Transcripts, "transcript")} last written before {date}, {StoreWeight.Size(plan.SessionBytes)}");
        cli.Say(plan.Blobs.Count == 0
            ? "no images left without a session"
            : $"{Plural.Of(plan.Blobs.Count, "image")} named by no remaining session, {StoreWeight.Size(plan.BlobBytes)}");

        if (plan.SessionFiles.Count == 0 && plan.Blobs.Count == 0) return Done(cli, 0);

        if (!args.Yes)
        {
            //a scheduled run passes --yes, any other run without a terminal only reports
            if (!ctx.Interactive)
            {
                cli.Say("nothing was deleted: without a terminal the sweep deletes only with --yes");
                return Done(cli, 0);
            }
            cli.Blank();
            var answer = (ask ?? ctx.Prompter())("Delete them?", ["No, keep them", "Yes, delete them"]);
            cli.Blank();
            if (answer != 1)
            {
                cli.Say("nothing was deleted");
                return Done(cli, 0);
            }
        }

        var failures = SessionSweep.Delete(plan);
        foreach (var (path, why) in failures)
            Console.Error.WriteLine($"! couldn't delete {path}: {why}");
        if (failures.Count > 0)
        {
            cli.Say($"{Plural.Of(failures.Count, "file")} left in place, the rest deleted");
            return Done(cli, 1);
        }
        var what = new List<string>();
        if (plan.Transcripts > 0) what.Add(Plural.Of(plan.Transcripts, "transcript"));
        if (plan.Blobs.Count > 0) what.Add(Plural.Of(plan.Blobs.Count, "image"));
        cli.Say($"deleted {string.Join(" and ", what)}, {StoreWeight.Size(plan.SessionBytes + plan.BlobBytes)}");
        return Done(cli, 0);
    }

    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(Setup.AuditionTicker.FrameMs);   //the pulse's step, one home for it

    //reading a large home takes seconds, so a live timer runs until the rows print, and a plain or piped run draws none
    private static async Task<SweepPlan> PlanWithTimerAsync(CommandContext ctx, DateTime before)
    {
        var glyphs = CommandBanner.GlyphsFor(ctx.Home);
        using var tick = new TickLine(ctx.Out, ctx.Theme is not null);
        var work = Task.Run(() => SessionSweep.Plan(ctx.Home, before));
        var from = System.Diagnostics.Stopwatch.GetTimestamp();
        var frame = 0;
        //a quick plan draws no frame, the first one comes after one pulse
        while (await Task.WhenAny(work, Task.Delay(Frame)).ConfigureAwait(false) != work)
            tick.LivePainted(LoadingRow.Render($"reading the sessions{glyphs.Ellipsis}",
                System.Diagnostics.Stopwatch.GetElapsedTime(from), glyphs, ctx.Theme, frame++), glyphs);
        tick.Finish();
        return await work.ConfigureAwait(false);
    }

    //the closing row pairs the banner on every path that printed one
    private static int Done(CliSurface cli, int code)
    {
        cli.Close();
        return code;
    }
}
