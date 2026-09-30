using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Term;

//a row that exactly fills the width must keep its last character, since the cursor stays pending there and the erase takes it
public class FullWidthRowTests
{
    private const string Url =
        "https://github.com/ggml-org/llama.cpp/releases/download/b10076/llama-b10076-bin-win-vulkan-x64.zip";

    private sealed class OneKey : Gatto.Terminal.IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => new('1', ConsoleKey.D1, false, false, false);
    }

    private static string Render(int width, params BodyRow[] body)
    {
        var surface = new RecordingSurface { Width = width };
        new SelectPrompt(surface, new Theme(new TermCaps(true, true)), new OneKey())
            .Show(new SelectSpec(
                TitleRows: [],
                Question: new PromptQuestion("gatto needs llama.cpp to run models"),
                Options: [new SelectOption("Downloaded it - let me point at it")],
                BodyRows: body));
        return TerminalReplay.ScreenAt(surface.Text, width);
    }

    //the collector takes every consecutive non-blank line from the one starting with http, so a wrong rule here invents a loss
    private static string UrlRows(string screen)
    {
        var lines = screen.Split('\n').Select(l => l.Trim()).ToList();
        var start = lines.FindIndex(l => l.StartsWith("http", StringComparison.Ordinal));
        if (start < 0) return "";
        var taken = new List<string>();
        for (var i = start; i < lines.Count && lines[i].Length > 0; i++) taken.Add(lines[i]);
        return string.Concat(taken);
    }

    [Fact]
    public void A_HARD_BROKEN_URL_KEEPS_EVERY_CHARACTER_AT_EVERY_WIDTH()
    {
        //the sweep matters, since the defect fires only where a row hits the width exactly and that position moves with every width
        var broken = new List<string>();
        for (var width = 40; width <= 120; width++)
        {
            var joined = UrlRows(Render(width, new BodyRow(Url, BodyRowKind.Plain)));
            if (joined != Url) broken.Add($"  width {width}: {joined}");
        }
        Assert.True(broken.Count == 0,
            "characters lost on a deferred-wrap terminal:\n" + string.Join("\n", broken.Take(10)));
    }

    [Fact]
    public void HIS_EXACT_SCREENSHOT_RENDERS_THE_V()
    {
        //at 87 the budget is 85, so row one has to end in the v and ulkan-x64.zip is its continuation
        var screen = Render(87, new BodyRow(Url, BodyRowKind.Plain));
        var rows = screen.Split('\n').Select(l => l.Trim()).ToList();

        Assert.Contains(rows, r => r.EndsWith("bin-win-v", StringComparison.Ordinal));
        Assert.Contains(rows, r => r == "ulkan-x64.zip");
        Assert.DoesNotContain(rows, r => r.EndsWith("bin-win-", StringComparison.Ordinal));
    }

    [Fact]
    public void THE_ERASE_STILL_WIPES_A_TALLER_BLOCK_AT_A_FINITE_WIDTH()
    {
        //the erase moved to the start of the line, so it must still wipe the taller block at a finite width
        var surface = new RecordingSurface { Width = 44 };
        new SelectPrompt(surface, new Theme(new TermCaps(true, true)), new OneKey())
            .Show(new SelectSpec(
                TitleRows: [],
                Question: new PromptQuestion("carry on?"),
                Options:
                [
                    new SelectOption("the first option, which is a long one"),
                    new SelectOption("the second option, also long"),
                    new SelectOption("the third option, likewise"),
                ],
                FooterHint: "Esc to leave",
                EchoOnCompletion: true));

        var screen = TerminalReplay.ScreenAt(surface.Text, 44);
        Assert.DoesNotContain("the second option", screen, StringComparison.Ordinal);
        Assert.DoesNotContain("Esc to leave", screen, StringComparison.Ordinal);
        Assert.Contains("the first option", screen, StringComparison.Ordinal);   //the echoed answer stays on screen
    }
}
