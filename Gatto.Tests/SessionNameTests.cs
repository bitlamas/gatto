using System.Text.Json;
using Gatto.Core;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Session;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Terminal;
using Gatto.Terminal.Tests.Fakes;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

//a session's name: set by /name, carried by the identity through every seam, shown on the composer and the tab, and never in the prefix
[Collection("e2e")]
public sealed class SessionNameTests : IDisposable
{
    private const string Name = "Project Puropy Birudo!";
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-name-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-name-cwd-").FullName;
    private readonly TextReader _origIn = Console.In;
    private readonly TextWriter _origOut = Console.Out;

    public void Dispose()
    {
        Console.SetIn(_origIn);
        Console.SetOut(_origOut);
        foreach (var d in new[] { _home, _cwd })
            try { Directory.Delete(d, recursive: true); } catch (Exception) { }
    }

    private static Conversation Convo() => new("you are a test");

    private static JsonElement SystemRecord(string path) =>
        File.ReadLines(path).Select(l => JsonDocument.Parse(l).RootElement)
            .First(e => e.TryGetProperty("role", out var r) && r.GetString() == "system");

    //records the deed instead of touching a real console
    private sealed class Deed : ITerminalTitle
    {
        public readonly List<string> Applied = [];
        public void Apply(string title) { lock (Applied) Applied.Add(title); }   //the ticker applies a frame from its own thread
        public void Restore() { }
    }

    //the command line

    [Theory]
    [InlineData(Name)]
    [InlineData("\"" + Name + "\"")]
    [InlineData("   \"" + Name + "\"  ")]
    public void BOTH_FORMS_GIVE_ONE_NAME(string arg) =>
        Assert.Equal((Name, null), Gatto.Repl.Repl.ParseName(arg));

    [Theory]
    [InlineData("a\tb")]
    [InlineData("a\u0007b")]
    [InlineData("\"\"")]
    public void A_CONTROL_CHARACTER_OR_NOTHING_REFUSES_WITH_THE_USAGE(string arg) =>
        Assert.Equal((null, Gatto.Repl.Repl.NameUsage), Gatto.Repl.Repl.ParseName(arg));

    //the file

    [Fact]
    public void THE_NAME_IS_WRITTEN_ON_THE_NEXT_SAVE_AND_AN_UNNAMED_RECORD_HAS_NO_FIELD()
    {
        var store = new SessionStore(_home, _cwd);
        store.Save(Convo());
        Assert.False(SystemRecord(store.CurrentPath!).TryGetProperty("session_name", out _));

        store.Name(Name);
        store.Save(Convo());

        Assert.Equal(Name, SystemRecord(store.CurrentPath!).GetProperty("session_name").GetString());
        Assert.Equal(Name, SessionStore.IdentityOf(store.CurrentPath!)!.Name);
    }

    [Fact]
    public void A_SUCCESSOR_AND_A_RESUME_CARRY_THE_NAME_AND_NEW_DROPS_IT()
    {
        var store = new SessionStore(_home, _cwd);
        store.Name(Name);
        store.Save(Convo());
        var origin = store.CurrentPath!;

        store.StartSuccessor();
        store.Save(Convo());
        Assert.NotEqual(origin, store.CurrentPath);
        Assert.Equal(Name, SessionStore.IdentityOf(store.CurrentPath!)!.Name);

        var resumed = new SessionStore(_home, _cwd);
        resumed.Adopt(SessionStore.IdentityOf(origin)!, origin);
        resumed.Save(Convo());
        Assert.Equal(Name, SessionStore.IdentityOf(resumed.CurrentPath!)!.Name);

        store.StartNew();
        Assert.Null(store.Identity.Name);
    }

    private sealed class SessionContext(string cwd, SessionIdentity? session) : IToolContext
    {
        public string Cwd => cwd;
        public string HomePath => cwd;
        public IUserPrompter? Prompter => null;
        public SessionIdentity? Session => session;
    }

    //read through a real PowerShell child, the subject being what a child process sees
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_SHELL_CHILD_READS_THE_NAME_ONLY_WHEN_THERE_IS_ONE(bool named)
    {
        var identity = new SessionStore(_home, _cwd).Identity with { Name = named ? Name : null };
        var args = JsonSerializer.SerializeToElement(new { command = "Write-Output \"[$env:GATTO_SESSION_NAME]\"" });

        var r = await new ShellTool().ExecuteAsync(args, new SessionContext(_cwd, identity), default);

        Assert.False(r.IsError, r.Text);
        Assert.Equal(named ? $"[{Name}]" : "[]", r.Text.Trim());
    }

    //resuming by name

    private string SaveSession(string cwd, string? name)
    {
        var store = new SessionStore(_home, cwd);
        if (name is not null) store.Name(name);
        store.Save(Convo());
        return store.CurrentPath!;
    }

