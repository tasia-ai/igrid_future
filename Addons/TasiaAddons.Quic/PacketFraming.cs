using System;

namespace TasiaAddons.Quic;

    /// <summary>
    /// Helper for the QUIC stream packet framing protocol.
    ///
    /// Frame format:
    ///   [4 bytes: payload length (big-endian uint32)]
    ///   [N bytes: LL packet data (same format as LLUDP)]
    ///
    /// This matches the viewer's LLQuicStream framing.
    /// </summary>
    public static class PacketFraming
    {
        /// <summary>
        /// The size of the length prefix in bytes.
        /// </summary>
        public const int HEADER_SIZE = 4;

        /// <summary>
        /// Maximum payload size we'll accept to prevent memory exhaustion.
        /// </summary>
        public const int MAX_PAYLOAD = 64 * 1024; // 64KB

        /// <summary>
        /// Zero-length keepalive frame sent periodically to prevent
        /// NAT/firewall/QUIC idle timeout from killing the connection.
        /// A 4-byte all-zero frame ([00 00 00 00]) is the keepalive marker.
        /// </summary>
        public static readonly byte[] KeepaliveFrame = new byte[] { 0, 0, 0, 0 };

        /// <summary>
        /// Encode a packet payload into a framed buffer.
        /// </summary>
        /// <param name="payload">Raw LL packet bytes.</param>
        /// <returns>Framed buffer: [4-byte length][payload].</returns>
        public static byte[] FramePacket(byte[] payload)
        {
            if (payload == null)
                throw new ArgumentNullException(nameof(payload));
            if (payload.Length > MAX_PAYLOAD)
                throw new ArgumentException($"Payload too large: {payload.Length} > {MAX_PAYLOAD}");

            byte[] framed = new byte[HEADER_SIZE + payload.Length];

            // Big-endian uint32 length prefix
            framed[0] = (byte)((payload.Length >> 24) & 0xFF);
            framed[1] = (byte)((payload.Length >> 16) & 0xFF);
            framed[2] = (byte)((payload.Length >> 8) & 0xFF);
            framed[3] = (byte)(payload.Length & 0xFF);

            Buffer.BlockCopy(payload, 0, framed, HEADER_SIZE, payload.Length);

            return framed;
        }

        /// <summary>
        /// Try to read a frame from a buffer.
        /// </summary>
        /// <param name="buffer">The receive buffer.</param>
        /// <param name="offset">Offset to start reading from. Updated on success.</param>
        /// <param name="payload">The extracted payload on success.</param>
        /// <returns>True if a complete frame was read.</returns>
        public static bool TryReadFrame(byte[] buffer, int availableLength, ref int offset, out byte[] payload)
        {
            payload = null;

            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (availableLength < 0 || availableLength > buffer.Length)
                throw new ArgumentOutOfRangeException(nameof(availableLength));
            if (offset < 0 || offset > availableLength)
                throw new ArgumentOutOfRangeException(nameof(offset));

            if (availableLength - offset < HEADER_SIZE)
                return false;

            // Big-endian uint32
            int length = (buffer[offset] << 24) |
                         (buffer[offset + 1] << 16) |
                         (buffer[offset + 2] << 8) |
                         buffer[offset + 3];

            if (length < 0 || length > MAX_PAYLOAD)
                return false;

            // Zero-length frame is a keepalive marker — silently consume it
            // and continue reading (do not produce a payload).
            if (length == 0)
            {
                offset += HEADER_SIZE;
                return false;
            }

            if (availableLength - offset - HEADER_SIZE < length)
                return false;

            payload = new byte[length];
            Buffer.BlockCopy(buffer, offset + HEADER_SIZE, payload, 0, length);

            offset += HEADER_SIZE + length;
            return true;
        }
    }
