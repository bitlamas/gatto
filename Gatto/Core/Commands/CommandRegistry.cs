namespace Gatto.Core.Commands;

//the one shape rule for a slash command, shared by the gate that refuses a mistyped one and the registration an extension makes
public static class CommandNames
{
    //a slash and ASCII letters in any case, so a typed /Undo is still a command and gets the unknown-command line
    public static bool IsShaped(string token)
    {
        if (token.Length < 2 || token[0] != '/') return false;
        for (var i = 1; i < token.Length; i++)
            if (!char.IsAsciiLetter(token[i])) return false;
        return true;
    }

    //a registered name is shaped and lowercase, since the dispatch matches names by ordinal comparison
    public static bool IsRegistrable(string name)
    {
        if (!IsShaped(name)) return false;
        for (var i = 1; i < name.Length; i++)
            if (!char.IsAsciiLetter(name[i]) || !char.IsLower(name[i])) return false;
        return true;
    }
}

//one registered slash command: the name as typed, the args text and summary for the help table, the handler, and the file that registered it
public sealed record CommandRow(
    string Name, string Args, string Summary,
    Func<string, CancellationToken, Task<CommandAnswer>> Handler, string SourceFile, string? Origin = null)   //origin is the source name the off flag reads
{
    //a row whose handler answers text alone
    public static CommandRow OfText(string name, string args, string summary,
        Func<string, CancellationToken, Task<string>> handler, string sourceFile, string? origin = null) =>
        new(name, args, summary, TextHandler(handler), sourceFile, origin);

    //a text handler as an answer handler, its null text read as empty so the dispatch never meets a null
    public static Func<string, CancellationToken, Task<CommandAnswer>> TextHandler(Func<string, CancellationToken, Task<string>> handler) =>
        async (arg, ct) => await handler(arg, ct) ?? "";
}

//the rows extensions registered, filled at commit in file order and read by the dispatch. no static state, so parallel tests never share one
public sealed class CommandRegistry(Gatto.Core.Tools.ArmedSources? armed = null)
{
    private readonly List<CommandRow> _rows = new();
    private readonly Dictionary<string, CommandRow> _byName = new(StringComparer.Ordinal);

    //the rows of the sources that are on, which the dispatch and the help list read
    public IReadOnlyList<CommandRow> Rows => [.. _rows.Where(On)];

    //every row, an off source's included, for a view that names what a source holds
    public IReadOnlyList<CommandRow> RowsAll => _rows;

    public bool TryGet(string name, out CommandRow row) => _byName.TryGetValue(name, out row!) && On(row);

    private bool On(CommandRow row) => armed is null || armed.IsOn(row.Origin);

    //the collision check is the committer's, this throws only when it was skipped
    public void Add(CommandRow row)
    {
        if (_byName.ContainsKey(row.Name))
            throw new InvalidOperationException($"command {row.Name} is already registered by {_byName[row.Name].SourceFile}");
        _byName[row.Name] = row;
        _rows.Add(row);
    }
}