    [Fact]
    public void A_NAME_RESOLVES_WHOLE_ACROSS_FOLDERS_AND_IN_ANY_CASE()
    {
        var other = Directory.CreateTempSubdirectory("gatto-name-other-").FullName;
        try
        {
            SaveSession(_cwd, "something else");
            var named = SaveSession(other, Name);

            Assert.Equal(named, SessionStore.ResolvePathById(_home, Name.ToUpperInvariant()));
            Assert.Throws<GattoConfigException>(() => SessionStore.ResolvePathById(_home, "Project Puropy"));
        }
        finally { Directory.Delete(other, recursive: true); }
    }

    [Fact]
    public void TWO_SESSIONS_WITH_ONE_NAME_REFUSE()
    {
        var first = SaveSession(_cwd, Name);
        //a session file is named by the clock's ticks, so the second save waits for the clock to pass the first one's
        var stamp = long.Parse(Path.GetFileNameWithoutExtension(first)[^19..]);
        Waits.Until(() => DateTime.UtcNow.Ticks > stamp, () => $"the clock still reads {DateTime.UtcNow.Ticks}, the first file {stamp}");
        SaveSession(_cwd, Name);

        var ex = Assert.Throws<GattoConfigException>(() => SessionStore.ResolvePathById(_home, Name));
        Assert.Contains("names 2 saved sessions", ex.Message, StringComparison.Ordinal);
    }

