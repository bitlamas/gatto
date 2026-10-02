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
}
