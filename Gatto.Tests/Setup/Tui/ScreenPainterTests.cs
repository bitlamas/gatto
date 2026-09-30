using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//turn the frame primitives into a screen here, since this file is the only place that knows their order
public class ScreenPainterTests
{

    //the painter's rows as plain text, for tests about which row sits where and that none exceeds the width. a row's ink is checked in TuiWizardSurfaceTests
    private static IReadOnlyList<string> PaintPlain(Screen s, int width, string version, string build) =>
        [.. ScreenPainter.Paint(s, width, version, build, glyphs: GlyphSet.Unicode).Select(r => r.Text)];

    private static Screen Consent(string? armed = null) => new(
        [new("machine", StripState.Done), new("engine", StripState.Current),
         new("model", StripState.Pending), new("check", StripState.Pending), new("done", StripState.Pending)],
        Region.List,
        "Which engine build fits this machine?",
        ["  fetch        llama-b10076-bin-win-vulkan-x64.zip · 214 MB"],
        new DoorRow("type the path to llama-server.exe…"),
        [new("Tab", "next area"), new("Enter", "next"), new("Esc", "leave")],
        Armed: armed);

    //the header names the command that ran, gatto model enters the same flow and must name itself
    [Theory]
    [InlineData("gatto setup")]
    [InlineData("gatto model")]
    public void THE_HEADER_NAMES_THE_COMMAND_THAT_RAN(string command)
    {
        var rows = ScreenPainter.Paint(Consent(), 100, "0.5.0", "1a2b3c4",
            glyphs: GlyphSet.Unicode, command: command).Select(r => r.Text).ToList();

        Assert.StartsWith("  " + command, rows[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_screen_is_header_strip_rule_title_body_door_rule_footer()
    {
        var rows = PaintPlain(Consent(), 100, "0.5.0", "1a2b3c4");

        Assert.StartsWith("  gatto setup", rows[0], StringComparison.Ordinal);
        Assert.EndsWith("v0.5.0 · build 1a2b3c4", rows[0], StringComparison.Ordinal);
        Assert.Equal("  machine ✓ · ❯ engine · model · check · done", rows[1]);
        Assert.Equal(new string('─', 100), rows[2]);
        Assert.Equal("  Which engine build fits this machine?", rows[3]);
        Assert.Equal(new string('─', 100), rows[^2]);
        Assert.StartsWith("  Tab next area", rows[^1], StringComparison.Ordinal);
        Assert.Contains(rows, r => r.Contains("type the path to llama-server.exe…", StringComparison.Ordinal));
    }

    //no row may be wider than the terminal, a wide composer would otherwise break the one-row rule
    [Fact]
    public void NO_row_ever_exceeds_the_width_across_the_sweep()
    {
        for (var w = 78; w <= 120; w++)
            foreach (var rows in new[] { PaintPlain(Consent(), w, "0.5.0", "1a2b3c4"),
                                         PaintPlain(Consent("Esc again: stops the fetch"), w, "0.5.0", "1a2b3c4") })
                foreach (var r in rows)
                    Assert.True(Gatto.Terminal.UnicodeWidth.Of(r) <= w,
                        $"w={w}: a row is {Gatto.Terminal.UnicodeWidth.Of(r)} cells: {r}");
    }

    //a body row wider than the terminal, so the sweep can't pass on a short fixture. the clamp cuts it to the width instead of wrapping it
    [Fact]
    public void An_over_long_BODY_row_is_clamped_rather_than_wrapped()
    {
        var s = Consent() with { Body = [new string('x', 300)] };
        var rows = PaintPlain(s, 100, "0.5.0", "1a2b3c4");
        Assert.All(rows, r => Assert.True(Gatto.Terminal.UnicodeWidth.Of(r) <= 100));
        Assert.Contains(rows, r => r.Length == 100 && r.All(c => c == 'x'));
    }

    //the armed warning replaces the footer keys row rather than joining it
    [Fact]
    public void The_armed_row_replaces_the_footer_keys()
    {
        var rows = PaintPlain(Consent("Esc again: stops the fetch and deletes the 7.9 GB already here"),
            100, "0.5.0", "1a2b3c4");
        Assert.Equal("  Esc again: stops the fetch and deletes the 7.9 GB already here", rows[^1]);
        Assert.DoesNotContain("Tab next area", rows[^1], StringComparison.Ordinal);
    }

    //one cursor per screen, the strip never draws its own and the current section shows it
    [Fact]
    public void The_strip_shows_its_own_cursor_only_when_it_HAS_the_keys()
    {
        var inList = PaintPlain(Consent(), 100, "0.5.0", "1a2b3c4")[1];
        var inStrip = PaintPlain(Consent() with { Focused = Region.Strip }, 100, "0.5.0", "1a2b3c4")[1];
        Assert.Equal(inList, inStrip);      //the current section shows the cursor either way here.
        Assert.Equal(1, inList.Split("❯").Length - 1);
    }

    [Fact]
    public void A_screen_with_no_door_simply_has_no_door_row()
    {
        var rows = PaintPlain(Consent() with { Door = null }, 100, "0.5.0", "1a2b3c4");
        Assert.DoesNotContain(rows, r => r.Contains('❯') && r.Contains("type the path", StringComparison.Ordinal));
    }
}
