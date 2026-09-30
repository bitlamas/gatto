using Gatto.Cli;
using Gatto.Roles.Audition;

namespace Gatto.Tests.Audition;

//on a terminal the ticks rewrite one line and get erased, only the report reaches the scrollback
public class AuditionConsoleTests
{
    private static AuditionProgress Started(int i) =>
        new(AuditionStage.Started, TaskId: $"B{i}", Label: "run a command", Index: i, Total: 5);

    private static AuditionProgress Done(int i, bool pass = true) =>
        new(AuditionStage.Done, TaskId: $"B{i}", Label: "run a command", Index: i, Total: 5, Pass: pass);

    [Fact]
    public void On_a_TERMINAL_the_task_ticks_NEVER_REACH_THE_SCROLLBACK()
    {
        //a StringWriter keeps every byte, so check for a newline (one would make the tick line permanent)
        var sw = new StringWriter();
        using var console = new AuditionConsole(sw, rich: true, windowWidth: () => 80);

        for (var i = 1; i <= 5; i++) { console.Report(Started(i)); console.Report(Done(i)); }
        console.Finish();

        Assert.DoesNotContain("\n", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_live_line_is_ERASED_by_Finish_so_the_report_starts_on_a_clean_row()
    {
        var sw = new StringWriter();
        using var console = new AuditionConsole(sw, rich: true, windowWidth: () => 80);

        console.Report(Started(3));
        var beforeFinish = sw.ToString();
        console.Finish();
        var erased = sw.ToString()[beforeFinish.Length..];

        //the erase is a carriage return, blanks that cover the written text and a second carriage return.
        Assert.StartsWith("\r", erased, StringComparison.Ordinal);
        Assert.EndsWith("\r", erased, StringComparison.Ordinal);
        Assert.True(erased.Count(c => c == ' ') >= beforeFinish.TrimStart().Length,
            "the erase must be at least as wide as the line it covers");
    }

    [Fact]
    public void A_NOTE_survives_the_erase_because_the_user_needs_to_keep_it()
    {
        //a note reports something the user keeps, so the erase must leave it on screen
        var sw = new StringWriter();
        using var console = new AuditionConsole(sw, rich: true, windowWidth: () => 80);

        console.Report(Started(1));
        console.Report(AuditionProgress.Note("starting qwen3.6-35b-a3b…"));

        var lines = sw.ToString().Split('\n');
        Assert.Contains(lines, l => l.Contains("starting qwen3.6-35b-a3b", StringComparison.Ordinal));
    }

    [Fact]
    public void A_note_ERASES_the_live_line_first_so_the_two_never_collide_on_one_row()
    {
        var sw = new StringWriter();
        using var console = new AuditionConsole(sw, rich: true, windowWidth: () => 80);

        console.Report(Started(2));
        console.Report(AuditionProgress.Note("using the server already running for laguna"));

        //the note starts its own row after a carriage return, so it never joins the text of the tick
        var text = sw.ToString();
        Assert.DoesNotContain("(2 of 5) run a command…using", text, StringComparison.Ordinal);
        Assert.Contains("\r", text[..text.IndexOf("using", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void PIPED_output_keeps_one_LINE_per_task_and_never_writes_a_carriage_return()
    {
        //redirected output is a log, so it must hold no carriage returns (the live release check reads it too)
        var sw = new StringWriter();
        using var console = new AuditionConsole(sw, rich: false);

        for (var i = 1; i <= 3; i++) { console.Report(Started(i)); console.Report(Done(i, pass: i != 2)); }
        console.Finish();

        var lines = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);                                    //a Started event writes no line

        //normalize line endings first, only a bare carriage return moves the cursor
        Assert.DoesNotContain("\r", sw.ToString().Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
        Assert.Contains("✓ B1", lines[0], StringComparison.Ordinal);
        Assert.Contains("✗ B2", lines[1], StringComparison.Ordinal);
        Assert.Contains("run a command", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_WIDER_THAN_THE_WINDOW_is_clamped_so_the_erase_cannot_leave_half_a_row_behind()
    {
        //a wrapped line leaves its first row behind, a carriage return only reaches the start of the last row
        var sw = new StringWriter();
        using var console = new AuditionConsole(sw, rich: true, windowWidth: () => 20);

        console.Report(new AuditionProgress(AuditionStage.Started, TaskId: "B4",
            Label: new string('x', 200), Index: 4, Total: 5));

        Assert.True(sw.ToString().Length <= 19, $"line was {sw.ToString().Length} columns wide");
    }

    [Fact]
    public void DISPOSE_takes_the_line_down_even_when_nobody_called_Finish()
    {
        //an exception between the last tick and the report must not leave half a progress line above the error message.
        var sw = new StringWriter();
        var console = new AuditionConsole(sw, rich: true, windowWidth: () => 80);
        console.Report(Started(1));
        console.Dispose();

        Assert.EndsWith("\r", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_PAINTER_is_used_when_one_is_supplied_and_the_erase_still_measures_COLUMNS()
    {
        //erase by the width of the plain text, the painted string is longer and would blank the rows above
        var sw = new StringWriter();
        using var console = new AuditionConsole(sw, rich: true,
            paint: (s, ink) => $"<{ink}>{s}</{ink}>", windowWidth: () => 80);

        console.Report(Started(1));
        var painted = sw.ToString();
        console.Finish();
        var erased = sw.ToString()[painted.Length..];

        Assert.Contains("<Dim>", painted, StringComparison.Ordinal);
        Assert.Equal("  (1 of 5) run a command…".Length, erased.Count(c => c == ' '));
    }
}
