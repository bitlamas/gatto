using System.Text.Json;
using Gatto.Core.Home;

namespace Gatto.Roles;

//the model-side signal in a role's composition, hand-written for now, and a missing nudges.json is valid
public sealed record Nudges(IReadOnlyList<string> Gates, string? Append, ThinkingLevel? ThinkingCap)
{
    private static readonly string[] Keys = { "gates", "append", "thinking_cap" };

    public static Nudges Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new GattoConfigException("nudges must be a JSON object");

        foreach (var prop in root.EnumerateObject())
            if (!Keys.Contains(prop.Name))
                throw new GattoConfigException($"unknown key in nudges: {prop.Name}");

        var gates = new List<string>();
        if (root.TryGetProperty("gates", out var g))
        {
            if (g.ValueKind != JsonValueKind.Array)
                throw new GattoConfigException("nudges has a \"gates\" with the wrong type — must be an array of strings");
            foreach (var item in g.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new GattoConfigException("nudges has a non-string entry in \"gates\"");
                gates.Add(item.GetString()!);
            }
        }

        string? append = null;
        if (root.TryGetProperty("append", out var a))
        {
            if (a.ValueKind != JsonValueKind.String)
                throw new GattoConfigException("nudges has an \"append\" with the wrong type — must be a string");
            append = a.GetString();
        }

        ThinkingLevel? cap = null;
        if (root.TryGetProperty("thinking_cap", out var tc))
        {
            if (tc.ValueKind != JsonValueKind.String)
                throw new GattoConfigException("nudges has a \"thinking_cap\" with the wrong type — must be a string");
            cap = Thinking.Parse(tc.GetString());
        }

        return new Nudges(gates, append, cap);
    }
}
