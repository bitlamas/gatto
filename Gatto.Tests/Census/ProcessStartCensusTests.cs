using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//a child process that outlives its bound holds a port or a pipe after gatto moves on. a start is allowed only in a file that kills the child's tree
public class ProcessStartCensusTests
{
    private sealed record Site(string File, int Line, string Text);

    //the static start, and the construction that precedes an instance start, since text cannot tell which Start() belongs to a process
    private static readonly Regex Start = new(
        @"\bProcess\s*\.\s*Start\s*\(|\bnew\s+(System\s*\.\s*Diagnostics\s*\.\s*)?Process\s*[({]|\bProcess\??\s+\w+\s*=\s*new\s*\(",
        RegexOptions.Compiled);

    //the kill that takes the child's own children with it
    private static readonly Regex TreeKill = new(@"\.\s*Kill\s*\(\s*(entireProcessTree\s*:\s*)?true\s*\)", RegexOptions.Compiled);

    //a pin is a ceiling. lower it in the commit that removes a start, and raise it only after reading the new start at the code.
    private static readonly Dictionary<string, int> Pins = new(StringComparer.Ordinal)
    {
        ["Gatto/Cli/GattoApp.cs"] = 2,
        ["Gatto/Core/Hardware/HardwareProbe.cs"] = 1,
        ["Gatto/Core/Tools/LlamaServerProbe.cs"] = 1,
        ["Gatto/Core/Tools/ShellTool.cs"] = 1,
        ["Gatto/Roles/ServeManager.cs"] = 2,
    };

    private sealed record Exempt(string File, string Marker, string Why);

    //a start named here is a decision. it counts against its file's pin but needs no bound of its own.
    private static readonly Exempt[] Exempts =
    [
        new("Gatto/Cli/GattoApp.cs", "System.Diagnostics.Process.Start(psi);",
            "uninstall's self-delete helper waits for gatto to exit and then removes the install folder, so it must outlive gatto"),
    ];

    private static List<(string Rel, string Source)> ProductionTree() =>
        [.. SourceTree.ProductionFiles().Select(f =>
            (Path.GetRelativePath(SourceTree.RepoRoot(), f).Replace('\\', '/'), SourceTree.Read(f)))];

    //the one matcher, shared by the census and the fixtures written for it
    private static (List<Site> Starts, int TreeKills) Scan(string rel, string source)
    {
        var raw = source.Split('\n');
        var code = SourceTree.CodeOnly(source);
        Assert.True(code.Split('\n').Length == raw.Length,
            $"{rel}: the code view and the source differ in line count, so every reported line would be wrong");

        var starts = Start.Matches(code)
            .Select(m => 1 + code.AsSpan(0, m.Index).Count('\n'))
            .Select(line => new Site(rel, line, raw[line - 1].Trim()))
            .ToList();
        return (starts, TreeKill.Matches(code).Count);
    }

    private static bool IsExempt(Site s) =>
        Exempts.Any(e => e.File == s.File && s.Text.Contains(e.Marker, StringComparison.Ordinal));

    private static List<string> Offences(IEnumerable<(string Rel, string Source)> tree)
    {
        var offences = new List<string>();
        foreach (var (rel, source) in tree)
        {
            var (starts, treeKills) = Scan(rel, source);
            if (starts.Count == 0) continue;

            var pin = Pins.GetValueOrDefault(rel);
            if (starts.Count > pin)
                offences.Add($"{rel} is pinned at {pin} process starts and holds {starts.Count}:\n"
                    + string.Join("\n", starts.Select(s => $"    {s.Line}  {s.Text}")));
            if (treeKills == 0 && starts.Any(s => !IsExempt(s)))
                offences.Add($"{rel} starts a process and holds no tree kill:\n"
                    + string.Join("\n", starts.Where(s => !IsExempt(s)).Select(s => $"    {s.Line}  {s.Text}")));
        }
        return offences;
    }

    [Fact]
    public void EVERY_PROCESS_START_SITS_IN_A_PINNED_FILE_THAT_HOLDS_A_TREE_KILL()
    {
        var tree = ProductionTree();

        //a reader that found no files, or a matcher that found no starts, would pass every pin
        Assert.True(tree.Count > 200, $"the census read only {tree.Count} source files, so the reader is broken");
        Assert.True(tree.Sum(f => Scan(f.Rel, f.Source).Starts.Count) >= Pins.Count,
            "the census found fewer starts than it has seam files, so the matcher is broken");

        var offences = Offences(tree);
        Assert.True(offences.Count == 0,
            "a process start outside its pin, or without a tree kill beside it:\n"
            + string.Join("\n", offences)
            + "\nbound the child and kill its tree on expiry, or start it inside a seam that does. raise a pin only after reading the new start at the code.");
    }

