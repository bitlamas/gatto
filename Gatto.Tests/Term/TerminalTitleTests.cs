using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Terminal;
using Gatto.Tests.Fakes;
using Xunit;

namespace Gatto.Tests.Term;

//the tab names gatto while it runs, and leaving puts the old title back. the restore hangs on the alt screen, the one place every leave path passes through.
public sealed class TerminalTitleTests : IDisposable
{
    //the harness tests drive the real rich loop, which wants a real cwd to name
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-title-").FullName;
    public void Dispose() { try { Directory.Delete(_cwd, recursive: true); } catch { } }

    //records the deed instead of touching a real console
    private sealed class Deed : ITerminalTitle
    {
        public readonly List<string> Applied = [];
        public int Restores;
        public void Apply(string title) => Applied.Add(title);
        public void Restore() => Restores++;
    }

    //the title composition

    [Fact]
    public void THE_DEFAULT_ROLE_IS_JUST_GATTO()
        => Assert.Equal("gatto " + TerminalTitle.Glyph, TerminalTitle.For("generalist"));

    [Fact]
    public void ANY_OTHER_ROLE_IS_NAMED_AFTER_A_SEPARATOR()
    {
        Assert.Equal("gatto " + TerminalTitle.Glyph + " coder", TerminalTitle.For("coder"));
        Assert.Equal("gatto " + TerminalTitle.Glyph + " oracle", TerminalTitle.For("oracle"));
        //a custom role is named as the banner prints it
        Assert.Equal("gatto " + TerminalTitle.Glyph + " archivist", TerminalTitle.For("archivist"));
    }

    //an unnamed or blank role falls back to the bare word and glyph, no dangling separator
    [Fact]
    public void A_MISSING_ROLE_NAME_FALLS_BACK_TO_THE_BARE_WORD()
    {
        Assert.Equal("gatto " + TerminalTitle.Glyph, TerminalTitle.For(null));
        Assert.Equal("gatto " + TerminalTitle.Glyph, TerminalTitle.For(""));
        Assert.Equal("gatto " + TerminalTitle.Glyph, TerminalTitle.For("   "));
    }

    //the seam

    [Fact]
    public void ENTERING_TITLES_THE_TAB_ONCE_WITH_THE_COMPOSED_STRING()
    {
        var deed = new Deed();
        var alt = new AltScreen(new RecordingSurface(), deed);

        alt.Enter(TerminalTitle.For("coder"));
        alt.Enter(TerminalTitle.For("coder"));   //idempotent, like the buffer switch itself

        Assert.Equal(TerminalTitle.For("coder"), Assert.Single(deed.Applied));
    }

    //leaving puts the old title back, and the teardown's finally and the exit hooks both call AltScreen.Restore
    [Fact]
    public void LEAVING_PUTS_THE_OLD_TITLE_BACK_ONCE()
    {
        var deed = new Deed();
        var alt = new AltScreen(new RecordingSurface(), deed);

        alt.Enter(TerminalTitle.For("generalist"));
        alt.Restore();
        alt.Restore();   //the exit hook racing the teardown's finally is normal here

        Assert.Equal(1, deed.Restores);
    }

    //a session that never entered must not restore a title it never took (the row above proves the restore exists)
    [Fact]
    public void A_SESSION_THAT_NEVER_ENTERED_RESTORES_NOTHING()
    {
        var deed = new Deed();
        new AltScreen(new RecordingSurface(), deed).Restore();

        Assert.Empty(deed.Applied);
        Assert.Equal(0, deed.Restores);
    }

    //the tab follows a mid-session /role
    [Fact]
    public void RETITLING_MID_SESSION_SETS_THE_NEW_ROLE()
    {
        var deed = new Deed();
        var alt = new AltScreen(new RecordingSurface(), deed);

        alt.Enter(TerminalTitle.For("generalist"));
        alt.Retitle(TerminalTitle.For("coder"));

        Assert.Equal([TerminalTitle.For("generalist"), TerminalTitle.For("coder")], deed.Applied);
    }

    //outside the alt screen a /role must not title a tab nothing will restore
    [Fact]
    public void RETITLING_WITHOUT_HAVING_ENTERED_DOES_NOTHING()
    {
        var deed = new Deed();
        new AltScreen(new RecordingSurface(), deed).Retitle(TerminalTitle.For("coder"));

        Assert.Empty(deed.Applied);
    }

    //the folder title, its composition and its latch

    [Fact]
    public void THE_PROJECT_TITLE_IS_GLYPH_FIRST_AND_NAMES_THE_FOLDER()
        => Assert.Equal(TerminalTitle.Glyph + " myproj", TerminalTitle.Project(@"C:\dev\myproj"));

