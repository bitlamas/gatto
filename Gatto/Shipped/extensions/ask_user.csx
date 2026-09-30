//ask_user, bundled with gatto. it registers read-class, so it never prompts for permission.
//editing this file drops it to prompting until you revert it. your copy is your code.
using System.Text;
using System.Text.Json;
using Gatto.Core.Tools;
//aliased because `Gatto` is bound as the script host object, so the full type name parses
//as member access on it and fails to compile. a using directive resolves as a namespace.
using Cells = Gatto.Terminal.UnicodeWidth;

//cap a string to at most `max` display cells, walking runes, not a substring.
//a terminal counts cells: one CJK character is one UTF-16 unit but two cells, so a
//char-based cap admits a row twice too wide, and a char index can cut a surrogate pair.
//the host has this rule internally, where a script cannot reach it.
//a Func local so the tool lambda below closes over it.
Func<string, int, string> cap = (s, max) =>
{
    if (Cells.Of(s) <= max) return s;
    var sb = new StringBuilder();
    var w = 0;
    foreach (var rune in s.EnumerateRunes())
    {
        var rw = Cells.OfRune(rune);
        if (w + rw > max - 1) break;   // one cell reserved for the ellipsis
        sb.Append(rune.ToString());
        w += rw;
    }
    return sb.Append('…').ToString();
};

Gatto.Register(
    "ask_user",
    "Ask the user 1-4 multiple-choice questions (2-4 options each; user can always type a free-text answer). Use for decisions you cannot make yourself. Each option is a plain string, or an object {\"label\":string,\"description\"?:string,\"recommended\"?:bool} — description stays one short line; if you have a recommendation, mark exactly one option recommended and list it first.",
    """
    {"type":"object","properties":{"questions":{"type":"array","items":{"type":"object","properties":{"question":{"type":"string"},"header":{"type":"string"},"options":{"type":"array","items":{"anyOf":[{"type":"string"},{"type":"object","properties":{"label":{"type":"string"},"description":{"type":"string"},"recommended":{"type":"boolean"}},"required":["label"]}]}},"multi_select":{"type":"boolean"}},"required":["question","header","options"]}}},"required":["questions"]}
    """,
    async (args, ctx, ct) =>
    {
        if (!args.TryGetProperty("questions", out var qsEl) || qsEl.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("missing required parameter: questions");
        var questions = new List<AskQuestion>();
        foreach (var q in qsEl.EnumerateArray())
        {
            if (q.ValueKind != JsonValueKind.Object || !q.TryGetProperty("question", out var qu) || qu.ValueKind != JsonValueKind.String)
                throw new ArgumentException("missing required parameter: question");
            var question = qu.GetString()!;
            if (q.ValueKind != JsonValueKind.Object || !q.TryGetProperty("header", out var hd) || hd.ValueKind != JsonValueKind.String)
                throw new ArgumentException("missing required parameter: header");
            var header = hd.GetString()!;
            if (header.Length is 0 or > 32)
                throw new ArgumentException($"header '{header}' must be 1-32 chars");
            if (!q.TryGetProperty("options", out var opts) || opts.ValueKind != JsonValueKind.Array)
                throw new ArgumentException("each question needs options");
            //each entry is either a bare string or an object {label, description?, recommended?}.
            //an object with no "label" throws here, so the wizard never sees a labelless option.
            var options = new List<AskOption>();
            foreach (var o in opts.EnumerateArray())
            {
                if (o.ValueKind == JsonValueKind.String)
                {
                    options.Add(new AskOption(o.GetString()!));
                    continue;
                }
                if (o.ValueKind == JsonValueKind.Object)
                {
                    if (!o.TryGetProperty("label", out var lb) || lb.ValueKind != JsonValueKind.String)
                        throw new ArgumentException($"question '{header}' has an option with no label");
                    var description = o.TryGetProperty("description", out var de) && de.ValueKind == JsonValueKind.String
                        ? de.GetString() : null;
                    var recommended = o.TryGetProperty("recommended", out var rc) && rc.ValueKind == JsonValueKind.True;
                    options.Add(new AskOption(lb.GetString()!, description, recommended));
                    continue;
                }
                throw new ArgumentException($"question '{header}' has a non-string option");
            }
            if (options.Count is < 2 or > 4)
                throw new ArgumentException($"question '{header}' needs 2-4 options, got {options.Count}");
            if (options.Select(o => o.Label).Distinct().Count() != options.Count)
                throw new ArgumentException($"question '{header}' has duplicate options");
            var multi = q.TryGetProperty("multi_select", out var m) && m.ValueKind == JsonValueKind.True;
            questions.Add(new AskQuestion(question, header, options, multi));
        }
        if (questions.Count is < 1 or > 4)
            throw new ArgumentException($"ask_user takes 1-4 questions, got {questions.Count}");

        var answers = await Gatto.Ui.AskAsync(questions, ct);
        //this row is the only place the answers appear outside the model's own history,
        //because the rich prompter commits nothing to scrollback. each answer carries its
        //question, since unlabelled answers say nothing about which one they belong to.
        //a question the user never answered says "skipped" out loud.
        //both caps hold the row to one terminal line, measured in cells: 40 per answer so one
        //long answer cannot push the others off, then 120 for the whole row.
        //the tool result below is never capped; the model reads every answer in full.
        var gloss = string.Join(" · ", answers.Select(a =>
        {
            var picked = string.Join(", ", a.Selected);
            return a.Header + ": " + (picked.Length == 0 ? "skipped" : cap(picked, 40));
        }));
        gloss = cap(gloss, 120);
        return new ToolResult(JsonSerializer.Serialize(
            answers.Select(a => new { header = a.Header, selected = a.Selected })), Gloss: gloss);
    },
    readClass: true);

//when to reach for this tool, as opposed to how to call it. the description above is the
//per-call contract; this line is the standing rule, and it leaves with the file.
Gatto.Policy("When a choice is the user's to make, such as a design direction, a name, or a trade-off, stop and ask with ask_user (2-4 options) instead of choosing for them.");