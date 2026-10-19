using Gatto.Cli.Launch;
using Gatto.Core;

namespace Gatto.Tests.Cli;

//the launch's role precedence as a table, every row read from the code and not from its comments, so a reader can check any case by its line
public class LaunchRoleTests
{
    private static readonly string[] Disk = ["generalist", "coder", "oracle"];

    private static (LaunchRoleResult Result, List<string> Tried) Run(string? arg, string? defaultRole, params string[] broken)
    {
        var tried = new List<string>();
        var result = LaunchRole.Resolve(arg, defaultRole, Disk, name =>
        {
            tried.Add(name);
            return broken.Contains(name, StringComparer.Ordinal) ? new GattoConfigException($"{name}.json is broken") : null;
        });
        return (result, tried);
    }

    [Theory]
    [InlineData(null, null, "generalist")]
    [InlineData(null, "oracle", "oracle")]
    [InlineData("coder", "oracle", "coder")]
    [InlineData("coder", null, "coder")]
    public void A_ROLE_THAT_LOADS_RUNS_AS_ASKED(string? arg, string? defaultRole, string runs)
    {
        var (r, _) = Run(arg, defaultRole);

        Assert.Equal(new LaunchRoleResult(runs, null, null, null), r);
    }

    [Theory]
    [InlineData("coder", "oracle", "oracle")]
    [InlineData("coder", null, "generalist")]
    [InlineData("coder", "coder", "generalist")]
    [InlineData(null, "coder", "generalist")]
    public void A_BROKEN_ROLE_RUNS_DEFAULT_ROLE_WHEN_IT_DIFFERS_ELSE_GENERALIST_AND_CARRIES_THE_MESSAGE(string? arg, string? defaultRole, string runs)
    {
        var (r, _) = Run(arg, defaultRole, "coder");

        Assert.Equal(new LaunchRoleResult(runs, "coder", "coder.json is broken", null), r);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(null, "generalist")]
    [InlineData("generalist", null)]
    [InlineData("generalist", "generalist")]
    public void A_LAUNCH_WHOSE_FALLBACK_IS_THE_BROKEN_FILE_EXITS_WITH_THE_MESSAGE_ONLY(string? arg, string? defaultRole)
    {
        var (r, _) = Run(arg, defaultRole, "generalist");

        Assert.Equal(new LaunchRoleResult("generalist", null, null, "generalist.json is broken"), r);
    }

    //the refusal is gated on the fallback, not on the name, so a broken generalist with another default_role still runs
    [Fact]
    public void A_BROKEN_GENERALIST_RUNS_A_DEFAULT_ROLE_THAT_NAMES_ANOTHER_FILE()
    {
        Assert.Equal(new LaunchRoleResult("oracle", "generalist", "generalist.json is broken", null), Run("generalist", "oracle", "generalist").Result);
    }

    [Fact]
    public void EVERY_COMPARISON_IGNORES_CASE_AND_THE_ASKED_SPELLING_IS_KEPT()
    {
        Assert.Equal(new LaunchRoleResult("Coder", null, null, null), Run("Coder", null).Result);
        Assert.Equal(new LaunchRoleResult("generalist", "coder", "coder.json is broken", null), Run("Coder", "CODER", "coder").Result);
        Assert.Equal(new LaunchRoleResult("GENERALIST", null, null, "generalist.json is broken"), Run("GENERALIST", null, "generalist").Result);
    }

    [Fact]
    public void AN_UNKNOWN_NAME_PASSES_THROUGH_UNLOADED_FOR_THE_RESOLVER_TO_REFUSE()
    {
        var (r, tried) = Run("nobody", null);

        Assert.Equal(new LaunchRoleResult("nobody", null, null, null), r);
        Assert.Empty(tried);
    }

    [Fact]
    public void A_FALLBACK_NAMING_A_MISSING_FILE_PASSES_THROUGH_FOR_THE_RESOLVER_TO_REFUSE()
    {
        var (r, tried) = Run("coder", "missing", "coder");

        Assert.Equal(new LaunchRoleResult("missing", "coder", "coder.json is broken", null), r);
        Assert.Equal(["coder"], tried);
    }
}
