using Gatto.Cli;
using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Tests;

//every command opens with the banner cat. these tests cover the composition and the one place where it must not appear.
public class CommandBannerTests
{
    //the header holds the invocation as one run, gatto serve stop, and the dot separates only asides. one glyph must not stand for two relationships.
    [Fact]
    public void THE_IDENTITY_LINE_IS_THE_INVOCATION_THEN_ASIDES()
    {
        var w = new StringWriter();
        CommandBanner.WriteHeader(w, null, "serve stop", GlyphSet.Unicode,
            stamp: new VersionStamp("0.5.1", "f22c6ea", Dev: false));

        var line = w.ToString().Split('\n')[1];
        Assert.Equal("(^\u00b7 \u00b7^)\u2cca  gatto serve stop \u00b7 v0.5.1 \u00b7 build f22c6ea", line.TrimEnd('\r'));
    }

    //the header takes no subject, so serve start <model> names the model once, in the body.
    [Fact]
    public void THE_HEADER_TAKES_NO_SUBJECT()
    {
        var takesSubject = typeof(CommandBanner)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(m => m.Name is "Write" or "WriteHeader")
            .SelectMany(m => m.GetParameters())
            .Any(p => p.Name == "subject");

        Assert.False(takesSubject,
            "a header form still takes a subject. The model id lives in the body, so the "
            + "parameter has no caller and its presence invites one.");
    }

    private static Theme RichTheme() => new(new TermCaps(Rich: true, TrueColor: true), ThemeMode.Dark);

    private static string Render(Theme? theme, string command)
    {
        var w = new StringWriter();
        CommandBanner.Write(w, theme, command);
        return w.ToString();
    }

