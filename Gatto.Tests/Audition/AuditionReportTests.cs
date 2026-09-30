using System.Text.Json;
using Gatto.Core.Acquire;
using Gatto.Roles.Audition;

namespace Gatto.Tests.Audition;

//the report renders text from the model, and a badge must be impossible without a measurement
public class AuditionReportTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "gatto-badge-" + Guid.NewGuid().ToString("N"));
    public AuditionReportTests() => Directory.CreateDirectory(_home);
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private static AuditionStamp Stamp(string model = "Qwen3.6-35B-A3B-Q4_K_M.gguf") =>
        new("v0.4.0-38-g52d5ea2", model, "Q4_K_M", 8192, "temp 0.7");

    private static AuditionVerdict Verdict(bool pass, bool disqualified = false,
        string model = "Qwen3.6-35B-A3B-Q4_K_M.gguf")
    {
        var tasks = new List<AuditionTaskResult>
        {
            new("B1", pass, pass ? [] : [FailureShape.NoToolCall], TimeSpan.FromSeconds(3), "read a file"),
            new("B2", true, [], TimeSpan.FromSeconds(4), "write a file"),
            new("B3", true, [], TimeSpan.FromSeconds(5), "run a command"),
            new("B4", true, [], TimeSpan.FromSeconds(6), "use two tools in order"),
            new("B5", !disqualified,
                disqualified ? [FailureShape.FabricatedResult] : [], TimeSpan.FromSeconds(7),
                "say so when a file is missing"),
        };
        return new AuditionVerdict(pass && !disqualified, disqualified, tasks, Stamp(model),
            TimeSpan.FromSeconds(25), 28.4);
    }

    private static string Render(AuditionVerdict v) => AuditionReport.Render(v, s => s, Gatto.Roles.EngineMarks.Unicode);

    [Fact]
    public void A_PASS_writes_a_record_that_Lookup_reads_back_as_a_badge()
    {
        BadgeWriter.Write(_home, "org/model", Verdict(pass: true));

        var badge = BadgeRegister.Lookup(_home, "org/model");
        Assert.NotNull(badge);
        Assert.Equal("org/model", badge!.ModelKey);
        Assert.Equal("v0.4.0-38-g52d5ea2", badge.GattoBuild);
        Assert.Equal("temp 0.7", badge.SamplingNote);
    }

    [Fact]
    public void A_FAIL_writes_a_record_but_Lookup_returns_NOTHING()
    {
        //a failed measurement is still written but the lookup must return nothing, or the search row shows "tool-calling verified" for a failed model
        BadgeWriter.Write(_home, "org/bad", Verdict(pass: false));

        Assert.True(File.Exists(Path.Combine(_home, "audition", BadgeRegister.FileNameFor("org/bad"))));
        Assert.Null(BadgeRegister.Lookup(_home, "org/bad"));
    }

    [Fact]
    public void A_DISQUALIFIED_run_is_no_badge_even_though_it_scored_four()
    {
        BadgeWriter.Write(_home, "org/liar", Verdict(pass: true, disqualified: true));
        Assert.Null(BadgeRegister.Lookup(_home, "org/liar"));
    }

    [Fact]
    public void A_fail_record_SAYS_it_failed_rather_than_omitting_the_key()
    {
        //a fail record must state its verdict, a missing key would look like a file from before the verdict field
        BadgeWriter.Write(_home, "org/bad", Verdict(pass: false));
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_home, "audition", BadgeRegister.FileNameFor("org/bad"))));

        Assert.Equal("fail", doc.RootElement.GetProperty("verdict").GetString());
        Assert.False(doc.RootElement.TryGetProperty("gatto_build", out _));   //a fail record holds no badge fields.
    }

    [Fact]
    public void The_record_carries_the_evidence_keys_the_reader_ignores()
    {
        BadgeWriter.Write(_home, "org/model", Verdict(pass: true));
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_home, "audition", BadgeRegister.FileNameFor("org/model"))));
        var root = doc.RootElement;

        //read the schema off BadgeRegister.Schema, a literal here could disagree with the writer
        Assert.Equal(BadgeRegister.Schema, root.GetProperty("schema").GetInt32());
        Assert.Equal(Battery.Version, root.GetProperty("battery_version").GetInt32());
        Assert.Equal(25, root.GetProperty("wall_clock_s").GetDouble(), 1);
        Assert.Equal(28.4, root.GetProperty("decode_tok_s").GetDouble(), 1);
        Assert.Equal(5, root.GetProperty("tasks").GetArrayLength());
        Assert.NotNull(BadgeRegister.Lookup(_home, "org/model"));   //the reader must still accept the record with its evidence keys.
    }

    [Fact]
    public void A_re_audition_REPLACES_the_record_rather_than_accumulating_history()
    {
        //one record per model, staleness shows through the date
        BadgeWriter.Write(_home, "org/model", Verdict(pass: false));
        BadgeWriter.Write(_home, "org/model", Verdict(pass: true));

        Assert.Single(Directory.GetFiles(Path.Combine(_home, "audition")));
        Assert.NotNull(BadgeRegister.Lookup(_home, "org/model"));
    }

    [Fact]
    public void An_absent_decode_rate_is_OMITTED_never_invented()
    {
        var v = Verdict(pass: true) with { DecodeTokS = null };
        BadgeWriter.Write(_home, "org/model", v);
        using var doc = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_home, "audition", BadgeRegister.FileNameFor("org/model"))));

        Assert.False(doc.RootElement.TryGetProperty("decode_tok_s", out _));
    }

    [Fact]
    public void A_PASS_report_carries_the_EXACT_label_and_neither_forbidden_word()
    {
        var text = Render(Verdict(pass: true));

        Assert.Contains("tool-calling verified", text);
        //the label check must stand beside the absence checks, "recommended" would turn a capability check into an endorsement
        Assert.DoesNotContain("recommended", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Recommended", text);
    }

    [Fact]
    public void A_FAIL_report_says_what_went_wrong_IN_WORDS_not_in_enum_names()
    {
        //a new user cannot act on an enum name, so the report must describe each shape in words
        var text = Render(Verdict(pass: false));

        Assert.DoesNotContain("NoToolCall", text);
        Assert.Contains("B1", text);
        Assert.Contains("never called a tool", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_task_that_failed_with_NO_SHAPE_still_says_why_it_failed()
    {
        //a task can fail with no failure shape, so the report text must say why (the shape list is closed and must not grow)
        var tasks = new List<AuditionTaskResult>
        {
            new("B1", true, [], TimeSpan.FromSeconds(7)),
            new("B2", true, [], TimeSpan.FromSeconds(10)),
            new("B3", false, [FailureShape.NoToolCall], TimeSpan.FromSeconds(1.7)),
            new("B4", false, [], TimeSpan.FromSeconds(2.9)),      //a failed task with no failure shape.
            new("B5", true, [], TimeSpan.FromSeconds(5.6)),
        };
        var text = Render(new AuditionVerdict(false, false, tasks, Stamp(), TimeSpan.FromSeconds(27), null));

        //match the row by its glyph, B4 on its own also matches the summary line
        var b4Row = text.Split('\n').Single(l => l.TrimStart().StartsWith("✗ B4", StringComparison.Ordinal));
        Assert.Contains("wrong result", b4Row, StringComparison.OrdinalIgnoreCase);

        //the summary must count every failed task, including the one with no shape
        Assert.Contains("failed 2 of 5", text);
        Assert.Contains("never called a tool (B3)", text);
        Assert.Contains("(B4)", text);
    }

    [Fact]
    public void The_verdict_line_can_NEVER_name_something_no_row_shows()
    {
        //a passing task that trips a shape must show it on its row, or the verdict line names something no row explains
        var tasks = new List<AuditionTaskResult>
        {
            new("B1", true, [], TimeSpan.FromSeconds(7), "read a file"),
            new("B2", true, [FailureShape.RepeatLoop], TimeSpan.FromSeconds(53), "write a file"),  //the task passes but still trips a shape.
            new("B3", true, [], TimeSpan.FromSeconds(10), "run a command"),
            new("B4", true, [], TimeSpan.FromSeconds(9), "use two tools in order"),
            new("B5", true, [], TimeSpan.FromSeconds(5), "say so when a file is missing"),
        };
        var text = Render(new AuditionVerdict(true, false, tasks, Stamp(), TimeSpan.FromSeconds(85), null));

        var b2Row = text.Split('\n').Single(l => l.TrimStart().StartsWith("✓ B2", StringComparison.Ordinal));
        //the wording of each shape is tested in AuditionInWordsTests, this test only checks a passing row still shows the shape
        Assert.Contains("got stuck repeating the same step", b2Row, StringComparison.OrdinalIgnoreCase);

        //collapse runs of spaces before comparing, the check is about the content of the row
        var b1Row = text.Split('\n').Single(l => l.TrimStart().StartsWith("✓ B1", StringComparison.Ordinal));
        Assert.Equal("✓ B1 read a file 7.0s", Squash(b1Row));
    }

    //collapse the column padding of a row, so an assertion can compare what the row says
    private static string Squash(string row) =>
        System.Text.RegularExpressions.Regex.Replace(row.Trim(), @"\s+", " ");

    [Fact]
    public void A_ROW_NAMES_ITS_TASK_IN_WORDS_because_B1_tells_a_user_nothing()
    {
        //the row names the task in words as well as its id, which the verdict line and bug reports cite
        var text = Render(Verdict(pass: true));

        Assert.Equal("✓ B4 use two tools in order 6.0s",
            Squash(text.Split('\n').Single(l => l.TrimStart().StartsWith("✓ B4", StringComparison.Ordinal))));
    }

    [Fact]
    public void The_TIMINGS_LINE_UP_however_long_the_labels_are()
    {
        //pad the label column to the widest label, or the timings start in different columns
        var rows = Render(Verdict(pass: true)).Split('\n')
            .Where(l => l.TrimStart().StartsWith("✓ B", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(5, rows.Count);

        var columns = rows
            .Select(r => System.Text.RegularExpressions.Regex.Match(r, @"\d+\.\d+s").Index)
            .ToList();
        Assert.All(columns, c => Assert.True(c > 0, "no timing found in a task row"));
        Assert.Single(columns.Distinct());
    }

    [Fact]
    public void The_BUILD_belongs_to_GATTO_and_the_line_says_so()
    {
        //a bare build key would read as the build of the model, so the key must say gatto build
        var text = Render(Verdict(pass: true));

        Assert.Matches(@"gatto build\s+v0\.4\.0-38-g52d5ea2", text);
        Assert.DoesNotContain("\n  build ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_stamp_KEYS_stay_aligned_when_one_of_them_grows()
    {
        //gatto build is the longest key, so compute the column width from the keys present
        var text = Render(Verdict(pass: true));
        var starts = new[] { "model", "quant", "context", "sampling", "speed", "gatto build" }
            .Select(k =>
            {
                var line = text.Split('\n').Single(l => l.TrimStart().StartsWith(k, StringComparison.Ordinal));
                var col = line.IndexOf(k, StringComparison.Ordinal) + k.Length;
                while (col < line.Length && line[col] == ' ') col++;
                return col;                    //the column where the value begins.
            })
            .ToList();

        Assert.Single(starts.Distinct());
    }

    [Fact]
    public void COLOUR_arrives_through_an_injected_painter_and_STRICTLY_AFTER_the_sanitizer()
    {
        //sanitize the model text before painting, the two assertions below hold only in that order
        const string esc = "\u001b"; //write escapes as \u sequences, a raw ESC byte is invisible in a diff and a mangled one breaks both assertions
        var hostile = $"evil{esc}[2Jname.gguf";

        var text = AuditionReport.Render(Verdict(pass: true, model: hostile),
            s => s.Replace(esc, "", StringComparison.Ordinal), Gatto.Roles.EngineMarks.Unicode,
            (s, ink) => esc + (ink == ReportInk.Ok ? "[32m" : "[2m") + s + esc + "[0m");

        Assert.Contains(esc + "[32m✓ tool-calling verified", text, StringComparison.Ordinal);  //the escape of the painter must survive.
        Assert.DoesNotContain(esc + "[2J", text, StringComparison.Ordinal);                    //the escape from the model must not survive.
    }

    [Fact]
    public void The_SPEED_sits_in_the_stamp_block_between_sampling_and_the_build()
    {
        //speed belongs in the stamp block, it is one of the most useful figures about a model
        var lines = Render(Verdict(pass: true)).Split('\n').Select(l => l.TrimStart()).ToList();
        var sampling = lines.FindIndex(l => l.StartsWith("sampling", StringComparison.Ordinal));
        var speed = lines.FindIndex(l => l.StartsWith("speed", StringComparison.Ordinal));
        var build = lines.FindIndex(l => l.StartsWith("gatto build", StringComparison.Ordinal));

        Assert.True(speed == sampling + 1 && build == speed + 1,
            $"expected sampling→speed→build, got {sampling}/{speed}/{build}");
        Assert.Contains("28 tok/s", lines[speed], StringComparison.Ordinal);
    }

    [Fact]
    public void The_RATE_IS_STATED_ONCE_because_saying_a_number_twice_is_the_duplication_we_just_removed()
    {
        var text = Render(Verdict(pass: true));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(text, @"tok/s"));
    }

    [Theory]
    [InlineData(220.0, "great, most turns in seconds")]
    [InlineData(80.0, "great, most turns in seconds")]
    [InlineData(79.9, "good for daily driving")]
    [InlineData(50.0, "good for daily driving")]
    [InlineData(49.9, "usable")]
    [InlineData(20.0, "usable")]
    [InlineData(19.9, "slow, expect waiting")]
    [InlineData(5.0, "slow, expect waiting")]
    [InlineData(4.9, "unusable, minutes per answer")]
    [InlineData(0.4, "unusable, minutes per answer")]
    public void The_SPEED_BANDS_are_the_five_ruled_bands_verbatim(double rate, string expected)
    {
        //assert both sides of every band boundary, an edge tested from the inside is not placed. the floor is 5, a band from 1 to 20 would span a twenty-fold spread
        Assert.Equal(expected, AuditionReport.SpeedNote(rate));
    }

    [Fact]
    public void NO_band_carries_an_em_dash_because_the_row_already_joins_with_one()
    {
        //a speed note must hold no em dash, the row already joins with one
        foreach (var rate in new[] { 200.0, 90.0, 55.0, 30.0, 8.0, 0.4 })
            Assert.DoesNotContain("—", AuditionReport.SpeedNote(rate), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(220.0, "~220 tok/s", "great, most turns in seconds")]
    [InlineData(80.0, "~80 tok/s", "great, most turns in seconds")]
    [InlineData(79.6, "~80 tok/s", "great, most turns in seconds")]   //the rate rounds into the band of the figure that the row displays.
    [InlineData(50.0, "~50 tok/s", "good for daily driving")]
    [InlineData(49.9, "~50 tok/s", "good for daily driving")]
    [InlineData(19.9, "~20 tok/s", "usable")]
    [InlineData(19.4, "~19 tok/s", "slow, expect waiting")]
    [InlineData(9.9, "~9.9 tok/s", "slow, expect waiting")]
    [InlineData(0.4, "~0.4 tok/s", "unusable, minutes per answer")]
    [InlineData(0.04, "~0.04 tok/s", "unusable, minutes per answer")]
    [InlineData(0.004, "<0.01 tok/s", "unusable, minutes per answer")]
    public void THE_FIGURE_AND_THE_BAND_ARE_READ_OFF_THE_SAME_NUMBER(double rate, string figure, string band)
    {
        //compute the band from the rounded figure the row shows, or a row at a boundary contradicts itself
        var row = Render(Verdict(pass: true) with { DecodeTokS = rate })
            .Split('\n').Single(l => l.TrimStart().StartsWith("speed", StringComparison.Ordinal));

        Assert.Contains(figure, row, StringComparison.Ordinal);
        Assert.Contains(band, row, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(50.5, "~51 tok/s")]
    [InlineData(22.5, "~23 tok/s")]
    public void A_MIDPOINT_rounds_the_way_a_reader_expects_and_not_to_even(double rate, string figure)
    {
        //the default Math.Round sends a midpoint to even, so only this test catches a change of rounding mode
        var row = Render(Verdict(pass: true) with { DecodeTokS = rate })
            .Split('\n').Single(l => l.TrimStart().StartsWith("speed", StringComparison.Ordinal));

        Assert.Contains(figure, row, StringComparison.Ordinal);
    }

    [Fact]
    public void NO_RATE_CAN_PRODUCE_A_ROW_THAT_DISAGREES_WITH_ITSELF()
    {
        //sweep the rates rather than sampling them, so a boundary the table misses cannot hide
        for (var raw = 0.05; raw < 300; raw *= 1.017)
        {
            var row = Render(Verdict(pass: true) with { DecodeTokS = raw })
                .Split('\n').Single(l => l.TrimStart().StartsWith("speed", StringComparison.Ordinal));

            var shownText = System.Text.RegularExpressions.Regex.Match(row, @"~([\d.]+) tok/s").Groups[1].Value;
            Assert.False(shownText.Length == 0, $"no figure in: {row}");
            var shown = double.Parse(shownText, System.Globalization.CultureInfo.InvariantCulture);

            Assert.Contains(AuditionReport.SpeedNote(shown), row, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void A_GENUINELY_SLOW_model_reads_as_slow_and_never_as_ZERO()
    {
        //a big model on the CPU runs at a fraction of a token per second, so a plain F0 would show ~0 tok/s
        var text = Render(Verdict(pass: true) with { DecodeTokS = 0.4 });

        Assert.Contains("~0.4 tok/s", text, StringComparison.Ordinal);
        Assert.DoesNotContain("~0 tok/s", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_speed_note_describes_the_NUMBER_and_can_never_read_as_an_endorsement()
    {
        //a speed note describes the number, a fast model can still fail everything the audition measures
        foreach (var rate in new[] { 200.0, 90.0, 55.0, 30.0, 8.0, 0.4 })
        {
            var note = AuditionReport.SpeedNote(rate);
            Assert.DoesNotContain("recommend", note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("good model", note, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("better", note, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void An_ABSENT_rate_is_SAID_rather_than_left_as_a_gap_the_user_hunts_for()
    {
        //a server can omit timings, so the rate is unknown, and the speed row stays and says why
        var text = Render(Verdict(pass: true) with { DecodeTokS = null });
        var row = text.Split('\n').Single(l => l.TrimStart().StartsWith("speed", StringComparison.Ordinal));

        Assert.Contains("not reported", row, StringComparison.Ordinal);
        Assert.DoesNotContain("tok/s", text, StringComparison.Ordinal);   //the report must never invent a zero rate.
    }

    [Fact]
    public void The_RATE_wears_the_accent_so_the_eye_finds_it_without_reading_the_block()
    {
        var text = AuditionReport.Render(Verdict(pass: true), s => s, Gatto.Roles.EngineMarks.Unicode,
            (s, ink) => ink == ReportInk.Accent ? $"[{s}]" : s);

        Assert.Contains("[~28 tok/s]", text, StringComparison.Ordinal);
    }

    [Fact]
    public void With_NO_painter_the_report_is_byte_pure()
    {
        //a pipe, a NO_COLOR terminal and the release capture get no painter, so the report must be byte pure
        Assert.DoesNotContain("\u001b", Render(Verdict(pass: false, disqualified: true)),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_DISQUALIFIED_report_names_the_fabrication_as_the_reason_it_failed()
    {
        var text = Render(Verdict(pass: true, disqualified: true));

        Assert.DoesNotContain("tool-calling verified", text);
        Assert.Contains("disqualif", text, StringComparison.OrdinalIgnoreCase);
        //the headline and the failure row read one constant, so the headline is checked for naming the fabrication
        Assert.Contains("doesn't exist", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Timing_is_reported_as_EVIDENCE_and_never_as_a_criterion()
    {
        var text = Render(Verdict(pass: true));
        Assert.Contains("25", text);        //the wall-clock seconds.
        Assert.Contains("28", text);        //the decode rate in tok/s
    }

    [Fact]
    public void The_stamp_block_makes_the_pass_attributable()
    {
        var text = Render(Verdict(pass: true));
        Assert.Contains("v0.4.0-38-g52d5ea2", text);
        Assert.Contains("Q4_K_M", text);
        Assert.Contains("temp 0.7", text);
    }

    [Fact]
    public void MODEL_DERIVED_TEXT_GOES_THROUGH_THE_INJECTED_SANITIZER()
    {
        //the model name is attacker text, so every model-derived string must go through the sanitizer before it renders
        const string esc = "\u001b"; //write escapes as \u sequences, a raw control byte is invisible in a diff
        const string bel = "\u0007";
        var hostile = $"evil{esc}[2Jname{bel}.gguf";
        var called = new List<string>();

        var text = AuditionReport.Render(Verdict(pass: true, model: hostile), s =>
        {
            called.Add(s);
            return s.Replace(esc, "").Replace(bel, "");
        }, Gatto.Roles.EngineMarks.Unicode);

        Assert.Contains(hostile, called);              //the sanitizer must receive the model name.
        //use an ordinal comparison for control characters, a culture-sensitive one gives them zero weight and the assertions pass either way
        Assert.DoesNotContain(esc, text, StringComparison.Ordinal);
        Assert.DoesNotContain(bel, text, StringComparison.Ordinal);
        Assert.Contains("evil[2Jname.gguf", text);     //the harmless rest of the name still renders.
    }

    [Fact]
    public void The_sanitizer_has_NO_DEFAULT_so_a_caller_cannot_forget_it_by_omission()
    {
        //the sanitize parameter must have no default, a default no-op is invisible at the call site and fails only on hostile input
        var render = typeof(AuditionReport).GetMethod(nameof(AuditionReport.Render))!;
        var sanitize = render.GetParameters().Single(p => p.ParameterType == typeof(Func<string, string>));

        Assert.False(sanitize.HasDefaultValue);
        Assert.False(sanitize.IsOptional);
    }

    [Fact]
    public void The_report_uses_no_emoji()
    {
        //every mark in the report comes from the glyph table of gatto
        var text = Render(Verdict(pass: false, disqualified: true));
        Assert.DoesNotContain(text, c => char.IsSurrogate(c));
    }

    [Fact]
    public void THE_STAMP_SAYS_WHAT_THINKING_STATE_WAS_ACTUALLY_SENT()
    {
        //the stamp must say which thinking state was sent, a badge must not describe a configuration the model did not run
        var stamp = new AuditionStamp("abc1234", "m.gguf", "Q4_K_M", 8192, "defaults",
            "template default (the model's thinking setting is not applied by this check)");
        var verdict = new AuditionVerdict(true, false, [], stamp, TimeSpan.FromSeconds(9), 42.0);

        var text = AuditionReport.Render(verdict, s => s, Gatto.Roles.EngineMarks.Unicode, (t, _) => t);

        Assert.Contains("thinking", text, StringComparison.Ordinal);
        Assert.Contains("not applied by this check", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AN_EMPTY_THINKING_NOTE_RENDERS_NO_ROW()
    {
        //an empty row reads as a fact gatto could not find, so the note must render no row at all
        var stamp = new AuditionStamp("abc1234", "m.gguf", "Q4_K_M", 8192, "defaults");
        var verdict = new AuditionVerdict(true, false, [], stamp, TimeSpan.FromSeconds(9), 42.0);

        var text = AuditionReport.Render(verdict, s => s, Gatto.Roles.EngineMarks.Unicode, (t, _) => t);

        Assert.DoesNotContain("thinking", text, StringComparison.Ordinal);
    }
}
