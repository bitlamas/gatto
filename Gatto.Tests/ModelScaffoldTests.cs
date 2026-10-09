using System.Text.Json;
using Gatto.Core.Home;
using Gatto.Roles;

namespace Gatto.Tests;

public class ModelScaffoldTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gatto-scaffold-" + Guid.NewGuid().ToString("N"));
    public ModelScaffoldTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    //resolve fixtures from AppContext.BaseDirectory, since a parallel test can change the process cwd and a relative path then fails at random
    private static string Fixture(string n) => Path.Combine(AppContext.BaseDirectory, "Fixtures", n);

    [Fact]
    public void Create_WritesTheLlamaServerOverride_whenOneIsGiven()
    {
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235,
            llamaServer: @"C:\forks\bailing\llama-server.exe");

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, id, "profile.json")));
        Assert.Equal(@"C:\forks\bailing\llama-server.exe",
            doc.RootElement.GetProperty("llama_server").GetString());

        //the written key must be one Model.Load accepts, since a json check alone passes on a model that never loads
        Assert.Equal(@"C:\forks\bailing\llama-server.exe",
            Model.Load(_dir, id).Profile.LlamaServer);
    }

    //every flag here reaches the pinned llama-server as argv, and a flag the release dropped stops the server at start
    [Fact]
    public void THE_SCAFFOLDED_EXTRA_ARGS_ARE_FLAGS_THE_PINNED_RELEASE_ACCEPTS()
    {
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);

        Assert.Equal(["-np", "1", "-fa", "on", "-lm", "none", "--jinja", "--no-context-shift", "--cache-reuse", "0"],
            Model.Load(_dir, id).Profile.ExtraArgs);
    }

    //a pinned model ends its arguments on the device llama-server named, so the server runs where the fit counted
    [Fact]
    public void A_PINNED_MODEL_ENDS_ITS_ARGUMENTS_ON_THE_DEVICE()
    {
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235, device: "Vulkan0");

        Assert.Equal(["-np", "1", "-fa", "on", "-lm", "none", "--jinja", "--no-context-shift", "--cache-reuse", "0", "-dev", "Vulkan0"],
            Model.Load(_dir, id).Profile.ExtraArgs);
    }

    [Fact]
    public void Create_OMITS_THE_KEY_when_no_override_is_given()
    {
        //omit the key when there is no override, since an empty string asks ServeManager to spawn nothing and old profiles must stay byte-identical
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, id, "profile.json")));
        Assert.False(doc.RootElement.TryGetProperty("llama_server", out _));
    }

    [Fact]
    public void Create_WritesATuningNotesSiblingWithAllFiveFlags()
    {
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);
        var notes = File.ReadAllText(Path.Combine(_dir, id, "tuning.txt"));

        Assert.Contains("gpu_layers", notes);
        Assert.Contains("Flash attention", notes);
        Assert.Contains("cache_type_k", notes);
        Assert.Contains("Batch sizes", notes);
        Assert.Contains("MoE expert placement", notes);
    }

    [Fact]
    public void EVERY_CHARACTER_OF_A_GENERATED_TUNING_FILE_IS_ASCII()
    {
        //generated notes must be pure ASCII (the type command and powershell decode through the ANSI code page). non-ascii punctuation shows as garbage there
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);
        var notes = File.ReadAllText(Path.Combine(_dir, id, "tuning.txt"));

        var offenders = notes
            .Select((ch, i) => (ch, i))
            .Where(x => x.ch >= 0x80)
            .Select(x => $"U+{(int)x.ch:X4} at {x.i}: …{notes[Math.Max(0, x.i - 30)..Math.Min(notes.Length, x.i + 10)]}…")
            .ToList();

        Assert.True(offenders.Count == 0,
            "a generated tuning.txt must be pure ASCII — PS 5.1 reads it through the ANSI codepage:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Create_WritesAModelThatModel_Load_ACCEPTS()   //a write-only check is not an oracle. the profile must load.
    {
        //loading is the oracle, since it throws on any unknown key that a written-file check cannot see. the notes file stays a sibling found by convention
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);

        var model = Model.Load(_dir, id);

        Assert.Equal(1235, model.Profile.Port);
        Assert.True(File.Exists(Path.Combine(_dir, id, "tuning.txt")), "the sibling must exist...");
        Assert.DoesNotContain("tuning", File.ReadAllText(Path.Combine(_dir, id, "profile.json")));
    }

    [Fact]
    public void Create_CapsADerivedIdFromAnAbsurdModelName()
    {
        //the general.name field holds attacker text up to a 1 mb bound. without a cap the derived id makes a path too long
        var gguf = Path.Combine(_dir, "huge-name.gguf");
        File.WriteAllBytes(gguf, GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "qwen3");
            kv.Str("general.name", new string('n', 1_000_000));
            kv.U32("qwen3.context_length", 4096);
        }));

        var id = ModelScaffold.Create(_dir, gguf, port: 1235);

        Assert.True(id.Length <= 64, $"derived id must be a usable directory name, got {id.Length} chars");
        Assert.True(Directory.Exists(Path.Combine(_dir, id)));
    }

    [Fact]
    public void Create_WritesProfileWithDerivedContextAndBoilerplate()
    {
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);

        var root = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_dir, id, "profile.json"))).RootElement;

        Assert.Equal(131072, root.GetProperty("context").GetInt32());
        Assert.Equal(1235, root.GetProperty("port").GetInt32());
        Assert.Equal(99, root.GetProperty("gpu_layers").GetInt32());
        Assert.Equal("q8_0", root.GetProperty("cache_type_k").GetString());
        Assert.Equal("q8_0", root.GetProperty("cache_type_v").GetString());
        Assert.Contains("--jinja", root.GetProperty("extra_args").EnumerateArray().Select(e => e.GetString()));
    }

    //sampling and thinking come from the model card, so both keys stay out of the profile. a guess there is worse than the llama.cpp default
    [Fact]
    public void Create_OmitsSamplingAndThinking()
    {
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);
        var root = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_dir, id, "profile.json"))).RootElement;

        Assert.False(root.TryGetProperty("sampling", out _));
        Assert.False(root.TryGetProperty("thinking", out _));
    }

    [Fact]
    public void Create_ProducesAModelThatModelLoadAccepts()
    {
        //the contract is a round trip, so Model.Load must accept what Create writes
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);
        var model = Model.Load(_dir, id);
        Assert.Equal(131072, model.Profile.Context);
    }

    [Fact]
    public void Create_RefusesToOverwriteAnExistingModel()
    {
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);
        var ex = Assert.Throws<GattoConfigException>(
            () => ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235));
        Assert.Contains(id, ex.Message);
        Assert.Contains("already exists", ex.Message);
    }

    [Fact]
    public void Create_MultiShard_RecordsShardOne()
    {
        var shard = Path.Combine(_dir, "big-model-00001-of-00003.gguf");
        File.Copy(Fixture("tiny.gguf"), shard);
        var id = ModelScaffold.Create(_dir, shard, port: 1235);
        var model = Model.Load(_dir, id);
        Assert.EndsWith("00001-of-00003.gguf", model.Profile.ActivePath);
    }

    [Fact]
    public void Create_NoContextInGguf_OmitsTheKeyRatherThanWritingZero()
    {
        var id = ModelScaffold.Create(_dir, Fixture("tiny-noctx.gguf"), port: 1235);
        var root = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_dir, id, "profile.json"))).RootElement;
        Assert.False(root.TryGetProperty("context", out _));   //the key is left out when the gguf has none, since Model.Load demands it and a missing key fails loudly
    }

    [Fact]
    public void Create_WithExplicitId_UsesItVerbatim()
    {
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235, id: "qwen3.6-27b");

        Assert.Equal("qwen3.6-27b", id);
        Assert.True(Directory.Exists(Path.Combine(_dir, "qwen3.6-27b")));
    }

    [Fact]
    public void Create_WithoutExplicitId_StillFallsBackToDerivedId()
    {
        //the unattended scan has no human to supply a name, so the derived id path must keep working.
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);
        Assert.False(string.IsNullOrWhiteSpace(id));
        Assert.True(Directory.Exists(Path.Combine(_dir, id)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("a:b")]
    [InlineData("con*")]
    [InlineData("..")]
    public void Create_WithPathHostileId_ThrowsAFriendlyConfigException(string badId)
    {
        //validation must precede creation, so a rejected id leaves no stray model. the count of directories is the check, since hostile ids collapse onto _dir
        var before = Directory.GetDirectories(_dir).Length;
        var ex = Assert.Throws<GattoConfigException>(
            () => ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235, id: badId));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
        Assert.Equal(before, Directory.GetDirectories(_dir).Length);
    }

    //these exercise the real resolution path: a middle shard resolving down to shard one.

    [Fact]
    public void Create_MiddleShard_ResolvesToShardOneWhenItExists()
    {
        var shard1 = Path.Combine(_dir, "big-model-00001-of-00003.gguf");
        var shard2 = Path.Combine(_dir, "big-model-00002-of-00003.gguf");
        File.Copy(Fixture("tiny.gguf"), shard1);
        File.Copy(Fixture("tiny.gguf"), shard2);

        var id = ModelScaffold.Create(_dir, shard2, port: 1235, id: "shard-mid");
        var model = Model.Load(_dir, id);

        Assert.EndsWith("00001-of-00003.gguf", model.Profile.ActivePath);
        Assert.DoesNotContain("00002-of-00003.gguf", model.Profile.ActivePath);
    }

    [Fact]
    public void Create_MiddleShard_FallsBackToGivenPathWhenShardOneIsMissing()
    {
        //shard one is absent on purpose. the path is rewritten only when shard one exists, and otherwise it stays the given shard.
        var shard2 = Path.Combine(_dir, "big-model-00002-of-00003.gguf");
        File.Copy(Fixture("tiny.gguf"), shard2);

        var id = ModelScaffold.Create(_dir, shard2, port: 1235, id: "shard-missing-1");
        var model = Model.Load(_dir, id);

        Assert.EndsWith("00002-of-00003.gguf", model.Profile.ActivePath);
    }

    [Fact]
    public void Create_NoShardSuffix_PathIsUntouched()
    {
        var plain = Path.Combine(_dir, "solo-model.gguf");
        File.Copy(Fixture("tiny.gguf"), plain);

        var id = ModelScaffold.Create(_dir, plain, port: 1235, id: "solo-model");
        var model = Model.Load(_dir, id);

        Assert.Equal(Path.GetFullPath(plain), model.Profile.ActivePath);
    }

    [Fact]
    public void Create_FilenameWithDigitsButNoShardPattern_DoesNotMisfire()
    {
        //digits in a name must not fire the shard pattern, which needs the -NNNNN-of-NNNNN.gguf suffix exactly
        var named = Path.Combine(_dir, "qwen3.6-27b-q4_k_m.gguf");
        File.Copy(Fixture("tiny.gguf"), named);

        var id = ModelScaffold.Create(_dir, named, port: 1235, id: "qwen-digits-test");
        var model = Model.Load(_dir, id);

        Assert.Equal(Path.GetFullPath(named), model.Profile.ActivePath);
    }

    [Fact]
    public void Create_ShardLikeNumberWithoutOfPart_DoesNotMisfire()
    {
        //a five-digit run without the -of- part is not a shard marker
        var named = Path.Combine(_dir, "model-00001.gguf");
        File.Copy(Fixture("tiny.gguf"), named);

        var id = ModelScaffold.Create(_dir, named, port: 1235, id: "model-00001-test");
        var model = Model.Load(_dir, id);

        Assert.Equal(Path.GetFullPath(named), model.Profile.ActivePath);
    }

    [Fact]
    public void Create_OverShardThree_ResolvesExactlyWhatDiscoveryReports()
    {
        //the shard rule must match case-insensitively, exactly as discovery does. the uppercase fixture is what makes that assertion bite
        foreach (var n in new[] { "big-model-00001-of-00003.GGUF", "big-model-00002-of-00003.GGUF",
                                  "big-model-00003-of-00003.GGUF" })
            File.Copy(Fixture("tiny.gguf"), Path.Combine(_dir, n));
        var shard3 = Path.Combine(_dir, "big-model-00003-of-00003.GGUF");

        var model = Model.Load(_dir, ModelScaffold.Create(_dir, shard3, port: 1235, id: "shard-three"));

        //the model must name shard one, the file llama.cpp is pointed at. this assertion goes red when two sites disagree about the convention.
        Assert.EndsWith("-00001-of-00003.gguf", model.Profile.ActivePath, StringComparison.OrdinalIgnoreCase);
        //this asserts the same answer discovery gives, so a second private copy of the rule would fail it
        Assert.Equal(Gatto.Core.Acquire.ModelDiscovery.ShardSiblings(Path.GetFullPath(shard3))[0],
                     model.Profile.ActivePath);
    }

    [Fact]
    public void FindUnconfigured_ListsGgufsWithNoModelAndSkipsConfiguredOnes()
    {
        var weights = Path.Combine(_dir, "weights");
        var models = Path.Combine(_dir, "models");
        Directory.CreateDirectory(weights);
        Directory.CreateDirectory(models);

        File.Copy(Fixture("tiny.gguf"), Path.Combine(weights, "alpha.gguf"));
        File.Copy(Fixture("tiny.gguf"), Path.Combine(weights, "beta.gguf"));
        ModelScaffold.Create(models, Path.Combine(weights, "alpha.gguf"), 1235);

        var found = ModelScaffold.FindUnconfigured(weights, models);

        Assert.Contains(found, p => p.EndsWith("beta.gguf", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, p => p.EndsWith("alpha.gguf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindUnconfigured_MultiShard_ListsOnlyShardOne()
    {
        var weights = Path.Combine(_dir, "models2");
        var models = Path.Combine(_dir, "models2");
        Directory.CreateDirectory(weights);
        Directory.CreateDirectory(models);
        foreach (var n in new[] { "big-00001-of-00003.gguf", "big-00002-of-00003.gguf", "big-00003-of-00003.gguf" })
            File.Copy(Fixture("tiny.gguf"), Path.Combine(weights, n));

        var found = ModelScaffold.FindUnconfigured(weights, models);

        Assert.Single(found);
        Assert.EndsWith("big-00001-of-00003.gguf", found[0]);
    }

    [Fact]
    public void FindUnconfigured_MissingDir_IsEmptyNotAThrow()
    {
        Assert.Empty(ModelScaffold.FindUnconfigured(Path.Combine(_dir, "nope"), _dir));
    }

    [Fact]
    public void FindUnconfigured_ExcludesEmbeddingModels_ByPoolingType()
    {
        //an embedding gguf declares a real context length, so the context check alone misses it. the pooling_type key is what excludes it from the scan
        var weights = Path.Combine(_dir, "weights-embed");
        var models = Path.Combine(_dir, "models-embed");
        Directory.CreateDirectory(weights);
        Directory.CreateDirectory(models);

        File.WriteAllBytes(Path.Combine(weights, "bge-m3-FP16.gguf"), TinyEmbeddingGguf());
        File.Copy(Fixture("tiny.gguf"), Path.Combine(weights, "chat-model.gguf"));

        var found = ModelScaffold.FindUnconfigured(weights, models);

        Assert.Contains(found, p => p.EndsWith("chat-model.gguf", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(found, p => p.EndsWith("bge-m3-FP16.gguf", StringComparison.OrdinalIgnoreCase));
    }

    //build a minimal valid gguf v3 header with the bert arch, a real context length, and the pooling_type key generative weights never have
    private static byte[] TinyEmbeddingGguf()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        //the tensor count is real so the file counts as a model at all, and this fixture tests the pooling_type key
        w.Write(0x46554747U); w.Write(3U); w.Write(389UL); w.Write(3UL);  //gguf magic, version 3, the tensor count, then three key-value pairs.

        WriteKv(w, "general.architecture"); w.Write(8U); WriteGgufString(w, "bert");
        WriteKv(w, "bert.context_length"); w.Write(4U); w.Write(8192U);
        WriteKv(w, "bert.pooling_type"); w.Write(4U); w.Write(2U);
        w.Flush();
        return ms.ToArray();

        static void WriteKv(BinaryWriter w, string key)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(key);
            w.Write((ulong)b.Length); w.Write(b);
        }
        static void WriteGgufString(BinaryWriter w, string s)
        {
            var b = System.Text.Encoding.UTF8.GetBytes(s);
            w.Write((ulong)b.Length); w.Write(b);
        }
    }

    [Fact]
    public void FindUnconfigured_ExcludesGgufWithNoContextLength()
    {
        //a gguf without context_length is the shape of a multimodal projector. it can never yield a valid model, because Model.Load requires context
        var weights = Path.Combine(_dir, "models3");
        var models = Path.Combine(_dir, "models3");
        Directory.CreateDirectory(weights);
        Directory.CreateDirectory(models);

        File.Copy(Fixture("tiny-noctx.gguf"), Path.Combine(weights, "mmproj-model.gguf"));

        var found = ModelScaffold.FindUnconfigured(weights, models);

        Assert.Empty(found);
    }

    [Fact]
    public void FindUnconfigured_IncludesGgufWithContextLength()
    {
        var weights = Path.Combine(_dir, "models4");
        var models = Path.Combine(_dir, "models4");
        Directory.CreateDirectory(weights);
        Directory.CreateDirectory(models);

        File.Copy(Fixture("tiny.gguf"), Path.Combine(weights, "servable-model.gguf"));

        var found = ModelScaffold.FindUnconfigured(weights, models);

        Assert.Contains(found, p => p.EndsWith("servable-model.gguf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindUnconfigured_GarbageFile_IsSkippedWithoutThrowingAndDoesNotHideGoodFiles()
    {
        var weights = Path.Combine(_dir, "models5");
        var models = Path.Combine(_dir, "models5");
        Directory.CreateDirectory(weights);
        Directory.CreateDirectory(models);

        File.WriteAllBytes(Path.Combine(weights, "garbage.gguf"), [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07]);
        File.Copy(Fixture("tiny.gguf"), Path.Combine(weights, "good-model.gguf"));

        var found = ModelScaffold.FindUnconfigured(weights, models);

        Assert.DoesNotContain(found, p => p.EndsWith("garbage.gguf", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(found, p => p.EndsWith("good-model.gguf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FindUnconfigured_LockedGgufFile_IsSkippedAndDoesNotHideGoodFiles()
    {
        var weights = Path.Combine(_dir, "weights-locked");
        var models = Path.Combine(_dir, "models-locked");
        Directory.CreateDirectory(weights);
        Directory.CreateDirectory(models);

        var lockedFile = Path.Combine(weights, "locked.gguf");
        var goodFile = Path.Combine(weights, "good-model.gguf");

        File.Copy(Fixture("tiny.gguf"), lockedFile);
        File.Copy(Fixture("tiny.gguf"), goodFile);

        using (var fs = File.Open(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            //a locked file must be skipped without hiding the readable ones.
            var found = ModelScaffold.FindUnconfigured(weights, models);

            Assert.DoesNotContain(found, p => p.EndsWith("locked.gguf", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(found, p => p.EndsWith("good-model.gguf", StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void FindUnconfigured_NestedDirectory_RecursivelyWalks()
    {
        var weights = Path.Combine(_dir, "weights-nested");
        var models = Path.Combine(_dir, "models-nested");
        Directory.CreateDirectory(weights);
        Directory.CreateDirectory(models);
        Directory.CreateDirectory(Path.Combine(weights, "deep", "nested", "weights"));

        File.Copy(Fixture("tiny.gguf"), Path.Combine(weights, "root.gguf"));
        File.Copy(Fixture("tiny.gguf"), Path.Combine(weights, "deep", "nested", "weights", "nested.gguf"));

        var found = ModelScaffold.FindUnconfigured(weights, models);

        Assert.Equal(2, found.Count);
        Assert.Contains(found, p => p.EndsWith("root.gguf", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(found, p => p.EndsWith("nested.gguf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_SCAFFOLDED_PACK_TURNS_OFF_THE_SERVERS_OWN_CONTEXT_MANAGEMENT()
    {
        //gatto owns the context window, so the server must not shift it underneath. a second manager would mutate the window and hide the boundary errors gatto reads.
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);
        var args = ExtraArgs(Path.Combine(_dir, id, "profile.json"));

        Assert.Contains("--no-context-shift", args);
        //the flag and its value must stay together, since a lone 0 reaches the server as a stray positional and cache reuse stays on
        var i = args.IndexOf("--cache-reuse");
        Assert.True(i >= 0, "the cache-reuse flag is missing");
        Assert.Equal("0", args[i + 1]);
    }

    private static List<string> ExtraArgs(string profilePath)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(profilePath));
        return [.. doc.RootElement.GetProperty("extra_args").EnumerateArray().Select(e => e.GetString()!)];
    }

    //a copy of the fixture under another file name, since the derived id comes from the model and both then want the same name
    private string SecondCopy()
    {
        var copy = Path.Combine(_dir, "elsewhere.gguf");
        File.Copy(Fixture("tiny.gguf"), copy);
        return copy;
    }

    [Fact]
    public void CollidingModel_NAMES_THE_PACK_when_a_DIFFERENT_file_already_holds_the_id()
    {
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);

        Assert.Equal(id, ModelScaffold.CollidingModel(_dir, SecondCopy()));
    }

    [Fact]
    public void CollidingModel_IS_SILENT_for_the_SAME_file_because_that_is_ruling_Bs_reuse()
    {
        //the same file is the reuse question, the same name is the collision question. one probe answering both turns an idempotent re-run into a destructive prompt.
        ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);

        Assert.Null(ModelScaffold.CollidingModel(_dir, Fixture("tiny.gguf")));
        Assert.NotNull(ModelScaffold.ModelFor(_dir, Fixture("tiny.gguf")));
    }

    [Fact]
    public void CollidingModel_IS_SILENT_when_the_name_is_free()
    {
        Assert.Null(ModelScaffold.CollidingModel(_dir, Fixture("tiny.gguf")));
    }

    [Fact]
    public void Create_STILL_THROWS_on_a_taken_name_unless_replace_was_asked_for()
    {
        //the replace flag must default to off, so model new and unattended callers keep refusing. only a confirmed wizard answer sets it
        ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);

        Assert.Throws<GattoConfigException>(() => ModelScaffold.Create(_dir, SecondCopy(), port: 1235));
    }

    [Fact]
    public void Create_WITH_REPLACE_rewrites_the_model_for_the_new_file_and_leaves_nothing_stale()
    {
        var id = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235);
        //this file is per-model material beside the profile. it is what a kept directory would strand.
        var stale = Path.Combine(_dir, id, "system-append.md");
        File.WriteAllText(stale, "tuned for the OLD model");
        var second = SecondCopy();

        var again = ModelScaffold.Create(_dir, second, port: 1235, replace: true);

        Assert.Equal(id, again);
        var profile = File.ReadAllText(Path.Combine(_dir, again, "profile.json"));
        Assert.Contains("elsewhere.gguf", profile, StringComparison.OrdinalIgnoreCase);
        //the whole directory goes. material tuned for the old weights would be wrong beside the new ones, and wrong-invisible is worse than gone-and-said.
        Assert.False(File.Exists(stale));
        //what replaces the directory must still load as a model.
        Assert.Equal(again, Model.Load(_dir, again).Id);
    }

    private const string QatRepo = "unsloth/gemma-4-E2B-it-qat-GGUF";

    //the repo the user picked names a fetched model, since a header name such as mobile wNa8o8 -> gguf is not the model the shelf showed
    [Fact]
    public void A_FETCHED_MODEL_TAKES_ITS_ID_FROM_THE_REPO()
    {
        var fetched = ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235,
            source: new ModelSource(QatRepo, "tiny.gguf"));

        Assert.Equal("gemma-4-e2b-it-qat", fetched);
        //the twin: a file found on disk has no repo and keeps the header's name, so a source-blind id would pass the line above
        Assert.NotEqual(fetched, ModelScaffold.Create(_dir, SecondCopy(), port: 1235));
    }

    //the clash check names the id the scaffold will write, so a second quant of a fetched model finds the model it joins
    [Fact]
    public void THE_CLASH_CHECK_DERIVES_THE_SAME_ID_FROM_THE_REPO()
    {
        ModelScaffold.Create(_dir, Fixture("tiny.gguf"), port: 1235, source: new ModelSource(QatRepo, "tiny.gguf"));

        Assert.Equal((IdClash.SameModel, "gemma-4-e2b-it-qat"), ModelScaffold.ClashFor(_dir, SecondCopy(), QatRepo));
    }

    [Theory]
    [InlineData("unsloth/gemma-4-E2B-it-qat-GGUF", "gemma-4-e2b-it-qat")]
    [InlineData("bartowski/Qwen_Qwen3.5-9B-GGUF", "qwen-qwen3.5-9b")]
    [InlineData("ggml-org/gemma-3-1b-it-gguf", "gemma-3-1b-it")]
    [InlineData("someone/plain-model", "plain-model")]
    public void A_REPO_NAMES_ITS_MODEL_WITHOUT_ITS_ORG_AND_ITS_GGUF_SUFFIX(string repo, string id)
    {
        Assert.Equal(id, Gatto.Core.Models.ModelId.FromRepo(repo));
    }
}
