using Gatto.Cli;

namespace Gatto.Tests;

//the bare word help is what a newcomer types, so it reaches the help unless a role of that name is on disk
[Collection("e2e")]
public class HelpWordTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-help-").FullName;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    public HelpWordTests() => Environment.SetEnvironmentVariable("GATTO_HOME", _home);

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        try { Directory.Delete(_home, true); } catch { }
    }

    private static async Task<(int Exit, string Out, string Err)> Run(params string[] argv)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(stderr);
        var exit = await GattoApp.RunAsync(argv);
        return (exit, stdout.ToString(), stderr.ToString());
    }

    //a home with no config and no role named help, the case the field report hit
    [Theory]
    [InlineData("help")]
    [InlineData("Help")]
    public async Task THE_BARE_WORD_PRINTS_THE_SAME_HELP_AS_THE_FLAG(string word)
    {
        var flag = await Run("-h");
        var bare = await Run(word);

        Assert.Equal(0, bare.Exit);
        Assert.Equal("", bare.Err);
        Assert.Equal(flag.Out, bare.Out);
        Assert.Contains("commands:", bare.Out, StringComparison.Ordinal);
    }

    //a role the user named help is theirs, so the word launches it as any role name does and the help stays one flag away
    [Fact]
    public async Task A_ROLE_NAMED_HELP_IN_THE_HOME_WINS()
    {
        Directory.CreateDirectory(Path.Combine(_home, "roles"));
        File.WriteAllText(Path.Combine(_home, "roles", "help.json"), "{}");

        var flag = await Run("-h");
        var bare = await Run("help");

        Assert.NotEqual(flag.Out, bare.Out);
        Assert.DoesNotContain("commands:", bare.Out, StringComparison.Ordinal);
        Assert.NotEqual("", bare.Err);   //the launch path ran and said why it stopped, which the help never does
    }
}
