using Gatto.Core.Client;

namespace Gatto.Roles.Audition;

//the questions the predicates and the detectors ask of a transcript, one home so two tasks cannot read the same fact differently
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
}
