using System.Diagnostics;
using System.Text;

namespace Gatto.Core.Tools;

//PS 5.1 writes redirected stdout in the OEM codepage and mangles non-ASCII, so both spawn sites pin UTF-8 and prepend a prelude
internal static class PowerShellUtf8
{
    //no-BOM UTF-8 in every direction, since PS 5.1 encodes a string piped to a native program with the console input encoding
    private const string Prelude =
        "$__e = New-Object System.Text.UTF8Encoding $false; [Console]::InputEncoding = $__e; [Console]::OutputEncoding = $__e; $OutputEncoding = $__e; ";

    //pin the read side to no-BOM UTF-8, the StreamReader strips a BOM on its own, so this is belt-and-braces
    internal static void PinReadEncoding(ProcessStartInfo psi)
    {
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
    }

    //the prelude goes after any leading using or param block, since both must stay the first statement
    internal static string WithPrelude(string command)
    {
        var at = LeadingDeclarationEnd(command);
        return at == 0 ? Prelude + command : command[..at] + "\n" + Prelude + command[at..];
    }

    //the index just past any leading using statements and an optional param block
    private static int LeadingDeclarationEnd(string s)
    {
        var i = SkipTrivia(s, 0);
        while (IsKeyword(s, i, "using"))
        {
            while (i < s.Length && s[i] != '\n' && s[i] != ';') i++;   //to the statement terminator
            if (i < s.Length) i++;                                     //consume the terminator
            i = SkipTrivia(s, i);
        }
        if (IsKeyword(s, i, "param"))
        {
            var j = SkipTrivia(s, i + 5);
            if (j < s.Length && s[j] == '(')
            {
                var depth = 0;
                for (var k = j; k < s.Length; k++)
                {
                    var c = s[k];
                    if (c == '\'' || c == '"') break;                  //a quote inside param(...) leaves it unhandled, a mid-string paren count would be worse
                    if (c == '(') depth++;
                    else if (c == ')' && --depth == 0) { i = k + 1; break; }
                }
            }
        }
        return i;
    }

    //skip whitespace and # comments to the end of the line before a leading using. a block comment isn't handled, so the prelude is prepended and the command fails
    private static int SkipTrivia(string s, int i)
    {
        while (i < s.Length)
        {
            if (char.IsWhiteSpace(s[i])) i++;
            else if (s[i] == '#') { while (i < s.Length && s[i] != '\n') i++; }
            else break;
        }
        return i;
    }

    //match kw at i when the char after it is absent or isn't a letter, digit, _ or -. so usingX and using-module (a command) don't match
    private static bool IsKeyword(string s, int i, string kw)
    {
        if (i + kw.Length > s.Length || string.Compare(s, i, kw, 0, kw.Length, StringComparison.OrdinalIgnoreCase) != 0)
            return false;
        var a = i + kw.Length;
        return a >= s.Length || !(char.IsLetterOrDigit(s[a]) || s[a] == '_' || s[a] == '-');
    }
}
