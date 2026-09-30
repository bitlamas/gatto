using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//a test name prints in every report and a shipped name ships with the source, so no name cites a ruling, task or finding number
public class ReferenceFreeIdentifierTests
{
    //a number shape at the start of a name, after an underscore or glued after a lowercase letter or digit, with no digit after it
    private static readonly Regex Reference = new(
        @"(?:^|_|(?<=[a-z0-9]))(?:R\d{2,3}|D\d{1,2}|T\d{1,2}|U0\d{3}|CW\d{1,2}|SB\d{1,2}|CG\d{1,2})(?![0-9])",
        RegexOptions.Compiled);

    private static readonly Regex Identifier = new(@"(?<![A-Za-z0-9_])[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    //a console key such as the digit keys is the framework's name, and a name straight after a colon is a number format inside a hole
    private static bool IsFrameworkShape(string code, int at) =>
        code.AsSpan(0, at).EndsWith("ConsoleKey.", StringComparison.Ordinal) || (at > 0 && code[at - 1] == ':');

    //the names of a source that cite a reference, read from code alone, so literals and comments stay with their own censuses
    internal static List<string> Citing(string source)
    {
        var code = SourceTree.CodeOnly(source);
        return [.. Identifier.Matches(code)
            .Where(m => Reference.IsMatch(m.Value) && !IsFrameworkShape(code, m.Index))
            .Select(m => m.Value)
            .Distinct()];
    }

    private static IEnumerable<string> Sources() =>
        SourceTree.ProductProjects.Append("Gatto.Tests")
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(SourceTree.RepoRoot(), p), "*.cs", SearchOption.AllDirectories))
            .Where(f => !SourceTree.HasFolderBelow(SourceTree.RepoRoot(), f, "obj", "bin"))
            .OrderBy(f => f, StringComparer.Ordinal);

    [Fact]
    public void NO_NAME_IN_THE_SOURCE_CITES_A_REFERENCE()
    {
        var found = new List<string>();
        var files = 0;
        var names = 0;
        foreach (var file in Sources())
        {
            files++;
            var source = SourceTree.Read(file);
            names += Identifier.Matches(SourceTree.CodeOnly(source)).Count;
            var rel = Path.GetRelativePath(SourceTree.RepoRoot(), file).Replace('\\', '/');
            found.AddRange(Citing(source).Select(n => $"{rel}: {n}"));
        }

        //a reader that found nothing would pass every file
        Assert.True(files > 900, $"the census read only {files} source files, so the reader is broken");
        Assert.True(names > 500000, $"the census read only {names} names, so the name reader is broken");
        Assert.True(found.Count == 0,
            $"{found.Count} names cite a number a public reader cannot look up. name what the code does or what the test proves:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", found));
    }

    //one planted name for each place a number can sit in a name
    [Theory]
    [InlineData("void R54_A_MODEL_THAT_FITS_OUTRANKS() { }")]
    [InlineData("const string R64Empty = \"\";")]
    [InlineData("void AND_R48s_OWN_SCREEN() { }")]
    [InlineData("void walks_the_ticks_R47() { }")]
    [InlineData("void points_at_the_pre_R109_folder() { }")]
    [InlineData("class CW8AskCursorTests { }")]
    [InlineData("void CW9_AND_CW11_HOLD() { }")]
    [InlineData("void SB1_THE_STAMP() { }")]
    [InlineData("void CG14_READY() { }")]
    [InlineData("void T11_AIR_SITS_ABOVE() { }")]
    [InlineData("void D7_DEMOTED() { }")]
    [InlineData("void U0455_WALK() { }")]
    [InlineData("class SchemaDescriptionsAreD3 { }")]
    public void A_NAME_THAT_CITES_A_REFERENCE_IS_REFUSED(string member) =>
        Assert.Single(Citing("class A\n{\n    " + member + "\n}\n"));

    //a digit key, a format in a hole, quants, a card, hashes and type parameters are the product's names
    [Theory]
    [InlineData("var k = System.ConsoleKey.D1;")]
    [InlineData("var s = $\"{i:D5}\";")]
    [InlineData("var q = Quant.Q4_K_M;")]
    [InlineData("var a = Card.A770;")]
    [InlineData("var f = Kind.F16;")]
    [InlineData("int Int32 = 0;")]
    [InlineData("string Sha256 = \"\";")]
    [InlineData("void Map<T, TKey>() { }")]
    [InlineData("int R2 = 0;")]
    [InlineData("int IPv4 = 0;")]
    [InlineData("string s = \"R54 in a literal\";")]
    [InlineData("//R54 in a comment")]
    public void A_NAME_WITHOUT_A_REFERENCE_PASSES(string member) =>
        Assert.Empty(Citing("class A\n{\n    " + member + "\n}\n"));
}
