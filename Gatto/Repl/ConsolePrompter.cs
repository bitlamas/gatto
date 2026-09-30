using Gatto.Core.Tools;
using Gatto.Terminal;

namespace Gatto.Repl;

//the pure parts of the ask_user picker for both prompters, display text is sanitised here and the picks come back raw
internal static class PrompterCore
{
    //a free-text menu entry the prompter adds itself, kept out of the option list so TryParsePicks never sees it

    //a function of the glyph set, a const can't read one, and the pick is matched by number so the text has no identity
    public static string FreeTextLabelOf(GlyphSet g) => $"Type my own answer{g.Ellipsis}";

    //the menu rows, a numbered line per option with its description and (Recommended) mark, then the free-text row and the hint
    public static List<string> MenuLines(AskQuestion q, GlyphSet? glyphs)
    {
        var rows = new List<string> { $"[{TermText.Sanitize(q.Header)}] {TermText.Sanitize(q.Question)}" };
        var firstRecommended = -1;
        for (var i = 0; i < q.Options.Count; i++)
            if (q.Options[i].Recommended) { firstRecommended = i; break; }

        for (var i = 0; i < q.Options.Count; i++)
        {
            var opt = q.Options[i];
            var suffix = i == firstRecommended ? " (Recommended)" : "";
            rows.Add($"  {i + 1}. {TermText.Sanitize(opt.Label)}{suffix}");
            if (!string.IsNullOrEmpty(opt.Description))
                rows.Add($"     {TermText.Sanitize(opt.Description)}");
        }

        //a multi-select question gets no free-text row, a comma list and a verbatim sentence have no single honest rendering there
        if (!q.MultiSelect)
            rows.Add($"  {q.Options.Count + 1}. {FreeTextLabelOf(glyphs ?? GlyphSet.Unicode)}");

        rows.Add(HintLine(q));
        return rows;
    }

    //the hint under the options, how to pick a number or type an answer
    public static string HintLine(AskQuestion q) =>
        $"pick 1-{q.Options.Count}{(q.MultiSelect ? " (comma-separate for several)" : "")} or type your answer";

    //parse a trimmed answer into raw options, commas only for multi-select, and a rejected answer becomes the verbatim free-form answer
    public static bool TryParsePicks(AskQuestion q, string input, out List<string> selected)
    {
        var picks = input.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        selected = new List<string>();
        var ok = picks.Length >= 1 && (q.MultiSelect || picks.Length == 1);
        foreach (var p in picks)
            if (ok && int.TryParse(p, out var n) && n >= 1 && n <= q.Options.Count)
                selected.Add(q.Options[n - 1].Label);
            else ok = false;
        return ok;
    }
}

//the plain prompter draws marks too, its free-text row ends in the set's ellipsis, so a caller must pass the launch set
public sealed class ConsolePrompter(Gatto.Terminal.GlyphSet? glyphs = null)
    : IUserPrompter, IWizardPrompter
{
    private readonly Gatto.Terminal.GlyphSet _glyphs = glyphs ?? Gatto.Terminal.GlyphSet.Unicode;

    //a numbered menu on a cooked line, a blank line leaves, an unparseable pick re-asks, and nothing throws to mean the user left

    //control is always null here, this path reads cooked lines so there is no keystroke for it to be
    public WizardAnswer? AskOne(WizardAsk ask)
    {
        Console.WriteLine();
        foreach (var row in ask.BodyRows ?? []) Console.WriteLine("  " + TermText.Sanitize(row.Text));
        Console.WriteLine(TermText.Sanitize(ask.Question));
        for (var i = 0; i < ask.Options.Count; i++)
            Console.WriteLine($"  {i + 1}. {TermText.Sanitize(ask.Options[i].Label)}");
        Console.WriteLine(ask.Options.Count > 0
            ? $"  pick 1-{ask.Options.Count}, or leave blank to quit setup"
            : "  type your answer, or leave blank to quit setup");

        while (true)
        {
            Console.Write("> ");
            var raw = Console.ReadLine();
            if (raw is null) return null;                  //stdin closed, the wizard just leaves
            var input = raw.Trim();
            if (input.Length == 0) return null;            //a blank line leaves, on purpose

            if (ask.Options.Count == 0) return new WizardAnswer(raw);   //no options, so the typed line is the answer
            if (int.TryParse(input, out var pick) && pick >= 1 && pick <= ask.Options.Count)
                return new WizardAnswer(ask.Options[pick - 1].Identity);

            Console.WriteLine($"  that isn't one of the options — pick 1-{ask.Options.Count}.");
        }
    }

    public Task<IReadOnlyList<AskAnswer>> AskAsync(IReadOnlyList<AskQuestion> questions, CancellationToken ct)
    {
        var answers = new List<AskAnswer>();
        foreach (var q in questions)
        {
            Console.WriteLine();
            //model text is sanitised for display only, the AskAnswer returns the raw option string, and TryParsePicks never sees the free-text row
            foreach (var line in PrompterCore.MenuLines(q, _glyphs)) Console.WriteLine(line);
            var freeTextIndex = q.Options.Count + 1;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Console.Write("> ");
                var raw = Console.ReadLine();
                if (raw is null) throw new OperationCanceledException("stdin closed while awaiting an answer");
                var input = raw.Trim();
                if (input.Length == 0) continue;   //blank line silently asks again

                //picking the free-text row's number reads one more line as the verbatim answer, and multi-select never renders that row
                if (!q.MultiSelect && int.TryParse(input, out var picked) && picked == freeTextIndex)
                {
                    var freeText = Console.ReadLine();
                    if (freeText is null) throw new OperationCanceledException("stdin closed while awaiting an answer");
                    answers.Add(new AskAnswer(q.Header, new[] { freeText }));
                    break;
                }

                //a valid pick selects, anything else is the verbatim free-form answer the model reads as typed
                answers.Add(PrompterCore.TryParsePicks(q, input, out var selected)
                    ? new AskAnswer(q.Header, selected)
                    : new AskAnswer(q.Header, new[] { raw }));
                break;
            }
        }
        return Task.FromResult<IReadOnlyList<AskAnswer>>(answers);
    }
}
