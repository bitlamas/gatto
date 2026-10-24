using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Gatto.Core.Tools;

namespace Gatto.Tests.Census;

//every tool name the model sees is lowercase snake_case, read from the built-in types and from the shipped scripts' registrations
public class ToolNameCensusTests
{
    private static readonly Regex Snake = new(@"\A[a-z][a-z0-9_]*\z", RegexOptions.Compiled);   //a dollar sign also matches before a final newline
    private static readonly Regex Registration = new(@"Gatto\.Register\(\s*""([^""]*)""", RegexOptions.Compiled);

    //the extension wrapper takes its name from a script, so the scripts are read for it instead
    private static IReadOnlyList<string> BuiltinNames() =>
        [.. typeof(ITool).Assembly.GetTypes()
            .Where(t => typeof(ITool).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false })
            .Where(t => t.Namespace != "Gatto.Extensions")
            .Select(t => (string)t.GetProperty(nameof(ITool.Name))!.GetValue(RuntimeHelpers.GetUninitializedObject(t))!)
            .Order(StringComparer.Ordinal)];

    private static IReadOnlyList<string> ShippedNames() =>
        [.. SourceTree.ScriptsUnder("Shipped", "extensions")
            .SelectMany(f => Registration.Matches(SourceTree.Read(f)).Select(m => m.Groups[1].Value))
            .Order(StringComparer.Ordinal)];

    [Fact]
    public void EVERY_BUILT_IN_TOOL_NAME_IS_SNAKE_CASE()
    {
        var names = BuiltinNames();
        Assert.Equal(9, names.Count);
        Assert.All(names, n => Assert.Matches(Snake, n));
    }

    [Fact]
    public void EVERY_SHIPPED_SCRIPT_TOOL_NAME_IS_SNAKE_CASE()
    {
        var names = ShippedNames();
        Assert.Equal(["ask_user", "web_fetch", "web_search"], names);
        Assert.All(names, n => Assert.Matches(Snake, n));
    }

    [Fact]
    public void THE_NAME_PATTERN_REFUSES_EACH_OTHER_CASE()
    {
        foreach (var bad in new[] { "readFile", "Read_file", "read-file", "read file", "_read", "1read", "read\n", "" })
            Assert.DoesNotMatch(Snake, bad);
        Assert.Matches(Snake, "web_search2");
    }
}
