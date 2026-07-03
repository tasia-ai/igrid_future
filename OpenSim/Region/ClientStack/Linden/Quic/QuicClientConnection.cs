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
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Quic;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OpenMetaverse;

namespace OpenSim.Region.ClientStack.LindenUDP
{
    /// <summary>
    /// Represents a single QUIC connection from a viewer to this region server.
    /// Manages a bidirectional control stream for framed LLUDP packet I/O.
    /// </summary>
    /// <remarks>
    /// .NET 8's System.Net.Quic does not support datagrams, so all traffic
    /// (both reliable and unreliable LLUDP packets) is multiplexed over the
    /// control stream with 4-byte length-prefix framing.
    /// </remarks>
    public class QuicClientConnection : IDisposable
    {
        private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private readonly QuicConnection m_connection;
        private readonly QuicServerConfig m_config;
        private QuicStream m_controlStream;
        private readonly CancellationTokenSource m_cts = new();
        private readonly ConcurrentQueue<byte[]> m_pendingSends = new();
        private readonly object m_sendLock = new();
        private bool m_sending;
        private bool m_disposed;
        private bool m_connected;
        private int m_disconnectNotified;

        // Assembler state for stream framing
        private readonly byte[] m_streamBuffer = new byte[64 * 1024];
        private int m_streamBufferOffset;

        /// <summary>
        /// Raised when a complete unframed packet payload is received.
        /// </summary>
        public event Action<byte[]> OnPacketReceived;

        /// <summary>
        /// Raised when the connection is closed remotely.
        /// </summary>
        public event Action<string> OnDisconnected;

        public QuicClientConnection(QuicConnection connection, QuicServerConfig config)
        {
            m_connection = connection ?? throw new ArgumentNullException(nameof(connection));
            m_config = config ?? throw new ArgumentNullException(nameof(config));
        }

        /// <summary>
        /// Whether the QUIC connection is still alive and the local state is not disposed.
        /// Tracked via a manual flag since .NET 8's QuicConnection does not expose IsConnected.
        /// </summary>
        public bool IsConnected => m_connected && !m_disposed;

        public IPEndPoint RemoteEndPoint
        {
            get
            {
                if (m_connection.RemoteEndPoint is IPEndPoint ep)
                    return ep;
                return new IPEndPoint(IPAddress.Any, 0);
            }
        }

