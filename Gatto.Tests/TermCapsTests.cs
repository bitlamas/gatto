using Gatto.Terminal;

namespace Gatto.Tests;

public class TermCapsTests
{
    private static string? NoEnv(string _) => null;

    [Fact]
    public void StdoutRedirected_IsPlain() =>
        Assert.False(TermCaps.Detect(NoEnv, stdoutRedirected: true, stdinRedirected: false, () => true).Rich);

    [Fact]
    public void StdinRedirected_IsPlain() =>
        Assert.False(TermCaps.Detect(NoEnv, stdoutRedirected: false, stdinRedirected: true, () => true).Rich);

    [Fact]
    public void NoColorSet_IsPlain() =>
        Assert.False(TermCaps.Detect(k => k == "NO_COLOR" ? "1" : null, false, false, () => true).Rich);

    [Fact]
    public void NoColorEmpty_IsIgnored() =>
        Assert.True(TermCaps.Detect(k => k == "NO_COLOR" ? "" : null, false, false, () => true).Rich);

    [Fact]
    public void VtEnableFails_IsPlain() =>
        Assert.False(TermCaps.Detect(NoEnv, false, false, () => false).Rich);

    [Fact]
    public void WtSession_IsTrueColor() =>
        Assert.True(TermCaps.Detect(k => k == "WT_SESSION" ? "guid" : null, false, false, () => true).TrueColor);

    [Fact]
    public void ColortermTruecolor_IsTrueColor() =>
        Assert.True(TermCaps.Detect(k => k == "COLORTERM" ? "truecolor" : null, false, false, () => true).TrueColor);

    [Fact]
    public void BareVt_Is256() =>
        Assert.False(TermCaps.Detect(NoEnv, false, false, () => true).TrueColor);

    //output-only detection, gatto doctor writes and never reads a key

    //a piped stdin must not make the output plain (the stdin check is for the REPL's raw-mode ReadKey)
    [Fact]
    public void OutputOnly_StaysRich_WhenOnlyStdinIsRedirected() =>
        Assert.True(TermCaps.DetectForOutput(NoEnv, stdoutRedirected: false, () => true).Rich);

    [Fact]
    public void OutputOnly_IsPlain_WhenStdoutIsRedirected() =>
        Assert.False(TermCaps.DetectForOutput(NoEnv, stdoutRedirected: true, () => true).Rich);

    [Fact]
    public void OutputOnly_HonoursNoColor() =>
        Assert.False(TermCaps.DetectForOutput(k => k == "NO_COLOR" ? "1" : null, false, () => true).Rich);
}
