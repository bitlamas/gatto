using System.Runtime.Versioning;
using Gatto.Core.Sandbox;

namespace Gatto.Tests.Sandbox;

//every folder here is one the test creates and deletes whole, so no label outlives the test
[SupportedOSPlatform("windows")]
public sealed class SandboxGrantsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gatto-grants-" + Guid.NewGuid().ToString("N"));
    private string Home => Path.Combine(_root, "home");

    public SandboxGrantsTests() => Directory.CreateDirectory(Home);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string Folder(string name) => Directory.CreateDirectory(Path.Combine(_root, name)).FullName;

    private static Action<string> Counting(List<string> calls, Action<string>? then = null) =>
        f => { calls.Add(f); then?.Invoke(f); };

    [Fact]
    public void A_PLANT_LEAVES_ONE_INHERITABLE_LOW_LABEL_A_NEW_FILE_TAKES_IT_AND_A_SECOND_PLANT_WRITES_NOTHING()
    {
        var folder = Folder("project");
        SandboxGrants.Plant(folder, Home);
        var calls = new List<string>();
        SandboxGrants.Plant(folder, Home, Counting(calls));

        Assert.Equal("S:AI" + SandboxGrants.LowLabel, SandboxGrants.Label(folder));
        Assert.Empty(calls);
        var file = Path.Combine(folder, "after.txt");
        File.WriteAllText(file, "x");
        Assert.Equal("S:AI(ML;ID;NW;;;LW)", SandboxGrants.Label(file));
        Assert.Equal("S:AI(ML;OICIID;NW;;;LW)", SandboxGrants.Label(Folder(Path.Combine("project", "sub"))));
    }

    //the root call takes every inherited label, the walk takes an explicit low one, and an explicit label above low is the user's and stays
    [Fact]
    public void THE_UNPLANT_LEAVES_NO_LOW_LABEL_AND_KEEPS_AN_EXPLICIT_MEDIUM_ONE()
    {
        var folder = Folder("project");
        var before = Path.Combine(folder, "before.txt");
        File.WriteAllText(before, "x");
        SandboxGrants.Plant(folder, Home);
        var sub = Folder(Path.Combine("project", "sub"));
        var inherited = Path.Combine(sub, "inherited.txt");
        File.WriteAllText(inherited, "x");
        var explicitLow = Path.Combine(folder, "explicit-low.txt");
        File.WriteAllText(explicitLow, "x");
        Icacls(explicitLow, "low");
        var explicitMedium = Path.Combine(folder, "explicit-medium.txt");
        File.WriteAllText(explicitMedium, "x");
        Icacls(explicitMedium, "medium");
        Assert.Contains(";;;LW)", SandboxGrants.Label(explicitLow), StringComparison.Ordinal);
        Assert.Contains(";;;LW)", SandboxGrants.Label(inherited), StringComparison.Ordinal);

        SandboxGrants.Unplant(folder);

        foreach (var path in new[] { folder, before, sub, inherited, explicitLow })
            Assert.DoesNotContain(";;;LW)", SandboxGrants.Label(path), StringComparison.Ordinal);
        Assert.Contains(";;;ME)", SandboxGrants.Label(explicitMedium), StringComparison.Ordinal);
        Assert.False(SandboxGrants.Holds(folder));
    }

    //a root still holding the label at launch is a killed session's, removed and said once, and a clean machine is neither touched nor created
    [Fact]
    public void A_LABEL_LEFT_AT_LAUNCH_IS_REMOVED_ONCE_AND_A_CLEAN_LAUNCH_TOUCHES_NOTHING()
    {
        var project = Folder("project");
        var nuget = Path.Combine(_root, "nuget-absent");
        var cache = Path.Combine(_root, "cache-absent");
        var temp = Path.Combine(_root, "temp-absent");
        using (var clean = new SandboxState(project, Home, [], nuget, cache, temp))
            Assert.Null(clean.RemoveResidue());
        Assert.False(Directory.Exists(nuget) || Directory.Exists(cache) || Directory.Exists(temp));

        SandboxGrants.Plant(project, Home);
        var file = Path.Combine(project, "built.exe");
        File.WriteAllText(file, "x");
        using var next = new SandboxState(project, Home, [], nuget, cache, temp);
        Assert.Equal(SandboxState.ResidueLine, next.RemoveResidue());
        Assert.False(SandboxGrants.Holds(project));
        Assert.DoesNotContain(";;;LW)", SandboxGrants.Label(file), StringComparison.Ordinal);
        Assert.Null(next.RemoveResidue());
    }

    private static void Icacls(string path, string level)
    {
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls.exe", [path, "/setintegritylevel", level])
            { RedirectStandardOutput = true, UseShellExecute = false })!;
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    [Fact]
    public void AN_INHERITED_LABEL_IS_NOT_THE_GRANT_AND_THE_SUBFOLDER_IS_PLANTED()
    {
        var folder = Folder("project");
        SandboxGrants.Plant(folder, Home);
        var sub = Folder(Path.Combine("project", "sub"));
        //an inherited label is not the grant, since the parent it came from can lose it
        Assert.False(SandboxGrants.Holds(sub));
        SandboxGrants.Plant(sub, Home);
        Assert.True(SandboxGrants.Holds(sub));
    }

    [Fact]
    public void A_FAILED_PLANT_NAMES_THE_FOLDER_IS_LISTED_AND_IS_PLANTED_AGAIN_UNTIL_IT_COMPLETES()
    {
        var folder = Folder("locked");
        var ex = Assert.Throws<SandboxPlantException>(() =>
            SandboxGrants.Plant(folder, Home, _ => throw new UnauthorizedAccessException($"Access to the path '{folder}\\deep' is denied.")));
        Assert.Equal(folder, ex.Folder);
        Assert.Contains("deep", ex.Message);
        var record = Path.Combine(Home, SandboxGrants.FailedFile);
        Assert.Equal([folder], File.ReadAllLines(record));

        //the root may hold the label after a partial failure, so presence alone must not skip a listed folder
        SandboxGrants.Plant(folder, Home);
        var calls = new List<string>();
        File.AppendAllText(record, folder + "\n");
        SandboxGrants.Plant(folder, Home, Counting(calls));
        Assert.Single(calls);
        Assert.False(File.Exists(record));
    }

    //a label write needs WRITE_OWNER, and the label reaches every descendant, so one that refuses it fails the plant
    [Fact]
    public void A_DESCENDANT_THAT_REFUSES_THE_LABEL_FAILS_THE_PLANT_AND_LISTS_THE_FOLDER()
    {
        var folder = Folder("project");
        var child = Folder(Path.Combine("project", "locked"));
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var info = new DirectoryInfo(child);
        var acl = info.GetAccessControl();
        acl.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(me,
            System.Security.AccessControl.FileSystemRights.TakeOwnership, System.Security.AccessControl.AccessControlType.Deny));
        info.SetAccessControl(acl);

        var ex = Assert.Throws<SandboxPlantException>(() => SandboxGrants.Plant(folder, Home));
        Assert.Equal(folder, ex.Folder);
        Assert.Contains(child, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([folder], File.ReadAllLines(Path.Combine(Home, SandboxGrants.FailedFile)));
    }

    [Fact]
    public void THE_HTTP_CACHE_IS_THE_NAMED_FOLDER_CREATED_WHEN_ABSENT()
    {
        var cache = Path.Combine(_root, "http-cache");
        var old = Environment.GetEnvironmentVariable("NUGET_HTTP_CACHE_PATH");
        Environment.SetEnvironmentVariable("NUGET_HTTP_CACHE_PATH", cache);
        try { Assert.Equal(cache, SandboxGrants.NuGetHttpCache()); }
        finally { Environment.SetEnvironmentVariable("NUGET_HTTP_CACHE_PATH", old); }
        Assert.True(Directory.Exists(cache));
    }

    [Fact]
    public void THE_REPOSITORY_ROOT_IS_THE_PROJECT_WHEN_A_GIT_FOLDER_IS_ABOVE()
    {
        var repo = Folder("repo");
        Directory.CreateDirectory(Path.Combine(repo, ".git"));
        var sub = Folder(Path.Combine("repo", "src", "app"));
        Assert.Equal(repo, SandboxGrants.Project(sub));
        var loose = Folder("loose");
        Assert.Equal(loose, SandboxGrants.Project(loose));
    }

    [Fact]
    public void THE_PROCESS_TEMP_FOLDER_IS_MADE_AND_A_STALE_SIBLING_IS_SWEPT()
    {
        var parent = Folder("tmp");
        var stale = Directory.CreateDirectory(Path.Combine(parent, "99999999-20200101000000")).FullName;
        File.WriteAllText(Path.Combine(stale, "left.txt"), "x");
        var mine = SandboxGrants.ProcessTemp(parent);
        var other = Directory.CreateDirectory(Path.Combine(parent, "not-a-temp-name")).FullName;

        SandboxGrants.Sweep(parent);

        Assert.StartsWith(Environment.ProcessId + "-", Path.GetFileName(mine));
        Assert.True(Directory.Exists(mine));
        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(other));
    }
}
