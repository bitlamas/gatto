using Gatto.Terminal;

namespace Gatto.Repl.Render;

//bash and sh: the command position, variables, quotes with their holes, substitutions, heredocs, flags and redirections
internal sealed class ShellScanner(FallbackLanguage language, string text) : ScannerBase(text)
{
    private const char Quote = (char)39;
    private const char Tick = (char)96;
    private static readonly string[] AssignmentCommands = ["export", "local", "declare", "readonly", "typeset"];
    private static readonly string[] OperatorWords = ["==", "!=", "=~", "="];
    private static readonly string[] Redirections = ["&>>", "&>", ">>", ">&", ">|", "<&", "<>", ">", "<"];
    private readonly List<(string Delimiter, bool Quoted)> _heredocs = new();

    protected override void Scan() => Commands(null);

    //reads commands until the text ends or, inside a substitution, until its closing mark
    private void Commands(string? close)
    {
        var commandStart = true;
        var assignmentArgs = false;
        var functionName = false;
        var loopName = false;
        var caseSubject = false;
        var expectIn = false;
        var caseIn = false;
        var patterns = false;

        void OnValue()
        {
            commandStart = false;
            if (loopName)
            {
                loopName = false;
                expectIn = true;
            }
            else if (caseSubject)
            {
                caseSubject = false;
                expectIn = true;
                caseIn = true;
            }
        }

        while (!AtEnd)
        {
            Step();
            if (close is not null && Ahead(close)) return;
            var c = Cur;
            if (c is ' ' or (char)9) Pos++;
            else if (IsNewline(c))
            {
                Pos++;
                if (c == (char)10 && _heredocs.Count > 0) Heredocs();
                commandStart = true;
                assignmentArgs = false;
            }
            else if (c == Backslash && IsNewline(At(Pos + 1)))
            {
                Mark(Pos, Pos + 1, SpanRole.Punct);
                Pos += At(Pos + 1) == (char)13 && At(Pos + 2) == (char)10 ? 3 : 2;
            }
            else if (c == '#' && AtWordStart()) LineComment();
            else if (language.Quotes.FirstOrDefault(q => Ahead(q.Open)) is { } form)
            {
                Quoted(form);
                OnValue();
            }
            else if (c == '$')
            {
                if (!Dollar()) Pos++;
                OnValue();
            }
            else if (c == Tick)
            {
                Substitution("`", "`");
                OnValue();
            }
            else if (Ahead("&&") || Ahead("||") || Ahead(";;"))
            {
                patterns |= Ahead(";;");
                Mark(Pos, Pos + 2, SpanRole.Punct);
                Pos += 2;
                commandStart = true;
            }
            else if (Ahead("<<") && !Ahead("<<<")) Heredoc();
            else if (Redirection()) { }
            else if (c is '|' or ';' or '&')
            {
                Mark(Pos, Pos + 1, SpanRole.Punct);
                Pos++;
                if (!(patterns && c == '|')) commandStart = true;
            }
            else if (Ahead("()"))
            {
                Mark(Pos, Pos + 2, SpanRole.Punct);
                Pos += 2;
            }
            else if (c == '(')
            {
                Mark(Pos, Pos + 1, SpanRole.Punct);
                Pos++;
                commandStart = true;
            }
            else if (c == ')')
            {
                Mark(Pos, Pos + 1, SpanRole.Punct);
                Pos++;
                commandStart = patterns;
                patterns = false;
            }
            else if (c == '{' && (Pos + 1 >= Text.Length || char.IsWhiteSpace(At(Pos + 1))))
            {
                Mark(Pos, Pos + 1, SpanRole.Punct);
                Pos++;
                commandStart = true;
            }
            else if (c == '}' && AtWordStart())
            {
                Mark(Pos, Pos + 1, SpanRole.Punct);
                Pos++;
                commandStart = false;
            }
            else
            {
                var start = Pos;
                while (!AtEnd && IsWordChar(Cur))
                {
                    Step();
                    Pos++;
                }
                if (Pos == start)
                {
                    Pos++;
                    continue;
                }
                var word = Text[start..Pos];
                var eq = word.IndexOf('=');
                if (eq > 0 && IsName(word[..eq]) && (commandStart || assignmentArgs))
                {
                    Mark(start, start + eq, SpanRole.Variable);
                    Mark(start + eq, start + eq + 1, SpanRole.Punct);
                    if (eq + 1 < word.Length && word[(eq + 1)..].All(char.IsAsciiDigit)) Mark(start + eq + 1, Pos, SpanRole.Number);
                }
                else if (patterns)
                {
                    if (word != "esac") continue;
                    Mark(start, Pos, SpanRole.Keyword);
                    patterns = false;
                    commandStart = false;
                }
                else if (expectIn && word == "in")
                {
                    Mark(start, Pos, SpanRole.Keyword);
                    expectIn = false;
                    patterns = caseIn;
                    caseIn = false;
                }
                else if (word == "]]") Mark(start, Pos, SpanRole.Keyword);
                else if (functionName)
                {
                    Mark(start, Pos, SpanRole.Function);
                    functionName = false;
                }
                else if (commandStart && language.Keywords.Contains(word, StringComparer.Ordinal))
                {
                    Mark(start, Pos, SpanRole.Keyword);
                    expectIn = false;
                    if (word is "for" or "select") loopName = true;
                    else if (word == "case") caseSubject = true;
                    else if (language.FunctionDeclarers.Contains(word, StringComparer.Ordinal)) functionName = true;
                    commandStart = word is "if" or "then" or "else" or "elif" or "do" or "while" or "until" or "time";
                }
                else if (commandStart)
                {
                    Mark(start, Pos, SpanRole.Function);
                    commandStart = false;
                    assignmentArgs = AssignmentCommands.Contains(word, StringComparer.Ordinal);
                }
                else
                {
                    expectIn = false;
                    if (OperatorWords.Contains(word, StringComparer.Ordinal)) Mark(start, Pos, SpanRole.Punct);
                    else if (word.Length > 1 && word[0] == '-' && (char.IsLetter(word[1]) || word[1] == '-')) Mark(start, Pos, SpanRole.Parameter);
                    else if (word.All(char.IsAsciiDigit)) Mark(start, Pos, SpanRole.Number);
                    OnValue();
                }
            }
        }
    }

