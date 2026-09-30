//the command bodies of /role and /init, driven as extracted statics with fake closures, no live console and no real user files
using System.Linq;
using Gatto.Core.Client;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;
using Gatto.Repl;
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

public class ReplSlashTests
{

    [Fact]
    public void ApplyRoleSwitch_Success_SwapsSystemMessage_AndReturnsConfirmationData()
    {
        var convo = new Conversation("old system prompt");
        Func<string, RoleSwitchResult> fakeSwitch = name =>
            name == "coder"
                ? new RoleSwitchResult(true, "coder", "qwen3.6-coder", "new coder system prompt", null)
                : throw new InvalidOperationException("unexpected role in test");

        var result = Gatto.Repl.Repl.ApplyRoleSwitch(convo, fakeSwitch, "coder");

        Assert.True(result.Success);
        Assert.Equal("coder", result.RoleName);
        Assert.Equal("qwen3.6-coder", result.ModelName);
        Assert.Equal("system", convo.Messages[0].Role);
        Assert.Equal("new coder system prompt", convo.Messages[0].Content);
    }

    [Fact]
    public void ApplyRoleSwitch_Success_StampsConversationGattoRole()
    {
        var convo = new Conversation("old system prompt");
        convo.SetGattoRole("generalist");
        Func<string, RoleSwitchResult> fakeSwitch = name =>
            new RoleSwitchResult(true, "coder", "qwen3.6-coder", "new coder system prompt", null);

        var result = Gatto.Repl.Repl.ApplyRoleSwitch(convo, fakeSwitch, "coder");

        Assert.True(result.Success);
        Assert.Equal("coder", convo.GattoRole);
        convo.AddAssistant("hi");
        Assert.Equal("coder", convo.Messages[^1].GattoRole);
    }

    [Fact]
    public void ApplyRoleSwitch_UnknownRole_ReturnsOneLineListing_SessionUnchanged()
    {
        var convo = new Conversation("old system prompt");
        Func<string, RoleSwitchResult> fakeSwitch = _ =>
            new RoleSwitchResult(false, "", null, null,
                "unknown role 'nosuch' — available roles: coder, generalist, oracle");

        var result = Gatto.Repl.Repl.ApplyRoleSwitch(convo, fakeSwitch, "nosuch");

        Assert.False(result.Success);
        Assert.Equal("unknown role 'nosuch' — available roles: coder, generalist, oracle", result.Message);
        //a failed switch must leave the system message untouched.
        Assert.Equal("old system prompt", convo.Messages[0].Content);
        Assert.Single(convo.Messages);
    }

    [Fact]
    public void ApplyRoleSwitch_ValidationFailure_ReturnsOneLineError_NoSystemSwap()
    {
        var convo = new Conversation("old system prompt");
        Func<string, RoleSwitchResult> fakeSwitch = _ =>
            new RoleSwitchResult(false, "", null, null,
                "role 'cloud-role' targets a cloud endpoint: \"model\" is required and \"model\" must be absent (got model='x', model=none)");

        var result = Gatto.Repl.Repl.ApplyRoleSwitch(convo, fakeSwitch, "cloud-role");

        Assert.False(result.Success);
        Assert.Contains("targets a cloud endpoint", result.Message);
        Assert.Equal("old system prompt", convo.Messages[0].Content);   //a refused switch swaps nothing.
        Assert.Single(convo.Messages);
    }

    [Fact]
    public void ApplyRoleSwitch_EmptyArg_UsageMessage_NeverCallsSwitchRole()
    {
        var convo = new Conversation("old system prompt");
        var called = false;
        Func<string, RoleSwitchResult> fakeSwitch = _ => { called = true; return new RoleSwitchResult(true, "x", "m", "s", null); };

        var result = Gatto.Repl.Repl.ApplyRoleSwitch(convo, fakeSwitch, "");

        Assert.False(result.Success);
        Assert.Equal("usage: /role <name>", result.Message);
        Assert.False(called);
        Assert.Equal("old system prompt", convo.Messages[0].Content);
    }

    [Fact]
    public async Task InitPrompt_DispatchedThroughTheNormalLoop_ReachesTheModelVerbatim()
    {
        var client = new FakeChatClient();
        client.EnqueueTurn(new StreamEvent.TextDelta("GATTO.md written"), new StreamEvent.Finished("stop", null));
        var reg = new Gatto.Core.Tools.ToolRegistry();
        var loop = new AgentLoop(client, reg, new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var convo = new Conversation("sys");

        await Gatto.Repl.Repl.RunTurnGuardedAsync(loop, convo, Gatto.Repl.InitPrompt.Text, new RecordingObserver(), null, default);

        //the compiled-in constant reaches the loop through the same path as a typed message.
        var sentMessages = client.Requests[0].Messages;
        Assert.Contains(sentMessages, m => m.Role == "user" && m.Content == Gatto.Repl.InitPrompt.Text);
        Assert.Contains(convo.Messages, m => m.Role == "user" && m.Content == Gatto.Repl.InitPrompt.Text);
    }

    [Fact]
    public void InitPrompt_InterviewsWithAskUserAndWritesGattoMd()
    {
        //the brief must name the file it writes and the tool it interviews with
        Assert.Contains("GATTO.md", Gatto.Repl.InitPrompt.Text);
        Assert.Contains("ask_user", Gatto.Repl.InitPrompt.Text);
    }

    [Fact]
    public void InitPrompt_NamesNoRoleTool()
    {
        //roles change on their own schedule, a role tool named here would go stale
        Assert.DoesNotContain("task_restate", Gatto.Repl.InitPrompt.Text);
    }
}

//drives the real plain loop over scripted stdin and mutates the process-wide console, so this class runs serialized
[Collection("e2e")]
public class ReplWildTests : IDisposable
{
    private readonly TextReader _origIn = Console.In;
    private readonly TextWriter _origOut = Console.Out;
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-wild-").FullName;

    public void Dispose()
    {
        Console.SetIn(_origIn);
        Console.SetOut(_origOut);
        try { Directory.Delete(_root, true); } catch { }
    }

    private string PermissionsPath => Path.Combine(_root, ".gatto", "permissions.json");

