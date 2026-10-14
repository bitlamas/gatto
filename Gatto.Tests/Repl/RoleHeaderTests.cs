using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//the rule names the role only when it is not the home's default, and a bare /role names the running one
[Collection("e2e")]
public class RoleHeaderTests
{
    //the composer's top rule in the last frame, found by the brand word on a rule row
    private static string TopRule(RichReplHarness h) =>
        h.FinalFrame().Last(r => r.Contains("─", StringComparison.Ordinal) && r.Contains("gatto", StringComparison.Ordinal)).TrimEnd();

    //succeeds for the two shipped names with a marker system text, and records each name it was asked for
    private static Func<string, RoleSwitchResult> Switch(List<string> asked) => name =>
    {
        asked.Add(name);
        return name.ToLowerInvariant() is "coder" or "generalist"
            ? new RoleSwitchResult(true, name.ToLowerInvariant(), "test-model", "the " + name + " marker", null)
            : new RoleSwitchResult(false, "", null, null, "unknown role '" + name + "'");
    };

    [Fact]
    public async Task THE_RULE_READS_GATTO_ALONE_ON_THE_DEFAULT_ROLE()
    {
        var h = new RichReplHarness(Path.GetTempPath(), defaultRole: "generalist");
        h.Keys.Line("/quit");
        await h.RunAsync();

        Assert.EndsWith(" gatto ──", TopRule(h), StringComparison.Ordinal);
        //nothing the session ever wrote holds the long form, not only the last frame
        Assert.DoesNotContain("gatto · generalist ─", h.Written(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ROLE_CODER_PUTS_THE_NAME_ON_THE_RULE_AND_ROLE_GENERALIST_TAKES_IT_OFF()
    {
        var asked = new List<string>();
        var h = new RichReplHarness(Path.GetTempPath(), defaultRole: "generalist", switchRole: Switch(asked));
        h.Keys.Line("/role coder");
        await h.RunUntilAsync(() => h.ScreenText().Contains(" gatto · coder ──", StringComparison.Ordinal));

        var back = new RichReplHarness(Path.GetTempPath(), defaultRole: "generalist", switchRole: Switch(asked));
        back.Keys.Line("/role coder").Line("/role generalist").Line("/quit");
        await back.RunAsync();

        Assert.EndsWith(" gatto ──", TopRule(back), StringComparison.Ordinal);
        Assert.Equal(["coder", "coder", "generalist"], asked);
    }

    [Fact]
    public async Task WITH_DEFAULT_ROLE_CODER_THE_RULE_IS_QUIET_ON_CODER_AND_NAMES_GENERALIST()
    {
        var asked = new List<string>();
        var h = new RichReplHarness(Path.GetTempPath(), defaultRole: "coder", switchRole: Switch(asked));
        h.Keys.Line("/role coder");
        await h.RunUntilAsync(() => h.Written().Contains("role: coder", StringComparison.Ordinal));
        Assert.EndsWith(" gatto ──", TopRule(h), StringComparison.Ordinal);

        var g = new RichReplHarness(Path.GetTempPath(), defaultRole: "coder", switchRole: Switch(asked));
        g.Keys.Line("/role coder").Line("/role generalist").Line("/quit");
        await g.RunAsync();
        Assert.EndsWith(" gatto · generalist ──", TopRule(g), StringComparison.Ordinal);
    }

    //gatto.json's casing and the file's casing may differ, and the launch resolves either, so the rule compares as the launch does
    [Fact]
    public async Task A_DEFAULT_ROLE_CASED_UNLIKE_ITS_FILE_STILL_READS_GATTO_ALONE()
    {
        var asked = new List<string>();
        var h = new RichReplHarness(Path.GetTempPath(), defaultRole: "Coder", switchRole: Switch(asked));
        h.Keys.Line("/role coder").Line("/quit");
        await h.RunAsync();

        Assert.EndsWith(" gatto ──", TopRule(h), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_BARE_ROLE_PRINTS_THE_RUNNING_ROLE_AND_SWITCHES_NOTHING()
    {
        var asked = new List<string>();
        var h = new RichReplHarness(Path.GetTempPath(), defaultRole: "generalist", switchRole: Switch(asked));
        h.Keys.Line("/role").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw("role: generalist"), h.Written());
        Assert.Empty(asked);
        Assert.Equal("sys", h.Convo.Messages[0].Content);
        Assert.Equal("test-model", h.ModelName);
    }
}
