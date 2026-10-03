using Gatto.Cli;
using Gatto.Terminal;

namespace Gatto.Tests;

//a clause appears only when its input did, the reader would act on a guessed one
public class ServeNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);
    private static string Iso(TimeSpan ago) => Now.Subtract(ago).ToString("o");

    //drive the line through SayReusing, its only exit, so the test asserts the shape production can write
    private static string Reusing(string? servedModelPath, string? startedIso, DateTimeOffset now,
        long? sizeBytes)
    {
        var w = new StringWriter();
        ServeNotice.SayReusing(new CliSurface(w, null, glyphs: GlyphSet.Unicode), GlyphSet.Unicode,
            servedModelPath, startedIso, now, sizeBytes);
        var rows = w.ToString().Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        //the header glyph opens the first row, the sentence under test is the rest of it
        Assert.StartsWith(GlyphSet.Unicode.Header + " ", rows[0], StringComparison.Ordinal);
        return string.Join(" | ", rows.Select((r, i) => i == 0 ? r[(GlyphSet.Unicode.Header.Length + 1)..] : r));
    }

    [Fact]
    public void Reusing_names_the_SERVED_model_its_age_and_its_size()
    {
        var line = Reusing(@"D:\models\qwen3.6-35b-Q6_K.gguf", Iso(TimeSpan.FromHours(2)),
            Now, sizeBytes: 27_000_000_000);

        Assert.Equal("reusing llama-server started 2h ago | qwen3.6-35b-Q6_K.gguf · ~25.1 GB", line);
    }

    [Fact]
    public void Reusing_NAMES_WHAT_THE_PROBE_SERVES_not_what_a_model_expects()
    {
        //the serving chip reports when the model's own model_path disagrees with the file the probe serves
        var line = Reusing(@"D:\models\gemma-4-26B.gguf", Iso(TimeSpan.FromMinutes(5)),
            Now, sizeBytes: 20_000_000_000);

        Assert.Contains("gemma-4-26B.gguf", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_SERVER_GATTO_DID_NOT_START_has_no_age_to_report_and_claims_none()
    {
        //a serve.json from another program carries no start time, an age would be a made-up fact
        var line = Reusing(@"D:\m\x.gguf", startedIso: null, Now, sizeBytes: 1_000_000_000);

        Assert.Equal("reusing the llama-server already running | x.gguf · ~0.9 GB", line);
        Assert.DoesNotContain("ago", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AN_UNMEASURABLE_FILE_gets_no_size_clause_rather_than_a_zero()
    {
        var line = Reusing(@"D:\m\x.gguf", Iso(TimeSpan.FromHours(3)), Now, sizeBytes: null);

        Assert.Equal("reusing llama-server started 3h ago | x.gguf", line);
        Assert.DoesNotContain("GB", line, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not a timestamp")]
    [InlineData("")]
    public void A_TIMESTAMP_THAT_WILL_NOT_PARSE_drops_the_clause(string started)
    {
        Assert.DoesNotContain("ago", Reusing(@"D:\m\x.gguf", started, Now, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void A_CLOCK_THAT_WENT_BACKWARDS_does_not_produce_a_negative_age()
    {
        //a start time in the future is a clock change, so no age clause is written
        var line = Reusing(@"D:\m\x.gguf", Now.AddHours(4).ToString("o"), Now, 1);

        //assert the whole line, a check for a minus sign would trip on the hyphen in llama-server
        Assert.Equal("reusing the llama-server already running | x.gguf · <0.1 GB", line);
    }

    [Fact]
    public void A_SIZE_THAT_ROUNDS_TO_ZERO_SAYS_SO_rather_than_reading_as_nothing_loaded()
    {
        //a size that rounds to zero must read <0.1 GB, since ~0 GB claims nothing is loaded
        Assert.Contains("<0.1 GB", Reusing(@"D:\m\x.gguf", null, Now, 1), StringComparison.Ordinal);
        Assert.Contains("<0.1 GB", ServeNotice.ExitHint(true, 40_000_000, GlyphSet.Unicode)!.Text, StringComparison.Ordinal);
        //a real size still reads with its normal figure, so the zero guard did not change every size.
        Assert.Contains("~25.1 GB", ServeNotice.ExitHint(true, 27_000_000_000, GlyphSet.Unicode)!.Text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(30, "moments ago")]
    [InlineData(60 * 7, "7m ago")]
    [InlineData(60 * 60 * 5, "5h ago")]
    [InlineData(60 * 60 * 24 * 3, "3d ago")]
    public void The_age_is_coarse_because_nobody_needs_seconds_to_recognise_their_own_server(
        int secondsAgo, string expected)
    {
        Assert.Contains(expected,
            Reusing(@"D:\m\x.gguf", Iso(TimeSpan.FromSeconds(secondsAgo)), Now, 1),
            StringComparison.Ordinal);
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
    [InlineData("Stopped", Gatto.Roles.ServeManager.UnknownModelId, true, "llama-server stopped")]
    [InlineData("NotServing", null, true,
        "gatto won't close a server gatto didn't start.")]
    [InlineData("Stale", "p", true,
        "gatto's server already stopped on its own.")]
    [InlineData("NotOurs", "p", true,
        "that pid isn't gatto's server anymore, so gatto is leaving it alone.")]
    public void The_line_after_a_quit_names_only_the_stop_that_happened(string result, string? model, bool hadServer, string expected)
    {
        var line = ServeLines.AtExit.AfterStop(new(Enum.Parse<Gatto.Roles.ServeManager.StopResult>(result), model, 6000, null), hadServer, GlyphSet.Unicode);
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
        Assert.Null(ServeLines.AtExit.AfterStop(new(Enum.Parse<Gatto.Roles.ServeManager.StopResult>(result), "p", 6000, null), sessionHadServer: false, GlyphSet.Unicode));

    [Fact]
    public void A_failed_kill_went_wrong_and_names_the_pid_and_the_way_out()
    {
        var line = ServeLines.AtExit.AfterStop(new(Gatto.Roles.ServeManager.StopResult.KillFailed, "p", 6000, "Access is denied."), sessionHadServer: false, GlyphSet.Unicode);
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
