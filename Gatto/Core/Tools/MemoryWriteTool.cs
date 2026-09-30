using System.Text.Json;
using Gatto.Core.Memory;

namespace Gatto.Core.Tools;

//the writer for the project memory: one fact per slug, no append, size-capped, and the index budget is read at call time
public sealed class MemoryWriteTool(Func<int> indexBudgetTokens) : ITool
{
    public string Name => "memory_write";

    //the gated wording, copied verbatim, don't reword it without a gate
    public string Description =>
        "Save, revise, or retire one durable project fact in persistent memory (.gatto\\memory\\), " +
        "listed from the next session or /new — not this one. One fact per slug; a fact's first line " +
        "is auto-listed in every future session's Memory block, so revising means writing the same " +
        "slug again, never adding a duplicate. Args: slug (lowercase-hyphen name; reuse an existing " +
        "slug to revise that fact), content (line 1: the fact as one \"- \" bullet; optional detail " +
        "lines below), mode (\"write\" = create or overwrite the fact's file; \"delete\" = retire it, " +
        "content must be empty).";

    //content is not required, a delete call may omit it, and a write without one still fails in the body
    public JsonElement ParametersSchema => ToolArgs.Schema("""
        {"type":"object","properties":{"slug":{"type":"string"},"content":{"type":"string"},"mode":{"type":"string","enum":["write","delete"]}},"required":["slug","mode"]}
        """);

    public Task<ToolResult> ExecuteAsync(JsonElement args, IToolContext ctx, CancellationToken ct)
    {
        var slug = ToolArgs.RequiredString(args, "slug");

        //read mode by hand so missing and invalid share one message
        var mode = OptionalString(args, "mode");
        if (mode != "write" && mode != "delete")
            throw new ArgumentException("mode must be \"write\" or \"delete\"");

        var root = MemoryDir.FindProjectRoot(ctx.Cwd);
        //validate before any filesystem work, a traversal slug throws here and never touches disk
        var path = MemoryDir.PathFor(root, slug);
        var fileName = Path.GetFileName(path);

        if (mode == "delete")
        {
            var stray = OptionalString(args, "content");
            if (!string.IsNullOrEmpty(stray))
                throw new ArgumentException(
                    "content must be empty when mode is \"delete\" — a delete retires the whole fact");

            MemoryDir.Delete(root, slug);
            return Task.FromResult(new ToolResult($"deleted {fileName}"));
        }

        var content = ToolArgs.RequiredString(args, "content");
        MemoryDir.Write(root, slug, content);

        //every write gets the note, the memory block is rebuilt only at reload boundaries, so the prompt does not change mid-turn
        return Task.FromResult(new ToolResult(
            $"saved {fileName} — listed from the next session or /new" + BudgetAdvisory(root)));
    }

    //an advisory that never blocks a write, and it measures the uncapped index, the capped one could not report over 100%
    private string BudgetAdvisory(string root)
    {
        var budget = indexBudgetTokens();
        if (budget <= 0) return "";   //a misconfigured non-positive budget must never divide by zero

        var chars = MemoryIndex.ComposeUncapped(root).Length;
        var pct = 100 * (chars / 4) / budget;
        return pct >= 80 ? $"\nmemory index at {pct}% of budget — prune or shorten facts" : "";
    }

    private static string? OptionalString(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
