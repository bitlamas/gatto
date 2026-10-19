using Gatto.Cli.Diagnose;

namespace Gatto.Cli;

//gatto status, read-only like doctor
internal static class StatusCommand
{
    //routes gatto status to Status, read-only and with no EnsureInitialized like doctor
    internal static async Task<int> RunStatusAsync(CommandContext? context = null)
    {
        var ctx = context ?? CommandContext.Production();
        var cwd = Environment.CurrentDirectory;
        //untimed, the health read beneath status holds a 5s deadline of its own
        using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) })
            { Timeout = Timeout.InfiniteTimeSpan };
        //the one-line banner takes the glyph set so a host without those glyphs still draws the cat
        CommandBanner.WriteHeader(ctx.Out, ctx.Theme, "status", CommandBanner.GlyphsFor(ctx.Home),
            stamp: ctx.Stamp);
        var code = await Status.RunAsync(ctx.Home, cwd, ctx.Out, http, CancellationToken.None);
        ctx.Surface().Close();   //the frame's closing row, paired with the header above it
        return code;
    }
}
