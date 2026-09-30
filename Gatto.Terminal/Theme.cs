using System.Globalization;

namespace Gatto.Terminal;

//which palette a Theme paints with. light re-picks the tints so dark text stays readable on white
public enum ThemeMode { Dark, Light }

//the only place colour values live. the static fields are the dark palette and the keys callers pass, Map turns them light only in Light mode
public sealed class Theme(TermCaps caps, ThemeMode mode = ThemeMode.Dark)
{
    public bool TrueColor => caps.TrueColor;
    public ThemeMode Mode => mode;

    //canonical dark palette, also the lookup keys for Map
    public static readonly RgbColor Accent       = new(0xFF, 0xA6, 0x4D, 215);
    public static readonly RgbColor Bright       = new(0xE8, 0xE8, 0xE8, 254);
    public static readonly RgbColor Dim          = new(0x99, 0x99, 0x99, 246);
    public static readonly RgbColor Thought      = new(0xB7, 0xB7, 0xB7, 249);
    public static readonly RgbColor Ok           = new(0x97, 0xCA, 0x72, 113);
    public static readonly RgbColor Err          = new(0xEC, 0x60, 0x6B, 203);
    public static readonly RgbColor Warn         = new(0xF0, 0xC3, 0x70, 215);
    public static readonly RgbColor RoleCoder    = new(0x4B, 0xBE, 0xCD, 74);
    public static readonly RgbColor RoleOracle   = new(0xB8, 0x8A, 0xAF, 139);
    public static readonly RgbColor CodeInlineFg = new(0xDC, 0x9A, 0x5B, 173);
    public static readonly RgbColor CodeBlockFg  = new(0xC9, 0xCD, 0xC4, 251);
    public static readonly RgbColor CodeBlockBg  = new(0x15, 0x15, 0x15, 233);
    public static readonly RgbColor Rule         = new(0x3F, 0x3F, 0x3F, 237);
    public static readonly RgbColor UserInputBg  = new(0x22, 0x1B, 0x12, 235);   //band behind a submitted message, warm charcoal with an amber tint
    public static readonly RgbColor ToolName     = new(0xFF, 0xFF, 0xFF, 231);   //the tool call name, the subject of its row
    public static readonly RgbColor ToolArgs     = new(0xC9, 0xAB, 0x36, 179);   //the argument preview shown after a tool call name
    public static readonly RgbColor CampbellBrightYellow = new(0xF9, 0xF1, 0xA5, 11);   //colours PSReadLine gives PowerShell by default. the last number is its ANSI slot
    public static readonly RgbColor CampbellBrightGreen  = new(0x16, 0xC6, 0x0C, 10);
    public static readonly RgbColor CampbellGreen        = new(0x13, 0xA1, 0x0E, 2);
    public static readonly RgbColor CampbellCyan         = new(0x3A, 0x96, 0xDD, 6);
    public static readonly RgbColor CampbellBrightBlack  = new(0x76, 0x76, 0x76, 8);
    public static readonly RgbColor CampbellBrightWhite  = new(0xF2, 0xF2, 0xF2, 15);
    public static readonly RgbColor CampbellWhite        = new(0xCC, 0xCC, 0xCC, 7);
    public static readonly RgbColor VsKeyword           = new(0x56, 0x9C, 0xD6, 74);    //colours Visual Studio's dark theme gives C#
    public static readonly RgbColor VsType              = new(0x4E, 0xC9, 0xB0, 79);
    public static readonly RgbColor VsString            = new(0xD6, 0x9D, 0x85, 180);
    public static readonly RgbColor VsNumber            = new(0xB5, 0xCE, 0xA8, 151);
    public static readonly RgbColor VsComment           = new(0x57, 0xA6, 0x4A, 71);
    public static readonly RgbColor VsMethod            = new(0xDC, 0xDC, 0xAA, 187);
    public static readonly RgbColor VsPunct             = new(0xB4, 0xB4, 0xB4, 249);
    public static readonly RgbColor SelectionBg  = new(0x2D, 0x3B, 0x55, 60);    //drag-select ground, muted slate blue
    public static readonly RgbColor DiffAddedBg   = new(0x1B, 0x2E, 0x1D, 22);   //the ground under an added row of an edit
    public static readonly RgbColor DiffRemovedBg = new(0x38, 0x1D, 0x21, 52);   //the ground under a removed row of an edit

