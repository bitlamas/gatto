using Gatto.Cli.Setup;

namespace Gatto.Tests.Setup;

//the PATH and copy deeds are injected here, since a test host is a framework-dependent source SelfInstall.Apply refuses
public class WriteSetPathDeedTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-deed-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private readonly List<string> _pathAdds = [];
    private readonly List<(string From, string To)> _copies = [];

    private string? Run(WriteSet writes) =>
        WriteSetApply.Apply(_home, writes, out _, out _,
            addToPath: entry => { _pathAdds.Add(entry); return null; },
            copyExe: (from, to) => { _copies.Add((from, to)); return null; });

    [Fact]
    public void INSTALLING_COPIES_THEN_ADDS_THAT_DIRECTORY_TO_PATH()
    {
        Assert.Null(Run(new WriteSet { InstallTo = @"C:\Programs\gatto" }));

        Assert.Equal(@"C:\Programs\gatto", Assert.Single(_copies).To);
        Assert.Equal(@"C:\Programs\gatto", Assert.Single(_pathAdds));
    }

    //the oracle is the copy deed never being called, since a PATH assertion alone would pass a run that also copied
    [Fact]
    public void REPAIRING_ADDS_PATH_AND_COPIES_NOTHING()
    {
        Assert.Null(Run(new WriteSet { PathOnly = true }));

        Assert.Empty(_copies);
        Assert.Single(_pathAdds);
    }

    //the apply's else is load-bearing only when both are set, so only this row pins the choice at the deed
    [Fact]
    public void A_WRITE_SET_CARRYING_BOTH_COPIES_ONCE_AND_ADDS_PATH_ONCE()
    {
        Assert.Null(Run(new WriteSet { InstallTo = @"C:\Programs\gatto", PathOnly = true }));

        Assert.Single(_copies);
        Assert.Single(_pathAdds);
    }

    //a copy that landed with no PATH entry says so, rather than reporting success for a gatto no terminal can find
    [Fact]
    public void A_FAILED_PATH_EDIT_IS_REPORTED_and_names_the_copy_that_did_land()
    {
        var problem = WriteSetApply.Apply(_home, new WriteSet { InstallTo = @"C:\Programs\gatto" },
            out _, out _,
            addToPath: _ => "the PATH value was too long",
            copyExe: (_, _) => null);

        Assert.NotNull(problem);
        Assert.Contains(@"C:\Programs\gatto", problem, StringComparison.Ordinal);
    }
}
