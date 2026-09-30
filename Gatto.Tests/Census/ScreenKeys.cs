using System.Reflection;
using System.Text.RegularExpressions;
using Gatto.Cli.Setup;

namespace Gatto.Tests.Census;

//one home for screen-key extraction, so two censuses can't disagree about which screens exist, and the pattern's own checks live beside it
internal static class ScreenKeys
{
    //matches every WizardScreen construction and captures the key first. the \s* spans newlines, so it sees a site that puts the key on its own line
    public static readonly Regex Construction = new(
        @"new WizardScreen\.(?:Choice|Info|Ask|Progress|Terminal)\s*\(\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.Compiled);

    //the narration form. the match can't cross a semicolon, so it stays inside one statement and a following call isn't swept in
    public static readonly Regex Narration = new(
        @"_emitted\.(?:Add|Insert)\([^;]*?new WizardScreen\.(?:Choice|Info|Ask|Progress|Terminal)\s*\(\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_]*))",
        RegexOptions.Compiled);

    //this channel reaches a face, and the runner drains it before each screen. a guard that asserts emptiness uses it to show the reader sees a real entry
    public static readonly Regex NarrationChannel = new(
        @"_narrate\.Add\(new WizardScreen\.Info\(\s*(?:""([^""]+)""|([A-Za-z_][A-Za-z0-9_.]*))",
        RegexOptions.Compiled);

    //returns the construction count per key for Gatto/Cli, with comments stripped
    public static IReadOnlyDictionary<string, int> Extract(Regex pattern)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var file in SourceTree.ProductionFilesUnder("Cli"))
            Count(SourceTree.WithoutComments(File.ReadAllText(file)), pattern, counts);

        return counts;
    }

    //counts one block of text through the same code as Extract, and stays public so the pattern checks run that path too
    public static IReadOnlyDictionary<string, int> ExtractFrom(string text, Regex pattern)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        Count(text, pattern, counts);
        return counts;
    }

    private static void Count(string text, Regex pattern, Dictionary<string, int> counts)
    {
        foreach (Match m in pattern.Matches(text))
        {
            var key = m.Groups[1].Success ? m.Groups[1].Value : Resolve(m.Groups[2].Value);
            if (key is null) continue;
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
    }

    //resolve an identifier to the SetupFlow constant it names, null when it names no such constant. a new const-keyed screen enters every census on its own
    public static string? Resolve(string identifier) =>
        typeof(SetupFlow)
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)
            .FirstOrDefault(f => f.IsLiteral && f.FieldType == typeof(string) && f.Name == identifier)
            ?.GetRawConstantValue() as string;
}

//the extraction's positives and negative live beside it in this file. a census must not inherit a pattern nobody proved
public class ScreenKeysTests
{
    //one positive per spelling call sites use. a pattern that saw only the literal form would self-certify against half the tree.
    [Fact]
    public void THE_PATTERN_MATCHES_A_PLANTED_CONSTRUCTION_IN_BOTH_SPELLINGS()
    {
        var literal = ScreenKeys.ExtractFrom(
            "return Emit(new WizardScreen.Choice(\n    \"planted.key\",\n    \"a question?\",", ScreenKeys.Construction);
        Assert.Equal(1, literal.GetValueOrDefault("planted.key"));

        //the const spelling resolves through reflection, so it proves Resolve works. a rename drops every const-keyed screen from every census in silence
        var byConst = ScreenKeys.ExtractFrom(
            "return Emit(new WizardScreen.Choice(\n    FoundKey,\n    \"a question?\",", ScreenKeys.Construction);
        Assert.Equal(1, byConst.GetValueOrDefault(SetupFlow.FoundKey));
    }

    //the negative case against ordinary text. without it the positives only prove the pattern matches something, and a loose pattern turns every key set into noise
    [Fact]
    public void THE_PATTERN_DOES_NOT_FIRE_ON_ORDINARY_TEXT()
    {
        Assert.Empty(ScreenKeys.ExtractFrom(
            "// the welcome screen is a WizardScreen and its key is \"welcome\", said in prose\n"
            + "var name = \"new WizardScreen\";", ScreenKeys.Construction));
    }
}
