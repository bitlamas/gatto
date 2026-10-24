using Gatto.Roles.Audition;
using Gatto.Roles.Models;

namespace Gatto.Tests.Audition;

//the sampling the server was started with, from all three places a profile can name it, since a shipped mark needs llama-server's defaults
public class SamplingNoteTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-note-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (Exception) { } }

    private Model With(string? sampling = null, string[]? args = null, int? seed = null)
    {
        var dir = Path.Combine(_dir, "m");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(_dir, "m-Q4_K_M.gguf").Replace("\\", "\\\\");
        var extra = args is null ? "" : ",\"extra_args\":[" + string.Join(",", args.Select(a => "\"" + a + "\"")) + "]";
        File.WriteAllText(Path.Combine(dir, "profile.json"),
            $"{{\"files\":[{{\"path\":\"{file}\",\"active\":true}}],\"port\":1235,\"context\":8192"
            + (sampling is null ? "" : ",\"sampling\":" + sampling) + extra
            + (seed is null ? "" : $",\"seed\":{seed}") + "}");
        return Model.Load(_dir, "m");
    }

    [Fact]
    public void NOTHING_NAMED_IS_DEFAULTS() => Assert.Equal("defaults", AuditionRunner.SamplingNote(With()));

    [Fact]
    public void THE_BLOCK_ALONE_READS_AS_BEFORE() =>
        Assert.Equal("temperature 0.7", AuditionRunner.SamplingNote(With("{\"temperature\":0.7}")));

    [Theory]
    [InlineData("presence-penalty 1.5", "--presence-penalty", "1.5")]
    [InlineData("top-k 20", "--top-k", "20")]
    [InlineData("seed 7", "-s", "7")]
    [InlineData("temp 1.2", "--temperature", "1.2")]
    [InlineData("typical 0.9", "--typical-p", "0.9")]
    [InlineData("ignore-eos", "--ignore-eos")]
    [InlineData("backend-sampling", "-bs")]
    public void A_SAMPLING_FLAG_IN_EXTRA_ARGS_IS_NAMED_BY_ITS_FIRST_LONG_SPELLING(string expected, params string[] args) =>
        Assert.Equal(expected, AuditionRunner.SamplingNote(With(args: args)));

    [Fact]
    public void THE_SEED_KEY_STAMPS_AS_THE_SEED_FLAG_DOES() =>
        Assert.Equal("seed 7", AuditionRunner.SamplingNote(With(seed: 7)));

    [Fact]
    public void THE_BLOCK_AND_A_FLAG_ARE_BOTH_LISTED() =>
        Assert.Equal("top_k 40, presence-penalty 1.5",
            AuditionRunner.SamplingNote(With("{\"top_k\":40}", ["-np", "1", "--presence-penalty", "1.5", "--jinja"])));

    //the flags every scaffolded profile carries are not sampling, so a fresh model still reads defaults
    [Fact]
    public void A_NON_SAMPLING_FLAG_IS_NOT_LISTED() =>
        Assert.Equal("defaults", AuditionRunner.SamplingNote(With(args: ["-np", "2", "--jinja", "-fa", "on", "--no-context-shift"])));

    //one line per flag row of the pinned server's sampling block, so a row dropped from the table fails the count
    [Fact]
    public void THE_TABLE_HOLDS_THE_34_ROWS_OF_THE_SAMPLING_BLOCK()
    {
        Assert.Equal(34, AuditionRunner.SamplingFlags.Length);
        Assert.Equal(["--ignore-eos", "-bs"],
            AuditionRunner.SamplingFlags.Where(f => !f.TakesValue).Select(f => f.Spellings[0]));
        Assert.All(AuditionRunner.SamplingFlags, f => Assert.Contains(f.Spellings, s => s.StartsWith("--", StringComparison.Ordinal)));
    }
}
