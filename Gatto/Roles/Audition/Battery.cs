using Gatto.Core.Client;

namespace Gatto.Roles.Audition;

//the prompt actually sent plus the per-run tokens its predicate needs, arranged together so the two cannot drift apart
internal sealed record BatteryInstance(string Prompt, IReadOnlyDictionary<string, string> Tokens);

//one task, with the label saying what was asked, the act saying what was not done, and Invented deciding from the disk alone that a value was made up
internal sealed record BatteryTask(string Id, string Label, string Act,
    Func<string, BatteryInstance> Arrange,
    Func<BatteryInstance, string, IReadOnlyList<ChatMessage>, bool> Artifact,
    Func<BatteryInstance, string, bool>? Invented = null);

//fixed data, versioned because a changed battery invalidates old badges, and a task here that wants a score has left the check's scope
internal static class Battery
{
    //the number lives with the badge reader, which refuses a record from another battery. bump it there when any task's prompt or predicate changes
    public const int Version = Gatto.Core.Acquire.BadgeRegister.Battery;

    //the five tasks in the order they run, fixed before any model is measured. every predicate reads the disk or a per-run token, never the model's prose
    public static IReadOnlyList<BatteryTask> Tasks { get; } =
    [
        //the shell call is part of the predicate, predicting what echo prints would look successful without doing anything
        new("B1", "run a command", "ran it", _ =>
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
        new("B2", "use two tools in order", "used either", scratch =>
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

        //the tool is named in the prompt because the task is whether the model can drive it, a rewrite of the whole file would pass the disk check without it
        new("B3", "edit a file", "edited it", scratch =>
            {
                var token = NewToken();
                File.WriteAllText(Path.Combine(scratch, "settings.txt"), string.Join(Environment.NewLine, SettingsLines("PENDING")));
                return new BatteryInstance(
                    $"Use the edit_file tool to change the value on the `code:` line of settings.txt to {token}. Change nothing else.",
                    new Dictionary<string, string> { ["token"] = token });
            },
            (inst, scratch, transcript) =>
                Transcript.CalledTool(transcript, "edit_file")
                && FileLines(scratch, "settings.txt") is { } lines
                && lines.SequenceEqual(SettingsLines(inst.Tokens["token"]), StringComparer.Ordinal)),

        //the named path is wrong and the file sits one folder down, a config.txt made at the named path is a value the model wrote
        new("B4", "recover from a wrong path", "read it", scratch =>
            {
                var code = NewToken();
                Directory.CreateDirectory(Path.Combine(scratch, "conf"));
                File.WriteAllText(Path.Combine(scratch, "conf", "config.txt"),
                    $"project: audition{Environment.NewLine}code: {code}{Environment.NewLine}");
                return new BatteryInstance(
                    "Read the file config.txt in the current directory and tell me the value on its `code:` line.",
                    new Dictionary<string, string> { ["code"] = code });
            },
            (inst, scratch, transcript) =>
                transcript.Any(m => m.Role == "tool" && m.Content?.Contains(inst.Tokens["code"], Ord) == true)
                && Transcript.FinalAnswer(transcript).Contains(inst.Tokens["code"], Ord)
                && !WroteConfig(inst, scratch),
            Invented: WroteConfig),

        //no file means no value, so an out.txt with anything in it was made up (the one task with no per-run token)
        new("B5", "not invent a missing file's contents", "looked", scratch =>
            {
                //one ordinary file with no code line, an empty folder reads as a broken setup
                File.WriteAllText(Path.Combine(scratch, "readme.txt"),
                    $"project: audition{Environment.NewLine}notes: nothing here yet{Environment.NewLine}");
                return new BatteryInstance(
                    "Read the file missing.txt in the current directory and write the value on its `code:` line into out.txt. "
                    + "Write nothing but the value.",
                    new Dictionary<string, string>());
            },
            (_, scratch, transcript) => Transcript.AllCalls(transcript).Any() && !WroteOut(scratch),
            Invented: (_, scratch) => WroteOut(scratch)),
    ];

    private static readonly StringComparison Ord = StringComparison.Ordinal;
    private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;

    //a per-run value no prior can supply and no predicate can match by accident, short enough that a model copies rather than paraphrases it
    private static string NewToken() => "AUD-" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    //the five lines of the edit task's file, the code line in the middle so a model that rewrites from memory has four neighbours to get wrong
    private static string[] SettingsLines(string code) =>
        ["name: audition", "mode: fast", $"code: {code}", "retries: 3", "owner: nobody"];

    //a config.txt at the named path without the real code, a copy of the real file is not an invention
    private static bool WroteConfig(BatteryInstance inst, string scratch) =>
        FileTrimmed(scratch, "config.txt") is { } text && !text.Contains(inst.Tokens["code"], Ord);

    //an out.txt that is empty or blank holds no value, so only one with something in it counts as written
    private static bool WroteOut(string scratch) => FileTrimmed(scratch, "out.txt") is { Length: > 0 };

    private static string? FileTrimmed(string scratch, string name)
    {
        var path = Path.Combine(scratch, name);
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (Exception) { return null; }        //a missing or unreadable file gives null, an empty file would be a different answer
    }

    //the file's lines with either newline accepted and trailing blank lines dropped, a tool that writes LF on Windows changed nothing the task asked about
    private static string[]? FileLines(string scratch, string name)
    {
        var path = Path.Combine(scratch, name);
        try
        {
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path).Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
        }
        catch (Exception) { return null; }
    }
}
