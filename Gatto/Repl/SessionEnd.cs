namespace Gatto.Repl;

//how an interactive session ended, with the count of background tasks its end stopped
public sealed record SessionEnd(bool Farewell, bool Resume, int EndedTasks = 0);
