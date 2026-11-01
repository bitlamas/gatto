using System.Text;
using Gatto.Core.Tools;

namespace Gatto.Tests;

//a command that floods its pipe must not grow gatto's memory with it, and the result must read as it did when the whole output was kept
public class ShellOutputCapTests
{
    private const int Flood = 3_000_000;

    private static StreamReader Reader(string text) => new(new MemoryStream(Encoding.UTF8.GetBytes(text)), Encoding.UTF8);

    private static async Task<HeadBuffer> Drained(string text)
    {
        var into = new HeadBuffer();
        await ShellTool.DrainAsync(Reader(text), into, new object(), default);
        return into;
    }

    //three million chars drained, and what the drain keeps stays at the head the result can show
    [Fact]
    public async Task A_FLOOD_IS_KEPT_TO_THE_HEAD_WHILE_IT_DRAINS()
    {
        var into = await Drained(new string('x', Flood));

        Assert.True(into.ToString().Length <= ShellTool.OutputCap + 1, $"kept {into.ToString().Length} chars");
        Assert.Equal(Flood, into.Received);   //the count a screen watches still sees every char
    }

    //each shape composes as the whole output did: floods, trailing whitespace, a cut surrogate pair, two streams over the cap only together, short output
    public static TheoryData<string, string, int, bool> Shapes() => new()
    {
        { new string('a', Flood), "", 0, false },
        { new string('a', Flood) + "\n", "boom", 3, true },
        { "short out\n", new string('e', Flood), 1, false },
        { new string('a', 49_990) + new string(' ', Flood), "", 0, false },
        { new string('a', 49_990) + new string(' ', Flood) + "z", "", 0, false },
        { "ok\n", new string('e', 49_980) + new string('\n', Flood), 2, true },
        { new string('a', 49_999) + "\U0001F600" + new string('b', 100), "", 0, false },
        { "line one\nline two\n", "a warning\n", 0, false },
        { new string('a', 49_000), new string('e', 49_000), 0, false },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public async Task THE_RESULT_READS_AS_IT_DID_WHEN_THE_WHOLE_OUTPUT_WAS_KEPT(string stdout, string stderr, int exit, bool pipeHeld)
    {
        var whole = ShellTool.Compose(stdout, stderr, exit, pipeHeld, null, null);
        var kept = ShellTool.Compose((await Drained(stdout)).ToString(), (await Drained(stderr)).ToString(), exit, pipeHeld, null, null);

        Assert.Equal(whole, kept);
    }
}
