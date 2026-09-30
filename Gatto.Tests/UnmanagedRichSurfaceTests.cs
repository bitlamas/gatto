//these tests drive the rich dispatch, a guard proven on the plain path says nothing about it. both /model call sites need their own test
using Gatto.Tests.Fakes;

namespace Gatto.Tests;

[Collection("e2e")]
public class UnmanagedRichSurfaceTests
{
    private const string Notice = "this session talks to http://127.0.0.1:1234 — models apply to gatto-managed servers";

    [Fact]
    public async Task SlashModel_OnAUnmanagedSession_SaysWhatTheSessionIs_ON_SCREEN()
    {
        var h = new RichReplHarness(Path.GetTempPath(), unmanagedNotice: Notice);
        h.Keys.Line("/model gemma-4-26b").Line("/quit");

        await h.RunAsync();

        var screen = h.ScreenText();
        //assert on the rendered frame, an exit code would pass a run that dispatched nothing
        Assert.Contains("models apply to gatto-managed servers", screen);
        Assert.Contains("127.0.0.1:1234", screen);
        //the frame must not send a connect session to build a model it would not use
        Assert.DoesNotContain("model new", screen);
    }

    [Fact]
    public async Task SlashModel_Bare_OnAUnmanagedSession_NeverOpensThePicker()
    {
        //keep the unmanaged refusal ahead of the picker, or the empty-list advice paints instead
        var h = new RichReplHarness(Path.GetTempPath(), unmanagedNotice: Notice);
        h.Keys.Line("/model").Line("/quit");

        await h.RunAsync();

        var screen = h.ScreenText();
        Assert.Contains("models apply to gatto-managed servers", screen);
        Assert.DoesNotContain("no models", screen);
    }

    [Fact]
    public async Task A_PACK_SESSION_IS_UNTOUCHED_by_any_of_this()
    {
        //with no notice the unmanaged refusal must stay silent and /model keeps its normal path
        var h = new RichReplHarness(Path.GetTempPath());
        h.Keys.Line("/model gemma-4-26b").Line("/quit");

        await h.RunAsync();

        var screen = h.ScreenText();
        Assert.DoesNotContain("models apply to gatto-managed servers", screen);
    }

    //image refusals on an unmanaged session, wait on the event rather than the loop's exit (a queued /quit merges into the composer)

    private static string CwdWithPng()
    {
        var dir = Directory.CreateTempSubdirectory("gatto-vision-").FullName;
        File.WriteAllBytes(Path.Combine(dir, "shot.png"),
            [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 0x01]);
        return dir;
    }

    [Fact]
    public async Task AnImageAgainstAVisionlessConnectServer_IsRefusedNamingTheSERVER()
    {
        //the connect session needs the server's vision bit, without it the refusal can't fire and the attach dies with the server's 500
        var h = new RichReplHarness(CwdWithPng(), visionAvailable: false, unmanagedNotice: Notice,
            unmanagedVisionNotice: Gatto.Cli.UnmanagedSession.VisionUnavailable("http://127.0.0.1:1234"));
        h.Keys.Line("look at shot.png");

        await h.RunUntilAsync(() => h.Saw("can't see images"));

        var screen = h.ScreenText();
        Assert.Contains("127.0.0.1:1234", screen);          //the server answered, so its address is what the refusal names
        Assert.DoesNotContain("/model to one that can", screen);   //an unmanaged session cannot act on /model advice, so the refusal leaves it out
    }

    [Fact]
    public async Task AN_UNKNOWN_VISION_BIT_IS_NOT_A_NO_the_attach_goes()
    {
        //a probe that could not answer leaves the bit unknown, and unknown must not read as a no
        var h = new RichReplHarness(CwdWithPng(), visionAvailable: null, unmanagedNotice: Notice,
            unmanagedVisionNotice: Gatto.Cli.UnmanagedSession.VisionUnavailable("http://127.0.0.1:1234"));
        //waiting for the model's words is what proves the attach went, a refused one runs no turn
        h.Client.EnqueueTurn(new Gatto.Core.Client.StreamEvent.TextDelta("i see it"),
            new Gatto.Core.Client.StreamEvent.Finished("stop", null));
        h.Keys.Line("look at shot.png");

        await h.RunUntilAsync(() => h.Saw("i see it"));

        Assert.DoesNotContain("can't see images", h.ScreenText());
    }

    [Fact]
    public async Task A_PACK_SESSION_KEEPS_ITS_OWN_VISION_SENTENCE()
    {
        //a model session keeps the /model advice, there /model is the way out
        var h = new RichReplHarness(CwdWithPng(), visionAvailable: false);
        h.Keys.Line("look at shot.png");

        await h.RunUntilAsync(() => h.Saw("can't see images"));

        var screen = h.ScreenText();
        Assert.Contains("/model to one that can", screen);
        Assert.DoesNotContain("127.0.0.1:1234", screen);
    }

}
