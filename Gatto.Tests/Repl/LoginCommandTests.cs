using Gatto.Tests.Fakes;

namespace Gatto.Tests.Repl;

//login and logout on the rich face, each one line after whatever rows the handshake said
[Collection("e2e")]
public class LoginCommandTests
{
    [Fact]
    public async Task LOGIN_RUNS_THE_HANDSHAKE_AND_LOGOUT_SAYS_ITS_LINE()
    {
        var logins = 0;
        var h = new RichReplHarness(Path.GetTempPath(),
            loginNow: _ => { logins++; return Task.FromResult("someone@example.test"); },
            logoutNow: () => "logged out of hosted");
        h.Keys.Line("/login").Line("/logout").Line("/quit");
        await h.RunAsync();

        Assert.Equal(1, logins);
        Assert.True(h.Saw("logged in as someone@example.test"), h.Written());
        Assert.True(h.Saw("logged out of hosted"), h.Written());
        Assert.Empty(h.Client.Requests);
    }

    [Fact]
    public async Task A_LOGIN_THAT_FAILS_SAYS_ITS_MESSAGE()
    {
        var h = new RichReplHarness(Path.GetTempPath(),
            loginNow: _ => throw new InvalidOperationException("the device code expired"));
        h.Keys.Line("/login").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw("the device code expired"), h.Written());
    }

    [Fact]
    public async Task LOGIN_AND_LOGOUT_WITHOUT_THE_SEAM_ARE_ONE_SENTENCE_EACH()
    {
        var h = new RichReplHarness(Path.GetTempPath());
        h.Keys.Line("/login").Line("/logout").Line("/quit");
        await h.RunAsync();

        Assert.True(h.Saw(Gatto.Repl.Repl.LocalLogin), h.Written());
        Assert.Empty(h.Client.Requests);
    }
}
