//the shortcuts tab must keep its glosses aligned, rank the wild toggle fourth and name only keys the keymap binds
using System.Linq;
using Gatto.Repl;
using Gatto.Repl.Commands;
using Gatto.Repl.Input;
using Gatto.Terminal;

namespace Gatto.Tests;

public class ShortcutsTests
{
    private static string[] Rows(GlyphSet? g = null) => [.. HelpPanel.Shortcuts(g ?? GlyphSet.Unicode).Select(r => r.Text)];

    //the column is counted in cells, since a char count would misalign rows with arrows
    private static int GlossColumn(string row, IEnumerable<string> glosses)
    {
        var d = glosses.First(x => row.EndsWith(x, StringComparison.Ordinal));
        return UnicodeWidth.Of(row[..row.LastIndexOf(d, StringComparison.Ordinal)]);
    }

    private static IEnumerable<string> Glosses() =>
        Shortcuts.AllOf(GlyphSet.Unicode).Select(s => s.What).Append(HelpPanel.WildGloss);

    [Fact]
    public void The_glosses_line_up_in_one_column_in_both_glyph_sets()
    {
        Assert.Single(Rows().Select(r => GlossColumn(r, Glosses())).Distinct());
        Assert.Single(Rows(GlyphSet.Ascii).Select(r => GlossColumn(r, Shortcuts.AllOf(GlyphSet.Ascii).Select(s => s.What).Append(HelpPanel.WildGloss))).Distinct());
    }

    //a toggle a user reaches for ranks above a key they discover, so the wild row sits after the focus move and before the scroll row
    [Fact]
    public void The_tab_holds_the_four_keys_and_the_wild_toggle_fourth_keyed_on_its_constant()
    {
        var rows = Rows();
        Assert.Equal(5, rows.Length);
        Assert.StartsWith(WildKeys.KeyName + " ", rows[3], StringComparison.Ordinal);
        Assert.EndsWith(HelpPanel.WildGloss, rows[3], StringComparison.Ordinal);
        Assert.StartsWith(FocusKeys.MoveKeyNameOf(GlyphSet.Unicode), rows[2], StringComparison.Ordinal);
        Assert.StartsWith("PgUp", rows[4], StringComparison.Ordinal);
        Assert.All(HelpPanel.Shortcuts(GlyphSet.Unicode), r => Assert.False(r.Wrap || r.Dim));
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
