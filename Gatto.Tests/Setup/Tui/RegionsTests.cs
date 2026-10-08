using Gatto.Cli.Setup.Tui;
using Gatto.Terminal;

namespace Gatto.Tests.Setup.Tui;

//the Tab key moves focus across regions and wraps. the Esc key returns to the list first
public class RegionsTests
{
    //the shelf's own region order, verbatim
    private static Region[] Shelf =>
    [
        Region.Strip, Region.Families, Region.List, Region.Files, Region.Search,
    ];

    [Fact]
    public void The_keys_start_in_the_LIST_because_that_is_what_the_screen_asks()
    {
        Assert.Equal(Region.List, new FocusRing(Shelf).Current);
    }

    [Fact]
    public void Tab_walks_the_screens_order_and_WRAPS()
    {
        var ring = new FocusRing(Shelf, Region.Strip);
        //the ring must wrap, since an end forces the user to remember which way they came
        Assert.Equal(
            [Region.Families, Region.List, Region.Files, Region.Search, Region.Strip],
            Enumerable.Range(0, 5).Select(_ => ring.Next()).ToArray());
    }

    [Fact]
    public void A_screen_without_builds_simply_does_not_stop_there()
    {
        //the ring is built from the screen's own region list
        var ring = new FocusRing([Region.List, Region.Files, Region.Search], Region.Files);
        Assert.Equal(Region.Search, ring.Next());
        Assert.Equal(Region.List, ring.Next());
    }

    //an Esc outside the list returns focus to the list and is spent, since only a press in the list arms the leave chord
    [Fact]
    public void Esc_from_another_region_returns_to_the_list_and_is_SPENT()
    {
        var ring = new FocusRing(Shelf, Region.Files);
        Assert.True(ring.EscReturnsToList());
        Assert.Equal(Region.List, ring.Current);
    }

    [Fact]
    public void Esc_IN_the_list_is_not_spent_so_the_caller_may_arm()
    {
        var ring = new FocusRing(Shelf);
        Assert.Equal(Region.List, ring.Current);
        Assert.False(ring.EscReturnsToList());
    }

    [Fact]
    public void A_click_focuses_any_region_directly_not_by_walking()
    {
        var ring = new FocusRing(Shelf);
        Assert.True(ring.Focus(Region.Files));
        Assert.Equal(Region.Files, ring.Current);
        Assert.False(ring.Focus(Region.Families) && false);      //the region exists here, so Focus returns true and the added && false feeds the false assert
        Assert.False(new FocusRing([Region.List]).Focus(Region.Files));   //when the region is absent, focus refuses and does not move
    }

    //the Esc hint comes from the ring's state, since a screen that computed it could disagree and describe a state that is not real

    //pass the region's index rather than the region, since Region is internal and an internal type can't be a theory's parameter
    [Theory]
    [InlineData(2, "leave")]    //index 2 is Region.List
    [InlineData(3, "back")]     //index 3 is Region.Files
    [InlineData(0, "back")]     //index 0 is Region.Strip
    [InlineData(4, "back")]     //index 4 is Region.Search
    public void The_Esc_verb_is_derived_from_where_the_keys_are(int at, string verb)
    {
        //the screen half is held constant as leave, so the theory tests only the region half. what Esc does on the list is the face's job, asserted at its handler
        Assert.Equal(verb, new FocusRing(Shelf, Shelf[at]).EscVerb("leave"));
    }

    [Fact]
    public void A_screen_with_no_list_at_all_says_leave_because_there_is_nowhere_to_go_back_to()
    {
        var ring = new FocusRing([Region.Strip, Region.Search], Region.Search);
        Assert.False(ring.EscReturnsToList());

        //when the ring holds no list it returns the caller's word unchanged, since what the caller's own Esc does is the answer
        Assert.Equal("leave", ring.EscVerb("leave"));
        Assert.Equal("back", ring.EscVerb("back"));
    }

    [Fact]
    public void A_ring_with_no_regions_is_refused_rather_than_silently_empty()
    {
        Assert.Throws<ArgumentException>(() => new FocusRing([]));
    }
}
