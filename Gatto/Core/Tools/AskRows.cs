using System.Text.Json;

namespace Gatto.Core.Tools;

//one question's text and what the user chose, empty when skipped
public sealed record AskPair(string Question, IReadOnlyList<string> Chosen);

//the ask_user extension's arguments and result read back into questions and answers, for display only
public static class AskRows
{
    //null unless both parse, the counts agree and each header is its question's, paired by position since a header can repeat
    public static IReadOnlyList<AskPair>? Parse(string? argsJson, string resultText)
    {
        if (Questions(argsJson) is not { } asked) return null;
        try
        {
            using var doc = JsonDocument.Parse(resultText);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() != asked.Count) return null;
            var pairs = new List<AskPair>(asked.Count);
            var i = 0;
            foreach (var answer in doc.RootElement.EnumerateArray())
            {
                if (answer.ValueKind != JsonValueKind.Object
                    || !answer.TryGetProperty("header", out var h) || h.ValueKind != JsonValueKind.String || h.GetString() != asked[i].Header
                    || !answer.TryGetProperty("selected", out var s) || s.ValueKind != JsonValueKind.Array)
                    return null;
                var chosen = new List<string>();
                foreach (var pick in s.EnumerateArray())
                {
                    if (pick.ValueKind != JsonValueKind.String) return null;
                    chosen.Add(pick.GetString()!);
                }
                pairs.Add(new AskPair(asked[i].Question, chosen));
                i++;
            }
            return pairs;
        }
        catch (JsonException) { return null; }
    }

    //how many questions the call asked, null when the arguments are not the extension's
    public static int? QuestionsOf(string? argsJson) => Questions(argsJson)?.Count;

    private static List<(string Question, string Header)>? Questions(string? argsJson)
    {
        if (string.IsNullOrEmpty(argsJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(argsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("questions", out var qs) || qs.ValueKind != JsonValueKind.Array || qs.GetArrayLength() == 0)
                return null;
            var list = new List<(string, string)>();
            foreach (var q in qs.EnumerateArray())
            {
                if (q.ValueKind != JsonValueKind.Object
                    || !q.TryGetProperty("question", out var text) || text.ValueKind != JsonValueKind.String
                    || !q.TryGetProperty("header", out var header) || header.ValueKind != JsonValueKind.String)
                    return null;
                list.Add((text.GetString()!, header.GetString()!));
            }
            return list;
        }
        catch (JsonException) { return null; }
    }
}
