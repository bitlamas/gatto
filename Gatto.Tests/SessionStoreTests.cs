using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;

namespace Gatto.Tests;

public class SessionStoreTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-cwd-").FullName;
    public void Dispose() { Directory.Delete(_home, true); Directory.Delete(_cwd, true); }

    [Fact]
    public void Save_and_load_roundtrip_including_tool_calls()
    {
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
        var store = new SessionStore(_home, _cwd);
        var convo = new Conversation("sys");
        convo.AddUser("hi");
        convo.AddAssistant("", new[] { new ToolCall("c1", "shell", "{\"command\":\"dir\"}") });
        convo.AddToolResult("c1", new Gatto.Core.Tools.ToolResult("listing"));
        convo.AddAssistant("done");
        store.Save(convo);

        var loaded = new SessionStore(_home, _cwd).LoadLatestForCwd();

        Assert.NotNull(loaded);
        Assert.Equal(5, loaded!.Count);
        Assert.Equal("system", loaded[0].Role);
        Assert.Equal("shell", loaded[2].ToolCalls!.Single().Name);
        Assert.Equal("c1", loaded[3].ToolCallId);
        Assert.Equal("done", loaded[4].Content);
    }

    [Fact]
    public void Load_returns_null_when_no_session_for_cwd()
    {
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
        Assert.Null(new SessionStore(_home, _cwd).LoadLatestForCwd());
    }

    //the ledger sidecar is a .jsonl beside the session that sorts after it, so the glob must exclude sidecars
    [Fact]
    public void LatestPath_and_Count_ignore_the_ledger_sidecar_of_the_newest_session()
    {
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
        var store = new SessionStore(_home, _cwd);
        var convo = new Conversation(null);
        convo.AddUser("cited a source");
        store.Save(convo);

        var sessionPath = new SessionStore(_home, _cwd).LatestPathForCwd();
        Assert.NotNull(sessionPath);
        //write the sidecar beside the session with the same stem, the shape the citation ledger really writes.
        var sidecar = Path.Combine(Path.GetDirectoryName(sessionPath)!,
            Path.GetFileNameWithoutExtension(sessionPath) + ".ledger.jsonl");
        File.WriteAllText(sidecar, "{\"url\":\"x\"}\n");

        Assert.Equal(sessionPath, new SessionStore(_home, _cwd).LatestPathForCwd());  //the newest session is returned rather than its sidecar
        Assert.Equal(1, new SessionStore(_home, _cwd).CountForCwd());                 //only the session is counted, the sidecar is left out
    }

    [Fact]
    public void Different_cwd_sessions_do_not_mix()
    {
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
        var convo = new Conversation(null);
        convo.AddUser("other dir");
        new SessionStore(_home, _home).Save(convo);   //this session belongs to a different working directory.
        Assert.Null(new SessionStore(_home, _cwd).LoadLatestForCwd());
    }

    [Fact]
    public void StartNew_writes_subsequent_saves_to_a_fresh_file()
    {
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
        var store = new SessionStore(_home, _cwd);
        var convo = new Conversation(null);
        convo.AddUser("first");
        store.Save(convo);

        store.StartNew();                              //this is what the /new command calls
        var convo2 = new Conversation(null);
        convo2.AddUser("second");
        store.Save(convo2);

        Assert.Equal(2, Directory.GetFiles(Path.Combine(_home, "sessions")).Length);
        var latest = new SessionStore(_home, _cwd).LoadLatestForCwd();
        Assert.Equal("second", latest![0].Content);    //the old transcript survives on disk next to the new one.
    }

    [Fact]
    public void CurrentPath_is_set_after_Save_and_nulled_by_StartNew()
    {
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
        var store = new SessionStore(_home, _cwd);
        Assert.Null(store.CurrentPath);                 //nothing is saved yet, so there is no current path.

        var convo = new Conversation(null);
        convo.AddUser("hi");
        store.Save(convo);
        Assert.NotNull(store.CurrentPath);              //the /compact command must read this before StartNew clears it

        store.StartNew();
        Assert.Null(store.CurrentPath);                 //the path reads null once StartNew has run, so read it first
    }

    [Fact]
    public void Concurrent_saves_on_one_store_do_not_crash()
    {
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
        var store = new SessionStore(_home, _cwd);
        var convo = new Conversation(null);
        convo.AddUser("hello");
        Parallel.For(0, 20, _ => store.Save(convo));
        Assert.NotNull(new SessionStore(_home, _cwd).LoadLatestForCwd());
    }

    [Fact]
    public void ChatJson_omits_new_fields_at_default_byte_identical_to_legacy()
    {
        var m = new ChatMessage("tool", "3 files", ToolCallId: "call_1");   //the message has no gatto role and no error.
        var json = SessionStore.ChatJson(m);
        Assert.DoesNotContain("gatto_role", json);
        Assert.DoesNotContain("is_error", json);
        Assert.DoesNotContain("gloss", json);
        Assert.Equal("{\"role\":\"tool\",\"content\":\"3 files\",\"tool_call_id\":\"call_1\"}", json);
    }

    //the elision frontier and the ratio ride the assistant record only when something was cut, an uncut record stays as it was
    [Fact]
    public void ChatJson_writes_the_elision_shape_only_when_set_and_reads_it_back()
    {
        var plain = new ChatMessage("assistant", "hi");
        Assert.Equal("{\"role\":\"assistant\",\"content\":\"hi\"}", SessionStore.ChatJson(plain));

        var json = SessionStore.ChatJson(plain with { Shape = new Gatto.Core.Loop.ShapeMark(37, 1.25) });
        Assert.Contains("\"elided_through\":37", json);
        Assert.Contains("\"token_ratio\":1.25", json);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(new Gatto.Core.Loop.ShapeMark(37, 1.25), SessionStore.ParseChatElement(doc.RootElement).Shape);
        using var none = System.Text.Json.JsonDocument.Parse(SessionStore.ChatJson(plain));
        Assert.Null(SessionStore.ParseChatElement(none.RootElement).Shape);
    }

    [Fact]
    public void ChatJson_writes_new_fields_only_when_set()
    {
        var m = new ChatMessage("assistant", "hi", ToolCallId: null,
            GattoRole: "coder", IsError: false, Gloss: null);
        var json = SessionStore.ChatJson(m);
        Assert.Contains("\"gatto_role\":\"coder\"", json);
        Assert.DoesNotContain("is_error", json);   //a false is_error is the default, so it is omitted from the json
        Assert.DoesNotContain("gloss", json);       //a null gloss is omitted from the json.

        var tool = new ChatMessage("tool", "boom", ToolCallId: "c1",
            GattoRole: "coder", IsError: true, Gloss: "exit 1");
        var tj = SessionStore.ChatJson(tool);
        Assert.Contains("\"is_error\":true", tj);
        Assert.Contains("\"gloss\":\"exit 1\"", tj);
    }

    [Fact]
    public void ChatJson_ParseChatElement_round_trips_reasoning_content()
    {
        //reasoning is persisted so a --continue resume keeps the prior thinking
        var m = new ChatMessage("assistant", "answer", ReasoningContent: "my hidden thinking");
        var json = SessionStore.ChatJson(m);
        Assert.Contains("\"reasoning_content\":\"my hidden thinking\"", json);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal("my hidden thinking", SessionStore.ParseChatElement(doc.RootElement).ReasoningContent);
    }

    [Fact]
    public void ChatJson_ParseChatElement_round_trips_all_new_fields()
    {
        var m = new ChatMessage("tool", "boom", ToolCallId: "c1",
            GattoRole: "coder", IsError: true, Gloss: "exit 1");
        using var doc = System.Text.Json.JsonDocument.Parse(SessionStore.ChatJson(m));
        var back = SessionStore.ParseChatElement(doc.RootElement);
        Assert.Equal("coder", back.GattoRole);
        Assert.True(back.IsError);
        Assert.Equal("exit 1", back.Gloss);
    }

    [Fact]
    public void ParseChatElement_ignores_truthy_string_is_error()   //a truthy string must not read as an error, the parser checks the json kind
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            "{\"role\":\"tool\",\"content\":\"x\",\"is_error\":\"true\"}");
        Assert.False(SessionStore.ParseChatElement(doc.RootElement).IsError);
    }

    [Fact]
    public void LoadLatestForCwd_reads_new_fields_via_shared_parser()
    {
        var home = Path.Combine(Path.GetTempPath(), "gatto-t-" + Guid.NewGuid().ToString("N"));
        var store = new SessionStore(home, cwd: "C:\\proj");
        store.WriteLines(new[] {
            SessionStore.ChatJson(new ChatMessage("tool", "boom", ToolCallId: "c1",
                GattoRole: "coder", IsError: true, Gloss: "exit 1")) });
        var loaded = store.LoadLatestForCwd();
        var tool = Assert.Single(loaded!);
        Assert.True(tool.IsError);
        Assert.Equal("exit 1", tool.Gloss);
        Assert.Equal("coder", tool.GattoRole);
        Directory.Delete(home, true);
    }

    [Fact]
    public void ResolvePathById_ExactStemMatch()
    {
        var sessionsDir = Path.Combine(_home, "sessions");
        Directory.CreateDirectory(sessionsDir);
        var target = Path.Combine(sessionsDir, "d8dc926051ec-0639202104498276335.jsonl");
        File.WriteAllText(target, "");
        File.WriteAllText(Path.Combine(sessionsDir, "d8dc926051ec-0639202104498276999.jsonl"), "");

        var store = new SessionStore(_home, _cwd);
        Assert.Equal(target, SessionStore.ResolvePathById(_home, "d8dc926051ec-0639202104498276335"));
    }

    [Fact]
    public void ResolvePathById_AcceptsAPastedTrailingJsonlExtension()
    {
        var sessionsDir = Path.Combine(_home, "sessions");
        Directory.CreateDirectory(sessionsDir);
        var stem = "d8dc926051ec-0639202104498276335";
        var target = Path.Combine(sessionsDir, stem + ".jsonl");
        File.WriteAllText(target, "");

        var store = new SessionStore(_home, _cwd);
        Assert.Equal(target, SessionStore.ResolvePathById(_home, stem + ".jsonl"));
    }

    [Fact]
    public void ResolvePathById_UniquePrefixMatch()
    {
        var sessionsDir = Path.Combine(_home, "sessions");
        Directory.CreateDirectory(sessionsDir);
        var target = Path.Combine(sessionsDir, "d8dc926051ec-0639202104498276335.jsonl");
        File.WriteAllText(target, "");

        var store = new SessionStore(_home, _cwd);
        Assert.Equal(target, SessionStore.ResolvePathById(_home, "d8dc926051ec"));
    }

    //the exact match must be checked before prefix matching, abc is also a prefix of abcdef
    [Fact]
    public void ResolvePathById_ExactMatchWinsOverAnAmbiguousPrefix()
    {
        var sessionsDir = Path.Combine(_home, "sessions");
        Directory.CreateDirectory(sessionsDir);
        var exact = Path.Combine(sessionsDir, "abc.jsonl");
        File.WriteAllText(exact, "");
        File.WriteAllText(Path.Combine(sessionsDir, "abcdef.jsonl"), "");

        var store = new SessionStore(_home, _cwd);
        Assert.Equal(exact, SessionStore.ResolvePathById(_home, "abc"));
    }

    [Fact]
    public void ResolvePathById_AmbiguousPrefix_ThrowsNamingTheCount()
    {
        var sessionsDir = Path.Combine(_home, "sessions");
        Directory.CreateDirectory(sessionsDir);
        File.WriteAllText(Path.Combine(sessionsDir, "abc111.jsonl"), "");
        File.WriteAllText(Path.Combine(sessionsDir, "abc222.jsonl"), "");

        var store = new SessionStore(_home, _cwd);
        var ex = Assert.Throws<GattoConfigException>(() => SessionStore.ResolvePathById(_home, "abc"));
        Assert.Contains("2", ex.Message);
    }

    [Fact]
    public void ResolvePathById_NoMatch_Throws()
    {
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
        var store = new SessionStore(_home, _cwd);
        var ex = Assert.Throws<GattoConfigException>(() => SessionStore.ResolvePathById(_home, "nope"));
        Assert.Contains("no", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void ResolvePathById_NoMatch_WhenSessionsDirDoesNotExistYet_Throws()
    {
        var store = new SessionStore(_home, _cwd);   //the sessions dir was never created.
        Assert.Throws<GattoConfigException>(() => SessionStore.ResolvePathById(_home, "nope"));
    }

    [Fact]
    public void ResolvePathById_SearchesTheWholeSessionsDir_NotJustThisStoresCwd()
    {
        var sessionsDir = Path.Combine(_home, "sessions");
        Directory.CreateDirectory(sessionsDir);
        //id resolution searches every session by design, so a key from another cwd still resolves
        var target = Path.Combine(sessionsDir, "otherkey-12345.jsonl");
        File.WriteAllText(target, "");

        var store = new SessionStore(_home, _cwd);
        Assert.Equal(target, SessionStore.ResolvePathById(_home, "otherkey-12345"));
    }

    //ledger sidecars end in .jsonl too, and never count as a session or as a prefix match
    [Fact]
    public void ResolvePathById_IgnoresLedgerSidecarFiles()
    {
        var sessionsDir = Path.Combine(_home, "sessions");
        Directory.CreateDirectory(sessionsDir);
        var target = Path.Combine(sessionsDir, "abc-001.jsonl");
        File.WriteAllText(target, "");
        File.WriteAllText(Path.Combine(sessionsDir, "abc-001.ledger.jsonl"), "");

        var store = new SessionStore(_home, _cwd);
        Assert.Equal(target, SessionStore.ResolvePathById(_home, "abc-001"));    //exact resolve must not see the sidecar.
        Assert.Equal(target, SessionStore.ResolvePathById(_home, "abc"));        //the sidecar must not make a prefix resolve ambiguous
    }

    [Fact]
    public void LoadPath_parses_a_specific_file_directly()
    {
        var sessionsDir = Path.Combine(_home, "sessions");
        Directory.CreateDirectory(sessionsDir);
        var path = Path.Combine(sessionsDir, "explicit-id.jsonl");
        File.WriteAllText(path, SessionStore.ChatJson(new ChatMessage("user", "hello")) + "\n");

        var store = new SessionStore(_home, _cwd);
        var loaded = store.LoadPath(path);
        var msg = Assert.Single(loaded);
        Assert.Equal("user", msg.Role);
        Assert.Equal("hello", msg.Content);
    }

    [Fact]
    public void LegacyGoldenFixture_parses_to_defaults()
    {
        //the golden fixture comes from the real writer, and holds frozen legacy fields asserted for presence, format and round-trip
        var lines = File.ReadAllLines("Fixtures/legacy-session-7d209d82.jsonl");
        var expectedTs = new[]
        {
            new DateTime(2026, 7, 21, 6, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 7, 21, 6, 0, 5, DateTimeKind.Utc),
            new DateTime(2026, 7, 21, 6, 0, 12, DateTimeKind.Utc),
            new DateTime(2026, 7, 21, 6, 0, 13, DateTimeKind.Utc),
        };
        for (var i = 0; i < lines.Length; i++)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(lines[i]);
            var el = doc.RootElement;
            Assert.Contains("\"ts\":\"", lines[i]);                              //assert the ts key is present and in o format
            var m = SessionStore.ParseChatElement(el);
            Assert.Null(m.GattoRole);
            Assert.False(m.IsError);
            Assert.Null(m.Gloss);
            Assert.Equal(expectedTs[i], m.Ts!.Value.ToUniversalTime());          //the frozen timestamp must round-trip through writer and parser.
            if (i == 2) continue;   //the tool_calls record is checked below, where usage and timings are not default values
            Assert.Null(m.Usage);
            Assert.Null(m.Timings);
        }

        //schema_version appears on the system record only, and nothing reads it back so the assertion is on the raw json
        Assert.Contains("\"schema_version\":1", lines[0], StringComparison.Ordinal);

        //the record at index 2 holds the frozen usage and timings.
        using var toolCallDoc = System.Text.Json.JsonDocument.Parse(lines[2]);
        var toolCallMsg = SessionStore.ParseChatElement(toolCallDoc.RootElement);
        Assert.Equal(new Usage(42, 18), toolCallMsg.Usage);
        Assert.NotNull(toolCallMsg.Timings);
        using var timingsDoc = System.Text.Json.JsonDocument.Parse(toolCallMsg.Timings!);
        Assert.Equal(12.31, timingsDoc.RootElement.GetProperty("predicted_per_second").GetDouble());
        Assert.Equal(18, timingsDoc.RootElement.GetProperty("predicted_n").GetInt32());
    }

    [Fact]
    public void ChatJson_writes_schema_version_on_the_system_record_only()
    {
        var sys = new ChatMessage("system", "you are gatto");
        Assert.Contains("\"schema_version\":1", SessionStore.ChatJson(sys));
        var asst = new ChatMessage("assistant", "hi", GattoRole: "coder");
        Assert.DoesNotContain("schema_version", SessionStore.ChatJson(asst));
        var user = new ChatMessage("user", "q");
        Assert.DoesNotContain("schema_version", SessionStore.ChatJson(user));
    }

    [Fact]
    public void ChatJson_writes_ts_when_set_and_round_trips_it()
    {
        var t = new DateTime(2026, 7, 21, 6, 0, 0, DateTimeKind.Utc);
        var json = SessionStore.ChatJson(new ChatMessage("user", "q", Ts: t));
        Assert.Contains("\"ts\":\"2026-07-21T06:00:00.0000000Z\"", json);
        Assert.DoesNotContain("ts", SessionStore.ChatJson(new ChatMessage("user", "q")));   //with no timestamp set the ts key is omitted
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(t, SessionStore.ParseChatElement(doc.RootElement).Ts!.Value.ToUniversalTime());
    }

    [Fact]
    public void ChatJson_omits_usage_and_timings_when_null()
    {
        var m = new ChatMessage("assistant", "hi");
        var json = SessionStore.ChatJson(m);
        Assert.DoesNotContain("usage", json);
        Assert.DoesNotContain("timings", json);
    }

    [Fact]
    public void ChatJson_writes_usage_and_timings_when_set_and_round_trips_them()
    {
        var usage = new Usage(5, 2);
        var timings = "{\"predicted_per_second\":12.31,\"predicted_n\":7}";
        var m = new ChatMessage("assistant", "hi", Usage: usage, Timings: timings);
        var json = SessionStore.ChatJson(m);

        Assert.Contains("\"usage\":{\"prompt_tokens\":5,\"completion_tokens\":2}", json);
        //timings is written as a raw json object
        Assert.Contains("\"timings\":{\"predicted_per_second\":12.31,\"predicted_n\":7}", json);
        Assert.DoesNotContain("\"timings\":\"", json);   //a stringified timings object must never appear

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var back = SessionStore.ParseChatElement(doc.RootElement);
        Assert.Equal(usage, back.Usage);
        using var timingsDoc = System.Text.Json.JsonDocument.Parse(back.Timings!);
        Assert.Equal(12.31, timingsDoc.RootElement.GetProperty("predicted_per_second").GetDouble());
        Assert.Equal(7, timingsDoc.RootElement.GetProperty("predicted_n").GetInt32());
    }

    [Fact]
    public void ParseChatElement_ignores_non_object_usage_and_timings()   //the parser checks the json kind, so a non-object usage or timings reads as null
    {
        using var doc = System.Text.Json.JsonDocument.Parse(
            "{\"role\":\"assistant\",\"content\":\"x\",\"usage\":\"nope\",\"timings\":5}");
        var m = SessionStore.ParseChatElement(doc.RootElement);
        Assert.Null(m.Usage);
        Assert.Null(m.Timings);
    }

    //the system record stamps the full cwd, which a scoped session search will compare against a project root

    [Fact]
    public void SystemRecord_CarriesCwd()
    {
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
        var store = new SessionStore(_home, _cwd);
        var convo = new Conversation("sys");
        convo.AddUser("hi");
        store.Save(convo);

        var first = File.ReadLines(store.CurrentPath!).First();
        using var doc = System.Text.Json.JsonDocument.Parse(first);
        Assert.Equal("system", doc.RootElement.GetProperty("role").GetString());
        Assert.Equal(Path.GetFullPath(_cwd), doc.RootElement.GetProperty("cwd").GetString());
    }

    [Fact]
    public void NonSystemRecords_NoCwd()
    {
        Directory.CreateDirectory(Path.Combine(_home, "sessions"));
        var store = new SessionStore(_home, _cwd);
        var convo = new Conversation("sys");
        convo.AddUser("hi");
        convo.AddAssistant("hello");
        store.Save(convo);

        var lines = File.ReadAllLines(store.CurrentPath!);
        foreach (var line in lines.Skip(1))   //skip the leading system record, the only record allowed to hold cwd
        {
            using var doc = System.Text.Json.JsonDocument.Parse(line);
            Assert.False(doc.RootElement.TryGetProperty("cwd", out _));
        }
    }

    [Fact]
    public void LegacyFileWithoutCwd_LoadsFine()
    {
        var sessionsDir = Path.Combine(_home, "sessions");
        Directory.CreateDirectory(sessionsDir);
        var path = Path.Combine(sessionsDir, "legacy-no-cwd.jsonl");
        File.WriteAllText(path, "{\"role\":\"system\",\"content\":\"you are gatto\",\"schema_version\":1}\n");

        var store = new SessionStore(_home, _cwd);
        var loaded = store.LoadPath(path);

        var sys = Assert.Single(loaded);
        Assert.Equal("system", sys.Role);
        Assert.Equal("you are gatto", sys.Content);
        //what this pins is that a record with a cwd stamp still loads, the field is ignored
    }

    [Fact]
    public void ChatJson_NoCwdArg_ByteIdenticalToBefore()
    {
        //the cwd parameter defaults to null and changes nothing for a caller that omits it
        var sys = new ChatMessage("system", "you are gatto");
        Assert.Equal("{\"role\":\"system\",\"content\":\"you are gatto\",\"schema_version\":1}", SessionStore.ChatJson(sys));

        var user = new ChatMessage("user", "hi");
        Assert.Equal("{\"role\":\"user\",\"content\":\"hi\"}", SessionStore.ChatJson(user));
    }

    [Fact]
    public void ChatJson_writes_stripped_only_when_positive()
    {
        var clean = new ChatMessage("assistant", "hi");
        Assert.DoesNotContain("stripped", SessionStore.ChatJson(clean));

        var healed = new ChatMessage("assistant", "hi", Stripped: 3);
        Assert.Contains("\"stripped\":3", SessionStore.ChatJson(healed));
    }

    [Fact]
    public void ParseChatElement_round_trips_stripped()
    {
        var json = SessionStore.ChatJson(new ChatMessage("assistant", "hi", Stripped: 5));
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var m = SessionStore.ParseChatElement(doc.RootElement);
        Assert.Equal(5, m.Stripped);

        using var clean = System.Text.Json.JsonDocument.Parse("{\"role\":\"assistant\",\"content\":\"hi\"}");
        Assert.Equal(0, SessionStore.ParseChatElement(clean.RootElement).Stripped);
    }

    [Fact]
    public void LoadPath_heals_poisoned_assistant_records_and_accumulates_stripped()
    {
        var dir = Directory.CreateTempSubdirectory().FullName;
        var path = Path.Combine(dir, "x-0000000000000000001.jsonl");
        File.WriteAllLines(path, new[]
        {
            "{\"role\":\"system\",\"content\":\"s\",\"schema_version\":1}",
            //this record already carries a persisted stripped count of 2, and one new token is stripped on top
            "{\"role\":\"assistant\",\"content\":\"<|tool_call_begin|>hello\",\"stripped\":2}",
            "{\"role\":\"user\",\"content\":\"about <|tool_call_begin|> tokens\"}",
            "{\"role\":\"tool\",\"content\":\"<|im_start|>template<|im_end|>\",\"tool_call_id\":\"c1\"}",
        });

        var messages = new SessionStore(dir, dir).LoadPath(path);

        var assistant = messages.First(m => m.Role == "assistant");
        Assert.Equal("hello", assistant.Content);
        Assert.Equal(3, assistant.Stripped);                          //the count adds the persisted 2 and the new 1.
        Assert.Equal("about <|tool_call_begin|> tokens",
            messages.First(m => m.Role == "user").Content);           //the user record stays verbatim.
        Assert.Equal("<|im_start|>template<|im_end|>",
            messages.First(m => m.Role == "tool").Content);           //the tool record stays verbatim.
    }

    [Fact]
    public void All_pollution_no_calls_record_heals_to_empty_string_never_the_wedge_shape()
    {
        //healing must not produce an assistant record with null content and no tool calls, that shape is rejected on every replay
        using var doc = System.Text.Json.JsonDocument.Parse("{\"role\":\"assistant\",\"content\":\"<|tool_calls_section_end|>\"}");
        var m = SessionStore.ParseChatElement(doc.RootElement);
        Assert.Equal("", m.Content);                          //the healed content is the empty string, since null with no calls wedges every replay
        Assert.Null(m.ToolCalls);
        Assert.Equal(1, m.Stripped);

        //the sibling with tool calls heals to null content, the normal valid tool-call shape.
        using var doc2 = System.Text.Json.JsonDocument.Parse("{\"role\":\"assistant\",\"content\":\"<|tool_call_begin|>\"," +
            "\"tool_calls\":[{\"id\":\"c1\",\"name\":\"echo\",\"arguments\":\"{}\"}]}");
        var m2 = SessionStore.ParseChatElement(doc2.RootElement);
        Assert.Null(m2.Content);
        Assert.NotNull(m2.ToolCalls);
        Assert.Equal(1, m2.Stripped);
    }

    [Fact]
    public void Restore_heal_is_idempotent_across_a_second_resume()
    {
        //a healed message re-serialized and reloaded keeps its stripped count rather than doubling it
        using var doc1 = System.Text.Json.JsonDocument.Parse("{\"role\":\"assistant\",\"content\":\"<|tool_call_begin|>hi\"}");
        var once = SessionStore.ParseChatElement(doc1.RootElement);
        Assert.Equal(1, once.Stripped);

        using var doc2 = System.Text.Json.JsonDocument.Parse(SessionStore.ChatJson(once));
        var twice = SessionStore.ParseChatElement(doc2.RootElement);
        Assert.Equal("hi", twice.Content);
        Assert.Equal(1, twice.Stripped);                              //the count is conserved across the round trip
    }
}
