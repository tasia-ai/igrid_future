using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;

namespace TasiaAddons.InternalScriptRpc;

internal static class RpcHttpClientFactory
{
    internal static HttpClient Create(bool loopbackOnly)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            SslOptions = new SslClientAuthenticationOptions
            {
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.Online
            },
            UseProxy = false
        };

        if (loopbackOnly)
        {
            // Connect only to this machine while retaining the URI hostname for
            // SNI and normal certificate hostname validation.
            handler.ConnectCallback = static async (context, cancellationToken) =>
            {
                var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, context.DnsEndPoint.Port),
                        cancellationToken).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            };
        }

        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }
}
