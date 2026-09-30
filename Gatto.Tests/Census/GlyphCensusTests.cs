using System.Text;

namespace Gatto.Tests.Census;

//every non-ASCII glyph on a render path must come from the table, so the count only ratchets down. a rise means a new glyph literal arrived.
public class GlyphCensusTests
{
    //this declaration is the only place to change the count, every copy change must claim it
    private const int EmDashes = 170;

    //a ceiling the same census measured, so it may only fall. the other figure is not comparable, a different counting method made it
    private const int PreWave = 658;

    //keep the figure later work was sized against, though nothing asserts against it
    private const int PlanReported = 243;

    //name each exemption by file with its reason, a glob would exempt the next file that matches it
    private static readonly (string File, string Why)[] Exempt =
    [
        ("Gatto.Terminal/GlyphSet.cs", "IS the table; its literals are the two sets"),
        ("Gatto/Roles/EngineMarks.cs", "the table's second home, for Roles: it cannot reach "
            + "GlyphSet without the first Roles-to-Repl edge, so its Unicode fallback is the "
            + "same kind of literal GlyphSet.cs carries"),
        ("Gatto/Repl/Cats.cs", "the Unicode set's ROLE data: the coder's and oracle's CATS, drawings "
            + "the table does not carry. Under the ASCII set they are unreachable - `For` returns "
            + "the table's one ruled cat for every role. ⚰ The role FACES left when every role took one face, so this "
            + "exemption covers the banner art alone"),
        //remove an exemption when its file's remaining literals all come from the table or are excluded by rule
        ("Gatto/Core/Loop/Compactor.cs", "the elided-arguments note inside a compaction summary: text "
            + "the MODEL reads, not chrome a terminal draws"),
        ("Gatto/Core/Memory/MemoryIndex.cs", "MEMORY.md's index lines, written to a file the model "
            + "reads back; a twin here would rewrite stored notes, not a screen"),
        ("Gatto/Core/Tools/SearchTools.cs", "provider payloads and their words, not gatto's chrome"),
        ("Gatto/Core/Home/GattoConfig.cs", "error copy about a file, read before any painter exists"),
        ("Gatto/Core/GattoVersion.cs", "the build stamp"),
        ("Gatto/Repl/Render/SpanWrap.cs", "a NO-BREAK SPACE in layout logic. Its twin would be a "
            + "plain space, which CHANGES where a line breaks: a behaviour change, not a glyph swap"),
        ("Gatto/Cli/Setup/WizardRows.cs", "a FIGURE SPACE, chosen because it is the width of a "
            + "digit. Same reason as the no-break space: it is spacing, and its twin would move text"),
        ("Gatto.Terminal/TerminalTitle.cs", "the window TITLE, which the host draws and gatto's "
            //a name inside a string literal is not stripped the way a comment is, so describe the glyph and leave names out
            + "frame never does. Its own category, and decoration rather than layout"),
        ("Gatto/Cli/Setup/HardwareNaming.cs", "vendor marks inside a hardware NAME: data that is "
            + "stripped rather than drawn"),
        ("Gatto/Core/Tools/RecallMemoryTool.cs", "a control character at a sanitizer boundary, "
            + "never drawn"),
    ];

    //non-exempt literals that hold a non-ASCII character once em dashes are removed, each reported as path:line with the literal
    private static IReadOnlyList<string> Sites()
    {
        var root = SourceTree.RepoRoot();
        var exempt = Exempt.Select(e => e.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();

        foreach (var path in SourceTree.ProductionFiles())
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (exempt.Contains(rel)) continue;
            found.AddRange(NonAsciiLiterals(File.ReadAllText(path)).Select(l => $"{rel}:{l}"));
        }

        return found;
    }

    //the em dash is excluded by rule, since a glyph twin would keep it while copy loses it by rewording. its own test counts it exactly
    private const char EmDash = '\u2014';

    //scan with a state machine, since a pattern cannot see what precedes it. count a raw glyph and its escape alike, and an interpolated string once.

    //every literal with its starting line, comments skipped (both counts read this one population, so they cannot describe different trees)
    private static IReadOnlyList<(int Line, string Text)> AllLiterals(string source)
    {
        var found = new List<(int, string)>();
        var line = 1;
        var i = 0;

        while (i < source.Length)
        {
            var c = source[i];

            if (c == '\n') { line++; i++; continue; }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/'))
                {
                    if (source[i] == '\n') line++;
                    i++;
                }
                i = Math.Min(source.Length, i + 2);
                continue;
            }

            var at = line;
            var literal = ReadLiteral(source, ref i, ref line);
            if (literal is null) { i++; continue; }
            found.Add((at, literal));
        }

