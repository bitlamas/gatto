using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Cli;

//the three consent screens ask through SelectPrompt, and the ask is a seam so a test can drive the logic or draw the screens
internal static class UninstallConsent
{
    //says what leaving means on every screen of the path, rather than the generic word cancel
    internal const string Footer = "Esc to cancel — nothing is removed";

    //one home for this sentence, so every cancel route ends on the same words
    internal const string NothingRemoved = "nothing was removed.";

    //the follow-up has to be possible from Task Manager, so never name gatto serve stop here
    internal static string LeftRunning(Gatto.Roles.RunningInfo running) =>
        $"llama-server for {running.Model} is still running on port {running.Port} — "
        + $"end process {running.Pid} from Task Manager when you want it stopped.";

    //null is Esc, anything else is the index of the chosen row
    internal delegate int? Asker(string question, IReadOnlyList<string> options);

    //screens 1 and 2 here, and null means the user left at either of them
    internal static (bool StopServer, bool DeleteHome)? Ask(
        Asker ask, string home, Gatto.Roles.RunningInfo? running)
    {
        var stop = false;
        if (running is { } live)
        {
            //only ask when a server is running (a question about stopping nothing teaches the user their answer does not matter)
            var answer = ask($"Stop the llama-server gatto started for {live.Model}?",
                ["Stop it now", "Leave it running"]);
            if (answer is null) return null;
            stop = answer == 0;
        }

        var scope = ask($"Also remove your settings, model setups, sessions and memory at {home}?",
            ["Keep them", "Remove them too"]);
        if (scope is null) return null;

        //keep them first, so the default answer keeps the user's records instead of removing them
        return (stop, scope == 1);
    }

    //the No is about the uninstall rather than the files, and the row stays first
    internal static bool Confirm(Asker ask) =>
        ask("Go ahead with the removals above?", ["No, cancel the uninstall", "Yes, remove them"]) == 1;

    //two rows per item, the second indented under the mark column
    internal static void WriteMap(CliSurface cli, IReadOnlyList<OwnedItem> map)
    {
        foreach (var item in map)
        {
            var rows = Uninstall.Rows(item);
            var word = Uninstall.Word(item.Mark);

            //only the mark cell gets the state colour, painting the whole row would spend it on the path too
            cli.Row((word.PadRight(Uninstall.MarkColumn), Ink(item.Mark)),
                    (rows[0][Uninstall.MarkColumn..], Theme.Bright));

            //a second row is always a path someone can check, so nothing else may go in that slot
            if (rows.Count > 1) cli.Row((rows[1], Theme.Dim));
        }
    }

    //one colour per mark, reached through CliSurface's raw overload so CliInk stays free of red and green
    private static RgbColor Ink(UninstallMark mark) => mark switch
    {
        UninstallMark.Removed => Theme.Err,
        UninstallMark.Stopped => Theme.Warn,
        _ => Theme.Ok,
    };

    //the only part of this file that touches a keyboard, so everything above stays data a test can drive

    //the ask draws over a surface and keys somebody else owns so a test renders the screens, and the glyph set is handed in
    internal static Asker Prompter(Theme? theme, ITermSurface surface, IKeySource keys,
        Gatto.Terminal.GlyphSet? glyphs) => (question, options) =>
    {
        var spec = new SelectSpec(
            TitleRows: [],
            Question: new PromptQuestion(question),
            Options: [.. options.Select(o => new SelectOption(o))],
            FooterHint: Footer);

        //no theme means a plain terminal (a redirected run never reaches this far)
        return new SelectPrompt(surface, theme ?? new Theme(TermCaps.Plain),
            keys, pump: null, chrome: null, glyphs: glyphs).Show(spec) switch
        {
            SelectOutcome.Chosen c => c.Index,
            _ => null,
        };
    };
}
