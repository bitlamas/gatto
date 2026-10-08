using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Hardware;
using Gatto.Core.Models;

namespace Gatto.Tests;

//derive the structure column from the header's general.size_label, since the repo name is marketing text and the label is what the converter measured
public class ModelStructureTests
{
    private static GgufHeader Parse(byte[] bytes) =>
        GgufHeaderParser.Parse(new MemoryStream(bytes));

    [Fact]
    public void AN_MOE_WITH_AN_A_NUMBER_LABEL_READS_MOE_A4B()
    {
        var h = Parse(GgufTestBytes.WithStructure(
            expertCount: 128, expertUsed: 8, sizeLabel: "26B-A4B"));

        Assert.Equal("MoE A4B", ModelStructure.Cell(h));
        Assert.Equal((128L, 8L), ModelStructure.Experts(h));
    }

    //a label like 512x2.5B is a product, so the cell shows bare MoE when no honest active size exists
    [Fact]
    public void A_PRODUCT_LABEL_GIVES_BARE_MOE()
    {
        var h = Parse(GgufTestBytes.WithStructure(
            expertCount: 512, expertUsed: 10, sizeLabel: "512x2.5B"));

        Assert.Equal("MoE", ModelStructure.Cell(h));
        //the experts line still renders. an absent active size does not hide the expert counts.
        Assert.Equal((512L, 10L), ModelStructure.Experts(h));
    }

    [Fact]
    public void A_MODEL_WITH_NO_EXPERT_KEYS_READS_DENSE()
    {
        var h = Parse(GgufTestBytes.WithStructure(sizeLabel: "27B"));

        Assert.Equal("dense", ModelStructure.Cell(h));
        Assert.Null(ModelStructure.Experts(h));
    }

    //a stated expert count of zero or one means dense. an moe with one expert is a structure no model has.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void AN_EXPLICIT_ZERO_OR_ONE_EXPERT_COUNT_IS_DENSE(int experts)
    {
        //setting fitTerms to false is intentional, since a stated count answers on its own and needs no evidence of a complete read
        var h = Parse(GgufTestBytes.WithStructure(expertCount: experts, fitTerms: false));

        Assert.Equal("dense", ModelStructure.Cell(h));
        Assert.Null(ModelStructure.Experts(h));
    }

    //a truncated read must answer nothing, since reading a missing key as dense mislabels every moe
    [Fact]
    public void A_TRUNCATED_READ_SAYS_NOTHING_RATHER_THAN_DENSE()
    {
        //the fit terms sit behind 64 kb of padding, so the 16 kb window holds the architecture and nothing else.
        var whole = GgufTestBytes.RichWithLeadingPadding(64 * 1024);
        var cut = whole[..(16 * 1024)];

        var partial = Parse(cut);
        var full = Parse(whole);

        Assert.Equal(GgufOutcome.Truncated, partial.Outcome);
        Assert.Equal("qwen3", partial.Architecture);     //the read did capture the architecture.
        Assert.Null(ModelStructure.Cell(partial));
        Assert.False(ModelStructure.Answered(partial));

        Assert.Equal("dense", ModelStructure.Cell(full));
        Assert.True(ModelStructure.Answered(full));
    }

    //an moe answers from a genuinely truncated read, since the expert keys come early. assert the truncation, so the fixture is a real prefix
    [Fact]
    public void AN_MOE_ANSWERS_FROM_A_TRUNCATED_PREFIX()
    {
        //the expert keys precede the fit terms, so cutting the tail falls after the structure answer.
        var whole = GgufTestBytes.WithStructure(
            expertCount: 128, expertUsed: 8, sizeLabel: "26B-A4B");
        var h = Parse(whole[..^30]);

        Assert.Equal(GgufOutcome.Truncated, h.Outcome);   //this proves the fixture really is truncated.
        Assert.True(ModelStructure.Answered(h));
        Assert.Equal("MoE A4B", ModelStructure.Cell(h));
    }

    //general.size_label is uploader text, so the cell takes only an anchored A-number. its digits are capped, and an escape or a newline would break the frame.
    [Theory]
    [InlineData("26B-A4B\u001b[31m")]                     //an escape sequence follows the number.
    [InlineData("26B-A4B\nrow two")]                      //a newline would turn one cell into two rows.
    [InlineData("26B-A99999B")]                           //five digits exceed the bound.
    [InlineData("26B-A4B extra")]                         //a word follows the number.
    [InlineData("26B-\u001b[31mA4B")]                  //an escape precedes the number.
    [InlineData("26B-Aircraft")]                          //a word follows the A, where a digit belongs
    [InlineData("")]
    public void AN_UNTRUSTED_SIZE_LABEL_NEVER_REACHES_THE_CELL(string label)
    {
        var h = Parse(GgufTestBytes.WithStructure(
            expertCount: 128, expertUsed: 8, sizeLabel: label));

        //the label contributes nothing and is never echoed, so the cell falls back to bare MoE
        Assert.Equal("MoE", ModelStructure.Cell(h));
    }

