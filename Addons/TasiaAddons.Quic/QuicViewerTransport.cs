using System;
using System.Net;
using log4net;
using OpenSim.Framework;

namespace TasiaAddons.Quic;

    /// <summary>
    /// IViewerTransport implementation over a QUIC connection.
    /// Wraps a QuicClientConnection to send/receive packet payloads.
    /// </summary>
    public class QuicViewerTransport : IViewerTransport
    {
        private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly QuicClientConnection m_connection;
        private readonly QuicServerConfig m_config;

        public QuicViewerTransport(QuicClientConnection connection, QuicServerConfig config)
        {
            m_connection = connection ?? throw new ArgumentNullException(nameof(connection));
            m_config = config ?? throw new ArgumentNullException(nameof(config));

            // Forward received packets
            m_connection.OnPacketReceived += OnConnectionPacketReceived;
        }

        public string TransportName => "QUIC";

        public QuicClientConnection Connection => m_connection;

        public bool IsConnected => m_connection.IsConnected;

        public IPEndPoint RemoteEndPoint => m_connection.RemoteEndPoint;

        public void SendPacket(byte[] payload, bool reliable)
        {
            try
            {
                m_connection.Send(payload, reliable);
            }
            catch (Exception ex)
            {
                m_log.Error($"[QuicTransport] SendPacket failed: {ex.Message}");
            }
        }

        public void Close(string reason)
        {
            m_connection.OnPacketReceived -= OnConnectionPacketReceived;
            m_connection.Close(reason);
        }

        public event Action<byte[], IViewerTransport> PacketReceived;

        private void OnConnectionPacketReceived(byte[] payload)
        {
            try
            {
                PacketReceived?.Invoke(payload, this);
            }
            catch (Exception ex)
            {
                m_log.Error($"[QuicTransport] Error dispatching received packet: {ex.Message}");
            }
        }
    }
