using Gatto.Cli;
using Gatto.Terminal;

namespace Gatto.Tests;

//every fact on these lines comes from the probe, the reader would act on a guessed one
public class ServeNoticeTests
{
    //drive the line through SayUsing, its only exit, so the test asserts the shape production can write
    private static string Using((string, CliInk)[] line, bool blank = true)
    {
        var w = new StringWriter();
        ServeNotice.SayUsing(new CliSurface(w, null, glyphs: GlyphSet.Unicode), GlyphSet.Unicode, line, blank);
        var text = w.ToString().Replace("\r\n", "\n");
        var rows = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        //one row only, the header glyph opens it and the sentence under test is the rest
        Assert.Single(rows);
        Assert.StartsWith(GlyphSet.Unicode.Header + " ", rows[0], StringComparison.Ordinal);
        Assert.Equal(blank, text.StartsWith('\n'));
        return rows[0][(GlyphSet.Unicode.Header.Length + 1)..];
    }

    [Fact]
    public void THE_ARMED_MODEL_IS_THE_SERVED_ONE() =>
        Assert.Equal("using qwen3.5-9b, served on :1238", Using(ServeNotice.Using("qwen3.5-9b", 1238)));

    //the session paints its own row, so it takes the words with no ink
    [Fact]
    public void THE_SESSION_ROW_IS_THE_SAME_WORDS() =>
        Assert.Equal("using qwen3.5-9b, served on :1238", ServeNotice.Text(ServeNotice.Using("qwen3.5-9b", 1238)));

    [Fact]
    public void A_FILE_NO_SHELF_MODEL_CLAIMS_KEEPS_THE_DEFAULT_AND_NAMES_THE_FILE() =>
        Assert.Equal("using qwen3.6-35b-a3b, the server on :1238 holds mystery-Q4_K_M, not on the shelf",
            Using(ServeNotice.UsingOffShelf("qwen3.6-35b-a3b", 1238, "mystery-Q4_K_M")));

    [Fact]
    public void A_SHELF_MODEL_ON_ANOTHER_PORT_KEEPS_THE_DEFAULT_AND_NAMES_THE_MODEL() =>
        Assert.Equal("using qwen3.6-35b-a3b, the server on :1238 holds qwen3.5-9b",
            Using(ServeNotice.UsingOtherPort("qwen3.6-35b-a3b", 1238, "qwen3.5-9b")));

    [Fact]
    public void A_SERVER_THAT_NAMES_NO_FILE_IS_NOT_SAID_TO_SERVE_THE_DEFAULT() =>
        Assert.Equal("using qwen3.6-35b-a3b, the server on :1238 does not name its file",
            Using(ServeNotice.UsingNoFile("qwen3.6-35b-a3b", 1238)));

    //a one-shot writes it to stderr beside its other launch lines, which open with no blank row
    [Fact]
    public void UNDER_P_THE_LINE_HAS_NO_BLANK_ROW_ABOVE_IT() =>
        Assert.Equal("using qwen3.5-9b, served on :1238", Using(ServeNotice.Using("qwen3.5-9b", 1238), blank: false));

