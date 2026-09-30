namespace Gatto.Cli;

//check, ask, verify, replace. every refusal happens before the download starts, and exit codes are 0 for nothing changed, 1 for a refusal, 2 for usage
internal static class UpdateCommand
{
    //a consent this command cannot obtain is not one it may assume, so a non-interactive run refuses before the frame opens
    public static async Task<int> RunAsync(CommandContext ctx, CancellationToken ct,
        UpdateSite? site = null, UpdateActs? acts = null, UpdateAsker? ask = null,
        EngineSite? engineSite = null, EngineActs? engineActs = null, EngineAsker? engineAsk = null)
    {
        if (!ctx.Interactive)
        {
            ctx.Out.WriteLine("gatto update needs a terminal it can ask questions in. Run it "
                + "directly rather than through a pipe or a script.");
            return 2;
        }

        //the closing row paired with the header the body writes, in a finally so every exit from the body reaches it
        try
        {
            var code = await GattoPhaseAsync(ctx, ct, site, acts, ask).ConfigureAwait(false);
            //this process still holds the previous pin after it replaced gatto, so the engine phase belongs to the next run
            if (code == Updated)
            {
                ctx.Surface().Say("run gatto update again to bring the engine to the release the new gatto is tested with.");
                return 0;
            }
            return code != 0 ? code
                : EngineUpdate.Run(ctx, ctx.Surface(), CommandBanner.GlyphsFor(ctx.Home), engineSite, engineActs, engineAsk);
        }
        finally { ctx.Surface().Close(); }
    }

    //a code the gatto phase returns only after it replaced the binary, distinct from every exit code the command reports
    private const int Updated = -1;

    private static async Task<int> GattoPhaseAsync(CommandContext ctx, CancellationToken ct,
        UpdateSite? site, UpdateActs? acts, UpdateAsker? ask)
    {
        var cli = ctx.Surface();
        //one glyph set for the whole run, resolved from the home this command acts on
        var g = CommandBanner.GlyphsFor(ctx.Home);

        //the one-line header, since this command answers a question about this machine. it sits after the interactive refusal so a piped run prints its error alone
        CommandBanner.WriteHeader(ctx.Out, ctx.Theme, "update", g, stamp: ctx.Stamp); //the header goes first, before every resolution that can throw, so a closing row never appears above a missing header

        //the subject is the install directory, since a first install belongs to the wizard
        var where = site ?? UpdateSite.Probe();
        var deeds = acts ?? UpdateActs.Production;
        if (!where.ExePresent)
        {
            cli.Say($"gatto isn't installed on this machine (nothing at {where.InstallDir}). "
                + $"Download the latest release from {UpdateCheck.ReleasesPage} and run gatto setup.");
            return 2;
        }

        //the check's own client, with a whole-exchange deadline of 10s, since the body is small JSON
        using var checkHttp = UpdateCheck.Client(TimeSpan.FromSeconds(3));
        using var checkRead = CancellationTokenSource.CreateLinkedTokenSource(ct);
        checkRead.CancelAfter(TimeSpan.FromSeconds(10));
        var release = await deeds.Fetch(checkHttp, DateTimeOffset.Now, checkRead.Token).ConfigureAwait(false);
        if (release is null)
        {
            cli.Say("couldn't reach GitHub just now. Try again later.");
            return 1;
        }

        if (UpdateCheck.WindowsZip(release.Assets) is not { } zip)
        {
            cli.Say("the latest release has no Windows download gatto recognises. "
                + $"See {release.NotesUrl}.");
            return 1;
        }

        //forward only, and an unparseable version on either side refuses rather than being guessed
        var running = where.InstalledVersion;
        if (!ReleaseVersion.TryParse(release.State.Latest, out var latest)
            || !ReleaseVersion.TryParse(running, out var have))
        {
            cli.Say($"couldn't compare versions ({release.State.Latest ?? "?"} vs {running ?? "?"}). "
                + "Nothing was changed.");
            return 0;   //nothing was asked of this phase and nothing failed, and the engine phase does not depend on the comparison
        }

        if (!latest.IsNewerThan(have))
        {
            //equal and older both download nothing and neither is a failure, so the engine phase follows either
            if (have.IsNewerThan(latest))
            {
                //both versions print without the v, or the tag and the running version read as two kinds of thing
                cli.Say($"the latest release ({ReleaseVersion.Bare(release.State.Latest)}) is older "
                    + $"than this gatto ({running}). Nothing was changed.");
                return 0;
            }
            cli.Say($"you're on the latest release ({running}).");
            return 0;
        }

        //the probe happens at the top, so a doomed update never spends the download, and the sentence is gatto's own rather than the OS's
        if (where.OldStillHeld)
        {
            cli.Say("an older gatto session is still holding the previous binary. Close it and "
                + "run gatto update again.");
            return 1;
        }

        //the ask takes no pre-selected default, and declining it exits 0 since nothing changed by the user's choice
        var asker = ask ?? UpdateConsent.Prompter(ctx);
        var spec = UpdateConsent.SpecFor(release.State.Latest!, running!, zip.Size, where.InstallDir,
            release.NotesUrl, CommandBanner.GlyphsFor(ctx.Home));
        var answer = asker(spec);

        if (answer != 0)
        {
            cli.Say("nothing was changed.");
            return 0;
        }

        //the zip is swept on every exit from here, success included, since the installed copy is the artifact
        string? downloaded = null;
        try
        {
            try
            {
                //the download gets its own untimed client, since the check's whole-exchange budget would cap a 38 MB transfer
                using var downloadHttp = UpdateDownload.Client(TimeSpan.FromSeconds(3));
                downloaded = await deeds.Download(downloadHttp, zip, ctx.Home, Progress(cli, g), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                //close the live row here before the sentence, and again on the success path, since a finally would run after the catch body
                cli.Blank();
                cli.Say($"the download failed: {ex.Message}. Nothing was changed.");
                return 1;
            }

            cli.Blank();   //close the live row before anything else writes

            if (await deeds.Verify(downloaded, zip.Sha256, ct).ConfigureAwait(false) is { } bad)
            {
                cli.Say(bad);
                return 1;
            }

            var staged = Path.Combine(UpdateDownload.Dir(ctx.Home), "staged");
            if (deeds.Extract(downloaded, staged) is { } cannot)
            {
                cli.Say(cannot);
                return 1;
            }

            if (deeds.Apply(SelfInstall.ExeIn(staged), where.InstallDir) is { } failed)
            {
                cli.Say(failed);
                return 1;
            }
        }
        finally
        {
            UpdateDownload.Sweep(ctx.Home);
        }

        //the report names live sessions only when there are any, and the count comes from the list the probe read
        cli.Say($"updated gatto {running} {g.Right} {release.State.Latest} at {where.InstallDir}.");
        if (where.SiblingGattos.Count is > 0 and var n)
            cli.Say($"{n} session{(n == 1 ? " is" : "s are")} still on {running}. "
                + "They'll pick it up when you restart them.");

        return Updated;
    }

    //the live byte counter, reported inline so the numbers stay in order. using Progress<T> would post each report onto a thread pool a console app doesn't have
    private static IProgress<(long Done, long Total)> Progress(CliSurface cli, Gatto.Terminal.GlyphSet g) =>
        new Inline<(long Done, long Total)>(p =>
            cli.Live($"downloading{g.Ellipsis} {Mb(p.Done)} of {Mb(p.Total)}"));

    //reports on the calling thread, in order, which is safe only while the download's read loop is sequential
    private sealed class Inline<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static string Mb(long bytes) => (bytes / 1024d / 1024d).ToString("0.0") + " MB";
}
