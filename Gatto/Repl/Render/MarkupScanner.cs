using Gatto.Terminal;

namespace Gatto.Repl.Render;

//xml and html: tags, attributes, declarations, comments and CDATA. text between tags and its entities stay plain
internal sealed class MarkupScanner(FallbackLanguage language, string text) : ScannerBase(text)
{
    private const string CdataOpen = "<![CDATA[";
    private const string CdataClose = "]]>";

    protected override void Scan()
    {
        while (!AtEnd)
        {
            Step();
            if (language.BlockOpen is { } open && Ahead(open)) BlockComment(open, language.BlockClose!, false);
            else if (Ahead(CdataOpen)) Cdata();
            else if (Cur == '<' && At(Pos + 1) is '?' or '!' && char.IsLetter(At(Pos + 2))) Tag(2, false, true);
            else if (Cur == '<' && At(Pos + 1) == '/' && char.IsLetter(At(Pos + 2))) Tag(2, true, false);
            else if (Cur == '<' && char.IsLetter(At(Pos + 1))) Tag(1, false, false);
            else Pos++;
        }
    }

    private static bool IsNameChar(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' or ':' or '.';

    private static bool IsAttributeChar(char c) => c != '\0' && !char.IsWhiteSpace(c) && c is not ('/' or '>' or '=' or '<' or '"' or (char)39);

    private void Cdata()
    {
        var start = Pos;
        var close = Text.IndexOf(CdataClose, Pos + CdataOpen.Length, StringComparison.Ordinal);
        Pos = close < 0 ? Text.Length : close + CdataClose.Length;
        Mark(start, Pos, SpanRole.String);
    }

    //a tag ends at its closing mark, or where the next tag starts while this one streams without a close
    private void Tag(int openLength, bool closing, bool declaration)
    {
        Mark(Pos, Pos + openLength, SpanRole.Punct);
        Pos += openLength;
        var nameStart = Pos;
        while (IsNameChar(Cur))
        {
            Step();
            Pos++;
        }
        var name = Text[nameStart..Pos];
        Mark(nameStart, Pos, SpanRole.Keyword);
        var expectValue = false;
        while (!AtEnd)
        {
            Step();
            var c = Cur;
            if (char.IsWhiteSpace(c)) Pos++;
            else if (declaration && c == '?' && At(Pos + 1) == '>') Close(2);
            else if (c == '/' && At(Pos + 1) == '>') Close(2);
            else if (c == '>')
            {
                Close(1);
                if (!closing && !declaration) RawText(name);
            }
            else if (c == '<') return;
            else if (c == '=')
            {
                Mark(Pos, Pos + 1, SpanRole.Punct);
                Pos++;
                expectValue = true;
                continue;
            }
            else if (language.Quotes.FirstOrDefault(q => Ahead(q.Open)) is { } quote) Value(quote);
            else if (expectValue) Unquoted();
            else if (IsAttributeChar(c)) Attribute();
            else Pos++;
            if (c is '>' || (c is '/' or '?' && At(Pos - 1) == '>')) return;
            if (!char.IsWhiteSpace(c)) expectValue = false;
        }
    }

    private void Close(int length)
    {
        Mark(Pos, Pos + length, SpanRole.Punct);
        Pos += length;
    }

    private void Attribute()
    {
        var start = Pos;
        while (IsAttributeChar(Cur))
        {
            Step();
            Pos++;
        }
        Mark(start, Pos, SpanRole.Parameter);
    }

    //an attribute value takes its line rule and its escape rule from the table row. an unclosed value that may cross lines runs to the end
    private void Value(QuoteForm quote)
    {
        var start = Pos;
        Pos += quote.Open.Length;
        while (!AtEnd && !Ahead(quote.Close) && (quote.CrossesLines || !IsNewline(Cur)))
        {
            Step();
            Pos += quote.Escapes && Cur == Backslash && Pos + 1 < Text.Length ? 2 : 1;
        }
        if (Ahead(quote.Close)) Pos += quote.Close.Length;
        Mark(start, Pos, SpanRole.String);
    }

    private void Unquoted()
    {
        var start = Pos;
        while (!AtEnd && !char.IsWhiteSpace(Cur) && Cur != '>' && !Ahead("/>"))
        {
            Step();
            Pos++;
        }
        Mark(start, Pos, SpanRole.String);
    }

    //the body of a raw-text element is plain up to its own closing tag, so markup inside a script never reads as a tag
    private void RawText(string name)
    {
        var comparison = language.NamesIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!language.RawTextElements.Contains(name, comparison)) return;
        var end = Text.IndexOf("</" + name, Pos, language.NamesIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        Pos = end < 0 ? Text.Length : end;
    }
}
