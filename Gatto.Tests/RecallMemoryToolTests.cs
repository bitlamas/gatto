using System.Runtime.Versioning;
using System.Text.Json;
using Gatto.Core.Tools;

namespace Gatto.Tests;

//a fake context with independent Cwd and HomePath, since the tool reads sessions from the home path and scopes them against the project root
public sealed class RecallTestContext(string cwd, string homePath) : IToolContext
{
    public string Cwd => cwd;
    public string HomePath => homePath;
    public IUserPrompter? Prompter => null;
    public IReadOnlyList<string> SessionFiles { get; set; } = [];
}

public class RecallMemoryToolTests : IDisposable
{
    private readonly string _base = Directory.CreateTempSubdirectory("gatto-recall-").FullName;
    private string Home => Path.Combine(_base, "home");
    //the sibling folder proj-other is a prefix extension of proj, and the scoping rule must reject it
    private string Proj => Path.Combine(_base, "proj");
    private string ProjOther => Path.Combine(_base, "proj-other");
    private string SessionsDir => Path.Combine(Home, "sessions");

    private IToolContext Ctx => new RecallTestContext(Proj, Home);

    public RecallMemoryToolTests()
    {
        Directory.CreateDirectory(Proj);
        Directory.CreateDirectory(ProjOther);
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;

    private static List<JsonElement> ParseHits(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    //writes a session file by hand in gatto's format, where a null cwd omits the field and produces a legacy lead record
    private string WriteSessionFile(string fileName, string? cwd, params (string role, string content, string? ts)[] turns)
    {
        Directory.CreateDirectory(SessionsDir);
        var path = Path.Combine(SessionsDir, fileName);
        var lines = new List<string>();

        object lead = cwd is null
            ? new { role = "system", content = "…", schema_version = 1 }
            : new { role = "system", content = "…", schema_version = 1, cwd };
        lines.Add(JsonSerializer.Serialize(lead));

        foreach (var (role, content, ts) in turns)
        {
            object rec = ts is null ? new { role, content } : new { role, content, ts };
            lines.Add(JsonSerializer.Serialize(rec));
        }

        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private static void AppendRawLine(string path, string rawJsonLine) =>
        File.AppendAllText(path, rawJsonLine + "\n");

    private void WriteLedgerSidecar(string fileName, string content)
    {
        Directory.CreateDirectory(SessionsDir);
        var path = Path.Combine(SessionsDir, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(new { role = "user", content }) + "\n");
    }

    //seeds one fixture set per test: a in scope, b in a subdirectory, c under a prefix-cousin root, d with no cwd
    private (string A, string B, string C, string D) SeedStandardFixtures()
    {
        var a = WriteSessionFile("0001-a.jsonl", Proj, ("user", "the nginx port is 8080", null));
        AppendRawLine(a, """{"kind":"display","text":"nginx"}"""); //the role-less display line holds the query word but must never match.
        var b = WriteSessionFile("0002-b.jsonl", Path.Combine(Proj, "sub"), ("user", "postgres password rotated", null));
        var c = WriteSessionFile("0003-c.jsonl", ProjOther, ("user", "nginx elsewhere", null));
        var d = WriteSessionFile("0004-d.jsonl", null, ("user", "nginx legacy fact", null));
        WriteLedgerSidecar("0005-e.ledger.jsonl", "nginx");
        return (a, b, c, d);
    }

    //a hit carries no role, and its excerpt is the record's head, so a background end reads as the task it was and never as the user's words
    [Fact]
    public async Task A_BACKGROUND_END_S_EXCERPT_OPENS_WITH_ITS_HEAD_LINE()
    {
        var path = WriteSessionFile("0001-a.jsonl", Proj, ("user", "start the server", null));
        AppendRawLine(path, JsonSerializer.Serialize(new
        {
            role = "user", content = "background task 1 ended · exit 1 · 4s\nnginx failed to bind port 8080", gloss = "exit 1", is_error = true, background = 1,
        }));

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"nginx bind"}"""), Ctx, default);

        var hit = Assert.Single(ParseHits(result.Text));
        Assert.StartsWith("background task 1 ended · exit 1 · 4s", hit.GetProperty("excerpt").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Scoped_FindsAtAndUnderRoot_NotOther()
    {
        var (a, _, _, _) = SeedStandardFixtures();
        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"nginx"}"""), Ctx, default);
        var hits = ParseHits(result.Text);
        Assert.Contains(hits, h => h.GetProperty("session").GetString() == a);
        Assert.DoesNotContain(hits, h => h.GetProperty("excerpt").GetString()!.Contains("elsewhere"));
    }

