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
            {"schema":1,"verdict":"pass","model_key":"lmstudio-community/Qwen3.6-35B-A3B-GGUF",
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
        WriteRecord("org/measured", """{"schema":1,"verdict":"pass","measured":"2026-08-09"}""");
        Assert.Null(BadgeRegister.Lookup(_home, "org/never-measured"));
    }

    [Fact]
    public void A_malformed_record_is_skipped_and_other_records_still_read()
    {
        //a corrupt record must not stop a whole search, so the reader skips it and still reads the other records.
        WriteRecord("org/broken", "{ not json at all");
        WriteRecord("org/good", """{"schema":1,"verdict":"pass","measured":"2026-08-09","gatto_build":"b"}""");

        Assert.Null(BadgeRegister.Lookup(_home, "org/broken"));
        Assert.NotNull(BadgeRegister.Lookup(_home, "org/good"));
    }

    [Fact]
    public void A_record_without_a_measurement_date_is_not_a_measurement()
    {
        WriteRecord("org/dateless", """{"schema":1,"verdict":"pass","gatto_build":"b"}""");
        Assert.Null(BadgeRegister.Lookup(_home, "org/dateless"));
    }

    [Fact]
    public void An_absent_sampling_note_is_a_field_that_does_not_apply_not_a_parse_failure()
    {
        //a measurement can have no sampling note, so an absent note is not a parse failure. the reader must keep accepting a record without one
        WriteRecord("org/nonote", """{"schema":1,"verdict":"pass","measured":"2026-08-09","gatto_build":"b"}""");
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
}
