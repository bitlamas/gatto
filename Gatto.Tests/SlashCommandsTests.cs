//the slash command table lives in SlashCommands.All, and the canonical list here is what the name-set test below pins it to
using System.Linq;
using System.Text.RegularExpressions;
using Gatto.Core.Client;
using Gatto.Core.Commands;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Commands;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

[Collection("e2e")]
public class SlashCommandsTests
{
    //the name-set test pins SlashCommands.All to this list and can't see the Repl dispatch, so a branch with no row here goes unnoticed
    private static readonly string[] CanonicalNames =
        ["/help", "/new", "/role", "/name", "/model", "/effort", "/compact", "/remember",
         "/wild", "/safe", "/sandbox", "/permissions", "/tools", "/context", "/init", "/quit"];

    [Fact]
    public void THE_SAFE_ROW_FOLLOWS_WILD_IN_THE_PLAIN_RENDER()
    {
        var lines = SlashCommands.RenderPlain(SlashCommands.All).Split('\n');
        var safe = Array.FindIndex(lines, l => l.TrimStart().StartsWith("/safe ", StringComparison.Ordinal));
        Assert.True(safe > 0, string.Join("\n", lines));
        Assert.StartsWith("/wild ", lines[safe - 1].TrimStart(), StringComparison.Ordinal);
        Assert.EndsWith("no prompts (" + Gatto.Terminal.WildKeys.KeyName + " toggles)", lines[safe - 1].TrimEnd(), StringComparison.Ordinal);
        Assert.EndsWith("prompts back on", lines[safe].TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain("checkpoint", string.Join("\n", lines), StringComparison.Ordinal);
    }

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
            () => Composed.Text("sys"), _ => new RoleSwitchResult(false, "", null, null, "unused"));

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
    public async Task PLAIN_LOOP_DISPATCHES_A_REGISTERED_COMMAND_ONE_LINE_PER_WRITE_AND_LISTS_IT_IN_HELP()
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var convo = new Conversation("sys");
        var commands = new CommandRegistry();
        commands.Add(new CommandRow("/undo", "[n|path]", "list snapshots for this folder, or restore one",
            (arg, _) => Task.FromResult($"one {arg}\ntwo"), "undo.csx"));
        var repl = new Gatto.Repl.Repl(
            loop, convo, "generalist", "m", null, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), _ => new RoleSwitchResult(false, "", null, null, "unused"),
            commands: commands);

        var origIn = Console.In;
        var origOut = Console.Out;
        var sw = new StringWriter();
        try
        {
            Console.SetOut(sw);
            Console.SetIn(new StringReader("/undo 3\n/help\n/quit\n"));
            await repl.RunAsync(CancellationToken.None);
        }
        finally
        {
            Console.SetOut(origOut);
            Console.SetIn(origIn);
        }

        //the loop writes its > prompt before each read and redirected stdin is not echoed, so the first row of an answer shares the prompt's line
        var lines = sw.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Select(l => l.StartsWith("> ", StringComparison.Ordinal) ? l[2..] : l).ToList();
        Assert.Contains("one 3", lines);
        Assert.Contains("two", lines);
        Assert.Contains(lines, l => l.StartsWith("/undo [n|path]", StringComparison.Ordinal) && l.EndsWith("undo.csx", StringComparison.Ordinal));
        Assert.Empty(client.Requests);
    }

    [Fact]
    public void A_REGISTERED_ROW_RENDERS_AFTER_THE_BUILT_INS_WITH_ITS_FILE_DIM_AT_THE_END()
    {
        var rows = new List<SlashCommands.Cmd>(SlashCommands.All)
            { new("/undo", "[n|path]", "list snapshots for this folder, or restore one", Source: "undo.csx") };
        var plain = SlashCommands.RenderPlain(rows).Split('\n');
        Assert.EndsWith("list snapshots for this folder, or restore one  undo.csx", plain[^1]);
        Assert.StartsWith("/undo [n|path]", plain[^1]);
        var theme = new Theme(new TermCaps(Rich: true, TrueColor: true), ThemeMode.Dark);
        var rich = SlashCommands.RenderRich(theme, rows).Split('\n')[^1];
        Assert.Contains(theme.Paint("undo.csx", Theme.Dim), rich, StringComparison.Ordinal);
        Assert.Equal(TermText.StripAnsiForWidth(rich), plain[^1]);
    }

    [Fact]
    public void RenderPlain_IsByteWisePure_NoAnsiEscapes()
    {
        var s = SlashCommands.RenderPlain(Gatto.Repl.Commands.SlashCommands.All);
        var esc = s.IndexOf((char)0x1b);
        Assert.True(esc < 0, $"ESC found at {esc}: {string.Join(",", s.Select(c => (int)c))}");
    }

    [Fact]
    public void RenderPlain_AlignsSummaryColumnByCellWidth_AcrossAllRows()
    {
        var lines = SlashCommands.RenderPlain(Gatto.Repl.Commands.SlashCommands.All).Split('\n');
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

        var rendered = SlashCommands.RenderRich(theme, Gatto.Repl.Commands.SlashCommands.All);

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

        var stripped = Regex.Replace(SlashCommands.RenderRich(theme, Gatto.Repl.Commands.SlashCommands.All), "\x1b\\[[0-9;]*m", "");

        Assert.Equal(SlashCommands.RenderPlain(Gatto.Repl.Commands.SlashCommands.All), stripped);
    }
}
