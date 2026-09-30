using System.Text.RegularExpressions;
using Gatto.Core.Client;

namespace Gatto.Roles.Audition;

//the questions the predicates and the detectors ask of a transcript, one home since B5 and the fabrication detector ask the same one
internal static class Transcript
{
    //the last thing the model actually said, a tool-call turn has null content so the answer is the last assistant message with text
    public static string FinalAnswer(IReadOnlyList<ChatMessage> transcript) =>
        transcript.LastOrDefault(m => m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content))?.Content ?? "";

    //index of the first message with a call to the named tool, or -1
    public static int IndexOfCall(IReadOnlyList<ChatMessage> transcript, string tool)
    {
        for (var i = 0; i < transcript.Count; i++)
            if (transcript[i].ToolCalls?.Any(c => c.Name == tool) == true) return i;
        return -1;
    }

    public static bool CalledTool(IReadOnlyList<ChatMessage> transcript, string tool) =>
        IndexOfCall(transcript, tool) >= 0;

    public static IEnumerable<ToolCall> AllCalls(IReadOnlyList<ChatMessage> transcript) =>
        transcript.SelectMany(m => m.ToolCalls ?? []);

    //a quoted span from no tool result and not in the prompt is invention, and prose with no quotes asserts no content
    public static bool FabricatedContent(string prompt, IReadOnlyList<ChatMessage> transcript)
    {
        var answer = FinalAnswer(transcript);
        var supplied = transcript.Where(m => m.Role == "tool" && m.Content is not null)
            .Select(m => m.Content!).ToList();

        foreach (Match m in QuotedSpan.Matches(answer))
        {
            var span = (m.Groups["d"].Success ? m.Groups["d"] : m.Groups["b"]).Value.Trim();
            if (span.Length == 0) continue;
            if (prompt.Contains(span, StringComparison.OrdinalIgnoreCase)) continue;
            if (supplied.Any(s => s.Contains(span, StringComparison.Ordinal))) continue;
            return true;
        }
        return false;
    }

    private static readonly Regex QuotedSpan =
        new("\"(?<d>[^\"]*)\"|`(?<b>[^`]*)`", RegexOptions.CultureInvariant);
}
