//scan every string literal under Gatto/ for the retired word, and fail hard when there's no source. fixtures may hold the word, production code may not
using System.Text.RegularExpressions;

namespace Gatto.Tests;

public class PackWordSweepTests
{
    //holds a literal that keeps the retired word and why it may. an exemption with no reason is where the sweep stops being complete
    private sealed record Exemption(string RelativeFile, string Fragment, string Why);

    //the exemption list is empty because nothing in production says the retired word, and adding a row is a deliberate decision
    private static readonly Exemption[] Exemptions = Array.Empty<Exemption>();

    //match the retired word whole, with _ read as a separator rather than a word character, so pack_id matches. ordinary words like package must not match
    private static readonly Regex RetiredWord =
        new("(?<![A-Za-z0-9])packs?(?![A-Za-z0-9])", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    //strip interpolation holes before matching, so identifiers can never inflate the count. a census must not move with variable names
    private static readonly Regex Hole = new(@"\{[^{}]*\}", RegexOptions.Compiled);

    [Fact]
    public void NO_STRING_LITERAL_UNDER_GATTO_STILL_SAYS_THE_RETIRED_WORD()
    {
        var root = Gatto.Tests.Census.SourceTree.RepoRoot();
        var files = Gatto.Tests.Census.SourceTree.ProductionFiles();

        var scanned = 0;
        var offenders = new List<string>();

        foreach (var file in files)
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            foreach (var literal in Gatto.Tests.Census.SourceTree.StringLiterals(File.ReadAllText(file)))
            {
                scanned++;
                if (!RetiredWord.IsMatch(Hole.Replace(literal, " "))) continue;
                if (Exemptions.Any(e =>
                        string.Equals(e.RelativeFile, rel, StringComparison.OrdinalIgnoreCase)
                        && literal.Contains(e.Fragment, StringComparison.Ordinal)))
                    continue;
                offenders.Add($"  {rel}: \"{Trim(literal)}\"");
            }
        }

        //a guard that finds nothing is a green light wired to nothing, so this fails when the scan isn't looking at the codebase
        Assert.True(files.Count >= 40,
            $"only {files.Count} .cs files found under {root} — the census is not looking at the codebase.");
        //set the floor close to the measured corpus, so dropping a whole string form fails here. generated build output is not counted
        Assert.True(scanned >= 3000,
            $"only {scanned} string literals extracted from {files.Count} files — the literal "
            + "extractor has stopped recognising C#'s string forms, so this guard is vacuous. Fix it.");

        Assert.True(offenders.Count == 0,
            $"{offenders.Count} string literal(s) under Gatto/ still say the retired word pack:\n"
            + string.Join("\n", offenders.Take(200))
            + (offenders.Count > 200 ? $"\n  ... and {offenders.Count - 200} more" : "")
            + "\n\nEvery user-facing sentence is READ, not translated. If a literal legitimately "
            + "keeps the word, add it to Exemptions WITH a reason.");
    }

    private static string Trim(string s) =>
        s.Length <= 90 ? s.Replace("\n", "\\n") : s[..90].Replace("\n", "\\n") + "...";

}
