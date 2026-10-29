using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Repl.Commands;

//the help panel's rows, pure in their arguments so a test reads every tab with no loop
internal static class HelpPanel
{
    internal const string WildGloss = "wild mode on or off";
    internal const string EmptyLine = "no extensions in the home";
    internal const string OnNextLaunch = "on at the next launch";
    internal const string ServesEndpoint = "serves this session's endpoint";
    private const int ColumnGap = 2;

    //a command row is the name and args as the table prints them, its gloss, and a registered row's file dim after it
    public static List<SelectOption> Commands(IReadOnlyList<SlashCommands.Cmd> known) =>
        [.. known.Select(c => new SelectOption(SlashCommands.Left(c), Description: c.Summary, Key: c.Name,
            Trail: c.Source is { } file ? [new SelectRun(file, Theme.Dim)] : null))];

    //the keys and their glosses, the wild toggle fourth since a user reaches for it before a key they discover. a key row is never wrapped
    public static List<DetailRow> Shortcuts(GlyphSet g)
    {
        List<(string Keys, string What)> rows = [.. global::Gatto.Repl.Shortcuts.AllOf(g).Select(s => (s.Keys, s.What))];
        rows.Insert(Math.Min(3, rows.Count), (WildKeys.KeyName, WildGloss));
        var col = rows.Max(r => UnicodeWidth.Of(r.Keys)) + ColumnGap;
        return [.. rows.Select(r => new DetailRow(r.Keys + new string(' ', col - UnicodeWidth.Of(r.Keys)) + r.What))];
    }

    //one row per source in discovery order: on wears the tick, off and failed a word in their own ink, the names follow. no tick leaves its cells blank, so a toggle moves no column
    public static List<SelectOption> Extensions(IReadOnlyList<ExtensionView> views, GlyphSet g) =>
        [.. views.Select(v => new SelectOption(v.Name, Key: v.Name,
            Description: Names(v, g) is { Length: > 0 } names ? names : null,
            Tail: v.On && v.Loaded ? [new SelectRun(" "), new SelectRun(g.Ok, Theme.Ok)]
                : [new SelectRun(" "), new SelectRun(new string(' ', UnicodeWidth.Of(g.Ok)))],
            Mark: !v.On ? new SelectRun("off", Theme.Dim) : Failed(v) ? new SelectRun("failed", Theme.Warn) : null))];

    //a source that is on and did not load failed, unless it was off at launch and is only waiting for the next one
    public static bool Failed(ExtensionView v) => v.On && !v.Loaded && !v.OffAtLaunch;

    //the fact under the cursor row: why it failed, that on waits for the next launch, or that it carries the session
    public static string? Sentence(ExtensionView v) =>
        Failed(v) ? v.Error
        : v.On && v.OffAtLaunch ? OnNextLaunch
        : v.ServesEndpoint ? ServesEndpoint
        : null;

    //each group only when it holds a name, its word singular or plural by the count, the dot between groups
    private static string Names(ExtensionView v, GlyphSet g)
    {
        var groups = new List<string>();
        void Group(string one, IReadOnlyList<string> names)
        {
            if (names.Count > 0) groups.Add((names.Count == 1 ? one : one + "s") + " " + string.Join(", ", names));
        }
        Group("tool", v.Tools);
        Group("command", v.Commands);
        Group("endpoint", v.Endpoints);
        return string.Join($" {g.Dot} ", groups);
    }
}
