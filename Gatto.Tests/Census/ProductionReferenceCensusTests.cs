using System.Text.RegularExpressions;

namespace Gatto.Tests.Census;

//every production file must be referenced from another file or pinned with a reason. a reference isn't reachability, and only top-level types count
public class ProductionReferenceCensusTests
{
    //the unit is the file, var hides type names so a type-level rule would pin mostly-alive types. a file is dead when no other live file names its top-level types
    private static readonly Dictionary<string, string> KnownHoles = new(StringComparer.Ordinal)
    {
        //a pin is discharged by real wiring. a census answered by a call that exists only for the census asks nothing


        //a pin's own guess about which commit frees it is often wrong. the census is what actually knows.

        //a file whose type names collide with live production names is invisible to this census, so a dead file can pass

        //before deleting a dead file, re-run the consumer census, its tests die with it. a test of a surviving rule is re-pointed at the live surface

        ["Registers.cs"] =
            "⬥ the wizard's ink rule and its one register for verdict rows, complete and guarded by "
            + "RegistersTests, but its consumers are the DONE step's verdict rows and the shelf's "
            + "marks, and neither exists yet. "
            + "Pinned rather than given a contrived caller: wiring it into the face today would mean "
            + "inventing a use for it, and a caller that exists to satisfy a census is worse than a pin "
            + "that says why. "
            + "REMOVED IN THE SAME COMMIT AS the DONE step's verdict rows; the census goes red then and demands "
            + "its own removal, which is how this instrument is meant to work.",

        //when deleting a pin, move its open hole into the doc of the member that owns it. a pin must name its destination
    };

    [Fact]
    public void EVERY_PRODUCTION_FILE_IS_REACHED_OR_PINNED()
    {
        var census = Run();

        //a sweep that parsed nothing would report no dead files and pass. these floors exist only to make an empty sweep loud.
        Assert.True(census.Files.Count >= 100,
            $"census swept only {census.Files.Count} production files — it is reading the wrong tree");
        Assert.True(census.Declared.Count >= 150,
            $"census found only {census.Declared.Count} top-level types — the declaration pattern is broken");

        var unpinned = census.DeadFiles
            .Where(f => !KnownHoles.ContainsKey(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(unpinned.Count == 0,
            $"swept {census.Files.Count} files / {census.Declared.Count} top-level types.\n"
            + "No top-level type in these files is referenced by any other production file — the\n"
            + "unit is unwired. Wire it, delete it, or pin it in KnownHoles with a reason:\n  "
            + string.Join("\n  ", unpinned.Select(f =>
                $"{f}  (declares: {string.Join(", ", census.TypesIn(f))})")));
    }

    //a pin that outlives its hole turns a known-holes list into a suppression list. this guard fails when a pinned file gains its wiring.
    [Fact]
    public void NO_PINNED_HOLE_HAS_QUIETLY_BEEN_WIRED()
    {
        var census = Run();
        var present = census.Files.Select(Path.GetFileName).ToHashSet(StringComparer.Ordinal);

        var healed = KnownHoles.Keys
            .Where(f => present.Contains(f) && !census.DeadFiles.Contains(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(healed.Count == 0,
            "these files are pinned as unwired but are now REACHED — remove the pin in the same "
            + "commit that wired them:\n  " + string.Join("\n  ", healed));

        var vanished = KnownHoles.Keys
            .Where(f => !present.Contains(f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        Assert.True(vanished.Count == 0,
            "these files are pinned but no longer exist — delete the pin with the file:\n  "
            + string.Join("\n  ", vanished));
    }

    private sealed record Census(
        IReadOnlyList<string> Files,
        IReadOnlyDictionary<string, string> Declared,
        IReadOnlySet<string> Unreferenced,
        IReadOnlySet<string> DeadFiles)
    {
        //the failure message names the types so a reader can judge the unit without opening the file.
        public IEnumerable<string> TypesIn(string fileName) =>
            Declared.Where(d => d.Value == fileName).Select(d => d.Key).OrderBy(t => t, StringComparer.Ordinal);
    }

    //the pattern is anchored at column zero so nested types stay out of scope, and the record forms still give up their name
    private static readonly Regex Declaration = new(
        @"^(?:(?:public|internal|private|protected|sealed|static|abstract|partial|file|readonly|ref|unsafe|new)\s+)*"
        + @"(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+"
        + @"([A-Za-z_][A-Za-z0-9_]*)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex Identifier = new(@"[A-Za-z_][A-Za-z0-9_]*", RegexOptions.Compiled);

    private static Census Run()
    {
        var files = SourceTree.ProductionFiles();
        var code = files.ToDictionary(f => f, f => SourceTree.CodeOnly(File.ReadAllText(f)), StringComparer.Ordinal);

        //a partial type declares in several files, so each name maps to a set of files.
        var declaredIn = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var f in files)
            foreach (Match m in Declaration.Matches(code[f]))
            {
                var name = m.Groups[1].Value;
                if (!declaredIn.TryGetValue(name, out var set))
                    declaredIn[name] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(f);
            }

        var identifiers = files.ToDictionary(
            f => f,
            f => (IReadOnlySet<string>)Identifier.Matches(code[f]).Select(m => m.Value).ToHashSet(StringComparer.Ordinal),
            StringComparer.Ordinal);

        //an orphan cluster vouches for itself, so one pass reads it as live. each round retires files whose every declared type is unreferenced
        var live = new HashSet<string>(files, StringComparer.Ordinal);
        var deadFiles = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> unreferenced;

        while (true)
        {
            unreferenced = declaredIn.Keys
                .Where(name => !live.Any(f =>
                    !declaredIn[name].Contains(f) && identifiers[f].Contains(name)))
                .ToHashSet(StringComparer.Ordinal);

            var dead = files
                .Where(f => live.Contains(f))
                .Where(f => declaredIn.Any(d => d.Value.Contains(f))
                         && declaredIn.Where(d => d.Value.Contains(f)).All(d => unreferenced.Contains(d.Key)))
                .ToList();

            if (dead.Count == 0) break;
            foreach (var f in dead) { live.Remove(f); deadFiles.Add(Path.GetFileName(f)); }
        }

        return new Census(
            files,
            declaredIn.ToDictionary(d => d.Key, d => Path.GetFileName(d.Value.First()), StringComparer.Ordinal),
            unreferenced,
            deadFiles);
    }
}
