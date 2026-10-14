using Gatto.Core.Loop.Permissions;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//safe is the off half of a bare wild for this session, and never under a home-wide true says the next launch is wild again
[Collection("e2e")]
public class SafeCommandTests
{
    //the composer's top rule in the last frame, found by the brand word on a rule row
    private static string TopRule(RichReplHarness h) =>
        h.FinalFrame().Last(r => r.Contains("─", StringComparison.Ordinal) && r.Contains("gatto", StringComparison.Ordinal));

    [Fact]
    public async Task SAFE_TURNS_WILD_OFF_FOR_THE_SESSION_AND_THE_RULE_LOSES_THE_WORD()
    {
        var wild = new WildState { On = true };
        var h = new RichReplHarness(Path.GetTempPath(), wild: wild);
        h.Keys.Line("/safe").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw(Gatto.Repl.Repl.WildLine(false, null)), h.Written());
        Assert.False(wild.On);
        Assert.DoesNotContain("wild", TopRule(h), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SAFE_WHEN_WILD_IS_OFF_SAYS_SO_AND_CHANGES_NOTHING()
    {
        var wild = new WildState { On = true };
        var h = new RichReplHarness(Path.GetTempPath(), wild: wild);
        h.Keys.Line("/safe").Line("/safe").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw("wild mode is off"), h.Written());
        Assert.False(wild.On);
    }

    [Fact]
    public async Task SAFE_LEAVES_THE_CHECKPOINT_TOGGLE_ALONE()
    {
        var calls = 0;
        var wild = new WildState { On = true };
        var h = new RichReplHarness(Path.GetTempPath(), wild: wild, toggleAuto: () => { calls++; return true; });
        h.Keys.Line("/safe").Line("/quit");
        await h.RunAsync();

        Assert.False(wild.On);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task WILD_NEVER_UNDER_A_HOME_WIDE_TRUE_PRINTS_THE_NEXT_LAUNCH_LINE()
    {
        var persisted = new List<bool>();
        var wild = new WildState { On = true };
        var h = new RichReplHarness(Path.GetTempPath(), wild: wild, wildFromHome: true,
            persistWild: persisted.Add, wildPersisted: () => false);
        h.Keys.Line("/wild never").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw("wild mode off for this session, gatto.json sets \"wild\": true, so it is on again next launch"), h.Written());
        Assert.False(wild.On);
        Assert.Equal([false], persisted);
    }

    [Fact]
    public async Task WILD_NEVER_UNDER_A_HOME_WIDE_TRUE_STILL_SAYS_A_GRANT_WAS_REMOVED()
    {
        var wild = new WildState { On = true };
        var h = new RichReplHarness(Path.GetTempPath(), wild: wild, wildFromHome: true, persistWild: _ => { }, wildPersisted: () => true);
        h.Keys.Line("/wild never").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw("so it is on again next launch"), h.Written());
        Assert.True(h.Saw("persistence removed from your gatto home"), h.Written());
    }

    //a project's .gatto.json keeps every launch in that folder off, so the next launch is not wild there
    [Fact]
    public async Task WILD_NEVER_UNDER_A_PROJECT_VETO_PROMISES_NO_WILD_NEXT_LAUNCH()
    {
        var wild = new WildState { On = false, ForbiddenBy = Path.Combine(Path.GetTempPath(), ".gatto.json") };
        var h = new RichReplHarness(Path.GetTempPath(), wild: wild, wildFromHome: true, persistWild: _ => { }, wildPersisted: () => false);
        h.Keys.Line("/wild never").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw(Gatto.Repl.Repl.WildLine(false, null)), h.Written());
        Assert.False(h.Saw("on again next launch"), h.Written());
    }

    //gatto.json can start the session wild with no grant stored, and the listing says what the session is
    [Theory]
    [InlineData("/permissions")]
    [InlineData("/permissions all")]
    public async Task PERMISSIONS_SAYS_THE_SESSION_IS_WILD_UNDER_A_HOME_WIDE_TRUE(string command)
    {
        var root = Directory.CreateTempSubdirectory("gatto-safe-perm-").FullName;
        try
        {
            var store = PermissionStore.Load(root, root, out _);
            var h = new RichReplHarness(root, wild: new WildState { On = true }, wildFromHome: true, permissions: store);
            h.Keys.Line(command).Line("/quit");
            await h.RunAsync();

            Assert.True(h.Saw("wild mode: on"), h.Written());
            Assert.False(store.Wild);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [Fact]
    public async Task WILD_NEVER_WITHOUT_A_GRANT_PRINTS_NO_REMOVAL_ROW()
    {
        var wild = new WildState { On = true };
        var h = new RichReplHarness(Path.GetTempPath(), wild: wild, persistWild: _ => { }, wildPersisted: () => false);
        h.Keys.Line("/wild never").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw(Gatto.Repl.Repl.WildLine(false, null)), h.Written());
        Assert.False(h.Saw("persistence removed"), h.Written());
    }

    [Fact]
    public async Task WILD_NEVER_WITH_A_GRANT_PRINTS_THE_REMOVAL_ROW()
    {
        var wild = new WildState { On = true };
        var h = new RichReplHarness(Path.GetTempPath(), wild: wild, persistWild: _ => { }, wildPersisted: () => true);
        h.Keys.Line("/wild never").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw("persistence removed from your gatto home"), h.Written());
    }
}
