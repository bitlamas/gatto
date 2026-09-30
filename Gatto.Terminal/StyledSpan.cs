namespace Gatto.Terminal;

[Flags]
public enum SpanFlags { None = 0, Bold = 1, Italic = 2, Strike = 4, Chip = 8, Link = 16 }

public readonly record struct StyledSpan(string Text, SpanFlags Flags, string? LinkUrl = null);
