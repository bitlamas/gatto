using System.Text.Json;
using Gatto.Core.Memory;
using Gatto.Core.Tools;

namespace Gatto.Tests;

//reuse the TestToolContext fake in FileToolsTests.cs in this namespace
public class MemoryWriteToolTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-test-").FullName;
    private IToolContext Ctx => new TestToolContext(_dir);
    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static MemoryWriteTool Tool(int budget = 1000) => new(() => budget);

    private Task<ToolResult> Run(MemoryWriteTool tool, string json) =>
        tool.ExecuteAsync(Args(json), Ctx, default);

    private Task<ToolResult> Run(string json) => Run(Tool(), json);

    [Fact]
    public async Task Write_CreatesTheFactFile_AndNamesIt()
    {
        var r = await Run("""{"slug":"build-outputs","content":"- tmp/testbin","mode":"write"}""");

        Assert.Contains("saved build-outputs.md", r.Text);
        Assert.Equal("- tmp/testbin\n", File.ReadAllText(MemoryDir.PathFor(_dir, "build-outputs")));
    }

    [Fact]
    public async Task EveryWrite_CarriesTheVisibilityNote()
    {
        //every write repeats the visibility note, a fact's first line only joins the index at a reload boundary
        var r = await Run("""{"slug":"shell-gotchas","content":"- no && in PS 5.1","mode":"write"}""");

        Assert.Contains("listed from the next session or /new", r.Text);
    }

    [Fact]
    public async Task Write_OverwritesWholeFile_SoRevisingIsRewritingTheSlug()
    {
        await Run("""{"slug":"fact","content":"- the old claim","mode":"write"}""");
        await Run("""{"slug":"fact","content":"- the corrected claim","mode":"write"}""");

        Assert.Equal("- the corrected claim\n", File.ReadAllText(MemoryDir.PathFor(_dir, "fact")));
    }

    [Fact]
    public async Task Delete_RetiresTheFact()
    {
        await Run("""{"slug":"doomed","content":"- wrong","mode":"write"}""");

        var r = await Run("""{"slug":"doomed","content":"","mode":"delete"}""");

        Assert.Contains("deleted doomed.md", r.Text);
        Assert.False(File.Exists(MemoryDir.PathFor(_dir, "doomed")));
    }

    [Fact]
    public async Task Delete_WithContentOmittedEntirely_IsFine()
    {
        //content stays optional in the schema, requiring it would reject the ordinary delete with a generic error and cost a retry
        await Run("""{"slug":"doomed","content":"- wrong","mode":"write"}""");

        var r = await Run("""{"slug":"doomed","mode":"delete"}""");

        Assert.Contains("deleted doomed.md", r.Text);
    }

    [Fact]
    public async Task Delete_WithNonEmptyContent_ThrowsAndSaysWhy()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Run("""{"slug":"x","content":"- something","mode":"delete"}"""));

        Assert.Contains("content must be empty", ex.Message);
    }

    [Fact]
    public async Task Delete_MissingSlug_Throws()
    {
        //the delete must be loud when the slug is missing, silence teaches the model that a wrong slug is harmless
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Run("""{"slug":"never-existed","content":"","mode":"delete"}"""));

        Assert.Equal("no such fact: never-existed", ex.Message);
    }

    [Fact]
    public async Task AppendMode_IsGone()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Run("""{"slug":"x","content":"- y","mode":"append"}"""));

        Assert.Equal("mode must be \"write\" or \"delete\"", ex.Message);
    }

    [Fact]
    public async Task MissingMode_Throws()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Run("""{"slug":"x","content":"- y"}"""));

        Assert.Equal("mode must be \"write\" or \"delete\"", ex.Message);
    }

    [Fact]
    public async Task Write_WithoutContent_ThrowsTheOrdinaryMissingParameterError()
    {
        //content stays optional for delete's sake, a write still needs it and fails clearly instead of writing an empty fact
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Run("""{"slug":"x","mode":"write"}"""));

        Assert.Contains("content", ex.Message);
    }

    [Fact]
    public async Task TraversalSlug_Throws()
        => await Assert.ThrowsAsync<ArgumentException>(() =>
            Run("""{"slug":"../evil","content":"- x","mode":"write"}"""));

    [Fact]
    public async Task Advisory_MeasuresTheUNCAPPEDIndex_SoItCanExceed100Percent()
    {
        //the advisory measures the uncapped index. a capped one can't pass 100%, and a tight budget reads zero exactly when the warning matters
        var tool = Tool(budget: 5);   //budget 5 is about 20 characters, one fact already passes it
        for (var i = 0; i < 6; i++)
            await Run(tool, $$"""{"slug":"fact-{{i}}","content":"- {{new string('x', 30)}}","mode":"write"}""");

        var r = await Run(tool, """{"slug":"last","content":"- one more","mode":"write"}""");

        Assert.Contains("% of budget", r.Text);
    }

    [Fact]
    public async Task Advisory_TracksTheBudgetAtCallTime_NotAtConstruction()
    {
        //read the budget at call time, a /model into a tighter index budget moves the cap and the advisory together
        var budget = 100_000;
        var tool = new MemoryWriteTool(() => budget);

        var before = await Run(tool, """{"slug":"a","content":"- a fact","mode":"write"}""");
        Assert.DoesNotContain("% of budget", before.Text);

        budget = 1;   //the drop to one mimics a /model switch into a tighter model
        var after = await Run(tool, """{"slug":"b","content":"- another fact","mode":"write"}""");

        Assert.Contains("% of budget", after.Text);
    }

    [Fact]
    public async Task UnderBudget_NoAdvisory()
    {
        var r = await Run("""{"slug":"x","content":"- small","mode":"write"}""");
        Assert.DoesNotContain("% of budget", r.Text);
    }

    [Fact]
    public async Task ZeroOrNegativeBudget_NoAdvisory_NoDivideByZero()
    {
        //the config layer allows a zero or negative budget and the advisory divides by it, the guard returns nothing
        var zero = await Run(new MemoryWriteTool(() => 0), """{"slug":"x","content":"- x","mode":"write"}""");
        Assert.DoesNotContain("% of budget", zero.Text);

        var negative = await Run(new MemoryWriteTool(() => -5), """{"slug":"y","content":"- y","mode":"write"}""");
        Assert.DoesNotContain("% of budget", negative.Text);
    }
}
