using Gatto.Terminal;

namespace Gatto.Terminal.Tests;

public class ThemeTests
{
    private static readonly Theme T = new(new TermCaps(Rich: true, TrueColor: true));

    //an inline-code chip is orange text with no background, so assert on the SGR background introducer rather than on a named colour
    [Fact]
    public void Chip_carries_no_background_sgr()
    {
        //the background introducer opens both the truecolor and the 256-color background form
        Assert.DoesNotContain("48;", T.Chip("x"), StringComparison.Ordinal);
        Assert.DoesNotContain("48;", new Theme(new TermCaps(true, true), ThemeMode.Light).Chip("x"), StringComparison.Ordinal);
        Assert.DoesNotContain("48;", new Theme(new TermCaps(true, TrueColor: false)).Chip("x"), StringComparison.Ordinal);
    }

    //the values are the terminal's default scheme, and the slot stays so a terminal below truecolour paints its own colour
    [Theory]
    [InlineData(SpanRole.Function, 0xF9, 0xF1, 0xA5, 11)]
    [InlineData(SpanRole.Keyword, 0x16, 0xC6, 0x0C, 10)]
    [InlineData(SpanRole.Variable, 0x16, 0xC6, 0x0C, 10)]
    [InlineData(SpanRole.Comment, 0x13, 0xA1, 0x0E, 2)]
    [InlineData(SpanRole.String, 0x3A, 0x96, 0xDD, 6)]
    [InlineData(SpanRole.Parameter, 0x76, 0x76, 0x76, 8)]
    [InlineData(SpanRole.Punct, 0x76, 0x76, 0x76, 8)]
    [InlineData(SpanRole.Number, 0xF2, 0xF2, 0xF2, 15)]
    [InlineData(SpanRole.None, 0xCC, 0xCC, 0xCC, 7)]
    public void A_shell_role_takes_its_colour_from_the_campbell_scheme(SpanRole role, byte r, byte g, byte b, byte slot)
    {
        foreach (var language in new[] { CodeLanguage.PowerShell, CodeLanguage.Bash })
            Assert.Equal(new RgbColor(r, g, b, slot), Theme.RoleColor(language, role));
    }

    [Fact]
    public void Paint_WrapsWithReset() =>
        Assert.Equal("\x1b[38;2;255;166;77m\x1b[1mhi\x1b[0m", T.Paint("hi", Theme.Accent, bold: true));

    [Fact]
    public void Paint_256Fallback()
    {
        var t256 = new Theme(new TermCaps(true, TrueColor: false));
        Assert.Equal("\x1b[38;5;215mhi\x1b[0m", t256.Paint("hi", Theme.Accent));
    }

    [Fact]
    public void PaintBgLine_FillsToEol() =>
        Assert.Equal("\x1b[48;2;21;21;21m\x1b[K\x1b[38;2;201;205;196mcode\x1b[0m",
            T.PaintBgLine("code", Theme.CodeBlockFg, Theme.CodeBlockBg));

    [Fact]
    public void Chip_NoPadding_TightToText() =>   //orange fg only, no bg band, no padding
        Assert.Equal("\x1b[38;2;220;154;91mx\x1b[0m", T.Chip("x"));

    [Fact]
    public void PARSE_TINT_READS_RRGGBB_AND_FINDS_THE_NEAREST_INDEX()
    {
        Assert.Equal(new RgbColor(0x4B, 0xBE, 0xCD, 74), Theme.ParseTint("#4BBECD"));
        Assert.Equal(139, Theme.ParseTint("#B88AAF").Index256);
        Assert.Equal(30, Theme.ParseTint("#0E7C86").Index256);
        //the oracle's light twin is nearest index 96, cube level (2,1,2), where the constant it replaces carried 91, a flat purple
        Assert.Equal(96, Theme.ParseTint("#834E8F").Index256);
        Assert.Equal(Theme.ParseTint("#4BBECD"), Theme.ParseTint("#4bbecd"));
    }

    [Fact]
    public void ROLE_TINT_IS_THE_FILES_COLOUR_FOR_THE_RUNNING_ROLE_AND_THE_ACCENT_FOR_ANY_OTHER()
    {
        var told = new Theme(new TermCaps(true, true));
        told.SetRoleTint("coder", Theme.ParseTint("#4BBECD"), null);

        Assert.Equal(Theme.ParseTint("#4BBECD"), told.RoleTint("coder"));
        Assert.Equal(Theme.Accent, told.RoleTint("oracle"));
        Assert.Equal(Theme.Accent, told.RoleTint("generalist"));
        foreach (var name in new[] { "coder", "oracle", "generalist" })
            Assert.Equal(Theme.Accent, new Theme(new TermCaps(true, true)).RoleTint(name));
    }

