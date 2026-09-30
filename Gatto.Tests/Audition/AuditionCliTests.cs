using System.Text.Json;
using Gatto.Cli;

namespace Gatto.Tests.Audition;

//exit 1 is a verdict about the model, anything else that goes wrong exits 2
[Collection("e2e")] //these change GATTO_HOME and the console writers, so they never run in parallel
public class AuditionCliTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-audcli-").FullName;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    public AuditionCliTests() => Environment.SetEnvironmentVariable("GATTO_HOME", _home);

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
    public async Task An_unknown_model_exits_2_with_a_message_that_names_it()
    {
        //a harness error exits 2, nothing was measured and no verdict exists to report
        var (exit, _, err) = await Run("audition", "no-such-model");

        Assert.Equal(2, exit);
        Assert.Contains("no-such-model", err);
        Assert.DoesNotContain("Unhandled", err);          //the message must be friendly and never a stack trace.
    }

    [Fact]
    public async Task No_model_and_no_default_model_exits_2_and_SAYS_WHAT_TO_DO()
    {
        //the error must name the next step, a bare refusal leaves a new user stuck
        var (exit, outp, err) = await Run("audition");

        Assert.Equal(2, exit);
        Assert.Contains("audition", err, StringComparison.OrdinalIgnoreCase);
        Assert.True(err.Contains("default_model", StringComparison.OrdinalIgnoreCase)
            || err.Contains("model", StringComparison.OrdinalIgnoreCase),
            $"the message must point somewhere actionable; got: {err}");
        //the refusal throws before the header opens, so stdout stays empty (the finally blank has no header to pair with)
        Assert.Equal(string.Empty, outp);
    }

    [Fact]
    public async Task A_HARNESS_failure_exits_2_and_never_1()
    {
        //exit 1 belongs to verdicts, so a harness fault must exit 2 and the audition route needs its own catch
        var models = Path.Combine(_home, "models", "broken");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "profile.json"), "{ this is not json");

        var (exit, _, err) = await Run("audition", "broken");

        Assert.Equal(2, exit);
        Assert.NotEqual(1, exit);
        Assert.NotEmpty(err);
    }

    [Fact]
    public async Task A_model_whose_GGUF_IS_GONE_says_so_INSTEAD_of_sending_the_user_to_another_command()
    {
        //the message must say the file is gone, a check of exit 2 alone passes without the fix
        var modelDir = Path.Combine(_home, "models", "gone");
        Directory.CreateDirectory(modelDir);
        var gguf = Path.Combine(_home, "nowhere", "Some-Model-Q4_K_M.gguf");
        File.WriteAllText(Path.Combine(modelDir, "profile.json"),
            $$"""{ "files": [{ "path": {{JsonSerializer.Serialize(gguf)}}, "active": true }], "port": 1235, "context": 4096 }""");

        var (exit, _, err) = await Run("audition", "gone");

        Assert.Equal(2, exit);                                   //exit 2 marks a harness error, nothing was measured
        Assert.Contains(gguf, err, StringComparison.Ordinal);    //the message must name the missing file path
        Assert.Contains("files", err, StringComparison.Ordinal);            //the message must also mention the files field
        Assert.DoesNotContain("serve start", err, StringComparison.Ordinal);  //the message never sends the user to a second command.
    }

    [Fact]
    public void The_command_is_routed_not_merely_reserved()
    {
        //an unrouted audition command would go down the launch path and chat with a model about the word audition
        var args = ArgRouter.Parse(["audition", "qwen"]);
        Assert.Equal("audition", args.Command);
        Assert.Equal("qwen", args.Target);
    }

    [Fact]
    public void Nothing_tells_users_to_wait_for_Plan_5()
    {
        //no source may tell the user to wait for audition (it exists), so the census must scan every file
        var offenders = Census.SourceTree.ProductionFiles()
            .Where(f => File.ReadAllText(f).Contains("once Plan 5 lands", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .ToList();

        Assert.True(offenders.Count == 0,
            "a surface tells the user to wait for Plan 5; audition shipped: " + string.Join(", ", offenders));
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Gatto.sln"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("Gatto.sln not found above the test binary");
    }

    [Fact]
    public async Task The_usage_line_advertises_the_command()
    {
        var (_, outText, err) = await Run("--help");
        Assert.Contains("audition", outText + err);
    }
}
