using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//a client timeout is a budget beneath every caller's token, and a linked token cannot lengthen it. every product client is untimed and each read holds its own deadline
public class UntimedClientCensusTests
{
    private sealed record Site(string File, int Line, string Text);

    //an assignment to a property named Timeout whose value is not the infinite span, in an initializer or on an instance
    private static readonly Regex TimedClient = new(
        @"(?<!\w)Timeout\s*=(?![=>])(?>\s*)(?!(System\s*\.\s*Threading\s*\.\s*)?Timeout\s*\.\s*InfiniteTimeSpan\b)",
        RegexOptions.Compiled);

    //the one matcher, so the planted checks run the code the census runs
    private static List<Site> Scan(string rel, string source)
    {
        var raw = source.Split('\n');
        var code = SourceTree.CodeOnly(source);
        Assert.True(code.Split('\n').Length == raw.Length,
            $"{rel}: the code view and the source differ in line count, so every reported line would be wrong");
        return [.. TimedClient.Matches(code)
            .Select(m => 1 + code.AsSpan(0, m.Index).Count('\n'))
            .Select(line => new Site(rel, line, raw[line - 1].Trim()))];
    }

    [Fact]
    public void NO_PRODUCT_CLIENT_CARRIES_A_TIMEOUT()
    {
        var root = SourceTree.RepoRoot();
        var files = SourceTree.ProductionFiles();
        Assert.True(files.Count > 100, $"the census read {files.Count} product files, so it is reading the wrong tree");

        var sites = files
            .SelectMany(f => Scan(Path.GetRelativePath(root, f).Replace('\\', '/'), SourceTree.Read(f)))
            .ToList();
        Assert.True(sites.Count == 0,
            "these clients carry a timeout. make the client untimed and give each read a deadline its caller owns:\n"
            + string.Join("\n", sites.Select(s => $"    {s.File}:{s.Line}  {s.Text}")));
    }

    //each shape a timed client shipped in
    [Theory]
    [InlineData("var probeHttp = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(2) };")]
    [InlineData("new(new SocketsHttpHandler { ConnectTimeout = connect }) { Timeout = total };")]
    [InlineData("private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };")]
    [InlineData("http.Timeout = TimeSpan.FromSeconds(5);")]
    [InlineData("{ Timeout = Timeout.FromSeconds(5) }")]
    public void A_TIMED_CLIENT_IS_FOUND(string line)
    {
        Assert.Single(Scan("Gatto/Planted.cs", "class Planted\n{\n    " + line + "\n}\n"));
    }

    //the infinite span in both spellings, a connect bound, another name ending in timeout, a comparison, a lambda, a comment and a literal
    [Theory]
    [InlineData("using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };")]
    [InlineData("{ Timeout = System.Threading.Timeout.InfiniteTimeSpan },")]
    [InlineData("new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(2) }")]
    [InlineData("_pollTimeout = pollTimeout;")]
    [InlineData("if (http.Timeout == TimeSpan.Zero) return;")]
    [InlineData("public TimeSpan Timeout => _budget;")]
    [InlineData("//a client with { Timeout = TimeSpan.FromSeconds(5) } is the shape this census refuses")]
    [InlineData("var text = \"Timeout = 5\";")]
    public void AN_UNTIMED_CLIENT_IS_NOT_FOUND(string line)
    {
        Assert.Empty(Scan("Gatto/Planted.cs", "class Planted\n{\n    " + line + "\n}\n"));
    }
}
