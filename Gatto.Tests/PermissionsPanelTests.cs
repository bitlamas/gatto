using Gatto.Core.Loop.Permissions;
using Gatto.Repl.Commands;
using Gatto.Terminal;

namespace Gatto.Tests;

//the /permissions rows and words as pure functions of the store's entries: the order, the keys, the count, the subtitle and the revoked line
public class PermissionsPanelTests
{
    private static readonly PermissionEntry[] Three =
    [
        new(PermissionKind.ShellPrefix, "git status"),
        new(PermissionKind.WriteDir, @"C:\work"),
        new(PermissionKind.Tool, "web_search"),
    ];

    [Fact]
    public void Three_grants_of_the_three_kinds_are_three_rows_in_store_order_with_the_kind_and_the_text()
    {
        var rows = PermissionsPanel.Rows(Three);

        Assert.Equal(["shell", "write", "tool"], rows.Select(r => r.Label));
        Assert.Equal(["git status", @"C:\work", "web_search"], rows.Select(r => Assert.Single(r.Columns!)));
        Assert.Equal(3, rows.Select(r => r.Key).Distinct().Count());
    }

    [Fact]
    public void A_shell_prefix_and_a_tool_with_one_text_keep_two_keys()
    {
        var rows = PermissionsPanel.Rows([new(PermissionKind.ShellPrefix, "grep"), new(PermissionKind.Tool, "grep")]);

        Assert.NotEqual(rows[0].Key, rows[1].Key);
    }

    [Fact]
    public void The_count_takes_its_plural()
    {
        Assert.Equal("1 grant", PermissionsPanel.Count(1));
        Assert.Equal("3 grants", PermissionsPanel.Count(3));
    }

    [Fact]
    public void The_subtitle_states_wild_while_it_is_on_and_nothing_off()
    {
        Assert.Equal("wild on · prompts off this session", PermissionsPanel.Subtitle(true, GlyphSet.Unicode));
        Assert.Equal("wild on . prompts off this session", PermissionsPanel.Subtitle(true, GlyphSet.Ascii));
        Assert.Null(PermissionsPanel.Subtitle(false, GlyphSet.Unicode));
    }

    [Fact]
    public void The_revoked_line_is_the_typed_forms_with_the_kind_padded_to_six()
    {
        Assert.Equal(@"revoked: write C:\work", PermissionsPanel.Revoked(Three[1]));
        Assert.Equal("revoked: " + CommandText.FormatKindAndText(PermissionKind.Tool, "web_search"), PermissionsPanel.Revoked(Three[2]));
    }
}
