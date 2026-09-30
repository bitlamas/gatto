using Gatto.Cli.Setup.Tui;

namespace Gatto.Tests.Setup.Tui;

//one typed row decides by shape, there is no mode key, so the text alone has to say what was typed
public class TypedDoorTests
{
    private static string K(string s) => TypedDoor.Classify(s).ToString();

    [Theory]
    //paths, unmistakable by shape
    [InlineData(@"C:\x\llama.cpp\llama-server.exe", "Path")]
    [InlineData(@"C:\Users\you\Downloads", "Path")]
    [InlineData(@"..\weights", "Path")]
    [InlineData("~/models", "Path")]
    [InlineData("gemma-4-26B-A4B-it-Q4_K_M.gguf", "Path")]
    //repo ids, one forward slash and URL-safe only
    [InlineData("unsloth/gemma-4-e4b-it-GGUF", "RepoId")]
    [InlineData("bartowski/Qwen3-8B-GGUF", "RepoId")]
    //hub urls
    [InlineData("https://huggingface.co/unsloth/gemma-4-e4b-it-GGUF", "Url")]
    [InlineData("https://huggingface.co/unsloth/x-GGUF/blob/main/x.gguf", "Url")]
    //addresses, the connect screens
    [InlineData("http://127.0.0.1:1235", "Address")]
    [InlineData("http://localhost:8080/v1", "Address")]
    [InlineData("127.0.0.1:11434", "Address")]
    //numbers, commas included because the context screen prints them
    [InlineData("65536", "Number")]
    [InlineData("65,536", "Number")]
    //everything else is a search
    [InlineData("gemma vision", "Search")]
    [InlineData("qwen", "Search")]
    [InlineData("", "Search")]
    [InlineData("   ", "Search")]
    public void The_door_decides_by_SHAPE(string typed, string expected) => Assert.Equal(expected, K(typed));

    //a repo id contains a forward slash and must not be read as a path. if it were, every typed repo id would look like a file the wizard cannot find
    [Fact]
    public void A_forward_slash_alone_is_NOT_evidence_of_a_path()
    {
        Assert.Equal(DoorInput.RepoId, TypedDoor.Classify("unsloth/gemma-4-e4b-it-GGUF"));
        Assert.Equal(DoorInput.Path, TypedDoor.Classify(@"unsloth\gemma-4-e4b-it-GGUF"));
    }

    //a huggingface.co URL is a model, any other http address is a server. the host decides, which keeps a local server off the model path
    [Fact]
    public void The_host_decides_between_a_model_and_a_server()
    {
        Assert.Equal(DoorInput.Url, TypedDoor.Classify("https://huggingface.co/x/y"));
        Assert.Equal(DoorInput.Address, TypedDoor.Classify("http://huggingface.internal:8080"));
    }

    //the repo-id shape is the Hub client's own, a door that accepted what the client refuses would send the user to a 400
    [Theory]
    [InlineData("has space/name")]
    [InlineData("too/many/slashes")]
    [InlineData("/leading")]
    public void The_door_refuses_repo_ids_the_HUB_CLIENT_would_refuse(string bad)
    {
        Assert.NotEqual(DoorInput.RepoId, TypedDoor.Classify(bad));
        Assert.False(Gatto.Core.Acquire.HubClient.LooksLikeRepoId(bad));
    }

    [Fact]
    public void A_real_repo_id_is_accepted_by_BOTH()
    {
        //without this, a pair that refused everything would satisfy the theory above
        Assert.True(Gatto.Core.Acquire.HubClient.LooksLikeRepoId("unsloth/x-GGUF"));
        Assert.Equal(DoorInput.RepoId, TypedDoor.Classify("unsloth/x-GGUF"));
    }

    //the draft window, the REPL composer's rule

    [Fact]
    public void A_draft_that_fits_is_shown_whole()
    {
        Assert.Equal("gemma", TypedDoor.Window("gemma", 20));
    }

    //the window is anchored to the end, where the caret is. anchored to the start it would show the beginning of what the user is typing the end of
    [Fact]
    public void A_long_draft_SCROLLS_keeping_the_caret_end_visible()
    {
        var draft = "unsloth/gemma-4-26B-A4B-it-GGUF";
        var win = TypedDoor.Window(draft, 10);
        Assert.Equal(10, Gatto.Terminal.UnicodeWidth.Of(win));
        Assert.EndsWith(win, draft, StringComparison.Ordinal);
        Assert.True(draft.EndsWith(win, StringComparison.Ordinal), "the window must be the TAIL of the draft");
    }

    [Fact]
    public void The_window_NEVER_wraps_and_never_exceeds_its_cells()
    {
        var draft = new string('x', 200);
        for (var cells = 1; cells <= 60; cells++)
            Assert.True(Gatto.Terminal.UnicodeWidth.Of(TypedDoor.Window(draft, cells)) <= cells);
    }

    //a wide glyph is never split into a half-cell, the window steps by cells so it is in or out
    [Fact]
    public void A_wide_glyph_is_never_half_shown()
    {
        //any wide CJK pair proves the property, the two characters in this workspace path would be refused by the public sync
        var draft = "漢字漢字漢字";                       //three two-cell glyphs, 12 cells
        Assert.Equal(4, Gatto.Terminal.UnicodeWidth.Of(TypedDoor.Window(draft, 5)));
        Assert.Equal("漢字", TypedDoor.Window(draft, 5));
    }
}
