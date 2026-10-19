namespace Gatto.Core.Client;

//the line the edit starts on, the rest of its first and last line, and up to three lines on each side, for display only
public sealed record EditView(int Start, string Head, string Tail, IReadOnlyList<string> Before, IReadOnlyList<string> After);
