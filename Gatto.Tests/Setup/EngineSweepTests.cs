using Gatto.Roles;
using Xunit;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

//sweep tests run against a fake root, sweeping a real drive would make results depend on what the machine has installed.
public class EngineSweepTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-sweep-").FullName;

    public void Dispose() { try { Directory.Delete(_root, true); } catch (Exception) { } }

    private string Engine(string relativeDir)
    {
        var dir = Path.Combine(_root, relativeDir);
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, EngineSearch.ExeName);
        File.WriteAllText(exe, "not a real binary");
        return exe;
    }

    //keep only the paths, most tests check which files are found rather than the provenance beside them.
    private static IReadOnlyList<string> Paths(IEnumerable<FoundEngine> found) =>
        [.. found.Select(f => f.Path)];

    private string At(string relativeDir) => Path.Combine(_root, relativeDir);

    [Fact]
    public void IT_FINDS_AN_ENGINE_ON_PATH()
    {
        var exe = Engine("tools");

        Assert.Equal([exe], Paths(EngineSearch.Find([At("tools")], null, null, configured: null)));
    }

    //gatto's own downloads sit one level in, at ~\.gatto\llama\<build>\.
    [Fact]
    public void IT_FINDS_AN_ENGINE_GATTO_FETCHED()
    {
        var exe = Engine(Path.Combine("llama", "b10076"));

        Assert.Equal([exe], Paths(EngineSearch.Find(null, At("llama"), null, configured: null)));
    }

    //cover the extracted-zip layout, a build folder under llama that keeps its own name.
    [Fact]
    public void IT_FINDS_AN_EXTRACTED_ZIP_UNDER_A_LLAMA_FOLDER()
    {
        var exe = Engine(Path.Combine("llama", "llama-b10076-bin-win-vulkan-x64"));

        Assert.Equal([exe], Paths(EngineSearch.Find(null, null, _root, configured: null)));
    }

    //never sweep Downloads, an engine there is not installed, and three deep is past the limit. the negatives are real files, a found engine proves the sweep ran.
    [Fact]
    public void IT_DOES_NOT_SWEEP_DOWNLOADS_OR_THREE_DEEP()
    {
        var wanted = Engine(Path.Combine("llama", "b10076"));
        Engine("Downloads");
        Engine(Path.Combine("llama", "one", "two"));
        Engine(Path.Combine("x", "y", "z"));

        var found = Paths(EngineSearch.Find(null, At("llama"), _root, configured: null));

        Assert.Contains(wanted, found);                                   //prove the sweep found the wanted engine, so the negatives are refusals rather than an empty sweep.
        Assert.DoesNotContain(found, f => f.Contains("Downloads", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, f => f.Contains(Path.Combine("one", "two"), StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, f => f.Contains(Path.Combine("y", "z"), StringComparison.OrdinalIgnoreCase));
    }

    //the drive arm enters only folders named like llama, even at depth one, since extracted engines live there.
    [Fact]
    public void THE_DRIVE_ARM_ONLY_LOOKS_INSIDE_LLAMA_FOLDERS()
    {
        Engine(Path.Combine("tools", "llama-b10076-bin-win-vulkan-x64"));

        Assert.Empty(Paths(EngineSearch.Find(null, null, _root, configured: null)));
    }

    //report one file once, even when two search arms reach it, otherwise the found screen offers a path twice.
    [Fact]
    public void THE_SAME_FILE_REACHED_TWICE_IS_REPORTED_ONCE()
    {
        var exe = Engine(Path.Combine("llama", "b10076"));

        var found = Paths(EngineSearch.Find([At(Path.Combine("llama", "b10076"))], At("llama"), null, configured: null));

        Assert.Equal([exe], found);
    }

    //the sweep records provenance only for engines under gatto's own llama root. a folder name unlike the binary's banner means a hand-placed build.
    [Fact]
    public void AN_ENGINE_IN_GATTOS_OWN_FOLDER_CARRIES_THAT_FOLDERS_NAME()
    {
        Engine(Path.Combine("llama", "laguna-2bd55d0"));

        var found = Assert.Single(EngineSearch.Find(null, At("llama"), null, configured: null));

        Assert.Equal("laguna-2bd55d0", found.GattoFolder);
    }

    //an engine found outside gatto's own llama root gets no provenance. a folder name there would fire the fork arm on a directory that only looks hand-named.
    [Fact]
    public void ENGINES_FOUND_ELSEWHERE_CARRY_NO_PROVENANCE()
    {
        Engine("tools");
        Engine(Path.Combine("llama-side", "laguna-2bd55d0"));

        var found = EngineSearch.Find([At("tools")], null, _root, configured: null);

        Assert.NotEmpty(found);
        Assert.All(found, f => Assert.Null(f.GattoFolder));
    }

    //the home arm must run before PATH, or de-duplication drops the folder name. the count can't tell the orders apart, so assert the field instead.
    [Fact]
    public void A_HOME_ENGINE_ALSO_ON_PATH_KEEPS_ITS_PROVENANCE()
    {
        Engine(Path.Combine("llama", "laguna-2bd55d0"));

        var found = Assert.Single(EngineSearch.Find(
            [At(Path.Combine("llama", "laguna-2bd55d0"))], At("llama"), null, configured: null));

        Assert.Equal("laguna-2bd55d0", found.GattoFolder);
    }

    //the sweep skips junk PATH entries such as an unmounted drive or an empty string. none of them throws, so this checks the skip rather than exception-safety.
    [Fact]
    public void JUNK_ON_PATH_IS_SKIPPED_AND_THE_GOOD_ENTRY_STILL_ARRIVES()
    {
        var exe = Engine("tools");

        var found = Paths(EngineSearch.Find(
            [@"Z:\not-mounted", "", "   ", "a\0b", At("tools")], null, null, configured: null));

        Assert.Equal([exe], found);
    }

    //return an empty list, a null would make "none" and "did not look" the same answer to a count.
    [Fact]
    public void NOTHING_FOUND_IS_AN_EMPTY_LIST() =>
        Assert.Empty(Paths(EngineSearch.Find([At("nowhere")], At("nope"), _root, configured: null)));

    //the default EngineSweep derives from LlamaServerPath and must be removed with it, so the removal commit cannot build without this test.
    [Fact]
    public void THE_DEFAULT_SWEEP_STILL_DERIVES_FROM_THE_METHOD_IT_REPLACES()
    {
        const string exe = @"C:\llama\llama-server.exe";
        Gatto.Cli.Setup.ISetupProbes probes = new Gatto.Tests.Fakes.WizardProbes { Llama = exe };

        Assert.Equal(exe, probes.LlamaServerPath());

        var swept = Assert.Single(probes.EngineSweep());
        Assert.Equal(exe, swept.Path);
        Assert.Null(swept.Build);      //the sweep never runs the engine, so the build field stays unset.

        //check the empty case too, so a stub that always answers one fails here.
        Gatto.Cli.Setup.ISetupProbes none = new Gatto.Tests.Fakes.WizardProbes { Llama = null };
        Assert.Null(none.LlamaServerPath());
        Assert.Empty(none.EngineSweep());
    }

    //an engine the config names must reach the sweep even when every other arm misses it, else the flow offers to download a working engine.
    [Fact]
    public void THE_CONFIGURED_ENGINE_IS_FOUND_EVEN_WHEN_NO_OTHER_ARM_REACHES_IT()
    {
        var exe = Engine("somewhere-else");

        var found = Paths(EngineSearch.Find(null, At("llama"), null, configured: exe));

        Assert.Equal([exe], found);
    }

    //the home arm must see a configured engine before the configured arm does, or de-duplication drops the folder name.
    [Fact]
    public void A_CONFIGURED_ENGINE_UNDER_GATTOS_OWN_ROOT_KEEPS_ITS_PROVENANCE()
    {
        var exe = Engine(Path.Combine("llama", "laguna-2bd55d0"));

        var found = Assert.Single(EngineSearch.Find(null, At("llama"), null, configured: exe));

        Assert.Equal("laguna-2bd55d0", found.GattoFolder);
    }

    //a configured path that no longer exists is not an engine. every arm passes the same existence check, so a stale entry cannot put a row on a screen.
    [Fact]
    public void A_CONFIGURED_PATH_THAT_IS_GONE_IS_NOT_SWEPT() =>
        Assert.Empty(EngineSearch.Find(null, null, null, configured: At("deleted\\llama-server.exe")));

    //the test drives the LiveSetupProbes.EngineSweep probe, a search-only guard would miss it. the home holds no llama folder and names an engine elsewhere.
    [Fact]
    public void THE_LIVE_SWEEP_ASKS_FOR_THE_CONFIGURED_ENGINE()
    {
        var home = Path.Combine(_root, "home");
        Gatto.Core.Home.GattoHome.EnsureInitialized(home);
        var exe = Engine("elsewhere");
        File.WriteAllText(Path.Combine(home, "gatto.json"),
            """{"endpoints":{"local":{"base_url":"http://x"}},"default_endpoint":"local","llama_server":"""
            + System.Text.Json.JsonSerializer.Serialize(exe) + "}");

        using var probes = new Gatto.Cli.Setup.LiveSetupProbes(home, GlyphSet.Unicode, TextWriter.Null);

        Assert.False(Directory.Exists(Path.Combine(home, "llama")),
            "the fixture's home has its own llama folder, so it no longer discriminates");
        Assert.Equal(exe, probes.LlamaServerPath());
        Assert.Contains(exe, Paths(probes.EngineSweep()));
    }
}
