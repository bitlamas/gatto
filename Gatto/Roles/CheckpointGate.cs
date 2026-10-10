using System.Text.Json;
using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;

namespace Gatto.Roles;

//every git commit the model tries pauses for the user before the permission gate, and it can never be granted for always
public sealed class CheckpointGate(
    IPermissionPrompter? prompter, Func<string, string> runReadOnly, CheckpointApproval? approval = null,
    WildState? wild = null)
{
    //on by default, /auto turns it off for the session and the --auto flag starts it off
    public bool Enabled { get; set; } = true;

    public Task CheckAsync(HookPayload payload)
    {
        if (!Enabled) return Task.CompletedTask;   //with checkpointing off the pause is skipped, the permission gate still runs after
        if (wild?.On == true) return Task.CompletedTask;   //wild mode covers the checkpoint too

        var call = payload.Call;
        if (call is null || call.Name != "shell") return Task.CompletedTask;
        if (!TryGetCommand(call.ArgumentsJson, out var command)) return Task.CompletedTask;

        //match the raw command, a chained git commit has to pause too, and the checkpoint has no prefix grant to defend against forging
        if (!IsCommitInvocation(command))
            return Task.CompletedTask;

        //gather the status even when git is unreachable, a failure here must not skip the pause and the error message goes into the summary
        string statusOutput;
        try { statusOutput = runReadOnly("git status --short"); }
        catch (Exception ex) { statusOutput = $"(git status unavailable: {ex.Message})"; }

        //armed with no terminal to ask through fails closed, --yes governs the permission gate only and must not stand in for --auto
        if (prompter is null)
            throw new InvalidOperationException(
                "checkpoint armed and no interactive terminal — pass --auto, and --yes or a standing grant for the command, to run commits unattended");

        var request = new PermissionRequest("checkpoint", command + "\n" + statusOutput, GrantOffer: null);
        //deny and cancel both refuse, a withdrawn question grants nothing the way a refusal does
        if (prompter.Ask(request) is PermissionAnswer.Deny or PermissionAnswer.Cancel)
            throw new InvalidOperationException("checkpoint declined — commit not run");

        //hand the approved call to the permission gate through the single-use latch, so the same shell command is not asked about twice
        if (approval is not null) approval.Call = call;
        return Task.CompletedTask;   //an approving answer passes, the permission gate still runs after
    }

    //liberal and fail-toward-pause, a token naming git followed by one starting with commit, since a spurious pause costs one keypress
    private static bool IsCommitInvocation(string command)
    {
        var tokens = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < tokens.Length; i++)
        {
            var gitToken = tokens[i].Trim('"', '\'');
            if (!string.Equals(Path.GetFileNameWithoutExtension(gitToken), "git", StringComparison.OrdinalIgnoreCase))
                continue;
            for (var j = i + 1; j < tokens.Length; j++)
                if (tokens[j].Trim('"', '\'').StartsWith("commit", StringComparison.OrdinalIgnoreCase))
                    return true;
        }
        return false;
    }

    private static bool TryGetCommand(string argumentsJson, out string command)
    {
        command = "";
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson.Length > 0 ? argumentsJson : "{}");
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("command", out var v) && v.ValueKind == JsonValueKind.String)
            {
                command = v.GetString()!;
                return true;
            }
        }
        catch (JsonException) { } //malformed arguments are not a commit match
        return false;
    }
}
