using System.Text.RegularExpressions;

namespace Gatto.Tests;

//two launches are two sessions, so a test comparing their prompts byte for byte masks the one line that differs by design
internal static partial class SessionLine
{
    [GeneratedRegex(@"This is session [0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\.")]
    private static partial Regex Line();

    public static string Masked(string text) => Line().Replace(text, "This is session <id>.");
}
