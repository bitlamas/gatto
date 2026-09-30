using Gatto.Cli.Setup;
using Gatto.Cli.Setup.Tui;
using Gatto.Core.Acquire;
using Gatto.Core.Models;
using Gatto.Terminal;
using Gatto.Tests.Fakes;

namespace Gatto.Tests.Setup;

//the publisher slot is the one control, and Enter there opens the picker
public class PublisherPickerTests
{
    private static ShelfRow Row(string id) => new(
        id, id.Split('/')[0], new HubQuant("m-Q4_K_M.gguf", 4_000_000_000, null),
        FitRegime.FitsGpu, 262144, false, null, 900, false, Params: 3_000_000_000);

    private static (WizardProbes Probes, SetupFlow Flow, WizardScreen.Choice Shelf) OnTheShelf()
    {
        var probes = new WizardProbes
        {
            Rows = [Row("unsloth/a"), Row("unsloth/b")],
            Curated = "unsloth",
        };
        var flow = new SetupFlow(probes);
        return (probes, flow, Assert.IsType<WizardScreen.Choice>(flow.StartPastEngine()));
    }

    private static IReadOnlyList<Region> Ring(WizardScreen.Choice c) =>
        Shelf.Regions(c.Shelf!, 100, 0, c.Door is not null);

    //the tab stop must reach the publisher slot and show the focus mark, since a stop with no visible state is not a control
    [Fact]
    public void TAB_REACHES_THE_PUBLISHER_SLOT_AND_THE_SLOT_WEARS_THE_MARK()
    {
        var (_, _, shelf) = OnTheShelf();

        Assert.Contains(Region.Publisher, Ring(shelf));

        var focused = Shelf.Chips(shelf.Shelf!, 0, Region.Publisher, 100, GlyphSet.Unicode).Text;
        var elsewhere = Shelf.Chips(shelf.Shelf!, 0, Region.List, 100, GlyphSet.Unicode).Text;

        //the mark is the shelf's own HereOf, Prompt plus one space, so the assertion spells it as a row does
        Assert.Contains($"{GlyphSet.Unicode.Prompt} publisher", focused, StringComparison.Ordinal);
        Assert.DoesNotContain($"{GlyphSet.Unicode.Prompt} publisher", elsewhere, StringComparison.Ordinal);
        //the mark comes from the gap inside the row, so the row end stays flush at the right edge.
        Assert.Equal(elsewhere.TrimEnd().Length, focused.TrimEnd().Length);
    }

    //the local shelf's folder slot must stay out of the focus ring, since it names a folder and opens nothing.
    [Fact]
    public void THE_LOCAL_SHELFS_FOLDER_SLOT_IS_NOT_A_STOP()
    {
        var local = new ShelfView([Row("a/b")], null, Gatto.Core.Hardware.MachineShape.Discrete,
            Families: ["gemma", "all"], Family: "all", Source: ShelfSource.Local, Folder: @"D:\m");

        Assert.False(local.HasPublisherSlot);
        Assert.DoesNotContain(Region.Publisher, Shelf.Regions(local, 100, 0, hasDoor: true));
    }

    //a verb guard alone passes a change that adds a footer key, so assert the count too
    [Fact]
    public void THE_FOOTER_SAYS_pick_ON_THE_SLOT_AND_GROWS_NO_KEY()
    {
        var (_, _, shelf) = OnTheShelf();
        var ring = Ring(shelf);

        IReadOnlyList<FooterKey> KeysAt(Region r) => Shelf.Keys(
            new FocusRing([.. ring], r), ShelfSource.Hub, "leave", searchKey: true,
            GlyphSet.Unicode, lift: true, back: true);

        Assert.Equal("pick", KeysAt(Region.Publisher).Single(k => k.Key == "Enter").Verb);
        Assert.Equal("next", KeysAt(Region.List).Single(k => k.Key == "Enter").Verb);

        //eight keys pinned as a literal, since comparing the two regions passes when both gain one key
        Assert.Equal(8, KeysAt(Region.Publisher).Count);
        Assert.Equal(8, KeysAt(Region.List).Count);
    }

    //the picker lists exactly the approved publishers, and a pick re-searches as the one chosen
    [Fact]
    public void THE_PICKER_LISTS_THE_APPROVED_PUBLISHERS_AND_ANSWERS_A_PICK()
    {
        var (probes, flow, _) = OnTheShelf();

        var picker = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlPublisher));
        Assert.Equal(SetupFlow.PublisherKey, picker.Key);
        foreach (var p in probes.ApprovedPublishers())
            Assert.Contains(picker.Options, o => o.Key == SetupFlow.PickPublisher + p);

        var back = Assert.IsType<WizardScreen.Choice>(
            flow.Answer(SetupFlow.PickPublisher + "bartowski"));

        //the search request is what matters, since a shelf filtered only in view shows a different list under the same label
        Assert.Equal("bartowski", probes.LastRequest?.Search is null
            ? probes.LastRequest?.Publisher : null);
        Assert.Equal(SetupFlow.SearchKey, back.Key);
        //the slot must also show the chosen publisher, since that is the half the user reads.
        Assert.Equal("bartowski", back.Shelf?.CuratedPublisher);
    }

    //the wide row must keep its search over every approved publisher, or a tidy-up drops the feature by accident
    [Fact]
    public void THE_PICKER_KEEPS_THE_ROAD_TO_EVERY_APPROVED_PUBLISHER()
    {
        var (probes, flow, _) = OnTheShelf();
        flow.Answer(SetupFlow.CtlPublisher);

        var back = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.PickEveryPublisher));

        Assert.Equal(HubSearchView.Broadened, probes.LastRequest?.View);
        Assert.Null(probes.LastRequest?.Publisher);
        Assert.Null(back.Shelf?.CuratedPublisher);
    }

    //a screen the user can leave must be answered, so its Esc returns to the shelf
    [Fact]
    public void ESC_FROM_THE_PICKER_RETURNS_TO_THE_SHELF()
    {
        var (_, flow, _) = OnTheShelf();
        flow.Answer(SetupFlow.CtlPublisher);

        var back = Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.BackKey));

        Assert.Equal(SetupFlow.SearchKey, back.Key);
    }

    //the plain face has f as its only publisher input, so deleting it would leave that face with no publisher path
    [Fact]
    public void f_OPENS_THE_SAME_PICKER_THE_SLOT_DOES()
    {
        var (_, flow, _) = OnTheShelf();

        Assert.Contains(ShelfControls.All, c => c.Key == 'f' && c.Answer == SetupFlow.CtlPublisher);
        Assert.Equal(SetupFlow.PublisherKey,
            Assert.IsType<WizardScreen.Choice>(flow.Answer(SetupFlow.CtlPublisher)).Key);
    }

    //a control stays on the shelf and never pushes a back step, so opening the picker is a screen answer
    [Fact]
    public void OPENING_THE_PICKER_IS_A_SCREEN_ANSWER_NOT_A_CONTROL()
    {
        //the key works and leaves the screen, so it is no control even though All lists it
        Assert.Contains(ShelfControls.All, c => c.Answer == SetupFlow.CtlPublisher);
        Assert.False(ShelfControls.IsControl(SetupFlow.CtlPublisher));
        //these positive assertions stop a predicate that answers nothing from passing the negative check above.
        Assert.True(ShelfControls.IsControl(SetupFlow.CtlLift));
        Assert.True(ShelfControls.IsControl(SetupFlow.CtlSort));
        Assert.True(ShelfControls.IsControl(ShelfControls.FamilyAnswer("gemma")));
    }
}
