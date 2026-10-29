using Gatto.Core.Tools;

namespace Gatto.Tests;

public class ArmedSourcesTests
{
    [Fact]
    public void A_NULL_ORIGIN_IS_A_BUILT_IN_AND_ALWAYS_ON()
    {
        var armed = new ArmedSources(["web_search"]);
        Assert.True(armed.IsOn(null));
        Assert.False(armed.IsOn("web_search"));
        Assert.True(armed.IsOn("undo"));
    }

    [Fact]
    public void OFF_THEN_ON_AND_THE_OFF_LIST_IS_SORTED()
    {
        var armed = new ArmedSources();
        armed.Set("web_search", on: false);
        armed.Set("cards", on: false);
        Assert.Equal(["cards", "web_search"], armed.Off);
        armed.Set("web_search", on: true);
        Assert.True(armed.IsOn("web_search"));
        Assert.Equal(["cards"], armed.Off);
    }
}
