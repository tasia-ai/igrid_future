using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace OSHttpServer
{
    public class OSHttpListener: IDisposable
    {
        private readonly IPAddress m_address;
        private readonly X509Certificate m_certificate;
        private readonly IHttpContextFactory m_contextFactory;
        private readonly int m_port;
        private readonly ManualResetEvent m_shutdownEvent = new(false);
        private readonly SslProtocols m_sslProtocols = SslProtocols.Tls | SslProtocols.Tls11 | SslProtocols.Tls12 | SslProtocols.Tls13;

        private TcpListener m_listener;
        private readonly ConcurrentDictionary<Socket, byte> m_initializingSockets = new();
        private readonly ConcurrentDictionary<int, Task> m_initializationTasks = new();
        private readonly SemaphoreSlim m_initializationSlots = new(128, 128);
        private int m_nextInitializationId;
        private Task m_acceptLoopTask;
        private ILogWriter m_logWriter = NullLogWriter.Instance;
        private volatile bool m_shutdown;
        private static readonly TimeSpan TlsHandshakeTimeout = TimeSpan.FromSeconds(10);
        public readonly CancellationTokenSource m_CancellationSource = new();
        protected RemoteCertificateValidationCallback m_clientCertValCallback = null;

        public event EventHandler<ClientAcceptedEventArgs> Accepted;
        public event ExceptionHandler ExceptionThrown;
        public event EventHandler<RequestEventArgs> RequestReceived;

        /// <summary>
        /// Listen for regular HTTP connections
        /// </summary>
        /// <param name="address">IP Address to accept connections on</param>
        /// <param name="port">TCP Port to listen on, default HTTP port is 80.</param>
        /// <param name="factory">Factory used to create <see cref="IHttpClientContext"/>es.</param>
        /// <exception cref="ArgumentNullException"><c>address</c> is null.</exception>
        /// <exception cref="ArgumentException">Port must be a positive number.</exception>
        protected OSHttpListener(IPAddress address, int port)
        {
            m_address = address;
            m_port = port;
            m_contextFactory = new HttpContextFactory(m_logWriter);
            m_contextFactory.RequestReceived += OnRequestReceived;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OSHttpListener"/> class.
        /// </summary>
        /// <param name="address">IP Address to accept connections on</param>
        /// <param name="port">TCP Port to listen on, default HTTPS port is 443</param>
        /// <param name="factory">Factory used to create <see cref="IHttpClientContext"/>es.</param>
        /// <param name="certificate">Certificate to use</param>
        protected OSHttpListener(IPAddress address, int port, X509Certificate certificate)
            : this(address, port)
        {
            m_certificate = certificate;
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="OSHttpListener"/> class.
        /// </summary>
        /// <param name="address">IP Address to accept connections on</param>
        /// <param name="port">TCP Port to listen on, default HTTPS port is 443</param>
        /// <param name="factory">Factory used to create <see cref="IHttpClientContext"/>es.</param>
        /// <param name="certificate">Certificate to use</param>
        /// <param name="protocols">which HTTPS protocol to use, default is TLS.</param>
        protected OSHttpListener(IPAddress address, int port, X509Certificate certificate, SslProtocols protocols)
            : this(address, port)
        {
            m_certificate = certificate;
            m_sslProtocols = protocols;
        }

        public static OSHttpListener Create(IPAddress address, int port)
        {
            return new OSHttpListener(address, port);
        }

        public static OSHttpListener Create(IPAddress address, int port, X509Certificate certificate)
        {
            return new OSHttpListener(address, port, certificate);
        }

        public static OSHttpListener Create(IPAddress address, int port, X509Certificate certificate, SslProtocols protocols)
        {
            return new OSHttpListener(address, port, certificate, protocols);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void OnRequestReceived(object sender, RequestEventArgs e)
        {
            RequestReceived?.Invoke(sender, e);
        }

        public RemoteCertificateValidationCallback CertificateValidationCallback
        {
            set { m_clientCertValCallback = value; }
        }

        /// <summary>
        /// Gives you a change to receive log entries for all internals of the HTTP library.
        /// </summary>
        /// <remarks>
        /// You may not switch log writer after starting the listener.
        /// </remarks>
        public ILogWriter LogWriter
        {
            get { return m_logWriter; }
            set
            {
                m_logWriter = value ?? NullLogWriter.Instance;
                if (m_certificate is not null)
                    m_logWriter.Write(this, LogPrio.Info, $"HTTPS({m_sslProtocols}) listening on {m_address}:{m_port}");
                else
                    m_logWriter.Write(this, LogPrio.Info, $"HTTP listening on {m_address}:{m_port}");
            }
        }

        /// <summary>
        /// True if we should turn on trace logs.
        /// </summary>
        public bool UseTraceLogs { get; set; }

        private async Task AcceptLoop()
        {
            try
            {
                while (!m_shutdown)
                {
                    Socket socket = null;
                    try
                    {
                        socket = await m_listener.AcceptSocketAsync(m_CancellationSource.Token).ConfigureAwait(false);
                        if (!socket.Connected)
                        {
                            socket.Dispose();
                            continue;
                        }

                        socket.NoDelay = true;

                        if (!OnAcceptingSocket(socket))
                        {
                            socket.Dispose();
                            continue;
                        }

                        if (!socket.Connected)
                        {
                            socket.Dispose();
                            continue;
                        }

                        if (!m_initializationSlots.Wait(0))
                        {
                            m_logWriter.Write(this, LogPrio.Warning,
                                $"HTTP connection initialization limit reached; rejecting {socket.RemoteEndPoint}");
                            socket.Dispose();
                            continue;
                        }

                        Socket acceptedSocket = socket;
                        socket = null;
                        m_initializingSockets.TryAdd(acceptedSocket, 0);
                        if (m_shutdown)
                        {
                            m_initializingSockets.TryRemove(acceptedSocket, out _);
                            acceptedSocket.Dispose();
                            m_initializationSlots.Release();
                            continue;
                        }

                        int initializationId = Interlocked.Increment(ref m_nextInitializationId);
                        Task initializationTask = InitializeSocketAsync(acceptedSocket);
                        m_initializationTasks[initializationId] = initializationTask;
                        _ = initializationTask.ContinueWith(
                            _ => m_initializationTasks.TryRemove(initializationId, out _),
                            CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously,
                            TaskScheduler.Default);
                    }
                    catch (OperationCanceledException) when (m_shutdown)
                    {
                        break;
                    }
                    catch (ObjectDisposedException) when (m_shutdown)
                    {
                        break;
                    }
                    catch (Exception err)
                    {
                        socket?.Dispose();
                        m_logWriter.Write(this, LogPrio.Error, $"Failed to accept or initialize HTTP connection: {err}");
                        try
                        {
                            ExceptionThrown?.Invoke(this, err);
                        }
                        catch (Exception eventError)
                        {
                            m_logWriter.Write(this, LogPrio.Error, $"HTTP exception handler failed: {eventError}");
                        }
                    }
                }
            }
            finally
            {
                m_shutdownEvent.Set();
            }
        }

        private async Task InitializeSocketAsync(Socket socket)
        {
            Socket trackedSocket = socket;
            try
            {
                m_logWriter.Write(this, LogPrio.Debug, $"Accepted connection from: {socket.RemoteEndPoint}");
                IHttpClientContext context;

                if (m_certificate is null)
                {
                    context = m_contextFactory.CreateContext(socket);
                }
                else if (await IsPlainHttpRequestAsync(socket).ConfigureAwait(false))
                {
                    m_logWriter.Write(this, LogPrio.Debug,
                        $"Accepted plaintext HTTP request on HTTPS listener {m_address}:{m_port} from {socket.RemoteEndPoint}");
                    context = m_contextFactory.CreateContext(socket);
                }
                else
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(m_CancellationSource.Token);
                    timeout.CancelAfter(TlsHandshakeTimeout);
                    context = await m_contextFactory.CreateSecureContextAsync(
                        socket, m_certificate, m_sslProtocols, m_clientCertValCallback, timeout.Token).ConfigureAwait(false);
                }

                // The context owns the socket after successful creation.
                if (context is not null)
                    socket = null;
            }
            catch (Exception err)
            {
                m_logWriter.Write(this, LogPrio.Error, $"Failed to initialize HTTP connection: {err}");
                try
                {
                    ExceptionThrown?.Invoke(this, err);
                }
                catch (Exception eventError)
                {
                    m_logWriter.Write(this, LogPrio.Error, $"HTTP exception handler failed: {eventError}");
                }
            }
            finally
            {
                m_initializingSockets.TryRemove(trackedSocket, out _);
                socket?.Dispose();
                m_initializationSlots.Release();
            }
        }

        /// <summary>
        /// Detect plain HTTP sent to an HTTPS listener. Some Hypergrid/viewer
        /// clients do not follow 301/302 redirects and may try http://host:sslport.
        /// Sniffing lets the same port accept both HTTP and HTTPS without a
        /// redirect or external proxy.
        /// </summary>
        private async Task<bool> IsPlainHttpRequestAsync(Socket socket)
        {
            try
            {
                // TLS clients send ClientHello immediately; HTTP clients send a
                // request line. Bound the probe so a silent peer cannot retain
                // resources indefinitely.
                byte[] probe = new byte[8];
                DateTime deadline = DateTime.UtcNow.AddMilliseconds(500);
                while (socket.Available == 0 && DateTime.UtcNow < deadline)
                    await Task.Delay(10, m_CancellationSource.Token).ConfigureAwait(false);
                if (socket.Available == 0)
                    return false;

                int read = socket.Receive(probe, 0, probe.Length, SocketFlags.Peek);
                if (read <= 0)
                    return false;

                // TLS handshake records normally start with 0x16 0x03 ...
                // Anything non-ASCII is treated as TLS/unknown.
                if (probe[0] < 0x20 || probe[0] > 0x7e)
                    return false;

                string prefix = Encoding.ASCII.GetString(probe, 0, read).ToUpperInvariant();
                return prefix.StartsWith("GET ") ||
                       prefix.StartsWith("POST ") ||
                       prefix.StartsWith("HEAD ") ||
                       prefix.StartsWith("PUT ") ||
                       prefix.StartsWith("DELETE ") ||
                       prefix.StartsWith("OPTIONS ") ||
                       prefix.StartsWith("PATCH ") ||
                       prefix.StartsWith("TRACE ") ||
                       prefix.StartsWith("CONNECT ") ||
                       prefix.StartsWith("PRI ");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Can be used to create filtering of new connections.
        /// </summary>
        /// <param name="socket">Accepted socket</param>
        /// <returns>true if connection can be accepted; otherwise false.</returns>
        protected bool OnAcceptingSocket(Socket socket)
        {
            if(Accepted!=null)
            {
                ClientAcceptedEventArgs args = new(socket);
                Accepted?.Invoke(this, args);
                return !args.Revoked;
            }
            return true;
        }

        /// <summary>
        /// Start listen for new connections
        /// </summary>
        /// <param name="backlog">Number of connections that can stand in a queue to be accepted.</param>
        /// <exception cref="InvalidOperationException">Listener have already been started.</exception>
        public void Start(int backlog)
        {
            if (m_listener != null)
                throw new InvalidOperationException("Listener have already been started.");

            m_listener = new TcpListener(m_address, m_port);
            m_listener.Start(backlog);
            m_acceptLoopTask = AcceptLoop();
        }

        /// <summary>
        /// Stop the listener
        /// </summary>
        /// <exception cref="SocketException"></exception>
        public void Stop()
        {
            if (m_shutdown)
                return;

            m_shutdown = true;
            m_CancellationSource.Cancel();
            m_listener?.Stop();

            foreach (Socket socket in m_initializingSockets.Keys)
                socket.Dispose();

            if (m_acceptLoopTask is not null && !m_acceptLoopTask.Wait(TimeSpan.FromSeconds(5)))
                m_logWriter.Write(this, LogPrio.Error, "Failed to stop HTTP accept loop within timeout.");

            // Catch any socket registered concurrently with the first snapshot.
            foreach (Socket socket in m_initializingSockets.Keys)
                socket.Dispose();

            Task[] initializationTasks = m_initializationTasks.Values.ToArray();
            if (initializationTasks.Length > 0 &&
                !Task.WaitAll(initializationTasks, TimeSpan.FromSeconds(5)))
                m_logWriter.Write(this, LogPrio.Error, "Failed to stop all HTTP connection initializers within timeout.");

            m_initializingSockets.Clear();
            m_initializationTasks.Clear();
            m_contextFactory.Shutdown();
            m_listener = null;
            Dispose();
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if (m_shutdownEvent != null)
            {
                m_shutdownEvent.Dispose();
                m_CancellationSource.Dispose();
            }
        }
    }
}
