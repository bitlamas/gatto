using Gatto.Cli.Setup;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//a .gguf typed on the local shelf is the model the walk adds, its folder is swept and the named file adopted
public class TypedPathAdoptionTests
{
    private static string WriteGguf(string dir, string name)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "qwen3");
            kv.U32("qwen3.context_length", 4096);
        }));
        return path;
    }

    //the shelf opens the way gatto model opens it, and CanSwitchSource stays false so Discover reaches the local shelf. no Hub search joins the screen's history
    private static (SetupFlow Flow, WizardProbes Probes, WizardScreen.Choice Shelf) LocalShelf(
        params string[] found)
    {
        var probes = new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            Found = [.. found.Select(f =>
                new Gatto.Core.Acquire.FoundModel(f, 4_000_000_000, null))],
        };
        var flow = new SetupFlow(probes);
        return (flow, probes, Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment()));
    }

    //a typed .gguf is the model the flow writes, and the oracle is the write rather than the next screen
    [Fact]
    public void A_TYPED_GGUF_IS_THE_MODEL_THE_WALK_ADDS()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-typed-").FullName;
        try
        {
            var typed = WriteGguf(dir, "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf");
            //a model is already on the shelf, and the typed file is in the scan too, since the real sweep would find it
            var (flow, _, _) = LocalShelf(WriteGguf(dir, "something-else-Q4_K_M.gguf"), typed);

            flow.Answer(ShelfControls.TypedAnswer(typed));

            Assert.Equal(typed, flow.Writes.CreateModel?.GgufPath);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    //a typed folder is still swept, and the oracle is the root the scan was handed rather than the rows that came back
    [Fact]
    public void AND_A_TYPED_FOLDER_IS_STILL_SWEPT()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-typed-dir-").FullName;
        try
        {
            var (flow, probes, _) = LocalShelf(WriteGguf(dir, "qwen3-4b-Q4_K_M.gguf"));

            flow.Answer(ShelfControls.TypedAnswer(dir));

            Assert.Contains(dir, probes.ScanRoots);
            Assert.Null(flow.Writes.CreateModel);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }
}
