using Gatto.Cli;
using Gatto.Core.Session;

namespace Gatto.Tests.Cli;

//the sweep deletes a transcript last written before the date with every file of its stem, and an image no remaining file names
public sealed class SessionsCommandTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-sweep-").FullName;
    private readonly StringWriter _out = new();

    public void Dispose() { try { Directory.Delete(_home, true); } catch (Exception) { } }

    private string Sessions => Path.Combine(_home, "sessions");
    private string Blobs => Path.Combine(_home, "blobs");

    private static readonly DateTime Old = new(2026, 3, 1, 12, 0, 0);
    private static readonly DateTime New = new(2026, 6, 1, 12, 0, 0);
    private static readonly DateTime Cut = new(2026, 5, 1);

    private const string OldStem = "0123456789ab-0639000000000000001";
    private const string NewStem = "0123456789ab-0639000000000000002";
    private static readonly string KeptBlob = new string('a', 64) + ".png";
    private static readonly string LoneBlob = new string('b', 64) + ".png";
    private static readonly string OldOnlyBlob = new string('c', 64) + ".jpg";

    private string Plant(string folder, string name, string text, DateTime written)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        File.SetLastWriteTime(path, written);
        return path;
    }

    //an old session with both sidecars naming one image, a new session naming another, and an image nothing names
    private void PlantHome()
    {
        Plant(Sessions, OldStem + ".jsonl", Record(OldOnlyBlob), Old);
        Plant(Sessions, OldStem + ".ledger.jsonl", "{}", Old);
        Plant(Sessions, OldStem + ".trace.jsonl", "{}", New);
        Plant(Sessions, NewStem + ".jsonl", Record(KeptBlob), New);
        Plant(Blobs, KeptBlob, "png", Old);
        Plant(Blobs, LoneBlob, "png", Old);
        Plant(Blobs, OldOnlyBlob, "jpg", Old);
        Plant(Blobs, "notes.txt", "mine", Old);
    }

    //the image path as the transcript writes it, a json-escaped absolute path
    private string Record(string blob) =>
        "{\"role\":\"user\",\"images\":[{\"path\":\"" + Path.Combine(Blobs, blob).Replace("\\", "\\\\") + "\",\"bytes\":3}]}\n";

    private CommandContext Ctx(bool interactive) => new(_out, _home, interactive, Theme: null);

    private static ParsedArgs Sweep(string? date, bool yes = false) =>
        new("sessions", null, null, null, false, yes, "sweep", date);

    [Fact]
    public void THE_PLAN_TAKES_THE_OLD_STEM_WHOLE_AND_EVERY_IMAGE_NO_REMAINING_FILE_NAMES()
    {
        PlantHome();
        var plan = SessionSweep.Plan(_home, Cut);

        Assert.Equal(1, plan.Transcripts);
        Assert.Equal(
            new[] { OldStem + ".jsonl", OldStem + ".ledger.jsonl", OldStem + ".trace.jsonl" }.OrderBy(n => n),
            plan.SessionFiles.Select(Path.GetFileName).OrderBy(n => n));
        Assert.Equal(new[] { LoneBlob, OldOnlyBlob }.OrderBy(n => n), plan.Blobs.Select(Path.GetFileName).OrderBy(n => n));
    }

    [Fact]
    public async Task YES_DELETES_WITHOUT_ASKING_AND_LEAVES_THE_REST()
    {
        PlantHome();
        var exit = await SessionsCommand.RunAsync(Sweep("2026-05-01", yes: true), Ctx(interactive: false),
            ask: (_, _) => throw new InvalidOperationException("--yes must not ask"));

        Assert.Equal(0, exit);
        Assert.Equal(new[] { NewStem + ".jsonl" }, Directory.GetFiles(Sessions).Select(Path.GetFileName));
        Assert.Equal(new[] { KeptBlob, "notes.txt" }.OrderBy(n => n), Directory.GetFiles(Blobs).Select(Path.GetFileName).OrderBy(n => n));
        Assert.Contains("1 transcript last written before 2026-05-01", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("2 images named by no remaining session", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("deleted 1 transcript and 2 images", _out.ToString(), StringComparison.Ordinal);
    }

    //a run with no terminal and no --yes reports what would go and deletes nothing
    [Fact]
    public async Task WITHOUT_A_TERMINAL_OR_YES_NOTHING_IS_DELETED()
    {
        PlantHome();
        var exit = await SessionsCommand.RunAsync(Sweep("2026-05-01"), Ctx(interactive: false),
            ask: (_, _) => throw new InvalidOperationException("a run with no terminal must not ask"));

        Assert.Equal(0, exit);
        Assert.Equal(4, Directory.GetFiles(Sessions).Length);
        Assert.Equal(4, Directory.GetFiles(Blobs).Length);
        Assert.Contains("nothing was deleted: without a terminal the sweep deletes only with --yes", _out.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public async Task KEEP_OR_ESC_AT_THE_QUESTION_DELETES_NOTHING(int? answer)
    {
        PlantHome();
        string? asked = null;
        var exit = await SessionsCommand.RunAsync(Sweep("2026-05-01"), Ctx(interactive: true),
            ask: (q, options) => { asked = q + " " + string.Join(" / ", options); return answer; });

        Assert.Equal(0, exit);
        Assert.Equal("Delete them? No, keep them / Yes, delete them", asked);
        Assert.Equal(4, Directory.GetFiles(Sessions).Length);
        Assert.Contains("nothing was deleted", _out.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task YES_AT_THE_QUESTION_DELETES()
    {
        PlantHome();
        var exit = await SessionsCommand.RunAsync(Sweep("2026-05-01"), Ctx(interactive: true), ask: (_, _) => 1);

        Assert.Equal(0, exit);
        Assert.Single(Directory.GetFiles(Sessions));
    }

    //nothing older than the date and every image named, so there is nothing to ask about
    [Fact]
    public async Task NOTHING_TO_DELETE_NEVER_ASKS()
    {
        Plant(Sessions, NewStem + ".jsonl", Record(KeptBlob), New);
        Plant(Blobs, KeptBlob, "png", Old);
        var exit = await SessionsCommand.RunAsync(Sweep("2026-05-01"), Ctx(interactive: true),
            ask: (_, _) => throw new InvalidOperationException("nothing to delete must not ask"));

        Assert.Equal(0, exit);
        Assert.Contains("no transcripts last written before 2026-05-01", _out.ToString(), StringComparison.Ordinal);
        Assert.Contains("no images left without a session", _out.ToString(), StringComparison.Ordinal);
    }

    //a home with no sessions or blobs folder is an empty sweep, not an error
    [Fact]
    public void AN_EMPTY_HOME_SWEEPS_NOTHING()
    {
        var plan = SessionSweep.Plan(_home, Cut);
        Assert.Equal(0, plan.Transcripts);
        Assert.Empty(plan.SessionFiles);
        Assert.Empty(plan.Blobs);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2026-13-01")]
    [InlineData("01-05-2026")]
    [InlineData("30d")]
    public async Task A_MISSING_OR_MALFORMED_DATE_IS_A_USAGE_ERROR_AND_DELETES_NOTHING(string? date)
    {
        PlantHome();
        Assert.Equal(2, await SessionsCommand.RunAsync(Sweep(date, yes: true), Ctx(interactive: false)));
        Assert.Equal(4, Directory.GetFiles(Sessions).Length);
    }

    [Fact]
    public async Task ANOTHER_SUBCOMMAND_IS_A_USAGE_ERROR()
    {
        PlantHome();
        var args = new ParsedArgs("sessions", null, null, null, false, true, "purge", "2026-05-01");
        Assert.Equal(2, await SessionsCommand.RunAsync(args, Ctx(interactive: false)));
        Assert.Equal(4, Directory.GetFiles(Sessions).Length);
    }

    [Fact]
    public void THE_ROUTER_READS_THE_DATE_AND_YES()
    {
        var args = ArgRouter.Parse(["sessions", "sweep", "2026-05-01", "--yes"]);
        Assert.Equal(("sessions", "sweep", "2026-05-01", true), (args.Command, args.Subcommand, args.Target, args.Yes));
    }

    //setup falls through to a launch that reads yes as tool approval, so no other reserved command takes it
    [Fact]
    public void NO_OTHER_RESERVED_COMMAND_TAKES_YES()
    {
        Assert.False(ArgRouter.Parse(["setup", "--yes"]).Yes);
        Assert.False(ArgRouter.Parse(["update", "--yes"]).Yes);
    }

    //a file that cannot be deleted is named with its reason and costs only itself
    [Fact]
    public void A_HELD_FILE_FAILS_ALONE()
    {
        PlantHome();
        var plan = SessionSweep.Plan(_home, Cut);
        var held = plan.SessionFiles.First();
        IReadOnlyList<(string Path, string Why)> failures;
        using (new FileStream(held, FileMode.Open, FileAccess.Read, FileShare.Read))
            failures = SessionSweep.Delete(plan);

        Assert.Equal(held, Assert.Single(failures).Path);
        Assert.True(File.Exists(held));
        Assert.Equal(2, Directory.GetFiles(Sessions).Length);
    }
}