    //a trailing separator names the same folder, so the cwd's spelling never changes the tab
    [Fact]
    public void A_TRAILING_SEPARATOR_STILL_NAMES_THE_FOLDER()
        => Assert.Equal(TerminalTitle.Project(@"C:\dev\myproj"), TerminalTitle.Project(@"C:\dev\myproj\"));

    //at a drive or UNC root the framework gives no file name, so the title falls back to the trimmed root
    [Theory]
    [InlineData(@"C:\", "C:")]
    [InlineData(@"\\srv\share", @"\\srv\share")]
    public void A_ROOT_NAMES_ITSELF_RATHER_THAN_NOTHING(string cwd, string expected)
        => Assert.Equal(TerminalTitle.Glyph + " " + expected, TerminalTitle.Project(cwd));

    [Fact]
    public void THE_FINAL_TITLE_OUTLIVES_EVERY_LATER_RETITLE()
    {
        var deed = new Deed();
        var alt = new AltScreen(new RecordingSurface(), deed);

        alt.Enter(TerminalTitle.For("generalist"));
        alt.SetFinalTitle(TerminalTitle.Project(@"C:\dev\myproj"));
        alt.Retitle(TerminalTitle.For("coder"));   //the folder title survives a later /role

        Assert.Equal([TerminalTitle.For("generalist"), TerminalTitle.Project(@"C:\dev\myproj")], deed.Applied);
    }

    //a -p run builds no Repl and never enters the alt screen, so SetFinalTitle does nothing there
    [Fact]
    public void A_SESSION_THAT_NEVER_ENTERED_GETS_NO_FINAL_TITLE()
    {
        var deed = new Deed();
        new AltScreen(new RecordingSurface(), deed).SetFinalTitle(TerminalTitle.Project(@"C:\dev\myproj"));

        Assert.Empty(deed.Applied);
    }

    //the moment, through the real rich loop

    //samples the deed from inside a live turn, the only place the launch title can be seen
    private sealed class MidTurnTitleProbe(Deed deed, TurnAbortHandle? abort) : ITool
    {
        public readonly List<string> MidTurnTitles = [];
        public bool Ran;
        public string Name => "probe";
        public string Description => "records the applied window titles from inside a turn";
        public JsonElement ParametersSchema => JsonDocument.Parse("""{"type":"object"}""").RootElement;
        public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
        {
            MidTurnTitles.AddRange(deed.Applied);
            Ran = true;
            if (abort is not null) abort.RequestAbort();
            return Task.FromResult(new ToolResult("probed"));
        }
    }

    [Fact]
    public async Task THE_TITLE_BECOMES_THE_PROJECT_FOLDER_AFTER_THE_FIRST_TURN()
    {
        var deed = new Deed();
        var probe = new MidTurnTitleProbe(deed, abort: null);
        var reg = new ToolRegistry();
        reg.Register(probe);
        var h = new RichReplHarness(_cwd, tools: reg, title: deed);
        h.Client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "probe", "{}")),
            new StreamEvent.Finished("tool_calls", null));
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("first done"), new StreamEvent.Finished("stop", null));
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("second done"), new StreamEvent.Finished("stop", null));
        h.Keys.Line("go").Line("again").Line("/quit");

        await h.RunAsync();

        Assert.True(probe.Ran, "the scripted turn never reached the tool - the harness script did not dispatch");
        var launch = TerminalTitle.For("generalist");
        //mid-turn the only title on is the launch one, so a retitle at launch fails this row
        Assert.Equal([launch], probe.MidTurnTitles);
        Assert.Equal([launch, TerminalTitle.Project(_cwd)], deed.Applied);   //one retitle, and a second turn does not repeat it
    }

    [Fact]
    public async Task A_CANCELLED_TURN_DOES_NOT_NAME_THE_FOLDER()
    {
        var deed = new Deed();
        var handle = new TurnAbortHandle();
        var probe = new MidTurnTitleProbe(deed, handle);
        var reg = new ToolRegistry();
        reg.Register(probe);
        var h = new RichReplHarness(_cwd, tools: reg, turnAbort: handle, title: deed);
        h.Client.EnqueueTurn(
            new StreamEvent.ToolCallReady(new ToolCall("c1", "probe", "{}")),
            new StreamEvent.Finished("tool_calls", null));
        h.Client.EnqueueTurn(new StreamEvent.TextDelta("never accepted"), new StreamEvent.Finished("stop", null));   //the loop dequeues it, but the abort ends the turn first
        h.Keys.Line("go").Line("/quit");

        await h.RunAsync();

        Assert.Contains("turn cancelled", h.ScreenText(), StringComparison.Ordinal);
        Assert.Equal([TerminalTitle.For("generalist")], deed.Applied);   //a cancelled turn sets no folder title
    }

    //when there is no tab to title

    //redirected output has no tab, so no deed is made (the decision is a pure function so a test can assert it)
    [Fact]
    public void A_REDIRECTED_STREAM_GETS_NO_DEED()
    {
        Assert.Null(TerminalTitle.Deed(outputRedirected: true));
        Assert.NotNull(TerminalTitle.Deed(outputRedirected: false));
    }

    //the glyph is pinned by code point, an astral pair is not proofreadable and a pipe or an editor can swap it silently
    [Fact]
    public void THE_GLYPH_IS_THE_TWO_CODEPOINTS_THE_EVIDENCE_WAS_GATHERED_FOR()
    {
        var cps = new List<int>();
        for (var i = 0; i < TerminalTitle.Glyph.Length; i += char.IsSurrogatePair(TerminalTitle.Glyph, i) ? 2 : 1)
            cps.Add(char.ConvertToUtf32(TerminalTitle.Glyph, i));

        Assert.Equal(new[] { 0x133F2, 0x133A8 }, cps);
    }

    //both halves are astral, so a substitute that merely looked right would pass the length check and fail here
    [Fact]
    public void BOTH_HALVES_OF_THE_GLYPH_ARE_ASTRAL()
    {
        Assert.Equal(4, TerminalTitle.Glyph.Length);          //four chars, two surrogate pairs
        Assert.True(char.IsSurrogatePair(TerminalTitle.Glyph, 0));
        Assert.True(char.IsSurrogatePair(TerminalTitle.Glyph, 2));
    }
}
