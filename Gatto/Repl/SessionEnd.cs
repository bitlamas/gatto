namespace Gatto.Repl;

//how an interactive session ended
public sealed record SessionEnd(bool Farewell, bool Resume);
