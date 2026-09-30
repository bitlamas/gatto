using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

public class AddRoadFaceTests
{
    private static readonly ConsoleKeyInfo Esc = new('\u001b', ConsoleKey.Escape, false, false, false);
    private static readonly ConsoleKeyInfo Enter = new('\r', ConsoleKey.Enter, false, false, false);

    //the fake throws when the script runs out, so an exhausted script fails instead of spinning on a default key
    private sealed class Keys(IEnumerable<ConsoleKeyInfo> k) : IKeySource
    {
        private readonly Queue<ConsoleKeyInfo> _q = new(k);
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => _q.Count > 0
            ? _q.Dequeue()
            : throw new InvalidOperationException("the face asked for a key the script does not have");
    }

    //the clock never advances, so WaitForKey must say the key is ready or the wait never ends
    private sealed class Ready : Gatto.Repl.IPollClock
    {
        public bool WaitForKey(TimeSpan budget) => true;
        public long ElapsedMs => 0;
    }

    //take the screen from the flow, it stamps the route, and leave CanSwitchSource false so the local shelf answers with no network
    private static WizardScreen.Choice AddRoadScreen()
    {
        var gguf = Path.Combine(Path.GetTempPath(), "addroadface-tiny.gguf");
        if (!File.Exists(gguf)) File.WriteAllBytes(gguf, GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "qwen3");
            kv.U32("qwen3.context_length", 4096);
        }));

        var flow = new SetupFlow(new WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            Found = [new Gatto.Core.Acquire.FoundModel(gguf, 4_000_000_000, null)],
        });
        var screen = Assert.IsType<WizardScreen.Choice>(flow.StartAtModelSegment());
        //the footer row exists only for a screen with a shelf, so the fixture asserts it has one
        Assert.NotNull(screen.Shelf);
        return screen;
    }

    private static (TuiWizardSurface F, Func<IReadOnlyList<string>> Painted) Face(
        params ConsoleKeyInfo[] keys)
    {
        var f = new TuiWizardSurface(new RecordingSurface { Width = 100 }, new Keys(keys),
            new Theme(new TermCaps(true, true)),
            Gatto.Core.GattoVersion.String, "1a2b3c4", () => 0, clock: _ => new Ready());
        return (f, () => f.LastPainted);
    }

    //the gatto model command owns the terminal, so it draws its own banner and version
    [Fact]
    public void THE_ADD_ROAD_DRAWS_ITS_BANNER()
    {
        var frame = WalkRender.Choice(AddRoadScreen(), 100, script: [Enter]).Rows;

        Assert.Contains("gatto setup", frame[0], StringComparison.Ordinal);
        //assert the fixture's own version literal, the live release value moves with the next release
        Assert.Contains("0.5.0", frame[0], StringComparison.Ordinal);
        //the head is the banner row and the rule under the strip, so assert both
        Assert.StartsWith("─", frame[2], StringComparison.Ordinal);
    }

    //assert the Esc word itself, "leave" also appears in this screen's body copy
    [Fact]
    public void THE_ADD_ROADS_FOOTER_SAYS_LEAVE_NOT_BACK()
    {
        //the script sends Enter (an Esc script captures the armed frame, whose footer holds the chord's sentence instead of the keys row)
        var frame = WalkRender.Choice(AddRoadScreen(), 100, script: [Enter]).Rows;

        var keys = frame.LastOrDefault(r => r.Contains("Esc ", StringComparison.Ordinal));
        Assert.True(keys is not null, $"the frame has no keys row:\n{string.Join("\n", frame)}");
        Assert.Contains("Esc leave", keys!, StringComparison.Ordinal);
        Assert.DoesNotContain("Esc back", keys!, StringComparison.Ordinal);
    }

    //the Esc key must arm the chord rather than leave at once, a standalone run can lose progress
    [Fact]
    public void ON_THE_ADD_ROAD_ESC_ARMS_THE_CHORD_RATHER_THAN_LEAVING_AT_ONCE()
    {
        var (f, painted) = Face(Esc, Esc);

        Assert.Null(f.Choose(AddRoadScreen()));
        Assert.Contains(painted(), r => r.Contains("Esc again to leave", StringComparison.Ordinal));
    }
}