    //a real repl over a temp root with the app's persist closure, so writes go through the store the test hands in
    private (string output, WildState wild) RunWild(string stdin, WildState wild, PermissionStore store)
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(_root), "m");
        var convo = new Conversation("sys");
        var repl = new Gatto.Repl.Repl(
            loop, convo, "generalist", "m", null, client, null, null, _root,
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"),
            null, wild, on => store.SetWild(on, persist: true));

        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader(stdin));
        repl.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.SetOut(_origOut);
        return (sw.ToString(), wild);
    }

    [Fact]
    public void Wild_BareToggle_TurnsOn_PrintsKaomoji_DoesNotPersist()
    {
        var store = PermissionStore.Load(_root, out _);
        var wild = new WildState { On = store.Wild };

        var (output, w) = RunWild("/wild\n/quit\n", wild, store);

        Assert.Contains("wild mode on", output);
        //the spelling is pinned in GlyphSetTests, so read the member instead of writing a second literal
        Assert.Contains(Gatto.Terminal.GlyphSet.Unicode.Wild, output, StringComparison.Ordinal);
        Assert.True(w.On);                            //the loop flipped the state object the test handed in
        Assert.False(File.Exists(PermissionsPath));   //a bare toggle must not write the permissions file.
    }

    [Fact]
    public void Wild_ToggleTwice_TurnsBackOff()
    {
        var store = PermissionStore.Load(_root, out _);
        var wild = new WildState { On = store.Wild };

        var (output, w) = RunWild("/wild\n/wild\n/quit\n", wild, store);

        Assert.Contains("wild mode off", output);
        Assert.False(w.On);
    }

    [Fact]
    public void Wild_Always_TurnsOn_AndPersistsToProjectFile()
    {
        var store = PermissionStore.Load(_root, out _);
        var wild = new WildState { On = store.Wild };

        var (_, w) = RunWild("/wild always\n/quit\n", wild, store);

        Assert.True(w.On);
        Assert.Contains("\"wild\": true", File.ReadAllText(PermissionsPath));
    }

    [Fact]
    public void Wild_Never_TurnsOff_AndRemovesPersistence()
    {
        var store = PermissionStore.Load(_root, out _);
        store.SetWild(true, persist: true);           //seed persisted state, so /wild never has something to remove
        Assert.Contains("wild", File.ReadAllText(PermissionsPath));
        var wild = new WildState { On = store.Wild };

        var (_, w) = RunWild("/wild never\n/quit\n", wild, store);

        Assert.False(w.On);
        Assert.DoesNotContain("wild", File.ReadAllText(PermissionsPath));   //a false value drops the key from the file
    }

    [Fact]
    public void Wild_UnknownArg_PrintsUsage_LeavesStateUnchanged()
    {
        var store = PermissionStore.Load(_root, out _);
        var wild = new WildState { On = store.Wild };

        var (output, w) = RunWild("/wild sideways\n/quit\n", wild, store);

        Assert.Contains(
            "usage: /wild [always|never] — toggle wild mode; always/never also persists for this project",
            output);
        Assert.False(w.On);   //an unknown argument must leave the state off.
    }
}

//drives the real plain loop over scripted stdin, since the rich path cannot be driven and its wiring is checked by reading the source
[Collection("e2e")]
public class ReplPermissionsTests : IDisposable
{
    private readonly TextReader _origIn = Console.In;
    private readonly TextWriter _origOut = Console.Out;
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-perms-repl-").FullName;

    public void Dispose()
    {
        Console.SetIn(_origIn);
        Console.SetOut(_origOut);
        try { Directory.Delete(_root, true); } catch { }
    }

