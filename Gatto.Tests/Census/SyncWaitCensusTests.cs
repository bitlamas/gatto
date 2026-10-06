using System.Text.RegularExpressions;
using Gatto.Core.Loop;
using Gatto.Roles;

namespace Gatto.Tests.Census;

//a blocking wait on a task can deadlock the thread it runs on. waits live only in the seam files pinned here, so a wait anywhere else goes red and gets read.
public class SyncWaitCensusTests
{
    private sealed record Site(string File, int Line, string Form, string Text);

    //the four blocking forms, matched over the whole code view so a call split across lines still counts
    private static readonly (string Form, Regex Pattern)[] Forms =
    [
        ("GetAwaiter().GetResult()", new(@"\.\s*GetAwaiter\s*\(\s*\)\s*\.\s*GetResult\s*\(\s*\)", RegexOptions.Compiled)),
        ("Task.WaitAll", new(@"\bWait(All|Any)\s*\(", RegexOptions.Compiled)),
        (".Wait(", new(@"\.\s*Wait\s*\(", RegexOptions.Compiled)),
        (".Result", new(@"\.\s*Result\b", RegexOptions.Compiled)),
    ];

    //a pin is a ceiling. lower it in the commit that removes a wait, and raise it only after reading the new wait at the code.
    private static readonly Dictionary<string, int> Pins = new(StringComparer.Ordinal)
    {
        ["Gatto/Cli/GattoApp.cs"] = 11,
        ["Gatto/Cli/Setup/LiveSetupProbes.cs"] = 20,
        ["Gatto/Cli/Setup/SetupFlow.cs"] = 11,
        ["Gatto/Cli/Setup/WriteSetApply.cs"] = 2,
        ["Gatto/Core/Hardware/HardwareProbe.cs"] = 3,
        ["Gatto/Core/Tools/LlamaServerProbe.cs"] = 3,
        ["Gatto/Extensions/ExtensionHost.cs"] = 1,
        ["Gatto/Repl/Repl.cs"] = 1,
    };

    //text can't see the receiver's type, so each .Result read here names its record and its own ceiling
    private sealed record PropertyRead(string File, string Receiver, Type Record, int Ceiling);

    private static readonly PropertyRead[] PropertyReads =
    [
        new("Gatto/Cli/ServeLines.cs", "outcome", typeof(ServeManager.StopOutcome), 2),
        new("Gatto/Core/Loop/HookBus.cs", "p", typeof(HookPayload), 2),
        new("Gatto/Roles/CitationGate.cs", "payload", typeof(HookPayload), 1),
        new("Gatto/Roles/GroundingGate.cs", "payload", typeof(HookPayload), 1),
        new("Gatto/Roles/ServeManager.cs", "Stop(listener)", typeof(ServeManager.StopOutcome), 1),
    ];

    private static List<(string Rel, string Source)> ProductionTree() =>
        [.. SourceTree.ProductionFiles().Select(f =>
            (Path.GetRelativePath(SourceTree.RepoRoot(), f).Replace('\\', '/'), SourceTree.Read(f)))];

    //one matcher for every caller, so the checks with known matches exercise the code the census runs
    private static (List<Site> Waits, List<(PropertyRead Read, Site Site)> Reads) Scan(string rel, string source)
    {
        var raw = source.Split('\n');
        var code = SourceTree.CodeOnly(source);
        Assert.True(code.Split('\n').Length == raw.Length,
            $"{rel}: the code view and the source differ in line count, so every reported line would be wrong");

        var waits = new List<Site>();
        var reads = new List<(PropertyRead, Site)>();
        foreach (var (form, pattern) in Forms)
            foreach (Match m in pattern.Matches(code))
            {
                var line = 1 + code.AsSpan(0, m.Index).Count('\n');
                var site = new Site(rel, line, form, raw[line - 1].Trim());
                var read = form == ".Result" ? PropertyReadAt(rel, code, m.Index) : null;
                if (read is null) waits.Add(site);
                else reads.Add((read, site));
            }
        return (waits, reads);
    }