    //a compaction's chain is one session under one name, and a resume wants its newest file
    [Fact]
    public void A_SUCCESSOR_CHAIN_RESOLVES_TO_ITS_NEWEST_FILE()
    {
        var store = new SessionStore(_home, _cwd);
        store.Name(Name);
        store.Save(Convo());
        var first = store.CurrentPath!;
        store.StartSuccessor();
        store.Save(Convo());
        File.SetLastWriteTimeUtc(first, DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(store.CurrentPath, SessionStore.ResolvePathById(_home, Name));
    }

    //the exact stem first, then a name, then a unique prefix
    [Fact]
    public void THE_ORDER_IS_STEM_THEN_NAME_THEN_PREFIX()
    {
        var target = SaveSession(_cwd, null);
        var stem = Path.GetFileNameWithoutExtension(target);
        var prefix = stem[..14];
        var namedAfterPrefix = SaveSession(_cwd, prefix);
        var namedAfterStem = SaveSession(_cwd, stem);

        Assert.Equal(target, SessionStore.ResolvePathById(_home, stem));
        Assert.Equal(namedAfterPrefix, SessionStore.ResolvePathById(_home, prefix));
        Assert.NotEqual(namedAfterStem, SessionStore.ResolvePathById(_home, stem));
    }

    //the composer

    private static readonly Theme T = new(new TermCaps(true, true));

    [Fact]
    public void THE_TOP_RULE_LEADS_WITH_THE_GLYPH_AND_THE_NAME_AND_UNNAMED_IS_TODAYS()
    {
        Assert.EndsWith($" {TerminalTitle.Glyph} {Name} · coder · wild ──",
            InputFrame.BuildTopRuleVisible("coder", 100, T, wild: true, glyphs: GlyphSet.Unicode, name: Name), StringComparison.Ordinal);
        Assert.Equal(InputFrame.BuildTopRule("coder", 100, T, wild: true, glyphs: GlyphSet.Unicode),
            InputFrame.BuildTopRule("coder", 100, T, wild: true, glyphs: GlyphSet.Unicode, name: null));
        Assert.EndsWith(" gatto · coder · wild ──",
            InputFrame.BuildTopRuleVisible("coder", 100, T, wild: true, glyphs: GlyphSet.Unicode), StringComparison.Ordinal);
    }

    [Fact]
    public void A_WIDE_NAME_TRUNCATES_AND_NEVER_WRAPS()
    {
        var visible = InputFrame.BuildTopRuleVisible("coder", 30, T, glyphs: GlyphSet.Unicode, name: new string('n', 80));
        Assert.DoesNotContain('\n', visible);
        Assert.True(UnicodeWidth.Of(visible) <= 30, visible);
    }

    //the ASCII glyph set is the console that cannot draw the hieroglyphs, so the name stands alone there
    [Fact]
    public void THE_ASCII_SET_DRAWS_THE_NAME_WITHOUT_THE_GLYPH()
    {
        var visible = InputFrame.BuildTopRuleVisible("coder", 100, new Theme(TermCaps.Plain), glyphs: GlyphSet.Ascii, name: Name);
        Assert.DoesNotContain(TerminalTitle.Glyph, visible, StringComparison.Ordinal);
        Assert.Contains($" {Name} ", visible, StringComparison.Ordinal);
    }

    //the tab

    [Fact]
    public void A_NAME_HOLDS_THE_TAB_AND_GOING_BACK_PUTS_THE_UNNAMED_TITLE_BACK()
    {
        var deed = new Deed();
        var alt = new AltScreen(new RecordingSurface(), deed);
        alt.Enter(TerminalTitle.For("generalist"));

        alt.Name(TerminalTitle.Named(Name));
        alt.Retitle(TerminalTitle.For("coder"));
        alt.SetFinalTitle(TerminalTitle.Project(@"C:\dev\myproj"));
        alt.Name(null);

        Assert.Equal([TerminalTitle.For("generalist"), TerminalTitle.Named(Name), TerminalTitle.Project(@"C:\dev\myproj")], deed.Applied);
    }

    //through the real rich loop: the row, the composer and the tab after /name, a turn that sets no folder title, and /new giving the tab back
    [Fact]
    public async Task NAME_IN_THE_RICH_LOOP_SAYS_IT_SHOWS_IT_AND_NEW_REVERTS_THE_TAB()
    {
        var deed = new Deed();
        var h = new RichReplHarness(_cwd, title: deed);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("/name " + Name).Line("go").Line("/new").Line("/quit");

        await h.RunAsync();

        Assert.Equal([TerminalTitle.For("generalist"), TerminalTitle.Named(Name), TerminalTitle.Project(_cwd)], SettledTitles.Of(deed.Applied));
        Assert.DoesNotContain(Name, h.ScreenText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task THE_COMPOSER_SHOWS_THE_NAME_AND_NO_ROW_SAYS_IT()
    {
        var h = new RichReplHarness(_cwd);
        h.Keys.Line("/name \"" + Name + "\"").Line("/quit");

        await h.RunAsync();

        var screen = h.ScreenText();
        Assert.DoesNotContain("named " + Name, screen, StringComparison.Ordinal);
        Assert.Contains($"{TerminalTitle.Glyph} {Name}", screen, StringComparison.Ordinal);
    }

    //a resumed session opens with its name on the tab and the composer
    [Fact]
    public async Task A_RESUMED_NAMED_SESSION_OPENS_WITH_ITS_NAME()
    {
        var store = new SessionStore(_home, _cwd);
        store.Adopt(store.Identity with { Name = Name }, Path.Combine(_home, "sessions", "named.jsonl"));
        var deed = new Deed();
        var h = new RichReplHarness(_cwd, title: deed, sessions: store);
        h.Keys.Line("/quit");

        await h.RunAsync();

        Assert.Equal([TerminalTitle.For("generalist"), TerminalTitle.Named(Name)], deed.Applied);
        Assert.Contains($"{TerminalTitle.Glyph} {Name}", h.ScreenText(), StringComparison.Ordinal);
    }

    //the served line is about this launch alone: shown, never saved, so a resume shows its own line and no second one
    [Fact]
    public async Task THE_SERVED_LINE_IS_SHOWN_AND_NEVER_SAVED()
    {
        const string served = "using qwen3.5-9b, served on :1238";
        var store = new SessionStore(_home, _cwd);
        var h = new RichReplHarness(_cwd, sessions: store, servedNotice: served, launchWarnings: ["a row that is saved"]);
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("done"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("go").Line("/quit");

        await h.RunAsync();

        Assert.Contains(served, h.ScreenText(), StringComparison.Ordinal);
        var saved = File.ReadAllText(store.CurrentPath!);
        Assert.DoesNotContain(served, saved, StringComparison.Ordinal);
        Assert.Contains("a row that is saved", saved, StringComparison.Ordinal);
    }

    //the prefix

    private static Gatto.Repl.Repl PlainRepl(Conversation convo, SessionStore store)
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        return new Gatto.Repl.Repl(
            loop, convo, "generalist", "m", store, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), _ => new RoleSwitchResult(false, "", null, null, "unused"));
    }

    //the name reaches the model nowhere, so the system text and the baseline the request prefix is built from stay byte for byte
    [Fact]
    public async Task NAMING_MOVES_NO_BYTE_OF_THE_PREFIX()
    {
        var convo = new Conversation("you are a test");
        var system = convo.Messages[0].Content;
        var baseline = convo.Baseline;
        var store = new SessionStore(_home, _cwd);
        var repl = PlainRepl(convo, store);
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader("/name " + Name + "\n/name\n/quit\n"));

        await repl.RunAsync(CancellationToken.None);

        Assert.Equal(Name, repl.SessionNameForTest);
        Assert.Equal(Name, store.Identity.Name);
        Assert.Same(convo, repl.ConversationForTest);
        Assert.Equal(system, convo.Messages[0].Content);
        Assert.Equal(baseline, convo.Baseline);
        Assert.DoesNotContain(Name, string.Concat(convo.Messages.Select(m => m.Content)), StringComparison.Ordinal);
        Assert.DoesNotContain("named " + Name, sw.ToString(), StringComparison.Ordinal);
        Assert.Contains("name: " + Name, sw.ToString(), StringComparison.Ordinal);
    }

    //a name given before any turn would otherwise live only on the screen, and a resume by that name finds nothing
    [Fact]
    public async Task A_NAME_SET_BEFORE_ANY_TURN_IS_ON_DISK_AT_ONCE_AND_RESOLVES()
    {
        var store = new SessionStore(_home, _cwd);
        var repl = PlainRepl(Convo(), store);
        Console.SetOut(new StringWriter());
        Console.SetIn(new StringReader("/name " + Name + "\n/quit\n"));

        await repl.RunAsync(CancellationToken.None);

        var path = store.CurrentPath;
        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(Name, SessionStore.IdentityOf(path)!.Name);
        Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(SessionStore.ResolvePathById(_home, Name)));
    }
}
