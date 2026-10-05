using Gatto.Core.Loop.Permissions;

namespace Gatto.Tests;

//the boundary's readings: a resolved path for the file tools, the command's text for the shell, and gatto's own folder by any name
public class WorkspaceBoundaryTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "gatto-ws-0f3a", "proj");
    private static readonly string Granted = Path.Combine(Path.GetTempPath(), "gatto-ws-0f3a", "shared");
    private static readonly string Home = Path.Combine(Path.GetTempPath(), "gatto-ws-0f3a", "home", ".gatto");

    private static WorkspaceBoundary Boundary => new(Root,
        p => Path.GetFullPath(p).StartsWith(Granted + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
        Home);

    private static string Full(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(Root, path));

    [Theory]
    [InlineData(".")]
    [InlineData("config.txt")]
    [InlineData("conf\\config.txt")]
    [InlineData("./conf/config.txt")]
    [InlineData("conf\\..\\config.txt")]
    public void A_path_under_the_working_folder_is_INSIDE(string path) => Assert.True(Boundary.Inside(Full(path)));

    [Theory]
    [InlineData("..")]
    [InlineData("..\\other\\settings.txt")]
    [InlineData("conf\\..\\..\\other")]
    [InlineData("C:\\")]
    [InlineData("C:\\Users")]
    public void A_path_that_resolves_elsewhere_is_OUTSIDE(string path) => Assert.False(Boundary.Inside(Full(path)));

    [Fact]
    public void A_granted_folder_is_inside_and_a_sibling_with_a_longer_name_is_not()
    {
        Assert.True(Boundary.Inside(Path.Combine(Granted, "notes.md")));
        Assert.False(Boundary.Inside(Root + "x"));
        Assert.True(Boundary.Inside(Root.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("echo hi")]
    [InlineData("Get-ChildItem -Recurse -Filter config.txt")]
    [InlineData("Get-Content .\\conf\\config.txt")]
    [InlineData("cat ./conf/config.txt")]
    [InlineData("cd conf; ls")]
    [InlineData("findstr /s /i code *.txt")]
    [InlineData("cmd /c dir /s /b")]
    [InlineData("1..3 | % { $_ * 2 }")]
    [InlineData("echo \"looking...\"")]
    public void A_command_that_works_in_the_folder_STAYS(string command) => Assert.False(Boundary.Leaves(command));

    [Theory]
    [InlineData("Get-ChildItem -Path C:\\ -Recurse")]
    [InlineData("Get-ChildItem C:/Users -Recurse")]
    [InlineData("dir D:\\")]
    [InlineData("Get-ChildItem ..")]
    [InlineData("ls ../")]
    [InlineData("Get-Content ..\\other\\settings.txt")]
    [InlineData("Get-ChildItem ~ -Recurse")]
    [InlineData("Get-ChildItem $HOME -Recurse")]
    [InlineData("Get-ChildItem $env:USERPROFILE")]
    [InlineData("cmd /c dir %USERPROFILE%")]
    [InlineData("Get-ChildItem \\\\server\\share")]
    [InlineData("[Environment]::GetFolderPath('UserProfile')")]
    public void A_command_whose_text_reaches_outside_LEAVES(string command) => Assert.True(Boundary.Leaves(command));

    [Fact]
    public void A_command_spelling_the_folder_or_a_grant_in_full_stays_and_one_naming_a_sibling_leaves()
    {
        Assert.False(Boundary.Leaves($"Get-Content '{Path.Combine(Root, "conf", "config.txt")}'"));
        Assert.False(Boundary.Leaves($"Get-ChildItem {Root.Replace('\\', '/')} -Recurse"));
        Assert.False(Boundary.Leaves($"Get-Content {Path.Combine(Granted, "notes.md")}"));
        Assert.True(Boundary.Leaves($"Get-ChildItem '{Root}-2'"));
        Assert.True(Boundary.Leaves($"Get-ChildItem '{Path.GetDirectoryName(Root)}'"));
    }

    [Fact]
    public void Gattos_folder_is_in_home_and_a_folder_beside_it_is_not()
    {
        Assert.True(Boundary.InHome(Path.Combine(Home, "gatto.json")));
        Assert.True(Boundary.InHome(Home));
        Assert.False(Boundary.InHome(Home + "-old"));
        Assert.False(Boundary.InHome(Path.Combine(Root, ".gatto", "memory", "x.md")));
    }

    [Theory]
    [InlineData("Get-Content ~\\.gatto\\gatto.json")]
    [InlineData("cat ~/.gatto/gatto.json")]
    [InlineData("Remove-Item $HOME\\.gatto -Recurse")]
    [InlineData("Set-Content $env:USERPROFILE\\.gatto\\gatto.json x")]
    [InlineData("type %USERPROFILE%\\.gatto\\gatto.json")]
    [InlineData("dir $env:GATTO_HOME")]
    [InlineData("dir %GATTO_HOME%")]
    public void A_command_naming_gattos_folder_by_a_shell_name_NAMES_HOME(string command) =>
        Assert.True(Boundary.NamesHome(command));

    [Fact]
    public void A_command_naming_gattos_folder_in_full_names_home_and_near_misses_do_not()
    {
        Assert.True(Boundary.NamesHome($"Set-Content '{Path.Combine(Home, "gatto.json")}' x"));
        Assert.True(Boundary.NamesHome($"dir {Home.Replace('\\', '/')}"));
        Assert.False(Boundary.NamesHome($"dir {Home}-old"));
        Assert.False(Boundary.NamesHome("Get-ChildItem .gatto\\memory"));
        Assert.False(Boundary.NamesHome("cd gatto; ls"));
        Assert.False(Boundary.NamesHome("Get-Content ~\\.gatto-old\\gatto.json"));
    }
}
