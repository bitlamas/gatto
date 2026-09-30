using System.Net;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Roles;
using Gatto.Roles.Audition;

namespace Gatto.Tests.Setup;

//the writer files a record under the model-file name, the reader looks it up by repo id. a round trip that uses one key on both sides proves nothing.
public class BadgeSeedsTests : IDisposable
{
    private readonly string _home = Path.Combine(
        Path.GetTempPath(), "gatto-seeds-" + Guid.NewGuid().ToString("N"));

    public BadgeSeedsTests() => Directory.CreateDirectory(_home);

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch (Exception) { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task A_MEASUREMENT_WRITTEN_THE_WAY_PRODUCTION_WRITES_IT_APPEARS_ON_THE_SEARCH_ROW()
    {
        BadgeWriter.Write(_home, "Qwen3-8B-Q4_K_M.gguf", Verdict(pass: true), repoId: "org/Qwen3-8B-GGUF");

        var row = await SearchRowFor("org/Qwen3-8B-GGUF");

        Assert.NotNull(row.Badge);
        Assert.Equal("Qwen3-8B-Q4_K_M.gguf", row.Badge!.ModelKey);
        Assert.Equal("org/Qwen3-8B-GGUF", row.Badge.RepoId);
    }

    [Fact]
    public async Task A_MEASUREMENT_OF_A_DIFFERENT_REPO_DOES_NOT_BADGE_THIS_ONE()
    {
        //the only record on disk belongs to another repo, so a reader that returns whatever it finds fails here.
        BadgeWriter.Write(_home, "Other-Q4_K_M.gguf", Verdict(pass: true), repoId: "org/something-else");

        Assert.Null((await SearchRowFor("org/Qwen3-8B-GGUF")).Badge);
    }

    [Fact]
    public async Task A_FAILED_MEASUREMENT_IS_NOT_A_BADGE_BY_REPO_ID_EITHER()
    {
        //every measurement is written, pass or fail, and only the reader decides a fail is no badge. a second reader with its own rule would badge a failed model.
        BadgeWriter.Write(_home, "Qwen3-8B-Q4_K_M.gguf", Verdict(pass: false), repoId: "org/Qwen3-8B-GGUF");

        Assert.Null(BadgeRegister.LookupByRepoId(_home, "org/Qwen3-8B-GGUF"));
        Assert.Null((await SearchRowFor("org/Qwen3-8B-GGUF")).Badge);
    }

    [Fact]
    public void THE_RECORD_CARRIES_BOTH_KEYS_and_the_FILE_NAME_is_the_one_on_disk()
    {
        BadgeWriter.Write(_home, "Qwen3-8B-Q4_K_M.gguf", Verdict(pass: true), repoId: "org/Qwen3-8B-GGUF");

        //the file is filed under the model-file name, the one key every model has.
        var path = Path.Combine(_home, BadgeRegister.DirectoryName,
            BadgeRegister.FileNameFor("Qwen3-8B-Q4_K_M.gguf"));
        Assert.True(File.Exists(path), $"the record is not filed under the file-name key: {path}");

        Assert.Equal("org/Qwen3-8B-GGUF",
            BadgeRegister.Lookup(_home, "Qwen3-8B-Q4_K_M.gguf")!.RepoId);
        Assert.Equal("Qwen3-8B-Q4_K_M.gguf",
            BadgeRegister.LookupByRepoId(_home, "org/Qwen3-8B-GGUF")!.ModelKey);
    }

    [Fact]
    public void A_DISK_ADOPTED_MODEL_HAS_NO_REPO_ID_and_is_still_readable_by_file_name()
    {
        //a hand-downloaded model has no repo id, so a null repo id is a real state and this case must stay.
        BadgeWriter.Write(_home, "hand-downloaded-Q5_K_M.gguf", Verdict(pass: true), repoId: null);

        var badge = BadgeRegister.Lookup(_home, "hand-downloaded-Q5_K_M.gguf");
        Assert.NotNull(badge);
        Assert.Null(badge!.RepoId);

        //a record with no repo id answers null by repo id, and that is the intended answer.
        Assert.Null(BadgeRegister.LookupByRepoId(_home, "hand-downloaded-Q5_K_M.gguf"));
    }

    [Fact]
    public void MODEL_FILE_IS_GONE_because_it_was_model_keys_duplicate()
    {
        //the model_file field held a copy of model_key and had no reader, so schema 3 drops it with no migration.
        BadgeWriter.Write(_home, "Qwen3-8B-Q4_K_M.gguf", Verdict(pass: true), repoId: "org/m");

        var json = File.ReadAllText(Path.Combine(_home, BadgeRegister.DirectoryName,
            BadgeRegister.FileNameFor("Qwen3-8B-Q4_K_M.gguf")));
        using var doc = System.Text.Json.JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("model_file", out _));
        Assert.Equal(3, doc.RootElement.GetProperty("schema").GetInt32());
    }

    [Fact]
    public void AN_EMPTY_REGISTER_ANSWERS_NULL_rather_than_throwing()
    {
        //a fresh machine has no register dir at all, and that must answer null.
        Assert.Null(BadgeRegister.LookupByRepoId(_home, "org/anything"));
        Assert.Null(BadgeRegister.LookupByRepoId(_home, ""));
    }

    //one search row through the real engine over a stub hub, with the badge lookup wired as production wires it.
    private async Task<ShelfRow> SearchRowFor(string repoId)
    {
        var http = new HttpClient(new StubHub(repoId)) { Timeout = Timeout.InfiniteTimeSpan };
        var outcome = await HubSearch.AssembleAsync(
            new HubClient(http), new UploaderAllowlist("2026-08-14", ["org"]),
            new HardwareClass(MemoryTopology.Discrete, ShareKind.None, 40_000_000_000, 80_000_000_000,
                new HardwareSnapshot(0UL, 1UL, GpuKind.Discrete, 0UL), 0, BudgetBound.None),
            ctxForFit: 8192,
            badgeLookup: id => BadgeRegister.LookupByRepoId(_home, id),
            CancellationToken.None);

        return Assert.Single(outcome.Rows);
    }

    private sealed class StubHub(string repoId) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            var body = r.RequestUri!.ToString().Contains("/tree/main", StringComparison.Ordinal)
                ? """[{"type":"file","path":"Qwen3-8B-Q4_K_M.gguf","size":4000000000,"lfs":{"oid":"abc"}}]"""
                : $$"""[{"id":"{{repoId}}","downloads":10,"gated":false}]""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }

    private static AuditionVerdict Verdict(bool pass) =>
        new(pass, false,
            [
                new AuditionTaskResult("B1", pass, pass ? [] : [FailureShape.NoToolCall],
                    TimeSpan.FromSeconds(3), "read a file"),
                new AuditionTaskResult("B2", true, [], TimeSpan.FromSeconds(4), "write a file"),
            ],
            new AuditionStamp("v0.4.0-99-gdeadbee", "Qwen3-8B-Q4_K_M.gguf", "Q4_K_M", 8192, "temp 0.7"),
            TimeSpan.FromSeconds(25), 28.4);
}
