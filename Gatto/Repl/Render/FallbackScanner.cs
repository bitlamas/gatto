using Gatto.Terminal;

namespace Gatto.Repl.Render;

//one scanner per family, and a new language in a known family is a table entry rather than a branch here
public static class FallbackScanner
{
    public static IReadOnlyList<RoleSpan> Classify(FallbackLanguage language, string text) => language.Family switch
    {
        ScanFamily.Json => new JsonScanner(language, text).Run(),
        ScanFamily.Code => new CodeScanner(language, text).Run(),
        ScanFamily.Markup => new MarkupScanner(language, text).Run(),
        _ => new ShellScanner(language, text).Run(),
    };
}

//the cursor, the step budget, the nesting guard and the span list that every family shares
internal abstract class ScannerBase(string text)
{
    protected const char Backslash = (char)92;
    private const int MaxDepth = 200;
    private readonly long _budget = 64 + (long)text.Length * 32;
    private long _steps;
    private int _depth;

    protected readonly List<RoleSpan> Spans = new();
    protected readonly string Text = text;
    protected int Pos;

    public List<RoleSpan> Run()
    {
        Scan();
        return Spans.Where(s => s.Length > 0).ToList();
    }

    protected abstract void Scan();

    //every loop takes a step, so a loop that stops advancing throws instead of hanging the renderer
    protected void Step()
    {
        if (++_steps > _budget) throw new InvalidOperationException("a fallback scanner stopped advancing");
    }

    //nesting deeper than any real text throws before the stack can overflow and end the process
    protected void Enter()
    {
        if (++_depth > MaxDepth) throw new InvalidOperationException("the text nests too deeply to highlight");
    }

    protected void Leave() => _depth--;

    protected bool AtEnd => Pos >= Text.Length;

    protected char At(int i) => i >= 0 && i < Text.Length ? Text[i] : '\0';

    protected char Cur => At(Pos);

    protected bool Ahead(string s, int at) => at >= 0 && at + s.Length <= Text.Length && Text.AsSpan(at, s.Length).SequenceEqual(s);

    protected bool Ahead(string s) => Ahead(s, Pos);

    protected static bool IsNewline(char c) => c is (char)10 or (char)13;

    protected void Mark(int start, int end, SpanRole role)
    {
        end = Math.Min(end, Text.Length);
        if (end > start) Spans.Add(new RoleSpan(start, end - start, role));
    }

    protected void LineComment()
    {
        var start = Pos;
        while (!AtEnd && !IsNewline(Cur))
        {
            Step();
            Pos++;
        }
        Mark(start, Pos, SpanRole.Comment);
    }

    //a block comment runs to its close, or to the end of the text while the close has not arrived. a nesting block counts every inner opener
    protected void BlockComment(string open, string close, bool nests)
    {
        var start = Pos;
        var depth = 0;
        while (!AtEnd)
        {
            Step();
            if (Ahead(open) && (nests || depth == 0))
            {
                depth++;
                Pos += open.Length;
            }
            else if (Ahead(close))
            {
                depth--;
                Pos += close.Length;
                if (depth <= 0) break;
            }
            else Pos++;
        }
        Pos = Math.Min(Pos, Text.Length);
        Mark(start, Pos, SpanRole.Comment);
    }

    protected bool AnyAhead(IEnumerable<string> markers) => markers.Any(m => Ahead(m));
}

//read the json a model writes into a fence, including comments, trailing commas and property lines without braces
internal sealed class JsonScanner(FallbackLanguage language, string text) : ScannerBase(text)
{
    protected override void Scan()
    {
        while (!AtEnd)
        {
            Step();
            var c = Cur;
            if (char.IsWhiteSpace(c)) Pos++;
            else if (AnyAhead(language.LineComments)) LineComment();
            else if (language.BlockOpen is { } open && Ahead(open)) BlockComment(open, language.BlockClose!, language.BlockNests);
            else if (language.Quotes.FirstOrDefault(q => Ahead(q.Open)) is { } form) String(form);
            else if (char.IsAsciiDigit(c) || (c == '-' && char.IsAsciiDigit(At(Pos + 1)))) Number();
            else if (char.IsAsciiLetter(c)) Word();
            else
            {
                if (c is '{' or '}' or '[' or ']' or ':' or ',') Mark(Pos, Pos + 1, SpanRole.Punct);
                Pos++;
            }
        }
    }

    //the table row's quote form says where a string may end. an unclosed json string stops at its line, so the next property keeps its roles
    private void String(QuoteForm form)
    {
        var start = Pos;
        Pos += form.Open.Length;
        while (!AtEnd && (form.CrossesLines || !IsNewline(Cur)))
        {
            Step();
            if (Ahead(form.Close))
            {
                Pos += form.Close.Length;
                break;
            }
            var skip = form.Escapes && Cur == Backslash && Pos + 1 < Text.Length && (form.CrossesLines || !IsNewline(At(Pos + 1)));
            Pos += skip ? 2 : 1;
        }
        Mark(start, Pos, ColonFollows() ? SpanRole.Variable : SpanRole.String);
    }

    private bool ColonFollows()
    {
        var i = Pos;
        while (i < Text.Length && char.IsWhiteSpace(Text[i])) i++;
        return At(i) == ':';
    }

    private void Number()
    {
        var start = Pos;
        if (Cur == '-') Pos++;
        while (char.IsAsciiDigit(Cur) || Cur is '.' or 'e' or 'E' || (Cur is '+' or '-' && At(Pos - 1) is 'e' or 'E'))
        {
            Step();
            Pos++;
        }
        Mark(start, Pos, SpanRole.Number);
    }

    private void Word()
    {
        var start = Pos;
        while (char.IsAsciiLetterOrDigit(Cur))
        {
            Step();
            Pos++;
        }
        if (language.Keywords.Contains(Text[start..Pos], StringComparer.Ordinal)) Mark(start, Pos, SpanRole.Keyword);
    }
}
