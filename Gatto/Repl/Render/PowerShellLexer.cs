using Gatto.Terminal;

namespace Gatto.Repl.Render;

//a PowerShell tokenizer for display. its roles are measured against Windows PowerShell's own parser on a committed corpus
public static class PowerShellLexer
{
    public static IReadOnlyList<RoleSpan> Classify(string text) => new Scanner(text).Run();

    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "begin", "break", "catch", "class", "continue", "data", "define", "do", "dynamicparam", "else", "elseif", "end",
        "enum", "exit", "filter", "finally", "for", "foreach", "from", "function", "hidden", "if", "in", "param",
        "process", "return", "static", "switch", "throw", "trap", "try", "until", "using", "var", "while", "workflow",
    };

    private static readonly HashSet<string> OperatorWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "and", "or", "xor", "not", "band", "bor", "bxor", "bnot", "shl", "shr", "f", "as", "is", "isnot", "join", "split",
        "eq", "ne", "gt", "ge", "lt", "le", "like", "notlike", "match", "notmatch", "replace", "contains", "notcontains", "in", "notin",
        "ieq", "ine", "igt", "ige", "ilt", "ile", "ilike", "inotlike", "imatch", "inotmatch", "ireplace", "icontains", "inotcontains",
        "iin", "inotin", "isplit", "ceq", "cne", "cgt", "cge", "clt", "cle", "clike", "cnotlike", "cmatch", "cnotmatch", "creplace",
        "ccontains", "cnotcontains", "cin", "cnotin", "csplit",
    };

    private sealed class Scanner(string text)
    {
        private const int MaxDepth = 200;
        private readonly List<RoleSpan> _spans = new();
        private readonly long _budget = 64 + (long)text.Length * 32;
        private int _pos;
        private long _steps;
        private int _depth;

        public List<RoleSpan> Run()
        {
            while (!AtEnd)
            {
                var before = _pos;
                Statements('\0');
                if (_pos == before) _pos++;
            }
            return _spans.Where(s => s.Length > 0).ToList();
        }

        //every scanning loop takes a step, so a loop that stops advancing throws instead of hanging the renderer
        private void Step()
        {
            if (++_steps > _budget) throw new InvalidOperationException("the PowerShell lexer stopped advancing");
        }

        //nesting deeper than any real script throws before the stack can overflow and end the process
        private void Enter()
        {
            if (++_depth > MaxDepth) throw new InvalidOperationException("the PowerShell text nests too deeply to highlight");
        }

        private char At(int i) => i < text.Length ? text[i] : '\0';

        private char Cur => At(_pos);

        private bool AtEnd => _pos >= text.Length;

        private void Mark(int start, int end, SpanRole role)
        {
            if (end > start) _spans.Add(new RoleSpan(start, end - start, role));
        }

        private void Punct(int length)
        {
            Mark(_pos, Math.Min(text.Length, _pos + length), SpanRole.Punct);
            _pos = Math.Min(text.Length, _pos + length);
        }

        //holds a place for a token whose nested spans come after it in the list, so they paint over it
        private int Reserve()
        {
            _spans.Add(default);
            return _spans.Count - 1;
        }

        private static bool IsSpace(char c) => c is ' ' or '\t' or (char)11 or (char)12 or (char)0x00A0;

        private static bool IsNewline(char c) => c is '\n' or '\r';

        private static bool IsDash(char c) => c is '-' or (char)0x2013 or (char)0x2014 or (char)0x2015;

        private static bool IsSingleQuote(char c) => c is '\'' or (char)0x2018 or (char)0x2019 or (char)0x201A or (char)0x201B;

        private static bool IsDoubleQuote(char c) => c is '"' or (char)0x201C or (char)0x201D or (char)0x201E;

        private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        private static bool EndsStatement(char c, char close) =>
            c is ';' or ')' or '}' || IsNewline(c) || (close != '\0' && c == close);

        private static bool EndsGeneric(char c) =>
            c == '\0' || IsSpace(c) || IsNewline(c) || c is ';' or '|' or '&' or '(' or ')' or '{' or '}' or ',' or '<' or '>'
            || IsSingleQuote(c) || IsDoubleQuote(c);

        private int WordEnd(int i)
        {
            while (IsNameChar(At(i))) i++;
            return i;
        }

        private int GenericEnd(int i)
        {
            while (i < text.Length && !EndsGeneric(text[i]))
            {
                if (text[i] == '`' && IsNewline(At(i + 1))) break;
                i += text[i] == '`' && i + 1 < text.Length ? 2 : 1;
            }
            return i;
        }

        private bool KeywordAt(int start, int end)
        {
            var c = At(end);
            var bounded = c == '\0' || IsSpace(c) || IsNewline(c) || c is '(' or '{' or ';' or '}' or ')';
            return end > start && bounded && Keywords.Contains(text[start..end]);
        }

        private void SkipBlanks()
        {
            while (!AtEnd)
            {
                Step();
                var c = Cur;
                if (IsSpace(c)) _pos++;
                else if (c == '`' && IsNewline(At(_pos + 1))) _pos += At(_pos + 1) == '\r' && At(_pos + 2) == '\n' ? 3 : 2;
                else if (c == '<' && At(_pos + 1) == '#') BlockComment();
                else if (c == '#') LineComment();
                else return;
            }
        }

        private void LineComment()
        {
            var start = _pos;
            while (!AtEnd && !IsNewline(Cur)) _pos++;
            Mark(start, _pos, SpanRole.Comment);
        }

        private void BlockComment()
        {
            var start = _pos;
            var close = text.IndexOf("#>", _pos + 2, StringComparison.Ordinal);
            _pos = close < 0 ? text.Length : close + 2;
            Mark(start, _pos, SpanRole.Comment);
        }

        //a statement list up to close, which is ) or }, or the null char for the whole text
        private void Statements(char close)
        {
            Enter();
            try
            {
                while (!AtEnd)
                {
                    Step();
                    SkipBlanks();
                    if (AtEnd) return;
                    var c = Cur;
                    if (close != '\0' && c == close) return;
                    if (IsNewline(c)) { _pos++; continue; }
                    if (c == ';') { Punct(1); continue; }
                    if (c is ')' or '}' or ']')
                    {
                        if (close != '\0') return;
                        Punct(1);
                        continue;
                    }
                    var before = _pos;
                    Statement(close);
                    if (_pos == before) _pos++;
                }
            }
            finally { _depth--; }
        }

        private void Statement(char close)
        {
            var end = WordEnd(_pos);
            if (!KeywordAt(_pos, end))
            {
                Pipeline(close);
                return;
            }
            var word = text[_pos..end].ToLowerInvariant();
            Mark(_pos, end, SpanRole.Keyword);
            _pos = end;
            switch (word)
            {
                case "function" or "filter" or "workflow":
                    SkipBlanks();
                    _pos = GenericEnd(_pos);
                    Tail(close);
                    break;
                case "class" or "enum":
                    SkipBlanks();
                    var nameEnd = WordEnd(_pos);
                    if (word == "class") Mark(_pos, nameEnd, SpanRole.Type);
                    _pos = nameEnd;
                    MemberBlock(word == "enum");
                    break;
                case "using":
                    SkipBlanks();
                    var kindEnd = WordEnd(_pos);
                    if (text[_pos..kindEnd].ToLowerInvariant() is "namespace" or "module" or "assembly")
                    {
                        Mark(_pos, kindEnd, SpanRole.Keyword);
                        _pos = kindEnd;
                    }
                    CommandArgs(close);
                    break;
                case "switch":
                    SwitchHead(close);
                    break;
                case "return" or "throw" or "exit" or "break" or "continue":
                    Pipeline(close);
                    break;
                default:
                    Tail(close);
                    break;
            }
        }

        //the rest of a compound statement: its groups, its blocks and the keywords that join them
        private void Tail(char close)
        {
            while (!AtEnd)
            {
                Step();
                SkipBlanks();
                if (AtEnd || EndsStatement(Cur, close)) return;
                var before = _pos;
                var c = Cur;
                var end = WordEnd(_pos);
                if (c == '(') Group('(', ')');
                else if (c == '{') Group('{', '}');
                else if (KeywordAt(_pos, end)) { Mark(_pos, end, SpanRole.Keyword); _pos = end; }
                else if (IsDash(c) && char.IsLetter(At(_pos + 1))) Parameter();
                else Pipeline(close);
                if (_pos == before) _pos++;
            }
        }

        private void SwitchHead(char close)
        {
            while (!AtEnd)
            {
                Step();
                SkipBlanks();
                if (AtEnd || Cur is ';' or ')' or '}') return;
                var before = _pos;
                var c = Cur;
                if (IsNewline(c)) _pos++;
                else if (c == '{') { SwitchBody(); return; }
                else if (c == '(') Group('(', ')');
                else if (IsDash(c) && char.IsLetter(At(_pos + 1))) Parameter();
                else ArgumentWord();
                if (_pos == before) _pos++;
            }
        }

        //a switch body holds clause and action pairs, a bare clause such as default takes no role
        private void SwitchBody()
        {
            Punct(1);
            while (!AtEnd)
            {
                Step();
                SkipBlanks();
                if (AtEnd) return;
                var c = Cur;
                if (c == '}') { Punct(1); return; }
                if (c == ')') return;
                var before = _pos;
                if (IsNewline(c)) _pos++;
                else if (c == ';') Punct(1);
                else if (c == '{') Group('{', '}');
                else if (IsSingleQuote(c) || IsDoubleQuote(c)) StringToken();
                else if (c == '$') Dollar();
                else if (c == '(') Group('(', ')');
                else ArgumentWord();
                if (_pos == before) _pos++;
            }
        }

        //a class or enum body, member names take no role and a class member may be a typed property or a method
        private void MemberBlock(bool isEnum)
        {
            while (!AtEnd && Cur != '{')
            {
                Step();
                if (Cur is ';' or ')' or '}') return;
                _pos++;
            }
            if (AtEnd) return;
            Punct(1);
            while (!AtEnd)
            {
                Step();
                SkipBlanks();
                if (AtEnd) return;
                var c = Cur;
                if (c == '}') { Punct(1); return; }
                if (c == ')') return;
                var before = _pos;
                var end = WordEnd(_pos);
                if (IsNewline(c)) _pos++;
                else if (c == ';') Punct(1);
                else if (c == '=') { Punct(1); Expression('}'); }
                else if (isEnum) _pos = Math.Max(_pos + 1, end);
                else if (c == '[') TypeLiteral();
                else if (c == '$') Variable();
                else if (KeywordAt(_pos, end)) { Mark(_pos, end, SpanRole.Keyword); _pos = end; }
                else if (end > _pos)
                {
                    _pos = end;
                    SkipBlanks();
                    if (Cur == '(') Group('(', ')');
                    SkipBlanks();
                    if (Cur == '{') Group('{', '}');
                }
                if (_pos == before) _pos++;
            }
        }

        private void Group(char open, char close)
        {
            Punct(1);
            Statements(close);
            if (Cur == close) Punct(1);
        }

        private void Pipeline(char close)
        {
            while (!AtEnd)
            {
                Step();
                SkipBlanks();
                if (AtEnd || EndsStatement(Cur, close)) return;
                var before = _pos;
                Element(close);
                SkipBlanks();
                if (Cur == '|' || (Cur == '&' && At(_pos + 1) == '&'))
                {
                    Punct(At(_pos + 1) == Cur ? 2 : 1);
                    continue;
                }
                if (_pos == before) _pos++;
                return;
            }
        }

        //one pipeline element: a command, or an expression when its first token is a value
        private void Element(char close)
        {
            var c = Cur;
            var next = At(_pos + 1);
            if (c is '&' or '.' && (IsSpace(next) || IsSingleQuote(next) || IsDoubleQuote(next) || next is '$' or '{' or '('))
            {
                Punct(1);
                SkipBlanks();
                CommandName();
                CommandArgs(close);
                return;
            }
            if (c == ']')
            {
                Punct(1);
                return;
            }
            if (StartsExpression(c, next))
            {
                Expression(close);
                return;
            }
            CommandName();
            CommandArgs(close);
        }

        private bool StartsExpression(char c, char next)
        {
            if (c is '$' or '@' or '(' or '[' or '!' or ',' || char.IsDigit(c) || IsSingleQuote(c) || IsDoubleQuote(c)) return true;
            if (c is '.' or '+' && char.IsDigit(next)) return true;
            if (!IsDash(c)) return false;
            if (char.IsDigit(next)) return true;
            return char.IsLetter(next) && OperatorWords.Contains(text[(_pos + 1)..WordEnd(_pos + 1)]);
        }

        private void CommandName()
        {
            var c = Cur;
            if (IsSingleQuote(c) || IsDoubleQuote(c)) StringToken();
            else if (c == '$') Dollar();
            else if (c == '{') Group('{', '}');
            else if (c == '(') Group('(', ')');
            else
            {
                var end = GenericEnd(_pos);
                Mark(_pos, end, SpanRole.Function);
                _pos = end;
            }
        }

        private void CommandArgs(char close)
        {
            while (!AtEnd)
            {
                Step();
                SkipBlanks();
                if (AtEnd) return;
                var c = Cur;
                var next = At(_pos + 1);
                if (EndsStatement(c, close) || c == '|' || (c == '&' && next == '&')) return;
                var before = _pos;
                if (IsDash(c) && (char.IsLetter(next) || next is '_' or '?')) Parameter();
                else if (c == '$') Dollar();
                else if (c == '@') AtSign();
                else if (IsSingleQuote(c) || IsDoubleQuote(c)) StringToken();
                else if (c == '(') Group('(', ')');
                else if (c == '{') Group('{', '}');
                else if (c is ',' or '&') Punct(1);
                else if (c is '>' or '<' || ((char.IsDigit(c) || c == '*') && next == '>')) Redirection();
                else ArgumentWord();
                if (_pos == before) _pos++;
            }
        }

        private void Parameter()
        {
            var start = _pos;
            _pos++;
            while (IsNameChar(Cur) || Cur == '?') _pos++;
            if (Cur == ':') _pos++;
            Mark(start, _pos, SpanRole.Parameter);
        }

        private void Redirection()
        {
            var start = _pos;
            if (Cur != '>' && Cur != '<') _pos++;
            if (Cur == '<') _pos++;
            else if (Cur == '>')
            {
                _pos++;
                if (Cur == '>') _pos++;
                else if (Cur == '&' && char.IsDigit(At(_pos + 1))) _pos += 2;
            }
            Mark(start, _pos, SpanRole.Punct);
        }

        //a bare command argument is text, except a lone dot or double dot and a word that is wholly a number
        private void ArgumentWord()
        {
            var end = GenericEnd(_pos);
            var word = text[_pos..end];
            if (word is "." or "..") Mark(_pos, end, SpanRole.Punct);
            else if (word.Length > 0 && char.IsDigit(word[0]) && NumberEnd(_pos) == end) Mark(_pos, end, SpanRole.Number);
            _pos = end;
        }

        private void Dollar()
        {
            if (At(_pos + 1) == '(') SubExpression();
            else Variable();
        }

        //inside a string the subexpression is code, give its whole region no role so the string role doesn't show through
        private void SubExpression(bool inString = false)
        {
            Punct(2);
            var slot = inString ? Reserve() : -1;
            var inner = _pos;
            Statements(')');
            if (slot >= 0) _spans[slot] = new RoleSpan(inner, _pos - inner, SpanRole.None);
            if (Cur == ')') Punct(1);
        }

        private void Variable()
        {
            var start = _pos;
            var i = _pos + 1;
            var c = At(i);
            if (c == '{')
            {
                var close = text.IndexOf('}', i + 1);
                i = close < 0 ? text.Length : close + 1;
            }
            else if (c is '$' or '?' or '^') i++;
            else
            {
                var nameStart = i;
                i = WordEnd(i);
                if (At(i) == ':' && i > nameStart && IsNameChar(At(i + 1))) i = WordEnd(i + 1);
                if (i == nameStart)
                {
                    _pos = start + 1;
                    return;
                }
            }
            Mark(start, i, SpanRole.Variable);
            _pos = i;
        }

        private void AtSign()
        {
            var next = At(_pos + 1);
            if (next == '(') SubExpression();
            else if (next == '{') Hashtable();
            else if ((IsSingleQuote(next) || IsDoubleQuote(next)) && HereString()) return;
            else if (IsNameChar(next))
            {
                var end = WordEnd(_pos + 1);
                Mark(_pos, end, SpanRole.Variable);
                _pos = end;
            }
            else _pos++;
        }

        private void StringToken()
        {
            var start = _pos;
            if (IsSingleQuote(Cur))
            {
                _pos++;
                while (!AtEnd)
                {
                    if (!IsSingleQuote(Cur)) { _pos++; continue; }
                    if (IsSingleQuote(At(_pos + 1))) { _pos += 2; continue; }
                    _pos++;
                    break;
                }
                Mark(start, _pos, SpanRole.String);
                return;
            }
            var slot = Reserve();
            _pos++;
            while (!AtEnd)
            {
                Step();
                var c = Cur;
                if (c == '`') _pos = Math.Min(text.Length, _pos + 2);
                else if (IsDoubleQuote(c) && IsDoubleQuote(At(_pos + 1))) _pos += 2;
                else if (IsDoubleQuote(c)) { _pos++; break; }
                else if (c == '$') InString();
                else _pos++;
            }
            _spans[slot] = new RoleSpan(start, _pos - start, SpanRole.String);
        }

        //inside an expandable string a $ starts a variable or a subexpression, any other $ is text
        private void InString()
        {
            var next = At(_pos + 1);
            if (next == '(') SubExpression(inString: true);
            else if (next == '{' || IsNameChar(next) || next is '$' or '?' or '^') Variable();
            else _pos++;
        }

        //opens with @" or @' at the end of a line and closes on a line that starts with the quote and @
        private bool HereString()
        {
            var start = _pos;
            var expandable = IsDoubleQuote(At(_pos + 1));
            var i = _pos + 2;
            while (IsSpace(At(i))) i++;
            if (i < text.Length && !IsNewline(At(i))) return false;
            var slot = Reserve();
            _pos = i;
            while (!AtEnd)
            {
                Step();
                var c = Cur;
                if (IsNewline(c))
                {
                    var lineStart = _pos + (c == '\r' && At(_pos + 1) == '\n' ? 2 : 1);
                    var quote = At(lineStart);
                    _pos = lineStart;
                    if ((expandable ? IsDoubleQuote(quote) : IsSingleQuote(quote)) && At(lineStart + 1) == '@')
                    {
                        _pos = lineStart + 2;
                        break;
                    }
                }
                else if (expandable && c == '`') _pos = Math.Min(text.Length, _pos + 2);
                else if (expandable && c == '$') InString();
                else _pos++;
            }
            _spans[slot] = new RoleSpan(start, _pos - start, SpanRole.String);
            return true;
        }

        private void Hashtable()
        {
            Punct(2);
            while (!AtEnd)
            {
                Step();
                SkipBlanks();
                if (AtEnd) return;
                var c = Cur;
                if (c == '}') { Punct(1); return; }
                if (c == ')') return;
                var before = _pos;
                if (IsNewline(c)) _pos++;
                else if (c == ';') Punct(1);
                else if (IsSingleQuote(c) || IsDoubleQuote(c)) StringToken();
                else if (c == '$') Dollar();
                else if (c == '(') Group('(', ')');
                else if (c == '=') { Punct(1); Pipeline('}'); }
                else
                {
                    while (!AtEnd && !IsSpace(Cur) && !IsNewline(Cur) && Cur is not ('=' or ';' or '}' or ')')) _pos++;
                }
                if (_pos == before) _pos++;
            }
        }

        private void Expression(char close)
        {
            Enter();
            try
            {
                var operand = false;
                while (!AtEnd)
                {
                    Step();
                    SkipBlanks();
                    if (AtEnd) return;
                    var c = Cur;
                    var next = At(_pos + 1);
                    if (EndsStatement(c, close) || c == '|' || (c == '&' && next == '&')) return;
                    if (c == ']')
                    {
                        if (close == ']') return;
                        Punct(1);
                        operand = true;
                        continue;
                    }
                    var before = _pos;
                    var value = true;
                    var assignment = AssignmentLength();
                    if (c == '$') Dollar();
                    else if (c == '@') AtSign();
                    else if (IsSingleQuote(c) || IsDoubleQuote(c)) StringToken();
                    else if (c == '(') Group('(', ')');
                    else if (c == '{') Group('{', '}');
                    else if (c == '[') { if (operand) Index(); else TypeLiteral(); }
                    else if (!operand && (char.IsDigit(c) || (c == '.' && char.IsDigit(next)) || (IsDash(c) && char.IsDigit(next)))) NumberOrWord();
                    else if (IsDash(c) && char.IsLetter(next)) { DashWord(); value = false; }
                    else if (c == '.' && next == '.') { Punct(2); value = false; }
                    else if (c == '.' || (c == ':' && next == ':')) Member();
                    else if (assignment > 0)
                    {
                        Punct(assignment);
                        Pipeline(close);
                        return;
                    }
                    else if (c is '+' or '*' or '/' or '%' or '!' or ',' or '?' or ':' or '>' or '<' || IsDash(c))
                    {
                        Punct(c is '+' or '-' && next == c || c == '?' && next == '?' ? 2 : 1);
                        value = false;
                    }
                    else if (IsNameChar(c))
                    {
                        var end = WordEnd(_pos);
                        if (KeywordAt(_pos, end))
                        {
                            var inKeyword = text[_pos..end].Equals("in", StringComparison.OrdinalIgnoreCase);
                            Mark(_pos, end, SpanRole.Keyword);
                            _pos = end;
                            if (inKeyword)
                            {
                                Pipeline(close);
                                return;
                            }
                            value = false;
                        }
                        else _pos = end;
                    }
                    else _pos++;
                    operand = value;
                    if (_pos == before) _pos++;
                }
            }
            finally { _depth--; }
        }

        private int AssignmentLength()
        {
            var c = Cur;
            var next = At(_pos + 1);
            if (c == '=') return 1;
            if ((c is '+' or '*' or '/' or '%' || IsDash(c)) && next == '=') return 2;
            return c == '?' && next == '?' && At(_pos + 2) == '=' ? 3 : 0;
        }

        private void DashWord()
        {
            var end = WordEnd(_pos + 1);
            Mark(_pos, end, OperatorWords.Contains(text[(_pos + 1)..end]) ? SpanRole.Punct : SpanRole.Parameter);
            _pos = end;
        }

        //a member after . or :: takes no role, whether it is a property or a method
        private void Member()
        {
            Punct(Cur == ':' ? 2 : 1);
            _pos = WordEnd(_pos);
        }

        private void Index()
        {
            Punct(1);
            Expression(']');
            if (Cur == ']') Punct(1);
        }

        private void TypeLiteral()
        {
            Enter();
            try
            {
                Punct(1);
                while (!AtEnd)
                {
                    Step();
                    SkipBlanks();
                    if (AtEnd) return;
                    var c = Cur;
                    if (c == ']') { Punct(1); return; }
                    if (c == '[') TypeLiteral();
                    else if (c == ',') Punct(1);
                    else if (c == '(')
                    {
                        Punct(1);
                        Expression(')');
                        if (Cur == ')') Punct(1);
                    }
                    else if (IsNameChar(c))
                    {
                        var start = _pos;
                        while (IsNameChar(Cur) || Cur is '.' or '`' or '+') _pos++;
                        Mark(start, _pos, SpanRole.Type);
                    }
                    else return;
                }
            }
            finally { _depth--; }
        }

        private void NumberOrWord()
        {
            var start = _pos;
            var digits = IsDash(Cur) ? _pos + 1 : _pos;
            var end = NumberEnd(digits);
            if (end > digits && !IsNameChar(At(end)))
            {
                Mark(start, end, SpanRole.Number);
                _pos = end;
                return;
            }
            _pos = Math.Max(start + 1, WordEnd(digits));
        }

        private int NumberEnd(int i)
        {
            var start = i;
            if (At(i) == '0' && At(i + 1) is 'x' or 'X' && Uri.IsHexDigit(At(i + 2)))
            {
                i += 2;
                while (Uri.IsHexDigit(At(i))) i++;
            }
            else
            {
                while (char.IsDigit(At(i))) i++;
                if (At(i) == '.' && char.IsDigit(At(i + 1)))
                {
                    i++;
                    while (char.IsDigit(At(i))) i++;
                }
                if (i == start) return start;
                if (At(i) is 'e' or 'E' && (char.IsDigit(At(i + 1)) || (At(i + 1) is '+' or '-' && char.IsDigit(At(i + 2)))))
                {
                    i += At(i + 1) is '+' or '-' ? 2 : 1;
                    while (char.IsDigit(At(i))) i++;
                }
            }
            if (At(i) is 'l' or 'L' or 'd' or 'D') i++;
            if (i + 1 < text.Length && text.Substring(i, 2).ToLowerInvariant() is "kb" or "mb" or "gb" or "tb" or "pb") i += 2;
            return i;
        }
    }
}
