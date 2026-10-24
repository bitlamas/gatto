using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests;

//a refused command clears the composer and does not restore it, or enter would resubmit hidden text
[Collection("e2e")]
public class RefusalClearsTheComposerTests
{
    //the run goes on this long after the refusal, so a restore that comes late still lands inside it
    private static readonly System.TimeSpan BadWindow = System.TimeSpan.FromMilliseconds(200);

    private static int Count(string haystack, string needle)
    {
        var c = 0;
        for (var i = haystack.IndexOf(needle, System.StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, System.StringComparison.Ordinal)) c++;
        return c;
    }

    [Fact]
    public async Task The_composer_is_empty_after_a_refusal()
    {
        var h = new RichReplHarness(System.IO.Path.GetTempPath());
        h.Keys.Line("/hepl");

        await h.RunForAsync(() => h.Saw("/hepl is not a command"), BadWindow);

        var screen = h.ScreenText();
        Assert.False(screen.Contains("❯ /hepl", System.StringComparison.Ordinal),
            "the refused text is back in the composer; the ruling is that it clears. Frame:\n" + screen);
    }

    //a bare enter on an empty-looking composer must send nothing, otherwise a second refusal would mean the editor still held text
    [Fact]
    public async Task A_bare_enter_after_a_refusal_submits_nothing()
    {
        var h = new RichReplHarness(System.IO.Path.GetTempPath());
        h.Keys.Line("/hepl");
        h.Keys.Line("");   //an empty line replays the user pressing enter again with nothing typed.

        await h.RunForAsync(() => h.Saw("/hepl is not a command"), BadWindow);

        var refusals = Count(h.ScreenText(), "/hepl is not a command");
        Assert.True(refusals <= 1,
            $"the refusal fired {refusals} times: a bare Enter resubmitted a composer that looked empty");
        Assert.Empty(h.Client.Requests);
    }

    //the clear rule covers mistyped commands only, a refused message still returns to the composer, otherwise the user loses a paragraph
    [Fact]
    public void The_ruling_is_scoped_to_commands_and_the_attach_refusal_still_restores()
    {
        var source = System.IO.File.ReadAllText(System.IO.Path.Combine(RepoRoot(), "Gatto", "Repl", "Repl.Linear.cs"))
            .Replace("\r\n", "\n");

        //the attach refusal still restores the composer text and the command refusal does not
        Assert.Contains("foreach (var line in attach.RefusalLines!) s.Renderer.CommitSystem(line);\n                _restoreComposer?.Invoke(input);",
            source, System.StringComparison.Ordinal);
        Assert.DoesNotContain("s.Renderer.CommitSystem(gate.Refusal!);\n            _restoreComposer",
            source, System.StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "Gatto.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
