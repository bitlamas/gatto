using System.Net;
using System.Net.Sockets;

namespace Gatto.Tests.Fakes;

//a port that is free right now, so a refused-connection fixture is fine, but never rely on it for a guaranteed bind
internal static class FreePort
{
    public static int Next()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }
}
