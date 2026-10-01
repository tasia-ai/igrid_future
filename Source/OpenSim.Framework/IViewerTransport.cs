using System;
using System.Net;

namespace OpenSim.Framework;

    /// <summary>
    /// Abstraction for a viewer transport connection.
    /// Implementations exist for LLUDP and QUIC transports.
    /// </summary>
    public interface IViewerTransport
    {
        /// <summary>
        /// Send a packet payload over this transport.
        /// </summary>
        /// <param name="payload">Raw LL packet bytes.</param>
        /// <param name="reliable">If true, transport should guarantee delivery.</param>
        void SendPacket(byte[] payload, bool reliable);

        /// <summary>
        /// Raised when a complete packet payload is received.
        /// Parameter 1: raw LL packet bytes.
        /// Parameter 2: the transport that received them.
        /// </summary>
        event Action<byte[], IViewerTransport> PacketReceived;

        /// <summary>
        /// Close the transport connection.
        /// </summary>
        void Close(string reason);

        /// <summary>
        /// Human-readable transport name for logging.
        /// </summary>
        string TransportName { get; }

        /// <summary>
        /// Whether the transport is still connected.
        /// </summary>
        bool IsConnected { get; }

        /// <summary>
        /// The remote endpoint of this transport.
        /// </summary>
        IPEndPoint RemoteEndPoint { get; }
    }