    [Fact]
    public void The_banner_opens_with_the_SAME_cat_the_REPL_draws()
    {
        //the banner must render the shared Cats art. a second copy of the ascii art is forbidden.
        var text = Render(null, "doctor");

        foreach (var line in Cats.For("generalist", glyphs: GlyphSet.Unicode).Split('\n'))
            Assert.Contains(line.TrimEnd('\r'), text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_GENERALIST_cat_is_used_whatever_else_is_configured()
    {
        //a command runs under no role, so the banner must not borrow another role's face.
        var text = Render(null, "status");

        Assert.DoesNotContain("▀", text, StringComparison.Ordinal);   //this glyph belongs to the coder role.
        Assert.DoesNotContain("☆", text, StringComparison.Ordinal);   //this glyph belongs to the oracle role.
    }

    [Fact]
    public void The_identity_line_names_the_COMMAND_and_the_build()
    {
        var text = Render(null, "audition");

        var line = text.Split('\n').Single(l => l.StartsWith("gatto", StringComparison.Ordinal));
        Assert.Contains("audition", line, StringComparison.Ordinal);
        Assert.Contains(Gatto.Core.GattoVersion.String, line, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_BANNERS_IDENTITY_LINE_IS_THE_INVOCATION_THEN_ASIDES()
    {
        //a join can't leave an empty slot, but two asides can still meet, so keep the double separator check
        var line = Render(null, "doctor").Split('\n')
            .Single(l => l.StartsWith("gatto", StringComparison.Ordinal));

        Assert.DoesNotContain("·  ·", line, StringComparison.Ordinal);
        Assert.StartsWith("gatto doctor · v", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_banner_OPENS_with_a_blank_row_so_the_cat_never_touches_the_typed_command()
    {
        //the blank row keeps the cat from reading as a continuation of the line the user just typed
        var lines = Render(null, "audition").Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        Assert.Equal("", lines[0]);
        Assert.NotEqual("", lines[1]);   //and exactly one, since the art starts right after.
    }

    [Fact]
    public void The_art_and_the_text_are_separated_by_EXACTLY_one_blank_row()
    {
        //the REPL banner asserts the same spacing, so the two surfaces can't drift apart
        var lines = Render(null, "doctor").Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var identity = lines.FindIndex(l => l.StartsWith("gatto ", StringComparison.Ordinal));

        Assert.True(identity > 0);
        Assert.Equal("", lines[identity - 1]);
        Assert.NotEqual("", lines[identity - 2]);   //the row above the blank is the last art row
    }

    [Fact]
    public void With_NO_THEME_the_banner_is_byte_pure()
    {
        //redirected stdout gets the null theme, so the banner holds no escape code
        Assert.DoesNotContain("", Render(null, "doctor"), StringComparison.Ordinal);
    }

    [Fact]
    public void With_a_theme_the_cat_wears_the_accent()
    {
        var theme = RichTheme();
        var text = Render(theme, "doctor");

        Assert.Contains(theme.Paint("  /l、", Theme.Accent), text, StringComparison.Ordinal);
        Assert.Contains(theme.Paint("gatto", Theme.Accent, bold: true), text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_banner_carries_NO_EMOJI_only_the_terminal_glyph_vocabulary()
    {
        //the kaomoji stays inside the BMP, so the surrogate check is what keeps an emoji out
        Assert.DoesNotContain(Render(null, "audition"), char.IsSurrogate);
    }

    private static string Header(Theme? theme, string command, GlyphSet g)
    {
        var w = new StringWriter();
        CommandBanner.WriteHeader(w, theme, command, g);
        return w.ToString();
    }

    //the one-liner must hold the banner's identity line byte for byte. compare two renders, since a typed string can be wrong on both sides the same way
    [Theory]
    [InlineData("doctor")]
    [InlineData("audition")]
    [InlineData("serve start")]
    public void THE_ONE_LINER_CARRIES_THE_BANNERS_OWN_IDENTITY_LINE(string command)
    {
        //key the finder on the version aside, the half of the identity line both forms hold
        static string IdentityOf(string text) => text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Single(l => l.Contains(" · v", StringComparison.Ordinal));

        var banner = IdentityOf(Render(null, command));
        var header = Header(null, command, GlyphSet.Unicode);

        Assert.EndsWith(banner, header.Trim('\n', '\r'), StringComparison.Ordinal);
    }

    //the face, exactly two spaces, then the text, on both glyph sets
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void THE_ONE_LINER_OPENS_WITH_THE_HEADER_FACE_AND_TWO_SPACES(bool ascii)
    {
        var g = ascii ? GlyphSet.Ascii : GlyphSet.Unicode;

        var row = Header(null, "doctor", g).Split('\n').Select(l => l.TrimEnd('\r'))
            .Single(l => l.Length > 0);

        Assert.StartsWith($"{g.Header}  gatto doctor", row, StringComparison.Ordinal);
    }

    //assert the ear line in the banner too, so the absence here can't pass vacuously
    [Fact]
    public void THE_ONE_LINER_DRAWS_NO_CAT_AND_THE_BANNER_STILL_DOES()
    {
        var earLine = Cats.For("generalist", glyphs: GlyphSet.Unicode).Split('\n')[0].TrimEnd('\r');

        Assert.DoesNotContain(earLine, Header(null, "doctor", GlyphSet.Unicode), StringComparison.Ordinal);
        Assert.Contains(earLine, Render(null, "doctor"), StringComparison.Ordinal);
    }

    //a blank row before and after the header, since a command sits in the middle of scrollback
    [Fact]
    public void THE_ONE_LINER_IS_A_BLANK_A_ROW_AND_A_BLANK()
    {
        var lines = Header(null, "status", GlyphSet.Unicode).Split('\n')
            .Select(l => l.TrimEnd('\r')).ToList();

        Assert.Equal("", lines[0]);
        Assert.NotEqual("", lines[1]);
        Assert.Equal("", lines[2]);
    }

    //the face follows the banner: accent under a theme, byte-pure without one
    [Fact]
    public void THE_ONE_LINERS_FACE_WEARS_THE_ACCENT_AND_A_PLAIN_RUN_STAYS_BYTE_PURE()
    {
        var theme = RichTheme();

        Assert.Contains(theme.Paint(GlyphSet.Unicode.Header, Theme.Accent),
            Header(theme, "doctor", GlyphSet.Unicode), StringComparison.Ordinal);
        Assert.DoesNotContain("\x1b", Header(null, "doctor", GlyphSet.Unicode), StringComparison.Ordinal);
    }

}

//machine output must never get the cat, and this class swaps the console, so it runs in the serialized collection
[Collection("e2e")]
public class BannerNeverBreaksAParserTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-banner-").FullName;
    private readonly TextWriter _origOut = Console.Out;
    private readonly TextWriter _origErr = Console.Error;

    public BannerNeverBreaksAParserTests() => Environment.SetEnvironmentVariable("GATTO_HOME", _home);

    public void Dispose()
    {
        Console.SetOut(_origOut);
        Console.SetError(_origErr);
        Environment.SetEnvironmentVariable("GATTO_HOME", null);
        try { Directory.Delete(_home, true); } catch { }
    }

    private static async Task<(int Exit, string Out)> Run(params string[] argv)
    {
        var stdout = new StringWriter();
        Console.SetOut(stdout);
        Console.SetError(new StringWriter());
        var exit = await GattoApp.RunAsync(argv);
        return (exit, stdout.ToString());
    }

    [Fact]
    public async Task serve_status_json_still_PARSES_as_one_JSON_object()
    {
        //tooling parses serve status --json, so art above it breaks somebody else's program
        var (exit, text) = await Run("serve", "status", "--json");

        Assert.Equal(0, exit);
        var doc = System.Text.Json.JsonDocument.Parse(text);   //the parse throws if anything precedes the json.
        Assert.Equal(System.Text.Json.JsonValueKind.Object, doc.RootElement.ValueKind);
        Assert.DoesNotContain("/l、", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task version_stays_a_bare_line_the_release_gate_can_read()
    {
        //release scripts regex-check this line, so it must stay one bare line with no art.
        var (exit, text) = await Run("--version");

        Assert.Equal(0, exit);
        Assert.Single(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("gatto " + Gatto.Core.GattoVersion.String, text.Trim(), StringComparison.Ordinal);
        Assert.DoesNotContain("/l、", text, StringComparison.Ordinal);
    }

    //the header is reachable, so the checks above catch an unwanted one. read the glyph set through GlyphsFor, since a harness without WT_SESSION is ascii
    [Fact]
    public async Task But_a_HUMAN_facing_command_really_does_open_with_a_face()
    {
        var g = CommandBanner.GlyphsFor(_home);

        var (_, oneLiner) = await Run("serve", "status");

        Assert.Contains(g.Header + "  gatto", oneLiner, StringComparison.Ordinal);
        Assert.Contains("serve status", oneLiner, StringComparison.Ordinal);
        Assert.Contains(Gatto.Core.GattoVersion.String, oneLiner, StringComparison.Ordinal);
        Assert.DoesNotContain(g.Cat[0].Trim(), oneLiner, StringComparison.Ordinal);

        //the refusal comes before the banner, so a missing input is never reported under a cat
        var (exit, quiet) = await Run("model");

        Assert.Equal(2, exit);
        Assert.DoesNotContain(g.Cat[0].Trim(), quiet, StringComparison.Ordinal);
    }

    //the uninstall banner has no coverage here, it needs a threaded context with a screen and a key source
}