    //light palette, dark-on-light re-picks. body tints aim for 4.5:1 or better on white. tokens ending in Bg are bands, the contrast is on the paired fg
    public static readonly RgbColor AccentLight       = new(0xB2, 0x5E, 0x00, 130);   //burnt orange, about 4.7:1 on white
    public static readonly RgbColor BrightLight       = new(0x11, 0x11, 0x11, 233);   //near-black ink for bold text
    public static readonly RgbColor DimLight          = new(0x5F, 0x5F, 0x5F, 59);    //secondary gray, about 6.4:1 on white
    public static readonly RgbColor OkLight           = new(0x2E, 0x7D, 0x32, 28);    //the ok green, about 5.1:1 on white
    public static readonly RgbColor ErrLight          = new(0xC6, 0x28, 0x28, 160);   //the error red, about 5.6:1 on white
    public static readonly RgbColor WarnLight         = new(0x94, 0x6A, 0x00, 136);   //warn amber, about 4.9:1 on white
    public static readonly RgbColor RoleCoderLight    = new(0x0E, 0x7C, 0x86, 30);    //the coder role teal, about 5.0:1 on white
    public static readonly RgbColor RoleOracleLight   = new(0x83, 0x4E, 0x8F, 91);    //the oracle role mauve, about 6.1:1 on white
    public static readonly RgbColor CodeInlineFgLight = new(0x9A, 0x3E, 0x00, 130);   //inline code text, about 6.9:1 on the light chip
    public static readonly RgbColor CodeBlockFgLight  = new(0x24, 0x29, 0x2E, 235);   //ink on the code paper
    public static readonly RgbColor CodeBlockBgLight  = new(0xF0, 0xF0, 0xF0, 255);   //the code block's paper colour
    public static readonly RgbColor RuleLight         = new(0xBD, 0xBD, 0xBD, 250);   //subtle divider line
    public static readonly RgbColor UserInputBgLight  = new(0xFB, 0xF1, 0xE3, 230);   //warm off-white band under the input
    public static readonly RgbColor ToolNameLight     = new(0x11, 0x11, 0x11, 233);   //near-black, the same ink as BrightLight
    public static readonly RgbColor ToolArgsLight     = new(0x6A, 0x67, 0x4D, 59);    //olive, about 5.7:1 on white
    public static readonly RgbColor CampbellBrightYellowLight = new(0x7A, 0x5F, 0x00, 94);    //light pairs for code, each at least 4.5:1 on the light code band
    public static readonly RgbColor CampbellBrightGreenLight  = new(0x1B, 0x6E, 0x1B, 22);
    public static readonly RgbColor CampbellGreenLight        = new(0x2E, 0x6B, 0x2E, 238);
    public static readonly RgbColor CampbellCyanLight         = new(0x0B, 0x66, 0x74, 24);
    public static readonly RgbColor CampbellBrightBlackLight  = new(0x5C, 0x5C, 0x5C, 59);
    public static readonly RgbColor CampbellBrightWhiteLight  = new(0x11, 0x11, 0x11, 233);
    public static readonly RgbColor CampbellWhiteLight        = new(0x33, 0x33, 0x33, 236);
    public static readonly RgbColor VsKeywordLight           = new(0x00, 0x00, 0xFF, 21);
    public static readonly RgbColor VsTypeLight              = new(0x19, 0x6B, 0x7A, 23);
    public static readonly RgbColor VsStringLight            = new(0xA3, 0x15, 0x15, 124);
    public static readonly RgbColor VsNumberLight            = new(0x0F, 0x6E, 0x4E, 23);
    public static readonly RgbColor VsCommentLight           = new(0x2B, 0x6B, 0x24, 237);
    public static readonly RgbColor VsMethodLight            = new(0x74, 0x53, 0x1F, 94);
    public static readonly RgbColor VsPunctLight             = new(0x33, 0x33, 0x33, 236);
    public static readonly RgbColor SelectionBgLight  = new(0xCE, 0xDD, 0xF2, 189);   //pale blue drag-select ground
    public static readonly RgbColor DiffAddedBgLight   = new(0xDD, 0xF2, 0xDD, 194);   //pale green under an added row, the nearest slot of the cube
    public static readonly RgbColor DiffRemovedBgLight = new(0xF9, 0xDE, 0xDE, 224);   //pale red under a removed row, the nearest slot of the cube

