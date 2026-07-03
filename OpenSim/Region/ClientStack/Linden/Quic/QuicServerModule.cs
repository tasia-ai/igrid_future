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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using Mono.Addins;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;

namespace OpenSim.Region.ClientStack.LindenUDP
{
    /// <summary>
    /// Server-side QUIC transport module for OpenSim regions.
    ///
    /// Listens for Tasia Viewer QUIC connections and bridges
    /// packet traffic into the existing LLUDP dispatch pipeline.
    ///
    /// Also registers client circuits with the centralized QUIC proxy
    /// (QuicProxyConnector in ROBUST) so the proxy knows where to route
    /// viewer QUIC connections.
    ///
    /// Configuration: [ClientStack.Quic] section in OpenSimDefaults.ini
    /// </summary>
    [Extension(Path = "/OpenSim/RegionModules",
               NodeName = "RegionModule",
               Id = "QuicServerModule")]
    public class QuicServerModule : INonSharedRegionModule
    {
        private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private QuicServerConfig m_config;
        private QuicListener m_listener;
        private Scene m_scene;
        private LLUDPServer m_udpServer;
        private Thread m_listenerThread;
        private CancellationTokenSource m_cts;
        private bool m_enabled;

        // Proxy registration config
        private string m_proxyRegistrationUrl = "";
        private string m_simHost = "";
        private int m_simPort;

        // HTTP client for proxy registration (shared, thread-safe)
        private static readonly HttpClient m_httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(5)
        };

        // Active QUIC connections: circuit code -> connections
        private readonly ConcurrentDictionary<uint, QuicClientConnection> m_connectionsByCircuit = new();
        private readonly ConcurrentDictionary<string, QuicClientConnection> m_connectionsByAgentId = new();

        #region INonSharedRegionModule

        public string Name => "QuicServerModule";

        public Type ReplaceableInterface => null;

        public void Initialise(IConfigSource configSource)
        {
            m_config = QuicServerConfig.FromConfig(configSource);
            m_enabled = m_config.Enabled;

            if (m_enabled)
            {
                // Load proxy registration config
                IConfig quicConfig = configSource.Configs["ClientStack.Quic"];
                if (quicConfig != null)
                {
                    m_proxyRegistrationUrl = NormalizeQuicConfigString(quicConfig.GetString("ProxyRegistrationURL", ""));
                    if (string.IsNullOrWhiteSpace(m_proxyRegistrationUrl))
                    {
                        // Default: ROBUST internal connector port for QuicProxyConnector.
                        m_proxyRegistrationUrl = "http://localhost:8003/admin/quic/circuit";
                    }
                }

                m_log.Info($"[QuicServer] Initialized: port={m_config.Port}, ALPN={m_config.Alpn}, proxy={m_proxyRegistrationUrl}");
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_enabled)
                return;

            // Find the LLUDPServer for this scene so we can bridge packets
            m_udpServer = FindUdpServer(scene);

            if (m_udpServer == null)
            {
                m_log.Warn("[QuicServer] Could not find LLUDPServer for scene, QUIC disabled");
                return;
            }

            // Determine this sim's hostname and LLUDP port for proxy registration
            m_simHost = scene.RegionInfo.ExternalHostName;
            m_simPort = scene.RegionInfo.InternalEndPoint.Port;

            // Subscribe to circuit creation events for proxy registration
            m_udpServer.OnQuicCircuitCreated += OnQuicCircuitCreated;

            StartListener();

            m_log.Info($"[QuicServer] Region loaded, sim={m_simHost}:{m_simPort}, proxy={m_proxyRegistrationUrl}");
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            // Unsubscribe from circuit creation events
            if (m_udpServer != null)
                m_udpServer.OnQuicCircuitCreated -= OnQuicCircuitCreated;

            StopListener();
        }

        public void Close()
        {
            StopListener();
        }

        #endregion

        private static string NormalizeQuicConfigString(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Trim('"', '\'');
        }

        #region Proxy Circuit Registration

        /// <summary>
        /// Called when a QUIC circuit is created on the LLUDPServer.
        /// Registers the circuit with the centralized QUIC proxy so it
        /// knows to route this viewer's packets to this sim.
        /// </summary>
        private void OnQuicCircuitCreated(uint circuitCode, UUID agentId, IViewerTransport transport)
        {
            if (transport is QuicViewerTransport quicTransport)
                RegisterQuicClient(circuitCode, agentId.ToString(), quicTransport.Connection);

            m_log.Debug($"[QuicServer] Circuit created: {circuitCode} for agent {agentId}, registering with proxy at {m_proxyRegistrationUrl}...");

            // Fire-and-forget the registration call
            _ = RegisterCircuitWithProxyAsync(circuitCode);
        }

