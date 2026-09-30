using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//one validator serves comments and config descriptions, two validators are two rules and they drift, and the judgment stays at authoring time
internal static class PublicProseStyle
{
    public static readonly (string Name, Regex Pattern)[] Violations =
    [
        ("an em dash", new Regex("—", RegexOptions.Compiled)),
        ("a capitalised opening word", new Regex(@"^\s*[A-Z]", RegexOptions.Compiled)),
        //match the full date shape (a bare 20\d\d reads a context size or port as a date). narrow it only against a real comment that trips it, with no exception list
        ("a date", new Regex(@"\b20\d\d-\d\d-\d\d\b", RegexOptions.Compiled)),
    ];

    //returns the names of the rules this text breaks, and an empty list when it is clean.
    public static IReadOnlyList<string> Check(string text) =>
        Violations.Where(v => v.Pattern.IsMatch(text)).Select(v => v.Name).ToList();
}
