//passing the image cap asks instead of refusing. byte limits miss many small screenshots that add thousands of tokens to the permanent prefix.
using Gatto.Core.Loop.Permissions;
using Gatto.Repl;

namespace Gatto.Tests;

public class ConfirmAttachTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("gatto-settings-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    [Fact]
    public void The_question_states_the_count_AND_the_token_cost()
    {
        //a bare count gives nothing to judge with, the token figure travels with it. question and notice read one constant, their numbers cannot differ
        var q = ConfirmAttach.Spec(12).Question!.Text;

        Assert.Contains("12 images", q, StringComparison.Ordinal);
        Assert.Contains("3.4k", q, StringComparison.Ordinal);   //the figure is 12 times 280, the tilde marks it approximate
        Assert.Contains("~", q, StringComparison.Ordinal);
    }

    [Fact]
    public void The_three_answers_are_offered_in_the_ruled_order()
    {
        var opts = ConfirmAttach.Spec(12).Options;
        Assert.Equal(3, opts.Count);
        Assert.Contains("Yes", opts[0].Label, StringComparison.Ordinal);
        Assert.Contains("don't ask again", opts[1].Label, StringComparison.Ordinal);
        Assert.Contains("No", opts[2].Label, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, ConfirmAttachAnswer.Yes)]
    [InlineData(1, ConfirmAttachAnswer.YesAlways)]
    [InlineData(2, ConfirmAttachAnswer.No)]
    public void Each_option_maps_to_its_answer(int index, ConfirmAttachAnswer expected) =>
        Assert.Equal(expected, ConfirmAttach.Interpret(new SelectOutcome.Chosen(index)));

    [Fact]
    public void Escape_and_anything_unexpected_mean_No()
    {
        //every ambiguous path becomes No, the answer that sends nothing and costs nothing. the message comes back intact
        Assert.Equal(ConfirmAttachAnswer.No, ConfirmAttach.Interpret(new SelectOutcome.Cancelled()));
        Assert.Equal(ConfirmAttachAnswer.No, ConfirmAttach.Interpret(new SelectOutcome.FreeText("what")));
        Assert.Equal(ConfirmAttachAnswer.No, ConfirmAttach.Interpret(new SelectOutcome.Chosen(99)));
    }

    //the standing yes lives with the grants in the home, for the folder gatto was launched in
    private PermissionStore Store(string dir) => PermissionStore.Load(_dir, dir, out _);

    [Fact]
    public void No_grant_until_one_is_given()
    {
        //reading must not create anything, an absent store resolves to no grant and must not fail a launch
        Assert.False(Store(_dir).AttachMany);
        Assert.False(File.Exists(PermissionStore.PathFor(_dir, _dir)));
    }

    [Fact]
    public void A_grant_persists_and_is_read_back_by_a_fresh_load()
    {
        Store(_dir).GrantAttachMany();
        Assert.True(Store(_dir).AttachMany);
    }

    [Fact]
    public void The_grant_is_scoped_to_ITS_project()
    {
        //do not ask again means this project only. a leaked grant would silently disarm the question everywhere from one answer.
        var other = Directory.CreateTempSubdirectory("gatto-settings-other-").FullName;
        try
        {
            Store(_dir).GrantAttachMany();
            Assert.False(Store(other).AttachMany);
        }
        finally { try { Directory.Delete(other, recursive: true); } catch { } }
    }

    [Fact]
    public void A_corrupt_store_asks_again_rather_than_locking_the_user_out()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PermissionStore.PathFor(_dir, _dir))!);
        File.WriteAllText(PermissionStore.PathFor(_dir, _dir), "{ this is not json");
        Assert.False(Store(_dir).AttachMany);
    }

    //a repository that ships the old settings file with the grant set gets nothing from it, the question is still asked
    [Fact]
    public void A_project_settings_file_grants_nothing()
    {
        Directory.CreateDirectory(Path.Combine(_dir, ".gatto"));
        File.WriteAllText(Path.Combine(_dir, ".gatto", "settings.json"), """{"attach_many_granted":true}""");
        Assert.False(Store(_dir).AttachMany);
    }
}
