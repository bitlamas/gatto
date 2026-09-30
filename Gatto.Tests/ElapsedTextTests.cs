using Gatto.Core;
using Gatto.Repl.Render;
using Gatto.Roles;
using Gatto.Roles.Agents;

namespace Gatto.Tests;

//elapsed time reads the same at every call site, its seconds are cut and anything under one second reads <1s
public class ElapsedTextTests
{
    [Theory]
    [InlineData(0.0, "<1s")]
    [InlineData(0.999, "<1s")]
    [InlineData(59.0, "59s")]
    [InlineData(60.0, "1m 0s")]
    [InlineData(3599.0, "59m 59s")]
    [InlineData(3600.0, "1h 0m 0s")]
    [InlineData(7435.6, "2h 3m 55s")]
    public void ONE_SPELLING_AT_EVERY_CALL_SITE(double seconds, string expected)
    {
        var t = TimeSpan.FromSeconds(seconds);

        Assert.Equal(expected, ElapsedText.Of(t));
        Assert.Equal(expected, ChromeTicker.FormatElapsed((long)t.TotalMilliseconds));
        Assert.Equal(expected, ItemRender.FormatElapsed(t));
        Assert.Equal($"helper · 1 turn · {expected}", RunAgentTool.GlossOf("helper", 1, t, EngineMarks.Unicode));
    }
}
