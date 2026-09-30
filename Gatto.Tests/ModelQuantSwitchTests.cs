using Gatto.Cli;
using Gatto.Repl;
using Gatto.Roles;

namespace Gatto.Tests;

//switching a quant while the model serves restarts its server and the conversation is read again, so the screen has to say so
public class ModelQuantSwitchTests : IDisposable
{
    private static Gatto.Repl.WizardAsk Ask() => SwapConfirm.QuantSwitch("gemma-4-e4b-it", "Q6_K", "Q4_K_M");

    private readonly string _modelsDir = Directory.CreateTempSubdirectory("gatto-quant-").FullName;
    public void Dispose() { try { Directory.Delete(_modelsDir, true); } catch { } }

    private const string Borrowed = "read again";

    //the quant confirm and the model swap both check through here, so the two screens can't drift apart
    private static bool SaysReadAgain(string text) =>
        text.Contains(Borrowed, StringComparison.Ordinal);

    //pin the question as the exact sentence Run <id> on <quant> from now on?
    [Fact]
    public void THE_QUESTION_IS_THE_SPECS_OWN_SENTENCE()
    {
        Assert.Equal("Run gemma-4-e4b-it on Q6_K from now on?", Ask().Question);
    }

    //the restart and its cost come before the answer, the first message after a switch takes longer
    [Fact]
    public void THE_BODY_SAYS_THE_SERVER_RESTARTS_AND_THE_CONVERSATION_IS_READ_AGAIN()
    {
        var body = string.Join("\n", Ask().BodyRows!.Select(r => r.Text));

        Assert.Contains("Q6_K", body, StringComparison.Ordinal);
        Assert.Contains("restarts", body, StringComparison.Ordinal);
        Assert.True(SaysReadAgain(body), $"the screen owes the sentence that the conversation is read again: {body}");
    }

    //each answer names its outcome, a bare yes would leave the reader holding the consequence in memory
    [Fact]
    public void BOTH_ANSWERS_NAME_WHAT_THEY_DO()
    {
        var options = Ask().Options!.Select(o => o.Label).ToList();

        Assert.Contains(options, o => o.Contains("Q6_K", StringComparison.Ordinal));
        Assert.Contains(options, o => o.Contains("Q4_K_M", StringComparison.Ordinal));
    }

    //the no answer changes nothing, so it still names what the user keeps and what stays on disk
    [Fact]
    public void THE_STAY_SIDE_NAMES_WHAT_THE_USER_KEEPS_AND_WHAT_THEY_DO_NOT_LOSE()
    {
        var no = Assert.Single(Ask().Options!, o => o.Label.StartsWith("No", StringComparison.Ordinal));

        Assert.Contains("keep Q4_K_M", no.Label, StringComparison.Ordinal);
        Assert.Contains("stays on disk", no.Label, StringComparison.Ordinal);
    }

    //a quant switch and a model swap both restart a server, so one matcher checks the cost sentence on both screens
    [Fact]
    public void AND_THE_SWAP_SCREEN_SAYS_THE_SAME_THING()
    {
        var swap = SwapConfirm.Compose(
            MakeModel("qwen"), MakeModel("gemma"), servingModelId: "qwen");
        var body = string.Join("\n", swap.BodyRows!.Select(r => r.Text));

        Assert.True(SaysReadAgain(body),
            "the swap screen's history sentence no longer says 'read again' - the needle above is stale");
    }

    private Model MakeModel(string id)
    {
        var dir = Path.Combine(_modelsDir, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "profile.json"),
            $"{{\"files\": [{{\"path\": \"{id}.gguf\", \"active\": true}}], \"port\": 1235, \"context\": 8192}}");
        return Model.Load(_modelsDir, id);
    }
}
