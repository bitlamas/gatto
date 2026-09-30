//the shortcuts table must keep its descriptions aligned, fit a narrow window and name only keys the keymap binds
using System.Linq;
using Gatto.Repl;
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Tests;

public class ShortcutsTests
{
    private static readonly Theme T = new(new TermCaps(true, true));

    private static string[] Rows() =>
        Shortcuts.RenderRich(T, glyphs: GlyphSet.Unicode).Split('\n').Select(TermText.StripAnsiForWidth).ToArray();

    //the column is counted in cells, since a char count would misalign rows with arrows
    private static int DescriptionColumn(string row, IEnumerable<string> descriptions)
    {
        var d = descriptions.First(x => row.EndsWith(x, StringComparison.Ordinal));
        return UnicodeWidth.Of(row[..row.LastIndexOf(d, StringComparison.Ordinal)]);
    }

    private static string[] CommandRows() =>
        SlashCommands.RenderRich(T).Split('\n').Select(TermText.StripAnsiForWidth).ToArray();

    [Fact]
    public void The_descriptions_line_up_in_one_column()
    {
        //the shared column is what makes it a table
        var entries = Rows().Where(r => r.Trim().Length > 0 && r != "shortcuts").ToList();
        Assert.Equal(Shortcuts.AllOf(Gatto.Terminal.GlyphSet.Unicode).Count, entries.Count);

        var columns = entries.Select(r => DescriptionColumn(r, Shortcuts.AllOf(Gatto.Terminal.GlyphSet.Unicode).Select(s => s.What)))
                             .Distinct().ToList();
        Assert.Single(columns);
    }

    [Fact]
    public void It_shares_ONE_column_with_the_command_table_above_it()
    {
        //the two tables share one column, since two descriptions that do not line up read as a mistake
        var shortcutColumn = Assert.Single(
            Rows().Where(r => r.Trim().Length > 0 && r != "shortcuts")
                  .Select(r => DescriptionColumn(r, Shortcuts.AllOf(Gatto.Terminal.GlyphSet.Unicode).Select(s => s.What)))
                  .Distinct());
        var commandColumn = Assert.Single(
            CommandRows().Select(r => DescriptionColumn(r, SlashCommands.All.Select(c => c.Summary)))
                         .Distinct());

        Assert.Equal(commandColumn, shortcutColumn);
    }

    [Fact]
    public void No_shortcut_row_makes_help_any_wider_than_the_commands_already_do()
    {
        //the shortcuts must not make /help any wider than the command table already does, measured with GutterWrap.Hang on both sides
        var widestCommand = CommandRows().Max(r => UnicodeWidth.Of(Gatto.Repl.Render.GutterWrap.Hang + r));
        Assert.All(Rows(), r =>
        {
            var onScreen = UnicodeWidth.Of(Gatto.Repl.Render.GutterWrap.Hang + r);
            Assert.True(onScreen <= widestCommand,
                $"{onScreen} cells on screen vs the command table's {widestCommand}: {r}");
        });
    }

    [Fact]
    public void It_is_labelled_shortcuts_and_the_label_is_not_indented_like_a_row()
    {
        var rows = Rows();
        Assert.Contains("shortcuts", rows[0], StringComparison.Ordinal);
        Assert.False(rows[0].StartsWith(" ", StringComparison.Ordinal));
    }

    [Fact]
    public void Every_key_it_names_is_a_key_the_keymap_actually_binds()
    {
        //the guard checks both the binding and the advertised name, since either alone proves nothing about the other
        var keys = Shortcuts.AllOf(Gatto.Terminal.GlyphSet.Unicode).Select(s => s.Keys).ToList();
        Assert.Contains(FocusKeys.ToggleKeyName, keys);
        Assert.Contains(FocusKeys.MoveKeyNameOf(Gatto.Terminal.GlyphSet.Unicode), keys);

        Assert.Equal(FocusKey.Toggle, FocusKeys.Of(Ctrl(ConsoleKey.R)));
        Assert.Contains("Ctrl", FocusKeys.ToggleKeyName, StringComparison.Ordinal);
        Assert.Contains("R", FocusKeys.ToggleKeyName, StringComparison.Ordinal);

        Assert.Equal(FocusKey.Prev, FocusKeys.Of(Ctrl(ConsoleKey.UpArrow)));
        Assert.Equal(FocusKey.Next, FocusKeys.Of(Ctrl(ConsoleKey.DownArrow)));
        Assert.Contains("Ctrl", FocusKeys.MoveKeyNameOf(Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal);
        Assert.Contains("↑", FocusKeys.MoveKeyNameOf(Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal);
        Assert.Contains("↓", FocusKeys.MoveKeyNameOf(Gatto.Terminal.GlyphSet.Unicode), StringComparison.Ordinal);

        //the Alt+V and scroll rows have no constant to borrow, so they are pinned by name only
        Assert.Contains(keys, k => k.Contains("Alt+V", StringComparison.Ordinal));
        Assert.Contains(keys, k => k.Contains("PgUp", StringComparison.Ordinal));
    }

    private static ConsoleKeyInfo Ctrl(ConsoleKey k) =>
        new('\0', k, shift: false, alt: false, control: true);

    [Fact]
    public void The_footer_hint_is_built_from_the_same_two_constants()
    {
        //the footer and /help both build their key names from the same two constants
        Assert.Contains(FocusKeys.MoveKeyNameOf(Gatto.Terminal.GlyphSet.Unicode), FocusKeys.HintOf(glyphs: GlyphSet.Unicode), StringComparison.Ordinal);
        Assert.Contains(FocusKeys.ToggleKeyName, FocusKeys.HintOf(glyphs: GlyphSet.Unicode), StringComparison.Ordinal);
    }
}
