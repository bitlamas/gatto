using Gatto.Extensions;
using Gatto.Roles;

namespace Gatto.Tests;

public sealed class ShippedExtensionsTests : IDisposable
{
    private readonly string _home = Directory.CreateTempSubdirectory("gatto-shipped-").FullName;
    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string Full(string rel) => Path.Combine(_home, rel.Replace('/', Path.DirectorySeparatorChar));
    private string ManifestPath => Path.Combine(_home, ".shipped");

    private static readonly IReadOnlySet<string> NoHistory = new HashSet<string>();

    private static IReadOnlyDictionary<string, string> One(string rel, string text) =>
        new Dictionary<string, string> { [rel] = text };

    //the .shipped manifest matrix

    [Fact]
    public void Absent_and_unmanifested_is_written_and_recorded()
    {
        var files = One("extensions/foo.csx", "// canonical\n");
        ShippedExtensions.EnsureWritten(_home, files, NoHistory);

        Assert.True(File.Exists(Full("extensions/foo.csx")));
        Assert.Equal("// canonical\n", File.ReadAllText(Full("extensions/foo.csx")));
        Assert.Contains("extensions/foo.csx", File.ReadAllLines(ManifestPath));
    }

    [Fact]
    public void Deletion_sticks_a_manifested_file_is_not_rewritten()
    {
        var files = One("extensions/foo.csx", "// canonical\n");
        ShippedExtensions.EnsureWritten(_home, files, NoHistory);   //the first run writes the file and records it in the manifest
        File.Delete(Full("extensions/foo.csx"));                    //the user deletes it between the two runs

        ShippedExtensions.EnsureWritten(_home, files, NoHistory);   //the second run must not rewrite what the user deleted

        Assert.False(File.Exists(Full("extensions/foo.csx")));      //the deletion sticks, since the first run manifested the file
    }

    [Fact]
    public void Present_but_unmanifested_file_is_backfilled_so_its_first_deletion_sticks()
    {
        //a file present on disk with no manifest row must be recorded, or the user's first delete is undone once
        Directory.CreateDirectory(Path.GetDirectoryName(Full("roles/coder.json"))!);
        File.WriteAllText(Full("roles/coder.json"), "// pre-manifest copy\n");
        var files = One("roles/coder.json", "// canonical\n");

        ShippedExtensions.EnsureWritten(_home, files, NoHistory);   //the run records the file it found rather than rewriting it
        Assert.Contains("roles/coder.json", File.ReadAllLines(ManifestPath));
        Assert.Equal("// pre-manifest copy\n", File.ReadAllText(Full("roles/coder.json")));   //the file's existing bytes are left alone

        File.Delete(Full("roles/coder.json"));                      //the user deletes the file the manifest now names
        ShippedExtensions.EnsureWritten(_home, files, NoHistory);

        Assert.False(File.Exists(Full("roles/coder.json")));        //the first delete sticks
    }

    [Fact]
    public void User_edits_are_left_untouched()
    {
        var files = One("extensions/foo.csx", "// canonical\n");
        ShippedExtensions.EnsureWritten(_home, files, NoHistory);
        File.WriteAllText(Full("extensions/foo.csx"), "// I changed this\n");

        ShippedExtensions.EnsureWritten(_home, files, NoHistory);   //the second run must not clobber the user's edit

        Assert.Equal("// I changed this\n", File.ReadAllText(Full("extensions/foo.csx")));
    }

    //the read-only reading the extensions check uses

    [Fact]
    public void Stale_paths_names_the_unmodified_old_copy_and_writes_nothing()
    {
        const string oldText = "// v1\n";
        const string newText = "// v2\n";
        Directory.CreateDirectory(Full("extensions"));
        File.WriteAllText(Full("extensions/foo.csx"), oldText);
        var historical = new HashSet<string> { ShippedExtensions.VettedHashFor("extensions/foo.csx", oldText) };

        var stale = ShippedExtensions.StaleShippedPaths(_home, One("extensions/foo.csx", newText), historical);

        Assert.Equal(new[] { "extensions/foo.csx" }, stale);
        Assert.Equal(oldText, File.ReadAllText(Full("extensions/foo.csx")));   //the helper is read-only, the upgrade waits for the next launch
        Assert.False(File.Exists(ManifestPath));                               //the manifest stays untouched too
    }

