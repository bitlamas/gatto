using System.Text.Json;
using Gatto.Cli;
using Gatto.Terminal;

namespace Gatto.Tests.Cli;

//every refusal comes before the banner, so the art never prints above an error. each case here must refuse, since reaching the wizard opens an alt screen
[Collection("e2e")]     //console output and error are process-global, so this class never runs in parallel.
public class ModelCommandTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-modelcmd-").FullName;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        try { Directory.Delete(_home, true); } catch (Exception) { } //deleting the temp home is best effort.
    }

    //the theme is built rich on purpose. a detected theme in a test process is null, and these checks judge the designed product.
    private static Theme Themed => new(new TermCaps(true, true));

    private sealed record Ran(string Err, int Exit);

    //drives the real entry point and captures stderr and the exit code. the refusal reads the Interactive seam, since Console.IsInputRedirected is not injectable.
    private Ran Run(bool interactive, params string[] args)
    {
        var raw = new StringWriter();
        var err = new StringWriter();
        Console.SetOut(raw);
        Console.SetError(err);
        try
        {
            var ctx = new CommandContext(raw, _home, interactive, Themed);
            var argv = args.Length == 0 ? new[] { "model" } : args;
            var exit = GattoApp.RunAsync(argv, ctx).GetAwaiter().GetResult();
            return new Ran(err.ToString(), exit);
        }
        finally
        {
            Console.SetOut(_origOut);
            Console.SetError(_origErr);
        }
    }

    //the model command takes no argument, so a typed path is refused. the home here has no engine, or the engine sentence answers and hides the argument one
    [Theory]
    [InlineData(@"C:\weights\thing.gguf", null)]
    [InlineData("new", @"C:\weights\thing.gguf")]
    public void gatto_model_refuses_an_argument_and_names_where_a_path_goes(string first, string? second)
    {
        GiveItAModel();
        GiveItAConfig(withEngine: false);

        var ran = second is null ? Run(true, "model", first) : Run(true, "model", first, second);

        Assert.Equal(2, ran.Exit);
        Assert.Contains("takes no argument", ran.Err);
        Assert.Contains("press m", ran.Err);
    }

    //the word new alone is the retired verb, so it reaches the same refusal as no argument.
    [Fact]
    public void gatto_model_new_alone_is_not_an_argument()
    {
        GiveItAModel();
        GiveItAConfig(withEngine: false);

        var ran = Run(true, "model", "new");

        Assert.Equal(2, ran.Exit);
        Assert.Contains("gatto setup", ran.Err);
        Assert.DoesNotContain("takes no argument", ran.Err);
    }

    //writes a model profile on disk, which is all the configured check needs to answer false.
    private void GiveItAModel()
    {
        var dir = Path.Combine(_home, "models", "gemma");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "profile.json"),
            """{ "files": [{ "path": "gemma-Q4_K_M.gguf", "active": true }], "port": 41235, "context": 4096 }""");
    }

    private void GiveItAConfig(bool withEngine)
    {
        var engine = Path.Combine(_home, "llama-server.exe");
        if (withEngine) File.WriteAllText(engine, "");
        File.WriteAllText(Path.Combine(_home, "gatto.json"),
            $$"""
            {
              "endpoints": { "local": { "base_url": "http://127.0.0.1:41235" } },
              "default_endpoint": "local",
              "default_model": "gemma"{{(withEngine ? ",\n  \"llama_server\": " + JsonSerializer.Serialize(engine) : "")}}
            }
            """);
    }

    [Fact]
    public void gatto_model_refuses_a_non_interactive_context_and_names_the_read_only_twin()
    {
        GiveItAModel();
        GiveItAConfig(withEngine: true);

        var ran = Run(interactive: false);

        Assert.Equal(2, ran.Exit);
        Assert.Contains("/model", ran.Err);
        //the message offers one alternative. the read-only twin gatto doctor belongs to setup and has no place in this command.
        Assert.DoesNotContain("gatto doctor", ran.Err);
    }

    //the model-and-no-engine case is the one that discriminates. the configured check answers false once a model exists, so it alone would miss this home
    [Theory]
    [InlineData(false, false)]   //a home with no model and no engine.
    [InlineData(true, false)]    //a model exists and no engine, the case the configured check cannot see.
    public void gatto_model_refuses_a_home_with_no_engine_and_names_setup(bool model, bool engine)
    {
        if (model) GiveItAModel();
        if (model || engine) GiveItAConfig(engine);

        var ran = Run(interactive: true);

        Assert.Equal(2, ran.Exit);
        Assert.Contains("gatto setup", ran.Err);
    }

    //every refusal must end with one empty line, so the prompt that follows does not sit against the sentence.
    [Theory]
    [InlineData("pipe")]
    [InlineData("engine")]
    [InlineData("argument")]
    public void every_refusal_ends_with_one_empty_line(string which)
    {
        GiveItAModel();
        GiveItAConfig(withEngine: which == "pipe");

        var ran = which switch
        {
            "pipe" => Run(interactive: false),
            "engine" => Run(interactive: true),
            _ => Run(true, "model", @"C:\weights\thing.gguf"),
        };

        var nl = Environment.NewLine;
        Assert.Equal(2, ran.Exit);
        Assert.EndsWith(nl + nl, ran.Err);
        Assert.False(ran.Err.EndsWith(nl + nl + nl, StringComparison.Ordinal), $"more than one empty line: [{ran.Err}]");
    }
}
