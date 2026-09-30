using System.Text;
using System.Text.Json;
using Gatto.Repl.Render;
using Gatto.Terminal;
using Gatto.Tests.Census;
using Xunit;

namespace Gatto.Tests.Render;

//the fallback scanners measured against python's tokenize, node's acorn, the .NET json reader and the hand-labelled corpora
public sealed class FallbackScannerTests
{
    private static readonly string Nl = ((char)10).ToString();
    private static readonly string Separator = Nl + "----" + Nl;
    private static readonly string RenderDir = Path.Combine(SourceTree.RepoRoot(), "Gatto.Tests", "Render");
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };
    private static readonly string[] LabelledFiles = ["json", "typescript", "xml", "html", "rust", "bash"];

    private sealed record PythonToken(string K, int S, int E, bool Kw);

    private sealed record PythonEntry(string Source, string Parser, List<PythonToken> Tokens, string? Unterminated);

    private sealed record ScriptToken(string K, string? Kw, int S, int E);

    private sealed record ScriptEntry(string Source, string Node, string Acorn, List<ScriptToken> Tokens, string? Unterminated);

    private static readonly Lazy<List<PythonEntry>> Python = new(() => Oracle<PythonEntry>("PythonOracle"));

    private static readonly Lazy<List<ScriptEntry>> Script = new(() => Oracle<ScriptEntry>("JavaScriptOracle"));

    private static List<T> Oracle<T>(string folder) =>
        JsonSerializer.Deserialize<List<T>>(SourceTree.Read(Path.Combine(RenderDir, folder, "oracle.json")), Options)!;

    private static List<string> Entries(string path)
    {
        var raw = SourceTree.Read(path);
        while (raw.EndsWith(Nl, StringComparison.Ordinal)) raw = raw[..^1];
        return [.. raw.Split(Separator).Where(e => e.Trim().Length > 0)];
    }

    private static FallbackLanguage Entry(CodeLanguage language) => FallbackLanguages.Of(language)!;

    private static SpanRole[] ScannerRoles(CodeLanguage language, string text)
    {
        var roles = new SpanRole[text.Length];
        foreach (var span in FallbackScanner.Classify(Entry(language), text))
            for (var i = span.Start; i < span.Start + span.Length; i++) roles[i] = span.Role;
        return roles;
    }

    private static string Codes(IEnumerable<SpanRole> roles) => new(roles.Select(r => r switch
    {
        SpanRole.Keyword => 'K', SpanRole.Type => 'T', SpanRole.String => 'S', SpanRole.Number => 'N', SpanRole.Comment => 'C',
        SpanRole.Punct => 'P', SpanRole.Variable => 'V', SpanRole.Parameter => 'A', SpanRole.Function => 'F', _ => '.',
    }).ToArray());

    private static SpanRole RoleOfCode(char code) => code switch
    {
        'K' => SpanRole.Keyword, 'T' => SpanRole.Type, 'S' => SpanRole.String, 'N' => SpanRole.Number, 'C' => SpanRole.Comment,
        'P' => SpanRole.Punct, 'V' => SpanRole.Variable, 'A' => SpanRole.Parameter, 'F' => SpanRole.Function, '.' => SpanRole.None,
        _ => throw new InvalidDataException($"no role has the code {code}"),
    };

    //white space has no role on either side, and both are set to None before the comparison
    private static void AssertSameRoles(string text, SpanRole[] expected, SpanRole[] actual, string label)
    {
        for (var i = 0; i < text.Length; i++)
            if (char.IsWhiteSpace(text[i])) expected[i] = actual[i] = SpanRole.None;
        Assert.True(expected.SequenceEqual(actual), string.Join(Environment.NewLine,
            "", label, "source:   " + text.Replace((char)10, (char)0x00B6), "expected: " + Codes(expected), "actual:   " + Codes(actual)));
    }

    //from the first non-blank character after the last token, the text takes the role of the form the tokenizer left open
    private static void FillUnterminated(string text, SpanRole[] expected, int lastEnd, string? kind)
    {
        if (kind is null) return;
        var role = kind switch
        {
            "string" or "template" => SpanRole.String,
            "comment" => SpanRole.Comment,
            _ => throw new InvalidDataException($"the oracle names an open form this test does not know: {kind}"),
        };
        var at = lastEnd;
        while (at < text.Length && char.IsWhiteSpace(text[at])) at++;
        for (var i = at; i < text.Length; i++) expected[i] = role;
    }

    private static SpanRole[] PythonExpected(PythonEntry entry)
    {
        var text = entry.Source;
        var expected = new SpanRole[text.Length];
        var tokens = entry.Tokens;
        for (var n = 0; n < tokens.Count; n++)
        {
            var t = tokens[n];
            var previous = n > 0 ? text[tokens[n - 1].S..tokens[n - 1].E] : "";
            var next = n + 1 < tokens.Count ? text[tokens[n + 1].S..tokens[n + 1].E] : "";
            var role = t.K switch
            {
                "COMMENT" => SpanRole.Comment,
                "STRING" or "FSTRING_START" or "FSTRING_MIDDLE" or "FSTRING_END" or "TSTRING_START" or "TSTRING_MIDDLE" or "TSTRING_END" => SpanRole.String,
                "NUMBER" => SpanRole.Number,
                "OP" => SpanRole.Punct,
                "NAME" when t.Kw => SpanRole.Keyword,
                "NAME" when previous == "def" => SpanRole.Function,
                "NAME" when previous == "class" => SpanRole.Type,
                "NAME" when previous == "@" => SpanRole.Function,
                "NAME" when next == "(" => SpanRole.Function,
                _ => SpanRole.None,
            };
            for (var i = t.S; i < t.E; i++) expected[i] = role;
        }
        FillUnterminated(text, expected, tokens.Count > 0 ? tokens[^1].E : 0, entry.Unterminated);
        return expected;
    }

    private static SpanRole[] ScriptExpected(ScriptEntry entry)
    {
        var text = entry.Source;
        var contextual = Entry(CodeLanguage.JavaScript).ContextualKeywords;
        var expected = new SpanRole[text.Length];
        var code = entry.Tokens.Where(t => t.K != "comment").ToList();
        foreach (var t in entry.Tokens)
        {
            var n = code.IndexOf(t);
            var previous = n > 0 ? code[n - 1] : null;
            var next = n >= 0 && n + 1 < code.Count ? code[n + 1] : null;
            var word = text[t.S..t.E];
            var afterDot = previous is { K: "." or "?." };
            SpanRole NameRole() =>
                previous is { Kw: "function" } ? SpanRole.Function
                : previous is { Kw: "class" } ? SpanRole.Type
                : next is { K: "(" } ? SpanRole.Function
                : SpanRole.None;
            var role = t.K switch
            {
                "comment" => SpanRole.Comment,
                "string" or "template" or "`" or "regexp" => SpanRole.String,
                "num" => SpanRole.Number,
                "privateId" => SpanRole.None,
                "name" when contextual.Contains(word) && !afterDot => SpanRole.Keyword,
                "name" => NameRole(),
                _ when t.Kw is not null => afterDot ? NameRole() : SpanRole.Keyword,
                _ => SpanRole.Punct,
            };
            for (var i = t.S; i < t.E; i++) expected[i] = role;
        }
        FillUnterminated(text, expected, entry.Tokens.Count > 0 ? entry.Tokens.Max(t => t.E) : 0, entry.Unterminated);
        return expected;
    }

    private static List<(string Text, SpanRole[] Roles)> Labelled(string file)
    {
        var result = new List<(string, SpanRole[])>();
        foreach (var entry in Entries(Path.Combine(RenderDir, "FallbackCorpus", file + ".txt")))
        {
            var lines = entry.Split(Nl);
            Assert.True(lines.Length % 2 == 0, $"{file}.txt holds an entry whose last source line has no codes line");
            var source = new List<string>();
            var roles = new List<SpanRole>();
            for (var k = 0; k < lines.Length; k += 2)
            {
                Assert.True(lines[k].Length == lines[k + 1].Length, $"{file}.txt: the codes line under `{lines[k]}` has another length");
                if (k > 0) roles.Add(SpanRole.None);
                source.Add(lines[k]);
                roles.AddRange(lines[k + 1].Select(RoleOfCode));
            }
            result.Add((string.Join(Nl, source), roles.ToArray()));
        }
        return result;
    }

    private static CodeLanguage LanguageOfFile(string file) => file switch
    {
        "json" => CodeLanguage.Json,
        "typescript" => CodeLanguage.TypeScript,
        "xml" => CodeLanguage.Xml,
        "html" => CodeLanguage.Html,
        "rust" => CodeLanguage.Rust,
        _ => CodeLanguage.Bash,
    };

    public static TheoryData<int> PythonIndexes() => Indexes(Python.Value.Count);

    public static TheoryData<int> ScriptIndexes() => Indexes(Script.Value.Count);

    public static TheoryData<int> JsonIndexes() => Indexes(Entries(Path.Combine(RenderDir, "FallbackCorpus", "json-valid.txt")).Count);

    public static TheoryData<string, int> LabelledEntries()
    {
        var data = new TheoryData<string, int>();
        foreach (var file in LabelledFiles)
            for (var i = 0; i < Labelled(file).Count; i++) data.Add(file, i);
        return data;
    }

    private static TheoryData<int> Indexes(int count)
    {
        var data = new TheoryData<int>();
        for (var i = 0; i < count; i++) data.Add(i);
        return data;
    }

    //a splitter that found nothing would leave every theory above with zero rows, so the reviewed counts are pinned here
    [Fact]
    public void Every_corpus_holds_the_entries_that_were_reviewed()
    {
        Assert.Equal(16, Python.Value.Count);
        Assert.Equal(Entries(Path.Combine(RenderDir, "PythonOracle", "corpus.txt")).Count, Python.Value.Count);
        Assert.Equal(12, Script.Value.Count);
        Assert.Equal(Entries(Path.Combine(RenderDir, "JavaScriptOracle", "corpus.txt")).Count, Script.Value.Count);
        Assert.Equal(9, Entries(Path.Combine(RenderDir, "FallbackCorpus", "json-valid.txt")).Count);
        Assert.Equal([5, 9, 9, 9, 13, 18], LabelledFiles.Select(f => Labelled(f).Count));
    }

    [Theory]
    [MemberData(nameof(PythonIndexes))]
    public void The_python_scanner_reads_each_corpus_entry_the_way_tokenize_does(int index)
    {
        var entry = Python.Value[index];
        AssertSameRoles(entry.Source, PythonExpected(entry), ScannerRoles(CodeLanguage.Python, entry.Source), $"python entry {index + 1}, tokenize {entry.Parser}");
    }

    [Theory]
    [MemberData(nameof(ScriptIndexes))]
    public void The_javascript_scanner_reads_each_corpus_entry_the_way_acorn_does(int index)
    {
        var entry = Script.Value[index];
        AssertSameRoles(entry.Source, ScriptExpected(entry), ScannerRoles(CodeLanguage.JavaScript, entry.Source), $"javascript entry {index + 1}, acorn {entry.Acorn}");
    }

    [Theory]
    [MemberData(nameof(JsonIndexes))]
    public void The_json_scanner_reads_each_valid_entry_the_way_the_json_reader_does(int index)
    {
        var text = Entries(Path.Combine(RenderDir, "FallbackCorpus", "json-valid.txt"))[index];
        var bytes = Encoding.UTF8.GetBytes(text);
        var charAt = new int[bytes.Length + 1];
        for (int i = 0, b = 0; i < text.Length;)
        {
            var width = char.IsHighSurrogate(text[i]) ? 2 : 1;
            var count = Encoding.UTF8.GetByteCount(text.AsSpan(i, width));
            for (var k = 0; k < count; k++) charAt[b + k] = i;
            b += count;
            i += width;
        }
        charAt[bytes.Length] = text.Length;

        var expected = new SpanRole[text.Length];
        var covered = new bool[text.Length];
        var reader = new Utf8JsonReader(bytes);
        while (reader.Read())
        {
            var start = (int)reader.TokenStartIndex;
            var quoted = reader.TokenType is JsonTokenType.String or JsonTokenType.PropertyName;
            var length = reader.TokenType is JsonTokenType.StartObject or JsonTokenType.EndObject or JsonTokenType.StartArray or JsonTokenType.EndArray
                ? 1 : reader.ValueSpan.Length + (quoted ? 2 : 0);
            var role = reader.TokenType switch
            {
                JsonTokenType.PropertyName => SpanRole.Variable,
                JsonTokenType.String => SpanRole.String,
                JsonTokenType.Number => SpanRole.Number,
                JsonTokenType.True or JsonTokenType.False or JsonTokenType.Null => SpanRole.Keyword,
                _ => SpanRole.Punct,
            };
            var from = charAt[start];
            var to = charAt[start + length];
            Assert.True(!quoted || (text[from] == '"' && text[to - 1] == '"'), $"the string token at char {from} does not start and end at a quote, so the byte map is wrong");
            for (var i = from; i < to; i++)
            {
                expected[i] = role;
                covered[i] = true;
            }
        }
        for (var i = 0; i < text.Length; i++)
        {
            if (covered[i] || char.IsWhiteSpace(text[i])) continue;
            Assert.True(text[i] is ':' or ',', $"char {i} is outside every token and is not a colon or a comma");
            expected[i] = SpanRole.Punct;
        }
        AssertSameRoles(text, expected, ScannerRoles(CodeLanguage.Json, text), $"json-valid entry {index + 1}");
    }

    [Theory]
    [MemberData(nameof(LabelledEntries))]
    public void The_scanner_reads_each_hand_labelled_entry_as_it_was_labelled(string file, int index)
    {
        var (text, expected) = Labelled(file)[index];
        AssertSameRoles(text, expected, ScannerRoles(LanguageOfFile(file), text), $"{file}.txt entry {index + 1}");
    }

    [Theory]
    [InlineData(CodeLanguage.Json, "{}[]:,/*0123456789.-+eEtrufalsn")]
    [InlineData(CodeLanguage.JavaScript, "abc{}()[]/*=+-.,;:<>?$#0123456789xnlet")]
    [InlineData(CodeLanguage.TypeScript, "abc{}()[]/*=+-.,;:<>?$#0123456789interface")]
    [InlineData(CodeLanguage.Python, "abc{}()[]#=+-.,;:@0123456789jfrbdefclass")]
    [InlineData(CodeLanguage.Xml, "<>/?!=-[]CDATAabc:&;")]
    [InlineData(CodeLanguage.Html, "<>/?!=-[]scriptstyleabc:&;")]
    [InlineData(CodeLanguage.Rust, "abc{}()[]/*=+-.,;:<>!#&|0123456789ufrb")]
    [InlineData(CodeLanguage.Bash, "abc{}()[]$#=|&;<>-0123456789EOFifthen")]
    public void Random_text_never_throws_and_every_span_stays_inside_the_text(CodeLanguage language, string visible)
    {
        var alphabet = visible + " " + (char)10 + (char)9 + (char)92 + (char)34 + (char)39 + (char)96;
        var rng = new Random(13);
        var spans = 0;
        for (var n = 0; n < 2000; n++)
        {
            var text = new string(Enumerable.Range(0, rng.Next(0, 60)).Select(_ => alphabet[rng.Next(alphabet.Length)]).ToArray());
            foreach (var s in FallbackScanner.Classify(Entry(language), text))
            {
                spans++;
                Assert.True(s.Start >= 0 && s.Length > 0 && s.Start + s.Length <= text.Length, $"the span {s} falls outside `{text}`");
            }
        }
        Assert.True(spans > 0, "the scanner returned no spans at all, so the bounds were never checked");
    }

    //a hash after a quote or a variable continues the word, while one after a blank opens a comment. a scanner that paints nothing still fails here
    [Theory]
    [InlineData(@"echo ""$HOME""#tail # a comment", "#tail")]
    [InlineData(@"echo $x#y # a comment", "#y")]
    public void A_hash_that_continues_a_word_opens_no_comment_and_a_hash_after_a_blank_does(string line, string word)
    {
        var roles = ScannerRoles(CodeLanguage.Bash, line);
        var at = line.IndexOf(word, StringComparison.Ordinal);
        var comment = line.LastIndexOf('#');
        Assert.DoesNotContain(SpanRole.Comment, roles.Skip(at).Take(word.Length));
        Assert.All(roles.Skip(comment), r => Assert.Equal(SpanRole.Comment, r));
    }

    //a quoted form crosses a line only where its table row says so. an unclosed json string ends at its line, and a shell or attribute string runs on
    [Theory]
    [InlineData(CodeLanguage.Json, @"{""a"": ""one~""b"": 1}", @"""b""", SpanRole.Variable)]
    [InlineData(CodeLanguage.Xml, @"<item name=""one~two""/>", "two", SpanRole.String)]
    [InlineData(CodeLanguage.Html, "<p title='one~two'></p>", "two", SpanRole.String)]
    [InlineData(CodeLanguage.Bash, @"echo ""one~two""", "two", SpanRole.String)]
    [InlineData(CodeLanguage.Bash, "echo 'one~two'", "two", SpanRole.String)]
    public void A_quoted_form_crosses_a_line_only_where_its_table_row_says(CodeLanguage language, string line, string probe, SpanRole role)
    {
        var text = line.Replace('~', (char)10);
        var at = text.IndexOf(probe, StringComparison.Ordinal);
        Assert.All(ScannerRoles(language, text).Skip(at).Take(probe.Length), r => Assert.Equal(role, r));
    }

    //a quoted form honours a backslash only where its table row says so. a shell single quote and an attribute value close at the next quote
    [Theory]
    [InlineData(CodeLanguage.Bash, "echo 'a^' b", "b", SpanRole.None)]
    [InlineData(CodeLanguage.Xml, @"<a t=""x^""/>", "/>", SpanRole.Punct)]
    [InlineData(CodeLanguage.Html, "<a t='x^'>", ">", SpanRole.Punct)]
    public void A_quoted_form_escapes_only_where_its_table_row_says(CodeLanguage language, string line, string probe, SpanRole role)
    {
        var text = line.Replace('^', (char)92);
        var at = text.LastIndexOf(probe, StringComparison.Ordinal);
        Assert.All(ScannerRoles(language, text).Skip(at).Take(probe.Length), r => Assert.Equal(role, r));
    }

    [Fact]
    public void The_table_names_each_tag_and_extension_once_and_takes_no_shipped_name()
    {
        var tags = FallbackLanguages.All.SelectMany(e => e.Tags).ToList();
        var extensions = FallbackLanguages.All.SelectMany(e => e.Extensions).ToList();
        Assert.Equal(tags.Count, tags.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(extensions.Count, extensions.Distinct(StringComparer.Ordinal).Count());
        foreach (var tag in tags) Assert.Equal(FallbackLanguages.OfTag(tag), SyntaxHighlight.LanguageOfTag(tag));
        foreach (var shipped in new[] { "csharp", "cs", "c#", "csx", "powershell", "pwsh", "ps1", "ps", "posh" })
            Assert.Equal(CodeLanguage.None, FallbackLanguages.OfTag(shipped));
        foreach (var value in Enum.GetValues<CodeLanguage>().Where(v => v is not (CodeLanguage.None or CodeLanguage.CSharp or CodeLanguage.PowerShell)))
            Assert.NotNull(FallbackLanguages.Of(value));
        foreach (var entry in FallbackLanguages.All)
            for (var i = 0; i < entry.Quotes.Length; i++)
                for (var j = i + 1; j < entry.Quotes.Length; j++)
                    Assert.False(entry.Quotes[j].Open.Length > entry.Quotes[i].Open.Length && entry.Quotes[j].Open.StartsWith(entry.Quotes[i].Open, StringComparison.Ordinal),
                        $"{entry.Language} lists the quote {entry.Quotes[i].Open} before the longer {entry.Quotes[j].Open}");
    }
}