    private static bool IsName(string s) => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_') && s.All(ch => char.IsLetterOrDigit(ch) || ch == '_');

    private static bool IsWordChar(char c) =>
        !char.IsWhiteSpace(c) && c is not ('|' or '&' or ';' or '(' or ')' or '<' or '>' or '"' or '$' or Quote or Tick);

    //a hash opens a comment only where a word could start, so the hash inside a word stays part of it
    private bool AtWordStart() => Pos == 0 || char.IsWhiteSpace(At(Pos - 1)) || At(Pos - 1) is ';' or '&' or '|' or '(' or ')' or Tick;

    //a quoted form reads its line rule and its escape rule from the table row. only a form with a hole opener expands variables and substitutions
    private void Quoted(QuoteForm form)
    {
        var segment = Pos;
        Pos += form.Open.Length;
        while (!AtEnd)
        {
            Step();
            if (Ahead(form.Close))
            {
                Pos += form.Close.Length;
                break;
            }
            var c = Cur;
            if (!form.CrossesLines && IsNewline(c)) break;
            if (form.Escapes && c == Backslash) Pos = Math.Min(Text.Length, Pos + 2);
            else if (form.HoleOpen is not null && c is '$' or Tick) segment = Hole(segment);
            else Pos++;
        }
        Mark(segment, Pos, SpanRole.String);
    }

    //the string text before a variable or a substitution keeps its role, and the hole takes its own
    private int Hole(int segment)
    {
        Mark(segment, Pos, SpanRole.String);
        if (Cur == Tick) Substitution("`", "`");
        else if (!Dollar())
        {
            Pos++;
            return segment;
        }
        return Pos;
    }

    private bool Dollar()
    {
        if (Ahead("$((")) Arithmetic();
        else if (Ahead("$(")) Substitution("$(", ")");
        else if (Ahead("${")) Braced();
        else if (char.IsLetter(At(Pos + 1)) || At(Pos + 1) == '_')
        {
            var start = Pos++;
            while (char.IsLetterOrDigit(Cur) || Cur == '_') Pos++;
            Mark(start, Pos, SpanRole.Variable);
        }
        else if (char.IsAsciiDigit(At(Pos + 1)) || At(Pos + 1) is '@' or '?' or '#' or '$' or '!' or '*' or '-')
        {
            Mark(Pos, Pos + 2, SpanRole.Variable);
            Pos += 2;
        }
        else return false;
        return true;
    }

