using Gatto.Cli;
using Gatto.Cli.Launch;
using Gatto.Core;
using Gatto.Core.Home;
using Gatto.Core.Session;

namespace Gatto.Tests.Cli;

//the launch state names the session it is handed or the store's live one, and remembers neither, so a /new that never commits leaves no id behind
public class LaunchStateSessionTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-launch-session-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-launch-session-cwd-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_home, true); } catch { }
        try { Directory.Delete(_cwd, true); } catch { }
    }

    private LaunchState Launched(SessionStore store)
    {
        Directory.CreateDirectory(Path.Combine(_home, "roles"));
        File.WriteAllText(Path.Combine(_home, "roles", "generalist.json"), "{\"model\":\"test-model\"}");
        var modelDir = Path.Combine(_home, "models", "test-model");
        Directory.CreateDirectory(modelDir);
        File.WriteAllText(Path.Combine(modelDir, "profile.json"), """{"files":[{"path":"m.gguf","active":true}],"port":1235,"context":8192}""");
        File.WriteAllText(Path.Combine(_home, "gatto.json"), """{"endpoints":{"local":{}},"default_endpoint":"local","default_model":"test-model"}""");
        var config = GattoConfig.Load(_home);
        var state = new LaunchState(new ParsedArgs("launch", null, null, "", false, false), config, _home, _cwd,
            Path.Combine(_home, "roles"), Path.Combine(_home, "models"), config.Endpoints, null, null, new WarningSink(_ => { }),
            false, [], Gatto.Terminal.GlyphSet.Unicode, sessionOf: () => store.Identity.IdText);
        var res = state.ResolveRole("generalist");
        state.Role = res.Role;
        state.Model = res.Model;
        state.Endpoint = res.Endpoint;
        state.EndpointName = res.EndpointName;
        state.ModelString = res.ModelString;
        state.Comp = res.Comp;
        return state;
    }

    //a /new composes with the identity it is about to start, and when its rotation never commits the next compaction names the session still live
    [Fact]
    public void A_NEW_THAT_NEVER_COMMITS_LEAVES_THE_NEXT_COMPOSITION_NAMING_THE_LIVE_SESSION()
    {
        var store = new SessionStore(_home, _cwd);
        var state = Launched(store);
        var live = store.Identity.IdText;

        var next = store.NewIdentity();
        var forNext = state.RecomposeSystem(next).Text;
        var compaction = state.RecomposeSystem().Text;

        Assert.NotEqual(live, next.IdText);
        Assert.EndsWith($"This is session {next.IdText}.", forNext, StringComparison.Ordinal);
        Assert.EndsWith($"This is session {live}.", compaction, StringComparison.Ordinal);
        Assert.StartsWith("You are test-model, running through gatto, a local agent harness, on ", state.Comp.SystemText, StringComparison.Ordinal);
    }

    //once the store starts the planned identity, a composition with no argument names it, the file and the prompt agree
    [Fact]
    public void A_COMMITTED_NEW_IS_THE_SESSION_THE_NEXT_COMPOSITION_NAMES()
    {
        var store = new SessionStore(_home, _cwd);
        var state = Launched(store);

        var next = store.NewIdentity();
        state.RecomposeSystem(next);
        store.StartNew(next);

        Assert.Equal(next, store.Identity);
        Assert.EndsWith($"This is session {next.IdText}.", state.RecomposeSystem().Text, StringComparison.Ordinal);
    }
}