    [Fact]
    public void A_SIZE_THAT_ROUNDS_TO_ZERO_SAYS_SO_rather_than_reading_as_nothing_loaded()
    {
        //a size that rounds to zero must read <0.1 GB, since ~0 GB claims nothing is loaded
        Assert.Contains("<0.1 GB", ServeNotice.ExitHint(true, 40_000_000, GlyphSet.Unicode)!.Text, StringComparison.Ordinal);
        //a real size still reads with its normal figure, so the zero guard did not change every size.
        Assert.Contains("~25.1 GB", ServeNotice.ExitHint(true, 27_000_000_000, GlyphSet.Unicode)!.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_exit_hint_says_what_is_still_held_and_how_to_release_it()
    {
        var hint = ServeNotice.ExitHint(serverRunning: true, sizeBytes: 27_000_000_000, GlyphSet.Unicode);

        //a fact row ends bare, the two parts meet at one separator
        Assert.Equal("model still loaded (~25.1 GB) · gatto serve stop to eject", hint!.Text);
    }

    [Fact]
    public void NO_SERVER_NO_EXIT_HINT()
    {
        //with nothing running the hint is noise, and noise on every exit is how a real notice stops being read.
        Assert.Null(ServeNotice.ExitHint(serverRunning: false, sizeBytes: 27_000_000_000, GlyphSet.Unicode));
    }

    [Fact]
    public void The_exit_hint_survives_an_unmeasurable_file_without_inventing_a_size()
    {
        Assert.Equal("model still loaded · gatto serve stop to eject",
            ServeNotice.ExitHint(serverRunning: true, sizeBytes: null, GlyphSet.Unicode)!.Text);
    }

    //each stop outcome names the reason that is true of it. a reader is never told the record is absent when the record is stale.
    [Theory]
    [InlineData("Stopped", "qwen3.6-35b", true, "llama-server stopped · qwen3.6-35b unloaded")]
    [InlineData("Stopped", "qwen3.6-35b", false, "llama-server stopped · qwen3.6-35b unloaded")]
    [InlineData("Stopped", Gatto.Roles.Serve.ServeManager.UnknownModelId, true, "llama-server stopped")]
    [InlineData("NotServing", null, true,
        "gatto won't close a server gatto didn't start.")]
    [InlineData("Stale", "p", true,
        "gatto's server already stopped on its own.")]
    [InlineData("NotOurs", "p", true,
        "that pid isn't gatto's server anymore, so gatto is leaving it alone.")]
    public void The_line_after_a_quit_names_only_the_stop_that_happened(string result, string? model, bool hadServer, string expected)
    {
        var line = ServeLines.AtExit.AfterStop(new(Enum.Parse<Gatto.Roles.Serve.ServeManager.StopResult>(result), model, 6000, null), hadServer, GlyphSet.Unicode);
        Assert.NotNull(line);
        Assert.Equal(expected, line.Text);
        Assert.False(line.WentWrong);
    }

    //a session that never had a server hears nothing when nothing stopped, a line on every exit stops being read
    [Theory]
    [InlineData("NotServing")]
    [InlineData("Stale")]
    [InlineData("NotOurs")]
    public void No_line_when_the_session_had_no_server_and_nothing_stopped(string result) =>
        Assert.Null(ServeLines.AtExit.AfterStop(new(Enum.Parse<Gatto.Roles.Serve.ServeManager.StopResult>(result), "p", 6000, null), sessionHadServer: false, GlyphSet.Unicode));

    [Fact]
    public void A_failed_kill_went_wrong_and_names_the_pid_and_the_way_out()
    {
        var line = ServeLines.AtExit.AfterStop(new(Gatto.Roles.Serve.ServeManager.StopResult.KillFailed, "p", 6000, "Access is denied."), sessionHadServer: false, GlyphSet.Unicode);
        Assert.NotNull(line);
        Assert.True(line.WentWrong);
        Assert.Equal("could not stop llama-server, pid 6000: Access is denied. Stop it with gatto serve stop, or end it in Task Manager.", line.Text);
    }

    //the session's line for a dead server names the code once, then status for more and start to bring it back
    [Fact]
    public void The_gone_line_names_the_code_and_both_commands()
    {
        var line = ServeLines.GoneLine(new("qwen3.6-35b-a3b", 4276, 1, null), GlyphSet.Unicode);
        Assert.Equal("llama-server for qwen3.6-35b-a3b is not running (exited with code 1) · gatto serve status for more info, "
            + "and gatto serve start qwen3.6-35b-a3b brings it back", line);
        Assert.Null(ServeLines.GoneLine(null, GlyphSet.Unicode));
    }

    //the refusal names the served model, both ways out, and says the one-server limit is temporary
    [Fact]
    public void The_refusal_names_the_served_model_and_both_ways_out()
    {
        var said = ServeNotice.Refusing(@"D:\models\qwen3.6-35b-a3b-Q4_K_M.gguf");

        Assert.Contains("qwen3.6-35b-a3b-Q4_K_M", said, StringComparison.Ordinal);
        Assert.Contains("gatto serve stop", said, StringComparison.Ordinal);
        Assert.Contains("without -m", said, StringComparison.Ordinal);
        Assert.Contains("comes later", said, StringComparison.Ordinal);
    }

    //a refusal with an unknown served file still reads as a whole sentence, saying less rather than nothing
    [Fact]
    public void An_unnamed_served_file_still_gets_a_whole_sentence()
    {
        var said = ServeNotice.Refusing(null);

        Assert.Contains("another model", said, StringComparison.Ordinal);
        Assert.Contains("gatto serve stop", said, StringComparison.Ordinal);
        Assert.DoesNotContain("  ", said, StringComparison.Ordinal);
    }

    //the refusal holds no port number, no timing constant and no em dash, and each pattern first matches a known string
    [Fact]
    public void The_refusal_carries_no_port_no_timing_constant_and_no_em_dash()
    {
        var said = ServeNotice.Refusing(@"D:\models\qwen-Q4_K_M.gguf");
        const string planted = "port 1235, about 30 seconds, and an em dash \u2014 here";

        foreach (var (pattern, name) in new[]
                 { ("1235", "a port"), ("second", "a timing constant"), ("\u2014", "an em dash") })
        {
            Assert.Contains(pattern, planted, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(pattern, said, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain("minute", said, StringComparison.OrdinalIgnoreCase);
    }

}