    private static PropertyRead? PropertyReadAt(string rel, string code, int dot)
    {
        var before = code[..dot].TrimEnd();
        return PropertyReads.FirstOrDefault(r => r.File == rel
            && before.EndsWith(r.Receiver, StringComparison.Ordinal)
            && (before.Length == r.Receiver.Length
                || !(char.IsLetterOrDigit(before[^(r.Receiver.Length + 1)]) || before[^(r.Receiver.Length + 1)] is '_' or '.')));
    }

    private static List<string> Offences(IEnumerable<(string Rel, string Source)> tree)
    {
        var offences = new List<string>();
        var waits = new List<Site>();
        var reads = new List<(PropertyRead Read, Site Site)>();
        foreach (var (rel, source) in tree)
        {
            var scan = Scan(rel, source);
            waits.AddRange(scan.Waits);
            reads.AddRange(scan.Reads);
        }

        foreach (var file in waits.GroupBy(s => s.File))
        {
            var pin = Pins.GetValueOrDefault(file.Key);
            if (file.Count() > pin)
                offences.Add($"{file.Key} is pinned at {pin} blocking waits and holds {file.Count()}:\n"
                    + string.Join("\n", file.Select(s => $"    {s.Line} {s.Form}  {s.Text}")));
        }

        foreach (var read in reads.GroupBy(r => r.Read))
            if (read.Count() > read.Key.Ceiling)
                offences.Add($"{read.Key.File} is pinned at {read.Key.Ceiling} reads of `{read.Key.Receiver}.Result` and holds {read.Count()}:\n"
                    + string.Join("\n", read.Select(r => $"    {r.Site.Line}  {r.Site.Text}")));
        return offences;
    }

    private static bool IsAwaitable(Type t) => t.GetMethod("GetAwaiter", Type.EmptyTypes) is not null;

    [Fact]
    public void NO_FILE_HOLDS_MORE_BLOCKING_WAITS_THAN_ITS_PIN()
    {
        var tree = ProductionTree();

        //a reader that found no files, or a matcher that found no waits, would pass every pin
        Assert.True(tree.Count > 200, $"the census read only {tree.Count} source files, so the reader is broken");
        Assert.True(tree.Sum(f => Scan(f.Rel, f.Source).Waits.Count) >= Pins.Count,
            "the census found fewer waits than it has seam files, so the matcher is broken");

        var offences = Offences(tree);
        Assert.True(offences.Count == 0,
            "a blocking wait outside its pin. await it, or move it into a seam that already bounds its wait:\n"
            + string.Join("\n", offences)
            + "\nraise a pin or add a file only after reading the wait at the code. never raise one to make this pass.");
    }

    //an entry that matches nothing is a permission for code that no longer exists
    [Fact]
    public void EVERY_PIN_AND_EVERY_RESULT_EXCLUSION_STILL_MATCHES_THE_TREE()
    {
        var scans = ProductionTree().Select(f => Scan(f.Rel, f.Source)).ToList();
        var waits = scans.SelectMany(s => s.Waits).ToList();
        var reads = scans.SelectMany(s => s.Reads).ToList();

        var stale = Pins.Keys.Where(f => !waits.Any(w => w.File == f)).Select(f => $"pin {f}")
            .Concat(PropertyReads.Where(r => !reads.Any(x => x.Read == r)).Select(r => $"exclusion `{r.Receiver}.Result` in {r.File}"))
            .ToList();
        Assert.True(stale.Count == 0,
            "these entries no longer match the tree. delete them, or re-key them if the code moved:\n  "
            + string.Join("\n  ", stale));
    }

