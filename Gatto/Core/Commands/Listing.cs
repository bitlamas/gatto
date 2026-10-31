namespace Gatto.Core.Commands;

//one row of a listing: the cursor cell's word, the cells after it in header order, and the fact said under the list while the cursor is on it
public sealed record ListingRow(string Label, IReadOnlyList<string>? Columns = null, string? Sentence = null);

//a list a command answers with in place of text. the host titles it with the command's name, draws it as a panel and calls Pick with the chosen row's index
public sealed record Listing
{
    public string Unit { get; }   //the count's noun, so the host makes 1 snapshot or 10 snapshots
    public string Verb { get; }   //the word after Enter on the key row
    public IReadOnlyList<ListingRow> Rows { get; }
    public Func<int, Task<string>> Pick { get; }   //the text it returns is what the transcript says
    public IReadOnlyList<string>? Header { get; }   //one word per cell, the label's first, null draws no header

    //every check runs here, so a listing that would draw a silent empty or a header that lies never reaches the host
    public Listing(string unit, string verb, IReadOnlyList<ListingRow> rows, Func<int, Task<string>> pick, IReadOnlyList<string>? header = null)
    {
        if (string.IsNullOrWhiteSpace(unit) || string.IsNullOrWhiteSpace(verb) || pick is null)
            throw new InvalidOperationException("a listing needs a unit, a verb and a pick");
        if (rows is null || rows.Count == 0)
            throw new InvalidOperationException("a listing needs a row; answer text for an empty list");
        if (rows.Any(r => r is null || r.Label is null || r.Columns?.Any(c => c is null) == true))
            throw new InvalidOperationException("a listing row needs a label, and no cell may be null");
        var widest = rows.Max(r => r.Columns?.Count ?? 0);
        if (header is not null && header.Any(w => w is null))
            throw new InvalidOperationException("a listing header word must not be null");
        if (header is not null && header.Count != widest + 1)
            throw new InvalidOperationException(
                $"a listing header names every cell, the label first: {Plural.Of(header.Count, "word")} for {Plural.Of(widest, "column")}");
        Unit = unit;
        Verb = verb;
        Rows = [.. rows];   //a copy, so the caller's list can't change under the count the check read
        Pick = pick;
        Header = header is null ? null : [.. header];
    }
}

//what a command handler answers: text said as rows, or a listing drawn as a panel. a string or a listing converts, so a handler returns either
public abstract record CommandAnswer
{
    //a closed hierarchy of exactly these two, so a caller can switch without an unreachable arm
    private protected CommandAnswer() { }

    public sealed record Text(string Value) : CommandAnswer;

    public sealed record List(Listing Value) : CommandAnswer;

    public static implicit operator CommandAnswer(string text) => new Text(text);

    public static implicit operator CommandAnswer(Listing listing) => new List(listing);
}
