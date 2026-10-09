using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//the wizard owns the screen for the length of a run. the restore call is idempotent, so its finally can race a ProcessExit handler
internal static class WizardSession
{
    //run walk in the alt buffer, restore on every exit, then print the last frame to scrollback. read the frame after the restore, so a throw prints it
    public static int Run(
        AltScreen alt, Func<int> walk, Func<IReadOnlyList<string>> lastFrame, TextWriter epilogue,
        Func<IReadOnlyList<string>>? notes = null, bool registerHooks = true,
        Func<bool?>? take = null, Action<bool?>? giveBack = null,
        string command = Tui.ScreenPainter.DefaultCommand, ConsoleInputMode? inputMode = null)
    {
        if (registerHooks)
        {
            alt.RegisterExitHooks();
            inputMode?.RegisterExitHooks();
        }
        //the takeover hides the cursor and the Restore in the finally brings it back, on the throw path too
        alt.Enter(command, hideCursor: true);   //pass the same command the header shows, so the tab and the header read alike
        //the input mode and the Ctrl+C take both write the processed-input bit, so they nest and the second in leaves first
        inputMode?.Enter();
        var ctrlC = (take ?? TakeControlC)();
        try
        {
            return walk();
        }
        finally
        {
            (giveBack ?? GiveBackControlC)(ctrlC);
            inputMode?.Restore();
            alt.Restore();

            //print the notes first, then the closing frame, one empty line on each side. each of them ends with an empty row, so drop those blank rows at the edges
            var rows = new List<string>(notes?.Invoke() ?? []);
            rows.AddRange(lastFrame());
            var first = rows.FindIndex(r => !string.IsNullOrWhiteSpace(r));
            if (first >= 0)
            {
                var last = rows.FindLastIndex(r => !string.IsNullOrWhiteSpace(r));
                epilogue.WriteLine();
                for (var i = first; i <= last; i++) epilogue.WriteLine(rows[i]);
                epilogue.WriteLine();
            }
        }
    }

    //what a run leaves in scrollback: gatto model's line, setup's record, the leave block, or the last frame on a throw
    internal static IReadOnlyList<string> Scrollback(SetupFlow flow, Func<IReadOnlyList<string>> lastPainted,
        int width, GlyphSet glyphs, VersionStamp stamp, DateOnly on, string command, Theme? theme) =>
        flow.AddedClosings is { Count: > 0 } added
            ? Tui.Epilogue.LeaveBlock(glyphs, stamp.Version, stamp.Build, command, added, width, stamp.Dev, theme)
        : flow.RecordRows() is { Count: > 0 } facts
            ? Tui.Epilogue.Lines(facts, stamp.Version, stamp.Build, on, flow.RecordClosing, width, glyphs,
                flow.LeftAt, command, stamp.Dev, theme)
            : flow.Left
                ? Tui.Epilogue.LeaveBlock(glyphs, stamp.Version, stamp.Build, command, flow.PrintedLeave,
                    width, stamp.Dev, theme)
                : lastPainted();

    //taking Ctrl+C as input lets one rule cover both presses, and null means no console. don't swap in a CancelKeyPress handler, it never unblocks the read
    private static bool? TakeControlC()
    {
        try
        {
            var was = Console.TreatControlCAsInput;
            Console.TreatControlCAsInput = true;
            return was;
        }
        catch (IOException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
    }

    //put Ctrl+C back the way the process had it. it must not throw, a throw on the crash path would replace the real failure
    private static void GiveBackControlC(bool? was)
    {
        if (was is not { } value) return;
        try { Console.TreatControlCAsInput = value; }
        catch (IOException) { }
        catch (PlatformNotSupportedException) { }
    }
}
