/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS "AS IS" AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Server.Handlers
{
    /// <summary>
    /// Central QUIC proxy embedded in ROBUST.
    ///
    /// Viewers connect via QUIC to ROBUST (single port, single cert).
    /// ROBUST bridges framed LLUDP packets to the correct region simulator via
    /// the simulator's internal QUIC listener. Both sides use the same 4-byte
    /// big-endian length-prefixed stream framing.
    ///
    /// Routing uses a multi-layer strategy:
    ///   1. Pre-registration - LLLoginService registers circuit->sim in QuicCircuitRegistry
    ///      during login (same process, zero-latency first connect).
    ///   2. Sim registration - each sim's QuicServerModule registers circuits on creation
    ///      via HTTP POST to the proxy (handles teleport re-routing).
    ///   3. Endpoint-aware unregister prevents old source simulators from removing
    ///      freshly registered destination routes during teleport.
    ///
    /// Configuration: [QuicProxy] section in Robust.HG.ini
    /// </summary>
    public class QuicProxyConnector : ServiceConnector
    {
        private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private bool m_enabled;
        private int m_listenPort = 9001;
        private string m_alpn = "opensim-ll/1";
        private string m_certPath = "";
        private string m_keyPath = "";
        private int m_idleTimeoutMs = 60000;
        private int m_broadcastTimeoutMs = 3000; // max wait for broadcast response
        private int m_quicPoolStart = 0; // first valid simulator QUIC port (0 = no pool validation)
        private int m_quicPoolEnd = 0; // last valid simulator QUIC port

        private QuicListener m_listener;
        private CancellationTokenSource m_cts;
        private readonly string m_quickGControlPort = Environment.GetEnvironmentVariable("OPENSIM_QUICKG_CONTROL_PORT");
        private static readonly HttpClient s_quickGClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        // Grid service for region/sim discovery (lazy-loaded)
        private IGridService m_gridService;

        // Active proxy sessions: circuit_code -> session
        private readonly ConcurrentDictionary<uint, ProxySession> m_sessions = new();

        // Zero-length keepalive frame: [00 00 00 00]
        // Written periodically on both viewer and sim streams to prevent
        // NAT/firewall/QUIC idle timeout from killing the connection.
        private static readonly byte[] s_keepaliveFrame = new byte[] { 0, 0, 0, 0 };

        #region IServiceConnector

        public string Name => "QuicProxyConnector";

        /// <summary>
        /// Constructor called by ServerMain with modargs: (IConfigSource, IHttpServer, string configName).
        /// We start our own QUIC listener and register HTTP handlers on ROBUST's HTTP server.
        /// </summary>
        public QuicProxyConnector(IConfigSource configSource, IHttpServer httpServer, string configName)
            : base(configSource, httpServer, configName)
        {
            try
            {
                IConfig quicConfig = configSource.Configs["QuicProxy"];
                if (quicConfig == null)
                {
                    m_log.Debug("[QuicProxy] No [QuicProxy] section found, proxy disabled");
                    return;
                }

                m_enabled = quicConfig.GetBoolean("Enabled", false);
                if (!m_enabled)
                {
                    m_log.Debug("[QuicProxy] Proxy disabled via config");
                    return;
                }

                m_listenPort = quicConfig.GetInt("Port", m_listenPort);
                m_alpn = quicConfig.GetString("ALPN", m_alpn);
                m_certPath = quicConfig.GetString("CertificatePath", m_certPath);
                m_keyPath = quicConfig.GetString("PrivateKeyPath", m_keyPath);
                m_idleTimeoutMs = quicConfig.GetInt("IdleTimeoutMs", m_idleTimeoutMs);
                m_broadcastTimeoutMs = quicConfig.GetInt("BroadcastTimeoutMs", m_broadcastTimeoutMs);
                m_quicPoolStart = quicConfig.GetInt("QuicPoolStart", m_quicPoolStart);
                m_quicPoolEnd = quicConfig.GetInt("QuicPoolEnd", m_quicPoolEnd);

                if (!string.IsNullOrEmpty(m_quickGControlPort))
                {
                    QuicCircuitRegistry.SimEndpointRegistered += ForwardRegistryRouteToQuickG;
                    QuicCircuitRegistry.CircuitUnregistered += ForwardRegistryUnregisterToQuickG;
                }

                // Load IGridService for sim discovery (graceful if unavailable)
                try { LoadGridService(configSource); }
                catch (Exception ex) { m_log.Warn($"[QuicProxy] LoadGridService error: {ex.Message}"); }

                // Register HTTP endpoints for sim circuit registration
                try { RegisterHttpEndpoints(httpServer); }
                catch (Exception ex) { m_log.Warn($"[QuicProxy] RegisterHttpEndpoints error: {ex.Message}"); }

                // Pre-populate known sim endpoints from grid service
                try { PreloadKnownSims(); }
                catch (Exception ex) { m_log.Warn($"[QuicProxy] PreloadKnownSims error: {ex.Message}"); }

                if (!string.IsNullOrEmpty(m_quickGControlPort))
                    m_log.Info($"[QuicProxy] Quick-G owns QUIC port {m_listenPort}; native listener bypassed");
                else
                {
                    m_log.Info($"[QuicProxy] Starting QUIC proxy on port {m_listenPort}...");
                    StartListener();
                }
            }
            catch (Exception ex)
            {
                m_log.Error($"[QuicProxy] Constructor failed: {ex.GetType().Name}: {ex.Message}");
                m_log.Error($"[QuicProxy] Stack trace: {ex.StackTrace}");
                m_enabled = false;
            }
        }

        #endregion

        /// <summary>
        /// Load IGridService from the [GridService] config section.
        /// This lets us query all known regions for sim discovery and broadcasting.
        /// </summary>
        private void LoadGridService(IConfigSource configSource)
        {
            try
            {
                IConfig gridConfig = configSource.Configs["GridService"];
                if (gridConfig == null)
                {
                    m_log.Warn("[QuicProxy] No [GridService] section - sim discovery via grid unavailable");
                    return;
                }

                string module = gridConfig.GetString("LocalServiceModule", string.Empty);
                if (string.IsNullOrEmpty(module))
                {
                    m_log.Warn("[QuicProxy] GridService.LocalServiceModule not configured - sim discovery unavailable");
                    return;
                }

                Object[] args = new Object[] { configSource };
                m_gridService = ServerUtils.LoadPlugin<IGridService>(module, args);
                m_log.Info("[QuicProxy] IGridService loaded for sim discovery");
            }
            catch (Exception ex)
            {
                m_log.Warn($"[QuicProxy] Failed to load IGridService: {ex.Message}");
            }
        }

        /// <summary>
        /// Register HTTP endpoints on ROBUST's existing server for sim-to-proxy circuit registration.
        /// </summary>
        private void RegisterHttpEndpoints(IHttpServer server)
        {
            // POST /admin/quic/circuit/register - body: {"circuitCode":1234,"simHost":"sim-Grid_Welcome","simPort":2001}
            server.AddHTTPHandler("/admin/quic/circuit/register", HandleCircuitRegister);

            // POST /admin/quic/circuit/unregister - body: {"circuitCode":1234}
            server.AddHTTPHandler("/admin/quic/circuit/unregister", HandleCircuitUnregister);

            m_log.Info("[QuicProxy] Registered HTTP endpoints: /admin/quic/circuit/register, /admin/quic/circuit/unregister");
        }

        /// <summary>
        /// Handle sim registration of a circuit.
        /// </summary>
        private Hashtable HandleCircuitRegister(Hashtable request)
        {
            var response = new Hashtable();
            try
            {
                string body = request["body"] as string;
                if (string.IsNullOrEmpty(body))
                {
                    response["int_response_code"] = 400;
                    response["content_type"] = "application/json";
                    response["str_response_string"] = "{\"success\":false,\"error\":\"Missing body\"}";
                    return response;
                }

                OSDMap map = OSDParser.DeserializeJson(body) as OSDMap;
                if (map == null || !map.ContainsKey("circuitCode"))
                {
                    response["int_response_code"] = 400;
                    response["content_type"] = "application/json";
                    response["str_response_string"] = "{\"success\":false,\"error\":\"Missing circuitCode\"}";
                    return response;
                }

                ForwardToQuickG("register", body);

                uint circuitCode = (uint)map["circuitCode"].AsInteger();
                string regionName = map.ContainsKey("regionName") ? map["regionName"].AsString() : string.Empty;
                string regionId = map.ContainsKey("regionId") ? map["regionId"].AsString() : string.Empty;
                string regionTag = !string.IsNullOrEmpty(regionName) ? regionName : regionId;
                if (string.IsNullOrEmpty(regionTag))
                    regionTag = "unknown-region";
                bool brainLease = map.ContainsKey("brainLease") && map["brainLease"].AsBoolean();
                string agentType = map.ContainsKey("agentType") ? map["agentType"].AsString() : "unknown";

                if (map.ContainsKey("simPort") || map.ContainsKey("quicPort"))
                {
                    string simHost = map.ContainsKey("simHost") ? map["simHost"].AsString() : "127.0.0.1";
                    int simPort = map.ContainsKey("simPort") ? map["simPort"].AsInteger() : 0;

                    if (simPort > 0)
                    {
                        IPAddress simAddress = ResolveRegistrationAddress(simHost);
                        var simEndpoint = new IPEndPoint(simAddress, simPort);
                        if (string.Equals(agentType, "child", StringComparison.OrdinalIgnoreCase) &&
                            QuicCircuitRegistry.TryGetCircuit(circuitCode, out IPEndPoint currentSim) &&
                            (currentSim.Port != simEndpoint.Port || !currentSim.Address.Equals(simEndpoint.Address)))
                        {
                            m_log.Info($"[QuicProxy] Circuit {circuitCode} region {regionTag} child-agent flip {currentSim} -> {simEndpoint} ignored; keeping root route");
                        }
                        else
                        {
                            QuicCircuitRegistry.Register(circuitCode, simEndpoint);
                            m_log.Info($"[QuicProxy] Circuit {circuitCode} region {regionTag} agent {agentType} registered -> LLUDP {simEndpoint}");
                        }
                    }

                    string quicHost = map.ContainsKey("quicHost") ? map["quicHost"].AsString() : simHost;
                    int quicPort = map.ContainsKey("quicPort") ? map["quicPort"].AsInteger() : 0;

                    if (quicPort > 0 && quicPort <= 65535)
                    {
                        if (quicPort == m_listenPort || (m_quicPoolStart > 0 && (quicPort < m_quicPoolStart || quicPort > m_quicPoolEnd)))
                        {
                            m_log.Warn($"[QuicProxy] Circuit {circuitCode} region {regionTag} sent stale quicPort {quicPort} (pool {m_quicPoolStart}-{m_quicPoolEnd}, lease={brainLease}); keeping LLUDP only");
                        }
                        else
                        {
                            IPAddress quicAddress = ResolveRegistrationAddress(quicHost);
                            var quicEndpoint = new IPEndPoint(quicAddress, quicPort);
                            QuicCircuitRegistry.RegisterQUIC(circuitCode, quicEndpoint);
                            m_log.Info($"[QuicProxy] Circuit {circuitCode} region {regionTag} registered -> QUIC {quicEndpoint}");
                        }
                    }
                }

                response["int_response_code"] = 200;
                response["content_type"] = "application/json";
                response["str_response_string"] = "{\"success\":true}";
            }
            catch (Exception ex)
            {
                m_log.Error($"[QuicProxy] HandleCircuitRegister error: {ex.Message}");
                response["int_response_code"] = 500;
                response["content_type"] = "application/json";
                response["str_response_string"] = "{\"success\":false,\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}";
            }
            return response;
        }

        private static IPAddress ResolveRegistrationAddress(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
                return IPAddress.Loopback;

            if (IPAddress.TryParse(host, out IPAddress ip))
                return ip;

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                return IPAddress.Loopback;

            try
            {
                IPAddress[] addrs = Dns.GetHostAddresses(host);
                foreach (IPAddress addr in addrs)
                {
                    if (addr.AddressFamily == AddressFamily.InterNetwork)
                        return addr;
                }
                if (addrs.Length > 0)
                    return addrs[0];
            }
            catch
            {
                // Host-networked simulator containers are reachable from ROBUST
                // via loopback even when their advertised names do not resolve.
            }

            return IPAddress.Loopback;
        }

        /// <summary>
        /// Handle sim unregistration of a circuit.
        /// </summary>
        private Hashtable HandleCircuitUnregister(Hashtable request)
        {
            var response = new Hashtable();
            try
            {
                string body = request["body"] as string;
                if (string.IsNullOrEmpty(body))
                {
                    response["int_response_code"] = 400;
                    response["content_type"] = "application/json";
                    response["str_response_string"] = "{\"success\":false,\"error\":\"Missing body\"}";
                    return response;
                }

                OSDMap map = OSDParser.DeserializeJson(body) as OSDMap;
                if (map == null || !map.ContainsKey("circuitCode"))
                {
                    response["int_response_code"] = 400;
                    response["content_type"] = "application/json";
                    response["str_response_string"] = "{\"success\":false,\"error\":\"Missing circuitCode\"}";
                    return response;
                }

                ForwardToQuickG("unregister", body);

                uint circuitCode = (uint)map["circuitCode"].AsInteger();

                IPEndPoint simEndpoint = null;
                if (map.ContainsKey("simPort"))
                {
                    string simHost = map.ContainsKey("simHost") ? map["simHost"].AsString() : "127.0.0.1";
                    int simPort = map["simPort"].AsInteger();
                    if (simPort > 0 && simPort <= 65535)
                        simEndpoint = new IPEndPoint(ResolveRegistrationAddress(simHost), simPort);
                }

                IPEndPoint quicEndpoint = null;
                if (map.ContainsKey("quicPort"))
                {
                    string quicHost = map.ContainsKey("quicHost") ? map["quicHost"].AsString() : "127.0.0.1";
                    int quicPort = map["quicPort"].AsInteger();
                    if (quicPort > 0 && quicPort <= 65535)
                        quicEndpoint = new IPEndPoint(ResolveRegistrationAddress(quicHost), quicPort);
                }

                if (simEndpoint == null && quicEndpoint == null)
                {
                    m_log.Warn($"[QuicProxy] Ignoring blind unregister for circuit {circuitCode}; endpoint required to avoid teleport route races");
                }
                else if (QuicCircuitRegistry.TryUnregisterIfMatches(circuitCode, simEndpoint, quicEndpoint))
                {
                    m_log.Info($"[QuicProxy] Circuit {circuitCode} unregistered for {quicEndpoint ?? simEndpoint}");
                }
                else
                {
                    m_log.Info($"[QuicProxy] Ignored stale unregister for circuit {circuitCode} from {quicEndpoint ?? simEndpoint}");
                }

                response["int_response_code"] = 200;
                response["content_type"] = "application/json";
                response["str_response_string"] = "{\"success\":true}";
            }
            catch (Exception ex)
            {
                m_log.Error($"[QuicProxy] HandleCircuitUnregister error: {ex.Message}");
                response["int_response_code"] = 500;
                response["content_type"] = "application/json";
                response["str_response_string"] = "{\"success\":false,\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}";
            }
            return response;
        }

        private void ForwardToQuickG(string operation, string body)
        {
            if (string.IsNullOrEmpty(m_quickGControlPort))
                return;

            using StringContent content = new StringContent(body, Encoding.UTF8, "application/json");
            HttpResponseMessage result = s_quickGClient.PostAsync(
                $"http://127.0.0.1:{m_quickGControlPort}/{operation}", content).GetAwaiter().GetResult();
            if (!result.IsSuccessStatusCode)
                throw new InvalidOperationException($"Quick-G {operation} returned {(int)result.StatusCode}");
        }

        private void ForwardRegistryRouteToQuickG(uint circuitCode, IPEndPoint endpoint)
        {
            string body = $"{{\"circuitCode\":{circuitCode},\"simHost\":\"{endpoint.Address}\",\"simPort\":{endpoint.Port}}}";
            ForwardToQuickG("register", body);
        }

        private void ForwardRegistryUnregisterToQuickG(uint circuitCode)
        {
            ForwardToQuickG("unregister", $"{{\"circuitCode\":{circuitCode}}}");
        }

        /// <summary>
        /// Pre-populate the known sims list from grid service on startup.
        /// This enables broadcast fallback before any sim has registered a circuit.
        /// </summary>
        private void PreloadKnownSims()
        {
            if (m_gridService == null)
                return;

            try
            {
                var allRegions = m_gridService.GetRegionRange(UUID.Zero, int.MinValue, int.MaxValue, int.MinValue, int.MaxValue);
                int count = 0;

                foreach (var region in allRegions)
                {
                    try
                    {
                        // Use the ExternalEndPoint which resolves hostname + InternalEndPoint.Port
                        IPEndPoint simEp = region.ExternalEndPoint;
                        if (simEp != null && simEp.Port > 0)
                        {
                            QuicCircuitRegistry.RegisterSim(simEp);
                            count++;
                        }
                    }
                    catch
                    {
                        // Skip regions that fail DNS resolution
                    }
                }

                m_log.Info($"[QuicProxy] Preloaded {count} known sim endpoints from grid service");
            }
            catch (Exception ex)
            {
                m_log.Warn($"[QuicProxy] Failed to preload sims: {ex.Message}");
            }
        }

        private void StartListener()
        {
            m_cts = new CancellationTokenSource();

            try
            {
                // Pre-load the certificate context with full chain (cached)
                LoadCertificateContext();

                var listenerOptions = new QuicListenerOptions
                {
                    ListenEndPoint = new IPEndPoint(IPAddress.Any, m_listenPort),
                    ApplicationProtocols = new List<SslApplicationProtocol>
                    {
                        new SslApplicationProtocol(m_alpn)
                    },
                    ConnectionOptionsCallback = OnConnectionOptions,
                };

                m_listener = QuicListener.ListenAsync(listenerOptions, m_cts.Token)
                    .GetAwaiter().GetResult();

                m_log.Info($"[QuicProxy] QUIC proxy listening on port {m_listenPort}");

                Thread acceptThread = new Thread(() => AcceptLoopAsync(m_cts.Token).GetAwaiter().GetResult())
                {
                    Name = "QuicProxyAccept",
                    IsBackground = true
                };
                acceptThread.Start();
            }
            catch (Exception ex)
            {
                m_log.Error($"[QuicProxy] Failed to start: {ex.Message}");
                m_enabled = false;
            }
        }

        private ValueTask<QuicServerConnectionOptions> OnConnectionOptions(
            QuicConnection quicConnection,
            SslClientHelloInfo clientHello,
            CancellationToken ct)
        {
            var serverAuthOptions = new SslServerAuthenticationOptions
            {
                ServerCertificateContext = m_cts != null ? LoadCertificateContext() : null,
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    new SslApplicationProtocol(m_alpn)
                },
                ClientCertificateRequired = false
            };

            var options = new QuicServerConnectionOptions
            {
                ServerAuthenticationOptions = serverAuthOptions,
                IdleTimeout = TimeSpan.FromMilliseconds(m_idleTimeoutMs),
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
            };

            return ValueTask.FromResult(options);
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    QuicConnection quicConn = await m_listener.AcceptConnectionAsync(ct);
                    m_log.Info($"[QuicProxy] New connection from {quicConn.RemoteEndPoint}");

                    _ = HandleConnectionAsync(quicConn, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (QuicException ex) when (ex.QuicError == QuicError.OperationAborted) { }
            catch (Exception ex)
            {
                m_log.Error($"[QuicProxy] Accept loop: {ex.Message}");
            }
        }

        private async Task HandleConnectionAsync(QuicConnection viewerConn, CancellationToken ct)
        {
            QuicStream viewerStream = null;
            ProxySession session = null;
            try
            {
                // Accept the control stream from viewer
                viewerStream = await viewerConn.AcceptInboundStreamAsync(ct);

                // Read the first framed packet to get the circuit code
                byte[] headerBuf = new byte[4];
                int headerRead = await ReadExactlyAsync(viewerStream, headerBuf, 0, 4, ct);
                if (headerRead < 4)
                    return;

                int payloadLen = (headerBuf[0] << 24) | (headerBuf[1] << 16) |
                                 (headerBuf[2] << 8) | headerBuf[3];

                if (payloadLen <= 0 || payloadLen > 64 * 1024)
                    return;

                byte[] firstPayload = new byte[payloadLen];
                int payloadRead = await ReadExactlyAsync(viewerStream, firstPayload, 0, payloadLen, ct);
                if (payloadRead < payloadLen)
                    return;

                // Parse and validate the first packet. It must be UseCircuitCode.
                if (!TryExtractCircuitCode(firstPayload, out uint circuitCode))
                {
                    m_log.Warn($"[QuicProxy] First packet from {viewerConn.RemoteEndPoint} is not a valid UseCircuitCode; dropping connection");
                    return;
                }

                m_log.Info($"[QuicProxy] Viewer from {viewerConn.RemoteEndPoint}, circuit={circuitCode}");

                // Resolve target sim - QUIC endpoint from registry
                if (!QuicCircuitRegistry.TryGetQUIC(circuitCode, out IPEndPoint quicEndpoint))
                {
                    // Race condition: sim may still be registering this circuit.
                    // Wait briefly for the registration POST to arrive.
                    m_log.Info($"[QuicProxy] No QUIC route for circuit {circuitCode} yet, waiting for registration...");
                    int waited = 0;
                    int waitBudget = 2000; // max 2 seconds
                    while (waited < waitBudget)
                    {
                        await Task.Delay(200, ct);
                        waited += 200;
                        if (QuicCircuitRegistry.TryGetQUIC(circuitCode, out quicEndpoint))
                        {
                            m_log.Info($"[QuicProxy] Circuit {circuitCode} QUIC route appeared after {waited}ms");
                            break;
                        }
                    }
                }

                if (quicEndpoint == null)
                {
                    m_log.Warn($"[QuicProxy] No QUIC route for circuit {circuitCode} after wait, dropping connection");
                    return;
                }

                m_log.Info($"[QuicProxy] Bridging circuit {circuitCode} to sim QUIC {quicEndpoint}");

                // Establish outbound QUIC connection to the sim
                var simConnOptions = new QuicClientConnectionOptions
                {
                    RemoteEndPoint = quicEndpoint,
                    DefaultStreamErrorCode = 0,
                    DefaultCloseErrorCode = 0,
                    ClientAuthenticationOptions = new SslClientAuthenticationOptions
                    {
                        ApplicationProtocols = new List<SslApplicationProtocol>
                        {
                            new SslApplicationProtocol(m_alpn)
                        },
                        TargetHost = "localhost",
                        RemoteCertificateValidationCallback = (_, _, _, _) => true  // trust self-signed sim certs
                    },
                    IdleTimeout = TimeSpan.FromMilliseconds(m_idleTimeoutMs)
                };

                QuicConnection simConn = await QuicConnection.ConnectAsync(simConnOptions, ct);
                QuicStream simStream = await simConn.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, ct);

                // Forward the first UseCircuitCode packet to the sim (reframe it back)
                byte[] framedFirst = FramePacket(firstPayload);
                await simStream.WriteAsync(framedFirst.AsMemory(0, framedFirst.Length), ct);
                await simStream.FlushAsync(ct);

                session = new ProxySession
                {
                    ViewerConn = viewerConn,
                    ViewerStream = viewerStream,
                    SimConn = simConn,
                    SimStream = simStream,
                    CircuitCode = circuitCode,
                    KeepaliveCts = CancellationTokenSource.CreateLinkedTokenSource(ct)
                };

                m_sessions[circuitCode] = session;

                // Start keepalive on both streams to prevent idle timeout
                _ = KeepaliveLoopAsync(session, session.KeepaliveCts.Token);

                // Bidirectional QUIC stream bridging
                var tasks = new[]
                {
                    BridgeViewerToSim(session, ct),
                    BridgeSimToViewer(session, ct)
                };

                await Task.WhenAny(tasks);
                CleanupSession(circuitCode, session);
            }
            catch (Exception ex)
            {
                m_log.Error($"[QuicProxy] Handle connection: {ex.Message}");
            }
            finally
            {
                if (session == null)
                {
                    try { viewerStream?.Dispose(); } catch { }
                    try { await viewerConn.CloseAsync(0, CancellationToken.None); } catch { }
                    try { await viewerConn.DisposeAsync(); } catch { }
                }
            }
        }

        /// <summary>
        /// Extract the circuit code from a valid UseCircuitCode packet using proper LLUDP parsing.
        /// </summary>
        private static bool TryExtractCircuitCode(byte[] payload, out uint circuitCode)
        {
            circuitCode = 0;
            try
            {
                int bufferLen = payload.Length;
                Packet packet = Packet.BuildPacket(payload, ref bufferLen, null);
                if (packet != null && packet.Type == PacketType.UseCircuitCode)
                {
                    var uccp = (UseCircuitCodePacket)packet;
                    circuitCode = uccp.CircuitCode.Code;
                    return circuitCode != 0;
                }
            }
            catch
            {
            }

            return false;
        }

        private IPEndPoint ResolveSimEndpoint(uint circuitCode, byte[] useCircuitCodeBytes)
        {
            // Layer 1: Check in-process registry (pre-registered by LLLoginService or proxy HTTP endpoint)
            if (QuicCircuitRegistry.TryGetCircuit(circuitCode, out IPEndPoint endpoint))
            {
                m_log.Debug($"[QuicProxy] Circuit {circuitCode} resolved via registry -> {endpoint}");
                return endpoint;
            }

            // Layer 2: Broadcast fallback - send UseCircuitCode to all known sims,
            // the one that owns the circuit will respond.
            m_log.Info($"[QuicProxy] Circuit {circuitCode} not in registry, broadcasting to known sims...");
            return BroadcastLookup(circuitCode, useCircuitCodeBytes);
        }

        /// <summary>
        /// Broadcast the UseCircuitCode packet to all known sims.
        /// Only the sim that owns the circuit will process it and respond.
        /// The response comes back via UDP to this proxy's client socket,
        /// establishing the route.
        /// </summary>
        private IPEndPoint BroadcastLookup(uint circuitCode, byte[] useCircuitCodeBytes)
        {
            List<IPEndPoint> knownSims = QuicCircuitRegistry.GetAllKnownSims();
            if (knownSims.Count == 0)
            {
                m_log.Warn("[QuicProxy] No known sims for broadcast lookup");
                return null;
            }

            // Send the UseCircuitCode as a UDP probe to every known sim
            // The sim that owns this circuit will accept it and start responding.
            // We send it raw (no length prefix) since these go to the sim's LLUDP port
            // which expects raw LLUDP packets.
            foreach (IPEndPoint simEp in knownSims)
            {
                try
                {
                    using (var probeClient = new UdpClient())
                    {
                        probeClient.Connect(simEp);
                        probeClient.Send(useCircuitCodeBytes, useCircuitCodeBytes.Length);
                    }
                }
                catch (Exception ex)
                {
                    m_log.Debug($"[QuicProxy] Broadcast to {simEp} failed: {ex.Message}");
                }
            }

            // After broadcast, check if the circuit was registered (sim processing the
            // UseCircuitCode should register it). Wait briefly for async registration.
            int waited = 0;
            while (waited < m_broadcastTimeoutMs)
            {
                Thread.Sleep(100);
                waited += 100;

                if (QuicCircuitRegistry.TryGetCircuit(circuitCode, out IPEndPoint registeredEp))
                {
                    m_log.Info($"[QuicProxy] Circuit {circuitCode} resolved via broadcast -> {registeredEp}");
                    return registeredEp;
                }
            }

            // Final check after timeout
            if (QuicCircuitRegistry.TryGetCircuit(circuitCode, out IPEndPoint lateEp))
            {
                return lateEp;
            }

            m_log.Warn($"[QuicProxy] Broadcast lookup failed for circuit {circuitCode} after {m_broadcastTimeoutMs}ms");
            return null;
        }

        /// <summary>
        /// Bridge framed LLUDP packets from the viewer's QUIC stream to the sim's QUIC stream.
        /// Both sides use the same framing (4-byte big-endian length prefix).
        /// The entire framed packet (prefix + payload) is forwarded as-is.
        /// Zero-length frames are keepalive markers and are silently consumed.
        /// </summary>
        private async Task BridgeViewerToSim(ProxySession session, CancellationToken ct)
        {
            try
            {
                byte[] headerBuf = new byte[4];
                byte[] buffer = new byte[4 + 64 * 1024];

                while (!ct.IsCancellationRequested && session.ViewerStream.CanRead)
                {
                    // Read 4-byte length prefix from viewer
                    int read = await ReadExactlyAsync(session.ViewerStream, headerBuf, 0, 4, ct);
                    if (read < 4)
                        break;

                    int payloadLen = (headerBuf[0] << 24) | (headerBuf[1] << 16) |
                                     (headerBuf[2] << 8) | headerBuf[3];

                    // Zero-length frame = keepalive marker - silently consume
                    if (payloadLen == 0)
                        continue;

                    if (payloadLen < 0 || payloadLen > 64 * 1024)
                        break;

                    // Read payload into buffer starting at offset 4 (after prefix slot)
                    read = await ReadExactlyAsync(session.ViewerStream, buffer, 4, payloadLen, ct);
                    if (read < payloadLen)
                        break;

                    // Prepend the prefix
                    Buffer.BlockCopy(headerBuf, 0, buffer, 0, 4);

                    // Forward the entire framed packet to sim
                    await session.SimStream.WriteAsync(buffer.AsMemory(0, 4 + payloadLen), ct);
                    await session.SimStream.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                m_log.Debug($"[QuicProxy] Viewer->Sim bridge ended: {ex.Message}");
            }
        }

        /// <summary>
        /// Bridge framed LLUDP packets from the sim's QUIC stream to the viewer's QUIC stream.
        /// The sim frames packets with the same 4-byte length prefix convention.
        /// The entire framed packet (prefix + payload) is forwarded as-is.
        /// Zero-length frames are keepalive markers and are silently consumed.
        /// </summary>
        private async Task BridgeSimToViewer(ProxySession session, CancellationToken ct)
        {
            try
            {
                byte[] headerBuf = new byte[4];
                byte[] buffer = new byte[4 + 64 * 1024];

                while (!ct.IsCancellationRequested && session.SimStream.CanRead)
                {
                    // Read 4-byte length prefix from sim
                    int read = await ReadExactlyAsync(session.SimStream, headerBuf, 0, 4, ct);
                    if (read < 4)
                        break;

                    int payloadLen = (headerBuf[0] << 24) | (headerBuf[1] << 16) |
                                     (headerBuf[2] << 8) | headerBuf[3];

                    // Zero-length frame = keepalive marker - silently consume
                    if (payloadLen == 0)
                        continue;

                    if (payloadLen < 0 || payloadLen > 64 * 1024)
                        break;

                    // Read payload
                    read = await ReadExactlyAsync(session.SimStream, buffer, 4, payloadLen, ct);
                    if (read < payloadLen)
                        break;

                    // Prepend the prefix
                    Buffer.BlockCopy(headerBuf, 0, buffer, 0, 4);

                    // Forward the entire framed packet to viewer
                    await session.ViewerStream.WriteAsync(buffer.AsMemory(0, 4 + payloadLen), ct);
                    await session.ViewerStream.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                m_log.Debug($"[QuicProxy] Sim->Viewer bridge ended: {ex.Message}");
            }
        }

        /// <summary>
        /// Close both connections when a session ends.
        /// </summary>
        private void CleanupSession(uint circuitCode, ProxySession session)
        {
            if (session == null)
                return;

            // Stop keepalive
            try { session.KeepaliveCts?.Cancel(); } catch { }
            try { session.KeepaliveCts?.Dispose(); } catch { }

            bool removed = false;
            if (m_sessions.TryGetValue(circuitCode, out ProxySession current) && ReferenceEquals(current, session))
            {
                removed = ((ICollection<KeyValuePair<uint, ProxySession>>)m_sessions)
                    .Remove(new KeyValuePair<uint, ProxySession>(circuitCode, session));
            }

            try { session.SimStream?.Dispose(); } catch { }
            try { session.ViewerStream?.Dispose(); } catch { }
            try { session.SimConn?.CloseAsync(0, CancellationToken.None).AsTask().GetAwaiter().GetResult(); } catch { }
            try { session.ViewerConn?.CloseAsync(0, CancellationToken.None).AsTask().GetAwaiter().GetResult(); } catch { }
            try { session.SimConn?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }
            try { session.ViewerConn?.DisposeAsync().AsTask().GetAwaiter().GetResult(); } catch { }

            if (removed)
                m_log.Info($"[QuicProxy] Closed bridge for circuit {circuitCode}");
            else
                m_log.Info($"[QuicProxy] Closed stale bridge for circuit {circuitCode}; newer session kept alive");
        }

        /// <summary>
        /// Send periodic keepalive flushes on both viewer and sim QUIC streams
        /// to prevent NAT/firewall idle timeout from killing the connection.
        /// Writes a zero-length frame marker ([00 00 00 00]) that the receiving
        /// side silently consumes. This ensures actual bytes are sent on the wire,
        /// which resets the QUIC connection idle timer on both sides.
        /// </summary>
        private async Task KeepaliveLoopAsync(ProxySession session, CancellationToken ct)
        {
            int intervalMs = Math.Max(m_idleTimeoutMs / 3, 15000); // flush every 1/3 of idle timeout, min 15s
            byte[] keepalive = s_keepaliveFrame;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(intervalMs, ct);

                    try
                    {
                        if (session.ViewerStream != null && session.ViewerStream.CanWrite)
                        {
                            await session.ViewerStream.WriteAsync(keepalive.AsMemory(0, keepalive.Length), ct);
                            await session.ViewerStream.FlushAsync(ct);
                        }
                        if (session.SimStream != null && session.SimStream.CanWrite)
                        {
                            await session.SimStream.WriteAsync(keepalive.AsMemory(0, keepalive.Length), ct);
                            await session.SimStream.FlushAsync(ct);
                        }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception)
                    {
                        // Stream write failure - connection is likely dead, exit loop
                        break;
                    }
                }
            }
            catch (OperationCanceledException) { }
        }

        private SslStreamCertificateContext m_cachedCertContext;
        private readonly object m_certLock = new();

        /// <summary>
        /// Load the TLS certificate context for QUIC, including intermediate
        /// CA certificates from the fullchain PEM file.
        /// Uses the same approach as QuicServerConfig.LoadCertificateContext()
        /// on the sim side - loads leaf cert with private key from PEM, then
        /// extracts intermediate CA certs from the fullchain PEM and includes
        /// them in the SslStreamCertificateContext.
        /// Cached and reused across all connections.
        /// </summary>
        private SslStreamCertificateContext LoadCertificateContext()
        {
            if (m_cachedCertContext != null)
                return m_cachedCertContext;

            lock (m_certLock)
            {
                if (m_cachedCertContext != null)
                    return m_cachedCertContext;

                X509Certificate2 certificate;
                X509Certificate2Collection intermediates = new();

                if (!string.IsNullOrEmpty(m_certPath) && System.IO.File.Exists(m_certPath))
                {
                    if (!string.IsNullOrEmpty(m_keyPath) && System.IO.File.Exists(m_keyPath))
                        certificate = X509Certificate2.CreateFromPemFile(m_certPath, m_keyPath);
                    else
                        certificate = new X509Certificate2(m_certPath);

                    // Load the full chain from the fullchain PEM to extract intermediate CA certs.
                    // This is the same approach as QuicServerConfig.LoadCertificateContext().
                    try
                    {
                        X509Certificate2Collection pemCertificates = new();
                        pemCertificates.ImportFromPemFile(m_certPath);
                        foreach (X509Certificate2 c in pemCertificates)
                        {
                            if (!string.Equals(c.Thumbprint, certificate.Thumbprint, StringComparison.OrdinalIgnoreCase))
                                intermediates.Add(c);
                        }
                        m_log.Info($"[QuicProxy] Loaded {intermediates.Count} intermediate CA cert(s) from {m_certPath}");
                    }
                    catch (Exception ex)
                    {
                        m_log.Warn($"[QuicProxy] Could not extract intermediates from PEM: {ex.Message}");
                    }
                }
                else
                {
                    // No certificate configured or not found - generate self-signed for development
                    m_log.Warn("[QuicProxy] No certificate found, generating self-signed");
                    certificate = GenerateSelfSignedCertificate();
                }

                m_cachedCertContext = SslStreamCertificateContext.Create(
                    certificate, intermediates.Count > 0 ? intermediates : null, false);

                m_log.Info($"[QuicProxy] Certificate context created (leaf={certificate.Subject}, intermediates={intermediates.Count})");
                return m_cachedCertContext;
            }
        }

        private static X509Certificate2 GenerateSelfSignedCertificate()
        {
            using (var rsa = RSA.Create(2048))
            {
                var request = new CertificateRequest(
                    "CN=" + Dns.GetHostName(),
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                request.CertificateExtensions.Add(
                    new X509BasicConstraintsExtension(false, false, 0, false));
                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(
                        new OidCollection
                        {
                            new Oid("1.3.6.1.5.5.7.3.1")
                        }, true));

                var cert = request.CreateSelfSigned(
                    DateTimeOffset.UtcNow.AddDays(-1),
                    DateTimeOffset.UtcNow.AddYears(10));

                return new X509Certificate2(cert.Export(X509ContentType.Pkcs12));
            }
        }

        #region Stream helpers

        private static async Task<int> ReadExactlyAsync(QuicStream stream, byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(offset + totalRead, count - totalRead), ct);
                if (read <= 0)
                    break;
                totalRead += read;
            }
            return totalRead;
        }

        #endregion

        #region Packet framing (matches PacketFraming in LindenUDP)

        private static byte[] FramePacket(byte[] payload)
        {
            if (payload == null || payload.Length > 64 * 1024)
                throw new ArgumentException("Invalid payload");

            byte[] framed = new byte[4 + payload.Length];
            framed[0] = (byte)((payload.Length >> 24) & 0xFF);
            framed[1] = (byte)((payload.Length >> 16) & 0xFF);
            framed[2] = (byte)((payload.Length >> 8) & 0xFF);
            framed[3] = (byte)(payload.Length & 0xFF);
            Buffer.BlockCopy(payload, 0, framed, 4, payload.Length);
            return framed;
        }

        #endregion

        #region ProxySession

        private class ProxySession
        {
            // Viewer side
            public QuicConnection ViewerConn { get; set; }
            public QuicStream ViewerStream { get; set; }

            // Sim side
            public QuicConnection SimConn { get; set; }
            public QuicStream SimStream { get; set; }

            public uint CircuitCode { get; set; }

            // Keepalive cancellation
            public CancellationTokenSource KeepaliveCts { get; set; }
        }

        #endregion
    }
}
