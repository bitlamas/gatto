using Gatto.Roles;

namespace Gatto.Tests;

//a sourced model matches on its source and a sourceless one falls back to the file name. keep the precedence in one place
public class ModelForHubRowTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-hubrow-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private const string Shared = "model-Q4_K_M.gguf";

    //write a model whose profile records the repo it came from.
    private void Sourced(string id, string repo, string onDisk = Shared) => Write(id,
        $$"""
        {"files": [{"path": "{{onDisk}}", "quant": "Q4_K_M", "active": true}],
         "source": {"repo_id": "{{repo}}", "file": "{{Shared}}"},
         "port": 1235, "context": 8192}
        """);

    //write a model adopted from a local file, which never records a repo
    private void Local(string id, string onDisk = Shared) => Write(id,
        $$"""{"files": [{"path": "{{onDisk}}", "quant": "Q4_K_M", "active": true}], "port": 1235, "context": 8192}""");

    private void Write(string id, string profile)
    {
        Directory.CreateDirectory(Path.Combine(_dir, id));
        File.WriteAllText(Path.Combine(_dir, id, "profile.json"), profile);
    }

    //renaming the file on disk must not erase the have-mark, the source still records where it came from
    [Fact]
    public void A_RENAMED_FILE_STILL_MATCHES_because_the_repo_is_what_is_asked()
    {
        Sourced("gemma-4-e4b-it", "google/gemma-4-e4b-it-GGUF", onDisk: "my-renamed-copy.gguf");

        Assert.Equal("gemma-4-e4b-it",
            ModelScaffold.ModelForHubRow(_dir, "google/gemma-4-e4b-it-GGUF", Shared));
    }

    //a sourced model must never answer by file name, or the mark claims a file the user does not have
    [Fact]
    public void A_SOURCED_MODEL_NEVER_ANSWERS_FOR_ANOTHER_REPOS_IDENTICALLY_NAMED_FILE()
    {
        Sourced("mine", "publisher-a/model-GGUF");

        Assert.Null(ModelScaffold.ModelForHubRow(_dir, "publisher-b/model-GGUF", Shared));
    }

    //the name fallback is structural, a model adopted from a local file can never record a repo
    [Fact]
    public void A_SOURCELESS_MODEL_IS_STILL_MATCHED_BY_FILE_NAME()
    {
        Local("adopted");

        Assert.Equal("adopted", ModelScaffold.ModelForHubRow(_dir, "publisher-b/model-GGUF", Shared));
    }

    //a sourced model answers only its own repo, and the sourceless one only by name
    [Fact]
    public void AND_THE_TWO_KINDS_DO_NOT_SHADOW_EACH_OTHER()
    {
        Sourced("sourced", "publisher-a/model-GGUF", onDisk: "a-Q4_K_M.gguf");
        Local("adopted", onDisk: Shared);

        Assert.Equal("sourced", ModelScaffold.ModelForHubRow(_dir, "publisher-a/model-GGUF", "a-Q4_K_M.gguf"));
        Assert.Equal("adopted", ModelScaffold.ModelForHubRow(_dir, "publisher-b/model-GGUF", Shared));
    }

    //repo id comparison folds case, like every id comparison in this area.
    [Fact]
    public void THE_REPO_MATCH_FOLDS_CASE()
    {
        Sourced("gemma", "Google/Gemma-4-E4B-it-GGUF");

        Assert.Equal("gemma", ModelScaffold.ModelForHubRow(_dir, "google/gemma-4-e4b-it-gguf", Shared));
    }

    //an empty file name must match nothing, a match would mark every row
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AN_EMPTY_FILE_NAME_MATCHES_NOTHING(string name)
    {
        Local("adopted");

        Assert.Null(ModelScaffold.ModelForHubRow(_dir, "publisher-b/model-GGUF", name));
    }

    //an empty repo must not match the first sourced model it meets, a recorded repo_id is never empty
    [Fact]
    public void AN_EMPTY_REPO_MATCHES_NO_SOURCED_MODEL()
    {
        Sourced("sourced", "publisher-a/model-GGUF", onDisk: "a-Q4_K_M.gguf");

        Assert.Null(ModelScaffold.ModelForHubRow(_dir, "", "a-Q4_K_M.gguf"));
    }

    //one broken model must not stop the others being found, reporting bad profiles is the doctor's job
    [Fact]
    public void A_BROKEN_PROFILE_IS_SKIPPED_NOT_THROWN()
    {
        Write("broken", "{ this is not json");
        Sourced("good", "publisher-a/model-GGUF");

        Assert.Equal("good", ModelScaffold.ModelForHubRow(_dir, "publisher-a/model-GGUF", Shared));
    }
}
