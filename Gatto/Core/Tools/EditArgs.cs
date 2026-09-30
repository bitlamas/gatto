using System.Text.Json;

namespace Gatto.Core.Tools;

//the edit's two strings and the write's content read from a call's arguments, for display only, nulls when the arguments do not hold them
public static class EditArgs
{
    public static (string? Old, string? New, string? Content) Of(string toolName, string argsJson)
    {
        if (toolName is not ("edit_file" or "write_file")) return (null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(argsJson.Length > 0 ? argsJson : "{}");
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (null, null, null);
            string? Str(string key) => doc.RootElement.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            return toolName == "edit_file" ? (Str("old_string"), Str("new_string"), null) : (null, null, Str("content"));
        }
        catch (JsonException) { return (null, null, null); }
    }
}
