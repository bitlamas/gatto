using System.Text;

namespace Gatto.Tests.Census;

//every wizard face built in production must be handed the resolved glyph set. the argument is optional, so no other guard sees an omission
public class WizardWiringCensusTests
{
    //the three faces are listed by name. a suffix rule would gain silent exceptions the first time an unrelated class took the name
    private static readonly string[] Faces =
        ["TuiWizardSurface", "PrompterWizardSurface", "SetupFace"];

    //the wizard-face constructions that omit the glyph set, as face@offset entries. matching uses balanced parens, these calls span lines and nest calls
    internal static IReadOnlyList<string> Unwired(string text)
    {
        var found = new List<string>();
        //blank literals and comments first, the length stays so offsets still name the place. a quoted construction isn't one, and reading it reports its own pattern
        var source = SourceMask.Mask(text);

        foreach (var face in Faces)
        {
            var needle = "new ";
            for (var i = source.IndexOf(needle, StringComparison.Ordinal); i >= 0;
                 i = source.IndexOf(needle, i + 1, StringComparison.Ordinal))
            {
                var open = source.IndexOf('(', i);
                if (open < 0) continue;
                //the type name sits between new and the opening parenthesis, qualified or not
                var name = source[(i + needle.Length)..open].Trim();
                if (name != face && !name.EndsWith("." + face, StringComparison.Ordinal)) continue;

                var args = Balanced(source, open);
                if (args is null || args.Contains("glyphs:", StringComparison.Ordinal)) continue;
                found.Add($"{face}@{i}");
            }
        }

        return found;
    }

    //the argument text of the call at open, or null when it never closes. a string is scanned char by char, an unbalanced paren inside it would end the list early
    private static string? Balanced(string s, int open)
    {
        var depth = 0;
        var sb = new StringBuilder();
        for (var i = open; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"')
            {
                var j = i + 1;
                while (j < s.Length && (s[j] != '"' || s[j - 1] == '\\')) j++;
                sb.Append(s, i, Math.Min(j, s.Length - 1) - i + 1);
                i = j;
                continue;
            }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return sb.ToString();
            sb.Append(c);
        }
        return null;
    }

    [Fact]
    public void EVERY_PRODUCTION_WIZARD_FACE_IS_HANDED_THE_RESOLVED_SET()
    {
        var offenders = new List<string>();
        foreach (var file in SourceTree.ProductionFiles())
        {
            var rel = Path.GetRelativePath(SourceTree.RepoRoot(), file);
            foreach (var hit in Unwired(SourceTree.Read(file)))
                offenders.Add($"{rel}: {hit}");
        }

        Assert.True(offenders.Count == 0,
            "a wizard face is built without the run's glyph set, so its whole walk draws the Unicode "
            + "set whatever the config says:\n" + string.Join("\n", offenders));
    }

    //a known match for every spelling the source uses (testing one spelling alone would certify only the matcher's blind spot)
    [Theory]
    [InlineData("var f = new TuiWizardSurface(surface, keys, theme, v, b);")]
    [InlineData("var f = new Setup.Tui.TuiWizardSurface(surface, keys, theme, v, b);")]
    [InlineData("var f = new Gatto.Cli.Setup.Tui.TuiWizardSurface(surface, keys, theme, v, b);")]
    [InlineData("var f = new SetupFace(surface, theme, keys, output);")]
    [InlineData("var f = new Setup.PrompterWizardSurface(prompter, w, theme);")]
    [InlineData("var f = new TuiWizardSurface(\n    surface, keys, theme,\n    v, b,\n    pulse: p);")]
    public void THE_CENSUS_FIRES_ON_A_FACE_BUILT_WITHOUT_A_SET(string planted) =>
        Assert.NotEmpty(Unwired(planted));

    //a wired construction must not fire, including one that spans lines or holds a nested call
    [Theory]
    [InlineData("var f = new TuiWizardSurface(surface, keys, theme, v, b, glyphs: g);")]
    [InlineData("var f = new Setup.Tui.TuiWizardSurface(\n    surface, keys, theme, v, b,\n"
                + "    pulse: () => new TimerPurrPulse(),\n    glyphs: CommandBanner.GlyphsFor(home));")]
    [InlineData("var s = \"new TuiWizardSurface(a, b)\";")]
    [InlineData("var x = new Theme(caps);")]
    public void AND_IT_DOES_NOT_FIRE_ON_A_WIRED_ONE(string source) =>
        Assert.Empty(Unwired(source));

    //the face must report the glyph set it was built with. read it through the interface, a shadowed property would pass a concrete read
    [Fact]
    public void EVERY_FACE_REPORTS_THE_SET_IT_WAS_BUILT_WITH()
    {
        var ascii = Gatto.Terminal.GlyphSet.Ascii;
        var theme = new Gatto.Terminal.Theme(new Gatto.Terminal.TermCaps(true, true));
        var surface = new Gatto.Tests.Fakes.RecordingSurface { Width = 80 };

        Gatto.Cli.Setup.IWizardSurface[] faces =
        [
            new Gatto.Cli.Setup.SetupFace(surface, theme, new NoKeys(), TextWriter.Null,
                glyphs: ascii),
            new Gatto.Cli.Setup.PrompterWizardSurface(new NoPrompter(), TextWriter.Null, theme,
                glyphs: ascii),
            new Gatto.Cli.Setup.Tui.TuiWizardSurface(surface, new NoKeys(), theme, "0.5.0", "1a2b3c4",
                glyphs: ascii),
        ];

        foreach (var face in faces)
            Assert.Same(ascii, face.Glyphs);
    }

    private sealed class NoKeys : Gatto.Terminal.IKeySource
    {
        public bool KeyAvailable => false;
        public ConsoleKeyInfo ReadKey() => throw new InvalidOperationException("no key is scripted");
    }

    private sealed class NoPrompter : Gatto.Repl.IWizardPrompter
    {
        public Gatto.Repl.WizardAnswer? AskOne(Gatto.Repl.WizardAsk ask) =>
            throw new InvalidOperationException("this face is only asked for its glyph set");
    }
}