    [Theory]
    [InlineData("#4BBEC")]
    [InlineData("4BBECD#")]
    [InlineData("#GGGGGG")]
    [InlineData("# 1 2 3")]
    [InlineData("#4B BEC")]
    [InlineData("# 1BECD")]
    [InlineData("#4BBE C")]
    [InlineData("#4 BECD")]
    [InlineData("")]
    public void PARSE_TINT_REFUSES_ANY_OTHER_SHAPE(string hex) =>
        Assert.Throws<FormatException>(() => Theme.ParseTint(hex));

    //a role switch tells the same theme again, and the last telling is the one it keeps
    [Fact]
    public void A_SECOND_TELLING_REPLACES_THE_FIRST()
    {
        var theme = new Theme(new TermCaps(true, true));
        theme.SetRoleTint("coder", Theme.ParseTint("#4BBECD"), null);
        theme.SetRoleTint("oracle", Theme.ParseTint("#B88AAF"), null);

        Assert.Equal(Theme.Accent, theme.RoleTint("coder"));
        Assert.Equal(Theme.ParseTint("#B88AAF"), theme.RoleTint("oracle"));

        theme.SetRoleTint("oracle", Theme.Accent, null);
        Assert.Equal(Theme.Accent, theme.RoleTint("oracle"));
    }

    [Fact]
    public void LIGHT_MODE_TAKES_TINT_LIGHT_WHEN_SET_AND_TINT_OTHERWISE()
    {
        var dark = Theme.ParseTint("#B88AAF");
        var light = Theme.ParseTint("#834E8F");
        var withTwin = new Theme(new TermCaps(true, true), ThemeMode.Light);
        withTwin.SetRoleTint("oracle", dark, light);
        var alone = new Theme(new TermCaps(true, true), ThemeMode.Light);
        alone.SetRoleTint("oracle", dark, null);
        var darkTheme = new Theme(new TermCaps(true, true));
        darkTheme.SetRoleTint("oracle", dark, light);

        Assert.Equal(light, withTwin.RoleTint("oracle"));
        Assert.Equal(dark, alone.RoleTint("oracle"));
        Assert.Equal(dark, darkTheme.RoleTint("oracle"));
    }

    [Fact]
    public void A_ROLE_TINT_SURVIVES_PAINT_UNMAPPED()
    {
        var light = new Theme(new TermCaps(true, true), ThemeMode.Light);
        light.SetRoleTint("oracle", Theme.ParseTint("#B88AAF"), null);

        Assert.Equal("\x1b[38;2;184;138;175mx\x1b[0m", light.Paint("x", light.RoleTint("oracle")));
    }

    //the one shipped collision with the light map: the command ink is the coder's teal, and its light twin is the coder's tint_light
    [Fact]
    public void THE_COMMAND_INK_IS_THE_CODERS_TEAL_AND_MAPS_TO_ITS_LIGHT_TWIN()
    {
        Assert.Equal(Theme.ParseTint("#4BBECD"), Theme.Command);
        Assert.Equal(Theme.ParseTint("#0E7C86"), new Theme(new TermCaps(true, true), ThemeMode.Light).Map(Theme.Command));
    }

    [Fact]
    public void THE_ORACLE_TINT_IS_NO_LIGHT_MAP_KEY()
    {
        var oracle = Theme.ParseTint("#B88AAF");
        var statics = typeof(Theme).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(RgbColor))
            .Select(f => (Name: f.Name, Colour: (RgbColor)f.GetValue(null)!))
            .ToList();

