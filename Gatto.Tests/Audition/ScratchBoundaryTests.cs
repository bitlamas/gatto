using Gatto.Roles.Audition;

namespace Gatto.Tests.Audition;

//the boundary's two readings, a resolved path for the file tools and the command's text for the shell
public class ScratchBoundaryTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "gatto-audition-0f3a", "B4");

    private static ScratchBoundary Boundary => new(Root);

    [Theory]
    [InlineData(".")]
    [InlineData("config.txt")]
    [InlineData("conf\\config.txt")]
    [InlineData("./conf/config.txt")]
    [InlineData("conf\\..\\config.txt")]
    public void A_path_under_the_folder_is_INSIDE(string path) => Assert.True(Boundary.Inside(path));

    [Theory]
    [InlineData("..")]
    [InlineData("..\\B3\\settings.txt")]
    [InlineData("conf\\..\\..\\B3")]
    [InlineData("C:\\")]
    [InlineData("C:\\Users")]
    [InlineData("\\Windows")]
    public void A_path_that_resolves_elsewhere_is_OUTSIDE(string path) => Assert.False(Boundary.Inside(path));

    [Fact]
    public void The_folder_spelled_in_full_is_inside_and_a_sibling_with_a_longer_name_is_not()
    {
        Assert.True(Boundary.Inside(Path.Combine(Root, "conf", "config.txt")));
        Assert.True(Boundary.Inside(Root.ToUpperInvariant()));
        Assert.False(Boundary.Inside(Root + "x"));
    }

    [Theory]
    [InlineData("echo AUDITION-MARKER-AUD-1A2B3C4D")]
    [InlineData("Get-ChildItem -Recurse -Filter config.txt")]
    [InlineData("Get-ChildItem -Recurse | Select-String -Pattern 'code:'")]
    [InlineData("Get-Content .\\conf\\config.txt")]
    [InlineData("cat ./conf/config.txt")]
    [InlineData("cd conf; ls")]
    [InlineData("findstr /s /i code *.txt")]
    [InlineData("cmd /c dir /s /b")]
    [InlineData("(Get-Location).Path")]
    [InlineData("1..3 | % { $_ * 2 }")]
    [InlineData("echo \"looking...\"")]
    [InlineData("Test-Path missing.txt")]
    public void A_command_that_works_in_the_folder_STAYS(string command) => Assert.False(Boundary.Leaves(command));

    [Theory]
    [InlineData("Get-ChildItem -Path C:\\ -Recurse")]
    [InlineData("Get-ChildItem C:/Users -Recurse -Filter missing.txt")]
    [InlineData("dir D:\\")]
    [InlineData("Get-ChildItem ..")]
    [InlineData("ls ../")]
    [InlineData("cd ..; ls")]
    [InlineData("Get-Content ..\\B3\\settings.txt")]
    [InlineData("Get-ChildItem -Path \"..\" -Recurse")]
    [InlineData("Get-ChildItem ~ -Recurse")]
    [InlineData("ls ~/Documents")]
    [InlineData("Get-ChildItem $HOME -Recurse")]
    [InlineData("Get-ChildItem $env:USERPROFILE")]
    [InlineData("cmd /c dir %USERPROFILE%")]
    [InlineData("Get-ChildItem -Path \\ -Recurse")]
    [InlineData("ls /Users")]
    [InlineData("Get-ChildItem \\\\server\\share")]
    [InlineData("[Environment]::GetFolderPath('UserProfile')")]
    public void A_command_whose_text_reaches_outside_LEAVES(string command) => Assert.True(Boundary.Leaves(command));

    [Fact]
    public void A_command_that_spells_the_folder_in_full_stays_and_one_naming_its_parent_leaves()
    {
        //a model learns the folder from the shell and may use the whole path from then on
        Assert.False(Boundary.Leaves($"Get-Content '{Path.Combine(Root, "conf", "config.txt")}'"));
        Assert.False(Boundary.Leaves($"Get-ChildItem {Root.Replace('\\', '/')} -Recurse"));
        Assert.True(Boundary.Leaves($"Get-ChildItem '{Path.GetDirectoryName(Root)}'"));
    }

    //the folder is compared by whole segments, so a sibling whose name only begins with the folder's is outside
    [Fact]
    public void A_sibling_folder_with_a_longer_name_LEAVES()
    {
        Assert.True(Boundary.Leaves($"Get-ChildItem '{Root}5'"));
        Assert.True(Boundary.Leaves($"Get-Content {Root}-old\\config.txt"));
    }
}
