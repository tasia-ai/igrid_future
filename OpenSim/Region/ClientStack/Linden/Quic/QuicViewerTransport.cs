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
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using log4net;
using OpenSim.Framework;

namespace OpenSim.Region.ClientStack.LindenUDP
{
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
}
