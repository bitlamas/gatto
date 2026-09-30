using Gatto.Cli;

namespace Gatto.Tests;

public class ContextWarningTests
{
    [Fact]
    public void Warns_precisely_when_the_resolved_budget_is_null()
    {
        //the Compose call sees only the resolved budget and nothing about the probe, so no probe outcome can suppress the warning
        Assert.NotNull(ContextWarning.Compose(null));
        Assert.Null(ContextWarning.Compose(8192));
    }

    [Fact]
    public void Warning_names_the_three_features_and_the_fix_key()
    {
        var w = ContextWarning.Compose(null)!;
        //the warning must name what is off and the key that fixes it, a bare context unknown names neither
        Assert.Contains("elision", w);
        Assert.Contains("compact", w);          //one substring covers both the 85 percent offer and the auto-compaction wording.
        Assert.Contains("context", w);
        Assert.Contains("endpoints", w);        //the substring stands for the endpoints.<name>.context fix key
    }
}