    private string RunPermissions(string stdin, PermissionStore? store)
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(_root), "m");
        var convo = new Conversation("sys");
        var repl = new Gatto.Repl.Repl(
            loop, convo, "generalist", "m", null, client, null, null, _root,
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"),
            permissions: store);

        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader(stdin));
        repl.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.SetOut(_origOut);
        return sw.ToString();
    }

    [Fact]
    public void Permissions_Bare_EmptyStore_FriendlyEmptyState()
    {
        var store = PermissionStore.Load(_root, out _);

        var output = RunPermissions("/permissions\n/quit\n", store);

        Assert.Contains("no standing grants", output);
        Assert.Contains("wild mode: off", output);
    }

    [Fact]
    public void Permissions_Bare_ListsAllGrants_NumberedContinuously_WithKindLabels()
    {
        var store = PermissionStore.Load(_root, out _);
        store.GrantShellPrefix("git status", persist: true);
        store.GrantWriteDir(@"C:\proj", persist: true);
        store.GrantTool("web_search", persist: true);

        var output = RunPermissions("/permissions\n/quit\n", store);

        Assert.Matches(@"1\.\s+shell", output);   //the columns are padded, so the match allows spaces
        Assert.Contains("git status", output);
        Assert.Matches(@"2\.\s+write", output);
        Assert.Contains(@"C:\proj", output);
        Assert.Matches(@"3\.\s+tool", output);
        Assert.Contains("web_search", output);
    }

    [Fact]
    public void Permissions_Bare_ShowsWildState_AndPointsAtWildAsTheOnlyWayToChangeIt()
    {
        var store = PermissionStore.Load(_root, out _);
        store.SetWild(true, persist: true);

        var output = RunPermissions("/permissions\n/quit\n", store);

        Assert.Contains("wild mode: on", output);
        Assert.Contains("/wild", output);
    }

    [Fact]
    public void Permissions_WildRow_UsesTheRuledWording_NamingBothScopesAndShiftTab()
    {
        //the row must lead with the session toggle and Shift+Tab, and end with the per-project persist option
        var store = PermissionStore.Load(_root, out _);

        var output = RunPermissions("/permissions\n/quit\n", store);

        Assert.Contains(
            "wild mode: off — /wild or Shift+Tab toggles it for this session; "
            + "/wild always|never persists it per project",
            output, StringComparison.Ordinal);
    }

    [Fact]
    public void Permissions_ListsTheRevokeInstruction_AndWhereGrantsLive()
    {
        var store = PermissionStore.Load(_root, out _);
        store.GrantShellPrefix("git status", persist: true);

        var output = RunPermissions("/permissions\n/quit\n", store);

        Assert.Contains(
            @"/permissions revoke <n> removes one — stored in .gatto\permissions.json",
            output, StringComparison.Ordinal);
    }

    [Fact]
    public void Permissions_EmptyStore_OmitsTheRevokeInstruction()
    {
        //with nothing to revoke the instruction is noise, so it must stay off the screen
        var store = PermissionStore.Load(_root, out _);

        var output = RunPermissions("/permissions\n/quit\n", store);

        Assert.DoesNotContain("revoke <n> removes one", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Permissions_Bare_CapsAtTenEntries_AndSaysHowManyMore()
    {
        var store = PermissionStore.Load(_root, out _);
        for (var i = 1; i <= 25; i++) store.GrantShellPrefix("cmd" + i.ToString("00"), persist: true);

        var output = RunPermissions("/permissions\n/quit\n", store);

        Assert.Contains("cmd10", output, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd11", output, StringComparison.Ordinal);
        Assert.Contains("… 15 more — /permissions all", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Permissions_All_ShowsEveryEntry_AndNoMoreLine()
    {
        var store = PermissionStore.Load(_root, out _);
        for (var i = 1; i <= 25; i++) store.GrantShellPrefix("cmd" + i.ToString("00"), persist: true);

        var output = RunPermissions("/permissions all\n/quit\n", store);

        Assert.Contains("cmd25", output, StringComparison.Ordinal);
        Assert.DoesNotContain("more — /permissions all", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Permissions_ExactlyAtTheCap_ShowsNoMoreLine()
    {
        //at exactly the cap nothing is hidden, so the more-line would lie.
        var store = PermissionStore.Load(_root, out _);
        for (var i = 1; i <= 10; i++) store.GrantShellPrefix("cmd" + i.ToString("00"), persist: true);

        var output = RunPermissions("/permissions\n/quit\n", store);

        Assert.Contains("cmd10", output, StringComparison.Ordinal);
        Assert.DoesNotContain("more — /permissions", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Permissions_CappedListing_StillNumbersAgainstTheFullSet()
    {
        //the numbers are revoke keys, so a capped listing must number against the full set
        var store = PermissionStore.Load(_root, out _);
        for (var i = 1; i <= 25; i++) store.GrantShellPrefix("cmd" + i.ToString("00"), persist: true);

        var capped = RunPermissions("/permissions\n/quit\n", store);
        var all = RunPermissions("/permissions all\n/quit\n", store);

        Assert.Contains("10. shell cmd10", capped.Replace("  ", " "), StringComparison.Ordinal);
        Assert.Contains("10. shell cmd10", all.Replace("  ", " "), StringComparison.Ordinal);
    }

    [Fact]
    public void Permissions_Revoke_RemovesTheNumberedEntry_PersistsAndConfirms()
    {
        var store = PermissionStore.Load(_root, out _);
        store.GrantShellPrefix("git status", persist: true);
        store.GrantWriteDir(@"C:\proj", persist: true);

        var output = RunPermissions("/permissions revoke 2\n/quit\n", store);

        Assert.Contains(@"C:\proj", output);              //the confirmation must name the entry it removed.
        Assert.False(store.AllowsWrite(@"C:\proj\a.txt"));
        Assert.True(store.AllowsShell("git status"));     //revoking one entry must leave the others alone.

        var reloaded = PermissionStore.Load(_root, out _);
        Assert.False(reloaded.AllowsWrite(@"C:\proj\a.txt"));
        Assert.True(reloaded.AllowsShell("git status"));
    }

    [Fact]
    public void Permissions_Revoke_OutOfRange_FriendlyError_NothingChanges()
    {
        var store = PermissionStore.Load(_root, out _);
        store.GrantShellPrefix("git status", persist: true);

        var output = RunPermissions("/permissions revoke 99\n/quit\n", store);

        Assert.Contains("no grant numbered 99", output);
        Assert.True(store.AllowsShell("git status"));   //a bad index revokes nothing.
    }

    [Fact]
    public void Permissions_Revoke_NonNumeric_FriendlyError_NothingChanges()
    {
        var store = PermissionStore.Load(_root, out _);
        store.GrantShellPrefix("git status", persist: true);

        var output = RunPermissions("/permissions revoke abc\n/quit\n", store);

        Assert.Contains("is not a number", output);
        Assert.True(store.AllowsShell("git status"));   //a non-numeric index revokes nothing.
    }

    [Fact]
    public void Permissions_Revoke_Overflow_IsDiagnosedAsOutOfRange_NotAsNonNumeric()
    {
        //a value past the int range parses like abc, so the out-of-range message is the honest one
        var store = PermissionStore.Load(_root, out _);
        store.GrantShellPrefix("git status", persist: true);

        var output = RunPermissions("/permissions revoke 2147483648\n/quit\n", store);

        Assert.DoesNotContain("is not a number", output);
        Assert.Contains("no grant numbered 2147483648", output);
        Assert.True(store.AllowsShell("git status"));   //an overflow index is a no-op like any bad index.
    }

    [Fact]
    public void Permissions_UnknownArg_PrintsUsage()
    {
        var store = PermissionStore.Load(_root, out _);

        var output = RunPermissions("/permissions sideways\n/quit\n", store);

        Assert.Contains(Gatto.Repl.Repl.PermissionsUsage, output);
    }

    [Fact]
    public void Permissions_Revoke_NeverTouchesWild()
    {
        var store = PermissionStore.Load(_root, out _);
        store.SetWild(true, persist: true);
        store.GrantShellPrefix("git status", persist: true);

        RunPermissions("/permissions revoke 1\n/quit\n", store);

        Assert.True(store.Wild);
        var reloaded = PermissionStore.Load(_root, out _);
        Assert.True(reloaded.Wild);
    }

    [Fact]
    public void Permissions_Bare_StoredGrantTextIsSanitized_NoEscapeBytesReachTheTerminal()
    {
        //grant text is model-authored, so the plain loop must sanitize it, or an embedded clear erases the listing
        var store = PermissionStore.Load(_root, out _);
        store.GrantShellPrefix("git \u001b[2Jstatus", persist: true);
        store.GrantWriteDir("C:\\pr\u001b[31moj", persist: true);
        store.GrantTool("web\u001b]8;;http://evil\u0007_search", persist: true);

        var output = RunPermissions("/permissions\n/quit\n", store);

        Assert.DoesNotContain('\u001b', output);
        //an escape payload must print as inert text, so a grant row never disappears
        Assert.Contains("git [2Jstatus", output);
        Assert.Contains("[31moj", output);
        Assert.Contains("_search", output);
        //each grant must stay its own row even though sanitize drops newlines.
        Assert.Matches(@"1\.\s+shell", output);   //the columns are padded, so the match allows spaces
        Assert.Matches(@"2\.\s+write", output);
        Assert.Matches(@"3\.\s+tool", output);
        Assert.Contains("wild mode: off", output);
    }

    [Fact]
    public void Permissions_NoStoreConstructed_ReportsUnavailable()
    {
        //older construction sites pass no permissions argument, so a null store answers with the unavailable line rather than throwing
        var output = RunPermissions("/permissions\n/quit\n", store: null);

        Assert.Contains(Gatto.Repl.Repl.PermissionsUnavailable, output);
    }
}

//one match-and-slice function serves every dispatch site, so identical parsing holds by construction
public class TryMatchCommandTests
{
    [Fact]
    public void BareCommand_ExactMatch_ReturnsTrue_EmptyArg()
    {
        Assert.True(Gatto.Repl.Repl.TryMatchCommand("/wild", "/wild", out var arg));
        Assert.Equal("", arg);
    }

    [Fact]
    public void CommandWithArgument_ReturnsTrue_TrimmedArg()
    {
        Assert.True(Gatto.Repl.Repl.TryMatchCommand("/wild always", "/wild", out var arg));
        Assert.Equal("always", arg);
    }

    [Fact]
    public void CommandWithExtraLeadingSpacesBeforeArgument_TrimsThemOff()
    {
        Assert.True(Gatto.Repl.Repl.TryMatchCommand("/wild   always", "/wild", out var arg));
        Assert.Equal("always", arg);
    }

    [Fact]
    public void CommandWithTrailingSpaceOnly_ReturnsTrue_EmptyArg()
    {
        Assert.True(Gatto.Repl.Repl.TryMatchCommand("/wild ", "/wild", out var arg));
        Assert.Equal("", arg);
    }

    [Fact]
    public void MultiWordArgument_IsPreservedAndTrimmed()
    {
        Assert.True(Gatto.Repl.Repl.TryMatchCommand("/permissions revoke 2", "/permissions", out var arg));
        Assert.Equal("revoke 2", arg);
    }

    [Fact]
    public void NameWithNoSpaceBoundary_DoesNotMatch_LongerCommandIsNotAPrefixHit()
    {
        //the character after the name must be a space, so /wildcard never matches /wild
        Assert.False(Gatto.Repl.Repl.TryMatchCommand("/wildcard", "/wild", out _));
    }

    [Fact]
    public void UnrelatedCommand_DoesNotMatch()
    {
        Assert.False(Gatto.Repl.Repl.TryMatchCommand("/model", "/wild", out _));
        Assert.False(Gatto.Repl.Repl.TryMatchCommand("/permissions", "/wild", out _));
    }

    [Fact]
    public void EmptyLine_DoesNotMatch()
    {
        Assert.False(Gatto.Repl.Repl.TryMatchCommand("", "/wild", out var arg));
        Assert.Equal("", arg);
    }

    [Fact]
    public void BothWildAndPermissions_ParseIdentically_ForTheSameShapeOfInput()
    {
        //both names go through one matcher, so no per-command special case can drift
        Assert.True(Gatto.Repl.Repl.TryMatchCommand("/wild never", "/wild", out var wildArg));
        Assert.True(Gatto.Repl.Repl.TryMatchCommand("/permissions revoke 1", "/permissions", out var permArg));
        Assert.Equal("never", wildArg);
        Assert.Equal("revoke 1", permArg);
    }
}

//the command rewrites the operator's line into a user turn, so only that rewrite seam is pinned here
[Collection("e2e")]
public class ReplRememberTests : IDisposable
{
    private readonly TextReader _origIn = Console.In;
    private readonly TextWriter _origOut = Console.Out;

    public void Dispose()
    {
        Console.SetIn(_origIn);
        Console.SetOut(_origOut);
    }

    private (string output, FakeChatClient client) RunRemember(string stdin)
    {
        var client = new FakeChatClient();
        //enqueued turns are consumed only on real dispatch, so an unused turn does not fail the usage case.
        client.EnqueueTurn(new StreamEvent.TextDelta("ok"), new StreamEvent.Finished("stop", null));
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var convo = new Conversation("sys");
        var repl = new Gatto.Repl.Repl(
            loop, convo, "generalist", "m", null, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"));

        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader(stdin));
        repl.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.SetOut(_origOut);
        return (sw.ToString(), client);
    }

    [Fact]
    public void Remember_WithText_DispatchesATurn_WrapperPlusVerbatimText()
    {
        var (_, client) = RunRemember("/remember what we just figured out about the slots poll\n/quit\n");

        Assert.Single(client.Requests);
        var sent = client.Requests[0].Messages.Single(m => m.Role == "user");
        Assert.Equal(Gatto.Repl.Repl.RememberPrompt("what we just figured out about the slots poll"), sent.Content);
    }

    [Fact]
    public void Remember_Bare_PrintsUsage_DispatchesNoTurn()
    {
        var (output, client) = RunRemember("/remember\n/quit\n");

        Assert.Contains(Gatto.Repl.Repl.RememberUsage, output);
        Assert.Empty(client.Requests);
    }

    [Fact]
    public void RememberWithOnlyWhitespaceArgument_TreatedAsBare_PrintsUsage_DispatchesNoTurn()
    {
        var (output, client) = RunRemember("/remember    \n/quit\n");

        Assert.Contains(Gatto.Repl.Repl.RememberUsage, output);
        Assert.Empty(client.Requests);
    }

    //an unknown slash word is refused rather than sent, and the empty request list shows nothing was banked
    [Fact]
    public void RememberFoo_IsNotTheRememberCommand_AndIsRefusedRatherThanSent()
    {
        var (output, client) = RunRemember("/rememberfoo\n/quit\n");

        Assert.Empty(client.Requests);
        Assert.Contains("/rememberfoo is not a command", output, StringComparison.Ordinal);
    }

    [Fact]
    public void RememberPrompt_NamesTheToolAndAsksForASlug_TextRidesVerbatim()
    {
        //the prompt asks for a slug (reusing one for a revision) and the operator's text rides verbatim at the end
        var prompt = Gatto.Repl.Repl.RememberPrompt("a raw <verbatim> string, untouched by the harness");

        Assert.Contains("memory_write", prompt, StringComparison.Ordinal);
        Assert.Contains("slug", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("topic", prompt, StringComparison.Ordinal);
        Assert.EndsWith("a raw <verbatim> string, untouched by the harness", prompt, StringComparison.Ordinal);
    }
}

//output is redirected, so the plain loop runs and the real /model dispatch is driven with a fake switchModel
[Collection("e2e")]
public class ReplModelSlashTests : IDisposable
{
    private readonly TextReader _origIn = Console.In;
    private readonly TextWriter _origOut = Console.Out;

    public void Dispose()
    {
        Console.SetIn(_origIn);
        Console.SetOut(_origOut);
    }

    private static Gatto.Repl.Repl ReplWith(
        string modelName = "qwen3.6-35b", Func<string, bool, ModelSwitchResult>? switchModel = null,
        Func<IReadOnlyList<PickerItem>>? listModels = null, IListPicker? picker = null,
        Conversation? convo = null,
        string? unmanagedNotice = null,
        Func<string, string?>? setDefault = null)
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        convo ??= new Conversation("sys");
        return new Gatto.Repl.Repl(
            loop, convo, "generalist", modelName, null, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"),
            switchModel: switchModel, listModels: listModels, picker: picker,
            unmanagedNotice: unmanagedNotice, setDefault: setDefault);
    }

    //the picker fakes ignore the title, since only the shape under test matters
    private sealed class FixedIndexPicker(int? index) : IListPicker
    {
        public PickOutcome Pick(string title, IReadOnlyList<PickerItem> items, string? markLegend = null,
            IReadOnlyList<char>? controlKeys = null, int? openAt = null) =>
            index is { } i ? new PickOutcome.Picked(i) : new PickOutcome.Cancelled();
    }
    private static IListPicker FakePickerChoosing(int? index) => new FixedIndexPicker(index);

    private sealed class CapturingPickerImpl(Func<IReadOnlyList<PickerItem>, int?> onPick) : IListPicker
    {
        public PickOutcome Pick(string title, IReadOnlyList<PickerItem> items, string? markLegend = null,
            IReadOnlyList<char>? controlKeys = null, int? openAt = null) =>
            onPick(items) is { } i ? new PickOutcome.Picked(i) : new PickOutcome.Cancelled();
    }

    //this fake records the legend, which render tests supply themselves, so a swap of it stays visible
    private sealed class LegendCapturingPicker(Action<string?, IReadOnlyList<PickerItem>> seen) : IListPicker
    {
        public PickOutcome Pick(string title, IReadOnlyList<PickerItem> items, string? markLegend = null,
            IReadOnlyList<char>? controlKeys = null, int? openAt = null)
        {
            seen(markLegend, items);
            return new PickOutcome.Cancelled();
        }
    }
    private static IListPicker CapturingPicker(Func<IReadOnlyList<PickerItem>, int?> onPick) => new CapturingPickerImpl(onPick);

    //each open is recorded separately, since the subject is what changed between two of them
    private sealed class ScriptedPicker(Queue<PickOutcome> answers) : IListPicker
    {
        public List<IReadOnlyList<PickerItem>> Opens { get; } = [];
        public List<int?> OpenedAt { get; } = [];
        public List<IReadOnlyList<char>?> Controls { get; } = [];

        public PickOutcome Pick(string title, IReadOnlyList<PickerItem> items, string? markLegend = null,
            IReadOnlyList<char>? controlKeys = null, int? openAt = null)
        {
            Opens.Add(items);
            OpenedAt.Add(openAt);
            Controls.Add(controlKeys);
            return answers.Count > 0 ? answers.Dequeue() : new PickOutcome.Cancelled();
        }
    }

    private static async Task<string> Feed(Gatto.Repl.Repl repl, string input)
    {
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader(input + "\n/quit\n"));
        await repl.RunAsync(CancellationToken.None);
        return sw.ToString();
    }

    [Fact]
    public async Task SlashModel_WithModelName_ArmsItAndReportsSuccess()
    {
        var repl = ReplWith(switchModel: (id, _) => new ModelSwitchResult(
            true, id, "new system text", 131072, null, null, null));

        var output = await Feed(repl, "/model gemma-4-26b");

        Assert.Contains("gemma-4-26b", output);
        Assert.Equal("gemma-4-26b", repl.ModelNameForTest);
    }

    [Fact]
    public async Task SlashModel_hands_the_conversation_the_baseline_of_the_new_text()
    {
        var convo = new Conversation("sys");
        var baseline = new SessionBaseline(5, "generalist", "gemma-4-26b", "all", new ThinkingMark("medium", null), new List<ToolMark>(),
            new BaselineSources(null, null, new List<ContextFileMark>(), new List<PolicyMark>(), "r", "m"));
        var repl = ReplWith(convo: convo, switchModel: (id, _) => new ModelSwitchResult(
            true, id, "new system text", 131072, null, null, null, Baseline: baseline));

        await Feed(repl, "/model gemma-4-26b");

        Assert.Equal(("new system text", 5), (convo.Messages[0].Content, convo.Baseline!.Seq));
    }

    //a completed switch with a message shows it and drops the model line, and only the plain site can be driven here
    [Fact]
    public async Task SlashModel_ALandedSwitchWithAMessage_SaysItInsteadOfTheModelLine()
    {
        var repl = ReplWith(switchModel: (id, _) => new ModelSwitchResult(
            true, id, "sys", 131072, "now on " + id + ", everything said so far is read again", null, null));

        var output = await Feed(repl, "/model gemma-4-26b");

        Assert.Contains("now on gemma-4-26b, everything said so far is read again", output);
        Assert.DoesNotContain("model: gemma-4-26b", output);
    }

    //without this negative, a build that never printed the model line would still pass
    [Fact]
    public async Task SlashModel_ALandedSwitchWithNoMessage_StillSaysTheModelLine()
    {
        var repl = ReplWith(switchModel: (id, _) => new ModelSwitchResult(
            true, id, "sys", 131072, null, null, null));

        Assert.Contains("model: gemma-4-26b", await Feed(repl, "/model gemma-4-26b"));
    }

    [Fact]
    public async Task SlashModel_Failure_ShowsMessageAndKeepsOldModel()
    {
        var repl = ReplWith(
            modelName: "qwen3.6-35b",
            switchModel: (id, _) => new ModelSwitchResult(false, id, null, null, "no model 'zzz'", null, null));

        var output = await Feed(repl, "/model zzz");

        Assert.Contains("no model 'zzz'", output);
        Assert.Equal("qwen3.6-35b", repl.ModelNameForTest);   //a failed switch must not change the model name.
    }

    [Fact]
    public async Task SlashModel_Mismatch_SurfacesTheServingModel()
    {
        var repl = ReplWith(switchModel: (id, _) => new ModelSwitchResult(
            true, id, "sys", 131072, null, "qwen3.6-35b", null));

        await Feed(repl, "/model gemma-4-26b");

        Assert.Equal("qwen3.6-35b", repl.ServingForTest);
    }

    [Fact]
    public async Task SlashModel_NoMismatch_ServingStaysNull()
    {
        var repl = ReplWith(switchModel: (id, _) => new ModelSwitchResult(
            true, id, "sys", 131072, null, null, null));

        await Feed(repl, "/model gemma-4-26b");

        Assert.Null(repl.ServingForTest);
    }

    [Fact]
    public async Task SlashModel_OverBudgetHistory_WarnsAtTheSwitch_AgainstTheProbedWindow()
    {
        //the switch check uses the server's real window when smaller, and the over-budget tokens sit in the history since a switch replaces the system text
        var convo = new Conversation("sys");
        convo.AddUser(new string('x', 400));   //400 chars is about 100 tokens, from the chars÷4 estimate
        var repl = ReplWith(convo: convo, switchModel: (id, _) => new ModelSwitchResult(
            true, id, "sys", 131072, null, null, ProbedNCtx: 50));

        var output = await Feed(repl, "/model gemma-4-26b");

        Assert.Contains("/compact", output);
        Assert.Contains("server's actual window", output);   //the warning must name the server's window rather than the declared size
    }

    [Fact]
    public async Task SlashModel_Bare_CallsThePickerStub_AndIsANoOpWhenItReturnsNull()
    {
        //a bare /model whose picker cancels must change nothing, say nothing and throw nothing
        var called = false;
        var repl = ReplWith(switchModel: (id, _) => { called = true; return new ModelSwitchResult(true, id, "s", 1, null, null, null); });

        var output = await Feed(repl, "/model");

        Assert.False(called);   //a cancelled picker never calls the switch closure
        Assert.Equal("qwen3.6-35b", repl.ModelNameForTest);
        Assert.DoesNotContain("model:", output);
    }

    [Fact]
    public async Task SlashModel_NoSwitchModelClosure_ReportsUnavailable_NeverThrows()
    {
        //older constructions pass no switch closure, so the command reports unavailable rather than throwing
        var repl = ReplWith(switchModel: null);

        var output = await Feed(repl, "/model gemma-4-26b");

        Assert.Contains(Gatto.Repl.Repl.ModelSwitchUnavailable, output);
        Assert.Equal("qwen3.6-35b", repl.ModelNameForTest);
    }

    //every other row reads AddMovedText, so this is the one place the wording literal is pinned
    [Fact]
    public void THE_MOVED_SENTENCE_IS_THE_RULED_ONE()
        => Assert.Equal(
            "adding a model needs its own terminal \u2014 run gatto model there, then /model to switch to it",
            Gatto.Repl.Repl.AddMovedText);

    [Theory]
    [InlineData("/model add")]
    [InlineData("/model new")]
    public async Task Both_add_verbs_answer_with_a_line_and_open_nothing(string typed)
    {
        //the add flow moved to gatto model, so both old add verbs must still answer with the moved line
        var switched = new List<string>();
        var repl = ReplWith(
            switchModel: (id, _) => { switched.Add(id); return new ModelSwitchResult(true, id, "s", 1, null, null, null); });

        var output = await Feed(repl, typed);

        Assert.Contains(Gatto.Repl.Repl.AddMovedText, output, StringComparison.Ordinal);
        //these words are verbs, so neither may reach the switch closure
        Assert.Empty(switched);
    }

    [Fact]
    public async Task The_model_picker_offers_no_add_row()
    {
        //the picker must offer no add row, since a row would read as a way back in
        IReadOnlyList<PickerItem> offered = [];
        var repl = ReplWith(
            listModels: () => [new PickerItem("gemma", "gemma", false, false),
                               new PickerItem("qwen", "qwen", false, true)],
            picker: CapturingPicker(items => { offered = items; return null; }));

        await Feed(repl, "/model");

        Assert.Equal(2, offered.Count);
        Assert.DoesNotContain(offered, r => r.Label.Contains("add", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SlashModelAdd_OnAUnmanagedSession_IS_REFUSED_like_every_other_model_command()
    {
        //the unmanaged notice is read before argument parsing, so it wins over the moved-command message
        var repl = ReplWith(
            unmanagedNotice: Gatto.Cli.UnmanagedSession.ModelUnavailable("http://127.0.0.1:1234"));

        var output = await Feed(repl, "/model add");

        Assert.Contains("gatto's own model settings only apply to servers it starts", output);
        //this assertion fails if the order of the two checks flips.
        Assert.DoesNotContain(Gatto.Repl.Repl.AddMovedText, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SlashModel_OnAUnmanagedSession_SaysWhatTheSessionIs_AndNeverSwitches()
    {
        //a connect session must never switch models, since a model on a matching port could slip past the guard
        var switched = false;
        var notice = Gatto.Cli.UnmanagedSession.ModelUnavailable("http://127.0.0.1:1234");
        var repl = ReplWith(
            switchModel: (id, _) => { switched = true; return new ModelSwitchResult(true, id, "s", 1, null, null, null); },
            unmanagedNotice: notice);

        var output = await Feed(repl, "/model gemma-4-26b");

        Assert.Contains(notice, output);
        Assert.False(switched);
        Assert.Equal("qwen3.6-35b", repl.ModelNameForTest);
    }

    [Fact]
    public async Task SlashModel_Bare_OnAUnmanagedSession_NeverOpensThePicker()
    {
        //the notice must fire before the picker opens, since the picker would offer a model this session never uses
        var opened = false;
        var repl = ReplWith(
            listModels: () => { opened = true; return []; },
            picker: FakePickerChoosing(0),
            unmanagedNotice: Gatto.Cli.UnmanagedSession.ModelUnavailable("http://127.0.0.1:1234"));

        var output = await Feed(repl, "/model");

        Assert.False(opened);
        Assert.Contains("gatto's own model settings only apply to servers it starts", output);
        Assert.DoesNotContain("gatto model new", output);
    }

    [Fact]
    public void TheUnmanagedSentenceNamesTheServerAndPromisesNoModelFix()
    {
        //this wording lives in one place and must not offer a model fix that cannot work here
        var line = Gatto.Cli.UnmanagedSession.ModelUnavailable("http://host:9");

        Assert.Contains("http://host:9", line);
        Assert.DoesNotContain("model new", line);
        //the null-endpoint shape must also refuse to name a model fix.
        Assert.DoesNotContain("model new", Gatto.Cli.UnmanagedSession.ModelUnavailable(null));
    }

    [Fact]
    public async Task SlashModel_Bare_ShowsPickerAndArmsTheChoice()
    {
        var repl = ReplWith(
            listModels: () =>
            [
                new PickerItem("gemma-4-26b", "gemma-4-26b  128k", Marked: true, Current: false),
                new PickerItem("qwen3.6-35b", "qwen3.6-35b  256k", Marked: false, Current: true),
            ],
            picker: FakePickerChoosing(0),   //the fake picker answers with the gemma row.
            switchModel: (id, _) => new ModelSwitchResult(true, id, "sys", 131072, null, null, null));

        await Feed(repl, "/model");
        Assert.Equal("gemma-4-26b", repl.ModelNameForTest);
    }

    [Fact]
    public async Task SlashModel_Bare_Cancelled_ChangesNothing()
    {
        var repl = ReplWith(
            modelName: "qwen3.6-35b",
            listModels: () => [new PickerItem("gemma-4-26b", "gemma", false, false)],
            picker: FakePickerChoosing(null));   //the fake picker answers cancel, as Escape would.

        await Feed(repl, "/model");
        Assert.Equal("qwen3.6-35b", repl.ModelNameForTest);
    }

    [Fact]
    public async Task SlashModel_Bare_MarksTheLoadedModelAndTheArmedModel()
    {
        IReadOnlyList<PickerItem>? shown = null;
        var repl = ReplWith(
            listModels: () =>
            [
                new PickerItem("gemma-4-26b", "gemma", Marked: true, Current: false),
                new PickerItem("qwen3.6-35b", "qwen",  Marked: false, Current: true),
            ],
            picker: CapturingPicker(items => { shown = items; return null; }));

        await Feed(repl, "/model");

        Assert.True(shown![0].Marked);    //marked means the weights are actually loaded.
        Assert.True(shown[1].Current);    //current means the model is armed now.
    }

    //assert the legend and rows the product sends, since the widget's own tests cannot see this call site
    [Fact]
    public async Task SlashModel_Bare_SendsTheRuledLegend_AndOnlyModels()
    {
        string? legend = null;
        IReadOnlyList<PickerItem>? shown = null;
        var repl = ReplWith(
            listModels: () => [new PickerItem("qwen3.6-35b", "qwen", Marked: true, Current: true)],
            picker: new LegendCapturingPicker((l, items) => { legend = l; shown = items; }));

        await Feed(repl, "/model");

        Assert.Equal("weights loaded   d set as default", legend);
        //the picker receives exactly what listModels produced and adds nothing of its own
        Assert.Single(shown!);
        Assert.Equal("qwen", shown![0].Label);
        //the legend keeps d, since gatto never changes default_model on its own
    }

    //the d key writes the default for the row the cursor was on, so a handler that writes row zero would fail here
    [Fact]
    public async Task SlashModel_D_WritesTheDefaultForTheROW_ItWasPressedOn()
    {
        var written = new List<string>();
        var p = new ScriptedPicker(new Queue<PickOutcome>(
            [new PickOutcome.Control('d', 1), new PickOutcome.Cancelled()]));
        var repl = ReplWith(listModels: Two(null), picker: p,
            setDefault: id => { written.Add(id); return null; });

        await Feed(repl, "/model");

        Assert.Equal(["b"], written);
    }

    //the reopened list reads the store again, so a test that watched only the first list would prove nothing
    [Fact]
    public async Task SlashModel_D_TheReopenedListSaysDefaultOnThatRowAndNoOther()
    {
        string? store = "a";
        var p = new ScriptedPicker(new Queue<PickOutcome>(
            [new PickOutcome.Control('d', 1), new PickOutcome.Cancelled()]));
        var repl = ReplWith(listModels: () => Rows(store), picker: p,
            setDefault: id => { store = id; return null; });

        await Feed(repl, "/model");

        Assert.Equal(2, p.Opens.Count);
        Assert.True(p.Opens[0][0].Default);     //before the write, a is the default.
        Assert.False(p.Opens[0][1].Default);
        Assert.False(p.Opens[1][0].Default);    //after the write, only b is the default.
        Assert.True(p.Opens[1][1].Default);
        Assert.Equal(1, p.OpenedAt[1]);         //the reopened picker starts where the user was.
    }

    //a failed write changes nothing and reports its sentence, so the second open still shows the old default
    [Fact]
    public async Task SlashModel_D_AFailedWriteChangesNothingAndTellsTheUser()
    {
        var p = new ScriptedPicker(new Queue<PickOutcome>(
            [new PickOutcome.Control('d', 1), new PickOutcome.Cancelled()]));
        var repl = ReplWith(listModels: Two("a"), picker: p,
            setDefault: _ => "could not persist the model choice: disk is full");

        var output = await Feed(repl, "/model");

        Assert.Contains("could not persist the model choice: disk is full", output, StringComparison.Ordinal);
        Assert.True(p.Opens[1][0].Default);     //the old default remains after a failed write.
        Assert.False(p.Opens[1][1].Default);
    }

    //without a writer the picker is told no control keys, so no d can come back from it
    [Fact]
    public async Task SlashModel_WithNoWriter_DoesNotOfferTheKey()
    {
        var p = new ScriptedPicker(new Queue<PickOutcome>([new PickOutcome.Cancelled()]));
        var repl = ReplWith(listModels: Two("a"), picker: p, setDefault: null);

        await Feed(repl, "/model");

        Assert.Null(p.Controls[0]);
    }

    private static IReadOnlyList<PickerItem> Rows(string? isDefault) =>
    [
        new PickerItem("a", "a", Marked: false, Current: true, Default: isDefault == "a"),
        new PickerItem("b", "b", Marked: false, Current: false, Default: isDefault == "b"),
    ];

    private static Func<IReadOnlyList<PickerItem>> Two(string? isDefault) => () => Rows(isDefault);

    //the by-name call must not claim the swap was already answered, or its confirm is skipped
    [Fact]
    public async Task SlashModel_ByName_IsNeverMarkedAsAlreadyAnswered()
    {
        bool? wasAnswered = null;
        var repl = ReplWith(
            modelName: "qwen3.6-35b",
            switchModel: (id, answered) =>
            {
                wasAnswered = answered;
                return new ModelSwitchResult(true, id, "s", 1, null, null, null);
            });

        await Feed(repl, "/model gemma-4-e4b");

        Assert.False(wasAnswered);
    }

}

//a model's account of its own tools is generation, so the command reports what gatto advertised
[Collection("e2e")]
public class ReplToolsTests : IDisposable
{
    private readonly TextReader _origIn = Console.In;
    private readonly TextWriter _origOut = Console.Out;
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-tools-repl-").FullName;

    public void Dispose()
    {
        Console.SetIn(_origIn);
        Console.SetOut(_origOut);
        try { Directory.Delete(_root, true); } catch { }
    }

    private string RunTools(string stdin, Func<IReadOnlyList<ToolInfo>>? listTools,
        Func<IReadOnlyList<(string Extension, string Line)>>? listPolicy = null)
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(_root), "m");
        var repl = new Gatto.Repl.Repl(
            loop, new Conversation("sys"), "generalist", "m", null, client, null, null, _root,
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"),
            listTools: listTools, listPolicy: listPolicy);

        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader(stdin));
        repl.RunAsync(CancellationToken.None).GetAwaiter().GetResult();
        Console.SetOut(_origOut);
        return sw.ToString();
    }

    private static IReadOnlyList<ToolInfo> Sample() =>
    [
        new ToolInfo("read_file", "Read a file from disk.", null),
        new ToolInfo("shell", "Run a PowerShell command.", null),
        new ToolInfo("web_search", "Search the web.", "web_search.csx"),
        new ToolInfo("shout", "Uppercase text.", "kit"),
    ];

    [Fact]
    public void Tools_ListsEveryArmedTool_WithItsDescription()
    {
        var output = RunTools("/tools\n/quit\n", Sample);

        Assert.Contains("read_file", output, StringComparison.Ordinal);
        Assert.Contains("Read a file from disk.", output, StringComparison.Ordinal);
        Assert.Contains("web_search", output, StringComparison.Ordinal);
        Assert.Contains("shout", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Tools_NamesTheExtensionThatArmedEachOne_AndSaysNothingForBuiltIns()
    {
        //naming the extension that armed a tool is the fact the model cannot tell you.
        var output = RunTools("/tools\n/quit\n", Sample);

        Assert.Contains("web_search.csx", output, StringComparison.Ordinal);
        Assert.Contains("kit", output, StringComparison.Ordinal);
        var readFileRow = output.Split('\n').First(l => l.Contains("read_file", StringComparison.Ordinal));
        Assert.DoesNotContain(".csx", readFileRow, StringComparison.Ordinal);
    }

    [Fact]
    public void Tools_CountsThem_SoTheHeaderIsCheckableAtAGlance()
    {
        var output = RunTools("/tools\n/quit\n", Sample);
        Assert.Contains("4 tools", output, StringComparison.Ordinal);
    }

    //the rule has two sites and this harness reaches only the plain one, so the rich half is held by source-invariant tests
    [Fact]
    public void Tools_ShowsAnExtensionsPolicyLine_BeneathItsOwnTools()
    {
        var output = RunTools("/tools\n/quit\n", Sample,
            () => new[] { ("web_search.csx", "cite every page you fetch") });

        Assert.Contains("Policy: cite every page you fetch", output, StringComparison.Ordinal);
        var rows = output.Split('\n');
        var web = Array.FindIndex(rows, r => r.Contains("Search the web.", StringComparison.Ordinal));
        var pol = Array.FindIndex(rows, r => r.Contains("Policy:", StringComparison.Ordinal));
        Assert.True(web >= 0 && pol > web, "the line follows the tool it arrived with");
        Assert.Contains("4 tools", output, StringComparison.Ordinal);   //a policy row must not count as a tool.
    }

    [Fact]
    public void Tools_WithNoPolicyLines_RendersExactlyWhatItRenderedBefore()
    {
        Assert.Equal(RunTools("/tools\n/quit\n", Sample),
                     RunTools("/tools\n/quit\n", Sample, () => Array.Empty<(string, string)>()));
    }

    [Fact]
    public void Tools_Unavailable_WhenTheSessionHasNoToolSource_IsFriendly()
    {
        var output = RunTools("/tools\n/quit\n", null);
        Assert.Contains("unavailable", output, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Tools_ColumnsStayAligned_WhenAnExtensionToolNameCarriesControlCharacters()
    {
        //extension names are untrusted, so they must be sanitized before column widths are computed
        IReadOnlyList<ToolInfo> Hostile() =>
        [
            new ToolInfo("read_file", "Read a file from disk.", null),
            new ToolInfo("evil[31m", "Hostile name.", "evil.csx"),
        ];

        var output = RunTools("/tools\n/quit\n", Hostile);

        var rows = output.Split('\n')
            .Where(l => l.Contains("Read a file from disk.", StringComparison.Ordinal)
                     || l.Contains("Hostile name.", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, rows.Count);

        //every row's description must start at the same column
        var columns = rows.Select(r => r.IndexOf(r.Contains("Hostile", StringComparison.Ordinal)
            ? "Hostile name." : "Read a file from disk.", StringComparison.Ordinal)).Distinct().ToList();
        Assert.Single(columns);

        //control bytes must never reach the terminal.
        Assert.DoesNotContain('', output);
        Assert.DoesNotContain('', output);
    }}

//redirected test output forces a plain terminal, so this drives the plain half of the effort command
[Collection("e2e")]
public class ReplEffortSlashTests : IDisposable
{
    private readonly TextReader _origIn = Console.In;
    private readonly TextWriter _origOut = Console.Out;

    public void Dispose()
    {
        Console.SetIn(_origIn);
        Console.SetOut(_origOut);
    }

    private static Gatto.Repl.Repl ReplWith(
        string? thinkingName = null, Func<string, bool, EffortResult>? setEffort = null,
        Func<IReadOnlyList<PickerItem>>? listEfforts = null, IListPicker? picker = null)
    {
        var client = new FakeChatClient();
        var loop = new AgentLoop(client, new ToolRegistry(), new HookBus(), new TestToolContext(Path.GetTempPath()), "m");
        var convo = new Conversation("sys");
        return new Gatto.Repl.Repl(
            loop, convo, "generalist", "qwen3.6-35b", null, client, null, null, Path.GetTempPath(),
            () => Composed.Text("sys"), () => null, _ => new RoleSwitchResult(false, "", null, null, "unused"),
            thinkingName: thinkingName, setEffort: setEffort, listEfforts: listEfforts, picker: picker);
    }

    private static async Task<string> Feed(Gatto.Repl.Repl repl, string input)
    {
        var sw = new StringWriter();
        Console.SetOut(sw);
        Console.SetIn(new StringReader(input + "\n/quit\n"));
        await repl.RunAsync(CancellationToken.None);
        return sw.ToString();
    }

    [Fact]
    public async Task SlashEffortLevel_AlwaysPersists_TheSameAsPickingEnter()
    {
        (string Level, bool Persist)? seen = null;
        var repl = ReplWith(setEffort: (level, persist) =>
        {
            seen = (level, persist);
            return new EffortResult(true, level, $"effort: {level}");
        });

        var output = await Feed(repl, "/effort xhigh");

        Assert.Equal(("xhigh", true), seen);
        Assert.Contains("effort: xhigh", output);
    }

    [Fact]
    public async Task SlashEffort_NoSetEffortClosure_ReportsUnavailable()
    {
        var repl = ReplWith(setEffort: null);

        var output = await Feed(repl, "/effort xhigh");

        Assert.Contains(Gatto.Repl.Repl.EffortUnavailable, output);
    }

    [Fact]
    public async Task SlashEffortLevel_Failure_ShowsTheMessageAndLeavesTheFooterUntouched()
    {
        var repl = ReplWith(thinkingName: "medium",
            setEffort: (_, _) => new EffortResult(false, null, "invalid thinking level 'nope'"));

        var output = await Feed(repl, "/effort nope");

        Assert.Contains("invalid thinking level 'nope'", output);
    }

    //a plain terminal prints the usage and current level, even when a real picker is wired
    [Fact]
    public async Task SlashEffort_Bare_InAPlainTerminal_PrintsUsage_NeverThePicker()
    {
        var opened = false;
        var repl = ReplWith(thinkingName: "medium",
            listEfforts: () =>
            [
                new PickerItem("medium", "medium", Marked: true, Current: true),
                new PickerItem("xhigh", "xhigh", Marked: false, Current: false),
            ],
            picker: new RecordingPicker(() => opened = true));

        var output = await Feed(repl, "/effort");

        Assert.False(opened, "a plain terminal must never open the picker");
        Assert.Contains(Gatto.Repl.Repl.EffortUsage("medium", isToggle: false, unavailable: false), output);
    }

    [Fact]
    public async Task SlashEffort_Bare_NoSetEffortOrListEfforts_PrintsUsage()
    {
        var repl = ReplWith(thinkingName: null);

        var output = await Feed(repl, "/effort");

        Assert.Contains(Gatto.Repl.Repl.EffortUsage(null, isToggle: false, unavailable: false), output);
    }

    private sealed class RecordingPicker(Action onPick) : IListPicker
    {
        public PickOutcome Pick(string title, IReadOnlyList<PickerItem> items, string? markLegend = null,
            IReadOnlyList<char>? controlKeys = null, int? openAt = null)
        {
            onPick();
            return new PickOutcome.Cancelled();
        }
    }
}

//the translation is split out of the command handler so tests can drive it with no terminal
public class InterpretEffortPick_Tests
{
    private static readonly IReadOnlyList<PickerItem> Items =
    [
        new PickerItem("low", "low", Marked: false, Current: false),
        new PickerItem("medium", "medium", Marked: true, Current: true),
        new PickerItem("xhigh", "xhigh", Marked: false, Current: false),
    ];

    [Fact]
    public void ENTER_ON_A_ROW_PERSISTS_THAT_ROWS_LEVEL()
    {
        var r = Gatto.Repl.Repl.InterpretEffortPick(Items, new PickOutcome.Picked(2));

        Assert.Equal(("xhigh", true), r);
    }

    [Fact]
    public void THE_S_KEY_ON_A_ROW_APPLIES_THAT_ROWS_LEVEL_WITHOUT_PERSISTING()
    {
        var r = Gatto.Repl.Repl.InterpretEffortPick(Items, new PickOutcome.Control('s', 0));

        Assert.Equal(("low", false), r);
    }

    //both answers read the same row, so an oracle on different rows would hide a swap of the two
    [Fact]
    public void ENTER_AND_S_ON_THE_SAME_ROW_AGREE_ON_THE_LEVEL_AND_DISAGREE_ONLY_ON_PERSIST()
    {
        var enter = Gatto.Repl.Repl.InterpretEffortPick(Items, new PickOutcome.Picked(1));
        var s = Gatto.Repl.Repl.InterpretEffortPick(Items, new PickOutcome.Control('s', 1));

        Assert.Equal("medium", enter!.Value.LevelArg);
        Assert.Equal("medium", s!.Value.LevelArg);
        Assert.True(enter.Value.Persist);
        Assert.False(s.Value.Persist);
    }

    [Fact]
    public void CANCELLED_IS_NULL_NOTHING_TO_APPLY()
    {
        Assert.Null(Gatto.Repl.Repl.InterpretEffortPick(Items, new PickOutcome.Cancelled()));
    }

    //the picker never reports an out-of-list row, so a bogus one counts as a cancel
    [Fact]
    public void AN_S_KEY_OUTSIDE_THE_LIST_IS_TREATED_AS_A_CANCEL()
    {
        Assert.Null(Gatto.Repl.Repl.InterpretEffortPick(Items, new PickOutcome.Control('s', 99)));
        Assert.Null(Gatto.Repl.Repl.InterpretEffortPick(Items, new PickOutcome.Control('s', -1)));
    }
}
