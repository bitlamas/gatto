using Gatto.Terminal;

namespace Gatto.Repl.Render;

//javascript, typescript, python and rust, with names, numbers, strings and their holes, comments and punctuation read from the table
internal sealed class CodeScanner(FallbackLanguage language, string text) : ScannerBase(text)
{
    private enum Last { None, Word, Keyword, Number, String, Close, Punct }

    private Last _last = Last.None;
    private string _lastText = "";

    protected override void Scan() => Code(null, false);

    //reads code until the text ends, or inside a hole until its closing brace at bracket depth zero
    private void Code(char? close, bool stopAtColon)
    {
        var depth = 0;
        while (!AtEnd)
        {
            Step();
            var c = Cur;
            if (depth == 0 && (c == close || (stopAtColon && c == ':'))) return;
            if (char.IsWhiteSpace(c)) Pos++;
            else if (c == Backslash && IsNewline(At(Pos + 1))) Pos++;
            else if (language.AttributeOpeners.FirstOrDefault(o => Ahead(o)) is { } opener)
            {
                Mark(Pos, Pos + opener.Length, SpanRole.Punct);
                Pos += opener.Length;
                Done(Last.Punct, opener);
            }
            else if (AnyAhead(language.LineComments)) LineComment();
            else if (language.BlockOpen is { } open && Ahead(open)) BlockComment(open, language.BlockClose!, language.BlockNests);
            else if (TryString()) { }
            else if (language.RegexLiterals && c == '/' && RegexAllowed() && TryRegex()) { }
            else if (char.IsAsciiDigit(c) || (c == '.' && char.IsAsciiDigit(At(Pos + 1)) && _last is not (Last.Word or Last.Number or Last.Close or Last.String))) Number();
            else if (IsNameStart(c) || (language.PrivateNames && c == '#' && IsNameStart(At(Pos + 1)))) Name();
            else
            {
                if (c > ' ' && c < (char)127)
                {
                    Mark(Pos, Pos + 1, SpanRole.Punct);
                    if (c is '(' or '[' or '{') depth++;
                    else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
                    Done(c is ')' or ']' or '}' ? Last.Close : Last.Punct, c.ToString());
                }
                Pos++;
            }
        }
    }

    private void Done(Last kind, string lastText = "")
    {
        _last = kind;
        _lastText = lastText;
    }

    private bool IsNameStart(char c) => char.IsLetter(c) || c == '_' || (c != '\0' && language.IdentifierExtraChars.Contains(c));

    private bool IsNamePart(char c) => IsNameStart(c) || char.IsDigit(c);

    private QuoteForm? FormAt(int at) => language.Quotes.FirstOrDefault(q => Ahead(q.Open, at));

    //a string may start with a one or two letter prefix, and a rust lifetime looks like an opening quote
    private bool TryString()
    {
        var c = Cur;
        if (IsNameStart(c))
        {
            for (var k = 1; k <= 2 && char.IsAsciiLetter(At(Pos + k - 1)); k++)
            {
                var prefix = Text.Substring(Pos, k);
                var after = Pos + k;
                if (language.RawQuotePrefixes.Contains(prefix, StringComparer.Ordinal) && RawString(Pos, after)) return true;
                var comparer = language.QuotePrefixesIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
                if (language.QuotePrefixes.Contains(prefix, comparer) && FormAt(after) is { } form)
                {
                    QuoteBody(form, Pos, after, prefix.Any(ch => language.HolePrefixLetters.Contains(ch)));
                    return true;
                }
            }
            return false;
        }
        if (language.Lifetimes && c == '\'' && IsNameStart(At(Pos + 1)))
        {
            var end = Pos + 1;
            while (IsNamePart(At(end))) end++;
            if (At(end) != '\'')
            {
                Mark(Pos, end, SpanRole.Type);
                Pos = end;
                Done(Last.Word);
                return true;
            }
        }
        if (FormAt(Pos) is not { } direct) return false;
        QuoteBody(direct, Pos, Pos, false);
        return true;
    }

