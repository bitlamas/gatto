using Gatto.Cli.Setup;

namespace Gatto.Tests.Census;

//nothing the user reads may quote the wait before the load ask. a screen guard can't hold it, the copy has many homes and the constant has one
public class LoadAskConstantCensusTests
{
    //the spellings a person would write. a bare digit search would match 5 tasks and task 3 of 5, and nobody could keep it green
    private static readonly string[] Quoted =
        ["5 minutes", "five minutes", "5 min", "five min", "300 seconds"];

    private static bool Quotes(string text) =>
        Quoted.Any(q => text.Contains(q, StringComparison.OrdinalIgnoreCase));

    //the matcher is checked on a sentence that quotes the figure, so it and the absence test can't drift onto different patterns
    [Fact]
    public void THE_MATCHER_FINDS_A_SENTENCE_THAT_QUOTES_THE_FIGURE()
    {
        Assert.True(Quotes("gatto will ask again in 5 minutes."));
        Assert.True(Quotes("It waits five min before asking."));
        //the matcher must not fire on ordinary copy. otherwise a green match would mean nothing.
        Assert.False(Quotes("gatto will give it five short tasks and watch what happens."));
        Assert.False(Quotes("task 3 of 5"));
    }

    //no row on the load ask quotes the threshold. the only figure there is the time actually waited
    [Fact]
    public void THE_LOAD_ASK_QUOTES_NO_CONSTANT()
    {
        var probes = new Gatto.Tests.Fakes.WizardProbes
        {
            Llama = @"C:\llama\llama-server.exe",
            ActiveFile = "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            AuditionBlockUntilCancelled = true,
        };
        var flow = new SetupFlow(probes) { LoadAskAfter = TimeSpan.FromMilliseconds(40) };
        flow.StartAtModelSegment();
        var screen = flow.ResumeAfterWrites("gemma-4-26B-A4B-it");
        while (flow.NeedsWritesApplied) screen = flow.ResumeAfterWrites(null);
        flow.Answer(SetupFlow.Yes);

        Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(20)), "no question");
        var ask = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.Landed));
        Assert.Equal(SetupFlow.AuditionLoadAskKey, ask.Key);

        //the assertion above proves the screen was reached, so a non-match is not vacuous.
        var said = string.Join("\n", ask.BodyRows!)
            + "\n" + ask.Question
            + "\n" + string.Join("\n", ask.Options.Select(o => o.Label));

        Assert.False(Quotes(said), $"the load ask quotes its own threshold:\n{said}");

        flow.Answer(SetupFlow.StopUnchecked);   //this answer releases the held check rather than leaking a thread.
    }
}
