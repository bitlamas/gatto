using Gatto.Cli.Launch;
using Gatto.Core;

namespace Gatto.Tests.Cli;

//the launch's role precedence as a table, every row read from the code and not from its comments, so a reader can check any case by its line
public class LaunchRoleTests
{
    private static readonly string[] Disk = ["coder", "oracle"];

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
    [InlineData(null, null, "default")]
    [InlineData(null, "oracle", "oracle")]
    [InlineData("coder", "oracle", "coder")]
    [InlineData("coder", null, "coder")]
    [InlineData("default", "oracle", "default")]
    public void A_ROLE_THAT_LOADS_RUNS_AS_ASKED(string? arg, string? defaultRole, string runs)
    {
        var (r, _) = Run(arg, defaultRole);

        Assert.Equal(new LaunchRoleResult(runs, null, null, null), r);
    }

    [Theory]
    [InlineData("coder", "oracle", "oracle")]
    [InlineData("coder", null, "default")]
    [InlineData("coder", "coder", "default")]
    [InlineData(null, "coder", "default")]
    public void A_BROKEN_ROLE_RUNS_DEFAULT_ROLE_WHEN_IT_DIFFERS_ELSE_THE_SEAT_WITH_NO_ROLE_AND_CARRIES_THE_MESSAGE(string? arg, string? defaultRole, string runs)
    {
        var (r, _) = Run(arg, defaultRole, "coder");

        Assert.Equal(new LaunchRoleResult(runs, "coder", "coder.json is broken", null), r);
    }

    //a default_role of default is the absent key, so a bare launch runs the seat with no role and a broken typed role falls back to it
    [Theory]
    [InlineData("default")]
    [InlineData("DEFAULT")]
    public void A_DEFAULT_ROLE_OF_DEFAULT_IS_THE_ABSENT_KEY(string defaultRole)
    {
        Assert.Equal(new LaunchRoleResult("default", null, null, null), Run(null, defaultRole).Result);
        Assert.Equal(new LaunchRoleResult("default", "coder", "coder.json is broken", null), Run("coder", defaultRole, "coder").Result);
    }

    //the config's word gets its own sentence, a bare launch with nothing loaded and a broken typed role with its message beside it
    [Fact]
    public void A_DEFAULT_ROLE_NAMING_NO_FILE_IS_REFUSED_IN_ITS_OWN_SENTENCE()
    {
        const string refusal = "default_role names 'ghost', and roles\\ghost.json does not exist";
        var (bare, tried) = Run(null, "ghost");
        Assert.Equal(new LaunchRoleResult("ghost", null, null, refusal), bare);
        Assert.Empty(tried);
        Assert.Equal(new LaunchRoleResult("coder", "coder", "coder.json is broken", refusal), Run("coder", "ghost", "coder").Result);
    }

    [Fact]
    public void A_TYPED_ROLE_THAT_LOADS_NEVER_CONSULTS_A_MISSING_DEFAULT_ROLE()
    {
        Assert.Equal(new LaunchRoleResult("coder", null, null, null), Run("coder", "ghost").Result);
    }

    [Fact]
    public void EVERY_COMPARISON_IGNORES_CASE_AND_THE_ASKED_SPELLING_IS_KEPT()
    {
        Assert.Equal(new LaunchRoleResult("Coder", null, null, null), Run("Coder", null).Result);
        Assert.Equal(new LaunchRoleResult("default", "coder", "coder.json is broken", null), Run("Coder", "CODER", "coder").Result);
        Assert.Equal(new LaunchRoleResult("Oracle", null, null, null), Run(null, "Oracle").Result);
    }

    [Fact]
    public void AN_UNKNOWN_NAME_PASSES_THROUGH_UNLOADED_FOR_THE_RESOLVER_TO_REFUSE()
    {
        var (r, tried) = Run("nobody", null);

        Assert.Equal(new LaunchRoleResult("nobody", null, null, null), r);
        Assert.Empty(tried);
    }
}
