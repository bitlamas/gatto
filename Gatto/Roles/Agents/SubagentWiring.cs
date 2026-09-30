using Gatto.Core.Loop;
using Gatto.Core.Loop.Permissions;
using Gatto.Core.Tools;

namespace Gatto.Roles.Agents;

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

    //builds the wiring closure RunAgentTool takes, the catalog defaults to the six built-ins and a test can pass its own
    public static Func<AgentDefinition, (ToolRegistry Tools, HookBus Hooks)> Build(
        PermissionGate gate, IReadOnlyDictionary<string, Func<ITool>>? catalog = null,
        Action<string>? diag = null)
    {
        var builtins = catalog ?? Builtins();
        //route hook errors through the diag sink, a raw stderr write mid-turn corrupts the alt-screen frame
        var warn = diag ?? (m => Console.Error.WriteLine($"! {m}"));
        return def =>
        {
            var subTools = new ToolRegistry();
            foreach (var name in def.Tools)
                if (builtins.TryGetValue(name, out var make)) subTools.Register(make());
            var subHooks = new HookBus();
            subHooks.OnHandlerError += (evt, ex) => warn($"hook error ({evt}): {ex.Message}");
            subHooks.On(HookEvent.ToolCall, async payload =>
            {
                gate.AgentLabel = def.Name;
                try { await gate.CheckAsync(payload); }
                finally { gate.AgentLabel = null; }
            });
            return (subTools, subHooks);
        };
    }
}
