using Gatto.Cli.Setup;

namespace Gatto.Tests.Setup;

//the row split is its own rule, so it is testable without a real server
public class TranscriptRowsTests
{
    [Fact]
    public void A_MULTI_LINE_LOG_BECOMES_ONE_ROW_PER_LINE()
    {
        //the whole log tail arrives in one call, the shape the probe sees
        var log = "loading model 'x.gguf'\nerror loading model: incomplete\nexiting due to error";

        var rows = LiveSetupProbes.TranscriptRows(log);

        Assert.Equal(3, rows.Length);
        Assert.All(rows, r => Assert.StartsWith("  ", r, StringComparison.Ordinal));
        //a row may hold no line break, or the terminal decides where the text appears
        Assert.All(rows, r => Assert.DoesNotContain('\n', r));
        Assert.Contains("exiting due to error", rows[2], StringComparison.Ordinal);
    }

    [Fact]
    public void WINDOWS_LINE_ENDINGS_do_not_leave_a_stray_carriage_return()
    {
        //splitting on \n alone would leave a \r from a child process on Windows, so assert the trim
        var rows = LiveSetupProbes.TranscriptRows("first\r\nsecond");

        Assert.Equal(["  first", "  second"], rows);
    }

    [Fact]
    public void EVERY_ROW_IS_STILL_SANITIZED_so_a_server_s_output_cannot_drive_the_terminal()
    {
        //the split comes first and every row is sanitized after it, so the filter can't be skipped
        var esc = Convert.ToChar(0x1B);
        var rows = LiveSetupProbes.TranscriptRows($"plain\n{esc}[2Jwiped");

        Assert.Equal(2, rows.Length);
        Assert.DoesNotContain(esc, rows[1]);
    }

    [Fact]
    public void A_SINGLE_LINE_IS_UNCHANGED_by_the_split()
    {
        //a single line is the simplest case, pinned so a change to the split can't regress it
        Assert.Equal(["  starting qwen…"], LiveSetupProbes.TranscriptRows("starting qwen…"));
    }
}
