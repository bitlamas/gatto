using Gatto.Cli;

namespace Gatto.Tests;

//what stays is half the answer, which is what a user deciding whether to uninstall is asking
public class UninstallTests
{
    private const string Install = @"C:\Users\me\AppData\Local\Programs\gatto";
    private const string Home = @"C:\Users\me\.gatto";

    //the ordinary case an installed copy, stated here rather than assumed by every test
    private static readonly InstallFacts Installed = new(ExePresent: true, ExeBytes: 40_000_000, OnPath: true);

    private static IReadOnlyList<OwnedItem> Map(bool deleteHome = false, InstallFacts? install = null) =>
        Uninstall.Map(Install, Home, deleteHome, install ?? Installed,
            @"C:\tools\llama.cpp\llama-server.exe",
            [("qwen3.6-35b", @"D:\models\qwen.gguf"), ("gemma-4-26b", @"D:\models\gemma.gguf")]);

    [Fact]
    public void GATTO_NEVER_REMOVES_SOFTWARE_IT_DID_NOT_INSTALL()
    {
        //llama.cpp and the model files belong to the user, so the map shows them staying, by path
        var map = Map();

        var llama = Assert.Single(map, i => i.What.Contains("llama.cpp"));
        Assert.False(llama.Removed);
        Assert.All(map.Where(i => i.What.Contains("model for")), m => Assert.False(m.Removed));
    }

