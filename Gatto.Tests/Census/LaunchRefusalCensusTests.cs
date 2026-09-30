namespace Gatto.Tests.Census;

//the launch refusal is gated on an explicit -m and the probe's own mismatch, computed once and shared with the chip, and exits non-zero
public class LaunchRefusalCensusTests
{
    private static string Source() => File.ReadAllText(
        Path.Combine(SourceTree.RepoRoot(), "Gatto", "Cli", "GattoApp.cs"));

    [Fact]
    public void THE_REFUSAL_IS_GATED_ON_AN_EXPLICIT_MODEL_AND_THE_PROBES_OWN_MISMATCH()
    {
        var src = Source();

        //both halves belong in one condition. a refusal gated on the model argument alone would refuse every -m launch
        Assert.Contains("if (args.Model is not null && launchMismatch is not null)",
            src, StringComparison.Ordinal);

        //the refusal must exit rather than fall through into the session it refused.
        var at = src.IndexOf("if (args.Model is not null && launchMismatch is not null)",
            StringComparison.Ordinal);
        var block = src[at..(at + 400)];
        Assert.Contains("ServeNotice.Refusing(", block, StringComparison.Ordinal);
        Assert.Contains("return 1;", block, StringComparison.Ordinal);
    }

    //compute the mismatch once, a second probe call could answer differently and gatto would refuse one server while charting another
    [Fact]
    public void THE_MISMATCH_IS_COMPUTED_ONCE_AND_SHARED()
    {
        var src = Source();
        const string call = "ModelSwitch.DescribeProbe(model, launchLoaded, modelsDir)";

        Assert.Equal(1, Occurrences(src, call));
        //the count of one means nothing unless the counter can see a second call
        Assert.Equal(2, Occurrences(src + "\n// " + call + "\n", call));
    }

    private static int Occurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal)) n++;
        return n;
    }
}
