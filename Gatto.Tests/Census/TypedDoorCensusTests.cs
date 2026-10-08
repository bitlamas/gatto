using System.Text.RegularExpressions;
using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;
using Gatto.Tests.Setup;

namespace Gatto.Tests.Census;

//every screen that offers typed input must answer it and verify what was typed. a screen that only doesn't throw can swallow the typed path
public class TypedDoorCensusTests
{
    private const string TypedExe = @"D:\somewhere\else\llama-server.exe";

    //the exe that reaches the failed screen is deliberately not the one typed later. that screen re-checks the exe it named, so the two answers have to disagree
    private const string RoutedExe = @"D:\the\first\llama-server.exe";
    private const string TypedDir = @"D:\somewhere\else";
    private const string Into = @"C:\Users\you\.gatto\llama\b10076\";
    private const string Zip = "llama-b10076-bin-win-vulkan-x64.zip";

    private static readonly Gatto.Roles.LlamaAsset Pick = new(Zip, null, "an AMD discrete card", "Vulkan");
    private static readonly EngineAsset Asset =
        new(Zip, "https://example.invalid/" + Zip, new string('a', 64), 214L * 1024 * 1024);
    private static readonly HubQuant Quant = new("m-Q4_K_M.gguf", 1_000_000_000, null);

    private static ModelRow Row() => ShelfRows.Of("unsloth/m", "unsloth", Quant,
        FitRegime.FitsGpu, 32768, false, null, 10, false, Params: 4_000_000_000);

    //reaches every screen with a typed option the way a user reaches it. the screen key is asserted before anything is typed, so a drifted recipe fails
    private static (SetupFlow Flow, WizardProbes Probes, List<string> Verified, WizardScreen Screen)
        At(string key)
    {
        var verified = new List<string>();

        //the verify probe records each path it is called with. only the first call on the failed screen's route fails, and that failure is what reaches the screen.
        Func<string, ProbeResult> recording = p =>
        {
            verified.Add(p);
            return key == SetupFlow.FailedKey && verified.Count == 1
                ? new ProbeResult(ProbeShape.DllNotFound, "d", Build: "b10076", VcRuntimeAbsent: true)
                : new ProbeResult(ProbeShape.ClassicServer, "d", Build: "b10076");
        };

        WizardProbes probes = key switch
        {
            SetupFlow.FoundKey or SetupFlow.SearchKey or SetupFlow.RetryKey or SetupFlow.ShelfLoadingKey =>
                new() { Verify = recording },

            SetupFlow.ConsentKey => new()
            {
                Llama = null,
                Asset = Pick,
                Offer = new EngineFetchOffer(Into, new EnginePair(Asset, null)),
                Verify = recording,
            },

            SetupFlow.FallbackKey => new()
            {
                Llama = null,
                Asset = Pick,
                Offer = new EngineFetchOffer(Into, null),
                Verify = recording,
            },

            SetupFlow.SteerKey or SetupFlow.FailedKey =>
                new() { Llama = null, Offer = null, Verify = recording },

            SetupFlow.ConfirmServerKey => new()
            {
                //use a fake port rather than gatto's live one. a test naming the live port needs an allowlist entry, and a fixture detail must not widen that safety rule
                Server = new ConnectProbe("http://127.0.0.1:18080", ["m"], null),
                Verify = recording,
            },

            SetupFlow.DiscoveredKey => new() { Roots = [@"C:\models"], Verify = recording },
            SetupFlow.DownloadKey => new() { Rows = [Row()], Verify = recording },

            SetupFlow.ModelConsentKey => new()
            {
                Rows = [Row()],
                HubOffer = new ModelFetchOffer("unsloth/m", "m", Into, Quant, null),
                Verify = recording,
            },

            _ => throw new InvalidOperationException($"no reach recipe for the door screen '{key}'"),
        };

        //the loading shelf is drawn only by a face that says it draws one, so its route sets what the runner sets
        var flow = new SetupFlow(probes) { LoadsShelfInBackground = key == SetupFlow.ShelfLoadingKey };
        var screen = flow.StartPastOpening();

        foreach (var answer in Route(key))
            screen = flow.Answer(answer);

        Assert.Equal(key, ScreenKey.Of(screen));
        return (flow, probes, verified, screen);
    }

    //a typed answer must be wrapped exactly as the face wraps it, so this calls the same member. a bare key that is nobody's option isn't typed input and throws
    private static string DoorAnswer(WizardScreen screen, string text) =>
        ShelfControls.TypedAnswer(text);

    private static IReadOnlyList<string> Route(string key) => key switch
    {
        SetupFlow.FoundKey or SetupFlow.ConsentKey or SetupFlow.FallbackKey
            or SetupFlow.SteerKey => [],
        //this route imitates a typed folder path, so the answer goes through the same ShelfControls.TypedAnswer wrap the face uses
        SetupFlow.FailedKey => [ShelfControls.TypedAnswer(RoutedExe)],
        SetupFlow.RetryKey or SetupFlow.ConfirmServerKey => [SetupFlow.ForkConnect],
        SetupFlow.SearchKey or SetupFlow.ShelfLoadingKey => [SetupFlow.FoundUse],
        SetupFlow.DiscoveredKey => [SetupFlow.FoundUse, SetupFlow.CtlSource],
        SetupFlow.DownloadKey or SetupFlow.ModelConsentKey => [SetupFlow.FoundUse, "0"],
        _ => throw new InvalidOperationException($"no route to '{key}'"),
    };

