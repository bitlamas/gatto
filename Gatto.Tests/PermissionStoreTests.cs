using Gatto.Core.Loop.Permissions;

namespace Gatto.Tests;

public class PermissionStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("gatto-perms-").FullName;
    public void Dispose() => Directory.Delete(_root, recursive: true);

    //the store's file in the home, the test folder standing for both the home and the project
    private string PermsPath => PermissionStore.PathFor(_root, _root);

    [Theory]
    [InlineData("git status; Remove-Item x")]
    [InlineData("git status | out-null")]
    [InlineData("git status `; x")]
    [InlineData("git status $(rm x)")]
    [InlineData("git status & calc")]
    [InlineData("git status\nrm x")]
    [InlineData("git status\rrm x")]
    [InlineData("git status > out.txt")]        //a redirect must force a prompt, so it counts as chaining.
    [InlineData("git status >> out.txt")]       //the append redirect, which contains >
    [InlineData("git status 2> err.txt")]       //the stderr redirect, which contains >
    [InlineData("git status 1>&2")]             //the file-descriptor dup, which contains >
    [InlineData("cat < in.txt")]                //the input redirect case.
    public void IsChained_true_for_chaining_metacharacters(string command)
    {
        Assert.True(ShellChainingGuard.IsChained(command));
    }

    [Theory]
    [InlineData("git status")]
    [InlineData("git status --short")]
    [InlineData("git log --oneline -3")]
    [InlineData("")]
    public void IsChained_false_for_plain_commands(string command)
    {
        Assert.False(ShellChainingGuard.IsChained(command));
    }

    [Theory]
    [InlineData("git status; Remove-Item x")]
    [InlineData("git status | out-null")]
    [InlineData("git status `; x")]
    [InlineData("git status $(rm x)")]
    [InlineData("git status & calc")]
    [InlineData("git status\nRemove-Item x")]
    [InlineData("git status > C:\\Users\\u\\.gatto\\extensions\\evil.csx")]  //the bug shape: a redirect wrote a file under a shell grant.
    [InlineData("git status >> log.txt")]
    [InlineData("git status 2> err.txt")]
    public void AllowsShell_denies_chained_even_with_matching_prefix(string command)
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: false);

        //both facets must hold, the guard flags the command and the store denies it.
        Assert.True(ShellChainingGuard.IsChained(command));
        Assert.False(store.AllowsShell(command));
    }

    [Fact]
    public void AllowsShell_allows_plain_extension_of_granted_prefix()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: false);

        Assert.True(store.AllowsShell("git status --short"));
        Assert.True(store.AllowsShell("git status"));
    }

    [Fact]
    public void AllowsShell_is_case_insensitive_on_prefix()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: false);

        Assert.True(store.AllowsShell("GIT STATUS --short"));
    }

    [Fact]
    public void AllowsShell_ignores_leading_whitespace()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: false);

        Assert.True(store.AllowsShell("   git status --short"));
    }

    [Fact]
    public void AllowsShell_denies_non_matching_command()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: false);

        Assert.False(store.AllowsShell("npm install"));
    }

    [Fact]
    public void AllowsShell_denies_everything_when_no_grants()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        Assert.False(store.AllowsShell("git status"));
    }

    [Fact]
    public void AllowsWrite_allows_file_under_granted_dir()
    {
        var dir = Path.Combine(_root, "proj");
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantWriteDir(dir, persist: false);

        Assert.True(store.AllowsWrite(Path.Combine(dir, "sub", "a.txt")));
    }

    [Fact]
    public void AllowsWrite_denies_sibling_dir_with_shared_prefix()
    {
        //a grant on C:\proj must not allow C:\proj2\evil.txt. the paths need not exist, GetFullPath only normalizes text
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantWriteDir(@"C:\proj", persist: false);

        Assert.False(store.AllowsWrite(@"C:\proj2\evil.txt"));
        Assert.True(store.AllowsWrite(@"C:\proj\ok.txt"));
    }

    [Fact]
    public void AllowsWrite_normalizes_relative_path_before_matching()
    {
        var dir = Path.Combine(_root, "proj");
        Directory.CreateDirectory(dir);
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantWriteDir(dir, persist: false);

        var cwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = dir;
            Assert.True(store.AllowsWrite("sub/../a.txt"));   //the relative path normalizes to a file inside the granted directory.
        }
        finally { Environment.CurrentDirectory = cwd; }
    }

    [Fact]
    public void AllowsWrite_handles_trailing_separator_on_grant()
    {
        var dir = Path.Combine(_root, "proj");
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantWriteDir(dir + Path.DirectorySeparatorChar, persist: false);

        Assert.True(store.AllowsWrite(Path.Combine(dir, "a.txt")));
        Assert.False(store.AllowsWrite(Path.Combine(_root, "proj2", "evil.txt")));
    }

    [Fact]
    public void AllowsWrite_denies_when_no_grants()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        Assert.False(store.AllowsWrite(Path.Combine(_root, "anything.txt")));
    }

    [Fact]
    public void Grant_with_persist_is_seen_by_a_fresh_Load()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: true);
        store.GrantWriteDir(@"C:\proj", persist: true);

        Assert.True(File.Exists(PermsPath));

        var reloaded = PermissionStore.Load(_root, _root, out var warning);
        Assert.Null(warning);
        Assert.True(reloaded.AllowsShell("git status --short"));
        Assert.True(reloaded.AllowsWrite(@"C:\proj\a.txt"));
    }

    [Fact]
    public void Session_only_grant_is_not_written_to_disk()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: false);
        store.GrantWriteDir(@"C:\proj", persist: false);

        Assert.False(File.Exists(PermsPath));

        var reloaded = PermissionStore.Load(_root, _root, out _);
        Assert.False(reloaded.AllowsShell("git status"));
        Assert.False(reloaded.AllowsWrite(@"C:\proj\a.txt"));
    }

    //the key only names the file, so a store that records another folder grants nothing here
    [Fact]
    public void A_store_that_records_another_folder_loads_empty_with_a_warning()
    {
        var other = PermissionStore.Load(_root, Path.Combine(_root, "other"), out _);
        other.GrantShellPrefix("git", persist: true);
        Directory.CreateDirectory(Path.GetDirectoryName(PermsPath)!);
        File.Copy(PermissionStore.PathFor(_root, Path.Combine(_root, "other")), PermsPath);

        var store = PermissionStore.Load(_root, _root, out var warning);

        Assert.False(store.AllowsShell("git status"));
        Assert.Contains("records the folder", warning, StringComparison.Ordinal);
    }

    //the store's file sits in the home under the folder's key, and nothing is written into the folder itself
    [Fact]
    public void Grants_are_written_to_the_home_and_never_into_the_project()
    {
        var home = Path.Combine(_root, "home");
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        var store = PermissionStore.Load(home, project, out _);
        store.GrantShellPrefix("git", persist: true);
        store.SetWild(true, persist: true);

        Assert.True(File.Exists(PermissionStore.PathFor(home, project)));
        Assert.False(Directory.Exists(Path.Combine(project, ".gatto")));
        Assert.Contains($"\"project\": \"{Gatto.Core.Home.ProjectKey.PathOf(project).Replace("\\", "\\\\")}\"", File.ReadAllText(PermissionStore.PathFor(home, project)));
    }

    //a store in memory grants for the run and writes nothing anywhere
    [Fact]
    public void An_in_memory_store_writes_nothing()
    {
        var store = PermissionStore.InMemory(_root);
        store.GrantShellPrefix("git", persist: true);
        store.SetWild(true, persist: true);

        Assert.True(store.AllowsShell("git status"));
        Assert.Null(store.FilePath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    //the batch-attach grant survives a reload
    [Fact]
    public void The_attach_grant_round_trips()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantAttachMany();

        var again = PermissionStore.Load(_root, _root, out var warning);

        Assert.Null(warning);
        Assert.True(again.AttachMany);
    }

    [Fact]
    public void Missing_file_loads_empty_store_no_warning()
    {
        var store = PermissionStore.Load(_root, _root, out var warning);
        Assert.Null(warning);
        Assert.False(store.AllowsShell("git status"));
    }

    [Fact]
    public void Corrupt_json_loads_empty_store_with_warning_no_throw()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PermsPath)!);
        File.WriteAllText(PermsPath, "{ this is not valid json ]");

        var store = PermissionStore.Load(_root, _root, out var warning);

        Assert.NotNull(warning);
        Assert.False(store.AllowsShell("git status"));
        Assert.False(store.AllowsWrite(@"C:\proj\a.txt"));
    }

    [Fact]
    public void Wrong_shape_json_degrades_to_empty_not_permissive()
    {
        //the shell_prefixes field must be an array, so a bare string is malformed. fail-closed means no partially parsed grant survives
        Directory.CreateDirectory(Path.GetDirectoryName(PermsPath)!);
        File.WriteAllText(PermsPath, """{ "shell_prefixes": "git status", "write_dirs": ["C:\\proj"] }""");

        var store = PermissionStore.Load(_root, _root, out var warning);

        Assert.NotNull(warning);
        Assert.False(store.AllowsShell("git status"));
        Assert.False(store.AllowsWrite(@"C:\proj\a.txt"));   //a well-formed grant must not survive a malformed file.
    }

    [Fact]
    public void Write_dir_with_embedded_nul_loads_empty_with_warning_no_throw()
    {
        //the GetFullPath call throws on an embedded NUL, legal in JSON. the apply step sits in the parse try, so Load never throws and the store degrades to empty
        Directory.CreateDirectory(Path.GetDirectoryName(PermsPath)!);
        File.WriteAllText(PermsPath, "{ \"write_dirs\": [\"C:\\\\x\\u0000y\"] }");

        var store = PermissionStore.Load(_root, _root, out var warning);   //loading must never throw, whatever the file holds.

        Assert.NotNull(warning);
        Assert.False(store.AllowsWrite(@"C:\x\a.txt"));
    }

    [Fact]
    public void Partial_apply_failure_discards_earlier_valid_grants()
    {
        //a later bad entry must not leave an earlier valid grant loaded. fail-closed degrades the whole file to empty.
        Directory.CreateDirectory(Path.GetDirectoryName(PermsPath)!);
        File.WriteAllText(PermsPath,
            "{ \"shell_prefixes\": [\"git status\"], \"write_dirs\": [\"C:\\\\x\\u0000y\"] }");

        var store = PermissionStore.Load(_root, _root, out var warning);

        Assert.NotNull(warning);
        Assert.False(store.AllowsShell("git status"));
    }

    [Fact]
    public void AllowsShell_grant_on_bare_token_matches_token_boundary_only()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git", persist: false);

        Assert.True(store.AllowsShell("git status"));    //a space after the grant is a token boundary.
        Assert.True(store.AllowsShell("git"));           //the command equal to the grant is allowed.
        Assert.False(store.AllowsShell("github-cli x")); //a non-space after the grant names a different program.
        Assert.False(store.AllowsShell("gitk"));         //the grant is only a prefix of gitk, a different program
    }

    [Fact]
    public void AllowsShell_multi_token_grant_still_requires_boundary()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: false);

        Assert.True(store.AllowsShell("git status --short"));   //whitespace after the granted prefix is a boundary.
        Assert.False(store.AllowsShell("git statusfoo"));       //a glued token is not the granted command.
    }

    [Fact]
    public void Persist_creates_directory_and_file_when_absent()
    {
        Assert.False(Directory.Exists(Path.Combine(_root, ".gatto")));
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: true);
        Assert.True(File.Exists(PermsPath));
    }

    [Fact]
    public void Persist_does_not_duplicate_repeated_grants()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: true);
        store.GrantShellPrefix("git status", persist: true);
        store.GrantShellPrefix("GIT STATUS", persist: true);

        var text = File.ReadAllText(PermsPath).ToLowerInvariant();
        //a repeated grant, in any case, must appear once in the saved array.
        var count = text.Split("git status").Length - 1;
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData("git status --short", "git status")]
    [InlineData("git status", "git status")]
    [InlineData("ls", "ls")]
    [InlineData("   git   status   --short", "git status")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void SuggestShellPrefix_takes_first_two_tokens(string command, string expected)
    {
        Assert.Equal(expected, PermissionStore.SuggestShellPrefix(command));
    }

    [Theory]
    [InlineData("python -c print(1)")]              //the -c flag runs arbitrary code, so no grant may name it
    [InlineData("pwsh -Command Get-Date")]
    [InlineData("powershell -EncodedCommand ZQ==")] //an encoded-command flag bypasses the check if it were grantable.
    [InlineData("node -e 1+1")]
    [InlineData("bash -c ls")]
    [InlineData("cmd /c dir")]                       //a slash-prefixed flag counts too.
    [InlineData("pwsh -File x.ps1")]                 //a file flag would cover any file the interpreter runs.
    [InlineData("git -C dir status")]               //this false positive is accepted, since denying it is the safe direction.
    public void HasFlagShapedSecondToken_true_for_flag_shaped_second_token(string command)
    {
        Assert.True(PermissionStore.HasFlagShapedSecondToken(command));
    }

    [Theory]
    [InlineData("git status --short")]              //the flag sits third here, so the second token is plain.
    [InlineData("python build.py")]                 //a plain second token stays offerable.
    [InlineData("npm install")]
    [InlineData("python")]                          //one token leaves no second token to check.
    [InlineData("")]                                //the empty command case.
    [InlineData("   ")]                             //the whitespace-only command case.
    public void HasFlagShapedSecondToken_false_when_second_token_is_plain_or_absent(string command)
    {
        Assert.False(PermissionStore.HasFlagShapedSecondToken(command));
    }

    //the flag check gates the offer only, a standing grant still matches. wiring the rule into AllowsShell would kill the granted python -c prefix
    [Theory]
    [InlineData("python -c", "python -c print(1)")]
    [InlineData("git -C .", "git -C . status")]
    public void AllowsShell_still_matches_an_already_granted_flag_shaped_prefix(string grant, string command)
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix(grant, persist: false);

        Assert.True(PermissionStore.HasFlagShapedSecondToken(command));   //the offer would be suppressed for this command.
        Assert.True(store.AllowsShell(command));                          //the standing grant matches regardless of the offer rule.
    }

    [Fact]
    public void GrantShellPrefix_ignores_empty_prefix_so_it_never_matches_everything()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("", persist: false);
        store.GrantShellPrefix("   ", persist: false);

        Assert.False(store.AllowsShell("git status"));
        Assert.False(store.AllowsShell("rm -rf /"));
    }

    [Fact]
    public void AllowsShell_on_whitespace_only_command_is_denied()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: false);
        Assert.False(store.AllowsShell("     "));
    }

    [Fact]
    public void Wild_defaults_false_and_roundtrips_through_persist_and_load()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        Assert.False(store.Wild);

        store.SetWild(true, persist: true);
        Assert.True(store.Wild);

        var reloaded = PermissionStore.Load(_root, _root, out var warning);
        Assert.Null(warning);
        Assert.True(reloaded.Wild);
    }

    [Fact]
    public void Wild_wrong_type_treats_whole_file_as_corrupt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PermsPath)!);
        File.WriteAllText(PermsPath,
            "{\"shell_prefixes\":[\"git status\"],\"write_dirs\":[],\"wild\":\"yes\"}");

        var store = PermissionStore.Load(_root, _root, out var warning);
        Assert.NotNull(warning);                      //the wrong type must surface a load warning.
        Assert.False(store.Wild);
        Assert.False(store.AllowsShell("git status"));   //fail closed means no grant survives a corrupt file.
    }

    [Fact]
    public void Wild_survives_a_later_grant_persist_and_never_resurrects_after_set_false()
    {
        var store = PermissionStore.Load(_root, _root, out _);

        //a later grant rewrite must not drop a wild flag that is on.
        store.SetWild(true, persist: true);
        store.GrantShellPrefix("git status", persist: true);
        Assert.True(PermissionStore.Load(_root, _root, out _).Wild);

        //a later grant must not turn the wild flag back on.
        store.SetWild(false, persist: true);
        store.GrantShellPrefix("git log", persist: true);
        var reloaded = PermissionStore.Load(_root, _root, out _);
        Assert.False(reloaded.Wild);
        Assert.True(reloaded.AllowsShell("git log --oneline"));   //the wild flag never changes how a prefix grant matches.
    }

    [Fact]
    public void AllowsTool_exact_name_only_no_prefix_semantics()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantTool("web_search", persist: false);

        Assert.True(store.AllowsTool("web_search"));
        Assert.False(store.AllowsTool("web"));          //a tool grant never authorizes a shorter or longer name.
        Assert.False(store.AllowsTool("web_search2"));
        Assert.False(store.AllowsTool("Web_Search"));   //names match by ordinal, so only the canonical lowercase name matches.
    }

    [Fact]
    public void GrantTool_persists_and_reloads()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantTool("web_search", persist: true);

        var reloaded = PermissionStore.Load(_root, _root, out var warning);
        Assert.Null(warning);
        Assert.True(reloaded.AllowsTool("web_search"));
    }

    [Fact]
    public void Corrupt_tools_entry_poisons_the_whole_file()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PermsPath)!);
        File.WriteAllText(PermsPath, "{\"shell_prefixes\":[\"git\"],\"tools\":[1]}");

        var store = PermissionStore.Load(_root, _root, out var warning);

        Assert.NotNull(warning);
        Assert.False(store.AllowsShell("git status"));   //one corrupt entry drops every grant in the file
        Assert.False(store.AllowsTool("web_search"));
    }

    [Fact]
    public void Persist_omits_tools_key_while_empty()
    {
        //an empty tool list must not add the key, so an older file round-trips unchanged.
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git", persist: true);
        Assert.DoesNotContain("\"tools\"", File.ReadAllText(PermsPath));
    }

    //revocation is index-based through RevokeAt, and the tests below cover every branch, including the tool offset

    [Fact]
    public void ListEntries_IsEmpty_ForAFreshStore()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        Assert.Empty(store.ListEntries());
    }

    [Fact]
    public void ListEntries_OrdersShellPrefixesThenWriteDirsThenTools()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: false);
        store.GrantWriteDir(@"C:\proj", persist: false);
        store.GrantTool("web_search", persist: false);

        var entries = store.ListEntries();

        Assert.Equal(3, entries.Count);
        Assert.Equal(PermissionKind.ShellPrefix, entries[0].Kind);
        Assert.Equal("git status", entries[0].Text);
        Assert.Equal(PermissionKind.WriteDir, entries[1].Kind);
        Assert.Equal(@"C:\proj", entries[1].Text);
        Assert.Equal(PermissionKind.Tool, entries[2].Kind);
        Assert.Equal("web_search", entries[2].Text);
    }

    [Fact]
    public void RevokeAt_RemovesExactlyTheEntryTheListingPrintedAtThatIndex()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: true);
        store.GrantWriteDir(@"C:\proj", persist: true);
        store.GrantTool("web_search", persist: true);

        var before = store.ListEntries();
        Assert.Equal(3, before.Count);

        var removed = store.RevokeAt(1);   //index 1 is the row the listing printed for the write directory.

        Assert.NotNull(removed);
        Assert.Equal(before[1], removed);
        var after = store.ListEntries();
        Assert.Equal(2, after.Count);
        Assert.DoesNotContain(after, e => e.Kind == PermissionKind.WriteDir);
        Assert.Equal(PermissionKind.ShellPrefix, after[0].Kind);
        Assert.Equal(PermissionKind.Tool, after[1].Kind);

        //a revoke must reach the saved file
        var reloaded = PermissionStore.Load(_root, _root, out _);
        Assert.False(reloaded.AllowsWrite(@"C:\proj\a.txt"));
        Assert.True(reloaded.AllowsShell("git status"));
        Assert.True(reloaded.AllowsTool("web_search"));
    }

    [Fact]
    public void RevokeAt_RemovesTheToolRow_UsingBothPrecedingKindCounts_AsTheOffset()
    {
        //the tool offset subtracts both earlier kind counts, so the fixture needs two prefixes and one write dir
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: true);
        store.GrantShellPrefix("ls", persist: true);
        store.GrantWriteDir(@"C:\proj", persist: true);
        store.GrantTool("web_search", persist: true);

        var before = store.ListEntries();
        Assert.Equal(4, before.Count);

        var removed = store.RevokeAt(3);   //index 3 is the row the listing printed for the tool.

        Assert.NotNull(removed);
        Assert.Equal(PermissionKind.Tool, removed!.Kind);
        Assert.Equal("web_search", removed.Text);
        Assert.Equal(before[3], removed);

        //the other kinds must still allow, so a wrong-slot removal shows here
        Assert.False(store.AllowsTool("web_search"));
        Assert.True(store.AllowsShell("git status --short"));
        Assert.True(store.AllowsShell("ls -la"));
        Assert.True(store.AllowsWrite(@"C:\proj\a.txt"));

        var after = store.ListEntries();
        Assert.Equal(3, after.Count);
        Assert.DoesNotContain(after, e => e.Kind == PermissionKind.Tool);

        var reloaded = PermissionStore.Load(_root, _root, out _);
        Assert.False(reloaded.AllowsTool("web_search"));
        Assert.True(reloaded.AllowsShell("git status --short"));
        Assert.True(reloaded.AllowsWrite(@"C:\proj\a.txt"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(999)]
    public void RevokeAt_OutOfRange_ReturnsNull_NothingChanges(int index)
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: true);
        store.GrantWriteDir(@"C:\proj", persist: true);
        store.GrantTool("web_search", persist: true);
        var before = File.ReadAllText(PermsPath);

        Assert.Null(store.RevokeAt(index));
        Assert.Equal(3, store.ListEntries().Count);
        Assert.Equal(before, File.ReadAllText(PermsPath));
    }

    [Fact]
    public void RevokeAt_NeverTouchesWild()
    {
        var store = PermissionStore.Load(_root, _root, out _);
        store.SetWild(true, persist: true);
        store.GrantShellPrefix("git status", persist: true);

        store.RevokeAt(0);

        Assert.True(store.Wild);
        var reloaded = PermissionStore.Load(_root, _root, out _);
        Assert.True(reloaded.Wild);
    }

    [Fact]
    public void RevokeAt_IsIndexBased_SequentialRevokesRemoveExactlyOneEachTime()
    {
        //a revoke must remove by index, since removal by value would delete every duplicate in the same kind
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantShellPrefix("git status", persist: true);
        store.GrantShellPrefix("npm install", persist: true);
        store.GrantShellPrefix("ls", persist: true);

        var before = store.ListEntries();
        Assert.Equal(3, before.Count);
        var formerEntry1 = before[1];   //entry 1 holds the npm install prefix

        var removed0 = store.RevokeAt(0);
        Assert.Equal(before[0], removed0);

        var afterFirstRevoke = store.ListEntries();
        Assert.Equal(2, afterFirstRevoke.Count);
        Assert.Equal(formerEntry1, afterFirstRevoke[0]);   //the surviving entries shift by exactly one slot.

        var removed1 = store.RevokeAt(0);   //index 0 now names the entry that shifted down.
        Assert.Equal(formerEntry1, removed1);
        Assert.Single(store.ListEntries());
        Assert.Equal(before[2], store.ListEntries()[0]);   //the last surviving entry is the ls prefix
    }

    [Fact]
    public void GrantWriteDir_AliasedByTrailingSeparatorAndCasing_NeverEntersTheListTwice()
    {
        //one directory spelled two ways must never make two entries, so dedup uses the same normalization as matching and revoking
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantWriteDir(@"c:\proj", persist: true);
        store.GrantWriteDir(@"C:\Proj\", persist: true);   //this is the same directory in another spelling.

        Assert.Single(store.ListEntries());   //the alias must not enter the list as a second row.

        var removed = store.RevokeAt(0);

        Assert.NotNull(removed);
        Assert.Empty(store.ListEntries());
        Assert.False(store.AllowsWrite(@"C:\proj\a.txt"));
        var reloaded = PermissionStore.Load(_root, _root, out _);
        Assert.False(reloaded.AllowsWrite(@"C:\proj\a.txt"));
    }

    [Fact]
    public void GrantWriteDir_EmptyOrWhitespace_IsANoOp_DoesNotThrow()
    {
        //the add side must stop before Path.GetFullPath, which throws on an empty path
        var store = PermissionStore.Load(_root, _root, out _);
        store.GrantWriteDir(@"C:\proj", persist: true);

        store.GrantWriteDir("", persist: true);
        store.GrantWriteDir("   ", persist: true);

        Assert.Single(store.ListEntries());                //a blank grant adds no row.
        Assert.True(store.AllowsWrite(@"C:\proj\a.txt"));  //a blank grant must not disturb the standing grant.
    }
}
