using Gatto.Core.Client;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//a login inside a turn waits on the user in the browser, so the ticker shows its waiting row and not the prefill row
[Collection("e2e")]
public class LoginWaitTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-login-wait-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (Exception) { }
    }

    [Fact]
    public async Task A_login_inside_a_turn_shows_the_waiting_row_and_the_turn_answers_after_it()
    {
        var h = new RichReplHarness(_dir);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("answered"), new StreamEvent.Finished("stop", null, null));
        var waiting = false;
        var readingWhileWaiting = true;
        h.Client.HoldNextTurn = async ct =>
        {
            h.LoginWait(true);
            waiting = await h.UntilAsync(() => h.ScreenText().Contains("waiting for your input", StringComparison.Ordinal), Waits.Bound);
            readingWhileWaiting = h.ScreenText().Contains("reading context", StringComparison.Ordinal);
            h.LoginWait(false);
        };
        h.Keys.Line("hello");

        await h.RunUntilAsync(() => h.Saw("answered"));

        Assert.True(waiting, "the waiting row never showed while the login held the turn");
        Assert.False(readingWhileWaiting, "the prefill row showed beside the waiting row");
    }
}