        Assert.NotEmpty(statics);
        Assert.DoesNotContain(statics, s => s.Colour.R == oracle.R && s.Colour.G == oracle.G && s.Colour.B == oracle.B);
    }

    [Fact]
    public void CONTRAST_RATIO_IS_WCAG()
    {
        var white = Theme.ParseTint("#FFFFFF");
        var black = Theme.ParseTint("#000000");

        Assert.Equal(21.0, Theme.ContrastRatio(white, black), 2);
        Assert.Equal(21.0, Theme.ContrastRatio(black, white), 2);
        Assert.Equal(1.0, Theme.ContrastRatio(Theme.Accent, Theme.Accent), 2);
        Assert.True(Theme.ContrastRatio(Theme.ParseTint("#4BBECD"), black) > 4.5);
        Assert.True(Theme.ContrastRatio(Theme.ParseTint("#0E7C86"), white) > 4.5);
        Assert.True(Theme.ContrastRatio(Theme.ParseTint("#101010"), black) < 1.5);
    }

    [Fact]
    public void TrueColor_ReflectsCaps()
    {
        Assert.True(T.TrueColor);
        var t256 = new Theme(new TermCaps(true, TrueColor: false));
        Assert.False(t256.TrueColor);
    }

    //light theme and OSC 11 detection

    private static readonly TermCaps RichTrue = new(Rich: true, TrueColor: true);

    [Fact]
    public void DefaultMode_IsDark()
    {
        Assert.Equal(ThemeMode.Dark, new Theme(RichTrue).Mode);
        Assert.Equal(ThemeMode.Light, new Theme(RichTrue, ThemeMode.Light).Mode);
    }

    [Fact]
    public void DarkMode_IsByteIdentical_ToTodaysOutput()
    {
        //an explicit dark mode must emit the same bytes as the default theme, and Map is an identity remap there
        var dark = new Theme(RichTrue, ThemeMode.Dark);
        var def = new Theme(RichTrue);
        Assert.Equal("\x1b[38;2;255;166;77m\x1b[1mhi\x1b[0m", dark.Paint("hi", Theme.Accent, bold: true));
        Assert.Equal(def.Paint("code", Theme.CodeBlockFg, italic: true), dark.Paint("code", Theme.CodeBlockFg, italic: true));
        Assert.Equal(def.PaintBgLine("x", Theme.CodeBlockFg, Theme.CodeBlockBg), dark.PaintBgLine("x", Theme.CodeBlockFg, Theme.CodeBlockBg));
        Assert.Equal(def.Chip("x"), dark.Chip("x"));
        //an identity remap in dark mode for every canonical token
        foreach (var c in new[] { Theme.Accent, Theme.Bright, Theme.Dim, Theme.Ok, Theme.Err, Theme.Warn,
                                  Theme.Command, Theme.CodeInlineFg,
                                  Theme.CodeBlockFg, Theme.CodeBlockBg, Theme.Rule, Theme.UserInputBg,
                                  Theme.DiffAddedBg, Theme.DiffRemovedBg, Theme.ContextPrefix, Theme.ContextReasoning, Theme.ContextPastLine })
            Assert.Equal(c, dark.Map(c));
    }

    [Fact]
    public void LightMode_Remaps_EveryCanonicalToken_ToADifferentColor()
    {
        var light = new Theme(RichTrue, ThemeMode.Light);
        foreach (var c in new[] { Theme.Accent, Theme.Bright, Theme.Dim, Theme.Ok, Theme.Err, Theme.Warn,
                                  Theme.Command, Theme.CodeInlineFg,
                                  Theme.CodeBlockFg, Theme.CodeBlockBg, Theme.Rule, Theme.UserInputBg,
                                  Theme.DiffAddedBg, Theme.DiffRemovedBg, Theme.ContextPrefix, Theme.ContextReasoning, Theme.ContextPastLine })
            Assert.NotEqual(c, light.Map(c));
    }

    [Fact]
    public void LightMode_PaintsWithLightPalette()
    {
        var light = new Theme(RichTrue, ThemeMode.Light);
        //accent in light mode is burnt orange #B25E00 (178,94,0), pinned
        Assert.Equal("\x1b[38;2;178;94;0m\x1b[1mhi\x1b[0m", light.Paint("hi", Theme.Accent, bold: true));
        //code block fg/bg in light mode is dark ink on light paper
        Assert.Equal("\x1b[48;2;240;240;240m\x1b[K\x1b[38;2;36;41;46mcode\x1b[0m",
            light.PaintBgLine("code", Theme.CodeBlockFg, Theme.CodeBlockBg));
    }

    [Fact]
    public void LightMode_256Fallback_UsesLightIndex()
    {
        var light = new Theme(new TermCaps(true, TrueColor: false), ThemeMode.Light);
        Assert.Equal("\x1b[38;5;130mhi\x1b[0m", light.Paint("hi", Theme.Accent));
    }

    [Theory]
    [InlineData("\x1b]11;rgb:ffff/ffff/ffff\x07")]   //a pure white background is light
    [InlineData("\x1b]11;rgb:eeee/eeee/eeee\x1b\\")] //a near-white reply with an ST terminator is light
    [InlineData("rgb:f0/f0/f0")]                       //2-digit components with no escape wrapper are light
    public void ClassifyBackground_LightReplies_AreLight(string reply) =>
        Assert.Equal(ThemeMode.Light, Theme.ClassifyBackground(reply));

    [Theory]
    [InlineData("\x1b]11;rgb:0000/0000/0000\x07")]   //a pure black background is dark
    [InlineData("\x1b]11;rgb:1e1e/1e1e/1e1e\x07")]   //a near-black background is dark
    public void ClassifyBackground_DarkReplies_AreDark(string reply) =>
        Assert.Equal(ThemeMode.Dark, Theme.ClassifyBackground(reply));

    [Theory]
    [InlineData(null)]                 //a timeout gives dark, the safe fallback
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("\x1b]11;rgb:zzzz/0000/0000\x07")]   //malformed hex is dark
    [InlineData("\x1b]11;rgb:ffff/ffff\x07")]        //too few components is dark
    public void ClassifyBackground_NoReplyOrMalformed_IsDark(string? reply) =>
        Assert.Equal(ThemeMode.Dark, Theme.ClassifyBackground(reply));

    [Fact]
    public void ResolveMode_ExplicitWins_WithoutProbing()
    {
        Func<string?> boom = () => throw new Exception("probe must not run for an explicit theme");
        Assert.Equal(ThemeMode.Dark, Theme.ResolveMode("dark", rich: true, boom));
        Assert.Equal(ThemeMode.Light, Theme.ResolveMode("light", rich: true, boom));
    }

    [Fact]
    public void ResolveMode_Auto_OnPlainPath_IsDark_AndNeverProbes()
    {
        //the probe never runs on a plain path, so piped output stays byte-pure
        Func<string?> boom = () => throw new Exception("probe must not run on the plain path");
        Assert.Equal(ThemeMode.Dark, Theme.ResolveMode("auto", rich: false, boom));
    }

    [Fact]
    public void ResolveMode_Auto_OnRichPath_ClassifiesTheProbeReply()
    {
        Assert.Equal(ThemeMode.Light, Theme.ResolveMode("auto", rich: true, () => "\x1b]11;rgb:ffff/ffff/ffff\x07"));
        Assert.Equal(ThemeMode.Dark, Theme.ResolveMode("auto", rich: true, () => "\x1b]11;rgb:0000/0000/0000\x07"));
        Assert.Equal(ThemeMode.Dark, Theme.ResolveMode("auto", rich: true, () => null));   //no reply is a timeout, so dark
    }

    [Fact]   //the selection background SGR the highlight styler injects
    public void SelectionBgOn_is_a_background_sgr_and_differs_light_vs_dark()
    {
        var dark = new Theme(new TermCaps(true, true)).SelectionBgOn;
        var light = new Theme(new TermCaps(true, true), ThemeMode.Light).SelectionBgOn;
        Assert.Contains("48;2;", dark);        //a truecolor background set
        Assert.NotEqual(dark, light);          //the selection background follows the theme mode
    }

    //the chip colour and the 256-colour fallback

    private static bool IsGreyRamp(byte i) => i >= 232;   //indices 232 to 255 are the achromatic ramp in xterm-256

    [Theory]
    [InlineData("#FFA64D")]
    [InlineData("#4BBECD")]
    [InlineData("#0E7C86")]
    [InlineData("#B88AAF")]
    [InlineData("#834E8F")]
    [InlineData("#B25E00")]
    public void ChipColor_never_falls_into_the_greyscale_ramp_for_a_chromatic_role(string hex)
    {
        //a chromatic role's chip must never resolve into the grey ramp, a muted mauve sits close enough for the RGB search to choose it
        var chip = T.ChipColor(Theme.ParseTint(hex));
        Assert.False(IsGreyRamp(chip.Index256),
            $"{hex}: chip #{chip.R:X2}{chip.G:X2}{chip.B:X2} resolved to grey-ramp index {chip.Index256}");
    }

    [Fact]
    public void ChipColor_still_uses_the_greyscale_ramp_for_a_genuinely_achromatic_accent()
    {
        //the ramp still wins for an accent that really is grey, a user can configure one (808080 is exactly achromatic)
        var chip = T.ChipColor(new RgbColor(0x80, 0x80, 0x80, 244));
        Assert.True(IsGreyRamp(chip.Index256), $"expected a grey-ramp index, got {chip.Index256}");
    }

    [Fact]
    public void ChipColor_reproduces_the_legacy_generalist_chip()
    {
        //the derived generalist chip must match the hand-picked CodeInlineFg it replaced, same 256 index and within 8 per channel
        var chip = T.ChipColor(T.Map(Theme.Accent));
        Assert.Equal(Theme.CodeInlineFg.Index256, chip.Index256);
        Assert.InRange(Math.Abs(chip.R - Theme.CodeInlineFg.R), 0, 8);
        Assert.InRange(Math.Abs(chip.G - Theme.CodeInlineFg.G), 0, 8);
        Assert.InRange(Math.Abs(chip.B - Theme.CodeInlineFg.B), 0, 8);
    }
}
