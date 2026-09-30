using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//a raw Console.Write bypasses the command layer that keeps pipes byte-pure. the count is pinned, so a new write fails and a broken reader fails too
public class RawConsoleWriteCensusTests
{
    //the report omits the enclosing method on purpose. attributing a site to a member needs an approximate parser, and an approximate label misleads the reader
    private sealed record Site(string File, int Line, string Text);

    //allowlist entries key on a stable fragment of the line. each entry is checked to still match a real site, so stale entries fail loudly instead of accumulating
    private sealed record Scripted(string Marker, string Why);

    //these paths write raw by contract, their output is machine-readable. the serve status --json path writes through an injected TextWriter, so it isn't here
    private static readonly Scripted[] ScriptedPaths =
    [
        new("args.Command == \"help\"",
            "the usage banner is plain text by contract — it is also written to stderr on any usage "
            + "error, where chrome would corrupt a redirect"),
        new("CommandBanner.VersionLine(",
            "internal-tools/release-gate.ps1 regex-checks this line; a themed version string would break "
            + "the release gate, which is the one consumer that must never see a glyph. re-keyed "
            + "from the `args.Command` test when the version branch took the corpus stamp: the "
            + "marker keys on the line that holds the write, and the write moved"),
        new("OnTextDelta",
            "the -p one-shot path streams the model's own text to stdout. This is the byte-pure "
            + "surface: a single injected glyph would corrupt every script "
            + "that pipes gatto, so it must stay a raw write and no migration may move it"),
    ];

    //lower the pin in the same commit that migrates writes, an old floor hides the next one. an injected TextWriter isn't seen here, only a live check sees it
    private const int RawWritesToday = 8;

    //only stdout is in scope. stderr holds diagnostics and is plain by contract, so one known notice there is invisible here
    private static readonly Regex RawWrite = new(@"\bConsole\s*\.\s*(Out\s*\.\s*)?Write(Line)?\s*\(", RegexOptions.Compiled);

    //each stdout spelling is tried here (a pattern that misses one spelling reads that spelling's writes as zero)
    [Theory]
    [InlineData("Console.Write(\"x\");")]
    [InlineData("Console.WriteLine();")]
    [InlineData("Console.Out.Write(\"x\");")]
    [InlineData("Console.Out.WriteLine(line);")]
    [InlineData("System.Console . Out . WriteLine (line);")]
    public void THE_PATTERN_SEES_EVERY_STDOUT_SPELLING(string line) => Assert.Matches(RawWrite, line);

    [Theory]
    [InlineData("Console.Error.Write(\"x\");")]
    [InlineData("Console.Error.WriteLine(line);")]
    [InlineData("Console.OutputEncoding = utf8;")]
    [InlineData("writer.WriteLine(line);")]
    public void THE_PATTERN_LEAVES_STDERR_AND_OTHER_WRITERS_OUT(string line) => Assert.DoesNotMatch(RawWrite, line);

    [Fact]
    public void THE_COMMAND_SURFACE_CARRIES_NO_RAW_WRITE_BEYOND_THE_PINNED_SET()
    {
        var sites = Census();

        Assert.True(sites.Count == RawWritesToday,
            $"the command surface has {sites.Count} raw stdout writes; the pin says {RawWritesToday}.\n\n"
            + (sites.Count > RawWritesToday
                ? "A NEW ONE WAS ADDED. That is what this instrument exists to catch: output that "
                  + "bypasses the designed layer cannot be made to degrade to plain, because nothing "
                  + "it passes through knows the terminal is a pipe. Route it through the command "
                  + "layer's painter instead — or, if it genuinely belongs on a scripted path, add it "
                  + "to ScriptedPaths WITH a reason and raise the pin in the same commit.\n\n"
                : "WRITES WERE MIGRATED — good. Lower the pin to the new count in THIS commit, so the "
                  + "next addition is caught against the new floor rather than hidden under the old "
                  + "one.\n\n")
            + string.Join("\n", sites.Select(s => $"  {s.File}:{s.Line}  {s.Text}")));
    }

    [Fact]
    public void EVERY_ALLOWLISTED_SCRIPTED_PATH_STILL_EXISTS()
    {
        //an allowlist entry that matches nothing is a fossil. the list must keep describing real code
        var sites = Census();

        var missing = ScriptedPaths
            .Where(p => !sites.Any(s => s.Text.Contains(p.Marker, StringComparison.Ordinal)))
            .Select(p => $"  \"{p.Marker}\" — {p.Why}")
            .ToList();

        Assert.True(missing.Count == 0,
            "these allowlisted scripted paths no longer match any raw write in the command surface:\n"
            + string.Join("\n", missing)
            + "\n\nEither the path moved (re-key the marker) or it stopped writing raw (delete the "
            + "entry). Leaving it is an exemption for something that does not exist.");
    }

    private static List<Site> Census()
    {
        var root = SourceTree.RepoRoot();
        var sites = new List<Site>();

        //the scope is Cli and the whole terminal library. the -p path never builds a REPL, but a painter primitive in the library is reachable from both paths
        foreach (var file in SourceTree.ProductionFilesUnder("Cli").Concat(SourceTree.ProductionFilesIn("Gatto.Terminal")))
        {
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            //detection reads CodeOnly, so a call named in a comment isn't a finding. the report reads the raw line, since CodeOnly blanks the strings the markers quote
            var raw = SourceTree.Read(file).Split('\n');
            var code = SourceTree.CodeOnly(SourceTree.Read(file)).Split('\n');

            Assert.True(code.Length == raw.Length,
                $"{rel}: the code-only view has {code.Length} lines and the source has {raw.Length} — "
                + "the two views no longer index the same lines, so every reported location would be "
                + "wrong. Fix SourceTree.CodeOnly's newline handling before trusting this census.");

            for (var i = 0; i < code.Length; i++)
                if (RawWrite.IsMatch(code[i]))
                    sites.Add(new Site(rel, i + 1, raw[i].Trim()));
        }

        return sites;
    }
}
