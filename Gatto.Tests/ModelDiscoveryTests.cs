using Gatto.Core.Acquire;
using Gatto.Core.Models;

namespace Gatto.Tests;

//every fixture is a temporary folder, the scan takes its roots verbatim and never reaches a real user folder
public class ModelDiscoveryTests : IDisposable
{
    private readonly List<string> _dirs = [];

    public void Dispose()
    {
        foreach (var d in _dirs) { try { Directory.Delete(d, recursive: true); } catch { } }
    }

    private string NewTempDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "gatto-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d);
        _dirs.Add(d);
        return d;
    }

    private static void WriteTinyGguf(string path) =>
        File.WriteAllBytes(path, GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "qwen3");
            kv.U32("qwen3.context_length", 4096);
        }));

    private static void WriteProjector(string path, string arch = "clip") =>
        File.WriteAllBytes(path, GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", arch);
        }));

    [Fact]
    public void A_FILE_A_BROWSER_IS_STILL_WRITING_IS_NOT_A_MODEL_ON_DISK_YET()
    {
        //a browser fills the empty final-name file from a .part file, so a gguf with a .part sibling is not a model yet
        var dir = NewTempDir();
        var arriving = Path.Combine(dir, "LFM2-700M-Q6_K.gguf");
        File.WriteAllBytes(arriving, []);
        File.WriteAllBytes(arriving + ".part", new byte[4096]);

        Assert.Empty(ModelDiscovery.Scan([dir]));
    }

    [Fact]
    public void A_FINISHED_FILE_IS_STILL_FOUND_and_a_locked_one_does_not_vanish()
    {
        //the exclusion keys on the sibling .part file, a locked file must not vanish when its header can't be read
        var dir = NewTempDir();
        var finished = Path.Combine(dir, "done.gguf");
        WriteTinyGguf(finished);
        Assert.Single(ModelDiscovery.Scan([dir]));

        //a leftover .part file from another download must not hide this model
        File.WriteAllBytes(Path.Combine(dir, "something-else.gguf.part"), new byte[16]);
        Assert.Single(ModelDiscovery.Scan([dir]));
    }

    [Fact]
    public void A_PUBLISHER_SHAPED_LAYOUT_IS_FOUND_from_the_models_root()
    {
        //publisher tools write models at publisher/repo/model.gguf, two levels below the root, and the scan reaches that deep
        var dir = NewTempDir();
        var repo = Path.Combine(dir, "lmstudio-community", "gemma-4-26B-A4B-it-GGUF");
        Directory.CreateDirectory(repo);
        WriteTinyGguf(Path.Combine(repo, "gemma-4-26B-A4B-it-Q6_K.gguf"));

        var found = ModelDiscovery.Scan([dir]);

        Assert.Equal("gemma-4-26B-A4B-it-Q6_K.gguf", Path.GetFileName(Assert.Single(found).Path));
    }

    //the ceiling is pinned here too, a check that only asserts what is found can't fail if the sweep goes recursive
    [Fact]
    public void THE_SWEEP_REACHES_A_MODELS_OWN_FOLDER_and_stops_at_the_ceiling()
    {
        var weights = NewTempDir();

        //the model sits in its own folder, one level below the weights root.
        var own = Path.Combine(weights, "gemma-4-26b-a4b-it");
        Directory.CreateDirectory(own);
        WriteTinyGguf(Path.Combine(own, "gemma-4-26B-A4B-it-Q4_K_M.gguf"));

        //three levels down is past the ceiling, the sweep must not reach it
        var tooDeep = Path.Combine(weights, "a", "b", "c");
        Directory.CreateDirectory(tooDeep);
        WriteTinyGguf(Path.Combine(tooDeep, "buried.gguf"));

        var found = ModelDiscovery.Scan([weights]);
        var names = found.Select(f => Path.GetFileName(f.Path)).ToArray();

        Assert.Contains("gemma-4-26B-A4B-it-Q4_K_M.gguf", names);
        Assert.DoesNotContain("buried.gguf", names);
    }

    //a gguf with no tensors is not a model, and no other rule can tell, so the fixture isn't named *.tokenizer.gguf
    [Fact]
    public void A_GGUF_WITH_NO_TENSORS_IS_NOT_A_MODEL()
    {
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "gemma-4-26B-A4B-it-Q6_K.gguf"));
        File.WriteAllBytes(Path.Combine(dir, "gemma-4-e4b-it-companion.gguf"),
            GgufTestBytes.TokenizerSidecar());

        var found = ModelDiscovery.Scan([dir]);

        var m = Assert.Single(found);
        Assert.Equal("gemma-4-26B-A4B-it-Q6_K.gguf", Path.GetFileName(m.Path));
    }

    //unreadable evidence is not a claim that a file has no weights, and the casts say which source the null means
    [Fact]
    public void ABSENT_EVIDENCE_IS_NOT_A_CLAIM_THAT_SOMETHING_IS_WEIGHTLESS()
    {
        Assert.False(ModelDiscovery.IsWeightless((Gatto.Core.Models.GgufHeader?)null));
        Assert.False(ModelDiscovery.IsWeightless((long?)null));
        //the listing side gets a known match too, neither half of the pair is vacuous
        Assert.True(ModelDiscovery.IsWeightless((long?)0));
    }

    private static void WriteShard(string path, ulong tensorCount) =>
        File.WriteAllBytes(path, GgufTestBytes.SyntheticHeader(kv =>
        {
            kv.Str("general.architecture", "qwen3");
            kv.U32("qwen3.context_length", 262144);
        }, tensorCount));

    //the weightless question goes to the whole set, a metadata-only shard one is still a model
    [Fact]
    public void A_SHARD_SET_WHOSE_FIRST_SHARD_IS_METADATA_ONLY_IS_STILL_A_MODEL()
    {
        var dir = NewTempDir();
        const string stem = "Qwen3.8-Flash-Next-Q4_K_XL-DN4";
        WriteShard(Path.Combine(dir, $"{stem}-00001-of-00004.gguf"), 0);     //shard one is metadata only, no tensors
        WriteShard(Path.Combine(dir, $"{stem}-00002-of-00004.gguf"), 297);
        WriteShard(Path.Combine(dir, $"{stem}-00003-of-00004.gguf"), 752);
        WriteShard(Path.Combine(dir, $"{stem}-00004-of-00004.gguf"), 175);

        var m = Assert.Single(ModelDiscovery.Scan([dir]));

        Assert.EndsWith($"{stem}-00001-of-00004.gguf", m.Path);
        Assert.Equal(4, m.ShardsPresent);
    }

    //a set whose every shard has zero tensors is still not a model, or the whole-set question could degrade into accepting any set
    [Fact]
    public void A_SHARD_SET_WITH_NO_TENSORS_ANYWHERE_IS_STILL_NOT_A_MODEL()
    {
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "real-model-Q6_K.gguf"));
        for (var i = 1; i <= 3; i++)
            WriteShard(Path.Combine(dir, $"empty-{i:D5}-of-00003.gguf"), 0);

        var m = Assert.Single(ModelDiscovery.Scan([dir]));

        Assert.Equal("real-model-Q6_K.gguf", Path.GetFileName(m.Path));
    }

    //an unreadable shard answers false for the set, or a transient read failure would delete a real model
    [Fact]
    public void A_TRUNCATED_SHARD_DOES_NOT_MAKE_THE_SET_WEIGHTLESS()
    {
        var dir = NewTempDir();
        WriteShard(Path.Combine(dir, "part-00001-of-00002.gguf"), 0);
        File.WriteAllBytes(Path.Combine(dir, "part-00002-of-00002.gguf"), new byte[100]);

        var m = Assert.Single(ModelDiscovery.Scan([dir]));

        Assert.EndsWith("part-00001-of-00002.gguf", m.Path);
        Assert.Equal(2, m.ShardsPresent);
    }

    //no mutation of the weightless check turns this test red, ShardSiblings stops at the first gap
    [Fact]
    public void A_MID_DOWNLOAD_WITH_ONLY_A_METADATA_SHARD_ONE_IS_NOT_A_MODEL_YET()
    {
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "real-model-Q6_K.gguf"));
        WriteShard(Path.Combine(dir, "arriving-00001-of-00004.gguf"), 0);   //shards 2, 3 and 4 are not present yet.

        var m = Assert.Single(ModelDiscovery.Scan([dir]));

        Assert.Equal("real-model-Q6_K.gguf", Path.GetFileName(m.Path));
    }

    //a companion sidecar on disk is not a model either
    [Fact]
    public void A_SIDECAR_ON_DISK_IS_NOT_OFFERED_AS_A_MODEL()
    {
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "Qwen3.8-Flash-Next-Q4_K_M.gguf"));
        WriteTinyGguf(Path.Combine(dir, "mtp-Qwen3.8-Flash-Next-shared-Q4_K_M.gguf"));   //a companion sidecar matched by the leading kind word mtp, the scan must skip it
        WriteTinyGguf(Path.Combine(dir, "Qwen3.8-imatrix-calibration.gguf"));            //a calibration file matched by imatrix anywhere in the name, the scan must skip it too

        var m = Assert.Single(ModelDiscovery.Scan([dir]));

        Assert.Equal("Qwen3.8-Flash-Next-Q4_K_M.gguf", Path.GetFileName(m.Path));
    }

    //match the head kind words only at the start of a name, a kind word in the middle belongs to a real model
    [Fact]
    public void A_KIND_WORD_IN_THE_MIDDLE_OF_A_NAME_IS_PART_OF_THE_NAME()
    {
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "Qwen3.5-35B-A3B-MTP-Q4_K_M.gguf"));

        var m = Assert.Single(ModelDiscovery.Scan([dir]));

        Assert.Equal("Qwen3.5-35B-A3B-MTP-Q4_K_M.gguf", Path.GetFileName(m.Path));
    }

    [Fact]
    public void An_MMPROJ_IS_NOT_OFFERED_AS_A_MODEL()
    {
        //an mmproj file holds a vision encoder and no language model, and a setup scaffolded over one can't run
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "gemma-4-26B-A4B-it-Q6_K.gguf"));
        WriteProjector(Path.Combine(dir, "mmproj-gemma-4-26B-A4B-it-BF16.gguf"));

        var found = ModelDiscovery.Scan([dir]);

        var m = Assert.Single(found);
        Assert.Equal("gemma-4-26B-A4B-it-Q6_K.gguf", Path.GetFileName(m.Path));
    }

    [Fact]
    public void A_PROJECTOR_IS_STILL_A_PROJECTOR_WHEN_SOMEBODY_RENAMES_IT()
    {
        //a renamed projector must still be caught, the clip architecture in the header is the fact and the name is only a convention
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "real-model.gguf"));
        WriteProjector(Path.Combine(dir, "vision-bits.gguf"));

        var found = ModelDiscovery.Scan([dir]);

        var m = Assert.Single(found);
        Assert.Equal("real-model.gguf", Path.GetFileName(m.Path));
    }

    [Fact]
    public void A_PROJECTOR_WHOSE_HEADER_WE_CANNOT_READ_IS_STILL_CAUGHT_BY_ITS_NAME()
    {
        //a truncated projector reads back a null header, so the name is the only signal left
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "real-model.gguf"));
        File.WriteAllBytes(Path.Combine(dir, "mmproj-model-f16.gguf"), new byte[8]);

        var found = ModelDiscovery.Scan([dir]);

        Assert.Equal("real-model.gguf", Path.GetFileName(Assert.Single(found).Path));
    }

    [Fact]
    public void A_MODEL_IS_NOT_MISTAKEN_FOR_A_PROJECTOR()
    {
        //a vision-capable model has an architecture of its own, so the projector filter must not drop it
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "qwen3-vl-8b-Q4_K_M.gguf"));

        Assert.Single(ModelDiscovery.Scan([dir]));
    }

    [Fact]
    public void Shard_sets_collapse_to_shard_one_with_summed_bytes()
    {
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "big-00001-of-00003.gguf"));       //the scan reads the header of the representative shard only, the other shards add their size to the total
        File.WriteAllBytes(Path.Combine(dir, "big-00002-of-00003.gguf"), new byte[100]);
        File.WriteAllBytes(Path.Combine(dir, "big-00003-of-00003.gguf"), new byte[50]);
        var found = ModelDiscovery.Scan([dir]);
        var m = Assert.Single(found);
        Assert.EndsWith("big-00001-of-00003.gguf", m.Path);
        Assert.Equal(new FileInfo(Path.Combine(dir, "big-00001-of-00003.gguf")).Length + 150, m.FileBytes);
    }

    [Fact]
    public void ShardName_reads_the_convention_and_writes_it_back()
    {
        var s = ShardName.Parse("big-model-Q4_K_M-00002-of-00013.gguf");

        Assert.NotNull(s);
        Assert.Equal("big-model-Q4_K_M", s.Stem);
        Assert.Equal(2, s.Index);
        Assert.Equal(13, s.Count);
        Assert.True(s.IsSet);
        Assert.Equal("big-model-Q4_K_M-00001-of-00013.gguf", s.FileNameFor(1));
    }

    [Fact]
    public void ShardName_reads_a_PATH_without_dragging_the_directory_into_the_stem()
    {
        //the parser strips the directory even from a full path, otherwise the stem holds C:\models\ and FileNameFor gives back neither
        var s = ShardName.Parse(Path.Combine(@"C:\models\org", "m-00001-of-00003.gguf"));

        Assert.Equal("m", s!.Stem);
        Assert.Equal("m-00002-of-00003.gguf", s.FileNameFor(2));
    }

    [Theory]
    [InlineData("plain-model-Q4_K_M.gguf")]        //the deviation here is a missing shard suffix.
    [InlineData("m-0001-of-0003.gguf")]            //the convention is exactly five digits. four digits must decline.
    [InlineData("m-00001-of-00003.bin")]           //the convention matches the .gguf extension only
    public void ShardName_declines_a_name_that_is_not_the_convention(string name) =>
        Assert.Null(ShardName.Parse(name));

    [Fact]
    public void A_shard_shaped_name_asserting_ZERO_shards_is_parsed_but_is_NOT_a_set()
    {
        //a name with -of-00000 parses but is not a set, or a zero-iteration loop would answer that nothing is missing
        var s = ShardName.Parse("m-00001-of-00000.gguf");

        Assert.NotNull(s);
        Assert.False(s.IsSet);
    }

    [Fact]
    public void ShardName_matches_an_UPPERCASE_extension_set()
    {
        //matching must ignore case through the extension, and the rule has one home
        Assert.Equal(4, ShardName.Parse("M-00002-OF-00004.GGUF")!.Count);
    }

    [Fact]
    public void A_COMPLETE_shard_set_on_disk_counts_every_file_and_reads_complete()
    {
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "big-00001-of-00003.gguf"));
        File.WriteAllBytes(Path.Combine(dir, "big-00002-of-00003.gguf"), new byte[100]);
        File.WriteAllBytes(Path.Combine(dir, "big-00003-of-00003.gguf"), new byte[50]);

        var m = Assert.Single(ModelDiscovery.Scan([dir]));

        Assert.Equal(3, m.ShardsPresent);
        Assert.Equal(3, m.Set!.Count);
        Assert.True(m.IsComplete);
    }

    [Fact]
    public void A_HALF_DOWNLOADED_SET_IS_ONE_INCOMPLETE_CANDIDATE_not_three_offerable_ones()
    {
        //shards that arrive partly group into one incomplete candidate, scattered shards each name a file llama-server can't load
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "big-00006-of-00013.gguf"));
        WriteTinyGguf(Path.Combine(dir, "big-00007-of-00013.gguf"));

        var m = Assert.Single(ModelDiscovery.Scan([dir]));

        Assert.Equal(2, m.ShardsPresent);
        Assert.Equal(13, m.Set!.Count);
        Assert.False(m.IsComplete);
        //the representative names the lowest-index shard on disk, a synthesized path would point at a file that isn't there
        Assert.EndsWith("big-00006-of-00013.gguf", m.Path);
    }

    [Fact]
    public void A_shard_set_whose_files_disagree_about_CASE_is_still_one_candidate()
    {
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "big-00001-of-00002.gguf"));
        File.WriteAllBytes(Path.Combine(dir, "BIG-00002-OF-00002.GGUF"), new byte[100]);

        var m = Assert.Single(ModelDiscovery.Scan([dir]));

        Assert.Equal(2, m.ShardsPresent);
        Assert.True(m.IsComplete);
    }

    [Fact]
    public void A_PLAIN_FILE_IS_ALWAYS_COMPLETE_and_belongs_to_no_set()
    {
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "plain-Q4_K_M.gguf"));

        var m = Assert.Single(ModelDiscovery.Scan([dir]));

        Assert.Null(m.Set);
        Assert.True(m.IsComplete);
        Assert.Equal(1, m.ShardsPresent);
    }

    [Fact]
    public void A_hand_built_FoundModel_defaults_to_one_file_present()
    {
        //the present-shard count defaults to one file, a wrong default would quietly claim a partial set complete
        Assert.True(new FoundModel(@"C:\m\plain.gguf", 100, null).IsComplete);
        Assert.False(new FoundModel(@"C:\m\big-00001-of-00013.gguf", 100, null).IsComplete);
    }

    [Fact]
    public void A_malformed_gguf_is_returned_with_its_outcome_not_silently_dropped()
    {
        var dir = NewTempDir();
        File.WriteAllBytes(Path.Combine(dir, "broken.gguf"), new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        var m = Assert.Single(ModelDiscovery.Scan([dir]));
        Assert.Equal(GgufOutcome.Malformed, m.Header!.Outcome);
    }

    [Fact]
    public void Binaries_and_other_files_are_never_returned()
    {
        //the scan enumerates *.gguf only and returns nothing else
        var dir = NewTempDir();
        File.WriteAllBytes(Path.Combine(dir, "llama-server.exe"), new byte[10]);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "hello");
        WriteTinyGguf(Path.Combine(dir, "real.gguf"));
        var m = Assert.Single(ModelDiscovery.Scan([dir]));
        Assert.EndsWith("real.gguf", m.Path);
    }

    [Fact]
    public void A_nonexistent_root_is_skipped_not_thrown()
    {
        //a configured folder that no longer exists is a normal state, the scan skips it
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "a.gguf"));
        var found = ModelDiscovery.Scan([dir, Path.Combine(dir, "does-not-exist")]);
        Assert.Single(found);
    }

    [Fact]
    public void Duplicate_roots_yield_one_result()
    {
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "a.gguf"));
        Assert.Single(ModelDiscovery.Scan([dir, dir]));
    }

    [Fact]
    public void The_gguf_extension_match_is_case_insensitive()
    {
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "shouty.GGUF"));
        Assert.Single(ModelDiscovery.Scan([dir]));
    }

    [Fact]
    public void Both_levels_are_scanned_and_the_third_is_not()
    {
        //the scan reaches two levels below the root, and a third is a full-disk crawl
        var root = NewTempDir();
        var publisher = Path.Combine(root, "lmstudio-community");
        var repo = Path.Combine(publisher, "some-model-GGUF");
        var deeper = Path.Combine(repo, "nested");
        Directory.CreateDirectory(deeper);
        WriteTinyGguf(Path.Combine(root, "at-the-root.gguf"));
        WriteTinyGguf(Path.Combine(publisher, "one-down.gguf"));
        WriteTinyGguf(Path.Combine(repo, "two-down.gguf"));
        WriteTinyGguf(Path.Combine(deeper, "three-down.gguf"));

        var names = ModelDiscovery.Scan([root]).Select(m => Path.GetFileName(m.Path)).ToList();

        Assert.Contains("at-the-root.gguf", names);
        Assert.Contains("one-down.gguf", names);
        Assert.Contains("two-down.gguf", names);
        Assert.DoesNotContain("three-down.gguf", names);
    }

    [Fact]
    public void A_header_is_parsed_for_each_found_model()
    {
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "a.gguf"));
        var m = Assert.Single(ModelDiscovery.Scan([dir]));
        Assert.Equal("qwen3", m.Header!.Architecture);
        Assert.Equal(4096L, m.Header.ContextLength);
    }

    [Fact]
    public void A_model_that_is_THERE_is_missing_nothing()
    {
        var dir = NewTempDir();
        var path = Path.Combine(dir, "a.gguf");
        WriteTinyGguf(path);
        Assert.Empty(ModelDiscovery.MissingFiles(path, null).Model);
    }

    [Fact]
    public void A_DELETED_model_is_reported_BY_PATH_because_naming_the_file_is_the_whole_value()
    {
        //a missing model is reported by its full path, naming the file tells the user what to restore
        var path = Path.Combine(NewTempDir(), "gone.gguf");
        Assert.Equal([path], ModelDiscovery.MissingFiles(path, null).Model);
    }

    [Fact]
    public void A_COMPLETE_shard_set_is_missing_nothing_even_though_the_model_names_only_shard_one()
    {
        var dir = NewTempDir();
        for (var i = 1; i <= 3; i++) WriteTinyGguf(Path.Combine(dir, $"m-{i:D5}-of-00003.gguf"));

        Assert.Empty(ModelDiscovery.MissingFiles(Path.Combine(dir, "m-00001-of-00003.gguf"), null).Model);
    }

    [Fact]
    public void A_shard_set_with_a_HOLE_in_it_is_missing_that_shard_even_though_shard_one_exists()
    {
        //a deleted tail shard breaks serving even when shard one exists, and the check lives beside the shard convention
        var dir = NewTempDir();
        WriteTinyGguf(Path.Combine(dir, "m-00001-of-00003.gguf"));
        WriteTinyGguf(Path.Combine(dir, "m-00002-of-00003.gguf"));

        Assert.Equal([Path.Combine(dir, "m-00003-of-00003.gguf")],
            ModelDiscovery.MissingFiles(Path.Combine(dir, "m-00001-of-00003.gguf"), null).Model);
    }

    [Fact]
    public void A_NAMED_PROJECTOR_THAT_IS_GONE_IS_MISSING_TOO_even_when_the_model_is_all_there()
    {
        //the check must ask about the named projector too, a model naming a deleted mmproj fails inside llama-server
        var dir = NewTempDir();
        var model = Path.Combine(dir, "seeing.gguf");
        WriteTinyGguf(model);
        var projector = Path.Combine(dir, "mmproj-seeing-BF16.gguf");   //the projector file is left unwritten on purpose.

        var missing = ModelDiscovery.MissingFiles(model, projector);

        Assert.Empty(missing.Model);
        Assert.Equal([projector], missing.Projector);      //a missing projector alone is enough to make the model unserveable.
        Assert.Equal(1, missing.Count);
    }

    [Fact]
    public void A_PRESENT_projector_is_missing_nothing()
    {
        //a guard tested only on refusal can't tell a working check from one that always fires
        var dir = NewTempDir();
        var model = Path.Combine(dir, "seeing.gguf");
        var projector = Path.Combine(dir, "mmproj-seeing-BF16.gguf");
        WriteTinyGguf(model);
        WriteTinyGguf(projector);

        Assert.Equal(0, ModelDiscovery.MissingFiles(model, projector).Count);
    }

    [Fact]
    public void A_model_naming_NO_projector_is_judged_on_its_model_alone()
    {
        //null, empty and blank must all mean no projector, reading them as a path would fail every non-vision model
        var dir = NewTempDir();
        var model = Path.Combine(dir, "plain.gguf");
        WriteTinyGguf(model);

        Assert.Equal(0, ModelDiscovery.MissingFiles(model, null).Count);
        Assert.Equal(0, ModelDiscovery.MissingFiles(model, "").Count);
        Assert.Equal(0, ModelDiscovery.MissingFiles(model, "   ").Count);
    }

    [Fact]
    public void A_shard_shaped_name_claiming_ZERO_shards_still_answers_the_plain_question()
    {
        //a name claiming zero shards is just a file, counting its members would call a model that cannot load healthy
        var path = Path.Combine(NewTempDir(), "m-00001-of-00000.gguf");
        Assert.Equal([path], ModelDiscovery.MissingFiles(path, null).Model);
    }
}