    //an excluded read is safe only while its record's Result is a plain value, since an awaitable one hides a real wait
    [Fact]
    public void EVERY_RESULT_EXCLUSION_NAMES_A_RECORD_WHOSE_RESULT_IS_NOT_AWAITABLE()
    {
        Assert.True(IsAwaitable(typeof(Task<int>)), "the awaitable check cannot see a task, so it would pass every record");

        foreach (var read in PropertyReads)
        {
            var result = read.Record.GetProperty("Result");
            Assert.True(result is not null,
                $"{read.Record.Name} has no `Result` property, so the exclusion for `{read.Receiver}.Result` in {read.File} names the wrong record");
            Assert.False(IsAwaitable(read.Record) || IsAwaitable(result!.PropertyType),
                $"{read.Record.Name}.Result is awaitable, so `{read.Receiver}.Result` in {read.File} is a blocking wait. remove the exclusion");
        }
    }

    [Fact]
    public void EACH_FORM_MATCHES_ITS_PLANTED_POSITIVE_AND_NOT_ITS_NEGATIVE()
    {
        (string Form, string Positive, string Negative)[] plants =
        [
            ("GetAwaiter().GetResult()",
                "var r = task\n    .GetAwaiter()\n    .GetResult();",
                "var r = await GetResultAsync(); // not task.GetAwaiter().GetResult()"),
            ("Task.WaitAll",
                "Task.WaitAll(new Task[] { a, b }, 2000);",
                "await Task.WhenAll(a, b); var s = \"Task.WaitAll(a)\";"),
            (".Wait(",
                "task.Wait(TimeSpan.FromSeconds(1));",
                "proc.WaitForExit(1000); await gate.WaitAsync(ct);"),
            (".Result",
                "var s = so.Result.Trim();",
                "var r = ToolResult.Ok(\"so.Result\"); var n = page.Results;"),
        ];

        foreach (var (form, positive, negative) in plants)
        {
            Assert.Equal(form, Assert.Single(Scan("Plant.cs", positive).Waits).Form);
            Assert.Empty(Scan("Plant.cs", negative).Waits);
        }
    }

    [Fact]
    public void A_WAIT_MOVED_OUT_OF_THE_SEAM_FILES_GOES_RED()
    {
        var tree = ProductionTree();
        Assert.Empty(Offences(tree));

        const string moved = "\ninternal static class Moved { internal static void Wait(Task t) => t.GetAwaiter().GetResult(); }\n";
        var outside = tree.Select(f => f.Rel == "Gatto/Cli/ServeLines.cs" ? (f.Rel, f.Source + moved) : f).ToList();
        Assert.Contains(Offences(outside), o => o.StartsWith("Gatto/Cli/ServeLines.cs is pinned at 0 blocking waits and holds 1:", StringComparison.Ordinal));

        var overPin = tree.Select(f => f.Rel == "Gatto/Cli/GattoApp.cs" ? (f.Rel, f.Source + moved) : f).ToList();
        Assert.Contains(Offences(overPin), o => o.StartsWith("Gatto/Cli/GattoApp.cs is pinned at 11 blocking waits and holds 12:", StringComparison.Ordinal));

        const string extraRead = "\ninternal static class Extra { internal static object? Read(HookPayload payload) => payload.Result; }\n";
        var overCeiling = tree.Select(f => f.Rel == "Gatto/Roles/CitationGate.cs" ? (f.Rel, f.Source + extraRead) : f).ToList();
        Assert.Contains(Offences(overCeiling), o => o.StartsWith("Gatto/Roles/CitationGate.cs is pinned at 1 reads of `payload.Result` and holds 2:", StringComparison.Ordinal));

        const string taskRead = "\ninternal static class TaskRead { internal static int Read(Task<int> task) => task.Result; }\n";
        var unnamed = tree.Select(f => f.Rel == "Gatto/Roles/CitationGate.cs" ? (f.Rel, f.Source + taskRead) : f).ToList();
        Assert.Contains(Offences(unnamed), o => o.StartsWith("Gatto/Roles/CitationGate.cs is pinned at 0 blocking waits and holds 1:", StringComparison.Ordinal));
    }
}
