using Gatto.Cli.Setup;
using Gatto.Core.Home;

namespace Gatto.Tests.Setup;

//what apply does to a disk, the other half of the flow's intent tests (those prove nothing is applied early)
public class WriteSetApplyTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-ws-").FullName;
    public void Dispose() { try { Directory.Delete(_home, true); } catch { } }

    private static string Fixture(string n) => Path.Combine(AppContext.BaseDirectory, "Fixtures", n);

    private string Gguf()
    {
        var dst = Path.Combine(_home, "tiny.gguf");
        File.Copy(Fixture("tiny.gguf"), dst);
        return dst;
    }

    [Fact]
    public void AN_EMPTY_WRITESET_touches_nothing()
    {
        //an empty write set writes nothing at all, so a user who left the wizard gets no scaffold of defaults
        GattoHome.EnsureInitialized(_home);
        var before = Directory.GetFileSystemEntries(_home, "*", SearchOption.AllDirectories).Length;

        Assert.Null(WriteSetApply.Apply(_home, new WriteSet(), out var modelId));

        Assert.Null(modelId);
        Assert.Equal(before, Directory.GetFileSystemEntries(_home, "*", SearchOption.AllDirectories).Length);
    }

    //the connect path's three writes, and the order between them

    [Fact]
    public void A_CONNECT_WRITESET_lands_the_endpoint_its_default_AND_the_model()
    {
        //all three writes happen in the apply before prove-it, which reads the config from disk moments later
        GattoHome.EnsureInitialized(_home);
        var writes = new WriteSet
        {
            UpsertEndpoint = new WriteSet.Endpoint("server", "http://host:9", 8192),
            DefaultEndpoint = "server",
            DefaultModel = "their-model-id",
        };

        Assert.Null(WriteSetApply.Apply(_home, writes, out _));

        var config = GattoConfig.Load(_home);
        Assert.Equal("server", config.DefaultEndpoint);
        Assert.Equal("their-model-id", config.DefaultModel);
        Assert.True(config.Endpoints.ContainsKey("server"));
    }

    //the move trio, against real files

    //a moved model must record where the file ended up, since a profile naming the old path cannot load
    [Fact]
    public void A_COMPLETED_MOVE_lands_the_file_and_the_model_points_at_it()
    {
        GattoHome.EnsureInitialized(_home);
        var src = Gguf();
        var dest = Path.Combine(_home, "moved");

        Assert.Null(WriteSetApply.Apply(_home,
            new WriteSet { CreateModel = new WriteSet.Model(src, 1235, 4096), MoveTo = dest },
            out var modelId, out var note));

        Assert.Null(note);
        //the file ends up in the model's own folder under the weights root. deriving the path from modelId keeps the assertion honest if the folder naming changes
        var landed = Path.Combine(dest, modelId!, "tiny.gguf");
        Assert.True(File.Exists(landed));
        Assert.False(File.Exists(src));
        Assert.Equal(landed, Gatto.Roles.Model.Load(Path.Combine(_home, "models"), modelId!).Profile.ActivePath);
    }

    //the move is declined by default, so the file stays where it is
    [Fact]
    public void A_DECLINED_MOVE_touches_the_file_and_says_nothing()
    {
        GattoHome.EnsureInitialized(_home);
        var src = Gguf();

        Assert.Null(WriteSetApply.Apply(_home,
            new WriteSet { CreateModel = new WriteSet.Model(src, 1235, 4096) },
            out var modelId, out var note));

        Assert.Null(note);
        Assert.True(File.Exists(src));
        Assert.Equal(src, Gatto.Roles.Model.Load(Path.Combine(_home, "models"), modelId!).Profile.ActivePath);
    }

    //only a blocked destination is forced here, which ModelAdoption refuses before copying (the mid-copy failure is covered in ModelAdoptionTests)
    [Fact]
    public void A_FAILED_MOVE_LEAVES_A_WORKING_INSTALL_and_says_so()
    {
        GattoHome.EnsureInitialized(_home);
        var src = Gguf();
        var dest = Directory.CreateDirectory(Path.Combine(_home, "blocked")).FullName;
        //the blocker goes where the file will end up, in the model's own folder, the id comes from the same ModelId.Derive the apply calls
        var blockedId = Gatto.Core.Models.ModelId.Derive(Gatto.Core.Models.GgufReader.Read(src), src);
        var blockedDir = Directory.CreateDirectory(Path.Combine(dest, blockedId)).FullName;
        File.WriteAllText(Path.Combine(blockedDir, "tiny.gguf"), "someone else's file");

        Assert.Null(WriteSetApply.Apply(_home,
            new WriteSet { CreateModel = new WriteSet.Model(src, 1235, 4096), MoveTo = dest },
            out var modelId, out var note));

        //the model points at the original file, which is still there
        Assert.NotNull(modelId);
        Assert.True(File.Exists(src));
        Assert.Equal(src, Gatto.Roles.Model.Load(Path.Combine(_home, "models"), modelId!).Profile.ActivePath);

        //the failure is not silent
        Assert.NotNull(note);
        Assert.Contains("stays where it is", note!, StringComparison.Ordinal);

        //whatever was at the destination is untouched, since the mover never overwrites and a partial copy would be an orphan the user can't see
        Assert.Equal("someone else's file", File.ReadAllText(Path.Combine(blockedDir, "tiny.gguf")));
    }

    //the vision encoder sits beside the weights, so a move has to take it to the same folder
    [Fact]
    public void THE_PROJECTOR_TRAVELS_WITH_THE_MODEL()
    {
        GattoHome.EnsureInitialized(_home);
        var src = Gguf();
        var encoder = Path.Combine(_home, "mmproj-tiny.gguf");
        File.Copy(Fixture("tiny.gguf"), encoder);
        var dest = Path.Combine(_home, "moved");

        Assert.Null(WriteSetApply.Apply(_home,
            new WriteSet
            {
                CreateModel = new WriteSet.Model(src, 1235, 4096),
                Projector = encoder,
                MoveTo = dest,
            },
            out var modelId, out _));

        var model = Gatto.Roles.Model.Load(Path.Combine(_home, "models"), modelId!);
        //the projector goes into the same per-model folder as the weights
        Assert.Equal(Path.Combine(dest, modelId!, "mmproj-tiny.gguf"), model.Profile.MmProj);
        Assert.True(File.Exists(Path.Combine(dest, modelId!, "mmproj-tiny.gguf")));
        Assert.False(File.Exists(encoder));
    }

    //the per-model build, from intent to disk

    //the oracle is the model loading with the override, since a text assertion would pass over a key Model.Load rejects
    [Fact]
    public void A_PER_PACK_BUILD_REACHES_THE_PACK_ON_DISK()
    {
        GattoHome.EnsureInitialized(_home);
        var writes = new WriteSet
        {
            CreateModel = new WriteSet.Model(Gguf(), Port: 1235, Context: 4096),
            ModelLlamaServer = @"C:\forks\bailing\llama-server.exe",
        };

        Assert.Null(WriteSetApply.Apply(_home, writes, out var modelId));

        var model = Gatto.Roles.Model.Load(Path.Combine(_home, "models"), modelId!);
        Assert.Equal(@"C:\forks\bailing\llama-server.exe", model.Profile.LlamaServer);
    }

    [Fact]
    public void AN_ORDINARY_PACK_CARRIES_NO_OVERRIDE()
    {
        //the per-model build is the exception, so an ordinary model writes no LlamaServer
        GattoHome.EnsureInitialized(_home);
        var writes = new WriteSet { CreateModel = new WriteSet.Model(Gguf(), Port: 1235, Context: 4096) };

        Assert.Null(WriteSetApply.Apply(_home, writes, out var modelId));

        Assert.Null(Gatto.Roles.Model.Load(Path.Combine(_home, "models"), modelId!).Profile.LlamaServer);
    }

    [Fact]
    public void THE_DEFAULT_IS_NEVER_WRITTEN_WHEN_THE_ENDPOINT_ITSELF_FAILED_TO_LAND()
    {
        //a context of 0 makes UpsertEndpoint throw, which is the cheapest real failure at that step
        GattoHome.EnsureInitialized(_home);
        GattoConfigWriter.UpsertEndpoint(_home, "local", "http://127.0.0.1:1235", 4096);
        GattoConfigWriter.SetDefaultEndpoint(_home, "local");
        var writes = new WriteSet
        {
            UpsertEndpoint = new WriteSet.Endpoint("server", "http://host:9", 0),
            DefaultEndpoint = "server",
            DefaultModel = "their-model-id",
        };

        var problem = WriteSetApply.Apply(_home, writes, out _);

        Assert.NotNull(problem);
        var config = GattoConfig.Load(_home);
        Assert.Equal("local", config.DefaultEndpoint);   //the default is still the one set before the apply
        Assert.Null(config.DefaultModel);                //a failure at one step leaves the later steps unwritten
        Assert.False(config.Endpoints.ContainsKey("server"));
    }

    //the audition offer must stay after the llama_server write, since the runner resolves its server through config.LlamaServer
    [Fact]
    public void THE_LLAMA_PATH_LANDS_ITS_DEFAULT_IN_THE_SAME_APPLY_AS_THE_MODEL()
    {
        GattoHome.EnsureInitialized(_home);
        var writes = new WriteSet { CreateModel = new WriteSet.Model(Gguf(), Port: 1235, Context: 8192) };

        Assert.Null(WriteSetApply.Apply(_home, writes, out var modelId));

        Assert.NotNull(modelId);
        //the id comes from the apply's own out parameter, so the assertion can't pass against an apply that wrote somebody else's id
        Assert.Equal(modelId, GattoConfig.Load(_home).DefaultModel);
    }

    [Fact]
    public void The_PACK_IS_WRITTEN_WITH_THE_GIVEN_CONTEXT_and_not_the_ggufs_own()
    {
        //the wizard's context must win over the gguf's own, since a trained maximum this machine can't hold is a model that fails to load
        GattoHome.EnsureInitialized(_home);
        var writes = new WriteSet { CreateModel = new WriteSet.Model(Gguf(), Port: 1235, Context: 8192) };

        Assert.Null(WriteSetApply.Apply(_home, writes, out var modelId));

        Assert.NotNull(modelId);
        var profile = File.ReadAllText(Path.Combine(_home, "models", modelId!, "profile.json"));
        using var doc = System.Text.Json.JsonDocument.Parse(profile);
        Assert.Equal(8192, doc.RootElement.GetProperty("context").GetInt32());
    }

    [Fact]
    public void The_ORDER_IS_PACK_THEN_LLAMA_SERVER_and_default_model_is_a_SEPARATE_call()
    {
        //the order is model then llama_server, with default_model written in the same apply
        GattoHome.EnsureInitialized(_home);
        var writes = new WriteSet
        {
            CreateModel = new WriteSet.Model(Gguf(), 1235, 8192),
            LlamaServer = @"C:\llama\llama-server.exe",
        };

        Assert.Null(WriteSetApply.Apply(_home, writes, out var modelId));

        var config = File.ReadAllText(Path.Combine(_home, "gatto.json"));
        Assert.Contains("llama-server.exe", config, StringComparison.Ordinal);
        using var doc = System.Text.Json.JsonDocument.Parse(config);
        //the default names the model this apply created, so the config never points at nothing
        Assert.Equal(modelId, doc.RootElement.GetProperty("defaults").GetProperty("local").GetProperty("model").GetString());
    }

    [Fact]
    public void A_FAILURE_PART_WAY_LEAVES_THE_EARLIER_WRITES_VALID_and_NAMES_THE_STEP()
    {
        //there is no rollback, since a model with no default is recoverable and the next launch offers setup again
        GattoHome.EnsureInitialized(_home);
        var writes = new WriteSet
        {
            CreateModel = new WriteSet.Model(Gguf(), 1235, 8192),
            UpsertEndpoint = new WriteSet.Endpoint("server", "http://host:9", 4096),
        };
        Assert.Null(WriteSetApply.Apply(_home, writes, out var modelId));

        //a second apply that fails must leave the first run's model on disk
        var broken = new WriteSet { CreateModel = new WriteSet.Model(Path.Combine(_home, "gone.gguf"), 1235, 8192) };
        var complaint = WriteSetApply.Apply(_home, broken, out _);

        Assert.NotNull(complaint);
        Assert.Contains("model", complaint!, StringComparison.OrdinalIgnoreCase);   //the complaint names the step that failed
        Assert.True(Directory.Exists(Path.Combine(_home, "models", modelId!)));
    }

    [Fact]
    public void The_CONNECT_PATHS_ENDPOINT_lands_with_its_context()
    {
        GattoHome.EnsureInitialized(_home);
        var writes = new WriteSet { UpsertEndpoint = new WriteSet.Endpoint("server", "http://127.0.0.1:9", 16384) };

        Assert.Null(WriteSetApply.Apply(_home, writes, out _));

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(_home, "gatto.json")));
        var ep = doc.RootElement.GetProperty("endpoints").GetProperty("server");
        Assert.Equal("http://127.0.0.1:9", ep.GetProperty("base_url").GetString());
        Assert.Equal(16384, ep.GetProperty("context").GetInt32());
    }

    //a joined quant arrives inactive, so the file answering the next message doesn't change until the done step's question is answered
    [Fact]
    public void A_JOIN_ADDS_THE_FILE_AND_LEAVES_THE_ACTIVE_ONE_ALONE()
    {
        GattoHome.EnsureInitialized(_home);
        var first = Path.Combine(_home, "gemma-Q4_K_M.gguf");
        File.Copy(Fixture("tiny.gguf"), first);
        Assert.Null(WriteSetApply.Apply(
            _home, new WriteSet { CreateModel = new WriteSet.Model(first, 1235, 8192) }, out var id));

        var second = Path.Combine(_home, "gemma-Q6_K.gguf");
        File.Copy(Fixture("tiny.gguf"), second);
        Assert.Null(WriteSetApply.Apply(
            _home,
            new WriteSet { CreateModel = new WriteSet.Model(second, 1235, 8192, Id: id, AddToModelId: id) },
            out var joined));

        Assert.Equal(id, joined);                     //the apply reports the model that was joined
        var profile = Gatto.Roles.Model.Load(Path.Combine(_home, "models"), id!).Profile;
        Assert.Equal(2, profile.Files.Count);
        Assert.Equal("Q4_K_M", profile.Files.Single(f => f.Active).Quant);
    }

    //the done step's yes is applied here rather than by the screen that took it, so the sentence and the deed can't part
    [Fact]
    public void AND_THE_ACTIVATE_INTENT_MAKES_THE_JOINED_FILE_THE_ONE_IN_USE()
    {
        GattoHome.EnsureInitialized(_home);
        var first = Path.Combine(_home, "gemma-Q4_K_M.gguf");
        File.Copy(Fixture("tiny.gguf"), first);
        WriteSetApply.Apply(_home, new WriteSet { CreateModel = new WriteSet.Model(first, 1235, 8192) }, out var id);
        var second = Path.Combine(_home, "gemma-Q6_K.gguf");
        File.Copy(Fixture("tiny.gguf"), second);
        WriteSetApply.Apply(
            _home,
            new WriteSet { CreateModel = new WriteSet.Model(second, 1235, 8192, Id: id, AddToModelId: id) },
            out _);

        Assert.Null(WriteSetApply.Apply(
            _home, new WriteSet { ActivateFile = (id!, "Q6_K") }, out _));

        var profile = Gatto.Roles.Model.Load(Path.Combine(_home, "models"), id!).Profile;
        Assert.Equal("Q6_K", profile.Files.Single(f => f.Active).Quant);
        Assert.Equal(2, profile.Files.Count);          //activating keeps the other file in the profile
    }
}
