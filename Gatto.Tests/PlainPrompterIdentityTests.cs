using Gatto.Repl;
using Gatto.Terminal;

namespace Gatto.Tests;

//the answer must name the option identity, and this file runs serially because the plain prompter reads Console directly
[Collection("e2e")]
public sealed class PlainPrompterIdentityTests : IDisposable
{
    private readonly TextReader _origIn = Console.In;
    private readonly TextWriter _origOut = Console.Out;

    public void Dispose()
    {
        Console.SetIn(_origIn);
        Console.SetOut(_origOut);
    }

    private static WizardAsk Ask(GlyphSet set) => new("go on?",
        [new SelectOption("keep going"),
         new SelectOption(Gatto.Cli.Setup.SetupFace.BackLabelOf(set),
             Key: Gatto.Cli.Setup.SetupFace.BackOptionKey)]);

    private static string? Answer(GlyphSet set, string typed)
    {
        Console.SetOut(TextWriter.Null);
        Console.SetIn(new StringReader(typed + "\n"));
        return new ConsolePrompter().AskOne(Ask(set))?.Label;
    }

    //the back row answers the same key under both sets, though each draws it differently
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void THE_PLAIN_PROMPTER_ANSWERS_WITH_THE_KEY_UNDER_EITHER_SET(bool ascii) =>
        Assert.Equal(Gatto.Cli.Setup.SetupFace.BackOptionKey,
            Answer(ascii ? GlyphSet.Ascii : GlyphSet.Unicode, "2"));

    //an option with no key still answers its label, so a prompter that answered back for everything would fail here
    [Fact]
    public void AN_OPTION_WITHOUT_A_KEY_STILL_ANSWERS_ITS_LABEL() =>
        Assert.Equal("keep going", Answer(GlyphSet.Unicode, "1"));

    //the two sets draw that row differently, so the pair is a real discriminator
    [Fact]
    public void THE_TWO_SETS_DRAW_THE_BACK_ROW_DIFFERENTLY() =>
        Assert.NotEqual(Gatto.Cli.Setup.SetupFace.BackLabelOf(GlyphSet.Unicode),
            Gatto.Cli.Setup.SetupFace.BackLabelOf(GlyphSet.Ascii));
}
