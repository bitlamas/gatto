using System.Text;
using System.Text.Json;
using Gatto.Core.Acquire;

namespace Gatto.Tests;

//the register that ships with gatto: its battery is the reader's, every row holds the bar from its own numbers, and the lookup goes by sha256 alone
public class ShippedMarksTests
{
    private static JsonDocument Shipped()
    {
        using var s = typeof(ShippedMarks).Assembly.GetManifestResourceStream("Gatto.Core.Acquire.tool-use-marks.json")!;
        return JsonDocument.Parse(s);
    }

    //a battery bump without a reseed fails here rather than shipping a register no row of which can match
    [Fact]
    public void THE_SHIPPED_REGISTER_IS_ON_THE_CURRENT_BATTERY()
    {
        using var doc = Shipped();
        Assert.Equal(1, doc.RootElement.GetProperty("schema").GetInt32());
        Assert.Equal(BadgeRegister.Battery, doc.RootElement.GetProperty("battery").GetInt32());
        Assert.NotEmpty(doc.RootElement.GetProperty("rows").EnumerateArray());
    }

    //the raw rows, since the reader keeps no task counts and could not answer this
    [Fact]
    public void EVERY_SHIPPED_ROW_HOLDS_THE_BAR_FROM_ITS_OWN_NUMBERS()
    {
        using var doc = Shipped();
        foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
        {
            var file = row.GetProperty("file").GetString();
            var runs = row.GetProperty("runs").GetInt32();
            var passed = row.GetProperty("passed").GetInt32();
            Assert.True(runs >= 20, $"{file}: {runs} runs");
            Assert.True(passed * 10 >= runs * 9, $"{file}: {passed} of {runs}");
            Assert.Equal("defaults", row.GetProperty("sampling_note").GetString());
            Assert.Matches("^[0-9a-f]{64}$", row.GetProperty("sha256").GetString()!);

            var named = row.GetProperty("tasks").EnumerateObject()
                .Where(t => 2 * t.Value.GetInt32() < runs).Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal);
            var fellShort = row.GetProperty("fell_short").EnumerateArray().Select(x => x.GetString()!).OrderBy(n => n, StringComparer.Ordinal);
            Assert.Equal(named, fellShort);
        }
    }

    //every shipped row is reachable through the lookup, so the reader and the file agree on the key
    [Fact]
    public void EVERY_SHIPPED_ROW_IS_FOUND_BY_ITS_SHA256()
    {
        using var doc = Shipped();
        foreach (var row in doc.RootElement.GetProperty("rows").EnumerateArray())
        {
            var mark = ShippedMarks.Lookup(row.GetProperty("sha256").GetString());
            Assert.NotNull(mark);
            Assert.Equal(row.GetProperty("file").GetString(), mark.File);
        }
        Assert.Null(ShippedMarks.Lookup(null));
        Assert.Null(ShippedMarks.Lookup(new string('0', 64)));
    }

    private static Stream Fixture(int battery) => new MemoryStream(Encoding.UTF8.GetBytes($$"""
        {
          "schema": 1,
          "battery": {{battery}},
          "rows": [
            { "sha256": "{{new string('a', 64)}}", "file": "planted-Q4_K_M.gguf", "repo_id": null,
              "runs": 20, "passed": 19, "tasks": { "B4": 3 }, "fell_short": ["B4"] }
          ]
        }
        """));

    [Fact]
    public void A_PLANTED_ROW_IS_FOUND_AND_AN_UNKNOWN_HASH_IS_NOT()
    {
        var rows = ShippedMarks.Parse(Fixture(BadgeRegister.Battery));
        var mark = rows[new string('a', 64)];
        Assert.Equal("planted-Q4_K_M.gguf", mark.File);
        Assert.Null(mark.RepoId);
        Assert.Equal(["B4"], mark.FellShort);
        Assert.False(rows.ContainsKey(new string('b', 64)));
    }

    //a register from another battery reads as untested for every row, whatever its rows say
    [Fact]
    public void A_REGISTER_ON_ANOTHER_BATTERY_HAS_NO_ROW()
    {
        Assert.Empty(ShippedMarks.Parse(Fixture(BadgeRegister.Battery + 1)));
    }
}
