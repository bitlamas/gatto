using System.Runtime.Versioning;
using Gatto.Cli;
using Gatto.Core.Sandbox;
using Gatto.Terminal;

namespace Gatto.Tests.Sandbox;

//the residue sweep and the plant on the launch's live line, on folders the test makes and deletes whole
[SupportedOSPlatform("windows")]
public sealed class ResidueLiveLineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-residue-line-").FullName;
    private string Project => Path.Combine(_root, "project");
    private string Home => Path.Combine(_root, "home");

    public ResidueLiveLineTests()
    {
        foreach (var d in new[] { "project", "home", "nuget", "http-cache", "tmp" }) Directory.CreateDirectory(Path.Combine(_root, d));
    }

    public void Dispose()
    {
        try { SandboxGrants.Unplant(Project); } catch (Exception) { }
        try { Directory.Delete(_root, true); } catch (Exception) { }
    }

    private SandboxState State() => new(Project, Home, [],
        nugetFolder: Path.Combine(_root, "nuget"), httpCache: Path.Combine(_root, "http-cache"), tempParent: Path.Combine(_root, "tmp"));

    [Fact]
    public async Task LEFT_LABELS_ARE_REMOVED_ON_THE_LIVE_LINE_AND_THE_LINE_COMES_DOWN()
    {
        SandboxGrants.Plant(Project, Home);
        using var state = State();
        var sink = new StringWriter();
        using var line = new TickLine(sink, rich: true, windowWidth: () => 120);

        var said = await GattoApp.PrepareSandboxAsync(state, source: null, line, GlyphSet.Unicode, theme: null);

        Assert.Equal(SandboxState.ResidueLine, said);
        Assert.False(SandboxGrants.Holds(Project));
        var drawn = sink.ToString();
        Assert.Contains("removing labels left by an earlier session…", drawn, StringComparison.Ordinal);
        Assert.EndsWith("\r", drawn, StringComparison.Ordinal);
        Assert.Equal("", drawn.Split('\r')[^2].Trim());
    }

    [Fact]
    public async Task NOTHING_LEFT_WITH_THE_MODE_OFF_DRAWS_NOTHING()
    {
        using var state = State();
        var sink = new StringWriter();
        using var line = new TickLine(sink, rich: true, windowWidth: () => 120, blankBeforeFirst: true);

        Assert.Null(await GattoApp.PrepareSandboxAsync(state, source: null, line, GlyphSet.Unicode, theme: null));
        Assert.Equal("", sink.ToString());
    }

    [Fact]
    public async Task A_CLEAN_START_PLANTS_ON_THE_LIVE_LINE_AND_THE_LINE_COMES_DOWN()
    {
        using var state = State();
        var sink = new StringWriter();
        using var line = new TickLine(sink, rich: true, windowWidth: () => 120);

        Assert.Null(await GattoApp.PrepareSandboxAsync(state, source: "--sandbox", line, GlyphSet.Unicode, theme: null));

        Assert.True(state.On, state.Line);
        Assert.True(SandboxGrants.Holds(Project));
        var drawn = sink.ToString();
        Assert.Contains("preparing the sandbox\u2026", drawn, StringComparison.Ordinal);
        Assert.DoesNotContain("removing labels", drawn, StringComparison.Ordinal);
        Assert.EndsWith("\r", drawn, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LEFT_LABELS_AND_A_START_SHOW_THE_RESIDUE_WORDS_ALONE()
    {
        SandboxGrants.Plant(Project, Home);
        using var state = State();
        var sink = new StringWriter();
        using var line = new TickLine(sink, rich: true, windowWidth: () => 120);

        Assert.Equal(SandboxState.ResidueLine, await GattoApp.PrepareSandboxAsync(state, source: "--sandbox", line, GlyphSet.Unicode, theme: null));

        Assert.True(state.On, state.Line);
        var drawn = sink.ToString();
        Assert.Contains("removing labels left by an earlier session", drawn, StringComparison.Ordinal);
        Assert.DoesNotContain("preparing the sandbox", drawn, StringComparison.Ordinal);
    }
}
