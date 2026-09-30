//the slash command table lives in SlashCommands.All, and the canonical list here is what the name-set test below pins it to
using System.Linq;
using System.Text.RegularExpressions;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

[Collection("e2e")]
public class SlashCommandsTests
{
    //the name-set test pins SlashCommands.All to this list and can't see the Repl dispatch, so a branch with no row here goes unnoticed
    private static readonly string[] CanonicalNames =
        ["/help", "/new", "/role", "/model", "/effort", "/compact", "/auto", "/remember",
         "/wild", "/permissions", "/tools", "/init", "/quit"];

    [Fact]
    public void All_NameSet_MatchesCanonicalPinnedSet()
    {
        var actual = SlashCommands.All.Select(c => c.Name).ToHashSet();
        Assert.Equal(CanonicalNames.ToHashSet(), actual);
    }

    [Fact]
    public void All_NamesAreUnique()
    {
        var names = SlashCommands.All.Select(c => c.Name).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void All_EverySummaryIsNonEmpty()
    {
        Assert.All(SlashCommands.All, c => Assert.False(string.IsNullOrWhiteSpace(c.Summary)));
    }

    //the plain loop: dotnet test redirects stdout so TermCaps.Detect() always reports Plain, and these tests drive the real Repl.RunAsync to /help dispatch

    [Fact]
    public async Task PlainLoop_Help_PrintsEveryRow()
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var convo = new Conversation("sys");
        var repl = new Gatto.Repl.Repl(
            loop, convo, "generalist", "m", null, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"));

        var origIn = Console.In;
        var origOut = Console.Out;
        var sw = new StringWriter();
        try
        {
            Console.SetOut(sw);
            Console.SetIn(new StringReader("/help\n/quit\n"));
            await repl.RunAsync(CancellationToken.None);
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetIn(origIn);
        }

        var output = sw.ToString();
        Assert.All(SlashCommands.All, c => Assert.Contains(c.Name, output));
        Assert.All(SlashCommands.All, c => Assert.Contains(c.Summary, output));
    }

    [Fact]
    public void RenderPlain_IsByteWisePure_NoAnsiEscapes()
    {
        var s = SlashCommands.RenderPlain();
        var esc = s.IndexOf((char)0x1b);
        Assert.True(esc < 0, $"ESC found at {esc}: {string.Join(",", s.Select(c => (int)c))}");
    }

    [Fact]
    public void RenderPlain_AlignsSummaryColumnByCellWidth_AcrossAllRows()
    {
        var lines = SlashCommands.RenderPlain().Split('\n');
        Assert.Equal(SlashCommands.All.Count, lines.Length);

        var prefixLengths = lines.Zip(SlashCommands.All, (l, c) =>
        {
            var left = (c.Name + " " + c.Args).TrimEnd();
            Assert.StartsWith(left, l);
            var afterLeft = l[left.Length..];
            var padded = afterLeft.Length - afterLeft.TrimStart(' ').Length;
            Assert.Equal(c.Summary, afterLeft.TrimStart(' '));
            return left.Length + padded;
        }).ToList();

        Assert.Single(prefixLengths.Distinct());   //every row's summary starts at the same column
    }

    //the rich loop: RunRichAsync can't be driven in a test process, so these tests call the render body its /help branch calls

    [Fact]
    public void RenderRich_PrintsEveryRow_WithAccentColoredNameAndArgs()
    {
        var theme = new Theme(new TermCaps(Rich: true, TrueColor: true));

        var rendered = SlashCommands.RenderRich(theme);

        foreach (var c in SlashCommands.All)
        {
            var left = (c.Name + " " + c.Args).TrimEnd();
            Assert.Contains(theme.Paint(left, Theme.Accent), rendered);
            Assert.Contains(c.Summary, rendered);
        }
    }

    [Fact]
    public void RenderRich_StrippedOfAnsi_MatchesRenderPlain_SameCellAlignment()
    {
        var theme = new Theme(new TermCaps(Rich: true, TrueColor: true));

        var stripped = Regex.Replace(SlashCommands.RenderRich(theme), "\x1b\\[[0-9;]*m", "");

        Assert.Equal(SlashCommands.RenderPlain(), stripped);
    }
}
