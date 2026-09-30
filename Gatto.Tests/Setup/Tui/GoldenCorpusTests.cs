using System.Text.Json;
using System.Text.RegularExpressions;

namespace Gatto.Tests.Setup.Tui;

//the corpus is the oracle for the wizard's words. the em dash is checked on the render, since a source census misses one joined at render time
public class GoldenCorpusTests
{
    //the base directory, since EndToEndTests moves the process cwd and a relative path would resolve into its temp directory
    private static string GoldensDir => Path.Combine(AppContext.BaseDirectory, "Setup", "Tui", "Goldens");

    //the screens a later task may render, so adding one is a deliberate act
    private static readonly string[] Expected =
    [
        //s1: 5 screens
        "s1-A100-100.txt",
        "s1-A80-80.txt",
        "s1-B-REPL100-100.txt",
        "s1-B100-100.txt",
        "s1-B80-80.txt",
        //s2: 5 screens
        "s2-strip-states-100.txt",
        "s2-tab-word-100.txt",
        "s2-welcome-100-100.txt",
        "s2-welcome-80-80.txt",
        "s2-welcome-map-installed-100.txt",
        //s3: 10 screens
        "s3-carveout-1-done-100.txt",
        "s3-cpu-only-100.txt",
        "s3-dynamic-83-80-80.txt",
        "s3-dynamic-83-done-100.txt",
        "s3-discrete-8-done-100.txt",
        "s3-floor-bound-8-80-80.txt",
        "s3-floor-bound-8-done-100.txt",
        "s3-unified-96-80-80.txt",
        "s3-unified-96-done-100.txt",
        "s3-unified-96-live-100.txt",
        //s4: 14 screens
        "s4-consent-100.txt",
        "s4-consent-80-80.txt",
        "s4-done-100.txt",
        "s4-fail-crt-100.txt",
        "s4-fail-notclassic-100.txt",
        "s4-fallback-100.txt",
        "s4-fetching-100.txt",
        "s4-fetching-80-80.txt",
        "s4-fetching-armed-100.txt",
        "s4-fetching-pair-100.txt",
        "s4-found-100.txt",
        "s4-found-fork-100.txt",
        "s4-found-newer-100.txt",
        "s4-found-older-100.txt",
        //s5: 23 screens
        "s5-carveout-1-100.txt",
        "s5-discrete-8-100.txt",
        "s5-discrete-8-all-100.txt",
        //a screen that had no golden frame, so the sweep and the width check missed it
        "s5-discrete-8-chip-empty-100.txt",
        "s5-unified-96-80-80.txt",
        "s5-unified-96-armed-100.txt",
        "s5-unified-96-chips-100.txt",
        "s5-unified-96-default-100.txt",
        "s5-unified-96-empty-100.txt",
        "s5-unified-96-full-build-100.txt",
        "s5-unified-96-full-file-100.txt",
        "s5-unified-96-hub-found-100.txt",
        //the Hub outage screen as the flow draws it, since the older mock is kept as the design record
        "s5-unified-96-hub-out-of-reach-100.txt",
        "s5-unified-96-local-all-100.txt",
        "s5-unified-96-local-autoland-100.txt",
        "s5-unified-96-local-empty-100.txt",
        "s5-unified-96-local-gemma-100.txt",
        //the 80-column shelf folds, since names cap at 24 and the pane cannot clear its floor. the folder chip keeps the path tail, leaving the keys row one line
        "s5-unified-96-local-all-80.txt",
        "s5-unified-96-local-pane-100.txt",
        "s5-unified-96-pane-100.txt",
        "s5-unified-96-qwen-100.txt",
        "s5-unified-96-qwen-folded-100.txt",
        "s5-unified-96-search-100.txt",
        //s6: 13 screens
        "s6-consent-100.txt",
        "s6-done-100.txt",
        "s6-dropped-100.txt",
        "s6-dropped-armed-100.txt",
        "s6-fallback-arrived-100.txt",
        "s6-fallback-partial-100.txt",
        "s6-fallback-watch-100.txt",
        "s6-fallback-wrongfile-100.txt",
        "s6-fetching-100.txt",
        "s6-fetching-80-80.txt",
        "s6-fetching-armed-100.txt",
        "s6-fetching-set-100.txt",
        "s6-mismatch-100.txt",
        //s7: 16 screens
        "s7-answers-100.txt",
        "s7-exited-100.txt",
        "s7-failed-100.txt",
        "s7-failed-80-80.txt",
        "s7-failed-dq-100.txt",
        "s7-held-100.txt",
        "s7-incomplete-100.txt",
        "s7-loading-100.txt",
        "s7-offer-100.txt",
        "s7-passed-100.txt",
        "s7-running-100.txt",
        "s7-running-80-80.txt",
        "s7-running-armed-100.txt",
        "s7-still-loading-100.txt",
        "s7-waiting-100.txt",
        "s7-stopped-100.txt",
        //s8: 21 screens
        "s8-answered-no-100.txt",
        "s8-answers-80-80.txt",
        "s8-answers-llama-100.txt",
        "s8-answers-lms-100.txt",
        "s8-context-100.txt",
        "s8-door-100.txt",
        "s8-door-fetch-100.txt",
        "s8-door-none-100.txt",
        "s8-door-standard-100.txt",
        "s8-first-reply-100.txt",
        "s8-model-name-100.txt",
        "s8-model-pick-100.txt",
        "s8-no-chat-404-100.txt",
        "s8-no-chat-405-100.txt",
        "s8-no-stream-100.txt",
        "s8-refused-100.txt",
        "s8-server-80-80.txt",
        "s8-server-llama-100.txt",
        "s8-server-lms-100.txt",
        "s8-server-none-100.txt",
        "s8-server-own-100.txt",
        //s9: 28 screens
        "s9-epilogue-80-80.txt",
        "s9-epilogue-cpu-only-100.txt",
        "s9-epilogue-connect-100.txt",
        "s9-epilogue-declined-100.txt",
        "s9-epilogue-discrete-100.txt",
        "s9-epilogue-installed-100.txt",
        "s9-epilogue-left-100.txt",
        "s9-epilogue-left-80-80.txt",
        "s9-epilogue-noshare-100.txt",
        "s9-epilogue-start-100.txt",
        "s9-install-100.txt",
        //the same screen plus two rows, which makes it cheap to draw and easy to leave out
        "s9-install-legacy-100.txt",
        "s9-install-repair-100.txt",
        "s9-install-repair-older-100.txt",
        "s9-install-update-100.txt",
        "s9-summary-100.txt",
        "s9-summary-80-80.txt",
        "s9-summary-connect-100.txt",
        "s9-summary-connect-beside-local-100.txt",
        "s9-summary-dev-100.txt",
        "s9-summary-elsewhere-100.txt",
        "s9-summary-failed-100.txt",
        "s9-summary-found-100.txt",
        "s9-summary-held-100.txt",
        "s9-summary-skipped-100.txt",
        "s9-summary-stopped-100.txt",
        "s9-summary-stopped-80-80.txt",
        "s9-updates-100.txt",
        //s10: 29 screens. the closing frames' record rows come from GutterWrap, a hand-typed ♯ row can't be evidence about wrapping
        "s10-after-back-100.txt",
        "s10-after-failed-100.txt",
        "s10-after-failed-check-100.txt",
        "s10-after-leave-100.txt",
        "s10-after-loaded-100.txt",
        "s10-after-swap-100.txt",
        "s10-after-unrun-check-100.txt",
        "s10-check-100.txt",
        "s10-check-ask-100.txt",
        "s10-check-ask-80-80.txt",
        "s10-done-checked-100.txt",
        "s10-done-second-quant-100.txt",
        "s10-done-unchecked-100.txt",
        "s10-done-unchecked-80-80.txt",
        "s10-download-100.txt",
        "s10-download-80-80.txt",
        "s10-download-armed-100.txt",
        "s10-hub-failed-100.txt",
        "s10-local-100.txt",
        "s10-local-have-100.txt",
        "s10-local-landing-100.txt",
        "s10-picker-100.txt",
        "s10-shelf-100.txt",
        "s10-shelf-discrete-100.txt",
        "s10-shelf-80-80.txt",
        "s10-shelf-added-100.txt",
        "s10-shelf-loaded-100.txt",
        "s10-shelf-news-100.txt",
        "s10-shelf-two-100.txt",
    ];