        /// <summary>
        /// Start the connection: accept the control stream and begin reading.
        /// </summary>
        public async Task StartAsync()
        {
            try
            {
                if (m_config.LogHandshake)
                    m_log.Info($"[QuicClient] Starting QUIC client from {RemoteEndPoint}");

                // Accept the first bidirectional stream — this is the control/data stream
                m_controlStream = await m_connection.AcceptInboundStreamAsync(m_cts.Token);
                if (!m_controlStream.CanRead || !m_controlStream.CanWrite)
                {
                    NotifyDisconnected("Control stream is not bidirectional");
                    Dispose();
                    return;
                }

                m_connected = true;
                if (m_config.LogHandshake)
                    m_log.Info($"[QuicClient] Control stream accepted (canRead={m_controlStream.CanRead}, canWrite={m_controlStream.CanWrite})");

                // Start reading from the stream
                _ = Task.Run(() => ReadStreamLoopAsync(m_cts.Token));

                // Start accepting additional streams (we dispose them for now)
                _ = Task.Run(() => AcceptAdditionalStreamsAsync(m_cts.Token));
            }
            catch (Exception ex)
            {
                m_log.Error($"[QuicClient] Start failed: {ex.Message}");
                NotifyDisconnected($"Start failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Send a packet payload over the QUIC stream.
        /// All traffic uses stream framing (no datagrams in .NET 8).
        /// </summary>
        public void Send(byte[] payload, bool reliable)
        {
            if (m_disposed || m_controlStream == null)
                return;

            lock (m_sendLock)
            {
                m_pendingSends.Enqueue(payload);
                if (!m_sending)
                {
                    m_sending = true;
                    _ = Task.Run(() => FlushPendingSendsAsync());
                }
            }
        }

        /// <summary>
        /// Close the connection with a reason (logged only; QUIC error code is numeric).
        /// </summary>
        public void Close(string reason)
        {
            if (m_disposed)
                return;

            m_log.Debug($"[QuicClient] Closing: {reason}");
            m_connected = false;
            m_cts.Cancel();

            try
            {
                m_connection.CloseAsync((long)QuicError.Success, CancellationToken.None)
                    .AsTask().GetAwaiter().GetResult();
            }
            catch
            {
                // Ignore close errors
            }

            Dispose();
            NotifyDisconnected(reason);
        }

        public void Dispose()
        {
            if (m_disposed)
                return;
            m_disposed = true;
            m_connected = false;
            m_cts.Cancel();
            m_cts.Dispose();
            DisposeAsyncHelper(m_controlStream);
            DisposeAsyncHelper(m_connection);
        }

        #region Private Methods

        /// <summary>
        /// Synchronously dispose an IAsyncDisposable by blocking on DisposeAsync.
        /// </summary>
        private static void DisposeAsyncHelper(IAsyncDisposable disposable)
        {
            if (disposable != null)
            {
                try
                {
                    disposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
                catch
                {
                    // Ignore disposal errors
                }
            }
        }

        private async Task ReadStreamLoopAsync(CancellationToken ct)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);

            try
            {
                if (m_config.LogHandshake)
                    m_log.Info("[QuicClient] Stream read loop started");

                while (!ct.IsCancellationRequested && m_controlStream != null && m_controlStream.CanRead)
                {
                    int bytesRead = await m_controlStream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct);

                    if (m_config.LogPackets)
                        m_log.Info($"[QuicClient] Read {bytesRead} bytes from stream");

                    if (bytesRead == 0)
                    {
                        // Graceful stream close
                        NotifyDisconnected("Stream closed by peer");
                        return;
                    }

                    // Append to assembler buffer
                    if (m_streamBufferOffset + bytesRead > m_streamBuffer.Length)
                    {
                        m_log.Warn($"[QuicClient] Stream buffer overflow, resetting");
                        m_streamBufferOffset = 0;
                        continue;
                    }

                    Buffer.BlockCopy(buffer, 0, m_streamBuffer, m_streamBufferOffset, bytesRead);
                    m_streamBufferOffset += bytesRead;

                    // Try to extract complete frames
                    int offset = 0;
                    while (PacketFraming.TryReadFrame(m_streamBuffer, m_streamBufferOffset, ref offset, out byte[] payload))
                    {
                        if (m_config.LogPackets)
                            m_log.Info($"[QuicClient] Received frame: {payload.Length} bytes, first={FormatPrefix(payload)}");

                        OnPacketReceived?.Invoke(payload);
                    }

                    // Compact remaining data
                    if (offset > 0)
                    {
                        int remaining = m_streamBufferOffset - offset;
                        if (remaining > 0)
                            Buffer.BlockCopy(m_streamBuffer, offset, m_streamBuffer, 0, remaining);
                        m_streamBufferOffset = remaining;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                {
                    m_log.Warn($"[QuicClient] Stream read error: {ex.Message}");
                    NotifyDisconnected($"Read error: {ex.Message}");
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
                if (!m_disposed && !ct.IsCancellationRequested)
                {
                    NotifyDisconnected("Read loop ended");
                    Dispose();
                }
            }
        }

        private void NotifyDisconnected(string reason)
        {
            m_connected = false;
            if (Interlocked.Exchange(ref m_disconnectNotified, 1) == 0)
                OnDisconnected?.Invoke(reason);
        }

        private async Task AcceptAdditionalStreamsAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    QuicStream additionalStream = await m_connection.AcceptInboundStreamAsync(ct);
                    // For now, additional streams are not used — dispose them
                    await additionalStream.DisposeAsync();
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown
            }
            catch (QuicException ex) when (ex.QuicError == QuicError.ConnectionAborted || ex.QuicError == QuicError.InternalError)
            {
                // Connection closed
            }
            catch (Exception ex)
            {
                if (!ct.IsCancellationRequested)
                    m_log.Warn($"[QuicClient] Additional stream accept error: {ex.Message}");
            }
        }

        private async Task FlushPendingSendsAsync()
        {
            try
            {
                while (true)
                {
                    if (!m_pendingSends.TryDequeue(out byte[] payload))
                    {
                        lock (m_sendLock)
                        {
                            if (m_pendingSends.IsEmpty)
                            {
                                m_sending = false;
                                return;
                            }
                            // Re-check under lock
                            if (!m_pendingSends.TryDequeue(out payload))
                            {
                                m_sending = false;
                                return;
                            }
                        }
                    }

                    byte[] framed = PacketFraming.FramePacket(payload);

                    await m_controlStream.WriteAsync(framed.AsMemory(0, framed.Length), m_cts.Token);
                    await m_controlStream.FlushAsync(m_cts.Token);

                    if (m_config.LogPackets)
                        m_log.Debug($"[QuicClient] Sent frame: {payload.Length} bytes");
                }
            }
            catch (OperationCanceledException)
            {
                lock (m_sendLock)
                    m_sending = false;
            }
            catch (Exception ex)
            {
                m_log.Warn($"[QuicClient] Flush error: {ex.Message}");
                lock (m_sendLock)
                    m_sending = false;
                OnDisconnected?.Invoke($"Send error: {ex.Message}");
            }
        }

        private static string FormatPrefix(byte[] payload)
        {
            if (payload == null || payload.Length == 0)
                return "(empty)";

            int count = Math.Min(payload.Length, 12);
            return Utils.BytesToHexString(payload, count, null);
        }

        #endregion
    }
}
