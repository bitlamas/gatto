namespace Gatto.Tests;

//no test may dial the live server at 127.0.0.1:1235, so the files naming that exact literal are listed and reviewed by hand
public class NoTestReachesTheLiveServerTests
{
    private const string RealPort = "127.0.0.1:1235";

    //every file that names the real port, with the reason it does not dial it, keyed by a forward-slashed path under Gatto.Tests/
    private static readonly (string File, string Why)[] Allowlist =
    [
        ("BadgeServerIdentityTests.cs", "CannedHandler intercepts every request before any socket work"),
        ("ConfigSchemaBreadcrumbTests.cs", "no HttpClient, no GattoApp.RunAsync, no ServeProbe — config-shape assertions only"),
        ("ConfigTests.cs", "no HttpClient, no GattoApp.RunAsync, no ServeProbe — config-shape assertions only"),
        ("Copy/CorpusDriver.cs", "a starter-shaped config with an explicit local base_url, " +
            "written explicitly only to also pin glyphs; the connection attempt fails fast since nothing " +
            "listens, which is what every SERVE_STATUS_NOT_SERVING/SERVE_STOP_NOTHING_TO_STOP golden asserts"),
        ("DoctorTests.cs", "FakeHandler intercepts every request before any socket work"),
        ("GattoAppTests.cs", "the six remaining uses all exit or reroute before the launch-time probe: " +
            "an unknown role, a role with no model at all, an eagerly-validated bad thinking-map name, " +
            "a role that selects the 'cloudy' endpoint instead of 'local', the backstop test (crashes on " +
            "EnsureInitialized's directory scaffold before config is ever used for a connection), and " +
            "ModelSwitchBaseUrl, which only ever feeds Gatto.Roles.ModelSwitch.Switch's pure core with a " +
            "synthesized probe result, never a live HTTP call — each verified individually in the fix"),
        ("GattoConfigWriterTests.cs", "no HttpClient, no GattoApp.RunAsync, no ServeProbe — config-shape assertions only"),
        ("ModelSwitchTests.cs", "no HttpClient, no GattoApp.RunAsync, no ServeProbe — config-shape assertions only"),
        ("OpenAiCompatClientTests.cs", "every use is a FuncHandler/StubHandler fake; the two real HttpClients in the file target port 1 or server.BaseUrl instead"),
        ("ProveItTests.cs", "the file's own doc comment: \"Every server here is a stub\" — StubHandler intercepts every request"),
        ("ServeProbeTests.cs", "StubHandler/ThrowingHandler intercept every request before any socket work"),
        ("Setup/SetupFlowTests.cs", "no HttpClient, no GattoApp.RunAsync, no ServeProbe — config-shape assertions only"),
        ("Setup/Tui/ConnectTests.cs", "a ConnectProbe DATA RECORD built as a render fixture, fed to WalkRender/Golden — no HttpClient anywhere in the file"),
        ("Setup/Tui/TypedDoorTests.cs", "a pure string classifier's test input (TypedDoor.Classify) — proving the text LOOKS like an address, never dialing one"),
        ("Setup/WriteSetApplyTests.cs", "no HttpClient, no GattoApp.RunAsync, no ServeProbe — WriteSetApply/GattoConfig.Load only"),
        ("SlotsReaderTests.cs", "SlotsStub/SlotsThrowingHandler intercept every request before any socket work"),
        ("StatusTests.cs", "the file's own doc comment: \"Read-only; no real network\" — FakeHandler intercepts every request"),
        ("UpdateCommandTests.cs", "the literal only satisfies GattoConfig.Load's endpoints requirement; UpdateCommand.cs " +
            "never reads Endpoints or dials one, since checking and downloading a release never touches the local server"),
    ];

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Gatto.sln"))) d = d.Parent;
        Assert.True(d is not null, "could not find the repo root");
        return d!.FullName;
    }

    private static string TestsSource() => Path.Combine(RepoRoot(), "Gatto.Tests");

    //the census and the known-match tests share this scan so they cannot drift apart. the scan excludes itself by filename
    private static string[] FindFilesNamingTheRealPort(string root, string? excludeFileName = null) =>
        [.. Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => excludeFileName is null || Path.GetFileName(f) != excludeFileName)
            .Where(f => File.ReadAllText(f).Contains(RealPort, StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/'))
            .OrderBy(f => f, StringComparer.Ordinal)];

    [Fact]
    public void OnlyTheAllowlistedFilesNameTheRealPort()
    {
        var actual = FindFilesNamingTheRealPort(TestsSource(), excludeFileName: "NoTestReachesTheLiveServerTests.cs");
        var expected = Allowlist.Select(a => a.File).OrderBy(f => f, StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, actual);
    }

    //a stray file naming the real port is what a regression looks like, and without this case an empty list would pass
    [Fact]
    public void ThePlantedPositive_AFileNamingThePort_IsFound()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-portcensus-positive-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Sneaky.cs"),
                "// a test that would reach " + RealPort + " for real");

            var found = FindFilesNamingTheRealPort(dir);

            Assert.Equal(["Sneaky.cs"], found);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    //plain text and other ports must not match, or a scan that matched everything would also pass the known-match case
    [Fact]
    public void ThePlantedNegative_OrdinaryTextAndOtherPorts_IsNotFound()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-portcensus-negative-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "Plain.cs"), "// nothing interesting here");
            File.WriteAllText(Path.Combine(dir, "OtherPort.cs"), "var url = \"http://127.0.0.1:1\";");
            File.WriteAllText(Path.Combine(dir, "BarePortNumber.cs"), "WriteModel(\"m\", port: 1235);");

            var found = FindFilesNamingTheRealPort(dir);

            Assert.Empty(found);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