    [Fact]
    public void Stale_paths_leaves_current_edited_and_unshipped_copies_unlisted()
    {
        //the current copy matches its own hash, an edit is outside the set, an unshipped file is nobody's to read
        Directory.CreateDirectory(Full("extensions"));
        File.WriteAllText(Full("extensions/a.csx"), "// current\n");
        File.WriteAllText(Full("extensions/b.csx"), "// I changed this\n");
        File.WriteAllText(Full("extensions/c.csx"), "// a user's own script\n");
        var files = new Dictionary<string, string>
        {
            ["extensions/a.csx"] = "// current\n",
            ["extensions/b.csx"] = "// canonical\n",
        };

        var stale = ShippedExtensions.StaleShippedPaths(_home, files, NoHistory);

        Assert.Empty(stale);
    }

    [Fact]
    public void Unmodified_old_shipped_copy_is_upgraded_in_place()
    {
        const string oldText = "// v1\n";
        const string newText = "// v2\n";
        //the copy on disk is the old canonical, and its hash is what marks it historical
        Directory.CreateDirectory(Full("extensions"));
        File.WriteAllText(Full("extensions/foo.csx"), oldText);
        var historical = new HashSet<string> { ShippedExtensions.VettedHashFor("extensions/foo.csx", oldText) };

        ShippedExtensions.EnsureWritten(_home, One("extensions/foo.csx", newText), historical);

        Assert.Equal(newText, File.ReadAllText(Full("extensions/foo.csx")));   //the stale copy is upgraded in place
    }

    [Fact]
    public void A_present_file_not_matching_a_historical_hash_is_left()
    {
        //the bytes on disk are the user's, so no historical hash matches them
        Directory.CreateDirectory(Full("extensions"));
        File.WriteAllText(Full("extensions/foo.csx"), "// mine\n");
        var historical = new HashSet<string> { ShippedExtensions.VettedHashFor("extensions/foo.csx", "// some other old\n") };

        ShippedExtensions.EnsureWritten(_home, One("extensions/foo.csx", "// v2\n"), historical);

        Assert.Equal("// mine\n", File.ReadAllText(Full("extensions/foo.csx")));   //nothing rewrites a file whose bytes are the user's
    }

    [Fact]
    public void Production_Files_carries_shipped_scripts_and_EnsureWritten_materializes_them()
    {
        //the real Files overload writes each shipped script verbatim and records it in the manifest
        Assert.True(ShippedExtensions.Files.ContainsKey("extensions/ask_user.csx"));
        Assert.True(ShippedExtensions.Files.ContainsKey("extensions/web_search.csx"));
        Assert.False(ShippedExtensions.Files.ContainsKey("extensions/journal.csx"));   //no journal script ships any more

        ShippedExtensions.EnsureWritten(_home);   //the overload GattoApp calls, so the real shipped files are the ones written

        foreach (var rel in new[] { "extensions/ask_user.csx", "extensions/web_search.csx" })
        {
            var written = Full(rel);
            Assert.True(File.Exists(written));
            Assert.Equal(ShippedExtensions.Files[rel], File.ReadAllText(written));
            Assert.Contains(rel, File.ReadAllLines(ManifestPath));
        }
    }

    //the roles go through the same manifest and historical-hash upgrade machinery as the extensions

    private string RolesDir => Path.Combine(_home, "roles");

