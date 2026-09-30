using Gatto.Cli;

namespace Gatto.Tests;

//the image refusal names the one-key fix when the encoder already sits beside the model, so nobody hunts for another model
public class VisionFixHintTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "gatto-t14hint-" + Guid.NewGuid().ToString("N"));

    public VisionFixHintTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (Exception) { } //best effort, a failed delete is fine here
        GC.SuppressFinalize(this);
    }

    private string Make(string name)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, [0x47, 0x47, 0x55, 0x46]);
        return p;
    }

    [Fact]
    public void THE_FIX_IS_NAMED_WHEN_THE_ENCODER_IS_RIGHT_THERE()
    {
        //the plain refusal is true but useless here, the fix is one config key and the encoder is already on disk
        var model = Make("qwen3-vl-8b-Q4_K_M.gguf");
        Make("mmproj-qwen3-vl-8b-f16.gguf");

        var hint = UnmanagedSession.VisionFixBesideTheModel(model);

        Assert.NotNull(hint);
        Assert.Contains("mmproj-qwen3-vl-8b-f16.gguf", hint, StringComparison.Ordinal);
        //named exactly as it is typed into profile.json, that's the next thing the user does
        Assert.Contains("\"mmproj\"", hint, StringComparison.Ordinal);
        Assert.Contains("profile.json", hint, StringComparison.Ordinal);
    }

    [Fact]
    public void NO_ENCODER_MEANS_NO_HINT_rather_than_advice_to_add_a_key_naming_nothing()
    {
        //a hint that names nothing must not fire, it sounds specific while pointing at a file the user doesn't have
        Assert.Null(UnmanagedSession.VisionFixBesideTheModel(Make("qwen3-8b-Q4_K_M.gguf")));
        Assert.Null(UnmanagedSession.VisionFixBesideTheModel(null));
        Assert.Null(UnmanagedSession.VisionFixBesideTheModel("   "));
        Assert.Null(UnmanagedSession.VisionFixBesideTheModel(Path.Combine(_dir, "gone", "x.gguf")));
    }

    [Fact]
    public void THE_HINT_BLAMES_THE_PACK_and_not_the_model()
    {
        //the cause is ours, wording that reads as a deficiency of the model is wrong and discouraging
        var model = Make("gemma-4-26b-Q6_K.gguf");
        Make("mmproj-gemma-4-26b-f16.gguf");

        var hint = UnmanagedSession.VisionFixBesideTheModel(model)!;

        foreach (var banned in new[] { "unsupported", "can't", "cannot", "unable" })
            Assert.DoesNotContain(banned, hint, StringComparison.OrdinalIgnoreCase);
    }
}
