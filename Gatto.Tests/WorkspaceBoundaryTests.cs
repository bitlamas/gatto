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

    //the environment by every spelling PowerShell reads it with, the provider drive and the braced variable as much as $env:
    [Theory]
    [InlineData("$env:USERPROFILE")]
    [InlineData("$Env:USERPROFILE")]
    [InlineData("$ENV:Path")]
    [InlineData("${env:USERPROFILE}")]
    [InlineData("Get-Item Env:USERPROFILE")]
    [InlineData("Get-Item -Path Env:USERPROFILE")]
    [InlineData("gci env:")]
    [InlineData("Get-ChildItem Env:\\")]
    [InlineData("Get-Content env:Path")]
    [InlineData("cd env:; ls")]
    [InlineData("(Get-Item 'Env:USERPROFILE').Value")]
    [InlineData("[System.Environment]::GetEnvironmentVariable('USERPROFILE')")]
    [InlineData("[System.Environment]::GetEnvironmentVariables()")]
    public void A_command_reading_the_environment_LEAVES(string command) => Assert.True(Boundary.Leaves(command));

    //a name that only contains env reads no variable
    [Theory]
    [InlineData("Get-Content .env")]
    [InlineData("Get-Content .\\conf\\dev.env")]
    [InlineData("npm run build:env")]
    [InlineData("python -m venv .venv")]
    [InlineData("echo environment")]
    public void A_command_that_only_names_env_STAYS(string command) => Assert.False(Boundary.Leaves(command));

    //gatto's own variables are readable in every spelling the environment is screened in, each by its whole name
    [Theory]
    [InlineData("Write-Output $env:GATTO_SESSION_ID")]
    [InlineData("Write-Output $Env:gatto_session_id")]
    [InlineData("Write-Output ${env:GATTO_SESSION_ID}")]
    [InlineData("Write-Output \"$env:GATTO_SESSION_ID $env:GATTO_SESSION_START\"")]
    [InlineData("Get-Item Env:GATTO_SESSION_ID")]
    [InlineData("Get-Item Env:\\GATTO_SESSION_ID")]
    [InlineData("Get-Content Env:GATTO_SESSION_ID")]
    [InlineData("cmd /c echo %GATTO_SESSION_ID%")]
    [InlineData("[System.Environment]::GetEnvironmentVariable('GATTO_SESSION_ID')")]
    [InlineData("[Environment]::GetEnvironmentVariable(\"GATTO_SESSION_ID\")")]
    public void A_command_reading_only_gattos_own_variables_STAYS(string command) => Assert.False(Boundary.Leaves(command));

    //a GATTO_ read beside any other, the whole drive, every variable at once, or a name that only contains GATTO_ still leaves
    [Theory]
    [InlineData("Write-Output $env:GATTO_SESSION_ID $env:USERPROFILE")]
    [InlineData("Write-Output $env:GATTO_SESSION_ID$env:PATH")]
    [InlineData("Get-Item Env:GATTO_SESSION_ID, Env:PATH")]
    [InlineData("cmd /c echo %GATTO_SESSION_ID% %USERPROFILE%")]
    [InlineData("cmd /c echo %A%GATTO_X%")]
    [InlineData("gci env:")]
    [InlineData("cd env:; ls")]
    [InlineData("[System.Environment]::GetEnvironmentVariables()")]
    [InlineData("[Environment]::GetEnvironmentVariable('GATTO_X'.Replace('GATTO_X', 'PATH'))")]
    [InlineData("Write-Output $env:MY_GATTO_X")]
    [InlineData("Get-Item Env:MY_GATTO_X")]
    [InlineData("cmd /c echo %MY_GATTO_X%")]
    [InlineData("[Environment]::GetEnvironmentVariable('MY_GATTO_X')")]
    public void A_command_reading_more_than_gattos_own_variables_LEAVES(string command) => Assert.True(Boundary.Leaves(command));

    //GATTO_HOME holds a path, so it leaves in every spelling, a wildcard that could list it included
    [Theory]
    [InlineData("Write-Output $env:GATTO_HOME")]
    [InlineData("Write-Output $Env:gatto_home")]
    [InlineData("Write-Output ${env:GATTO_HOME}")]
    [InlineData("Get-Item Env:GATTO_HOME")]
    [InlineData("Get-Item Env:\\gatto_home")]
    [InlineData("Get-Content Env:GATTO_HOME")]
    [InlineData("cmd /c echo %GATTO_HOME%")]
    [InlineData("[System.Environment]::GetEnvironmentVariable('GATTO_HOME')")]
    [InlineData("[Environment]::GetEnvironmentVariable(\"gatto_home\")")]
    [InlineData("Get-ChildItem Env:GATTO_*")]
    [InlineData("Get-ChildItem Env:GATTO_H*")]
    [InlineData("Get-Item Env:GATTO_?OME")]
    [InlineData("Get-ChildItem Env:GATTO_[H]OME")]
    [InlineData("Write-Output $env:GATTO_SESSION_ID $env:GATTO_HOME")]
    public void GATTO_HOME_LEAVES_IN_EVERY_SPELLING(string command) => Assert.True(Boundary.Leaves(command));

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
