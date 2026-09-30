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

    //first four tokens, lowercased and joined with dashes, c- on the front, and c-fact for a line with no letters or digits
    public static string FromFactLine(string line)
    {
        var sb = new StringBuilder(line.Length);
        foreach (var ch in line.ToLowerInvariant())
            sb.Append(ch is >= 'a' and <= 'z' or >= '0' and <= '9' ? ch : ' ');

        var tokens = sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return Prefix + "fact";

        var slug = string.Join('-', tokens.Take(Tokens));
        if (slug.Length > BaseCap) slug = slug[..BaseCap];
        //when the cut falls on a dash it leaves "foo-bar-", which reads as truncated and sits oddly next to a -2 collision suffix
        slug = slug.TrimEnd('-');

        return slug.Length == 0 ? Prefix + "fact" : Prefix + slug;
    }
}
