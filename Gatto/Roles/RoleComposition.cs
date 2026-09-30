using System.Globalization;
using System.Text.Json;
using Gatto.Core;

namespace Gatto.Roles;

//what composing a role, its model, the nudges and the context files gives the caller: the system prompt and the settings for the request
public sealed record Composition(
    IReadOnlySet<string> Gates,
    string SystemText,
    ThinkingLevel Thinking,
    JsonElement? Sampling,
    JsonElement? ThinkingBody,
    string? ThinkingSuffix,
    bool RepairArmed,
    bool Checkpoints,
    //the values the text was composed from, so a resume can say what changed without composing again
    Gatto.Core.Loop.BaselineSources Sources);

//role x model x nudges x context files, pure and total so /role and /compact can recompose through it
public static class RoleComposition
{
    //the first block of every system prompt, naming the OS and the shell so a model that assumes Linux doesn't reach for ls. don't reword without a gate
    public const string BasePrompt =
        "You are gatto, a local agent harness on Windows. Use the provided tools; keep answers grounded in what you actually read or ran.\n" +
        "The shell is PowerShell. Never use Bash or Linux-only commands; when a Unix habit tempts you, find the PowerShell way.\n" +
        "Say what you actually conclude. If the user's idea has a problem, name it plainly before helping -- agreement you don't hold is a disservice.\n" +
        "When you are unsure, say so and say why; never present a guess as a fact.\n" +
        "Be direct and warm; no flattery, no filler praise, no apologizing for existing.";

    //the standing write instruction, armed by memory.enabled even when no fact exists, so the first one gets written. don't reword without a gate
    public const string MemoryNudge =
        "When you learn something durable — a build quirk, an environment gotcha, a decision, a fact " +
        "about this project you'd otherwise rediscover — save it with memory_write as you go; don't " +
        "wait for the end — compactions can't lose what's on disk. One fact per slug: before minting " +
        "a new slug, check the Memory block for an existing entry on the same subject and rewrite " +
        "that slug instead; delete a fact you've disproven. First line is the fact as one \"- \" " +
        "bullet and SHORT — aim under about 15 words, because line 1 of every fact rides every " +
        "future prompt; put values, specifics, and reasoning on the lines BELOW it, where they " +
        "cost nothing until something reads the file. Don't save what the repo already states or what only matters " +
        "to this conversation — asked to remember something like that anyway, save what was " +
        "non-obvious about it instead.";

    //the heading says what the block is and who wrote it, so the model doesn't read its own notes as instructions
    private const string MemoryHeading = "## Memory (project notes gatto keeps between sessions):";

    //the staleness caveat, next to the notes rather than in the nudge so a model meets it as it reads a fact
    private const string MemoryStaleness =
        "[notes were true when written — verify a path, flag, or version before relying on one]";

    //the block names its own directory absolutely, since a relative-looking path gets resolved against the user's home. omitted when cwd is null
    private static string MemoryLocation(string root) =>
        $"[stored in {Gatto.Core.Memory.MemoryDir.DirFor(root)} — one file per fact]";

