using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Gatto.Core.Home;

namespace Gatto.Tests;

public class GattoConfigWriterTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "gatto-cfgw-" + Guid.NewGuid().ToString("N"));

    public GattoConfigWriterTests() => Directory.CreateDirectory(_home);
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private string Write(string json)
    {
        var p = Path.Combine(_home, "gatto.json");
        File.WriteAllText(p, json);
        return p;
    }

    //these mutations share the extracted Mutate() core. the untouched tests above guard it, so their staying green proves the model writer kept its behavior

    [Fact]
    public void UpsertEndpoint_creates_an_endpoint_that_did_not_exist()
    {
        Write("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local"}""");

        GattoConfigWriter.UpsertEndpoint(_home, "server", "http://127.0.0.1:8080", 8192);

        var ep = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json")))
            .RootElement.GetProperty("endpoints").GetProperty("server");
        Assert.Equal("http://127.0.0.1:8080", ep.GetProperty("base_url").GetString());
        Assert.Equal(8192, ep.GetProperty("context").GetInt32());
    }

    [Fact]
    public void UpsertEndpoint_updates_in_place_and_leaves_unknown_sibling_keys_intact()
    {
        //the config file legitimately holds keys this writer does not own. a typed record round-trip would drop them, so the edit stays surgical
        Write("""
            {"endpoints":{"server":{"base_url":"http://old","context":1024,"key_env":"MY_KEY","thinking":{"low":{}}}},
             "default_endpoint":"server","regions":"accepted-and-ignored"}
            """);

        GattoConfigWriter.UpsertEndpoint(_home, "server", "http://new", 4096);

        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json"))).RootElement;
        var ep = root.GetProperty("endpoints").GetProperty("server");
        Assert.Equal("http://new", ep.GetProperty("base_url").GetString());
        Assert.Equal(4096, ep.GetProperty("context").GetInt32());
        Assert.Equal("MY_KEY", ep.GetProperty("key_env").GetString());
        Assert.True(ep.TryGetProperty("thinking", out _));
        Assert.Equal("accepted-and-ignored", root.GetProperty("regions").GetString());
    }

    [Fact]
    public void UpsertEndpoint_never_writes_a_model_or_a_llama_server_for_the_connect_path()
    {
        //the model and llama_server keys belong to the local branch only. a connect-path endpoint that grew one would undo the split between the two paths
        Write("""{"endpoints":{},"default_endpoint":"local"}""");

        GattoConfigWriter.UpsertEndpoint(_home, "server", "http://127.0.0.1:8080", 8192);

        var ep = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json")))
            .RootElement.GetProperty("endpoints").GetProperty("server");
        Assert.Equal(2, ep.EnumerateObject().Count());
        Assert.False(ep.TryGetProperty("llama_server", out _));
        Assert.False(ep.TryGetProperty("model", out _));
    }

    [Fact]
    public void UpsertEndpoint_refuses_a_nonsense_context()
    {
        //test with -1, since 0 reads as unset as easily as invalid. this guard is a backstop only, and the primary check is the fit arithmetic upstream
        Write("""{"endpoints":{},"default_endpoint":"local"}""");
        Assert.Throws<GattoConfigException>(
            () => GattoConfigWriter.UpsertEndpoint(_home, "server", "http://x", -1));
    }

    [Fact]
    public void SetConsentKey_round_trips_both_answers_as_a_visible_key()
    {
        //a remembered consent answer is a visible, revocable config key and never hidden state.
        Write("""{"endpoints":{},"default_endpoint":"local"}""");

        GattoConfigWriter.SetConsentKey(_home, "update_check", true);
        Assert.True(JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json")))
            .RootElement.GetProperty("update_check").GetBoolean());

        GattoConfigWriter.SetConsentKey(_home, "update_check", false);
        Assert.False(JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json")))
            .RootElement.GetProperty("update_check").GetBoolean());
    }

    [Fact]
    public void The_new_mutations_refuse_a_missing_home_the_same_way_the_old_one_does()
    {
        var empty = Path.Combine(_home, "nothing-here");
        Directory.CreateDirectory(empty);
        Assert.Throws<GattoConfigException>(() => GattoConfigWriter.UpsertEndpoint(empty, "s", "http://x", 1));
        Assert.Throws<GattoConfigException>(() => GattoConfigWriter.SetConsentKey(empty, "k", true));
    }

    [Fact]
    public void SetEndpointDefaultModel_ReplacesTheLegacyKeysValue()
    {
        Write("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local","default_model":"qwen3.6-35b"}""");
        GattoConfigWriter.SetEndpointDefaultModel(_home, "local", "gemma-4-26b");
        var root = Root();
        Assert.Equal("gemma-4-26b", root.GetProperty("defaults").GetProperty("local").GetProperty("model").GetString());
        Assert.False(root.TryGetProperty("default_model", out _));
    }

    [Fact]
    public void SetEndpointDefaultModel_AddsTheEntryWhenAbsent()
    {
        Write("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local"}""");
        GattoConfigWriter.SetEndpointDefaultModel(_home, "local", "gemma-4-26b");
        Assert.Equal("gemma-4-26b", Root().GetProperty("defaults").GetProperty("local").GetProperty("model").GetString());
    }

    private JsonElement Root() => JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json"))).RootElement;

    //the regression from the cloud gate: a write for one endpoint must leave every other entry and the legacy key's meaning alone
    [Fact]
    public void SetEndpointDefaultModel_WritesOnlyThatEndpointsEntry()
    {
        Write("""{"endpoints":{"local":{}},"default_endpoint":"local","defaults":{"local":{"model":"q"},"other":{"model":"o","effort":{"o":"low"}}}}""");
        GattoConfigWriter.SetEndpointDefaultModel(_home, "acme", "acme-max");

        var d = Root().GetProperty("defaults");
        Assert.Equal("acme-max", d.GetProperty("acme").GetProperty("model").GetString());
        Assert.Equal("q", d.GetProperty("local").GetProperty("model").GetString());
        var other = d.GetProperty("other");
        Assert.Equal("o", other.GetProperty("model").GetString());
        Assert.Equal("low", other.GetProperty("effort").GetProperty("o").GetString());
        Assert.Equal(2, other.EnumerateObject().Count());
    }

    [Fact]
    public void Any_write_moves_the_legacy_key_into_the_endpoint_it_belonged_to()
    {
        Write("""{"endpoints":{"local":{},"r":{"base_url":"https://r.test"}},"default_endpoint":"local","default_model":"q"}""");

        GattoConfigWriter.SetDefaultEndpoint(_home, "r");

        var root = Root();
        Assert.False(root.TryGetProperty("default_model", out _));
        Assert.Equal("q", root.GetProperty("defaults").GetProperty("local").GetProperty("model").GetString());
        Assert.Equal("r", root.GetProperty("default_endpoint").GetString());
    }

    [Fact]
    public void SetEndpointEffort_KeepsTheEntrysModelAndOtherLevels()
    {
        Write("""{"endpoints":{"local":{}},"default_endpoint":"local","defaults":{"acme":{"model":"acme-max","effort":{"acme-lite":"medium"}}}}""");

        GattoConfigWriter.SetEndpointEffort(_home, "acme", "acme-max", "high");

        var acme = Root().GetProperty("defaults").GetProperty("acme");
        Assert.Equal("acme-max", acme.GetProperty("model").GetString());
        Assert.Equal("high", acme.GetProperty("effort").GetProperty("acme-max").GetString());
        Assert.Equal("medium", acme.GetProperty("effort").GetProperty("acme-lite").GetString());
    }

    [Fact]
    public void A_write_on_a_file_whose_two_keys_disagree_refuses_and_leaves_it()
    {
        var text = """{"endpoints":{"local":{}},"default_endpoint":"local","default_model":"q","defaults":{"local":{"model":"r"}}}""";
        Write(text);

        Assert.Throws<GattoConfigException>(() => GattoConfigWriter.SetLlamaServer(_home, "C:\\x.exe"));
        Assert.Equal(text, File.ReadAllText(Path.Combine(_home, "gatto.json")));
    }

    [Fact]
    public void SetDefaultEndpoint_ReplacesTheKey()
    {
        Write("""
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"},"server":{"base_url":"http://host:9","context":8192}},
             "default_endpoint":"local"}
            """);

        GattoConfigWriter.SetDefaultEndpoint(_home, "server");

        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json"))).RootElement;
        Assert.Equal("server", root.GetProperty("default_endpoint").GetString());
        //the key names an endpoint and does not define one, so the endpoints map must stay untouched.
        Assert.Equal(2, root.GetProperty("endpoints").EnumerateObject().Count());
    }

    [Fact]
    public void SetDefaultEndpoint_AddsTheKeyWhenAbsent()
    {
        Write("""{"endpoints":{"server":{"base_url":"http://host:9","context":8192}}}""");

        GattoConfigWriter.SetDefaultEndpoint(_home, "server");

        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json"))).RootElement;
        Assert.Equal("server", root.GetProperty("default_endpoint").GetString());
    }

    [Fact]
    public void SetDefaultEndpoint_RefusesAnEmptyName()
    {
        //an empty name would write a config whose reader throws at the next launch. the writer fails instead, where the message can still say what went wrong.
        Write("""{"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local"}""");

        Assert.Throws<GattoConfigException>(() => GattoConfigWriter.SetDefaultEndpoint(_home, "  "));

        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json"))).RootElement;
        Assert.Equal("local", root.GetProperty("default_endpoint").GetString());
    }

    //keys the writer does not own must survive, including the providers order. dropping or reordering any of it quietly kills the provider fallback
    [Fact]
    public void SetEndpointDefaultModel_PreservesKeysItDoesNotOwn()
    {
        Write("""
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},
             "default_endpoint":"local","default_model":"qwen3.6-35b",
             "regions":"on",
             "search":{"providers":["ddg","tavily"],"tavily":{"apiKey":"dev-not-a-real-key"}}}
            """);
        GattoConfigWriter.SetEndpointDefaultModel(_home, "local", "gemma-4-26b");

        var root = JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json"))).RootElement;
        Assert.Equal("gemma-4-26b", root.GetProperty("defaults").GetProperty("local").GetProperty("model").GetString());
        Assert.Equal("on", root.GetProperty("regions").GetString());

        var search = root.GetProperty("search");
        Assert.Equal(
            new[] { "ddg", "tavily" },
            search.GetProperty("providers").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal("dev-not-a-real-key", search.GetProperty("tavily").GetProperty("apiKey").GetString());
    }

    [Fact]
    public void SetEndpointDefaultModel_LeavesNoTempFileBehind()
    {
        Write("""{"endpoints":{},"default_endpoint":"local"}""");
        GattoConfigWriter.SetEndpointDefaultModel(_home, "local", "gemma-4-26b");
        Assert.Empty(Directory.GetFiles(_home, "*.tmp"));
    }

    [Fact]
    public void SetEndpointDefaultModel_MissingFile_ThrowsFriendly()
    {
        var ex = Assert.Throws<GattoConfigException>(() => GattoConfigWriter.SetEndpointDefaultModel(_home, "local", "x"));
        Assert.Contains("gatto.json", ex.Message);
    }

    [Fact]
    public void SetEndpointDefaultModel_CorruptJson_ThrowsFriendlyAndLeavesFileIntact()
    {
        var p = Write("{not json");
        Assert.Throws<GattoConfigException>(() => GattoConfigWriter.SetEndpointDefaultModel(_home, "local", "x"));
        Assert.Equal("{not json", File.ReadAllText(p));   //the file keeps its exact original bytes
    }

    //non-ASCII text must be written literally, since the config is a hand-edited file the user reads directly
    [Fact]
    public void SetEndpointDefaultModel_PreservesNonAsciiPathsLiterally()
    {
        var p = Path.Combine(_home, "gatto.json");
        //write with a UTF-8 BOM so the CJK literals survive the round-trip.
        File.WriteAllText(p, """
            {"endpoints":{"local":{"base_url":"http://127.0.0.1:1235"}},"default_endpoint":"local",
             "llama_server":"C:\\Users\\user\\terra 月球\\llama.cpp\\llama-server.exe"}
            """, new System.Text.UTF8Encoding(true));

        GattoConfigWriter.SetEndpointDefaultModel(_home, "local", "gemma-4-26b");

        var raw = File.ReadAllText(p);
        Assert.Contains("月球", raw);
        Assert.DoesNotContain("\\u5730", raw); //the escape sequence must not appear in the raw file text either.

        var root = JsonDocument.Parse(raw).RootElement;
        Assert.Contains("月球", root.GetProperty("llama_server").GetString());
    }

    //replace must keep explicit ACL entries, the config can hold a hardened secret. the fixture sets ALLOW, since DENY would block the test process
    [Fact]
    public void SetEndpointDefaultModel_PreservesExplicitAclAcrossReplace()
    {
        if (!OperatingSystem.IsWindows()) return;

        var p = Write("""{"endpoints":{},"default_endpoint":"local","default_model":"orig"}""");

        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var fileInfo = new FileInfo(p);
        var acl = fileInfo.GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Read, AccessControlType.Allow));
        fileInfo.SetAccessControl(acl);

        //assert the ACE is in place first, so a later failure means the writer dropped it
        Assert.True(HasExplicitEveryoneAllow(p), "test setup did not actually apply the explicit ACE");

        GattoConfigWriter.SetEndpointDefaultModel(_home, "local", "gemma-4-26b");

        Assert.True(HasExplicitEveryoneAllow(p), "explicit ACE on gatto.json must survive SetEndpointDefaultModel");

    }

    //keep this a method, CA1416 ignores the attribute on a local function. annotate rather than suppress, a blanket suppression hides the next call
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool HasExplicitEveryoneAllow(string path)
    {
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var acl = new FileInfo(path).GetAccessControl();
        var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier));
        return rules.Cast<FileSystemAccessRule>().Any(r =>
            r.IdentityReference == everyone &&
            r.AccessControlType == AccessControlType.Allow &&
            (r.FileSystemRights & FileSystemRights.Read) == FileSystemRights.Read);
    }

    //opening with FileShare.Read lets the read succeed and denies the move. the write phase fails, leaving the original intact and no temp file
    [Fact]
    public void SetEndpointDefaultModel_WriteFailure_LeavesNoTempFileAndOriginalIntact()
    {
        const string original = """{"endpoints":{},"default_endpoint":"local","default_model":"orig"}""";
        var p = Write(original);

        using (new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<GattoConfigException>(() => GattoConfigWriter.SetEndpointDefaultModel(_home, "local", "new-model"));
        }

        Assert.Empty(Directory.GetFiles(_home, "*.tmp"));
        Assert.Equal(original, File.ReadAllText(p));
    }
}