    [Fact]
    public async Task SubdirSession_Included()
    {
        var (_, b, _, _) = SeedStandardFixtures();
        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"postgres"}"""), Ctx, default);
        var hits = ParseHits(result.Text);
        Assert.Contains(hits, h => h.GetProperty("session").GetString() == b);
    }

    [Fact]
    public async Task ProjPrefixCousin_NotMatched()
    {
        SeedStandardFixtures();
        //the query word exists only in c, which sits under the prefix-cousin root, so a bare StartsWith would wrongly admit it
        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"elsewhere"}"""), Ctx, default);
        Assert.Equal("no matches", result.Text);
    }

    [Fact]
    public async Task RotatedSession_LeadingWithDisplayEvents_IsStillInScope()
    {
        //a rotated session leads with display events, so the record holding cwd sits several lines down and a line-one check would drop it
        Directory.CreateDirectory(SessionsDir);
        var path = Path.Combine(SessionsDir, "0006-rotated.jsonl");
        File.WriteAllLines(path,
        [
            JsonSerializer.Serialize(new { @event = "lead", text = "carried summary" }),
            JsonSerializer.Serialize(new { @event = "system", text = "memory index over budget" }),
            JsonSerializer.Serialize(new { @event = "completion", text = "purred" }),
            JsonSerializer.Serialize(new { role = "system", content = "…", schema_version = 1, cwd = Proj }),
            JsonSerializer.Serialize(new { role = "user", content = "the rotated nginx fact" }),
        ]);

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"rotated"}"""), Ctx, default);

        Assert.Contains(ParseHits(result.Text), h => h.GetProperty("session").GetString() == path);
    }

    [Fact]
    public async Task NoCwdSession_IsNeverIncluded_EvenWhenTheOldFlagIsPassed()
    {
        //a session with no cwd is out of scope, and the retired include_legacy flag changes nothing
        SeedStandardFixtures();
        var tool = new RecallMemoryTool();

        Assert.Equal("no matches", (await tool.ExecuteAsync(Args("""{"query":"legacy"}"""), Ctx, default)).Text);
        Assert.Equal("no matches",
            (await tool.ExecuteAsync(Args("""{"query":"legacy","include_legacy":true}"""), Ctx, default)).Text);

        //a genuinely different project stays out of scope whatever the retired flag says.
        Assert.Equal("no matches",
            (await tool.ExecuteAsync(Args("""{"query":"elsewhere","include_legacy":true}"""), Ctx, default)).Text);
    }

    [Fact]
    public async Task InScopeSession_IsStillFound_TheScopingMechanismSurvivesTheDeletion()
    {
        //an in-scope session must still be found, cwd is the scoping mechanism itself
        var (a, _, _, _) = SeedStandardFixtures();
        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"nginx"}"""), Ctx, default);
        Assert.Contains(ParseHits(result.Text), h => h.GetProperty("session").GetString() == a);
    }

    [Fact]
    public async Task LedgerSidecar_ExcludedFromResults()
    {
        //proves no ledger content reaches the results, the glob filters it before anything opens
        SeedStandardFixtures();   //the ledger sidecar holds the query word and must never surface in the hits.
        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"nginx"}"""), Ctx, default);
        var hits = ParseHits(result.Text);
        Assert.DoesNotContain(hits, h => h.GetProperty("session").GetString()!.EndsWith(".ledger.jsonl"));
    }

    [Fact]
    public async Task RoleLessRecords_NeverMatch()
    {
        var (a, _, _, _) = SeedStandardFixtures();
        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"nginx"}"""), Ctx, default);
        var hits = ParseHits(result.Text);
        //the role-less display line also holds the query word, so the reported turn must come from a role-bearing record
        var hitA = Assert.Single(hits, h => h.GetProperty("session").GetString() == a);
        Assert.Equal(2, hitA.GetProperty("turn").GetInt32());
    }

    [Fact]
    public async Task Excerpt_ControlCharsStripped_Capped300()
    {
        var raw = "\x1b[31mkeyword" + new string('a', 500);
        WriteSessionFile("0001-a.jsonl", Proj, ("user", raw, null));
        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"keyword"}"""), Ctx, default);
        var excerpt = Assert.Single(ParseHits(result.Text)).GetProperty("excerpt").GetString()!;
        //only control bytes are stripped, so the printable [31m survives sanitization
        Assert.DoesNotContain((char)0x1b, excerpt);
        Assert.Contains("[31m", excerpt);
        Assert.True(excerpt.Length <= 300);
        Assert.Contains("keyword", excerpt);
    }

    [Fact]
    public async Task SessionField_IsAbsolutePath()
    {
        WriteSessionFile("0001-a.jsonl", Proj, ("user", "the nginx port is 8080", null));
        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"nginx"}"""), Ctx, default);
        var hit = Assert.Single(ParseHits(result.Text));
        Assert.True(Path.IsPathRooted(hit.GetProperty("session").GetString()));
    }

    [Fact]
    public async Task MemoryTopicFiles_Searched()
    {
        var memDir = Path.Combine(Proj, ".gatto", "memory");
        Directory.CreateDirectory(memDir);
        File.WriteAllText(Path.Combine(memDir, "shell.md"), "- use pwsh not cmd\n- powershell heredocs need care\n");

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"powershell"}"""), Ctx, default);
        var hit = Assert.Single(ParseHits(result.Text));
        Assert.EndsWith("shell.md", hit.GetProperty("session").GetString());
        Assert.Equal(2, hit.GetProperty("turn").GetInt32());   //the reported turn is the number of the matching line.
    }

    [Fact]
    public async Task MemoryTopicFile_HighestScoringLineWins()
    {
        var memDir = Path.Combine(Proj, ".gatto", "memory");
        Directory.CreateDirectory(memDir);
        //line one overlaps one query term and line two overlaps two, so the highest scoring line must win
        File.WriteAllText(Path.Combine(memDir, "notes.md"),
            "nginx runs fine\nnginx port config lives here\n");

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"nginx port"}"""), Ctx, default);
        var hit = Assert.Single(ParseHits(result.Text));
        Assert.Equal(2, hit.GetProperty("turn").GetInt32());
    }

    [Fact]
    public async Task MemoryTopicFile_TieBrokenByLowestLineNumber()
    {
        //both lines overlap identically, so the max must stay strict and >= would hand the win to the last tied line
        var memDir = Path.Combine(Proj, ".gatto", "memory");
        Directory.CreateDirectory(memDir);
        File.WriteAllText(Path.Combine(memDir, "notes.md"), "widget note one\nwidget note two\n");

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"widget"}"""), Ctx, default);
        var hit = Assert.Single(ParseHits(result.Text));
        Assert.Equal(1, hit.GetProperty("turn").GetInt32());   //the first tied line wins
    }

    [Fact]
    public async Task NoMatches_FriendlyBody()
    {
        WriteSessionFile("0001-a.jsonl", Proj, ("user", "totally unrelated content", null));
        var result = await new RecallMemoryTool().ExecuteAsync(
            Args("""{"query":"zzz_nomatch_zzz"}"""), Ctx, default);
        Assert.Equal("no matches", result.Text);
        Assert.False(result.IsError);
    }

    [Fact]
    public async Task RecencyBreaksTies()
    {
        var older = WriteSessionFile("0001-older.jsonl", Proj, ("user", "the widget broke today", null));
        var newer = WriteSessionFile("0002-newer.jsonl", Proj, ("user", "the widget broke again", null));
        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"widget"}"""), Ctx, default);
        var hits = ParseHits(result.Text);
        Assert.Equal(newer, hits[0].GetProperty("session").GetString());
        Assert.Equal(older, hits[1].GetProperty("session").GetString());
    }

    [Fact]
    public async Task OutOfScopeSession_DoesNotAffectInScopeRecency()
    {
        //recency rank must cover the in-scope subset only, so an out-of-scope session cannot shift the scores
        var older = WriteSessionFile("0001-older.jsonl", Proj, ("user", "the widget broke today", null));
        var newer = WriteSessionFile("0002-newer.jsonl", Proj, ("user", "the widget broke again", null));
        var outsidePath = Path.Combine(SessionsDir, "0003-outside.jsonl");

        double ScoreOf(List<JsonElement> hits, string session) =>
            hits.Single(h => h.GetProperty("session").GetString() == session).GetProperty("score").GetDouble();

        WriteSessionFile("0003-outside.jsonl", ProjOther, ("user", "the widget is irrelevant here", null));
        var withOutside = ParseHits(
            (await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"widget"}"""), Ctx, default)).Text);
        var newerScoreWith = ScoreOf(withOutside, newer);
        var olderScoreWith = ScoreOf(withOutside, older);

        File.Delete(outsidePath);
        var withoutOutside = ParseHits(
            (await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"widget"}"""), Ctx, default)).Text);
        var newerScoreWithout = ScoreOf(withoutOutside, newer);
        var olderScoreWithout = ScoreOf(withoutOutside, older);

        Assert.Equal(newerScoreWithout, newerScoreWith);
        Assert.Equal(olderScoreWithout, olderScoreWith);
    }

    [Fact]
    public async Task SessionFileIOFailure_DoesNotDiscardOtherHits()
    {
        //one unreadable file must not discard a sibling's hits, a live session file can be swapped mid-scan
        var good = WriteSessionFile("0001-good.jsonl", Proj, ("user", "widget report ready", null));
        var locked = WriteSessionFile("0002-locked.jsonl", Proj, ("user", "widget report also here", null));

        using var handle = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"widget"}"""), Ctx, default);
        var hits = ParseHits(result.Text);
        Assert.Contains(hits, h => h.GetProperty("session").GetString() == good);
        Assert.DoesNotContain(hits, h => h.GetProperty("session").GetString() == locked);
    }

    [Fact]
    public async Task MemoryFileIOFailure_DoesNotDiscardOtherHits()
    {
        //memory files have no pre-check, so a locked topic file exercises the read guard in isolation and the failure must skip only that file
        var memDir = Path.Combine(Proj, ".gatto", "memory");
        Directory.CreateDirectory(memDir);
        var goodPath = Path.Combine(memDir, "good.md");
        var lockedPath = Path.Combine(memDir, "locked.md");
        File.WriteAllText(goodPath, "widget lives here\n");
        File.WriteAllText(lockedPath, "widget lives here too\n");

        using var handle = new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None);

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"widget"}"""), Ctx, default);
        var hits = ParseHits(result.Text);
        Assert.Contains(hits, h => h.GetProperty("session").GetString() == goodPath);
        Assert.DoesNotContain(hits, h => h.GetProperty("session").GetString() == lockedPath);
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task MemoryFileAclDenied_DoesNotDiscardOtherHits()
    {
        //an acl denial throws UnauthorizedAccessException, so the guard must catch it too or collected hits are lost
        var memDir = Path.Combine(Proj, ".gatto", "memory");
        Directory.CreateDirectory(memDir);
        var goodPath = Path.Combine(memDir, "good.md");
        var deniedPath = Path.Combine(memDir, "denied.md");
        File.WriteAllText(goodPath, "widget lives here\n");
        File.WriteAllText(deniedPath, "widget lives here too\n");

        var identity = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            identity, System.Security.AccessControl.FileSystemRights.Read,
            System.Security.AccessControl.AccessControlType.Deny);
        var fileInfo = new FileInfo(deniedPath);
        var acl = fileInfo.GetAccessControl();
        acl.AddAccessRule(rule);
        fileInfo.SetAccessControl(acl);
        try
        {
            var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"widget"}"""), Ctx, default);
            var hits = ParseHits(result.Text);
            Assert.Contains(hits, h => h.GetProperty("session").GetString() == goodPath);
            Assert.DoesNotContain(hits, h => h.GetProperty("session").GetString() == deniedPath);
        }
        finally
        {
            acl.RemoveAccessRule(rule);
            fileInfo.SetAccessControl(acl);
        }
    }

    [Fact]
    public async Task MissingSessionsDir_NoError()
    {
        //the sessions folder may never have been created, so recall must answer no matches rather than throw
        Assert.False(Directory.Exists(SessionsDir));
        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"anything"}"""), Ctx, default);
        Assert.Equal("no matches", result.Text);
    }

    [Fact]
    public async Task Assistant_excerpts_are_stripped_but_tool_and_user_excerpts_are_verbatim()
    {
        //stripping is gated on role, so a real tool record keeps those tokens verbatim
        WriteSessionFile("0001-a.jsonl", Proj,
            ("assistant", "<|tool_call_begin|>quarterly report findings", null),
            ("tool", "<|im_start|>quarterly template<|im_end|>", null),
            ("user", "about the quarterly <|tool_call_begin|> token", null));

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"quarterly"}"""), Ctx, default);
        var excerpts = ParseHits(result.Text).Select(h => h.GetProperty("excerpt").GetString()!).ToList();

        Assert.Contains(excerpts, e => e == "quarterly report findings");                  //the assistant excerpt has its control tokens stripped
        Assert.Contains(excerpts, e => e == "<|im_start|>quarterly template<|im_end|>");   //the tool excerpt stays verbatim.
        Assert.Contains(excerpts, e => e == "about the quarterly <|tool_call_begin|> token"); //the user excerpt stays verbatim.
    }

    private static string SessionOf(JsonElement hit) => hit.GetProperty("session").GetString()!;

    //the caller's file is the newest and holds the best match, which the model already has in context
    [Fact]
    public async Task THE_CALLERS_OWN_FILE_NEVER_HITS_AND_THE_NEXT_NEWEST_TAKES_RECENCY()
    {
        var older = WriteSessionFile("0001-older.jsonl", Proj, ("user", "the widget broke", null));
        var own = WriteSessionFile("0002-own.jsonl", Proj, ("user", "the widget broke again, widget broke", null));
        var ctx = new RecallTestContext(Proj, Home) { SessionFiles = [own] };

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"widget broke"}"""), ctx, default);

        var hit = Assert.Single(ParseHits(result.Text));
        Assert.Equal(older, SessionOf(hit));
        Assert.Equal(1.5, hit.GetProperty("score").GetDouble());   //full overlap plus half of rank 1.0
    }

    //a compaction successor shares its predecessor's session id, and the predecessor holds turns the summary lost
    [Fact]
    public async Task A_COMPACTION_PREDECESSOR_WITH_THE_SAME_SESSION_ID_STILL_HITS()
    {
        var id = Guid.NewGuid().ToString();
        string Write(string name, string content)
        {
            Directory.CreateDirectory(SessionsDir);
            var path = Path.Combine(SessionsDir, name);
            File.WriteAllLines(path,
            [
                JsonSerializer.Serialize(new { role = "system", content = "…", schema_version = 1, cwd = Proj, session_id = id, session_start = "2026-10-25T10:00:00-04:00" }),
                JsonSerializer.Serialize(new { role = "user", content }),
            ]);
            return path;
        }
        var predecessor = Write("0001-before.jsonl", "the gasket size was twelve");
        var successor = Write("0002-after.jsonl", "summary of the earlier talk");
        var ctx = new RecallTestContext(Proj, Home) { SessionFiles = [successor] };

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"gasket"}"""), ctx, default);

        Assert.Equal(predecessor, SessionOf(Assert.Single(ParseHits(result.Text))));
    }

    //a resume copies the loaded file's turns into context, so that file is the caller's own as much as the one being written
    [Fact]
    public async Task THE_FILE_A_SESSION_RESUMED_FROM_NEVER_HITS()
    {
        var oldest = WriteSessionFile("0001-oldest.jsonl", Proj, ("user", "the widget broke", null));
        var resumed = WriteSessionFile("0002-resumed.jsonl", Proj, ("user", "the widget broke", null));
        var writing = WriteSessionFile("0003-writing.jsonl", Proj, ("user", "the widget broke", null));
        var ctx = new RecallTestContext(Proj, Home) { SessionFiles = [writing, resumed] };

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"widget"}"""), ctx, default);

        Assert.Equal(oldest, SessionOf(Assert.Single(ParseHits(result.Text))));
    }

    //the system record holds the index and the context files, and a skipped record still counts so every turn pointer stays put
    [Fact]
    public async Task A_SYSTEM_RECORD_NEVER_HITS_AND_STILL_COUNTS_AS_A_TURN()
    {
        Directory.CreateDirectory(SessionsDir);
        var path = Path.Combine(SessionsDir, "0001-a.jsonl");
        File.WriteAllLines(path,
        [
            JsonSerializer.Serialize(new { role = "system", content = "memory index: the widget fact", schema_version = 1, cwd = Proj }),
            JsonSerializer.Serialize(new { role = "user", content = "the widget broke" }),
        ]);

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"widget"}"""), Ctx, default);

        var hit = Assert.Single(ParseHits(result.Text));
        Assert.Equal(2, hit.GetProperty("turn").GetInt32());
        Assert.Equal("the widget broke", hit.GetProperty("excerpt").GetString());
    }

    [Fact]
    public async Task EMPTY_SESSION_FILES_SCANS_EVERY_FILE()
    {
        var older = WriteSessionFile("0001-older.jsonl", Proj, ("user", "the widget broke", null));
        var newer = WriteSessionFile("0002-newer.jsonl", Proj, ("user", "the widget broke", null));
        var ctx = new RecallTestContext(Proj, Home) { SessionFiles = [] };

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"widget"}"""), ctx, default);

        Assert.Equal([newer, older], ParseHits(result.Text).Select(SessionOf));
    }

    //the store and the scan can spell one file differently, so the comparison is on the full path with case folded
    [Fact]
    public async Task SESSION_FILES_COMPARE_FULL_PATHS()
    {
        var older = WriteSessionFile("0001-older.jsonl", Proj, ("user", "the widget broke", null));
        WriteSessionFile("0002-own.jsonl", Proj, ("user", "the widget broke", null));
        var spelled = Path.Combine(SessionsDir, "..", "SESSIONS", "0002-OWN.jsonl");
        var ctx = new RecallTestContext(Proj, Home) { SessionFiles = [spelled] };

        var result = await new RecallMemoryTool().ExecuteAsync(Args("""{"query":"widget"}"""), ctx, default);

        Assert.Equal(older, SessionOf(Assert.Single(ParseHits(result.Text))));
    }
}
