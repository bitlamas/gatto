namespace Gatto.Roles;

//the marks an engine surface draws, handed in by the caller so Roles needs no terminal vocabulary, and only the four it draws
public sealed record EngineMarks(string Ok, string Bad, string Dot, string Ellipsis)
{
    //the Unicode set, for tests and any path reached before a terminal exists. a call site has to name it rather than inherit it
    public static EngineMarks Unicode { get; } = new("✓", "✗", "·", "…");
}
