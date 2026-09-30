namespace Gatto.Cli;

//what a launch does about a local server that isn't answering
internal enum AutoServeAction
{
    //no message and no start: any non-interactive launch, any endpoint gatto doesn't serve, or a model whose owner said no
    Nothing,
    //tell the user the server is down and how to start it
    Hint,
    //start it, and an absent auto_serve means this too (a local process isn't a network call, so absent can't mean no)
    Serve,
}

//null consent means yes only where a human is there, false is a standing no. a scripted run builds no prompter, so it never starts a server
internal static class AutoServe
{
    //canAsk means a human is at the terminal (a redirected stdin gets the hint), and a connect endpoint is somebody else's server
    public static AutoServeAction Decide(
        bool interactive, bool gattoServesThisEndpoint, bool serverAnswered, bool? consent, bool canAsk)
    {
        if (!interactive || !gattoServesThisEndpoint) return AutoServeAction.Nothing;
        if (serverAnswered) return AutoServeAction.Nothing;

        return consent switch
        {
            true => AutoServeAction.Serve,
            //the hint stays for a declined auto_serve, silence here would read as gatto being broken
            false => AutoServeAction.Hint,
            //an absent auto_serve means yes, but only where somebody is at the terminal
            null => canAsk ? AutoServeAction.Serve : AutoServeAction.Hint,
        };
    }
}