        /// <summary>
        /// POST to the QUIC proxy to register this circuit.
        /// </summary>
        private async Task RegisterCircuitWithProxyAsync(uint circuitCode)
        {
            try
            {
                string url = m_proxyRegistrationUrl + "/register";
                var payload = new OSDMap
                {
                    ["circuitCode"] = OSD.FromInteger((int)circuitCode),
                    ["simHost"] = OSD.FromString(m_simHost),
                    ["simPort"] = OSD.FromInteger(m_simPort),
                    // The centralized proxy and region simulators run on the same
                    // host network. Register the simulator's internal QUIC listener
                    // so proxy-routed reconnects and teleports do not fall back to
                    // direct per-sim viewer QUIC.
                    ["quicHost"] = OSD.FromString("127.0.0.1"),
                    ["quicPort"] = OSD.FromInteger(m_config.Port)
                };

                string json = OSDParser.SerializeJsonString(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await m_httpClient.PostAsync(url, content);
                if (response.IsSuccessStatusCode)
                {
                    m_log.Info($"[QuicServer] Circuit {circuitCode} registered with proxy → {m_simHost}:{m_simPort}");
                }
                else
                {
                    m_log.Warn($"[QuicServer] Proxy registration returned {response.StatusCode} for circuit {circuitCode}");
                }
            }
            catch (Exception ex)
            {
                // Registration failure is non-fatal — proxy will use broadcast fallback
                m_log.Warn($"[QuicServer] Failed to register circuit {circuitCode} with proxy: {ex.Message}");
            }
        }

        /// <summary>
        /// POST to the QUIC proxy to unregister this circuit.
        /// Called from OnQuicDisconnected.
        /// </summary>
        private async Task UnregisterCircuitWithProxyAsync(uint circuitCode)
        {
            try
            {
                string url = m_proxyRegistrationUrl + "/unregister";
                var payload = new OSDMap
                {
                    ["circuitCode"] = OSD.FromInteger((int)circuitCode),
                    ["simHost"] = OSD.FromString(m_simHost),
                    ["simPort"] = OSD.FromInteger(m_simPort),
                    ["quicHost"] = OSD.FromString("127.0.0.1"),
                    ["quicPort"] = OSD.FromInteger(m_config.Port)
                };

                string json = OSDParser.SerializeJsonString(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await m_httpClient.PostAsync(url, content);
                if (response.IsSuccessStatusCode)
                {
                    m_log.Debug($"[QuicServer] Circuit {circuitCode} unregistered from proxy");
                }
            }
            catch (Exception ex)
            {
                m_log.Debug($"[QuicServer] Proxy unregistration for circuit {circuitCode} failed: {ex.Message}");
            }
        }

        #endregion

        #region Listener Management

        private void StartListener()
        {
            m_cts = new CancellationTokenSource();

            try
            {
                IPEndPoint listenEndpoint = new(
                    IPAddress.Any,
                    m_config.Port);

                var listenerOptions = new QuicListenerOptions
                {
                    ListenEndPoint = listenEndpoint,
                    ApplicationProtocols = new List<SslApplicationProtocol>
                    {
                        new SslApplicationProtocol(m_config.Alpn)
                    },
                    ConnectionOptionsCallback = OnConnectionOptions,
                };

                m_listener = QuicListener.ListenAsync(listenerOptions, m_cts.Token)
                    .GetAwaiter().GetResult();

                m_log.Info($"[QuicServer] QUIC listener started on port {m_config.Port} with ALPN '{m_config.Alpn}'");

                // Start accepting connections on a background thread
                m_listenerThread = new Thread(() => AcceptLoopAsync(m_cts.Token).GetAwaiter().GetResult())
                {
                    Name = "QuicAcceptLoop",
                    IsBackground = true
                };
                m_listenerThread.Start();
            }
            catch (Exception ex)
            {
                m_log.Error($"[QuicServer] Failed to start QUIC listener: {ex.Message}");
                m_log.Debug($"[QuicServer] Stack: {ex.StackTrace}");
                m_enabled = false;
            }
        }

        private async Task AcceptLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    QuicConnection quicConnection = await m_listener.AcceptConnectionAsync(ct);

                    _ = HandleAcceptedConnectionAsync(quicConnection, ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (QuicException ex) when (ex.QuicError == QuicError.OperationAborted)
            {
                // Listener stopped
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    m_log.Error($"[QuicServer] Accept loop error: {ex.Message}");
            }
        }

        private async Task HandleAcceptedConnectionAsync(QuicConnection quicConnection, CancellationToken ct)
        {
            if (m_config.LogHandshake)
            {
                IPEndPoint remoteEp = quicConnection.RemoteEndPoint as IPEndPoint;
                m_log.Info($"[QuicServer] New QUIC connection from {remoteEp}");
            }

            var clientConnection = new QuicClientConnection(quicConnection, m_config);

            // Create the transport wrapper once per connection and reuse it.
            // The transport is captured by the packet handler lambda and also
            // stored on LLUDPClient.Transport after UseCircuitCode processing.
            var transport = new QuicViewerTransport(clientConnection, m_config);

            try
            {
                // Wire up disconnect handler
                clientConnection.OnDisconnected += (reason) =>
                    OnQuicDisconnected(clientConnection, reason);

                // Wire up packet handler — bridges QUIC packets into the LLUDP pipeline
                clientConnection.OnPacketReceived += (payload) =>
                {
                    if (m_udpServer != null)
                        m_udpServer.ProcessIncomingQuicPacket(payload, transport);
                };

                await clientConnection.StartAsync();
            }
            catch (Exception ex)
            {
                m_log.Error($"[QuicServer] Failed to handle connection: {ex.Message}");
                clientConnection.Close($"Setup failed: {ex.Message}");
                clientConnection.Dispose();
            }
        }

        private void StopListener()
        {
            try
            {
                m_cts?.Cancel();

                // QuicListener implements IAsyncDisposable — use DisposeAsync
                if (m_listener != null)
                {
                    m_listener.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    m_listener = null;
                }

                // Close all active QUIC connections
                foreach (var kvp in m_connectionsByCircuit)
                {
                    kvp.Value.Close("Server shutting down");
                    kvp.Value.Dispose();
                }
                m_connectionsByCircuit.Clear();
                m_connectionsByAgentId.Clear();

                m_log.Info("[QuicServer] QUIC listener stopped");
            }
            catch (Exception ex)
            {
                m_log.Warn($"[QuicServer] Error stopping listener: {ex.Message}");
            }
        }

        #endregion

        #region TLS/QUIC Options

        /// <summary>
        /// Callback invoked by QuicListener for each incoming connection.
        /// Returns QuicServerConnectionOptions for the connection.
        /// </summary>
        private ValueTask<QuicServerConnectionOptions> OnConnectionOptions(
            QuicConnection quicConnection,
            SslClientHelloInfo clientHello,
            CancellationToken ct)
        {
            var serverAuthOptions = new SslServerAuthenticationOptions
            {
                ServerCertificateContext = m_config.LoadCertificateContext(),
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    new SslApplicationProtocol(m_config.Alpn)
                },
                ClientCertificateRequired = false
            };

            var options = new QuicServerConnectionOptions
            {
                ServerAuthenticationOptions = serverAuthOptions,
                IdleTimeout = TimeSpan.FromMilliseconds(m_config.IdleTimeoutMs),
                DefaultStreamErrorCode = 0,
                DefaultCloseErrorCode = 0,
            };

            return ValueTask.FromResult(options);
        }

        #endregion

        #region Packet Handling (Bridge to LLUDP)

        /// <summary>
        /// Called when a QUIC connection disconnects.
        /// Removes the connection from tracking dictionaries
        /// and unregisters from the proxy.
        /// </summary>
        private void OnQuicDisconnected(QuicClientConnection connection, string reason)
        {
            m_log.Info($"[QuicServer] QUIC client disconnected: {reason}");

            uint circuitCode = 0;

            // Remove from tracking dictionaries and capture circuit code
            foreach (var kvp in m_connectionsByCircuit)
            {
                if (kvp.Value == connection)
                {
                    circuitCode = kvp.Key;
                    m_connectionsByCircuit.TryRemove(kvp.Key, out _);
                    break;
                }
            }

            foreach (var kvp in m_connectionsByAgentId)
            {
                if (kvp.Value == connection)
                {
                    m_connectionsByAgentId.TryRemove(kvp.Key, out _);
                    break;
                }
            }

            // Unregister from proxy
            if (circuitCode > 0)
            {
                _ = UnregisterCircuitWithProxyAsync(circuitCode);
            }
        }

        /// <summary>
        /// Register a QUIC client connection associated with a circuit code and agent ID.
        /// </summary>
        public void RegisterQuicClient(uint circuitCode, string agentId, QuicClientConnection connection)
        {
            m_connectionsByCircuit[circuitCode] = connection;
            m_connectionsByAgentId[agentId] = connection;

            m_log.Info($"[QuicServer] Registered QUIC client: circuit={circuitCode}, agent={agentId}");
        }

        /// <summary>
        /// Find the QuicClientConnection for a given circuit code.
        /// </summary>
        public QuicClientConnection FindQuicClient(uint circuitCode)
        {
            m_connectionsByCircuit.TryGetValue(circuitCode, out var conn);
            return conn;
        }

        /// <summary>
        /// Find the LLUDPServer for a scene.
        /// Searches through scene modules for the UDP shim.
        /// </summary>
        private static LLUDPServer FindUdpServer(Scene scene)
        {
            var shim = scene.RequestModuleInterface<LLUDPServerShim>();
            return shim?.UdpServer;
        }

        #endregion
    }
}
