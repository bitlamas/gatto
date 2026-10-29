using Gatto.Repl.Prompts;
using Gatto.Terminal;

namespace Gatto.Tests;

//the panel's rows, composed at a width and a room with no loop, so every shed and every mark is reachable
public class PanelComposerTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static readonly FooterKey[] ModelKeys = [new("Enter", "switch"), new("d", "default"), new("Esc", "close")];

    private static PanelFields Model(PanelUnder? under = null, string? subtitle = null) =>
        new("model", ModelKeys, Header: ["ctx", "file"], Legend: "loaded", Under: under, Subtitle: subtitle);

    private static List<SelectOption> Models(int n) =>
        [.. Enumerable.Range(0, n).Select(i => new SelectOption($"model-{i}", Columns: ["96k", $"File-{i}-Q4"]))];

    private static PanelView View(int count, int cursor = 0, string draft = "", bool inBox = false, int offset = 0,
        string? setting = null, string? sentence = null, IReadOnlyList<int>? shown = null) =>
        new(0, shown ?? [.. Enumerable.Range(0, count)], cursor, draft, inBox, offset, setting, sentence, null);

    private static List<string> Plain(PanelFrame f) => [.. f.Rows.Select(TermText.StripAnsiForWidth)];

    private static PanelFrame Compose(PanelFields panel, IReadOnlyList<SelectOption> options, PanelView view,
        int w = 80, int room = 20, bool numbered = true, GlyphSet? glyphs = null) =>
        PanelComposer.Compose(panel, options, numbered, view, w, room, T, glyphs ?? GlyphSet.Unicode);

    [Fact]
    public void A_panel_at_rest_shows_the_title_the_header_the_rows_and_the_key_row_in_order()
    {
        var rows = Plain(Compose(Model(), Models(3), View(3)));

        Assert.Equal("  model", rows[0]);
        Assert.Equal("", rows[1]);
        Assert.Equal("              ctx  file", rows[2]);
        Assert.StartsWith("❯ 1. model-0", rows[3]);
        Assert.StartsWith("  2. model-1", rows[4]);
        Assert.Equal("", rows[6]);
        Assert.StartsWith("  Enter switch   d default   Esc close", rows[7]);
        Assert.EndsWith("● loaded", rows[7].TrimEnd());
        Assert.Equal(8, rows.Count);   //three rows fit, so no box and no marks
    }

    [Fact]
    public void The_header_stands_over_its_columns()
    {
        var rows = Plain(Compose(Model(), Models(3), View(3)));

        Assert.Equal(rows[3].IndexOf("96k", StringComparison.Ordinal), rows[2].IndexOf("ctx", StringComparison.Ordinal));
        Assert.Equal(rows[3].IndexOf("File-0", StringComparison.Ordinal), rows[2].IndexOf("file", StringComparison.Ordinal));
    }

    [Fact]
    public void A_list_longer_than_the_room_shows_the_box_the_first_rows_and_a_down_mark_with_its_count()
    {
        var frame = Compose(Model(), Models(20), View(20), room: 12);
        var rows = Plain(frame);

        Assert.True(rows.Count <= 12);
        Assert.Contains("? to search", rows[1]);
        var shownRows = rows.Count(r => r.Contains("model-", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Trim() == $"↓ {20 - shownRows} more");
        Assert.DoesNotContain(rows, r => r.Contains('↑'));
        Assert.Contains(rows, r => r.StartsWith("❯ 1. model-0", StringComparison.Ordinal));
    }

    [Fact]
    public void The_cursor_at_the_end_shows_an_up_mark_and_no_down_mark()
    {
        var rows = Plain(Compose(Model(), Models(20), View(20, cursor: 19), room: 12));

        Assert.Contains(rows, r => r.Trim().StartsWith("↑ ", StringComparison.Ordinal) && r.Trim().EndsWith(" more", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Trim().StartsWith("↓ ", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.StartsWith("❯    model-19", StringComparison.Ordinal));   //past the ninth row there is no digit
    }

    //the blanks go first, then the row under the list, and the title, the box and the key row stay
    [Fact]
    public void A_room_of_seven_sheds_the_blanks_and_then_the_row_under_the_list()
    {
        var panel = Model(new PanelUnder.Sentence(_ => "a fact of the row"), subtitle: "a subtitle") with { Header = null };
        var rows = Plain(Compose(panel, Models(20), View(20, sentence: "a fact of the row"), room: 7));

        Assert.Equal(7, rows.Count);
        Assert.Equal("  model", rows[0]);
        Assert.Equal("  a subtitle", rows[1]);
        Assert.Contains("? to search", rows[2]);
        Assert.DoesNotContain("", rows);
        Assert.DoesNotContain(rows, r => r.Contains("a fact", StringComparison.Ordinal));
        Assert.StartsWith("  ", rows[^1]);
        Assert.Contains("Esc close", rows[^1]);
    }

    //the subtitle goes last, and a body of two rows draws one mark and the cursor row
    [Fact]
    public void A_room_of_five_sheds_the_subtitle_and_shows_one_mark_and_the_cursor_row()
    {
        var panel = Model(new PanelUnder.Sentence(_ => "a fact of the row"), subtitle: "a subtitle") with { Header = null };
        var rows = Plain(Compose(panel, Models(20), View(20, cursor: 10), room: 5));

        Assert.Equal(5, rows.Count);
        Assert.DoesNotContain("  a subtitle", rows);
        Assert.Equal(1, rows.Count(r => r.Contains(" more", StringComparison.Ordinal)));
        Assert.Contains(rows, r => r.StartsWith("❯", StringComparison.Ordinal));
    }

    //the frame's own cut never fires on a panel, the rows it publishes fit the room it was given
    [Fact]
    public void The_rows_never_exceed_the_room()
    {
        var panel = Model(new PanelUnder.Sentence(_ => "a fact"), subtitle: "a subtitle");
        for (var room = 5; room <= 40; room++)
            for (var cursor = 0; cursor < 30; cursor += 7)
                Assert.True(Compose(panel, Models(30), View(30, cursor: cursor, sentence: "a fact"), room: room).Rows.Count <= room,
                    $"room {room} cursor {cursor}");
    }

    [Fact]
    public void A_label_past_the_cap_is_cut_with_the_ellipsis_and_the_columns_keep_their_cells()
    {
        var options = new List<SelectOption> { new("a-very-long-model-name-that-runs-on", Columns: ["256k", "File-Q4"]) };
        var rows = Plain(Compose(Model(), options, View(1), w: 40));

        var row = rows.First(r => r.Contains("256k", StringComparison.Ordinal));
        Assert.Contains("…", row);
        Assert.DoesNotContain("runs-on", row);
        Assert.Contains("File-Q4", row);
    }

    [Fact]
    public void A_tab_strip_bands_the_active_tab()
    {
        var tabs = new PanelTab[] { new("commands", Models(2)), new("shortcuts", Models(2)) };
        var panel = new PanelFields("help", [new("Esc", "close")], Tabs: tabs);
        var frame = Compose(panel, tabs[0].Options, View(2));

        Assert.Equal("  help    commands    shortcuts", TermText.StripAnsiForWidth(frame.Rows[0]));
        var band = Ansi.Bg(T.Map(T.BandOf(Theme.Accent)), T.TrueColor);
        Assert.Contains(band + Ansi.Fg(T.Map(Theme.Accent), T.TrueColor) + "commands", frame.Rows[0]);
        Assert.DoesNotContain(band + Ansi.Fg(T.Map(Theme.Dim), T.TrueColor) + "shortcuts", frame.Rows[0]);
    }

    [Fact]
    public void The_ascii_set_draws_the_marks_with_its_own_arrows()
    {
        var rows = Plain(Compose(Model(), Models(20), View(20, cursor: 10), room: 12, glyphs: GlyphSet.Ascii));

        Assert.Contains(rows, r => r.Trim().StartsWith("^ ", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Trim().StartsWith("v ", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.StartsWith("> ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_setting_draws_its_row_and_a_null_value_draws_none()
    {
        var setting = new PanelUnder.Setting("default effort", _ => ["low", "medium"], _ => "medium");

        var shown = Plain(Compose(Model(setting), Models(3), View(3, setting: "medium")));
        Assert.Contains("  default effort medium   ←→ adjust", shown);

        var none = Plain(Compose(Model(setting), Models(3), View(3, setting: null)));
        Assert.DoesNotContain(none, r => r.Contains("adjust", StringComparison.Ordinal));
    }

    [Fact]
    public void With_the_keys_in_the_box_the_key_row_is_the_boxs()
    {
        var rows = Plain(Compose(Model(), Models(20), View(20, inBox: true), room: 12));

        Assert.Equal("  Enter list   Esc back", rows[^1].TrimEnd());
        Assert.Contains("Esc back", rows[1]);
    }

    //a numbered list names the moves once a row on screen has no digit
    [Fact]
    public void A_numbered_list_slid_past_its_ninth_row_names_the_moves_and_one_at_rest_does_not()
    {
        var slid = Plain(Compose(Model(), Models(15), View(15, cursor: 12), room: 10));
        Assert.StartsWith("  ↑↓ move   Enter switch", slid[^1]);

        var rest = Plain(Compose(Model(), Models(15), View(15, cursor: 0), room: 10));
        Assert.StartsWith("  Enter switch", rest[^1]);
    }

    [Fact]
    public void An_empty_match_says_so_and_counts_zero()
    {
        var rows = Plain(Compose(Model(), Models(20), View(20, draft: "zz", inBox: true, shown: []), room: 12));

        Assert.Contains("  nothing matches", rows);
        Assert.Contains("0 of 20", rows[1]);
        Assert.DoesNotContain(rows, r => r.Contains("ctx", StringComparison.Ordinal));   //no header stands over nothing
    }

    //each key word carries its own press target, the arrows pair none
    [Fact]
    public void The_key_rows_words_are_press_targets_and_the_pair_is_not()
    {
        var frame = Compose(Model(), Models(15), View(15, cursor: 12), room: 10);
        var keyRow = frame.Rows.Count - 1;
        var plain = TermText.StripAnsiForWidth(frame.Rows[keyRow]);

        var esc = plain.IndexOf("Esc", StringComparison.Ordinal);
        Assert.Equal(new PanelHit.Key("Esc"), frame.Layout.At(keyRow, esc));
        Assert.Null(frame.Layout.At(keyRow, plain.IndexOf("↑↓", StringComparison.Ordinal)));
    }
}
