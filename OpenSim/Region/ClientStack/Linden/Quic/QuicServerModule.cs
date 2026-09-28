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

        // Quick-G brain state. Port=0 in [ClientStack.Quic] enables automatic
        // region listener allocation from the central Quick-G brain.
        private string m_brainUrl = "";
        private string m_regionId = "";
        private string m_regionName = "";
        private bool m_brainLeaseHeld;
        private Timer m_brainHeartbeatTimer;

        // HTTP client for proxy registration and Quick-G brain calls (shared, thread-safe)
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

                    m_brainUrl = NormalizeQuicConfigString(m_config.BrainURL);
                }

                string portDescription = m_config.Port == 0 ? "brain/auto" : m_config.Port.ToString();
                m_log.Info($"[QuicServer] Initialized: port={portDescription}, ALPN={m_config.Alpn}, proxy={m_proxyRegistrationUrl}");
            }
        }

        public void AddRegion(Scene scene)
        {
            if (!m_enabled)
                return;

            m_scene = scene;

            // Grid registration happens after AddRegion() but before RegionLoaded().
            // When Port=0, acquire the Quick-G brain lease here so RegionInfo carries
            // the actual assigned QUIC port into GridService.  If we wait until
            // RegionLoaded(), Robust stores the literal config value (0) or a stale
            // previous value and login/teleport responses advertise the wrong port.
            m_simHost = scene.RegionInfo.ExternalHostName;
            m_simPort = scene.RegionInfo.InternalEndPoint.Port;
            m_regionId = scene.RegionInfo.RegionID.ToString();
            m_regionName = scene.RegionInfo.RegionName;

            if (m_config.Port == 0 && !m_brainLeaseHeld)
            {
                if (!AcquireBrainLease())
                {
                    m_log.Error($"[QuicServer] Quick-G brain could not assign a QUIC port for {m_regionName}; QUIC disabled for this region");
                    m_enabled = false;
                    return;
                }
            }

            UpdateRegionInfoQuicEndpoint(scene);
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

            // AddRegion() initializes identity and, in brain mode, acquires the
            // lease early enough for grid registration.  Keep RegionInfo in sync
            // in case startup ordering changes or explicit-port config is used.
            UpdateRegionInfoQuicEndpoint(scene);

            // Subscribe to circuit creation events for proxy registration
            m_udpServer.OnQuicCircuitCreated += OnQuicCircuitCreated;

            // In Quick-G brain mode there is no native QUIC listener, so
            // bridged viewer circuits arrive as plain loopback UDP. Complete
            // the viewer QUIC handshake (quicready) for those circuits.
            m_udpServer.OnLoopbackCircuitCreated += OnLoopbackCircuitCreated;

            if (m_brainLeaseHeld)
            {
                StartBrainHeartbeat();
                m_log.Info($"[QuicServer] Region loaded, sim={m_simHost}:{m_simPort}, Quick-G brain transport active, logical quic={m_config.Port}, proxy={m_proxyRegistrationUrl}");
                return;
            }

            StartListener();
            if (m_listener == null)
            {
                ReleaseBrainLease();
                return;
            }

            StartBrainHeartbeat();
            m_log.Info($"[QuicServer] Region loaded, sim={m_simHost}:{m_simPort}, quic={m_config.Port}, proxy={m_proxyRegistrationUrl}");
        }

        public void RemoveRegion(Scene scene)
        {
            if (!m_enabled && m_listener == null && !m_brainLeaseHeld)
                return;

            // Unsubscribe from circuit creation events
            if (m_udpServer != null)
            {
                m_udpServer.OnQuicCircuitCreated -= OnQuicCircuitCreated;
                m_udpServer.OnLoopbackCircuitCreated -= OnLoopbackCircuitCreated;
            }

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

        private void UpdateRegionInfoQuicEndpoint(Scene scene)
        {
            if (scene == null || m_config.Port <= 0)
                return;

            scene.RegionInfo.QuicHost = string.IsNullOrWhiteSpace(scene.RegionInfo.QuicHost)
                ? m_simHost
                : scene.RegionInfo.QuicHost;
            scene.RegionInfo.QuicPort = (uint)m_config.Port;
            m_log.Info($"[QuicServer] RegionInfo QUIC endpoint set for {m_regionName}: {scene.RegionInfo.QuicHost}:{scene.RegionInfo.QuicPort}");
        }

        #region Quick-G Brain

        private string GetBrainUrl()
        {
            if (!string.IsNullOrWhiteSpace(m_brainUrl))
                return m_brainUrl.TrimEnd('/');

            try
            {
                Uri proxy = new Uri(m_proxyRegistrationUrl);
                var builder = new UriBuilder(proxy.Scheme, proxy.Host, m_config.BrainPort);
                m_brainUrl = builder.Uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
                return m_brainUrl;
            }
            catch (Exception ex)
            {
                m_log.Error($"[QuicServer] Cannot derive Quick-G BrainURL from ProxyRegistrationURL: {ex.Message}");
                return string.Empty;
            }
        }

        private OSDMap BuildBrainPayload()
        {
            return new OSDMap
            {
                ["regionId"] = OSD.FromString(m_regionId),
                ["regionName"] = OSD.FromString(m_regionName ?? string.Empty),
                ["host"] = OSD.FromString(m_simHost ?? string.Empty),
                ["simPort"] = OSD.FromInteger(m_simPort),
                ["quicPort"] = OSD.FromInteger(m_config.Port)
            };
        }

        private bool AcquireBrainLease()
        {
            string brainUrl = GetBrainUrl();
            if (string.IsNullOrWhiteSpace(brainUrl))
                return false;

            try
            {
                OSDMap payload = BuildBrainPayload();
                payload["quicPort"] = OSD.FromInteger(0);
                string json = OSDParser.SerializeJsonString(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = m_httpClient.PostAsync(brainUrl + "/allocate", content).GetAwaiter().GetResult();
                string responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                if (!response.IsSuccessStatusCode)
                {
                    m_log.Error($"[QuicServer] Quick-G brain allocation failed: {(int)response.StatusCode} {responseBody}");
                    return false;
                }

                OSDMap result = OSDParser.DeserializeJson(responseBody) as OSDMap;
                if (result == null || !result.ContainsKey("quicPort"))
                {
                    m_log.Error("[QuicServer] Quick-G brain returned no quicPort");
                    return false;
                }

                int assignedPort = result["quicPort"].AsInteger();
                if (assignedPort <= 0 || assignedPort > 65535)
                {
                    m_log.Error($"[QuicServer] Quick-G brain returned invalid quicPort {assignedPort}");
                    return false;
                }

                m_config.UseAssignedPort(assignedPort);
                m_brainLeaseHeld = true;
                m_log.Info($"[QuicServer] Quick-G brain assigned {m_regionName} -> QUIC {assignedPort}");
                return true;
            }
            catch (Exception ex)
            {
                m_log.Error($"[QuicServer] Quick-G brain allocation error: {ex.Message}");
                return false;
            }
        }

        private void StartBrainHeartbeat()
        {
            if (!m_brainLeaseHeld)
                return;

            int intervalMs = checked(m_config.BrainHeartbeatSeconds * 1000);
            m_brainHeartbeatTimer?.Dispose();
            m_brainHeartbeatTimer = new Timer(
                _ => _ = SendBrainHeartbeatAsync(),
                null,
                intervalMs,
                intervalMs);
        }

        private async Task SendBrainHeartbeatAsync()
        {
            if (!m_brainLeaseHeld)
                return;

            try
            {
                string brainUrl = GetBrainUrl();
                string json = OSDParser.SerializeJsonString(BuildBrainPayload());
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await m_httpClient.PostAsync(brainUrl + "/heartbeat", content);
                if (!response.IsSuccessStatusCode)
                {
                    string body = await response.Content.ReadAsStringAsync();
                    m_log.Warn($"[QuicServer] Quick-G brain heartbeat returned {(int)response.StatusCode}: {body}");
                }
            }
            catch (Exception ex)
            {
                m_log.Warn($"[QuicServer] Quick-G brain heartbeat failed: {ex.Message}");
            }
        }

        private void ReleaseBrainLease()
        {
            m_brainHeartbeatTimer?.Dispose();
            m_brainHeartbeatTimer = null;

            if (!m_brainLeaseHeld)
                return;

            m_brainLeaseHeld = false;
            try
            {
                string brainUrl = GetBrainUrl();
                string json = OSDParser.SerializeJsonString(BuildBrainPayload());
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using HttpResponseMessage response = m_httpClient.PostAsync(brainUrl + "/release", content).GetAwaiter().GetResult();
                if (!response.IsSuccessStatusCode)
                    m_log.Debug($"[QuicServer] Quick-G brain release returned {(int)response.StatusCode}");
            }
            catch (Exception ex)
            {
                m_log.Debug($"[QuicServer] Quick-G brain release failed: {ex.Message}");
            }
        }

        #endregion

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
            _ = RegisterCircuitWithProxyAsync(circuitCode, agentId);
        }

        /// <summary>
        /// Called when a plain-LLUDP UseCircuitCode arrives from loopback
        /// (Quick-G bridged viewer traffic: the bridge relays viewer QUIC as
        /// local UDP, also when Pangolin/newt masks the remote viewer as
        /// 127.0.0.1). Sends the quicready handshake so the Tasia
        /// Viewer unblocks its queued session packets. Without this the
        /// viewer stalls after UseCircuitCode and cleanly closes the QUIC
        /// connection (~30s), after which the sim kills the starved child
        /// agent at the 60s LLUDP timeout.
        /// NOTE: must NOT require a brain lease — grids using explicit QUIC
        /// ports never hold one, which silently disabled quicready for every
        /// bridged (child) circuit.
        /// </summary>
        private void OnLoopbackCircuitCreated(uint circuitCode, UUID agentId, IPEndPoint endPoint)
        {
            if (m_scene == null)
                return;

            try
            {
                ScenePresence presence = m_scene.GetScenePresence(agentId);
                if (presence?.ControllingClient == null)
                {
                    // Child agents usually arrive BEFORE the sim-to-sim
                    // handshake creates their presence. Retry instead of
                    // dropping quicready forever.
                    m_log.Debug($"[QuicServer] Loopback circuit {circuitCode} for agent {agentId}: no scene presence yet, quicready will retry");
                    _ = SendQuicReadyWhenReadyAsync(circuitCode, agentId, endPoint);
                    return;
                }

                // LLUDPServer already sends quicready directly for loopback
                // bridged circuits before invoking this event, so sending it
                // again here would duplicate the message (harmless to the
                // viewer but noisy). Nothing further to do when the presence
                // already exists — the direct send covered the handshake.
            }
            catch (Exception ex)
            {
                m_log.Warn($"[QuicServer] Failed to send quicready for circuit {circuitCode}: {ex.Message}");
            }
        }

        /// <summary>
        /// Delayed quicready for bridged circuits whose presence did not
        /// exist yet at UseCircuitCode time. Gives up after ~10s.
        /// </summary>
        private async Task SendQuicReadyWhenReadyAsync(uint circuitCode, UUID agentId, IPEndPoint endPoint)
        {
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(1000);
                try
                {
                    if (m_scene == null)
                        return;
                    ScenePresence presence = m_scene.GetScenePresence(agentId);
                    if (presence?.ControllingClient != null)
                    {
                        presence.ControllingClient.SendGenericMessage("quicready", UUID.Zero, new List<string>());
                        m_log.Info($"[QuicServer] Sent delayed quicready for bridged circuit {circuitCode} agent {agentId} via {endPoint} (attempt {i + 1})");
                        return;
                    }
                }
                catch { }
            }
            m_log.Warn($"[QuicServer] Giving up delayed quicready for bridged circuit {circuitCode} agent {agentId}: presence never appeared");
        }

        /// <summary>
        /// POST to the QUIC proxy to register this circuit.
        /// agentType is root for teleport/login destinations and child for
        /// automatic neighbour setups, so bridges can tell a teleport route
        /// flip (accept) from a neighbour child-agent flip (ignore).
        /// </summary>
        private async Task RegisterCircuitWithProxyAsync(uint circuitCode, UUID agentId)
        {
            try
            {
                string agentType = "unknown";
                try
                {
                    if (m_scene != null)
                    {
                        ScenePresence presence = m_scene.GetScenePresence(agentId);
                        if (presence != null)
                            agentType = presence.IsChildAgent ? "child" : "root";
                    }
                }
                catch { agentType = "unknown"; }

                string url = m_proxyRegistrationUrl + "/register";
                var payload = new OSDMap
                {
                    ["circuitCode"] = OSD.FromInteger((int)circuitCode),
                    ["simHost"] = OSD.FromString(m_simHost),
                    ["simPort"] = OSD.FromInteger(m_simPort),
                    ["regionName"] = OSD.FromString(m_regionName ?? string.Empty),
                    ["regionId"] = OSD.FromString(m_regionId ?? string.Empty),
                    ["brainLease"] = OSD.FromBoolean(m_brainLeaseHeld),
                    ["agentType"] = OSD.FromString(agentType)
                };

                if (!m_brainLeaseHeld)
                {
                    payload["quicHost"] = OSD.FromString(m_simHost);
                    payload["quicPort"] = OSD.FromInteger(m_config.Port);
                }

                string json = OSDParser.SerializeJsonString(payload);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                HttpResponseMessage response = await m_httpClient.PostAsync(url, content);
                if (response.IsSuccessStatusCode)
                {
                    m_log.Info($"[QuicServer] Circuit {circuitCode} registered with proxy -> {m_simHost}:{m_simPort}");
                }
                else
                {
                    m_log.Warn($"[QuicServer] Proxy registration returned {response.StatusCode} for circuit {circuitCode}");
                }
            }
            catch (Exception ex)
            {
                // Registration failure is non-fatal - proxy will use broadcast fallback
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
                    ["regionName"] = OSD.FromString(m_regionName ?? string.Empty),
                    ["regionId"] = OSD.FromString(m_regionId ?? string.Empty),
                    ["brainLease"] = OSD.FromBoolean(m_brainLeaseHeld)
                };

                if (!m_brainLeaseHeld)
                {
                    payload["quicHost"] = OSD.FromString(m_simHost);
                    payload["quicPort"] = OSD.FromInteger(m_config.Port);
                }

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

                // Wire up packet handler - bridges QUIC packets into the LLUDP pipeline
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
                m_brainHeartbeatTimer?.Dispose();
                m_brainHeartbeatTimer = null;
                m_cts?.Cancel();

                // QuicListener implements IAsyncDisposable - use DisposeAsync
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

                ReleaseBrainLease();
                m_log.Info("[QuicServer] QUIC listener stopped");
            }
            catch (Exception ex)
            {
                m_log.Warn($"[QuicServer] Error stopping listener: {ex.Message}");
                ReleaseBrainLease();
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
