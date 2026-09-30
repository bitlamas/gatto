using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Cli;

//what gatto update reads about this machine, gathered once before it decides anything. the reads are here and the writes sit in UpdateActs
internal sealed record UpdateSite(
    string InstallDir,
    bool ExePresent,
    string? InstalledVersion,
    IReadOnlyList<int> SiblingGattos,
    bool OldStillHeld)
{
    public static UpdateSite Probe()
    {
        var dir = SelfInstall.DefaultDir();
        return new UpdateSite(dir, SelfInstall.IsInstalled(), SelfInstall.InstalledVersion(dir),
            UninstallSite.Siblings(), SelfInstall.OldStillHeld(dir));
    }
}

//null when the user left. the prompter takes the whole spec so it shows exactly what SpecFor built
internal delegate int? UpdateAsker(SelectSpec spec);

//the consent screen, with no pre-selected row and no Recommended tail. a highlighted default on a network call and a binary replacement is a nudge
internal static class UpdateConsent
{
    //the one spelling of the yes on both update questions, the program's and the engine's
    internal const string YesUpdate = "Yes — update";

    //the ask as data, so a test can assert its shape without a terminal, and it sets NoInitialCursor
    public static SelectSpec SpecFor(string latest, string running, long bytes, string installDir,
        string notesUrl, Gatto.Terminal.GlyphSet? glyphs)
    {
        var g = glyphs ?? Gatto.Terminal.GlyphSet.Unicode;
        return new(
            TitleRows: [],
            Question: new PromptQuestion(
                $"download {(bytes / 1024d / 1024d):0.0} MB and replace {Path.Combine(installDir, "gatto.exe")}?"),
            Options: [new SelectOption(YesUpdate), new SelectOption("Not now")],
            FooterHint: $"{g.ArrowsKey} choose {g.Dot} Enter confirm {g.Dot} Esc leave",
            BodyRows:
            [
                //say "you're on", matching the weekly line's phrasing so one fact reads one way
                new BodyRow($"gatto {latest} is out — you're on {running}."),
                //the release notes sit here rather than in the weekly line, where they are useful at the moment of deciding
                new BodyRow($"what changed: {notesUrl}"),
            ],
            NoInitialCursor: true);
    }

    //the real prompt on whatever screen the context names, the same construction UninstallConsent.Prompter makes
    public static UpdateAsker Prompter(CommandContext ctx) => spec =>
        new SelectPrompt(ctx.Screen ?? new ConsoleSurface(), ctx.Theme ?? new Theme(TermCaps.Plain),
                ctx.Keys ?? new ConsoleKeySource(), pump: null, chrome: null,
                glyphs: CommandBanner.GlyphsFor(ctx.Home))
            .Show(spec) switch
        {
            SelectOutcome.Chosen c => c.Index,
            _ => null,
        };
}

//the deeds behind a seam, so a refusal row can assert that none of them ran
internal sealed record UpdateActs(
    Func<HttpClient, DateTimeOffset, CancellationToken, Task<Release?>> Fetch,
    Func<HttpClient, ReleaseAsset, string, IProgress<(long Done, long Total)>?, CancellationToken, Task<string>> Download,
    Func<string, string?, CancellationToken, Task<string?>> Verify,
    Func<string, string, string?> Extract,
    Func<string, string, string?> Apply)
{
    public static UpdateActs Production { get; } = new(
        UpdateCheck.FetchAsync,
        //the seam speaks in homes and the engine in folders, so this is the one line that turns a home into an update folder
        (http, asset, home, progress, ct) =>
            UpdateDownload.FetchAsync(http, asset, UpdateDownload.Dir(home), progress, ct),
        //a lambda rather than the method group, so gatto's own update keeps its own releases page by saying so
        (zip, sha, ct) => UpdateDownload.VerifyAsync(zip, sha, ct),
        UpdateDownload.ExtractExe,
        SelfInstall.Apply);
}
