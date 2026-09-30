namespace Gatto.Cli;

//the PATH edit as a pure function, so every byte but the appended entry survives. nothing is expanded, so a %USERPROFILE% the user wrote stays as it is
internal static class UserPath
{
    private const char Separator = ';';

    //append the entry, or null when it's already there, since null means write nothing and a re-run stays idempotent
    public static string? Append(string current, string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;
        if (Contains(current, entry)) return null;

        //the separator goes in only when the value is non-empty and doesn't end with one, since a trailing separator is legal
        if (current.Length == 0) return entry;
        return current.EndsWith(Separator) ? current + entry : current + Separator + entry;
    }

    //remove exactly our entry and leave every other byte alone, since both directions share the matching rule
    public static string? Remove(string current, string entry)
    {
        if (string.IsNullOrWhiteSpace(entry) || !Contains(current, entry)) return null;

        //rebuild from the original segments, dropping only the matching ones, with no trimming, dedupe or reordering
        var parts = current.Split(Separator);
        var kept = parts.Where(p => !Matches(p, entry)).ToArray();

        //a value that ended with a separator still ends with one, since Split gives a trailing empty segment that joins back
        return string.Join(Separator, kept);
    }

    //is this entry on the PATH, matched segment by segment, since a substring test would find C:\tools\gatto inside C:\tools\gatto-old
    public static bool Contains(string current, string entry) =>
        current.Split(Separator).Any(p => Matches(p, entry));

    //trailing separators and whitespace are ignored for matching only, and paths compare case-insensitively
    private static bool Matches(string segment, string entry) =>
        string.Equals(
            segment.Trim().TrimEnd('\\'),
            entry.Trim().TrimEnd('\\'),
            StringComparison.OrdinalIgnoreCase);
}
