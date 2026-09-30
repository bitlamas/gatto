using Gatto.Cli;

namespace Gatto.Tests;

//the oracle is byte-level, the output differs from the input by exactly the appended entry
public class UserPathTests
{
    private const string Entry = @"C:\Users\me\AppData\Local\Programs\gatto";

    [Fact]
    public void APPEND_DIFFERS_FROM_THE_INPUT_BY_EXACTLY_THE_NEW_ENTRY()
    {
        //strip the appended tail and what remains must be the original, byte for byte
        var before = @"%USERPROFILE%\bin;C:\Program Files\Git\cmd;C:\tools\;;D:\quoted path\bin";

        var after = UserPath.Append(before, Entry)!;

        Assert.Equal(before + ";" + Entry, after);
        Assert.Equal(before, after[..^(Entry.Length + 1)]);
    }

    [Fact]
    public void UNEXPANDED_VARIABLES_SURVIVE_because_they_are_the_whole_point_of_being_there()
    {
        //read and write the PATH without expanding variables, otherwise the user's own names are baked in
        var before = @"%USERPROFILE%\bin;%LOCALAPPDATA%\Microsoft\WindowsApps";

        var after = UserPath.Append(before, Entry)!;

        Assert.StartsWith(before, after, StringComparison.Ordinal);
        Assert.Contains("%USERPROFILE%", after, StringComparison.Ordinal);
        Assert.Contains("%LOCALAPPDATA%", after, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"C:\a;;C:\b")]              //a doubled separator that came from the user
    [InlineData(@"  C:\a  ;C:\b")]           //padding inside a segment
    [InlineData(@"C:\a;C:\b;")]              //a trailing separator, which is legal and common
    public void NOTHING_ELSE_IS_TIDIED_UP(string before)
    {
        var after = UserPath.Append(before, Entry)!;

        //whatever oddity was already there stays there, tidying it would be bytes this function doesn't own
        Assert.StartsWith(before, after, StringComparison.Ordinal);
        Assert.EndsWith(Entry, after, StringComparison.Ordinal);
    }

    [Fact]
    public void A_TRAILING_SEPARATOR_IS_NOT_DOUBLED()
    {
        Assert.Equal(@"C:\a;" + Entry, UserPath.Append(@"C:\a;", Entry));
    }

    [Fact]
    public void AN_EMPTY_PATH_GETS_THE_ENTRY_ALONE_with_no_leading_separator()
    {
        Assert.Equal(Entry, UserPath.Append("", Entry));
    }

    [Fact]
    public void ALREADY_PRESENT_MEANS_WRITE_NOTHING()
    {
        //null means there is nothing to write, an unchanged string would still cost a registry write
        Assert.Null(UserPath.Append(@"C:\a;" + Entry + @";C:\b", Entry));
        Assert.Null(UserPath.Append(Entry.ToUpperInvariant(), Entry));       //the comparison is case-insensitive, as the filesystem is
        Assert.Null(UserPath.Append(Entry + @"\", Entry));                    //a trailing slash is the same folder
        Assert.Null(UserPath.Append(@"C:\a; " + Entry + " ;C:\\b", Entry));   //padding around the entry doesn't change it
    }

    [Fact]
    public void A_LOOKALIKE_ENTRY_IS_NOT_THE_SAME_ENTRY()
    {
        //compare whole segments, a substring test would treat gatto-old as gatto and skip the install
        var before = @"C:\tools\gatto-old;C:\tools\gattox";

        var after = UserPath.Append(before, @"C:\tools\gatto");

        Assert.NotNull(after);
        Assert.Equal(before + @";C:\tools\gatto", after);
    }

    [Fact]
    public void REMOVE_TAKES_EXACTLY_OURS_and_leaves_the_rest_byte_intact()
    {
        var before = @"%USERPROFILE%\bin;" + Entry + @";C:\Program Files\Git\cmd";

        var after = UserPath.Remove(before, Entry)!;

        Assert.Equal(@"%USERPROFILE%\bin;C:\Program Files\Git\cmd", after);
    }

    [Fact]
    public void REMOVE_KEEPS_A_TRAILING_SEPARATOR_that_was_already_there()
    {
        Assert.Equal(@"C:\a;", UserPath.Remove(@"C:\a;" + Entry + ";", Entry));
    }

    [Fact]
    public void REMOVING_WHAT_IS_NOT_THERE_WRITES_NOTHING()
    {
        Assert.Null(UserPath.Remove(@"C:\a;C:\b", Entry));
    }

    [Fact]
    public void A_BLANK_ENTRY_IS_NEVER_APPENDED_OR_REMOVED()
    {
        //a blank entry must never be appended, it would put a bare separator in the user's PATH
        Assert.Null(UserPath.Append(@"C:\a", ""));
        Assert.Null(UserPath.Append(@"C:\a", "   "));
        Assert.Null(UserPath.Remove(@"C:\a", ""));
    }
}

//the plan is a function of the read, so a failed read can be checked without the registry
public class PathInstallerPlanTests
{
    private const string Entry = @"C:\Users\me\AppData\Local\Programs\gatto";

    [Fact]
    public void AN_UNREADABLE_PATH_WRITES_NOTHING()
    {
        //an unreadable PATH must not be treated as empty, that would replace the user's whole PATH with one entry
        Assert.Null(PathInstaller.Plan(null, wasExpandable: false, Entry));
        Assert.Null(PathInstaller.PlanRemoval(null, wasExpandable: false, Entry));
    }

    [Fact]
    public void AN_EMPTY_PATH_IS_A_LEGAL_VALUE_and_is_NOT_the_unreadable_case()
    {
        //null is unreadable and must not be written, an empty string is a legal PATH and may be
        var write = PathInstaller.Plan("", wasExpandable: false, Entry);

        Assert.NotNull(write);
        Assert.Equal(Entry, write!.Value.Value);
    }

    [Fact]
    public void A_PATH_WITH_VARIABLES_GOES_BACK_AS_AN_EXPANDABLE_VALUE()
    {
        //write it back as an expandable value, a plain string stops the variables in the PATH working
        var write = PathInstaller.Plan(@"%USERPROFILE%\bin", wasExpandable: true, Entry);

        Assert.True(write!.Value.Expandable);
        Assert.Contains("%USERPROFILE%", write.Value.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void A_PLAIN_PATH_THAT_GAINS_NO_VARIABLES_STAYS_PLAIN()
    {
        var write = PathInstaller.Plan(@"C:\tools", wasExpandable: false, Entry);

        Assert.False(write!.Value.Expandable);
    }

    [Fact]
    public void ALREADY_ON_PATH_IS_A_NO_OP_so_a_re_run_writes_nothing()
    {
        Assert.Null(PathInstaller.Plan(@"C:\a;" + Entry, wasExpandable: false, Entry));
    }
}