    private static IReadOnlyDictionary<string, HashSet<int>> ModelRegions()
    {
        var path = Path.Combine(GoldensDir, "model-regions.json");
        Assert.True(File.Exists(path), $"the model-region sidecar is missing: {path}");
        var raw = JsonSerializer.Deserialize<Dictionary<string, int[]>>(File.ReadAllText(path))!;
        return raw.ToDictionary(kv => kv.Key, kv => new HashSet<int>(kv.Value));
    }

    [Fact]
    public void Every_screen_in_the_census_has_a_golden()
    {
        var present = Directory.GetFiles(GoldensDir, "*.txt").Select(Path.GetFileName).ToHashSet();
        var missing = Expected.Where(e => !present.Contains(e)).ToArray();
        Assert.True(missing.Length == 0, "goldens missing for: " + string.Join(", ", missing));
    }

    //the other direction, a golden nobody declared, since an exporter that invented a screen would otherwise pass
    [Fact]
    public void No_golden_exists_outside_the_census()
    {
        var declared = Expected.ToHashSet();
        var stray = Directory.GetFiles(GoldensDir, "*.txt")
            .Select(Path.GetFileName)
            .Where(f => !declared.Contains(f!))
            .ToArray();
        Assert.True(stray.Length == 0, "goldens not in the census: " + string.Join(", ", stray));
    }

