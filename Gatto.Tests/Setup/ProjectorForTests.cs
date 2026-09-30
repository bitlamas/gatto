using Gatto.Core.Acquire;

namespace Gatto.Tests.Setup;

//uses a real temp directory, the subject is a directory scan and a fake one would only guess what folders look like
public class ProjectorForTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "gatto-t14-" + Guid.NewGuid().ToString("N"));

    public ProjectorForTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { }
        GC.SuppressFinalize(this);
    }

    private string File_(string name)
    {
        var p = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(p, [0x47, 0x47, 0x55, 0x46]);   //writes the GGUF magic bytes only, the file just has to exist
        return p;
    }

    [Fact]
    public void THE_PROJECTOR_BESIDE_THE_MODEL_IS_FOUND_BY_ITS_NAME()
    {
        var model = File_("qwen3-vl-8b-Q4_K_M.gguf");
        var proj = File_("mmproj-qwen3-vl-8b-f16.gguf");

        Assert.Equal(proj, ModelDiscovery.ProjectorFor(model));
    }

    [Fact]
    public void A_FOLDER_WITH_NO_PROJECTOR_ANSWERS_NULL()
    {
        var model = File_("qwen3-8b-Q4_K_M.gguf");
        File_("gemma-4-26b-Q6_K.gguf");

        //a null answer means no projector was found, and the caller must not read that as the model having none
        Assert.Null(ModelDiscovery.ProjectorFor(model));
    }

    [Fact]
    public void THE_MODEL_ITSELF_IS_NEVER_ITS_OWN_PROJECTOR()
    {
        //a model whose own name contains mmproj must not come back as its own companion
        var model = File_("mmproj-weird-model-Q4_K_M.gguf");

        Assert.Null(ModelDiscovery.ProjectorFor(model));
    }

    [Fact]
    public void A_MISSING_DIRECTORY_ANSWERS_NULL_rather_than_throwing()
    {
        //a probe must answer null when a path is gone, a wizard that dies over a moved folder fails its only user
        Assert.Null(ModelDiscovery.ProjectorFor(
            Path.Combine(_dir, "no-such-folder", "model.gguf")));
        Assert.Null(ModelDiscovery.ProjectorFor(""));
    }

    [Fact]
    public void THE_SEARCH_IS_DETERMINISTIC_when_two_projectors_sit_together()
    {
        //repos ship f16 and Q8_0 of the same encoder, the answer must not depend on the order the filesystem enumerates
        var model = File_("qwen3-vl-8b-Q4_K_M.gguf");
        var f16 = File_("mmproj-qwen3-vl-8b-f16.gguf");
        File_("mmproj-qwen3-vl-8b-Q8_0.gguf");

        Assert.Equal(f16, ModelDiscovery.ProjectorFor(model));
        Assert.Equal(f16, ModelDiscovery.ProjectorFor(model));
    }

    [Fact]
    public void ONE_PREDICATE_OWNS_THE_CONVENTION()
    {
        //the search must ask IsProjector rather than hold its own copy of the naming rule, a second copy is how conventions drift
        var model = File_("model-Q4_K_M.gguf");
        var found = File_("mmproj-thing.gguf");

        Assert.True(ModelDiscovery.IsProjector(found, header: null));
        Assert.Equal(found, ModelDiscovery.ProjectorFor(model));

        //the converse too, a file the predicate rejects must not come back from the search
        var plain = Path.Combine(_dir, "notes.gguf");
        System.IO.File.WriteAllBytes(plain, [0x47, 0x47, 0x55, 0x46]);
        Assert.False(ModelDiscovery.IsProjector(plain, header: null));
    }
}
