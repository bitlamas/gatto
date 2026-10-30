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

    private static readonly FooterKey[] TaskKeys = [new("Enter", "detail"), new("x", "stop"), new("Esc", "close")];
    private static readonly FooterKey[] ShellDetailKeys = [new("x", "stop"), new("Esc", "back")];

    private static PanelFields Tasks(string count = "2 shells") => new("tasks", TaskKeys, Count: count, LabelWhole: true, SearchWhenLong: false);

    private static List<SelectOption> Shells() =>
        [new("1  running 12s", Key: "1", Columns: ["npm run build"]), new("2  ended · killed, waiting for the model", Key: "2", Columns: ["sleep 30"])];

    private static List<DetailRow> Output(int lines) =>
        [new("npm run build"), .. Enumerable.Range(1, lines).Select(i => new DetailRow($"line {i}", Gutter: i == 1 ? "⎿ " : "  ", Dim: true))];

    private static PanelView Detail(IReadOnlyList<DetailRow> rows, int offset = int.MaxValue, IReadOnlyList<FooterKey>? keys = null) =>
        new(0, [0, 1], 0, "", false, 0, null, null, null, keys ?? ShellDetailKeys, "1", "1  running 12s", rows, offset);

    [Fact]
    public void A_detail_draws_the_title_the_rows_label_its_rows_end_first_and_its_keys()
    {
        var frame = Compose(Tasks(), Shells(), Detail(Output(30)), room: 12, numbered: false);
        var rows = Plain(frame);

        Assert.Equal("  tasks · 2 shells", rows[0]);
        Assert.Equal("  1  running 12s", rows[1]);
        Assert.Equal("", rows[2]);
        Assert.StartsWith("  ↑ ", rows[3]);
        Assert.Equal("    line 30", rows[^3]);   //the end shows first, and no mark below it
        Assert.Equal("", rows[^2]);
        Assert.Equal("  x stop   Esc back", rows[^1].TrimEnd());
        Assert.Equal(12, rows.Count);
        Assert.Equal("1", frame.Layout.Detail);
        Assert.Equal(4, frame.Layout.BodyStart);
        Assert.Equal(6, frame.Layout.BodyCount);
    }

    [Fact]
    public void A_detail_at_an_offset_shows_both_marks_and_never_exceeds_the_room()
    {
        foreach (var room in new[] { 5, 6, 8, 12, 30 })
        {
            var rows = Plain(Compose(Tasks(), Shells(), Detail(Output(40), offset: 10), room: room, numbered: false));
            Assert.True(rows.Count <= room, $"room {room}: {rows.Count} rows");
        }
        var mid = Plain(Compose(Tasks(), Shells(), Detail(Output(40), offset: 10), room: 12, numbered: false));
        Assert.Contains("  ↑ 10 more", mid);
        Assert.Contains(mid, r => r.StartsWith("  ↓ ", StringComparison.Ordinal));
    }

    [Fact]
    public void A_detail_at_a_three_row_body_draws_one_mark_and_two_rows()
    {
        var rows = Plain(Compose(Tasks(), Shells(), Detail(Output(20)), room: 5, numbered: false));

        Assert.Equal(["  tasks · 2 shells", "  ↑ 19 more", "    line 19", "    line 20", "  x stop   Esc back"], rows.Select(r => r.TrimEnd()));
    }

    [Fact]
    public void A_detail_row_longer_than_the_width_is_cut_and_its_inks_follow_the_row()
    {
        var rows = new List<DetailRow> { new("echo " + new string('a', 120)), new("oops", Warn: true), new("quiet", Dim: true) };
        var frame = Compose(Tasks(), Shells(), Detail(rows), room: 12, w: 80, numbered: false);

        Assert.True(UnicodeWidth.Of(TermText.StripAnsiForWidth(frame.Rows[3])) <= 80);
        Assert.EndsWith("…", TermText.StripAnsiForWidth(frame.Rows[3]).TrimEnd());
        Assert.Contains(T.Paint("oops", Theme.Warn), frame.Rows[4]);
        Assert.Contains(T.Paint("quiet", Theme.Dim), frame.Rows[5]);
    }

    [Fact]
    public void A_detail_with_no_keys_of_its_own_names_Esc_back_and_never_the_moves()
    {
        var rows = Plain(Compose(Tasks(), Shells(), Detail(Output(40)) with { Keys = null }, room: 10, numbered: false));

        Assert.Equal("  Esc back", rows[^1].TrimEnd());
    }

    [Fact]
    public void The_cursor_rows_keys_draw_in_place_of_the_panels()
    {
        var watchKeys = new FooterKey[] { new("Enter", "detail"), new("Esc", "close") };
        var shell = Plain(Compose(Tasks(), Shells(), View(2), numbered: false));
        var watch = Plain(Compose(Tasks(), Shells(), View(2, cursor: 1) with { Keys = watchKeys }, numbered: false));

        Assert.Equal("  ↑↓ move   Enter detail   x stop   Esc close", shell[^1].TrimEnd());
        Assert.Equal("  ↑↓ move   Enter detail   Esc close", watch[^1].TrimEnd());
    }

    [Fact]
    public void A_count_the_caller_composed_draws_as_given()
    {
        Assert.Equal("  tasks · watch", Plain(Compose(Tasks("watch"), Shells(), View(2), numbered: false))[0]);
        Assert.Equal("  tasks · 1 shell · watch", Plain(Compose(Tasks("1 shell · watch"), Shells(), View(2), numbered: false))[0]);
    }

    //a whole label column keeps the state readable at 80 and the command takes the cut
    [Fact]
    public void A_whole_label_column_lays_a_long_label_whole_at_80_and_the_column_takes_the_cut()
    {
        var shells = Shells();
        shells[0] = shells[0] with { Columns = ["npm run build -- --configuration Release --verbosity detailed --no-restore --framework net10.0"] };
        var rows = Plain(Compose(Tasks(), shells, View(2), w: 80, numbered: false));
        var capped = Plain(Compose(Tasks() with { LabelWhole = false }, shells, View(2), w: 80, numbered: false));

        Assert.Contains("2  ended · killed, waiting for the model", rows[3]);
        Assert.EndsWith("…", rows[2].TrimEnd());
        Assert.DoesNotContain("waiting for the model", capped[3]);
    }

    //a command row wraps so it reads whole, an output row beside it is cut, and the window counts the wrapped rows
    [Fact]
    public void A_wrapping_command_row_wraps_at_the_width_and_the_window_counts_its_lines()
    {
        var command = string.Join(" ", Enumerable.Range(1, 40).Select(i => $"word{i:00}"));
        var rows = new List<DetailRow> { new(command, Wrap: true), new("out " + new string('=', 200), Gutter: "⎿ ", Dim: true) };
        var frame = Compose(Tasks(), Shells(), Detail(rows), room: 30, w: 80, numbered: false);
        var plain = Plain(frame);

        var wrapped = plain.Skip(frame.Layout.BodyStart).Take(frame.Layout.BodyCount).ToList();
        Assert.True(wrapped.Count >= 4, $"{wrapped.Count} rows");
        Assert.Equal(command, string.Join(" ", wrapped.Take(wrapped.Count - 1).Select(r => r.Trim())));
        Assert.All(wrapped.Take(wrapped.Count - 1), r => Assert.True(UnicodeWidth.Of(r) <= 78, r));
        Assert.StartsWith("  ⎿ out ===", wrapped[^1]);
        Assert.EndsWith("…", wrapped[^1].TrimEnd());
        Assert.Equal(wrapped.Count, frame.Layout.BodyTotal);

        var narrow = Compose(Tasks(), Shells(), Detail(rows), room: 6, w: 80, numbered: false);
        Assert.Equal(frame.Layout.BodyTotal, narrow.Layout.BodyTotal);
        Assert.Contains(Plain(narrow), r => r.StartsWith("  ↑ ", StringComparison.Ordinal));
    }

    private static PanelFields ToolsFields() =>
        new("tools", [new("Enter", "detail"), new("Esc", "close")], CountUnit: "armed", LabelInk: Theme.ToolName, DetailEndFirst: false);

    private static SelectOption Ext(string name, string description, string file) =>
        new(name, Description: description, Key: name, Trail: [new SelectRun(file, Theme.Dim)]);

    private static List<SelectOption> NineBuiltIns() =>
        [.. Gatto.Repl.Commands.ToolsPanel.Rows([.. new[] { "read_file", "write_file", "edit_file", "glob", "grep", "shell", "memory_write", "recall_memory", "run_agent" }
            .Select(n => new ToolInfo(n, "unused", null))])];

    [Fact]
    public void A_label_ink_paints_the_rows_off_the_cursor_and_the_cursor_row_keeps_the_tint()
    {
        var options = new List<SelectOption> { new("glob", Description: "find files by pattern"), new("grep", Description: "search file contents") };
        var rows = Compose(ToolsFields(), options, View(2), numbered: false).Rows;

        Assert.Contains(T.Paint("grep", Theme.ToolName), rows[3], StringComparison.Ordinal);
        Assert.Contains(T.Paint("glob", Theme.Accent), rows[2], StringComparison.Ordinal);
        Assert.DoesNotContain(T.Paint("grep", Theme.Bright), rows[3], StringComparison.Ordinal);
    }

    [Fact]
    public void A_description_and_a_trail_are_two_cells_apart()
    {
        var rows = Plain(Compose(ToolsFields(), [Ext("web_search", "Search the web.", "web_search.csx")], View(1), numbered: false));

        Assert.Equal("❯ web_search  Search the web.  web_search.csx", rows[2].TrimEnd());
    }

    [Fact]
    public void A_description_past_its_budget_ends_in_the_ellipsis_and_the_trail_stands_whole_inside_the_margin()
    {
        var gloss = "Ask the user 1-4 multiple-choice questions (2-4 options each; user can always type a free-text answer).";
        var options = new List<SelectOption> { new("recall_memory", Description: "search past sessions and memory"), Ext("ask_user", gloss, "ask_user.csx") };
        var row = Plain(Compose(ToolsFields(), options, View(2), numbered: false))[3].TrimEnd();

        Assert.EndsWith("…  ask_user.csx", row);
        Assert.True(UnicodeWidth.Of(row) <= Margins.Inside(80), $"{UnicodeWidth.Of(row)} cells: {row}");
    }

    [Fact]
    public void The_nine_built_in_glosses_fit_whole_at_80_columns()
    {
        var options = NineBuiltIns();
        var rows = Plain(Compose(ToolsFields(), options, View(options.Count), room: 30, numbered: false));

        foreach (var o in options)
            Assert.Contains(rows, r => r.Contains(o.Label, StringComparison.Ordinal) && r.TrimEnd().EndsWith(o.Description!, StringComparison.Ordinal));
        Assert.DoesNotContain(rows, r => r.Contains('…'));
    }

    [Fact]
    public void A_filtered_title_keeps_the_unit()
    {
        var options = Enumerable.Range(0, 12).Select(i => new SelectOption($"tool_{i}", Description: "gloss")).ToList();
        var rows = Plain(Compose(ToolsFields(), options, View(12, draft: "mem", shown: [3, 5]), numbered: false));

        Assert.Equal("  tools · 2 of 12 armed", rows[0]);
    }

    [Fact]
    public void A_trail_with_no_description_keeps_the_cells_it_had()
    {
        var options = new List<SelectOption> { new("high"), new("low", Trail: [new SelectRun("default", Theme.Dim)]) };
        var rows = Plain(Compose(new PanelFields("effort", [new("Esc", "close")]), options, View(2)));

        Assert.Equal("  2. low   default", rows[3].TrimEnd());
    }

    [Fact]
    public void A_negative_description_budget_draws_the_label_its_gap_and_the_trail_alone()
    {
        var options = new List<SelectOption> { new("recall_memory", Description: "search past sessions and memory"), Ext("web_fetch", "Fetch a URL.", "web_search.csx") };
        var rows = Plain(Compose(ToolsFields(), options, View(2), w: 30, numbered: false));

        Assert.Equal("  web_fetch     web_search.csx", rows[3].TrimEnd());
        Assert.DoesNotContain(rows, r => r.Contains("Fetch", StringComparison.Ordinal));
    }

    private static readonly FooterKey[] EscOnly = [new("Esc", "close")];

    private static PanelFields Help() => new("help", [new("Enter", "compose"), .. EscOnly], Tabs:
    [
        new PanelTab("commands", [new("/help", Description: "this screen")]),
        new PanelTab("shortcuts", [], Keys: EscOnly),
        new PanelTab("extensions", [], Keys: EscOnly),
    ]);

    private static PanelView Text(int tab, IReadOnlyList<DetailRow> rows) =>
        new(tab, [], -1, "", false, 0, null, null, null, EscOnly, "tab:x", null, rows, 0, Cursorless: true);

    [Fact]
    public void A_cursorless_tab_draws_its_rows_as_text_under_the_tabbed_title_with_its_keys_and_no_move()
    {
        var rows = Plain(Compose(Help(), [], Text(1, [new DetailRow("Enter      send"), new DetailRow("Esc        stop")]), numbered: false));

        Assert.Equal("  help    commands    shortcuts    extensions", rows[0].TrimEnd());
        Assert.Equal("", rows[1]);
        Assert.Equal("  Enter      send", rows[2]);
        Assert.Equal("  Esc        stop", rows[3]);
        Assert.Equal("  ←→ tabs   Esc close", rows[^1].TrimEnd());
        Assert.DoesNotContain(rows, r => r.Contains("move", StringComparison.Ordinal));
    }

    [Fact]
    public void The_empty_extensions_tab_draws_its_one_dim_row()
    {
        var f = Compose(Help(), [], Text(2, [new DetailRow("no extensions in the home", Dim: true)]), numbered: false);

        Assert.Equal("  no extensions in the home", Plain(f)[2]);
        Assert.Contains(T.Paint("no extensions in the home", Theme.Dim), f.Rows[2]);
    }

    //the state word rides before the description in its own ink, and the gap alone separates them
    [Fact]
    public void A_marked_row_draws_the_word_in_its_ink_then_the_gap_then_the_description()
    {
        var options = new List<SelectOption>
        {
            new("ask_user", Description: "tool ask_user", Key: "ask_user"),
            new("web_search", Description: "tools web_search, web_fetch", Key: "web_search", Mark: new SelectRun("off", Theme.Dim)),
            new("broken", Key: "broken", Mark: new SelectRun("failed", Theme.Warn)),
        };
        var f = Compose(new PanelFields("help", EscOnly), options, View(3), numbered: false);
        var rows = Plain(f);

        Assert.Equal("  web_search  off  tools web_search, web_fetch", rows[3].TrimEnd());
        Assert.Equal("  broken      failed", rows[4].TrimEnd());
        Assert.Contains(T.Paint("off", Theme.Dim), f.Rows[3]);
        Assert.Contains(T.Paint("failed", Theme.Warn), f.Rows[4]);
    }

    private static readonly FooterKey[] GrantKeys = [new("r", "revoke"), new("Esc", "close")];

    [Fact]
    public void A_label_header_stands_over_the_label_cell_and_the_header_words_over_the_columns()
    {
        var panel = new PanelFields("permissions", GrantKeys, LabelHeader: "kind", Header: ["target"]);
        List<SelectOption> grants = [new("shell", Columns: ["git status"]), new("write", Columns: [@"C:\work"]), new("tool", Columns: ["web_search"])];
        var rows = Plain(Compose(panel, grants, View(3)));

        Assert.Equal("     kind   target", rows[2]);
        Assert.Equal(rows[3].IndexOf("shell", StringComparison.Ordinal), rows[2].IndexOf("kind", StringComparison.Ordinal));
        Assert.Equal(rows[3].IndexOf("git status", StringComparison.Ordinal), rows[2].IndexOf("target", StringComparison.Ordinal));
    }

    [Fact]
    public void A_label_header_wider_than_the_label_cell_is_cut_to_it_and_the_column_words_stay_over_their_cells()
    {
        var panel = new PanelFields("permissions", GrantKeys, LabelHeader: "permission kind", Header: ["target"]);
        List<SelectOption> grants = [new("tool", Columns: ["web_search"]), new("tool", Columns: ["ask_user"])];
        var rows = Plain(Compose(panel, grants, View(2)));

        Assert.Equal("     per…  target", rows[2]);
        Assert.Equal(rows[3].IndexOf("web_search", StringComparison.Ordinal), rows[2].IndexOf("target", StringComparison.Ordinal));
    }

    [Fact]
    public void A_header_with_more_words_than_the_columns_prints_over_the_columns_and_never_over_the_label()
    {
        var panel = new PanelFields("model", ModelKeys, Header: ["ctx", "file", "size"]);
        var rows = Plain(Compose(panel, Models(3), View(3)));

        Assert.Equal(rows[3].IndexOf("96k", StringComparison.Ordinal), rows[2].IndexOf("ctx", StringComparison.Ordinal));
        Assert.Equal(rows[3].IndexOf("File-0", StringComparison.Ordinal), rows[2].IndexOf("file", StringComparison.Ordinal));
        Assert.DoesNotContain("size", rows[2]);
        Assert.Equal("", rows[2][..rows[2].IndexOf("ctx", StringComparison.Ordinal)].Trim());
    }
}
