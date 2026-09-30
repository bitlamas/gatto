using Gatto.Cli.Setup;
using Gatto.Core.Models;
using Gatto.Terminal;

namespace Gatto.Tests.Setup;

public class LiveProbeRedTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-wbr1-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    [Fact]
    public void An_audition_that_COULD_NOT_RUN_must_never_report_that_the_model_passed()
    {
        //a missing model on disk makes the runner throw, and could-not-run must stay a third outcome distinct from pass and fail
        var check = new LiveSetupProbes(_home, glyphs: GlyphSet.Unicode).RunAudition("no-such-model", repoId: null);

        Assert.Equal(AuditionOutcome.CouldNotRun, check.Outcome);
        //a run that never happened must report no facts, a quant or a speed on that outcome would be invented
        Assert.Null(check.Facts);
    }

    //the check reports the reason the probe composed, this asserts it is non-empty only (the wording belongs to the probe)
    [Fact]
    public void A_CHECK_THAT_COULD_NOT_RUN_CARRIES_THE_REASON_IT_ALREADY_HAD()
    {
        var check = new LiveSetupProbes(_home, glyphs: GlyphSet.Unicode).RunAudition("no-such-model", repoId: null);

        Assert.Equal(AuditionOutcome.CouldNotRun, check.Outcome);
        Assert.NotEmpty(Assert.IsType<CheckStumble.Unavailable>(check.Stumble).Reason);
    }

    //every moment of a check must reach both sinks, a null sink must never silence the other. a sink that reports once then stops would pass an arrival check
    [Fact]
    public void EVERY_MOMENT_OF_ONE_CHECK_REACHES_BOTH_SINKS()
    {
        var a = new Collecting();
        var b = new Collecting();
        var moments = new[]
        {
            Gatto.Roles.Audition.AuditionProgress.Note("starting"),
            Gatto.Roles.Audition.AuditionProgress.Started(Gatto.Roles.Audition.Battery.V1[0], 1, 5),
            Gatto.Roles.Audition.AuditionProgress.Done(Gatto.Roles.Audition.Battery.V1[0], 1, 5, true),
        };

        foreach (var m in moments) LiveSetupProbes.Both(a, b).Report(m);

        Assert.Equal(moments, a.Seen);
        Assert.Equal(moments, b.Seen);

        //one null sink must not block the other sink.
        var alone = new Collecting();
        LiveSetupProbes.Both(null, alone).Report(moments[0]);
        LiveSetupProbes.Both(alone, null).Report(moments[1]);
        Assert.Equal([moments[0], moments[1]], alone.Seen);
    }

    private sealed class Collecting : IProgress<Gatto.Roles.Audition.AuditionProgress>
    {
        public List<Gatto.Roles.Audition.AuditionProgress> Seen { get; } = [];
        public void Report(Gatto.Roles.Audition.AuditionProgress value) => Seen.Add(value);
    }

    [Fact]
    public void THE_PACK_IS_ON_DISK_AND_LOADABLE_BY_THE_TIME_THE_OFFER_RUNS()
    {
        //apply the writes then read the model back, the fake probes shared the flow's misunderstanding so only the real path proves the model loads
        Gatto.Core.Home.GattoHome.EnsureInitialized(_home);
        var gguf = Path.Combine(_home, "tiny.gguf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny.gguf"), gguf);

        var writes = new WriteSet
        {
            CreateModel = new WriteSet.Model(gguf, Port: 1235, Context: 4096),
            LlamaServer = @"C:\llama\llama-server.exe",
        };

        Assert.Null(WriteSetApply.Apply(_home, writes, out var modelId));
        Assert.NotNull(modelId);

        //these are the audition's own first two steps, reading the config and loading the model
        var config = Gatto.Core.Home.GattoConfig.Load(_home);
        Assert.Equal(@"C:\llama\llama-server.exe", config.LlamaServer);
        var model = Gatto.Roles.Model.Load(Path.Combine(_home, "models"), modelId!);
        Assert.Equal(4096, model.Profile.Context);
    }

    [Fact]
    public void The_id_the_offer_uses_is_the_WRITERS_own_never_a_filename_guess()
    {
        //a filename guess diverges from the scaffold's slugging whenever the name needs slugging, and the audition would then load the wrong model or none
        Gatto.Core.Home.GattoHome.EnsureInitialized(_home);
        var gguf = Path.Combine(_home, "Some Model (Q4).gguf");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "tiny.gguf"), gguf);

        Assert.Null(WriteSetApply.Apply(_home,
            new WriteSet { CreateModel = new WriteSet.Model(gguf, 1235, 4096) }, out var modelId));

        Assert.NotEqual(Path.GetFileNameWithoutExtension(gguf), modelId);
        Assert.True(Directory.Exists(Path.Combine(_home, "models", modelId!)));
    }

    //writes a profile pointing at a file of an exact length. the fixture holds only a header, so a whole file has to grow to the floor the header states
    private string WriteProfile(string modelId, byte[] gguf, long lengthBytes)
    {
        var path = Path.Combine(_home, modelId + ".gguf");
        var bytes = new byte[lengthBytes];
        gguf.AsSpan(0, (int)Math.Min(gguf.Length, lengthBytes)).CopyTo(bytes);
        File.WriteAllBytes(path, bytes);
        var dir = Path.Combine(_home, "models", modelId);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "profile.json"),
            $$"""{ "files": [{ "path": {{System.Text.Json.JsonSerializer.Serialize(path)}}, "active": true }], "port": 1235, "context": 4096 }""");
        return path;
    }

    //the probe files a truncated model before it asks for a server. the outcome stays CouldNotRun with an Incomplete stumble, which proves no spawn was requested
    [Fact]
    public void A_TRUNCATED_MODEL_FILE_IS_FILED_BEFORE_ANY_SERVER_IS_ASKED_FOR()
    {
        var whole = GgufTestBytes.WithTensors([("a", [256], 0u), ("b", [512], 8u)]);
        var least = GgufHeaderParser.Parse(new MemoryStream(whole)).Tensors!.MinimumFileBytes;
        var path = WriteProfile("truncated", whole, least - 1);

        var check = new LiveSetupProbes(_home, glyphs: GlyphSet.Unicode).RunAudition("truncated", repoId: null);

        Assert.Equal(AuditionOutcome.CouldNotRun, check.Outcome);
        var filed = Assert.IsType<CheckStumble.Incomplete>(check.Stumble);
        Assert.Equal(Path.GetFileName(path), filed.File);
        //the folder comes with a trailing separator (the row reads "in <folder>")
        Assert.Equal(_home + Path.DirectorySeparatorChar, filed.Folder);
        Assert.Equal(least - 1, filed.FoundBytes);
        //the expected size is quoted from the header, a second sum here could disagree with what the screen shows
        Assert.Equal(least, filed.ExpectedBytes);
        Assert.True(filed.FoundBytes < filed.ExpectedBytes, "the fixture is not actually short");
    }

    //a whole file must not be filed, a hard-wired yes would pass every check without this case
    [Fact]
    public void A_WHOLE_MODEL_FILE_IS_NOT_FILED()
    {
        var whole = GgufTestBytes.WithTensors([("a", [256], 0u), ("b", [512], 8u)]);
        var least = GgufHeaderParser.Parse(new MemoryStream(whole)).Tensors!.MinimumFileBytes;
        WriteProfile("complete", whole, least);

        var check = new LiveSetupProbes(_home, glyphs: GlyphSet.Unicode).RunAudition("complete", repoId: null);

        Assert.Equal(AuditionOutcome.CouldNotRun, check.Outcome);   //the missing engine is what fails this run
        Assert.IsNotType<CheckStumble.Incomplete>(check.Stumble);
    }
    //the runner's stumble mapping must hold at its producer, both kinds are asserted, a mapping that answers one for both passes either alone
    [Fact]
    public void THE_RUNNERS_STUMBLE_BECOMES_THE_WIZARDS_BOTH_WAYS()
    {
        var died = new Gatto.Roles.Audition.AuditionRunner.AuditionStumbleException(
            "exited", Gatto.Roles.Audition.AuditionStumbleKind.ExitedWhileLoading, ["boom"]);
        var stopped = new Gatto.Roles.Audition.AuditionRunner.AuditionStumbleException(
            "stopped", Gatto.Roles.Audition.AuditionStumbleKind.StoppedMidCheck, []);

        var exited = Assert.IsType<CheckStumble.Exited>(LiveSetupProbes.StumbleFrom(died, _home));
        Assert.Equal(["boom"], exited.LastLines);
        //the log path comes from ServeManager, this file doesn't spell it again
        Assert.Equal(Gatto.Roles.ServeManager.LogPathFor(_home), exited.LogPath);

        Assert.Empty(Assert.IsType<CheckStumble.Stopped>(
            LiveSetupProbes.StumbleFrom(stopped, _home)).LastLines);
    }
}
