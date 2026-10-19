namespace Gatto.Repl.Input;

//the profile root read as ~ wherever a path is shown, a string rule the composer and the transcript share
internal static class HomePath
{
    //a path under the profile reads from ~, and this runs after the working-directory rule so that rule wins first
    internal static string AbbreviateProfile(string path, string profile)
    {
        var root = profile.TrimEnd('\\', '/');
        if (root.Length == 0 || path.Length < root.Length || !SameRoot(path.AsSpan(0, root.Length), root)) return path;
        if (path.Length == root.Length) return "~";
        return path[root.Length] is '\\' or '/' ? "~" + path[root.Length..] : path;
    }

    internal static string AbbreviateProfileEmbedded(string text, string profile) => ReplaceEmbeddedRoot(text, profile, "~");

    //a null replacement drops the root and its separator, and any other stands in for the root
    internal static string ReplaceEmbeddedRoot(string text, string root, string? replacement)
    {
        root = root.TrimEnd('\\', '/');
        if (root.Length == 0) return text;   //a root of "" would strip every leading separator

        var sb = new System.Text.StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            var sep = i + root.Length;
            var bounded = sep <= text.Length && (i == 0 || !IsPathNameChar(text[i - 1]));
            var named = bounded && sep + 1 < text.Length && (text[sep] is '\\' or '/') && StartsPathName(text[sep + 1]);
            var bare = bounded && replacement is not null && (sep == text.Length || EndsBareRoot(text[sep]));
            if ((named || bare) && SameRoot(text.AsSpan(i, root.Length), root))
            {
                if (replacement is null) i = sep + 1;
                else { sb.Append(replacement); i = sep; }
                continue;
            }
            sb.Append(text[i++]);
        }
        return sb.ToString();
    }

    private static bool IsPathNameChar(char c) =>
        char.IsLetterOrDigit(c) || c is '\\' or '/' or '.' or '_' or '-' or ':' or '?' or '~';

    //a shell delimiter such as a pipe ends the root, so only a name character lets it drop
    private static bool StartsPathName(char c) =>
        char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '~' or '*' or '$' or '%' or '@';

    //a root on its own ends at a space, a quote or a shell delimiter, so a sibling folder never reads as the profile
    private static bool EndsBareRoot(char c) => c is ' ' or '\t' or '\r' or '\n' or '"' or '\'' or ';' or '|' or ')' or ',';

    private static bool SameRoot(ReadOnlySpan<char> candidate, string root)
    {
        for (var k = 0; k < root.Length; k++)
        {
            bool same;
            if (candidate[k] is '\\' or '/') same = root[k] is '\\' or '/';
            else same = char.ToUpperInvariant(candidate[k]) == char.ToUpperInvariant(root[k]);
            if (!same) return false;
        }
        return true;
    }
}
