using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//a test client with no fake handler opens real sockets. the port a fixture picks can reach whatever listens there, a live server included
public class RealHttpClientCensusTests
{
    private sealed record Site(string File, int Line, string Text);

    //the shapes read as a real client: no handler or a named handler class, a target-typed new(), or a handler built alone
    private static readonly Regex RealClient = new(
        @"\bnew\s+(System\s*\.\s*Net\s*\.\s*Http\s*\.\s*)?HttpClient\s*(\(\s*\)|\{|\(\s*new\s+(HttpClientHandler|SocketsHttpHandler)\b)"
        + @"|\bHttpClient\??\s+\w+\s*(=|=>|\(\s*\)\s*=>)\s*new\s*\(\s*\)"
        + @"|\bnew\s+(HttpClientHandler|SocketsHttpHandler)\s*[({]",
        RegexOptions.Compiled);

    private sealed record Pin(int Ceiling, string Why);

    //a pin is a ceiling. lower it in the commit that removes a client, and raise it only after reading where the new client dials.
    private static readonly Dictionary<string, Pin> Pins = new(StringComparer.Ordinal)
    {
        ["Gatto.Tests/AgentLoopTests.cs"] = new(4, "each client targets the `BaseUrl` of a fake server on a port the test bound"),
        ["Gatto.Tests/Audition/AuditionServerReadinessTests.cs"] = new(1, "every home the class writes names the closed port 1"),
        ["Gatto.Tests/CompactorTests.cs"] = new(6, "each client targets the `BaseUrl` of a fake server on a port the test bound"),
        ["Gatto.Tests/FakeServerTests.cs"] = new(5, "each client posts to the `BaseUrl` of the fake server under test"),
        ["Gatto.Tests/LaunchLoadingWaitTests.cs"] = new(1, "the recorded ports are a fake server's port or that port plus one"),
        ["Gatto.Tests/LaunchStartLineTests.cs"] = new(1, "the model's port is the port of a fake server the test bound"),
        ["Gatto.Tests/LiveCaptureFixtureTests.cs"] = new(4, "each client targets the `BaseUrl` of a fake server replaying a capture"),
        ["Gatto.Tests/ModelSwitchDeedTests.cs"] = new(1, "every model, endpoint and serve record the class writes names port 9"),
        ["Gatto.Tests/OpenAiCompatClientTests.cs"] = new(8, "each client targets a fake server's `BaseUrl` or the closed port 1"),
        ["Gatto.Tests/OpenAiCompatPropsTests.cs"] = new(3, "each client targets a fake server's `BaseUrl` or the closed port 1"),
        ["Gatto.Tests/OpenAiCompatUsageTests.cs"] = new(4, "each client targets the `BaseUrl` of a fake server on a port the test bound"),
        ["Gatto.Tests/PermissionPrompterTests.cs"] = new(1, "the client targets the `BaseUrl` of a fake server on a port the test bound"),
        ["Gatto.Tests/RoundPersistenceTests.cs"] = new(4, "each client targets the `BaseUrl` of a fake server on a port the test bound"),
        ["Gatto.Tests/RunAgentToolTests.cs"] = new(9, "each client targets the `BaseUrl` of a fake server on a port the test bound"),
        ["Gatto.Tests/SessionBaselineTests.cs"] = new(1, "the client targets the `BaseUrl` of a fake server on a port the test bound"),
        ["Gatto.Tests/ServeManagerTests.cs"] = new(1, "health polls reach only fake server ports. port 1235 appears only in records, foreground starts and starts refused before the spawn, none of which polls"),
        ["Gatto.Tests/Setup/HeldServerRestoreTests.cs"] = new(1, "the held port is a fake server's port or 41888, where nothing of this project listens"),
        ["Gatto.Tests/Setup/Tui/ModelFetchTests.cs"] = new(1, "the fetch refuses a quant with no digest before it sends any request"),
        ["Gatto.Tests/TornRoundLoadTests.cs"] = new(1, "the client targets the `BaseUrl` of a fake server on a port the test bound"),
        ["Gatto.Tests/WireLogTests.cs"] = new(3, "each client targets the `BaseUrl` of a fake server on a port the test bound"),
    };

