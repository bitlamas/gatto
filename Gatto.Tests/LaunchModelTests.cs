using Gatto.Cli;

namespace Gatto.Tests;

public class LaunchModelTests
{
    private static readonly string[] Acme = ["acme-lite", "acme-max"];

    [Fact]
    public void An_explicit_model_wins_over_the_saved_one() =>
        Assert.Equal(new LaunchModel.Choice("acme-lite", null), LaunchModel.Resolve("acme", Acme, "acme-lite", "acme-max"));

    [Fact]
    public void The_saved_model_wins_over_the_first_listed() =>
        Assert.Equal(new LaunchModel.Choice("acme-max", null), LaunchModel.Resolve("acme", Acme, null, "acme-max"));

    [Fact]
    public void Nothing_saved_takes_the_first_listed() =>
        Assert.Equal(new LaunchModel.Choice("acme-lite", null), LaunchModel.Resolve("acme", Acme, null, null));

    [Fact]
    public void A_stale_saved_model_takes_the_first_listed_and_says_which()
    {
        var c = LaunchModel.Resolve("acme", Acme, null, "acme-ultra");
        Assert.Equal("acme-lite", c.Model);
        Assert.Contains("acme-ultra", c.Warning);
        Assert.Contains("acme-lite", c.Warning);
    }

    [Fact]
    public void An_unlisted_endpoint_takes_the_saved_model_unchecked() =>
        Assert.Equal(new LaunchModel.Choice("anything", null), LaunchModel.Resolve("or", null, null, "anything"));

    [Fact]
    public void An_unlisted_endpoint_with_nothing_saved_has_no_model() =>
        Assert.Null(LaunchModel.Resolve("or", null, null, null).Model);
}
