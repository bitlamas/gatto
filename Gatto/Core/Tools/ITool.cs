using System.Text.Json;

namespace Gatto.Core.Tools;

public interface ITool
{
    string Name { get; }
    string Description { get; }
    JsonElement ParametersSchema { get; }
    Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct);

    //a gate-owned tool reports false while its gate is disarmed, so the registry stays fixed and only what it offers changes
    bool IsAvailable => true;
}