    private static List<(string Rel, string Source)> TestTree()
    {
        var root = SourceTree.RepoRoot();
        return [.. Directory.EnumerateFiles(Path.Combine(root, "Gatto.Tests"), "*.cs", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .Where(rel => !rel.Contains("/bin/") && !rel.Contains("/obj/"))
            .OrderBy(rel => rel, StringComparer.Ordinal)
            .Select(rel => (rel, SourceTree.Read(Path.Combine(root, rel))))];
    }

    //the one matcher, so the checks below run the code the census runs
    private static List<Site> Scan(string rel, string source)
    {
        var raw = source.Split('\n');
        var code = SourceTree.CodeOnly(source);
        Assert.True(code.Split('\n').Length == raw.Length,
            $"{rel}: the code view and the source differ in line count, so every reported line would be wrong");
        return [.. RealClient.Matches(code)
            .Select(m => 1 + code.AsSpan(0, m.Index).Count('\n'))
            .Select(line => new Site(rel, line, raw[line - 1].Trim()))];
    }

    private static List<string> Offences(IEnumerable<(string Rel, string Source)> tree)
    {
        var offences = new List<string>();
        foreach (var (rel, source) in tree)
        {
            var sites = Scan(rel, source);
            var ceiling = Pins.TryGetValue(rel, out var pin) ? pin.Ceiling : 0;
            if (sites.Count > ceiling)
                offences.Add($"{rel} is pinned at {ceiling} real clients and holds {sites.Count}. read where each one dials before pinning it:\n"
                    + string.Join("\n", sites.Select(s => $"    {s.Line}  {s.Text}")));
        }
        return offences;
    }

    [Fact]
    public void EVERY_REAL_HTTP_CLIENT_IN_A_TEST_SITS_IN_A_PINNED_FILE()
    {
        var tree = TestTree();
        Assert.True(tree.Count > 100, $"the census read {tree.Count} test files, so it is reading the wrong tree");
        var offences = Offences(tree);
        Assert.True(offences.Count == 0, string.Join("\n", offences));
    }

    [Fact]
    public void EVERY_PIN_NAMES_A_FILE_THAT_STILL_HOLDS_A_REAL_CLIENT()
    {
        var counts = TestTree().ToDictionary(t => t.Rel, t => Scan(t.Rel, t.Source).Count, StringComparer.Ordinal);
        var stale = Pins.Where(p => counts.GetValueOrDefault(p.Key) == 0).Select(p => p.Key).ToList();
        Assert.True(stale.Count == 0, "these pins name a file with no real client, so remove them: " + string.Join(", ", stale));
    }

    //use the exact class field that shipped (a look-alike would prove nothing)
    [Theory]
    [InlineData("private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };")]
    [InlineData("var client = new OpenAiCompatClient(new HttpClient(), \"local\", config);")]
    [InlineData("using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };")]
    [InlineData("var http = new System.Net.Http.HttpClient();")]
    [InlineData("var http = new HttpClient(new HttpClientHandler());")]
    [InlineData("var handler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(1) };")]
    [InlineData("private static HttpClient Http() => new();")]
    public void A_REAL_CLIENT_IN_AN_UNPINNED_FILE_IS_AN_OFFENCE(string line)
    {
        var offences = Offences([("Gatto.Tests/Planted.cs", "class Planted\n{\n    " + line + "\n}\n")]);
        Assert.Single(offences);
        Assert.Contains("Gatto.Tests/Planted.cs is pinned at 0", offences[0]);
    }

    //a fake handler, a nullable parameter, a comment and a literal all name the type without opening a socket
    [Theory]
    [InlineData("private static HttpClient Healthy() => new(new ScriptedHealth());")]
    [InlineData("var http = new HttpClient(new FakeHandler());")]
    [InlineData("HttpClient? http = null) =>")]
    [InlineData("//a real client here would be `new HttpClient()`")]
    [InlineData("var text = \"new HttpClient()\";")]
    public void A_CLIENT_WITH_A_FAKE_HANDLER_IS_NOT_AN_OFFENCE(string line)
    {
        Assert.Empty(Offences([("Gatto.Tests/Planted.cs", "class Planted\n{\n    " + line + "\n}\n")]));
    }
}
