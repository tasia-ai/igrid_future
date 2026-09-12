using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using OSHttpServer;

namespace OpenSim.Framework.Servers.Tests
{
    [TestFixture]
    public class OSHttpListenerTests
    {
        [Test]
        public async Task ExceptionFromOneAcceptedConnectionDoesNotStopAcceptLoop()
        {
            int port = GetFreeTcpPort();
            OSHttpListener listener = OSHttpListener.Create(IPAddress.Loopback, port);
            var secondAccepted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int acceptedCount = 0;

            listener.Accepted += (_, _) =>
            {
                if (Interlocked.Increment(ref acceptedCount) == 1)
                    throw new InvalidOperationException("intentional per-connection failure");

                secondAccepted.TrySetResult(true);
            };

            try
            {
                listener.Start(8);
                using var first = new TcpClient();
                await first.ConnectAsync(IPAddress.Loopback, port);
                await WaitUntilAsync(() => Volatile.Read(ref acceptedCount) >= 1, TimeSpan.FromSeconds(2));

                using var second = new TcpClient();
                await second.ConnectAsync(IPAddress.Loopback, port);

                Assert.That(
                    await CompletesWithinAsync(secondAccepted.Task, TimeSpan.FromSeconds(2)),
                    Is.True,
                    "The accept loop stopped after a single connection handler threw");
            }
            finally
            {
                ForceStop(listener);
            }
        }

        [Test]
        public async Task StalledTlsClientDoesNotBlockFollowingTlsClient()
        {
            int port = GetFreeTcpPort();
            using X509Certificate2 certificate = CreateCertificate();
            OSHttpListener listener = OSHttpListener.Create(IPAddress.Loopback, port, certificate, SslProtocols.Tls12);
            using var stalledClient = new TcpClient();

            try
            {
                listener.Start(8);
                await stalledClient.ConnectAsync(IPAddress.Loopback, port);
                await Task.Delay(750); // Let the server enter the first TLS handshake.

                using var healthyClient = new TcpClient();
                await healthyClient.ConnectAsync(IPAddress.Loopback, port);
                using var tls = new SslStream(healthyClient.GetStream(), false, (_, _, _, _) => true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));

                Assert.DoesNotThrowAsync(async () =>
                    await tls.AuthenticateAsClientAsync("localhost", null, SslProtocols.Tls12, false)
                        .WaitAsync(timeout.Token));
            }
            finally
            {
                stalledClient.Close();
                ForceStop(listener);
            }
        }

        [Test]
        public async Task StalledTlsHandshakeIsClosedByServerTimeout()
        {
            int port = GetFreeTcpPort();
            using X509Certificate2 certificate = CreateCertificate();
            OSHttpListener listener = OSHttpListener.Create(IPAddress.Loopback, port, certificate, SslProtocols.Tls12);
            using var stalledClient = new TcpClient();

            try
            {
                listener.Start(8);
                await stalledClient.ConnectAsync(IPAddress.Loopback, port);
                var buffer = new byte[1];
                using var testTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));

                int bytesRead = await stalledClient.GetStream().ReadAsync(buffer, testTimeout.Token);
                Assert.That(bytesRead, Is.Zero, "Server did not close a stalled TLS handshake");
            }
            finally
            {
                stalledClient.Close();
                ForceStop(listener);
            }
        }

        [Test]
        public async Task StopClosesActiveHttpConnections()
        {
            int port = GetFreeTcpPort();
            OSHttpListener listener = OSHttpListener.Create(IPAddress.Loopback, port);
            using var client = new TcpClient();

            try
            {
                listener.Start(8);
                await client.ConnectAsync(IPAddress.Loopback, port);
                await Task.Delay(100);
                listener.Stop();

                var buffer = new byte[1];
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                int bytesRead = await client.GetStream().ReadAsync(buffer, timeout.Token);
                Assert.That(bytesRead, Is.Zero, "Listener shutdown left an active HTTP socket open");
            }
            finally
            {
                client.Close();
                ForceStop(listener);
            }
        }

        [Test]
        public void TimeoutManagerIsReferenceCountedAcrossListeners()
        {
            Type managerType = typeof(ContextTimeoutManager);
            var threadField = managerType.GetField("m_internalThread", BindingFlags.Static | BindingFlags.NonPublic)!;
            var usersField = managerType.GetField("m_userCount", BindingFlags.Static | BindingFlags.NonPublic)!;
            int baselineUsers = (int)usersField.GetValue(null)!;

            OSHttpListener first = OSHttpListener.Create(IPAddress.Loopback, GetFreeTcpPort());
            OSHttpListener second = OSHttpListener.Create(IPAddress.Loopback, GetFreeTcpPort());
            try
            {
                first.Start(4);
                second.Start(4);
                Assert.That((int)usersField.GetValue(null)!, Is.EqualTo(baselineUsers + 2));

                first.Stop();
                Assert.That((int)usersField.GetValue(null)!, Is.EqualTo(baselineUsers + 1));
                Assert.That(((Thread)threadField.GetValue(null)!).IsAlive, Is.True,
                    "Stopping one listener killed the timeout manager used by another listener");

                second.Stop();
                Assert.That((int)usersField.GetValue(null)!, Is.EqualTo(baselineUsers));
            }
            finally
            {
                ForceStop(first);
                ForceStop(second);
            }
        }

        private static X509Certificate2 CreateCertificate()
        {
            using RSA rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
            request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));
            request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
            return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        }

        private static int GetFreeTcpPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (!condition() && DateTime.UtcNow < deadline)
                await Task.Delay(10);
            Assert.That(condition(), Is.True, "Condition was not reached before timeout");
        }

        private static async Task<bool> CompletesWithinAsync(Task task, TimeSpan timeout)
        {
            Task completed = await Task.WhenAny(task, Task.Delay(timeout));
            return ReferenceEquals(completed, task);
        }

        private static void ForceStop(OSHttpListener listener)
        {
            Type type = typeof(OSHttpListener);
            var shutdown = (ManualResetEvent?)type.GetField("m_shutdownEvent", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(listener);
            var tcp = (TcpListener?)type.GetField("m_listener", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(listener);
            var factory = (IHttpContextFactory?)type.GetField("m_contextFactory", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(listener);

            try { listener.m_CancellationSource.Cancel(); } catch (ObjectDisposedException) { }
            try { shutdown?.Set(); } catch (ObjectDisposedException) { }
            tcp?.Stop();
            factory?.Shutdown();
        }
    }
}
