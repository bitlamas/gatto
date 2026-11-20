using System.Text.RegularExpressions;

namespace Gatto.Roles;

//the links a session was shown, each stored whole, so a link is seen only when the very link was shown and never a longer one it starts
public sealed class SeenLinks
{
    //http and https URLs, without whitespace or the punctuation that never belongs inside one
    private static readonly Regex UrlRe = new("https?://[^\\s<>()\\[\\]\"'`]+", RegexOptions.Compiled);

    private readonly HashSet<string> _links = new(StringComparer.Ordinal);

    //every link in the text, in order, each without the sentence punctuation glued to its end
    public static IEnumerable<string> UrlsIn(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match m in UrlRe.Matches(text))
            yield return TrimTrailingPunctuation(m.Value);
    }

    //sentence punctuation glued to the end of a URL is not part of it
    public static string TrimTrailingPunctuation(string url) => url.TrimEnd('.', ',', ';', ':', '!', '?', ')');

    public static string TrimSlash(string s) => s.EndsWith('/') ? s[..^1] : s;

    public void Add(string? text)
    {
        foreach (var url in UrlsIn(text)) _links.Add(TrimSlash(url));
    }

    public void AddLink(string link) => _links.Add(TrimSlash(TrimTrailingPunctuation(link)));

    //compared whole and by case, so a host written in another case than it was shown is not seen
    public bool Seen(string link) => _links.Contains(TrimSlash(TrimTrailingPunctuation(link)));

    public void Clear() => _links.Clear();
}