    //a quoted form runs to its close, unless the form cannot cross a line, and an unclosed one ends with the text
    private void QuoteBody(QuoteForm form, int start, int openAt, bool prefixHoles)
    {
        Pos = openAt + form.Open.Length;
        var segment = start;
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
            else if (form.HoleOpen is { } hole && Ahead(hole))
            {
                Mark(segment, Pos, SpanRole.String);
                Hole(hole.Length, false);
                segment = Pos;
            }
            else if (prefixHoles && c is '{' or '}' && At(Pos + 1) == c) Pos += 2;
            else if (prefixHoles && c == '{')
            {
                Mark(segment, Pos, SpanRole.String);
                Hole(1, true);
                segment = Pos;
            }
            else Pos++;
        }
        Mark(segment, Pos, SpanRole.String);
        Done(Last.String);
    }

    //a hole holds code up to its closing brace. in a prefixed string a colon at depth zero starts a format spec, which is string text
    private void Hole(int openLength, bool formatSpec)
    {
        Enter();
        Mark(Pos, Pos + openLength, SpanRole.Punct);
        Pos += openLength;
        Done(Last.Punct, "{");
        Code('}', formatSpec);
        if (formatSpec && Cur == ':')
        {
            Mark(Pos, Pos + 1, SpanRole.Punct);
            var spec = ++Pos;
            while (!AtEnd && Cur != '}' && !IsNewline(Cur))
            {
                Step();
                Pos++;
            }
            Mark(spec, Pos, SpanRole.String);
        }
        if (Cur == '}')
        {
            Mark(Pos, Pos + 1, SpanRole.Punct);
            Pos++;
        }
        Leave();
    }

    //a raw string ignores backslashes and closes at a quote followed by as many hashes as opened it
    private bool RawString(int start, int at)
    {
        var hashes = 0;
        while (At(at + hashes) == '#') hashes++;
        if (At(at + hashes) != '"') return false;
        var close = @"""" + new string('#', hashes);
        Pos = at + hashes + 1;
        while (!AtEnd && !Ahead(close))
        {
            Step();
            Pos++;
        }
        Pos = Math.Min(Text.Length, Pos + close.Length);
        Mark(start, Pos, SpanRole.String);
        Done(Last.String);
        return true;
    }

    //a slash opens a regex only where a value can start
    private bool RegexAllowed() =>
        _last is Last.None or Last.Punct || (_last == Last.Keyword && _lastText is not ("this" or "super" or "true" or "false" or "null"));

    private bool TryRegex()
    {
        var i = Pos + 1;
        var inClass = false;
        while (i < Text.Length && !IsNewline(Text[i]))
        {
            var c = Text[i];
            if (c == Backslash)
            {
                i += 2;
                continue;
            }
            if (c == '[') inClass = true;
            else if (c == ']') inClass = false;
            else if (c == '/' && !inClass) break;
            i++;
        }
        if (i >= Text.Length || IsNewline(Text[i]) || i == Pos + 1) return false;
        i++;
        while (i < Text.Length && char.IsAsciiLetter(Text[i])) i++;
        Mark(Pos, i, SpanRole.String);
        Pos = i;
        Done(Last.String);
        return true;
    }

    private void Number()
    {
        var start = Pos;
        if (Cur == '0' && At(Pos + 1) is 'x' or 'X' or 'o' or 'O' or 'b' or 'B' && char.IsAsciiHexDigit(At(Pos + 2)))
        {
            Pos += 2;
            while (char.IsAsciiHexDigit(Cur) || Cur == '_') Pos++;
        }
        else
        {
            if (Cur == '.') Pos++;
            Digits();
            if (Cur == '.' && char.IsAsciiDigit(At(Pos + 1)) && !Text.AsSpan(start, Pos - start).Contains('.'))
            {
                Pos++;
                Digits();
            }
            if (Cur is 'e' or 'E' && (char.IsAsciiDigit(At(Pos + 1)) || (At(Pos + 1) is '+' or '-' && char.IsAsciiDigit(At(Pos + 2)))))
            {
                Pos += 2;
                Digits();
            }
        }
        foreach (var suffix in language.NumberSuffixes.OrderByDescending(s => s.Length))
            if (Ahead(suffix) && !IsNamePart(At(Pos + suffix.Length)))
            {
                Pos += suffix.Length;
                break;
            }
        Mark(start, Pos, SpanRole.Number);
        Done(Last.Number);
    }

    private void Digits()
    {
        while (char.IsAsciiDigit(Cur) || Cur == '_')
        {
            Step();
            Pos++;
        }
    }

    private void Name()
    {
        var start = Pos;
        if (Cur == '#') Pos++;
        while (IsNamePart(Cur))
        {
            Step();
            Pos++;
        }
        var word = Text[start..Pos];
        var role = NameRole(word);
        Mark(start, Pos, role);
        Done(role == SpanRole.Keyword ? Last.Keyword : Last.Word, word);
    }

    private SpanRole NameRole(string word)
    {
        if (word[0] == '#') return SpanRole.None;
        if (language.MacroIsFunction && Cur == '!' && At(Pos + 1) is '(' or '[' or '{') return SpanRole.Function;
        if (_last == Last.Punct && language.AttributeOpeners.Contains(_lastText, StringComparer.Ordinal)) return SpanRole.Type;
        var afterDot = _last == Last.Punct && _lastText == ".";
        var keyword = language.Keywords.Contains(word, StringComparer.Ordinal) || language.ContextualKeywords.Contains(word, StringComparer.Ordinal);
        if (keyword && !(afterDot && language.KeywordAfterDotIsPlain)) return SpanRole.Keyword;
        if (language.Types.Contains(word, StringComparer.Ordinal)) return SpanRole.Type;
        if (_last == Last.Keyword && language.FunctionDeclarers.Contains(_lastText, StringComparer.Ordinal)) return SpanRole.Function;
        if (_last == Last.Keyword && language.TypeDeclarers.Contains(_lastText, StringComparer.Ordinal)) return SpanRole.Type;
        if (language.DecoratorIsFunction && _last == Last.Punct && _lastText == "@") return SpanRole.Function;
        var next = Pos;
        while (At(next) is ' ' or '\t') next++;
        if (language.CallIsFunction && At(next) == '(' && !(language.CapitalisedCallIsPlain && char.IsUpper(word[0]))) return SpanRole.Function;
        return SpanRole.None;
    }
}