    private void Substitution(string open, string close)
    {
        Enter();
        Mark(Pos, Pos + open.Length, SpanRole.Punct);
        Pos += open.Length;
        Commands(close);
        if (Ahead(close))
        {
            Mark(Pos, Pos + close.Length, SpanRole.Punct);
            Pos += close.Length;
        }
        Leave();
    }

    private void Arithmetic()
    {
        Mark(Pos, Pos + 3, SpanRole.Punct);
        Pos += 3;
        while (!AtEnd && !Ahead("))"))
        {
            Step();
            var c = Cur;
            if (char.IsAsciiDigit(c))
            {
                var start = Pos;
                while (char.IsAsciiDigit(Cur)) Pos++;
                Mark(start, Pos, SpanRole.Number);
            }
            else if (c == '$' && Dollar()) { }
            else
            {
                if (c > ' ' && c < (char)127 && !char.IsAsciiLetter(c) && c != '_') Mark(Pos, Pos + 1, SpanRole.Punct);
                Pos++;
            }
        }
        if (!Ahead("))")) return;
        Mark(Pos, Pos + 2, SpanRole.Punct);
        Pos += 2;
    }

    //a braced variable runs to the brace that closes it, so an inner hash or bracket belongs to the variable
    private void Braced()
    {
        var start = Pos;
        Pos += 2;
        var depth = 1;
        while (!AtEnd && depth > 0)
        {
            Step();
            if (Cur == '{') depth++;
            else if (Cur == '}') depth--;
            Pos++;
        }
        Mark(start, Pos, SpanRole.Variable);
    }

    private bool Redirection()
    {
        var i = Pos;
        while (char.IsAsciiDigit(At(i))) i++;
        var op = Redirections.FirstOrDefault(o => Ahead(o, i));
        if (op is null || (i > Pos && op[0] == '&')) return false;
        i += op.Length;
        if (op[^1] == '&')
            while (char.IsAsciiDigit(At(i)) || At(i) == '-') i++;
        Mark(Pos, i, SpanRole.Punct);
        Pos = i;
        return true;
    }

    //the delimiter is recorded here and the body is read after the line ends, so the rest of this line keeps its roles
    private void Heredoc()
    {
        var start = Pos;
        Pos += 2;
        if (Cur == '-') Pos++;
        Mark(start, Pos, SpanRole.Punct);
        while (Cur is ' ' or (char)9) Pos++;
        var d = Pos;
        var quoted = Cur is '"' or Quote;
        if (quoted)
        {
            var q = Cur;
            Pos++;
            while (!AtEnd && Cur != q && !IsNewline(Cur)) Pos++;
            if (Cur == q) Pos++;
        }
        else
            while (!AtEnd && IsWordChar(Cur)) Pos++;
        if (Pos == d) return;
        Mark(d, Pos, SpanRole.String);
        _heredocs.Add((Text[d..Pos].Trim('"', Quote), quoted));
    }

    private void Heredocs()
    {
        foreach (var (delimiter, quoted) in _heredocs.ToList())
        {
            while (!AtEnd)
            {
                Step();
                var lineEnd = Pos;
                while (lineEnd < Text.Length && !IsNewline(Text[lineEnd])) lineEnd++;
                if (Text[Pos..lineEnd].TrimStart((char)9) == delimiter)
                {
                    Mark(Pos, lineEnd, SpanRole.String);
                    Pos = lineEnd;
                    break;
                }
                if (quoted) Mark(Pos, lineEnd, SpanRole.String);
                else
                {
                    var segment = Pos;
                    while (Pos < lineEnd)
                    {
                        Step();
                        if (Cur is '$' or Tick) segment = Hole(segment);
                        else if (Cur == Backslash) Pos += 2;
                        else Pos++;
                    }
                    Mark(segment, Math.Min(Pos, lineEnd), SpanRole.String);
                }
                Pos = Math.Min(Text.Length, lineEnd + 1);
            }
        }
        _heredocs.Clear();
    }
}
