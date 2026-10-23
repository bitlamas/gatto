using Gatto.Core.Client;
using Gatto.Core.Session;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a launch row about this launch alone is shown and never saved, so a resumed session shows its own and no stale copy. a launch warning still saves
[Collection("e2e")]
public sealed class TransientLaunchRowsTests : IDisposable
{
    private const string Wild = "wild mode on, permission prompts are off for this session";
    private const string Sandbox = "sandbox on: the shell tool and derivatives cannot write outside the project folder";
    private const string Warning = "a launch warning that is saved";
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-rows-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-rows-cwd-").FullName;

    public void Dispose()
    {
        foreach (var d in new[] { _home, _cwd })
            try { Directory.Delete(d, recursive: true); } catch (Exception) { }
    }

    private static int Count(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    //one session with a turn, so its file holds what a resume replays
    private async Task<string> LaunchSessionAsync()
    {
        var store = new SessionStore(_home, _cwd);
        var h = new RichReplHarness(_cwd, sessions: store, wildNotice: Wild, sandboxNotice: Sandbox, launchWarnings: [Warning]);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("go").Line("/quit");
        await h.RunAsync();
        Assert.Contains(Wild, h.ScreenText(), StringComparison.Ordinal);
        Assert.Contains(Sandbox, h.ScreenText(), StringComparison.Ordinal);
        return store.CurrentPath!;
    }

    [Fact]
    public async Task THE_WILD_AND_SANDBOX_NOTICES_ARE_SHOWN_AND_NEVER_SAVED_AND_A_WARNING_IS()
    {
        var saved = File.ReadAllText(await LaunchSessionAsync());

        Assert.DoesNotContain(Wild, saved, StringComparison.Ordinal);
        Assert.DoesNotContain(Sandbox, saved, StringComparison.Ordinal);
        Assert.Contains(Warning, saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_RESUME_OF_A_WILD_SANDBOXED_SESSION_SHOWS_EACH_NOTICE_ONCE()
    {
        var path = await LaunchSessionAsync();

        var resumed = new SessionStore(_home, _cwd);
        var h = new RichReplHarness(_cwd, sessions: resumed, wildNotice: Wild, sandboxNotice: Sandbox, resumedFrom: "earlier", resumedPath: path, height: 60);
        h.Keys.Line("/quit");
        await h.RunAsync();

        var screen = h.ScreenText();
        Assert.Contains("resumed from earlier", screen, StringComparison.Ordinal);
        Assert.Equal(1, Count(screen, Wild));
        Assert.Equal(1, Count(screen, Sandbox));
    }
}
