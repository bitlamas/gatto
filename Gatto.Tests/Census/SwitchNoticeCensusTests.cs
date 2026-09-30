namespace Gatto.Tests.Census;

//the switch notice is decided in SwapConfirm.SwitchNotice and passed on by one expression. no behaviour test reaches it, so the census pins the call
public class SwitchNoticeCensusTests
{
    //the call site must not rebuild the sentence or re-test confirmed, or two homes decide. the test asserts the call and that its words are gone
    [Fact]
    public void THE_SUCCESSFUL_SWITCH_CARRIES_THE_NOTICE_FROM_SWAPCONFIRM()
    {
        var source = SourceTree.Read(Path.Combine(
            SourceTree.RepoRoot(), "Gatto", "Cli", "GattoApp.cs"));

        //anchor on the result-building line, so a later miss means the call went, and the file is the right one
        Assert.Contains("return new ModelSwitchResult(true, outcome.ModelId,", source,
            StringComparison.Ordinal);

        Assert.Contains("SwapConfirm.SwitchNotice(confirmed, outgoing?.Id, outcome.ModelId)", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("everything said so far is read again", source, StringComparison.Ordinal);
    }
}
