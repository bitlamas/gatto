using System.Text.Json;

namespace Gatto.Core.Tools;

//the names a tool's schema declares, and the refusal of a call that uses any other, so a misnamed argument fails loudly instead of being ignored
public static class ToolArgumentNames
{
    //null when every name is declared, or when the schema declares no properties to check against, as a loose extension schema may
    public static string? Refusal(string toolName, JsonElement schema, JsonElement args)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("properties", out var props)
            || props.ValueKind != JsonValueKind.Object || args.ValueKind != JsonValueKind.Object)
            return null;
        //a schema that accepts other names, by true or by a schema for them, says so itself, and only false or its absence closes the list
        if (schema.TryGetProperty("additionalProperties", out var more) && more.ValueKind is JsonValueKind.True or JsonValueKind.Object)
            return null;
        var declared = props.EnumerateObject().Select(p => p.Name).ToList();
        var unknown = args.EnumerateObject().Select(p => p.Name).Where(n => !declared.Contains(n, StringComparer.Ordinal)).ToList();
        if (unknown.Count == 0) return null;
        var which = unknown.Count == 1 ? "unknown argument: " : "unknown arguments: ";
        var takes = declared.Count == 0 ? "no arguments" : string.Join(", ", declared);
        return $"{which}{string.Join(", ", unknown)}. {toolName} takes {takes}";
    }
}
