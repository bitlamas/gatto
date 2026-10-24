using Gatto.Cli.Setup;

namespace Gatto.Tests.Fakes;

//the wizard reads a file on a watched screen before its model is written, and a face answers that screen when the poll says the read is done
internal static class FlowReads
{
    public static SetupScreen Settle(this SetupFlow flow, SetupScreen screen)
    {
        while (screen is SetupScreen.Choice { Key: SetupFlow.ReadingKey })
        {
            Assert.True(SpinWait.SpinUntil(flow.PollWatch, TimeSpan.FromSeconds(10)), "the read never finished");
            screen = flow.Answer(SetupFlow.Landed);
        }
        return screen;
    }

    //what a scripted face answers on the read's screen once the poll resolves, null on every other screen so the script answers those
    public static string? ThroughRead(SetupScreen.Choice c, Func<bool>? watch)
    {
        if (c.Key != SetupFlow.ReadingKey) return null;
        Assert.True(watch is not null && SpinWait.SpinUntil(watch, TimeSpan.FromSeconds(10)), "the read never finished");
        return SetupFlow.Landed;
    }

    //an answer that passes through any read it starts, as the face does, for tests that are about what comes after
    public static SetupScreen AnswerSettled(this SetupFlow flow, string key) => flow.Settle(flow.Answer(key));
}
