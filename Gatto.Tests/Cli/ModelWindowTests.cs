using Gatto.Cli;

namespace Gatto.Tests.Cli;

//the window a request gets is the smaller of the profile and the server, and only a smaller server is said
public class ModelWindowTests
{
    [Theory]
    [InlineData(262144, 131072, 131072, "server reports 131072. Using it (the profile says 262144)")]
    [InlineData(262144, 262144, 262144, null)]
    [InlineData(131072, 262144, 131072, null)]
    [InlineData(262144, 0, 262144, null)]
    [InlineData(262144, -1, 262144, null)]
    [InlineData(262144, null, 262144, null)]
    public void THE_SMALLER_WINDOW_WINS_AND_ONLY_A_SMALLER_SERVER_IS_SAID(
        int declared, int? served, int budget, string? line)
    {
        var window = ModelWindow.Resolve(declared, served);

        Assert.Equal(budget, window.Budget);
        Assert.Equal(line, window.Line);
    }
}