    //this census lists every setup screen that takes typed input, checked against the source. a new one fails here instead of joining the untested set unnoticed
    public static TheoryData<string> Doors() =>
    [
        SetupFlow.FoundKey, SetupFlow.ConsentKey, SetupFlow.FallbackKey, SetupFlow.SteerKey,
        SetupFlow.FailedKey, SetupFlow.RetryKey, SetupFlow.ConfirmServerKey,
        SetupFlow.DiscoveredKey, SetupFlow.DownloadKey, SetupFlow.ModelConsentKey,
        SetupFlow.SearchKey, SetupFlow.ShelfLoadingKey,
    ];

    //the flow must answer typed text on every screen. a thrown exception quits the wizard mid-setup
    [Theory]
    [MemberData(nameof(Doors))]
    public void EVERY_DOOR_SCREEN_ANSWERS_A_TYPED_STRING(string key)
    {
        var (flow, _, _, screen) = At(key);

        var next = flow.Answer(DoorAnswer(screen, TypedExe));

        Assert.NotNull(next);
    }

    //every typed screen must reject a bare key rather than read it as a folder, which starts a multi-gigabyte download
    [Theory]
    [MemberData(nameof(Doors))]
    public void A_BARE_KEY_IS_REFUSED_BY_EVERY_DOOR_SCREEN(string key)
    {
        var (flow, _, _, _) = At(key);

        Assert.Throws<InvalidOperationException>(() => flow.Answer("consent.something"));
    }

    //an engine screen's handler must verify the path the user typed. accepting an answer without verifying it shows a result for a path nobody chose.
    [Theory]
    [InlineData(SetupFlow.FoundKey)]
    [InlineData(SetupFlow.ConsentKey)]
    [InlineData(SetupFlow.FallbackKey)]
    [InlineData(SetupFlow.SteerKey)]
    [InlineData(SetupFlow.FailedKey)]
    public void AN_ENGINE_DOOR_VERIFIES_THE_PATH_THE_USER_TYPED(string key)
    {
        var (flow, _, verified, screen) = At(key);
        var before = verified.Count;

        flow.Answer(DoorAnswer(screen, TypedExe));

        Assert.Contains(TypedExe, verified.Skip(before));
    }

    //the discovered screen must scan the folder the user typed. a row-index parse throws on anything that isn't a small integer
    [Fact]
    public void THE_DISCOVERED_DOOR_SCANS_THE_FOLDER_THE_USER_TYPED()
    {
        var (flow, probes, _, screen) = At(SetupFlow.DiscoveredKey);
        var before = probes.ScanRoots.Count;

        flow.Answer(DoorAnswer(screen, TypedDir));

        Assert.Contains(TypedDir, probes.ScanRoots.Skip(before));
    }

    //a row index must stay a row. the flow decides a folder answer by failing to parse an integer, so a path-first check would swallow every row
    [Fact]
    public void A_ROW_INDEX_IS_NOT_READ_AS_A_TYPED_FOLDER()
    {
        var probes = new WizardProbes
        {
            Roots = [@"C:\models"],
            Found = [new FoundModel(@"C:\m\a.gguf", 1_000_000_000, null)],
        };
        var flow = new SetupFlow(probes);
        flow.StartPastOpening();
        flow.Answer(SetupFlow.FoundUse);
        var before = probes.ScanRoots.Count;

        flow.Answer("0");

        Assert.Equal(before, probes.ScanRoots.Count);
    }

    //the screen key that each Door: in the flow belongs to. read it from the masked source, so comments and steeredDoor cannot match
    private static HashSet<string> DoorScreensInSource()
    {
        var src = SourceMask.Mask(SourceTree.Read(
            Path.Combine(SourceTree.RepoRoot(), "Gatto", "Cli", "Setup", "SetupFlow.cs")));

        var consts = Regex.Matches(src, @"const\s+string\s+(\w+)\s*=")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(src, @"(?<![A-Za-z])Door:"))
        {
            //the screen key is the constant that closes the Emit containing this Door:, a few lines below
            var close = Regex.Match(src[m.Index..], @"\)\s*,\s*(\w+Key)\s*\)\s*;");
            Assert.True(close.Success,
                $"a Door: at offset {m.Index} belongs to no Emit this census can read");
            var name = close.Groups[1].Value;
            Assert.True(consts.Contains(name), $"'{name}' is not a screen-key constant");
            keys.Add(name);
        }

