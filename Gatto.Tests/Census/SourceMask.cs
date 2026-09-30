namespace Gatto.Tests.Census;

//one masker for every census, drifting copies would disagree about what is code. it blanks raw and verbatim strings too, or a multi-line body reads as code
internal static class SourceMask
{
    //replace the body of every literal and comment with spaces, char for char. the length survives, so a reported offset still points at the real line
    public static string Mask(string s)
    {
        var b = s.ToCharArray();
        var n = s.Length;

        void Blank(int from, int to)
        {
            for (var k = from; k <= Math.Min(to, n - 1); k++)
                if (b[k] != '\n') b[k] = ' ';
        }

        for (var i = 0; i < n; i++)
        {
            var c = s[i];

            if (c == '/' && i + 1 < n && (s[i + 1] == '/' || s[i + 1] == '*'))
            {
                var block = s[i + 1] == '*';
                var j = i + 2;
                while (j < n && !(block ? s[j - 1] == '*' && s[j] == '/' : s[j] == '\n')) j++;
                Blank(i, j);
                i = j;
            }
            else if (c == '"' && i + 2 < n && s[i + 1] == '"' && s[i + 2] == '"')
            {
                //a raw string ends only at a quote run at least as long as the opening fence, and nothing inside it escapes.
                var fence = 0;
                while (i + fence < n && s[i + fence] == '"') fence++;

                var j = i + fence;
                while (j < n)
                {
                    if (s[j] != '"') { j++; continue; }

                    var run = 0;
                    while (j + run < n && s[j + run] == '"') run++;
                    if (run >= fence) { j += run - 1; break; }
                    j += run;
                }
                Blank(i, j);
                i = j;
            }
            else if (c == '@' && i + 1 < n && s[i + 1] == '"')
            {
                //a verbatim string spans lines, and a doubled quote is an escaped one, so the scan must skip pairs.
                var j = i + 2;
                while (j < n)
                {
                    if (s[j] != '"') { j++; continue; }
                    if (j + 1 < n && s[j + 1] == '"') { j += 2; continue; }
                    break;
                }
                Blank(i, j);
                i = j;
            }
            else if (c == '"' || c == '\'')
            {
                //stop the literal scan at the newline. an unterminated literal is a syntax error, and blanking to the end of the file would hide real code.
                var j = i + 1;
                while (j < n && s[j] != '\n')
                {
                    if (s[j] == '\\') { j += 2; continue; }
                    if (s[j] == c) break;
                    j++;
                }
                Blank(i, j);
                i = j;
            }
        }

        return new string(b);
    }
}
