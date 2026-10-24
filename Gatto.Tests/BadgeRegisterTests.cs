using Gatto.Core.Acquire;

namespace Gatto.Tests;

//a badge must never appear without a measurement file behind it.
public class BadgeRegisterTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "gatto-badges-" + Guid.NewGuid().ToString("N"));

    public BadgeRegisterTests() => Directory.CreateDirectory(_home);
    public void Dispose() { try { Directory.Delete(_home, recursive: true); } catch { } }

    private void WriteRecord(string modelKey, string json)
    {
        var dir = Path.Combine(_home, BadgeRegister.DirectoryName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, BadgeRegister.FileNameFor(modelKey)), json);
    }

    [Fact]
    public void A_hand_written_record_round_trips_into_a_badge()
    {
        WriteRecord("lmstudio-community/Qwen3.6-35B-A3B-GGUF", """
            {"schema":1,"verdict":"pass","battery_version":4,"model_key":"lmstudio-community/Qwen3.6-35B-A3B-GGUF",
             "measured":"2026-08-09","gatto_build":"v0.4.0-12-gabc1234",
             "sampling_note":"temp 0.7, top_p 0.8"}
            """);

        var b = BadgeRegister.Lookup(_home, "lmstudio-community/Qwen3.6-35B-A3B-GGUF");

        Assert.NotNull(b);
        Assert.Equal(new DateOnly(2026, 8, 9), b!.Measured);
        Assert.Equal("v0.4.0-12-gabc1234", b.GattoBuild);
        Assert.Equal("temp 0.7, top_p 0.8", b.SamplingNote);
    }

    [Fact]
    public void An_absent_register_directory_is_null_not_a_throw()
    {
        Assert.Null(BadgeRegister.Lookup(_home, "anything/at-all"));
    }

    [Fact]
    public void An_unmeasured_model_has_no_badge()
    {
        WriteRecord("org/measured", """{"schema":1,"verdict":"pass","battery_version":4,"measured":"2026-08-09"}""");
        Assert.Null(BadgeRegister.Lookup(_home, "org/never-measured"));
    }

    [Fact]
    public void A_malformed_record_is_skipped_and_other_records_still_read()
    {
        //a corrupt record must not stop a whole search, so the reader skips it and still reads the other records.
        WriteRecord("org/broken", "{ not json at all");
        WriteRecord("org/good", """{"schema":1,"verdict":"pass","battery_version":4,"measured":"2026-08-09","gatto_build":"b"}""");

        Assert.Null(BadgeRegister.Lookup(_home, "org/broken"));
        Assert.NotNull(BadgeRegister.Lookup(_home, "org/good"));
    }

    [Fact]
    public void A_record_without_a_measurement_date_is_not_a_measurement()
    {
        WriteRecord("org/dateless", """{"schema":1,"verdict":"pass","battery_version":4,"gatto_build":"b"}""");
        Assert.Null(BadgeRegister.Lookup(_home, "org/dateless"));
    }

    [Fact]
    public void An_absent_sampling_note_is_a_field_that_does_not_apply_not_a_parse_failure()
    {
        //a measurement can have no sampling note, so an absent note is not a parse failure. the reader must keep accepting a record without one
        WriteRecord("org/nonote", """{"schema":1,"verdict":"pass","battery_version":4,"measured":"2026-08-09","gatto_build":"b"}""");
        var b = BadgeRegister.Lookup(_home, "org/nonote");
        Assert.NotNull(b);
        Assert.Equal("", b!.SamplingNote);
    }

    [Theory]
    [InlineData("org/name", "org__name.json")]
    [InlineData("plain-file.gguf", "plain-file.gguf.json")]
    public void Repo_ids_map_to_collision_free_file_names(string key, string expected)
    {
        Assert.Equal(expected, BadgeRegister.FileNameFor(key));
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("a/b/../c")]
    [InlineData("C:\\windows\\system32")]
    [InlineData("")]
    public void A_hostile_model_key_cannot_escape_the_register_directory(string key)
    {
        //a model key is attacker text, so the result must stay a single file name inside the register folder. assert that shape, whatever the exact mapping is.
        var dir = Path.Combine(_home, BadgeRegister.DirectoryName);
        var full = Path.GetFullPath(Path.Combine(dir, BadgeRegister.FileNameFor(key)));
        Assert.Equal(Path.GetFullPath(dir), Path.GetDirectoryName(full));
    }

    [Fact]
    public void A_pass_on_ANOTHER_battery_is_not_a_badge_and_neither_is_one_that_names_none()
    {
        //the tasks changed, so a pass on the old ones says nothing about these. a reader that kept it would show a measurement nobody took
        var current = BadgeRegister.Battery;
        WriteRecord("org/old", $$"""{"schema":3,"verdict":"pass","battery_version":{{current - 1}},"measured":"2026-08-09"}""");
        WriteRecord("org/unnumbered", """{"schema":3,"verdict":"pass","measured":"2026-08-09"}""");
        WriteRecord("org/worded", """{"schema":3,"verdict":"pass","battery_version":"2","measured":"2026-08-09"}""");
        WriteRecord("org/current", $$"""{"schema":3,"verdict":"pass","battery_version":{{current}},"measured":"2026-08-09"}""");

        Assert.Null(BadgeRegister.Lookup(_home, "org/old"));
        Assert.Null(BadgeRegister.Lookup(_home, "org/unnumbered"));
        Assert.Null(BadgeRegister.Lookup(_home, "org/worded"));
        Assert.NotNull(BadgeRegister.Lookup(_home, "org/current"));
    }

    //a record as the writer lays it out, the sha256 key only when the stamp had one
    private static string Record(string verdict, string tasks, string? sha256 = null) =>
        "{" + Field("schema") + "3," + Field("verdict") + Str(verdict) + "," + Field("battery_version") + BadgeRegister.Battery + ","
        + Field("measured") + Str("2026-10-20") + "," + (sha256 is null ? "" : Field("sha256") + Str(sha256) + ",")
        + Field("tasks") + "[" + tasks + "]}";

    private static string Str(string s) => "\"" + s + "\"";

    private static string Field(string name) => Str(name) + ":";

    //the reader returns the last run here whatever its verdict, since the failed mark needs a fail to read
    [Fact]
    public void A_FAIL_RECORD_READS_AS_NOT_PASSED_AND_NAMES_NOTHING()
    {
        WriteRecord("m-Q4_K_M.gguf", Record("fail", """{"id":"B1","pass":false},{"id":"B2","pass":false},{"id":"B3","skipped":true}"""));

        var b = BadgeRegister.Lookup(_home, "m-Q4_K_M.gguf");
        Assert.NotNull(b);
        Assert.False(b!.Passed);
        Assert.Empty(b.FellShort);
    }

    [Fact]
    public void A_PASS_WITH_ONE_FAILED_TASK_NAMES_THAT_TASK()
    {
        WriteRecord("m-Q4_K_M.gguf", Record("pass", """{"id":"B1","pass":true},{"id":"B4","pass":false},{"id":"B5","pass":true}"""));

        var b = BadgeRegister.Lookup(_home, "m-Q4_K_M.gguf");
        Assert.True(b!.Passed);
        Assert.Equal(["B4"], b.FellShort);
    }

    //a skip cannot happen on a pass, and the reader must not invent a task out of one
    [Fact]
    public void A_SKIPPED_ROW_IS_NOT_A_TASK_FALLEN_SHORT_ON()
    {
        WriteRecord("m-Q4_K_M.gguf", Record("pass", """{"id":"B1","pass":true},{"id":"B5","skipped":true}"""));
        Assert.Empty(BadgeRegister.Lookup(_home, "m-Q4_K_M.gguf")!.FellShort);
    }

    //a record from a build before the key holds no hash, and the shelf shows nothing beside it
    [Fact]
    public void THE_SHA256_IS_READ_WHEN_THE_RECORD_HOLDS_ONE()
    {
        var sha = new string('a', 64);
        WriteRecord("keyed-Q4_K_M.gguf", Record("pass", """{"id":"B1","pass":true}""", sha));
        WriteRecord("bare-Q4_K_M.gguf", Record("pass", """{"id":"B1","pass":true}"""));

        Assert.Equal(sha, BadgeRegister.Lookup(_home, "keyed-Q4_K_M.gguf")!.Sha256);
        Assert.Null(BadgeRegister.Lookup(_home, "bare-Q4_K_M.gguf")!.Sha256);
    }

    [Fact]
    public void A_RECORD_THAT_STATES_NEITHER_VERDICT_IS_NOT_A_MEASUREMENT()
    {
        WriteRecord("m-Q4_K_M.gguf", Record("maybe", """{"id":"B1","pass":true}"""));
        Assert.Null(BadgeRegister.Lookup(_home, "m-Q4_K_M.gguf"));
    }

    //the writer files the record under the file name, the one key every model has, and the reader finds it by that name
    [Fact]
    public void THE_WRITER_FILES_BY_FILE_NAME_AND_THE_READER_FINDS_IT_THERE()
    {
        Gatto.Roles.Audition.BadgeWriter.Write(_home, "Qwen3-8B-Q4_K_M.gguf", Verdict(pass: true), repoId: "org/Qwen3-8B-GGUF");
        Gatto.Roles.Audition.BadgeWriter.Write(_home, "hand-downloaded-Q5_K_M.gguf", Verdict(pass: false), repoId: null);

        Assert.True(File.Exists(Path.Combine(_home, BadgeRegister.DirectoryName, BadgeRegister.FileNameFor("Qwen3-8B-Q4_K_M.gguf"))));
        Assert.Equal("org/Qwen3-8B-GGUF", BadgeRegister.Lookup(_home, "Qwen3-8B-Q4_K_M.gguf")!.RepoId);
        //a hand-downloaded model has no repo id, a real state, and its fail still reads
        var hand = BadgeRegister.Lookup(_home, "hand-downloaded-Q5_K_M.gguf");
        Assert.Null(hand!.RepoId);
        Assert.False(hand.Passed);
    }

    //model_file held a copy of model_key and had no reader, so schema 3 drops it with no migration
    [Fact]
    public void MODEL_FILE_IS_GONE_BECAUSE_IT_WAS_MODEL_KEYS_DUPLICATE()
    {
        Gatto.Roles.Audition.BadgeWriter.Write(_home, "Qwen3-8B-Q4_K_M.gguf", Verdict(pass: true), repoId: "org/m");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(_home, BadgeRegister.DirectoryName, BadgeRegister.FileNameFor("Qwen3-8B-Q4_K_M.gguf"))));

        Assert.False(doc.RootElement.TryGetProperty("model_file", out _));
        Assert.Equal(3, doc.RootElement.GetProperty("schema").GetInt32());
    }

    private static Gatto.Roles.Audition.AuditionVerdict Verdict(bool pass) =>
        new(pass, false,
            [
                new Gatto.Roles.Audition.AuditionTaskResult("B1", pass, pass ? [] : [Gatto.Roles.Audition.FailureShape.NoToolCall],
                    TimeSpan.FromSeconds(3), "read a file"),
                new Gatto.Roles.Audition.AuditionTaskResult("B2", true, [], TimeSpan.FromSeconds(4), "write a file"),
            ],
            new Gatto.Roles.Audition.AuditionStamp("v0.4.0-99-gdeadbee", "Qwen3-8B-Q4_K_M.gguf", "Q4_K_M", 8192, "temp 0.7"),
            TimeSpan.FromSeconds(25), 28.4);
}
