using Gatto.Core.Hardware;

namespace Gatto.Tests.Fakes;

//one loader turns the real probe reports into snapshots, so two test readers can't disagree about the corpus
internal static class HwFixture
{
    //parse the fixture files through ParseReport, so the files stay the source of truth. the visible figure must be at least a gigabyte, or it was a parse failure
    public static HardwareSnapshot Fixture(string name)
    {
        var text = File.ReadAllText(
            //anchor on AppContext.BaseDirectory, other tests change the process working directory in parallel
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "hwprobe", name));
        var reading = HardwareProbe.ParseReport(text);
        Assert.NotNull(reading.Snapshot);   //fail here when the fixture lacks a required line, so nothing fails later as an empty reading
        Assert.True(reading.Snapshot!.OsVisibleBytes >= 1UL << 30,
            $"{name}: visible parsed as {reading.Snapshot.OsVisibleBytes} bytes — that is a parse failure, not a machine");
        return reading.Snapshot;
    }
}
