using System.Text.Json;
using Gatto.Cli;
using Gatto.Cli.Launch;
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Roles.Models;

namespace Gatto.Tests;

//d and the effort picker on a contributed endpoint write its extension's companion file, and every reader asks the owner of the endpoint it resolves
public sealed class ContributedDefaultsSwitchTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-contrib-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-contrib-cwd-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private static readonly IReadOnlyDictionary<string, JsonElement?> Levels = new Dictionary<string, JsonElement?>
    {
        ["none"] = null,
        ["low"] = JsonDocument.Parse("""{"reasoning_effort":"low"}""").RootElement.Clone(),
        ["high"] = JsonDocument.Parse("""{"reasoning_effort":"high"}""").RootElement.Clone(),
    };

    private string GattoJson => Path.Combine(_home, "gatto.json");

    private void Companion(string name, string json)
    {
        Directory.CreateDirectory(Path.Combine(_home, "extensions"));
        File.WriteAllText(Path.Combine(_home, "extensions", name + ".json"), json);
    }

    //two extensions, hostext contributing hosted and secondext contributing second, beside the configured local endpoint
    private LaunchState State(string onEndpoint)
    {
        Directory.CreateDirectory(Path.Combine(_home, "roles"));
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), "{}");
        File.WriteAllText(Path.Combine(_home, "roles", "second.json"), """{"endpoint":"second","model":"s1"}""");
        File.WriteAllText(GattoJson, """{"endpoints":{"local":{}},"default_endpoint":"hosted"}""");
        var config = GattoConfig.Load(_home);
        var endpoints = new Dictionary<string, EndpointConfig>(config.Endpoints, StringComparer.Ordinal)
        {
            ["hosted"] = new("https://hosted.example.test/v1", Thinking: Levels, Models: ["h1", "h2"], Owner: "hostext"),
            ["second"] = new("https://second.example.test/v1", Thinking: Levels, Models: ["s1", "s2"], Owner: "secondext"),
        };
        var state = new LaunchState(new ParsedArgs("launch", null, null, "", false, false), config, _home, _cwd,
            Path.Combine(_home, "roles"), Path.Combine(_home, "models"), endpoints, null, null, new WarningSink(_ => { }),
            false, [], Gatto.Terminal.GlyphSet.Unicode);
        state.EndpointName = onEndpoint;
        state.Endpoint = endpoints[onEndpoint];
        state.ModelString = endpoints[onEndpoint].Models![0];
        return state;
    }

    [Fact]
    public void d_on_a_contributed_endpoint_writes_the_companion_and_the_picker_marks_it()
    {
        var state = State("hosted");
        var before = File.ReadAllBytes(GattoJson);

        Assert.Null(ModelSwitch.Persist(_home, "hosted", state.OwnerOf("hosted"), "h2"));

        Assert.Equal("h2", state.CurrentDefaultFor("hosted"));
        Assert.Equal(before, File.ReadAllBytes(GattoJson));
        var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "extensions", "hostext.json"))).RootElement;
        Assert.Equal("h2", saved.GetProperty("defaults").GetProperty("hosted").GetProperty("model").GetString());
    }

    [Fact]
    public void SetModelDefaultEffort_on_a_contributed_endpoint_writes_the_companion()
    {
        var state = State("hosted");
        var before = File.ReadAllBytes(GattoJson);

        Assert.Null(state.SetModelDefaultEffort("h2", "high"));

        Assert.Equal(ThinkingLevel.High, state.SavedCloudEffortOrNull("h2"));
        Assert.Equal(before, File.ReadAllBytes(GattoJson));
        var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "extensions", "hostext.json"))).RootElement;
        Assert.Equal("high", saved.GetProperty("defaults").GetProperty("hosted").GetProperty("effort").GetProperty("h2").GetString());
    }

    [Fact]
    public void A_child_on_a_second_contributed_endpoint_reads_its_own_owners_effort()
    {
        Companion("hostext", """{"defaults":{"hosted":{"effort":{"s1":"low"}}}}""");
        Companion("secondext", """{"defaults":{"second":{"effort":{"s1":"high"}}}}""");
        var state = State("hosted");

        var resolved = state.ResolveRole("second");

        Assert.Equal("second", resolved.EndpointName);
        Assert.Equal(ThinkingLevel.High, resolved.Comp.Thinking);
        Assert.Equal(ThinkingLevel.High,
            LaunchState.SavedCloudEffort(null, _home, "second", state.OwnerOf("second"), "s1"));
        Assert.Equal(ThinkingLevel.Low, state.SavedCloudEffortOrNull("s1"));   //the session on hosted reads its own owner's row, not the child's
    }

    [Fact]
    public void The_local_picker_mark_never_reads_a_companion()
    {
        Companion("hostext", """{"defaults":{"local":{"model":"wrong"}}}""");
        var state = State("hosted");
        File.WriteAllText(GattoJson, """{"endpoints":{"local":{}},"default_endpoint":"hosted","defaults":{"local":{"model":"right"}}}""");

        Assert.Equal("right", state.CurrentDefaultFor("local"));
    }

    [Fact]
    public void A_live_entry_narrows_the_picker_row_to_its_levels_and_its_default()
    {
        //the row steps through the levels /effort would take for that model, and lands on the entry's own default when nothing is saved
        Directory.CreateDirectory(Path.Combine(_home, "roles"));
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), "{}");
        File.WriteAllText(GattoJson, """{"endpoints":{"local":{}},"default_endpoint":"hosted"}""");
        var config = GattoConfig.Load(_home);
        var map = new Dictionary<string, JsonElement?>(Levels)
        {
            ["medium"] = JsonDocument.Parse("""{"reasoning_effort":"medium"}""").RootElement.Clone(),
        };
        var endpoints = new Dictionary<string, EndpointConfig>(config.Endpoints, StringComparer.Ordinal)
        {
            ["hosted"] = new("https://hosted.example.test/v1", Thinking: map, Models: ["h1"], Owner: "hostext"),
        };
        var live = new ModelCatalogue([new ModelEntry("c1", Efforts: ["low", "high"], DefaultEffort: "high"), new ModelEntry("c2")]);
        var state = new LaunchState(new ParsedArgs("launch", null, null, "", false, false), config, _home, _cwd,
            Path.Combine(_home, "roles"), Path.Combine(_home, "models"), endpoints, null, null, new WarningSink(_ => { }),
            false, [], Gatto.Terminal.GlyphSet.Unicode, catalogue: live, catalogueFor: "hosted")
        {
            EndpointName = "hosted", Endpoint = endpoints["hosted"], ModelString = "c1",
        };

        var rows = state.ListModels();

        Assert.Equal(["c1", "c2"], rows.Select(r => r.Id));
        Assert.Equal(["low", "high"], rows[0].Levels);
        Assert.Equal("high", rows[0].DefaultEffort);
        Assert.Equal(["none", "low", "medium", "high"], rows[1].Levels);
        Assert.Equal("medium", rows[1].DefaultEffort);
    }
}