    private static readonly IReadOnlyDictionary<RgbColor, RgbColor> LightMap = new Dictionary<RgbColor, RgbColor>
    {
        [Accent]       = AccentLight,
        [Bright]       = BrightLight,
        [Dim]          = DimLight,
        [Ok]           = OkLight,
        [Err]          = ErrLight,
        [Warn]         = WarnLight,
        [RoleCoder]    = RoleCoderLight,
        [RoleOracle]   = RoleOracleLight,
        [CodeInlineFg] = CodeInlineFgLight,
        [CodeBlockFg]  = CodeBlockFgLight,
        [CodeBlockBg]  = CodeBlockBgLight,
        [Rule]         = RuleLight,
        [UserInputBg]  = UserInputBgLight,
        [ToolName]     = ToolNameLight,
        [ToolArgs]     = ToolArgsLight,
        [CampbellBrightYellow] = CampbellBrightYellowLight,
        [CampbellBrightGreen]  = CampbellBrightGreenLight,
        [CampbellGreen]        = CampbellGreenLight,
        [CampbellCyan]         = CampbellCyanLight,
        [CampbellBrightBlack]  = CampbellBrightBlackLight,
        [CampbellBrightWhite]  = CampbellBrightWhiteLight,
        [CampbellWhite]        = CampbellWhiteLight,
        [VsKeyword]           = VsKeywordLight,
        [VsType]              = VsTypeLight,
        [VsString]            = VsStringLight,
        [VsNumber]            = VsNumberLight,
        [VsComment]           = VsCommentLight,
        [VsMethod]            = VsMethodLight,
        [VsPunct]             = VsPunctLight,
        [SelectionBg]  = SelectionBgLight,
        [DiffAddedBg]   = DiffAddedBgLight,
        [DiffRemovedBg] = DiffRemovedBgLight,
    };

    //maps a dark palette key to the active mode, identity in dark. every paint path goes through here so callers keep passing the dark keys
    public RgbColor Map(RgbColor c) =>
        mode == ThemeMode.Light && LightMap.TryGetValue(c, out var l) ? l : c;

    public RgbColor RoleTint(string role) => role switch
    {
        "coder" => RoleCoder,
        "oracle" => RoleOracle,
        _ => Accent,
    };

    public string Paint(string s, RgbColor fg, bool bold = false, bool italic = false) =>
        Ansi.Fg(Map(fg), caps.TrueColor) + (bold ? Ansi.Bold : "") + (italic ? Ansi.Italic : "") + s + Ansi.Reset;

    //paints spans leftmost-first and drops one that overlaps a span already painted. shared by two surfaces, keep the rules here so they can't drift
    public string PaintSpans(string line, IReadOnlyList<string>? spans, RgbColor? baseFg, RgbColor? spanFg)
    {
        string Ground(string s) => s.Length == 0 ? s : baseFg is { } f ? Paint(s, f) : s;
        if (spans is not { Count: > 0 }) return Ground(line);

        var hits = new List<(int At, int Len)>();
        foreach (var span in spans)
        {
            if (string.IsNullOrEmpty(span)) continue;
            var at = line.IndexOf(span, StringComparison.Ordinal);
            if (at >= 0) hits.Add((at, span.Length));
        }
        if (hits.Count == 0) return Ground(line);
        hits.Sort((a, b) => a.At.CompareTo(b.At));

        var sb = new System.Text.StringBuilder();
        var cut = 0;
        foreach (var (at, len) in hits)
        {
            if (at < cut) continue;   //inside a span already painted, the first one wins
            sb.Append(Ground(line[cut..at]));
            var text = line.Substring(at, len);
            sb.Append(spanFg is { } sf ? Paint(text, sf) : text);
            cut = at + len;
        }
        sb.Append(Ground(line[cut..]));
        return sb.ToString();
    }

