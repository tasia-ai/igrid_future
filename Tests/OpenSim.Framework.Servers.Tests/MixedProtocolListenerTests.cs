using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NUnit.Framework;
using OSHttpServer;

namespace OpenSim.Framework.Servers.Tests;

public class MixedProtocolListenerTests
{
    [Test]
    [Timeout(30000)]
    public async Task MixedListenerAcceptsPlainHttpAndTlsOnTheSamePort()
    {
        // Select an ephemeral loopback port to avoid test environment conflicts.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        using RSA rsa = RSA.Create(2048);
        CertificateRequest request = new(
            "CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

        using OSHttpListener listener = OSHttpListener.Create(
            IPAddress.Loopback, port, certificate);
        listener.AllowPlainHttp = true;

        var httpReceived = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var httpsReceived = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int requests = 0;
        listener.RequestReceived += (_, _) =>
        {
            int count = Interlocked.Increment(ref requests);
            if (count == 1)
                httpReceived.TrySetResult(true);
            if (count == 2)
                httpsReceived.TrySetResult(true);
        };

        listener.Start(16);
        try
        {
            byte[] get = Encoding.ASCII.GetBytes(
                "GET /hg-test HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n");

            using (var plain = new TcpClient())
            {
                await plain.ConnectAsync(IPAddress.Loopback, port);
                await plain.GetStream().WriteAsync(get);
                await httpReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            using (var secureTcp = new TcpClient())
            {
                await secureTcp.ConnectAsync(IPAddress.Loopback, port);
                // The certificate is self-signed for this local integration test only.
                using var tls = new SslStream(secureTcp.GetStream(), false,
                    (_, _, _, _) => true);
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                });
                await tls.WriteAsync(get);
                await tls.FlushAsync();
                await httpsReceived.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            Assert.That(requests, Is.EqualTo(2));
        }
        finally
        {
            listener.Stop();
        }
    }
}
