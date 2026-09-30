using Gatto.Cli;

namespace Gatto.Tests;

//install mechanics against a real filesystem, as far as the windows seams allow a test
public class SelfInstallTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-install-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string MakeExe(string name, string content = "v1")
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content);
        return p;
    }

    [Fact]
    public void RUNNING_FROM_THE_INSTALL_FOLDER_IS_INSTALLED_whatever_else_is_true()
    {
        var target = Path.Combine(_dir, "gatto.exe");

        Assert.Equal(InstallState.Installed,
            SelfInstall.Probe(_dir, target, installedExeExists: true, dirOnPath: false, frameworkDependent: false));
    }

    [Fact]
    public void INSTALLED_BUT_NOT_ON_PATH_IS_ITS_OWN_STATE()
    {
        //installed but not on the path is its own state, from a new terminal the command still fails
        Assert.Equal(InstallState.InstalledButNotOnPath,
            SelfInstall.Probe(_dir, @"D:\Downloads\gatto.exe", installedExeExists: true, dirOnPath: false, frameworkDependent: false));
    }

    [Fact]
    public void AN_INSTALLED_AND_REACHABLE_COPY_NEEDS_NO_OFFER()
    {
        Assert.Equal(InstallState.AlreadyInstalledElsewhere,
            SelfInstall.Probe(_dir, @"D:\Downloads\gatto.exe", installedExeExists: true, dirOnPath: true, frameworkDependent: false));
    }

    [Fact]
    public void NOTHING_INSTALLED_IS_THE_OFFER_CASE()
    {
        Assert.Equal(InstallState.NotInstalled,
            SelfInstall.Probe(_dir, @"D:\Downloads\gatto.exe", installedExeExists: false, dirOnPath: false, frameworkDependent: false));
    }

    [Fact]
    public void APPLY_COPIES_THE_ARTIFACT_INTO_PLACE()
    {
        var source = MakeExe("source.exe", "the new build");
        var into = Path.Combine(_dir, "install");

        Assert.Null(SelfInstall.Apply(source, into));

        Assert.Equal("the new build", File.ReadAllText(SelfInstall.ExeIn(into)));
    }

    [Fact]
    public void REINSTALLING_MOVES_THE_PREDECESSOR_ASIDE_rather_than_overwriting_it()
    {
        //a running exe can be copied and renamed on windows but not deleted or overwritten, so the old copy steps aside
        var into = Path.Combine(_dir, "install");
        Assert.Null(SelfInstall.Apply(MakeExe("v1.exe", "old build"), into));
        var source2 = MakeExe("v2.exe", "new build");

        Assert.Null(SelfInstall.Apply(source2, into));

        Assert.Equal("new build", File.ReadAllText(SelfInstall.ExeIn(into)));
        Assert.Equal("old build", File.ReadAllText(SelfInstall.ExeIn(into) + SelfInstall.OldSuffix));
    }

    [Fact]
    public void THE_SWEEP_REMOVES_A_PREDECESSOR_and_is_silent_when_there_is_none()
    {
        var into = Path.Combine(_dir, "install");
        Assert.Null(SelfInstall.Apply(MakeExe("v1.exe"), into));
        Assert.Null(SelfInstall.Apply(MakeExe("v2.exe"), into));
        var aside = SelfInstall.ExeIn(into) + SelfInstall.OldSuffix;
        Assert.True(File.Exists(aside));

        SelfInstall.SweepOld(into);
        Assert.False(File.Exists(aside));

        //the sweep runs on every launch, so on a clean or missing folder it must be quiet and harmless
        SelfInstall.SweepOld(into);
        SelfInstall.SweepOld(Path.Combine(_dir, "never-existed"));
    }

    [Fact]
    public void A_THIRD_INSTALL_DOES_NOT_TRIP_OVER_THE_SECONDS_LEFTOVER()
    {
        //a third install reuses the predecessor name the second left, now that the file is deletable
        var into = Path.Combine(_dir, "install");
        Assert.Null(SelfInstall.Apply(MakeExe("v1.exe", "one"), into));
        Assert.Null(SelfInstall.Apply(MakeExe("v2.exe", "two"), into));

        Assert.Null(SelfInstall.Apply(MakeExe("v3.exe", "three"), into));

        Assert.Equal("three", File.ReadAllText(SelfInstall.ExeIn(into)));
        Assert.Equal("two", File.ReadAllText(SelfInstall.ExeIn(into) + SelfInstall.OldSuffix));
    }

    [Fact]
    public void A_MISSING_SOURCE_IS_REPORTED_not_thrown()
    {
        var problem = SelfInstall.Apply(Path.Combine(_dir, "nope.exe"), Path.Combine(_dir, "install"));

        Assert.NotNull(problem);
        Assert.Contains("couldn't install", problem);
    }

    //a framework-dependent build has nothing to offer, copying its exe yields a launcher with nothing to launch
    [Theory]
    [InlineData(false, false)]   //nothing installed, so a normal build would offer an install.
    [InlineData(true, false)]    //installed but not on the path, so a normal build would offer to finish.
    public void A_DEV_BUILD_IS_NOT_OFFERED_AN_INSTALL(bool installedExeExists, bool dirOnPath)
    {
        Assert.Equal(
            InstallState.DevBuild,
            SelfInstall.Probe(_dir, @"D:\src\gattoin\gatto.exe", installedExeExists, dirOnPath,
                frameworkDependent: true));
    }

    //a dev build already installed elsewhere keeps its own state, which is the truer answer
    [Fact]
    public void A_DEV_BUILD_ALREADY_INSTALLED_ELSEWHERE_KEEPS_ITS_OWN_STATE()
    {
        Assert.Equal(
            InstallState.AlreadyInstalledElsewhere,
            SelfInstall.Probe(_dir, @"D:\src\gattoin\gatto.exe", installedExeExists: true,
                dirOnPath: true, frameworkDependent: true));
    }

    //a dev build is detected by the dll beside the exe, which dotnet build produces and a self-contained publish does not
    [Fact]
    public void THE_DLL_BESIDE_THE_EXE_IS_WHAT_MAKES_IT_A_DEV_BUILD()
    {
        var exe = Path.Combine(_dir, "gatto.exe");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(exe, "launcher");

        Assert.False(SelfInstall.IsFrameworkDependent(exe));   //a release artifact is one file.

        File.WriteAllText(Path.Combine(_dir, "gatto.dll"), "managed");
        Assert.True(SelfInstall.IsFrameworkDependent(exe));    //a dev build needs its folder.
    }

    //the refusal lives in Apply itself, so a caller that skipped the probe still cannot install a gatto that will not start
    [Fact]
    public void APPLY_REFUSES_A_DEV_BUILD_EVEN_IF_NOBODY_ASKED_THE_PROBE()
    {
        var source = Path.Combine(_dir, "src", "gatto.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllText(source, "launcher");
        File.WriteAllText(Path.ChangeExtension(source, ".dll"), "managed");

        var target = Path.Combine(_dir, "installed");
        var problem = SelfInstall.Apply(source, target);

        Assert.NotNull(problem);
        Assert.Contains("development build", problem, StringComparison.Ordinal);
        //it must refuse before writing, a half-install left behind is worse than the copy it prevented
        Assert.False(File.Exists(SelfInstall.ExeIn(target)));
    }

    //both install paths place the identical file set, so this one places only the exe and the decoys beside it prove it
    [Fact]
    public void APPLY_PLACES_EXACTLY_THE_EXE_even_with_the_release_documents_beside_it()
    {
        var src = Directory.CreateTempSubdirectory("gatto-src-").FullName;
        var dst = Directory.CreateTempSubdirectory("gatto-dst-").FullName;
        try
        {
            var exe = Path.Combine(src, "gatto.exe");
            File.WriteAllText(exe, "the binary");
            foreach (var decoy in new[] { "LICENSE", "README.md", "gatto.pdb" })
                File.WriteAllText(Path.Combine(src, decoy), "not the binary");

            Assert.Null(SelfInstall.Apply(exe, dst));

            Assert.Equal(["gatto.exe"], Directory.GetFiles(dst).Select(f => Path.GetFileName(f)!).ToArray());
            Assert.Equal("the binary", File.ReadAllText(Path.Combine(dst, "gatto.exe")));
        }
        finally
        {
            try { Directory.Delete(src, true); } catch { }
            try { Directory.Delete(dst, true); } catch { }
        }
    }
}
