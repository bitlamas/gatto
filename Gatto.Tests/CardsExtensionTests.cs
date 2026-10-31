using System.Text;
using System.Text.Json;
using Gatto.Core.Commands;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Extensions;
using Gatto.Roles;

namespace Gatto.Tests;

//one extensions folder for the whole class, so the first load compiles and every later one runs the cached dll
public sealed class CardsScriptFolder : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("gatto-cards-ext-").FullName;

    public CardsScriptFolder() =>
        File.WriteAllText(System.IO.Path.Combine(Path, "cards.csx"), ShippedExtensions.Files["extensions/cards.csx"]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { }
    }
}

//the shipped cards script through the real host, with the home and the working folder in temp. roslyn compiles once per class
[Collection("e2e")]
public sealed class CardsExtensionTests(CardsScriptFolder script) : IClassFixture<RoslynWarmupFixture>, IClassFixture<CardsScriptFolder>, IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-cards-home-").FullName;
    private readonly string _cwd = Directory.CreateTempSubdirectory("gatto-cards-cwd-").FullName;
    private readonly List<string> _log = new();

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { }
        try { Directory.Delete(_cwd, recursive: true); } catch { }
    }

    private string RolesDir => Path.Combine(_home, "roles");
    private string RolePath(string name) => Path.Combine(RolesDir, name + ".json");

    private (LoadedExtension Loaded, CommandRegistry Commands) Load()
    {
        var (api, _, _) = ExtensionHost.BuildApi(_home, _cwd, () => null, () => null, _log.Add);
        var loaded = ExtensionHost.LoadAll(script.Path, api, _log.Add);
        Assert.Single(loaded);
        var commands = new CommandRegistry();
        ExtensionHost.Commit(loaded, new ToolRegistry(), new HookBus(), commands, ["/help"], _log.Add);
        Assert.Empty(_log);
        return (loaded[0], commands);
    }

    private async Task<string> Run(string arg)
    {
        Assert.True(Load().Commands.TryGet("/card", out var row));
        return Assert.IsType<CommandAnswer.Text>(await row.Handler(arg, CancellationToken.None)).Value;
    }

    private static void BigEndian(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 24)); s.WriteByte((byte)(v >> 16)); s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v);
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        BigEndian(s, data.Length);
        s.Write(Encoding.ASCII.GetBytes(type));
        s.Write(data);
        BigEndian(s, 0);   //the walker never checks the crc
    }

    private static byte[] Png(params (string Keyword, string Base64)[] chunks)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        Chunk(ms, "IHDR", new byte[13]);
        foreach (var (k, b) in chunks)
            Chunk(ms, "tEXt", [.. Encoding.Latin1.GetBytes(k), 0, .. Encoding.Latin1.GetBytes(b)]);
        Chunk(ms, "IEND", Array.Empty<byte>());
        return ms.ToArray();
    }

    private static string Card(string spec, object data) =>
        JsonSerializer.Serialize(new Dictionary<string, object> { ["spec"] = spec, ["spec_version"] = "2.0", ["data"] = data });

    private static string B64(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

    private static readonly Dictionary<string, object> SeraData = new()
    {
        ["name"] = "Sera Lin",
        ["description"] = "DESC",
        ["personality"] = "PERS",
        ["scenario"] = "SCEN",
        ["first_mes"] = "Hi, I am Sera.",
        ["mes_example"] = "EXAMPLE",
        ["system_prompt"] = "SYS",
    };

    private string WriteFile(string name, byte[] bytes)
    {
        var path = Path.Combine(_cwd, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private string SeraPng() => WriteFile("sera.png", Png(("chara", B64(Card("chara_card_v2", SeraData)))));

    [Fact]
    public async Task A_V2_PNG_WRITES_THE_ROLE_FROM_ITS_CHARA_CHUNK()
    {
        var output = await Run(SeraPng());

        var role = RoleFile.Load(RolesDir, "sera-lin");
        const string append = "SYS\n\nDESC\n\nPERS\n\nSCEN\n\nEXAMPLE";
        Assert.True(role.Raw);
        Assert.Equal(append, role.Append);
        Assert.Equal("Hi, I am Sera.", role.FirstMessage);
        Assert.Equal(
            [
                "wrote " + RolePath("sera-lin"),
                "append: system_prompt", "append: description", "append: personality", "append: scenario", "append: mes_example",
                $"append: {Encoding.UTF8.GetByteCount(append)} bytes",
                "first_message: first_mes",
                "dropped: the picture",
            ],
            output.Split('\n'));
    }

    [Fact]
    public async Task A_V3_PNG_WITH_BOTH_CHUNKS_READS_CCV3()
    {
        var path = WriteFile("both.png", Png(
            ("chara", B64(Card("chara_card_v2", new Dictionary<string, object> { ["name"] = "Old", ["description"] = "old" }))),
            ("ccv3", B64(Card("chara_card_v3", new Dictionary<string, object> { ["name"] = "New", ["description"] = "new" })))));

        await Run(path);

        Assert.True(File.Exists(RolePath("new")));
        Assert.False(File.Exists(RolePath("old")));
    }

    [Fact]
    public async Task A_JSON_CARD_WRITES_THE_SAME_FILE()
    {
        await Run(SeraPng());
        var fromPng = File.ReadAllBytes(RolePath("sera-lin"));
        File.Delete(RolePath("sera-lin"));

        var output = await Run(WriteFile("sera.json", Encoding.UTF8.GetBytes(Card("chara_card_v2", SeraData))));

        Assert.Equal(fromPng, File.ReadAllBytes(RolePath("sera-lin")));
        Assert.DoesNotContain("dropped: the picture", output);
    }

    [Fact]
    public async Task THE_NAME_RULE()
    {
        await Run(WriteFile("c.json", Encoding.UTF8.GetBytes(Card("chara_card_v2",
            new Dictionary<string, object> { ["name"] = "  Sera  Lin, the 2nd!  ", ["description"] = "d" }))));

        Assert.Equal(["sera--lin-the-2nd.json"], Directory.GetFiles(RolesDir).Select(Path.GetFileName));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_CARD_WITHOUT_A_NAME_IS_REFUSED(bool emptyName)
    {
        var data = new Dictionary<string, object> { ["description"] = "d" };
        if (emptyName) data["name"] = "";

        var output = await Run(WriteFile("c.json", Encoding.UTF8.GetBytes(Card("chara_card_v2", data))));

        Assert.Equal("the card has no name", output);
        Assert.False(Directory.Exists(RolesDir) && Directory.GetFiles(RolesDir).Length > 0);
    }

    [Fact]
    public async Task AN_EXISTING_ROLE_FILE_IS_REFUSED()
    {
        Directory.CreateDirectory(RolesDir);
        File.WriteAllText(RolePath("sera-lin"), "{ \"model\": \"mine\" }\n");
        var before = File.ReadAllBytes(RolePath("sera-lin"));

        var output = await Run(SeraPng());

        Assert.Equal(Path.Combine("roles", "sera-lin.json") + " exists, delete it first", output);
        Assert.Equal(before, File.ReadAllBytes(RolePath("sera-lin")));
    }

    [Fact]
    public async Task CONSTANT_LOREBOOK_ENTRIES_GO_IN_BY_INSERTION_ORDER_AND_THE_REST_ARE_COUNTED()
    {
        var data = new Dictionary<string, object>
        {
            ["name"] = "Lore",
            ["scenario"] = "SCEN",
            ["mes_example"] = "EXAMPLE",
            ["character_book"] = new Dictionary<string, object>
            {
                ["entries"] = new object[]
                {
                    new Dictionary<string, object> { ["constant"] = true, ["insertion_order"] = 2, ["content"] = "B" },
                    new Dictionary<string, object> { ["constant"] = true, ["insertion_order"] = 1, ["content"] = "A" },
                    new Dictionary<string, object> { ["constant"] = false, ["keys"] = new[] { "x" }, ["content"] = "K" },
                    new Dictionary<string, object> { ["constant"] = true, ["enabled"] = false, ["content"] = "D" },
                },
            },
        };

        var output = await Run(WriteFile("lore.json", Encoding.UTF8.GetBytes(Card("chara_card_v2", data))));

        Assert.Equal("SCEN\n\nA\n\nB\n\nEXAMPLE", RoleFile.Load(RolesDir, "lore").Append);
        var lines = output.Split('\n');
        Assert.Contains("append: 2 lorebook entries", lines);
        Assert.Contains("dropped: 1 lorebook entry that needs a keyword", lines);
        Assert.Contains("dropped: 1 disabled lorebook entry", lines);
    }

    [Fact]
    public async Task POST_HISTORY_AND_GREETINGS_ARE_DROPPED_BY_NAME()
    {
        var data = new Dictionary<string, object>
        {
            ["name"] = "Greet",
            ["description"] = "d",
            ["post_history_instructions"] = "POSTHIST",
            ["alternate_greetings"] = new[] { "ALT ONE", "ALT TWO" },
        };

        var output = await Run(WriteFile("g.json", Encoding.UTF8.GetBytes(Card("chara_card_v2", data))));

        var text = File.ReadAllText(RolePath("greet"));
        Assert.DoesNotContain("POSTHIST", text);
        Assert.DoesNotContain("ALT ONE", text);
        var lines = output.Split('\n');
        Assert.Contains("dropped: post_history_instructions", lines);
        Assert.Contains("dropped: 2 alternate greetings", lines);
    }

    [Fact]
    public async Task THE_PLACEHOLDERS_ARE_REPLACED()
    {
        var data = new Dictionary<string, object>
        {
            ["name"] = "Sera",
            ["description"] = "{{char}} helps {{USER}}.",
            ["first_mes"] = "I am {{Char}}, and {{user}} is welcome.",
        };

        await Run(WriteFile("p.json", Encoding.UTF8.GetBytes(Card("chara_card_v2", data))));

        var role = RoleFile.Load(RolesDir, "sera");
        Assert.Equal("Sera helps User.", role.Append);
        Assert.Equal("I am Sera, and User is welcome.", role.FirstMessage);
    }

    [Fact]
    public async Task A_FIRST_MES_HOLDING_A_CONTROL_TOKEN_IS_DROPPED_SO_THE_FILE_LOADS()
    {
        var data = new Dictionary<string, object>
        {
            ["name"] = "Leak",
            ["description"] = "d",
            ["first_mes"] = "hello <|tool_call_begin|> there",
        };

        var output = await Run(WriteFile("t.json", Encoding.UTF8.GetBytes(Card("chara_card_v2", data))));

        Assert.DoesNotContain("first_message", File.ReadAllText(RolePath("leak")));
        Assert.Contains("dropped: first_mes, it holds a control token", output.Split('\n'));
        Assert.Null(RoleFile.Load(RolesDir, "leak").FirstMessage);
    }

    [Fact]
    public async Task A_CARD_WITH_A_NAME_ALONE_WRITES_RAW_AND_SAYS_SO()
    {
        var output = await Run(WriteFile("n.json", Encoding.UTF8.GetBytes(Card("chara_card_v3",
            new Dictionary<string, object> { ["name"] = "Bare" }))));

        Assert.Equal("{\n  \"raw\": true\n}\n", File.ReadAllText(RolePath("bare")));
        Assert.Equal(["wrote " + RolePath("bare"), "raw only, the card has no text"], output.Split('\n'));
    }

    [Fact]
    public async Task A_FILE_THAT_IS_NOT_A_CARD_IS_REFUSED()
    {
        var png = WriteFile("plain.png", Png());
        Assert.Equal("no character card in " + png, await Run(png));

        var json = WriteFile("plain.json", Encoding.UTF8.GetBytes("""{"data":{"name":"x"}}"""));
        Assert.Equal(json + " is not a V2 or V3 character card", await Run(json));

        var text = WriteFile("plain.txt", Encoding.UTF8.GetBytes("hello"));
        Assert.Equal("not a PNG or a JSON file: " + text, await Run(text));

        Assert.False(Directory.Exists(RolesDir) && Directory.GetFiles(RolesDir).Length > 0);
    }

    [Fact]
    public async Task A_MISSING_PATH_AND_A_BARE_CARD_PRINT_THEIR_LINES()
    {
        var missing = Path.Combine(_cwd, "nowhere.png");
        Assert.Equal("no file at " + missing, await Run(missing));
        Assert.Equal("usage: /card <path>", await Run("  "));
    }

    [Fact]
    public async Task A_QUOTED_PATH_WITH_A_SPACE_IS_READ()
    {
        var dir = Path.Combine(_cwd, "my cards");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "card.json");
        File.WriteAllText(path, Card("chara_card_v2", SeraData));

        await Run("\"" + path + "\"");

        Assert.True(File.Exists(RolePath("sera-lin")));
    }

    [Fact]
    public async Task THE_WRITTEN_FILE_IS_LF_UTF8_WITHOUT_BOM_AND_READABLE()
    {
        await Run(WriteFile("jp.json", Encoding.UTF8.GetBytes(Card("chara_card_v2",
            new Dictionary<string, object> { ["name"] = "Kumo", ["description"] = "雲の上の猫\nふたつめ she doesn't <b>mind</b> R&D" }))));

        var bytes = File.ReadAllBytes(RolePath("kumo"));
        Assert.DoesNotContain((byte)'\r', bytes);
        Assert.False(bytes is [0xEF, 0xBB, 0xBF, ..]);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.Contains("雲の上の猫", text);
        Assert.Contains("she doesn't <b>mind</b> R&D", text);
        Assert.DoesNotContain("\\u", text);
        Assert.EndsWith("}\n", text);
    }

    [Fact]
    public async Task CCV3_WINS_WHEN_IT_COMES_FIRST_IN_THE_FILE()
    {
        var path = WriteFile("both.png", Png(
            ("ccv3", B64(Card("chara_card_v3", new Dictionary<string, object> { ["name"] = "New", ["description"] = "new" }))),
            ("chara", B64(Card("chara_card_v2", new Dictionary<string, object> { ["name"] = "Old", ["description"] = "old" })))));

        await Run(path);

        Assert.True(File.Exists(RolePath("new")));
        Assert.False(File.Exists(RolePath("old")));
    }

    [Fact]
    public async Task A_RELATIVE_PATH_IS_READ_AGAINST_THE_WORKING_FOLDER()
    {
        WriteFile("rel.json", Encoding.UTF8.GetBytes(Card("chara_card_v2", SeraData)));

        await Run("rel.json");

        Assert.True(File.Exists(RolePath("sera-lin")));
    }

    [Fact]
    public async Task AN_UNKNOWN_SPEC_IS_REFUSED()
    {
        var json = WriteFile("v1.json", Encoding.UTF8.GetBytes(Card("chara_card_v1", SeraData)));

        Assert.Equal(json + " is not a V2 or V3 character card", await Run(json));
    }

    [Fact]
    public async Task A_CHUNK_THAT_IS_NOT_BASE64_IS_REFUSED_WITH_THE_CARD_LINE()
    {
        var png = WriteFile("bad.png", Png(("chara", "not base64 at all!!")));

        Assert.Equal(png + " is not a V2 or V3 character card", await Run(png));
    }

    [Fact]
    public async Task THE_PLACEHOLDERS_ARE_REPLACED_IN_THE_LOREBOOK_AND_THE_EXAMPLES()
    {
        var data = new Dictionary<string, object>
        {
            ["name"] = "Sera",
            ["mes_example"] = "{{user}}: hi\n{{char}}: hello",
            ["character_book"] = new Dictionary<string, object>
            {
                ["entries"] = new object[] { new Dictionary<string, object> { ["constant"] = true, ["content"] = "{{char}} likes tea." } },
            },
        };

        await Run(WriteFile("p.json", Encoding.UTF8.GetBytes(Card("chara_card_v2", data))));

        Assert.Equal("Sera likes tea.\n\nUser: hi\nSera: hello", RoleFile.Load(RolesDir, "sera").Append);
    }

    [Fact]
    public async Task THE_SIZE_LINE_CARRIES_A_THOUSANDS_SEPARATOR()
    {
        var output = await Run(WriteFile("big.json", Encoding.UTF8.GetBytes(Card("chara_card_v2",
            new Dictionary<string, object> { ["name"] = "Big", ["description"] = new string('x', 1234) }))));

        Assert.Contains("append: 1,234 bytes", output.Split('\n'));
    }

    [Fact]
    public void THE_SCRIPT_REGISTERS_CARD_AND_NOTHING_ELSE()
    {
        var staged = Load().Loaded.Registrations;

        Assert.Equal(["/card"], staged.Commands.Select(c => c.Name));
        Assert.Empty(staged.Tools);
        Assert.Empty(staged.Hooks);
        Assert.Null(staged.Policy);
        Assert.Empty(staged.Endpoints);
    }
}
