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
        //a think tag a template left in the answer, seen once ending an answer and splitting a repeated sentence
        "<think>",
        "</think>",
    };

    //when nothing matches, the same string instance comes back with 0, so the common case never allocates
    public static (string Text, int Count) Strip(string text) => Scan(text, null);

    //each token the strip takes, in order, read only when a notice must name them
    public static IReadOnlyList<string> Removed(string text)
    {
        var removed = new List<string>();
        Scan(text, removed);
        return removed;
    }

    //the one line the user sees, naming the model, where the tokens were found, and each distinct token once
    public static string Notice(string model, string channel, IReadOnlyList<string> removed) =>
        removed.Count == 1
            ? $"removed a stray {removed[0]} from {model}'s {channel}"
            : $"removed {removed.Count} stray template tags from {model}'s {channel}: {string.Join(", ", removed.Distinct())}";

    private static (string Text, int Count) Scan(string text, List<string>? removed)
    {
        if (text.Length == 0 || !text.Contains('<')) return (text, 0);
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
                        removed?.Add(token);
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

    //the strip for a stream, which holds back a tail that could still grow into a token, since a token can split across chunks
    public sealed class StreamFilter
    {
        private string _held = "";

        //a token holds '<' at its start only, so only the last '<' can open a tail worth holding, and no match crosses it
        public string Push(string chunk)
        {
            if (_held.Length == 0 && !chunk.Contains('<')) return chunk;
            var s = _held + chunk;
            var cut = s.LastIndexOf('<');
            _held = cut >= 0 && IsProperPrefix(s, cut) ? s[cut..] : "";
            return Strip(_held.Length == 0 ? s : s[..cut]).Text;
        }

        //a held tail is shorter than every token it could become, so it goes out as text
        public string Flush()
        {
            var tail = _held;
            _held = "";
            return tail;
        }

        private static bool IsProperPrefix(string s, int at)
        {
            var length = s.Length - at;
            foreach (var token in Allowlist)
                if (length < token.Length && string.CompareOrdinal(s, at, token, 0, length) == 0) return true;
            return false;
        }
    }
}