    [Fact]
    public void No_screen_carries_an_em_dash_outside_a_model_authored_line()
    {
        var regions = ModelRegions();
        var offenders = new List<string>();
        foreach (var path in Directory.GetFiles(GoldensDir, "*.txt"))
        {
            var name = Path.GetFileName(path)!;
            var exempt = regions.TryGetValue(name, out var idx) ? idx : [];
            var lines = Lines(path);
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains('—') && !exempt.Contains(i))
                    offenders.Add($"{name}:{i}  {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "em dash on a gatto-authored screen line:\n" + string.Join("\n", offenders));
    }

    //the exemption must stay narrow, so a sidecar that stopped naming real model lines fails here
    [Fact]
    public void The_model_exemption_covers_real_lines_and_only_those()
    {
        var regions = ModelRegions();
        Assert.NotEmpty(regions);

        var exemptWithDash = 0;
        foreach (var (name, idx) in regions)
        {
            var lines = Lines(Path.Combine(GoldensDir, name));
            foreach (var i in idx)
            {
                Assert.InRange(i, 0, lines.Length - 1);
                if (lines[i].Contains('—')) exemptWithDash++;
            }
        }
        //the simulated reply holds a dash, and the exemption would be dead machinery if it stopped
        Assert.True(exemptWithDash > 0,
            "no exempt line carries an em dash: the model region is dead machinery");
    }

    //width is measured with UnicodeWidth, the function the product paints with, so a golden that passes here cannot wrap there
    [Fact]
    public void No_row_exceeds_the_width_its_filename_claims()
    {
        var offenders = new List<string>();
        foreach (var path in Directory.GetFiles(GoldensDir, "*.txt"))
        {
            var name = Path.GetFileName(path)!;
            var width = int.Parse(name[..^4].Split('-')[^1]);
            var lines = Lines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                var cells = Gatto.Terminal.UnicodeWidth.Of(lines[i]);
                if (cells > width) offenders.Add($"{name}:{i} is {cells} cells, claims {width}");
            }
        }
        Assert.True(offenders.Count == 0, string.Join("\n", offenders));
    }

