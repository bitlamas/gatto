using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Gatto.Tests.Sandbox;

//an https listener on the loopback with a self-signed certificate, answering 200 to every request until disposed
internal sealed class LoopbackHttps : IDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2 _cert;
    private readonly CancellationTokenSource _stop = new();

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    private LoopbackHttps(TcpListener listener, X509Certificate2 cert)
    {
        _listener = listener;
        _cert = cert;
        _ = Task.Run(AcceptAsync);
    }

    public static LoopbackHttps Start()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=127.0.0.1", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        //schannel on the server side wants a key it can reach through a store, which an ephemeral one is not
        var cert = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pfx), null);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return new LoopbackHttps(listener, cert);
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                await using var ssl = new SslStream(client.GetStream());
                await ssl.AuthenticateAsServerAsync(_cert);
                var buffer = new byte[4096];
                var seen = new StringBuilder();
                while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var n = await ssl.ReadAsync(buffer, _stop.Token);
                    if (n == 0) return;
                    seen.Append(Encoding.ASCII.GetString(buffer, 0, n));
                }
                await ssl.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), _stop.Token);
            }
            catch (Exception) { }   //a client that hangs up mid-handshake fails only its own request
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _cert.Dispose();
    }
}
