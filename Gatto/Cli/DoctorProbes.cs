namespace Gatto.Cli;

//doctor's two windows on the network, so a test can drive doctor with no live call. separate from CommandContext, a network concern gets its own name
internal sealed record DoctorProbes(
    HttpMessageHandler Transport,
    Func<CancellationToken, Task<UpdateState?>>? CheckUpdate)
{
    public static DoctorProbes Production => new(
        new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(5) },
        async token =>
        {
            using var updateHttp = UpdateCheck.Client(TimeSpan.FromMilliseconds(500));
            using var read = CancellationTokenSource.CreateLinkedTokenSource(token);
            read.CancelAfter(TimeSpan.FromSeconds(1));
            //doctor wants the state only, it reports whether a newer gatto exists and never downloads
            return (await UpdateCheck.FetchAsync(updateHttp, DateTimeOffset.Now, read.Token))?.State;
        });
}