        return keys;
    }

    //this check compares the typed-input screens in the source with the ones this census drives. a new such screen fails here until a test types into it.
    [Fact]
    public void THE_CENSUS_DRIVES_EVERY_DOOR_IN_THE_FLOW()
    {
        var driven = new HashSet<string>(
            new[]
            {
                nameof(SetupFlow.FoundKey), nameof(SetupFlow.ConsentKey), nameof(SetupFlow.FallbackKey),
                nameof(SetupFlow.SteerKey), nameof(SetupFlow.FailedKey), nameof(SetupFlow.RetryKey),
                nameof(SetupFlow.ConfirmServerKey), nameof(SetupFlow.DiscoveredKey),
                nameof(SetupFlow.DownloadKey), nameof(SetupFlow.ModelConsentKey),
                nameof(SetupFlow.SearchKey), nameof(SetupFlow.ShelfLoadingKey),
            }, StringComparer.Ordinal);

        var inSource = DoorScreensInSource();

        Assert.True(inSource.SetEquals(driven),
            "the flow's door screens and the ones this census drives have parted:\n"
            + $"  only in the source: {string.Join(", ", inSource.Except(driven).Order())}\n"
            + $"  only in the census: {string.Join(", ", driven.Except(inSource).Order())}");
    }

    //this count is the floor for the source reader, so an empty answer can't pass. the set equality in the other test is satisfied by two empty sets
    [Fact]
    public void AND_THE_READER_CAN_SEE_THE_DOORS()
    {
        Assert.Equal(12, DoorScreensInSource().Count);
    }

    //the fixture adds a new screen with a Door: argument, a reader matching nothing would agree with the census forever
    [Fact]
    public void THE_READER_FINDS_A_DOOR_THAT_WAS_JUST_ADDED()
    {
        const string source = "        return Emit(new WizardScreen.Choice(\n"
            + "            BrandNewKey,\n"
            + "            \"a question?\",\n"
            + "            [],\n"
            + "            Door: \"type something\u2026\"), BrandNewKey);\n";

        var close = Regex.Match(SourceMask.Mask(source)[source.IndexOf("Door:", StringComparison.Ordinal)..],
            @"\)\s*,\s*(\w+Key)\s*\)\s*;");

        Assert.True(close.Success, "the reader cannot find the key of a door screen");
        Assert.Equal("BrandNewKey", close.Groups[1].Value);
    }

    //the negative is steeredDoor: true, which contains the text the pattern searches for
    [Fact]
    public void A_STEERED_DOOR_ARGUMENT_IS_NOT_A_DOOR()
    {
        Assert.Empty(Regex.Matches("        steeredDoor: true, result.VcRuntimeAbsent)",
            @"(?<![A-Za-z])Door:"));
    }

    //a fixture naming a real screen key may not give it a Door the flow does not set. synthetic keys are exempt, their screen has no producer
    private static readonly Regex FixtureDoor = new(
        @"new(?:\s+WizardScreen\.Choice)?\(\s*SetupFlow\.(\w+Key)\b[^;]*?Door:",
        RegexOptions.Compiled | RegexOptions.Singleline);

    //the matcher is shown a known match and a known miss before the tree check trusts its answer
    [Fact]
    public void THE_FIXTURE_READER_FIRES_ON_A_PLANTED_DOOR_AND_REFUSES_A_DOORLESS_FIXTURE()
    {
        Assert.Equal("MachineKey", FixtureDoor
            .Match("new WizardScreen.Choice(SetupFlow.MachineKey, \"t\", opts, Door: \"x\");")
            .Groups[1].Value);
        Assert.Equal("SearchKey", FixtureDoor
            .Match("new(SetupFlow.SearchKey, \"t\", opts, Shelf: v, Door: D);").Groups[1].Value);
        Assert.DoesNotMatch(FixtureDoor, "new WizardScreen.Choice(SetupFlow.MachineKey, \"t\", opts);");
        //a synthetic key matches nothing, stating the exemption as a pattern rather than a list of files.
        Assert.DoesNotMatch(FixtureDoor, "new WizardScreen.Choice(\"planted.shelf\", \"t\", opts, Door: \"x\");");
    }

    [Fact]
    public void NO_FIXTURE_GIVES_A_SCREEN_A_DOOR_ITS_PRODUCER_DOES_NOT_SET()
    {
        var real = DoorScreensInSource();
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(
            Path.Combine(SourceTree.RepoRoot(), "Gatto.Tests"), "*.cs", SearchOption.AllDirectories))
        {
            if (path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                continue;

            //this file is skipped, the fixtures inside it look like the defect. the scan would report its own evidence, but those fixtures build no screen
            if (Path.GetFileName(path) == "TypedDoorCensusTests.cs") continue;

            foreach (Match m in FixtureDoor.Matches(File.ReadAllText(path)))
                if (!real.Contains(m.Groups[1].Value))
                    offenders.Add($"{Path.GetFileName(path)} gives {m.Groups[1].Value} a door");
        }

        Assert.True(offenders.Count == 0,
            "a fixture claims a door the flow does not set, which is how the shelf's missing door "
            + "survived two waves: " + string.Join("; ", offenders.Order()));
    }
}
