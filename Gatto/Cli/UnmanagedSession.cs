namespace Gatto.Cli;

//what a session on somebody else's server is told, in one home so every model command refuses with the same sentence
internal static class UnmanagedSession
{
    //why /model does nothing here: say what the session is, and that models belong to servers gatto starts itself
    public static string ModelUnavailable(string? baseUrl) =>
        baseUrl is { Length: > 0 } url
            ? $"this session talks to {url}: gatto's own model settings only apply to servers it starts"
            //should never happen, since a connect endpoint always has a base_url, and even here the sentence names no model fix
            : "this session talks to a server gatto doesn't manage: gatto's own model settings "
              + "only apply to servers it starts";

    //fires only on a definite Vision false, and the sentence names the server and what the user can do from the message they hold
    public static string VisionUnavailable(string? baseUrl) =>
        baseUrl is { Length: > 0 } url
            ? $"the server at {url} reports it can't see images. Message not sent. Remove the "
              + "image tokens to send it anyway."
            : "this server reports it can't see images. Message not sent. Remove the image tokens "
              + "to send it anyway.";

    //only returns a hint when a projector really sits beside the model, null otherwise so the caller keeps its own sentence
    public static string? VisionFixBesideTheModel(string? servedModelPath)
    {
        if (string.IsNullOrWhiteSpace(servedModelPath)) return null;
        if (Gatto.Core.Acquire.ModelDiscovery.ProjectorFor(servedModelPath) is not { } projector) return null;

        //mmproj is spelled the way profile.json spells it, since the user's next move is to type it into that file
        return $"found {System.IO.Path.GetFileName(projector)} beside your model. Add "
            + "\"mmproj\" to the model's profile.json to enable vision.";
    }
}
