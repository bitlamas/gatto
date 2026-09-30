using Gatto.Cli;
using Gatto.Repl;

namespace Gatto.Tests;

//forgetting a file never deletes the gguf, so no word on the screen may hint at freed disk
public class ModelQuantRemoveTests
{
    private static WizardAsk Ask() => SwapConfirm.QuantRemove("gemma-4-e4b-it", "Q6_K");

    private static string Body() => string.Join("\n", Ask().BodyRows!.Select(r => r.Text));

    private const string Forbidden = "delete";

    //the checks for the word and the strings that prove the matcher sees it both go through here, so the pattern can't drift
    private static bool SaysDelete(string text) =>
        text.Contains(Forbidden, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void THE_QUESTION_IS_FORGET_NOT_DELETE()
    {
        var q = Ask().Question;

        Assert.Equal("Forget Q6_K from gemma-4-e4b-it?", q);
        //the forbidden word may appear nowhere on this screen.
        Assert.False(SaysDelete(q), $"the question says a word this screen must not say: {q}");
    }

    //forgetting the entry never touches the file, both answers keep the bytes
    [Fact]
    public void THE_BODY_SAYS_THE_FILE_STAYS_ON_DISK_EITHER_WAY()
    {
        var body = Body();

        Assert.Contains("stays on disk either way", body, StringComparison.Ordinal);
        Assert.False(SaysDelete(body), $"the body says a word this screen must not say: {body}");
    }

    //gatto can't know which file the process loaded, so no sentence may name one
    [Fact]
    public void AND_IT_DOES_NOT_CLAIM_WHICH_FILE_THE_SERVER_LOADED()
    {
        var body = string.Join("\n", Ask().BodyRows!.Select(r => r.Text));

        Assert.Contains("whatever it loaded", body, StringComparison.Ordinal);
        //the quant goes in the options, and a body sentence about the running server must not name it
        Assert.DoesNotContain("running on Q6_K", body, StringComparison.Ordinal);
    }

    //each answer names its concrete outcome, and the no side names what is kept (the swap and the switch follow the same rule)
    [Fact]
    public void BOTH_ANSWERS_NAME_WHAT_THEY_DO()
    {
        var options = Ask().Options!.Select(o => o.Label).ToList();

        Assert.Contains(options, o => o.Contains("stops listing Q6_K", StringComparison.Ordinal));
        Assert.Contains(options, o => o.Contains("keep Q6_K on the list", StringComparison.Ordinal));
    }

    //an absence proves nothing until the matcher fires. add the word in both spellings to the real screen text (the capital case proves the fold)
    [Theory]
    [InlineData("delete")]
    [InlineData("Delete")]
    public void AND_THE_MATCHER_CAN_SEE_THAT_WORD_IN_EITHER_SPELLING(string spelling)
    {
        var planted = Body() + $"\nSaying yes will {spelling} the file from disk.";

        Assert.True(SaysDelete(planted), $"the matcher cannot see the word it forbids: {spelling}");
    }
}
