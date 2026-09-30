using Gatto.Cli.Setup;
using Gatto.Core.Tools;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the found engine step: the exe and build come off the generator's own screen, since the golden is its spec
public class EngineTests
{
    //the exe the generator's found screen names, to the byte
    private const string Exe = @"C:\llama\llama-b11071-bin-win-vulkan-x64\llama-server.exe";

    //the release is read through the constant rather than copied, so a moved pin moves this fixture and the golden is what disagrees
    private static readonly string Pin = Gatto.Roles.LlamaAssetSteering.PinnedRelease;

    //a machine with one engine on it, which answers as the pinned release
    private static SetupFlow Flow(string? build = null, string exe = Exe) =>
        new(new WizardProbes
        {
            Llama = exe,
            Verify = _ => new ProbeResult(ProbeShape.ClassicServer, "d", Build: build ?? Pin),
        });

    //the found screen as the flow emits it, reached from the opening. a hand-built one would only prove the renderer works on a screen nobody reaches
    private static WizardScreen.Choice Found(SetupFlow flow)
    {
        return Assert.IsType<WizardScreen.Choice>(flow.StartPastOpening());
    }

    private static WizardScreen.Choice Found() => Found(Flow());

    [Fact]
    public void THE_EXACT_MATCH_ARM_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s4", "found", 100, WalkRender.Choice(Found(), 100, script: [WizardRig.Enter]).Rows);

    //the real banner reports a bare number while gatto spells the build with a b, so this drives the string the machine produces
    [Fact]
    public void A_BANNER_REPORTING_A_BARE_NUMBER_STILL_READS_AS_GATTOS_OWN_SPELLING() =>
        Golden.AssertEquals("s4", "found", 100,
            WalkRender.Choice(Found(Flow(build: "11071")), 100, script: [WizardRig.Enter]).Rows);

    //the longest row here is a path, and one width can't see an overflow that only happens at another
    [Fact]
    public void EVERY_ROW_FITS_AT_EVERY_WIDTH_THE_LADDER_SERVES()
    {
        for (var w = 78; w <= 120; w++)
            foreach (var row in WalkRender.Choice(Found(), w, script: [WizardRig.Enter]).Rows)
                Assert.True(Gatto.Terminal.UnicodeWidth.Of(row) <= w,
                    $"at width {w} a row is {Gatto.Terminal.UnicodeWidth.Of(row)} cells: {row}");
    }

    //the connect door is row 2 here, which is the reason this screen has numbered options at all
    [Fact]
    public void THE_CONNECT_ROAD_IS_ON_THE_SCREEN_AS_ROW_TWO()
    {
        var options = Found().Options;

        Assert.Equal(SetupFlow.FoundUse, options[0].Key);
        Assert.Equal(SetupFlow.ForkConnect, options[1].Key);
        Assert.Equal(2, options.Count);
    }

    //the standing typed door, idle: a machine with two builds points setup at the other one without leaving the screen
    [Fact]
    public void THE_SCREEN_CARRIES_THE_STANDING_TYPED_DOOR() =>
        Assert.Equal(SetupFlow.LlamaDoorPlaceholderOf(Gatto.Terminal.GlyphSet.Unicode), Found().Door);

    //every option goes through Answer, since a missing arm in the dispatch throws and names the screen
    [Fact]
    public void EVERY_OPTION_ON_THE_SCREEN_CAN_BE_ANSWERED()
    {
        foreach (var option in Found().Options)
        {
            var flow = Flow();
            Found(flow);
            Assert.NotNull(flow.Answer(option.Key));
        }
    }

    //the write holds the same exe string the golden shows, so the row the user read and the stored path can't part
    [Fact]
    public void USE_IT_WRITES_THE_EXE_THE_SCREEN_NAMED()
    {
        var flow = Flow();
        var screen = Found(flow);

        Assert.Contains(screen.BodyRows!, r => r.Text.Contains(Exe, StringComparison.Ordinal));

        flow.Answer(SetupFlow.FoundUse);

        Assert.Equal(Exe, flow.Writes.LlamaServer);
        Assert.Equal(SetupPath.Llama, flow.Path);
    }

