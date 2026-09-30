using System.Text;

namespace Gatto.Core;

//strips the control tokens a model leaks as text, from model output only, exact strings only (a pattern would eat real tool results' tokens)
public static class ControlTokens
{
    private static readonly string[] Allowlist =
    {
        "<|tool_calls_section_begin|>",
        "<|tool_calls_section_end|>",
        "<|tool_call_begin|>",
        "<|tool_call_end|>",
    };

    //when nothing matches, the same string instance comes back with 0, so the common case never allocates
    public static (string Text, int Count) Strip(string text)
    {
        if (text.Length == 0 || !text.Contains("<|", StringComparison.Ordinal)) return (text, 0);
        var sb = new StringBuilder(text.Length);
        var count = 0;
        var i = 0;
        while (i < text.Length)
        {
            var matched = false;
            if (text[i] == '<')
            {
                foreach (var token in Allowlist)
                {
                    if (i + token.Length <= text.Length &&
                        string.CompareOrdinal(text, i, token, 0, token.Length) == 0)
                    {
                        count++;
                        i += token.Length;
                        matched = true;
                        break;
                    }
                }
            }
            if (!matched) sb.Append(text[i++]);
        }
        return count == 0 ? (text, 0) : (sb.ToString(), count);
    }
}
