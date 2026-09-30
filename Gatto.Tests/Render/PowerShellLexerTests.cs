using System.Text.Json;
using Gatto.Repl.Render;
using Gatto.Terminal;
using Gatto.Tests.Census;
using Xunit;

namespace Gatto.Tests.Render;

//compare the lexer against the parser of Windows PowerShell. run generate-oracle.ps1 to rebuild the oracle from the corpus
public sealed class PowerShellLexerTests
{
    private const string OracleFile = "Gatto.Tests/Render/PowerShellOracle/oracle.json";

    private sealed record OracleToken(string K, string F, int S, int E);

    private sealed record OracleEntry(string Source, string Parser, List<OracleToken> Tokens);

    private static readonly Lazy<List<OracleEntry>> Oracle = new(() =>
        JsonSerializer.Deserialize<List<OracleEntry>>(
            SourceTree.Read(Path.Combine(SourceTree.RepoRoot(), OracleFile)),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);

    public static TheoryData<int> Entries()
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < Oracle.Value.Count; i++) data.Add(i);
        return data;
    }

    //maps a parser token to the role the lexer must give it. nested tokens come after their parent and win
    private static SpanRole OracleRole(string kind, string flags, string text)
    {
        var set = flags.Split(", ");
        bool Has(string flag) => set.Contains(flag);
        if (kind == "Comment") return SpanRole.Comment;
        if (kind is "StringLiteral" or "StringExpandable" or "HereStringLiteral" or "HereStringExpandable") return SpanRole.String;
        if (kind is "Variable" or "SplattedVariable") return SpanRole.Variable;
        if (kind == "Parameter") return SpanRole.Parameter;
        if (kind == "Number") return SpanRole.Number;
        if (Has("Keyword")) return SpanRole.Keyword;
        if (Has("CommandName")) return SpanRole.Function;
        if (Has("TypeName") || Has("AttributeName")) return SpanRole.Type;
        if (Has("BinaryOperator") || Has("UnaryOperator") || Has("AssignmentOperator") || Has("PrefixOrPostfixOperator") || Has("SpecialOperator"))
            return SpanRole.Punct;
        if (kind is "LParen" or "RParen" or "LCurly" or "RCurly" or "LBracket" or "RBracket" or "AtParen" or "AtCurly"
            or "DollarParen" or "Semi" or "Pipe" or "Comma" or "Redirection" or "RedirectInStd" or "Ampersand")
            return SpanRole.Punct;
        return SpanRole.None;
    }

    private static string Codes(IEnumerable<SpanRole> roles) => new(roles.Select(r => r switch
    {
        SpanRole.Keyword => 'K', SpanRole.Type => 'T', SpanRole.String => 'S', SpanRole.Number => 'N', SpanRole.Comment => 'C',
        SpanRole.Punct => 'P', SpanRole.Variable => 'V', SpanRole.Parameter => 'A', SpanRole.Function => 'F', _ => '.',
    }).ToArray());

    private static SpanRole[] LexerRoles(string text)
    {
        var roles = new SpanRole[text.Length];
        foreach (var span in PowerShellLexer.Classify(text))
            for (var i = span.Start; i < span.Start + span.Length; i++) roles[i] = span.Role;
        return roles;
    }

    [Fact]
    public void The_oracle_holds_the_whole_corpus()
    {
        var corpus = SourceTree.Read(Path.Combine(SourceTree.RepoRoot(), "Gatto.Tests/Render/PowerShellOracle/corpus.txt"));
        var entries = corpus.Split("\n----\n").Count(e => e.Trim().Length > 0);
        Assert.True(Oracle.Value.Count >= 100, $"the oracle holds only {Oracle.Value.Count} entries");
        Assert.Equal(entries, Oracle.Value.Count);
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public void The_lexer_reads_each_corpus_entry_the_way_PowerShell_does(int index)
    {
        var entry = Oracle.Value[index];
        var text = entry.Source;
        var expected = new SpanRole[text.Length];
        foreach (var token in entry.Tokens)
        {
            var start = Math.Min(token.S, text.Length);
            var end = Math.Min(Math.Max(token.E, start), text.Length);
            var role = OracleRole(token.K, token.F, text[start..end]);
            for (var i = start; i < end; i++) expected[i] = role;
        }

        var actual = LexerRoles(text);
        for (var i = 0; i < text.Length; i++)
            if (char.IsWhiteSpace(text[i])) expected[i] = actual[i] = SpanRole.None;

        Assert.True(expected.SequenceEqual(actual),
            $"\nsource:   {text.Replace('\n', (char)0x00B6)}\nexpected: {Codes(expected)}\nactual:   {Codes(actual)}");
    }

    [Theory]
    [InlineData("git pull && dotnet test", "&&", SpanRole.Punct)]
    [InlineData("git pull && dotnet test", "dotnet", SpanRole.Function)]
    [InlineData("Test-Path x || Write-Error missing", "||", SpanRole.Punct)]
    [InlineData("Test-Path x || Write-Error missing", "Write-Error", SpanRole.Function)]
    [InlineData("$v = $a ?? 'fallback'", "??", SpanRole.Punct)]
    [InlineData("$v = $ok ? 'yes' : 'no'", "'yes'", SpanRole.String)]
    [InlineData("$v = $ok ? 'yes' : 'no'", "'no'", SpanRole.String)]
    public void PowerShell_7_syntax_that_the_oracle_cannot_parse_still_reads_right(string code, string probe, SpanRole role)
    {
        var at = code.IndexOf(probe, StringComparison.Ordinal);
        var got = LexerRoles(code).Skip(at).Take(probe.Length).ToArray();
        Assert.True(got.All(r => r == role), $"'{probe}' in `{code}` read as {Codes(got)}");
    }

    [Fact]
    public void Random_text_never_throws_and_every_span_stays_inside_the_text()
    {
        var rng = new Random(11);
        const string alphabet = "ab-$@{}()[]\"'`#<>|&;,.:=+*!?0x9 \n\t";
        var spans = 0;
        for (var n = 0; n < 2000; n++)
        {
            var text = new string(Enumerable.Range(0, rng.Next(0, 50)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
            foreach (var s in PowerShellLexer.Classify(text))
            {
                spans++;
                Assert.True(s.Start >= 0 && s.Length > 0 && s.Start + s.Length <= text.Length, $"the span {s} falls outside `{text}`");
            }
        }
        Assert.True(spans > 0, "the lexer returned no spans at all, so the bounds were never checked");
    }
}