    //a painted piece on a ground that runs to the row's end, the ground written again after every reset since each painted span closes with one
    public string Ground(string painted, RgbColor bg)
    {
        var on = Ansi.Bg(Map(bg), caps.TrueColor);
        return on + Ansi.ClearToEol + painted.Replace(Ansi.Reset, Ansi.Reset + on, StringComparison.Ordinal) + Ansi.Reset;
    }

    public string PaintBgLine(string s, RgbColor fg, RgbColor bg) =>
        Ansi.Bg(Map(bg), caps.TrueColor) + Ansi.ClearToEol + Ansi.Fg(Map(fg), caps.TrueColor) + s + Ansi.Reset;

    //the colour of a role in a language, or null for a char that keeps the colour of its surface
    public static RgbColor? RoleColor(CodeLanguage language, SpanRole role) => language switch
    {
        CodeLanguage.None => null,
        CodeLanguage.PowerShell or CodeLanguage.Bash => role switch
        {
            SpanRole.Function => CampbellBrightYellow,
            SpanRole.Keyword or SpanRole.Variable => CampbellBrightGreen,
            SpanRole.Comment => CampbellGreen,
            SpanRole.String => CampbellCyan,
            SpanRole.Parameter or SpanRole.Punct => CampbellBrightBlack,
            SpanRole.Number => CampbellBrightWhite,
            _ => CampbellWhite,
        },
        _ => role switch
        {
            SpanRole.Keyword => VsKeyword,
            SpanRole.Type => VsType,
            SpanRole.String => VsString,
            SpanRole.Number => VsNumber,
            SpanRole.Comment => VsComment,
            SpanRole.Function => VsMethod,
            SpanRole.Punct => VsPunct,
            _ => (RgbColor?)null,
        },
    };

