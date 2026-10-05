namespace Gatto.Tests;

public sealed class TestTempRootTests
{
    //a test's temp folder lands under this run's folder, which the next run deletes, so a test that skips its own cleanup leaves nothing in the user's temp
    [Fact]
    public void A_temp_folder_a_test_makes_sits_under_the_runs_folder()
    {
        var root = TestTempRoot.Root;
        Assert.NotNull(root);
        Assert.StartsWith(TestTempRoot.Prefix, Path.GetFileName(root), StringComparison.Ordinal);

        var made = Directory.CreateTempSubdirectory("gatto-roottest-");
        try
        {
            Assert.Equal(Path.GetFullPath(root!), Path.GetFullPath(made.Parent!.FullName));
            Assert.StartsWith(Path.GetFullPath(root!), Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase);
        }
        finally { made.Delete(true); }
    }

    //a finished run's folder goes with its read-only git objects, a live run's folder stays
    [Fact]
    public void A_folder_left_by_a_finished_run_is_swept_and_a_live_one_is_kept()
    {
        var parent = Directory.CreateTempSubdirectory("gatto-sweep-").FullName;
        try
        {
            var gone = Directory.CreateDirectory(Path.Combine(parent, TestTempRoot.Prefix + int.MaxValue)).FullName;
            var live = Directory.CreateDirectory(Path.Combine(parent, TestTempRoot.Prefix + Environment.ProcessId)).FullName;
            var objects = Directory.CreateDirectory(Path.Combine(gone, "repo", ".git", "objects", "9e")).FullName;
            var readOnly = Path.Combine(objects, "26dfeeb6");
            File.WriteAllText(readOnly, "x");
            File.SetAttributes(readOnly, FileAttributes.ReadOnly);

            TestTempRoot.SweepFinishedRuns(parent);

            Assert.False(Directory.Exists(gone));
            Assert.True(Directory.Exists(live));
        }
        finally { Directory.Delete(parent, true); }
    }

    //pid 4 is the system process, which this user cannot open, so its folder counts as live and the sweep goes on instead of failing the run
    [Fact]
    public void A_folder_whose_pid_cannot_be_opened_is_kept_and_the_sweep_does_not_throw()
    {
        var parent = Directory.CreateTempSubdirectory("gatto-sweep-").FullName;
        try
        {
            var locked = Directory.CreateDirectory(Path.Combine(parent, TestTempRoot.Prefix + 4)).FullName;
            var gone = Directory.CreateDirectory(Path.Combine(parent, TestTempRoot.Prefix + int.MaxValue)).FullName;

            TestTempRoot.SweepFinishedRuns(parent);

            //an elevated runner may open pid 4 and never reach the catch, so the kept folder is asserted only where this user cannot open it
            if (Pid4IsUnopenable()) Assert.True(Directory.Exists(locked));
            Assert.False(Directory.Exists(gone));
        }
        finally { Directory.Delete(parent, true); }
    }

    private static bool Pid4IsUnopenable()
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(4); _ = p.HasExited; return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
        catch (Exception) { return false; }
    }
}
