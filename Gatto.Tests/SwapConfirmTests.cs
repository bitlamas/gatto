using Gatto.Cli;
using Gatto.Core.Home;
using Gatto.Roles;

namespace Gatto.Tests;

//the screen that confirms a model swap before the old model is unloaded
public class SwapConfirmTests : IDisposable
{
    private readonly string _modelsDir = Directory.CreateTempSubdirectory("gatto-swap-").FullName;
    public void Dispose() { try { Directory.Delete(_modelsDir, true); } catch { } }

    private Model MakeModel(string id, bool? autoServe = null, int port = 1235)
    {
        var dir = Path.Combine(_modelsDir, id);
        Directory.CreateDirectory(dir);
        var consent = autoServe is { } a ? $", \"auto_serve\": {(a ? "true" : "false")}" : "";
        File.WriteAllText(Path.Combine(dir, "profile.json"),
            $"{{\"files\": [{{\"path\": \"{id}.gguf\", \"active\": true}}], \"port\": {port}, \"context\": 8192{consent}}}");
        return Model.Load(_modelsDir, id);
    }

    private static string Body(Gatto.Repl.WizardAsk ask) =>
        string.Join(" ", (ask.BodyRows ?? []).Select(r => r.Text));

    [Fact]
    public void THE_HISTORY_LINE_IS_THERE_because_the_re_read_is_the_cost_the_user_pays()
    {
        //a re-prefill is the user's choice here, and the wait is announced before it starts rather than left as an unexplained pause
        var ask = SwapConfirm.Compose(MakeModel("qwen"), MakeModel("gemma", autoServe: true), "qwen");

        var body = Body(ask);
        Assert.Contains("read again by the new model", body, StringComparison.Ordinal);
        Assert.Contains("take longer", body, StringComparison.Ordinal);
        //the wait is named but no number is given, since how long a re-read takes depends on the conversation and the machine
        Assert.DoesNotContain("second", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("minute", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void THE_CONSEQUENCE_NAMES_BOTH_MODELS_so_neither_is_a_surprise()
    {
        var ask = SwapConfirm.Compose(MakeModel("qwen"), MakeModel("gemma", autoServe: true), "qwen");

        Assert.Contains("gemma", ask.Question, StringComparison.Ordinal);
        Assert.Contains("qwen", Body(ask), StringComparison.Ordinal);
        Assert.Equal(2, ask.Options.Count);
    }



    //the unload side names the model the server holds, so the fixture makes it differ from the session's
    [Fact]
    public void THE_UNLOAD_SIDE_NAMES_THE_SERVED_MODEL_not_the_session_one()
    {
        var ask = SwapConfirm.Compose(
            outgoing: MakeModel("gemma"),          //the model the session is composed for, which the server may not hold
            incoming: MakeModel("lfm", autoServe: true),
            servingModelId: "qwen");               //the model the server actually holds

        var unload = ask.Options[0].Label;
        Assert.Contains("unload qwen", unload, StringComparison.Ordinal);
        Assert.DoesNotContain("unload gemma", unload, StringComparison.Ordinal);
        //the consequence row comes from the same fact, so it can't drift from the label
        Assert.Contains("qwen's server stops", Body(ask), StringComparison.Ordinal);
        //declining keeps the server as it is, so this label names the model the server holds
        Assert.Equal("No, keep qwen running", ask.Options[1].Label);
    }

    //declining reads stay when the server already holds the session's model
    [Fact]
    public void DECLINING_STAYS_ON_THE_SESSION_MODEL_WHEN_THE_SERVER_HOLDS_IT()
    {
        var options = SwapConfirm.SwitchOptions("qwen", "gemma", "qwen");
        Assert.Equal("No, stay on qwen", options[1].Label);
    }

    [Fact]
    public void ESC_IS_A_STAY_never_a_swap_nobody_confirmed()
    {
        Assert.False(SwapConfirm.Confirmed(null));
        Assert.False(SwapConfirm.Confirmed("No — stay on qwen"));
        Assert.True(SwapConfirm.Confirmed("Yes — unload qwen and load gemma"));
    }

    //shape B: the same swap asked as the wizard's done step

    //the assertions read the member rather than copied strings, so a shape that grew its own copy of the sentences fails here
    [Fact]
    public void SHAPE_B_READS_ITS_ROWS_QUESTION_AND_OPTIONS_AT_THE_MEMBER()
    {
        var outgoing = MakeModel("qwen");
        var incoming = MakeModel("gemma", autoServe: null, port: 1235);

        var b = SwapConfirm.InSessionDone(outgoing.Id, incoming.Id, "qwen");
        var prompterForm = SwapConfirm.Compose(outgoing, incoming, "qwen");

        //the oracle is the prompter form's rows, since reading the member here would be the code agreeing with itself
        Assert.Equal(
            (prompterForm.BodyRows ?? []).Select(r => r.Text),
            (b.BodyRows ?? []).Select(r => r.Text));
        //the two consequence rows are the whole body
        Assert.Equal(2, (b.BodyRows ?? []).Count);
        //the member both surfaces read
        Assert.Equal(
            SwapConfirm.Consequences(incoming.Id, "qwen").Select(r => r.Text),
            (b.BodyRows ?? []).Select(r => r.Text));
        Assert.Equal(prompterForm.Question, b.Question);
        Assert.Equal(prompterForm.Options.Select(o => o.Label), b.Options.Select(o => o.Label));
    }


    //the predicate reads the label's first word, so both surfaces have to keep it
    [Fact]
    public void BOTH_SURFACES_LABELS_STILL_ANSWER_THE_SAME_PREDICATE()
    {
        var outgoing = MakeModel("qwen");
        var incoming = MakeModel("gemma", autoServe: true);
        var options = SwapConfirm.SwitchOptions(outgoing.Id, incoming.Id, "qwen");

        Assert.True(SwapConfirm.Confirmed(options[0].Label));
        Assert.False(SwapConfirm.Confirmed(options[1].Label));
    }
    //the notice is only for a wizard switch that moved the model, so confirming a switch to the model already serving says nothing
    [Theory]
    [InlineData(true, "qwen", "gemma", "now on gemma, everything said so far is read again on your next message")]
    [InlineData(true, "gemma", "gemma", null)]
    [InlineData(false, "qwen", "gemma", null)]
    public void THE_SWITCH_NOTICE_IS_THE_WIZARD_ROAD_THAT_ACTUALLY_MOVED(
        bool confirmed, string outgoing, string incoming, string? says)
        => Assert.Equal(says, SwapConfirm.SwitchNotice(confirmed, outgoing, incoming));

    //the id comparison ignores case, or a differently typed id would read as a move
    [Fact]
    public void A_DIFFERENT_SPELLING_OF_THE_SAME_ID_IS_NOT_A_MOVE()
        => Assert.Null(SwapConfirm.SwitchNotice(true, "Gemma-4-26B", "gemma-4-26b"));
}