    //assert on flow.Path rather than the screen that comes back, since a renamed screen would leave the path unchanged
    [Fact]
    public void THE_CONNECT_OPTION_PUTS_THE_WALK_ON_THE_CONNECT_ROAD()
    {
        var flow = Flow();
        Found(flow);

        flow.Answer(SetupFlow.ForkConnect);

        Assert.Equal(SetupPath.Connect, flow.Path);
    }

    //a machine with no engine goes to the steering screen, so the found screen is only reached when one was found
    [Fact]
    public void A_MACHINE_WITH_NO_ENGINE_NEVER_SEES_THIS_SCREEN()
    {
        var flow = new SetupFlow(new WizardProbes { Llama = null });
        Assert.Equal(SetupFlow.SteerKey, ScreenKey.Of(flow.StartPastOpening()));
    }

    //every build the probe can read reaches the screen, and a null build must not, since the rows say gatto read its version
    [Theory]
    [InlineData("b11137", true)]           //newer than the pin
    [InlineData("b9848", true)]            //older than the pin
    [InlineData("laguna-2bd55d0", true)]   //a build gatto can't place
    [InlineData(null, false)]              //the probe read no version
    public void ONLY_A_BUILD_THE_PROBE_COULD_READ_REACHES_THE_SCREEN(string? build, bool reaches)
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = Exe,
            Verify = _ => new ProbeResult(ProbeShape.ClassicServer, "d", Build: build),
        });
        var key = ScreenKey.Of(flow.StartPastOpening());
        Assert.Equal(reaches, key == SetupFlow.FoundKey);
    }

    //the three off-pin arms

    //the first option is the default, and only the older build fetches, since the shelf's fit answers come from the tested release
    [Fact]
    public void THE_NEWER_ARM_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s4", "found-newer", 100, WalkRender.Choice(
            Found(Flow("b11137", Exe.Replace(Pin, "b11137", StringComparison.Ordinal))),
            100, script: [WizardRig.Enter]).Rows);

    //this arm dims one line, which is why the default flips: nobody knows the engine feeds the shelf's answers
    [Fact]
    public void THE_OLDER_ARM_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s4", "found-older", 100, WalkRender.Choice(
            Found(Flow("b9848", Exe.Replace(Pin, "b9848", StringComparison.Ordinal))),
            100, script: [WizardRig.Enter]).Rows);

    //a real fork's report, whose build gatto can't place, so the row shows both and claims no relation between them
    [Fact]
    public void THE_FORK_ARM_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s4", "found-fork", 100, WalkRender.Choice(
            Found(Flow("laguna-2bd55d0", Exe.Replace("llama-" + Pin, "llama-laguna-2bd55d0", StringComparison.Ordinal))),
            100, script: [WizardRig.Enter]).Rows);

    //every found state renders and every option on it answers, and the four builds must reach four different states
    [Fact]
    public void EVERY_FOUND_STATE_RENDERS_AND_EVERY_OPTION_ON_IT_ANSWERS()
    {
        var builds = new[] { Pin, "b11137", "b9848", "laguna-2bd55d0" };
        var states = builds.Select(b => Gatto.Roles.LlamaAssetSteering.Classify(b, null).ToString()).ToList();

        Assert.Equal(Enum.GetValues<Gatto.Roles.LlamaAssetSteering.FoundState>().Length, states.Distinct().Count());

        foreach (var build in builds)
        {
            var screen = Found(Flow(build));
            Assert.NotEmpty(screen.Options);

            foreach (var option in screen.Options)
            {
                var flow = Flow(build);
                Found(flow);
                Assert.NotNull(flow.Answer(option.Key));
            }
        }
    }

    //an answer that is not an option must be refused with nothing verified and nothing written, since a no-op writes nothing too
    [Fact]
    public void AN_ANSWER_THAT_IS_NOT_AN_OPTION_IS_CHECKED_BEFORE_ANYTHING_IS_WRITTEN()
    {
        var asked = new List<string>();
        var flow = new SetupFlow(new WizardProbes
        {
            Verify = p =>
            {
                asked.Add(p);
                return new ProbeResult(ProbeShape.NotClassic, "d", Build: "b11071");
            },
        });
        Found(flow);

        Assert.Throws<InvalidOperationException>(() => flow.Answer("found.something-else"));

        //the bare key never reached the verify, and an earlier step did, so assert on the string rather than an empty list
        Assert.DoesNotContain("found.something-else", asked);
        Assert.Null(flow.Writes.LlamaServer);
    }

    //the keep label is the same deed and key as Use it, since a second key would be two handlers for one thing
    [Fact]
    public void KEEPING_THE_OLDER_BUILD_WRITES_IT_LIKE_ANY_OTHER_USE_IT()
    {
        var exe = Exe.Replace(Pin, "b9848", StringComparison.Ordinal);
        var flow = Flow("b9848", exe);
        var screen = Found(flow);

        Assert.Equal("Keep the one that's here", Assert.Single(
            screen.Options, o => o.Key == SetupFlow.FoundUse && o.Label != "Use it").Label);

        flow.Answer(SetupFlow.FoundUse);

        Assert.Equal(exe, flow.Writes.LlamaServer);
    }

    //the fetch option opens the screen that gets the pinned build, asserted on the screen rather than on a download
    [Fact]
    public void THE_FETCH_OPTION_OPENS_THE_SCREEN_THAT_GETS_THE_TESTED_RELEASE()
    {
        var flow = Flow("b9848", Exe.Replace(Pin, "b9848", StringComparison.Ordinal));
        Found(flow);

        Assert.Equal(SetupFlow.SteerKey, ScreenKey.Of(flow.Answer(SetupFlow.FoundFetch)));
    }

    //one number serves the whole step, since two engine screens disagreeing by a cell is the defect, and it is computed from every label
    [Fact]
    public void THE_ENGINE_STEPS_FACT_COLUMN_IS_COMPUTED_FROM_EVERY_LABEL_IT_SERVES()
    {
        var widest = SetupFlow.EngineFacts.Labels.MaxBy(l => l.Length)!;

        Assert.Equal(13, SetupFlow.EngineFacts.Column);
        Assert.Equal("extract to", widest);

        //one label longer than the widest must move the column by one, and the label is derived from widest rather than hand-counted
        Assert.Equal(SetupFlow.EngineFacts.Column + 1,
            SetupFlow.EngineFacts.Labels.Append(widest + "!").Max(l => l.Length) + 3);
    }

    //the failure arms

    //a flow that reaches the steering screen and types a path the probe refuses
    private static WizardScreen Refused(
        ProbeShape shape, string typed, bool vcAbsent = false, bool cudart = false)
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = null,
            Asset = new Gatto.Roles.LlamaAsset("z.zip", cudart ? "cudart.zip" : null, "a card", "CUDA"),
            Verify = _ => new ProbeResult(shape, "d", VcRuntimeAbsent: vcAbsent),
        });
        flow.StartPastOpening();
        return flow.Answer(ShelfControls.TypedAnswer(typed));
    }

    //the file is right but Windows can't load it, so the same path stays on screen, and one option reads Enter next
    [Fact]
    public void THE_RUNTIME_FAILURE_ARM_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s4", "fail-crt", 100, WalkRender.Choice(
            Assert.IsType<WizardScreen.Choice>(Refused(
                ProbeShape.DllNotFound, @"C:\Users\you\.gatto\llama\b11071\llama-server.exe", vcAbsent: true)),
            100, script: [WizardRig.Enter]).Rows);

    //the file is wrong, so there is no retry and the cursor starts in the door. the Esc word comes from the flow's back flag
    [Fact]
    public void THE_WRONG_KIND_FAILURE_ARM_MATCHES_ITS_GOLDEN() =>
        Golden.AssertEquals("s4", "fail-notclassic", 100, WalkRender.Ask(
            Assert.IsType<WizardScreen.Ask>(Refused(ProbeShape.NotClassic, @"C:\llama\llama-cli.exe")), 100).Rows);

    //the retry must verify the exe the screen named, since a button that checked something else would be the worst kind of working button
    [Fact]
    public void CHECK_IT_AGAIN_RE_RUNS_THE_VERIFY_ON_THE_PATH_THE_SCREEN_NAMED()
    {
        var asked = new List<string>();
        var shape = ProbeShape.DllNotFound;
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = null,
            Verify = p => { asked.Add(p); return new ProbeResult(shape, "d", Build: "b11071", VcRuntimeAbsent: true); },
        });
        flow.StartPastOpening();
        const string typed = @"C:\Users\you\.gatto\llama\b11071\llama-server.exe";
        flow.Answer(ShelfControls.TypedAnswer(typed));

        //the runtime is installed between the two presses, so the second look succeeds
        shape = ProbeShape.ClassicServer;
        flow.Answer(SetupFlow.FailedRetry);

        Assert.Equal([typed, typed], asked);
        Assert.Equal(typed, flow.Writes.LlamaServer);
    }

    //the Esc key on the wrong-kind arm goes back, so AllowBack must be true for the footer's word to hold
    [Fact]
    public void A_REFUSED_PATH_CAN_STEP_BACK()
    {
        var screen = Refused(ProbeShape.NotClassic, @"C:\llama\llama-cli.exe");

        Assert.True(screen.AllowBack, "the wrong-kind arm does not advertise a back road");
    }

    //every failure arm names the exe it tested, since a screen that says what went wrong without where sends the user hunting
    [Theory]
    [InlineData((int)ProbeShape.DllNotFound)]
    [InlineData((int)ProbeShape.NotClassic)]
    [InlineData((int)ProbeShape.TimedOut)]
    [InlineData((int)ProbeShape.Failed)]
    public void EVERY_FAILURE_ARM_NAMES_THE_EXE_IT_TESTED(int shape)
    {
        const string typed = @"D:\somewhere\llama-server.exe";

        Assert.Equal(typed, FailureScreen.Tested(Refused((ProbeShape)shape, typed)));
    }

    //no failure sentence says to point at it again, since the retry key is that clause, and the census covers all six rows
    [Theory]
    [InlineData((int)ProbeShape.DllNotFound, false, false)]
    [InlineData((int)ProbeShape.DllNotFound, true, false)]    //this Windows has no VC++ runtime
    [InlineData((int)ProbeShape.DllNotFound, false, true)]     //the steered build wants a cudart zip
    [InlineData((int)ProbeShape.NotClassic, false, false)]
    [InlineData((int)ProbeShape.TimedOut, false, false)]
    [InlineData((int)ProbeShape.Failed, false, false)]
    public void NO_FAILURE_ARM_TELLS_THE_USER_TO_POINT_AT_IT_AGAIN(int shape, bool vcAbsent, bool cudart)
    {
        var sentence = FailureScreen.Sentence(
            Refused((ProbeShape)shape, @"D:\x\llama-server.exe", vcAbsent, cudart));

        Assert.DoesNotContain("point at it again", sentence, StringComparison.OrdinalIgnoreCase);
        //the sentence must also be non-empty, so a scan that matched nothing because the rows were empty can't read as a pass
        Assert.NotEmpty(sentence);
    }

    //a known sibling opens the typed arm with the door focused, and the sentence names both files
    [Fact]
    public void A_KNOWN_SIBLING_IS_NAMED_AND_SO_IS_THE_FILE_THAT_SHOULD_BE_THERE()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = null,
            Verify = _ => new ProbeResult(ProbeShape.KnownSibling, "d", SiblingName: "llama-cli.exe"),
        });
        flow.StartPastOpening();

        var screen = flow.Answer(ShelfControls.TypedAnswer(@"C:\llama\b11071\llama-cli.exe"));

        Assert.IsType<WizardScreen.Ask>(screen);   //the typed arm, where the fix is another path
        var sentence = FailureScreen.Sentence(screen);
        Assert.Contains("llama-cli.exe", sentence, StringComparison.Ordinal);
        Assert.Contains("llama-server.exe", sentence, StringComparison.Ordinal);
    }

    //an engine that does not verify renders no found screen, since it must not be shown as checked when it was not
    [Fact]
    public void AN_ENGINE_THAT_DOES_NOT_ANSWER_RENDERS_NO_FOUND_SCREEN()
    {
        var flow = new SetupFlow(new WizardProbes
        {
            Llama = Exe,
            Verify = _ => new ProbeResult(ProbeShape.NotClassic, "d", Build: "b11071"),
        });
        Assert.NotEqual(SetupFlow.FoundKey, ScreenKey.Of(flow.StartPastOpening()));
    }
}