    //the known match for the guard above, so a guard that rejects everything cannot pass every row
    [Theory]
    [InlineData("26B-A4B", "MoE A4B")]
    [InlineData("235B-A22B", "MoE A22B")]
    [InlineData("30B-A1.5B", "MoE A1.5B")]
    [InlineData("100B-A500M", "MoE A500M")]
    public void A_WELL_SHAPED_LABEL_DOES_REACH_THE_CELL(string label, string expected)
    {
        var h = Parse(GgufTestBytes.WithStructure(expertCount: 128, sizeLabel: label));

        Assert.Equal(expected, ModelStructure.Cell(h));
    }

    //these rows pin the mapping measured from four real ggufs, so no test needs a weights folder. a read that stops early belongs to RangedStructureReadTests
    [Theory]
    [InlineData("lfm2", null, null, "1.2B", "dense")]                     //measured from LFM2.5-1.2B-Thinking
    [InlineData("qwen35moe", 256, 8, "35B-A3B", "MoE A3B")]               //measured from Qwen3.6-35B-A3B
    [InlineData("gemma4", 128, 8, "26B-A4B", "MoE A4B")]                  //measured from gemma-4-26B-A4B-it
    [InlineData("mistral4", 128, 4, "119B", "MoE")]                       //measured from Mistral-Small-4-119B
    public void THE_FOUR_MODELS_ON_THIS_MACHINE_READ_AS_MEASURED(
        string arch, int? experts, int? active, string sizeLabel, string expected)
    {
        var h = Parse(GgufTestBytes.WithStructure(
            expertCount: experts, expertUsed: active, sizeLabel: sizeLabel, arch: arch));

        Assert.Equal(expected, ModelStructure.Cell(h));
    }

    //the ratio needs both halves. a total without an active count is not a ratio, so the pane renders nothing.
    [Fact]
    public void THE_EXPERTS_RATIO_NEEDS_BOTH_HALVES()
    {
        Assert.Null(ModelStructure.Experts(Parse(GgufTestBytes.WithStructure(expertCount: 128))));
        Assert.Null(ModelStructure.Experts(Parse(GgufTestBytes.WithStructure(expertUsed: 8))));
        Assert.NotNull(ModelStructure.Experts(
            Parse(GgufTestBytes.WithStructure(expertCount: 128, expertUsed: 8))));
    }

    //the rendered ratio must be bounded, because nothing downstream bounds it. a wild or inverted count prints an absurd sentence about the model.
    [Theory]
    [InlineData(50_000, 8)]         //the total is past the rendering ceiling.
    [InlineData(128, 900)]          //the active count exceeds the total.
    public void AN_IMPLAUSIBLE_EXPERT_COUNT_RENDERS_NOTHING(int total, int active)
    {
        var h = Parse(GgufTestBytes.WithStructure(expertCount: total, expertUsed: active));

        Assert.Null(ModelStructure.Experts(h));
        //the cell still reads MoE, since many experts is credible. only the ratio is refused.
        Assert.Equal("MoE", ModelStructure.Cell(h));
    }

    //the known match for the bound above, since refusing every ratio would pass without a case that renders
    [Theory]
    [InlineData(10_000, 8)]
    [InlineData(128, 128)]
    public void A_PLAUSIBLE_EXPERT_COUNT_STILL_RENDERS(int total, int active)
    {
        var h = Parse(GgufTestBytes.WithStructure(expertCount: total, expertUsed: active));

        Assert.Equal(((long)total, (long)active), ModelStructure.Experts(h));
    }

    //assert on the line the shipped pane renders. checking both sides with the same pattern only proves the code agrees with itself.
    [Fact]
    public void THE_COMPOSED_CELL_IS_READ_BACK_BY_THE_PANE()
    {
        var h = Parse(GgufTestBytes.WithStructure(
            expertCount: 128, expertUsed: 8, sizeLabel: "26B-A4B"));
        var facts = new ModelFacts(Structure: ModelStructure.Cell(h), Experts: ModelStructure.Experts(h));

        var text = string.Join("\n", Pane
            .Rows(Row(), facts, MachineShape.Discrete, 40)
            .Select(r => r.Text.TrimEnd()));

        //the pane drops the A when it reads the number back, so the cell says MoE A4B and the line says reads 4B/token
        Assert.Contains("MoE A4B", facts.Structure!, StringComparison.Ordinal);
        Assert.Contains("reads 4B/token", text, StringComparison.Ordinal);
    }

    //a bare MoE cell must still render the experts line. a real file reaches this shape
    [Fact]
    public void A_BARE_MOE_CELL_RENDERS_THE_EXPERTS_LINE()
    {
        var h = Parse(GgufTestBytes.WithStructure(
            expertCount: 512, expertUsed: 10, sizeLabel: "512x2.5B"));
        var facts = new ModelFacts(Structure: ModelStructure.Cell(h), Experts: ModelStructure.Experts(h));

        var text = string.Join("\n", Pane
            .Rows(Row(), facts, MachineShape.Discrete, 40)
            .Select(r => r.Text.TrimEnd()));

        Assert.Contains("reads 10 of 512 experts/token", text, StringComparison.Ordinal);
    }

    private static ModelRow Row() => ShelfRows.Of(
        "qwen/Qwen3.8-27B-GGUF", "qwen", new HubQuant("q.gguf", 16_000_000_000, null),
        FitRegime.FitsGpu, 32768, false, null, 100, false);
}
