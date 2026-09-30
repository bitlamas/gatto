using Gatto.Cli;

namespace Gatto.Tests.Cli;

//a fatal message must stay readable when the process owns the console. a test covering only the wait passes a build that waits always and hangs every script
public class FatalPauseTests
{
    private sealed record Run(int Exit, IReadOnlyList<string> Said, int Waits);

    private static Run Hold(int exit, int clients)
    {
        var said = new List<string>();
        var waits = 0;
        var code = FatalPause.Hold(exit, () => clients, said.Add, () => waits++);
        return new Run(code, said, waits);
    }

    //when gatto is the console's only client the window dies with the process, so a fatal message must be held.
    [Fact]
    public void A_FATAL_EXIT_WAITS_WHEN_THIS_PROCESS_OWNS_THE_CONSOLE()
    {
        var run = Hold(exit: 1, clients: 1);

        Assert.Equal(1, run.Waits);
        Assert.Equal([FatalPause.CloseLine], run.Said);
    }

    //a shell launch must never wait. two clients means a shell holds the window, and waiting hangs a scripted run until someone kills it.
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(0)]   //a count that could not be read fails open, toward not waiting.
    public void A_SHELL_LAUNCH_NEVER_WAITS(int clients)
    {
        var run = Hold(exit: 1, clients);

        Assert.Equal(0, run.Waits);
        Assert.Empty(run.Said);
    }

    //a clean exit must never wait, even from Explorer. holding the window would end every successful run with a keypress.
    [Fact]
    public void A_CLEAN_EXIT_NEVER_WAITS()
    {
        var run = Hold(exit: 0, clients: 1);

        Assert.Equal(0, run.Waits);
        Assert.Empty(run.Said);
    }

    //the pause must return the exit code unchanged. swallowing or rewriting it would break every script that reads the exit code.
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(1, 4)]
    [InlineData(2, 0)]
    public void THE_EXIT_CODE_IS_HANDED_BACK_UNCHANGED(int exit, int clients)
    {
        Assert.Equal(exit, Hold(exit, clients).Exit);
    }

    //the entry point must route its exit code through the pause. the internal overload the tests drive must stay unpaused, or the suite hangs
    [Fact]
    public void THE_PROCESS_ENTRY_POINT_ROUTES_ITS_EXIT_CODE_THROUGH_THE_PAUSE()
    {
        static bool Paused(string src) =>
            EntryBody(src) is { } body && body.Contains("FatalPause.Hold", StringComparison.Ordinal);

        Assert.True(Paused("public static async Task<int> RunAsync(string[] argv) => "
            + "FatalPause.Hold(await RunAsync(argv, null));"), "the matcher cannot see a wired entry");
        Assert.False(Paused("public static Task<int> RunAsync(string[] argv) => RunAsync(argv, null);"),
            "the matcher says an unwired entry is wired");

        var real = File.ReadAllText(Path.Combine(
            Gatto.Tests.Census.SourceTree.RepoRoot(), "Gatto", "Cli", "GattoApp.cs"));
        Assert.True(Paused(real), "the process entry point does not hold a fatal exit open");

        //the seam the tests drive must not pause, or the suite hangs on its own harness.
        Assert.DoesNotContain("FatalPause", InternalEntryBody(real), StringComparison.Ordinal);
    }

    //returns the arrow body of the one-argument overload, or null when the signature moved.
    private static string? EntryBody(string src) => Body(src, "public static async Task<int> RunAsync(string[] argv)")
        ?? Body(src, "public static Task<int> RunAsync(string[] argv)");

    private static string InternalEntryBody(string src)
    {
        var at = src.IndexOf("internal static async Task<int> RunAsync(string[] argv, CommandContext? ctx)",
            StringComparison.Ordinal);
        Assert.True(at >= 0, "the internal seam's signature moved");
        //the closing brace is matched through Environment.NewLine, since an escaped newline inside a literal can be rewritten into a real one and break parsing.
        var end = src.IndexOf(Environment.NewLine + "    }", at, StringComparison.Ordinal);
        return src[at..(end < 0 ? src.Length : end)];
    }

    private static string? Body(string src, string signature)
    {
        var at = src.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return null;
        var end = src.IndexOf(';', at);
        return end < 0 ? null : src[at..end];
    }

    //the console owner is asked only when the exit code says failure. a clean exit must not ask, or every healthy launch pays a P/Invoke.
    [Fact]
    public void A_CLEAN_EXIT_DOES_NOT_EVEN_ASK_WHO_OWNS_THE_CONSOLE()
    {
        var asked = 0;

        FatalPause.Hold(0, () => { asked++; return 1; }, _ => { }, () => { });

        Assert.Equal(0, asked);
    }
}