    //the index comes from the caller, so Compose never touches disk, and the block is omitted when null. a mid-session memory_write must never move the prefix
    public static Composition Compose(
        RoleFile role,
        Model? model,
        IReadOnlyList<(string Path, string Content)> contextFiles,
        IReadOnlyDictionary<string, JsonElement?>? endpointThinkingMap,
        //required, and null means no working-directory line, since an optional parameter is one a call site can silently omit
        string? cwd,
        DateTime? date = null,
        string? memoryIndex = null,
        int memoryTruncatedLines = 0,
        bool memoryNudge = false,
        IReadOnlyList<(string Extension, string Line)>? policyLines = null)
    {
        //the gates are the role's and the model's, merged case-insensitively
        var gates = new HashSet<string>(role.Gates, StringComparer.OrdinalIgnoreCase);
        if (model?.Nudges?.Gates is { } modelGates)
            foreach (var g in modelGates) gates.Add(g);

        //the date is passed in by the caller so Compose stays pure, formatted in ISO order with the invariant culture
        var dateLine = date is { } d
            ? $"Today's date: {d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}."
            : null;

        var modelLine = model is null ? null : $"Model: {model.Id}.";   //a local model can't tell which model it is, so the stanza names the profile id the footer and the request body show
        //the working directory is caller-supplied and composed once, an environment fact, so the model is told where it is. null means no line
        var cwdLine = string.IsNullOrWhiteSpace(cwd) ? null : $"Working directory: {cwd}.";
        var envParts = new[] { dateLine, modelLine, cwdLine }.Where(p => p is not null).ToArray();
        var envLine = envParts.Length == 0 ? null : string.Join("\n", envParts);

        //the memory block goes last, after the context files, and is built outside the whitespace filter since an empty index still renders its truncation note
        var memoryBlock = memoryIndex is null
            ? null
            : MemoryHeading + "\n"
              + (string.IsNullOrWhiteSpace(cwd) ? "" : MemoryLocation(cwd) + "\n")
              + MemoryStaleness + "\n" + memoryIndex
              //count facts here, since what the budget drops is whole facts while the operator prunes files
              + (memoryTruncatedLines > 0
                  ? $"\n[index truncated at budget — {Plural.Of(memoryTruncatedLines, "fact")} not shown]"
                  : "");

        //every extension's policy line, in one block ordered by extension name. the block sits upstream of the context files, so the user's own notes keep the last word
        var policyOrdered = (policyLines ?? Array.Empty<(string Extension, string Line)>()).OrderBy(p => p.Extension, StringComparer.Ordinal).ToList();
        var policyBlock = policyOrdered.Count == 0 ? null : string.Join("\n", policyOrdered.Select(p => p.Line));

        //fixed order, empty parts skipped. the date sits before any model/role text so model and role keep the last word
        var blocks = new[] { BasePrompt, memoryNudge ? MemoryNudge : null, envLine, model?.SystemAppend, role.Append, model?.Nudges?.Append, policyBlock }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .Concat(contextFiles.Select(f => $"## Context: {f.Path}\n{f.Content}"));
        if (memoryBlock is not null) blocks = blocks.Append(memoryBlock);
        var systemText = string.Join("\n\n", blocks);

        //the role's request wins, then the model's own default_effort, then Medium, all capped by the nudge cap
        var requested = role.ThinkingRequested ?? model?.Profile.DefaultEffort ?? ThinkingLevel.Medium;
        var effective = Thinking.Min(requested, model?.Nudges?.ThinkingCap ?? ThinkingLevel.Max);

        //the model map wins, the endpoint map is the cloud path. a prompt_suffix goes on the user message, since the request body can't express it
        var map = model?.Profile.Thinking ?? endpointThinkingMap;
        var (thinkingBody, thinkingSuffix) = Thinking.ResolveEntry(map, effective);

        //the level reported is the one ResolveEntry resolved to, since a map with a gap would otherwise show a level the wire never sends
        var thinking = Thinking.LandedLevel(map, effective) ?? effective;

        //the role's sampling goes into the request body, since the model profile's own sampling belongs to the server argv
        return new Composition(
            gates,
            systemText,
            thinking,
            role.Sampling,
            thinkingBody,
            thinkingSuffix,
            model?.Repair is not null,
            role.Checkpoints,
            new Gatto.Core.Loop.BaselineSources(
                date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                memoryIndex,
                contextFiles.Select(f => new Gatto.Core.Loop.ContextFileMark(f.Path, Gatto.Core.Loop.BaselineMarks.Sha256Hex(f.Content))).ToList(),
                policyOrdered.Select(p => new Gatto.Core.Loop.PolicyMark(p.Extension, p.Line)).ToList(),
                Gatto.Core.Loop.BaselineMarks.Sha256Hex(role.Append ?? ""),
                Gatto.Core.Loop.BaselineMarks.Sha256Hex((model?.SystemAppend ?? "") + "\n" + (model?.Nudges?.Append ?? ""))));
    }
}
