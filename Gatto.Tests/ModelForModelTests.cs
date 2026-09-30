using Gatto.Core.Acquire;
using Gatto.Core.Home;

namespace Gatto.Tests;

//one folder per model under the weights root, and ForModel is the one answer to where a model's files go
public class ModelForModelTests
{
    [Fact]
    public void ForModel_nests_the_model_under_the_default_weights_root()
    {
        Assert.Equal(Path.Combine(@"C:\home", "weights", "gemma-4-e4b-it"),
            ModelLocation.ForModel(@"C:\home", null, "gemma-4-e4b-it"));
    }

    [Fact]
    public void ForModel_nests_under_a_CONFIGURED_weights_dir_when_there_is_one()
    {
        //the configured weights dir replaces the root, and every model still gets one folder inside it
        Assert.Equal(Path.Combine(@"D:\weights", "gemma-4-e4b-it"),
            ModelLocation.ForModel(@"C:\home", @"D:\weights", "gemma-4-e4b-it"));
    }

    //the suggested directory is the root, and the whole weights tree survives an uninstall
    [Fact]
    public void SuggestedDir_still_answers_the_ROOT_not_a_model_folder()
    {
        Assert.Equal(Path.Combine(@"C:\home", "weights"), ModelLocation.SuggestedDir(@"C:\home", null));
    }

    //the id becomes a path segment, so a crafted general.name must not escape the weights root
    [Theory]
    [InlineData(@"..\x")]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData(@"a\b")]
    [InlineData("")]
    [InlineData("   ")]
    public void ForModel_REFUSES_an_id_that_is_not_a_safe_single_segment(string hostile)
    {
        var ex = Assert.Throws<GattoConfigException>(
            () => ModelLocation.ForModel(@"C:\home", null, hostile));
        Assert.Contains("model id", ex.Message, StringComparison.Ordinal);
    }

    //the folder builder and the scaffold must refuse the same ids, or a model scaffolds and cannot be located
    [Theory]
    [InlineData(@"..\x")]
    [InlineData("a/b")]
    [InlineData("")]
    public void ForModel_and_the_scaffold_refuse_the_SAME_ids(string hostile)
    {
        Assert.Throws<GattoConfigException>(() => ModelLocation.ForModel(@"C:\home", null, hostile));
        Assert.Throws<GattoConfigException>(() => Gatto.Core.Models.ModelId.Validate(hostile));
    }

    [Fact]
    public void A_normal_derived_id_is_accepted_by_both()
    {
        //the known match for the theory above, refusing everything would satisfy it without an id both accept
        Gatto.Core.Models.ModelId.Validate("gemma-4-26b-a4b-it");
        Assert.EndsWith("gemma-4-26b-a4b-it",
            ModelLocation.ForModel(@"C:\home", null, "gemma-4-26b-a4b-it"), StringComparison.Ordinal);
    }
}
