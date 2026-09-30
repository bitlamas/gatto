using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Cli.Setup;

//the one place a wizard row's tone becomes paint, both faces need it and the screen data must stay unaware of a terminal
internal static class WizardPaint
{
    public static IReadOnlyList<BodyRow>? Body(IReadOnlyList<WizardRow>? rows) =>
        rows is null ? null : [.. rows.Select(Row)];

    //back is always the last option, so the numbers beside the real options never shift when it appears
    public static IReadOnlyList<SelectOption> Options(WizardScreen.Choice c, Gatto.Terminal.GlyphSet g) =>
    [
        .. c.Options.Select(o =>
            new SelectOption(o.Label, o.Description, Recommended: o.Recommended, Disabled: o.Disabled,
                //the mark's word travels with the option, so the two faces can't say different things about one row
                MarkWord: o.MarkWord)),
        .. c.AllowBack
            ? new[] { new SelectOption(SetupFace.BackLabelOf(g), Key: SetupFace.BackOptionKey) }
            : [],
    ];

    //the wizard's default ground is plain (SelectPrompt is left alone), and the indent must pass through here
    public static BodyRow Row(WizardRow r) => new(r.Text, r.Tone switch
    {
        RowTone.Subject => BodyRowKind.Accent,
        RowTone.Aside => BodyRowKind.Note,
        _ => BodyRowKind.Plain,
    }, r.Highlight, r.Indent, r.Hang);

    //the same tone table for rows a face prints itself (sanitize first, painting emits escapes that sanitizing would strip)
    public static string Line(WizardRow r, Theme theme)
    {
        var text = TermText.Sanitize(r.Text);
        return r.Tone switch
        {
            RowTone.Subject => theme.Paint(text, Theme.Accent),
            //an Aside's highlight paints accent, the ink is chosen by whether the user is about to act on it
            RowTone.Aside => theme.PaintSpans(text, r.Highlight, Theme.Dim, Theme.Accent),
            _ => theme.PaintSpans(text, r.Highlight, null, Theme.Accent),
        };
    }
}
