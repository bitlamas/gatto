using System.Reflection;
using Gatto.Cli.Setup;

namespace Gatto.Tests.Setup;

//adding a wizard screen without a test naming it fails the build. the audit checks that a key is named, it cannot check the copy.
public class CopyGuardAuditTests
{
    //gather both kinds of screen key separately, seeing one kind makes an audit blind to the other. a const field name and its literal both name the key.
    private sealed record ScreenKey(string Value, IReadOnlyList<string> Named);

    private static IEnumerable<ScreenKey> DeclaredKeys() =>
        typeof(SetupFlow)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name.EndsWith("Key", StringComparison.Ordinal))
            .Select(f => new ScreenKey((string)f.GetRawConstantValue()!,
                [$"\"{(string)f.GetRawConstantValue()!}\"", $"SetupFlow.{f.Name}"]));

    //reflection can't see a string typed at a constructor, so literal keys are listed here. a companion test greps the real source, so the list can't fall behind.
    private static readonly string[] LiteralKeys =
    [
        //the list holds exactly the literal keys of the Info, Terminal and Progress screens the CLI builds. a removed key leaves it, order does not matter.
        "back.refused", "consent.ready", "default.ready", "left", "llama.ok", "model.none",
        //the narration for a quant added to an existing model, said before the question about using it.
        "model.quantadded",
        "model.reused", "move.incomplete", "segment.install", "step.first",
        "step.model", "write.failed", "writes.pending"
    ];

    [Fact]
    public void EVERY_WIZARD_SCREEN_IS_NAMED_BY_A_TEST()
    {
        var tests = TestSources();

        var all = DeclaredKeys()
            .Concat(LiteralKeys.Select(k => new ScreenKey(k, [$"\"{k}\""])));

        var unmapped = all
            .Where(k => !k.Named.Any(n => tests.Any(src => src.Contains(n, StringComparison.Ordinal))))
            .Select(k => k.Value)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.True(unmapped.Count == 0,
            "screens no test names, add the guard, or delete the screen:\n  " + string.Join("\n  ", unmapped));
    }

    //a hand-written list drifts from the code, so this test reads the real construction sites.
    [Fact]
    public void EVERY_LITERAL_KEYED_SCREEN_IN_THE_SOURCE_IS_LISTED_HERE()
    {
        var pattern = new System.Text.RegularExpressions.Regex(
            @"new WizardScreen\.(?:Info|Terminal|Progress)\(""([^""]+)""");

        var inSource = Gatto.Tests.Census.SourceTree.ProductionFilesUnder("Cli")
            .SelectMany(f => pattern.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Distinct()
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(inSource, LiteralKeys.OrderBy(k => k, StringComparer.Ordinal).ToList());
    }

    //excluding this file from the corpus is load-bearing, its own list names every literal key, so a check satisfied by its own evidence can't fail.
    private static IReadOnlyList<string> TestSources() =>
        [.. Directory
            .EnumerateFiles(Path.Combine(RepoRoot(), "Gatto.Tests"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).Equals("CopyGuardAuditTests.cs", StringComparison.Ordinal))
            .Select(File.ReadAllText)];

    //the repo-root read has one home in SourceTree, so the audit and the censuses can't disagree about which tree they read.
    private static string RepoRoot() => Gatto.Tests.Census.SourceTree.RepoRoot();
}
