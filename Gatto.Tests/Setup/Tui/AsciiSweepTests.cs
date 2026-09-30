using System.Text;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//sweeps every stored golden under the ASCII table, so no frame may need a character the table can't translate
public class AsciiSweepTests
{
    //the em dash is the one character a golden may hold that the table can't translate, so its count is pinned
    private const char EmDash = '—';

    //the count is measured over the corpus, and it is exact so removing one has to come and say so
    private const int CorpusEmDashes = 21;

    //pairs come from the two sets by reflection, so a member added later is swept at once. longest first, so a prefix is not replaced under a longer one
    private static IReadOnlyList<(string From, string To)> Pairs()
    {
        var u = GlyphSet.Unicode;
        var a = GlyphSet.Ascii;
        var pairs = new List<(string From, string To)>();

        foreach (var p in typeof(GlyphSet).GetProperties())
        {
            if (p.PropertyType == typeof(string))
                pairs.Add(((string)p.GetValue(u)!, (string)p.GetValue(a)!));
            else if (p.PropertyType == typeof(IReadOnlyList<string>))
            {
                var uu = (IReadOnlyList<string>)p.GetValue(u)!;
                var aa = (IReadOnlyList<string>)p.GetValue(a)!;
                Assert.Equal(uu.Count, aa.Count);
                for (var i = 0; i < uu.Count; i++) pairs.Add((uu[i].Trim(), aa[i].Trim()));
            }
            else if (p.PropertyType == typeof(BoxSet))
            {
                var ub = (BoxSet)p.GetValue(u)!;
                var ab = (BoxSet)p.GetValue(a)!;
                foreach (var b in typeof(BoxSet).GetProperties())
                    pairs.Add(((string)b.GetValue(ub)!, (string)b.GetValue(ab)!));
            }
        }

        return [.. pairs.Where(x => x.From.Length > 0).DistinctBy(x => x.From)
            .OrderByDescending(x => x.From.Length)];
    }

    private static string Transliterate(string row)
    {
        var sb = new StringBuilder(row);
        foreach (var (from, to) in Pairs()) sb.Replace(from, to);
        return sb.ToString();
    }

    //the width comes from the golden's own file name, so no frame is checked at a width nobody rendered it at
    private static int WidthOf(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        var tail = stem[(stem.LastIndexOf('-') + 1)..];
        return int.TryParse(tail, out var w) ? w : 0;
    }

    private static IReadOnlyList<string> Corpus() =>
        [.. Directory.EnumerateFiles(Golden.Dir, "*.txt")
            .Select(f => Path.GetFileName(f)!)
            .OrderBy(f => f, StringComparer.Ordinal)];

    public static TheoryData<string> Goldens()
    {
        var data = new TheoryData<string>();
        foreach (var f in Corpus()) data.Add(f);
        return data;
    }

    //every non-ASCII character in a golden must be one the table can translate, so a host without the glyph gets its twin
    [Theory]
    [MemberData(nameof(Goldens))]
    public void EVERY_GOLDEN_FRAME_IS_ASCII_ONCE_TRANSLITERATED(string name)
    {
        var rows = File.ReadAllLines(Path.Combine(Golden.Dir, name));

        for (var i = 0; i < rows.Length; i++)
        {
            var got = Transliterate(rows[i]);
            foreach (var ch in got)
                Assert.True(ch <= 0x7F || ch == EmDash,
                    $"{name} row {i + 1} still carries U+{(int)ch:X4} after the table:\n  {rows[i]}\n  {got}");
        }
    }

    //the naming is pinned here, since a golden with no width in its name is invisible to any check that prices frames
    [Theory]
    [MemberData(nameof(Goldens))]
    public void EVERY_GOLDEN_CARRIES_ITS_WIDTH_IN_ITS_NAME(string name) =>
        Assert.True(WidthOf(name) > 0, $"{name} carries no width in its name; nothing can price it");

    //without this, the two checks above are claims about an empty sweep, so the corpus must be real and change under transliteration
    [Fact]
    public void THE_SWEEP_READS_A_REAL_CORPUS_WITH_MARKS_IN_IT()
    {
        var files = Corpus();
        //the count is a floor, so a corpus that grows with every drawn scene doesn't fail this test
        Assert.True(files.Count >= 102, $"the sweep found only {files.Count} frames");

        var all = string.Concat(files.Select(f => File.ReadAllText(Path.Combine(Golden.Dir, f))));
        Assert.Contains(GlyphSet.Unicode.Rule, all, StringComparison.Ordinal);
        Assert.Contains(GlyphSet.Unicode.Dot, all, StringComparison.Ordinal);
        Assert.Contains(GlyphSet.Unicode.Ellipsis, all, StringComparison.Ordinal);
        Assert.Contains(GlyphSet.Unicode.Prompt, all, StringComparison.Ordinal);
        Assert.NotEqual(all, Transliterate(all));
    }

    //the pair table must come from the sets themselves (if reflection missed a kind of member, the sweep would go vacuous in silence)
    [Fact]
    public void THE_PAIR_TABLE_COVERS_EVERY_KIND_OF_MEMBER()
    {
        var pairs = Pairs();

        Assert.Contains((GlyphSet.Unicode.Ok, GlyphSet.Ascii.Ok), pairs);
        Assert.Contains((GlyphSet.Unicode.Box.Cross, GlyphSet.Ascii.Box.Cross), pairs);
        Assert.Contains((GlyphSet.Unicode.Cat[1].Trim(), GlyphSet.Ascii.Cat[1].Trim()), pairs);
        Assert.Contains((GlyphSet.Unicode.Face, GlyphSet.Ascii.Face), pairs);
        Assert.True(pairs.Count >= 40, $"the pair table holds only {pairs.Count} members");
    }

    //the width check means something only if a twin can widen a row, so these are pinned as literals
    [Fact]
    public void THE_WIDENING_TWINS_REALLY_ARE_WIDER()
    {
        Assert.Equal(3, UnicodeWidth.Of(GlyphSet.Ascii.Ellipsis));
        Assert.Equal(3, UnicodeWidth.Of(GlyphSet.Ascii.MidEllipsis));
        Assert.Equal(3, UnicodeWidth.Of(GlyphSet.Ascii.Sum));
        foreach (var wide in new[]
                 {
                     GlyphSet.Ascii.Right, GlyphSet.Ascii.Left, GlyphSet.Ascii.Continue,
                     GlyphSet.Ascii.AtMost, GlyphSet.Ascii.Elbow,
                 })
            Assert.Equal(2, UnicodeWidth.Of(wide));
    }

    //an exclusion is where a check goes quietly blind, so the corpus's em dashes are counted exactly
    [Fact]
    public void THE_CORPUS_EM_DASHES_ARE_COUNTED_EXACTLY()
    {
        var found = Corpus()
            .Sum(f => File.ReadAllText(Path.Combine(Golden.Dir, f)).Count(c => c == EmDash));

        Assert.Equal(CorpusEmDashes, found);
    }
}
