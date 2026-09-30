using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//the count of nullable GlyphSet parameters may only go down. a missing argument falls back to the Unicode set, so a console that cannot draw it shows boxes
public class GlyphFallbackRatchetTests
{
    //the number of remaining sites is measured at the tree, so it may only go down. raising it spends a deferred cleanup rather than paying it
    private const int Left = 40;

    private static readonly Regex Nullable = new(@"GlyphSet\?\s+\w+\s*=\s*null", RegexOptions.Compiled);

    [Fact]
    public void THE_NULLABLE_GLYPH_PARAMETER_ONLY_EVER_GOES_DOWN()
    {
        Assert.Single(Nullable.Matches("void Draw(GlyphSet? glyphs = null) { }"));
        Assert.Empty(Nullable.Matches("void Draw(GlyphSet glyphs) { }"));
        Assert.Empty(Nullable.Matches("GlyphSet? held = Resolve();"));

        var found = SourceTree.ProductionFiles()
            .Sum(f => Nullable.Matches(File.ReadAllText(f)).Count);

        Assert.True(found <= Left,
            $"{found} nullable glyph parameters, up from {Left}. A caller that does not say which "
            + "alphabet it draws in gets Unicode, and on a console that cannot draw it that is a row "
            + "of boxes. Pass the set the caller already holds.");
    }
}
