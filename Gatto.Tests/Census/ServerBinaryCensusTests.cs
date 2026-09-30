namespace Gatto.Tests.Census;

//build a ServeManager with a model from ServerBinaryFor, so no site forgets the config fallback. an empty binary throws after the old server stopped
public class ServerBinaryCensusTests
{
    private const string Needle = "Profile.LlamaServer";

    private static string Source() => File.ReadAllText(
        Path.Combine(SourceTree.RepoRoot(), "Gatto", "Cli", "GattoApp.cs"));

    private static string[] Constructions(string src) =>
        [.. src.Split('\n').Where(l => l.Contains("new ServeManager(", StringComparison.Ordinal)
            || l.Contains("new Gatto.Roles.ServeManager(", StringComparison.Ordinal))];

    [Fact]
    public void NO_SERVE_MANAGER_IS_BUILT_FROM_A_HAND_ROLLED_BINARY()
    {
        var offenders = Constructions(Source())
            .Where(l => l.Contains(Needle, StringComparison.Ordinal))
            .Select(l => l.Trim())
            .ToArray();

        Assert.True(offenders.Length == 0,
            "a ServeManager is built from a hand-rolled binary; use ServerBinaryFor so the config "
            + "fallback cannot be dropped:" + Environment.NewLine + "  "
            + string.Join(Environment.NewLine + "  ", offenders));
    }

    //the exact line the defect shipped is written out here. a positive that shares the check's blind spot certifies nothing
    [Fact]
    public void AND_THE_CENSUS_CAN_SEE_THE_LINE_THAT_SHIPPED()
    {
        var planted = Source()
            + "\n        var oops = new Gatto.Roles.ServeManager(home, reloaded.Profile.LlamaServer ?? \"\");\n";

        Assert.Contains(Constructions(planted), l => l.Contains(Needle, StringComparison.Ordinal));
    }

    //a construction with no model must not be reported. the census would otherwise demand a fallback where there is nothing to fall back from.
    [Fact]
    public void BUT_A_CONFIG_ONLY_CONSTRUCTION_IS_NOT_AN_OFFENDER()
    {
        var lines = Constructions(Source());

        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.Contains("config.LlamaServer", StringComparison.Ordinal)
            && !l.Contains(Needle, StringComparison.Ordinal));
    }
    //the one home must read the model override first and the config fallback second. a helper that dropped either would make the site census certify nothing
    [Fact]
    public void THE_ONE_HOME_READS_THE_OVERRIDE_AND_THEN_THE_CONFIG()
    {
        var src = Source();
        var at = src.IndexOf("private static string ServerBinaryFor(", StringComparison.Ordinal);
        Assert.True(at > 0, "ServerBinaryFor is gone - the census above now certifies nothing");

        var body = src[at..src.IndexOf(';', at)];
        var override_ = body.IndexOf("Profile.LlamaServer", StringComparison.Ordinal);
        var fallback = body.IndexOf("config.LlamaServer", StringComparison.Ordinal);

        Assert.True(override_ > 0, $"the one home does not read the model's own binary: {body}");
        Assert.True(fallback > 0, $"the one home does not read the config's binary: {body}");
        Assert.True(override_ < fallback, $"the config must be the FALLBACK, not the winner: {body}");
    }
}
