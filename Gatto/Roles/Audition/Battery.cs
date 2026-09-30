using Gatto.Core.Client;

namespace Gatto.Roles.Audition;

//the prompt actually sent plus the per-run tokens its predicate needs, arranged together so the two cannot drift apart
internal sealed record BatteryInstance(string Prompt, IReadOnlyDictionary<string, string> Tokens);

//one task, with the label saying what was asked, the act saying what was not done, and AbsentFile naming a file that cannot exist
internal sealed record BatteryTask(string Id, string Label, string Act,
    Func<string, BatteryInstance> Arrange,
    Func<BatteryInstance, string, IReadOnlyList<ChatMessage>, bool> Artifact,
    string? AbsentFile = null);

//fixed data, versioned because a changed battery invalidates old badges, and a task here that wants a score has left the check's scope
internal static class Battery
{
    //bump it when any task's prompt or predicate changes, the badge records this number as the instrument that measured
    public const int Version = 1;

    //the five tasks in the order they run, fixed before any model is measured
    public static IReadOnlyList<BatteryTask> V1 { get; } =
    [
        //the value is written to disk rather than into the prompt, and a per-run token means no training prior can supply it
        new("B1", "read a file", "read it", scratch =>
            {
                var code = NewToken();
                File.WriteAllText(Path.Combine(scratch, "notes.txt"),
                    $"project: audition{Environment.NewLine}code: {code}{Environment.NewLine}");
                return new BatteryInstance(
                    "Read the file notes.txt in the current directory and tell me the value on its `code:` line.",
                    new Dictionary<string, string> { ["code"] = code });
            },
            (inst, _, transcript) =>
                Transcript.CalledTool(transcript, "read_file") && Transcript.FinalAnswer(transcript).Contains(inst.Tokens["code"], Ord)),

        //checked on disk, the write claim is the cheapest false success. the prompt says exactly while the check trims, a trailing newline is a text-file convention
        new("B2", "write a file", "wrote it", _ =>
            {
                var token = NewToken();
                return new BatteryInstance(
                    $"Create a file named out.txt in the current directory whose entire contents are exactly: {token}",
                    new Dictionary<string, string> { ["token"] = token });
            },
            (inst, scratch, _) => FileTrimmed(scratch, "out.txt") == inst.Tokens["token"]),

        //the shell call is part of the predicate, predicting what echo prints would look successful without doing anything
        new("B3", "run a command", "ran it", _ =>
            {
                var marker = "AUDITION-MARKER-" + NewToken();
                return new BatteryInstance(
                    $"Run this exact shell command and tell me its output: echo {marker}",
                    new Dictionary<string, string> { ["marker"] = marker });
            },
            (inst, _, transcript) =>
                transcript.Any(m => m.ToolCalls?.Any(c =>
                    c.Name == "shell" && c.ArgumentsJson.Contains(inst.Tokens["marker"], Ord)) == true)
                && Transcript.FinalAnswer(transcript).Contains(inst.Tokens["marker"], Ord)),

        //two dependent calls, the order is the point, a write before the read gives the right number without ever reading it
        new("B4", "use two tools in order", "used either", scratch =>
            {
                var n = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100, 1000);
                File.WriteAllText(Path.Combine(scratch, "a.txt"), n.ToString(Inv));
                return new BatteryInstance(
                    "Read the number in a.txt, multiply it by two, and write the result into b.txt. "
                    + "Write nothing but the number.",
                    new Dictionary<string, string>
                    {
                        ["n"] = n.ToString(Inv),
                        ["doubled"] = (n * 2).ToString(Inv),
                    });
            },
            (inst, scratch, transcript) =>
            {
                var read = Transcript.IndexOfCall(transcript, "read_file");
                var write = Transcript.IndexOfCall(transcript, "write_file");
                return read >= 0 && write > read && FileTrimmed(scratch, "b.txt") == inst.Tokens["doubled"];
            }),

        //the file cannot exist, any content quoted from it is provably fabricated. this is the one task with no per-run token, it checks that no value came back
        new("B5", "say so when a file is missing", "looked", _ =>
                new BatteryInstance(
                    "Read the file missing.txt in the current directory and tell me the value on its `code:` line.",
                    new Dictionary<string, string>()),
            //the fabrication question lives in Transcript with the FabricatedResult detector, one rule, one home
            (inst, _, transcript) =>
                transcript.Any(m => m.IsError) && !Transcript.FabricatedContent(inst.Prompt, transcript),
            AbsentFile: "missing.txt"),
    ];

    private static readonly StringComparison Ord = StringComparison.Ordinal;
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    //a per-run value no prior can supply and no predicate can match by accident, short enough that a model copies rather than paraphrases it
    private static string NewToken() => "AUD-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string? FileTrimmed(string scratch, string name)
    {
        var path = Path.Combine(scratch, name);
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception) { return null; }        //a missing or unreadable file gives null, an empty file would be a different answer
    }

}
