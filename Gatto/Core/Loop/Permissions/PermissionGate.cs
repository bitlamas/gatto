using System.Text;
using System.Text.Json;
using Gatto.Core.Client;
using Gatto.Core.Tools;

namespace Gatto.Core.Loop.Permissions;

//fail closed on the tool_call hook, a blocked call throws and the tool's ExecuteAsync never runs
public sealed class PermissionGate(
    PermissionStore store, IPermissionPrompter? prompter, bool autoYes, CheckpointApproval? approval = null,
    WildState? wild = null)
{
    //how the gate reads a file for an edit's view, a test counts the reads through it
    internal Func<string, string> ReadFile { get; init; } = File.ReadAllText;

    //how the gate reads a file a write would replace, a test makes it throw
    internal Func<string, byte[]> ReadBytes { get; init; } = File.ReadAllBytes;

    //only tools that never mutate anything, and the names must match the registry's lowercase snake_case exactly
    private static readonly HashSet<string> ReadClass =
        new(StringComparer.Ordinal) { "read_file", "glob", "grep", "task_restate", "recall_memory" };

    //writers whose whole write surface is bounded by construction, the bar for any tool added here
    private static readonly HashSet<string> BoundedWriteClass =
        new(StringComparer.Ordinal) { "memory_write" };

    //read-class status granted for this session, kept apart from the built-ins so an unvetted extension can never widen them
    private readonly HashSet<string> _instanceReadClass = new(StringComparer.Ordinal);

    //lets a tool pass without a prompt, called only for a tool a vetted shipped extension declared read-class
    public void AllowReadClass(string toolName) => _instanceReadClass.Add(toolName);

    //the subagent's name during its gate check, so its prompts show an [agent-name] tag and the main loop's never do
    public string? AgentLabel { get; set; }

    private enum Kind { Shell, Write, Opaque }

    private sealed record Classified(Kind Kind, string? Command, string? FullPath, PermissionRequest Request);

    public Task CheckAsync(HookPayload payload)
    {
        var call = payload.Call;
        if (call is null) return Task.CompletedTask;

        if (ReadClass.Contains(call.Name) || BoundedWriteClass.Contains(call.Name) || _instanceReadClass.Contains(call.Name))
            return Task.CompletedTask;                                   //1. read-class or bounded-write, both pass without asking
        if (autoYes || wild?.On == true) return Task.CompletedTask;     //2. the --yes flag or wild mode, both pass

        //2b. the call the user already approved at the CheckpointGate pause, cleared on the first match so it can't leak
        if (approval?.Call is not null && ReferenceEquals(approval.Call, payload.Call))
        {
            approval.Call = null;
            return Task.CompletedTask;
        }

        var c = Classify(call);

        var granted = c.Kind switch                                     //3. a matching standing grant from the store
        {
            Kind.Shell => store.AllowsShell(c.Command!),
            Kind.Write => store.AllowsWrite(c.FullPath!),
            Kind.Opaque => store.AllowsTool(call.Name),                 //an opaque call is granted by tool name, which is how extension tools work
            _ => false,
        };
        if (granted) return Task.CompletedTask;

        if (prompter is null)                                           //4. no prompter means no terminal to ask on, so throw
            throw new InvalidOperationException(
                "no interactive terminal to grant permission (use --yes or pre-grant in .gatto\\permissions.json)");

        //5. ask, stamped with the subagent label, through AskWithReason whose default forwards to Ask with no reason. a call that cannot run is refused first
        if (Unrunnable(call) is { } why) throw new CannotApplyException(why);
        var decision = prompter.AskWithReason(WithView(c) with { Agent = AgentLabel });
        switch (decision.Answer)
        {
            case PermissionAnswer.Once:
                return Task.CompletedTask;                             //allow once and persist nothing
            case PermissionAnswer.Always:
                Persist(c);
                return Task.CompletedTask;
            //a cancel has its own arm so it throws CancelMessage, folding it into default would tell the model the user declined
            case PermissionAnswer.Cancel:
                throw new InvalidOperationException(CancelMessage);
            case PermissionAnswer.Deny:
            default:
                throw new InvalidOperationException(DenyMessage(decision.Reason));
        }
    }

    //what a cancelled prompt tells the model, bare and pinned so no intent is invented for the user
    internal const string CancelMessage = "cancelled by user";

    //a caller may name this length in its notice but must never measure with it, the reason is capped and sanitized in PrepareReason
    internal const int MaxReasonLength = 256;

    //the model's copy of a deny reason and whether the cap cut it, with null Text meaning the bare message
    internal readonly record struct ModelReason(string? Text, bool Truncated)
    {
        //the user typed something and none of it reaches the model, set when the reason sanitized or capped away to nothing
        public bool LostEntirely { get; init; }
    }

    //what a bare deny tells the model, it refuses and steers the model to ask instead of retrying
    internal const string DenyNudge =
        "The user declined this tool call. Do not retry it — ask the user what to do instead.";

    //prefix for a deny that has a reason, no ask-instead clause follows since the reason is the steer
    internal const string DenyWithReasonPrefix = "The user declined this tool call: ";

    //the nudge when there is no usable reason, otherwise the prefix plus the reason and no punctuation of our own
    private static string DenyMessage(string? reason)
    {
        var text = PrepareReason(reason).Text;
        return text is null ? DenyNudge : DenyWithReasonPrefix + text;
    }

    //strip every control character then cap the reason, the only place either happens. the Truncated flag means the model's copy differs from what the user typed
    internal static ModelReason PrepareReason(string? reason)
    {
        if (string.IsNullOrEmpty(reason)) return default;   //default is Text null and Truncated false
        var sb = new StringBuilder(reason.Length);
        foreach (var ch in reason)
        {
            if (char.IsControl(ch)) continue;
            sb.Append(ch);
        }
        var all = sb.ToString();
        //cap first, then trim, that order is the model-facing behavior
        var text = (all.Length <= MaxReasonLength ? all : all[..MaxReasonLength]).Trim();
        var truncated = !string.Equals(text, all.Trim(), StringComparison.Ordinal);
        //an empty text here means the user typed something and none of it reaches the model, decided in the same pass
        return new ModelReason(text.Length == 0 ? null : text, truncated) { LostEntirely = text.Length == 0 };
    }

    private void Persist(Classified c)
    {
        //no offer means nothing to persist, so Always degrades to a one-time allow rather than inventing a grant
        if (string.IsNullOrEmpty(c.Request.GrantOffer)) return;
        if (c.Kind == Kind.Shell) store.GrantShellPrefix(c.Request.GrantOffer, persist: true);
        else if (c.Kind == Kind.Write) store.GrantWriteDir(c.Request.GrantOffer, persist: true);
        else if (c.Kind == Kind.Opaque) store.GrantTool(c.Request.GrantOffer, persist: true);
    }

    private Classified Classify(ToolCall call)
    {
        var tool = call.Name;
        var raw = call.ArgumentsJson;
        JsonElement args = default;
        var parsed = false;
        try
        {
            using var doc = JsonDocument.Parse(raw.Length > 0 ? raw : "{}");
            args = doc.RootElement.Clone();
            parsed = true;
        }
        catch (JsonException) { } //fall through to opaque

        if (tool == "shell")
        {
            if (parsed && TryGetString(args, "command", out var command))
                //no Always offer for a chained command or one with a flag-shaped second token, either prefix would be a dead or dangerous grant
                return new Classified(Kind.Shell, command, null,
                    new PermissionRequest(tool, command,
                        ShellChainingGuard.IsChained(command) || PermissionStore.HasFlagShapedSecondToken(command)
                            ? null : PermissionStore.SuggestShellPrefix(command)));
            return Opaque(tool, raw, offerGrant: false);
        }

        if (tool is "write_file" or "edit_file")
        {
            if (parsed && TryGetString(args, "path", out var path))
            {
                var full = ResolvePath(path);
                //a write's prompt shows its head, an edit's shows its change
                var (previewLines, previewTotal) = tool == "write_file" ? WritePreview(tool, args) : (null, 0);
                var (editOld, editNew, _) = EditArgs.Of(tool, raw);
                return new Classified(Kind.Write, null, full,
                    new PermissionRequest(tool, WriteSummary(tool, full, args), WriteGrantOffer(full),
                        PreviewLines: previewLines, PreviewTotalLines: previewTotal, EditOld: editOld, EditNew: editNew));
            }
            return Opaque(tool, raw, offerGrant: false);
        }

        //the only opaque path that offers an Always, a malformed shell or write call must never mint a tool-name grant
        return Opaque(tool, raw, offerGrant: true);
    }

    //the edit's view on the file as it is, read only here where the prompt is about to be asked. any failure gives no view and the prompt all the same
    private PermissionRequest WithView(Classified c)
    {
        if (c.Request is { Tool: "write_file" } w && c.FullPath is { } target)
            return File.Exists(target) ? w with { Existing = Measure(target) } : w;
        if (c.Request is not { Tool: "edit_file", EditOld: { } oldS, EditNew: { } newS } r || c.FullPath is not { } path) return c.Request;
        //an edit that cannot apply is refused here in the tool's words, a prompt would ask the user to approve what cannot happen
        if (oldS.Length == 0) throw new CannotApplyException(EditLocate.EmptyOld);
        if (!File.Exists(path)) throw new CannotApplyException(EditLocate.NoFile(path));
        string text;
        try { text = ReadFile(path); }
        catch (Exception) { return r; }
        var (match, count) = EditLocate.Find(text, oldS, newS, default);
        if (count == 0) throw new CannotApplyException(EditLocate.NotFound(path));
        if (count > 1) throw new CannotApplyException(EditLocate.NotUnique(count, path));
        return r with { View = EditLocate.View(text, match!) };
    }

    //the arguments each file tool cannot run without, in the order the tool reads them, so the first one missing is the one the tool would name
    private static readonly Dictionary<string, string[]> RequiredArguments = new(StringComparer.Ordinal)
    {
        ["write_file"] = ["path", "content"],
        ["edit_file"] = ["path", "old_string", "new_string"],
    };

    //why a call fails before its tool can run, in the words the loop or the tool would say, null when nothing stops it here
    private static string? Unrunnable(ToolCall call)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(call.ArgumentsJson.Length > 0 ? call.ArgumentsJson : "{}");
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex) { return LoopErrors.MalformedArgumentsPrefix + ex.Message; }
        if (!RequiredArguments.TryGetValue(call.Name, out var required)) return null;
        foreach (var name in required)
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
                return ToolArgs.MissingParameter(name);
        return null;
    }

    //past this size the gate gives bytes from the file's length and never reads it, a prompt must not wait on a huge file
    private const long MeasureCap = 16 * 1024 * 1024;

    //text is a file with no NUL in its first 8000 bytes, the test git uses. a read that fails still says the file exists
    private ExistingFile Measure(string path)
    {
        try
        {
            var length = new FileInfo(path).Length;
            if (length > MeasureCap) return new ExistingFile(null, length);
            var bytes = ReadBytes(path);
            return Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, 8000)) >= 0
                ? new ExistingFile(null, bytes.Length)
                : new ExistingFile(CountLines(Encoding.UTF8.GetString(bytes)), bytes.Length);
        }
        catch (Exception) { return new ExistingFile(null, null); }
    }

    private static Classified Opaque(string tool, string raw, bool offerGrant) =>
        new(Kind.Opaque, null, null, new PermissionRequest(tool, raw, offerGrant ? tool : null));

    private string ResolvePath(string path) =>
        Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(store.ProjectRoot, path));

    //write_file is sized in lines of content, edit_file in UTF-8 bytes of new_string, and a missing size argument leaves the path alone
    private static string WriteSummary(string tool, string fullPath, JsonElement args)
    {
        if (tool == "write_file" && TryGetString(args, "content", out var content))
            return $"{fullPath} (+{Plural.Of(CountLines(content), "line")})";
        if (tool == "edit_file" && TryGetString(args, "new_string", out var newString))
            return $"{fullPath} ({Plural.Of(Encoding.UTF8.GetByteCount(newString), "byte")})";
        return fullPath;
    }

    //the head of the write and its total line count, counted by CountLines so a trailing newline adds no blank line
    private static (IReadOnlyList<string>? Lines, int Total) WritePreview(string tool, JsonElement args) =>
        FilePreview.Of(tool, args);

    private static int CountLines(string s)
    {
        if (s.Length == 0) return 0;
        var n = 0;
        foreach (var ch in s) if (ch == '\n') n++;
        if (s[^1] != '\n') n++;   //a final unterminated line still counts
        return n;
    }

    //a write inside the project offers the project root, one outside offers only its own directory
    private string WriteGrantOffer(string fullPath)
    {
        var root = Path.GetFullPath(store.ProjectRoot);
        var rootTerm = Terminate(root);
        var pathTerm = Terminate(fullPath);
        if (pathTerm.StartsWith(rootTerm, StringComparison.OrdinalIgnoreCase))
            return root;
        return Path.GetDirectoryName(fullPath) ?? root;
    }

    private static string Terminate(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        if (obj.ValueKind == JsonValueKind.Object &&
            obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
        {
            value = v.GetString()!;
            return true;
        }
        value = "";
        return false;
    }
}
