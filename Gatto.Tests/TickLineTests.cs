using Gatto.Cli;

namespace Gatto.Tests;

//the tick line never commits a line to scrollback and its erase measures display columns, so an extraction that loses either property must fail here
public class TickLineTests
{
    [Fact]
    public void On_a_TERMINAL_the_ticks_NEVER_COMMIT_A_LINE_to_the_scrollback()
    {
        //a StringWriter keeps every byte a terminal would overwrite, so the newline is the only proof that no line became permanent
        var sw = new StringWriter();
        using var tick = new TickLine(sw, rich: true, windowWidth: () => 80);

        for (var i = 1; i <= 5; i++) tick.Live($"  ({i} of 5) doing a thing…");
        tick.Finish();

        Assert.DoesNotContain("\n", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_ERASE_measures_COLUMNS_and_never_the_painted_bytes()
    {
        //escape bytes occupy no columns, and erasing by the painted length would blank the rows above the tick
        var sw = new StringWriter();
        using var tick = new TickLine(sw, rich: true, paint: s => $"<dim>{s}</dim>", windowWidth: () => 80);

        tick.Live("  working…");
        var painted = sw.ToString();
        tick.Finish();
        var erased = sw.ToString()[painted.Length..];

        Assert.Contains("<dim>", painted, StringComparison.Ordinal);
        Assert.Equal("  working…".Length, erased.Count(c => c == ' '));
    }

    [Fact]
    public void A_line_WIDER_THAN_THE_WINDOW_is_clamped_so_the_erase_cannot_leave_half_a_row_behind()
    {
        //a wrapped tick is two rows and a carriage return only reaches the start of the last one, so the clamp keeps the erase whole
        var sw = new StringWriter();
        using var tick = new TickLine(sw, rich: true, windowWidth: () => 20);

        tick.Live(new string('x', 200));

        Assert.True(sw.ToString().Length <= 19, $"line was {sw.ToString().Length} columns wide");
    }

    [Fact]
    public void A_NOTE_survives_the_erase_because_the_user_needs_to_keep_it()
    {
        var sw = new StringWriter();
        using var tick = new TickLine(sw, rich: true, windowWidth: () => 80);

        tick.Live("  working…");
        tick.Note("  started the server");

        var lines = sw.ToString().Split('\n');
        Assert.Contains(lines, l => l.Contains("started the server", StringComparison.Ordinal));
    }

    [Fact]
    public void A_note_ERASES_the_live_line_first_so_the_two_never_collide_on_one_row()
    {
        var sw = new StringWriter();
        using var tick = new TickLine(sw, rich: true, windowWidth: () => 80);

        tick.Live("  working…");
        tick.Note("keepme");

        var text = sw.ToString();
        Assert.DoesNotContain("working…keepme", text, StringComparison.Ordinal);
        Assert.Contains("\r", text[..text.IndexOf("keepme", StringComparison.Ordinal)], StringComparison.Ordinal);
    }

    [Fact]
    public void In_PLAIN_mode_a_tick_writes_NOTHING_and_never_emits_a_carriage_return()
    {
        //the caller decides what stands in for a tick in plain mode, this type only promises not to litter a log with in-place rewrites
        var sw = new StringWriter();
        using var tick = new TickLine(sw, rich: false);

        tick.Live("  working…");
        tick.Live("  still working…");
        tick.Finish();

        Assert.Equal("", sw.ToString());
    }

    [Fact]
    public void In_plain_mode_a_NOTE_is_still_a_committed_line()
    {
        var sw = new StringWriter();
        using var tick = new TickLine(sw, rich: false);

        tick.Note("  started the server");

        Assert.Equal("  started the server", sw.ToString().TrimEnd('\r', '\n'));
        Assert.DoesNotContain("\r", sw.ToString().Replace("\r\n", "\n", StringComparison.Ordinal),
            StringComparison.Ordinal);
    }

    [Fact]
    public void DISPOSE_takes_the_line_down_even_when_nobody_called_Finish()
    {
        //an exception between the last tick and the next output would otherwise leave half a progress line above the error
        var sw = new StringWriter();
        var tick = new TickLine(sw, rich: true, windowWidth: () => 80);
        tick.Live("  working…");
        tick.Dispose();

        Assert.EndsWith("\r", sw.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void FINISH_is_idempotent_so_a_caller_may_always_call_it_before_writing()
    {
        var sw = new StringWriter();
        using var tick = new TickLine(sw, rich: true, windowWidth: () => 80);

        tick.Live("  working…");
        tick.Finish();
        var afterFirst = sw.ToString();
        tick.Finish();
        tick.Finish();

        Assert.Equal(afterFirst, sw.ToString());   //nothing more was emitted
    }

    [Fact]
    public void A_LEAD_TICK_OPENS_WITH_ONE_BLANK_LINE_BEFORE_ITS_FIRST_LIVE_PAINT()
    {
        var sw = new StringWriter();
        using var tick = new TickLine(sw, rich: true, windowWidth: () => 80, blankBeforeFirst: true);

        tick.Live("  working…");
        tick.Live("  still working…");

        Assert.StartsWith(Environment.NewLine, sw.ToString(), StringComparison.Ordinal);
        Assert.False(sw.ToString().StartsWith(Environment.NewLine + Environment.NewLine, StringComparison.Ordinal));
    }

    [Fact]
    public void A_LEAD_TICK_IN_PLAIN_MODE_OPENS_WITH_ONE_BLANK_LINE_BEFORE_ITS_FIRST_NOTE()
    {
        var sw = new StringWriter();
        using var tick = new TickLine(sw, rich: false, blankBeforeFirst: true);

        tick.Note("started the server");

        Assert.Equal(Environment.NewLine + "started the server" + Environment.NewLine, sw.ToString());
    }

    [Fact]
    public void WITHOUT_THE_FLAG_NO_LEADING_BLANK_LINE_APPEARS()
    {
        var sw = new StringWriter();
        using var tick = new TickLine(sw, rich: true, windowWidth: () => 80);

        tick.Live("  working…");

        Assert.False(sw.ToString().StartsWith(Environment.NewLine, StringComparison.Ordinal));
    }

    [Fact]
    public void The_caller_can_ASK_whether_motion_is_visible_so_it_can_choose_a_plain_fallback()
    {
        //the caller asks this to pick its policy, a terminal updates the live line and a log commits one line per task
        Assert.True(new TickLine(new StringWriter(), rich: true).Rich);
        Assert.False(new TickLine(new StringWriter(), rich: false).Rich);
    }
}
