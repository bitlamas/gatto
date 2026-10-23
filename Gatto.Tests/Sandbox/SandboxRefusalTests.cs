using Gatto.Core.Sandbox;

namespace Gatto.Tests.Sandbox;

public sealed class SandboxRefusalTests
{
    private const string Project = @"C:\work\app";
    private static readonly string Write = SandboxRefusal.WriteLine(Project);

    [Theory]
    [InlineData("+ CategoryInfo : PermissionDenied: (C:\\x:String) [Set-Content], UnauthorizedAccessException")]
    [InlineData("[Set-Content], Unauthorized \r\n   AccessException")]
    [InlineData("error MSB3191: Unable to create directory \"C:\\out\". Access to the path 'C:\\out' is denied.")]
    [InlineData("error MSB3021: Unable to copy file")]
    [InlineData("fatal: could not create work tree dir 'x': Permission denied")]
    [InlineData("PermissionError: [Errno 13] Permission denied: 'C:\\\\tmp\\\\x'")]
    [InlineData("npm ERR! code EPERM")]
    [InlineData("Program 'dotnet.exe' failed to run: Access is denied + FullyQualifiedErrorId : NativeCommandFailed")]
    [InlineData("PermissionDenied UnauthorizedAccessException MSB3191")]
    public void A_REFUSED_WRITE_IN_STDERR_APPENDS_THE_WRITE_LINE_ONCE(string stderr)
    {
        Assert.Equal(Write, SandboxRefusal.Note(stderr, Project));
    }

    [Theory]
    [InlineData("New-ItemProperty : Requested registry access is not allowed.\n+ try { New-ItemProperty -Path 'HKCU:\\Software\\x' -Name v\n    + CategoryInfo          : PermissionDenied: (HKEY_CURRENT_US...e\\x:String) [New-ItemProperty], Securit \n   yException")]
    [InlineData("New-Item : Access to the registry key 'HKEY_CURRENT_USER\\Software\\x\\sub' is denied.\n    + CategoryInfo : PermissionDenied: (HKEY_CURRENT_US...e\\x\\sub:String) [New-Item], UnauthorizedAccessException")]
    [InlineData("Set-ItemProperty : Access denied + CategoryInfo : PermissionDenied: (Registry::HKEY_USERS\\x:String)")]
    public void A_REFUSED_REGISTRY_WRITE_APPENDS_THE_REGISTRY_LINE_AND_NOT_THE_FOLDER_LINE(string stderr)
    {
        Assert.Equal(SandboxRefusal.RegistryLine, SandboxRefusal.Note(stderr, Project));
    }

    //a refused path inside the labelled folders is not the sandbox's refusal, a file the user relabelled or another process holds
    [Theory]
    [InlineData("Copy-Item : Access to the path \n'C:\\work\\app\\bin\\tool.exe' is denied.\n    + CategoryInfo : PermissionDenied: (C:\\x:FileInfo) [Copy-Item], Unauthorized \n   AccessException")]
    [InlineData("Set-Content : Access to the path 'C:\\nuget\\pkg\\x.dll' is denied. [Set-Content], UnauthorizedAccessException")]
    [InlineData("error MSB3021: Unable to copy file \"C:\\work\\app\\obj\\a.dll\" to \"C:\\work\\app\\bin\\a.dll\". Access to the path 'C:\\work\\app\\bin\\a.dll' is denied.")]
    public void A_REFUSED_PATH_INSIDE_THE_ROOTS_APPENDS_NOTHING(string stderr)
    {
        Assert.Null(SandboxRefusal.Note(stderr, Project, [Project, @"C:\nuget"]));
    }

    [Theory]
    [InlineData("Set-Content : Access to the path 'C:\\Users\\u\\AppData\\Local\\Temp\\x.txt' is denied. UnauthorizedAccessException")]
    [InlineData("error MSB3191: Unable to create directory \"C:\\elsewhere\\out\". Access to the path 'C:\\elsewhere\\out' is denied.")]
    [InlineData("PermissionError: [Errno 13] Permission denied: 'C:\\elsewhere\\x'")]
    [InlineData("fatal: could not create work tree dir 'C:\\elsewhere\\repo': Permission denied")]
    public void A_REFUSED_PATH_OUTSIDE_THE_ROOTS_APPENDS_THE_WRITE_LINE(string stderr)
    {
        Assert.Equal(Write, SandboxRefusal.Note(stderr, Project, [Project, @"C:\nuget"]));
    }

    //with no full path in the message the id alone decides, as before
    [Fact]
    public void A_REFUSAL_WITH_NO_PATH_KEEPS_THE_ID_RULE()
    {
        Assert.Equal(Write, SandboxRefusal.Note("[Set-Content], UnauthorizedAccessException", Project, [Project]));
        Assert.Equal(Write, SandboxRefusal.Note("fatal: could not create work tree dir 'repo': Permission denied", Project, [Project]));
    }

    [Theory]
    [InlineData("Access is denied")]
    [InlineData("the file mentions access to the path in its text")]
    [InlineData("NativeCommandFailed: exit code 1")]
    [InlineData("Get-ItemProperty : Cannot find path 'HKCU:\\Software\\nope' because it does not exist.")]
    [InlineData("MethodInvocationNotSupportedInConstrainedLanguage")]
    [InlineData("")]
    public void A_BARE_PHRASE_OR_AN_ID_WITHOUT_ITS_WORDS_APPENDS_NOTHING(string stderr)
    {
        Assert.Null(SandboxRefusal.Note(stderr, Project));
    }
}
