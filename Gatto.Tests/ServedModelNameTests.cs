using Gatto.Core.Models;
using Gatto.Repl.Input;
using Gatto.Repl.Term;
using Gatto.Terminal;

namespace Gatto.Tests;

public class ServedModelNameTests
{
    private static readonly Theme T = new(new TermCaps(false, false));

    [Theory]
    [InlineData(@"C:\models\Qwen3.5-9B-Q4_K_M.gguf", "Qwen3.5-9B-Q4_K_M")]
    [InlineData("/home/x/weights/model-00001-of-00003.GGUF", "model-00001-of-00003")]
    [InlineData("Qwen3.5-9B-Q4_K_M.gguf", "Qwen3.5-9B-Q4_K_M")]
    public void A_gguf_path_shows_as_its_file_name_without_the_extension(string id, string shown) =>
        Assert.Equal(shown, ServedModelName.Display(id));

    //a slash in a cloud id names the provider, and a dot inside a name is no extension, so both show whole
    [Theory]
    [InlineData("openai/gpt-4.1")]
    [InlineData("Qwen/Qwen2.5-7B-Instruct")]
    [InlineData("qwen3.8")]
    [InlineData("")]
    public void Any_other_model_string_shows_as_it_is(string id) =>
        Assert.Equal(id, ServedModelName.Display(id));

    [Fact]
    public void The_banner_names_a_gguf_path_by_its_file_name()
    {
        var rows = string.Join("\n", Gatto.Repl.Repl.BannerItem(T, "coder", @"C:\models\Qwen-9B.gguf", "v1", GlyphSet.Unicode)
            .Render(120, T, GlyphSet.Unicode));
        Assert.Contains("Qwen-9B", rows, StringComparison.Ordinal);
        Assert.DoesNotContain(".gguf", rows, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\models", rows, StringComparison.Ordinal);
    }

    [Fact]
    public void The_model_notice_names_a_gguf_path_by_its_file_name() =>
        Assert.Equal("model: Qwen-9B", Gatto.Repl.Repl.ModelLine(@"C:\models\Qwen-9B.gguf"));

    [Fact]
    public void The_status_line_names_a_gguf_path_by_its_file_name()
    {
        var status = new StatusInfo(@"C:\proj", @"C:\models\Qwen-9B.gguf", "coder", new CtxState(), @"C:\Users\x");
        var line = InputFrame.BuildStatusLine(status, 120, T, GlyphSet.Unicode);
        Assert.Contains("Qwen-9B", line, StringComparison.Ordinal);
        Assert.DoesNotContain(".gguf", line, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\models", line, StringComparison.Ordinal);
    }
}
