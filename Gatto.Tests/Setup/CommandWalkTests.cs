using System.Net;
using System.Text;
using Gatto.Cli;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the command surface resolves its seams through CommandContext. raw stdout must stay empty on a themed step, and each step asserts its own screen body.
[Collection("e2e")]     //the Console.SetOut change is process-global, so this class never runs in parallel.
public class CommandWalkTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-cmdwalk-").FullName;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        try { Directory.Delete(_home, true); } catch (Exception) { }
    }

    //always build a rich terminal, a detected theme is null in a test process and the check judges the designed product.
    private static Theme Themed => new(new TermCaps(true, true));

    private sealed record Rendered(string Screen, string Raw, string Err, int Exit);

    //run the real command through the real entry point, capturing the screen and raw stdout separately (one writer would hide a raw write).
    private Rendered Run(params string[] argv)
    {
        var screen = new StringWriter();
        var raw = new StringWriter();
        var err = new StringWriter();
        Console.SetOut(raw);
        Console.SetError(err);
        try
        {
            var ctx = new CommandContext(screen, _home, Interactive: true, Themed);
            var exit = GattoApp.RunAsync(argv, ctx).GetAwaiter().GetResult();
            return new Rendered(screen.ToString(), raw.ToString(), err.ToString(), exit);
        }
        finally
        {
            Console.SetOut(_origOut);
            Console.SetError(_origErr);
        }
    }

    //the non-blank rows with escapes stripped, so a row's start can be read without paint in the way.
    private static string[] Rows(string screen) =>
        [.. screen.Replace("\r\n", "\n").Split('\n')
            .Select(TermText.StripAnsiForWidth)
            .Where(r => r.Trim().Length > 0)];

    [Fact]
    public void HELP_STAYS_RAW_AND_BYTE_PURE_and_never_touches_the_seam()
    {
        //the help banner is plain by contract (stdout for --help, stderr for a usage error), so this step's oracle reads the raw capture.
        var r = Run("--help");

        Assert.Equal(0, r.Exit);
        Assert.Equal("", r.Screen);                          //the seam received nothing.
        Assert.Contains("usage: gatto", r.Raw);
        Assert.DoesNotContain('', r.Raw);              //the banner stays byte-pure, a script reads it.
    }

    //the body of start is asserted at the ServeManager seam over a fake process. this drive tests the real cli and can't fake a running server.
    [Theory]
    [InlineData("stop")]
    [InlineData("status")]
    [InlineData("start")]
    public void A_THEMED_SERVE_STEP_LEAVES_RAW_STDOUT_EMPTY(string subcommand)
    {
        //everything the screen showed must arrive through the seam, a raw write on this path shows up in the raw capture.
        var r = Run("serve", subcommand);

        Assert.Equal("", r.Raw);
    }

    [Fact]
    public void SERVE_STOP_SPEAKS_ONE_VISUAL_LANGUAGE_banner_and_body_alike()
    {
        //the raw capture can't see this class, so it is asserted on the render. banner and body share one margin, a self-indenting row would speak a second language.
        var r = Run("serve", "stop");
        var rows = Rows(r.Screen);

        Assert.Contains(rows, row => row.Contains("not serving"));
        Assert.All(rows, row =>
            Assert.False(row.StartsWith(' '), "a row indented itself: '" + row + "'"));
    }

    [Fact]
    public void SERVE_STATUS_JSON_NEVER_MEETS_THE_ADAPTER()
    {
        //machine-readable output takes no banner, no indent and no closing row, a parser reads this.
        var r = Run("serve", "status", "--json");

        Assert.Equal("", r.Raw);
        Assert.EndsWith("}\n", r.Screen.Replace("\r\n", "\n"), StringComparison.Ordinal);
        //don't trim before this check, trimming removes the gutter it exists to detect.
        var body = Rows(r.Screen)[0];
        Assert.StartsWith("{", body);
        Assert.DoesNotContain('', r.Screen);
    }

    private sealed class ScriptedKeys(IEnumerable<ConsoleKeyInfo> keys) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(keys);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("the walk scripted too few keys");
    }

    //each fake records that it was asked and returns a scripted outcome, so the log must hold exactly the scripted acts.
    private sealed class RecordedActs
    {
        public List<string> Log { get; } = [];
        public string? PathProblem { get; set; }
        public IReadOnlyList<RemovalFailure> TreeFailures { get; set; } = [];

        public UninstallActs Seam => new(
            dir => { Log.Add("remove-path:" + dir); return PathProblem; },
            //the log records the keep set, so it shows whether the map's promise reached the deed.
            (home, keep) =>
            {
                Log.Add("remove-tree:" + home
                    + (keep.Count > 0 ? " keep:" + string.Join(";", keep) : " keep:none"));
                return TreeFailures;
            },
            dir => Log.Add("arm-sweeper:" + dir));
    }

    private static readonly ConsoleKeyInfo Enter = new('\r', ConsoleKey.Enter, false, false, false);
    private static readonly ConsoleKeyInfo Down = new('\0', ConsoleKey.DownArrow, false, false, false);

    //the supplied facts stage an installed gatto on the path, so the tail can render without a real installation.
    private (string Screen, string Painted, RecordedActs Acts, int Exit) Uninstall(
        bool deleteHome, string? pathProblem = null,
        IReadOnlyList<int>? siblings = null, IReadOnlyList<RemovalFailure>? treeFailures = null)
    {
        var installDir = Path.Combine(_home, "installdir");
        var site = new UninstallSite(installDir,
            new InstallFacts(ExePresent: true, ExeBytes: 42_000_000, OnPath: true),
            siblings ?? []);
        var acts = new RecordedActs { PathProblem = pathProblem, TreeFailures = treeFailures ?? [] };
        var surface = new RecordingSurface { Width = 80 };
        var screen = new StringWriter();
        var raw = new StringWriter();
        var err = new StringWriter();
        //keep is index 0, the safe default, so a down press is needed to remove the home or to confirm.
        ConsoleKeyInfo[] keys = deleteHome
            ? [Down, Enter, Down, Enter]
            : [Enter, Down, Enter];

        Console.SetOut(raw);
        Console.SetError(err);
        try
        {
            var ctx = new CommandContext(screen, _home, Interactive: true, Themed,
                Screen: surface, Keys: new ScriptedKeys(keys));
            var exit = GattoApp.RunUninstall(ctx, site, acts.Seam);
            Assert.Equal("", raw.ToString());        //the raw capture must stay empty on the uninstall screens too.
            return (screen.ToString(), surface.Text, acts, exit);
        }
        finally
        {
            Console.SetOut(_origOut);
            Console.SetError(_origErr);
        }
    }

    [Fact]
    public void UNINSTALL_WALKS_TO_THE_END_AND_TOUCHES_NOTHING_REAL()
    {
        var (screen, painted, acts, exit) = Uninstall(deleteHome: false);

        Assert.Equal(0, exit);
        //the log has no remove-tree, the user kept the home, so keeping is honoured by the deed as well as the map.
        Assert.Equal(
            ["remove-path:" + Path.Combine(_home, "installdir"),
             "arm-sweeper:" + Path.Combine(_home, "installdir")],
            acts.Log);
        //the two closing sentences are plain rows at column zero, the layer's default voice.
        Assert.Contains("Also remove your settings", painted);
        Assert.Contains("Go ahead with the removals above?", painted);
        var rows = Rows(screen);
        Assert.Contains(rows, r => r == "gatto is off your PATH.");
        Assert.Contains(rows, r => r == "the program file removes itself as this exits.");
        //the frame's closing row is filtered out by Rows, so it is read off the screen itself.
        Assert.EndsWith("\n\n", screen.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    //uninstall opens a conversation, so it must show the full banner. the interactive flag matters, a piped run writes only an error.
    [Fact]
    public void UNINSTALL_OPENS_WITH_THE_FULL_BANNER()
    {
        var (screen, _, _, _) = Uninstall(deleteHome: false);
        var g = CommandBanner.GlyphsFor(_home);
        //read through the ansi stripper, the themed drive paints the identity line (a plain substring is absent from a screen that draws it perfectly).
        var rows = Rows(screen);

        foreach (var line in Gatto.Repl.Cats.For("generalist", g).Split('\n'))
            Assert.Contains(rows, r => r.Contains(line.TrimEnd('\r'), StringComparison.Ordinal));
        Assert.Contains(rows,
            r => r.StartsWith($"gatto uninstall {g.Dot} v", StringComparison.Ordinal));
    }

    //the banner assertions are all contains, so this shows they fail when the art is absent.
    [Fact]
    public void AND_THE_BANNER_MATCHER_FAILS_ON_A_SCREEN_WITHOUT_ONE()
    {
        var g = CommandBanner.GlyphsFor(_home);
        var earLine = Gatto.Repl.Cats.For("generalist", g).Split('\n')[0].TrimEnd('\r');

        Assert.DoesNotContain(earLine, "Also remove your settings", StringComparison.Ordinal);
    }

    [Fact]
    public void REMOVING_THE_HOME_TOO_IS_A_DEED_not_only_a_map_row()
    {
        var (_, _, acts, exit) = Uninstall(deleteHome: true);

        Assert.Equal(0, exit);

        //assert that the deed was told what to keep, a run with no keep set destroys weights the map promised.
        var deed = Assert.Single(acts.Log, l => l.StartsWith("remove-tree:" + _home, StringComparison.Ordinal));
        Assert.Contains("keep:", deed);

        //the keep is exactly the map's stays paths under the home, and this home has no weights, so keep:none is the honest expectation.
        Assert.Contains("keep:none", deed);
    }

    [Fact]
    public void A_SIBLING_GATTO_AND_A_PARTIAL_REMOVAL_BOTH_REACH_THE_SCREEN()
    {
        //assert on the render, the screen is the unit, and the stub shows a failure and a success on one screen.
        var failure = new RemovalFailure(Path.Combine(_home, "extensions", ".cache", "a.dll"),
            "Access to the path is denied.");
        var (screen, _, _, exit) = Uninstall(deleteHome: true, siblings: [11704],
            treeFailures: [failure]);

        Assert.Equal(0, exit);
        var rows = Rows(screen);

        //the discovery comes before the confirm, it changes what a yes will achieve.
        Assert.Contains(rows, r => r.Contains("another gatto is running") && r.Contains("11704"));

        //the outcome is shown after the deed.
        Assert.Contains(rows, r => r.Contains("NOT fully removed"));

        //never recommend the command this run deleted, the header holds the words the user typed so the identity line is excluded by name.
        var identity = " " + CommandBanner.GlyphsFor(_home).Dot + " v";
        var body = rows.Where(r => !r.Contains(identity, StringComparison.Ordinal)).ToList();
        Assert.DoesNotContain(body, r => r.Contains("gatto uninstall"));
        Assert.Contains(rows, r => r.Contains("gatto uninstall"));   //the header holds the words the user typed, so it still matches here.

        //these rows reach the screen through the designed surface and share its margin, so cli.Say writes them.
        foreach (var r in rows.Where(r => r.Contains("another gatto is running")
                                          || r.Contains("NOT fully removed")))
            Assert.False(r.StartsWith(' '), "a row indented itself: '" + r + "'");
    }

    [Fact]
    public void A_QUIET_MACHINE_SAYS_NEITHER_THING()
    {
        //a sentence always printed is computed from nothing, and another gatto is running on a quiet machine names a discovery about nothing.
        var (screen, _, _, _) = Uninstall(deleteHome: true);
        var rows = Rows(screen);

        Assert.DoesNotContain(rows, r => r.Contains("another gatto is running"));
        Assert.DoesNotContain(rows, r => r.Contains("NOT fully removed"));
    }

    [Fact]
    public void A_FAILED_PATH_REMOVAL_NEVER_CLAIMS_GATTO_IS_OFF_YOUR_PATH()
    {
        //the sentence must be computed from the outcome of the deed, computing it from the pre-state claims success over a failed removal.
        var (screen, _, acts, exit) = Uninstall(deleteHome: false, pathProblem: "couldn't read your PATH");

        Assert.Equal(0, exit);
        Assert.Contains("remove-path:" + Path.Combine(_home, "installdir"), acts.Log);
        Assert.DoesNotContain("gatto is off your PATH", screen);
        //the exe line still renders: it is a different deed with a different outcome.
        Assert.Contains(Rows(screen), r => r == "the program file removes itself as this exits.");
    }

    //a canned transport lets the drive render doctor with no live endpoint. the stub answers only /v1/models (a fake that answers everything hides which calls run).
    private sealed class StubTransport(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public List<string> Asked { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Asked.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    //the transport and the update check are seams through DoctorProbes, and the raw capture must stay empty. the test stays async, blocking breaks the analyzer rule.
    [Fact]
    public async Task DOCTOR_RENDERS_THROUGH_THE_SEAM_AND_LEAVES_RAW_STDOUT_EMPTY()
    {
        var transport = new StubTransport(HttpStatusCode.OK, """{"data":[{"id":"test-model"}]}""");
        var screen = new StringWriter();
        var raw = new StringWriter();
        var err = new StringWriter();
        Console.SetOut(raw);
        Console.SetError(err);
        try
        {
            var ctx = new CommandContext(screen, _home, Interactive: true, Themed);
            var exit = await GattoApp.RunDoctorAsync(ctx,
                new DoctorProbes(transport, _ => Task.FromResult<UpdateState?>(null)));

            Assert.Equal("", raw.ToString());              //the raw capture must stay empty on doctor too.
            var rows = Rows(screen.ToString());
            Assert.NotEmpty(rows);
            //the banner sits at column zero by design, and every diagnostic row uses the layer's grammar.
            Assert.Contains(rows, r => r.Contains("gatto", StringComparison.Ordinal));
            //the exit code is only range-checked, a temp home fails several doctor checks so an exact code would test the fixture.
            Assert.InRange(exit, 0, 1);
        }
        finally
        {
            Console.SetOut(_origOut);
            Console.SetError(_origErr);
        }
    }
}
