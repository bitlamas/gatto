using Gatto.Core.Client;
using Gatto.Roles;
using Gatto.Tests.Census;

namespace Gatto.Tests;

//model_path stays out of gatto and stays in the named server files. a known match and a known miss are what make a green run mean anything
public class ModelPathCensusTests
{
    private const string RetiredKey = "model_path";

    //these files parse llama-server's wire field, so their model_path stays (a pattern instead of the list would misfile a site)
    private static readonly string[] ServerSide =
    [
        "ServeProbe.cs", "ServeStatusJson.cs", "ServeManager.cs", "ServeNotice.cs", "UnmanagedSession.cs",
    ];

    [Fact]
    public void GATTOS_PROFILE_KEY_IS_GONE_FROM_THE_PRODUCT()
    {
        var offenders = new List<string>();
        var scannedAny = false;
        var scannedServerSide = false;

        foreach (var path in SourceTree.ProductionFiles())
        {
            var name = Path.GetFileName(path);
            //comments are stripped and strings are kept. a raw scan can't tell prose about the key from the key in code
            var text = SourceTree.WithoutComments(SourceTree.Read(path));
            if (ServerSide.Contains(name))
            {
                scannedServerSide |= text.Contains(RetiredKey, StringComparison.Ordinal);
                continue;
            }
            scannedAny = true;
            if (text.Contains(RetiredKey, StringComparison.Ordinal)) offenders.Add(name);
        }

        //the exclusion list must still match the key, so a green run rules out an exclusion that ate the whole tree
        Assert.True(scannedServerSide,
            "no excluded file carries model_path any more - the server's /props field was retired too, "
            + "and the server's field and the retired profile key must stay two different things");
        Assert.True(scannedAny, "the census scanned nothing - SourceTree.ProductionFiles found no files");
        Assert.Empty(offenders);
    }

    //match a string that really does contain the key and one that does not, so a green census means something
    [Fact]
    public void AND_THE_CENSUS_PATTERN_CAN_ACTUALLY_SEE_THE_KEY()
    {
        const string planted = "            w.WriteString(\"model_path\", resolved);";

        Assert.Contains(RetiredKey, planted, StringComparison.Ordinal);
        Assert.DoesNotContain(RetiredKey, "            w.WriteString(\"files\", resolved);", StringComparison.Ordinal);
    }

    //pin true, false and null, an oracle that only asks for not-true passes against one that calls every server unknown
    [Fact]
    public void MATCHES_LOADED_KEEPS_GATTOS_FILE_AND_THE_SERVERS_APART()
    {
        var model = new Model("m",
            new ModelProfile([new ModelFile(@"C:\weights\ours.gguf", "Q4_K_M", true)],
                1235, 4096, null, null, null, null, null, []),
            null, null, null);

        //three fixtures, one for each answer: they agree, they disagree, and the server reports nothing
        var same = new LoadedModel(@"C:\weights\ours.gguf", null);
        var other = new LoadedModel(@"C:\weights\theirs.gguf", null);
        var silent = new LoadedModel(null, null);

        Assert.True(Model.MatchesLoaded(model, same));
        Assert.False(Model.MatchesLoaded(model, other));
        //a server that has not reported its weights can't hold the wrong ones, so unknown answers null (a mismatch would warn forever)
        Assert.Null(Model.MatchesLoaded(model, silent));
        Assert.Null(Model.MatchesLoaded(model, null));
    }
}
