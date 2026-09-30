using Gatto.Cli;

namespace Gatto.Tests.Setup;

//the test class changes process globals, so it shares the e2e collection with the tests that change them
[Collection("e2e")]
public class SetupDoorTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-setup-").FullName;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    public SetupDoorTests() => Environment.SetEnvironmentVariable("GATTO_HOME", _home);

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

    [Fact]
    public void The_command_is_ROUTED_not_merely_reserved()
    {
        //a reserved word is not a route, a reservation alone sends setup down the launch path and asks a model about the word
        Assert.Equal("setup", ArgRouter.Parse(["setup"]).Command);
    }

    [Fact]
    public async Task Without_a_terminal_it_REFUSES_and_says_what_to_run_instead()
    {
        //setup only asks questions, so with nobody to answer it refuses with exit 2 (redirected output is the real non-interactive case)
        var (exit, outText, err) = await Run("setup");

        Assert.Equal(2, exit);
        Assert.Contains("terminal", err, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("gatto doctor", err, StringComparison.Ordinal);

        //the refusal comes before the banner, so nothing reaches stdout, art above the message is gatto talking to itself in a log
        Assert.Equal("", outText);
    }

    [Fact]
    public async Task The_refusal_writes_NOTHING_INTO_THE_HOME_AT_ALL()
    {
        //the refusal returns before the home is initialized, so a declined gatto setup leaves the home folder empty
        await Run("setup");

        Assert.Empty(Directory.GetFileSystemEntries(_home));
    }

    [Fact]
    public async Task The_usage_line_advertises_the_command()
    {
        var (_, outText, err) = await Run("--help");
        Assert.Contains("setup", outText + err, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_START_GATTO_NOW_CHOICE_IS_ACTUALLY_READ_BY_SOMETHING()
    {
        //a real launch needs a server, a REPL and a terminal, so the check for a reader is a source census
        var cli = Gatto.Tests.Census.SourceTree.ProductionFilesUnder("Cli");
        //count only code lines, a comment naming the flag would satisfy the guard without a reader
        var readers = cli
            .Where(f => !f.EndsWith("SetupFlow.cs", StringComparison.Ordinal))   //this file writes the flag, so it cannot count as a reader.
            .SelectMany(File.ReadAllLines)
            .Count(l => l.Contains("StartReplWhenDone", StringComparison.Ordinal)
                        && !l.TrimStart().StartsWith("//", StringComparison.Ordinal));

        Assert.True(readers > 0,
            "nothing outside the flow reads StartReplWhenDone, the 'Start gatto now' option is a dead affordance");
    }
}
