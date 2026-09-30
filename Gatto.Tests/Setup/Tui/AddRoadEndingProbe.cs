using Gatto.Cli.Setup;
using Gatto.Core.Acquire;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup.Tui;

//the questions are pinned by the keys the add flow passes through, and the update gate is driven
public class AddRoadEndingProbe
{
    private const string Incoming = "gemma-4-26B-A4B-it";
    private const string Serving = "qwen-qwen3.6-35b-a3b";

    //null answered means the question was never asked, so the parameter has no default
    private static WizardProbes Probes(string? heldBy, bool? answered) => new()
    {
        Llama = @"C:\llama\llama-server.exe",
        ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
        HeldBy = heldBy,
        UpdateAnswered = answered,
        Audition = new AuditionCheck(AuditionOutcome.Passed,
            new AuditionFacts("Q4_K_M", "greedy", "off", 42.5, 5, 5)),
    };

    //the ending's only key is "back", which Answer intercepts, and a bounded loop stops a looping flow from hanging the suite
    private static List<string> KeysAlong(string? heldBy, bool? answered) =>
        Walk(heldBy, answered).Keys;

    //hands back the flow that ran and the screen it stopped on, so a flag always comes from a real run
    private static (List<string> Keys, SetupFlow Flow, WizardScreen Last) Walk(
        string? heldBy, bool? answered, bool addUnchecked = false, string? stopAt = null)
    {
        var flow = new SetupFlow(Probes(heldBy, answered));
        flow.StartAtModelSegment();
        var screen = flow.ResumeAfterWrites(Incoming);
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);

        var seen = new List<string>();
        for (var i = 0; i < 25; i++)
        {
            var key = ScreenKey.Of(screen);
            seen.Add(key);
            if (key == stopAt || screen is WizardScreen.Terminal
                || key.StartsWith("model.done", StringComparison.Ordinal)) return (seen, flow, screen);
            if (screen is not WizardScreen.Choice c || c.Options.Count == 0) break;
            var pick = addUnchecked && key == SetupFlow.InSessionCheckKey
                ? SetupFlow.AddUnchecked
                : c.Options[0].Key;
            screen = flow.Answer(pick);
            while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        }

        Assert.Fail("the walk never reached an ending: " + string.Join(" -> ", seen));
        return (seen, flow, screen);
    }

    //a run of the add flow asks no install question, gatto model never offers to install gatto
    [Theory]
    [InlineData(null, null)]
    [InlineData(null, false)]
    [InlineData(Serving, null)]
    [InlineData(Serving, false)]
    public void THE_ADD_ROAD_NEVER_ASKS_THE_INSTALL_QUESTION(string? heldBy, bool? answered)
    {
        var keys = KeysAlong(heldBy, answered);

        Assert.DoesNotContain(keys, k => k.StartsWith("install", StringComparison.Ordinal));
        //the audition key proves the run reached the ask (green alone also fits a run that showed one screen)
        Assert.Contains(SetupFlow.AuditionRunningKey, keys);
    }

    //the add flow's own "done" ends the run, so assert the last key and the absence of the whole model.done family
    [Theory]
    [InlineData(null)]
    [InlineData(Serving)]
    public void THE_ADD_ROAD_ENDS_ON_THE_WALKS_OWN_COMPLETION(string? heldBy)
    {
        var keys = KeysAlong(heldBy, answered: false);

        Assert.DoesNotContain(keys, k => k.StartsWith("model.done", StringComparison.Ordinal));
        Assert.Equal("done", keys[^1]);
    }

    //assert both halves of the flag, a screen that never draws can set it and the summary can appear without it
    [Theory]
    [InlineData(null)]
    [InlineData(Serving)]
    public void AND_NEVER_OFFERS_TO_START_GATTO(string? heldBy)
    {
        var (keys, flow, _) = Walk(heldBy, answered: false);

        Assert.DoesNotContain(SetupFlow.SummaryKey, keys);
        Assert.False(flow.StartReplWhenDone);
    }

    //the unchecked arm must end on the add flow's own completion, so the setup summary must not appear
    [Fact]
    public void THE_UNCHECKED_ARM_ENDS_ON_THE_ADD_ROADS_OWN_COMPLETION()
    {
        var (keys, flow, _) = Walk(Serving, answered: false, addUnchecked: true);

        //the in-session key proves the run reached the ask, so it really took this arm
        Assert.Contains(SetupFlow.InSessionCheckKey, keys);
        Assert.DoesNotContain(SetupFlow.SummaryKey, keys);
        Assert.Equal("done", keys[^1]);
        Assert.False(flow.StartReplWhenDone);
    }

    //every arm's record closes with the add flow's own closing line
    [Theory]
    [InlineData(null, false)]
    [InlineData(Serving, false)]
    [InlineData(Serving, true)]
    public void EVERY_ARM_PRINTS_A_RECORD_THAT_CLOSES_WITH_THE_ROUND_TRIP(string? heldBy, bool addUnchecked)
    {
        var (_, flow, _) = Walk(heldBy, answered: false, addUnchecked);

        Assert.NotEmpty(flow.RecordRows());
        Assert.Equal(flow.AddRoadNextStep.Text, flow.RecordClosing?.Text);
    }

    //the update ask comes after the writes, so the armed row can't claim nothing was written
    [Fact]
    public void THE_ARMED_ROW_AFTER_THE_WRITES_CLAIMS_NOTHING_ABOUT_WHAT_WAS_WRITTEN()
    {
        var (keys, _, last) = Walk(heldBy: null, answered: null, stopAt: SetupFlow.UpdateKey);
        Assert.Equal(SetupFlow.UpdateKey, keys[^1]);

        var rows = WalkRender.Choice(Assert.IsType<WizardScreen.Choice>(last), 100,
            script: [WizardRig.Esc, WizardRig.Esc]).Rows;
        var frame = string.Join("\n", rows);

        Assert.Contains("Esc again to leave", frame, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing has been written", frame, StringComparison.Ordinal);
    }

    //a stored answer of either kind skips the question, and both directions share this test
    [Theory]
    [InlineData(null)]
    [InlineData(Serving)]
    public void AND_ASKS_THE_UPDATE_QUESTION_ONLY_WHEN_IT_WAS_NEVER_ANSWERED(string? heldBy)
    {
        Assert.Contains(SetupFlow.UpdateKey, KeysAlong(heldBy, answered: null));

        Assert.DoesNotContain(SetupFlow.UpdateKey, KeysAlong(heldBy, answered: false));
        Assert.DoesNotContain(SetupFlow.UpdateKey, KeysAlong(heldBy, answered: true));
    }
}
