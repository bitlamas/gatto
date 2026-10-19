using Gatto.Core;
using Gatto.Core.Home;
using Gatto.Extensions;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//gatto setup and gatto model, the two command entries into the wizard
internal static class SetupCommand
{
    //routes gatto setup and gatto model, which differ only in modelSegmentOnly, so the lifecycle has one home and its order holds
    internal static (int Code, bool Launch) RunSetup(bool modelSegmentOnly = false, string? command = null)
    {
        if (Console.IsInputRedirected || Console.IsOutputRedirected)   //refused before the banner with exit 2 and a pointer to gatto doctor, since a wizard with nobody to answer would invent answers
        {
            Console.Error.WriteLine(
                "gatto setup needs a terminal it can ask questions in. Run it directly rather than "
                + "through a pipe or a script. To check an existing setup instead, run: gatto doctor");
            return (2, false);
        }

        var home = GattoHome.Resolve();

        //one glyph set for the run, asked once and handed to every painter
        var glyphs = CommandBanner.GlyphsFor(home);

        //nothing is written on the way in, the home appears at the first real write so looking at the wizard leaves nothing behind
        var theme = CommandBanner.Chrome(home);

        //the wizard prints no banner, but CommandBanner.Chrome still runs to resolve the theme the face needs

        //the TUI face is the one this command builds, from the same flow the plain face got. the version is read off the build
        var surface = new ConsoleSurface();
        var chrome = theme ?? new Theme(TermCaps.Detect(), ThemeMode.Dark);
        //the face defaults no timer, the pulse is passed in by name and disposed at the end. the glyph set is resolved here beside the theme and handed to the face
        var wizardCommand = command ?? (modelSegmentOnly ? "gatto model" : "gatto setup");   //the words the user typed, so gatto model new is not reported as gatto model

        //the wizard runs before the config loader and reads the mouse setting itself, alt_screen gates the REPL's mouse and not this
        var input = Setup.SetupInput.For(Setup.SetupInput.MouseOf(home),
            () => new ConsoleInputSource(new Win32ConsoleInputReader()), () => new KeyInputSource(new ConsoleKeySource()),
            () => new ConsoleInputMode(new Win32ConsoleModeControl()), Setup.SetupInput.SystemDoubleClickMs);

        using var face = new Setup.Tui.TuiSetupSurface(
            surface, input.Source, chrome,
            Gatto.Core.GattoVersion.String, Gatto.Core.GattoVersion.Build ?? "",
            command: wizardCommand,
            pulse: () => new Gatto.Terminal.TimerPurrPulse(),
            glyphs: glyphs,
            //the purr is drawn once here from the pool, so nothing a test constructs can reach the draw
            fullPurr: Gatto.Repl.Render.Chrome.PurrFrames.RandomFromPool(),
            doubleClickMs: input.DoubleClickMs);

        //the wizard runs on the alt buffer and gives it back on every exit, SetupSession owns that lifecycle
        var alt = new AltScreen(surface) { QuietWheel = input.QuietWheel };

        //rich comes from the banner and the probes write into the face, so their lines reach the user at the epilogue
        var probes = new Setup.LiveSetupProbes(home, face.Glyphs, face.Notes,
            purrSet: Gatto.Repl.Render.Chrome.PurrFrames.RandomFromPool,
            rich: theme is not null,
            //these lines go to the console, Dispose runs after the record has printed
            afterWalk: Console.Out,
            working: face.Working);
        var flow = new Setup.SetupFlow(probes);
        using (probes)
        {
            //the scrollback is composed after the wizard returns, from the facts true then, with the width and the clock read here
            var exit = Setup.SetupSession.Run(
                alt, () => Setup.SetupRunner.Run(flow, face, home, modelSegmentOnly),
                () => Setup.SetupSession.Scrollback(flow, () => face.LastPainted, surface.Width, face.Glyphs,
                    VersionStamp.Running, DateOnly.FromDateTime(DateTime.Now), wizardCommand, theme),
                Console.Out, notes: () => face.CapturedNotes, command: wizardCommand, inputMode: input.Mode);

            //the shipped files are written after the wizard and only when the home exists, since writing them is what creates it
            if (Directory.Exists(home))
            {
                try { ShippedExtensions.EnsureWritten(home); }
                catch (Exception ex) { Console.Error.WriteLine($"! setup: could not write shipped files: {ex.Message}"); }
            }

            //the caller launches the REPL, and a server setup started stays up for the session
            if (!flow.StartReplWhenDone) return (exit, false);   //false when entered at the model segment, which has no REPL to start

            //the countdown runs after the record and before the REPL, as a live line (the record is composed once)
            var cli = new CliSurface(Console.Out, theme, glyphs);
            Setup.Tui.Countdown.Run(cli.Live, System.Threading.Thread.Sleep, glyphs: glyphs);
            cli.Blank();   //ends the live line, so the next row starts at column zero
            //the row is composed through the painter, so it degrades to the same plain sentence when theme is null
            if (probes.HandOverServer())
                cli.Row(
                    ("the server stays up for this session", CliInk.Plain),
                    ($" {glyphs.Dot} ", CliInk.Dim),
                    CliSurface.Command("gatto serve stop"),
                    (" when you're done.", CliInk.Plain));
            return (exit, true);
        }
    }

    //the engine half of the setup question, where FirstRunDoor asks about the model. it asks for the value only, and a config that will not load answers false
    private static bool NoEngineIn(string home)
    {
        try { return string.IsNullOrWhiteSpace(GattoConfig.Load(home).LlamaServer); }
        catch (GattoConfigException) { return false; }
    }

    //both refusals run before the banner, a cat above a usage error has already printed output
    internal static Task<int> RunModelAsync(ParsedArgs args, CommandContext? ctx = null)
    {
        //an argument or an unknown subcommand is refused, bare new still opens the shelf
        if (args.Target is not null || (args.Subcommand is { } sub && sub != "new"))
        {
            Console.Error.WriteLine(
                "gatto model takes no argument. Run gatto model, press m to list the models on this "
                + "machine, and type the path to your .gguf there.");
            Console.Error.WriteLine();
            return Task.FromResult(2);
        }

        //read ctx?.Interactive rather than the console properties, which no test can inject. the first-run door reads the same expression, so the two cannot disagree
        if (!(ctx?.Interactive ?? (!Console.IsInputRedirected && !Console.IsOutputRedirected)))
        {
            Console.Error.WriteLine(
                "gatto model needs a terminal it can ask questions in. Run it directly rather than "
                + "through a pipe or a script. To see the models you have, use /model inside a session.");
            Console.Error.WriteLine();
            return Task.FromResult(2);
        }

        var home = ctx?.Home ?? GattoHome.Resolve();

        //both arms, NotConfigured never reads llama_server and the segment this enters skips the engine check
        if (FirstRunDoor.NotConfigured(home) || NoEngineIn(home))
        {
            Console.Error.WriteLine(
                "gatto model sets up a model on a machine that already has an engine. This one "
                + "doesn't yet. Run gatto setup first.");
            Console.Error.WriteLine();
            return Task.FromResult(2);
        }

        //from here the command is the wizard at the model segment, with no EnsureInitialized, since opening it must not create a home
        return Task.FromResult(RunSetup(modelSegmentOnly: true, command: ModelInvocation(args.Subcommand)).Code);   //no banner either, the alt buffer hides it and the restore would put it above the wizard's closing record
    }

    //the words that reached the wizard, kept for its header and title so new is not dropped
    internal static string ModelInvocation(string? subcommand) =>
        subcommand == "new" ? "gatto model new" : "gatto model";
}
