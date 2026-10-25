using Gatto.Cli.Launch;
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Roles.Models;
using Gatto.Roles.Serve;
using Gatto.Terminal;

namespace Gatto.Cli;

//gatto serve, and the start, stop and swap of a server that the launch and a model switch share
internal static class ServeCommand
{
    //routes start|stop|status to ServeManager, where start without a target falls back to gatto.json's default_model. a config error exits 2, anything else 1
    internal static async Task<int> RunServeAsync(ParsedArgs args, CommandContext? context = null)
    {
        var ctx = context ?? CommandContext.Production();
        var home = ctx.Home;
        GattoHome.EnsureInitialized(home);

        GattoConfig config;
        try { config = GattoConfig.Load(home); }
        catch (GattoConfigException ex) { Console.Error.WriteLine(ex.Message); return 2; }

        var manager = new ServeManager(home, config.LlamaServer ?? "");

        //only serve status --json stays bare, a parser reads that one and a cat above the object breaks it
        var framed = !(args.Subcommand == "status" && args.Json);
        //the frame opens here and closes in the finally, and the glyph set is passed so a plain console does not draw tofu
        var glyphs = CommandBanner.GlyphsFor(home);
        if (framed)
            CommandBanner.WriteHeader(ctx.Out, ctx.Theme,   //the header names the command only, the manager prints the model in the body
                "serve " + (args.Subcommand ?? "status"), glyphs, ctx.Stamp);

        //every word this command prints is composed here, so the manager can report a fact without knowing how it reads
        var lines = new ServeLines(ctx.Surface(), glyphs);

        try
        {
            switch (args.Subcommand)
            {
                case "start":
                    var modelId = args.Target ?? config.DefaultModel
                        ?? throw new GattoConfigException(
                            "gatto serve start needs a model. Pass one (gatto serve start <model>) or set default_model in gatto.json");
                    var model = Model.Load(Path.Combine(home, "models"), modelId);
                    //print the exposure warning and continue, on stderr so redirecting a foreground start's log doesn't hide it
                    if (ServeArgs.ExposureWarning(model.Profile) is { } exposure)
                        Console.Error.WriteLine(exposure);
                    //the loading row is for a wait the user watches, and a foreground start already has llama-server's own log on screen
                    if (!args.Detach)
                    {
                        //the window is the server's while it runs, and Restore gives the old title back on every exit
                        var title = TerminalTitle.Deed(Console.IsOutputRedirected);
                        title?.Apply(TerminalTitle.Glyph + " llama-server");
                        try { return await manager.StartAsync(model, lines, CancellationToken.None, foreground: true); }
                        finally { title?.Restore(); }
                    }
                    {
                        using var waitLine = new TickLine(ctx.Out, ctx.Theme is not null);
                        var bytes = Gatto.Core.Acquire.ModelDiscovery.SetBytesOrNull(model.Profile.ActivePath);
                        var words = bytes is { } size ? SizeWords.Gb(size, approx: true) : null;
                        var row = LoadingLine.Over(waitLine, glyphs, ctx.Theme);
                        var detached = new ServeLines(ctx.Surface(), glyphs,
                            new StartWatch(e => row.Draw(e, model.Id, words), waitLine.Finish, words));
                        var code = await manager.StartAsync(model, detached, CancellationToken.None);
                        waitLine.Finish();
                        return code;
                    }
                case "stop":
                {
                    //the body goes through ServeLines, so a raw write can't appear between the banner and the rows under it
                    return await manager.StopAsync(lines, CancellationToken.None);
                }
                default: //the status arm, which a bare gatto serve also reaches (ArgRouter allows nothing else)
                {
                    //the --json path skips ServeLines and the banner, a parser has to get the object alone
                    if (args.Json) return await manager.StatusJsonAsync(ctx.Out, CancellationToken.None);
                    return await manager.StatusAsync(lines, CancellationToken.None);
                }
            }
        }
        catch (GattoConfigException ex) { Console.Error.WriteLine(ex.Message); return 2; }
        finally
        {
            //the closing blank follows the same fact as the header above, so a json run writes none
            if (framed) ctx.Surface().Close();
        }
    }


    //the previous model is restarted at most once, and false leaves the caller's session on the model it already had
    internal static async Task<bool> SwapServerAsync(
        ServeManager manager, Gatto.Roles.Models.Model? previous, Gatto.Roles.Models.Model incoming, Action<string> say,
        Gatto.Terminal.GlyphSet? glyphs, string? cannotRestore = null)
    {
        //the stop's rows go to NullServeListener, this path says what happened in its own sentence below
        await manager.StopAsync(Gatto.Roles.Serve.NullServeListener.Instance, CancellationToken.None).ConfigureAwait(false);

        if (await AutoServeAsk.StartAndWaitAsync(manager, incoming, say, CancellationToken.None, glyphs ?? Gatto.Terminal.GlyphSet.Unicode)
            .ConfigureAwait(false)) return true;

        if (previous is null)
        {
            //the reason the previous model did not load comes in cannotRestore, and it is said here
            say(cannotRestore ?? "nothing is serving now. gatto serve start when you're ready.");
            return false;
        }

        say($"loading {previous.Id} back{(glyphs ?? Gatto.Terminal.GlyphSet.Unicode).Ellipsis}");
        if (await AutoServeAsk.StartAndWaitAsync(manager, previous, say, CancellationToken.None, glyphs ?? Gatto.Terminal.GlyphSet.Unicode)
            .ConfigureAwait(false)) return false;

        //with both models gone the sentence names a reboot, memory a dead process still holds is the likely cause
        say($"couldn't restart {previous.Id} either; a reboot may be needed to reclaim GPU memory.");
        return false;
    }

    //the model a failed /model swap puts back, loaded from the id the server held. a model that will not load returns the sentence to say in its place
    internal static (Gatto.Roles.Models.Model? Model, string? CannotRestore) RestoreTarget(string modelsDir, string servedId)
    {
        try { return (Gatto.Roles.Models.Model.Load(modelsDir, servedId), null); }
        catch (GattoConfigException ex)
        {
            return (null, $"couldn't load {servedId} to put it back: {ex.Message}. nothing is serving now. "
                + "gatto serve start when you're ready.");
        }
    }

    //whether gatto serves this endpoint and whose window sizes the session: a folder's port, auto_serve and context count only on the endpoint gatto serves
    internal static (bool GattoServes, int? Window) ServingFor(bool servedHere, Gatto.Roles.Models.Model? model,
        EndpointConfig endpoint, string? baseUrl) =>
        servedHere && model is not null
            ? (baseUrl is not null, model.Profile.Context)
            : (false, endpoint.Context);

    //the binary a server starts with, the model's override or else the config's. a manager built only to read takes the config's own
    internal static string ServerBinaryFor(Gatto.Roles.Models.Model? model, GattoConfig config) =>
        model?.Profile.LlamaServer ?? config.LlamaServer ?? "";

    //is this model the one the running server holds, asked once so a switch names it. the binary does not decide this, Serving reads serve.json
    internal static Gatto.Roles.Serve.ServingState QuantServing(
        string home, Gatto.Roles.Models.Model model, GattoConfig config) =>
        new Gatto.Roles.Serve.ServeManager(home, ServerBinaryFor(model, config)).Serving(model.Id);
}
