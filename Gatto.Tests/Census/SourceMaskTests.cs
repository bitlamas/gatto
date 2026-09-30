using Gatto.Tests.Setup;

namespace Gatto.Tests.Census;

//the masker decides what every census reads, so it needs tests of its own. each case writes one syntax form out, a masker fails by not knowing one
public class SourceMaskTests
{
    private const string Q = "\"";
    private static readonly string Raw = Q + Q + Q;

    //the exact broken shape is written out here, a raw string holding a slash-star followed by ordinary code
    [Fact]
    public void A_RAW_STRING_DOES_NOT_OPEN_A_BLOCK_COMMENT()
    {
        var source = "        const string Help = " + Raw + "\n"
            + "          roles: <any ~/.gatto/roles/*.json>\n"
            + "        " + Raw + ";\n"
            + "        var real = Resolve(home);\n";

        var masked = SourceMask.Mask(source);

        Assert.Contains("var real = Resolve(home);", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("roles", masked, StringComparison.Ordinal);
    }

    //a verbatim string spans lines, and a doubled quote does not end it. a masker that stops at the first quote hands the rest back as code.
    [Fact]
    public void A_VERBATIM_STRING_SPANS_LINES_AND_SURVIVES_A_DOUBLED_QUOTE()
    {
        var source = "        var path = @" + Q + "C:\\a\n"
            + "b" + Q + Q + "c /* not a comment\n"
            + "d" + Q + ";\n"
            + "        var real = Resolve(home);\n";

        var masked = SourceMask.Mask(source);

        Assert.Contains("var real = Resolve(home);", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("not a comment", masked, StringComparison.Ordinal);
    }

    //an ordinary literal stays bounded by its line. blanking to the end of the file would repeat the raw-string failure, so this narrow case is pinned
    [Fact]
    public void AN_UNTERMINATED_LITERAL_STOPS_AT_THE_NEWLINE()
    {
        const string source = "        var oops = \"open\n"
            + "        var real = Resolve(home);\n";

        Assert.Contains("var real = Resolve(home);", SourceMask.Mask(source), StringComparison.Ordinal);
    }

    //pin the plain blanking jobs too, so a fix for rarer forms cannot lose them.
    [Fact]
    public void COMMENTS_AND_LITERALS_ARE_STILL_BLANKED()
    {
        const string source = "        // a comment naming Resolve(home)\n"
            + "        var s = \"Resolve(home)\";\n"
            + "        /* a block\n"
            + "           naming Resolve(home) */\n"
            + "        var real = Resolve(home);\n";

        var masked = SourceMask.Mask(source);

        Assert.Equal(1, masked.Split("Resolve(home)").Length - 1);
        Assert.Contains("var real = Resolve(home);", masked, StringComparison.Ordinal);
    }

    //masking keeps the length and the line count, so an offset points at the real line. this runs on the real tree, every form the codebase holds must keep it
    [Fact]
    public void MASKING_PRESERVES_LENGTH_AND_LINES_ACROSS_THE_TREE()
    {
        foreach (var file in SourceTree.ProductionFiles())
        {
            var source = SourceTree.Read(file);
            var masked = SourceMask.Mask(source);

            Assert.Equal(source.Length, masked.Length);
            Assert.Equal(source.Count(c => c == '\n'), masked.Count(c => c == '\n'));
        }
    }

    //no file may lose a huge run of code to the masker. state the floor as a ratio per file, a tree-wide total hides one blind file
    [Fact]
    public void NO_FILE_IS_ALMOST_ENTIRELY_BLANKED()
    {
        var blind = new List<string>();

        foreach (var file in SourceTree.ProductionFiles())
        {
            var source = SourceTree.Read(file);
            if (source.Length < 4000) continue;

            var masked = SourceMask.Mask(source);
            var blanked = source.Where((c, i) => c != '\n' && masked[i] != c).Count();
            var code = source.Count(c => c != '\n');
            if (code > 0 && blanked * 100L / code > 90)
                blind.Add($"  {Path.GetRelativePath(SourceTree.RepoRoot(), file)}: "
                    + $"{blanked * 100L / code}% blanked");
        }

        Assert.True(blind.Count == 0,
            "the masker cannot see most of these files, so a census over them proves nothing:\n"
            + string.Join("\n", blind));
    }
}