    //strip the CR after splitting, since a checkout that converted the corpus to CRLF would otherwise count it as a cell
    private static string[] Lines(string path) =>
        [.. File.ReadAllText(path).Split('\n').Select(l => l.TrimEnd('\r'))];

    //an empty golden passes every other check here and the exporters write one without a word
    [Fact]
    public void No_golden_is_empty()
    {
        var empty = Directory.GetFiles(GoldensDir, "*.txt")
            .Where(p => File.ReadAllText(p).Trim().Length == 0)
            .Select(p => Path.GetFileName(p)!)
            .ToArray();
        Assert.True(empty.Length == 0, "empty goldens: " + string.Join(", ", empty));
    }

    //no screen may point at .gatto\models, since the weights live under weights and a per-file constant is not a rule
    [Fact]
    public void No_screen_points_at_the_old_models_folder()
    {
        var offenders = new List<string>();
        foreach (var path in Directory.GetFiles(GoldensDir, "*.txt"))
        {
            var lines = Lines(path);
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains(@".gatto\models\"))
                    offenders.Add($"{Path.GetFileName(path)}:{i}  {lines[i].Trim()}");
        }
        Assert.True(offenders.Count == 0,
            "a screen still names the profile folder as where weights live:\n"
            + string.Join("\n", offenders));
    }

    //the section headings hold counts, so the heading is read from this file's own source and checked against its entries
    [Fact]
    public void EVERY_SECTION_HEADING_COUNTS_ITS_OWN_ENTRIES()
    {
        var source = Census.SourceTree.Read(Path.Combine(
            Census.SourceTree.RepoRoot(), "Gatto.Tests", "Setup", "Tui", "GoldenCorpusTests.cs"));
        var body = Body(source);

        var heading = new Regex(@"//\s*(s\d+):\s*(\d+)\s*screens");
        var entry = new Regex(@"""(s\d+)-[^""]+\.txt""");

        var claimed = new Dictionary<string, int>(StringComparer.Ordinal);
        var counted = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in body.Split('\n'))
        {
            var h = heading.Match(line);
            if (h.Success) { claimed[h.Groups[1].Value] = int.Parse(h.Groups[2].Value); continue; }
            var e = entry.Match(line);
            if (e.Success)
                counted[e.Groups[1].Value] = counted.GetValueOrDefault(e.Groups[1].Value) + 1;
        }

        //the matcher runs against a known match and a known miss, since an absence is evidence only once the pattern has matched something real
        Assert.Matches(heading, "        // s3: 6 screens");
        Assert.DoesNotMatch(heading, "        // s3: the six that draw a shelf");
        Assert.Matches(entry, @"        ""s3-ambiguous-100.txt"",");
        Assert.DoesNotMatch(entry, @"        ""model-regions.json"",");

        Assert.Equal(Expected.Length, counted.Values.Sum());
        var wrong = claimed
            .Where(kv => counted.GetValueOrDefault(kv.Key) != kv.Value)
            .Select(kv => $"{kv.Key}: the heading says {kv.Value}, the list holds "
                + counted.GetValueOrDefault(kv.Key))
            .ToArray();
        Assert.True(wrong.Length == 0, string.Join("\n", wrong));

        //every generator has its own heading, since one added without one would be uncounted and the check above cannot see that
        Assert.Equal(
            counted.Keys.OrderBy(k => k, StringComparer.Ordinal),
            claimed.Keys.OrderBy(k => k, StringComparer.Ordinal));
    }

    //the census array's text, from its declaration to its close, so a name in a doc comment elsewhere cannot count as an entry
    private static string Body(string source)
    {
        var at = source.IndexOf("private static readonly string[] Expected =", StringComparison.Ordinal);
        Assert.True(at >= 0, "the census array is no longer declared under that name");
        var close = source.IndexOf("\n    ];", at, StringComparison.Ordinal);
        Assert.True(close > at, "the census array's close was not found");
        return source[at..close];
    }
}