        return found;
    }

    //the literals that still hold a non-ASCII character once em dashes are removed, since the em dash has its own rule and count
    internal static IReadOnlyList<string> NonAsciiLiterals(string source) =>
        [.. AllLiterals(source)
            .Where(l => CarriesNonAscii(StripEmDashes(l.Text)))
            .Select(l => $"{l.Line} {Short(l.Text)}")];

    //read one literal starting at i, or return null when none starts there. any run of $ or @ prefixes on a string or char literal is handled
    private static string? ReadLiteral(string s, ref int i, ref int line)
    {
        var start = i;
        var j = i;
        while (j < s.Length && (s[j] == '$' || s[j] == '@')) j++;
        var prefixed = j > i;

        if (j >= s.Length) return null;
        if (s[j] != '"' && s[j] != '\'') return prefixed ? null : null;

        var verbatim = s[i..j].Contains('@');
        var quote = s[j];

        //a raw string opens with three or more quotes and closes at a run of the same length or longer. a shorter run inside the body does not close it.
        if (quote == '"' && j + 2 < s.Length && s[j + 1] == '"' && s[j + 2] == '"')
        {
            var open = 0;
            while (j + open < s.Length && s[j + open] == '"') open++;
            var body = j + open;
            var k = body;
            while (k < s.Length)
            {
                if (s[k] == '"')
                {
                    var run = 0;
                    while (k + run < s.Length && s[k + run] == '"') run++;
                    if (run >= open) { k += run; break; }
                    k += run;
                    continue;
                }
                if (s[k] == '\n') line++;
                k++;
            }
            i = k;
            return s[start..Math.Min(k, s.Length)];
        }

        var p = j + 1;
        while (p < s.Length)
        {
            if (verbatim)
            {
                if (s[p] == '"')
                {
                    if (p + 1 < s.Length && s[p + 1] == '"') { p += 2; continue; }
                    p++;
                    break;
                }
                if (s[p] == '\n') line++;
                p++;
                continue;
            }

            if (s[p] == '\\') { p += 2; continue; }
            if (s[p] == quote) { p++; break; }
            if (s[p] == '\n') { line++; p++; break; }   //an unterminated non-verbatim literal ends at the newline, so the scanner does not read the rest of the file as one literal.
            p++;
        }

        i = p;
        return s[start..Math.Min(p, s.Length)];
    }

    //report true when the literal holds a character above 0x7F or spells one as an escape (both spellings count, so neither hides a glyph)
    internal static bool CarriesNonAscii(string literal)
    {
        foreach (var ch in literal)
            if (ch > 0x7F) return true;

        for (var i = 0; i + 1 < literal.Length; i++)
        {
            if (literal[i] != '\\') continue;
            var kind = literal[i + 1];
            var digits = kind switch { 'u' => 4, 'x' => 2, 'U' => 8, _ => 0 };
            if (digits == 0) { i++; continue; }
            var from = i + 2;
            var take = 0;
            while (take < digits && from + take < literal.Length && Uri.IsHexDigit(literal[from + take]))
                take++;
            if (take > 0 && Convert.ToInt32(literal.Substring(from, take), 16) > 0x7F) return true;
            i = from + take - 1;
        }

        return false;
    }

    //remove the em dash character and its escape form, so a literal keeps only the glyphs the census counts
    private static string StripEmDashes(string literal) =>
        literal.Replace(EmDash.ToString(), "")
            .Replace("\\u2014", "", StringComparison.OrdinalIgnoreCase);

    //every em dash the scanner finds, over the same files and exemptions as the census, so the two numbers describe one population
    private static int CountEmDashes()
    {
        var root = SourceTree.RepoRoot();
        var exempt = Exempt.Select(e => e.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var path in SourceTree.ProductionFiles())
        {
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (exempt.Contains(rel)) continue;
            foreach (var hit in AllLiterals(File.ReadAllText(path)))
                n += hit.Text.Count(c => c == EmDash);
        }
        return n;
    }

    private static string Short(string literal) =>
        literal.Length <= 60 ? literal : literal[..57] + "...";

    //no non-exempt literal may hold a glyph outside the table (the em dash is excluded by rule and counted by its own test)
    [Fact]
    public void NO_RENDER_PATH_CARRIES_A_GLYPH_THE_TABLE_DOES_NOT_OWN()
    {
        var sites = Sites();

        Assert.True(sites.Count == 0,
            $"the census found {sites.Count} sites that the table does not own:\n"
            + string.Join("\n", sites.Take(20)));
    }

    //pin the em dash count exactly, so a reword that drops one must update this number (a cap would let it drop unclaimed)
    [Fact]
    public void THE_EM_DASHES_ARE_COUNTED_EXACTLY()
    {
        Assert.Equal(EmDashes, CountEmDashes());
        //keep the two figures without asserting against them, comparing numbers from different instruments fails for reasons unrelated to the tree
        Assert.True(PreWave > 0 && PlanReported > 0);
    }

    //a known match for each spelling of a glyph. the escape form is legal C# and displays identically, so a matcher of raw characters alone cannot see it
    [Theory]
    [InlineData("var ok = \"\u2713\";")]                    //the rendered spelling of the glyph.
    [InlineData("var ok = \"\\u2713\";")]                   //the escape spelling, which a census matching raw characters alone cannot see.
    [InlineData("var ok = '\u00b7';")]
    [InlineData("var ok = @\"a \u2500 b\";")]
    [InlineData("var ok = $\"x {n} \u00b7 y\";")]
    public void THE_CENSUS_FIRES_ON_A_PLANTED_GLYPH(string source)
    {
        Assert.NotEmpty(NonAsciiLiterals(source));
    }

    //add cases that must not fire, since a census that fires on everything proves nothing. a glyph in a comment is the case that matters most.
    [Theory]
    [InlineData("var plain = \"just ascii\";")]
    [InlineData("// a comment with \u2713 and \u00b7 in it")]
    [InlineData("/// <summary>\u2b25 a doc block with \u26a0 marks</summary>")]
    [InlineData("/* a block comment with \u2500 */")]
    [InlineData("var n = 2713;")]
    public void AND_IT_DOES_NOT_FIRE_ON_ORDINARY_SOURCE(string source)
    {
        Assert.Empty(NonAsciiLiterals(source));
    }

    //an exempt file's real content must still fire the scan, or the exemption names a file that would have passed anyway.
    [Fact]
    public void THE_EXEMPTIONS_ARE_LOAD_BEARING()
    {
        var root = SourceTree.RepoRoot();

        foreach (var (file, why) in Exempt)
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"the census exempts {file}, which is not in the tree");
            Assert.NotEmpty(why);
            Assert.NotEmpty(NonAsciiLiterals(File.ReadAllText(path)));
        }
    }
}