    //a weight-file row prices the whole set, since the row tells the user how much disk the uninstall leaves alone
    [Fact]
    public void A_WEIGHT_ROW_FOR_A_SHARD_SET_PRICES_THE_WHOLE_SET()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-uninstall-shards-").FullName;
        try
        {
            var one = Path.Combine(dir, "w-00001-of-00003.gguf");
            foreach (var (name, bytes) in new (string, long)[]
                     {
                         ("w-00001-of-00003.gguf", 10_000_000),
                         ("w-00002-of-00003.gguf", 100_000_000),
                         ("w-00003-of-00003.gguf", 100_000_000),
                     })
            {
                using var fs = File.Create(Path.Combine(dir, name));
                fs.SetLength(bytes);
            }

            var map = Uninstall.Map(Install, Home, deleteHome: false, Installed,
                llamaServerPath: null, [("sharded", one)]);

            var row = Assert.Single(map, i => i.Where == one);
            Assert.Equal(210_000_000, row.Bytes);
            Assert.NotEqual(new FileInfo(one).Length, row.Bytes);
        }
        finally { try { Directory.Delete(dir, true); } catch (Exception) { } }
    }

    [Fact]
    public void KEEP_IS_THE_DEFAULT_for_the_users_own_records()
    {
        //the home holds config, models, sessions and memory, and removing the user's own notes is not tidying up
        var records = Assert.Single(Map(deleteHome: false), i => i.Where == Home);

        Assert.False(records.Removed);
    }

    [Fact]
    public void DELETING_THE_HOME_IS_A_CHOICE_the_map_then_reflects()
    {
        var records = Assert.Single(Map(deleteHome: true), i => i.Where == Home);

        Assert.True(records.Removed);
    }

    [Fact]
    public void THE_EXE_AND_THE_PATH_ENTRY_ARE_ALWAYS_OURS()
    {
        var map = Map();

        Assert.True(Assert.Single(map, i => i.What == "gatto").Removed);
        Assert.True(Assert.Single(map, i => i.What == "PATH entry").Removed);
    }

    //every line is a row of its own, so the paths cannot fall out of alignment in a joined string
    [Fact]
    public void EVERY_LINE_CARRIES_ITS_PATH_because_a_map_without_locations_is_a_claim()
    {
        var map = Map(deleteHome: true);

        Assert.All(map, i => Assert.False(string.IsNullOrWhiteSpace(i.Where)));
        var rendered = string.Join("\n", map.SelectMany(Uninstall.Rows));
        Assert.Contains(@"D:\models\qwen.gguf", rendered, StringComparison.Ordinal);
        Assert.Contains(@"C:\tools\llama.cpp", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void THE_RENDERED_LINE_SAYS_REMOVED_OR_STAYS_in_words_not_symbols()
    {
        //the row says removed or stays in words, since a glyph legend is something to learn while deciding about 30 GB
        var lines = Map(deleteHome: true).SelectMany(Uninstall.Rows).ToList();

        Assert.Contains(lines, l => l.StartsWith("removed", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("stays", StringComparison.Ordinal));
    }

    [Fact]
    public void A_MACHINE_WITH_NO_LLAMA_AND_NO_PACKS_STILL_MAPS_what_gatto_owns()
    {
        var map = Uninstall.Map(Install, Home, deleteHome: false, Installed,
            llamaServerPath: null, models: []);

        //the fourth row states that gatto never removes model files, so a machine with no model still gets that answer
        Assert.Equal(4, map.Count);   //exe, PATH entry, records, the weights line
        Assert.True(Uninstall.RemovesAnything(map));
    }

    //a yes that removes nothing

    [Fact]
    public void A_MAP_THAT_REMOVES_NOTHING_IS_REACHABLE_and_says_only_what_survives()
    {
        //with nothing installed the map removes nothing, which is the case that makes RemovesAnything able to answer no
        var map = Uninstall.Map(Install, Home, deleteHome: false,
            new InstallFacts(ExePresent: false, ExeBytes: null, OnPath: false),
            llamaServerPath: null, models: []);

        Assert.False(Uninstall.RemovesAnything(map));

        //nothing absent gets a line, since a stays line promises something that exists
        Assert.Equal(2, map.Count);
        Assert.All(map, i => Assert.False(i.Removed));
        Assert.Contains(map, i => i.Where == Home);
        Assert.DoesNotContain(map, i => i.Where == Install);
    }

    [Fact]
    public void AN_ORPHANED_PATH_ENTRY_IS_STILL_WORTH_REMOVING()
    {
        //the exe and the PATH entry are read separately, so an entry whose exe was hand-deleted still gets removed
        var map = Uninstall.Map(Install, Home, deleteHome: false,
            new InstallFacts(ExePresent: false, ExeBytes: null, OnPath: true),
            llamaServerPath: null, models: []);

        Assert.True(Uninstall.RemovesAnything(map));
        Assert.DoesNotContain(map, i => i.What == "gatto");
        Assert.Contains(map, i => i.What == "PATH entry" && i.Removed);
    }

    [Fact]
    public void DELETING_THE_HOME_IS_ITSELF_ENOUGH_TO_NEED_A_CONFIRMATION()
    {
        //deleting the home is the most destructive thing this command does, so it needs the confirmation even with nothing installed
        var map = Uninstall.Map(Install, Home, deleteHome: true,
            new InstallFacts(ExePresent: false, ExeBytes: null, OnPath: false),
            llamaServerPath: null, models: []);

        Assert.True(Uninstall.RemovesAnything(map));
    }

    [Fact]
    public void AN_UNMEASURABLE_FILE_GETS_NO_SIZE_rather_than_a_zero()
    {
        //a size that cannot be read gets no figure, since one that rounds to nothing reads as nothing there
        var line = string.Join(" ", Uninstall.Rows(
            new OwnedItem("gatto", Install, UninstallMark.Removed, Bytes: null)));

        Assert.DoesNotContain("GB", line, StringComparison.Ordinal);
        Assert.DoesNotContain("0", line, StringComparison.Ordinal);
    }

    //per-entry honesty on the destructive path, so these use a real temp tree rather than a fake filesystem

    private static string TempTree()
    {
        var root = Path.Combine(Path.GetTempPath(), "gatto-t10r-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "sessions"));
        Directory.CreateDirectory(Path.Combine(root, "models", "qwen"));
        File.WriteAllText(Path.Combine(root, "gatto.json"), "{}");
        File.WriteAllText(Path.Combine(root, "sessions", "a.jsonl"), "{}");
        File.WriteAllText(Path.Combine(root, "sessions", "b.jsonl"), "{}");
        File.WriteAllText(Path.Combine(root, "models", "qwen", "profile.json"), "{}");
        return root;
    }

    //the weights live inside the home here, since ModelLocation.SuggestedDir puts them there
    private static string TempTreeWithWeightInside(out string weight)
    {
        var root = TempTree();
        Directory.CreateDirectory(Path.Combine(root, "weights"));
        weight = Path.Combine(root, "weights", "LFM2-700M-Q6_K.gguf");
        File.WriteAllBytes(weight, new byte[64]);
        return root;
    }

    [Fact]
    public void THE_DEED_KEEPS_WHAT_THE_MAP_PROMISES_a_weight_under_the_home_survives_its_removal()
    {
        //a weight under the home must survive the home's removal, and the oracle is the disk rather than the row's word
        var home = TempTreeWithWeightInside(out var weight);
        var models = new[] { ("lfm2-700m", weight) };

        var map = Uninstall.Map(Install, home, deleteHome: true, Installed,
            llamaServerPath: null, models);

        //the row the confirm screen renders for the weight, which promises it stays
        var promised = Assert.Single(map, i => i.Where == weight);
        Assert.Equal(UninstallMark.Stays, promised.Mark);

        var failures = Uninstall.RemoveTree(home, Uninstall.KeptUnder(map, home));

        Assert.Empty(failures);

        //a recursive delete would take the weight with the folder, so the weight on disk is the oracle
        Assert.True(File.Exists(weight), "the weight the map promised would stay was deleted");

        //and the files that had to go are gone, so this cannot pass by deleting nothing
        Assert.False(File.Exists(Path.Combine(home, "gatto.json")));
        Assert.False(Directory.Exists(Path.Combine(home, "sessions")));
        Assert.False(Directory.Exists(Path.Combine(home, "models")));
    }

    [Fact]
    public void THE_HOME_ROW_SAYS_WHAT_IT_SPARES_only_when_something_under_it_is_spared()
    {
        //a removed home row directly above a stays row for a weight inside it, so the home row has to say what it spares
        var inside = Uninstall.Map(Install, Home, deleteHome: true, Installed, null,
            new[] { ("lfm2-700m", Home + @"\weights\m.gguf") });
        Assert.Contains(inside, i => i.Where == Home
            && i.What.EndsWith("(except your model weight files)", StringComparison.Ordinal));

        //weights on another drive are not inside the home, so the parenthetical would be noise about a sparing that never happened
        var elsewhere = Uninstall.Map(Install, Home, deleteHome: true, Installed, null,
            new[] { ("lfm2-700m", @"D:\weights\m.gguf") });
        Assert.Contains(elsewhere, i => i.Where == Home
            && !i.What.Contains("except", StringComparison.Ordinal));

        //a kept home has nothing to spare it from, so the parenthetical stays off
        var kept = Uninstall.Map(Install, Home, deleteHome: false, Installed, null,
            new[] { ("lfm2-700m", Home + @"\weights\m.gguf") });
        Assert.Contains(kept, i => i.Where == Home
            && !i.What.Contains("except", StringComparison.Ordinal));
    }

    [Fact]
    public void A_WEIGHT_NO_MODEL_ROW_NAMES_SURVIVES_and_the_map_says_which_folder()
    {
        //a weight no model row names survives in its folder, and the map names that folder
        var home = TempTree();
        Directory.CreateDirectory(Path.Combine(home, "weights"));
        var orphan = Path.Combine(home, "weights", "abandoned.gguf");
        File.WriteAllBytes(orphan, new byte[64]);

        //no model row names this weight, which is the whole case
        var models = System.Array.Empty<(string, string)>();
        Assert.True(Uninstall.HasUnnamedWeights(home, models));

        var map = Uninstall.Map(Install, home, deleteHome: true, Installed, null, models,
            otherWeightFiles: true);

        //the map names the folder that survives
        var row = Assert.Single(map, i => i.What.StartsWith("other model weight files",
            StringComparison.Ordinal));
        Assert.Equal(UninstallMark.Stays, row.Mark);
        Assert.Equal(Path.Combine(home, "weights"), row.Where);

        //the deed keeps exactly what the map promised, reading the map and nothing else
        Assert.Empty(Uninstall.RemoveTree(home, Uninstall.KeptUnder(map, home)));
        Assert.True(File.Exists(orphan));
        Assert.False(File.Exists(Path.Combine(home, "gatto.json")));
    }

    [Fact]
    public void AN_EMPTY_WEIGHTS_FOLDER_IS_NOT_A_PROMISE_and_the_home_goes_entirely()
    {
        //an empty weights folder is not a promise, so the home goes entirely and no stays row names it
        var home = TempTree();
        Directory.CreateDirectory(Path.Combine(home, "weights"));

        var models = System.Array.Empty<(string, string)>();
        Assert.False(Uninstall.HasUnnamedWeights(home, models));

        var map = Uninstall.Map(Install, home, deleteHome: true, Installed, null, models);
        Assert.DoesNotContain(map, i => i.What.StartsWith("other model weight files",
            StringComparison.Ordinal));

        Assert.Empty(Uninstall.RemoveTree(home, Uninstall.KeptUnder(map, home)));
        Assert.False(Directory.Exists(home));
    }

    [Fact]
    public void A_SIBLING_GATTO_IS_A_DISCOVERY_reported_never_acted_on()
    {
        //the cause of a denied delete cannot be told from a live sibling, so the notice reports the pids and acts on nothing
        Assert.Null(Uninstall.SiblingNotice([]));

        var one = Uninstall.SiblingNotice([11704]);
        Assert.Contains("11704", one);
        Assert.Contains("Close it first", one);

        var two = Uninstall.SiblingNotice([11704, 22001]);
        Assert.Contains("11704", two);
        Assert.Contains("22001", two);

        //a stop offer over somebody's live session would be a different promise, so the notice never says stop
        Assert.DoesNotContain("stop", one, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_PARTIAL_REMOVAL_SAYS_SO_and_never_names_the_command_it_just_deleted()
    {
        //the removed row is a plan, so a partial removal needs a sentence after the deed saying what survived
        Assert.Null(Uninstall.PartialRemovalNotice(Home, [], []));

        var failures = new[] { new RemovalFailure(Home + @"\extensions\.cache\a.dll", "denied") };

        var alone = Uninstall.PartialRemovalNotice(Home, failures, []);
        Assert.Contains("NOT fully removed", alone);
        Assert.Contains("1 file", alone);
        Assert.Contains("by hand", alone);

        var withSibling = Uninstall.PartialRemovalNotice(Home, failures, [11704]);
        Assert.Contains("11704", withSibling);
        Assert.Contains("by hand", withSibling);

        //the exe and the PATH entry are gone by now, so the notice must not tell the user to run gatto uninstall again
        foreach (var line in new[] { alone, withSibling })
            Assert.DoesNotContain("gatto uninstall", line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_TREE_THAT_COMES_APART_CLEANLY_reports_nothing()
    {
        var root = TempTree();

        var failures = Uninstall.RemoveTree(root);

        Assert.Empty(failures);
        Assert.False(Directory.Exists(root));   //the disk is the oracle here, an empty failures list could come from a no-op
    }

    [Fact]
    public void A_MISSING_ROOT_IS_NOT_A_FAILURE()
    {
        //a root that is already gone is a success, so it reports no failure
        Assert.Empty(Uninstall.RemoveTree(
            Path.Combine(Path.GetTempPath(), "gatto-t10r-never-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void A_LOCKED_FILE_IS_NAMED_BY_PATH_and_everything_else_still_goes()
    {
        //every file that fails is named by path, so one locked session does not read as the whole removal failing
        var root = TempTree();
        var locked = Path.Combine(root, "sessions", "a.jsonl");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var failures = Uninstall.RemoveTree(root);

            //named by path, since telling the user that something was locked would send them hunting
            var failure = Assert.Single(failures);
            Assert.Equal(locked, failure.Path);
            Assert.NotEmpty(failure.Why);

            //the work that could be done was done, since a report naming one file would pass even if nothing else went
            Assert.False(File.Exists(Path.Combine(root, "gatto.json")));
            Assert.False(File.Exists(Path.Combine(root, "sessions", "b.jsonl")));
            Assert.False(File.Exists(Path.Combine(root, "models", "qwen", "profile.json")));
            Assert.True(File.Exists(locked));

            //only the locked file is named, so the failing folders above it do not bury the one line that matters
            Assert.DoesNotContain(failures, f => f.Path == root);
        }

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public void THE_WALK_NEVER_DESCENDS_THROUGH_A_LINK_out_of_the_tree()
    {
        //the delete must not follow a link out of the tree, since a plain file sweep would delete the target's contents
        Assert.True(Uninstall.Descends(FileAttributes.Directory));
        Assert.False(Uninstall.Descends(FileAttributes.Directory | FileAttributes.ReparsePoint));
    }
    //gatto's own engine

    //an engine gatto fetched, at <home>\llama\<tag>\llama-server.exe
    private static string Fetched(string home) =>
        Path.Combine(home, "llama", "b10076", "llama-server.exe");

    //an engine under the home's llama folder is gatto's, so the row names it fetched by gatto and its mark follows the home

    //the mark is internal, so the theory takes a bool and spells the expectation from the argument
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AN_ENGINE_GATTO_FETCHED_IS_NAMED_AS_ITS_OWN(bool deleteHome)
    {
        var map = Uninstall.Map(Install, Home, deleteHome, Installed, Fetched(Home), []);

        var llama = Assert.Single(map, i => i.What.Contains("llama.cpp", StringComparison.Ordinal));

        Assert.Equal("llama.cpp (fetched by gatto)", llama.What);
        Assert.Equal(deleteHome ? UninstallMark.Removed : UninstallMark.Stays, llama.Mark);
    }

    //an engine outside the home is the user's, so it keeps its words and stays either way
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AN_ENGINE_THE_USER_PUT_SOMEWHERE_ELSE_KEEPS_ITS_OWN_WORDS(bool deleteHome)
    {
        var map = Uninstall.Map(Install, Home, deleteHome, Installed,
            @"C:\tools\llama.cpp\llama-server.exe", []);

        var llama = Assert.Single(map, i => i.What.Contains("llama.cpp", StringComparison.Ordinal));

        Assert.Equal("llama.cpp (not fetched by gatto)", llama.What);
        Assert.Equal(UninstallMark.Stays, llama.Mark);
    }

    //removing the home removes the engine gatto fetched inside it, and the disk is the oracle
    [Fact]
    public void REMOVING_THE_HOME_REMOVES_THE_ENGINE_GATTO_FETCHED()
    {
        var home = TempTree();
        var engineDir = Path.Combine(home, "llama", "b10076");
        Directory.CreateDirectory(engineDir);
        var exe = Path.Combine(engineDir, "llama-server.exe");
        File.WriteAllBytes(exe, new byte[64]);

        var map = Uninstall.Map(Install, home, deleteHome: true, Installed, exe, []);

        Assert.Equal(UninstallMark.Removed,
            Assert.Single(map, i => i.What.Contains("llama.cpp", StringComparison.Ordinal)).Mark);
        Assert.DoesNotContain(engineDir, Uninstall.KeptUnder(map, home), StringComparer.OrdinalIgnoreCase);

        Assert.Empty(Uninstall.RemoveTree(home, Uninstall.KeptUnder(map, home)));

        Assert.False(Directory.Exists(Path.Combine(home, "llama")),
            "the engine gatto fetched survived an uninstall that said it would remove it");
    }

    //the parenthetical names what is kept under the home, read from the same rows KeptUnder keeps
    [Fact]
    public void THE_HOME_ROWS_PARENTHETICAL_NAMES_WHAT_IS_ACTUALLY_KEPT()
    {
        //the engine here sits inside the home but not under its llama folder, so it stays and is the only thing kept
        var elsewhere = Path.Combine(Home, "my-llama", "llama-server.exe");

        var map = Uninstall.Map(Install, Home, deleteHome: true, Installed, elsewhere, []);
        var homeRow = Assert.Single(map, i => i.Where == Home);

        Assert.Contains("the llama.cpp you put there", homeRow.What, StringComparison.Ordinal);
        Assert.DoesNotContain("weight files)", homeRow.What, StringComparison.Ordinal);
    }

    //a weight kept under the home still says weight files, so the fix cannot just name the engine everywhere
    [Fact]
    public void A_WEIGHT_KEPT_UNDER_THE_HOME_IS_STILL_NAMED_AS_ONE()
    {
        var home = TempTreeWithWeightInside(out var weight);

        var map = Uninstall.Map(Install, home, deleteHome: true, Installed,
            llamaServerPath: null, [("lfm2-700m", weight)]);

        Assert.Contains("(except your model weight files)",
            Assert.Single(map, i => i.Where == home).What, StringComparison.Ordinal);
    }

    //a home keeping nothing inside it carries no parenthetical, so the clause is not decoration
    [Fact]
    public void A_HOME_KEEPING_NOTHING_INSIDE_IT_CARRIES_NO_PARENTHETICAL()
    {
        var map = Uninstall.Map(Install, Home, deleteHome: true, Installed,
            @"C:\tools\llama.cpp\llama-server.exe", [("qwen", @"D:\models\qwen.gguf")]);

        Assert.DoesNotContain("(except", Assert.Single(map, i => i.Where == Home).What,
            StringComparison.Ordinal);
    }

}

//no terminal means no uninstall, it refuses the same way gatto setup does
[Collection("e2e")]
public class UninstallDoorTests : IDisposable
{
    private readonly TextWriter _origErr = Console.Error;
    private readonly TextWriter _origOut = Console.Out;
    public void Dispose() { Console.SetError(_origErr); Console.SetOut(_origOut); }

    [Fact]
    public async Task UNINSTALL_REFUSES_WITHOUT_A_TERMINAL_rather_than_deleting_on_assumptions()
    {
        //unattended, this would delete on answers nobody gave
        var err = new StringWriter();
        Console.SetError(err);
        Console.SetOut(new StringWriter());

        var exit = await GattoApp.RunAsync(new[] { "uninstall" });

        Assert.Equal(2, exit);
        Assert.Contains("needs a terminal", err.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void UNINSTALL_IS_A_RESERVED_WORD_and_can_never_be_read_as_a_role()
    {
        //a word gatto acts on can't also be a role name, otherwise gatto uninstall would do something else entirely
        Assert.Equal("uninstall", ArgRouter.Parse(["uninstall"]).Command);
    }
}
