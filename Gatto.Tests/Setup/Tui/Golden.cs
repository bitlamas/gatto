namespace Gatto.Tests.Setup.Tui;

//a golden failure names the row that differs. the width sits in the name and is asserted, since another width fails at the fold
internal static class Golden
{
    public static string Dir
    {
        get
        {
            var d = new DirectoryInfo(AppContext.BaseDirectory);
            var local = Path.Combine(d.FullName, "Setup", "Tui", "Goldens");
            if (Directory.Exists(local)) return local;
            while (d is not null && !File.Exists(Path.Combine(d.FullName, "Gatto.sln"))) d = d.Parent;
            Assert.True(d is not null, "could not find the repo root from " + AppContext.BaseDirectory);
            return Path.Combine(d!.FullName, "Gatto.Tests", "Setup", "Tui", "Goldens");
        }
    }

    //the corpus in the source tree, which a bless writes and the build copies
    private static string SourceDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "Gatto.sln"))) d = d.Parent;
        Assert.True(d is not null, "could not find the repo root from " + AppContext.BaseDirectory);
        return Path.Combine(d!.FullName, "Gatto.Tests", "Setup", "Tui", "Goldens");
    }

    //one screen by generator, name and width, so a caller cannot ask for a width the corpus does not hold
    public static IReadOnlyList<string> Load(string generator, string screen, int width)
    {
        var path = Path.Combine(Dir, $"{generator}-{screen}-{width}.txt");
        Assert.True(File.Exists(path),
            $"no golden for {generator}/{screen} at {width}: expected {Path.GetFileName(path)}. "
            + "A screen with no golden cannot be rendered by a task - add it to the corpus first.");
        return Gatto.Tests.Census.SourceTree.Read(path).TrimEnd('\n').Split('\n');
    }

    //the panel rows, found between the first rule and the last, since hard-coded indices would make an oracle from the answer it checks
    public static IReadOnlyList<string> Panel(string generator, string screen, int width)
    {
        var rows = Load(generator, screen, width);
        var rules = rows.Select((r, i) => (r, i))
            .Where(t => t.r.TrimStart().StartsWith('─'))
            .Select(t => t.i)
            .ToList();
        Assert.True(rules.Count >= 2,
            $"{generator}-{screen}-{width} has {rules.Count} rule lines; the panel cannot be located");
        return [.. rows.Skip(rules[0] + 1).Take(rules[^1] - rules[0] - 1)];
    }

    //the wizard's body, the title row through the row above the keys, anchored on the strip since the corpus mocks open with a transcript
    public static IReadOnlyList<string> Body(IReadOnlyList<string> rows)
    {
        var (start, count) = BodySpan(rows);
        return [.. rows.Skip(start).Take(count)];
    }

    //the body checked against a drawn mock. a bless rewrites the body inside the mock, keeping its transcript, and fails as AssertEquals does
    public static void AssertBody(string file, IReadOnlyList<string> rendered)
    {
        if (Environment.GetEnvironmentVariable("GATTO_BLESS_GOLDENS") == "1")
        {
            var written = Path.Combine(SourceDir(), file);
            var lines = Gatto.Tests.Census.SourceTree.Read(written).TrimEnd('\n').Split('\n');
            var (start, count) = BodySpan(lines);
            File.WriteAllText(written, string.Join("\n", [.. lines.Take(start), .. Body(rendered), .. lines.Skip(start + count)]) + "\n");
            Assert.Fail($"GATTO_BLESS_GOLDENS wrote {written}, so read its diff in git and run again without the variable");
        }
        Assert.Equal(Body(File.ReadAllLines(Path.Combine(Dir, file))), Body(rendered));
    }

    //where the body sits: the row after the title's blank, up to the keys row
    private static (int Start, int Count) BodySpan(IReadOnlyList<string> rows)
    {
        //the strip is the one row with a section cursor and the separators, so the count is exactly one
        var strips = rows.Select((r, i) => (r: r.Trim(), i))
            .Where(t => t.r.Contains(" · ", StringComparison.Ordinal)
                        && t.r.Contains('❯', StringComparison.Ordinal)
                        && t.r.EndsWith("done", StringComparison.Ordinal))
            .Select(t => t.i)
            .ToList();
        Assert.True(strips.Count == 1,
            $"the frame has {strips.Count} strip rows, so the body cannot be located:\n"
            + string.Join("\n", rows));

        var keys = Array.FindLastIndex([.. rows], r => r.Contains("Esc ", StringComparison.Ordinal));
        var title = strips[0] + 2;
        Assert.True(keys > title,
            $"the frame has no keys row under its title:\n{string.Join("\n", rows)}");
        return (title, keys - title);
    }

    //null when the rows match, otherwise the first row that differs with both values shown
    public static string? Diff(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        for (var i = 0; i < Math.Max(expected.Count, actual.Count); i++)
        {
            var e = i < expected.Count ? expected[i] : "<no such row>";
            var a = i < actual.Count ? actual[i] : "<no such row>";
            if (!string.Equals(e, a, StringComparison.Ordinal))
                return $"row {i} differs:\n  expected: {e}\n  actual  : {a}";
        }
        return null;
    }

    //asserts the render matches and that it was made at the golden's own width, since a narrow render could match by coincidence
    public static void AssertEquals(string generator, string screen, int width, IReadOnlyList<string> actual)
    {
        foreach (var row in actual)
            Assert.True(Gatto.Terminal.UnicodeWidth.Of(row) <= width,
                $"{generator}/{screen}: a rendered row is {Gatto.Terminal.UnicodeWidth.Of(row)} cells "
                + $"but the golden is {width} wide - this render was made at the wrong width");

        //a bless run writes the render over the golden and fails, so a bless is never green and a stray variable shows at once
        if (Environment.GetEnvironmentVariable("GATTO_BLESS_GOLDENS") == "1")
        {
            var written = Path.Combine(SourceDir(), $"{generator}-{screen}-{width}.txt");
            File.WriteAllText(written, string.Join("\n", actual) + "\n");
            Assert.Fail($"GATTO_BLESS_GOLDENS wrote {written}, so read its diff in git and run again without the variable");
        }

        var diff = Diff(Load(generator, screen, width), actual);
        Assert.True(diff is null, $"{generator}-{screen}-{width}.txt\n{diff}");
    }
}
