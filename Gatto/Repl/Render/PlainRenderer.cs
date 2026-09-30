using Gatto.Core.Client;
using Gatto.Core.Home;
using Gatto.Core.Loop;
using Gatto.Core.Tools;
using Gatto.Terminal;

namespace Gatto.Repl.Render;

//the plain and degraded observer, reasoning shown only in Expanded mode, a piped run treats it as noise
public sealed class PlainRenderer(ReasoningMode reasoning = ReasoningMode.Collapsed,
    GlyphSet? glyphs = null) : ITurnObserver
{
    //the glyph set chosen once at launch, resolved once so two painters cannot disagree about the host
    private readonly GlyphSet _glyphs = glyphs ?? GlyphSet.Unicode;

    //the per-turn reset the Repl calls, and there is nothing to reset here
    public void BeginTurn() { }

    //compact summaries come through this channel for the dim styling, and they must still print in full
    public bool PassthroughReasoning { get; set; }

    public void OnTextDelta(string t) => Console.Write(t);
    public void OnReasoningDelta(string t)
    {
        //collapsed mode drops the reasoning, expanded prints it dim, and a passthrough summary always prints
        if (reasoning == ReasoningMode.Collapsed && !PassthroughReasoning) return;
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write(t);
        Console.ResetColor();
    }
    public void OnToolCallStart(ToolCall c) => Console.WriteLine($"\n[tool] {c.Name} {Truncate(c.ArgumentsJson, 80, _glyphs)}");
    public void OnToolResult(ToolCall c, ToolResult r) => Console.WriteLine(ResultLine(c, r, _glyphs));

    //a shell result reads with the shell block's words, any other result with its first visible line, every piece sanitized
    internal static string ResultLine(ToolCall c, ToolResult r, GlyphSet g)
    {
        var name = TermText.Sanitize(c.Name);
        if (name == "shell" && ShellOutput.ExitCodeOf(r.Gloss) is int exit)
            return $"[tool] {name} " + ShellBlockRender.PlainWords(ShellOutput.Parse(r.Text), exit, r.Text.Length, g);
        return r.IsError
            ? $"[tool] {name} {g.Bad} {TermText.TruncateCells(ItemRender.ErrorPreview(r.Text), 120, g)}"
            : $"[tool] {name} {g.Ok}";
    }
    public void OnWarning(string m)
    {
        //the warning text can come from the endpoint, so strip control bytes like every other plain output
        m = TermText.Sanitize(m);
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n! {m}");
        Console.ResetColor();
    }
    public void OnUsage(Usage u) { }
    private static string Truncate(string s, int max, GlyphSet g) =>
        s.Length <= max ? s : s[..max] + g.Ellipsis;
}