    [Fact]
    public void ShippedRoles_MaterializeOnFirstRun_AndParseAsRoles()
    {
        ShippedExtensions.EnsureWritten(_home);   //the overload the program calls, so the real shipped roles are the ones written

        Assert.True(File.Exists(Full("roles/generalist.json")));
        Assert.True(File.Exists(Full("roles/coder.json")));
        Assert.True(File.Exists(Full("roles/oracle.json")));

        var coder = RoleFile.Load(RolesDir, "coder");
        Assert.False(coder.Checkpoints);   //no shipped role arms the commit pause, a user turns it on in their own role file
        //the coder append comes back as a real multi-line string
        Assert.Contains("report what changed", coder.Append!);
        Assert.DoesNotContain("commit", coder.Append!);   //a commit is the project's choice, the shipped role asks for none
        Assert.Contains("\n", coder.Append!);

        var oracle = RoleFile.Load(RolesDir, "oracle");
        Assert.Equal(new[] { "grounding", "citations" }, oracle.Gates);
        //the oracle append keeps its line breaks and its Windows path through the JSON read
        Assert.Contains("task_restate", oracle.Append!);
        Assert.Contains("\n", oracle.Append!);
        Assert.Contains(@"docs\specs\", oracle.Append!);
        Assert.Contains("one at a time", oracle.Append!);

        var generalist = RoleFile.Load(RolesDir, "generalist");
        Assert.Empty(generalist.Gates);
        Assert.Null(generalist.Append);
    }

    [Fact]
    public void StaleUnmodifiedRole_UpgradesInPlace()
    {
        //an untouched old oracle.json whose hash is in the historical set upgrades to the current canonical
        const string oldText = """
            {
              "gates": ["grounding"],
              "append": "old oracle, pre-citations"
            }
            """;
        var current = ShippedExtensions.Files["roles/oracle.json"];
        Directory.CreateDirectory(RolesDir);
        File.WriteAllText(Full("roles/oracle.json"), oldText);
        File.WriteAllText(ManifestPath, "roles/oracle.json\n");   //the manifest row means the file is not a fresh write
        var historical = new HashSet<string> { ShippedExtensions.VettedHashFor("roles/oracle.json", oldText) };

        ShippedExtensions.EnsureWritten(_home, One("roles/oracle.json", current), historical);

        Assert.Equal(current, File.ReadAllText(Full("roles/oracle.json")));   //the copy on disk is the current canonical
    }

    [Fact]
    public void UserEditedRole_IsNeverTouched()
    {
        //bytes that match no historical hash are the user's, so the file is left alone
        const string edited = """{ "gates": [], "append": "my own oracle" }""";
        Directory.CreateDirectory(RolesDir);
        File.WriteAllText(Full("roles/oracle.json"), edited);
        File.WriteAllText(ManifestPath, "roles/oracle.json\n");

        ShippedExtensions.EnsureWritten(_home);   //the real Files and the real historical set, which holds nothing for this file

        Assert.Equal(edited, File.ReadAllText(Full("roles/oracle.json")));   //not one byte of it changes
    }

    [Fact]
    public void DeletedRole_StaysDeleted()
    {
        ShippedExtensions.EnsureWritten(_home);            //the first run writes the role and manifests it
        Assert.True(File.Exists(Full("roles/coder.json")));
        File.Delete(Full("roles/coder.json"));             //the user deletes the role after the first run

        ShippedExtensions.EnsureWritten(_home);            //the second run must not write the role back

        Assert.False(File.Exists(Full("roles/coder.json")));   //the manifest row keeps the role deleted
    }

    [Fact]
    public void AskUser_Rev6HashIsRecordedAsHistorical_SoAnUnmodifiedPreWideningCopyUpgrades()
    {
        //the rev-6 hash is computed from the real rev-6 text, and it is what lets a stale copy upgrade
        Assert.Contains(
            "3a7090326db8e0c08bf4be4063afa2b2a0be32810dc4495e61c569361b6bde62",
            ShippedExtensions.HistoricalHashes);
    }

    //a mistyped historical hash would leave an untouched ask_user.csx reading as the user's own edit, so this uses the real previous file
    [Fact]
    public void An_untouched_pre_policy_ask_user_is_upgraded_in_place_by_the_real_bytes()
    {
        var previous = File.ReadAllText(Path.Combine(
            Census.SourceTree.RepoRoot(), "Gatto.Tests", "Fixtures", "ask_user-pre-policy.csx"));
        var current = ShippedExtensions.Files["extensions/ask_user.csx"];
        Assert.NotEqual(previous, current);   //the fixture must differ from today's canonical, or this row would prove nothing

        Directory.CreateDirectory(Full("extensions"));
        File.WriteAllText(Full("extensions/ask_user.csx"), previous);

        ShippedExtensions.EnsureWritten(
            _home, One("extensions/ask_user.csx", current), ShippedExtensions.HistoricalHashes.ToHashSet());

        //only a fixture whose hash is in HistoricalHashes can pass this, since a wrong hex digit reads as the user's own file
        Assert.Equal(current, File.ReadAllText(Full("extensions/ask_user.csx")));
    }

    //an installed copy that names the width type's old namespace fails to compile, so it must upgrade in place
    [Fact]
    public void An_untouched_pre_terminal_ask_user_is_upgraded_in_place_by_the_real_bytes()
    {
        var previous = File.ReadAllText(Path.Combine(
            Census.SourceTree.RepoRoot(), "Gatto.Tests", "Fixtures", "ask_user-pre-terminal.csx"));
        var current = ShippedExtensions.Files["extensions/ask_user.csx"];
        Assert.NotEqual(previous, current);

        Directory.CreateDirectory(Full("extensions"));
        File.WriteAllText(Full("extensions/ask_user.csx"), previous);

        ShippedExtensions.EnsureWritten(
            _home, One("extensions/ask_user.csx", current), ShippedExtensions.HistoricalHashes.ToHashSet());

        Assert.Equal(current, File.ReadAllText(Full("extensions/ask_user.csx")));
    }

    //no current canonical's hash may sit in the historical set, or pristine files would be rewritten forever
    [Fact]
    public void HistoricalHashes_NeverContainACurrentCanonical()
    {
        foreach (var (relPath, text) in ShippedExtensions.Files)
            Assert.DoesNotContain(ShippedExtensions.VettedHashFor(relPath, text),
                ShippedExtensions.HistoricalHashes);
    }

    //vetting

    [Fact]
    public void IsVetted_is_true_for_canonical_bytes_and_false_after_a_one_byte_edit()
    {
        const string text = "// sample\nvar x = 1;\n";
        var vetted = ShippedExtensions.ComputeVettedHashes(One("extensions/foo.csx", text), Array.Empty<string>());

        Directory.CreateDirectory(Full("extensions"));
        File.WriteAllText(Full("extensions/foo.csx"), text);
        var src = ExtensionDiscovery.Discover(Full("extensions")).Single();
        Assert.True(ShippedExtensions.IsVettedAgainst(src, vetted));    //the file's bytes are the canonical ones, so they vet

        File.WriteAllText(Full("extensions/foo.csx"), text + "x");      //the only change is one byte
        var edited = ExtensionDiscovery.Discover(Full("extensions")).Single();
        Assert.False(ShippedExtensions.IsVettedAgainst(edited, vetted)); //one byte is enough to make it unvetted
    }

    [Fact]
    public void IsVetted_ignores_line_ending_drift_CRLF_still_vets()
    {
        //the hash ignores line endings, so a CRLF copy still vets (otherwise a pristine bundled file prompts forever)
        const string lf = "// sample\nvar x = 1;\n";
        var vetted = ShippedExtensions.ComputeVettedHashes(One("extensions/foo.csx", lf), Array.Empty<string>());

        Directory.CreateDirectory(Full("extensions"));
        File.WriteAllText(Full("extensions/foo.csx"), lf.Replace("\n", "\r\n"));   //the same text with CRLF endings
        var crlf = ExtensionDiscovery.Discover(Full("extensions")).Single();

        Assert.True(ShippedExtensions.IsVettedAgainst(crlf, vetted));    //the CRLF copy still vets
    }

    [Fact]
    public void EnsureWritten_upgrades_a_CRLF_stale_shipped_copy_in_place()
    {
        //a CRLF copy of an old shipped file upgrades like an LF one, the drift must not strand it
        const string oldLf = "// v1\n";
        const string newLf = "// v2\n";
        var historicalHash = ShippedExtensions.VettedHashFor("extensions/foo.csx", oldLf);
        var historical = new HashSet<string> { historicalHash };

        Directory.CreateDirectory(Full("extensions"));
        File.WriteAllText(Full("extensions/foo.csx"), oldLf.Replace("\n", "\r\n"));   //the old text on disk with CRLF endings
        //manifested, so it isn't a fresh write
        File.WriteAllText(ManifestPath, "extensions/foo.csx\n");

        ShippedExtensions.EnsureWritten(_home, One("extensions/foo.csx", newLf), historical);

        Assert.Equal(newLf, File.ReadAllText(Full("extensions/foo.csx")));   //the new canonical replaces it in place
    }

    [Fact]
    public void Real_IsVetted_is_false_for_a_file_that_is_not_the_shipped_ask_user_script()
    {
        //a file that isn't a shipped script byte for byte reads as unvetted
        const string text = "// anything\n";
        Directory.CreateDirectory(Full("extensions"));
        File.WriteAllText(Full("extensions/foo.csx"), text);
        var src = ExtensionDiscovery.Discover(Full("extensions")).Single();

        Assert.False(ShippedExtensions.IsVetted(src));
    }

    //the two hashes must agree, or every future bundled extension prompts forever
    [Fact]
    public void VettedHash_matches_ExtensionDiscovery_ContentHash_for_a_single_file_extension()
    {
        const string text = "// cross-check\nWriteLine(\"hi\");\n";
        Directory.CreateDirectory(Full("extensions"));
        File.WriteAllText(Full("extensions/foo.csx"), text);

        var src = ExtensionDiscovery.Discover(Full("extensions")).Single();
        var discovered = ExtensionDiscovery.ContentHash(src);
        var shipped = ShippedExtensions.VettedHashFor("extensions/foo.csx", text);

        Assert.Equal(discovered, shipped);
    }

    [Fact]
    public void VettedContentHashes_includes_every_historical_hash()
    {
        var set = ShippedExtensions.ComputeVettedHashes(
            new Dictionary<string, string>(), new[] { "deadbeef", "cafef00d" });

        Assert.Contains("deadbeef", set);
        Assert.Contains("cafef00d", set);
    }
}
