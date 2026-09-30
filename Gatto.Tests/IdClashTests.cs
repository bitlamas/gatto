using Gatto.Roles;

namespace Gatto.Tests;

//a match adds, a proven difference collides, and anything less asks. headers agree across finetunes, so a collision is never guessed
public class IdClashTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-clash-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Models => Path.Combine(_dir, "models");
    private const string RepoA = "publisher-a/gemma-GGUF";
    private const string RepoB = "publisher-b/gemma-GGUF";

    //both files share one general.name, the shape that collides ids and that no header field can tell apart
    private string Gguf(string fileName)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "gemma3");
            kv.Str("general.name", "gemma");
            kv.U32("gemma3.context_length", 4096);
        }));
        return path;
    }

    private void Holder(string ownedFile, string? repo)
    {
        var source = repo is null ? "" : $"\"source\": {{\"repo_id\": \"{repo}\", \"file\": \"x.gguf\"}},";
        Directory.CreateDirectory(Path.Combine(Models, "gemma"));
        File.WriteAllText(Path.Combine(Models, "gemma", "profile.json"),
            $$"""
            {"files": [{"path": {{System.Text.Json.JsonSerializer.Serialize(ownedFile)}},
                        "quant": "Q4_K_M", "active": true}],
             {{source}} "port": 1235, "context": 8192}
            """);
    }

    //if no model holds the id, adoption is ordinary and no screen appears.
    [Fact]
    public void AN_UNHELD_ID_IS_FREE()
    {
        Assert.Equal((IdClash.Free, null), ModelScaffold.ClashFor(Models, Gguf("a-Q6_K.gguf"), RepoA));
    }

    //when both sides name the same repo, the file is added to that model and no collision screen appears.
    [Fact]
    public void THE_SAME_REPO_IS_THE_SAME_MODEL()
    {
        Holder(Gguf("a-Q4_K_M.gguf"), RepoA);

        Assert.Equal((IdClash.SameModel, "gemma"),
            ModelScaffold.ClashFor(Models, Gguf("a-Q6_K.gguf"), RepoA));
    }

    //two models that share general.name are proven different only when both name a repo and the repos differ
    [Fact]
    public void TWO_REPOS_THAT_DIFFER_ARE_TWO_MODELS()
    {
        Holder(Gguf("a-Q4_K_M.gguf"), RepoA);

        Assert.Equal((IdClash.DifferentModel, "gemma"),
            ModelScaffold.ClashFor(Models, Gguf("b-Q4_K_M.gguf"), RepoB));
    }

    //a local file has no repo and never will, which is structural rather than missing data, so the screen asks.
    [Fact]
    public void A_LOCAL_PICK_CANNOT_BE_TOLD_APART()
    {
        Holder(Gguf("a-Q4_K_M.gguf"), RepoA);

        Assert.Equal((IdClash.CannotTell, "gemma"),
            ModelScaffold.ClashFor(Models, Gguf("a-Q6_K.gguf"), incomingRepoId: null));
    }

    //a repo against an absent source proves nothing, so this stays CannotTell, or the user is told their own model collides with itself
    [Fact]
    public void AND_A_SOURCELESS_HOLDER_CANNOT_BE_TOLD_APART_EITHER()
    {
        Holder(Gguf("a-Q4_K_M.gguf"), repo: null);

        Assert.Equal((IdClash.CannotTell, "gemma"),
            ModelScaffold.ClashFor(Models, Gguf("a-Q6_K.gguf"), RepoA));
    }

    //a holder whose profile will not load gets the screen with no add offer. adding a file to a model that does not load is impossible
    [Fact]
    public void AN_UNREADABLE_HOLDER_IS_ITS_OWN_ANSWER()
    {
        Directory.CreateDirectory(Path.Combine(Models, "gemma"));
        File.WriteAllText(Path.Combine(Models, "gemma", "profile.json"), "{ not json");

        Assert.Equal((IdClash.HolderUnreadable, "gemma"),
            ModelScaffold.ClashFor(Models, Gguf("a-Q6_K.gguf"), RepoA));
    }

    //re-adopting a file the model already lists must reach reuse, so a rerun stays idempotent
    [Fact]
    public void A_FILE_THE_MODEL_ALREADY_LISTS_IS_NOT_A_CLASH()
    {
        var mine = Gguf("a-Q4_K_M.gguf");
        Holder(mine, RepoA);

        Assert.Equal((IdClash.Free, null), ModelScaffold.ClashFor(Models, mine, RepoA));
    }
}
