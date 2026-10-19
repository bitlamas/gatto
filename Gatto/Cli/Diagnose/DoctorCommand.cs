using Gatto.Core;
using Gatto.Core.Home;
using Gatto.Roles.Serve;

namespace Gatto.Cli.Diagnose;

//gatto doctor, read-only, with the client its checks read through
internal static class DoctorCommand
{
    //read-only, so a missing home is left for the checks to report. the HttpClient here is real, and the tests drive Doctor with their own probes

    //internal so a test can drive it with its own records, the dispatch line above stays uncovered
    internal static async Task<int> RunDoctorAsync(
        CommandContext? context = null, DoctorProbes? outside = null)
    {
        var ctx = context ?? CommandContext.Production();
        var probes = outside ?? DoctorProbes.Production;
        var home = ctx.Home;
        var cwd = Environment.CurrentDirectory;
        using var http = DoctorClient(probes.Transport);
        //the running-server row reads through ServeManager.DescribeRunning, which never spawns. an unreadable config leaves it null, and the config checks report that
        Func<Gatto.Roles.Serve.RunningInfo?>? describe = null;
        var configTheme = "auto";
        try
        {
            var cfgForServe = GattoConfig.Load(home);
            configTheme = cfgForServe.Theme;
            var mgr = new ServeManager(home, cfgForServe.LlamaServer ?? "");
            describe = () => mgr.DescribeRunning();
        }
        catch (GattoConfigException) { } //doctor's config checks own this failure

        //colour follows stdout alone, doctor never reads a key. an unloadable config keeps the auto theme, so a broken gatto.json cannot stop doctor
        var theme = ctx.Theme;
        CommandBanner.WriteHeader(ctx.Out, theme, "doctor", CommandBanner.GlyphsFor(ctx.Home),
            stamp: ctx.Stamp);
        var code = await new Doctor(http, describeRunning: describe, theme: theme,
            //the update check runs on a diagnostic's budget, 1s whole exchange and 500ms to connect
            checkUpdate: probes.CheckUpdate)
            .RunAsync(home, cwd, ctx.Out, CancellationToken.None);
        ctx.Surface().Close();   //the frame's closing row, paired with the header above it
        return code;
    }

    //the client the doctor reads through carries no timeout, each check holds its own deadline (internal so a test can read it)
    internal static HttpClient DoctorClient(HttpMessageHandler transport) =>
        new(transport) { Timeout = Timeout.InfiniteTimeSpan };
}
