namespace Gatto.Cli;

//a local model's window is what its server gives one request, which is less than the profile's context when the server splits it across slots
internal static class ModelWindow
{
    //the smaller of the two wins, and the line says so only when the server's is smaller. a server that answered nothing leaves the profile's
    public static ContextResolution Resolve(int declared, int? servedNCtx) =>
        servedNCtx is int n && n > 0 && n < declared
            ? new(n, $"server reports {n}. Using it (the profile says {declared})")
            : new(declared, null);
}