    //an entry that matches nothing is a permission for code that no longer exists
    [Fact]
    public void EVERY_PIN_AND_EVERY_EXEMPT_START_STILL_MATCHES_THE_TREE()
    {
        var starts = ProductionTree().SelectMany(f => Scan(f.Rel, f.Source).Starts).ToList();

        var stale = Pins.Keys.Where(f => !starts.Any(s => s.File == f)).Select(f => $"pin {f}")
            .Concat(Exempts.Where(e => starts.Count(s => s.File == e.File && s.Text.Contains(e.Marker, StringComparison.Ordinal)) != 1)
                .Select(e => $"exempt start `{e.Marker}` in {e.File}, which must match exactly one start"))
            .ToList();
        Assert.True(stale.Count == 0,
            "these entries no longer match the tree. delete them, or re-key them if the code moved:\n  "
            + string.Join("\n  ", stale));
    }

    [Fact]
    public void EACH_SPELLING_MATCHES_ITS_PLANTED_POSITIVE_AND_NOT_ITS_NEGATIVE()
    {
        string[] starts =
        [
            "using var proc = Process.Start(psi)!;",
            "System.Diagnostics.Process.Start(psi);",
            "using var p = new System.Diagnostics.Process { StartInfo = info };",
            "using var p = new Process();",
            "Process p = new() { StartInfo = info };",
        ];
        foreach (var s in starts) Assert.Single(Scan("Plant.cs", s).Starts);

        const string notStarts = "var psi = new ProcessStartInfo(\"x\");\nvar me = Process.GetCurrentProcess();\n"
            + "proc.WaitForExit(1000);\n// Process.Start(psi)\nvar s = \"new Process()\";\n";
        Assert.Empty(Scan("Plant.cs", notStarts).Starts);

        Assert.Equal(2, Scan("Plant.cs", "proc.Kill(entireProcessTree: true);\np.Kill(true);").TreeKills);
        Assert.Equal(0, Scan("Plant.cs", "proc.Kill();\np.Kill(entireProcessTree: false);\n// p.Kill(entireProcessTree: true);").TreeKills);
    }

    [Fact]
    public void A_START_MOVED_OUT_OF_THE_SEAMS_OR_LEFT_WITHOUT_A_TREE_KILL_GOES_RED()
    {
        var tree = ProductionTree();
        Assert.Empty(Offences(tree));

        const string started = "\ninternal static class Started { internal static void Go(System.Diagnostics.ProcessStartInfo psi) => System.Diagnostics.Process.Start(psi)?.Dispose(); }\n";
        var outside = tree.Select(f => f.Rel == "Gatto/Cli/ServeLines.cs" ? (f.Rel, f.Source + started) : f).ToList();
        Assert.Contains(Offences(outside), o => o.StartsWith("Gatto/Cli/ServeLines.cs is pinned at 0 process starts and holds 1:", StringComparison.Ordinal));

        var overPin = tree.Select(f => f.Rel == "Gatto/Core/Tools/ShellTool.cs" ? (f.Rel, f.Source + started) : f).ToList();
        Assert.Contains(Offences(overPin), o => o.StartsWith("Gatto/Core/Tools/ShellTool.cs is pinned at 1 process starts and holds 2:", StringComparison.Ordinal));

        var unbounded = tree.Select(f => f.Rel == "Gatto/Core/Tools/ShellTool.cs"
            ? (f.Rel, f.Source.Replace("Kill(entireProcessTree: true)", "Kill()", StringComparison.Ordinal)) : f).ToList();
        Assert.Contains(Offences(unbounded), o => o.StartsWith("Gatto/Core/Tools/ShellTool.cs starts a process and holds no tree kill:", StringComparison.Ordinal));

        const string exemptShape = "\ninternal static class Helper { internal static void Go(System.Diagnostics.ProcessStartInfo psi) { System.Diagnostics.Process.Start(psi); } }\n";
        var exemptElsewhere = tree.Select(f => f.Rel == "Gatto/Cli/ServeLines.cs" ? (f.Rel, f.Source + exemptShape) : f).ToList();
        Assert.Contains(Offences(exemptElsewhere), o => o.StartsWith("Gatto/Cli/ServeLines.cs starts a process and holds no tree kill:", StringComparison.Ordinal));
    }
}
