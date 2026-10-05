using System.Text.Json;
using Gatto.Core.Home;
using Gatto.Core.Tools;
using Gatto.Roles;

namespace Gatto.Tests;

//the project block must compose after the home block, so it wins. each assertion compares two sides, so a stray file cancels and isolation is unneeded
public class ContextLayerStabilityTests : IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("gatto-layer-").FullName;

    public void Dispose() { try { Directory.Delete(_tempDir, recursive: true); } catch { } }

    private static RoleFile Role() =>
        new("r", "p", null, Array.Empty<string>(), false, null, ThinkingLevel.Medium);

    private (string Home, string Cwd) Fixture(string homeText, string projectText)
    {
        var home = Path.Combine(_tempDir, "home");
        var cwd = Path.Combine(_tempDir, "proj");
        Directory.CreateDirectory(home);
        Directory.CreateDirectory(cwd);
        var homeMd = Path.Combine(home, "GATTO.md");
        File.WriteAllText(homeMd, homeText);
        File.WriteAllText(Path.Combine(cwd, "GATTO.md"), projectText);
        return (homeMd, cwd);
    }

    private static string Compose(string homeMd, string cwd) =>
        RoleComposition.Compose(
            Role(), null, ContextFiles.Collect(cwd, false, homeMd), null, cwd: cwd).SystemText;

    [Fact]
    public void The_home_block_precedes_every_ancestor_block_so_the_project_file_wins()
    {
        var (homeMd, cwd) = Fixture("GLOBAL-MARK", "PROJECT-MARK");

        var text = Compose(homeMd, cwd);

        var global = text.IndexOf("GLOBAL-MARK", StringComparison.Ordinal);
        var project = text.IndexOf("PROJECT-MARK", StringComparison.Ordinal);
        Assert.True(global >= 0 && project >= 0, "both files must be in the prompt");
        Assert.True(global < project,
            "the home layer is read FIRST so the project file, being later, overrides it");
    }

    [Fact]
    public void The_same_tree_composes_the_same_bytes()
    {
        //not a tautology, Collect reads the disk again and Compose must stay pure. a volatile date or an unstable order would force a re-prefill
        var (homeMd, cwd) = Fixture("GLOBAL", "PROJECT");

        Assert.Equal(Compose(homeMd, cwd), Compose(homeMd, cwd));
    }

    [Fact]
    public void A_comment_only_home_file_leaves_the_prompt_exactly_as_if_it_were_absent()
    {
        //a stripped-away entry must leave no trace in the prompt, with no empty labelled block and no blank line
        var (homeMd, cwd) = Fixture("<!-- private note -->\n", "PROJECT");
        var withCommentOnlyHome = Compose(homeMd, cwd);

        File.Delete(homeMd);
        var withNoHomeAtAll = Compose(homeMd, cwd);

        Assert.Equal(withNoHomeAtAll, withCommentOnlyHome);
    }
}
