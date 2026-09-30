using Gatto.Core.Home;
using Gatto.Terminal;

namespace Gatto.Cli;

//null means production, only a harness passes a context. everything a funnel screen shows goes through Out, the raw console stays empty on a themed step
internal sealed record CommandContext(
    TextWriter Out, string Home, bool Interactive, Theme? Theme,
    ITermSurface? Screen = null, IKeySource? Keys = null,
    Func<string>? ProbeMachine = null, VersionStamp? Stamp = null)
{
    //resolves the home, the interactivity and the chrome from the process, nothing new decided here
    public static CommandContext Production()
    {
        var home = GattoHome.Resolve();
        return new CommandContext(
            Console.Out,
            home,
            !Console.IsInputRedirected && !Console.IsOutputRedirected,
            CommandBanner.Chrome(home));
    }

    //the designed surface for this context, built per call (CliSurface holds no state of its own)
    public CliSurface Surface() => new(Out, Theme, CommandBanner.GlyphsFor(Home));

    //the real consent prompt, drawn on the screen and keys this context names (built here, so only the command that asks pays)
    public UninstallConsent.Asker Prompter() =>
        UninstallConsent.Prompter(Theme, Screen ?? new ConsoleSurface(), Keys ?? new ConsoleKeySource(),
            CommandBanner.GlyphsFor(Home));
}
