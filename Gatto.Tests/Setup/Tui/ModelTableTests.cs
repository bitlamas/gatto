using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the model table as the landing page draws it: model, params with its sort mark, size and quant, kind, and runs on a discrete card only
public class ModelTableTests
{
    private static ModelRow R(string model, long billions, FitRegime fit, string kind = "dense", double gb = 5.0) =>
        ShelfRows.Of(RepoId: "unsloth/" + model + "-GGUF", Publisher: "unsloth",
            PickedQuant: new HubQuant(model + "-Q4_K_M.gguf", (long)(gb * 1_073_741_824), null),
            Fit: fit, NativeCtx: 262144, Params: billions * 1_000_000_000, Structure: kind, Model: model);

    private static ShelfView View(MachineShape shape, bool? smallestFirst = null, bool lift = false, params ModelRow[] rows) =>
        new(rows, shape, Lift: lift, Families: Families.Load().Ladder, Lit: new HashSet<string>(["gemma", "qwen"]),
            SmallestFirst: smallestFirst);

    private static IReadOnlyList<string> Table(ShelfView v) =>
        [.. Shelf.Table(v, 0, focused: true, GlyphSet.Unicode).Select(r => r.Text.TrimEnd())];

    private static readonly ModelRow[] Vega =
    [
        R("Qwen3.5-9B", 9, FitRegime.FitsGpu),
        R("gemma-4-E4B-it", 8, FitRegime.FitsGpu),
        R("gemma-4-26B-A4B-it", 25, FitRegime.FitsRamOnly, "MoE A4B", 12.7),
    ];

    [Fact]
    public void THE_FIVE_COLUMNS_ON_A_DISCRETE_CARD()
    {
        var header = Table(View(MachineShape.Discrete, rows: Vega))[0];
        var at = new[] { "model", "params ▾", "size · quant", "kind", "runs" }.Select(h => header.IndexOf(h, StringComparison.Ordinal)).ToList();
        Assert.All(at, i => Assert.True(i >= 0, header));
        Assert.Equal(at.Order(), at);
    }

    [Theory]
    [InlineData((int)MachineShape.UnifiedWithShare)]
    [InlineData((int)MachineShape.CpuOnly)]
    public void NO_RUNS_COLUMN_WITHOUT_A_DISCRETE_CARD(int shape) =>
        Assert.DoesNotContain("runs", Table(View((MachineShape)shape, rows: Vega))[0], StringComparison.Ordinal);

    [Fact]
    public void NO_ROW_NUMBERS()
    {
        var rows = Table(View(MachineShape.Discrete, rows: Vega));
        Assert.DoesNotContain(rows, r => System.Text.RegularExpressions.Regex.IsMatch(r, @"^\S?\s*\d\. "));
    }

    [Fact]
    public void THE_NAME_IS_THE_MODEL_KEY()
    {
        var rows = string.Join("\n", Table(View(MachineShape.Discrete, rows: Vega)));
        Assert.Contains("Qwen3.5-9B ", rows, StringComparison.Ordinal);
        Assert.DoesNotContain("-GGUF", rows, StringComparison.Ordinal);
        Assert.DoesNotContain("unsloth/", rows, StringComparison.Ordinal);
    }

    //the group headings show only when both groups have rows, and never on a lifted, flat list
    [Fact]
    public void THE_HEADINGS_ONLY_WHEN_BOTH_GROUPS_HAVE_ROWS()
    {
        var both = Table(View(MachineShape.Discrete, rows: Vega)).ToList();
        Assert.Contains("  on the graphics card", both);
        Assert.Contains("  in system memory, fastest first", both);
        Assert.True(both.IndexOf("  on the graphics card") < both.IndexOf("  in system memory, fastest first"));

        var card = Table(View(MachineShape.Discrete, rows: [Vega[0], Vega[1]]));
        Assert.DoesNotContain(card, r => r.Contains("on the graphics card", StringComparison.Ordinal));

        var unified = Table(View(MachineShape.UnifiedWithShare, rows: Vega));
        Assert.DoesNotContain(unified, r => r.Contains("fastest first", StringComparison.Ordinal));

        var lifted = Table(View(MachineShape.Discrete, lift: true, rows: Vega));
        Assert.DoesNotContain(lifted, r => r.Contains("on the graphics card", StringComparison.Ordinal));
    }

    [Fact]
    public void THE_SORT_MARK_FLIPS_WITH_THE_SORT()
    {
        Assert.Contains("params ▾", Table(View(MachineShape.Discrete, rows: Vega))[0], StringComparison.Ordinal);
        Assert.Contains("params ▾", Table(View(MachineShape.Discrete, smallestFirst: false, rows: Vega))[0], StringComparison.Ordinal);
        Assert.Contains("params ▴", Table(View(MachineShape.Discrete, smallestFirst: true, rows: Vega))[0], StringComparison.Ordinal);
    }

    //a params value ends where its header's mark ends, so the column reads as one
    [Fact]
    public void THE_PARAMS_VALUE_ENDS_UNDER_THE_MARK()
    {
        var rows = Table(View(MachineShape.Discrete, rows: Vega));
        var end = rows[0].IndexOf("params ▾", StringComparison.Ordinal) + "params ▾".Length;
        var row = rows.First(r => r.Contains("Qwen3.5-9B", StringComparison.Ordinal));
        Assert.Equal(end, row.IndexOf("9.0B  5.0 GB", StringComparison.Ordinal) + 4);
    }

    //the footer's right end: the marks on a discrete card, the sentence for the column elsewhere
    [Fact]
    public void THE_FOOTER_RIGHT_END_PER_MACHINE()
    {
        Assert.Equal("✓ GPU · ⚠ RAM", FitMarks.LegendFor(MachineShape.Discrete, GlyphSet.Unicode).Text);
        Assert.Equal("fewer params = faster", FitMarks.LegendFor(MachineShape.UnifiedWithShare, GlyphSet.Unicode).Text);
        Assert.Equal("fewer params = faster", FitMarks.LegendFor(MachineShape.CpuOnly, GlyphSet.Unicode).Text);
    }

    //a Hub row carries no badge, and a local row keeps today's
    [Fact]
    public void NO_BADGE_ON_A_HUB_ROW_AND_TODAYS_ON_A_LOCAL_ROW()
    {
        var badge = new Badge("m", new DateOnly(2026, 8, 1), "v", "greedy", Passed: 5, Ran: 5);
        var local = R("local", 9, FitRegime.FitsGpu) with { Badge = badge };
        var marks = ShelfTable.Render([local], new Theme(TermCaps.Plain), 120, MachineShape.Discrete, GlyphSet.Unicode);
        Assert.Contains(marks, r => r.Text.Contains(GlyphSet.Unicode.Ok, StringComparison.Ordinal));
        var hub = ShelfTable.Render([R("hub", 9, FitRegime.FitsGpu)], new Theme(TermCaps.Plain), 120, MachineShape.Discrete, GlyphSet.Unicode);
        Assert.DoesNotContain(hub, r => r.Text.Contains(GlyphSet.Unicode.Ok, StringComparison.Ordinal));
    }
}
