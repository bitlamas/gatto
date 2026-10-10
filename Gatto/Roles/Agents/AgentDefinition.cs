namespace Gatto.Roles.Agents;

//one agents/*.md definition: name, one-line description, the built-in tools it may call, a turn budget and its system prompt
public sealed record AgentDefinition(
    string Name,
    string Description,
    IReadOnlyList<string> Tools,
    int MaxTurns,
    string SystemPrompt);

//loads agent definitions from a home and a project folder, a project file overrides a home file of the same name. a malformed file is skipped with a diagnostic
public static class AgentDefinitions
{
    private const int DefaultMaxTurns = 10;
    private const string Delimiter = "---";

    //reads the home folder then the project one, and a project file overrides a home file by name. a bad file is skipped with a diagnostic instead of throwing
    public static IReadOnlyList<AgentDefinition> LoadAll(
        string homeAgentsDir, string projectAgentsDir, ISet<string> builtinToolNames, Action<string> diagnostic)
    {
        var byName = new Dictionary<string, AgentDefinition>(StringComparer.Ordinal);

        foreach (var def in LoadDir(homeAgentsDir, builtinToolNames, diagnostic))
            byName[def.Name] = def;

        foreach (var def in LoadDir(projectAgentsDir, builtinToolNames, diagnostic))
        {
            //the project loop runs last, so a project file wins, and says so since opening a project must not swap an agent unseen
            if (byName.ContainsKey(def.Name))
                diagnostic($"agent '{def.Name}' at {Path.Combine(projectAgentsDir, def.Name + ".md")} overrides the home agent of the same name");
            byName[def.Name] = def;
        }

        return byName.Values.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
    }

    private static IEnumerable<AgentDefinition> LoadDir(
        string dir, ISet<string> builtinToolNames, Action<string> diagnostic)
    {
        if (!Directory.Exists(dir)) yield break;

        foreach (var path in Directory.EnumerateFiles(dir, "*.md", SearchOption.TopDirectoryOnly)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            AgentDefinition? def;
            try
            {
                def = ParseFile(path, name, builtinToolNames, diagnostic);
            }
            catch (Exception ex)
            {
                //a parsing bug must never take the session down, so the file is reported and skipped
                diagnostic($"agent '{name}' at {path} failed to parse: {ex.Message} — skipped");
                def = null;
            }

            if (def is not null) yield return def;
        }
    }

    private static AgentDefinition? ParseFile(
        string path, string name, ISet<string> builtinToolNames, Action<string> diagnostic)
    {
        var text = File.ReadAllText(path).Replace("\r\n", "\n");
        var lines = text.Split('\n');

        if (lines.Length == 0 || lines[0].Trim() != Delimiter)
        {
            diagnostic($"agent '{name}' at {path} has no frontmatter — skipped");
            return null;
        }

        var closeIdx = -1;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].Trim() != Delimiter) continue;
            closeIdx = i;
            break;
        }
        if (closeIdx == -1)
        {
            diagnostic($"agent '{name}' at {path} has no closing frontmatter delimiter — skipped");
            return null;
        }

        string? description = null;
        List<string>? rawTools = null;
        var maxTurns = DefaultMaxTurns;

        for (var i = 1; i < closeIdx; i++)
        {
            var line = lines[i];
            if (line.Trim().Length == 0) continue;

            var colon = line.IndexOf(':');
            if (colon < 0) continue;   //a line with no colon is skipped quietly

            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();

            switch (key)
            {
                case "description":
                    description = value;
                    break;
                case "tools":
                    rawTools = value.Split(',')
                        .Select(t => t.Trim())
                        .Where(t => t.Length > 0)
                        .ToList();
                    break;
                case "max_turns":
                    if (!int.TryParse(value, out maxTurns))
                    {
                        diagnostic($"agent '{name}' at {path} has a non-integer max_turns '{value}' — skipped");
                        return null;
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            diagnostic($"agent '{name}' at {path} is missing 'description' — skipped");
            return null;
        }

        if (rawTools is null || rawTools.Count == 0)
        {
            diagnostic($"agent '{name}' at {path} is missing 'tools' — skipped");
            return null;
        }

        var tools = new List<string>();
        foreach (var tool in rawTools)
        {
            if (tools.Contains(tool))
                //a duplicate tool name would throw inside ToolRegistry, so drop it here at load where a diagnostic belongs
                diagnostic($"agent '{name}' at {path} lists tool '{tool}' more than once — duplicate dropped");
            else if (builtinToolNames.Contains(tool))
                tools.Add(tool);
            else
                diagnostic($"agent '{name}' at {path} references unknown tool '{tool}' — dropped");
        }

        if (tools.Count == 0)
        {
            diagnostic($"agent '{name}' at {path} has no valid tools left after filtering — skipped");
            return null;
        }

        if (maxTurns <= 0)
        {
            diagnostic($"agent '{name}' at {path} has a non-positive max_turns {maxTurns} — skipped");
            return null;
        }

        var systemPrompt = string.Join('\n', lines.Skip(closeIdx + 1)).Trim();
        if (systemPrompt.Length == 0)
        {
            diagnostic($"agent '{name}' at {path} has an empty system prompt — skipped");
            return null;
        }

        return new AgentDefinition(name, description, tools, maxTurns, systemPrompt);
    }
}
