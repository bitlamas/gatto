namespace Gatto.Tests.Setup.Tui;

//every fake answers C:\Users\you as the user's folder, so a setup frame that still prints it names a path that missed the fold
public class TildeGoldenTests
{
    private const string Profile = @"C:\Users\you\";

    //the config row keeps its path whole, it is the one a user opens, and an editor in Windows PowerShell gets a tilde literally
    private static bool IsConfigRow(string line) =>
        line.TrimStart(' ', '❯', '>').StartsWith("config ", StringComparison.Ordinal);

    [Fact]
    public void NO_SETUP_FRAME_PRINTS_THE_USERS_FOLDER_but_the_config_row()
    {
        var missed = Directory.EnumerateFiles(Golden.Dir, "*.txt")
            .SelectMany(f => File.ReadLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(r => r.Text.Contains(Profile, StringComparison.OrdinalIgnoreCase) && !IsConfigRow(r.Text))
            .Select(r => $"{r.File}:{r.Line}: {r.Text.Trim()}")
            .ToList();

        Assert.True(missed.Count == 0, "these rows print the user's folder whole:\n" + string.Join("\n", missed));
    }
}
