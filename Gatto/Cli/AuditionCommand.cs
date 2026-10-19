using Gatto.Core;
using Gatto.Core.Home;
using Gatto.Terminal;

namespace Gatto.Cli;

//gatto audition, one model through the battery, and the ink its report is painted with
internal static class AuditionCommand
{
    //exit 0 is a model that passed, 1 is a model that failed, 2 is a problem on our side. the local config catch keeps a harness fault from exiting 1
    internal static async Task<int> RunAuditionAsync(ParsedArgs args)
    {
        var home = GattoHome.Resolve();
        GattoHome.EnsureInitialized(home);

        //the closing blank goes through cli.Close in a finally, and frameOpen keeps a refusal before the header from leaving a blank with no header
        var cli = new CliSurface(Console.Out, theme: null, glyphs: null);
        var frameOpen = false;
        try
        {
            var config = GattoConfig.Load(home);
            var modelId = args.Target ?? config.DefaultModel
                ?? throw new GattoConfigException(
                    "gatto audition <model>: no model given and no \"default_model\" set in gatto.json");

            //the theme and Sanitize enter here, since Cli may reach Repl and Roles takes both as parameters so it never does
            var theme = CommandBanner.Chrome(home);   //output-only detection, so a redirect to a file stays byte-pure with one line per task
            var paint = InkFor(theme);
            //one glyph set is chosen for this run and shared by the header and the voice below
            var glyphs = CommandBanner.GlyphsFor(home);
            //no stamp on this header, RunAuditionAsync takes no CommandContext and threading one through would be a second seam
            CommandBanner.WriteHeader(Console.Out, theme, "audition",   //the header names the command only, the report's own model row names the model
                glyphs);
            frameOpen = true;

            using var console = new AuditionConsole(Console.Out, theme is not null, paint);
            var verdict = await Gatto.Roles.Audition.AuditionRunner
                .RunAsync(home, modelId, console, CancellationToken.None,
                    engineMarks: CommandBanner.MarksFor(home),
                    //the runner is in Roles and has no vocabulary, so a serve failure is reported through ServeLines handed in here
                    serveVoice: w => new ServeLines(new CliSurface(w, null, glyphs), glyphs),
                    //the runner commits each loading rung as a note, with the words taken from LoadingRow
                    loadingWords: elapsed => LoadingRow.Words(elapsed, modelId, GattoApp.AuditionSize(home, modelId), glyphs));
            console.Finish();       //the ticks come down before the report goes up

            cli.Blank();
            Console.WriteLine(Gatto.Roles.Audition.AuditionReport.Render(verdict, TermText.Sanitize,
                CommandBanner.MarksFor(home), paint));

            //both badge keys come off the verdict, and a model adopted off disk keeps a null repo id
            Gatto.Roles.Audition.BadgeWriter.Write(home, verdict);
            return verdict.Pass ? 0 : 1;
        }
        //indent the failure like every other line this command prints, a message at column 0 reads as gatto crashing
        catch (GattoConfigException ex)
        {
            //the tail is printed as subordinate rows through CliSurface.Under, the one indent this layer writes
            var err = new CliSurface(Console.Error, theme: null, glyphs: null);
            err.Blank();
            foreach (var line in ex.Message.Split('\n'))
                err.Under(line.TrimEnd('\r'));
            return 2;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            //a disk failure is our problem, so it exits 2 instead of reading as a verdict about the model
            Console.Error.WriteLine($"audition could not run: {ex.Message}");
            return 2;
        }
        finally
        {
            if (frameOpen) cli.Close();
        }
    }

    //maps ReportInk onto the theme, so the colour decision stays in Cli where Theme lives
    private static Func<string, Gatto.Roles.Audition.ReportInk, string>? InkFor(Theme? theme) =>
        theme is null ? null : (text, ink) => theme.Paint(text, ink switch
        {
            Gatto.Roles.Audition.ReportInk.Ok => Theme.Ok,
            Gatto.Roles.Audition.ReportInk.Fail => Theme.Err,
            Gatto.Roles.Audition.ReportInk.Accent => Theme.Accent,
            _ => Theme.Dim,
        });
}