    //one colour per role. without truecolour every run takes baseFg (syntax colours need it). a null baseFg leaves the run unpainted
    public string PaintRuns(IReadOnlyList<(string Text, SpanRole Role)> runs, RgbColor? baseFg, CodeLanguage language)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var (text, role) in runs)
        {
            var fg = caps.TrueColor ? RoleColor(language, role) ?? baseFg : baseFg;
            sb.Append(fg is { } colour ? Paint(text, colour) : text);
        }
        return sb.ToString();
    }

    //the code band form of PaintRuns: one background for the whole row, fg switch at each run
    public string PaintBgRuns(IReadOnlyList<(string Text, SpanRole Role)> runs, RgbColor baseFg, RgbColor bg, CodeLanguage language)
    {
        var sb = new System.Text.StringBuilder(Ansi.Bg(Map(bg), caps.TrueColor)).Append(Ansi.ClearToEol);
        foreach (var (text, role) in runs)
            sb.Append(Ansi.Fg(Map(caps.TrueColor ? RoleColor(language, role) ?? baseFg : baseFg), caps.TrueColor)).Append(text);
        return sb.Append(Ansi.Reset).ToString();
    }

    //the selection-background SGR for the highlight styler. mode-mapped, no reset (TermText.HighlightCells restores the ground on range-exit)
    public string SelectionBgOn => Ansi.Bg(Map(SelectionBg), caps.TrueColor);

    //inline code chips are coloured text with no background band (the tint read as heavy in a sentence), don't add one
    public string Chip(string s) =>
        Ansi.Fg(Map(CodeInlineFg), caps.TrueColor) + s + Ansi.Reset;

    //muted chip colour derived from a role accent: saturation down to 60%, lightness to 90%, clamped 10% to 90%
    public RgbColor ChipColor(RgbColor accent)
    {
        var (h, s, l) = RgbToHsl(accent.R, accent.G, accent.B);
        //the 0.1 floor only applies when there's a hue to keep. an achromatic colour has h = 0, flooring it would invent a red tint (grey in, grey out)
        s = s > 0 ? Math.Clamp(s * 0.6, 0.1, 0.9) : 0;
        l = Math.Clamp(l * 0.9, 0.1, 0.9);
        var (r, g, b) = HslToRgb(h, s, l);
        return new RgbColor((byte)r, (byte)g, (byte)b, Nearest256(r, g, b));
    }

    //a passed accent makes italic, links and chips follow the role colour, bold keeps the text colour. close the link after the reset so the hyperlink encloses the chip text
    public string Paint(IReadOnlyList<StyledSpan> spans, string baseStyle = "", RgbColor? accent = null)
    {
        var linkFg = accent ?? Accent;
        var sb = new System.Text.StringBuilder();
        foreach (var s in spans)
        {
            var link = s.Flags.HasFlag(SpanFlags.Link) && s.LinkUrl is not null;
            if (link) sb.Append(Ansi.LinkOpen(s.LinkUrl!));
            if (s.Flags.HasFlag(SpanFlags.Chip))
                sb.Append(Ansi.Fg(accent is null ? Map(CodeInlineFg) : ChipColor(Map(accent.Value)), caps.TrueColor));   //chip colour derived from the role tint
            else if (link)
                sb.Append(Ansi.Fg(Map(linkFg), caps.TrueColor)).Append(Ansi.Underline);
            else if (accent is { } a && s.Flags.HasFlag(SpanFlags.Italic))
                sb.Append(Ansi.Fg(Map(a), caps.TrueColor));             //italic emphasis gets colour only when an accent was passed
            if (s.Flags.HasFlag(SpanFlags.Bold)) sb.Append(Ansi.Bold);
            if (s.Flags.HasFlag(SpanFlags.Italic)) sb.Append(Ansi.Italic);
            if (s.Flags.HasFlag(SpanFlags.Strike)) sb.Append(Ansi.Strike);
            sb.Append(s.Text);   //no chip padding, colour alone marks the chip
            if (s.Flags != SpanFlags.None) sb.Append(Ansi.Reset).Append(baseStyle);
            if (link) sb.Append(Ansi.LinkClose);
        }
        return sb.ToString();
    }

    //auto detection

    //auto probes the terminal background only on the rich path. on a piped path resolve dark without probing, no query byte must reach a redirected stdout
    public static ThemeMode ResolveMode(string configTheme, bool rich, Func<string?> probe) =>
        configTheme switch
        {
            "light" => ThemeMode.Light,
            "dark" => ThemeMode.Dark,
            _ => rich ? ClassifyBackground(probe()) : ThemeMode.Dark,   //the "auto" case
        };

    //light if the OSC 11 reply's luminance is over 0.5, dark on null or malformed (the safe fallback). no I/O here, the caller passes the reply
    public static ThemeMode ClassifyBackground(string? oscReply) =>
        TryParseOsc11(oscReply, out var r, out var g, out var b) && RelativeLuminance(r, g, b) > 0.5
            ? ThemeMode.Light : ThemeMode.Dark;

    private static bool TryParseOsc11(string? reply, out double r, out double g, out double b)
    {
        r = g = b = 0;
        if (string.IsNullOrEmpty(reply)) return false;
        var idx = reply.IndexOf("rgb:", StringComparison.Ordinal);
        if (idx < 0) return false;
        var body = reply[(idx + 4)..];
        var end = body.IndexOfAny(['\x07', '\x1b']);   //BEL or start of ST (ESC \)
        if (end >= 0) body = body[..end];
        var parts = body.Split('/');
        if (parts.Length != 3) return false;
        return TryHexFraction(parts[0], out r) && TryHexFraction(parts[1], out g) && TryHexFraction(parts[2], out b);
    }

    //a 1-4 hex-digit component scaled to 1.0 by its own max (ff and ffff both give 1.0). rejects empty, over-long or non-hex
    private static bool TryHexFraction(string hex, out double frac)
    {
        frac = 0;
        hex = hex.Trim();
        if (hex.Length is 0 or > 4) return false;
        if (!uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v)) return false;
        var max = (1u << (4 * hex.Length)) - 1;
        frac = (double)v / max;
        return true;
    }

    //WCAG relative luminance from linearized sRGB components in [0,1]
    private static double RelativeLuminance(double r, double g, double b)
    {
        static double Lin(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }

    //HSL helpers for ChipColor

    //sRGB bytes to HSL, H in [0,360), S and L in [0,1]. achromatic colors report H=0 (undefined but stable)
    private static (double H, double S, double L) RgbToHsl(byte r, byte g, byte b)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        var max = Math.Max(rd, Math.Max(gd, bd));
        var min = Math.Min(rd, Math.Min(gd, bd));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);   //achromatic
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        double h;
        if (max == rd) h = ((gd - bd) / d) % 6;
        else if (max == gd) h = (bd - rd) / d + 2;
        else h = (rd - gd) / d + 4;
        h *= 60;
        if (h < 0) h += 360;
        return (h, s, l);
    }

    //HSL back to sRGB bytes, H in [0,360), S and L in [0,1]
    private static (int R, int G, int B) HslToRgb(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var hp = h / 60;
        var x = c * (1 - Math.Abs(hp % 2 - 1));
        double r1, g1, b1;
        if (hp < 1) (r1, g1, b1) = (c, x, 0);
        else if (hp < 2) (r1, g1, b1) = (x, c, 0);
        else if (hp < 3) (r1, g1, b1) = (0, c, x);
        else if (hp < 4) (r1, g1, b1) = (0, x, c);
        else if (hp < 5) (r1, g1, b1) = (x, 0, c);
        else (r1, g1, b1) = (c, 0, x);
        var m = l - c / 2;
        return ((int)Math.Round((r1 + m) * 255), (int)Math.Round((g1 + m) * 255), (int)Math.Round((b1 + m) * 255));
    }

    //the grayscale ramp is a candidate only for a near-achromatic input. RGB distance is hue-blind, an open search snaps a muted tint onto the ramp and loses its hue
    private static byte Nearest256(int r, int g, int b)
    {
        var bestDist = double.MaxValue;
        var bestIdx = (byte)0;
        //the 6x6x6 colour cube, indices 16-231
        for (var i = 16; i <= 231; i++)
        {
            var ri = (i - 16) / 36;
            var gi = ((i - 16) % 36) / 6;
            var bi = (i - 16) % 6;
            var cr = CubeLevel(ri);
            var cg = CubeLevel(gi);
            var cb = CubeLevel(bi);
            var dist = (r - cr) * (r - cr) + (g - cg) * (g - cg) + (b - cb) * (b - cb);
            if (dist < bestDist) { bestDist = dist; bestIdx = (byte)i; }
        }
        //grayscale ramp, indices 232-255, near-achromatic inputs only
        const int GreyRampChroma = 10;   //the ramp's own per-channel step
        if (Math.Max(r, Math.Max(g, b)) - Math.Min(r, Math.Min(g, b)) < GreyRampChroma)
            for (var i = 232; i <= 255; i++)
            {
                var level = 8 + 10 * (i - 232);
                var dist = (r - level) * (r - level) + (g - level) * (g - level) + (b - level) * (b - level);
                if (dist < bestDist) { bestDist = dist; bestIdx = (byte)i; }
            }
        return bestIdx;
    }

    private static int CubeLevel(int level) => level switch
    {
        0 => 0, 1 => 95, 2 => 135, 3 => 175, 4 => 215, 5 => 255,
        _ => 0,
    };
}
