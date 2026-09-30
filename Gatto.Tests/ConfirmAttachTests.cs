//passing the image cap asks instead of refusing. byte limits miss many small screenshots that add thousands of tokens to the permanent prefix.
using Gatto.Core.Home;
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

    [Fact]
    public void No_settings_file_means_no_grant()
    {
        //the settings file must not be required. an absent file resolves to defaults and must not fail a launch.
        Assert.False(ProjectSettings.Load(_dir).AttachManyGranted);
        Assert.False(File.Exists(ProjectSettings.PathFor(_dir)));   //reading the settings must not create the file.
    }

    [Fact]
    public void A_grant_persists_and_is_read_back_by_a_fresh_load()
    {
        ProjectSettings.Load(_dir).GrantAttachMany();
        Assert.True(ProjectSettings.Load(_dir).AttachManyGranted);
    }

    [Fact]
    public void The_grant_is_scoped_to_ITS_project()
    {
        //do not ask again means this project only. a leaked grant would silently disarm the question everywhere from one answer.
        var other = Directory.CreateTempSubdirectory("gatto-settings-other-").FullName;
        try
        {
            ProjectSettings.Load(_dir).GrantAttachMany();
            Assert.False(ProjectSettings.Load(other).AttachManyGranted);
        }
        finally { try { Directory.Delete(other, recursive: true); } catch { } }
    }

    [Fact]
    public void A_corrupt_settings_file_asks_again_rather_than_locking_the_user_out()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ProjectSettings.PathFor(_dir))!);
        File.WriteAllText(ProjectSettings.PathFor(_dir), "{ this is not json");
        Assert.False(ProjectSettings.Load(_dir).AttachManyGranted);
    }

    [Fact]
    public void The_settings_file_lives_beside_permissions_but_is_NOT_permissions()
    {
        //the grant belongs in settings.json, every entry in permissions.json is a security record and a preference mixed in would tax every audit
        ProjectSettings.Load(_dir).GrantAttachMany();

        Assert.Equal(Path.Combine(_dir, ".gatto", "settings.json"), ProjectSettings.PathFor(_dir));
        Assert.False(File.Exists(Path.Combine(_dir, ".gatto", "permissions.json")));
    }
}
