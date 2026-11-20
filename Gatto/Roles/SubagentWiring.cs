using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;

namespace Gatto.Roles;

//what the calling seat holds, which a child never exceeds. tools null means the seat has no list, and write_dir null no folder
public sealed record SeatBound(IReadOnlyList<string>? Tools, string? WriteDir = null);

//the one place the depth-1 rule is enforced, Builtins holds the six core tools so run_agent and ask_user are absent from every sub-agent registry
public static class SubagentWiring
{
    //the six core built-ins a sub-agent may get, built fresh on every call, with no run_agent or ask_user in the list
    public static Dictionary<string, Func<ITool>> Builtins() => new(StringComparer.Ordinal)
    {
        ["read_file"] = () => new ReadFileTool(),
        ["write_file"] = () => new WriteFileTool(),
        ["edit_file"] = () => new EditFileTool(),
        ["glob"] = () => new GlobTool(),
        ["grep"] = () => new GrepTool(),
        ["shell"] = () => new ShellTool(),
    };

    //builds the wiring closure RunAgentTool takes, the catalog defaults to the six built-ins and a test can pass its own. bound is read at each call, so a child follows the last /role. cwd is the folder a write_dir is read under
    public static Func<RoleFile, (ToolRegistry Tools, HookBus Hooks)> Build(
        PermissionGate gate, string cwd, IReadOnlyDictionary<string, Func<ITool>>? catalog = null,
        Action<string>? diag = null, Func<SeatBound>? bound = null)
    {
        var builtins = catalog ?? Builtins();
        //route hook errors through the diag sink, a raw stderr write mid-turn corrupts the alt-screen frame
        var warn = diag ?? (m => Console.Error.WriteLine($"! {m}"));
        return role =>
        {
            var held = bound?.Invoke();
            var seat = held?.Tools;
            //the caller's folder and the called role's own both hold, so a path must be inside each
            string[] folders = [.. new[] { held?.WriteDir, role.WriteDir }.OfType<string>().Distinct(StringComparer.Ordinal)];
            var subTools = new ToolRegistry();
            //a name outside the catalog is dropped without a word, so no child holds run_agent even when its role lists it
            foreach (var name in role.Tools ?? builtins.Keys)
                if ((seat is null || seat.Contains(name)) && builtins.TryGetValue(name, out var make)) subTools.Register(make());
            var subHooks = new HookBus();
            subHooks.OnHandlerError += (evt, ex) => warn($"hook error ({evt}): {ex.Message}");
            subHooks.On(HookEvent.ToolCall, async payload =>
            {
                //ahead of the gate, as the main seat's is, so a write outside is refused before anyone is asked
                if (payload.Call is { } call && RoleWriteLimit.Refusal(cwd, folders, call, RoleWriteLimit.OnDisk) is { } refusal)
                    throw new InvalidOperationException(refusal);
                gate.AgentLabel = role.Name;
                try { await gate.CheckAsync(payload); }
                finally { gate.AgentLabel = null; }
            });
            return (subTools, subHooks);
        };
    }
}
