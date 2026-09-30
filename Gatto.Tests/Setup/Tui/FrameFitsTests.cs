using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Gatto.Tests.Setup;

namespace Gatto.Tests.Setup.Tui;

//a frame must fit the terminal it is drawn on, at 30 rows and 24. the machine frame already fitted, so the rows guard the fit rather than the tallest frame
public class FrameFitsTests
{
    //the widths gatto claims to serve, so the property is asserted where the product makes a promise
    public static TheoryData<int, int> Rungs()
    {
        var data = new TheoryData<int, int>();
        foreach (var height in new[] { 30, 24 })
            foreach (var width in new[] { 52, 72, 80, 120 })
                data.Add(height, width);
        return data;
    }

    //the machine screen as a walk reaches it, at one terminal size
    private static IReadOnlyList<string> MachineFrame(int height, int width)
    {
        var rig = new WizardRig(width);
        rig.Surface.Height = height;
        var home = Directory.CreateTempSubdirectory("gatto-fits-").FullName;
        try
        {
            Gatto.Core.Home.GattoHome.EnsureInitialized(home);
            SetupRunner.Run(new SetupFlow(new WizardProbes()),
                rig.TuiFace([.. WalkOpening.Keys, WizardRig.Esc, WizardRig.Esc]), home);
            //the welcome paints first and the machine section second, so index 1 is the frame wanted. a search for its words would pass on a frame that only mentions them
            Assert.True(rig.PaintedFrames.Count > 1, "the walk did not reach the machine section");
            return rig.PaintedFrames[1];
        }
        finally { try { Directory.Delete(home, true); } catch (Exception) { } }
    }

    //the product promise: it fits at both heights and at every width gatto claims to serve
    [Theory]
    [MemberData(nameof(Rungs))]
    public void THE_MACHINE_SCREEN_FITS_THE_TERMINAL(int height, int width)
    {
        var frame = MachineFrame(height, width);

        Assert.True(frame.Count <= height,
            $"the machine screen is {frame.Count} rows on a {width}x{height} terminal, so "
            + $"{frame.Count - height} row(s) scroll the header and the strip off the top:\n"
            + string.Join("\n", frame));
    }

    //a known match for the rows above, since a four-row screen would satisfy every fit assertion and prove nothing
    [Fact]
    public void AND_THE_FRAME_IS_A_REAL_SCREEN_NOT_A_STUB()
    {
        var frame = MachineFrame(30, 100);

        Assert.True(frame.Count > 10, $"the machine screen is only {frame.Count} rows");
        Assert.Contains(frame, r => r.Contains("What can this machine run?", StringComparison.Ordinal));
        Assert.Contains(frame, r => r.Contains("memory", StringComparison.Ordinal));
    }

    //below the widths gatto serves, the prose wraps enough to outgrow the screen, so the fit drops blank separators and keeps every fact row
    [Fact]
    public void A_FRAME_TOO_TALL_LOSES_ITS_BLANKS_AND_KEEPS_EVERY_OTHER_ROW()
    {
        //the same screen twice, at a height where the fit must fire and one where it cannot
        var fitted = MachineFrame(30, 30);
        var whole = MachineFrame(200, 30);

        Assert.True(fitted.Count < whole.Count,
            $"the fitted frame is {fitted.Count} rows against the whole one's {whole.Count}, so "
            + "nothing was dropped and the rule did not fire");

        //the non-blank rows must be identical, since dropping a blank cannot change that sequence and dropping anything else must
        Assert.Equal(
            whole.Where(r => r.Trim().Length > 0),
            fitted.Where(r => r.Trim().Length > 0));
    }

    //the head bound

    private sealed class NoKeys : Gatto.Terminal.IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => new('\u001b', ConsoleKey.Escape, false, false, false);
    }

    //the head bound can never bind now, since every frame opens header, strip and rule, so the guard sits on the chrome rows below

    //every chrome row is RowFit.Fixed, which makes Fit's Head and Tail statements about the frame, so the test reads each row's class
    [Fact]
    public void THE_CHROME_ROWS_ARE_FIXED_SO_THE_BOUNDS_HAVE_NOTHING_TO_DO()
    {
        //a screen with a door and a body, so the frame has rows between the chrome and index 2 differs from index [^2]
        var rows = ScreenPainter.Paint(new Screen(
            [new("machine", StripState.Done), new("engine", StripState.Current),
             new("model", StripState.Pending), new("check", StripState.Pending),
             new("done", StripState.Pending)],
            Region.List,
            "Which engine build fits this machine?",
            ["  fetch        llama-b10076-bin-win-vulkan-x64.zip · 214 MB"],
            new DoorRow("type the path to llama-server.exe…"),
            [new("Tab", "next area"), new("Enter", "next"), new("Esc", "leave")]),
            width: 100, "0.5.0", "1a2b3c4", GlyphSet.Unicode);

        Assert.True(rows.Count > 5, "the fixture must have rows between its chrome");

        Assert.Equal(RowFit.Fixed, rows[0].Fit);   //the header
        Assert.Equal(RowFit.Fixed, rows[1].Fit);   //the strip
        Assert.Equal(RowFit.Fixed, rows[2].Fit);   //the rule under it
        Assert.Equal(RowFit.Fixed, rows[^2].Fit);  //the rule above the keys
        Assert.Equal(RowFit.Fixed, rows[^1].Fit);  //the keys row

        //a known match for the rows above: this frame really has droppable rows, so the assertions are about the chrome
        Assert.Contains(rows, r => r.Fit != RowFit.Fixed);
    }
}
