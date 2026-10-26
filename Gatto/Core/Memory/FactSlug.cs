using System.Text;

namespace Gatto.Core.Memory;

//the slug is pure (no filesystem, clock or culture), and the c- prefix counts inside the 40-character cap so the base truncates to 38
public static class FactSlug
{
    //marks a fact as machine-fed, banked by /compact rather than written by the model, so the filename shows which facts can be pruned
    public const string Prefix = "c-";

    private const int SlugCap = 40;                     //the longest topic name MemoryDir accepts
    private const int BaseCap = SlugCap - 2;            //the -2 is Prefix.Length, kept derived so the two can't drift apart
    private const int Tokens = 4;

    //closed and English only, as the facts are, and negation stays because it opens a prohibition
    internal static readonly string[] Stopwords =
        ["a", "an", "the", "this", "that", "these", "those", "it", "its", "is", "are", "was", "be",
         "to", "of", "for", "in", "on", "at", "by", "with", "and", "or"];

    //first four tokens that are not one letter or a stopword, lowercased, dashed, c- on the front. the first four as they come when none survive, and c-fact for a line with no letters or digits
    public static string FromFactLine(string line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (var ch in line.ToLowerInvariant())
            sb.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' ? ch : ' ');

        var tokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return Prefix + "fact";

        var kept = tokens.Where(t => !(t.Length == 1 && char.IsLetter(t[0])) && !Stopwords.Contains(t)).ToArray();
        if (kept.Length == 0) kept = tokens;

        var slug = string.Join('-', kept.Take(Tokens));
        if (slug.Length > BaseCap) slug = slug[..BaseCap];
        //when the cut falls on a dash it leaves "foo-bar-", which reads as truncated and sits oddly next to a -2 collision suffix
        slug = slug.TrimEnd('-');

        return slug.Length == 0 ? Prefix + "fact" : Prefix + slug;
    }
}
